using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

// Opt-in local operations notifications, never a player chat relay.
public sealed class DesktopDiscordNotifications : IDisposable
{
    private sealed record Settings(string ProtectedUrl,string[] Categories,bool WarningsOnly,
        DateTimeOffset WatermarkUtc,Guid[] SeenAtWatermark,string LastStatus,
        DateTimeOffset? LastAttemptUtc,DateTimeOffset CooldownUntilUtc);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http=new(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false})
        {Timeout=TimeSpan.FromSeconds(12)};
    private readonly ClusterService cluster;
    private readonly string root,file,lockFile;
    private readonly Func<Uri,string,CancellationToken,Task<HttpStatusCode>> send;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly CancellationTokenSource closing=new();
    private int started;
    private static readonly string[] AllowedCategories={"watchdog","lifecycle","schedule","backup"};
    public DesktopDiscordNotifications(ClusterService cluster,
        Func<Uri,string,CancellationToken,Task<HttpStatusCode>>? isolatedSend=null)
    {
        this.cluster=cluster;
        root=Path.GetFullPath(cluster.Store.Root);
        if(root.StartsWith(@"\\",StringComparison.Ordinal)||
           new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-NOTIFY-LOCAL-PROFILE");
        file=Path.Combine(root,"discord-notifications-v1.json");
        lockFile=Path.Combine(root,"discord-notifications-v1.lock");
        send=isolatedSend??(async(uri,payload,ct)=>{
            using var body=new StringContent(payload,Encoding.UTF8,"application/json");
            using var response=await Http.PostAsync(uri,body,ct);
            return response.StatusCode;
        });
    }
    internal static Uri ValidateUrl(string raw)
    {
        if(raw.Length>512||!Uri.TryCreate(raw,UriKind.Absolute,out var uri)||
            uri.Scheme!="https"||!uri.Host.Equals("discord.com",StringComparison.OrdinalIgnoreCase)||
            !uri.IsDefaultPort||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||
            !Regex.IsMatch(uri.AbsolutePath,@"\A/api/webhooks/[0-9]{15,25}/[A-Za-z0-9_-]{20,180}\z",RegexOptions.CultureInvariant))
            throw new InvalidDataException("UM-NOTIFY-DISCORD-URL");
        return uri;
    }
    private static void CheckPath(string path)
    {
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(path)!);dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-NOTIFY-LINKED-ROOT");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-NOTIFY-LINKED-FILE");
    }
    private FileStream Own()
    {
        CheckPath(lockFile);Directory.CreateDirectory(root);
        return new FileStream(lockFile,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    private Settings Read()
    {
        CheckPath(file);
        if(!File.Exists(file))return new("",AllowedCategories,true,DateTimeOffset.UtcNow,
            Array.Empty<Guid>(),"not-configured",null,default);
        if(new FileInfo(file).Length is <2 or >128_000)throw new InvalidDataException("UM-NOTIFY-SETTINGS");
        var item=JsonSerializer.Deserialize<Settings>(File.ReadAllText(file),Json)
            ??throw new InvalidDataException("UM-NOTIFY-SETTINGS");
        if(item.ProtectedUrl==null||item.ProtectedUrl.Length>4096||item.Categories==null||
           item.Categories.Length>4||item.Categories.Any(c=>!AllowedCategories.Contains(c))||
           item.Categories.Distinct().Count()!=item.Categories.Length||
           item.SeenAtWatermark==null||item.SeenAtWatermark.Length>500||
           item.LastStatus==null||item.LastStatus.Length>80)
            throw new InvalidDataException("UM-NOTIFY-SETTINGS");
        return item;
    }
    private void Write(Settings item)
    {
        CheckPath(file);string tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {JsonSerializer.Serialize(output,item,Json);output.Flush(true);}
            File.Move(tmp,file,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    // Notification failure must never turn an ASA success into failure.
    internal static void Record(ClusterService cluster,Guid? map,string category,bool warning)
    {
        if(category is not ("lifecycle" or "schedule" or "backup"))return;
        try
        {
            var path=Path.Combine(cluster.Store.Root,"notification-events","events.jsonl");CheckPath(path);
            new EventJournal(Path.GetDirectoryName(path)!).Append(cluster.Config.LocalHostName,map,
                warning?"Warning":"Information",category+(warning?"-needs-review":"-completed"));
        }
        catch{}
    }
    private static string? Category(string code)=>code switch{
        "lifecycle-completed" or "lifecycle-needs-review"=>"lifecycle",
        "schedule-completed" or "schedule-needs-review"=>"schedule",
        "backup-completed" or "backup-needs-review"=>"backup",
        "process-observation-changed" or "watchdog-persistent-operator-review" or
        "watchdog-attention-required" or "watchdog-observed-ok" or "rcon-observation-changed"=>"watchdog",
        _=>null};
    private static IEnumerable<MonitorEvent> Tail(string path)
    {
        CheckPath(path);if(!File.Exists(path))return Array.Empty<MonitorEvent>();
        var list=new List<MonitorEvent>();
        using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        long size=f.Length,remaining=Math.Min(size,1_048_576);
        f.Position=size-remaining;
        byte[] bytes=new byte[(int)remaining];f.ReadExactly(bytes);
        var lines=Encoding.UTF8.GetString(bytes).Split('\n');
        for(int i=size>remaining?1:0;i<lines.Length-1;i++)
        {
            if(lines[i].Length>2048)continue;
            try
            {
                var e=JsonSerializer.Deserialize<MonitorEvent>(lines[i]);
                if(e!=null&&e.Id!=Guid.Empty&&e.Code!=null&&Category(e.Code)!=null&&
                   e.Severity is "Information" or "Warning" or "Error")list.Add(e);
            }
            catch(JsonException){}
        }
        return list;
    }
    private static string Safe(string? raw)=>new string((raw??"").Where(c=>!char.IsControl(c)).Take(96).ToArray())
        .Replace("@","＠",StringComparison.Ordinal).Replace((char)96,'\'');
    private string Payload(string code,Guid? map,string severity)
    {
        var mapName=cluster.Config.LocalMaps.FirstOrDefault(x=>x.Id==map)?.Name;
        string content="Obsidian Command | "+Safe(cluster.Config.LocalHostName)+" | "+
            (map.HasValue?Safe(mapName??"Helyi map")+" | ":"")+Safe(severity)+" | "+code;
        return JsonSerializer.Serialize(new{content,allowed_mentions=new{parse=Array.Empty<string>()}});
    }
    private async Task<Settings> Deliver(Settings claim,string payload,CancellationToken ct)
    {
        try
        {
            var uri=ValidateUrl(SecureStore.Unprotect(claim.ProtectedUrl));
            var status=await send(uri,payload,ct);
            if((int)status>=200&&(int)status<300)return claim with{LastStatus="delivered"};
            if(status==HttpStatusCode.TooManyRequests)
                return claim with{LastStatus="rate-limited-no-replay",CooldownUntilUtc=DateTimeOffset.UtcNow.AddMinutes(2)};
            return claim with{LastStatus="http-rejected-no-replay",CooldownUntilUtc=DateTimeOffset.UtcNow.AddMinutes(10)};
        }
        catch{return claim with{LastStatus="delivery-unknown-no-replay",CooldownUntilUtc=DateTimeOffset.UtcNow.AddMinutes(2)};}
    }
    internal async Task TickOnceAsync(CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct))return;
        try
        {
            if(!File.Exists(file))return;
            using var held=Own();
            var current=Read();var now=DateTimeOffset.UtcNow;
            if(current.ProtectedUrl.Length==0||current.CooldownUntilUtc>now)return;
            var items=Tail(Path.Combine(root,"notification-events","events.jsonl"))
                .Concat(Tail(Path.Combine(root,"supervision","events.jsonl")))
                .Where(e=>e.Host==cluster.Config.LocalHostName&&
                    (e.Map==null||cluster.Config.LocalMaps.Any(m=>m.Id==e.Map))&&
                    e.Utc>=now.AddMinutes(-10)&&e.Utc<=now&&current.Categories.Contains(Category(e.Code))&&
                    (!current.WarningsOnly||e.Severity!="Information")&&
                    (e.Utc>current.WatermarkUtc||
                       e.Utc==current.WatermarkUtc&&!current.SeenAtWatermark.Contains(e.Id)))
                .DistinctBy(e=>e.Id).OrderBy(e=>e.Utc).ThenBy(e=>e.Id).Take(5).ToArray();
            foreach(var e in items)
            {
                ct.ThrowIfCancellationRequested();
                if(current.WatermarkUtc==e.Utc&&current.SeenAtWatermark.Length>=500)
                    break; // bounded cursor; never evict a claim and replay it
                var seen=current.WatermarkUtc==e.Utc?
                    current.SeenAtWatermark.Append(e.Id).Distinct().ToArray():new[]{e.Id};
                current=current with{WatermarkUtc=e.Utc,SeenAtWatermark=seen,
                    LastStatus="claimed-no-replay",LastAttemptUtc=DateTimeOffset.UtcNow};
                Write(current);
                current=await Deliver(current,Payload(e.Code,e.Map,e.Severity),ct);Write(current);
                if(current.LastStatus!="delivered")break;
            }
        }
        finally{gate.Release();}
    }
    private static object Status(Settings s)=>new{configured=s.ProtectedUrl.Length>0,
        categories=s.Categories,warningsOnly=s.WarningsOnly,lastStatus=s.LastStatus,
        lastAttemptUtc=s.LastAttemptUtc,cooldownUntilUtc=s.CooldownUntilUtc};
    private static string Ok(string? id,object data)=>JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Fail(null,"UM-NOTIFY-REQUEST");
            using var doc=JsonDocument.Parse(raw);var a=doc.RootElement;
            id=a.GetProperty("id").GetGuid().ToString("D");var method=a.GetProperty("method").GetString();
            if(method is not ("notifications.status" or "notifications.save" or "notifications.clear" or "notifications.test"))
                return Fail(id,"UM-METHOD-DENIED");
            if(!await gate.WaitAsync(TimeSpan.FromSeconds(15)))return Fail(id,"UM-NOTIFY-BUSY");
            try
            {
                using var held=Own();var current=Read();
                if(method=="notifications.status")return Ok(id,Status(current));
                if(!a.TryGetProperty("confirm",out var approval)||approval.ValueKind!=JsonValueKind.True)
                    return Fail(id,"UM-NOTIFY-CONFIRM");
                if(method=="notifications.clear")
                {
                    current=current with{ProtectedUrl="",LastStatus="not-configured",
                        WatermarkUtc=DateTimeOffset.UtcNow,SeenAtWatermark=Array.Empty<Guid>(),CooldownUntilUtc=default};
                    Write(current);return Ok(id,Status(current));
                }
                if(method=="notifications.save")
                {
                    var categories=a.GetProperty("categories").EnumerateArray().Select(x=>x.GetString()??"").ToArray();
                    if(categories.Length is <1 or >4||categories.Distinct().Count()!=categories.Length||
                       categories.Any(c=>!AllowedCategories.Contains(c)))return Fail(id,"UM-NOTIFY-CATEGORIES");
                    string url=a.TryGetProperty("credential",out var value)?value.GetString()??"":"";
                    string protectedUrl=current.ProtectedUrl;
                    if(url.Length>0)protectedUrl=SecureStore.Protect(ValidateUrl(url).AbsoluteUri);
                    if(protectedUrl.Length==0)return Fail(id,"UM-NOTIFY-DISCORD-URL");
                    current=current with{ProtectedUrl=protectedUrl,Categories=categories,
                        WarningsOnly=a.GetProperty("warningsOnly").GetBoolean(),
                        WatermarkUtc=DateTimeOffset.UtcNow,SeenAtWatermark=Array.Empty<Guid>(),
                        LastStatus="configured-not-tested",CooldownUntilUtc=default};
                    Write(current);return Ok(id,Status(current));
                }
                if(current.ProtectedUrl.Length==0)return Fail(id,"UM-NOTIFY-DISCORD-URL");
                if(current.CooldownUntilUtc>DateTimeOffset.UtcNow||
                   current.LastAttemptUtc>DateTimeOffset.UtcNow.AddSeconds(-10))
                    return Fail(id,"UM-NOTIFY-COOLDOWN");
                current=current with{LastStatus="claimed-no-replay",LastAttemptUtc=DateTimeOffset.UtcNow};
                Write(current);current=await Deliver(current,Payload("test-notification",null,"Information"),closing.Token);
                Write(current);return Ok(id,Status(current));
            }
            finally{gate.Release();}
        }
        catch{return Fail(id,"UM-NOTIFY-FAILED");}
    }
    public void Start()
    {
        if(Interlocked.Exchange(ref started,1)!=0)return;
        _=Task.Run(async()=>{
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
            try{while(await timer.WaitForNextTickAsync(closing.Token))
                {try{await TickOnceAsync(closing.Token);}catch{}}
            }catch(OperationCanceledException){}
        });
    }
    public void Dispose()=>closing.Cancel();
}
