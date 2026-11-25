using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;

/// <summary>Manager-owned encrypted Game.ini draft; never writes a running
/// map. ETag conflict is local to this draft, not a host operation blocker.</summary>
public static class PendingGameIni
{
    private sealed record Draft(Guid MapId,string RootIdentity,string BaseSha,string NextSha,string Content,bool Consumed=false);
    public sealed record Receipt(Guid MapId,string State,string? NextSha256=null);
    private static string Game(LocalMap map)=>Path.Combine(map.InstallationPath,"ShooterGame","Saved","Config","WindowsServer","Game.ini");
    private static string FileFor(string profile,Guid id)
    {
        if(id==Guid.Empty)throw new InvalidDataException("UM-INI-DRAFT-MAP");
        string dir=Path.Combine(profile,"pending-game-ini");
        _=UntamedOperationCoordinator.ResourceKey(dir);
        string file=Path.Combine(dir,id.ToString("N")+".dpapi");
        if(File.Exists(file)&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-INI-DRAFT-LINKED");
        return file;
    }
    private static T Locked<T>(string file,Func<T> work)
    {
        string name="Local\\Obsidian-PendingIni-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())));
        using var mutex=new Mutex(false,name);bool entered=false;
        try{
            try{entered=mutex.WaitOne(TimeSpan.FromSeconds(3));}catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("UM-INI-DRAFT-BUSY");
            return work();
        }finally{if(entered)mutex.ReleaseMutex();}
    }
    public static void DemandEligible(ClusterService cluster,LocalMap map)
    {
        if(!cluster.Config.LocalMaps.Any(x=>ReferenceEquals(x,map))||map.ObservedReadOnlyImported||map.Maintenance||
           map.HistoricalLaunchModeNeedsApproval||!AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidOperationException("UM-INI-DRAFT-NOT-OWNED");
        _=UntamedOperationCoordinator.ResourceKey(map.InstallationPath);
        string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(map.InstallationPath));
        foreach(var peer in cluster.Config.LocalMaps.Where(x=>x.Id!=map.Id&&!string.IsNullOrWhiteSpace(x.InstallationPath)))
        {
            string other=Path.TrimEndingDirectorySeparator(Path.GetFullPath(peer.InstallationPath));
            if(root.Equals(other,StringComparison.OrdinalIgnoreCase)||root.StartsWith(other+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
               other.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UM-INI-DRAFT-SHARED-INSTALL");
        }
    }
    private static Draft? Read(string file)
    {
        if(!File.Exists(file))return null;
        if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("UM-INI-DRAFT-LINKED");
        using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length is <1 or >16_000_000)throw new InvalidDataException("UM-INI-DRAFT-SIZE");
        byte[] bytes=new byte[checked((int)input.Length)];input.ReadExactly(bytes);
        return JsonSerializer.Deserialize<Draft>(SecureStore.UnprotectBytes(bytes))??throw new InvalidDataException("UM-INI-DRAFT-INVALID");
    }
    private static void Write(string file,Draft draft)
    {
        byte[] bytes=SecureStore.ProtectBytes(JsonSerializer.SerializeToUtf8Bytes(draft));
        if(bytes.Length>16_000_000)throw new InvalidDataException("UM-INI-DRAFT-SIZE");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _=UntamedOperationCoordinator.ResourceKey(Path.GetDirectoryName(file)!);
        string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {output.Write(bytes);output.Flush(true);}
            if(File.Exists(file)&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("UM-INI-DRAFT-LINKED");
            File.Move(temp,file,true);
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal static void StageLocal(string profile,LocalMap map,ConfigSnapshot snapshot,string content)
    {
        string file=FileFor(profile,map.Id);
        Locked(file,()=>{
            var current=SafeConfigEditor.ReadGameDraft(Game(map),map.InstallationPath);
            if(!Path.GetFullPath(snapshot.Path).Equals(current.Path,StringComparison.OrdinalIgnoreCase)||
               snapshot.Sha256!=current.Sha256)
                throw new ConfigEtagConflictException("UM-INI-DRAFT-BASE-CHANGED");
            var preview=SafeConfigEditor.Preview(current,content);
            if(!preview.Changed)throw new InvalidDataException("UM-INI-DRAFT-NO-CHANGES");
            Write(file,new(map.Id,StorePathIdentity.LocalDirectoryFingerprint(map.InstallationPath),current.Sha256,preview.NewSha256,content));
            return true;
        });
    }
    public static void Stage(ClusterService cluster,LocalMap map,ConfigSnapshot snapshot,string content)
    {DemandEligible(cluster,map);StageLocal(cluster.Store.Root,map,snapshot,content);}
    public static Receipt Status(ClusterService cluster,LocalMap map)
    {
        try{
            string file=FileFor(cluster.Store.Root,map.Id);
            return Locked(file,()=>{
                var d=Read(file);
                if(d==null)return new Receipt(map.Id,"none");
                if(d.MapId!=map.Id||d.RootIdentity!=StorePathIdentity.LocalDirectoryFingerprint(map.InstallationPath))
                    return new Receipt(map.Id,"conflict",d.NextSha);
                var current=SafeConfigEditor.ReadGameDraft(Game(map),map.InstallationPath);
                return new Receipt(map.Id,current.Sha256==d.NextSha?"applied":!d.Consumed&&current.Sha256==d.BaseSha?"queued":"conflict",d.NextSha);
            });
        }catch{return new(map.Id,"unavailable");}
    }
    public static void Discard(ClusterService cluster,LocalMap map)
    {
        string file=FileFor(cluster.Store.Root,map.Id);
        Locked(file,()=>{if(File.Exists(file))File.Delete(file);return true;});
    }
    internal static bool ApplyLocal(string profile,LocalMap map,Func<ProcessCheck> inspect,Action mark)
    {
        // No pending file means the normal launch is entirely unchanged.
        string file;
        try{file=FileFor(profile,map.Id);if(!File.Exists(file))return false;}
        catch{return false;}
        return Locked(file,()=>{
            Draft? d;ConfigSnapshot current;
            try{
                d=Read(file);
                if(d==null||d.Consumed||d.MapId!=map.Id||d.RootIdentity!=StorePathIdentity.LocalDirectoryFingerprint(map.InstallationPath))return false;
                current=SafeConfigEditor.ReadGameDraft(Game(map),map.InstallationPath);
                if(current.Sha256!=d.BaseSha||SafeConfigEditor.Preview(current,d.Content).NewSha256!=d.NextSha)return false;
            }catch{return false;}
            bool marked=false;
            try{
                SafeConfigEditor.ApplyGameDraftUnderLaunchLease(current,d.Content,map.InstallationPath,inspect,()=>{
                    Write(file,d with{Consumed=true});
                    mark();marked=true;
                });
                // Keep encrypted receipt. Matching desired hash prevents
                // a second apply after a crash or a subsequent restart.
                return marked;
            }catch(ConfigEtagConflictException) when(!marked){return false;}
        });
    }
    internal static bool ApplyBeforeLaunch(ClusterService cluster,LocalMap map,Func<ProcessCheck> inspect,Action mark)
    {
        string file;
        try{file=FileFor(cluster.Store.Root,map.Id);if(!File.Exists(file))return false;DemandEligible(cluster,map);}
        catch{return false;}
        return ApplyLocal(cluster.Store.Root,map,inspect,mark);
    }
}
