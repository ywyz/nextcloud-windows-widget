using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
using NextcloudWidget.Core;
using NextcloudWidget.Core.Mail;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Run tests/Integration/run-live.sh.");
static void Check(bool result, string message)
{
    if (!result) throw new InvalidOperationException(message);
}

await MailProtocolChecks.RunAsync();
await MailTrashProtocolChecks.RunAsync();
await MailRejectedChecks.RunAsync();
MailCacheUpgradeChecks.Run();
if (args.SequenceEqual(new[] { "--protocol-only" })) return;

var server = new Uri(Required("NCW_LIVE_URL"));
Check(server.IsLoopback && server.Scheme == "https", "The live fixture must be a loopback HTTPS server.");
var pin = Required("NCW_LIVE_PIN");
var token = Required("NCW_LIVE_TOKEN");
HttpClientHandler MakeTransport() => new()
{
    AllowAutoRedirect = false, UseProxy = false,
    ServerCertificateCustomValidationCallback = (request, cert, _, _) =>
        request.RequestUri?.Authority == server.Authority && cert != null &&
        pin.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), StringComparison.OrdinalIgnoreCase)
};
HttpClient MakeClient() => new(MakeTransport());

var data = Directory.CreateTempSubdirectory("ncw-live-");
WidgetService Open(string name, bool offline = false, bool seed = true, Func<HttpClient>? client = null)
{
    var directory = Directory.CreateDirectory(Path.Combine(data.FullName, name));
    var profile = Path.Combine(directory.FullName, "account.json");
    if (seed && !File.Exists(profile))
        File.WriteAllText(profile, JsonSerializer.Serialize(new Account(Guid.NewGuid(), server, "fixture")));
    return new WidgetService(directory.FullName, new FixtureSecrets(token),
        offline ? () => new HttpClient(new OfflineHandler()) : client ?? MakeClient);
}

async Task GrantTestDeviceAsync(Uri login)
{
    // Submit the fixture account's built-in "Alternative log in using app password" form.
    using var browser = MakeClient();
    using var landing = await browser.GetAsync(login);
    var next = landing.Headers.Location ?? throw new InvalidDataException("Missing login redirect.");
    next = new Uri(login, next);
    Check(next.Authority == server.Authority && next.Scheme == "https", "Unexpected login redirect.");
    using var page = new HtmlParser().ParseDocument(await browser.GetStringAsync(next));
    var form = page.QuerySelector("#app-token-login-field") ?? throw new InvalidDataException("Missing app-password login form.");
    var target = new Uri(next, form.GetAttribute("action")!);
    Check(target.Authority == server.Authority && target.Scheme == "https", "Unexpected form destination.");
    var fields = form.QuerySelectorAll("input[type=hidden]").ToDictionary(
        item => item.GetAttribute("name")!, item => item.GetAttribute("value") ?? "");
    fields["user"] = "fixture";
    fields["password"] = token;
    using var request = new FormUrlEncodedContent(fields);
    using var granted = await browser.PostAsync(target, request);
    Check(granted.IsSuccessStatusCode, $"Test user's login grant failed ({granted.StatusCode}).");
}

