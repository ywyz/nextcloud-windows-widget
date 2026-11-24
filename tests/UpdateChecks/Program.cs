using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NextcloudWidget.Updates;

static void Assert(bool passed, string message)
{
    if (!passed) throw new Exception(message);
}

static async Task RejectAsync(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException or HttpRequestException) { return; }
    throw new Exception("Expected update to be rejected.");
}

var sample = Encoding.UTF8.GetBytes("""{"product":"Nextcloud Windows Widget","version":"0.1.0","asset":"NextcloudWidget-win-x64.exe","size":10,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""");
Assert(ReleaseInfo.Parse(sample).Version == new Version(0,1,0), "version");
using var key = RSA.Create(2048);
var request = new CertificateRequest("CN=Ephemeral QA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var untrusted = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
var forged = new SignedCms(new ContentInfo(sample), detached: true);
forged.ComputeSignature(new CmsSigner(untrusted));
await RejectAsync(() => Task.Run(() => PublisherTrust.CheckManifest(sample, forged.Encode())));
Console.WriteLine("PASS: manifest shape and rejection of foreign signers");

if (args.Length == 0) return;
if (args[0] == "--public-transfer")
{
    if (args.Length > 2) throw new ArgumentException("Usage: --public-transfer [expected-version]");
    await PublicTransfer.RunAsync(args.Length == 2 ? Version.Parse(args[1]) : null);
    return;
}
string releaseDir = Path.GetFullPath(args[0]);
string asset = Path.Combine(releaseDir, ReleaseInfo.Asset);
var expected = ReleaseInfo.Parse(await File.ReadAllBytesAsync(Path.Combine(releaseDir, "update-win-x64.json")));
var trustTime = System.Diagnostics.Stopwatch.StartNew();
Assert(PublisherTrust.CheckExecutable(asset) == expected.Version, "signed executable version");
Console.WriteLine($"Windows trust, pinned signer, product and version verified in {trustTime.ElapsedMilliseconds} ms.");
await TransferChecks.RunAsync(releaseDir, expected);
var stageParent = Directory.CreateTempSubdirectory("widget-update-test-");
try
{
    using var handler = new FixtureHandler(releaseDir);
    using var http = new HttpClient(handler);
    var updater = new UpdateClient(http, new Version(0,0,0), stageParent.FullName);
    handler.FailMirror = true;
    var release = await updater.CheckAsync() ?? throw new Exception("did not discover release");
    string staged = await updater.StageAsync(release);
    Assert(handler.MirrorHits > 0 && handler.DirectHits > 0 && File.Exists(staged), "proxy fallback");
    File.Delete(staged);
    Directory.Delete(Path.GetDirectoryName(staged)!);
    Console.WriteLine("PASS: signed manifest, fallback to origin, Authenticode, product and size");

    handler.FailMirror = false;
    var checkProgress = new List<UpdateProgress>();
    int previousDirect = handler.DirectHits;
    Assert(await updater.CheckAsync(new RecordedProgress(checkProgress), default) != null &&
        handler.DirectHits == previousDirect &&
        checkProgress.Select(x => x.Phase).SequenceEqual([UpdatePhase.Checking, UpdatePhase.Verifying]) &&
        checkProgress.All(x => x.Source == UpdateSource.Accelerator), "healthy mirror check should verify without direct requests");
    handler.MissingSignature = true;
    await RejectAsync(() => updater.CheckAsync());
    handler.MissingSignature = false;
    handler.BadSignature = true;
    await RejectAsync(() => updater.CheckAsync());
    handler.BadSignature = false;
    handler.BadAsset = true;
    await RejectAsync(() => updater.StageAsync(release));
    Assert(!stageParent.EnumerateFileSystemInfos().Any(), "unexpected partial download");
    handler.BadAsset = false;
    var current = new UpdateClient(http, expected.Version, stageParent.FullName);
    Assert(await current.CheckAsync() == null, "equal release offered");
    await RejectAsync(() => Task.Run(() => PublisherTrust.CheckExecutable(typeof(Program).Assembly.Location)));
    Console.WriteLine("PASS: invalid manifest, corrupt/truncated asset, unrelated PE, same-version update");
}
finally { if (!stageParent.EnumerateFileSystemInfos().Any()) stageParent.Delete(); }

sealed class FixtureHandler(string releaseDir) : HttpMessageHandler
{
    public bool FailMirror, BadSignature, BadAsset, MissingSignature;
    public int MirrorHits, DirectHits;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        bool mirror = request.RequestUri!.Host == "github.ywyz.tech";
        if (mirror) Interlocked.Increment(ref MirrorHits); else Interlocked.Increment(ref DirectHits);
        if (mirror && FailMirror) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        var name = Path.GetFileName(request.RequestUri.AbsolutePath);
        if (MissingSignature && name.EndsWith(".p7s")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        HttpContent response = (BadSignature && name.EndsWith(".p7s")) || (BadAsset && name.EndsWith(".exe"))
            ? new ByteArrayContent([9,1,0]) : new StreamContent(File.OpenRead(Path.Combine(releaseDir, name)));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = response });
    }
}

sealed class RecordedProgress(List<UpdateProgress> entries) : IProgress<UpdateProgress>
{
    public void Report(UpdateProgress value) => entries.Add(value);
}
