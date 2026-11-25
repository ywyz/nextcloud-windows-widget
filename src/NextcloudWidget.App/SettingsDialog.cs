using System.Diagnostics;
using System.Windows.Automation;
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
    private readonly Button location = new() { Content = "更改程序存放位置…" };
    private readonly Button translation = new() { Content = "配置翻译模型…" };
    private readonly Button cancelUpdate = new() { Content = "取消更新", Visibility = Visibility.Collapsed };
    private readonly TextBlock updateStatus = Text("");
    private readonly TextBlock transferDetails = Text("", 12);
    private readonly ProgressBar updateProgress = new() { Minimum = 0, Maximum = 100, Height = 7,
        Margin = new Thickness(0, 0, 0, 5), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? updateOperation;
    private readonly CancellationTokenSource lifetime = new();
    private Version? displayedUpdate;
    private bool working;

    public SettingsDialog(MainWindow host, WidgetService service) : base(host, "设置", 410, Math.Min(565, SystemParameters.WorkArea.Height))
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
        Fields.Children.Add(Text("邮件翻译", 19));
        System.Windows.Automation.AutomationProperties.SetAutomationId(translation, "TranslationSettingsButton");
        Configure(translation, (_, _) => new TranslationSettingsDialog(this,
            new TranslationPreferences(host.DataDirectory)).ShowDialog());
        Fields.Children.Add(Text("软件更新", 19));
        var version = typeof(SettingsDialog).Assembly.GetName().Version;
        Fields.Children.Add(Text($"当前版本 {version?.ToString(3)}"));
        var updateButtons = new WrapPanel();
        Fields.Children.Add(updateButtons);
        Configure(update, Update_Click, updateButtons);
        Configure(recheck, Update_Click, updateButtons);
        Configure(cancelUpdate, CancelUpdate_Click, updateButtons);
        recheck.Content = "重新检查更新";
        updateStatus.Margin = new Thickness(0, 6, 0, 5);
        updateStatus.Visibility = transferDetails.Visibility = Visibility.Collapsed;
        foreach (var (control, id) in new (FrameworkElement, string)[] {
            (update, "UpdateAction"), (recheck, "RecheckUpdate"), (cancelUpdate, "CancelUpdate"),
            (updateStatus, "UpdateStatus"), (updateProgress, "UpdateProgress"), (transferDetails, "UpdateTransfer") })
            AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(updateProgress, "更新进度");
        Fields.Children.Add(updateStatus);
        Fields.Children.Add(updateProgress);
        Fields.Children.Add(transferDetails);
        Fields.Children.Add(Text("存放位置", 19));
        Fields.Children.Add(Text($"数据目录　{host.DataDirectory}"));
        if (host.CanChooseProgramLocation)
        {
            Fields.Children.Add(Text($"当前程序　{Environment.ProcessPath}"));
            AutomationProperties.SetAutomationId(location, "ProgramLocationButton");
            Configure(location, (_, _) => host.ChooseProgramLocation(this));
        }
        ActionButton("关闭", (_, _) => Close()).IsCancel = true;
        Closed += (_, _) => lifetime.Cancel();
        RefreshButtons();
    }

    private void Configure(Button button, RoutedEventHandler click, Panel? parent = null)
    {
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 5, 6, 0);
        button.Padding = new Thickness(10, 5, 10, 5);
        button.Click += click;
        (parent ?? Fields).Children.Add(button);
    }
    private void RefreshButtons()
    {
        address.IsReadOnly = service.IsConnected || working;
        account.Content = service.IsConnected ? "断开并清除此账户缓存" : "在浏览器中授权";
        account.IsEnabled = reauthorize.IsEnabled = update.IsEnabled = recheck.IsEnabled = translation.IsEnabled = location.IsEnabled = !working;
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
                var mailPending = await service.GetPendingMailOperationCountAsync(lifetime.Token);
                string prompt = pending == 0 && mailPending == 0 ? "断开后会清除本机缓存的任务、邮件和登录信息。继续吗？" :
                    $"有 {pending} 条待办更改尚未同步或存在冲突，另有 {mailPending} 条邮件操作尚未同步。断开会丢弃这些操作，并清除本机缓存和登录信息。继续吗？";
                if (WidgetPrompt.Show(this, prompt, "断开账户", MessageBoxButton.YesNo, MessageBoxImage.Warning,
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
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateOperation = operation;
        working = true;
        RefreshButtons();
        updateStatus.Visibility = updateProgress.Visibility = cancelUpdate.Visibility = Visibility.Visible;
        cancelUpdate.IsEnabled = true;
        updateProgress.IsIndeterminate = true;
        updateProgress.Value = 0;
        transferDetails.Text = "";
        transferDetails.Visibility = Visibility.Collapsed;
        updateStatus.Text = selectedVersion == null ? "正在检查更新…" : $"正在准备下载 {selectedVersion.ToString(3)}…";
        updateProgress.BringIntoView();
        string? retryNote = null;
        bool preparingRestart = false;
        var progress = new Progress<UpdateProgress>(state =>
        {
            if (!ReferenceEquals(updateOperation, operation) || operation.IsCancellationRequested || preparingRestart) return;
            if (state.Phase == UpdatePhase.FallingBack)
                retryNote = string.IsNullOrEmpty(state.Detail) ? "加速器不可用" : $"加速器请求失败（{state.Detail}）";
            else if (state.Phase == UpdatePhase.Retrying)
                retryNote = state.Detail;
            ShowProgress(state, retryNote);
        });
        try
        {
            if (selectedVersion == null)
            {
                await host.CheckUpdatesAsync(operation.Token, progress);
                updateStatus.Text = host.AvailableVersion == null ? "当前已是最新版本。" :
                    $"发现新版本 {host.AvailableVersion.ToString(3)}，点击安装即可下载。";
            }
            else
            {
                await host.InstallUpdateAsync(selectedVersion, operation.Token, progress, () =>
                {
                    preparingRestart = true;
                    updateProgress.IsIndeterminate = true;
                    transferDetails.Visibility = Visibility.Collapsed;
                    updateStatus.Text = "正在复核当前程序与新版的 Windows 签名，准备重启小窗…";
                });
            }
        }
        catch (OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested)
                updateStatus.Text = operation.IsCancellationRequested ? "已取消本次更新操作。" : "更新请求超时，请重试。";
        }
        catch (Exception ex) { updateStatus.Text = "更新失败。 " + MainWindow.ErrorText(ex); }
        finally
        {
            updateOperation = null;
            updateProgress.Visibility = transferDetails.Visibility = cancelUpdate.Visibility = Visibility.Collapsed;
            working = false;
            RefreshButtons();
        }
    }

    private async void CancelUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (updateOperation is not { IsCancellationRequested: false } operation) return;
        cancelUpdate.IsEnabled = false;
        updateStatus.Text = "正在取消，等待当前步骤结束…";
        updateProgress.IsIndeterminate = true;
        await operation.CancelAsync();
    }

    private void ShowProgress(UpdateProgress state, string? retryNote)
    {
        string source = state.Source == UpdateSource.Accelerator ? "GitHub 加速器" : "GitHub 直连";
        string phase = state.Phase switch
        {
            UpdatePhase.Checking => "正在检查更新",
            UpdatePhase.Downloading => "正在下载安装包",
            UpdatePhase.Verifying => state.Detail ?? "正在验证更新",
            UpdatePhase.Retrying => "正在重试",
            UpdatePhase.FallingBack => "正在切换下载来源",
            _ => "正在更新"
        };
        updateStatus.Text = $"{phase} · {source}";
        var details = new List<string>();
        updateProgress.IsIndeterminate = state.Phase != UpdatePhase.Downloading || state.TotalBytes is not > 0;
        if (state.Phase == UpdatePhase.Downloading)
        {
            if (state.TotalBytes is > 0)
            {
                double percent = Math.Clamp(state.BytesReceived * 100d / state.TotalBytes.Value, 0, 100);
                updateProgress.Value = percent;
                details.Add($"{percent:0}%   {Size(state.BytesReceived)} / {Size(state.TotalBytes.Value)}");
            }
            else details.Add($"已下载 {Size(state.BytesReceived)}");
            if (double.IsFinite(state.BytesPerSecond) && state.BytesPerSecond > 0)
                details.Add($"{Size(state.BytesPerSecond)}/秒");
            else if (state.TotalBytes is long size && state.BytesReceived < size)
                details.Add("等待数据");
        }
        if (state.Attempt > 1) details.Add($"第 {state.Attempt} 次尝试");
        if (!string.IsNullOrWhiteSpace(retryNote)) details.Add(retryNote);
        transferDetails.Text = string.Join(" · ", details);
        transferDetails.Visibility = details.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private static string Size(double bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000:0.0} GB",
        >= 1_000_000 => $"{bytes / 1_000_000:0.0} MB",
        >= 1_000 => $"{bytes / 1_000:0} KB",
        _ => $"{bytes:0} B"
    };
}
