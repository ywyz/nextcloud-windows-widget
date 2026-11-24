using System.Security.Cryptography;
using System.Text;
using NextcloudWidget.Core;
using NextcloudWidget.Updates;

namespace NextcloudWidget.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showWait;
    private bool ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UpdateClient.RunMaintenance(e.Args, out var exitCode))
        {
            Shutdown(exitCode);
            return;
        }
        var smoke = Array.Exists(e.Args, a => a == "--smoke-test");
        try
        {
            var dirArg = Array.IndexOf(e.Args, "--data-dir");
            if (dirArg >= 0 && dirArg + 1 >= e.Args.Length)
                throw new ArgumentException("--data-dir 需要目录。");
            var directory = Path.GetFullPath(dirArg >= 0 ? e.Args[dirArg + 1] :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextcloudWidget"));
            Directory.CreateDirectory(directory);
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..20];
            if (!smoke && !AcquireInstance(id))
            {
                Shutdown();
                return;
            }
            var window = new MainWindow(new WidgetService(directory), new Preferences(directory), smoke,
                DesktopShortcut.IsUserSession(e.Args));
            MainWindow = window;
            window.Show();
            await window.InitializeAsync();
            // On an empty profile all reads can complete synchronously. Give WPF its first
            // Loaded and Render dispatch before validating the tree or signalling the updater.
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (smoke)
            {
                window.CheckControls();
                Shutdown(0);
                return;
            }
            bool updated = UpdateClient.TrySignalHealthy(e.Args);
            if (updated && DesktopShortcut.IsUserSession(e.Args, updateHandshake: true))
                window.EnableDesktopShortcut();
            window.StartBackgroundWork();
        }
        catch (Exception ex)
        {
            if (smoke || Array.Exists(e.Args, a => a == "--updated")) Console.Error.WriteLine(ex);
            else MessageBox.Show(ex.Message, "Nextcloud 小窗无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private bool AcquireInstance(string id)
    {
        // The event is created first so a concurrent second launch can always notify the first.
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NextcloudWidget-show-" + id);
        instanceMutex = new Mutex(true, @"Local\NextcloudWidget-" + id, out ownsMutex);
        if (!ownsMutex)
        {
            showEvent.Set();
            return false;
        }
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => ((MainWindow?)MainWindow)?.RestoreWindow())), null, -1, false);
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.Stop();
        showWait?.Unregister(null);
        showEvent?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
