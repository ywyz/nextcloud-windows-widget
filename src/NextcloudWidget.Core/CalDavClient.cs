using System.Net;
using System.Text;
using System.Xml.Linq;

namespace NextcloudWidget.Core;

internal sealed record DavChanges(List<DavResource> Changed, List<string> Deleted, string? Token);
internal sealed record DavResource(string Id, string ETag, string Ics);

internal sealed class CalDavClient(HttpClient http, Uri server)
{
    private static readonly XNamespace D = "DAV:", C = "urn:ietf:params:xml:ns:caldav";
    private Uri Resolve(Uri context, string href)
    {
        if (!Uri.TryCreate(context, href, out var uri) || !Authorization.SameOrigin(server, uri))
            throw new InvalidDataException("DAV 返回的资源地址不属于此服务器。");
        return uri;
    }
    private async Task<XElement> RequestAsync(string method, Uri uri, string depth, XElement body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Headers.Add("Depth", depth);
        request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/xml");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden && method == "REPORT")
        {
            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (xml.Contains("valid-sync-token", StringComparison.Ordinal)) throw new StaleToken();
        }
        response.EnsureSuccessStatusCode();
        var root = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Root;
        if (root?.Name != D + "multistatus") throw new InvalidDataException("DAV 未返回有效的多状态响应。");
        return root;
    }
    private sealed class StaleToken : Exception { }
    private static XElement Prop(params XName[] names) => new(D+"prop", names.Select(n => new XElement(n)));
    private static bool Status(XElement? status, string code) =>
        status?.Value.Split(' ',StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)==code;
    private static XElement? Value(XElement response, XName name) =>
        response.Elements(D+"propstat").Where(p => Status(p.Element(D+"status"),"200"))
            .Select(p=>p.Element(D+"prop")?.Element(name)).FirstOrDefault(p=>p!=null);
    private Task<XElement> Props(Uri url, string depth, CancellationToken ct, params XName[] names) =>
        RequestAsync("PROPFIND",url,depth,new XElement(D+"propfind",Prop(names)),ct);
    private async Task<Uri> DiscoverLink(Uri url,XName property,CancellationToken ct)
    {
        var result = await Props(url,"0",ct,property);
        string? href = result.Elements(D+"response").Select(r => Value(r,property)?.Element(D+"href")?.Value).FirstOrDefault(h => !string.IsNullOrEmpty(h));
        return Resolve(url,href ?? throw new InvalidDataException("服务器未提供 CalDAV 发现信息。"));
    }

    internal async Task<List<TaskList>> DiscoverAsync(CancellationToken ct)
    {
        var principal = await DiscoverLink(new Uri(server,"remote.php/dav/"),D+"current-user-principal",ct);
        var home = await DiscoverLink(principal,C+"calendar-home-set",ct);
        var result = await Props(home,"1",ct,D+"resourcetype",D+"displayname",D+"current-user-privilege-set",C+"supported-calendar-component-set");
        var lists = new List<TaskList>();
        foreach (var r in result.Elements(D+"response"))
        {
            if (Value(r,D+"resourcetype")?.Element(C+"calendar")==null) continue;
            var components = Value(r,C+"supported-calendar-component-set");
            if (components != null && !components.Elements(C+"comp").Any(e=>(string?)e.Attribute("name")=="VTODO")) continue;
            var id = Resolve(home,(string?)r.Element(D+"href") ?? throw new InvalidDataException("缺少清单地址。"));
            var privileges = Value(r,D+"current-user-privilege-set");
            bool writable = privileges?.Descendants().Any(e=>e.Name==D+"all"|| e.Name==D+"write" || e.Name==D+"write-content")==true;
            lists.Add(new TaskList(id.AbsoluteUri,Value(r,D+"displayname")?.Value??id.AbsolutePath,writable));
        }
        return lists;
    }

