using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

// Local moderation receipts, not an assertion that an ASA account is banned.
// No cross-host dispatch, direct ban-file mutation or automatic unban.
public sealed class DesktopBanManager
{
    private sealed record Target(Guid MapId,string MapName,bool Historical,Guid Token,string Command);
    private sealed record Plan(Guid Token,string Action,string EosId,string Reason,
        DateTimeOffset? ReviewAtUtc,DateTimeOffset Expires,Target[] Targets);
    public sealed record Row(Guid MapId,string MapName,string Status);
    public sealed record Receipt(Guid Id,DateTimeOffset CreatedUtc,string Action,string EosId,
        string Reason,DateTimeOffset? ReviewAtUtc,Row[] Targets);
    private readonly ClusterService cluster;
    private readonly Func<string,Task<string>> fresh,historical;
    private readonly string folder,lockPath;
    private Plan? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal DesktopBanManager(ClusterService cluster,Func<string,Task<string>> fresh,
        Func<string,Task<string>> historical)
    {
        this.cluster=cluster;this.fresh=fresh;this.historical=historical;
        folder=Path.Combine(cluster.Store.Root,"moderation-v1");
        _=UntamedOperationCoordinator.ResourceKey(folder);
        lockPath=Path.Combine(folder,"owner.lock");
    }
    internal static string Command(string action,string eos)
    {
        if(!Regex.IsMatch(eos,@"\A[A-Za-z0-9]{32}\z",RegexOptions.CultureInvariant))
            throw new InvalidDataException("UM-BAN-EOS-ID");
        return (action switch{
            "ban"=>"BanPlayer","unban"=>"UnbanPlayer","kick"=>"KickPlayer",
            _=>throw new InvalidDataException("UM-BAN-ACTION")})+" "+eos;
    }
    private void NoLink(string p)
    {
        _=UntamedOperationCoordinator.ResourceKey(folder);
        if(Directory.Exists(p)||(File.Exists(p)&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0))
            throw new InvalidDataException("UM-BAN-JOURNAL-LINK");
    }
    private FileStream Own()
    {
        NoLink(lockPath);Directory.CreateDirectory(folder);
        return new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    private string FileFor(Guid id)=>Path.Combine(folder,id.ToString("N")+".json");
    private void Write(Receipt row)
    {
        string dst=FileFor(row.Id);NoLink(dst);string tmp=dst+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {JsonSerializer.Serialize(f,row,Json);f.Flush(true);}
            File.Move(tmp,dst,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    private Receipt[] History()
    {
        _=UntamedOperationCoordinator.ResourceKey(folder);
        if(!Directory.Exists(folder))return Array.Empty<Receipt>();
        return Directory.EnumerateFiles(folder,"*.json",SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc).Take(500).Select(path=>{
                NoLink(path);
                if(new FileInfo(path).Length is <2 or >32768)throw new InvalidDataException("UM-BAN-JOURNAL");
                var row=JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path),Json)
                    ??throw new InvalidDataException("UM-BAN-JOURNAL");
                if(row.Id==Guid.Empty||FileFor(row.Id)!=path||row.Targets==null||row.Targets.Length is <1 or >5)
                    throw new InvalidDataException("UM-BAN-JOURNAL");
                return row;
            }).ToArray();
    }
    private LocalMap Owned(Guid id)=>cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==id&&
        !x.Maintenance&&!x.ObservedReadOnlyImported)??throw new UnauthorizedAccessException("UM-BAN-LOCAL-ONLY");
    private static string Message(string method,object fields)
    {
        var payload=new Dictionary<string,object?>{["id"]=Guid.NewGuid(),["method"]=method};
        foreach(var f in JsonSerializer.SerializeToElement(fields,Json).EnumerateObject())payload[f.Name]=f.Value.Clone();
        return JsonSerializer.Serialize(payload,Json);
    }
    private static JsonElement Decode(string raw)
    {
        using var doc=JsonDocument.Parse(raw);
        if(!doc.RootElement.GetProperty("ok").GetBoolean())throw new InvalidOperationException("UM-BAN-NATIVE-REJECTED");
        return doc.RootElement.GetProperty("data").Clone();
    }
    private static string Ok(string? id,object data)=>JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Fail(string? id,string error)=>JsonSerializer.Serialize(new{id,ok=false,error},Json);
    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Fail(null,"UM-BAN-REQUEST");
            using var doc=JsonDocument.Parse(raw);var a=doc.RootElement;
            id=a.GetProperty("id").GetGuid().ToString("D");string? method=a.GetProperty("method").GetString();
            using var held=Own();
            if(method=="moderation.history")return Ok(id,new{rows=History(),gameBanStateVerified=false});
            if(method=="moderation.preview")
            {
                string eos=a.GetProperty("eosId").GetString()??"";
                string action=a.GetProperty("action").GetString()??"";
                string reason=(a.GetProperty("reason").GetString()??"").Trim();
                string command=Command(action,eos);
                var ids=a.GetProperty("mapIds").EnumerateArray().Select(x=>x.GetGuid()).ToArray();
                if(ids.Length is <1 or >5||ids.Distinct().Count()!=ids.Length||
                    reason.Length is <1 or >200||reason.Any(char.IsControl))return Fail(id,"UM-BAN-SCOPE");
                DateTimeOffset? review=null;
                if(a.TryGetProperty("reviewHours",out var h))
                {
                    int hours=h.GetInt32();
                    if(hours<0||hours>8760||(action!="ban"&&hours!=0))return Fail(id,"UM-BAN-REVIEW-RANGE");
                    if(hours>0)review=DateTimeOffset.UtcNow.AddHours(hours);
                }
                var targets=new List<Target>();
                foreach(Guid mapId in ids)
                {
                    var map=Owned(mapId);
                    var handler=map.HistoricalStoreProtected?historical:fresh;
                    var pre=Decode(await handler(Message(map.HistoricalStoreProtected?"historical.adminPreview":"rcon.adminPreview",
                        new{mapId,action="custom",message=command})));
                    if(pre.GetProperty("mapId").GetGuid()!=map.Id||pre.GetProperty("command").GetString()!=command)
                        throw new InvalidDataException("UM-BAN-PREVIEW");
                    targets.Add(new(map.Id,map.Name,map.HistoricalStoreProtected,pre.GetProperty("token").GetGuid(),command));
                }
                pending=new(Guid.NewGuid(),action,eos,reason,review,DateTimeOffset.UtcNow.AddMinutes(2),targets.ToArray());
                return Ok(id,new{token=pending.Token,action,eosId=eos,reason,reviewAtUtc=review,
                    command,maps=targets.Select(x=>new{mapId=x.MapId,mapName=x.MapName}),remoteExecutionAllowed=false});
            }
            if(method=="moderation.apply")
            {
                if(!a.TryGetProperty("confirm",out var yes)||yes.ValueKind!=JsonValueKind.True||
                    pending==null||pending.Token!=a.GetProperty("token").GetGuid()||pending.Expires<DateTimeOffset.UtcNow)
                    return Fail(id,"UM-BAN-APPROVAL");
                var plan=pending;pending=null;
                if(a.GetProperty("typedApproval").GetString()!="MODERÁCIÓ")return Fail(id,"UM-BAN-TYPED-APPROVAL");
                foreach(var t in plan.Targets)
                    if(Owned(t.MapId).HistoricalStoreProtected!=t.Historical)return Fail(id,"UM-BAN-MAP-CHANGED");
                var receipt=new Receipt(Guid.NewGuid(),DateTimeOffset.UtcNow,plan.Action,plan.EosId,plan.Reason,
                    plan.ReviewAtUtc,plan.Targets.Select(t=>new Row(t.MapId,t.MapName,"not-sent")).ToArray());
                Write(receipt);
                for(int i=0;i<plan.Targets.Length;i++)
                {
                    var t=plan.Targets[i];
                    receipt.Targets[i]=receipt.Targets[i] with{Status="claimed-no-replay"};Write(receipt);
                    try
                    {
                        var handler=t.Historical?historical:fresh;
                        var data=Decode(await handler(Message(t.Historical?"historical.adminSend":"rcon.adminSend",
                            new{token=t.Token,confirm=true,typedApproval=t.Historical?"STORE-V1 ADMIN":"ADMIN"})));
                        if(data.GetProperty("mapId").GetGuid()!=t.MapId||data.GetProperty("command").GetString()!=t.Command||
                            !data.GetProperty("commandSent").GetBoolean()||!data.GetProperty("oneShot").GetBoolean())
                            throw new InvalidDataException("UM-BAN-RECEIPT");
                        receipt.Targets[i]=receipt.Targets[i] with{Status="sent-effect-unverified"};Write(receipt);
                    }
                    catch
                    {
                        receipt.Targets[i]=receipt.Targets[i] with{Status="unknown-no-replay"};Write(receipt);break;
                    }
                }
                return Ok(id,receipt);
            }
            return Fail(id,"UM-METHOD-DENIED");
        }
        catch{return Fail(id,"UM-BAN-OPERATION-UNVERIFIED");}
    }
}
