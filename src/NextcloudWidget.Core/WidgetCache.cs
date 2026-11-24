using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NextcloudWidget.Core;

internal sealed record CachedTask(string Id, string ListId, string? ETag, string? Remote, string? Local,
    string State, long Revision);

// A transaction never spans a network request. Each account has a separate database.
internal sealed class WidgetCache
{
    private readonly string connectionString;
    private readonly object gate = new();

    internal WidgetCache(string file)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString();
        Change(db => Run(db, """
            CREATE TABLE IF NOT EXISTS lists(id TEXT PRIMARY KEY, name TEXT NOT NULL, writable INTEGER NOT NULL, token TEXT, present INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY, list TEXT NOT NULL, etag TEXT, remote TEXT, local TEXT, state TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS tasks_list ON tasks(list);
            CREATE TABLE IF NOT EXISTS mail(id TEXT PRIMARY KEY, subject TEXT NOT NULL, sender TEXT NOT NULL, date TEXT NOT NULL, body TEXT NOT NULL, unread INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS sync(channel TEXT PRIMARY KEY, stamp TEXT NOT NULL);
            """));
    }

    private T Read<T>(Func<SqliteConnection, T> action)
    {
        lock (gate)
        {
            using var db = new SqliteConnection(connectionString);
            db.Open();
            return action(db);
        }
    }

    private void Change(Action<SqliteConnection> action) => Read(db =>
    {
        using var tx = db.BeginTransaction();
        action(db);
        tx.Commit();
        return true;
    });

    private static SqliteCommand Command(SqliteConnection db, string sql, params object?[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }

    private static void Run(SqliteConnection db, string sql, params object?[] args)
    {
        using var cmd = Command(db, sql, args);
        cmd.ExecuteNonQuery();
    }

    private static List<T> Rows<T>(SqliteConnection db, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var cmd = Command(db, sql, args);
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private const string TaskColumns = "SELECT id,list,etag,remote,local,state,revision FROM tasks";
    private static CachedTask MapTask(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4), r.GetString(5), r.GetInt64(6));
    private static CachedTask? Row(SqliteConnection db, string id) =>
        Rows(db, TaskColumns + " WHERE id=$p0", MapTask, id).SingleOrDefault();
    private static TaskList MapList(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetBoolean(2));
    private static TaskList GetList(SqliteConnection db, string id) =>
        Rows(db, "SELECT id,name,writable FROM lists WHERE id=$p0", MapList, id).Single();
    private static MailItem MapMail(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
        DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture), r.GetString(4), r.GetBoolean(5));
    private const string MailColumns = "SELECT id,subject,sender,date,body,unread FROM mail";

    internal List<TaskList> Lists() => Read(db => Rows(db, "SELECT id,name,writable FROM lists WHERE present=1 ORDER BY name", MapList));
    internal List<TaskItem> Tasks() => Read(db =>
    {
        var lists = Rows(db, "SELECT id,name,writable FROM lists", MapList).ToDictionary(x => x.Id);
        return Rows(db, TaskColumns, MapTask).Select(t =>
            TaskCalendar.Parse(t.Id, lists[t.ListId], t.Local ?? t.Remote!) with { SyncState = t.State, ListId = t.ListId }).ToList();
    });
    internal TaskItem? ConflictRemote(string id) => Read(db =>
    {
        var value = Row(db, id);
        return value?.Remote is not null && value.State == "conflict"
            ? TaskCalendar.Parse(id, GetList(db, value.ListId), value.Remote) with { ListId = value.ListId } : null;
    });

    internal void SetLists(IReadOnlyList<TaskList> lists) => Change(db =>
    {
        Run(db, "UPDATE lists SET present=0");
        foreach (var list in lists)
            Run(db, "INSERT INTO lists(id,name,writable) VALUES($p0,$p1,$p2) ON CONFLICT(id) DO UPDATE SET name=excluded.name,writable=excluded.writable,present=1", list.Id, list.Name, list.CanWrite);
        Run(db, "DELETE FROM tasks WHERE local IS NULL AND list IN (SELECT id FROM lists WHERE present=0)");
        Run(db, "UPDATE tasks SET state='conflict',remote=NULL,etag=NULL WHERE list IN (SELECT id FROM lists WHERE present=0)");
        Run(db, "UPDATE lists SET writable=0,token=NULL WHERE present=0");
    });

    internal (string? Token, IReadOnlyDictionary<string, string> Tags) Version(string listId) => Read(db =>
        (Rows(db, "SELECT token FROM lists WHERE id=$p0", r => Str(r, 0), listId).Single(),
         (IReadOnlyDictionary<string, string>)Rows(db, "SELECT id,etag FROM tasks WHERE list=$p0 AND remote IS NOT NULL", r => (r.GetString(0), Str(r, 1) ?? ""), listId).ToDictionary(x => x.Item1, x => x.Item2)));

    internal void Import(TaskList list, DavChanges changes) => Change(db =>
    {
        foreach (var item in changes.Changed)
        {
            var incoming = TaskCalendar.Parse(item.Id, list, item.Ics);
            var old = Row(db, item.Id);
            if (old?.Local != null && old.ETag != item.ETag)
            {
                var local = TaskCalendar.Parse(item.Id, list, old.Local);
                if (local.Title != incoming.Title || local.Description != incoming.Description || local.Due != incoming.Due || local.Completed != incoming.Completed)
                {
                    Run(db, "UPDATE tasks SET etag=$p0,remote=$p1,state='conflict' WHERE id=$p2", item.ETag, item.Ics, item.Id);
                    continue;
                }
            }
            else if (old?.Local != null) continue;
            Run(db, "INSERT INTO tasks(id,list,etag,remote,state) VALUES($p0,$p1,$p2,$p3,'synced') ON CONFLICT(id) DO UPDATE SET etag=excluded.etag,remote=excluded.remote,local=NULL,state='synced'",
                item.Id, list.Id, item.ETag, item.Ics);
        }
        foreach (var id in changes.Deleted)
        {
            Run(db, "DELETE FROM tasks WHERE id=$p0 AND local IS NULL", id);
            Run(db, "UPDATE tasks SET state='conflict',remote=NULL,etag=NULL WHERE id=$p0 AND local IS NOT NULL AND remote IS NOT NULL", id);
        }
        Run(db, "UPDATE lists SET token=$p0 WHERE id=$p1", changes.Token, list.Id);
    });

    internal List<CachedTask> Pending(string listId) => Read(db => Rows(db, TaskColumns + " WHERE list=$p0 AND state='pending'", MapTask, listId));
    internal void WriteAccepted(CachedTask sent, string? etag) => Change(db =>
        Run(db, "UPDATE tasks SET remote=$p0,etag=$p1,state=CASE WHEN revision=$p2 THEN 'synced' ELSE 'pending' END,local=CASE WHEN revision=$p2 THEN NULL ELSE local END WHERE id=$p3",
            sent.Local, etag, sent.Revision, sent.Id));
    internal void WriteConflict(string id) => Change(db =>
        Run(db, "UPDATE tasks SET state='conflict' WHERE id=$p0 AND local IS NOT NULL", id));

    internal string Create(string listId, string title, string description, DateTimeOffset? due)
    {
        string id = listId.TrimEnd('/') + "/" + Guid.NewGuid().ToString("N") + ".ics";
        Change(db =>
        {
            if (!GetList(db, listId).CanWrite) throw new InvalidOperationException("此清单是只读的。");
            Run(db, "INSERT INTO tasks(id,list,local,state) VALUES($p0,$p1,$p2,'pending')", id, listId, TaskCalendar.Create(title, description, due));
        });
        return id;
    }
    internal void Edit(string id, Func<string, string> update) => Change(db =>
    {
        var t = Row(db, id) ?? throw new KeyNotFoundException("任务已删除。");
        if (!GetList(db, t.ListId).CanWrite) throw new InvalidOperationException("此清单是只读的。");
        Run(db, "UPDATE tasks SET local=$p0,state=CASE WHEN state='conflict' THEN state ELSE 'pending' END,revision=revision+1 WHERE id=$p1",
            update(t.Local ?? t.Remote!), id);
    });
    internal void Resolve(string id, bool keepLocal) => Change(db =>
    {
        var t = Row(db, id);
        if (t is not { State: "conflict" }) return;
        if (keepLocal)
        {
            var list = GetList(db, t.ListId);
            if (!list.CanWrite) throw new InvalidOperationException("无法向此清单写入，请将本地内容复制到可写清单。");
            string local = t.Local!;
            if (t.Remote != null)
            {
                var chosen = TaskCalendar.Parse(id, list, local);
                local = TaskCalendar.SetCompleted(TaskCalendar.Edit(t.Remote, chosen.Title, chosen.Description, chosen.Due), chosen.Completed);
            }
            Run(db, "UPDATE tasks SET state='pending',local=$p0,revision=revision+1 WHERE id=$p1", local, id);
        }
        else if (t.Remote == null) Run(db, "DELETE FROM tasks WHERE id=$p0", id);
        else Run(db, "UPDATE tasks SET local=NULL,state='synced',revision=revision+1 WHERE id=$p0", id);
    });

    internal List<MailItem> Mail() => Read(db => Rows(db, MailColumns + " ORDER BY date DESC", MapMail));
    internal List<MailItem> MailHeaders() => Read(db => Rows(db,
        "SELECT id,subject,sender,date,'',unread FROM mail ORDER BY date DESC", MapMail));
    internal List<string> MissingBodies() => Read(db => Rows(db,
        "SELECT id FROM mail WHERE body='' ORDER BY date DESC", r => r.GetString(0)));
    internal MailItem? Message(string id) => Read(db => Rows(db, MailColumns + " WHERE id=$p0", MapMail, id).SingleOrDefault());
    internal void SaveMail(IReadOnlyList<MailItem> messages) => Change(db =>
    {
        Run(db, "CREATE TEMP TABLE current_mail(id TEXT PRIMARY KEY)");
        foreach (var m in messages)
        {
            Run(db, "INSERT INTO current_mail VALUES($p0)", m.Id);
            Run(db, "INSERT INTO mail VALUES($p0,$p1,$p2,$p3,$p4,$p5) ON CONFLICT(id) DO UPDATE SET subject=excluded.subject,sender=excluded.sender,date=excluded.date,unread=excluded.unread,body=CASE WHEN mail.body='' THEN excluded.body ELSE mail.body END",
                m.Id, m.Subject, m.Sender, m.Date.ToUniversalTime().ToString("O"), m.Body, m.Unread);
        }
        Run(db, "DELETE FROM mail WHERE id NOT IN (SELECT id FROM current_mail)");
    });
    internal void SaveBody(string id, string body) => Change(db => Run(db, "UPDATE mail SET body=$p0 WHERE id=$p1", body, id));
    internal void MarkSync(string channel) => Change(db => Run(db, "INSERT INTO sync VALUES($p0,$p1) ON CONFLICT(channel) DO UPDATE SET stamp=excluded.stamp", channel, DateTimeOffset.UtcNow.ToString("O")));
    internal SyncInfo SyncInfo() => Read(db =>
    {
        var stamps = Rows(db, "SELECT channel,stamp FROM sync", r => (r.GetString(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture))).ToDictionary(x => x.Item1, x => x.Item2);
        return new SyncInfo(stamps.TryGetValue("tasks", out var t) ? t : null, stamps.TryGetValue("mail", out var m) ? m : null);
    });
}
