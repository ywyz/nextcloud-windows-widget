using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Threading;
using NextcloudWidget.Core;
using NextcloudWidget.Updates;
using Forms = System.Windows.Forms;

namespace NextcloudWidget.App;

public partial class MainWindow : Window
{
    private sealed record TaskRow(TaskItem Item)
    {
        public string Title => Item.Title;
        public string DueText => Item.Due?.ToLocalTime().ToString("MM/dd HH:mm") ?? "";
        public string DetailText => Item.ListName;
        public string Group => Item.Completed ? "已完成" : Item.Due?.LocalDateTime.Date switch
        {
            null => "无截止日期",
            DateTime date when date < DateTime.Today => "已逾期",
            DateTime date when date == DateTime.Today => "今天",
            _ => "以后"
        };
        public string StateText => Item.SyncState switch
        {
            "pending" => "待上传", "conflict" => "有冲突", _ => Item.Completed ? "已完成" : ""
        };
    }
    private sealed record MailRow(MailItem Item)
    {
        public string Subject => string.IsNullOrWhiteSpace(Item.Subject) ? "（无主题）" : Item.Subject;
        public string Sender => Item.Sender;
        public string DateText => Item.Date.ToLocalTime().ToString("MM/dd HH:mm");
        public string UnreadText => Item.Unread ? "未读" : "";
    }
    private sealed record ListChoice(string Id, string Label);

    private readonly WidgetService service;
    private readonly Preferences preferences;
    private readonly bool smoke;
    private readonly UpdateClient updates = new();
    private readonly SemaphoreSlim updateLock = new(1, 1);
    private readonly SemaphoreSlim cacheRead = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource accountWork = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer cacheTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Forms.NotifyIcon? tray;
    private SettingsDialog? settings;
    private IReadOnlyList<TaskItem> allTasks = Array.Empty<TaskItem>();
    private IReadOnlyList<MailItem> allMail = Array.Empty<MailItem>();
    private IReadOnlyList<TaskList> taskLists = Array.Empty<TaskList>();
    private SyncInfo? lastSync;
    private Task? tasksWork, mailWork;
    private DateTimeOffset tasksNext, mailNext, updateRetryAt;
    private TimeSpan tasksRetry, mailRetry;
    private bool stopped, readingMail, rebuildingFilter;
    private DateTime displayedDay;
    private bool wasOnline = true;
    private string? tasksMessage, mailMessage;
    private ReleaseInfo? release;
    internal Version? AvailableVersion => release?.Version;

    internal MainWindow(WidgetService service, Preferences preferences, bool smoke)
    {
        this.service = service;
        this.preferences = preferences;
        this.smoke = smoke;
        InitializeComponent();
        var s = preferences.State;
        Topmost = s.Pinned;
        PinButton.IsChecked = s.Pinned;
        if (s.Width is double w && double.IsFinite(w)) Width = Math.Clamp(w, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        if (s.Height is double h && double.IsFinite(h)) Height = Math.Clamp(h, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        if (s.Left is double x && s.Top is double y && double.IsFinite(x) && double.IsFinite(y) &&
            x + Width >= SystemParameters.VirtualScreenLeft + 48 && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 48 &&
            y >= SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 48)
        {
            Left = x;
            Top = y;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        TasksList.SelectionChanged += (_, _) => UpdateButtons();
        MailList.SelectionChanged += (_, _) => UpdateButtons();
        TasksList.MouseDoubleClick += (_, e) =>
        {
            if (EditButton.IsEnabled && OnItem(TasksList, e)) EditTask_Click(this, new RoutedEventArgs());
        };
        TasksList.KeyDown += (_, e) => { if (e.Key == Key.Enter && EditButton.IsEnabled) { e.Handled = true; EditTask_Click(this, e); } };
        MailList.KeyDown += (_, e) => { if (e.Key == Key.Enter && ReadButton.IsEnabled) { e.Handled = true; ReadMail_Click(this, e); } };
        ShowCompleted.Click += (_, _) => PresentTasks();
        ListFilter.DisplayMemberPath = nameof(ListChoice.Label);
        ListFilter.SelectionChanged += (_, _) => { if (!rebuildingFilter) PresentTasks(); };
        timer.Tick += (_, _) => BackgroundTick();
        cacheTimer.Tick += async (_, _) =>
        {
            if ((mailWork is { IsCompleted: false } || tasksWork is { IsCompleted: false }) && cacheRead.CurrentCount > 0)
                await ReloadSafelyAsync();
        };
    }

    internal async Task InitializeAsync()
    {
        await ReloadAsync();
        if (smoke) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开小窗", null, (_, _) => RestoreWindow());
        menu.Items.Add("立即同步", null, (_, _) => Sync(true));
        menu.Items.Add("设置", null, (_, _) => OpenSettings_Click(this, new RoutedEventArgs()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        tray = new Forms.NotifyIcon { Text = "Nextcloud 小窗", Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) RestoreWindow(); };
    }

    internal void StartBackgroundWork()
    {
        NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
        wasOnline = NetworkInterface.GetIsNetworkAvailable();
        timer.Start();
        cacheTimer.Start();
        _ = Task.Run(UpdateClient.CleanOldDownloads);
        Dispatcher.BeginInvoke(new Action(BackgroundTick), DispatcherPriority.Background);
    }
    private void NetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (stopped) return;
            bool restored = !wasOnline && e.IsAvailable;
            wasOnline = e.IsAvailable;
            ShowStatus();
            if (restored) Sync(true);
        }));
    }

