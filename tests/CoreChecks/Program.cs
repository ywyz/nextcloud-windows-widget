using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using NextcloudWidget.Core;

internal static class Program
{
    private static void Check(bool ok,string description)
    {
        if (!ok) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }

    private static async Task Main(string[] args)
    {
        if (args is ["--seed-update" or "--verify-update",var path])
        {
            await CheckUpgradeData(args[0]=="--seed-update",Path.GetFullPath(path));
            return;
        }
        var dir = Directory.CreateTempSubdirectory("nextcloud-core-checks-").FullName;
        try
        {
            var server = new Uri("https://test.invalid/subdir/");
            var list = new TaskList(server + "tasks/","工作清单",true);
            var id = list.Id + "remote.ics";
            CheckCalendarEdges(id,list);
            await CheckDavFallback(server,list);
            var original = TaskCalendar.Create("测试，逗号","第一行\n第二行",null);
            original = original.Replace("END:VTODO","X-CUSTOM;LANGUAGE=zh:保留\r\nEND:VTODO");
            var cache = new WidgetCache(Path.Combine(dir,"test.db"));
            cache.SetLists([list]);
            cache.Import(list,new DavChanges([new(id,"\"v1\"",original)],[],"token1"));
            Check(cache.Tasks().Single().Description == "第一行\n第二行","iCalendar multiline text round trips");
            cache.Edit(id,ics => TaskCalendar.SetCompleted(ics,true));
            var sent = cache.Pending(list.Id).Single();
            Check(sent.ETag == "\"v1\"" && sent.Local!.Contains("X-CUSTOM"),"conditional update preserves unrecognized properties");
            var reopened = new WidgetCache(Path.Combine(dir,"test.db"));
            Check(reopened.Tasks().Single() is { Completed:true, SyncState:"pending" },"offline write survives restart");
            var remote = TaskCalendar.Edit(original,"服务器修改","远端备注",null);
            reopened.Import(list,new DavChanges([new(id,"\"v2\"",remote)],[],"token2"));
            Check(reopened.Tasks().Single().SyncState == "conflict" && reopened.ConflictRemote(id)?.Title == "服务器修改","remote edit preserves both copies");
            reopened.Resolve(id,true);
            sent = reopened.Pending(list.Id).Single();
            Check(sent.ETag == "\"v2\"" && sent.Local!.Contains("X-CUSTOM"),"keep-local retries against latest remote ETag");
            reopened.Edit(id,ics => TaskCalendar.Edit(ics,"另一条离线编辑","",null));
            reopened.WriteAccepted(sent,"\"v3\"");
            Check(reopened.Tasks().Single().Title == "另一条离线编辑" && reopened.Pending(list.Id).Count == 1,"write in flight preserves a later local edit");
            reopened.Import(list,new DavChanges([], [id],"token3"));
            Check(reopened.ConflictRemote(id) == null && reopened.Tasks().Single().SyncState == "conflict","remote deletion retains unsent work");
            reopened.Resolve(id,false);
            Check(reopened.Tasks().Count == 0,"accepting remote deletion removes local conflict");
            var localId = reopened.Create(list.Id,"本地新建","",null);
            Check(reopened.Pending(list.Id).Single().Id == localId,"offline create has a stable resource URL");
            reopened.SaveMail([new("77","主题","发件人",DateTimeOffset.Now,"",true)]);
            reopened.SaveBody("77","缓存正文");
            reopened.SaveMail([new("77","主题","发件人",DateTimeOffset.Now,"",false)]);
            Check(reopened.Mail().Single() is { Body:"缓存正文",Unread:false },"header refresh preserves concurrently downloaded body");
            reopened.SaveMail([]);
            Check(reopened.Mail().Count == 0,"inbox removals clear cached message");
            await CheckLoginAndReopen(dir,server);
        }
        finally { Directory.Delete(dir,true); }
    }

