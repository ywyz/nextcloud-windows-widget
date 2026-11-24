using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NextcloudWidget.Core.Mail;

internal sealed class MailTrashUnavailableException(string message) : InvalidOperationException(message);

// One instance per sync pass. Folder IDs and account preferences are shared across
// the batch, then discarded so later syncs see changes made in Nextcloud Mail.
internal sealed class MailActionClient(HttpClient http, Uri server)
{
    private Dictionary<long, long?>? inboxToTrash;

    internal async Task MarkReadAsync(string id, CancellationToken ct)
    {
        _ = await RequestAsync(HttpMethod.Put, $"messages/{Number(id)}/flags",
            new { flags = new { seen = true } }, ct).ConfigureAwait(false);
    }

    internal async Task MoveToTrashAsync(string id, CancellationToken ct)
    {
        string path = $"messages/{Number(id)}";
        var envelope = await RequestAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        long source = RequiredId(envelope, "mailboxId");
        inboxToTrash ??= await LoadDestinationsAsync(ct).ConfigureAwait(false);
        if (!inboxToTrash.TryGetValue(source, out long? trash))
            throw new MailTrashUnavailableException("邮件已离开收件箱，未执行移动。");
        if (trash == null || trash.Value == source)
            throw new MailTrashUnavailableException("该邮箱没有可用的废纸篓。请在 Nextcloud Mail 的账户设置中指定废纸篓后重试。");
        _ = await RequestAsync(HttpMethod.Post, path + "/move", new { destFolderId = trash.Value }, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<long, long?>> LoadDestinationsAsync(CancellationToken ct)
    {
        var map = new Dictionary<long, long?>();
        var accounts = await RequestAsync(HttpMethod.Get, "accounts", null, ct).ConfigureAwait(false);
        foreach (var account in accounts.EnumerateArray())
        {
            long accountId = RequiredId(account, "accountId");
            var all = await MailClient.GetDataAsync(http, server, $"ocs/mailboxes?accountId={accountId}", ct).ConfigureAwait(false);
            var folders = all.EnumerateArray().Where(f => RequiredId(f, "accountId") == accountId).ToArray();
            long? configured = Id(account, "trashMailboxId");
            // Trust an explicit account preference only if the folder belongs to
            // this account. Otherwise only recognize an unambiguous IMAP Trash role.
            long? trash = null;
            if (configured != null)
            {
                if (folders.Any(f => RequiredId(f, "databaseId") == configured && !MailClient.IsInbox(f)))
                    trash = configured;
            }
            else
            {
                var roles = folders.Where(f => !MailClient.IsInbox(f) && HasTrashRole(f)).ToArray();
                if (roles.Length == 1) trash = RequiredId(roles[0], "databaseId");
            }
            foreach (var inbox in folders.Where(MailClient.IsInbox)) map[RequiredId(inbox, "databaseId")] = trash;
        }
        return map;
    }

    private static bool HasTrashRole(JsonElement folder) =>
        folder.TryGetProperty("specialUse", out var roles) && roles.ValueKind == JsonValueKind.Array &&
        roles.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String &&
            string.Equals(e.GetString()?.TrimStart('\\'), "trash", StringComparison.OrdinalIgnoreCase));

    private static long? Id(JsonElement row, string field) =>
        row.TryGetProperty(field, out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value) && value > 0 ? value : null;

    private static long RequiredId(JsonElement row, string field) =>
        Id(row, field) ?? throw new InvalidDataException("服务器返回了无效的邮件或文件夹编号。");

    private static long Number(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n :
            throw new InvalidDataException("邮件编号无效。");

    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var url = new Uri(Authorization.BaseUri(server), "index.php/apps/mail/api/" + path);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OCS-APIRequest", "true");
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent && method != HttpMethod.Get) return default;
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("Nextcloud Mail 未确认此操作，请检查登录状态与服务器版本。");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct).ConfigureAwait(false);
        return json.RootElement.Clone();
    }
}
