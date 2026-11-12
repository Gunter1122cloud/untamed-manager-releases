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
            if(args.Contains("-usestore",StringComparison.OrdinalIgnoreCase)||
               args.Contains("-converttostore",StringComparison.OrdinalIgnoreCase)||
               args.Contains("-convertfromstore",StringComparison.OrdinalIgnoreCase))
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