    private static async Task CheckDavFallback(Uri server,TaskList list)
    {
        XNamespace d="DAV:",c="urn:ietf:params:xml:ns:caldav";
        string id=list.Id+"item.ics";
        var ics=TaskCalendar.Create("DAV fixture","",null);
        XElement Row(string resource,params XElement[] properties) => new(d+"response",new XElement(d+"href",resource),
            new XElement(d+"propstat",new XElement(d+"prop",properties),new XElement(d+"status","HTTP/1.1 200 OK")));
        HttpResponseMessage Multi(params XElement[] rows) => new((HttpStatusCode)207)
            {Content=new StringContent(new XElement(d+"multistatus",rows).ToString(),Encoding.UTF8,"application/xml")};
        int downloads=0;
        using var http=new HttpClient(new TestHandler(request =>
        {
            var query=XElement.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            if(query.Name==d+"sync-collection")
            {
                if ((string?)query.Element(d+"sync-token")=="page1")
                    return Multi(Row(id,new XElement(d+"getetag","E1")),
                        new XElement(d+"response",new XElement(d+"href",list.Id),new XElement(d+"status","HTTP/1.1 507 Insufficient Storage")),
                        new XElement(d+"sync-token","page2"));
                if ((string?)query.Element(d+"sync-token")=="page2")
                    return Multi(new XElement(d+"response",new XElement(d+"href",id),new XElement(d+"status","HTTP/1.1 404 Not Found")),
                        new XElement(d+"sync-token","done"));
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {Content=new StringContent(new XElement(d+"error",new XElement(d+"valid-sync-token")).ToString())};
            }
            if(query.Name==d+"propfind") return Multi(Row(list.Id,new XElement(d+"sync-token","new-token")));
            if(query.Name==c+"calendar-query") return Multi(Row(id,new XElement(d+"getetag","\"E\"")));
            if(query.Name==c+"calendar-multiget")
            {
                downloads++;
                return Multi(Row(id,new XElement(d+"getetag","\"E\""),new XElement(c+"calendar-data",ics)));
            }
            throw new InvalidOperationException("Unexpected DAV report");
        }));
        var dav=new CalDavClient(http,server);
        var synced=await dav.ReadChangesAsync(list,"expired",new Dictionary<string,string>{{list.Id+"gone.ics","old"}},default);
        Check(synced.Token=="new-token" && synced.Changed.Single().Id==id && synced.Deleted.Single().EndsWith("gone.ics"),
            "invalid DAV token refreshes inventory and propagates a server deletion");
        var unchanged=await dav.ReadChangesAsync(list,null,new Dictionary<string,string>{{id,"\"E\""}},default);
        Check(downloads==1 && unchanged.Changed.Count==0,"full-inventory fallback reuses identical ETags");
        var paged=await dav.ReadChangesAsync(list,"page1",new Dictionary<string,string>(),default);
        Check(paged.Token=="done" && paged.Deleted.Single()==id && paged.Changed.Count==0 && downloads==1,
            "paginated DAV sync applies the final state without downloading a deleted item");
        bool denied=false;
        try { await dav.WriteAsync("https://outside.invalid/task.ics",ics,null,default); } catch(InvalidDataException) { denied=true; }
        Check(denied,"CalDAV never sends authenticated requests to a foreign resource origin");
    }

    private static void CheckCalendarEdges(string id,TaskList list)
    {
        string sample=string.Join("\r\n", "BEGIN:VCALENDAR","VERSION:2.0",
            "BEGIN:VTODO","UID:master","RECURRENCE-ID:20261028T100000Z","SUMMARY:例外任务","END:VTODO",
            "BEGIN:VTODO","UID:master","RRULE:FREQ=WEEKLY","SUMMARY:主任务",
            "DUE;TZID=Asia/Shanghai:20261124T170000","BEGIN:VALARM","ACTION:DISPLAY",
            "DESCRIPTION:这是提醒","END:VALARM","END:VTODO","END:VCALENDAR");
        var task=TaskCalendar.Parse(id,list,sample);
        Check(task.Title=="主任务" && task.Due==new DateTimeOffset(2026,11,24,9,0,0,TimeSpan.Zero),
            "master selected before editing when a recurrence exception appears first");
        var title=string.Concat(Enumerable.Repeat("长标题🙂",24))+"\n下一行";
        var description="备注\n逗号,分号;斜杠\\";
        var updated=TaskCalendar.SetCompleted(TaskCalendar.Edit(sample,title,description,task.Due),true);
        var parsed=TaskCalendar.Parse(id,list,updated);
        Check(parsed.Title==title && parsed.Description==description && parsed.Completed &&
            updated.Split("\r\n").All(line=>Encoding.UTF8.GetByteCount(line)<=75),
            "UTF-8 folded lines, multiline title and escaped description round trip");
        Check(updated.Contains("DUE;TZID=Asia/Shanghai:20261124T170000") &&
            updated.Contains("DESCRIPTION:这是提醒\r\nEND:VALARM") &&
            updated.Contains("RECURRENCE-ID:20261028T100000Z\r\nSUMMARY:例外任务\r\nEND:VTODO"),
            "unchanged TZID, nested alarm and recurrence override survive edits");
    }

