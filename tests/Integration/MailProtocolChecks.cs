using System.Net;
using System.Text;
using System.Text.Json;
using NextcloudWidget.Core;
using NextcloudWidget.Core.Mail;

// Protocol checks for the mail client against a fake transport: no real server, credentials
// or network access are involved, and every route answers with OCS-shaped JSON.
internal static class MailProtocolChecks
{
    internal static async Task RunAsync()
    {
        var server = new Uri("https://fixture.test/nextcloud"); // base path must survive
        using var handler = new FakeHandler(Route);
        using var http = new HttpClient(handler);

        // Header sync: list and mailbox endpoints only; a cached body is reused, never refetched.
        var stale = new MailItem("101", "stale", "stale", DateTimeOffset.UnixEpoch, "cached body one", true);
        var fetched = await MailClient.FetchAsync(http, server, new[] { stale }, CancellationToken.None);
        Check(fetched.Count == 3 && fetched[0].Id == "101", "Expected three inbox headers, newest first.");
        Check(handler.Requests.SequenceEqual(new[]
        {
            "GET /nextcloud/ocs/v2.php/apps/mail/account/list",
            "GET /nextcloud/ocs/v2.php/apps/mail/ocs/mailboxes?accountId=7",
            "GET /nextcloud/ocs/v2.php/apps/mail/ocs/mailboxes/19/messages?view=singleton&limit=50"
        }), "Header sync must keep the server base path and must not fetch bodies or attachments: " +
            string.Join("; ", handler.Requests));

        var byId = fetched.ToDictionary(item => item.Id);
        Check(byId["101"].Body == "cached body one" && byId["101"].Subject == "Welcome one" &&
            byId["101"].Sender == "Fixture One <one@fixture.test>" && byId["101"].Unread,
            "A previously cached body must carry over while its subject and sender refresh.");
        Check(byId["102"].Subject == "（无主题）" && byId["102"].Sender == "two@fixture.test" && byId["102"].Unread,
            "A blank subject must fall back and a sender without a label must degrade to the bare address.");
        Check(byId["103"].Subject == "（无主题）" && byId["103"].Sender == "Alias Only" && !byId["103"].Unread,
            "A missing subject must fall back and a label-only sender must stay readable.");

        // Body sync: the safe link survives while script, style, iframe and javascript: content is dropped.
        var plain = await MailClient.FetchBodyAsync(http, server, "201", CancellationToken.None);
        Check(plain == "Safe manual <https://example.test/manual> link.\nTrap\n-- \nFixture Signature",
            "Sanitized body mismatch: " + plain);

        // Undecryptable mail (HTTP 206 with S/MIME) becomes notice text, unlike a real server error.
        var encrypted = await MailClient.FetchBodyAsync(http, server, "202", CancellationToken.None);
        Check(encrypted == "此邮件已加密，服务器无法提供正文。",
            "An undecryptable message should render as a notice instead of failing.");

        var failure = await ThrowsAsync<HttpRequestException>(
            () => MailClient.FetchBodyAsync(http, server, "203", CancellationToken.None));
        Check(failure.StatusCode == HttpStatusCode.InternalServerError,
            "An ordinary HTTP 500 on the detail endpoint must stay a distinct, retryable failure.");
        Console.WriteLine("PASS Mail OCS protocol checks against a fake transport.");
    }

    private static HttpResponseMessage Route(string path) => path switch
    {
        "/nextcloud/ocs/v2.php/apps/mail/account/list" => Ocs(new object[] { new { id = 7 } }),
        "/nextcloud/ocs/v2.php/apps/mail/ocs/mailboxes?accountId=7" => Ocs(new object[]
        {
            new { name = "drafts", databaseId = 81 },
            new { name = "inbox", databaseId = 19 }
        }),
        "/nextcloud/ocs/v2.php/apps/mail/ocs/mailboxes/19/messages?view=singleton&limit=50" => Ocs(new object[]
        {
            new { databaseId = 101, subject = "Welcome one", dateInt = 1770000000L, flags = new { seen = false },
                from = new object[] { new { label = "Fixture One", email = "one@fixture.test" } },
                attachments = new object[] { new { fileName = "manual.pdf" } } },
            new { databaseId = 102, subject = "   ", dateInt = 1760000000L, flags = new { seen = false },
                from = new object[] { new { email = "two@fixture.test" } } },
            new { databaseId = 103, dateInt = 1750000000L, flags = new { seen = true },
                from = new object[] { new { label = "Alias Only" } } }
        }),
        "/nextcloud/ocs/v2.php/apps/mail/message/201" => Ocs(new
        {
            body = """<p>Safe <a href="https://example.test/manual">manual</a> link.</p><script>alert('x')</script><style>.x{color:red}</style><iframe src="https://evil.test/">frame</iframe><a href="javascript:alert(1)">Trap</a>""",
            signature = "Fixture Signature"
        }),
        "/nextcloud/ocs/v2.php/apps/mail/message/202" =>
            Ocs(new { body = (string?)null, smime = new { isEncrypted = true } }, HttpStatusCode.PartialContent),
        "/nextcloud/ocs/v2.php/apps/mail/message/203" =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
        _ => throw new InvalidDataException("Unexpected mail request: " + path)
    };

    private static HttpResponseMessage Ocs(object data, HttpStatusCode status = HttpStatusCode.OK) =>
        new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(
                new { ocs = new { meta = new { status = "ok" }, data } }), Encoding.UTF8, "application/json")
        };

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T expected) { return expected; }
        throw new InvalidOperationException($"Expected a {typeof(T).Name}.");
    }

    private sealed class FakeHandler(Func<string, HttpResponseMessage> route) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.TryGetValues("OCS-APIRequest", out var values) && values.SingleOrDefault() == "true")
            {
                Requests.Add($"{request.Method.Method} {request.RequestUri!.PathAndQuery}");
                return Task.FromResult(route(request.RequestUri!.PathAndQuery));
            }
            throw new InvalidOperationException("The mail client must send the OCS-APIRequest: true header.");
        }
    }
}
