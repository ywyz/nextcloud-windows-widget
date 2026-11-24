using System.Windows.Automation;
using System.Windows.Media;
using System.Net.Http;
using NextcloudWidget.Core;

namespace NextcloudWidget.App;

internal class WidgetDialog : Window
{
    protected readonly StackPanel Fields = new();
    protected readonly WrapPanel Actions = new() { HorizontalAlignment = HorizontalAlignment.Right };

    protected WidgetDialog(Window owner, string title, double width, double height)
    {
        Owner = owner;
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = Math.Min(width, 350);
        MinHeight = Math.Min(height, 300);
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 14;
        var panel = new DockPanel { Margin = new Thickness(18) };
        Actions.Margin = new Thickness(0, 14, 0, 0);
        DockPanel.SetDock(Actions, Dock.Bottom);
        panel.Children.Add(Actions);
        panel.Children.Add(new ScrollViewer { Content = Fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
    }

    protected static TextBlock Text(string value, double size = 14) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 7)
    };
    protected void Field(string name, Control control)
    {
        Fields.Children.Add(new Label { Content = name, Target = control, Padding = new Thickness(0, 7, 0, 3) });
        control.Padding = new Thickness(5, 4, 5, 4);
        AutomationProperties.SetName(control, name);
        Fields.Children.Add(control);
    }
    protected Button ActionButton(string label, RoutedEventHandler click)
    {
        var button = new Button { Content = label, MinHeight = 31, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(6, 3, 0, 0) };
        button.Click += click;
        Actions.Children.Add(button);
        return button;
    }
}

internal sealed class TaskDialog : WidgetDialog
{
    private readonly ComboBox lists = new() { DisplayMemberPath = nameof(TaskList.Name) };
    private readonly TextBox title = new();
    private readonly TextBox notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 76 };
    private readonly DatePicker day = new() { IsTodayHighlighted = true };
    private readonly TextBox time = new();
    private readonly TaskItem? original;
    public string ListId => ((TaskList)lists.SelectedItem).Id;
    public string TaskTitle => title.Text.Trim();
    public string Notes => notes.Text;
    public DateTimeOffset? Due { get; private set; }

    public TaskDialog(Window owner, TaskItem? task, IReadOnlyList<TaskList> choices)
        : base(owner, task == null ? "新建待办" : task.CanWrite ? "编辑待办" : "待办详情", 400, 530)
    {
        original = task;
        if (task is null)
        {
            lists.ItemsSource = choices.Where(l => l.CanWrite).ToArray();
            lists.SelectedIndex = 0;
            Field("任务清单", lists);
        }
        else Fields.Children.Add(Text(task.ListName, 16));
        Field("标题", title);
        Field("备注", notes);
        Field("截止日期（可留空）", day);
        Field("截止时间（24 小时制，如 14:30）", time);
        title.Text = task?.Title ?? "";
        notes.Text = task?.Description ?? "";
        day.SelectedDate = task?.Due?.LocalDateTime.Date;
        time.Text = task?.Due?.ToLocalTime().ToString("HH:mm") ?? "";
        var canEdit = task?.CanWrite != false;
        title.IsReadOnly = notes.IsReadOnly = !canEdit;
        time.IsReadOnly = !canEdit;
        day.IsEnabled = canEdit;
        var cancel = ActionButton(canEdit ? "取消" : "关闭", (_, _) => Close());
        cancel.IsCancel = true;
        if (canEdit) ActionButton("保存", Save).IsDefault = true;
        Loaded += (_, _) => title.Focus();
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (TaskTitle.Length == 0 || (original is null && lists.SelectedItem == null))
        {
            WidgetPrompt.Show(this, "请选择任务清单并填写标题。", Title);
            return;
        }
        if ((!string.IsNullOrWhiteSpace(day.Text) && day.SelectedDate == null) ||
            (day.SelectedDate == null && !string.IsNullOrWhiteSpace(time.Text)))
        {
            WidgetPrompt.Show(this, "请填写有效的截止日期，或同时清空日期和时间。", Title);
            return;
        }
        if (day.SelectedDate is DateTime date)
        {
            var hours = TimeOnly.MinValue;
            if (!string.IsNullOrWhiteSpace(time.Text) &&
                !TimeOnly.TryParseExact(time.Text.Trim(), new[] { "H:mm", "HH:mm" },
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out hours))
            {
                WidgetPrompt.Show(this, "请使用 24 小时制填写时间，例如 14:30。", Title);
                return;
            }
            var local = date.Date.Add(hours.ToTimeSpan());
            if (TimeZoneInfo.Local.IsInvalidTime(local))
            {
                WidgetPrompt.Show(this, "该本地时间位于夏令时切换区间，请另选时间。", Title);
                return;
            }
            var previous = original?.Due?.ToLocalTime();
            Due = previous?.Date == local.Date && previous.Value.ToString("HH:mm") == local.ToString("HH:mm")
                ? original!.Due : new DateTimeOffset(local);
        }
        DialogResult = true;
    }
}

