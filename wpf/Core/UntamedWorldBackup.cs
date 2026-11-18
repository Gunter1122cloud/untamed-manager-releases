// Legacy source identifier is a compile-time alias only; the public type,
 // binary metadata, UI, and new APIs use the Untamed name. Historical on-disk
 // data contracts are unchanged.
global using KovaCompatibleWorldBackup = UntamedManager.Core.UntamedWorldBackup;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Per-map, stopped-server world backup/restore. Mirrors the proven Kova pattern:
/// exact SavedArks/map folder, checked SHA-256 copy, incomplete staging, atomic
/// publish; restore takes a second protective backup and retains the old folder.
/// This is independent of the original shared Store-v1 writer and MariaDB.
/// </summary>
public static class UntamedWorldBackup
{
    public sealed record Entry(string Relative,long Bytes,string Sha256);
    public sealed record Manifest(int Schema,Guid Owner,string MapKey,string Installation,
        DateTimeOffset CreatedUtc,string Kind,Entry[] Files);
    public sealed record Point(string Id,DateTimeOffset CreatedUtc,string Kind,
        int Files,long Bytes,int Characters,int Tribes);
    public sealed record Receipt(string Id,string Kind,int Files,long Bytes,
        int Characters,int Tribes,bool Verified,bool WorldCopied,
        string? ProtectedPrevious=null);
    public sealed record RestoreReceipt(string Point,string ProtectiveBackup,
        bool Restored,bool OldWorldRetained,string PreviousDirectory);

