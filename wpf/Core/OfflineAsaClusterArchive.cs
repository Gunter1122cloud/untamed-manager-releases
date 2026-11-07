using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

// Offline-only ASA/Store archive: 5 + 5 independently VERIFIED stopped-map
// packages and an operator-created LOCAL COPY of the original shared transfer
// root. There is no SQL/database, no remote operation, no Kova Store writer,
// and no claim of a consistent live 10-map snapshot.
public sealed record AsaOfflineMapInput(string Host,Guid Map,string Name,string Package);
public sealed record AsaOfflineMap(string Host,Guid Map,string Name,
    string PackageRelativePath,int Files,long Bytes,string ManifestSha256);
public sealed record AsaOfflineTransfer(string RelativePath,long Bytes,string Sha256);
public sealed record AsaOfflineConfig(string Host,Guid Map,string RelativePath,
    long Bytes,string Sha256);
public sealed record AsaOfflinePackage(int Schema,Guid Batch,DateTimeOffset CreatedUtc,
    AsaOfflineMap[] Maps,AsaOfflineTransfer[] Transfer,
    bool LiveConsistencyCertified,bool LiveStoreWriterCertified,
    bool RestoreToProductionAllowed,string Evidence,
    AsaOfflineConfig[]? Config=null);

public static class OfflineAsaClusterArchive
{
    private const string Index="asa-cluster-index.json";
    private const string HashFile="asa-cluster-index.sha256";
    private const string Completed="complete";
    private const string TransferFolder="TRANSFER";
    private const string ConfigurationFolder="CONFIG";
    private const long MaxConfigBytes=256L*1024*1024;
    private const int MaxConfigFiles=3000;
    private const string Evidence="OFFLINE_TEN_MAP_FILE_INTEGRITY_PLUS_COPIED_CLUSTER_DATA_ONLY";
    private const int MaxFiles=50000;

