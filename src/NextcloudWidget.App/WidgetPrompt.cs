using System.Windows.Automation;

namespace NextcloudWidget.App;

internal sealed class WidgetPrompt : WidgetDialog
{
    private MessageBoxResult choice = MessageBoxResult.Cancel;
    private WidgetPrompt(Window owner, string text, string title, string? confirmation, bool defaultYes)
        : base(owner, title, 375, 200)
    {
        MinHeight = 160;
        Fields.Children.Add(Text(text));
        if (confirmation == null)
        {
            var ok = ActionButton("确定", (_, _) => { choice = MessageBoxResult.OK; Close(); });
            ok.IsDefault = ok.IsCancel = true;
            return;
        }
        var cancel = ActionButton("取消", (_, _) => { choice = MessageBoxResult.No; Close(); });
        cancel.IsCancel = true;
        cancel.IsDefault = !defaultYes;
        AutomationProperties.SetAutomationId(cancel, "CancelAction");
        var accept = ActionButton(confirmation, (_, _) => { choice = MessageBoxResult.Yes; Close(); });
        accept.IsDefault = defaultYes;
        AutomationProperties.SetAutomationId(accept, "ConfirmAction");
        Loaded += (_, _) => (defaultYes ? accept : cancel).Focus();
    }

    internal static MessageBoxResult Show(Window owner, string text, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var prompt = new WidgetPrompt(owner, text, title, buttons == MessageBoxButton.YesNo ? "确定" : null,
            defaultResult == MessageBoxResult.Yes);
        prompt.ShowDialog();
        return prompt.choice;
    }
    internal static bool ConfirmTrash(Window owner, int count)
    {
        var prompt = new WidgetPrompt(owner, $"将选中的 {count} 封邮件移到废纸篓？离线时会在下次连线后同步。", "移到废纸篓", "移到废纸篓", false);
        prompt.ShowDialog();
        return prompt.choice == MessageBoxResult.Yes;
    }
}
