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
        var content = await MailClient.FetchContentAsync(http, server, "201", CancellationToken.None);
        Check(content.Text == "Safe manual link.\nTrap\n--\nFixture Signature",
            "Sanitized body mismatch: " + content.Text);
        var rich = await MailClient.FetchContentAsync(http, server, "204", CancellationToken.None);
        var runs = MailText.Parse(rich.Html);
        Check(rich.Text.Contains("查看照片") && !rich.Text.Contains("http") && !rich.Text.Contains("\n\n\n") &&
            rich.Text.Length < 100 && runs.Any(x => x.Link == "https://example.test/photos"),
            "Newsletter indentation and link URLs should not fill the reader: " + rich.Text);
        var images = runs.Where(x => x.Image != null).Select(x => MailImages.Resolve(x.Image!, server, "204")).ToArray();
        Check(images.Contains(new MailImageSource(MailImageKind.Attachment, "1.2")) &&
            images.Contains(new MailImageSource(MailImageKind.Remote, "https://photos.example/a.png")) &&
            images.Contains(new MailImageSource(MailImageKind.Remote, "https://photos.example/card.png")) && images.Length == 3,
            "Expected a CID attachment, a restored remote photo and a newsletter background. Got " + string.Join(", ", images.AsEnumerable()));
        Check(MailImages.Resolve("file:///c:/private.png", server, "204") == null &&
            MailImages.Resolve("https://fixture.test/photos/hosted.png", server, "204")?.Kind == MailImageKind.Remote &&
            MailImages.Resolve("/nextcloud/apps/mail/api/messages/999/attachment/2", server, "204") == null &&
            MailImages.Resolve("https://external.test/nextcloud/apps/mail/api/messages/204/attachment/1.2", server, "204")?.Kind == MailImageKind.Remote,
            "Only this server and message's own attachment may load with credentials.");
        var plain = await MailClient.FetchContentAsync(http, server, "205", CancellationToken.None);
        Check(plain.Text == "Line one\n\n    Line  two" && plain.Html == "", "Preserve plain-text paragraphs, spacing and indentation: " + plain.Text);
        Check(MailText.ToPlainText("<p>code</p><pre>if ok:\n    return 1</pre>") == "code\nif ok:\n    return 1", "Preserve preformatted indentation.");
        Check(MailText.Parse("<a href='mailto:person@example.test'>联系我</a>").Single() ==
            new MailRun("联系我", "mailto:person@example.test") && MailText.LinkTarget("file:///c:/test") == null,
            "Email links keep their label and target while local file links stay inert.");
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAMCAIAAADkharWAAAAFklEQVR4nGPQzushCTGMahjV0INVAwAm0NvBB5m+agAAAABJRU5ErkJggg==");
        foreach (var raw in new[] { png, Encoding.ASCII.GetBytes(Convert.ToBase64String(png, Base64FormattingOptions.InsertLineBreaks)) })
        {
            using var transport = new HttpClient(new FakeHandler(path => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) }));
            var bytes = await MailImages.DownloadAsync(transport, MailImages.AttachmentUrl(server, "204", "2.2"), default, attachment: true);
            Check(png.SequenceEqual(bytes), "A raw or MIME Base64 attachment should yield the original raster bytes.");
        }

        // Undecryptable mail (HTTP 206 with S/MIME) becomes notice text, unlike a real server error.
        var encrypted = await MailClient.FetchContentAsync(http, server, "202", CancellationToken.None);
        Check(encrypted.Text == "此邮件已加密，服务器无法提供正文。",
            "An undecryptable message should render as a notice instead of failing.");

        var failure = await ThrowsAsync<HttpRequestException>(
            () => MailClient.FetchContentAsync(http, server, "203", CancellationToken.None));
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
        "/nextcloud/ocs/v2.php/apps/mail/message/204" => Ocs(new
        {
            hasHtmlBody = true,
            inlineAttachments = new[] { new { id = "1.2", cid = "attached-photo" } },
            body = """
              <table>
                <tr>   <td>&nbsp;</td> </tr>
                <tr><td>十一月 25</td></tr>
                <tr>
                  <td><img src="cid:attached-photo" alt="照片"></td>
                  <td><img src="/apps/mail/img/blocked-image.png" style="display: none !important"
                        data-original-src="/nextcloud/index.php/apps/mail/proxy?id=204&amp;src=https%3A%2F%2Fphotos.example%2Fa.png" alt="回忆"></td>
                </tr>
                <tr><td data-original-style="background-image: url('https://photos.example/card.png')" style="background-image:url(/blocked-image.png)">往年</td></tr>
                <tr><td><img width="1" height="1" src="https://tracking.example/pixel"></td></tr>
                <tr><td><a href="https://example.test/photos">查看照片</a></td></tr>
              </table>
              """
        }),
        "/nextcloud/ocs/v2.php/apps/mail/message/205" => Ocs(new { hasHtmlBody = false, body = "Line one\r\n\r\n\r\n    Line  two" }),
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
