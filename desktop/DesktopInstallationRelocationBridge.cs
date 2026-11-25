using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

public sealed class DesktopInstallationRelocationBridge
{
    private readonly ClusterService cluster;
    private readonly object gate=new();
    private readonly Dictionary<Guid,Approval> approvals=new();
    private sealed record Approval(Guid MapId,string Profile,InstallationRelocation.Plan Plan,DateTimeOffset Expires);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public DesktopInstallationRelocationBridge(ClusterService source)=>cluster=source;
    private static bool Overlap(string a,string b)
    {
        a=Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));b=Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return a.Equals(b,StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            b.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    private LocalMap Owned(Guid id,string destination)
    {
        var map=cluster.Config.LocalMaps.Single(x=>x.Id==id);
        bool store=FreshStoreEnrollmentPolicy.IsEnrolled(map);
        if(map.ObservedReadOnlyImported||map.HistoricalStoreProtected||
           map.AutoStart||map.Maintenance||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||!string.IsNullOrEmpty(map.HistoricalArchiveSha256)||
           !string.IsNullOrEmpty(map.HistoricalMapKey)||!string.IsNullOrEmpty(map.StoreTestRoot))
            throw new InvalidOperationException("UM-MOVE-ONLY-STANDALONE-LOCAL");
        if(store)
        {
            // Only the existing local new-Store authority, not historical or
            // remote writer rights. The exact transfer ID is revalidated on
            // every source check; transfer and cluster settings never move.
            FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);
            if(!string.IsNullOrWhiteSpace(map.ApiLoaderArguments)&&map.ApiLoaderArguments!=map.Arguments)
                throw new InvalidOperationException("UM-MOVE-STORE-LOADER-ARGS");
            if(map.StartWithAsaApi)_=AsaApiLaunchPolicy.Resolve(map);
        }
        else if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map)||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.Arguments)||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.ApiLoaderArguments)||
           map.Arguments.Contains(":\\",StringComparison.Ordinal)||map.Arguments.Contains(":/",StringComparison.Ordinal)||
           map.ApiLoaderArguments.Contains(":\\",StringComparison.Ordinal)||map.ApiLoaderArguments.Contains(":/",StringComparison.Ordinal)||
           map.Arguments.Contains("ClusterDirOverride",StringComparison.OrdinalIgnoreCase)||
           map.ApiLoaderArguments.Contains("ClusterDirOverride",StringComparison.OrdinalIgnoreCase)||
           map.Arguments.Contains("clusterid=",StringComparison.OrdinalIgnoreCase)||
           map.ApiLoaderArguments.Contains("clusterid=",StringComparison.OrdinalIgnoreCase)||
           cluster.Registry.Snapshot().ClusterRegistry.MapAssignments.Any(x=>x.MapId==map.Id))
            throw new InvalidOperationException("UM-MOVE-ONLY-STANDALONE-LOCAL");
        if(Overlap(destination,cluster.Store.Root)||Overlap(map.InstallationPath,cluster.Store.Root)||
           cluster.Config.LocalMaps.Any(x=>x.Id!=map.Id&&!string.IsNullOrWhiteSpace(x.InstallationPath)&&
             (Overlap(x.InstallationPath,destination)||Overlap(x.InstallationPath,map.InstallationPath)))||
           cluster.Registry.Snapshot().ClusterRegistry.Clusters.Any(x=>
             (!string.IsNullOrWhiteSpace(x.TransferDirectory)&&(Overlap(destination,x.TransferDirectory)||Overlap(map.InstallationPath,x.TransferDirectory)))||
             (!string.IsNullOrWhiteSpace(x.BackupDirectory)&&(Overlap(destination,x.BackupDirectory)||Overlap(map.InstallationPath,x.BackupDirectory)))))
            throw new InvalidOperationException("UM-MOVE-PATH-OVERLAP");
        new AsaManualRestartJournal(map.InstallationPath).DemandNoPending();
        new AsaManualStopJournal(map.InstallationPath).DemandNoPending(id);
        var state=cluster.Check(map);
        if(!state.ConfirmedStopped||state.Running||state.Identity!=null||state.LoaderIdentity!=null)
            throw new InvalidOperationException("UM-MOVE-STOP-REQUIRED");
        return map;
    }
    private static string Relocate(string value,string source,string target)
    {
        if(string.IsNullOrWhiteSpace(value))return "";
        string rel=Path.GetRelativePath(source,Path.GetFullPath(value));
        if(Path.IsPathFullyQualified(rel)||rel=="."||rel==".."||rel.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal))
            throw new InvalidDataException("UM-MOVE-EXTERNAL-PROFILE-PATH");
        return Path.Combine(target,rel);
    }
    public Task<string> HandleAsync(string raw)=>Task.Run(()=>{
        string? id=null;
        string Error(string code)=>JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
        string Ok(object data)=>JsonSerializer.Serialize(new{id,ok=true,data},Json);
        try{
            if(raw.Length is <2 or >4096)return Error("UM-MOVE-REQUEST");
            using var doc=JsonDocument.Parse(raw);var args=doc.RootElement;
            id=args.GetProperty("id").GetGuid().ToString("D");
            string? method=args.GetProperty("method").GetString();
            if(method=="servers.relocationPreview")
            {
                Guid mapId=args.GetProperty("mapId").GetGuid();
                string target=args.GetProperty("root").GetString()??"";
                _=UntamedOperationCoordinator.ResourceKey(target);
                var map=Owned(mapId,target);
                var plan=InstallationRelocation.Preview(map.InstallationPath,target);
                _=Relocate(map.ExecutablePath,plan.Source,plan.Destination);
                _=Relocate(map.ApiLoaderPath,plan.Source,plan.Destination);
                _=Relocate(map.LogFilePath,plan.Source,plan.Destination);
                Guid token=Guid.NewGuid();
                lock(gate){
                    foreach(var old in approvals.Where(x=>x.Value.Expires<DateTimeOffset.UtcNow||x.Value.MapId==mapId).Select(x=>x.Key).ToArray())approvals.Remove(old);
                    if(approvals.Count>=8)return Error("UM-MOVE-PREVIEW-CAPACITY");
                    approvals[token]=new(mapId,JsonSerializer.Serialize(map),plan,DateTimeOffset.UtcNow.AddMinutes(5));
                }
                return Ok(new{token,mapId,files=plan.Entries.Count(x=>!x.Directory),bytes=plan.Bytes,freeBytes=plan.FreeBytes,sourceRetained=true});
            }
            if(method!="servers.relocationApply"||!args.GetProperty("confirm").GetBoolean())return Error("UM-MOVE-APPROVAL");
            Guid permit=args.GetProperty("token").GetGuid();Approval approved;
            lock(gate){
                if(!approvals.Remove(permit,out var found)||found.Expires<DateTimeOffset.UtcNow)return Error("UM-MOVE-APPROVAL");
                approved=found;
            }
            var selected=Owned(approved.MapId,approved.Plan.Destination);
            if(JsonSerializer.Serialize(selected)!=approved.Profile)return Error("UM-MOVE-PROFILE-CHANGED");
            // Profile, old and new paths are separately pinned. No shared
            // transfer data, source retirement or automatic restart.
            using var lease=cluster.Operations.AcquireMany(new[]{cluster.Store.Root,approved.Plan.Source,approved.Plan.Destination},UntamedOperationKind.Configuration);
            void Verify()
            {
                var map=Owned(approved.MapId,approved.Plan.Destination);
                if(!ReferenceEquals(map,selected)||JsonSerializer.Serialize(map)!=approved.Profile)
                    throw new InvalidOperationException("UM-MOVE-PROFILE-CHANGED");
            }
            string exe=Relocate(selected.ExecutablePath,approved.Plan.Source,approved.Plan.Destination);
            string api=Relocate(selected.ApiLoaderPath,approved.Plan.Source,approved.Plan.Destination);
            string log=Relocate(selected.LogFilePath,approved.Plan.Source,approved.Plan.Destination);
            InstallationRelocation.CopyVerified(approved.Plan,Verify);
            Verify();
            var candidate=JsonSerializer.Deserialize<LocalMap>(approved.Profile)!;
            candidate.InstallationPath=approved.Plan.Destination;candidate.ExecutablePath=exe;candidate.ApiLoaderPath=api;candidate.LogFilePath=log;
            candidate.LastProcess=null;candidate.LastLoader=null;
            if(!AsaReadyProbe.IsRegisteredServerBinary(candidate)||
               AsaSteamBuildCatalog.InstalledBuild(candidate.InstallationPath)!=AsaSteamBuildCatalog.InstalledBuild(selected.InstallationPath)||
               (selected.RconPort>0&&!AsaRconConfigurationEvidence.Matches(candidate)))
                throw new IOException("UM-MOVE-TARGET-SETUP-INVALID");
            var oldRoot=selected.InstallationPath;var oldExe=selected.ExecutablePath;var oldApi=selected.ApiLoaderPath;var oldLog=selected.LogFilePath;
            var oldProcess=selected.LastProcess;var oldLoader=selected.LastLoader;
            lease.MarkMutationStarted();
            selected.InstallationPath=candidate.InstallationPath;selected.ExecutablePath=exe;selected.ApiLoaderPath=api;selected.LogFilePath=log;
            selected.LastProcess=null;selected.LastLoader=null;
            try{
                if(FreshStoreEnrollmentPolicy.IsEnrolled(selected))
                {
                    FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,selected);
                    if(selected.StartWithAsaApi)_=AsaApiLaunchPolicy.Resolve(selected);
                    var destinationState=cluster.Check(selected);
                    if(!destinationState.ConfirmedStopped||destinationState.Running||destinationState.Identity!=null||destinationState.LoaderIdentity!=null)
                        throw new InvalidOperationException("UM-MOVE-TARGET-PROCESS-UNVERIFIED");
                }
                cluster.Save();
            }
            catch{
                // ConfigStore writes atomically. If persistence cannot be
                // verified do not remove either installation or retry.
                selected.InstallationPath=oldRoot;selected.ExecutablePath=oldExe;selected.ApiLoaderPath=oldApi;selected.LogFilePath=oldLog;
                selected.LastProcess=oldProcess;selected.LastLoader=oldLoader;
                throw;
            }
            lease.Complete();
            return Ok(new{mapId=selected.Id,files=approved.Plan.Entries.Count(x=>!x.Directory),bytes=approved.Plan.Bytes,sourceRetained=true,profileSwitched=true,serverStarted=false,localStoreEnrollmentPreserved=FreshStoreEnrollmentPolicy.IsEnrolled(selected),transferDirectoryUnchanged=true});
        }catch(Exception ex){string code=ex.Message;
            return Error(System.Text.RegularExpressions.Regex.IsMatch(code,@"\AUM-[A-Z0-9-]{1,100}\z")?code:"UM-MOVE-UNAVAILABLE");}
    });
}
