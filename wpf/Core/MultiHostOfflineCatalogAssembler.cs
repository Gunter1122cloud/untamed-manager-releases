using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Constructs the EXISTING v1 multi-host archive catalog from already stopped,
/// physically copied map/Store/transfer folders. Nothing is fetched from a
/// running ASA host; no alternative archive format or writer is introduced.
/// All source payload files are inventoried, not just world .ark files.
/// </summary>
public static class MultiHostOfflineCatalogAssembler
{
    public const string CatalogName = "multi-host-backup-catalog.json";
    public sealed record Preview(MultiHostBackupCatalog Catalog,string Fingerprint,
        int FileCount,long Bytes);
    private static readonly JsonSerializerOptions Json = new();

    private static string CanonicalRoot(string root)
    {
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\",StringComparison.Ordinal)||
           root.StartsWith("//",StringComparison.Ordinal)||
           root.StartsWith(@"\\?\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-LOCAL-ONLY");
        string full=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var drive=Path.GetPathRoot(full);
        if(string.IsNullOrEmpty(drive)||new DriveInfo(drive).DriveType!=DriveType.Fixed||
           !Directory.Exists(full))
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-OFFLINE-ROOT");
        for(DirectoryInfo? d=new(full);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-LINKED-ROOT");
        return full;
    }
    private static string SafeRelative(string relative)
    {
        string rel=relative.Replace('\\','/');
        if(string.IsNullOrWhiteSpace(rel)||rel.Length>512||
           rel.Split('/').Any(x=>string.IsNullOrWhiteSpace(x)||x is "." or ".."||
               x.EndsWith('.')||x.EndsWith(' ')||x.Contains('~')||
               x.Contains(':')||x.Any(char.IsControl)))
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-RELATIVE-INVALID");
        return rel;
    }
    private static string[] Files(string path)
    {
        if(!Directory.Exists(path))
            throw new DirectoryNotFoundException("UM-MULTIHOST-AUTOCATALOG-MISSING-SCOPE");
        var result=new List<string>();
        var pending=new Stack<string>();
        pending.Push(path);
        while(pending.Count!=0)
        {
            string directory=pending.Pop();
            if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-LINKED-DIRECTORY");
            foreach(string child in Directory.GetDirectories(directory))
                pending.Push(child);
            foreach(string file in Directory.GetFiles(directory))
            {
                var info=new FileInfo(file);
                if((info.Attributes&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-LINKED-FILE");
                if(info.Length>MultiHostBackupArchiveV1SafetyLimits.MaxComponentBytes)
                    throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-OVERSIZED-FILE");
                result.Add(file);
                if(result.Count>MultiHostBackupArchiveV1SafetyLimits.MaxComponentsPerArchive)
                    throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-TOO-MANY-FILES");
            }
        }
        return result.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static void ExactDirectoryMembers(string path,IEnumerable<string> expectedDirs,
        string? permittedFile=null)
    {
        if(!Directory.Exists(path))
            throw new DirectoryNotFoundException("UM-MULTIHOST-AUTOCATALOG-MISSING-ROOT");
        var expected=expectedDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual=Directory.GetDirectories(path).Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!actual.SetEquals(expected)||
           Directory.GetFiles(path).Any(f=>permittedFile==null||
               !string.Equals(Path.GetFileName(f),permittedFile,StringComparison.Ordinal)))
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-UNEXPECTED-SOURCE");
    }
    private static void AddFiles(List<MultiHostBackupCatalogItem> items,string root,
        string scope,string host,Guid? mapId,string? cluster,
        Func<string,MultiHostBackupComponentKind> choose)
    {
        string directory=Path.Combine(root,scope.Replace('/',Path.DirectorySeparatorChar));
        foreach(string file in Files(directory))
        {
            string rel=SafeRelative(Path.GetRelativePath(directory,file));
            items.Add(new(host,mapId,cluster,choose(rel),rel,
                SafeRelative(scope+"/"+rel)));
        }
    }
    public static Preview Inspect(string sourceRoot,MultiHostBackupOwnershipSnapshot ownership,
        IReadOnlyCollection<string>? forbiddenRoots=null)
    {
        string root=CanonicalRoot(sourceRoot);
        if(ownership?.Maps==null||ownership.Hosts==null||ownership.Clusters==null||
           string.IsNullOrWhiteSpace(ownership.LocalHostId))
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-REGISTRY-MISSING");
        foreach(var blocked in forbiddenRoots??Array.Empty<string>())
        {
            if(string.IsNullOrWhiteSpace(blocked))continue;
            string path=Path.GetFullPath(blocked).TrimEnd(Path.DirectorySeparatorChar);
            if(root.Equals(path,StringComparison.OrdinalIgnoreCase)||
               root.StartsWith(path+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
               path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("UM-MULTIHOST-AUTOCATALOG-ROOT-PROTECTED");
        }
        if(File.Exists(Path.Combine(root,CatalogName)))
            throw new IOException("UM-MULTIHOST-AUTOCATALOG-ALREADY-EXISTS");
        ExactDirectoryMembers(root,new[]{"hosts","maps","clusters"});
        string clusterFolder=Path.Combine(root,"clusters");
        var clusterIds=Directory.GetDirectories(clusterFolder)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if(clusterIds.Length==0)
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-NO-CLUSTERS");
        if(clusterIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=clusterIds.Length)
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-DUPLICATE-CLUSTER");
        var clusters=clusterIds.Select(id=>ownership.Clusters.SingleOrDefault(
            c=>c.ClusterId.Equals(id,StringComparison.OrdinalIgnoreCase))??
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-UNKNOWN-CLUSTER"))
            .Select(c=>new MultiHostBackupCluster(c.ClusterId,c.Name)).ToArray();
        var selected=clusterIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maps=ownership.Maps.Where(m=>m.ClusterId!=null&&
            selected.Contains(m.ClusterId)).OrderBy(m=>m.MapId).ToArray();
        if(maps.Length==0||
           maps.Length>MultiHostBackupArchiveV1SafetyLimits.MaxMapsPerArchive)
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-NO-MAPS");
        var hostIds=maps.Select(m=>m.HostId).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var hosts=hostIds.Select(id=>ownership.Hosts.SingleOrDefault(
            h=>h.HostId.Equals(id,StringComparison.OrdinalIgnoreCase)&&
               (h.IsLocal||h.IsPaired))??
            throw new UnauthorizedAccessException("UM-MULTIHOST-AUTOCATALOG-UNPAIRED-HOST"))
            .Select(h=>new MultiHostBackupHost(h.HostId,h.Name)).ToArray();
        if(!hosts.Any(x=>x.HostId.Equals(ownership.LocalHostId,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-NO-LOCAL-HOST");
        ExactDirectoryMembers(Path.Combine(root,"hosts"),hostIds);
        ExactDirectoryMembers(Path.Combine(root,"maps"),
            maps.Select(m=>m.MapId.ToString("N")));
        var items=new List<MultiHostBackupCatalogItem>();
        foreach(var host in hosts)
        {
            string hs="hosts/"+host.HostId;
            var hostClusterIds=maps.Where(m=>m.HostId.Equals(host.HostId,
                StringComparison.OrdinalIgnoreCase)).Select(m=>m.ClusterId!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ExactDirectoryMembers(Path.Combine(root,hs.Replace('/',Path.DirectorySeparatorChar)),
                new[]{"configuration","clusters"});
            AddFiles(items,root,hs+"/configuration",host.HostId,null,null,
                _=>MultiHostBackupComponentKind.HostConfiguration);
            ExactDirectoryMembers(Path.Combine(root,(hs+"/clusters").Replace('/',
                Path.DirectorySeparatorChar)),hostClusterIds);
            foreach(string cluster in hostClusterIds)
            {
                string scope=hs+"/clusters/"+cluster;
                ExactDirectoryMembers(Path.Combine(root,scope.Replace('/',
                    Path.DirectorySeparatorChar)),new[]{"Store-v1"});
                AddFiles(items,root,scope+"/Store-v1",host.HostId,null,cluster,
                    _=>MultiHostBackupComponentKind.HostStoreV1File);
            }
        }
        foreach(var map in maps)
        {
            string scope="maps/"+map.MapId.ToString("N");
            string folder=Path.Combine(root,scope.Replace('/',Path.DirectorySeparatorChar));
            string[] originals=Files(folder);
            string[] worlds=originals.Where(x=>
                Path.GetExtension(x).Equals(".ark",StringComparison.OrdinalIgnoreCase)&&
                (SafeRelative(Path.GetRelativePath(folder,x)).StartsWith("Saved/",
                    StringComparison.OrdinalIgnoreCase)||
                 SafeRelative(Path.GetRelativePath(folder,x)).StartsWith("SavedArks/",
                    StringComparison.OrdinalIgnoreCase))).ToArray();
            if(worlds.Length!=1)
                throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-WORLD-AMBIGUOUS");
            string world=SafeRelative(Path.GetRelativePath(folder,worlds[0]));
            string[] api=originals.Select(x=>SafeRelative(Path.GetRelativePath(folder,x)))
                .Where(x=>x.StartsWith("ASAApi/",StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if(api.Length==0)
                throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-PLUGIN-DATA-MISSING");
            string pluginPrimary=api.FirstOrDefault(x=>x.Equals("ASAApi/empty-state.json",
                StringComparison.OrdinalIgnoreCase))??api.Order(StringComparer.Ordinal).First();
            AddFiles(items,root,scope,map.HostId,map.MapId,map.ClusterId,rel=>
            {
                string name=Path.GetFileName(rel);
                if(rel.Equals(world,StringComparison.OrdinalIgnoreCase))
                    return MultiHostBackupComponentKind.MapWorld;
                if(name.EndsWith(".arkprofile",StringComparison.OrdinalIgnoreCase))
                    return MultiHostBackupComponentKind.MapProfile;
                if(name.EndsWith(".arktribe",StringComparison.OrdinalIgnoreCase))
                    return MultiHostBackupComponentKind.MapTribe;
                if(name.Equals("Game.ini",StringComparison.OrdinalIgnoreCase))
                    return MultiHostBackupComponentKind.GameIni;
                if(name.Equals("GameUserSettings.ini",StringComparison.OrdinalIgnoreCase))
                    return MultiHostBackupComponentKind.GameUserSettingsIni;
                if(rel.Equals("Manager/mods.json",StringComparison.OrdinalIgnoreCase))
                    return MultiHostBackupComponentKind.ModConfiguration;
                if(rel.Equals("Manager/plugins.json",StringComparison.OrdinalIgnoreCase))
                    return MultiHostBackupComponentKind.AsaApiPluginConfiguration;
                if(rel.Equals(pluginPrimary,StringComparison.OrdinalIgnoreCase))
                    return MultiHostBackupComponentKind.PluginPersistentData;
                return MultiHostBackupComponentKind.MapSavedFile;
            });
        }
        foreach(var cluster in clusters)
        {
            string baseScope="clusters/"+cluster.ClusterId;
            ExactDirectoryMembers(Path.Combine(root,baseScope.Replace('/',
                Path.DirectorySeparatorChar)),new[]{"Store-v1","Transfer"});
            var members=maps.Where(m=>m.ClusterId!.Equals(cluster.ClusterId,
                StringComparison.OrdinalIgnoreCase)).Select(m=>m.HostId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(string kind in new[]{"Store-v1","Transfer"})
                ExactDirectoryMembers(Path.Combine(root,(baseScope+"/"+kind).Replace('/',
                    Path.DirectorySeparatorChar)),members);
            foreach(var host in members)
            {
                AddFiles(items,root,baseScope+"/Store-v1/"+host,host,null,
                    cluster.ClusterId,_=>MultiHostBackupComponentKind.ClusterStoreV1File);
                AddFiles(items,root,baseScope+"/Transfer/"+host,host,null,
                    cluster.ClusterId,rel=>rel.Equals("snapshot.json",
                        StringComparison.OrdinalIgnoreCase)?
                            MultiHostBackupComponentKind.ClusterTransferManifest:
                            MultiHostBackupComponentKind.ClusterTransferFile);
            }
        }
        if(items.Count is <1 or >MultiHostBackupArchiveV1SafetyLimits.MaxComponentsPerArchive)
            throw new InvalidDataException("UM-MULTIHOST-AUTOCATALOG-INVENTORY-LIMIT");
        var catalog=new MultiHostBackupCatalog(1,Guid.NewGuid(),
            hosts,clusters,maps.Select(m=>new MultiHostBackupMap(m.MapId,m.HostId,
                m.ClusterId!,m.Name)).ToArray(),items.ToArray(),
            false,false,MultiHostClusterBackupArchive.OfflineCopyCatalogScope);
        // Required component counts, missing world/INI/plugin/Store/transfer,
        // duplicate logical destinations and ambiguous ownership are checked
        // by the EXISTING archive validator when the catalog is committed.
        long total=0;
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var item in items.OrderBy(x=>x.RelativeSource,StringComparer.Ordinal))
        {
            string file=Path.Combine(root,item.RelativeSource.Replace('/',
                Path.DirectorySeparatorChar));
            var info=new FileInfo(file);
            total=checked(total+info.Length);
            string line=item.Kind+"|"+item.HostId+"|"+item.MapId+"|"+
                item.ClusterId+"|"+item.RelativeSource+"|"+info.Length+"|"+
                info.LastWriteTimeUtc.Ticks+"\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }
        hash.AppendData(Encoding.UTF8.GetBytes(
            string.Join("|",catalog.Hosts.Select(h=>h.HostId))+"|"+
            string.Join("|",catalog.Clusters.Select(k=>k.ClusterId))+"|"+
            string.Join("|",catalog.Maps.Select(m=>m.MapId))));
        return new(catalog,Convert.ToHexString(hash.GetHashAndReset()),items.Count,total);
    }
    public static MultiHostBackupCatalog Publish(string offlineRoot,
        MultiHostBackupOwnershipSnapshot ownership,string expectedFingerprint,
        IReadOnlyCollection<string>? forbiddenRoots=null)
    {
        Preview inspected=Inspect(offlineRoot,ownership,forbiddenRoots);
        if(inspected.Fingerprint!=expectedFingerprint)
            throw new IOException("UM-MULTIHOST-AUTOCATALOG-SOURCE-CHANGED");
        string root=CanonicalRoot(offlineRoot);
        string target=Path.Combine(root,CatalogName);
        string staging=Path.Combine(root,".multi-host-catalog-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            using(var fs=new FileStream(staging,FileMode.CreateNew,FileAccess.Write,
                FileShare.None,8192,FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(fs,inspected.Catalog,Json);
                fs.Flush(true);
            }
            // The existing catalog validator covers file ownership/layout,
            // complete component inventory and registered host/map membership.
            MultiHostClusterBackupArchive.InspectCatalog(staging,ownership,forbiddenRoots);
            File.Move(staging,target);
            return inspected.Catalog;
        }
        finally
        {
            if(File.Exists(staging))File.Delete(staging);
        }
    }
}
