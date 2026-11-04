using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

public sealed record ApiInstallReceipt(Guid Map,string Installation,string Transaction,
    string ReleaseSha256,string Tag,bool FilesVerified,bool RuntimeVerified=false);

// Real file replacement and recoverable full-tree snapshot, restricted to an
// independently created TEMP installation. Never launches a loader or server.
public static class AsaApiIsolatedInstaller
{
    private static readonly string[] Required={"AsaApiLoader.exe","ArkApi/AsaApi.dll"};
    private sealed record Binding(int Schema,Guid Map,string Installation,string BinaryRoot);
    private static string Local(string path)
    {
        UntamedOperationCoordinator.ResourceKey(path);
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked API file refused");
        return full;
    }
    private static string Installation(LocalMap map)
    {
        string install=Local(map.InstallationPath),temp=Local(Path.GetTempPath());
        if(!install.StartsWith(temp+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("API installation requires an isolated TEMP lab");
        string relative=Path.GetRelativePath(temp,install);
        string[] parts=relative.Split(Path.DirectorySeparatorChar);
        if(parts.Length!=2||!parts[0].StartsWith("Untamed-ApiInstallLab-",StringComparison.Ordinal)||
           parts[1]!="installation"||!Directory.Exists(install))
            throw new UnauthorizedAccessException("Unrecognized isolated API installation");
        return install;
    }
    private static LocalMap Owned(ClusterService cluster,Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(x=>x.Id==id&&id!=Guid.Empty)??
        throw new UnauthorizedAccessException("API operation requires a registered local map");
    private static void Stopped(ClusterService cluster,string installation)
    {
        var maps=cluster.Config.LocalMaps.Where(x=>string.Equals(Local(x.InstallationPath),
            installation,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(maps.Length==0)throw new UnauthorizedAccessException("No local API update participants");
        foreach(var map in maps)
        {
            var check=cluster.Check(map);
            if(map.Maintenance||check.Running||!check.ConfirmedStopped||
               check.Identity!=null||check.LoaderIdentity!=null)
                throw new InvalidOperationException("Every map sharing the installation must be confirmed stopped");
        }
    }
    private static void Write(string path,object value)
    {
        using var fs=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(fs,value);fs.Flush(true);
    }
    private static string Hash(Stream stream)=>Convert.ToHexString(SHA256.HashData(stream));

    public static ApiInstallReceipt Install(ClusterService cluster,Guid mapId,string archive,
        AsaApiReleaseInfo release,AsaApiReleaseAsset asset,CancellationToken ct=default)=>
        InstallCore(cluster,mapId,archive,release,asset,ct,null);

    internal static ApiInstallReceipt InstallCore(ClusterService cluster,Guid mapId,string archive,
        AsaApiReleaseInfo release,AsaApiReleaseAsset asset,CancellationToken ct,Action<int>? afterReplace)
    {
        ct.ThrowIfCancellationRequested();
        var map=Owned(cluster,mapId);string install=Installation(map);
        string binary=Local(Path.Combine(install,"ShooterGame","Binaries","Win64"));
        if(!Directory.Exists(binary))throw new InvalidDataException("Existing API binary directory required");
        var proof=AsaApiReleaseStaging.VerifyAgainstPublisher(archive,release,asset);
        using var operation=cluster.Operations.Acquire(install,UntamedOperationKind.Update);
        Stopped(cluster,install);
        foreach(string proxy in new[]{"version.dll","winmm.dll","dinput8.dll","dsound.dll"})
            if(File.Exists(Path.Combine(binary,proxy)))
                throw new InvalidDataException("Unverified proxy loader conflict");
        // Hash and extract from the SAME held ZIP, preventing replacement after validation.
        using var input=new FileStream(Local(Path.Combine(archive,"release.zip")),FileMode.Open,
            FileAccess.Read,FileShare.Read);
        if(Hash(input)!=proof.OfficialSha256)throw new InvalidDataException("API ZIP changed before install");
        input.Position=0;
        using var zip=new ZipArchive(input,ZipArchiveMode.Read,true);
        var entries=zip.Entries.Where(x=>!x.FullName.EndsWith('/')&&
            (x.FullName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||
             x.FullName.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))).ToArray();
        if(entries.Length!=Required.Length||Required.Any(name=>entries.Count(e=>e.FullName==name)!=1))
            throw new InvalidDataException("Unsupported API binary layout or dependency; no partial install allowed");
        string transaction=Local(Path.Combine(Path.GetDirectoryName(install)!,
            "transactions",Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(transaction);
        Write(Path.Combine(transaction,"binding.json"),new Binding(1,map.Id,install,binary));
        string payload=Path.Combine(transaction,"payload");Directory.CreateDirectory(payload);
        var expected=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in entries)
        {
            ct.ThrowIfCancellationRequested();string to=Local(Path.Combine(payload,entry.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using(var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var source=entry.Open()){source.CopyTo(output);output.Flush(true);
                if(output.Length!=entry.Length)throw new InvalidDataException("Truncated API binary");}
            if(!AsaApiInspector.IsPe64(to))throw new InvalidDataException("API binary must be AMD64 PE32+");
            using var stream=File.OpenRead(to);expected.Add(entry.FullName,Hash(stream));
            Local(Path.Combine(binary,entry.FullName));
        }
        var snapshot=new DirectoryUpdateTransaction(Path.Combine(transaction,"rollback"));
        snapshot.Prepare(binary);
        ct.ThrowIfCancellationRequested();Stopped(cluster,install);
        operation.MarkMutationStarted();
        try
        {
            int replaced=0;
            foreach(string name in Required)
            {
                ct.ThrowIfCancellationRequested();Stopped(cluster,install);
                string target=Local(Path.Combine(binary,name));Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temporary=target+".untamed-"+Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(Path.Combine(payload,name),temporary,false);
                    using(var file=new FileStream(temporary,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                    {file.Flush(true);if(Hash(file)!=expected[name])throw new InvalidDataException("API payload changed");}
                    File.Move(temporary,target,true);
                }
                finally{if(File.Exists(temporary))File.Delete(temporary);}
                afterReplace?.Invoke(++replaced);
            }
            Stopped(cluster,install);
            foreach(string name in Required)
            {using var file=File.OpenRead(Local(Path.Combine(binary,name)));
                if(Hash(file)!=expected[name])throw new InvalidDataException("Installed API binary differs");}
            var receipt=new ApiInstallReceipt(map.Id,install,transaction,proof.OfficialSha256,proof.Tag,true);
            Write(Path.Combine(transaction,"receipt.json"),receipt);
            snapshot.Commit();operation.Complete();return receipt;
        }
        catch
        {
            // Never replace a directory beneath a newly running/uncertain server.
            // Its durable snapshot and operation journal remain for explicit recovery.
            Stopped(cluster,install);snapshot.Rollback();throw;
        }
    }

    public static void RecoverSnapshot(ClusterService cluster,Guid mapId,string transaction)
    {
        var map=Owned(cluster,mapId);string install=Installation(map);
        string allowed=Local(Path.Combine(Path.GetDirectoryName(install)!,"transactions"));
        string root=Local(transaction);
        if(!string.Equals(Path.GetDirectoryName(root),allowed,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("API recovery transaction outside this lab");
        string binding=Local(Path.Combine(root,"binding.json"));
        if(new FileInfo(binding).Length>4096)throw new InvalidDataException("Oversized API binding");
        var b=JsonSerializer.Deserialize<Binding>(File.ReadAllText(binding));
        string binary=Local(Path.Combine(install,"ShooterGame","Binaries","Win64"));
        if(b==null||b.Schema!=1||b.Map!=mapId||b.Installation!=install||b.BinaryRoot!=binary)
            throw new InvalidDataException("API recovery ownership mismatch");
        string rollback=Local(Path.Combine(root,"rollback"));
        var state=JsonSerializer.Deserialize<DirectoryUpdateTransaction.State>(
            File.ReadAllText(Local(Path.Combine(rollback,"update-transaction.json"))));
        if(state==null||state.Application!=binary||state.Phase is not ("prepared" or "rollingback"))
            throw new InvalidDataException("API snapshot is not pending recovery");
        // Use the same global installation semaphore, but a distinct durable
        // recovery record: never clear or overwrite the interrupted update record.
        using var lease=new UntamedOperationCoordinator(Path.Combine(root,"recovery-operations"))
            .Acquire(install,UntamedOperationKind.Restore);
        Stopped(cluster,install);lease.MarkMutationStarted();
        new DirectoryUpdateTransaction(rollback).Rollback();lease.Complete();
    }
}