    internal async Task<DavChanges> ReadChangesAsync(TaskList list,string? token,IReadOnlyDictionary<string,string> knownETags,CancellationToken ct)
    {
        var url = Resolve(server,list.Id);
        var updates = new Dictionary<string,string>();
        var removed = new HashSet<string>();
        string? next=null;
        if (token != null)
        {
            try
            {
                next = await ReadDelta(url,token,updates,removed,ct);
            }
            catch(StaleToken) { token=null; }
            catch(HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed) { token=null; }
        }
        if (token == null)
        {
            updates.Clear();
            removed.Clear();
            // Capture the token before reading the inventory. Concurrent changes may be replayed, never skipped.
            var caps=await Props(url,"0",ct,D+"sync-token");
            next = caps.Elements(D+"response").Select(r=>Value(r,D+"sync-token")?.Value).FirstOrDefault(t=>!string.IsNullOrEmpty(t));
            var report=await RequestAsync("REPORT",url,"1",new XElement(C+"calendar-query",Prop(D+"getetag"),new XElement(C+"filter",new XElement(C+"comp-filter",new XAttribute("name","VCALENDAR"),new XElement(C+"comp-filter",new XAttribute("name","VTODO"))))),ct);
            foreach(var r in report.Elements(D+"response"))
            {
                var id=Resolve(url,(string?)r.Element(D+"href") ?? throw new InvalidDataException("缺少资源路径。")).AbsoluteUri;
                updates[id]=Value(r,D+"getetag")?.Value??throw new InvalidDataException("任务缺少版本。");
            }
            removed.UnionWith(knownETags.Keys.Except(updates.Keys));
        }

        var changed = new List<DavResource>();
        foreach (var batch in updates.Where(p=>!knownETags.TryGetValue(p.Key,out var e)||e!=p.Value).Select(p=>p.Key).Chunk(50))
        {
            var report=await RequestAsync("REPORT",url,"1",new XElement(C+"calendar-multiget",Prop(D+"getetag",C+"calendar-data"),batch.Select(id=>new XElement(D+"href",new Uri(id).PathAndQuery))),ct);
            var outstanding=batch.ToHashSet();
            foreach (var r in report.Elements(D+"response"))
            {
                var id=Resolve(url,(string?)r.Element(D+"href")??throw new InvalidDataException("批量下载缺少地址。")).AbsoluteUri;
                if(!outstanding.Remove(id)) throw new InvalidDataException("批量下载收到意外资源。");
                if(Status(r.Element(D+"status"),"404")) { removed.Add(id); continue; }
                var data=Value(r,C+"calendar-data")?.Value??throw new InvalidDataException("服务器没有返回日历数据。");
                var etag=Value(r,D+"getetag")?.Value??throw new InvalidDataException("没有版本。");
                if (data.Split('\n').Any(l=>l.TrimEnd('\r').Equals("BEGIN:VTODO",StringComparison.OrdinalIgnoreCase))) changed.Add(new DavResource(id,etag,data));
                else removed.Add(id);
            }
            if(outstanding.Count>0) throw new InvalidDataException("批量读取不完整，保留上次同步的游标。");
        }
        return new DavChanges(changed,removed.ToList(),next);
    }

    private async Task<string> ReadDelta(Uri collection, string cursor, Dictionary<string,string> changed,
        HashSet<string> removed, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        while (seen.Add(cursor))
        {
            var query = new XElement(D+"sync-collection", new XElement(D+"sync-token",cursor),
                new XElement(D+"sync-level",1), Prop(D+"getetag"));
            var page = await RequestAsync("REPORT",collection,"1",query,ct);
            cursor = page.Element(D+"sync-token")?.Value ?? throw new InvalidDataException("增量同步缺少游标。");
            bool more = false;
            foreach (var row in page.Elements(D+"response"))
            {
                var id = Resolve(collection,(string?)row.Element(D+"href") ?? throw new InvalidDataException("缺少资源路径。"));
                var status = row.Element(D+"status");
                if (id.AbsolutePath.TrimEnd('/') == collection.AbsolutePath.TrimEnd('/') && Status(status,"507"))
                {
                    more = true;
                    continue;
                }
                if (Status(status,"404"))
                {
                    changed.Remove(id.AbsoluteUri);
                    removed.Add(id.AbsoluteUri);
                }
                else
                {
                    removed.Remove(id.AbsoluteUri);
                    changed[id.AbsoluteUri] = Value(row,D+"getetag")?.Value ?? throw new InvalidDataException("增量同步未返回资源版本。");
                }
            }
            if (!more) return cursor;
        }
        throw new InvalidDataException("服务端返回重复的分页游标。");
    }

    internal async Task<string?> WriteAsync(string id,string ics,string? previousETag,CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put,Resolve(server,id));
        req.Headers.TryAddWithoutValidation(previousETag==null?"If-None-Match":"If-Match",previousETag??"*");
        req.Content=new StringContent(ics,Encoding.UTF8,"text/calendar");
        using var response=await http.SendAsync(req,ct);
        response.EnsureSuccessStatusCode();
        return response.Headers.ETag?.ToString();
    }
}
