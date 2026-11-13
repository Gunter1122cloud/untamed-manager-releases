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
    // An enrolled Store map requires stronger ownership and transfer proof
    // than a fresh non-Store install. The original Legacy profiles remain denied.
    private static void DemandSafeOwnedMap(ClusterService cluster,LocalMap map)
    {
        if(map.Maintenance||map.HistoricalStoreProtected)
            throw new InvalidOperationException("UM-API-CORE-PROTECTED-MAP");
        if(FreshStoreEnrollmentPolicy.IsEnrolled(map))
            FreshStoreEnrollmentPolicy.DemandEnrolled(cluster,map);
        else if(!FreshAsaInstallationSafety.IsProvenFresh(cluster,map)||
                UntamedWorldBackup.HasLiveOrHistoricalStore(map))
            throw new InvalidOperationException("UM-API-CORE-UNVERIFIED-LOCAL-MAP");
    }

    private static (LocalMap Map,string Root,string Binary) Preflight(ClusterService cluster,Guid mapId)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var map=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==mapId&&m.Id!=Guid.Empty)
            ??throw new UnauthorizedAccessException("UM-API-CORE-LOCAL-OWNER");
        DemandSafeOwnedMap(cluster,map);
        if(!AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidOperationException("UM-API-CORE-LOCAL-ASA-EXE-REQUIRED");
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
            DemandSafeOwnedMap(cluster,participant);
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
    // UI receives opaque transaction identifiers only, never arbitrary disk
    // paths. The actual rollback revalidates every SHA-256 under a machine lock.
    public sealed record RollbackCandidate(string TransactionId,string ReleaseTag,
        int Binaries);

    public static IReadOnlyList<RollbackCandidate> ListCompletedRollbacks(
        ClusterService cluster,Guid mapId)
    {
        var (_,root,_)=Preflight(cluster,mapId);
        string parent=Safe(Path.Combine(cluster.Store.Root,"api-core-transactions"));
        if(!Directory.Exists(parent))return Array.Empty<RollbackCandidate>();
        var results=new List<RollbackCandidate>();
        foreach(string directory in Directory.GetDirectories(parent))
        {
            string name=Path.GetFileName(directory);
            if(!Guid.TryParseExact(name,"N",out _)||
               (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
                continue;
            string committed=Safe(Path.Combine(directory,"complete"));
            string reverted=Safe(Path.Combine(directory,"rolled-back"));
            string statePath=Safe(Path.Combine(directory,"transaction.json"));
            if(!File.Exists(committed)||File.Exists(reverted)||
               !File.Exists(statePath)||
               new FileInfo(statePath).Length is <50 or >32768)
                continue;
            try
            {
                var state=JsonSerializer.Deserialize<State>(File.ReadAllText(statePath));
                if(state?.Schema==1&&state.MapId==mapId&&
                   state.Installation.Equals(root,StringComparison.OrdinalIgnoreCase)&&
                   state.Phase=="prepared"&&state.Files is { Length: > 0 }&&
                   !string.IsNullOrWhiteSpace(state.ReleaseTag))
                    results.Add(new(name,state.ReleaseTag,state.Files.Length));
            }
            catch(JsonException){/* never advertise corrupt/untrusted recovery state */}
        }
        return results.OrderByDescending(x=>x.TransactionId,StringComparer.Ordinal)
            .Take(30).ToArray();
    }

    /// <summary>
    /// Explicit operator rollback of a COMPLETED owned CORE update. This is
    /// intentionally not an automatic recovery for an interrupted update:
    /// operation-journal NeedsOperator and any unexpected on-disk byte forbid it.
    /// A successful rollback restores only the exact, SHA-256-pinned API files;
    /// player plugins, configs, world saves and the original transaction survive.
    /// </summary>
    public sealed record RollbackReceipt(Guid MapId,string TransactionId,
        int FilesRestored,int FilesRemoved,bool ByteVerified,bool Restarted,
        string Evidence);

    public static RollbackReceipt RollbackCompletedOwnedStopped(
        ClusterService cluster,Guid mapId,string transactionId,
        bool operatorApproved,CancellationToken ct=default)
    {
        if(!operatorApproved)throw new UnauthorizedAccessException("UM-API-CORE-ROLLBACK-APPROVAL");
        if(transactionId==null||transactionId.Length!=32||
           transactionId.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidDataException("UM-API-CORE-ROLLBACK-ID");
        ct.ThrowIfCancellationRequested();
        var (_,root,binary)=Preflight(cluster,mapId);
        string transaction=Safe(Path.Combine(cluster.Store.Root,
            "api-core-transactions",transactionId));
        if(!Directory.Exists(transaction)||
           (File.GetAttributes(transaction)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-API-CORE-ROLLBACK-TRANSACTION");
        string done=Safe(Path.Combine(transaction,"complete"));
        string rolledBack=Safe(Path.Combine(transaction,"rolled-back"));
        string statePath=Safe(Path.Combine(transaction,"transaction.json"));
        if(!File.Exists(done)||File.Exists(rolledBack)||!File.Exists(statePath)||
           new FileInfo(statePath).Length is <50 or >32768)
            throw new InvalidOperationException("UM-API-CORE-ROLLBACK-NOT-COMMITTED");
        State state;
        try
        {
            state=JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)) ??
                throw new InvalidDataException("UM-API-CORE-ROLLBACK-STATE");
        }
        catch(JsonException e)
        {
            throw new InvalidDataException("UM-API-CORE-ROLLBACK-STATE",e);
        }
        var eligible=Required.Concat(Dependencies).ToHashSet(StringComparer.Ordinal);
        if(state.Schema!=1||state.MapId!=mapId||
           !state.Installation.Equals(root,StringComparison.OrdinalIgnoreCase)||
           state.Phase!="prepared"||state.Files==null||
           state.Files.Length<Required.Length||
           state.Files.Length>eligible.Count||
           state.Files.Any(item=>item==null||!eligible.Contains(item.Relative)||
               item.NewHash==null||item.NewHash.Length!=64||
               item.NewHash.Any(c=>!Uri.IsHexDigit(c))||
               (item.HadOriginal&&(item.PreviousHash==null||
                   item.PreviousHash.Length!=64||
                   item.PreviousHash.Any(c=>!Uri.IsHexDigit(c)))))||
           state.Files.Select(x=>x.Relative).Distinct(StringComparer.Ordinal).Count()!=state.Files.Length||
           Required.Any(name=>!state.Files.Any(x=>x.Relative==name)))
            throw new InvalidDataException("UM-API-CORE-ROLLBACK-STATE");
        // Before acquiring the mutation lock verify BOTH the installed files
        // and the historical copies. A later update or manual edit must never
        // be silently overwritten with an older loader.
        void VerifyInputs()
        {
            foreach(var item in state.Files)
            {
                string target=Safe(Path.Combine(binary,
                    item.Relative.Replace('/',Path.DirectorySeparatorChar)));
                if(!File.Exists(target)||Hash(target)!=item.NewHash)
                    throw new IOException("UM-API-CORE-ROLLBACK-DESTINATION-CHANGED");
                if(item.HadOriginal)
                {
                    string original=Safe(Path.Combine(transaction,"previous",
                        item.Relative.Replace('/',Path.DirectorySeparatorChar)));
                    if(!File.Exists(original)||Hash(original)!=item.PreviousHash)
                        throw new IOException("UM-API-CORE-ROLLBACK-SNAPSHOT-CHANGED");
                }
            }
        }
        VerifyInputs();
        using var lease=cluster.Operations.Acquire(root,UntamedOperationKind.Update);
        Preflight(cluster,mapId);
        VerifyInputs();
        ct.ThrowIfCancellationRequested();
        // Durable intent precedes every destructive change. Interrupted
        // recovery stays in NeedsOperator rather than blindly retrying.
        Durable(Path.Combine(transaction,"rollback-intent"),new {
            schema=1,mapId,transactionId,utc=DateTimeOffset.UtcNow,
            expected=state.Files.Length
        });
        lease.MarkMutationStarted();
        int restored=0,removed=0;
        foreach(var item in state.Files)
        {
            ct.ThrowIfCancellationRequested();
            Preflight(cluster,mapId);
            string target=Safe(Path.Combine(binary,
                item.Relative.Replace('/',Path.DirectorySeparatorChar)));
            if(!File.Exists(target)||Hash(target)!=item.NewHash)
                throw new IOException("UM-API-CORE-ROLLBACK-DESTINATION-CHANGED");
            if(item.HadOriginal)
            {
                string original=Safe(Path.Combine(transaction,"previous",
                    item.Relative.Replace('/',Path.DirectorySeparatorChar)));
                if(!File.Exists(original)||Hash(original)!=item.PreviousHash)
                    throw new IOException("UM-API-CORE-ROLLBACK-SNAPSHOT-CHANGED");
                string incoming=target+".untamed-rollback-"+Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(original,incoming,false);
                    if(Hash(incoming)!=item.PreviousHash)
                        throw new IOException("UM-API-CORE-ROLLBACK-COPY-CHANGED");
                    using(var output=new FileStream(incoming,FileMode.Open,
                        FileAccess.ReadWrite,FileShare.None))output.Flush(true);
                    File.Move(incoming,target,true);
                }
                finally{if(File.Exists(incoming))File.Delete(incoming);}
                restored++;
            }
            else
            {
                File.Delete(target);
                removed++;
            }
        }
        foreach(var item in state.Files)
        {
            string target=Safe(Path.Combine(binary,
                item.Relative.Replace('/',Path.DirectorySeparatorChar)));
            if(item.HadOriginal?
                (!File.Exists(target)||Hash(target)!=item.PreviousHash):
                File.Exists(target))
                throw new IOException("UM-API-CORE-ROLLBACK-VERIFY-FAILED");
        }
        Durable(rolledBack,new {schema=1,mapId,transactionId,
            utc=DateTimeOffset.UtcNow,filesRestored=restored,
            filesRemoved=removed,bytesVerified=true,runtimeVerified=false});
        lease.Complete();
        return new(mapId,transactionId,restored,removed,true,false,
            "STOPPED_OWNED_OFFICIAL_API_CORE_SHA256_ROLLBACK_ONLY");
    }

}
