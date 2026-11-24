using System.Net;
using System.Text;
using System.Text.Json;
using NextcloudWidget.Core;

// The transport returns plain JSON for writes and OCS envelopes for inbox discovery.
internal static class MailRejectedChecks
{
    private static readonly string[] ExpectedTraffic =
    [
        "PUT /index.php/apps/mail/api/messages/11/flags",
        "PUT /index.php/apps/mail/api/messages/11/flags",
        "GET /ocs/v2.php/apps/mail/account/list",
        "GET /ocs/v2.php/apps/mail/ocs/mailboxes?accountId=1",
        "GET /ocs/v2.php/apps/mail/ocs/mailboxes/5/messages?view=singleton&limit=50",
        "PUT /index.php/apps/mail/api/messages/11/flags",
        "GET /ocs/v2.php/apps/mail/account/list",
        "GET /ocs/v2.php/apps/mail/ocs/mailboxes?accountId=1",
        "GET /ocs/v2.php/apps/mail/ocs/mailboxes/5/messages?view=singleton&limit=50"
    ];

    internal static async Task RunAsync()
    {
        var server = new Uri("https://127.0.0.1/"); // loopback HTTPS placeholder, never contacted
        var data = Directory.CreateTempSubdirectory("ncw-mail-rejected-");
        try
        {
            var account = new Account(Guid.NewGuid(), server, "fixture");
            File.WriteAllText(Path.Combine(data.FullName, "account.json"), JsonSerializer.Serialize(account));

            // One cached unread message whose body must outlive every rejection.
            new WidgetCache(Path.Combine(data.FullName, account.CacheId.ToString("N") + ".db"))
                .SaveMail([new MailItem("11", "Queued read", "Fixture Sender <fixture@127.0.0.1>",
                    DateTimeOffset.UnixEpoch, "cached body eleven", true)]);

            bool seen = false;
            var put = HttpStatusCode.ServiceUnavailable;
            using var handler = new FakeHandler((method, path) => (method, path) switch
            {
                ("PUT", "/index.php/apps/mail/api/messages/11/flags") => Flags(put),
                ("GET", "/ocs/v2.php/apps/mail/account/list") => Ocs(new object[] { new { id = 1 } }),
                ("GET", "/ocs/v2.php/apps/mail/ocs/mailboxes?accountId=1") =>
                    Ocs(new object[] { new { name = "INBOX", databaseId = 5 } }),
                ("GET", "/ocs/v2.php/apps/mail/ocs/mailboxes/5/messages?view=singleton&limit=50") => Ocs(new object[]
                {
                    new
                    {
                        databaseId = 11, subject = "Queued read", dateInt = 1770000000L,
                        flags = new { seen },
                        from = new object[] { new { label = "Fixture Sender", email = "fixture@127.0.0.1" } }
                    }
                }),
                _ => throw new InvalidDataException("Unexpected mail request: " + method + " " + path)
            });

            WidgetService Open() => new(data.FullName, new StaticSecrets(),
                () => new HttpClient(handler, disposeHandler: false));

            // A transport failure on the flags write stays retryable and keeps the queue.
            using (var first = Open())
            {
                await first.MarkMailReadAsync(["11"]);
                var unavailable = await ThrowsAsync<HttpRequestException>(() => first.SyncMailAsync());
                Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable,
                    "A 503 from the flags endpoint must surface as a retryable HttpRequestException.");
                Check(await first.GetPendingMailOperationCountAsync() == 1,
                    "A 503 must leave the read queued instead of dropping it.");
                Check(!(await first.GetMailAsync()).Single(m => m.Id == "11").Unread,
                    "The optimistic read flag must hold while the write is still queued.");
            }

            // The queue is durable: after a restart the pending read still shows as done locally.
            put = HttpStatusCode.Forbidden;
            using (var second = Open())
            {
                Check(await second.GetPendingMailOperationCountAsync() == 1,
                    "Restart lost the queued read action.");
                Check(!(await second.GetMailHeadersAsync()).Single(m => m.Id == "11").Unread,
                    "The optimistic read flag did not survive the restart.");

                // The server refuses the write while its refreshed list still reports the
                // message unread: the flag must roll back, the action must clear, and the
                // refusal itself must be visible to the caller.
                var rejected = await ThrowsAsync<IOException>(() => second.SyncMailAsync());
                Check(rejected.Message.Contains("403", StringComparison.Ordinal) &&
                    rejected.InnerException is HttpRequestException { StatusCode: HttpStatusCode.Forbidden },
                    "A refused read must surface as a visible IOException carrying the 403: " + rejected.Message);
                Check(await second.GetPendingMailOperationCountAsync() == 0,
                    "A refused read must not stay queued.");
                var restored = (await second.GetMailAsync()).Single(m => m.Id == "11");
                Check(restored.Unread, "The optimistic flag must restore while the server still reports unread.");
                Check(restored.Body == "cached body eleven", "The cached body must survive the refusal.");

                // Retrying after the refusal lands: the write is accepted and the header confirms it.
                seen = true;
                put = HttpStatusCode.OK;
                await second.MarkMailReadAsync(["11"]);
                Check(await second.GetPendingMailOperationCountAsync() == 1,
                    "Re-queuing the read after the refusal failed.");
                await second.SyncMailAsync();
                Check(await second.GetPendingMailOperationCountAsync() == 0,
                    "An accepted read must leave the pending queue empty.");
                var settled = (await second.GetMailAsync()).Single(m => m.Id == "11");
                Check(!settled.Unread && settled.Body == "cached body eleven",
                    "The accepted read must persist in the cache without losing the cached body.");

                Check(handler.Log.Count(r => r.StartsWith("PUT ", StringComparison.Ordinal)) == 3,
                    "Expected exactly three flags PUT requests in total: " + string.Join("; ", handler.Log));
                Check(handler.Log.All(r => !r.StartsWith("DELETE ", StringComparison.Ordinal)),
                    "Mail sync must never issue DELETE requests: " + string.Join("; ", handler.Log));
                Check(handler.Log.SequenceEqual(ExpectedTraffic),
                    "Mail sync must issue only the expected requests: " + string.Join("; ", handler.Log));
            }
            Console.WriteLine("PASS Mail read rejections 503/403/200: durable queue, visible refusal, " +
                "optimistic restore and accepted flag with no extra traffic.");
        }
        finally
        {
            data.Delete(recursive: true);
        }
    }

    private static HttpResponseMessage Flags(HttpStatusCode status) => status switch
    {
        HttpStatusCode.ServiceUnavailable => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("overloaded") },
        HttpStatusCode.Forbidden => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("denied") },
        _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        }
    };

    private static HttpResponseMessage Ocs(object data) =>
        new HttpResponseMessage(HttpStatusCode.OK)
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

    // Synthetic device secrets: one fixed token, no credential store, no login flow.
    private sealed class StaticSecrets : IDeviceSecrets
    {
        public string? Read(Guid id) => "fixture-token";
        public void Write(Guid id, string user, string password) =>
            throw new InvalidOperationException("No login flow is expected in this check.");
        public void Delete(Guid id) =>
            throw new InvalidOperationException("No disconnect is expected in this check.");
    }

    private sealed class FakeHandler(Func<string, string, HttpResponseMessage> route) : HttpMessageHandler
    {
        public readonly List<string> Log = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string line = request.Method.Method + " " + request.RequestUri!.PathAndQuery;
            Log.Add(line);
            if (request.Method == HttpMethod.Delete)
                throw new InvalidOperationException("Mail sync must never issue DELETE requests.");
            Check(request.Headers.GetValues("OCS-APIRequest").Single() == "true", "Missing CSRF header.");
            if (request.Method == HttpMethod.Put)
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(payload.RootElement.GetProperty("flags").GetProperty("seen").GetBoolean(), "Incorrect seen flag.");
            }
            return route(request.Method.Method, request.RequestUri!.PathAndQuery);
        }
    }
}
