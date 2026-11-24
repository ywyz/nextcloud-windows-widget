using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NextcloudWidget.Core.Mail;

internal static class MailTrashProtocolChecks
{
    internal static async Task RunAsync()
    {
        using var handler = new Folders();
        using var http = new HttpClient(handler);
        var actions = new MailActionClient(http, new Uri("https://fixture.test/sub"));
        await actions.MoveToTrashAsync("1", default);
        await actions.MoveToTrashAsync("2", default);
        foreach (string id in new[] { "3", "4" })
        {
            try { await actions.MoveToTrashAsync(id, default); throw new Exception("Invalid trash accepted."); }
            catch (MailTrashUnavailableException) { }
        }
        if (handler.AccountReads != 1 || handler.FolderReads != 3 || handler.Moved != 2)
            throw new Exception("A batch must share discovered destinations and only move to a unique same-account trash.");
        Console.WriteLine("PASS Unique IMAP trash, ambiguous roles, cross-account configuration, subdirectory routes and shared folder discovery.");
    }

    private sealed class Folders : HttpMessageHandler
    {
        internal int AccountReads, FolderReads, Moved;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.PathAndQuery;
            if (request.Headers.GetValues("OCS-APIRequest").Single() != "true") throw new Exception("CSRF header missing.");
            if (request.Method == HttpMethod.Post && (path == "/sub/index.php/apps/mail/api/messages/1/move" ||
                path == "/sub/index.php/apps/mail/api/messages/2/move"))
            {
                var json = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                if (json.GetProperty("destFolderId").GetInt32() != 12) throw new Exception("Wrong trash destination.");
                Moved++;
                return Reply(Array.Empty<object>());
            }
            if (request.Method != HttpMethod.Get) throw new Exception($"Unexpected mutation: {request.Method}");
            if (path == "/sub/index.php/apps/mail/api/accounts")
            {
                AccountReads++;
                return Reply(new[] { new { accountId = 1, trashMailboxId = (int?)null },
                    new { accountId = 2, trashMailboxId = (int?)null }, new { accountId = 3, trashMailboxId = (int?)12 } });
            }
            if (path.StartsWith("/sub/ocs/v2.php/apps/mail/ocs/mailboxes?accountId="))
            {
                FolderReads++;
                int accountId = int.Parse(path.Split('=').Last());
                object Folder(int suffix, string name, params string[] roles) =>
                    new { accountId, databaseId = accountId * 10 + suffix, name, specialUse = roles };
                var data = new List<object> { Folder(1, "INBOX", "inbox"), Folder(2, "Corbeille", "\\Trash") };
                if (accountId == 2) data.Add(Folder(3, "Papierkorb", "trash"));
                return Reply(new { ocs = new { meta = new { status = "ok" }, data } });
            }
            if (path.StartsWith("/sub/index.php/apps/mail/api/messages/"))
                return Reply(new { mailboxId = path.Split('/').Last() switch { "1" or "2" => 11, "3" => 21, "4" => 31, _ => throw new Exception(path) } });
            throw new Exception(path);
        }
        private static HttpResponseMessage Reply(object value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
