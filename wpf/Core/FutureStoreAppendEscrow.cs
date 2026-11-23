using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Offline, Manager-owned before/after image escrow for a future Store-v1
/// append transaction. Stores exact source files and the proposed planner
/// output under a unique LOCAL directory. Never applies any of these bytes
/// to a running map, the original source, a transfer share or a DONE peer.
/// A future separately reviewed executor MUST certify a two-host quiescent
/// epoch before beginning its own crash-recoverable atomic commit.
/// </summary>
public sealed record FutureStoreStagedAppend(
    Guid MapId,Guid OperationId,string StagingDirectory,
    string BeforeSha256,string AfterSha256,string[] BeforeFileSha256,
    string[] AfterFileSha256,DateTimeOffset CreatedUtc)
{
    public bool OriginalStoreTouched=>false;
    public bool RemotePeerTouched=>false;
    public bool ApplyAuthorized=>false;
}

public static class FutureStoreAppendEscrow
{
    private static readonly string[] Names={
        "cluster-v1.json","cluster-store-v1.json","shared-store-lock-v1.json",
        "launch-profiles-v1.json","local-done-peer.json"
    };
    private sealed record Manifest(
        int Schema,Guid MapId,Guid OperationId,
        string BeforeSha256,string AfterSha256,
        string[] BeforeHashes,string[] AfterHashes,DateTimeOffset CreatedUtc);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Sha(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Hex(string? x)=>x?.Length==64&&x.All(Uri.IsHexDigit);
    private static string Digest(string[] files)=>Sha(Encoding.UTF8.GetBytes(
        string.Join("\n",files)));
    private static string Destination(string profile,Guid mapId,Guid operationId)
    {
        if(mapId==Guid.Empty||operationId==Guid.Empty)
            throw new InvalidDataException("UM-FUTURE-ESCROW-IDENTITY");
        string root=VaultReadSnapshot.LocalPath(profile,true);
        return Path.Combine(root,"future-store-append-escrow",
            mapId.ToString("N")+"-"+operationId.ToString("N"));
    }

    public static FutureStoreStagedAppend Stage(string managerProfileRoot,
        Guid mapId,Guid operationId,string[] sourceImages,
        FutureStoreAppendBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(sourceImages);
        ArgumentNullException.ThrowIfNull(bundle);
        if(sourceImages.Length!=Names.Length||
           sourceImages.Any(x=>string.IsNullOrWhiteSpace(x)||x.Length>1_048_576)||
           bundle.StoreWriteAuthorized||bundle.ConversionExecuted||
           bundle.CharacterTravelCertified)
            throw new InvalidDataException("UM-FUTURE-ESCROW-SOURCE-INVALID");
        string[] proposed={
            bundle.ClusterJson,bundle.StoreJson,bundle.LockJson,
            bundle.LaunchJson,bundle.LocalDonePeerJson
        };
        if(proposed.Any(x=>string.IsNullOrWhiteSpace(x)||x.Length>1_048_576)||
           Digest(sourceImages)!=bundle.OriginalSourceHash||
           Digest(proposed)!=bundle.NewSourceHash||
           bundle.OriginalSourceHash==bundle.NewSourceHash)
            throw new InvalidDataException("UM-FUTURE-ESCROW-PREVIEW-CHANGED");
        foreach(var json in sourceImages.Concat(proposed))
        {
            using var doc=JsonDocument.Parse(json);
            if(doc.RootElement.ValueKind!=JsonValueKind.Object)
                throw new InvalidDataException("UM-FUTURE-ESCROW-JSON-INVALID");
        }
        FutureStoreAppendInvariant.Verify(sourceImages,proposed,mapId);
        using var planned=JsonDocument.Parse(bundle.ClusterJson);
        var nextMembers=planned.RootElement.GetProperty("members");
        if(nextMembers.ValueKind!=JsonValueKind.Array||
           nextMembers.GetArrayLength()<6||
           nextMembers.EnumerateArray().Last().GetProperty("id").GetString()!=
               mapId.ToString("D"))
            throw new InvalidDataException("UM-FUTURE-ESCROW-NEW-MAP-IDENTITY");
        var journal=FutureStoreConversionJournal.Inspect(
            managerProfileRoot,mapId);
        if(journal==null||journal.OperationId!=operationId||
           journal.Phase!="conversionVerified"||
           journal.ClusterId!=VaultProtocol.ClusterId||
           journal.OriginalSourceSha256!=bundle.OriginalSourceHash)
            throw new InvalidOperationException("UM-FUTURE-ESCROW-CONVERSION-NOT-VERIFIED");
        string target=Destination(managerProfileRoot,mapId,operationId);
        string staging=target+".incomplete";
        if(Directory.Exists(target)||Directory.Exists(staging)||
           File.Exists(target)||File.Exists(staging))
            throw new IOException("UM-FUTURE-ESCROW-ALREADY-EXISTS");
        string[] beforeHashes=sourceImages.Select(x=>Sha(Encoding.UTF8.GetBytes(x))).ToArray();
        string[] afterHashes=proposed.Select(x=>Sha(Encoding.UTF8.GetBytes(x))).ToArray();
        var saved=new Manifest(1,mapId,operationId,bundle.OriginalSourceHash,
            bundle.NewSourceHash,beforeHashes,afterHashes,DateTimeOffset.UtcNow);
        // A leftover .incomplete directory is an operator-visible crash
        // marker. Never clean it up as part of an automatic retry.
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(Path.Combine(staging,"before"));
        Directory.CreateDirectory(Path.Combine(staging,"after"));
        for(int i=0;i<Names.Length;i++)
        {
            Durable(Path.Combine(staging,"before",Names[i]),sourceImages[i]);
            Durable(Path.Combine(staging,"after",Names[i]),proposed[i]);
        }
        Durable(Path.Combine(staging,"manifest.json"),
            JsonSerializer.Serialize(saved,Json));
        // Read back and verify every file BEFORE publication. A crash after
        // this rename but before the local journal advances remains blocked.
        Verify(staging,mapId,operationId);
        Directory.Move(staging,target);
        Verify(target,mapId,operationId);
        FutureStoreConversionJournal.Advance(managerProfileRoot,mapId,
            operationId,"conversionVerified","appendStaged",bundle.NewSourceHash);
        return new(mapId,operationId,target,saved.BeforeSha256,saved.AfterSha256,
            beforeHashes,afterHashes,saved.CreatedUtc);
    }

    private static void Durable(string path,string content)
    {
        var bytes=Encoding.UTF8.GetBytes(content);
        using var stream=new FileStream(path,FileMode.CreateNew,
            FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }

    public static FutureStoreStagedAppend Inspect(string managerProfileRoot,
        Guid mapId,Guid operationId)
    {
        string location=Destination(managerProfileRoot,mapId,operationId);
        if(Directory.Exists(location+".incomplete"))
            throw new IOException("UM-FUTURE-ESCROW-PARTIAL-STAGING");
        return Verify(location,mapId,operationId);
    }

    private static FutureStoreStagedAppend Verify(string location,Guid id,Guid operation)
    {
        if(!Directory.Exists(location)||
           (File.GetAttributes(location)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-FUTURE-ESCROW-NOT-FOUND-OR-LINK");
        string manifestPath=Path.Combine(location,"manifest.json");
        if((File.GetAttributes(manifestPath)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-FUTURE-ESCROW-LINK");
        var raw=File.ReadAllBytes(manifestPath);
        if(raw.Length is <20 or >16_384)
            throw new InvalidDataException("UM-FUTURE-ESCROW-MANIFEST-SIZE");
        var manifest=JsonSerializer.Deserialize<Manifest>(raw,Json) ??
            throw new InvalidDataException("UM-FUTURE-ESCROW-MANIFEST-MISSING");
        if(manifest.Schema!=1||manifest.MapId!=id||
           manifest.OperationId!=operation||!Hex(manifest.BeforeSha256)||
           !Hex(manifest.AfterSha256)||manifest.BeforeHashes?.Length!=Names.Length||
           manifest.AfterHashes?.Length!=Names.Length||
           manifest.BeforeHashes.Any(x=>!Hex(x))||
           manifest.AfterHashes.Any(x=>!Hex(x))||
           manifest.CreatedUtc==default)
            throw new InvalidDataException("UM-FUTURE-ESCROW-MANIFEST-INVALID");
        var arrays=new string[2][];
        for(int side=0;side<2;side++)
        {
            string folder=Path.Combine(location,side==0?"before":"after");
            if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-FUTURE-ESCROW-FOLDER-LINK");
            if(Directory.EnumerateFileSystemEntries(folder).Count()!=Names.Length)
                throw new InvalidDataException("UM-FUTURE-ESCROW-FILE-COUNT");
            var content=new string[Names.Length];
            for(int i=0;i<Names.Length;i++)
            {
                string file=Path.Combine(folder,Names[i]);
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("UM-FUTURE-ESCROW-FILE-LINK");
                var bytes=File.ReadAllBytes(file);
                if(bytes.Length is <2 or >1_048_576||
                   Sha(bytes)!=(side==0?manifest.BeforeHashes[i]:manifest.AfterHashes[i]))
                    throw new InvalidDataException("UM-FUTURE-ESCROW-IMAGE-TAMPERED");
                content[i]=Encoding.UTF8.GetString(bytes);
                using var doc=JsonDocument.Parse(content[i]);
                if(doc.RootElement.ValueKind!=JsonValueKind.Object)
                    throw new InvalidDataException("UM-FUTURE-ESCROW-IMAGE-INVALID");
            }
            arrays[side]=content;
        }
        if(Digest(arrays[0])!=manifest.BeforeSha256||
           Digest(arrays[1])!=manifest.AfterSha256)
            throw new InvalidDataException("UM-FUTURE-ESCROW-PLAN-TAMPERED");
        return new(id,operation,location,manifest.BeforeSha256,
            manifest.AfterSha256,manifest.BeforeHashes,manifest.AfterHashes,
            manifest.CreatedUtc);
    }
}
