using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NextcloudWidget.Core;
using NextcloudWidget.Core.Mail;

namespace NextcloudWidget.App;

internal sealed class MailBodyView : DockPanel
{
    private sealed record Picture(InlineUIContainer Slot, string Alt, double? Width, MailImageSource Source);
    private readonly RichTextBox reader = new() { IsReadOnly = true, IsDocumentEnabled = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        BorderThickness = new Thickness(0), Padding = new Thickness(8) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<(Image Image, double Width)> displayed = [];
    private readonly WidgetService service;
    private readonly string messageId;
    private readonly int decodeLimit;

    internal MailBodyView(string html, Uri server, string id, WidgetService service)
    {
        this.service = service;
        messageId = id;
        reader.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        reader.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        reader.Document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontSize = 14 };
        var paragraph = new Paragraph { Margin = new Thickness(0), LineStackingStrategy = LineStackingStrategy.MaxHeight };
        reader.Document.Blocks.Add(paragraph);
        var local = new List<Picture>();
        var remote = new List<Picture>();
        foreach (var run in MailText.Parse(html))
        {
            Inline inline;
            if (run.Image != null && MailImages.Resolve(run.Image, server, id) is { } source)
            {
                var slot = new InlineUIContainer(new TextBlock { Text = run.Text });
                (source.Kind == MailImageKind.Remote ? remote : local).Add(new(slot, run.Text, run.Width, source));
                inline = slot;
            }
            else inline = new Run(run.Text);
            if (MailText.LinkTarget(run.Link) is { } href)
            {
                var link = new Hyperlink(inline) { ToolTip = href.AbsoluteUri };
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                link.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(href.AbsoluteUri) { UseShellExecute = true }); }
                    catch (Win32Exception) { WidgetPrompt.Show(Window.GetWindow(this), "无法打开此链接的默认应用。", "邮件链接"); }
                };
                inline = link;
            }
            paragraph.Inlines.Add(inline);
        }
        // Divide the decoded-pixel budget across the message. Small newsletters keep
        // their full preview resolution and long photo albums still show every picture.
        decodeLimit = Math.Clamp((int)Math.Sqrt(12_000_000d / Math.Max(1, remote.Count + local.Count)), 1, 1400);
        if (remote.Count != 0)
        {
            var load = new Button { Content = "显示远程图片", HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(5), ToolTip = "从发件人引用的网站加载此封邮件的图片" };
            AutomationProperties.SetAutomationId(load, "LoadRemoteMailImages");
            load.Click += async (_, _) =>
            {
                load.IsEnabled = false;
                load.Content = "正在加载图片…";
                await LoadPictures(remote);
                if (remote.Count == 0) load.Visibility = Visibility.Collapsed;
                else { load.Content = "重试未加载的图片"; load.IsEnabled = true; }
            };
            SetDock(load, Dock.Top);
            Children.Add(load);
        }
        AutomationProperties.SetName(reader, "邮件正文");
        AutomationProperties.SetAutomationId(reader, "MailOriginalBody");
        Children.Add(reader);
        reader.SizeChanged += (_, _) => { foreach (var (image, width) in displayed) Fit(image, width); };
        Loaded += Start;
        async void Start(object sender, RoutedEventArgs args)
        {
            Loaded -= Start;
            await LoadPictures(local);
        }
    }

    private async Task LoadPictures(List<Picture> pictures)
    {
        var token = lifetime.Token;
        foreach (var picture in pictures.ToArray())
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var bitmap = await Task.Run(async () =>
                {
                    var bytes = picture.Source.Kind switch
                    {
                        MailImageKind.Attachment => await service.GetMailInlineImageAsync(messageId, picture.Source.Value, token),
                        MailImageKind.Remote => await MailImages.DownloadRemoteAsync(picture.Source.Value, token),
                        _ => Convert.FromBase64String(picture.Source.Value)
                    };
                    return Decode(bytes, decodeLimit);
                }, token);
                if (token.IsCancellationRequested) return;
                var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                var width = picture.Width ?? bitmap.PixelWidth;
                Fit(image, width);
                displayed.Add((image, width));
                AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(picture.Alt) ? "邮件图片" : picture.Alt);
                AutomationProperties.SetAutomationId(image, "MailBodyImage");
                picture.Slot.Child = image;
                pictures.Remove(picture);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // A broken image should never hide the rest of the message.
                picture.Slot.Child = new TextBlock { Text = string.IsNullOrWhiteSpace(picture.Alt) ? "［图片未加载］" : picture.Alt,
                    ToolTip = "图片未能加载" };
            }
        }
    }

    private void Fit(Image image, double width) => image.Width = Math.Max(1, Math.Min(width, reader.ActualWidth - 40));

    private static BitmapImage Decode(byte[] bytes, int maxDimension)
    {
        if (!MailImages.IsRaster(bytes)) throw new InvalidDataException("不支持的图片格式。");
        using var stream = new MemoryStream(bytes);
        var info = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if ((long)info.PixelWidth * info.PixelHeight > 32_000_000 || Math.Max(info.PixelWidth, info.PixelHeight) > 16000)
            throw new InvalidDataException("图片尺寸过大。");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (info.PixelWidth > maxDimension && info.PixelWidth >= info.PixelHeight) bitmap.DecodePixelWidth = maxDimension;
        else if (info.PixelHeight > maxDimension) bitmap.DecodePixelHeight = maxDimension;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal void Stop() { lifetime.Cancel(); lifetime.Dispose(); }
}
