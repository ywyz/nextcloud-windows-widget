using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace NextcloudWidget.Core.Mail;

internal static class MailClient
{
    private const int InboxLimit = 50;

    internal static async Task<IReadOnlyList<MailItem>> FetchAsync(
        HttpClient client, Uri server, IReadOnlyList<MailItem> previous, CancellationToken ct)
    {
        var cached = previous.ToDictionary(item => item.Id);
        var result = new Dictionary<string, MailItem>();
        var accounts = await GetDataAsync(client, server, "account/list", ct);
        foreach (var account in accounts.EnumerateArray())
        {
            var accountId = PositiveId(account, "id");
            var folders = await GetDataAsync(client, server, $"ocs/mailboxes?accountId={accountId}", ct);
            foreach (var inbox in folders.EnumerateArray().Where(IsInbox))
            {
                var inboxId = PositiveId(inbox, "databaseId");
                var path = $"ocs/mailboxes/{inboxId}/messages?view=singleton&limit={InboxLimit}";
                var headers = await GetDataAsync(client, server, path, ct);
                foreach (var header in headers.EnumerateArray().Take(InboxLimit))
                {
                    var id = PositiveId(header, "databaseId");
                    var subject = Text(header, "subject");
                    var date = DateTimeOffset.FromUnixTimeSeconds(header.GetProperty("dateInt").GetInt64());
                    var unread = !header.GetProperty("flags").GetProperty("seen").GetBoolean();
                    var body = cached.GetValueOrDefault(id)?.Body ?? "";
                    result[id] = new MailItem(id, string.IsNullOrWhiteSpace(subject) ? "（无主题）" : subject,
                        Sender(header.GetProperty("from")), date, body, unread);
                }
            }
        }
        return result.Values.OrderByDescending(message => message.Date).ThenBy(message => message.Id).ToArray();
    }

    internal static async Task<MailContent> FetchContentAsync(HttpClient client, Uri server, string id, CancellationToken ct)
    {
        var message = await GetDataAsync(client, server, $"message/{PositiveMessageNumber(id)}", ct);
        if (!message.TryGetProperty("body", out var body) || body.ValueKind == JsonValueKind.Null)
        {
            if (message.TryGetProperty("smime", out var smime) &&
                smime.TryGetProperty("isEncrypted", out var encrypted) && encrypted.ValueKind == JsonValueKind.True)
                return new MailContent("此邮件已加密，服务器无法提供正文。", "");
            throw new InvalidDataException("服务器未返回邮件正文。");
        }

        var content = body.GetString() ?? "";
        var plain = message.TryGetProperty("hasHtmlBody", out var rich) && rich.ValueKind == JsonValueKind.False;
        var signature = Text(message, "signature");
        if (signature.Length > 0)
            content += "<br>--<br>" + signature;
        if (!plain && content.Contains("cid:", StringComparison.OrdinalIgnoreCase)
            && message.TryGetProperty("inlineAttachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            using var doc = new HtmlParser().ParseDocument(content);
            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attachment in attachments.EnumerateArray())
            {
                var part = Text(attachment, "id");
                if (MailImages.ValidPart(part)) parts[Text(attachment, "cid").Trim('<', '>')] = part;
            }
            foreach (var image in doc.QuerySelectorAll("img[src]"))
            {
                var src = image.GetAttribute("src")!;
                if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) && parts.TryGetValue(src[4..].Trim('<', '>'), out var part))
                    image.SetAttribute("src", new Uri(Authorization.BaseUri(server), $"index.php/apps/mail/api/messages/{id}/attachment/{part}").AbsoluteUri);
            }
            content = doc.Body?.InnerHtml ?? content;
        }
        var text = MailText.ToPlainText(content, preserveNewlines: plain);
        return new MailContent(text.Length == 0 ? "（此邮件无正文）" : text, plain ? "" : content);
    }

    internal static async Task<bool> IsMessageGoneAsync(HttpClient client, Uri server, string id, CancellationToken ct)
    {
        var result = await GetDataAsync(client, server, $"message/{PositiveMessageNumber(id)}", ct, checkMissing: true);
        return result.ValueKind == JsonValueKind.Undefined;
    }

    private static string PositiveMessageNumber(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException("邮件编号无效。", nameof(id));

    internal static bool IsInbox(JsonElement folder)
    {
        if (string.Equals(Text(folder, "name"), "inbox", StringComparison.OrdinalIgnoreCase))
            return true;
        return folder.TryGetProperty("specialUse", out var roles) && roles.ValueKind == JsonValueKind.Array &&
            roles.EnumerateArray().Any(role => string.Equals(role.GetString()?.TrimStart('\\'),
                "inbox", StringComparison.OrdinalIgnoreCase));
    }

    private static string Sender(JsonElement addresses)
    {
        var labels = addresses.EnumerateArray().Select(address =>
        {
            var label = Text(address, "label");
            var email = Text(address, "email");
            if (label.Length == 0) return email;
            return email.Length == 0 || label.Equals(email, StringComparison.OrdinalIgnoreCase)
                ? label : $"{label} <{email}>";
        });
        return string.Join("、", labels.Where(label => !string.IsNullOrWhiteSpace(label)));
    }

    private static string PositiveId(JsonElement value, string field)
    {
        var id = value.GetProperty(field).GetInt64();
        return id > 0 ? id.ToString(CultureInfo.InvariantCulture) : throw new InvalidDataException("邮件接口返回了无效编号。");
    }

    private static string Text(JsonElement value, string field) =>
        value.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "" : "";

    internal static async Task<JsonElement> GetDataAsync(HttpClient client, Uri server, string relativePath,
        CancellationToken ct, bool checkMissing = false)
    {
        var url = server.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/ocs/v2.php/apps/mail/" + relativePath;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OCS-APIRequest", "true");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && relativePath == "account/list")
            throw new HttpRequestException("Nextcloud Mail 接口不可用。请确认服务器已启用 Mail 5.2 或更新版本。",
                null, response.StatusCode);
        bool missing = checkMissing && response.StatusCode == HttpStatusCode.NotFound;
        if (!missing) response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var envelope = json.RootElement.GetProperty("ocs");
        var meta = envelope.GetProperty("meta");
        if (missing)
        {
            if (meta.TryGetProperty("statuscode", out var code) && code.TryGetInt32(out var number) && number == 404)
                return default;
            throw new InvalidDataException("未能确认服务器上的邮件是否已移走。");
        }
        if (Text(meta, "status") != "ok")
            throw new InvalidDataException("Nextcloud Mail 返回同步错误。");
        return envelope.GetProperty("data").Clone();
    }
}
