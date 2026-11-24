using System.Windows.Automation;
using System.Windows.Media;
using NextcloudWidget.Core;

namespace NextcloudWidget.App;

internal class WidgetDialog : Window
{
    protected readonly StackPanel Fields = new();
    protected readonly WrapPanel Actions = new() { HorizontalAlignment = HorizontalAlignment.Right };

    protected WidgetDialog(Window owner, string title, double width, double height)
    {
        Owner = owner;
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
            MessageBox.Show(this, "请选择任务清单并填写标题。", Title);
            return;
        }
        if ((!string.IsNullOrWhiteSpace(day.Text) && day.SelectedDate == null) ||
            (day.SelectedDate == null && !string.IsNullOrWhiteSpace(time.Text)))
        {
            MessageBox.Show(this, "请填写有效的截止日期，或同时清空日期和时间。", Title);
            return;
        }
        if (day.SelectedDate is DateTime date)
        {
            var hours = TimeOnly.MinValue;
            if (!string.IsNullOrWhiteSpace(time.Text) &&
                !TimeOnly.TryParseExact(time.Text.Trim(), new[] { "H:mm", "HH:mm" },
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out hours))
            {
                MessageBox.Show(this, "请使用 24 小时制填写时间，例如 14:30。", Title);
                return;
            }
            var local = date.Date.Add(hours.ToTimeSpan());
            if (TimeZoneInfo.Local.IsInvalidTime(local))
            {
                MessageBox.Show(this, "该本地时间位于夏令时切换区间，请另选时间。", Title);
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
    public MailDialog(Window owner, MailItem message, string body) : base(owner, "阅读邮件", 530, 560)
    {
        Fields.Children.Add(Text(string.IsNullOrEmpty(message.Subject) ? "（无主题）" : message.Subject, 20));
        Fields.Children.Add(Text($"{message.Sender}   ·   {message.Date.ToLocalTime():g}"));
        var content = new TextBox { Text = body, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), AcceptsReturn = true, Padding = new Thickness(0, 8, 0, 0),
            Background = Brushes.Transparent };
        AutomationProperties.SetName(content, "邮件正文");
        Fields.Children.Add(content);
        ActionButton("关闭", (_, _) => Close()).IsCancel = true;
    }
}
