using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

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

    internal static async Task<string> FetchBodyAsync(HttpClient client, Uri server, string id, CancellationToken ct)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            throw new ArgumentException("邮件编号无效。", nameof(id));

        var message = await GetDataAsync(client, server, $"message/{number.ToString(CultureInfo.InvariantCulture)}", ct);
        if (!message.TryGetProperty("body", out var body) || body.ValueKind == JsonValueKind.Null)
        {
            if (message.TryGetProperty("smime", out var smime) &&
                smime.TryGetProperty("isEncrypted", out var encrypted) && encrypted.ValueKind == JsonValueKind.True)
                return "此邮件已加密，服务器无法提供正文。";
            throw new InvalidDataException("服务器未返回邮件正文。");
        }

        var content = body.GetString() ?? "";
        var signature = Text(message, "signature");
        if (signature.Length > 0)
            content += "\n-- \n" + signature;
        var text = MailText.ToPlainText(content);
        return text.Length == 0 ? "（此邮件无正文）" : text;
    }

    private static bool IsInbox(JsonElement folder)
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

    private static async Task<JsonElement> GetDataAsync(HttpClient client, Uri server, string relativePath, CancellationToken ct)
    {
        var url = server.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/ocs/v2.php/apps/mail/" + relativePath;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OCS-APIRequest", "true");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && relativePath == "account/list")
            throw new HttpRequestException("Nextcloud Mail 接口不可用。请确认服务器已启用 Mail 5.2 或更新版本。",
                null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var envelope = json.RootElement.GetProperty("ocs");
        var meta = envelope.GetProperty("meta");
        if (Text(meta, "status") != "ok")
            throw new InvalidDataException("Nextcloud Mail 返回同步错误。");
        return envelope.GetProperty("data").Clone();
    }
}
