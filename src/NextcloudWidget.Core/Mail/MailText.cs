using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace NextcloudWidget.Core.Mail;

public sealed record MailRun(string Text, string? Link = null, string? Image = null, double? Width = null);

public static class MailText
{
    // Only project inert text, links and image references. AngleSharp does not load the resources.
    public static IReadOnlyList<MailRun> Parse(string html, bool preserveNewlines = false)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        var output = new List<MailRun>();
        if (doc.Body is not { } body) return output;
        var nodes = new Stack<(INode Node, string? Link, bool Pre, bool End)>();
        nodes.Push((body, null, preserveNewlines, false));
        var spacing = 0;
        var buffer = new StringBuilder();
        string? lastLink = null;
        void Flush()
        {
            if (buffer.Length == 0) return;
            output.Add(new MailRun(buffer.ToString(), lastLink));
            buffer.Clear();
        }
        void Write(string text, string? link, string? image = null, double? width = null)
        {
            if (lastLink != link || image != null) Flush();
            lastLink = link;
            if (spacing != 0 && (output.Count != 0 || buffer.Length != 0))
                buffer.Append(spacing == 2 ? '\n' : ' ');
            spacing = 0;
            if (image == null) buffer.Append(text);
            else { Flush(); output.Add(new(text, link, image, width)); }
        }
        while (nodes.TryPop(out var next))
        {
            if (next.Node is IText leaf)
            {
                var text = leaf.Data;
                if (next.Pre)
                {
                    text = Regex.Replace(text.ReplaceLineEndings("\n"), @"\n(?:[^\S\n]*\n){2,}", "\n\n");
                    if (text.Length != 0) Write(text, next.Link);
                    continue;
                }
                int start = 0;
                for (var i = 0; i <= text.Length; i++)
                {
                    if (i < text.Length && !char.IsWhiteSpace(text[i])) continue;
                    if (i > start) Write(text[start..i], next.Link);
                    if (i < text.Length) spacing = Math.Max(spacing, 1);
                    start = i + 1;
                }
                continue;
            }
            if (next.Node is not IElement el) continue;
            var tag = el.LocalName;
            if (tag is "script" or "style" or "noscript" or "template" or "iframe" or "object" or "svg" or "head" || el.HasAttribute("hidden")) continue;
            var css = el.GetAttribute("data-original-style") ?? el.GetAttribute("style") ?? "";
            if (!(tag == "img" && el.HasAttribute("data-original-src")) && Regex.IsMatch(css, @"(?:^|;)\s*(?:display\s*:\s*none|visibility\s*:\s*hidden)\b", RegexOptions.IgnoreCase)) continue;
            if (tag is "p" or "div" or "li" or "tr" or "br" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "pre" or "blockquote" or "hr") spacing = 2;
            if (next.End)
            {
                if (tag is "td" or "th") spacing = Math.Max(1, spacing);
                continue;
            }
            var link = next.Link;
            if (tag == "a" && LinkTarget(el.GetAttribute("href")) is { } href) link = href.AbsoluteUri;
            string? src = null;
            if (tag == "img") src = el.GetAttribute("data-original-src") ?? el.GetAttribute("src");
            else
            {
                src = el.GetAttribute("background");
                var url = Regex.Match(css, @"(?:^|;)\s*background(?:-image)?\s*:[^;]*?url\(\s*['""]?(?<url>[^'""()]+?)['""]?\s*\)", RegexOptions.IgnoreCase);
                if (url.Success) src = url.Groups["url"].Value;
            }
            if (src != null && !src.Contains("blocked-image.png", StringComparison.OrdinalIgnoreCase))
            {
                double? Pixels(string name) => double.TryParse(el.GetAttribute(name), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)
                    && double.IsFinite(size) && size >= 0 ? size : null;
                var width = Pixels("width");
                if (!(width is < 5 && Pixels("height") is < 5))
                    Write(el.GetAttribute("alt")?.Trim() ?? "", link, src, width is > 0 ? Math.Min(1200, width.Value) : null);
            }
            nodes.Push((el, link, next.Pre, true));
            for (int i = el.ChildNodes.Length - 1; i >= 0; i--) nodes.Push((el.ChildNodes[i], link, next.Pre || tag == "pre", false));
        }
        Flush();
        return output;
    }

    public static Uri? LinkTarget(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "mailto" || (uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)) ? uri : null;

    public static string ToPlainText(string html, bool preserveNewlines = false) =>
        string.Concat(Parse(html, preserveNewlines).Select(r => r.Text)).TrimStart('\n').TrimEnd();
}
