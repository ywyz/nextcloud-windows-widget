using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace NextcloudWidget.Core;

internal sealed record Account(Guid CacheId, Uri Server, string User);

internal static class Authorization
{
    private sealed record Poll(string Token, Uri Endpoint);
    private sealed record Challenge(Poll Poll, Uri Login);
    private sealed record Credentials(Uri Server, string LoginName, string AppPassword);

    internal static Uri BaseUri(Uri input)
    {
        if (!input.IsAbsoluteUri || input.Scheme != Uri.UriSchemeHttps || input.UserInfo.Length != 0 ||
            input.Query.Length != 0 || input.Fragment.Length != 0)
            throw new ArgumentException("请输入完整的 HTTPS 服务器地址，可以包含安装子目录。");
        return new Uri(input.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static bool SameOrigin(Uri a, Uri b) => b.IsAbsoluteUri && b.UserInfo.Length == 0 &&
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    internal static async Task<(Uri Server, string User, string Password)> LoginAsync(HttpClient http, Uri server,
        Action<Uri> openBrowser, CancellationToken ct)
    {
        using var start = await http.PostAsync(new Uri(server, "index.php/login/v2"), null, ct);
        start.EnsureSuccessStatusCode();
        var challenge = await start.Content.ReadFromJsonAsync<Challenge>(ct)
            ?? throw new InvalidDataException("服务器没有返回登录信息。");
        if (challenge.Poll is null || string.IsNullOrWhiteSpace(challenge.Poll.Token) ||
            challenge.Login is null || challenge.Poll.Endpoint is null ||
            !SameOrigin(server, challenge.Login) || !SameOrigin(server, challenge.Poll.Endpoint))
            throw new InvalidDataException("服务器返回的授权地址无效，请核对 Nextcloud 对外地址。");

        openBrowser(challenge.Login);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        while (true)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { { "token", challenge.Poll.Token } });
            using var response = await http.PostAsync(challenge.Poll.Endpoint, form, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
                continue;
            }
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<Credentials>(timeout.Token);
            if (result?.Server is null || !SameOrigin(server, result.Server) ||
                string.IsNullOrWhiteSpace(result.LoginName) || string.IsNullOrWhiteSpace(result.AppPassword))
                throw new InvalidDataException("服务器返回的登录凭据无效。");
            return (BaseUri(result.Server), result.LoginName, result.AppPassword);
        }
    }
}

internal interface IDeviceSecrets
{
    string? Read(Guid id);
    void Write(Guid id, string user, string password);
    void Delete(Guid id);
}

internal sealed class WindowsDeviceSecrets : IDeviceSecrets
{
    private static string Key(Guid id) => "NextcloudWindowsWidget/" + id.ToString("N");
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias;
        public string UserName;
    }
    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential data, uint flags);
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string key, uint type, uint flags, out IntPtr ptr);
    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string key, uint type, uint flags);
    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr ptr);

    public string? Read(Guid id)
    {
        if (!CredRead(Key(id), 1, 0, out var ptr))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(ptr);
            return Marshal.PtrToStringUni(value.Blob, checked((int)value.BlobSize / 2));
        }
        finally { CredFree(ptr); }
    }
    public void Write(Guid id, string user, string password)
    {
        IntPtr blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var value = new Credential
            {
                TargetName = Key(id),
                Type = 1,
                Persist = 2,
                Blob = blob,
                BlobSize = checked((uint)Encoding.Unicode.GetByteCount(password)),
                UserName = user
            };
            if (!CredWrite(ref value, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
    public void Delete(Guid id)
    {
        if (!CredDelete(Key(id), 1, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error);
        }
    }
}
