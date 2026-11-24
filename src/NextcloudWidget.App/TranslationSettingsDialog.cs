using System.Windows.Automation;
using NextcloudWidget.Core.Translation;

namespace NextcloudWidget.App;

internal sealed class TranslationSettingsDialog : WidgetDialog
{
    private readonly TranslationPreferences preferences;
    private readonly TextBox endpoint = new();
    private readonly TextBox model = new();
    private readonly TextBox language = new() { Text = TranslationOptions.DefaultLanguage };
    private readonly PasswordBox key = new();
    private readonly CheckBox remove = new() { Content = "移除此地址已保存的 API Key", Margin = new Thickness(0, 6, 0, 4) };
    private readonly TextBlock feedback = Text("");

    internal TranslationSettingsDialog(Window owner, TranslationPreferences preferences)
        : base(owner, "邮件翻译设置", 465, 535)
    {
        this.preferences = preferences;
        Fields.Children.Add(Text("选择兼容 OpenAI Chat Completions 的服务。只有点击邮件中的“翻译”才会发送该邮件正文。"));
        Field("API 基址", endpoint);
        Fields.Children.Add(Text("例如 http://localhost:11434/v1 或 https://api.openai.com/v1。", 12));
        Field("模型名称", model);
        Field("目标语言", language);
        Field("API Key（可选）", key);
        Fields.Children.Add(Text("留空会保留此地址已有的密钥。本地无密钥服务可直接保存。", 12));
        Fields.Children.Add(remove);
        Fields.Children.Add(feedback);
        AutomationProperties.SetAutomationId(endpoint, "TranslationEndpoint");
        AutomationProperties.SetAutomationId(model, "TranslationModel");
        AutomationProperties.SetAutomationId(language, "TranslationLanguage");
        AutomationProperties.SetAutomationId(key, "TranslationApiKey");
        AutomationProperties.SetAutomationId(remove, "RemoveTranslationKey");
        ActionButton("取消", (_, _) => Close()).IsCancel = true;
        var save = ActionButton("保存", Save);
        save.IsDefault = true;
        AutomationProperties.SetAutomationId(save, "SaveTranslationSettings");
        try
        {
            if (preferences.Load() is { } options)
            {
                endpoint.Text = options.BaseUrl.AbsoluteUri;
                model.Text = options.Model;
                language.Text = options.TargetLanguage;
            }
        }
        catch (Exception)
        {
            feedback.Text = "无法读取现有翻译设置，请重新填写。";
        }
        endpoint.TextChanged += (_, _) => { key.Clear(); remove.IsChecked = false; };
        remove.Checked += (_, _) => { key.Clear(); key.IsEnabled = false; };
        remove.Unchecked += (_, _) => key.IsEnabled = true;
        Closed += (_, _) => key.Clear();
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = new TranslationOptions(endpoint.Text, model.Text, language.Text);
            preferences.Save(options, key.Password, remove.IsChecked == true);
            DialogResult = true;
        }
        catch (ArgumentException ex) { feedback.Text = ex.Message; }
        catch (Exception) { feedback.Text = "无法保存设置，请检查配置目录以及 Windows 凭据管理器。"; }
    }
}
