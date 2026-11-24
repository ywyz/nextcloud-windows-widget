using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NextcloudWidget.App;
using NextcloudWidget.Core.Translation;

static void Check(bool value, string description)
{
    if (!value) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
await using var app = builder.Build();
var requests = new ConcurrentQueue<(string Body, string? Authorization)>();
var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int redirected = 0;
app.MapPost("/{mode}/v1/chat/completions", async (string mode, HttpContext context) =>
{
    var body = await context.Request.ReadFromJsonAsync<JsonElement>();
    string fragment = body.GetProperty("messages")[1].GetProperty("content").GetString()!;
    requests.Enqueue((body.GetRawText(), context.Request.Headers.Authorization.SingleOrDefault()));
    if (mode == "redirect") return Results.Redirect("/redirected", preserveMethod: true);
    if (mode == "cancel")
    {
        delayed.TrySetResult();
        await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
    }
    if (mode == "auth") return Results.Json(new { error = "secret server diagnostic" }, statusCode: 401);
    return Results.Json(new { choices = new[] {new {finish_reason = mode == "cut" ? "length" : "stop",
        message = new { content = "译文" + fragment.TrimEnd() } } } });
});
app.MapPost("/redirected", () => { Interlocked.Increment(ref redirected); return Results.Ok(); });
await app.StartAsync();
string site = app.Urls.Single().TrimEnd('/');
string dir = Directory.CreateTempSubdirectory("nextcloud-translation-").FullName;
try
{
    var memory = new TestKeys();
    var prefs = new TranslationPreferences(dir, memory);
    TranslationOptions Options(string mode) => new(site + "/" + mode + "/v1", "fixture-translator", "简体中文");
    var a = Options("translate");
    var b = Options("other");
    var nativeKeys = new TranslationKeys(dir);
    try
    {
        nativeKeys.Save(a, "synthetic-vault-key");
        Check(new TranslationKeys(dir).Read(a) == "synthetic-vault-key" && nativeKeys.Read(b) == null &&
            new TranslationKeys(Path.Combine(dir, "other-profile")).Read(a) == null,
            "Windows Credential Manager isolates keys by profile and API base");
    }
    finally { nativeKeys.Save(a, null); }
    Check(prefs.Load() == null, "new profile is unconfigured");
    prefs.Save(a, "synthetic-key-A", false);
    prefs.Save(b, null, false);
    Check(memory.Read(b) == null && memory.Read(a) == "synthetic-key-A", "changing endpoint does not carry over a secret");
    prefs.Save(a, null, false);
    var restored = new TranslationPreferences(dir, memory).Load()!;
    Check(restored.BaseUrl == a.BaseUrl && restored.Model == "fixture-translator" && restored.TargetLanguage == "简体中文" &&
        !File.ReadAllText(Path.Combine(dir, "translation.json")).Contains("synthetic-key"), "settings survive restart without a persisted plaintext key");

    var service = new MailTranslator();
    var text = string.Concat(Enumerable.Repeat("Hello 🙂\r\n谢谢，世界。", 600)) + "最后一句。";
    string translation = await service.TranslateAsync(restored, memory.Read(restored), text);
    var snapshot = requests.ToArray();
    var originals = new List<string>();
    foreach (var item in snapshot)
    {
        using var doc = JsonDocument.Parse(item.Body);
        var result = doc.RootElement;
        var messages = result.GetProperty("messages");
        Check(result.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] {"messages", "model", "stream"}) &&
            result.GetProperty("model").GetString() == "fixture-translator" && messages.GetArrayLength() == 2 &&
            messages[0].GetProperty("role").GetString() == "system" && messages[0].GetProperty("content").GetString()!.Contains("简体中文") &&
            messages[1].GetProperty("role").GetString() == "user" && item.Authorization == "Bearer synthetic-key-A",
            "loopback server receives configured model, target language and origin-specific key");
        originals.Add(messages[1].GetProperty("content").GetString()!);
    }
    Check(originals.Count > 1 && string.Concat(originals) == text && translation.EndsWith("最后一句。"), "the entire long message reaches the provider in order");
    prefs.Save(a, null, true);
    await service.TranslateAsync(a, memory.Read(a), "仅正文");
    Check(requests.Last().Authorization == null, "keyless service sends no Authorization header");
    foreach (string scenario in new[]{"auth", "cut", "redirect"})
    {
        bool failed = false;
        try { await service.TranslateAsync(Options(scenario), null, "short message"); }
        catch (InvalidDataException error) { failed = !error.Message.Contains("secret server diagnostic"); }
        Check(failed, scenario + " reports a safe error");
    }
    Check(redirected == 0, "provider redirects are never followed");
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var waiting = service.TranslateAsync(Options("cancel"), null, "取消", cancel.Token);
    await delayed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    cancel.Cancel();
    bool stopped = false;
    try { await waiting; } catch (OperationCanceledException) { stopped = true; }
    Check(stopped, "cancel interrupts an in-flight provider request");
    foreach (string invalid in new[]{"http://example.com/v1", "https://user:pass@example.com/v1", "file:///v1", "https://service.invalid/v1?token=x"})
    {
        bool rejected = false;
        try { _ = new TranslationOptions(invalid, "fixture", "English"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "rejects unsafe or ambiguous API base URL");
    }
}
finally
{
    await app.StopAsync();
    Directory.Delete(dir, true);
}

internal sealed class TestKeys : ITranslationKeys
{
    private readonly Dictionary<Uri,string> values = new();
    public string? Read(TranslationOptions options) => values.GetValueOrDefault(options.BaseUrl);
    public void Save(TranslationOptions options, string? key)
    {
        if (key == null) values.Remove(options.BaseUrl);
        else values[options.BaseUrl] = key;
    }
}