    private static string Fixed(string input)
    {
        if(string.IsNullOrWhiteSpace(input)||!Path.IsPathFullyQualified(input)||
           input.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("Explicit local fixed-disk archive path required");
        string path=Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(path)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Offline archive needs local fixed disk");
        for(var dir=new DirectoryInfo(path);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked archive parent rejected");
        return path;
    }
    private static bool Within(string root,string child)=>
        child.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    private static string Inside(string root,string relative)
    {
        if(string.IsNullOrWhiteSpace(relative)||Path.IsPathFullyQualified(relative)||
           relative.Split(new[]{'\\','/'},StringSplitOptions.None)
               .Any(s=>s.Length==0||s is "." or ".."||s.EndsWith('.')||
                   s.EndsWith(' ')||s.Contains(':')))
            throw new InvalidDataException("Archive relative path is unsafe");
        string file=Path.GetFullPath(Path.Combine(root,relative));
        if(!Within(root,file))throw new InvalidDataException("Archive path escape");
        return file;
    }
    private static string Sha(string file)
    {
        if(!File.Exists(file)||(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Missing or linked archive file");
        using var data=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(data));
    }
    private static void DurableText(string file,string text)
    {
        using var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false),1024,true);
        writer.Write(text);writer.Flush();stream.Flush(true);
    }
    private static void CopyChecked(string source,string target,long size,string expectedSha)
    {
        if((File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked archive source rejected");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using(var from=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var to=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            if(from.Length!=size)throw new IOException("Archive source changed before copy");
            from.CopyTo(to,1024*1024);to.Flush(true);
        }
        if(new FileInfo(target).Length!=size ||
           !Sha(target).Equals(expectedSha,StringComparison.OrdinalIgnoreCase) ||
           !Sha(source).Equals(expectedSha,StringComparison.OrdinalIgnoreCase))
            throw new IOException("Archive copy hash mismatch or unstable input");
    }
    private static AsaOfflineTransfer[] InventoryTransfer(string folder)
    {
        var files=new List<AsaOfflineTransfer>();
        var queue=new Stack<string>();queue.Push(folder);
        while(queue.Count>0)
        {
            string current=queue.Pop();
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked transfer directory rejected");
            foreach(var child in Directory.GetDirectories(current))queue.Push(child);
            foreach(var file in Directory.GetFiles(current))
            {
                if(files.Count>=MaxFiles)throw new InvalidDataException("Transfer file count limit");
                var name=Path.GetRelativePath(folder,file);
                Inside(folder,name);
                var info=new FileInfo(file);
                if((info.Attributes&FileAttributes.ReparsePoint)!=0 ||
                   info.Length>100L*1024*1024*1024)
                    throw new InvalidDataException("Unsafe transfer file");
                files.Add(new(name,info.Length,Sha(file)));
            }
        }
        var result=files.OrderBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).ToArray();
        if(result.Select(f=>f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=result.Length)
            throw new InvalidDataException("Duplicate transfer paths");
        // This file is the original Store-v1 coordination identity, not a new
        // session. Its presence alone does NOT certify that no ASA map was writing.
        if(!result.Any(f=>f.RelativePath.Replace('\\','/').Equals(
               ".kova-store-v1/operation.json",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Offline copy lacks original Store operation metadata");
        return result;
    }

    // Optional full server-configuration copies, prepared on a separate test
    // computer. Source layout: HP/CONFIG/<map-guid>/WindowsServer/*.ini,
    // similarly on RYZEN. Incomplete or one-sided copies fail closed.
    private static AsaOfflineConfig[] InventoryConfig(string source,string host,
        AsaOfflineMapInput[] ownedMaps)
    {
        string root=Path.Combine(source,ConfigurationFolder);
        if(!Directory.Exists(root))return Array.Empty<AsaOfflineConfig>();
        Fixed(root);
        var dirs=Directory.GetDirectories(root);
        if(dirs.Length!=ownedMaps.Length ||
           dirs.Any(dir=>!ownedMaps.Any(m=>Path.GetFileName(dir)
               .Equals(m.Map.ToString("N"),StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Configuration map ownership mismatch");
        var result=new List<AsaOfflineConfig>();
        long bytesTotal=0;
        foreach(var map in ownedMaps)
        {
            string mapFolder=Fixed(Path.Combine(root,map.Map.ToString("N")));
            if(!Directory.Exists(mapFolder))
                throw new InvalidDataException("Configuration map copy missing");
            string windows=Path.Combine(mapFolder,"WindowsServer");
            if(!Directory.Exists(windows) ||
               !File.Exists(Path.Combine(windows,"Game.ini")) ||
               !File.Exists(Path.Combine(windows,"GameUserSettings.ini")))
                throw new InvalidDataException("Configuration pair incomplete");
            var stack=new Stack<string>();stack.Push(mapFolder);
            while(stack.Count>0)
            {
                string current=stack.Pop();
                Fixed(current);
                foreach(string dir in Directory.GetDirectories(current))
                    stack.Push(Fixed(dir));
                foreach(string file in Directory.GetFiles(current))
                {
                    if(result.Count>=MaxConfigFiles)
                        throw new InvalidDataException("Too many configuration files");
                    if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                        throw new InvalidDataException("Linked configuration file");
                    string relative=Path.GetRelativePath(mapFolder,file);
                    Inside(mapFolder,relative);
                    var info=new FileInfo(file);
                    if(info.Length>16L*1024*1024 ||
                       (bytesTotal=checked(bytesTotal+info.Length))>MaxConfigBytes)
                        throw new InvalidDataException("Configuration copy too large");
                    result.Add(new(host,map.Map,
                        Path.Combine(host,map.Map.ToString("N"),relative),
                        info.Length,Sha(file)));
                }
            }
        }
        return result.OrderBy(x=>x.RelativePath,StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (AsaOfflineMapInput[] Maps,string[] Roots) ScanMaps(
        string hpRoot,string ryzenRoot)
    {
        var inputs=new List<AsaOfflineMapInput>();
        foreach(var (source,host) in new[]{(hpRoot,"HP"),(ryzenRoot,"RYZEN")})
        {
            var dirs=Directory.GetDirectories(source)
                .Where(p=>!Path.GetFileName(p).Equals(ConfigurationFolder,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if(dirs.Length!=5)throw new InvalidDataException(host+" requires five offline map packages");
            foreach(var directory in dirs)
            {
                string root=Fixed(directory);
                if(!Within(source,root))throw new InvalidDataException("Unsafe map package root");
                string manifestFile=Inside(root,"manifest.json");
                var manifest=JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestFile))
                    ??throw new InvalidDataException("Map manifest missing");
                if(manifest.MapId==Guid.Empty)throw new InvalidDataException("Missing map owner");
                TestBackupPackage.Verify(root,manifest.MapId);
                inputs.Add(new(host,manifest.MapId,Path.GetFileName(root),root));
            }
        }
        if(inputs.Select(x=>x.Map).Distinct().Count()!=10 ||
           inputs.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=10)
            throw new InvalidDataException("Duplicate offline map identity");
        return(inputs.ToArray(),inputs.Select(x=>x.Package).ToArray());
    }
    public static AsaOfflinePackage BuildFromFolders(
        string hpCopies,string ryzenCopies,string transferCopy,string destination)
    {
        string hp=Fixed(hpCopies),ryzen=Fixed(ryzenCopies),
            transfer=Fixed(transferCopy),target=Fixed(destination);
        if(new[]{hp,ryzen,transfer,target}.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=4 ||
           new[]{hp,ryzen,transfer}.Any(x=>Within(x,target)||Within(target,x)) ||
           !Directory.Exists(hp)||!Directory.Exists(ryzen)||!Directory.Exists(transfer))
            throw new InvalidDataException("Offline sources missing or overlap archive destination");
        var (maps,roots)=ScanMaps(hp,ryzen);
        if(roots.Any(x=>Within(x,transfer)||Within(transfer,x)))
            throw new InvalidDataException("Transfer source overlaps map package");
        bool hpHasConfig=Directory.Exists(Path.Combine(hp,ConfigurationFolder));
        bool ryzenHasConfig=Directory.Exists(Path.Combine(ryzen,ConfigurationFolder));
        if(hpHasConfig!=ryzenHasConfig)
            throw new InvalidDataException("Both hosts require copied configurations");
        var configs=InventoryConfig(hp,"HP",maps.Where(m=>m.Host=="HP").ToArray())
            .Concat(InventoryConfig(ryzen,"RYZEN",maps.Where(m=>m.Host=="RYZEN").ToArray()))
            .OrderBy(x=>x.RelativePath,StringComparer.OrdinalIgnoreCase).ToArray();
        var transfers=InventoryTransfer(transfer);
        long size=transfers.Aggregate(0L,(sum,item)=>checked(sum+item.Bytes));
        size=configs.Aggregate(size,(sum,item)=>checked(sum+item.Bytes));
        foreach(var map in maps)
        {
            var verified=TestBackupPackage.Verify(map.Package,map.Map);
            size=checked(size+verified.Files.Sum(x=>x.Size));
        }
        long free=new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
        long reserve=Math.Max(64L*1024*1024,size/10);
        if(size>free||reserve>free-size)
            throw new IOException("Not enough free space for offline cluster archive");
        string stage=target+".incomplete";
        Fixed(stage);
        if(Directory.Exists(target)||Directory.Exists(stage)||
           File.Exists(target)||File.Exists(stage))
            throw new IOException("Existing archive or interrupted stage must be reviewed");
        var entries=new List<AsaOfflineMap>();
        Directory.CreateDirectory(stage);
        foreach(var map in maps)
        {
            var original=TestBackupPackage.Verify(map.Package,map.Map);
            string rel=Path.Combine(map.Host,map.Map.ToString("N"));
            string dest=Inside(stage,rel);
            Directory.CreateDirectory(Inside(dest,"data"));
            foreach(var file in original.Files)
            {
                string input=Inside(Path.Combine(map.Package,"data"),file.Path);
                CopyChecked(input,Inside(Path.Combine(dest,"data"),file.Path),file.Size,file.Sha256);
            }
            string originalIndex=Inside(map.Package,"manifest.json");
            CopyChecked(originalIndex,Inside(dest,"manifest.json"),
                new FileInfo(originalIndex).Length,Sha(originalIndex));
            DurableText(Inside(dest,"complete"),"1");
            var copied=TestBackupPackage.Verify(dest,map.Map);
            if(copied.Files.Count!=original.Files.Count ||
               !copied.Files.SequenceEqual(original.Files))
                throw new InvalidDataException("Copied map manifest differs");
            entries.Add(new(map.Host,map.Map,map.Name,rel,copied.Files.Count,
                copied.Files.Sum(x=>x.Size),Sha(Inside(dest,"manifest.json"))));
        }
        var copiedTransfer=Inside(stage,TransferFolder);
        Directory.CreateDirectory(copiedTransfer);
        foreach(var entry in transfers)
            CopyChecked(Inside(transfer,entry.RelativePath),
                Inside(copiedTransfer,entry.RelativePath),entry.Bytes,entry.Sha256);
        if(!InventoryTransfer(transfer).SequenceEqual(transfers))
            throw new IOException("Transfer source membership or contents changed during copy");
        if(configs.Length>0)
        {
            foreach(var entry in configs)
            {
                string ownerRoot=entry.Host=="HP"?hp:ryzen;
                string prefix=entry.Host+Path.DirectorySeparatorChar;
                if(!entry.RelativePath.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Configuration owner prefix mismatch");
                string from=Inside(Path.Combine(ownerRoot,ConfigurationFolder),
                    entry.RelativePath[prefix.Length..]);
                string to=Inside(Path.Combine(stage,ConfigurationFolder),
                    entry.RelativePath);
                CopyChecked(from,to,entry.Bytes,entry.Sha256);
            }
            var copiedConfigs=InventoryConfig(hp,"HP",maps.Where(m=>m.Host=="HP").ToArray())
                .Concat(InventoryConfig(ryzen,"RYZEN",maps.Where(m=>m.Host=="RYZEN").ToArray()))
                .OrderBy(x=>x.RelativePath,StringComparer.OrdinalIgnoreCase).ToArray();
            if(!copiedConfigs.SequenceEqual(configs))
                throw new IOException("Configuration source changed during archive creation");
        }
        var report=new AsaOfflinePackage(configs.Length>0?3:2,Guid.NewGuid(),
            DateTimeOffset.UtcNow,entries.ToArray(),transfers,
            false,false,false,Evidence,configs.Length>0?configs:null);
        string index=Inside(stage,Index);
        DurableText(index,JsonSerializer.Serialize(report));
        DurableText(Inside(stage,HashFile),Sha(index));
        DurableText(Inside(stage,Completed),report.Batch.ToString("N"));
        VerifyCore(stage,true);
        Directory.Move(stage,target);
        return Verify(target);
    }

    public static AsaOfflinePackage Verify(string package)=>VerifyCore(Fixed(package),false);
    private static AsaOfflinePackage VerifyCore(string root,bool internalStage)
    {
        if(!internalStage&&root.EndsWith(".incomplete",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Incomplete archive cannot be trusted");
        if(!Directory.Exists(root)||!File.Exists(Inside(root,Index))||
           !File.Exists(Inside(root,HashFile))||!File.Exists(Inside(root,Completed)) ||
           !Sha(Inside(root,Index)).Equals(File.ReadAllText(Inside(root,HashFile)).Trim(),
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Offline archive incomplete or index modified");
        var manifest=JsonSerializer.Deserialize<AsaOfflinePackage>(
            File.ReadAllText(Inside(root,Index)))
            ??throw new InvalidDataException("Missing ASA archive manifest");
        if(manifest.Schema is not (2 or 3) || manifest.Batch==Guid.Empty||
           (manifest.Schema==2 && manifest.Config is {Length:>0}) ||
           (manifest.Schema==3 && manifest.Config is not {Length:>0}) ||
           manifest.LiveConsistencyCertified||manifest.LiveStoreWriterCertified||
           manifest.RestoreToProductionAllowed||manifest.Evidence!=Evidence||
           manifest.Maps==null||manifest.Maps.Length!=10||
           manifest.Transfer==null||manifest.Transfer.Length<1||
           manifest.Maps.Count(m=>m.Host=="HP")!=5||
           manifest.Maps.Count(m=>m.Host=="RYZEN")!=5||
           manifest.Maps.Select(m=>m.Map).Distinct().Count()!=10 ||
           manifest.Maps.Select(m=>m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=10)
            throw new InvalidDataException("ASA-only archive scope/identity invalid");
        if(File.ReadAllText(Inside(root,Completed)).Trim()!=manifest.Batch.ToString("N"))
            throw new InvalidDataException("Archive completion marker invalid");
        if(Directory.GetFiles(root).Length!=3 ||
           Directory.GetDirectories(root).Length!=(manifest.Schema==3?4:3))
            throw new InvalidDataException("Offline archive contains unexpected root members");
        foreach(var host in new[]{"HP","RYZEN"})
            if(Directory.GetDirectories(Inside(root,host)).Length!=5 ||
               Directory.GetFiles(Inside(root,host)).Length!=0)
                throw new InvalidDataException("Offline archive host inventory invalid");
        foreach(var map in manifest.Maps)
        {
            if(map.Map==Guid.Empty || map.Files<=0||map.Bytes<=0 ||
               map.PackageRelativePath!=Path.Combine(map.Host,map.Map.ToString("N")))
                throw new InvalidDataException("ASA package map record invalid");
            string path=Inside(root,map.PackageRelativePath);
            if(Sha(Inside(path,"manifest.json"))!=map.ManifestSha256)
                throw new InvalidDataException("ASA map index was modified");
            var verified=TestBackupPackage.Verify(path,map.Map);
            if(verified.Files.Count!=map.Files||verified.Files.Sum(x=>x.Size)!=map.Bytes)
                throw new InvalidDataException("Map file list or bytes changed");
        }

        if(manifest.Schema==3)
        {
            string cfg=Inside(root,ConfigurationFolder);
            if(!Directory.Exists(cfg) || manifest.Config==null ||
               manifest.Config.Length>MaxConfigFiles ||
               manifest.Config.Select(x=>x.RelativePath)
                   .Distinct(StringComparer.OrdinalIgnoreCase).Count()!=manifest.Config.Length)
                throw new InvalidDataException("Configuration archive index invalid");
            var expected=manifest.Config.OrderBy(x=>x.RelativePath,
                StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var entry in expected)
            {
                if(entry.Host is not ("HP" or "RYZEN") || entry.Map==Guid.Empty ||
                   !manifest.Maps.Any(m=>m.Host==entry.Host&&m.Map==entry.Map) ||
                   !entry.RelativePath.StartsWith(entry.Host+Path.DirectorySeparatorChar+
                       entry.Map.ToString("N")+Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase) ||
                   entry.Bytes<0||entry.Sha256.Length!=64||
                   !entry.Sha256.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Configuration archive owner invalid");
                string source=Inside(cfg,entry.RelativePath);
                Fixed(Path.GetDirectoryName(source)!);
                if(new FileInfo(source).Length!=entry.Bytes ||
                   !Sha(source).Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Configuration archive hash mismatch");
            }
            var actual=Directory.GetFiles(cfg,"*",SearchOption.AllDirectories)
                .Select(p=>Path.GetRelativePath(cfg,p))
                .OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
            if(!actual.SequenceEqual(expected.Select(x=>x.RelativePath),
                   StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Configuration archive member changed");
            foreach(var map in manifest.Maps)
                foreach(string essential in new[]{"Game.ini","GameUserSettings.ini"})
                    if(!expected.Any(c=>c.Host==map.Host&&c.Map==map.Map&&
                        c.RelativePath.EndsWith(Path.Combine("WindowsServer",essential),
                            StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Mandatory configuration file missing");
        }
        string copied=Inside(root,TransferFolder);
        var actual=InventoryTransfer(copied);
        if(!actual.SequenceEqual(manifest.Transfer))
            throw new InvalidDataException("Transfer files added, missing or modified");
        return manifest;
    }

    // Deliberately no RestoreToProduction: a file-integrity snapshot cannot
    // establish an atomic Store-v1 timeline or character travel correctness.
    // This verifies the package only; the original 10 map worlds and transfer
    // files remain in an immutable, auditable offline package.
}
