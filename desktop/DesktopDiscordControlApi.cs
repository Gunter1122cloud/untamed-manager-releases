using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

// A separate, disabled-by-default bot endpoint. TLS, pinned peer IP, hashed
// per-host bearer, trusted bot's Discord role assertion, actor-bound approval,
// durable no-replay dispatch claim. Never forwards arbitrary bridge methods.
internal sealed class DesktopDiscordControlApi : IDisposable
{
    internal sealed record Settings(string BindIp,int Port,string BotIp,string CertificateThumbprint,
        string TokenSha256,string GuildId,string[] ChannelIds,string[] RoleIds,string? ServiceUserId=null);
    private sealed record Approval(Guid Token,string Actor,string Channel,string Action,Guid[] Maps,
        bool? UseApi,string Message,string Point,int Delay,string[] Profiles,DateTimeOffset Expires,
        string ApplyRoute,Guid? NativeToken);
    private sealed record Audit(Guid RequestId,DateTimeOffset Utc,string Actor,string Action,Guid[] Maps,
        string State,string? StatusRoute,Guid? NativeJobId);
    private sealed record CountSample(int Pid,DateTimeOffset StartedUtc,int? Count,DateTimeOffset ObservedUtc);
    private sealed record CpuSample(int Pid,DateTimeOffset StartedUtc,double Seconds,DateTimeOffset ObservedUtc);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly Regex Snowflake=new(@"\A[0-9]{15,25}\z",RegexOptions.CultureInvariant);
    private static readonly Regex PlayerRow=new(@"\A\s*[0-9]+\.\s+.+,\s*[A-Za-z0-9]{16,64}\s*\z",RegexOptions.CultureInvariant);
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim hostGate;
    private readonly Func<bool> frozen,enterLane;
    private readonly Action leaveLane;
    private readonly Func<string,string,Task<string>> native;
    private readonly string root,settingsPath,auditFolder;
    private readonly CancellationTokenSource closing=new();
    private readonly Dictionary<Guid,Approval> approvals=new();
    private readonly Dictionary<Guid,CountSample> players=new();
    private readonly Dictionary<Guid,CpuSample> cpu=new();
    private Settings? settings;
    private X509Certificate2? certificate;
    private TcpListener? listener;
    private readonly SemaphoreSlim connections=new(4,4);
    internal DesktopDiscordControlApi(ClusterService cluster,SemaphoreSlim hostGate,Func<bool> frozen,
        Func<bool> enterLane,Action leaveLane,Func<string,string,Task<string>> native)
    {
        this.cluster=cluster;this.hostGate=hostGate;this.frozen=frozen;
        this.enterLane=enterLane;this.leaveLane=leaveLane;this.native=native;
        root=Path.Combine(cluster.Store.Root,"discord-control-v1");
        settingsPath=Path.Combine(root,"settings.json");
        auditFolder=Path.Combine(root,"audit");
    }
    internal static bool PrivateIp(IPAddress address)
    {
        if(address.AddressFamily!=AddressFamily.InterNetwork)return false;
        var b=address.GetAddressBytes();
        return b[0]==10||b[0]==192&&b[1]==168||b[0]==172&&b[1]>=16&&b[1]<=31||IPAddress.IsLoopback(address);
    }
    internal static bool ValidSettings(Settings s)
    {
        return IPAddress.TryParse(s.BindIp,out var bind)&&PrivateIp(bind)&&
            IPAddress.TryParse(s.BotIp,out var bot)&&PrivateIp(bot)&&
            s.Port is >=1024 and <=65535&&s.CertificateThumbprint is {Length:40}&&s.CertificateThumbprint.All(Uri.IsHexDigit)&&
            s.TokenSha256 is {Length:64}&&s.TokenSha256.All(Uri.IsHexDigit)&&
            Snowflake.IsMatch(s.GuildId??"")&&(s.ServiceUserId==null||Snowflake.IsMatch(s.ServiceUserId))&&s.ChannelIds is {Length:>=1 and <=20}&&s.RoleIds is {Length:>=1 and <=20}&&
            s.ChannelIds.All(x=>Snowflake.IsMatch(x??""))&&s.RoleIds.All(x=>Snowflake.IsMatch(x??""));
    }
    private static void SafePath(string path)
    {
        for(var d=new DirectoryInfo(Path.GetDirectoryName(path)!);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("UM-DC-LINKED-PATH");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("UM-DC-LINKED-PATH");
    }
    private static string Field(JsonElement a,string key,int max=256)
    {
        string value=a.GetProperty(key).GetString()??"";
        if(value.Length>max||value.Any(char.IsControl))throw new InvalidDataException("UM-DC-FIELD");
        return value;
    }
    internal static bool Authorize(Settings s,JsonElement a,DateTimeOffset now)
    {
        try
        {
            var credential=Field(a,"credential",64);
            if(credential.Length!=64||!credential.All(Uri.IsHexDigit))return false;
            if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(credential)),Convert.FromHexString(s.TokenSha256)))return false;
            if(Field(a,"guildId",25)!=s.GuildId||!s.ChannelIds.Contains(Field(a,"channelId",25))||
                !Snowflake.IsMatch(Field(a,"userId",25)))return false;
            if(s.ServiceUserId!=null&&Field(a,"userId",25)==s.ServiceUserId)
                return Field(a,"kind",32) is "status" or "players"&&
                    Math.Abs((now-a.GetProperty("sentUtc").GetDateTimeOffset()).TotalSeconds)<=60;
            var roles=a.GetProperty("roleIds");
            if(roles.ValueKind!=JsonValueKind.Array||roles.GetArrayLength()>100||
                !roles.EnumerateArray().Any(x=>x.ValueKind==JsonValueKind.String&&s.RoleIds.Contains(x.GetString())))return false;
            return Math.Abs((now-a.GetProperty("sentUtc").GetDateTimeOffset()).TotalSeconds)<=60;
        }
        catch{return false;}
    }
    internal static int? ParsePlayerCount(string response)
    {
        if(response.Trim().Equals("No Players Connected",StringComparison.OrdinalIgnoreCase)||
            response.Trim().Equals("No Players Connected.",StringComparison.OrdinalIgnoreCase))return 0;
        var rows=response.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        if(rows.Length==0||rows.Length>1000||!rows.All(x=>PlayerRow.IsMatch(x)))return null;
        return rows.Length;
    }
    internal void Start()
    {
        try
        {
            SafePath(settingsPath);
            if(!File.Exists(settingsPath))return;
            if(new FileInfo(settingsPath).Length is <2 or >16384)throw new InvalidDataException();
            var cfg=JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath),Json);
            if(cfg==null||!ValidSettings(cfg))throw new InvalidDataException();
            using var store=new X509Store(StoreName.My,StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var cert=store.Certificates.Find(X509FindType.FindByThumbprint,cfg.CertificateThumbprint,false)
                .OfType<X509Certificate2>().Single();
            if(!cert.HasPrivateKey||DateTime.UtcNow<cert.NotBefore.ToUniversalTime()||DateTime.UtcNow>=cert.NotAfter.ToUniversalTime())
                throw new InvalidDataException();
            Directory.CreateDirectory(auditFolder);SafePath(Path.Combine(auditFolder,"probe"));
            certificate=cert;settings=cfg;
            listener=new TcpListener(IPAddress.Parse(cfg.BindIp),cfg.Port);listener.Start(8);
            _=Task.Run(AcceptLoop);
        }
        catch{listener?.Stop();listener=null;settings=null;certificate?.Dispose();certificate=null;}
    }
    private async Task AcceptLoop()
    {
        try
        {
            while(!closing.IsCancellationRequested)
            {
                var client=await listener!.AcceptTcpClientAsync(closing.Token);
                if(client.Client.RemoteEndPoint is not IPEndPoint peer||
                   !peer.Address.Equals(IPAddress.Parse(settings!.BotIp))||!connections.Wait(0))
                {client.Dispose();continue;}
                _=Task.Run(async()=>{try{await Serve(client);}catch{}finally{client.Dispose();connections.Release();}});
            }
        }
        catch(OperationCanceledException){}
        catch(ObjectDisposedException){}
        catch(SocketException){}
    }
    private async Task Serve(TcpClient client)
    {
        using var tls=new SslStream(client.GetStream(),false);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{
            ServerCertificate=certificate,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
            ClientCertificateRequired=false},timeout.Token);
        // Binary framing: unsigned 32-bit big-endian length, UTF-8 JSON.
        byte[] header=new byte[4];await tls.ReadExactlyAsync(header,timeout.Token);
        uint n=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        if(n is <2 or >16384)return;
        byte[] bytes=new byte[(int)n];await tls.ReadExactlyAsync(bytes,timeout.Token);
        using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=12});
        if(!Authorize(settings!,doc.RootElement,DateTimeOffset.UtcNow))return;
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        string reply=await Handle(doc.RootElement,settings!);
        if(Encoding.UTF8.GetByteCount(reply)>1_048_576)reply=Error("UM-DC-RESPONSE-LIMIT");
        byte[] output=Encoding.UTF8.GetBytes(reply);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header,(uint)output.Length);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await tls.WriteAsync(header,timeout.Token);await tls.WriteAsync(output,timeout.Token);
        await tls.FlushAsync(timeout.Token);
    }
    private static string Ok(object data)=>JsonSerializer.Serialize(new{ok=true,data},Json);
    private static string Error(string error)=>JsonSerializer.Serialize(new{ok=false,error},Json);
    private LocalMap Owned(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id)??
        throw new UnauthorizedAccessException("UM-DC-LOCAL-MAP-ONLY");
    private static string Profile(LocalMap map)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(map)));
    private static string Message(string method,object fields)
    {
        var payload=new Dictionary<string,object?>{["id"]=Guid.NewGuid(),["method"]=method};
        foreach(var f in JsonSerializer.SerializeToElement(fields,Json).EnumerateObject())payload[f.Name]=f.Value.Clone();
        return JsonSerializer.Serialize(payload,Json);
    }
    private async Task<JsonElement> Invoke(string route,object fields)
    {
        using var doc=JsonDocument.Parse(await native(route,Message(route,fields)));
        if(!doc.RootElement.GetProperty("ok").GetBoolean())
        {
            string? code=doc.RootElement.GetProperty("error").GetString();
            throw new InvalidOperationException(code!=null&&Regex.IsMatch(code,@"\AUM-[A-Z0-9_-]{1,150}\z")?code:"UM-DC-NATIVE-REJECTED");
        }
        return doc.RootElement.GetProperty("data").Clone();
    }
    private static bool Yes(JsonElement a,string k)=>a.TryGetProperty(k,out var t)&&t.ValueKind==JsonValueKind.True;
    private static JsonElement? Value(JsonElement a,string k)=>a.TryGetProperty(k,out var t)?t.Clone():null;
    private static object Projection(JsonElement a,params string[] names)=>names.Where(n=>a.TryGetProperty(n,out _))
        .ToDictionary(n=>n,n=>a.GetProperty(n).Clone());
    private string AuditPath(Guid id)=>Path.Combine(auditFolder,id.ToString("N")+".json");
    private void Claim(Audit row)
    {
        string path=AuditPath(row.RequestId);SafePath(path);Directory.CreateDirectory(auditFolder);SafePath(path);
        using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);
        JsonSerializer.Serialize(f,row,Json);f.Flush(true);
    }
    private void Finish(Audit row)
    {
        string path=AuditPath(row.RequestId),tmp=path+"."+Guid.NewGuid().ToString("N")+".next";
        SafePath(path);
        using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        {JsonSerializer.Serialize(f,row,Json);f.Flush(true);}
        File.Move(tmp,path,true);
    }
    private Audit ReadAudit(Guid id)
    {
        string path=AuditPath(id);SafePath(path);
        if(new FileInfo(path).Length is <2 or >16384)throw new InvalidDataException("UM-DC-AUDIT");
        var row=JsonSerializer.Deserialize<Audit>(File.ReadAllText(path),Json)??throw new InvalidDataException("UM-DC-AUDIT");
        if(row.RequestId!=id||row.Maps==null||row.Maps.Length is <1 or >25)throw new InvalidDataException("UM-DC-AUDIT");
        return row;
    }
    private async Task<object> Status()
    {
        var result=new List<object>();int offset=0;string? key=null;
        for(int page=0;page<100;page++)
        {
            var data=await Invoke("operations.snapshot",key==null?(object)new{offset,limit=50}:new{offset,limit=50,snapshotKey=key});
            key=data.GetProperty("snapshotKey").GetString();
            foreach(var row in data.GetProperty("maps").EnumerateArray())
            {
                var map=Owned(row.GetProperty("mapId").GetGuid());
                int? pid=Value(row,"pid") is {ValueKind:JsonValueKind.Number} p?p.GetInt32():null;
                DateTimeOffset? start=Value(row,"startedUtc") is {ValueKind:JsonValueKind.String} st?st.GetDateTimeOffset():null;
                int? playerCount=null;DateTimeOffset? playerObserved=null;double? cpuPercent=null;long? freeBytes=null,totalBytes=null;
                var now=DateTimeOffset.UtcNow;
                if(pid.HasValue&&start.HasValue)
                {
                    if(players.TryGetValue(map.Id,out var count)&&count.Pid==pid&&count.StartedUtc==start&&now-count.ObservedUtc<=TimeSpan.FromSeconds(65))
                    {playerCount=count.Count;playerObserved=count.ObservedUtc;}
                    try
                    {
                        using var process=Process.GetProcessById(pid.Value);
                        if(!process.HasExited&&new DateTimeOffset(process.StartTime.ToUniversalTime())==start)
                        {
                            double sec=process.TotalProcessorTime.TotalSeconds;
                            if(cpu.TryGetValue(map.Id,out var old)&&old.Pid==pid&&old.StartedUtc==start&&
                               (now-old.ObservedUtc).TotalSeconds is >=1 and <=120)
                                cpuPercent=Math.Clamp(100*(sec-old.Seconds)/(now-old.ObservedUtc).TotalSeconds/Environment.ProcessorCount,0,100);
                            cpu[map.Id]=new(pid.Value,start.Value,sec,now);
                        }
                    }catch{}
                }
                try{var drive=new DriveInfo(Path.GetPathRoot(map.InstallationPath)!);if(drive.DriveType==DriveType.Fixed){freeBytes=drive.AvailableFreeSpace;totalBytes=drive.TotalSize;}}catch{}
                result.Add(new{mapId=map.Id,mapName=map.Name,running=Yes(row,"running"),confirmedStopped=Yes(row,"confirmedStopped"),
                    pid,uptimeSeconds=Value(row,"uptimeSeconds"),memoryMiB=Value(row,"memoryMiB"),cpuPercent,
                    freeBytes,totalBytes,playerCount,playerObservedUtc=playerObserved,storeV1=map.HistoricalStoreProtected,
                    useApi=map.StartWithAsaApi,maintenance=map.Maintenance});
            }
            if(Value(data,"nextOffset") is not {ValueKind:JsonValueKind.Number} next)
                return new{hostId=cluster.Config.LocalHostId,hostName=cluster.Config.LocalHostName,observedUtc=DateTimeOffset.UtcNow,maps=result};
            offset=next.GetInt32();
        }
        throw new InvalidOperationException("UM-DC-SNAPSHOT-LIMIT");
    }
    private async Task<object> Players(Guid id)
    {
        var map=Owned(id);
        var status=await Invoke("operations.map",new{mapId=id});
        var row=status.GetProperty("maps").EnumerateArray().Single();
        var now=DateTimeOffset.UtcNow;
        if(!Yes(row,"running"))return new{mapId=id,playerCount=(int?)null,response="",observedUtc=now};
        int pid=row.GetProperty("pid").GetInt32();var start=row.GetProperty("startedUtc").GetDateTimeOffset();
        try
        {
            var data=await Invoke(map.HistoricalStoreProtected?"historical.rconPlayers":"rcon.listPlayers",new{mapId=id});
            if(data.GetProperty("mapId").GetGuid()!=id||!Yes(data,"readOnly"))throw new InvalidDataException();
            string response=data.GetProperty("response").GetString()??"";
            int? count=ParsePlayerCount(response);now=DateTimeOffset.UtcNow;
            players[id]=new(pid,start,count,now);
            return new{mapId=id,playerCount=count,response,observedUtc=now};
        }
        catch{players.Remove(id);return new{mapId=id,playerCount=(int?)null,response="",observedUtc=now};}
    }
    private async Task<Approval> Preview(JsonElement request,string actor,string channel)
    {
        string action=Field(request,"action",32);
        var ids=request.GetProperty("mapIds").EnumerateArray().Select(x=>x.GetGuid()).ToArray();
        if(ids.Length is <1 or >25||ids.Distinct().Count()!=ids.Length)throw new InvalidDataException("UM-DC-SCOPE");
        var maps=ids.Select(Owned).ToArray();
        if(maps.Any(m=>m.Maintenance||m.ObservedReadOnlyImported))throw new InvalidOperationException("UM-DC-MAP-INELIGIBLE");
        bool? mode=Value(request,"useApi") is {ValueKind:JsonValueKind.True or JsonValueKind.False} choice?choice.GetBoolean():null;
        string message=request.TryGetProperty("message",out _)?Field(request,"message",240):"";
        string point=request.TryGetProperty("point",out _)?Field(request,"point",40):"";
        int delay=request.TryGetProperty("delaySeconds",out var d)?d.GetInt32():15;
        Guid? nativeToken=null;string applyRoute;JsonElement preview;
        if(ids.Length>1||action=="batch")
        {
            if(mode.HasValue||action is not ("start" or "stop" or "restart")||delay is <0 or >300)
                throw new InvalidDataException("UM-DC-BATCH-SCOPE");
            preview=await Invoke("operations.lifecycleBatchPreview",new{mapIds=ids,action,delaySeconds=delay});
            if(!preview.GetProperty("maps").EnumerateArray().Select(x=>x.GetProperty("mapId").GetGuid()).SequenceEqual(ids))
                throw new InvalidDataException("UM-DC-BATCH-PREVIEW-SCOPE");
            applyRoute="operations.lifecycleBatchApply";
        }
        else
        {
            var m=maps[0];Guid mapId=m.Id;bool historical=m.HistoricalStoreProtected;
            string prefix=action switch{
                "start"=>historical?"historical.activeStart":"servers.freshStart",
                "stop"=>historical?"historical.gracefulStop":"servers.gracefulStop",
                "restart"=>historical?"historical.restart":"servers.restart",
                "save"=>historical?"historical.saveWorld":"rcon.saveWorld",
                "wipe" or "rcon"=>historical?"historical.admin":"rcon.admin",
                "backup"=>"backup","restore"=>"backup.restore","update"=>"steam.stopped",
                _=>throw new InvalidDataException("UM-DC-ACTION")};
            if(mode.HasValue&&action is not ("start" or "restart"))throw new InvalidDataException("UM-DC-MODE");
            if(action=="restart"&&mode.HasValue&&mode!=m.StartWithAsaApi)
                throw new InvalidOperationException("UM-DC-MODE-SWITCH-REQUIRES-STOP-THEN-START");
            if(action=="start"&&mode.HasValue&&mode!=m.StartWithAsaApi)
            {
                string changing=historical?"historical.loaderMode":"servers.apiMode";
                preview=await Invoke(changing+"Preview",new{mapId,useApi=mode.Value});
                applyRoute=changing+"Apply";
            }
            else if(action=="save"&&!historical)
            {
                preview=await Invoke("rcon.status",new{mapId});
                if(!Yes(preview,"permitted"))throw new InvalidOperationException("UM-DC-RCON-NOT-READY");
                applyRoute="rcon.saveWorld";
            }
            else if(action is "wipe" or "rcon")
            {
                preview=await Invoke(prefix+"Preview",new{mapId,action=action=="wipe"?"wildWipe":"custom",message=action=="wipe"?"":message});
                applyRoute=prefix+"Send";
            }
            else
            {
                string pre=action=="backup"?"backup.preview":prefix+"Preview";
                preview=await Invoke(pre,action=="restore"?(object)new{mapId,point}:new{mapId});
                applyRoute=action switch{"backup"=>"backup.create","restore"=>"backup.restore","update"=>"steam.stoppedApply",_=>prefix+"Apply"};
            }
            if(preview.TryGetProperty("mapId",out var owner)&&owner.GetGuid()!=mapId)throw new InvalidDataException("UM-DC-NATIVE-SCOPE");
        }
        if(preview.TryGetProperty("token",out var t))nativeToken=t.GetGuid();
        if(!nativeToken.HasValue&&applyRoute!="rcon.saveWorld")throw new InvalidDataException("UM-DC-NATIVE-TOKEN");
        return new(Guid.NewGuid(),actor,channel,action,ids,mode,message,point,delay,maps.Select(Profile).ToArray(),
            DateTimeOffset.UtcNow.AddSeconds(90),applyRoute,nativeToken);
    }
    private static string? StatusRoute(string apply)=>apply switch{
        "operations.lifecycleBatchApply"=>"operations.lifecycleBatchCurrent",
        "historical.restartApply"=>"historical.restartStatus",
        "servers.restartApply"=>"servers.restartStatus",
        "historical.gracefulStopApply"=>"historical.gracefulStopStatus",
        "servers.gracefulStopApply"=>"servers.gracefulStopStatus",
        "historical.saveWorldApply"=>"historical.saveWorldStatus",
        "steam.stoppedApply"=>"steam.stoppedStatus",_=>null};
    private async Task<string> Apply(JsonElement request,Guid requestId,string actor,string channel)
    {
        if(!Yes(request,"confirm")||!approvals.TryGetValue(request.GetProperty("token").GetGuid(),out var p)||
            p.Actor!=actor||p.Channel!=channel||p.Expires<DateTimeOffset.UtcNow)
            return Error("UM-DC-APPROVAL");
        approvals.Remove(p.Token);
        if(p.Maps.Select(Owned).Select(Profile).Where((hash,i)=>hash!=p.Profiles[i]).Any())
            return Error("UM-DC-PROFILE-CHANGED");
        if(!enterLane())return Error("UM-DC-HOST-BUSY");
        try
        {
            var row=new Audit(requestId,DateTimeOffset.UtcNow,actor,p.Action,p.Maps,"claimed-no-replay",null,null);
            Claim(row); // durable before any native dispatch
            try
            {
                if(frozen()||closing.IsCancellationRequested)throw new InvalidOperationException("UM-DC-HANDOFF");
                Guid mapId=p.Maps[0];var map=Owned(mapId);
                string apply=p.ApplyRoute;Guid? nativeToken=p.NativeToken;
                if(apply is "historical.loaderModeApply" or "servers.apiModeApply")
                {
                    var mode=await Invoke(apply,new{token=nativeToken,confirm=true});
                    if(mode.GetProperty("mapId").GetGuid()!=mapId||mode.GetProperty("useApi").GetBoolean()!=p.UseApi)
                        throw new InvalidDataException("UM-DC-MODE-RECEIPT");
                    apply=map.HistoricalStoreProtected?"historical.activeStartApply":"servers.freshStartApply";
                    string pre=map.HistoricalStoreProtected?"historical.activeStartPreview":"servers.freshStartPreview";
                    var started=await Invoke(pre,new{mapId});
                    if(started.GetProperty("mapId").GetGuid()!=mapId||
                        started.GetProperty("launchMode").GetString()!=(p.UseApi==true?"asaApiLoader":"directAsa"))
                        throw new InvalidDataException("UM-DC-MODE-PREFLIGHT");
                    nativeToken=started.GetProperty("token").GetGuid();
                }
                string typed=p.Action=="wipe"?(map.HistoricalStoreProtected?"STORE-V1 DINÓTÖRLÉS":"DINÓTÖRLÉS"):
                    map.HistoricalStoreProtected?"STORE-V1 ADMIN":"ADMIN";
                object fields=apply=="rcon.saveWorld"?new{mapId,confirm=true}:
                    (object)new{token=nativeToken,confirm=true,typedApproval=typed,mapName=map.Name};
                var response=await Invoke(apply,fields);
                if(response.TryGetProperty("mapId",out var returned)&&returned.GetGuid()!=mapId)
                    throw new InvalidDataException("UM-DC-RECEIPT-SCOPE");
                Guid? job=Value(response,"jobId") is {ValueKind:JsonValueKind.String} j?j.GetGuid():null;
                row=row with{State="native-receipt",NativeJobId=job,StatusRoute=StatusRoute(apply)};
                Finish(row);
                return Ok(new{requestId,action=p.Action,receipt=Projection(response,"mapId","jobId","state","startRequested",
                    "started","pid","launchMode","commandSent","rconCommandDispatched","backupVerified","point",
                    "restored","bytesRestoredAndHashesVerified","evidence","response"),
                    gameEffectVerified=false});
            }
            catch(Exception ex)
            {
                Finish(row with{State="uncertain-no-replay"});
                return Error(ex.Message.StartsWith("UM-DC-",StringComparison.Ordinal)?"UM-DC-UNCERTAIN-NO-REPLAY":"UM-DC-NATIVE-UNCERTAIN-NO-REPLAY");
            }
        }
        finally{leaveLane();}
    }
    internal async Task<string> Handle(JsonElement a,Settings cfg)
    {
        if(!Authorize(cfg,a,DateTimeOffset.UtcNow))return Error("UM-DC-AUTH");
        if(!await hostGate.WaitAsync(TimeSpan.FromSeconds(2)))return Error("UM-DC-HOST-BUSY");
        try
        {
            if(frozen()||closing.IsCancellationRequested)return Error("UM-DC-HANDOFF");
            if(!Authorize(cfg,a,DateTimeOffset.UtcNow))return Error("UM-DC-AUTH");
            Guid id=a.GetProperty("requestId").GetGuid();if(id==Guid.Empty)return Error("UM-DC-ID");
            string actor=Field(a,"userId",25),channel=Field(a,"channelId",25);
            string kind=Field(a,"kind",32);
            if(kind=="status")return Ok(await Status());
            if(kind=="players")return Ok(await Players(a.GetProperty("mapId").GetGuid()));
            if(kind=="backupHistory")
            {
                Guid mapId=a.GetProperty("mapId").GetGuid();_=Owned(mapId);
                var list=await Invoke("backup.history",new{mapId});
                return Ok(Projection(list,"mapId","points","recoveryRequired"));
            }
            if(kind=="job")
            {
                var row=ReadAudit(a.GetProperty("operationId").GetGuid());
                if(row.NativeJobId==null||row.StatusRoute==null)return Ok(row);
                if(row.StatusRoute is not ("operations.lifecycleBatchCurrent" or "historical.restartStatus" or
                    "servers.restartStatus" or "historical.gracefulStopStatus" or "servers.gracefulStopStatus" or
                    "historical.saveWorldStatus" or "steam.stoppedStatus"))return Error("UM-DC-JOB-ROUTE");
                var status=await Invoke(row.StatusRoute,new{jobId=row.NativeJobId});
                if(row.StatusRoute=="operations.lifecycleBatchCurrent")
                {
                    var receipt=status.GetProperty("receipt");
                    if(receipt.ValueKind!=JsonValueKind.Object||receipt.GetProperty("jobId").GetGuid()!=row.NativeJobId)
                        return Error("UM-DC-JOB-NOT-CURRENT");
                    status=receipt;
                }
                if(status.GetProperty("jobId").GetGuid()!=row.NativeJobId)return Error("UM-DC-JOB-ID");
                return Ok(new{operationId=row.RequestId,job=Projection(status,"jobId","state","stage","evidence","confirmedStopped","newPid","targets","changedUtc")});
            }
            if(kind=="preview")
            {
                foreach(var old in approvals.Where(x=>x.Value.Expires<DateTimeOffset.UtcNow).Select(x=>x.Key).ToArray())approvals.Remove(old);
                if(approvals.Count>=32)return Error("UM-DC-PREVIEW-LIMIT");
                var plan=await Preview(a,actor,channel);approvals.Add(plan.Token,plan);
                return Ok(new{token=plan.Token,action=plan.Action,maps=plan.Maps.Select(id=>new{mapId=id,mapName=Owned(id).Name}),
                    useApi=plan.UseApi,confirmationRequired=true,expiresUtc=plan.Expires,delaySeconds=plan.Delay,
                    point=plan.Point,command=plan.Message});
            }
            if(kind=="apply")return await Apply(a,id,actor,channel);
            return Error("UM-DC-METHOD-DENIED");
        }
        catch(Exception ex)
        {
            return Error(Regex.IsMatch(ex.Message,@"\AUM-[A-Z0-9_-]{1,150}\z")?ex.Message:"UM-DC-REQUEST-FAILED");
        }
        finally{hostGate.Release();}
    }
    public void Dispose(){closing.Cancel();listener?.Stop();}
}
