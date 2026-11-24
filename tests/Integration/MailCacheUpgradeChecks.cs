using Microsoft.Data.Sqlite;
using NextcloudWidget.Core;

internal static class MailCacheUpgradeChecks
{
    internal static void Run()
    {
        var temp = Directory.CreateTempSubdirectory("ncw-cache-upgrade-");
        try
        {
            var file = Path.Combine(temp.FullName, "account.db");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE mail(id TEXT PRIMARY KEY, subject TEXT NOT NULL, sender TEXT NOT NULL, date TEXT NOT NULL, body TEXT NOT NULL, unread INTEGER NOT NULL);
                    INSERT INTO mail VALUES('11','A','sender','2026-01-01T00:00:00Z','old body A',1),('22','B','sender','2026-01-01T00:00:00Z','old body B',1);
                    CREATE TABLE lists(id TEXT PRIMARY KEY,name TEXT NOT NULL,writable INTEGER NOT NULL,token TEXT,present INTEGER NOT NULL DEFAULT 1);
                    INSERT INTO lists VALUES('/old-calendar','Previous calendar',1,'old-sync-token',1);
                    CREATE TABLE tasks(id TEXT PRIMARY KEY,list TEXT NOT NULL,etag TEXT,remote TEXT,local TEXT,state TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO tasks VALUES('/old-task','/old-calendar',NULL,NULL,'previous unsynced bytes','pending',7);
                    """;
                command.ExecuteNonQuery();
            }
            var cache = new WidgetCache(file);
            var stale = cache.Mail();
            cache.QueueMailActions(["11"], "read");
            cache.QueueMailActions(["22"], "trash");
            cache.SaveMail(stale);
            cache = new WidgetCache(file);
            var first = cache.Mail().Single();
            Check(first.Id == "11" && first.Body == "old body A" && !first.Unread && cache.PendingMailOperationCount() == 2);
            Check(cache.Version("/old-calendar").Token == "old-sync-token" && cache.Pending("/old-calendar").Single() is { Revision: 7, Local: "previous unsynced bytes" });
            cache.AcceptMailAction("11", "read");
            cache.AcceptMailAction("22", "trash");
            cache.SaveMail(stale);
            Check(cache.Mail().Single() is { Id: "11", Unread: false } && cache.PendingMailOperationCount() == 0);
            cache.SaveMail([first]);
            cache.SaveMail([first with { Unread = true }]); // A subsequent upstream mark-unread is authoritative.
            Check(cache.Mail().Single() is { Id: "11", Unread: true, Body: "old body A" });
            cache.QueueMailActions(["11"], "read");
            cache.AcceptMailAction("11", "read");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                db.Open();
                using var age = db.CreateCommand();
                age.CommandText = "UPDATE mail_actions SET accepted=$when WHERE id='11' AND kind='read'";
                age.Parameters.AddWithValue("$when", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 121);
                age.ExecuteNonQuery();
            }
            // The other device reverts to unread before this client ever pulls a read flag.
            cache.SaveMail(stale);
            Check(cache.Mail().Single() is { Id: "11", Unread: true }); // Old inbox snapshot also includes the moved ID.
            Console.WriteLine("PASS 0.1 SQLite upgrade retains pending task, calendar token, bodies, flags and durable actions across stale pulls.");
        }
        finally { temp.Delete(true); }
    }

    private static void Check(bool result)
    {
        if (!result) throw new InvalidDataException("Mail cache upgrade/reconciliation invariant failed.");
    }
}
