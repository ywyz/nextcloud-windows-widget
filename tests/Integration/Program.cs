using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
using NextcloudWidget.Core;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Run tests/Integration/run-live.sh.");
static void Check(bool result, string message)
{
    if (!result) throw new InvalidOperationException(message);
}

await MailProtocolChecks.RunAsync();
if (args.SequenceEqual(new[] { "--protocol-only" })) return;

var server = new Uri(Required("NCW_LIVE_URL"));
Check(server.IsLoopback && server.Scheme == "https", "The live fixture must be a loopback HTTPS server.");
var pin = Required("NCW_LIVE_PIN");
var token = Required("NCW_LIVE_TOKEN");
HttpClient MakeClient() => new(new HttpClientHandler
{
    AllowAutoRedirect = false, UseProxy = false,
    ServerCertificateCustomValidationCallback = (request, cert, _, _) =>
        request.RequestUri?.Authority == server.Authority && cert != null &&
        pin.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), StringComparison.OrdinalIgnoreCase)
});

var data = Directory.CreateTempSubdirectory("ncw-live-");
WidgetService Open(string name, bool offline = false, bool seed = true)
{
    var directory = Directory.CreateDirectory(Path.Combine(data.FullName, name));
    var profile = Path.Combine(directory.FullName, "account.json");
    if (seed && !File.Exists(profile))
        File.WriteAllText(profile, JsonSerializer.Serialize(new Account(Guid.NewGuid(), server, "fixture")));
    return new WidgetService(directory.FullName, new FixtureSecrets(token),
        offline ? () => new HttpClient(new OfflineHandler()) : MakeClient);
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
        m.Body.Contains("手册") && m.Body.Contains("https://example.test/manual") && !m.Body.Contains("<a ")),
        "Unread state, sender, safe text or link target missing.");
    await primary.SyncMailAsync();
    Check((await primary.GetMailAsync()).All(m => m.Unread), "Background prefetch marked IMAP messages as read.");
    using var offline = Open("primary", true);
    Check((await offline.GetMailAsync()).Count == 100, "Mail cache unavailable offline.");
    Check((await offline.GetMailBodyAsync(mail[0].Id)) == mail[0].Body, "Body was not available offline.");
    Check((await offline.GetTasksAsync()).Any(t => t.Id == taskId), "Task cache unavailable offline.");
    Console.WriteLine("PASS Real Nextcloud Mail OCS, two IMAP inboxes, newest 50 per account, offline SQLite body and tasks.");
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
