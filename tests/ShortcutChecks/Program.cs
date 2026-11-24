using System.Runtime.InteropServices;
using NextcloudWidget.App;
using NextcloudWidget.Updates;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string token = Guid.NewGuid().ToString("N");
        Check(!UpdateClient.TrySignalHealthy(["--updated",token]), "a manually supplied update token without an active installer is not acknowledged");
        using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\NextcloudWidgetReady-" + token))
            Check(UpdateClient.TrySignalHealthy(["--updated",token]) && ready.WaitOne(0), "actual update event is signalled and confirmed");
        Check(DesktopShortcut.IsUserSession([]) &&
            DesktopShortcut.IsUserSession(["--updated",token], updateHandshake:true) &&
            !DesktopShortcut.IsUserSession(["--updated",token]) &&
            !DesktopShortcut.IsUserSession(["--updated","invalid"],updateHandshake:true) &&
            !DesktopShortcut.IsUserSession(["--updated",token,"--data-dir","profile"],updateHandshake:true) &&
            !DesktopShortcut.IsUserSession(["--data-dir","profile"]) &&
            !DesktopShortcut.IsUserSession(["--apply-widget-update"]) &&
            !DesktopShortcut.IsUserSession(["--smoke-test"]), "only a normal user profile or confirmed update is eligible");

        var root = Directory.CreateTempSubdirectory("nextcloud-shortcut-fixture-");
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true)!)!;
        try
        {
            string desktop = Directory.CreateDirectory(Path.Combine(root.FullName,"Fixture Desktop")).FullName;
            string first = Path.Combine(root.FullName,"First app.exe");
            string second = Path.Combine(root.FullName,"Moved app.exe");
            string appDll = Path.Combine(root.FullName,"Development app.dll");
            File.WriteAllText(first, "shortcut target fixture");
            File.WriteAllText(second, "shortcut target fixture");
            File.WriteAllText(appDll, "shortcut target fixture");
            var initial = ShortcutTarget.FromProcess(first,null);
            string link = DesktopShortcut.Ensure(desktop,initial);
            var data = Read(shell,link);
            Check(Path.GetFileName(link) == "Nextcloud 小窗.lnk" && data.Target == first &&
                data.Arguments == "" && data.Description == DesktopShortcut.Owner, "creates a real Windows shortcut with the expected target");
            var saved = File.ReadAllBytes(link);
            Check(DesktopShortcut.Ensure(desktop,initial)==link && File.ReadAllBytes(link).SequenceEqual(saved),
                "repeated close leaves a matching shortcut untouched");
            var moved = ShortcutTarget.FromProcess(second,null);
            Check(DesktopShortcut.Ensure(desktop,moved)==link && Read(shell,link).Target==second,
                "repairs an owned shortcut after the executable is moved");

            File.Delete(link);
            Foreign(shell,link,first);
            saved = File.ReadAllBytes(link);
            string fallback = DesktopShortcut.Ensure(desktop,moved);
            Check(Path.GetFileName(fallback)=="Nextcloud 小窗 (2).lnk" && File.ReadAllBytes(link).SequenceEqual(saved),
                "preserves a user's same-name link and creates an alternate name");
            File.Delete(link);
            Check(DesktopShortcut.Ensure(desktop,moved)==fallback && Directory.GetFiles(desktop,"*.lnk").Length==1,
                "reuses a numbered owned link even when the first name becomes available");

            File.Delete(fallback);
            Foreign(shell,link,second,arguments:"");
            saved = File.ReadAllBytes(link);
            Check(DesktopShortcut.Ensure(desktop,moved)==link && File.ReadAllBytes(link).SequenceEqual(saved),
                "reuses a functional user-created shortcut without claiming or rewriting it");
            var development = ShortcutTarget.FromProcess(Path.Combine(root.FullName,"dotnet.exe"),appDll);
            Check(development.Arguments=="\"" + appDll + "\"" && development.WorkingDirectory==root.FullName,
                "dotnet-hosted development launch includes the quoted application DLL");
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
            root.Delete(recursive:true);
        }
    }

    private static void Foreign(dynamic shell,string path,string target,string arguments = "--foreign")
    {
        dynamic shortcut = shell.CreateShortcut(path);
        try
        {
            shortcut.TargetPath=target;
            shortcut.Arguments=arguments;
            shortcut.Description="user-owned";
            shortcut.Save();
        }
        finally { Marshal.FinalReleaseComObject((object)shortcut); }
    }

    private static (string Target,string Arguments,string Description) Read(dynamic shell,string path)
    {
        dynamic shortcut = shell.CreateShortcut(path);
        try { return ((string)shortcut.TargetPath,(string)shortcut.Arguments,(string)shortcut.Description); }
        finally { Marshal.FinalReleaseComObject((object)shortcut); }
    }

    private static void Check(bool value,string description)
    {
        if (!value) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }
}
