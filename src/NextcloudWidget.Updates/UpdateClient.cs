using System.Net;
using System.Security.Cryptography;

namespace NextcloudWidget.Updates;

public sealed partial class UpdateClient
{
    private const string Mirror = "https://github.ywyz.tech/";
    private const string Manifest = ReleaseInfo.Repository + "/releases/latest/download/update-win-x64.json";
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { UseCookies = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http;
    private readonly Version installedVersion;
    private readonly string parentDirectory;
    public UpdateClient() : this(SharedHttp, CurrentVersion, Path.GetDirectoryName(Executable)!) { }
    internal UpdateClient(HttpClient http, Version installedVersion, string parentDirectory) =>
        (this.http, this.installedVersion, this.parentDirectory) = (http, installedVersion, parentDirectory);
    private static readonly string[] Origins = [Mirror, ""];
    private static readonly string Executable = Environment.ProcessPath ?? throw new InvalidOperationException();

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateClient).Assembly.GetName().Version!;
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        foreach (string prefix in Origins)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var body = GetSmallAsync(prefix + Manifest, timeout.Token);
                var sig = GetSmallAsync(prefix + Manifest + ".p7s", timeout.Token);
                await Task.WhenAll(body, sig).ConfigureAwait(false);
                PublisherTrust.CheckManifest(body.Result, sig.Result);
                var release = ReleaseInfo.Parse(body.Result);
                return release.Version > installedVersion ? release : null;
            }
            catch (HttpRequestException e) when (prefix.Length == 0 && e.StatusCode == HttpStatusCode.NotFound)
            { return null; }
            catch (Exception e) when (prefix.Length != 0 && !ct.IsCancellationRequested && IsDownloadError(e)) { }
        }
        return null;
    }

    public async Task<string> StageAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.Version <= installedVersion) throw new InvalidOperationException("当前版本已是最新版。");
        string stage = Path.Combine(parentDirectory, StagePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string path = Path.Combine(stage, ReleaseInfo.Asset);
        try
        {
            foreach (string prefix in Origins)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    await DownloadPackageAsync(path, prefix + release.DownloadUrl, release, timeout.Token).ConfigureAwait(false);
                    if (PublisherTrust.CheckExecutable(path) != release.Version)
                        throw new InvalidDataException("更新包版本不符。");
                    return path;
                }
                catch (Exception e) when (prefix.Length != 0 && !ct.IsCancellationRequested && IsDownloadError(e)) { }
            }
            throw new IOException("无法下载更新。");
        }
        catch { CleanStaging(stage); throw; }
    }

    private async Task DownloadPackageAsync(string path, string url, ReleaseInfo release, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long size && size != release.Size)
            throw new InvalidDataException("更新包大小不匹配。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > release.Size) throw new InvalidDataException("更新包超出声明大小。");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
        }
        if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包下载不完整或校验失败。");
    }

    private static bool IsDownloadError(Exception e) => e is HttpRequestException or IOException or InvalidDataException or
        OperationCanceledException or CryptographicException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException;

    private async Task<byte[]> GetSmallAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > 65536) throw new InvalidDataException("更新清单过大。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
