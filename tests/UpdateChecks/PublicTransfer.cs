using System.Diagnostics;
using NextcloudWidget.Updates;

internal static class PublicTransfer
{
    internal static async Task RunAsync(Version? expected)
    {
        var temp = Directory.CreateTempSubdirectory("ncw-public-update-");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var updater = new UpdateClient(http, new Version(0, 0, 0), temp.FullName);
        var timer = Stopwatch.StartNew();
        var reporter = new Reporter(timer);
        string? staged = null;
        try
        {
            var release = await updater.CheckAsync(reporter, cancellation.Token) ?? throw new InvalidDataException("The public release was not found.");
            Console.WriteLine($"Signed public manifest: version={release.Version} size={release.Size} sha256={release.Sha256}");
            if (expected != null && release.Version != expected)
                throw new InvalidDataException($"Expected public version {expected}, received {release.Version}.");
            staged = await updater.StageAsync(release, reporter, cancellation.Token);
            Console.WriteLine($"Verified temporary package: {staged}");
            Console.WriteLine($"PASS Public transfer and signature verification in {timer.Elapsed.TotalSeconds:F2}s.");
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (staged != null) updater.AbandonStaged(staged);
            temp.Delete();
            Console.WriteLine($"Removed isolated staging directory {temp.FullName}");
        }
    }

    private sealed class Reporter(Stopwatch timer) : IProgress<UpdateProgress>
    {
        private UpdateProgress? previous;
        private TimeSpan lastPrint;
        public void Report(UpdateProgress item)
        {
            bool changed = previous?.Phase != item.Phase || previous.Source != item.Source ||
                previous.Attempt != item.Attempt || previous.Detail != item.Detail;
            previous = item;
            if (!changed && timer.Elapsed - lastPrint < TimeSpan.FromSeconds(5) && item.BytesReceived != item.TotalBytes) return;
            lastPrint = timer.Elapsed;
            Console.WriteLine($"{timer.Elapsed.TotalSeconds:F1}s {item.Phase} via {item.Source} attempt={item.Attempt} " +
                $"bytes={item.BytesReceived}/{item.TotalBytes?.ToString() ?? "?"} rate={item.BytesPerSecond:F0} B/s {item.Detail}");
        }
    }
}
