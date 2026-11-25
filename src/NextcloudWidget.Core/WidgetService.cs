using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NextcloudWidget.Core.Mail;

namespace NextcloudWidget.Core;

public sealed class WidgetService : IDisposable
{
    private readonly string directory;
    private readonly IDeviceSecrets secrets;
    private readonly Func<HttpClient> clientFactory;
    private readonly SemaphoreSlim setup = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private Session? active;
    private bool disposed;
    private string ProfileFile => Path.Combine(directory, "account.json");
    private string CacheFile(Guid id) => Path.Combine(directory, id.ToString("N") + ".db");
    public bool IsConnected { get { lock (gate) return active != null; } }
    public Uri? Server { get { lock (gate) return active?.Account.Server; } }

    private sealed class Session(Account account, HttpClient http, WidgetCache cache)
    {
        internal readonly Account Account = account;
        internal readonly HttpClient Http = http;
        internal readonly WidgetCache Cache = cache;
        internal readonly CalDavClient Dav = new(http, account.Server);
        internal readonly CancellationTokenSource Stop = new();
        internal readonly SemaphoreSlim TasksSync = new(1), MailSync = new(1);
        internal readonly SemaphoreSlim[] Bodies = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Users;
        internal bool Closing;
    }

    public WidgetService(string dataDirectory) : this(dataDirectory, new WindowsDeviceSecrets(), () =>
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(60) })
    { }

    internal WidgetService(string dataDirectory, IDeviceSecrets secrets, Func<HttpClient> clientFactory)
    {
        directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        this.secrets = secrets;
        this.clientFactory = clientFactory;
        if (!File.Exists(ProfileFile)) return;
        var account = JsonSerializer.Deserialize<Account>(File.ReadAllText(ProfileFile))
            ?? throw new InvalidDataException("无法读取本地账号信息。");
        _ = Authorization.BaseUri(account.Server);
        active = Open(account, secrets.Read(account.CacheId));
    }

    private Session Open(Account account, string? password)
    {
        var cache = new WidgetCache(CacheFile(account.CacheId));
        var client = clientFactory();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NextcloudWindowsWidget/1.0");
        if (password != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(account.User + ":" + password)));
        return new Session(account, client, cache);
    }

    public async Task ConnectAsync(Uri server, Action<Uri> openBrowser, CancellationToken ct = default)
    {
        server = Authorization.BaseUri(server);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        ct = cancellation.Token;
        await setup.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
            using var anonymous = clientFactory();
            anonymous.DefaultRequestHeaders.UserAgent.ParseAdd("NextcloudWindowsWidget/1.0");
            var login = await Authorization.LoginAsync(anonymous, server, openBrowser, ct).ConfigureAwait(false);
            Session? old;
            lock (gate) old = active;
            if (old != null && (old.Account.Server != login.Server || old.Account.User != login.User))
                throw new InvalidOperationException("请先断开当前账号，再连接另一个账号。");
            var account = old?.Account ?? new Account(Guid.NewGuid(), login.Server, login.User);
            var next = Open(account, login.Password);
            try
            {
                secrets.Write(account.CacheId, account.User, login.Password);
                var temp = ProfileFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(account));
                File.Move(temp, ProfileFile, true);
            }
            catch
            {
                next.Http.Dispose();
                if (old == null) secrets.Delete(account.CacheId);
                throw;
            }
            await StopSession().ConfigureAwait(false);
            lock (gate) active = next;
        }
        finally { setup.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await setup.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Session? old = await StopSession().ConfigureAwait(false);
            if (old == null) return;
            secrets.Delete(old.Account.CacheId);
            File.Delete(ProfileFile);
            foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
                File.Delete(CacheFile(old.Account.CacheId) + suffix);
        }
        finally { setup.Release(); }
    }

    private async Task<Session?> StopSession()
    {
        Session? old;
        lock (gate)
        {
            old = active;
            if (old == null) return null;
            active = null;
            old.Closing = true;
            if (old.Users == 0) old.Drained.TrySetResult();
        }
        await old.Stop.CancelAsync().ConfigureAwait(false);
        await old.Drained.Task.ConfigureAwait(false);
        old.Http.Dispose();
        old.Stop.Dispose();
        return old;
    }

    private async Task<T> Use<T>(Func<Session, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        Session session;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            session = active ?? throw new InvalidOperationException("请先连接 Nextcloud。");
            session.Users++;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token, ct);
            linked.Token.ThrowIfCancellationRequested();
            return await action(session, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
                if (--session.Users == 0 && session.Closing) session.Drained.TrySetResult();
        }
    }
    private Task<T> Local<T>(Func<WidgetCache, T> operation, CancellationToken ct) =>
        Use((s, token) => Task.Run(() => operation(s.Cache), token), ct);

    public Task<IReadOnlyList<TaskList>> GetTaskListsAsync(CancellationToken ct = default) =>
        Local<IReadOnlyList<TaskList>>(c => c.Lists(), ct);
    public Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken ct = default) =>
        Local<IReadOnlyList<TaskItem>>(c => c.Tasks(), ct);
    public Task<IReadOnlyList<MailItem>> GetMailAsync(CancellationToken ct = default) =>
        Local<IReadOnlyList<MailItem>>(c => c.Mail(), ct);
    public Task<IReadOnlyList<MailItem>> GetMailHeadersAsync(CancellationToken ct = default) =>
        Local<IReadOnlyList<MailItem>>(c => c.MailHeaders(), ct);
    public Task<int> GetPendingMailOperationCountAsync(CancellationToken ct = default) =>
        Local(c => c.PendingMailOperationCount(), ct);
    public Task MarkMailReadAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        Local(c => { c.QueueMailActions(ids, "read"); return true; }, ct);
    public Task MoveMailToTrashAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        Local(c => { c.QueueMailActions(ids, "trash"); return true; }, ct);
    public Task<SyncInfo> GetSyncInfoAsync(CancellationToken ct = default) => Local(c => c.SyncInfo(), ct);
    public Task<TaskItem?> GetConflictServerVersionAsync(string id, CancellationToken ct = default) =>
        Local(c => c.ConflictRemote(id), ct);
    public Task<string> CreateTaskAsync(string listId, string title, string description, DateTimeOffset? due, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return Local(c => c.Create(listId, title.Trim(), description, due), ct);
    }
    public Task EditTaskAsync(string id, string title, string description, DateTimeOffset? due, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return Local(c => { c.Edit(id, ics => TaskCalendar.Edit(ics, title.Trim(), description, due)); return true; }, ct);
    }
    public Task SetTaskCompletedAsync(string id, bool completed, CancellationToken ct = default) =>
        Local(c => { c.Edit(id, ics => TaskCalendar.SetCompleted(ics, completed)); return true; }, ct);
    public Task ResolveConflictAsync(string id, bool keepLocal, CancellationToken ct = default) =>
        Local(c => { c.Resolve(id, keepLocal); return true; }, ct);

    public Task SyncTasksAsync(CancellationToken ct = default) => Use(async (s, token) =>
    {
        await s.TasksSync.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var lists = await s.Dav.DiscoverAsync(token).ConfigureAwait(false);
            s.Cache.SetLists(lists);
            foreach (var list in lists)
            {
                await Pull(s, list, token).ConfigureAwait(false);
                bool checkChanges = false;
                foreach (var task in s.Cache.Pending(list.Id))
                {
                    if (!list.CanWrite)
                    {
                        s.Cache.WriteConflict(task.Id);
                        continue;
                    }
                    // A server can omit a fresh ETag on success. Re-read before editing again.
                    if (task.Remote != null && string.IsNullOrEmpty(task.ETag))
                    {
                        checkChanges = true;
                        continue;
                    }
                    try
                    {
                        var etag = await s.Dav.WriteAsync(task.Id, task.Local!, task.ETag, token).ConfigureAwait(false);
                        s.Cache.WriteAccepted(task, etag);
                        checkChanges |= string.IsNullOrEmpty(etag);
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    {
                        s.Cache.WriteConflict(task.Id);
                        checkChanges = true;
                    }
                }
                if (checkChanges) await Pull(s, list, token, full: true).ConfigureAwait(false);
            }
            s.Cache.MarkSync("tasks");
            return true;
        }
        finally { s.TasksSync.Release(); }
    }, ct);

    private static async Task Pull(Session s, TaskList list, CancellationToken ct, bool full = false)
    {
        var version = s.Cache.Version(list.Id);
        var changes = await s.Dav.ReadChangesAsync(list, full ? null : version.Token, version.Tags, ct).ConfigureAwait(false);
        s.Cache.Import(list, changes);
    }

    public Task SyncMailAsync(CancellationToken ct = default) => Use(async (s, token) =>
    {
        await s.MailSync.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var rejected = new List<(string Id, string Kind, HttpRequestException Error)>();
            var actionFailure = await PushMailActions(s, rejected, token).ConfigureAwait(false);
            var messages = await MailClient.FetchAsync(s.Http, s.Account.Server, [], token).ConfigureAwait(false);
            var inbox = messages.Select(message => message.Id).ToHashSet();
            foreach (var (id, kind, error) in rejected)
            {
                // A move can succeed just before its reply is lost. A missing row in the
                // 50-message window alone cannot distinguish that from an older inbox item.
                if (!inbox.Contains(id) && await MailClient.IsMessageGoneAsync(s.Http, s.Account.Server, id, token).ConfigureAwait(false))
                {
                    s.Cache.AcceptMailAction(id, kind);
                    continue;
                }
                s.Cache.RemoveMailAction(id, kind);
                actionFailure ??= new IOException($"服务器拒绝了邮件操作（HTTP {(int)error.StatusCode!}），已恢复本地显示。", error);
            }
            s.Cache.SaveMail(messages);
            s.Cache.MarkSync("mail");
            Exception? bodyFailure = null;
            foreach (var id in s.Cache.MissingBodies())
            {
                try { await Body(s, id, token, refreshLegacy: true).ConfigureAwait(false); }
                catch (HttpRequestException ex) when (ex.StatusCode is not HttpStatusCode.Unauthorized)
                {
                    // Failures in one message leave downloaded headers and bodies available.
                    bodyFailure = ex;
                    if (ex.StatusCode == null || ex.StatusCode == HttpStatusCode.TooManyRequests) break;
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
                {
                    bodyFailure = ex;
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    bodyFailure = ex;
                    break;
                }
            }
            if (actionFailure != null) throw actionFailure;
            if (bodyFailure != null) throw new IOException("邮件列表已更新，部分正文将于下次同步时重试。", bodyFailure);
            return true;
        }
        finally { s.MailSync.Release(); }
    }, ct);

    private static async Task<Exception?> PushMailActions(Session session,
        List<(string Id, string Kind, HttpRequestException Error)> rejected, CancellationToken ct)
    {
        Exception? failure = null;
        var actions = new MailActionClient(session.Http, session.Account.Server);
        foreach (var (id, kind) in session.Cache.PendingMailActions())
        {
            try
            {
                if (kind == "read")
                    await actions.MarkReadAsync(id, ct).ConfigureAwait(false);
                else if (kind == "trash")
                    await actions.MoveToTrashAsync(id, ct).ConfigureAwait(false);
                else
                {
                    session.Cache.RemoveMailAction(id, kind);
                    failure ??= new InvalidDataException("检测到无法识别的本地邮件操作，已取消该操作。");
                    continue;
                }
                session.Cache.AcceptMailAction(id, kind);
            }
            catch (MailTrashUnavailableException ex)
            {
                session.Cache.RemoveMailAction(id, kind);
                failure = ex;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                or HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Gone or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented)
            {
                rejected.Add((id, kind, ex));
            }
        }
        return failure;
    }

    public async Task<string> GetMailBodyAsync(string id, CancellationToken ct = default) =>
        (await GetMailContentAsync(id, ct).ConfigureAwait(false)).Text;
    public Task<MailContent> GetMailContentAsync(string id, CancellationToken ct = default) =>
        Use((s, token) => Body(s, id, token), ct);
    private static async Task<MailContent> Body(Session s, string id, CancellationToken ct, bool refreshLegacy = false)
    {
        var cached = s.Cache.ReadContent(id);
        if (cached.Text.Length != 0 && (cached.Html != null || !refreshLegacy))
            return new MailContent(cached.Html == null ? CompactLegacyBody(cached.Text) : cached.Text, cached.Html ?? "");
        var single = s.Bodies[(uint)StringComparer.Ordinal.GetHashCode(id) % s.Bodies.Length];
        await single.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = s.Cache.ReadContent(id);
            if (cached.Html != null && cached.Text.Length != 0) return new MailContent(cached.Text, cached.Html);
            var body = await MailClient.FetchContentAsync(s.Http, s.Account.Server, id, ct).ConfigureAwait(false);
            s.Cache.SaveBody(id, body.Text, body.Html);
            return body;
        }
        finally { single.Release(); }
    }

    private static string CompactLegacyBody(string body)
    {
        var lines = body.ReplaceLineEndings("\n").Split('\n');
        var result = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length != 0 || (result.Count != 0 && result[^1].Length != 0)) result.Add(trimmed);
        }
        return string.Join("\n", result).TrimEnd();
    }

    public Task<byte[]> GetMailInlineImageAsync(string id, string partId, CancellationToken ct = default) =>
        Use(async (s, token) =>
        {
            var url = MailImages.AttachmentUrl(s.Account.Server, id, partId);
            if (s.Cache.ReadImage(id, partId) is { } cached) return cached;
            var bytes = await MailImages.DownloadAsync(s.Http, url, token, attachment: true).ConfigureAwait(false);
            s.Cache.SaveImage(id, partId, bytes);
            return bytes;
        }, ct);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        lifetime.Cancel();
        _ = CloseAfterSetupAsync();
    }

    private async Task CloseAfterSetupAsync()
    {
        await setup.WaitAsync().ConfigureAwait(false);
        try { await StopSession().ConfigureAwait(false); }
        finally { setup.Release(); }
    }
}
