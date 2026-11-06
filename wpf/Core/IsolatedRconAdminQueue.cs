using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public sealed class RconAdminPermit
{
    public Guid Operation {get;}=Guid.NewGuid();
    public Guid Map {get;}
    public DateTimeOffset ExpiresUtc {get;}
    internal string OperatorSid {get;}
    internal string Configuration {get;}
    internal string Profile {get;}
    internal RconDraft Draft {get;}
    internal ProcessIdentity Process {get;}
    internal RconAdminPermit(Guid map,string sid,string config,string profile,RconDraft draft,
        ProcessIdentity process,DateTimeOffset expires)
    {Map=map;OperatorSid=sid;Configuration=config;Profile=profile;Draft=draft;ExpiresUtc=expires;
        Process=new(){Pid=process.Pid,StartedUtc=process.StartedUtc,
            ExecutablePath=process.ExecutablePath,Arguments=process.Arguments};}
    public override string ToString()=>"Private one-shot local RCON permission";
}
public sealed record RconAdminReceipt(Guid Operation,Guid Map,RconAction Action,
    DateTimeOffset CompletedUtc,bool Acknowledged,bool SaveCompleteProven=false);

// Real authenticated TCP admin dispatch, confined to an independently created
// TEMP lab and a kernel-verified listener owned by the exact observed process.
// Public production read-only RCON and all lifecycle release gates stay unchanged.
public static class IsolatedRconAdminQueue
{
    private static string Local(string path)
    {
        UntamedOperationCoordinator.ResourceKey(path);string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked RCON lab file refused");
        return full;
    }
    private static string Sid()=>WindowsIdentity.GetCurrent().User?.Value??
        throw new UnauthorizedAccessException("Local Windows operator identity required");
    private static LocalMap Map(ClusterService cluster,Guid id)
    {
        var map=cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==id&&id!=Guid.Empty)??
            throw new UnauthorizedAccessException("RCON target is not a registered local map");
        string install=Local(map.InstallationPath),temp=Local(Path.GetTempPath());
        if(!install.StartsWith(temp+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Mutating RCON requires a separate lab installation");
        string[] parts=Path.GetRelativePath(temp,install).Split(Path.DirectorySeparatorChar);
        if(parts.Length!=2||!parts[0].StartsWith("Untamed-RconLab-",StringComparison.Ordinal)||parts[1]!="installation"||
           !Local(map.ExecutablePath).StartsWith(install+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
           !File.Exists(map.ExecutablePath)||map.RconHost!="127.0.0.1"||map.Maintenance||
           cluster.Config.LocalMaps.Any(x=>x.Id!=map.Id&&x.RconPort==map.RconPort))
            throw new UnauthorizedAccessException("Lab ownership, endpoint or maintenance precondition failed");
        return map;
    }
    private static bool Same(ProcessIdentity a,ProcessIdentity b)=>a.Pid==b.Pid&&a.StartedUtc==b.StartedUtc&&
        string.Equals(a.ExecutablePath,b.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&a.Arguments==b.Arguments;
    private static ProcessIdentity Running(ClusterService cluster,LocalMap map)
    {
        var state=cluster.Check(map);
        if(!state.Running||state.ConfirmedStopped||state.Identity==null||
           !string.Equals(Local(state.Identity.ExecutablePath),Local(map.ExecutablePath),StringComparison.OrdinalIgnoreCase)||
           state.Identity.Arguments!=map.Arguments||
           !RconPortOwnership.IsOwnedListener(map.RconPort,state.Identity.Pid))
            throw new UnauthorizedAccessException("Exact local process and RCON port ownership are not verified");
        return state.Identity;
    }
    private static string Configuration(LocalMap map)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new{map.Id,map.InstallationPath,map.ExecutablePath,map.Arguments,
            map.RconHost,map.RconPort,map.RconPasswordProtected,map.Maintenance}))));

    // Invoke only after the operator confirms the exact action preview. The
    // uneditable permit is bound to this Windows SID, config and process boot.
    public static RconAdminPermit Approve(ClusterService cluster,Guid mapId,RconAction action,string? text)
    {
        var map=Map(cluster,mapId);var process=Running(cluster,map);
        var draft=RconCommandPolicy.Preview(map.Id,action,text,true,false,true);
        if(action is RconAction.ListPlayers or RconAction.Custom)
            throw new UnauthorizedAccessException("Explicit mutating action required for admin permit");
        return new(map.Id,Sid(),Configuration(map),Local(cluster.Store.Root),draft,process,DateTimeOffset.UtcNow.AddSeconds(60));
    }

    public static async Task<RconAdminReceipt> ExecuteOnceAsync(ClusterService cluster,
        RconAdminPermit permit,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(permit);ct.ThrowIfCancellationRequested();
        var map=Map(cluster,permit.Map);string installation=Local(map.InstallationPath);
        void Validate()
        {
            var current=Map(cluster,permit.Map);
            if(!ReferenceEquals(current,map)||Local(current.InstallationPath)!=installation||
               permit.ExpiresUtc<DateTimeOffset.UtcNow||permit.ExpiresUtc>DateTimeOffset.UtcNow.AddSeconds(61)||
               permit.OperatorSid!=Sid()||Local(cluster.Store.Root)!=permit.Profile||Configuration(current)!=permit.Configuration||
               !Same(Running(cluster,current),permit.Process))
                throw new UnauthorizedAccessException("RCON permission expired or its operator, configuration or process changed");
        }
        using var lease=cluster.Operations.AcquireMany(new[]{installation,cluster.Store.Root},UntamedOperationKind.Configuration);
        Validate();
        string claims=Local(Path.Combine(cluster.Store.Root,"rcon-admin-claims"));Directory.CreateDirectory(claims);
        string claim=Local(Path.Combine(claims,permit.Operation.ToString("N")+".json"));
        bool dispatchStarted=false;
        lease.MarkMutationStarted();
        try
        {
            // Atomic create-new is also durable replay protection across restarts
            // and distinct Manager profiles sharing this claim directory.
            using(var file=new FileStream(claim,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(file,new{permit.Operation,permit.Map,Action=permit.Draft.Action.ToString(),
                State="claimed-never-auto-retry",Utc=DateTimeOffset.UtcNow});file.Flush(true);}
            string password=SecureStore.Unprotect(map.RconPasswordProtected);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await RconProtocol.DispatchAdminAsync(map.RconPort,password,permit.Draft,()=>{
                deadline.Token.ThrowIfCancellationRequested();Validate();dispatchStarted=true;
            },deadline.Token);
            Validate();
            var receipt=new RconAdminReceipt(permit.Operation,permit.Map,permit.Draft.Action,DateTimeOffset.UtcNow,true);
            using(var file=new FileStream(claim+".receipt",FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(file,receipt);file.Flush(true);}
            lease.Complete();return receipt;
        }
        catch
        {
            // Authentication/validation failures before command dispatch consume
            // the permit but do not claim a server mutation. Once dispatch may
            // have begun, the operation remains NeedsOperator even on timeout.
            if(!dispatchStarted)lease.Complete();
            throw;
        }
    }
}
