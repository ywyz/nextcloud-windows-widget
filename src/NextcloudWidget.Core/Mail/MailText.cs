using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace NextcloudWidget.Core.Mail;

internal static class MailText
{
    internal static string ToPlainText(string html)
    {
        // A parser alone neither loads external resources nor executes scripts.
        using var document = new HtmlParser().ParseDocument(html);
        var text = new StringBuilder();
        var nodes = new Stack<(INode Node, bool Closing)>();
        if (document.Body is { } body) nodes.Push((body, false));
        while (nodes.TryPop(out var next))
        {
            if (next.Node is IText leaf)
            {
                text.Append(leaf.Data);
                continue;
            }
            if (next.Node is not IElement element) continue;
            var tag = element.LocalName;
            if (tag is "script" or "style" or "template" or "noscript" or "svg" or "iframe" or "object" or "head"
                || element.HasAttribute("hidden")) continue;
            if (next.Closing)
            {
                if (tag == "a")
                {
                    var href = element.GetAttribute("href")?.Trim();
                    if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto"
                        && !string.Equals(element.TextContent.Trim(), href, StringComparison.Ordinal))
                        text.Append(" <").Append(href).Append('>');
                }
                if (tag is "td" or "th") text.Append(' ');
            }
            if (tag is "p" or "div" or "li" or "br" or "tr" or "blockquote" or "hr" or "pre"
                or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
            }
            if (next.Closing) continue;
            nodes.Push((element, true));
            for (var i = element.ChildNodes.Length - 1; i >= 0; --i)
                nodes.Push((element.ChildNodes[i], false));
        }
        return text.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }
}
