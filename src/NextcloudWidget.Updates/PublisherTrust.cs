using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace NextcloudWidget.Updates;

internal static class PublisherTrust
{
    // SHA-256 of the production signer's DER SubjectPublicKeyInfo.
    private const string PublicKey = "64088E2A63C4AAAFE4DD9C17FBE56EBCDA1613A226CDF4DDCF72AC0E93761EDC";

    internal static void CheckManifest(byte[] content, byte[] signature)
    {
        var signed = new SignedCms(new ContentInfo(content), detached: true);
        signed.Decode(signature);
        signed.CheckSignature(verifySignatureOnly: true);
        if (signed.SignerInfos.Count != 1 ||
            signed.SignerInfos[0].DigestAlgorithm.Value != "2.16.840.1.101.3.4.2.1")
            throw new CryptographicException("更新清单签名无效。");
        CheckPublisher(signed.SignerInfos[0].Certificate);
    }

    private static void CheckPublisher(X509Certificate2? signer)
    {
        if (signer == null ||
            Convert.ToHexString(SHA256.HashData(signer.PublicKey.ExportSubjectPublicKeyInfo())) != PublicKey)
            throw new CryptographicException("更新发布者与本应用不一致。");
    }

    internal static Version CheckExecutable(string path)
    {
        var file = new TrustFile { Name = Marshal.StringToCoTaskMemUni(path) };
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new TrustData { File = filePtr };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            try
            {
                int status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                if (status != 0) throw new CryptographicException($"Windows 签名验证失败（0x{status:X8}）。");
                // Bind the key pin to the signer Windows actually verified.
                IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
                IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                IntPtr chain = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
                if (chain == IntPtr.Zero) throw new CryptographicException("无法取得已验证的签名者。");
                IntPtr context = Marshal.PtrToStructure<ProviderCert>(chain).Cert;
                using var cert = new X509Certificate2(context);
                CheckPublisher(cert);
            }
            finally
            {
                data.StateAction = 2;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
        }
        finally { Marshal.FreeCoTaskMem(file.Name); Marshal.FreeHGlobal(filePtr); }
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != ReleaseInfo.Product ||
            !string.Equals(info.OriginalFilename, "NextcloudWidget-win-x64.dll", StringComparison.OrdinalIgnoreCase) ||
            info.FilePrivatePart != 0)
            throw new InvalidDataException("更新文件的产品标识或版本不正确。");
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCert { public uint Size; public IntPtr Cert; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        internal uint Size = (uint)Marshal.SizeOf<TrustFile>();
        internal IntPtr Name, Handle, KnownSubject;
        public TrustFile() { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size = (uint)Marshal.SizeOf<TrustData>();
        internal IntPtr PolicyData, SipData;
        internal uint UiChoice = 2, RevocationChecks = 0, UnionChoice = 1;
        internal IntPtr File;
        internal uint StateAction = 1;
        internal IntPtr StateData, UrlReference;
        internal uint ProvFlags = 0x80, UiContext;
        internal IntPtr SignatureSettings;
        public TrustData() { }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr data, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
}
