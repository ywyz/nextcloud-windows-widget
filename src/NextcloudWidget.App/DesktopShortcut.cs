using System.Runtime.InteropServices;

namespace NextcloudWidget.App;

internal sealed record ShortcutTarget(string Executable, string Arguments, string WorkingDirectory)
{
    internal static ShortcutTarget FromProcess(string executable, string? entryAssembly)
    {
        string target = Path.GetFullPath(executable);
        if (!Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return new(target, "", Path.GetDirectoryName(target)!);

        if (string.IsNullOrWhiteSpace(entryAssembly) || !File.Exists(entryAssembly))
            throw new IOException("找不到启动小窗所需的程序集。");
        string dll = Path.GetFullPath(entryAssembly);
        return new(target, "\"" + dll + "\"", Path.GetDirectoryName(dll)!);
    }
}

internal static class DesktopShortcut
{
    private const string BaseName = "Nextcloud 小窗";
    internal const string Owner = "打开 Nextcloud 小窗 [ywyz.nextcloud-windows-widget]";

    // Maintenance, smoke checks and custom profiles must never create a personal shortcut.
    internal static bool IsUserSession(string[] args, bool updateHandshake = false) =>
        args.Length == 0 || (updateHandshake && args is ["--updated", var token] && Guid.TryParseExact(token, "N", out _));

    internal static void EnsureForCurrentUser(string? executable = null)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            throw new DirectoryNotFoundException("Windows 桌面目录不可用。");
        executable ??= Environment.ProcessPath ?? throw new IOException("无法确定小窗的启动程序。");
        Ensure(desktop, ShortcutTarget.FromProcess(executable, Environment.GetCommandLineArgs().FirstOrDefault()));
    }

    internal static string Ensure(string directory, ShortcutTarget target)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        try
        {
            string? stale = null;
            foreach (string file in Directory.EnumerateFiles(directory, BaseName + "*.lnk")
                         .Where(IsCandidate).OrderBy(s => s.Length).ThenBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var state = Inspect(shell, file, target);
                if (state == LinkState.Matching) return file;
                if (state == LinkState.Owned) stale ??= file;
            }

            string temporary = Path.Combine(directory, ".nextcloud-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
            try
            {
                Write(shell, temporary, target);
                if (stale != null && File.Exists(stale) && Inspect(shell, stale, target) == LinkState.Owned)
                {
                    try
                    {
                        File.Replace(temporary, stale, null);
                        return stale;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A read-only or locked old link can coexist with a new numbered link.
                    }
                }
                for (int i = 1; ; i++)
                {
                    string name = i == 1 ? BaseName : $"{BaseName} ({i})";
                    string path = Path.Combine(directory, name + ".lnk");
                    if (Path.Exists(path)) continue;
                    try { File.Move(temporary, path, overwrite: false); return path; }
                    catch (IOException) when (Path.Exists(path)) { /* Another writer claimed the name. */ }
                }
            }
            finally { File.Delete(temporary); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static bool IsCandidate(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals(BaseName, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = BaseName + " (";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(')') &&
            int.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - 1), out int index) && index >= 2;
    }

    private enum LinkState { Foreign, Matching, Owned }

    private static LinkState Inspect(dynamic shell, string path, ShortcutTarget target)
    {
        dynamic? shortcut = null;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return LinkState.Foreign;
            shortcut = shell.CreateShortcut(path);
            string executable = shortcut.TargetPath;
            string arguments = shortcut.Arguments;
            if (string.Equals(executable, target.Executable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(arguments, target.Arguments, StringComparison.Ordinal))
                return LinkState.Matching;
            return string.Equals((string)shortcut.Description, Owner, StringComparison.Ordinal)
                ? LinkState.Owned : LinkState.Foreign;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            // A broken or inaccessible link may belong to the user. Leave it alone.
            return LinkState.Foreign;
        }
        finally { if (shortcut != null) Marshal.FinalReleaseComObject((object)shortcut); }
    }

    private static void Write(dynamic shell, string path, ShortcutTarget target)
    {
        dynamic shortcut = shell.CreateShortcut(path);
        try
        {
            shortcut.TargetPath = target.Executable;
            shortcut.Arguments = target.Arguments;
            shortcut.WorkingDirectory = target.WorkingDirectory;
            shortcut.Description = Owner;
            shortcut.Save();
        }
        finally { Marshal.FinalReleaseComObject((object)shortcut); }
    }
}