try
{
    using var primary = Open("primary", seed: false);
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    await primary.ConnectAsync(server, login => GrantTestDeviceAsync(login).GetAwaiter().GetResult(), deadline.Token);
    Check(primary.IsConnected, "Login Flow v2 did not connect the test user.");
    Console.WriteLine("PASS Real Nextcloud Login Flow v2 init, browser-form grant and poll.");
    await primary.SyncTasksAsync();
    var lists = await primary.GetTaskListsAsync();
    var calendar = lists.FirstOrDefault(x => x.CanWrite && x.Name == "Widget Tasks")
        ?? lists.FirstOrDefault(x => x.CanWrite) ?? throw new InvalidOperationException("No writable VTODO collection.");
    var taskId = await primary.CreateTaskAsync(calendar.Id, "Live fixture", "Original", null);
    await primary.SyncTasksAsync();
    using var other = Open("independent");
    await other.SyncTasksAsync();
    Check((await other.GetTasksAsync()).Any(t => t.Id == taskId && t.Description == "Original"),
        "A separate device could not read the created task from Nextcloud.");
    await primary.EditTaskAsync(taskId, "Live fixture updated", "Updated", null);
    await primary.SetTaskCompletedAsync(taskId, true);
    await primary.SyncTasksAsync();
    await other.SyncTasksAsync();
    Check((await other.GetTasksAsync()).Any(t => t.Id == taskId && t.Completed && t.Title == "Live fixture updated"),
        "The task changes did not reach the second device.");
    await primary.EditTaskAsync(taskId, "Local title", "Local wording", null);
    await other.EditTaskAsync(taskId, "Remote title", "Remote wording", null);
    await other.SyncTasksAsync();
    await primary.SyncTasksAsync();
    var conflict = (await primary.GetTasksAsync()).Single(t => t.Id == taskId);
    Check(conflict.SyncState == "conflict" && conflict.Title == "Local title", "Concurrent local changes were lost.");
    Check((await primary.GetConflictServerVersionAsync(taskId))?.Title == "Remote title", "Remote conflict version missing.");
    await primary.ResolveConflictAsync(taskId, keepLocal: true);
    await primary.SyncTasksAsync();
    await other.SyncTasksAsync();
    Check((await other.GetTasksAsync()).Single(t => t.Id == taskId).Title == "Local title", "Conflict resolution did not reach Nextcloud.");
    Console.WriteLine("PASS Real CalDAV discovery, create, edit, complete, incremental read and concurrent conflict resolution.");

    await primary.SyncMailAsync();
    var mail = await primary.GetMailAsync();
    Check(mail.Count == 100 && mail.Select(m => m.Id).Distinct().Count() == 100,
        $"Expected 50 messages from each of two inboxes, got {mail.Count}.");
    foreach (var user in new[] { "one", "two" })
    {
        var subjects = mail.Where(m => m.Subject.StartsWith($"fixture-{user}-")).Select(m => m.Subject).ToHashSet();
        Check(Enumerable.Range(2, 50).All(i => subjects.Contains($"fixture-{user}-{i:00}")),
            $"Recent message window incorrect for {user}.");
    }
    Check(mail.All(m => m.Unread && m.Sender.Contains("sender@fixture.test") &&
        m.Body.Contains("手册") && !m.Body.Contains("https://example.test/manual") && !m.Body.Contains("<a ")),
        "Unread state, sender or compact plain-text content incorrect.");
    var newsletterId = mail.Single(m => m.Subject == "fixture-one-51").Id;
    var newsletter = await primary.GetMailContentAsync(newsletterId);
    string photoPart;
    byte[] photo;
    using (var document = new HtmlParser().ParseDocument(newsletter.Html))
    {
        Check(document.QuerySelector("a[href='https://example.test/manual']")?.TextContent == "手册",
            "Reader lost the link behind the displayed label.");
        var attachment = document.QuerySelector("img[alt='当年的照片']")?.GetAttribute("src");
        Check(Uri.TryCreate(server, attachment, out var url) &&
            url.AbsolutePath.Contains($"/messages/{newsletterId}/attachment/", StringComparison.Ordinal),
            "Nextcloud did not return the embedded photo for the newsletter.");
        photoPart = url!.Segments[^1];
        photo = await primary.GetMailInlineImageAsync(newsletterId, photoPart);
        Check(Convert.ToHexStringLower(SHA256.HashData(photo)) ==
            "9469c2b6235fff73c3e8d839912a3a1800427bf9abe3560844d187664b803062",
            "Embedded photo differs from the original SMTP attachment.");
        var remoteImage = document.QuerySelector("img[alt='远程照片']");
        var remoteSource = remoteImage?.GetAttribute("data-original-src") ?? remoteImage?.GetAttribute("src");
        Check(remoteSource != null && MailImages.Resolve(remoteSource, server, newsletterId) ==
            new MailImageSource(MailImageKind.Remote, "https://example.test/album.png"),
            "Cannot recover the remote photo URL from the real Nextcloud Mail response.");
    }
    await primary.SyncMailAsync();
    Check((await primary.GetMailAsync()).All(m => m.Unread), "Background prefetch marked IMAP messages as read.");
    using var offline = Open("primary", true);
    Check((await offline.GetMailContentAsync(newsletterId)).Html == newsletter.Html,
        "Illustrated newsletter markup did not survive an offline restart.");
    var offlinePhoto = await offline.GetMailInlineImageAsync(newsletterId, photoPart);
    Check(photo.SequenceEqual(offlinePhoto),
        "Previously opened inline photo was unavailable after restarting offline.");
    Console.WriteLine("PASS HTML newsletter with a real CID attachment and an offline photo after restart.");
    Check((await offline.GetMailAsync()).Count == 100, "Mail cache unavailable offline.");
    Check((await offline.GetMailBodyAsync(mail[0].Id)) == mail[0].Body, "Body was not available offline.");
    Check((await offline.GetTasksAsync()).Any(t => t.Id == taskId), "Task cache unavailable offline.");
    Console.WriteLine("PASS Real Nextcloud Mail OCS, two IMAP inboxes, newest 50 per account, offline SQLite body and tasks.");

    string Id(string user, int number) => mail.Single(m => m.Subject == $"fixture-{user}-{number:00}").Id;
    string first = Id("one", 51), second = Id("one", 50), third = Id("two", 51), move = Id("one", 48);
    await offline.MarkMailReadAsync([first]);
    await offline.MarkMailReadAsync([first, second, third, second]);
    await offline.MoveMailToTrashAsync([move, move]);
    Check(await offline.GetPendingMailOperationCountAsync() == 4, "Offline actions were not deduplicated.");
    Check(!(await offline.GetMailHeadersAsync()).Any(m => m.Id == move || m.Id == first && m.Unread),
        "Optimistic flags and trash visibility are wrong.");
    try { await offline.SyncMailAsync(); throw new Exception("Offline sync unexpectedly succeeded."); }
    catch (HttpRequestException) { }
    using var restarted = Open("primary", client: () => new HttpClient(new LoseMoveReply(MakeTransport())));
    Check(await restarted.GetPendingMailOperationCountAsync() == 4, "Restart lost the offline actions.");
    try { await restarted.SyncMailAsync(); throw new Exception("The lost move acknowledgement was ignored."); }
    catch (HttpRequestException ex) when (ex.Message == LoseMoveReply.Failure) { }
    Check(await restarted.GetPendingMailOperationCountAsync() == 1, "The unacknowledged move should still be queued.");
    Check(!(await restarted.GetMailAsync()).Any(m => m.Id == move), "A lost reply resurfaced the trashed message.");
    await primary.SyncMailAsync();
    Check(await primary.GetPendingMailOperationCountAsync() == 0, "Retry did not reconcile the move after its reply was lost.");
    var after = await primary.GetMailHeadersAsync();
    Check(!after.Any(m => m.Id == move) && after.Count == 100, "Trash retry did not restore the newest-50 cache window.");
    Check(after.Where(m => m.Id == first || m.Id == second || m.Id == third).All(m => !m.Unread),
        "Synced read flags reverted.");
    string noTrash = Id("two", 47);
    await primary.MoveMailToTrashAsync([noTrash]);
    try { await primary.SyncMailAsync(); throw new Exception("Unsafe move unexpectedly succeeded."); }
    catch (NextcloudWidget.Core.Mail.MailTrashUnavailableException) { }
    Check((await primary.GetMailAsync()).Any(m => m.Id == noTrash && m.Body.Length > 0)
        && await primary.GetPendingMailOperationCountAsync() == 0, "Missing trash failed to restore the original message and body.");
    await other.SyncMailAsync();
    var secondDevice = await other.GetMailHeadersAsync();
    Check(!secondDevice.Any(m => m.Id == move) && secondDevice.Count(m => !m.Unread) == 3,
        "The independent device did not see the exact server-side mutations.");
    Console.WriteLine("PASS Offline read/batch/trash, durable restart, lost move acknowledgement, safe missing-trash rollback and independent server readback.");
}
finally
{
    data.Delete(recursive: true);
}

sealed class FixtureSecrets(string token) : IDeviceSecrets
{
    public string Read(Guid id) => token;
    public void Write(Guid id, string user, string password)
    {
        if (user != "fixture" || password != token)
            throw new InvalidDataException("Unexpected credentials returned by the test server.");
    }
    public void Delete(Guid id) { }
}

sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new HttpRequestException("Fixture is offline.");
}

sealed class LoseMoveReply(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    internal const string Failure = "Fixture dropped the successful move response.";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/move", StringComparison.Ordinal))
        {
            response.EnsureSuccessStatusCode();
            response.Dispose();
            throw new HttpRequestException(Failure);
        }
        return response;
    }
}
