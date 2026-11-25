using System.ComponentModel;
using System.Windows.Automation;
using Microsoft.Win32;

namespace NextcloudWidget.App;

internal sealed class ProgramLocationDialog : WidgetDialog
{
    private readonly MainWindow host;
    private readonly string initialDirectory;
    private readonly Button choose;
    private readonly Button keep;
    private readonly TextBlock status = Text("");
    private bool copying;
    internal string? SelectedExecutable { get; private set; }

    internal ProgramLocationDialog(MainWindow host, Window owner, bool fromDownloads)
        : base(owner, "程序存放位置", 470, 355)
    {
        this.host = host;
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        initialDirectory = fromDownloads && Directory.Exists(programs) ? programs : Path.GetDirectoryName(Environment.ProcessPath!)!;
        Fields.Children.Add(Text(fromDownloads
            ? "小窗正在从下载文件夹运行。清理下载前，可以将程序复制到常用文件夹。"
            : "选择一个可写文件夹，小窗会复制程序并从新位置重启。"));
        Fields.Children.Add(Text($"当前程序　{Environment.ProcessPath}"));
        Fields.Children.Add(Text($"数据目录　{host.DataDirectory}"));
        Fields.Children.Add(Text("待办、邮件和设置保存在数据目录，登录凭据保存在 Windows 凭据管理器。更换程序位置后可以继续使用。"));
        status.Visibility = Visibility.Collapsed;
        Fields.Children.Add(status);
        keep = ActionButton("继续使用当前位置", (_, _) => { SelectedExecutable = Environment.ProcessPath; DialogResult = true; });
        keep.IsCancel = true;
        choose = ActionButton("选择文件夹并重启…", Choose_Click);
        AutomationProperties.SetAutomationId(keep, "KeepProgramLocation");
        AutomationProperties.SetAutomationId(choose, "ChooseProgramFolder");
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择小窗程序的存放文件夹", InitialDirectory = initialDirectory };
        if (picker.ShowDialog(this) != true || !host.CanExitWithDrafts(this)) return;
        var folder = picker.FolderName;
        copying = true;
        choose.IsEnabled = keep.IsEnabled = false;
        status.Visibility = Visibility.Visible;
        status.Text = "正在复制程序…";
        try
        {
            SelectedExecutable = await Task.Run(() => PortableLocation.CopyToFolder(Environment.ProcessPath!, folder));
        }
        catch (Exception ex)
        {
            status.Text = "无法使用所选位置。 " + MainWindow.ErrorText(ex);
        }
        finally
        {
            copying = false;
            choose.IsEnabled = keep.IsEnabled = true;
        }
        if (SelectedExecutable != null) DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (copying) e.Cancel = true;
        base.OnClosing(e);
    }
}
