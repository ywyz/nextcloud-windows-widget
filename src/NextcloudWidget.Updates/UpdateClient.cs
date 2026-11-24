using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NextcloudWidget.Updates;

public enum UpdatePhase { Checking, Downloading, Verifying, Retrying, FallingBack }
public enum UpdateSource { Accelerator, GitHub }

public sealed record UpdateProgress(UpdatePhase Phase, UpdateSource Source, long BytesReceived,
    long? TotalBytes, double BytesPerSecond, int Attempt, string? Detail = null);

internal sealed record TransferPolicy(TimeSpan IdleTimeout, TimeSpan SlowWindow, TimeSpan RetryDelay)
{
    internal static readonly TransferPolicy Default = new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(25), TimeSpan.FromMilliseconds(400));
}

public sealed partial class UpdateClient
{
    private const string Mirror = "https://github.ywyz.tech/";
    private const string Manifest = ReleaseInfo.Repository + "/releases/latest/download/update-win-x64.json";
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { UseCookies = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http;
    private readonly Version installedVersion;
    private readonly string parentDirectory;
    private readonly TransferPolicy policy;
    private static readonly string Executable = Environment.ProcessPath ?? throw new InvalidOperationException();

    public UpdateClient() : this(SharedHttp, CurrentVersion, Path.GetDirectoryName(Executable)!) { }
    internal UpdateClient(HttpClient http, Version installedVersion, string parentDirectory)
        : this(http, installedVersion, parentDirectory, TransferPolicy.Default) { }
    internal UpdateClient(HttpClient http, Version installedVersion, string parentDirectory, TransferPolicy policy) =>
        (this.http, this.installedVersion, this.parentDirectory, this.policy) = (http, installedVersion, parentDirectory, policy);

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateClient).Assembly.GetName().Version!;
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    public Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default) => CheckAsync(null, ct);

    public Task<ReleaseInfo?> CheckAsync(IProgress<UpdateProgress>? progress, CancellationToken ct) =>
        FromSourcesAsync<ReleaseInfo?>(async (source, attempt) =>
        {
            Report(UpdatePhase.Checking);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(policy.IdleTimeout);
            string url = Address(source, Manifest);
            var manifest = GetSmallAsync(url, timeout.Token);
            var signature = GetSmallAsync(url + ".p7s", timeout.Token);
            try { await Task.WhenAll(manifest, signature).ConfigureAwait(false); }
            catch (HttpRequestException) when (source == UpdateSource.GitHub &&
                manifest.Exception?.GetBaseException() is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
            { return null; }
            Report(UpdatePhase.Verifying, "正在验证更新清单签名");
            var release = await Task.Run(() =>
            {
                PublisherTrust.CheckManifest(manifest.Result, signature.Result);
                return ReleaseInfo.Parse(manifest.Result);
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return release.Version > installedVersion ? release : null;

            void Report(UpdatePhase phase, string? detail = null) =>
                progress?.Report(new(phase, source, 0, null, 0, attempt, detail));
        }, progress, ct);

    public Task<string> StageAsync(ReleaseInfo release, CancellationToken ct = default) => StageAsync(release, null, ct);

    public async Task<string> StageAsync(ReleaseInfo release, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        ct.ThrowIfCancellationRequested();
        if (release.Version <= installedVersion) throw new InvalidOperationException("当前版本已是最新版。");
        string stage = Path.Combine(parentDirectory, StagePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string path = Path.Combine(stage, ReleaseInfo.Asset);
        try
        {
            var verified = await FromSourcesAsync(async (source, attempt) =>
            {
                long offset = source == UpdateSource.Accelerator && attempt == 2 && File.Exists(path)
                    ? new FileInfo(path).Length : 0;
                if (offset >= release.Size) offset = 0;
                await DownloadPackageAsync(path, release, source, attempt, offset, progress, ct).ConfigureAwait(false);
                progress?.Report(new(UpdatePhase.Verifying, source, release.Size, release.Size, 0, attempt, "正在核对 SHA-256"));
                await using var file = File.OpenRead(path);
                byte[] digest = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
                if (file.Length != release.Size || !Convert.ToHexString(digest).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包大小或 SHA-256 校验失败。");
                return (source, attempt);
            }, progress, ct).ConfigureAwait(false);

            // All origins must yield the exact same signed digest. A local Windows trust
            // failure cannot be repaired by downloading those identical bytes again.
            progress?.Report(new(UpdatePhase.Verifying, verified.source, release.Size, release.Size, 0, verified.attempt,
                "正在验证 Windows 数字签名和产品版本"));
            ct.ThrowIfCancellationRequested();
            var version = await Task.Run(() => PublisherTrust.CheckExecutable(path), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (version != release.Version) throw new InvalidDataException("更新包版本不符。");
            return path;
        }
        catch { CleanStaging(stage); throw; }
    }

    private static string Address(UpdateSource source, string url) => source == UpdateSource.Accelerator ? Mirror + url : url;

    private async Task<T> FromSourcesAsync<T>(Func<UpdateSource, int, Task<T>> fetch,
        IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await fetch(UpdateSource.Accelerator, attempt).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsDownloadError(ex))
            {
                bool retry = attempt == 1 && IsTransient(ex);
                progress?.Report(new(retry ? UpdatePhase.Retrying : UpdatePhase.FallingBack,
                    retry ? UpdateSource.Accelerator : UpdateSource.GitHub, 0, null, 0, retry ? 2 : 1, Reason(ex)));
                if (!retry) break;
                await Task.Delay(policy.RetryDelay, ct).ConfigureAwait(false);
            }
        }
        ct.ThrowIfCancellationRequested();
        try { return await fetch(UpdateSource.GitHub, 1).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("GitHub 直连请求超时。", ex); }
    }

    private static bool IsDownloadError(Exception e) => e is HttpRequestException or IOException or InvalidDataException or
        OperationCanceledException or TimeoutException or CryptographicException or System.Text.Json.JsonException or
        KeyNotFoundException or InvalidOperationException or FormatException;

    private static bool IsTransient(Exception e) => e switch
    {
        HttpRequestException http => http.StatusCode == null || http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500,
        IOException or OperationCanceledException or TimeoutException => true,
        _ => false
    };

    private static string Reason(Exception e) => e switch
    {
        TimeoutException timed => timed.Message,
        OperationCanceledException => "等待服务器响应超时",
        HttpRequestException { StatusCode: { } code } => $"服务器返回 HTTP {(int)code}",
        HttpRequestException or IOException => "网络传输中断",
        _ => "返回的更新文件未通过验证"
    };

    private async Task DownloadPackageAsync(string path, ReleaseInfo release, UpdateSource source, int attempt,
        long offset, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, Address(source, release.DownloadUrl));
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        void Report(long bytes, double rate) => progress?.Report(new(UpdatePhase.Downloading, source, bytes, release.Size, rate, attempt));
        Report(offset, 0);
        idle.CancelAfter(policy.IdleTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (offset == 0 || range?.Unit != "bytes" || range.From != offset || range.To != release.Size - 1 || range.Length != release.Size)
                    throw new InvalidDataException("服务器返回了错误的续传范围。");
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                if (offset != 0) Report(0, 0);
                offset = 0; // This server ignored Range; restart without appending a duplicate prefix.
            }
            else throw new InvalidDataException("服务器未返回完整更新包。");
            if (response.Content.Headers.ContentLength is long size && size != release.Size - offset)
                throw new InvalidDataException("更新包大小不匹配。");

            await using var output = new FileStream(path, offset == 0 ? FileMode.Create : FileMode.Open,
                FileAccess.Write, FileShare.None, 65536, useAsync: true);
            if (output.Length != offset) throw new InvalidDataException("暂存文件长度已改变。");
            output.Position = offset;
            await using var input = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            byte[] buffer = new byte[65536];
            long total = offset, sampleBytes = total, windowBytes = total;
            long sampleTime = Stopwatch.GetTimestamp(), windowStart = sampleTime;
            Action? reportWaiting = progress != null ? () => Report(total, 0) : null;
            bool first = true;
            while (true)
            {
                idle.CancelAfter(policy.IdleTimeout);
                int read = await ReadAsync(input, buffer, idle.Token, reportWaiting).ConfigureAwait(false);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0) break;
                total += read;
                if (total > release.Size) throw new InvalidDataException("更新包超出声明大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                long now = Stopwatch.GetTimestamp();
                double sample = Stopwatch.GetElapsedTime(sampleTime, now).TotalSeconds;
                if (first || sample >= 0.25 || total == release.Size)
                {
                    Report(total, sample > 0 ? (total - sampleBytes) / sample : 0);
                    sampleTime = now;
                    sampleBytes = total;
                    first = false;
                }
                var window = Stopwatch.GetElapsedTime(windowStart, now);
                if (window >= policy.SlowWindow)
                {
                    // A few trickling bytes should not keep a broken mirror request alive.
                    // Ordinary slow downloads have no whole-package deadline.
                    if (total < release.Size && (total - windowBytes) / window.TotalSeconds < 1024)
                        throw new TimeoutException("下载持续低于 1 KiB/s");
                    windowStart = now;
                    windowBytes = total;
                }
            }
            if (total != release.Size) throw new EndOfStreamException("更新包下载中断。");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("等待下载数据超时", ex); }
    }

    private static async ValueTask<int> ReadAsync(Stream input, Memory<byte> buffer, CancellationToken ct, Action? reportWaiting)
    {
        var result = input.ReadAsync(buffer, ct);
        if (result.IsCompleted || reportWaiting == null) return await result.ConfigureAwait(false);
        var pending = result.AsTask();
        while (true)
        {
            try { return await pending.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (TimeoutException) when (!pending.IsCompleted) { reportWaiting(); }
        }
    }

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
