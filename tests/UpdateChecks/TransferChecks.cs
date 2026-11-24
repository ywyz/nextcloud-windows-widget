using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using NextcloudWidget.Updates;

internal static class TransferChecks
{
    private const int PrefixSize = 65536;
    private static readonly TransferPolicy Fast = new(TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(160), TimeSpan.Zero);

    internal static async Task RunAsync(string releaseDir, ReleaseInfo release)
    {
        string asset = Path.Combine(releaseDir, ReleaseInfo.Asset);
        var dir = Directory.CreateTempSubdirectory("ncw-transfer-");
        try
        {
            foreach (string mode in new[] { "resume", "range-ignored", "wrong-range", "corrupt-prefix", "trickle", "cancel", "cancel-waiting", "cancel-verify" })
            {
                var trace = new List<UpdateProgress>();
                using var cancel = new CancellationTokenSource();
                using var handler = new TransferHandler(asset, mode);
                using var http = new HttpClient(handler);
                var policy = mode == "cancel-waiting" ? Fast with { IdleTimeout = TimeSpan.FromSeconds(3) } : Fast;
                var updater = new UpdateClient(http, new Version(0, 0, 0), dir.FullName, policy);
                var progress = new Capture(item =>
                {
                    trace.Add(item);
                    if ((mode == "cancel" && item is { Phase: UpdatePhase.Downloading, BytesReceived: > 0 }) ||
                        (mode == "cancel-waiting" && item is { Phase: UpdatePhase.Downloading, BytesReceived: > 0, BytesPerSecond: 0 }) ||
                        (mode == "cancel-verify" && item is { Phase: UpdatePhase.Verifying } && item.Detail!.Contains("Windows")))
                        cancel.Cancel();
                });

                if (mode.StartsWith("cancel", StringComparison.Ordinal))
                {
                    try { await updater.StageAsync(release, progress, cancel.Token); throw new Exception("Cancellation was ignored."); }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                    Check(handler.DirectCount == 0 && handler.MirrorCount == 1 && !dir.EnumerateFileSystemInfos().Any(), "Cancellation fallback or leftover");
                    continue;
                }

                string staged = await updater.StageAsync(release, progress, cancel.Token);
                Check(handler.MirrorCount == 2 && handler.Ranges[0] == null && handler.Ranges[1] > 0, "Bounded mirror retry with a Range");
                Check(trace.Any(p => p.Phase == UpdatePhase.Retrying && p.Source == UpdateSource.Accelerator), "Missing explicit retry");
                bool fallback = mode is "wrong-range" or "corrupt-prefix" or "trickle";
                Check(handler.DirectCount == (fallback ? 1 : 0) &&
                    trace.Any(p => p.Phase == UpdatePhase.FallingBack && p.Source == UpdateSource.GitHub) == fallback, "Fallback or origin mismatch");
                if (fallback) Check(handler.Ranges.Last() == null, "Direct origin appended a mirror prefix");
                if (mode == "trickle") Check(trace.Any(p => p.Detail?.Contains("1 KiB/s") == true), "Slow drip was not detected");
                if (mode == "range-ignored") Check(trace.Any(p => p is { Phase: UpdatePhase.Downloading, Attempt: 2, BytesReceived: 0 }), "Ignoring Range must reset the counter");
                Check(trace.Any(p => p is { Phase: UpdatePhase.Downloading, BytesPerSecond: > 0 } && p.BytesReceived > 0 && p.TotalBytes == release.Size), "No measured byte progress and speed");
                Check(trace.Any(p => p is { Phase: UpdatePhase.Verifying } && p.BytesReceived == release.Size && p.Detail!.Contains("Windows")), "Missing trust verification phase");
                using (var stopped = new CancellationTokenSource())
                {
                    stopped.Cancel();
                    try { updater.BeginInstall(staged, stopped.Token); throw new Exception("Launched cancelled helper."); }
                    catch (OperationCanceledException) { }
                }
                updater.AbandonStaged(staged);
                Check(!dir.EnumerateFileSystemInfos().Any(), "Staging cleanup failed");
            }
            Console.WriteLine("PASS Range 206 resume, 200 restart, invalid ranges and corrupt prefix fallback, slow/stalled streams, cancellation and cleanup.");
        }
        finally { dir.Delete(true); }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }

    private sealed class Capture(Action<UpdateProgress> action) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => action(value);
    }

    private sealed class TransferHandler(string path, string mode) : HttpMessageHandler
    {
        internal int MirrorCount, DirectCount;
        internal List<long?> Ranges { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bool mirror = request.RequestUri!.Host == "github.ywyz.tech";
            long? offset = request.Headers.Range?.Ranges.Single().From;
            Ranges.Add(offset);
            if (mirror) MirrorCount++; else DirectCount++;
            HttpResponseMessage reply;
            if (mirror && (mode == "trickle" || (MirrorCount == 1 && mode != "cancel-verify")))
            {
                reply = new(HttpStatusCode.OK) { Content = new StreamContent(new PartialStream(path, mode)) };
                // On the second trickling attempt, the mirror ignores the range and starts again.
            }
            else
            {
                var stream = File.OpenRead(path);
                bool range = mirror && offset.HasValue && mode != "range-ignored";
                if (range) stream.Position = offset!.Value;
                reply = new(range ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(stream) };
                if (range) reply.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset!.Value + (mode == "wrong-range" ? 1 : 0), stream.Length - 1, stream.Length);
            }
            return Task.FromResult(reply);
        }
    }

    private sealed class PartialStream(string path, string mode) : Stream
    {
        private readonly FileStream source = File.OpenRead(path);
        private bool sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (mode == "trickle")
            {
                await Task.Delay(90, ct);
                return await source.ReadAsync(buffer[..1], ct);
            }
            if (sent) { await Task.Delay(Timeout.Infinite, ct); return 0; }
            sent = true;
            int read = await source.ReadAsync(buffer[..Math.Min(PrefixSize, buffer.Length)], ct);
            if (mode == "corrupt-prefix") buffer.Span[0] ^= 1;
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