    private const int MaxFiles=100000;
    // Per-map offline world packages must remain bounded even when a source
    // folder contains unexpected enormous files. The multi-host archive has
    // its own independent resource limits.
    public const long MaxWorldPackageBytes=2L*1024*1024*1024*1024;
    // Pure budget helper for both source scanning and untrusted manifest reads.
    public static long CheckedWorldBytes(long current,long next)
    {
        if(current<0||next<0||next>MaxWorldPackageBytes||
           current>MaxWorldPackageBytes-next)
            throw new InvalidDataException("UM-BACKUP-SIZE-LIMIT");
        return current+next;
    }
    private const int MaxManifestBytes=12_000_000;
    private const int MaxCompleteMarkerBytes=16;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Guard(string candidate)
    {
        if(string.IsNullOrWhiteSpace(candidate)||!Path.IsPathFullyQualified(candidate)||
           candidate.StartsWith(@"\\",StringComparison.Ordinal)||
           candidate.Contains('~')||candidate.Contains('\0'))
            throw new InvalidDataException("UM-BACKUP-LOCAL-FIXED-PATH-REQUIRED");
        string path=Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(path)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-BACKUP-LOCAL-FIXED-DISK-REQUIRED");
        for(var d=new DirectoryInfo(path);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-BACKUP-LINKED-DIRECTORY-DENIED");
        return path;
    }
    private static bool Contained(string root,string child)=>
        child.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static string MapKey(LocalMap map)=>AsaSaveLayoutResolver.MapToken(map);
    // Active Store-v1 maps cannot be rolled back independently of the other
    // host's character transfer state. An imported historical map is Store
    // bound even if it was intentionally quarantined with empty arguments.
    public static bool HasLiveOrHistoricalStore(LocalMap map)
    {
        if(map==null)throw new ArgumentNullException(nameof(map));
        if(map.HistoricalStoreProtected||
           !string.IsNullOrWhiteSpace(map.HistoricalMapKey)||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot))
            return true;
        foreach(string args in new[]{
            map.Arguments,map.ApiLoaderArguments,map.LastProcess?.Arguments??"",
            map.LastLoader?.Arguments??""
        })
        {
            if(string.IsNullOrWhiteSpace(args))continue;
            if(AsaStoreArgumentSwitches.ContainsStoreWriter(args)||
               AsaStoreArgumentSwitches.ContainsConversion(args))
                return true;
        }
        return false;
    }
    private static string World(LocalMap map)=>
        Guard(AsaSaveLayoutResolver.Resolve(map).WorldDirectory);
    private static string ArchiveRoot(string profile,LocalMap map)
    {
        string path=Guard(Path.Combine(Guard(profile),"world-backups",map.Id.ToString("N")));
        string installation=Guard(map.InstallationPath);
        if(path==installation||Contained(path,installation)||Contained(installation,path))
            throw new InvalidDataException("UM-BACKUP-PATHS-OVERLAP");
        return path;
    }
    private static void Stopped(Func<ProcessCheck> process)
    {
        ProcessCheck state=process();
        if(state.Running||!state.ConfirmedStopped||state.Identity!=null||
           state.LoaderIdentity!=null)
            throw new InvalidOperationException("UM-BACKUP-CONFIRMED-STOP-REQUIRED");
    }
    private static string SafeRelative(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||Path.IsPathFullyQualified(name)||
           name.Length>1024||name.Split(new[]{'\\','/'},StringSplitOptions.None)
               .Any(p=>p.Length==0||p is "." or ".."||p.EndsWith('.')||
                   p.EndsWith(' ')||p.Contains(':')||p.Contains('~')))
            throw new InvalidDataException("UM-BACKUP-UNSAFE-RELATIVE");
        return name;
    }
    private static string Hash(string file)
    {
        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,
            1024*1024,FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static Entry[] Scan(string root,string map,bool allowHistoricalEmptyProfiles=false)
    {
        root=Guard(root);
        if(!Directory.Exists(root))
            throw new DirectoryNotFoundException("UM-BACKUP-WORLD-FOLDER-MISSING");
        var entries=new List<Entry>();
        long scannedBytes=0;
        void Walk(string directory)
        {
            string path=Guard(directory);
            if(path!=root&&!Contained(root,path))
                throw new InvalidDataException("UM-BACKUP-DIRECTORY-ESCAPES-MAP");
            foreach(string file in Directory.EnumerateFiles(path).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))
            {
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("UM-BACKUP-LINKED-FILE-DENIED");
                string relative=SafeRelative(Path.GetRelativePath(root,file));
                var info=new FileInfo(file);
                scannedBytes=CheckedWorldBytes(scannedBytes,info.Length);
                // ASA's documented -usestore mode embeds survivor/tribe data
                // in the .ark world. Former loose .arkprofile/.arktribe files
                // may linger after conversion and can even be empty during a
                // transfer. Preserve their exact bytes; NEVER erase or repair
                // them on a backup. A zero-byte world remains hard-invalid.
                bool worldFile=relative.Equals(map+".ark",StringComparison.OrdinalIgnoreCase);
                bool looseProfile=relative.EndsWith(".arkprofile",StringComparison.OrdinalIgnoreCase)||
                    relative.EndsWith(".arktribe",StringComparison.OrdinalIgnoreCase);
                if(info.Length==0 && (worldFile || (looseProfile&&!allowHistoricalEmptyProfiles)))
                    throw new InvalidDataException("UM-BACKUP-ZERO-BYTE-CRITICAL-SAVE");
                entries.Add(new(relative,info.Length,Hash(file)));
                if(entries.Count>MaxFiles)throw new InvalidDataException("UM-BACKUP-TOO-MANY-FILES");
            }
            foreach(string child in Directory.EnumerateDirectories(path))
                Walk(child);
        }
        Walk(root);
        if(!entries.Any(x=>x.Relative.Equals(map+".ark",StringComparison.OrdinalIgnoreCase)&&x.Bytes>0))
            throw new InvalidDataException("UM-BACKUP-VALID-WORLD-MISSING");
        if(entries.Select(x=>x.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=entries.Count)
            throw new InvalidDataException("UM-BACKUP-DUPLICATE-NAME");
        return entries.OrderBy(x=>x.Relative,StringComparer.Ordinal).ToArray();
    }
    private static void WriteDurable(string file,string data)
    {
        using var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new StreamWriter(stream,new UTF8Encoding(false),8192,leaveOpen:true);
        writer.Write(data);writer.Flush();stream.Flush(true);
    }
    private static void CopyVerified(string source,string destination,Entry[] files)
    {
        Directory.CreateDirectory(destination);
        foreach(var entry in files)
        {
            string relative=SafeRelative(entry.Relative);
            string from=Guard(Path.Combine(source,relative));
            string to=Guard(Path.Combine(destination,relative));
            if(!Contained(source,from)||!Contained(destination,to))
                throw new InvalidDataException("UM-BACKUP-COPY-ESCAPES-ROOT");
            if((File.GetAttributes(from)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-BACKUP-LINKED-FILE-DENIED");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using(var input=new FileStream(from,FileMode.Open,FileAccess.Read,FileShare.Read,
                1024*1024,FileOptions.SequentialScan))
            using(var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                if(input.Length!=entry.Bytes)throw new IOException("UM-BACKUP-SOURCE-CHANGED");
                input.CopyTo(output,1024*1024);output.Flush(true);
            }
            if(new FileInfo(to).Length!=entry.Bytes||
               !Hash(to).Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase)||
               !Hash(from).Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new IOException("UM-BACKUP-HASH-CHANGED");
        }
    }
    private static long Total(Entry[] files)
    {
        long bytes=0;
        foreach(var file in files)
        {
            bytes=CheckedWorldBytes(bytes,file.Bytes);
        }
        return bytes;
    }
    private static Point AsPoint(string id,Manifest m)=>
        new(id,m.CreatedUtc,m.Kind,m.Files.Length,Total(m.Files),
            m.Files.Count(f=>f.Relative.EndsWith(".arkprofile",StringComparison.OrdinalIgnoreCase)),
            m.Files.Count(f=>f.Relative.EndsWith(".arktribe",StringComparison.OrdinalIgnoreCase)));
    // SHA-verified backup archives are still user/operator-selected local
    // files and may be truncated, linked or modified by another process.
    // Never allocate an unbounded File.ReadAllText buffer before validating
    // their metadata. An incomplete or malformed package is NOT restorable.
    private static byte[] ReadMetadata(string path,int minimum,int maximum)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-BACKUP-METADATA-LINK-DENIED");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.Read,65536,FileOptions.SequentialScan);
        if(stream.Length<minimum||stream.Length>maximum)
            throw new InvalidDataException("UM-BACKUP-METADATA-SIZE");
        byte[] bytes=new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if(stream.ReadByte()!=-1)
            throw new InvalidDataException("UM-BACKUP-METADATA-CHANGED");
        return bytes;
    }
    private static void RequireCommitted(string folder)
    {
        string marker=Path.Combine(folder,"complete");
        if(!File.Exists(marker) ||
           Encoding.UTF8.GetString(ReadMetadata(marker,1,MaxCompleteMarkerBytes))!="1")
            throw new InvalidDataException("UM-BACKUP-INCOMPLETE");
    }

    private static Manifest Read(string folder,Guid owner,string map,string install)
    {
        if(!Directory.Exists(folder))
            throw new InvalidDataException("UM-BACKUP-POINT-NOT-FOUND");
        string file=Path.Combine(folder,"manifest.json"),data=Path.Combine(folder,"files");
        RequireCommitted(folder);
        if(!File.Exists(file))
            throw new InvalidDataException("UM-BACKUP-INCOMPLETE");
        var m=JsonSerializer.Deserialize<Manifest>(
            ReadMetadata(file,2,MaxManifestBytes),Json)??
            throw new InvalidDataException("UM-BACKUP-MANIFEST-INVALID");
        // System.Text.Json accepts null elements/properties despite the
        // C# record's nonnullable declaration. Reject BEFORE any full-world
        // SHA-256 scan or potentially destructive restore operation.
        if(m.Schema!=1||m.Owner!=owner||m.MapKey!=map||
           !string.Equals(m.Installation,install,StringComparison.OrdinalIgnoreCase)||
           m.Kind is not ("manual" or "protective")||
           m.Files is null||m.Files.Length is <1 or >MaxFiles||
           m.Files.Any(x=>x is null||x.Bytes<0||
               string.IsNullOrWhiteSpace(x.Relative)||
               x.Sha256 is null||x.Sha256.Length!=64||
               !x.Sha256.All(Uri.IsHexDigit)||
               SafeRelative(x.Relative).Length==0)||
           m.Files.Select(x=>x.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=m.Files.Length)
            throw new InvalidDataException("UM-BACKUP-MANIFEST-OWNER-MISMATCH");
        // Reject maliciously oversized metadata before hashing any archive
        // bytes. A valid marker does not override the source budget.
        _=Total(m.Files);
        var actual=Scan(data,map,allowHistoricalEmptyProfiles:true);
        if(!actual.SequenceEqual(m.Files))
            throw new InvalidDataException("UM-BACKUP-DIGEST-MISMATCH");
        return m;
    }
    public static bool InstallationRecoveryRequired(LocalMap map)
    {
        string root=Guard(map.InstallationPath);
        string saved=Guard(Path.Combine(root,"ShooterGame","Saved","SavedArks"));
        if(!Directory.Exists(saved))return false;
        // Never restart a shared SteamCMD process family while any map in
        // the installation has a Kova or Untamed interrupted atomic swap.
        return Directory.EnumerateFiles(saved,".untamed-restore-*.json",
                   SearchOption.TopDirectoryOnly).Any() ||
               Directory.EnumerateFiles(saved,".kova-restore-*.json",
                   SearchOption.TopDirectoryOnly).Any();
    }
    public static bool RecoveryRequired(LocalMap map)
    {
        string world=World(map);
        string parent=Path.GetDirectoryName(world)!;
        string marker=Path.Combine(parent,".untamed-restore-"+Path.GetFileName(world)+".json");
        return File.Exists(marker);
    }
    private static void PendingRestore(string world)
    {
        string parent=Path.GetDirectoryName(world)!;
        string marker=Path.Combine(parent,".untamed-restore-"+Path.GetFileName(world)+".json");
        if(File.Exists(marker))
            throw new InvalidOperationException("UM-BACKUP-RESTORE-RECOVERY-REQUIRED");
    }
    public static Point Create(LocalMap map,string profile,Func<ProcessCheck> process,
        string kind="manual",UntamedOperationCoordinator? operationCoordinator=null)
    {
        if(kind is not ("manual" or "protective"))throw new ArgumentException(nameof(kind));
        string world=World(map),archive=ArchiveRoot(profile,map),key=MapKey(map);
        Stopped(process);PendingRestore(world);
        var manifestFiles=Scan(world,key,HasLiveOrHistoricalStore(map));
        long size=Total(manifestFiles);
        // Manager-owned optional limits are enforced before creating any
        // staging/archive directory. A malformed policy is an explicit error;
        // it must not cause a silent fallback to an unbounded backup.
        BackupResourcePolicy.ForManagerProfile(profile)
            .DemandFits(manifestFiles.Length,size);
        Directory.CreateDirectory(archive);
        if(new DriveInfo(Path.GetPathRoot(archive)!).AvailableFreeSpace<size+64L*1024*1024)
            throw new IOException("UM-BACKUP-NOT-ENOUGH-SPACE");
        using var lease=(operationCoordinator??new UntamedOperationCoordinator()).Acquire(
            map.InstallationPath,UntamedOperationKind.Backup);
        Stopped(process);
        var uuid=Guid.NewGuid().ToString("D");
        string stage=Path.Combine(archive,".pending-"+uuid),target=Path.Combine(archive,uuid);
        Directory.CreateDirectory(stage);
        try
        {
            CopyVerified(world,Path.Combine(stage,"files"),manifestFiles);
            Stopped(process);
            if(!Scan(world,key,HasLiveOrHistoricalStore(map)).SequenceEqual(manifestFiles))
                throw new IOException("UM-BACKUP-SOURCE-CHANGED");
            var m=new Manifest(1,map.Id,key,Guard(map.InstallationPath),
                DateTimeOffset.UtcNow,kind,manifestFiles);
            WriteDurable(Path.Combine(stage,"manifest.json"),JsonSerializer.Serialize(m,Json));
            WriteDurable(Path.Combine(stage,"complete"),"1");
            Directory.Move(stage,target);
            Read(target,map.Id,key,Guard(map.InstallationPath));
            lease.Complete();
            return AsPoint(uuid,m);
        }
        finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }
    public static Point[] List(LocalMap map,string profile,int max=20)
    {
        string archive=ArchiveRoot(profile,map);
        if(!Directory.Exists(archive))return Array.Empty<Point>();
        int take=Math.Clamp(max,1,50);
        string key=MapKey(map),installation=Guard(map.InstallationPath);
        // Backup folder names are random GUIDs. Sorting them lexically was
        // returning arbitrary old archives instead of the latest backups.
        // Read only small, trusted ownership/date headers first; perform
        // expensive whole-world hash verification on selected recent points.
        var candidates=new List<(string Folder,DateTimeOffset Created)>();
        int examined=0;
        foreach(string folder in Directory.EnumerateDirectories(archive))
        {
            if(++examined>5000)
                throw new InvalidDataException("UM-BACKUP-ARCHIVE-LIST-TOO-LARGE");
            if(!Guid.TryParse(Path.GetFileName(folder),out _))continue;
            try
            {
                if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)continue;
                string path=Path.Combine(folder,"manifest.json");
                if(!File.Exists(path))continue;
                RequireCommitted(folder);
                using var doc=JsonDocument.Parse(
                    ReadMetadata(path,2,MaxManifestBytes));
                var json=doc.RootElement;
                if(json.ValueKind!=JsonValueKind.Object ||
                   !json.TryGetProperty("owner",out var owner) ||
                   owner.ValueKind!=JsonValueKind.String ||
                   !Guid.TryParse(owner.GetString(),out var id)||id!=map.Id||
                   !json.TryGetProperty("mapKey",out var mapKey)||
                   mapKey.ValueKind!=JsonValueKind.String||mapKey.GetString()!=key||
                   !json.TryGetProperty("installation",out var origin)||
                   origin.ValueKind!=JsonValueKind.String||
                   !string.Equals(origin.GetString(),installation,
                       StringComparison.OrdinalIgnoreCase)||
                   !json.TryGetProperty("createdUtc",out var date)||
                   date.ValueKind!=JsonValueKind.String)continue;
                candidates.Add((folder,date.GetDateTimeOffset()));
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or FormatException)
            {
                // Incomplete/tampered headers are not eligible restore points.
            }
        }
        var points=new List<Point>();
        // Try additional candidates if a recent package fails full integrity.
        foreach(var candidate in candidates
            .OrderByDescending(x=>x.Created).Take(Math.Min(250,take*5)))
        {
            try
            {
                var m=Read(Guard(candidate.Folder),map.Id,key,installation);
                points.Add(AsPoint(Path.GetFileName(candidate.Folder),m));
                if(points.Count==take)break;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or FormatException)
            {
                // Only fully verified packages may appear in selectable history.
            }
        }
        return points.ToArray();
    }
    public static Point Verify(LocalMap map,string profile,string point)
    {
        if(!Guid.TryParse(point,out var g)||g==Guid.Empty)
            throw new InvalidDataException("UM-BACKUP-INVALID-POINT");
        string folder=Guard(Path.Combine(ArchiveRoot(profile,map),g.ToString("D")));
        var m=Read(folder,map.Id,MapKey(map),Guard(map.InstallationPath));
        return AsPoint(g.ToString("D"),m);
    }
    public sealed record StagedWorld(string RelativeFolder,int Files,long Bytes);
    // Restore a committed archive to an isolated, manager-owned directory.
    // A running world is never opened or replaced; only archive bytes are
    // read. This does not restore a shared Store/transfer transaction.
    internal static StagedWorld StageVerifiedWorld(LocalMap map,string profile,string point,
        IEnumerable<string> installationRoots,
        IEnumerable<string>? sharedRoots=null)
    {
        if(map.ObservedReadOnlyImported)throw new UnauthorizedAccessException("UM-BACKUP-MAP-NOT-LOCAL");
        if(!Guid.TryParseExact(point,"D",out var id)||id==Guid.Empty)
            throw new InvalidDataException("UM-BACKUP-INVALID-POINT");
        string archive=ArchiveRoot(profile,map),key=MapKey(map),installation=Guard(map.InstallationPath);
        string package=Guard(Path.Combine(archive,id.ToString("D")));
        var manifest=Read(package,map.Id,key,installation);
        long bytes=Total(manifest.Files);
        BackupResourcePolicy.ForManagerProfile(profile).DemandFits(manifest.Files.Length,bytes);
        string parent=Guard(Path.Combine(Guard(profile),"world-restore-staging",map.Id.ToString("N")));
        foreach(var candidate in installationRoots)
        {
            string root=Guard(candidate);
            if(parent.Equals(root,StringComparison.OrdinalIgnoreCase)||
               Contained(root,parent)||Contained(parent,root))
                throw new InvalidDataException("UM-BACKUP-PATHS-OVERLAP");
        }
        // The Manager profile must not itself be located inside a shared
        // cluster/transfer directory. This is a lexical check, deliberately
        // not a network probe: an unavailable peer must not block archive
        // staging on an unrelated fixed local disk.
        foreach(var candidate in sharedRoots??Array.Empty<string>())
        {
            if(string.IsNullOrWhiteSpace(candidate))continue;
            if(!Path.IsPathFullyQualified(candidate))
                throw new InvalidDataException("UM-BACKUP-SHARED-ROOT-UNVERIFIED");
            string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if(parent.Equals(root,StringComparison.OrdinalIgnoreCase)||
               Contained(root,parent)||Contained(parent,root))
                throw new InvalidDataException("UM-BACKUP-PATHS-OVERLAP");
        }
        if(new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace<checked(bytes+64L*1024*1024))
            throw new IOException("UM-BACKUP-INSUFFICIENT-RESTORE-CAPACITY");
        string stage=Guard(Path.Combine(parent,Guid.NewGuid().ToString("N")+".incomplete"));
        string target=stage[..^".incomplete".Length];
        // Guard the final path independently; an existing reparse point
        // must not be followed even when it appeared after staging preview.
        _=Guard(target);
        if(Directory.Exists(stage)||File.Exists(stage)||Directory.Exists(target)||File.Exists(target))
            throw new IOException("UM-BACKUP-STAGE-EXISTS");
        Directory.CreateDirectory(stage);
        try
        {
            string world=Path.Combine(stage,"files");
            CopyVerified(Path.Combine(package,"files"),world,manifest.Files);
            if(!Scan(world,key,allowHistoricalEmptyProfiles:true).SequenceEqual(manifest.Files)||
               !Read(package,map.Id,key,installation).Files.SequenceEqual(manifest.Files))
                throw new InvalidDataException("UM-BACKUP-DIGEST-MISMATCH");
            WriteDurable(Path.Combine(stage,"manifest.json"),JsonSerializer.Serialize(manifest,Json));
            WriteDurable(Path.Combine(stage,"complete"),"1");
            _=Guard(target);
            Directory.Move(stage,target);
            var published=Read(Guard(target),map.Id,key,installation);
            if(!published.Files.SequenceEqual(manifest.Files))
                throw new InvalidDataException("UM-BACKUP-DIGEST-MISMATCH");
            return new(Path.GetRelativePath(Guard(profile),target),manifest.Files.Length,bytes);
        }
        finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }
    // Local cutover participant for a future coordinated Store transaction.
    // The native cross-host coordinator must supply a real all-host stop/
    // writer fence and durable global commit decision. No desktop/Discord
    // route exposes this method until that integration is implemented.
    internal sealed record StoreWorldSelection(LocalMap Map,string Point);
    internal sealed record StoreWorldCutoverReceipt(Guid TransactionId,string[] ProtectivePoints,
        int WorldsRestored,bool LocalHashesVerified,bool PreviousWorldsRetained);
    private sealed class StoreWorldCutoverRow
    {
        public required LocalMap Map;
        public required string Point,World,Key,Archive,Stage,Previous,Marker;
        public required Entry[] Before;
        public required Manifest Replacement;
        public string Protective="";
        public bool Marked,Moved,Installed;
    }

    internal static StoreWorldCutoverReceipt RestoreCoordinatedLocalWorlds(
        IReadOnlyList<StoreWorldSelection> selected,string profile,
        Func<LocalMap,ProcessCheck> inspect,Func<bool> allWritersFenced,
        Func<bool> globalCommitAuthorized,UntamedOperationCoordinator coordinator,
        Action<int>? beforeSwapForTesting=null)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(allWritersFenced);
        ArgumentNullException.ThrowIfNull(globalCommitAuthorized);
        ArgumentNullException.ThrowIfNull(coordinator);
        if(selected==null||selected.Count is <1 or >128||
           selected.Any(x=>x==null||x.Map==null||x.Map.Id==Guid.Empty||
               x.Map.ObservedReadOnlyImported||!HasLiveOrHistoricalStore(x.Map))||
           selected.Select(x=>x.Map.Id).Distinct().Count()!=selected.Count)
            throw new InvalidDataException("UM-STORE-RESTORE-LOCAL-SELECTION");
        void Fence()
        {
            if(!allWritersFenced())
                throw new InvalidOperationException("UM-STORE-RESTORE-CLUSTER-FENCE-LOST");
            foreach(var selection in selected)Stopped(()=>inspect(selection.Map));
        }
        Fence();
        Guid transaction=Guid.NewGuid();
        string tx=transaction.ToString("N");
        string localProfile=Guard(profile);
        var rows=new List<StoreWorldCutoverRow>();
        var budget=BackupResourcePolicy.ForManagerProfile(localProfile);
        foreach(var selection in selected)
        {
            if(!Guid.TryParseExact(selection.Point,"D",out var point)||point==Guid.Empty)
                throw new InvalidDataException("UM-BACKUP-INVALID-POINT");
            var map=selection.Map;
            string install=Guard(map.InstallationPath),world=World(map),key=MapKey(map);
            if(rows.Any(x=>{string prior=Guard(x.Map.InstallationPath);
                return install.Equals(prior,StringComparison.OrdinalIgnoreCase)||
                    Contained(install,prior)||Contained(prior,install);}))
                throw new InvalidDataException("UM-STORE-RESTORE-OVERLAPPING-INSTALLATIONS");
            PendingRestore(world);
            if(InstallationRecoveryRequired(map))
                throw new InvalidOperationException("UM-BACKUP-RESTORE-RECOVERY-REQUIRED");
            string archive=Guard(Path.Combine(ArchiveRoot(localProfile,map),point.ToString("D")));
            var replacement=Read(archive,map.Id,key,install);
            var before=Scan(world,key,allowHistoricalEmptyProfiles:true);
            budget.DemandFits(replacement.Files.Length,Total(replacement.Files));
            budget.DemandFits(before.Length,Total(before));
            string parent=Path.GetDirectoryName(world)!;
            rows.Add(new(){Map=map,Point=point.ToString("D"),World=world,Key=key,Archive=archive,
                Stage=Guard(Path.Combine(parent,".untamed-stage-"+tx+"-"+map.Id.ToString("N"))),
                Previous=Guard(Path.Combine(parent,".untamed-previous-"+key+"-"+tx)),
                Marker=Guard(Path.Combine(parent,".untamed-restore-"+key+".json")),
                Before=before,Replacement=replacement});
        }
        rows.Sort((a,b)=>StringComparer.OrdinalIgnoreCase.Compare(a.Map.InstallationPath,b.Map.InstallationPath));
        var space=rows.GroupBy(r=>Path.GetPathRoot(r.World)!,StringComparer.OrdinalIgnoreCase);
        foreach(var volume in space)
            if(new DriveInfo(volume.Key).AvailableFreeSpace<
               checked(volume.Sum(r=>Total(r.Replacement.Files))+128L*1024*1024))
                throw new IOException("UM-BACKUP-INSUFFICIENT-RESTORE-CAPACITY");
        long protectiveBytes=rows.Sum(r=>Total(r.Before));
        if(new DriveInfo(Path.GetPathRoot(localProfile)!).AvailableFreeSpace<checked(protectiveBytes+
           rows.Where(r=>string.Equals(Path.GetPathRoot(r.World),Path.GetPathRoot(localProfile),
               StringComparison.OrdinalIgnoreCase)).Sum(r=>Total(r.Replacement.Files))+128L*1024*1024))
            throw new IOException("UM-BACKUP-INSUFFICIENT-RESTORE-CAPACITY");
        // Durable protective copies, then acquire all installation leases
        // before staging/swapping any world. No nested same-resource lease.
        foreach(var row in rows)
        {
            Fence();
            row.Protective=Create(row.Map,localProfile,()=>inspect(row.Map),"protective",coordinator).Id;
            var protective=Read(Guard(Path.Combine(ArchiveRoot(localProfile,row.Map),row.Protective)),
                row.Map.Id,row.Key,Guard(row.Map.InstallationPath));
            if(!protective.Files.SequenceEqual(row.Before))
                throw new IOException("UM-STORE-RESTORE-PROTECTIVE-SOURCE-CHANGED");
        }
        string journalFolder=Guard(Path.Combine(localProfile,"store-world-cutovers"));
        Directory.CreateDirectory(journalFolder);
        string journal=Guard(Path.Combine(journalFolder,tx+".json"));
        void Journal(string phase,bool first=false)
        {
            string next=Guard(journal+".next");
            WriteDurable(next,JsonSerializer.Serialize(new{schema=1,transactionId=transaction,
                phase,utc=DateTimeOffset.UtcNow,maps=rows.Select(r=>new{
                    mapId=r.Map.Id,r.Point,r.Protective,r.World,r.Stage,r.Previous,r.Marker,
                    r.Marked,r.Moved,r.Installed})},Json));
            File.Move(next,journal,!first);
        }
        var leases=new List<UntamedOperationCoordinator.Lease>();
        bool journalCreated=false,mutationStarted=false,decisionAccepted=false;
        try
        {
            foreach(var row in rows)
                leases.Add(coordinator.Acquire(row.Map.InstallationPath,UntamedOperationKind.Restore));
            Fence();
            foreach(var row in rows)
            {
                PendingRestore(row.World);
                if(!Scan(row.World,row.Key,true).SequenceEqual(row.Before))
                    throw new IOException("UM-STORE-RESTORE-CURRENT-WORLD-CHANGED");
                if(Directory.Exists(row.Stage)||File.Exists(row.Stage)||
                   Directory.Exists(row.Previous)||File.Exists(row.Previous))
                    throw new IOException("UM-STORE-RESTORE-CUTOVER-PATH-EXISTS");
                CopyVerified(Path.Combine(row.Archive,"files"),row.Stage,row.Replacement.Files);
                if(!Scan(row.Stage,row.Key,true).SequenceEqual(row.Replacement.Files))
                    throw new IOException("UM-STORE-RESTORE-STAGE-HASH");
                Fence();
            }
            Journal("prepared",true);journalCreated=true;
            // Every affected installation gets a durable marker before the
            // FIRST rename. A crash never silently re-enables Start.
            foreach(var lease in leases)lease.MarkMutationStarted();
            mutationStarted=true;
            foreach(var row in rows)
            {
                WriteDurable(row.Marker,JsonSerializer.Serialize(new{schema=1,owner=row.Map.Id,
                    map=row.Key,point=row.Point,protective=row.Protective,world=row.World,
                    stage=row.Stage,old=row.Previous,transactionId=transaction,utc=DateTimeOffset.UtcNow},Json));
                row.Marked=true;
            }
            Journal("swapping");
            for(int i=0;i<rows.Count;i++)
            {
                var row=rows[i];
                beforeSwapForTesting?.Invoke(i);
                Fence();
                if(!Scan(row.World,row.Key,true).SequenceEqual(row.Before)||
                   !Scan(row.Stage,row.Key,true).SequenceEqual(row.Replacement.Files))
                    throw new IOException("UM-STORE-RESTORE-PREFLIGHT-BYTES-CHANGED");
                Directory.Move(row.World,row.Previous);row.Moved=true;Journal("swapping");
                Directory.Move(row.Stage,row.World);row.Installed=true;Journal("swapping");
            }
            Fence();
            foreach(var row in rows)
                if(!Scan(row.World,row.Key,true).SequenceEqual(row.Replacement.Files)||
                   !Scan(row.Previous,row.Key,true).SequenceEqual(row.Before))
                    throw new IOException("UM-STORE-RESTORE-FINAL-HASH");
            Journal("awaiting-global-decision");
            if(!globalCommitAuthorized())
                throw new IOException("UM-STORE-RESTORE-GLOBAL-COMMIT-NOT-AUTHORIZED");
            decisionAccepted=true;
            // A successful durable cluster decision is irreversible here.
            // If local finalization fails, retain markers and never roll
            // back one participant behind the other hosts' backs.
            Fence();
            Journal("commit-authorized");
            foreach(var row in rows){File.Delete(row.Marker);row.Marked=false;}
            foreach(var lease in leases)lease.Complete();
            Journal("local-committed");
            return new(transaction,rows.Select(r=>r.Protective).ToArray(),rows.Count,true,true);
        }
        catch
        {
            if(journalCreated)
            {
                try
                {
                    if(decisionAccepted)Journal("commit-finalization-needs-review");
                    else
                    {
                        if(mutationStarted)
                        {
                            Fence();
                            for(int i=rows.Count-1;i>=0;i--)
                            {
                                var row=rows[i];
                                if(!row.Moved)continue;
                                Fence();
                                if(row.Installed){Directory.Move(row.World,row.Stage);row.Installed=false;}
                                Directory.Move(row.Previous,row.World);row.Moved=false;
                                Journal("rolling-back");
                            }
                            foreach(var row in rows)
                                if(!Scan(row.World,row.Key,true).SequenceEqual(row.Before))
                                    throw new IOException("UM-STORE-RESTORE-ROLLBACK-HASH");
                            foreach(var row in rows.Where(r=>r.Marked))
                            {File.Delete(row.Marker);row.Marked=false;}
                            foreach(var lease in leases)lease.Complete();
                        }
                        Journal("rolled-back");
                    }
                }
                catch
                {
                    // Retain the recovery markers/journal as far as possible;
                    // do not delete old worlds or automatically retry.
                    try{Journal("needs-review");}catch{}
                    throw new IOException("UM-STORE-RESTORE-RECOVERY-REQUIRED");
                }
            }
            throw;
        }
        finally
        {
            for(int i=leases.Count-1;i>=0;i--)leases[i].Dispose();
        }
    }

    public static RestoreReceipt Restore(LocalMap map,string profile,string point,
        Func<ProcessCheck> process,Action? beforeSwap=null,
        UntamedOperationCoordinator? operationCoordinator=null)
    {
        if(HasLiveOrHistoricalStore(map))
            throw new UnauthorizedAccessException(
                "UM-BACKUP-STORE-CHARACTER-RESTORE-REQUIRES-TWO-HOST-TRANSACTION");
        string world=World(map),archive=ArchiveRoot(profile,map),key=MapKey(map);
        Stopped(process);PendingRestore(world);
        Point selected=Verify(map,profile,point);
        if(!Directory.Exists(world))
            throw new DirectoryNotFoundException("UM-BACKUP-CURRENT-WORLD-MISSING");
        var current=Scan(world,key);
        if(!selected.Kind.Equals("manual",StringComparison.Ordinal)&&
           !selected.Kind.Equals("protective",StringComparison.Ordinal))
            throw new InvalidDataException("UM-BACKUP-RESTORE-KIND-DENIED");
        string root=Path.GetDirectoryName(world)!;
        if(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace<
           selected.Bytes+Total(current)*2+128L*1024*1024)
            throw new IOException("UM-BACKUP-INSUFFICIENT-RESTORE-CAPACITY");
        // Take a real protective backup before acquiring the swap lease.
        // Create acquires its own installation lock; nesting the same
        // cross-process lock could deadlock on Windows.
        var coordinator=operationCoordinator??new UntamedOperationCoordinator();
        var protective=Create(map,profile,process,"protective",coordinator);
        // The protective copy is a Backup, but the subsequent directory swap
        // is a Restore. The durable operation type must identify the actual
        // mutating action so crash-recovery and operator review cannot mistake
        // a partially restored world for an interrupted backup.
        using var lease=coordinator.Acquire(
            map.InstallationPath,UntamedOperationKind.Restore);
        Stopped(process);
        if(!Scan(world,key).SequenceEqual(current))
            throw new IOException("UM-BACKUP-SOURCE-CHANGED-AFTER-PROTECTIVE");
        var id=Guid.NewGuid().ToString("N");
        string stage=Path.Combine(root,".untamed-stage-"+id);
        string old=Path.Combine(root,".untamed-previous-"+key+"-"+id);
        string marker=Path.Combine(root,".untamed-restore-"+key+".json");
        string selectedPath=Path.Combine(archive,point,"files");
        bool moved=false,installed=false,marked=false;
        try
        {
            var selectedManifest=Read(Guard(Path.Combine(archive,point)),
                map.Id,key,Guard(map.InstallationPath));
            CopyVerified(selectedPath,stage,selectedManifest.Files);
            Stopped(process);
            if(!Scan(world,key).SequenceEqual(current))
                throw new IOException("UM-BACKUP-CURRENT-WORLD-CHANGED");
            beforeSwap?.Invoke();
            // Crash during the swap must leave a NEEDS_OPERATOR journal.
            lease.MarkMutationStarted();
            // Durable recovery marker precedes every rename, exactly like Kova.
            WriteDurable(marker,JsonSerializer.Serialize(new{
                schema=1,owner=map.Id,map=key,point,protective=protective.Id,
                world,stage,old,utc=DateTimeOffset.UtcNow
            },Json));marked=true;
            Directory.Move(world,old);moved=true;
            Directory.Move(stage,world);installed=true;
            Read(Guard(Path.Combine(archive,point)),map.Id,key,Guard(map.InstallationPath));
            if(!Scan(world,key).SequenceEqual(selectedManifest.Files))
                throw new IOException("UM-BACKUP-RESTORED-WORLD-HASH-MISMATCH");
            File.Delete(marker);marked=false;
            lease.Complete();
            return new(point,protective.Id,true,true,old);
        }
        catch
        {
            if(moved)
            {
                try
                {
                    if(installed)Directory.Move(world,stage);
                    Directory.Move(old,world);moved=false;
                    if(marked){File.Delete(marker);marked=false;}
                }
                catch{throw new IOException(
                    "UM-BACKUP-RESTORE-INCOMPLETE-RECOVERY-MARKER-RETAINED");}
            }
            else if(marked){File.Delete(marker);marked=false;}
            throw;
        }
        finally{if(!marked&&Directory.Exists(stage))Directory.Delete(stage,true);}
    }
}
