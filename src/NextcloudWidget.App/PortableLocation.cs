using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NextcloudWidget.Updates;

namespace NextcloudWidget.App;

internal static class PortableLocation
{
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty Location identifies the published single-file bundle.")]
    internal static bool IsBundled => typeof(PortableLocation).Assembly.Location.Length == 0 &&
        Environment.ProcessPath is not null;

    internal static bool IsInDownloads(string executable)
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        int result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var value);
        try
        {
            var downloads = result == 0 ? Marshal.PtrToStringUni(value) : null;
            downloads ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return IsInside(downloads, executable);
        }
        finally { Marshal.FreeCoTaskMem(value); }
    }

    internal static bool IsInside(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static string CopyToFolder(string sourceExe, string selectedDirectory)
    {
        var source = Path.GetFullPath(sourceExe);
        var destination = Path.Combine(Path.GetFullPath(selectedDirectory), ReleaseInfo.Asset);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return source;

        var temporary = Path.Combine(selectedDirectory, $".nextcloud-widget-{Guid.NewGuid():N}.tmp");
        using var original = File.OpenRead(source);
        var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            // Publish the complete copy with a no-overwrite rename on the same volume.
            using (output) original.CopyTo(output);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination))
            {
                using var existing = File.OpenRead(destination);
                original.Position = 0;
                if (existing.Length != original.Length ||
                    (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0 ||
                    !SHA256.HashData(existing).SequenceEqual(SHA256.HashData(original)))
                    throw new IOException("所选文件夹中已有其他同名文件，请选择另一个文件夹。");
            }
        }
        finally { File.Delete(temporary); }
        return destination;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