    private static async Task CheckUpgradeData(bool seed,string directory)
    {
        var server = new Uri("https://127.0.0.1:1/nextcloud/");
        var profile = Path.Combine(directory,"account.json");
        if (seed)
        {
            Directory.CreateDirectory(directory);
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidOperationException("Upgrade fixture requires an empty directory.");
            var account = new Account(Guid.NewGuid(),server,"upgrade-check");
            var cache = new WidgetCache(Path.Combine(directory,account.CacheId.ToString("N")+".db"));
            cache.SetLists([new TaskList(new Uri(server,"tasks/").AbsoluteUri,"Fixture",true)]);
            File.WriteAllText(profile,JsonSerializer.Serialize(account));
        }
        var saved = JsonSerializer.Deserialize<Account>(File.ReadAllText(profile));
        if (saved?.User!="upgrade-check" || saved.Server!=server)
            throw new InvalidOperationException("Unexpected upgrade fixture identity.");
        using var service = new WidgetService(directory,new TestSecrets(), () =>
            new HttpClient(new TestHandler(_ => throw new InvalidOperationException("Upgrade checks must be offline."))));
        if (seed)
        {
            var list=(await service.GetTaskListsAsync()).Single();
            await service.CreateTaskAsync(list.Id,"Upgrade acceptance","",null);
        }
        Check((await service.GetTasksAsync()).Single() is {Title:"Upgrade acceptance",SyncState:"pending"},"upgrade cache retains the offline task");
    }

    private static async Task CheckLoginAndReopen(string dir,Uri server)
    {
        var secrets = new TestSecrets();
        int polls=0;
        var handler = new TestHandler(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path == "/subdir/index.php/login/v2")
                return Json(new {poll=new {token="poll-token",endpoint=server+"poll"},login=server+"grant"});
            if (path == "/subdir/poll")
                return ++polls == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                    Json(new {server=server.AbsoluteUri,loginName="alice",appPassword="fixture-password"});
            if (path.StartsWith("/subdir/ocs/v2.php/apps/mail/"))
            {
                string route = path["/subdir/ocs/v2.php/apps/mail/".Length..];
                object data = route switch
                {
                    "account/list" => new[] {new {id=1}},
                    "ocs/mailboxes" => new[] {new {name="INBOX",databaseId=7}},
                    "ocs/mailboxes/7/messages" => new[] {8,9}.Select(id => new {databaseId=id,subject="test",dateInt=100-id,flags=new {seen=false},from=Array.Empty<object>()}),
                    "message/8" => new {missingBody=true},
                    "message/9" => new {body="<p>Cached after a malformed message</p>"},
                    _ => throw new InvalidOperationException("Unexpected mail route")
                };
                return Json(new {ocs=new {meta=new {status="ok"},data}});
            }
            if (req.Method.Method=="PROPFIND") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            throw new InvalidOperationException("Unexpected network call " + path);
        });
        var root = Path.Combine(dir,"auth");
        using (var service = new WidgetService(root,secrets,() => new HttpClient(handler,false)))
        {
            Uri? opened = null;
            await service.ConnectAsync(server,uri => opened=uri);
            Check(opened == new Uri(server,"grant") && service.IsConnected && polls==2,"login v2 browser authorization and pending poll");
            Check(!File.ReadAllText(Path.Combine(root,"account.json")).Contains("fixture-password"),"profile contains no password");
            try { await service.SyncTasksAsync(); } catch(HttpRequestException) { }
            try { await service.SyncMailAsync(); } catch(IOException) { }
            var mail = await service.GetMailAsync();
            Check(mail.Count==2 && mail.Single(x=>x.Id=="8").Body=="" &&
                mail.Single(x=>x.Id=="9").Body.Contains("Cached after"),"task failure and one malformed email do not block later email bodies");
        }
        using var offline = new WidgetService(root,secrets,() => new HttpClient(handler,false));
        Check(offline.Server == server && (await offline.GetTasksAsync()).Count == 0,"offline startup reads account cache without contacting server");
        Check((await offline.GetMailBodyAsync("9")).Contains("Cached after"),"restart opens a cached email body");
        await offline.DisconnectAsync();
        Check(!offline.IsConnected && !Directory.EnumerateFiles(root).Any() && secrets.Values.Count == 0,"disconnect removes device cache and credential");
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json") };
    private sealed class TestHandler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken ct) => Task.FromResult(respond(message));
    }
    private sealed class TestSecrets : IDeviceSecrets
    {
        internal Dictionary<Guid,string> Values = new();
        public string? Read(Guid id) => Values.GetValueOrDefault(id);
        public void Write(Guid id,string user,string password) => Values[id]=password;
        public void Delete(Guid id) => Values.Remove(id);
    }
}
