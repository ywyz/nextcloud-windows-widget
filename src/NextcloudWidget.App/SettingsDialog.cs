using System.Diagnostics;
using NextcloudWidget.Core;
using NextcloudWidget.Updates;

namespace NextcloudWidget.App;

internal sealed class SettingsDialog : WidgetDialog
{
    private readonly MainWindow host;
    private readonly WidgetService service;
    private readonly TextBox address = new();
    private readonly TextBlock feedback = Text("");
    private readonly Button account = new();
    private readonly Button reauthorize = new();
    private readonly Button update = new();
    private readonly Button recheck = new();
    private readonly CancellationTokenSource lifetime = new();
    private Version? displayedUpdate;
    private bool working;

    public SettingsDialog(MainWindow host, WidgetService service) : base(host, "设置", 410, 390)
    {
        this.host = host;
        this.service = service;
        Fields.Children.Add(Text("Nextcloud 账户", 19));
        Field("服务器地址", address);
        address.Text = service.Server?.AbsoluteUri ?? "";
        Configure(account, Account_Click);
        Configure(reauthorize, Reauthorize_Click);
        reauthorize.Content = "重新授权并保留缓存";
        feedback.Margin = new Thickness(0, 9, 0, 12);
        Fields.Children.Add(feedback);
        Fields.Children.Add(Text("软件更新", 19));
        var version = typeof(SettingsDialog).Assembly.GetName().Version;
        Fields.Children.Add(Text($"当前版本 {version?.ToString(3)}"));
        Configure(update, Update_Click);
        Configure(recheck, Update_Click);
        recheck.Content = "重新检查更新";
        ActionButton("关闭", (_, _) => Close()).IsCancel = true;
        Closed += (_, _) => lifetime.Cancel();
        RefreshButtons();
    }

    private void Configure(Button button, RoutedEventHandler click)
    {
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 5, 0, 0);
        button.Padding = new Thickness(10, 5, 10, 5);
        button.Click += click;
        Fields.Children.Add(button);
    }
    private void RefreshButtons()
    {
        address.IsReadOnly = service.IsConnected || working;
        account.Content = service.IsConnected ? "断开并清除此账户缓存" : "在浏览器中授权";
        account.IsEnabled = reauthorize.IsEnabled = update.IsEnabled = recheck.IsEnabled = !working;
        reauthorize.Visibility = service.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        displayedUpdate = host.AvailableVersion;
        update.Content = displayedUpdate == null ? "检查更新" : $"安装 {displayedUpdate.ToString(3)}";
        recheck.Visibility = displayedUpdate == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        if (working) return;
        working = true;
        RefreshButtons();
        try
        {
            if (service.IsConnected)
            {
                var pending = (await service.GetTasksAsync(lifetime.Token)).Count(t => t.SyncState is "pending" or "conflict");
                string prompt = pending == 0 ? "断开后会清除本机缓存的任务、邮件和登录信息。继续吗？" :
                    $"有 {pending} 条本地更改尚未同步或存在冲突。断开会丢弃这些更改，并清除任务、邮件和登录信息。继续吗？";
                if (MessageBox.Show(this, prompt, "断开账户", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
                await host.DisconnectAsync(lifetime.Token);
                address.Clear();
                feedback.Text = "账户已断开。";
            }
            else
            {
                if (!Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out var url) ||
                    url.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(url.Host) ||
                    !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
                {
                    feedback.Text = "请输入完整的 HTTPS 服务器地址，可以包含 Nextcloud 所在的子目录。";
                    return;
                }
                await AuthorizeAsync(url);
            }
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) feedback.Text = "授权超时，请重试。"; }
        catch (Exception ex) { feedback.Text = "账户操作失败。 " + MainWindow.ErrorText(ex); }
        finally { working = false; RefreshButtons(); }
    }
    private async Task AuthorizeAsync(Uri url)
    {
        feedback.Text = "请在系统浏览器中完成授权。关闭设置可取消等待。";
        await service.ConnectAsync(url, login => Process.Start(new ProcessStartInfo(login.AbsoluteUri) {
            UseShellExecute = true }), lifetime.Token);
        address.Text = service.Server?.AbsoluteUri ?? address.Text;
        feedback.Text = "已连接，正在准备同步。";
        await host.AccountConnectedAsync();
    }
    private async void Reauthorize_Click(object sender, RoutedEventArgs e)
    {
        if (working || service.Server is not Uri url) return;
        working = true;
        RefreshButtons();
        try { await AuthorizeAsync(url); }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) feedback.Text = "授权超时，请重试。"; }
        catch (Exception ex) { feedback.Text = "重新授权失败。 " + MainWindow.ErrorText(ex); }
        finally { working = false; RefreshButtons(); }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (working) return;
        var selectedVersion = ReferenceEquals(sender, update) ? displayedUpdate : null;
        working = true;
        RefreshButtons();
        try
        {
            if (selectedVersion == null)
            {
                feedback.Text = "正在检查更新…";
                await host.CheckUpdatesAsync(lifetime.Token);
                feedback.Text = host.AvailableVersion == null ? "当前已是最新版本。" : "发现新版本，点击安装即可更新。";
            }
            else
            {
                feedback.Text = "正在下载安装包并验证签名…";
                await host.InstallUpdateAsync(selectedVersion, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) feedback.Text = "检查更新超时。"; }
        catch (Exception ex) { feedback.Text = "更新失败。 " + MainWindow.ErrorText(ex); }
        finally { working = false; RefreshButtons(); }
    }
}