    private async void BackgroundTick()
    {
        if (stopped) return;
        if (DateTime.Today != displayedDay) PresentTasks();
        Sync(false);
        var now = DateTimeOffset.UtcNow;
        if (!wasOnline || updateLock.CurrentCount == 0 || release != null || now < updateRetryAt ||
            preferences.State.LastUpdateCheck > now.AddDays(-1)) return;
        updateRetryAt = now.AddHours(1);
        try { await CheckUpdatesAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The settings page provides the explicit retry and error message. */ }
    }

    private void Sync(bool force)
    {
        if (!service.IsConnected || !wasOnline || accountWork.IsCancellationRequested || stopped) { ShowStatus(); return; }
        var now = DateTimeOffset.UtcNow;
        if ((tasksWork?.IsCompleted ?? true) && (force || now >= tasksNext))
            tasksWork = SyncSourceAsync(true, accountWork.Token);
        if ((mailWork?.IsCompleted ?? true) && (force || now >= mailNext))
            mailWork = SyncSourceAsync(false, accountWork.Token);
    }

    private async Task SyncSourceAsync(bool tasks, CancellationToken token)
    {
        // Allow Sync() to assign its in-flight task before continuations update control state.
        await Task.Yield();
        if (token.IsCancellationRequested) return;
        if (tasks) tasksMessage = "正在同步待办…"; else mailMessage = "正在同步邮件…";
        ShowStatus();
        TimeSpan delay = TimeSpan.FromMinutes(5);
        try
        {
            if (tasks) await service.SyncTasksAsync(token); else await service.SyncMailAsync(token);
            if (tasks) { tasksMessage = null; tasksRetry = default; }
            else { mailMessage = null; mailRetry = default; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            string message = "同步失败 · " + ErrorText(ex);
            if (tasks) { tasksMessage = message; delay = tasksRetry = NextRetry(tasksRetry); }
            else { mailMessage = message; delay = mailRetry = NextRetry(mailRetry); }
        }
        finally
        {
            if (tasks) tasksNext = DateTimeOffset.UtcNow + delay;
            else mailNext = DateTimeOffset.UtcNow + delay;
            if (!stopped && !token.IsCancellationRequested) await ReloadSafelyAsync();
        }
    }
    private static TimeSpan NextRetry(TimeSpan previous) =>
        TimeSpan.FromMinutes(Math.Clamp(previous.TotalMinutes * 2, 1, 30));

    internal static string ErrorText(Exception ex)
    {
        var message = ex.Message.Trim();
        return message.Length <= 300 ? message : message[..300] + "…";
    }
    private async Task ReloadSafelyAsync()
    {
        try { await ReloadAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!stopped) { tasksMessage = "读取缓存失败 · " + ErrorText(ex); ShowStatus(); } }
    }
    private async Task ReloadAsync()
    {
        if (stopped) return;
        await cacheRead.WaitAsync(lifetime.Token);
        try
        {
            var token = lifetime.Token;
            var previousTasks = allTasks;
            var previousSync = lastSync;
            IReadOnlyList<MailItem> mail;
            if (service.IsConnected)
            {
                allTasks = await service.GetTasksAsync(token);
                taskLists = await service.GetTaskListsAsync(token);
                mail = await service.GetMailHeadersAsync(token);
                lastSync = await service.GetSyncInfoAsync(token);
            }
            else
            {
                allTasks = Array.Empty<TaskItem>();
                taskLists = Array.Empty<TaskList>();
                mail = Array.Empty<MailItem>();
                lastSync = null;
            }
            if (stopped) return;
            var filterChoices = taskLists.Select(l => new ListChoice(l.Id, l.Name))
                .OrderBy(l => l.Label).Prepend(new ListChoice("", "全部清单")).ToArray();
            bool filterChanged = !(ListFilter.ItemsSource is ListChoice[] oldChoices && oldChoices.SequenceEqual(filterChoices));
            if (filterChanged)
            {
                rebuildingFilter = true;
                var current = (ListFilter.SelectedItem as ListChoice)?.Id;
                ListFilter.ItemsSource = filterChoices;
                ListFilter.SelectedItem = filterChoices.FirstOrDefault(l => l.Id == current) ?? filterChoices[0];
                rebuildingFilter = false;
            }
            if (filterChanged || displayedDay != DateTime.Today || !allTasks.SequenceEqual(previousTasks) ||
                previousSync?.TasksLastSync != lastSync?.TasksLastSync || allTasks.Count == 0) PresentTasks();
            if (MailList.ItemsSource == null || !allMail.SequenceEqual(mail))
            {
                var selected = (MailList.SelectedItem as MailRow)?.Item.Id;
                var messages = mail.OrderByDescending(m => m.Date).Select(m => new MailRow(m)).ToArray();
                MailList.ItemsSource = messages;
                MailList.SelectedItem = messages.FirstOrDefault(m => m.Item.Id == selected);
                allMail = mail;
            }
            MailEmpty.Text = !service.IsConnected ? "在设置中连接 Nextcloud 后，这里会显示收件箱。" :
                lastSync?.MailLastSync is null ? "正在等待首次邮件同步。" : "收件箱中暂无已缓存的邮件。";
            MailEmpty.Visibility = mail.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowStatus();
            UpdateButtons();
            UpdateSettingsLabel();
        }
        finally { cacheRead.Release(); }
    }
    private void PresentTasks()
    {
        var selected = (TasksList.SelectedItem as TaskRow)?.Item.Id;
        var listId = (ListFilter.SelectedItem as ListChoice)?.Id;
        var rows = allTasks.Where(t => (string.IsNullOrEmpty(listId) || t.ListId == listId) &&
            (!t.Completed || ShowCompleted.IsChecked == true || t.SyncState == "conflict"))
            .OrderBy(t => t.Completed).ThenBy(t => t.Due == null).ThenBy(t => t.Due).ThenBy(t => t.Title)
            .Select(t => new TaskRow(t)).ToArray();
        var groups = new ListCollectionView(rows);
        groups.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskRow.Group)));
        TasksList.ItemsSource = groups;
        displayedDay = DateTime.Today;
        TasksList.SelectedItem = rows.FirstOrDefault(t => t.Item.Id == selected);
        TasksEmpty.Text = !service.IsConnected ? "连接 Nextcloud，即可在这里查看待办。" :
            lastSync?.TasksLastSync is null ? "正在等待首次任务同步。" :
            ShowCompleted.IsChecked == true ? "当前清单中没有任务。" : "待办已清空。";
        TasksEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }
    private void ShowStatus()
    {
        TaskStatus.Text = Status(tasksMessage, lastSync?.TasksLastSync);
        MailStatus.Text = Status(mailMessage, lastSync?.MailLastSync);
        TaskStatus.ToolTip = TaskStatus.Text;
        MailStatus.ToolTip = MailStatus.Text;
        SyncNowButton.IsEnabled = service.IsConnected;
    }
    private string Status(string? message, DateTimeOffset? last)
    {
        if (!service.IsConnected) return "未连接账户";
        var time = last.HasValue ? $"上次同步 {last.Value.ToLocalTime():MM/dd HH:mm}" : "尚无成功同步记录";
        if (!wasOnline) return "离线 · " + time;
        return message == null ? time : message + " · " + time;
    }
    private void UpdateButtons()
    {
        var t = (TasksList.SelectedItem as TaskRow)?.Item;
        EditButton.Content = t?.CanWrite != false ? "编辑" : "详情";
        EditButton.IsEnabled = t != null;
        CompleteButton.Content = t?.Completed == true ? "恢复待办" : "标记完成";
        CompleteButton.IsEnabled = t?.CanWrite == true && t.SyncState != "conflict";
        ResolveButton.Visibility = t?.SyncState == "conflict" ? Visibility.Visible : Visibility.Collapsed;
        ReadButton.IsEnabled = MailList.SelectedItem is MailRow && !readingMail;
        CreateButton.IsEnabled = service.IsConnected && taskLists.Any(l => l.CanWrite);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
        if (!smoke) preferences.Set(preferences.State with { Pinned = Topmost });
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Sync(true);
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        RestoreWindow();
        if (settings != null) { settings.Activate(); return; }
        if (OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsVisible) is Window current)
        {
            current.Activate();
            return;
        }
        settings = new SettingsDialog(this, service);
        try { settings.ShowDialog(); }
        finally { settings = null; }
    }
    private void NewTask_Click(object sender, RoutedEventArgs e) => EditTask(null);
    private void EditTask_Click(object sender, RoutedEventArgs e)
    {
        if (TasksList.SelectedItem is TaskRow row)
        {
            if (row.Item.SyncState == "conflict") ResolveTask_Click(sender, e);
            else EditTask(row.Item);
        }
    }
    private async void EditTask(TaskItem? task)
    {
        var dialog = new TaskDialog(this, task, taskLists);
        if (dialog.ShowDialog() != true) return;
        await LocalChangeAsync(async () =>
        {
            if (task == null) await service.CreateTaskAsync(dialog.ListId, dialog.TaskTitle, dialog.Notes, dialog.Due, accountWork.Token);
            else await service.EditTaskAsync(task.Id, dialog.TaskTitle, dialog.Notes, dialog.Due, accountWork.Token);
        });
    }
    private async void CompleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (TasksList.SelectedItem is TaskRow row && row.Item.CanWrite)
            await LocalChangeAsync(() => service.SetTaskCompletedAsync(row.Item.Id, !row.Item.Completed, accountWork.Token));
    }
    private async void ResolveTask_Click(object sender, RoutedEventArgs e)
    {
        if (TasksList.SelectedItem is not TaskRow row) return;
        try
        {
            var remote = await service.GetConflictServerVersionAsync(row.Item.Id, accountWork.Token);
            var dialog = new ConflictDialog(this, row.Item, remote);
            if (dialog.ShowDialog() == true)
                await LocalChangeAsync(() => service.ResolveConflictAsync(row.Item.Id, dialog.KeepLocal, accountWork.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, ErrorText(ex), "无法打开冲突"); }
    }
    private async Task LocalChangeAsync(Func<Task> edit)
    {
        try
        {
            await edit();
            await ReloadAsync();
            if (wasOnline && (tasksWork?.IsCompleted ?? true))
                tasksWork = SyncSourceAsync(true, accountWork.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, ErrorText(ex), "待办保存失败"); }
    }
    private static bool OnItem(ListBox list, MouseButtonEventArgs e) =>
        e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(list, element) is ListBoxItem;
    private void Mail_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OnItem(MailList, e)) ReadMail_Click(sender, e);
    }
    private async void ReadMail_Click(object sender, RoutedEventArgs e)
    {
        if (MailList.SelectedItem is not MailRow row || readingMail) return;
        readingMail = true;
        UpdateButtons();
        var token = accountWork.Token;
        try
        {
            string body;
            if (!string.IsNullOrEmpty(row.Item.Body)) body = row.Item.Body;
            else
            {
                MailStatus.Text = "正在读取邮件正文…";
                body = await service.GetMailBodyAsync(row.Item.Id, token);
            }
            if (!stopped && !token.IsCancellationRequested)
                new MailDialog(this, row.Item, string.IsNullOrWhiteSpace(body) ? "（正文为空）" : body).ShowDialog();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, ErrorText(ex), "邮件正文暂不可用"); }
        finally { readingMail = false; UpdateButtons(); ShowStatus(); }
    }

    internal async Task AccountConnectedAsync()
    {
        tasksMessage = mailMessage = null;
        tasksNext = mailNext = default;
        await ReloadAsync();
        Sync(true);
    }
    internal async Task DisconnectAsync(CancellationToken token)
    {
        accountWork.Cancel();
        try
        {
            await Task.WhenAll(tasksWork ?? Task.CompletedTask, mailWork ?? Task.CompletedTask);
            await service.DisconnectAsync(token);
        }
        finally
        {
            accountWork.Dispose();
            accountWork = new CancellationTokenSource();
            tasksMessage = mailMessage = null;
            await ReloadAsync();
        }
    }
    internal async Task CheckUpdatesAsync(CancellationToken ct)
    {
        await updateLock.WaitAsync(ct);
        try
        {
            if (release == null) release = await updates.CheckAsync(ct);
            preferences.Set(preferences.State with { LastUpdateCheck = DateTimeOffset.UtcNow });
            UpdateSettingsLabel();
            if (release != null && tray != null) tray.Text = $"Nextcloud 小窗 · 有新版本 {release.Version}";
        }
        finally { updateLock.Release(); }
    }
    private void UpdateSettingsLabel() =>
        SettingsButton.Content = !service.IsConnected ? "连接 Nextcloud" : release == null ? "设置" : "设置 · 有更新";
    internal async Task InstallUpdateAsync(Version selectedVersion, CancellationToken ct)
    {
        var selectedRelease = release;
        if (selectedRelease == null || selectedRelease.Version != selectedVersion)
            throw new InvalidOperationException("可用版本已改变，请重新检查更新。");
        EnsureEditorsClosed();
        var path = await updates.StageAsync(selectedRelease, ct);
        ct.ThrowIfCancellationRequested();
        EnsureEditorsClosed();
        updates.BeginInstall(path);
        Stop();
        Application.Current.Shutdown();
    }
    private void EnsureEditorsClosed()
    {
        if (OwnedWindows.OfType<TaskDialog>().Any(w => w.IsVisible))
            throw new InvalidOperationException("请先保存或关闭正在编辑的待办，再安装更新。");
    }
    internal void RestoreWindow()
    {
        if (stopped) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void ExitApp()
    {
        if (OwnedWindows.OfType<TaskDialog>().Any(w => w.IsVisible) &&
            MessageBox.Show(this, "当前正在编辑待办。要放弃尚未保存的编辑并退出吗？", "退出小窗",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Stop();
        Application.Current.Shutdown();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!stopped && !smoke)
        {
            e.Cancel = true;
            SavePosition();
            Hide();
        }
        base.OnClosing(e);
    }
    private void SavePosition()
    {
        if (smoke || WindowState != WindowState.Normal) return;
        preferences.Set(preferences.State with { Width = ActualWidth, Height = ActualHeight, Left = Left, Top = Top });
    }
    internal void Stop()
    {
        if (stopped) return;
        stopped = true;
        SavePosition();
        timer.Stop();
        cacheTimer.Stop();
        NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
        lifetime.Cancel();
        accountWork.Cancel();
        service.Dispose();
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
    }
    internal void CheckControls()
    {
        UpdateLayout();
        if (!IsLoaded || ActualWidth < MinWidth || TasksTab == null || MailTab == null || SettingsButton == null ||
            TasksList == null || MailList == null || ShowCompleted == null || TaskStatus == null || ListFilter == null)
            throw new InvalidOperationException($"主窗口未能完整加载。 Loaded={IsLoaded}, Width={ActualWidth}, Height={ActualHeight}, Visible={IsVisible}");
        var settingsPage = new SettingsDialog(this, service);
        settingsPage.Show();
        settingsPage.UpdateLayout();
        settingsPage.Close();
        var editor = new TaskDialog(this, null, taskLists);
        editor.Show();
        editor.UpdateLayout();
        editor.Close();
    }
}