internal sealed class ConflictDialog : WidgetDialog
{
    public bool KeepLocal { get; private set; }
    public ConflictDialog(Window owner, TaskItem local, TaskItem? remote)
        : base(owner, "解决待办冲突", 430, 460)
    {
        Fields.Children.Add(Text("这条待办在本机和服务器上都有更改。", 16));
        AddVersion("本机版本", local);
        AddVersion("服务器版本", remote);
        ActionButton("稍后处理", (_, _) => Close()).IsCancel = true;
        ActionButton("采用服务器版本", (_, _) => { KeepLocal = false; DialogResult = true; });
        ActionButton("保留本机版本", (_, _) => { KeepLocal = true; DialogResult = true; });
    }
    private void AddVersion(string heading, TaskItem? item)
    {
        var label = Text(heading);
        label.FontWeight = FontWeights.SemiBold;
        Fields.Children.Add(label);
        Fields.Children.Add(Text(item == null ? "服务器已删除此任务，或清单已不可用。" :
            string.Join(Environment.NewLine, new[] { item.Title, item.Completed ? "已完成" : "待办",
                item.Due?.ToLocalTime().ToString("g"), item.Description }.Where(v => !string.IsNullOrWhiteSpace(v)))));
    }
}

internal sealed class MailDialog : WidgetDialog
{
    private readonly string original;
    private readonly TranslationPreferences preferences;
    private readonly TextBox translation = BodyBox("");
    private readonly TextBlock status = Text("");
    private readonly TabControl tabs = new();
    private readonly TabItem translated = new() { Header = "译文", IsEnabled = false };
    private readonly Button translate, cancel;
    private CancellationTokenSource? request;
    private bool closed;

    public MailDialog(Window owner, MailItem message, string body) : base(owner, "阅读邮件", 565, 580)
    {
        original = body;
        var host = (owner as MainWindow) ?? (MainWindow)Application.Current.MainWindow;
        preferences = new TranslationPreferences(host.DataDirectory);
        Fields.Children.Add(Text(string.IsNullOrEmpty(message.Subject) ? "（无主题）" : message.Subject, 20));
        Fields.Children.Add(Text($"{message.Sender}   ·   {message.Date.ToLocalTime():g}"));
        var source = BodyBox(body);
        AutomationProperties.SetName(source, "邮件正文");
        AutomationProperties.SetAutomationId(source, "MailOriginalBody");
        AutomationProperties.SetName(translation, "邮件译文");
        AutomationProperties.SetAutomationId(translation, "MailTranslatedBody");
        tabs.Items.Add(new TabItem { Header = "原文", Content = source });
        translated.Content = translation;
        tabs.Items.Add(translated);
        tabs.SelectedIndex = 0;
        Fields.Children.Add(tabs);
        AutomationProperties.SetAutomationId(status, "TranslationStatus");
        Fields.Children.Add(status);
        translate = ActionButton("翻译", Translate);
        AutomationProperties.SetAutomationId(translate, "TranslateMail");
        translate.IsEnabled = !string.IsNullOrWhiteSpace(body) && body is not ("（正文为空）" or "（此邮件无正文）" or "此邮件已加密，服务器无法提供正文。");
        cancel = ActionButton("取消翻译", (_, _) => request?.Cancel());
        AutomationProperties.SetAutomationId(cancel, "CancelTranslation");
        cancel.Visibility = Visibility.Collapsed;
        ActionButton("关闭", (_, _) => Close()).IsCancel = true;
        Closed += (_, _) => { closed = true; request?.Cancel(); };
    }

    private static TextBox BodyBox(string value) => new()
    {
        Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true, Padding = new Thickness(7), MinHeight = 150
    };

    private async void Translate(object sender, RoutedEventArgs args)
    {
        if (request != null) return;
        using var cancellation = new CancellationTokenSource();
        request = cancellation;
        translate.IsEnabled = false;
        try
        {
            var options = preferences.Load();
            if (options == null)
            {
                var settings = new TranslationSettingsDialog(this, preferences);
                status.Text = settings.ShowDialog() == true ? "模型已配置。点击“翻译”发送本封邮件正文。" : "请先配置翻译模型。";
                return;
            }
            status.Text = $"正在使用 {options.Model} 翻译为{options.TargetLanguage}…";
            cancel.Visibility = Visibility.Visible;
            string? key = preferences.Keys.Read(options);
            var result = await new NextcloudWidget.Core.Translation.MailTranslator().TranslateAsync(options, key, original, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            translation.Text = result;
            translated.IsEnabled = true;
            tabs.SelectedItem = translated;
            status.Text = $"已翻译为{options.TargetLanguage}。";
        }
        catch (OperationCanceledException) { if (!closed) status.Text = cancellation.IsCancellationRequested ? "翻译已取消。" : "模型请求超时，请重试。"; }
        catch (InvalidDataException ex) { if (!closed) status.Text = ex.Message; }
        catch (System.Text.Json.JsonException) { if (!closed) status.Text = "模型或设置返回了无效的 JSON。"; }
        catch (HttpRequestException) { if (!closed) status.Text = "无法连接翻译模型，请检查地址和网络。"; }
        catch (Exception) { if (!closed) status.Text = "翻译失败，请检查翻译设置及 Windows 凭据。"; }
        finally
        {
            request = null;
            if (!closed) { translate.IsEnabled = true; cancel.Visibility = Visibility.Collapsed; }
        }
    }
}
