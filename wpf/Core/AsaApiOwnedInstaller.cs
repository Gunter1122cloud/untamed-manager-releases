using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

/// <summary>
/// Real API CORE update for an explicitly approved, owned, STOPPED, fresh
/// ASA installation. Selectively snapshots ONLY the API binary files, not
/// multi-GB world saves/server EXEs or any plugin state. No Store-v1 changes.
/// Requires a staged GitHub asset whose SHA-256 is independently attested
/// by official publisher metadata, and leaves a durable recovery journal.
/// Never runs ASA, restarts it, or claims module runtime initialized.
/// </summary>
public static class AsaApiOwnedInstaller
{
    public sealed record Receipt(Guid MapId,string ReleaseTag,
        string TransactionId,int Binaries,bool HasProtectiveCopy,
        bool ByteVerified,bool LoaderRuntimeVerified,bool Restarted,
        string Evidence);
    private sealed record Item(string Relative,bool HadOriginal,
        string PreviousHash,string NewHash);
    private sealed record State(int Schema,Guid MapId,string Installation,
        string ReleaseTag,string AssetDigest,string Phase,Item[] Files);
    private static readonly string[] Required={"AsaApiLoader.exe","ArkApi/AsaApi.dll"};
    private static readonly string[] Dependencies={
        "libcrypto-3-x64.dll","libssl-3-x64.dll",
        "libcrypto-1_1-x64.dll","libssl-1_1-x64.dll"
    };
    private static string Hash(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.Read,65536,FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void Durable(string path,object value)
    {
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(stream,value);stream.Flush(true);
    }
    private static string Safe(string path)
    {
        string canonical=UntamedOperationCoordinator.ResourceKey(path);
        _=canonical;
        string full=Path.GetFullPath(path);
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-API-CORE-LINKED-FILE");
        return full;
    }
    private static (LocalMap Map,string Root,string Binary) Preflight(ClusterService cluster,Guid mapId)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId&&m.Id!=Guid.Empty)
            ??throw new UnauthorizedAccessException("UM-API-CORE-LOCAL-OWNER");
        if(map.Maintenance||!FreshAsaInstallationSafety.IsProvenFresh(cluster,map)||
           !AsaReadyProbe.IsRegisteredServerBinary(map)||
           UntamedWorldBackup.HasLiveOrHistoricalStore(map))
            throw new InvalidOperationException("UM-API-CORE-FRESH-NONSTORE-REQUIRED");
        string root=Safe(map.InstallationPath);
        var journal=new AsaManualRestartJournal(root);
        journal.DemandNoPending();
        if(UntamedWorldBackup.InstallationRecoveryRequired(map))
            throw new InvalidOperationException("UM-API-CORE-RESTORE-PENDING");
        var owned=cluster.Config.LocalMaps.Where(x=>
            !string.IsNullOrWhiteSpace(x.InstallationPath)&&
            string.Equals(Path.GetFullPath(x.InstallationPath),root,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if(owned.Length==0)throw new InvalidOperationException("UM-API-CORE-NO-OWNER");
        foreach(var participant in owned)
        {
            if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,participant)||
               participant.Maintenance ||
               UntamedWorldBackup.HasLiveOrHistoricalStore(participant))
                throw new InvalidOperationException("UM-API-CORE-STORE-OR-MAINTENANCE");
            var process=cluster.ObserveLocalProcessesReadOnly(participant);
            if(process.Running||!process.ConfirmedStopped||
               process.Identity!=null||process.LoaderIdentity!=null)
                throw new InvalidOperationException("UM-API-CORE-SERVER-UNCERTAIN-OR-RUNNING");
        }
        var binary=Safe(Path.Combine(root,"ShooterGame","Binaries","Win64"));
        if(!Directory.Exists(binary))throw new DirectoryNotFoundException("UM-API-CORE-BINARY-ROOT");
        foreach(var proxy in new[]{"version.dll","winmm.dll","dinput8.dll","dsound.dll"})
            if(File.Exists(Path.Combine(binary,proxy)))
                throw new InvalidDataException("UM-API-CORE-UNVERIFIED-PROXY");
        return(map,root,binary);
    }
    public static Receipt ApplyOwnedStopped(
        ClusterService cluster,Guid mapId,string verifiedStagedRelease,
        AsaApiReleaseInfo release,AsaApiReleaseAsset asset,
        bool operatorApproved,CancellationToken ct=default)
    {
        if(!operatorApproved)throw new UnauthorizedAccessException("UM-API-CORE-APPROVAL");
        ct.ThrowIfCancellationRequested();
        var (map,root,binary)=Preflight(cluster,mapId);
        var proof=AsaApiReleaseStaging.VerifyAgainstPublisher(
            verifiedStagedRelease,release,asset);
        using var zip=ZipFile.OpenRead(Safe(Path.Combine(verifiedStagedRelease,"release.zip")));
        var eligible=Required.Concat(Dependencies).ToArray();
        var core=zip.Entries.Where(e=>eligible.Contains(e.FullName,StringComparer.Ordinal))
            .ToArray();
        if(Required.Any(name=>core.Count(e=>e.FullName==name)!=1)||
           core.Length!=core.Select(e=>e.FullName).Distinct(StringComparer.Ordinal).Count()||
           core.Length>eligible.Length||
           core.Any(x=>x.Length is <=128 or >128L*1024*1024))
            throw new InvalidDataException("UM-API-CORE-UNEXPECTED-RELEASE-LAYOUT");
        // No plugins or config.json from upstream can overwrite operator files.
        string store=Safe(cluster.Store.Root);
        if(store.Equals(root,StringComparison.OrdinalIgnoreCase)||
           store.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
           root.StartsWith(store+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-API-CORE-BACKUP-OVERLAPS-INSTALL");
        string id=Guid.NewGuid().ToString("N");
        string journal=Safe(Path.Combine(store,"api-core-transactions",id));
        Directory.CreateDirectory(journal);
        string payload=Path.Combine(journal,"payload");
        string previous=Path.Combine(journal,"previous");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(previous);
        var items=new List<Item>();
        foreach(var entry in core)
        {
            ct.ThrowIfCancellationRequested();
            string target=Safe(Path.Combine(binary,entry.FullName.Replace('/',Path.DirectorySeparatorChar)));
            string prepared=Safe(Path.Combine(payload,entry.FullName.Replace('/',Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(prepared)!);
            using(var output=new FileStream(prepared,FileMode.CreateNew,
                FileAccess.Write,FileShare.None))
            using(var input=entry.Open())
            {
                input.CopyTo(output);output.Flush(true);
                if(output.Length!=entry.Length)
                    throw new InvalidDataException("UM-API-CORE-TRUNCATED-RELEASE");
            }
            if(!AsaApiInspector.IsPe64(prepared))
                throw new InvalidDataException("UM-API-CORE-NOT-X64-PE");
            string hash=Hash(prepared);
            bool had=File.Exists(target);
            string oldHash="";
            if(had)
            {
                string prior=Safe(Path.Combine(previous,
                    entry.FullName.Replace('/',Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(prior)!);
                File.Copy(target,prior,false);
                oldHash=Hash(prior);
                if(Hash(target)!=oldHash)
                    throw new IOException("UM-API-CORE-ORIGINAL-CHANGED");
            }
            items.Add(new(entry.FullName,had,oldHash,hash));
        }
        var state=new State(1,mapId,root,release.Tag,proof.OfficialSha256,
            "prepared",items.ToArray());
        Durable(Path.Combine(journal,"transaction.json"),state);
        using var lease=cluster.Operations.Acquire(root,UntamedOperationKind.Update);
        Preflight(cluster,mapId);
        foreach(var item in items)
        {
            string target=Safe(Path.Combine(binary,
                item.Relative.Replace('/',Path.DirectorySeparatorChar)));
            if(item.HadOriginal!=File.Exists(target)||
               (item.HadOriginal&&Hash(target)!=item.PreviousHash))
                throw new IOException("UM-API-CORE-SOURCE-CHANGED");
        }
        lease.MarkMutationStarted();
        try
        {
            foreach(var item in items)
            {
                ct.ThrowIfCancellationRequested();
                Preflight(cluster,mapId);
                string target=Safe(Path.Combine(binary,
                    item.Relative.Replace('/',Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string incoming=target+".untamed-"+Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(Path.Combine(payload,
                        item.Relative.Replace('/',Path.DirectorySeparatorChar)),incoming,false);
                    if(Hash(incoming)!=item.NewHash)
                        throw new InvalidDataException("UM-API-CORE-PAYLOAD-CHANGED");
                    using(var output=new FileStream(incoming,FileMode.Open,
                        FileAccess.ReadWrite,FileShare.None))output.Flush(true);
                    File.Move(incoming,target,true);
                }
                finally{if(File.Exists(incoming))File.Delete(incoming);}
            }
            Preflight(cluster,mapId);
            foreach(var item in items)
            {
                string file=Safe(Path.Combine(binary,
                    item.Relative.Replace('/',Path.DirectorySeparatorChar)));
                if(Hash(file)!=item.NewHash)
                    throw new InvalidDataException("UM-API-CORE-DESTINATION-DIGEST");
            }
            // Durable completion is distinct from runtime module initialization.
            Durable(Path.Combine(journal,"complete"),new{
                schema=1,mapId,release.Tag,utc=DateTimeOffset.UtcNow,
                binariesVerified=true,runtimeVerified=false
            });
            lease.Complete();
            return new(mapId,release.Tag,id,items.Count,true,true,
                LoaderRuntimeVerified:false,Restarted:false,
                "OFFICIAL_SHA256_STOPPED_API_CORE_SELECTIVE_ROLLBACK_SNAPSHOT");
        }
        catch
        {
            // Do not silently remove or overwrite unknown bytes after a
            // process-state change. The durable mutation journal survives
            // interrupted writes and must be reviewed before another update.
            throw;
        }
    }
}
