using System.Diagnostics;

namespace NextcloudWidget.Updates;

public sealed partial class UpdateClient
{
    private const string StagePrefix = ".nextcloud-widget-update-";
    private static readonly object StageGate = new();

    public void BeginInstall(string stagedExe) => BeginInstall(stagedExe, CancellationToken.None);

    public void BeginInstall(string stagedExe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string stage = ValidateStagedFile(stagedExe);
        var version = PublisherTrust.CheckExecutable(stagedExe);
        ct.ThrowIfCancellationRequested();
        if (version <= PublisherTrust.CheckExecutable(Executable))
            throw new InvalidDataException("更新版本不匹配。");
        lock (StageGate)
        {
            ct.ThrowIfCancellationRequested();
            ValidateStagedFile(stagedExe);
            string helper = Path.Combine(stage, "installer.exe");
            File.Copy(Executable, helper);
            try
            {
                ct.ThrowIfCancellationRequested();
                using var process = Launch(helper, ["--apply-widget-update", Environment.ProcessId.ToString(), Executable, .. DataDirectoryArguments()]);
            }
            catch { TryDelete(helper); throw; }
        }
    }

    public void AbandonStaged(string stagedExe)
    {
        lock (StageGate)
        {
            string stage = ValidateStagedFile(stagedExe);
            if (File.Exists(Path.Combine(stage, "installer.exe")) || File.Exists(Path.Combine(stage, "previous.exe")))
                throw new InvalidOperationException("安装助手正在使用此目录。");
            CleanStaging(stage);
        }
    }

    private string ValidateStagedFile(string stagedExe)
    {
        string full = Path.GetFullPath(stagedExe);
        string stage = Path.GetDirectoryName(full)!;
        ValidateStage(stage, parentDirectory);
        if (Path.GetFileName(full) != ReleaseInfo.Asset || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("临时更新包文件名或位置无效。");
        return stage;
    }

    public static bool RunMaintenance(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "--apply-widget-update") return false;
        try { Apply(args); }
        catch (Exception e)
        {
            exitCode = 1;
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(Executable)!, "update-error.txt"), e.Message); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return true;
    }

    private static void Apply(string[] args)
    {
        if (args.Length is not (3 or 5) || !int.TryParse(args[1], out int parentId) ||
            (args.Length == 5 && args[3] != "--data-dir"))
            throw new ArgumentException("更新参数错误。");
        string target = Path.GetFullPath(args[2]);
        string stage = Path.GetDirectoryName(Executable)!;
        ValidateStage(stage, Path.GetDirectoryName(target)!);
        if (!Path.GetFileName(Executable).Equals("installer.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新助手目录错误。");
        string candidate = Path.Combine(stage, ReleaseInfo.Asset);
        string backup = Path.Combine(stage, "previous.exe");
        using (var parent = GetProcess(parentId))
        {
            if (parent != null && !parent.HasExited &&
                (!string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase) || !parent.WaitForExit(60000)))
                throw new IOException("请先退出当前程序。");
        }
        string[] dataArgs = args[3..];
        if (PublisherTrust.CheckExecutable(candidate) <= PublisherTrust.CheckExecutable(target))
            throw new InvalidDataException("新版文件校验失败。");
        string token = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token));
        File.Replace(candidate, target, backup);
        Process? child = null;
        try
        {
            child = Launch(target, ["--updated", token, .. dataArgs]);
            for (int i = 0; i < 120; i++)
            {
                if (ready.WaitOne(500)) { TryDelete(backup); return; }
                if (child.HasExited) break;
            }
            throw new IOException("新版未能启动，已尝试恢复旧版。");
        }
        catch
        {
            if (child is { HasExited: false }) { child.Kill(); child.WaitForExit(); }
            File.Replace(backup, target, null);
            using var previous = Launch(target, dataArgs);
            throw;
        }
        finally { child?.Dispose(); }
    }

    public static void SignalHealthy(string[] args) => TrySignalHealthy(args);

    public static bool TrySignalHealthy(string[] args)
    {
        if (args.Length < 2 || args[0] != "--updated" || !Guid.TryParseExact(args[1], "N", out _)) return false;
        if (EventWaitHandle.TryOpenExisting(EventName(args[1]), out var ready))
            using (ready) return ready.Set();
        return false;
    }

    private static string EventName(string token) => @"Local\NextcloudWidgetReady-" + token;

    private static Process? GetProcess(int id)
    {
        try { return Process.GetProcessById(id); }
        catch (ArgumentException) { return null; }
    }

    private static string[] DataDirectoryArguments()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--data-dir");
        return i >= 0 && i + 1 < args.Length ? ["--data-dir", Path.GetFullPath(args[i + 1])] : [];
    }

    private static Process Launch(string path, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("无法启动程序。");
    }

    private static void ValidateStage(string stage, string parent)
    {
        string name = Path.GetFileName(stage);
        if (!string.Equals(Path.GetDirectoryName(stage), parent, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(StagePrefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[StagePrefix.Length..], "N", out _) ||
            (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("临时更新目录无效。");
    }

    // Running helpers cannot remove their own executable on Windows.
    public static void CleanOldDownloads()
    {
        string parent = Path.GetDirectoryName(Executable)!;
        try
        {
            foreach (var stage in Directory.EnumerateDirectories(parent, StagePrefix + "*"))
            {
                try
                {
                    ValidateStage(stage, parent);
                    if (Directory.GetLastWriteTimeUtc(stage) < DateTime.UtcNow.AddDays(-2) &&
                        !File.Exists(Path.Combine(stage, "previous.exe"))) CleanStaging(stage);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void CleanStaging(string stage)
    {
        foreach (var file in new[] { ReleaseInfo.Asset, "installer.exe", "update-error.txt" })
            TryDelete(Path.Combine(stage, file));
        try { Directory.Delete(stage); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
