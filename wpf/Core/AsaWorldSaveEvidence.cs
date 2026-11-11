using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

/// <summary>
/// Observes the ACTUAL registered ASA ShooterGame.log and world .ark, after
/// a SaveWorld dispatch boundary. No RCON acknowledgement is treated as
/// SaveComplete. This is evidence of an ASA log event + changed/stable world
/// metadata, NOT a world round-trip or an OS-level fsync inside ASA.
/// Read-only, local, bounded; never copies or hashes entire multi-GB worlds.
/// </summary>
public static class AsaWorldSaveEvidence
{
    public sealed record Boundary(Guid MapId,int Pid,DateTimeOffset PidStartedUtc,
        string Log,string World,long LogOffset,DateTime LogCreatedUtc,
        long PriorWorldBytes,DateTime PriorWorldWriteUtc);

    public sealed record Observation(bool LogStarted,bool LogCompleted,
        bool WorldUpdated,bool WorldStable,bool LogIdentityValid,
        bool CompletionObserved,string Stage)
    {
        public bool RestoreOrClientJoinVerified=>false;
        // A changed, stable world file is useful lifecycle evidence even when
        // ASA omits its optional save log markers. It is NOT SaveComplete proof.
        public bool WorldMetadataObserved=>WorldUpdated&&WorldStable&&
            (CompletionObserved||Stage is "WORLD_UPDATED_STABLE_LOG_UNCONFIRMED"
                or "WORLD_UPDATED_STABLE_LOG_MISSING");
    }

    private static bool Under(string root,string child)=>
        child.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+
            Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static string LocalFilePath(string root,string path)
    {
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-ASA-SAVE-LOCAL-ROOT");
        string canonical=Path.GetFullPath(root),full=Path.GetFullPath(path);
        if(!Under(canonical,full)||
           new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-ASA-SAVE-PATH-ESCAPE");
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(full)!);dir!=null;
            dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-ASA-SAVE-LINKED-PARENT");
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ASA-SAVE-LINKED-FILE");
        return full;
    }

    public static Boundary? Begin(LocalMap map,ProcessIdentity process)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(process);
        if(process.Pid<=0||process.StartedUtc==default||
           !AsaReadyProbe.IsRegisteredServerBinary(map))return null;
        string root=map.InstallationPath;
        string log=LocalFilePath(root,Path.Combine(root,"ShooterGame","Saved",
            "Logs","ShooterGame.log"));
        string world=LocalFilePath(root,
            AsaSaveLayoutResolver.Resolve(map).WorldFile);
        return BeginFromPaths(map.Id,process.Pid,process.StartedUtc,
            root,log,world);
    }

    // Tests use disposable synthetic files. No production caller accepts
    // a path supplied by WebView2 or a remote peer.
    internal static Boundary? BeginFromPaths(Guid mapId,int pid,
        DateTimeOffset startedUtc,string root,string log,string world)
    {
        if(mapId==Guid.Empty||pid<=0||startedUtc==default)
            throw new InvalidDataException("UM-ASA-SAVE-PROCESS-IDENTITY");
        log=LocalFilePath(root,log);
        world=LocalFilePath(root,world);
        // A local .ark boundary is still observable when an ASA instance
        // writes no ShooterGame.log. Never manufacture a log completion.
        var file=File.Exists(log)?new FileInfo(log):null;
        var original=File.Exists(world)?new FileInfo(world):null;
        return new(mapId,pid,startedUtc,log,world,file?.Length??-1,
            file?.CreationTimeUtc??DateTime.MinValue,original?.Length??-1,
            original?.LastWriteTimeUtc??DateTime.MinValue);
    }

    public static async Task<Observation> ObserveAsync(Boundary bookmark,
        Func<bool> sameOwnedProcess,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        ArgumentNullException.ThrowIfNull(sameOwnedProcess);
        if(!sameOwnedProcess())throw new InvalidOperationException(
            "UM-ASA-SAVE-OWNED-PROCESS-CHANGED");
        string logIssue="";
        string lines="";
        if(bookmark.LogOffset<0||!File.Exists(bookmark.Log))
            logIssue="LOG_MISSING";
        else if((File.GetAttributes(bookmark.Log)&FileAttributes.ReparsePoint)!=0)
            logIssue="LOG_FILE_LINKED";
        else
        {
            var file=new FileInfo(bookmark.Log);
            if(file.CreationTimeUtc!=bookmark.LogCreatedUtc||
               file.Length<bookmark.LogOffset)
                logIssue="LOG_ROTATED_OR_TRUNCATED";
            else
            {
                long count=file.Length-bookmark.LogOffset;
                if(count>512*1024)logIssue="LOG_WINDOW_TOO_LARGE";
                else if(count>0)
                {
                    byte[] bytes=new byte[checked((int)count)];
                    using(var input=new FileStream(bookmark.Log,FileMode.Open,
                        FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,
                        65536,FileOptions.SequentialScan))
                    {
                        if(input.Length<bookmark.LogOffset+count)
                            logIssue="LOG_CHANGED";
                        else
                        {
                            input.Position=bookmark.LogOffset;
                            await input.ReadExactlyAsync(bytes,ct);
                            lines=Encoding.UTF8.GetString(bytes);
                        }
                    }
                }
            }
        }
        bool logValid=logIssue.Length==0;
        bool started=lines.Contains("Saving world",StringComparison.OrdinalIgnoreCase);
        bool complete=lines.Contains("World Save Complete",StringComparison.OrdinalIgnoreCase);
        // Old autosave records precede LogOffset. A completion before a new
        // "Saving world" line in the post-dispatch window is not accepted.
        int first=lines.IndexOf("Saving world",StringComparison.OrdinalIgnoreCase);
        int last=first<0?-1:lines.IndexOf("World Save Complete",
            first+"Saving world".Length,StringComparison.OrdinalIgnoreCase);
        bool ordered=logValid&&first>=0&&last>first;
        bool updated=false,stable=false;
        if(File.Exists(bookmark.World))
        {
            var firstWorld=new FileInfo(bookmark.World);
            if((firstWorld.Attributes&FileAttributes.ReparsePoint)!=0)
                return new(started,complete,false,false,false,false,"WORLD_LINKED");
            updated=firstWorld.Length>0&&
                (firstWorld.Length!=bookmark.PriorWorldBytes||
                 firstWorld.LastWriteTimeUtc>bookmark.PriorWorldWriteUtc);
            if(updated)
            {
                // Stabilized metadata is a weaker witness than ASA's own
                // paired log markers; it is never fsync/restore certification.
                await Task.Delay(1500,ct);
                var secondWorld=new FileInfo(bookmark.World);
                stable=secondWorld.Exists&&secondWorld.Length>0&&
                    secondWorld.Length==firstWorld.Length&&
                    secondWorld.LastWriteTimeUtc==firstWorld.LastWriteTimeUtc;
            }
        }
        if(!sameOwnedProcess())throw new InvalidOperationException(
            "UM-ASA-SAVE-OWNED-PROCESS-CHANGED");
        bool observed=ordered&&updated&&stable;
        string stage=observed?"ASA_LOG_AND_STABLE_WORLD_OBSERVED":
            updated&&stable&&logValid?"WORLD_UPDATED_STABLE_LOG_UNCONFIRMED":
            updated&&stable&&logIssue=="LOG_MISSING"?"WORLD_UPDATED_STABLE_LOG_MISSING":
            !logValid?logIssue:
            !ordered?"SAVE_LOG_COMPLETION_NOT_OBSERVED":
            !updated?"WORLD_FILE_NOT_UPDATED":"WORLD_FILE_NOT_STABLE";
        return new(started,complete,updated,stable,logValid,observed,stage);
    }

    public static async Task<Observation> WaitAsync(Boundary bookmark,
        Func<bool> sameOwnedProcess,TimeSpan maximum,CancellationToken ct=default,
        bool allowWorldMetadata=false)
    {
        if(maximum<=TimeSpan.Zero||maximum>TimeSpan.FromMinutes(3))
            throw new ArgumentOutOfRangeException(nameof(maximum));
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(maximum);
        Observation? last=null;
        try
        {
            while(true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                last=await ObserveAsync(bookmark,sameOwnedProcess,deadline.Token);
                if(last.CompletionObserved||
                   (allowWorldMetadata&&last.WorldMetadataObserved)||
                   (!last.LogIdentityValid&&last.Stage is not ("LOG_MISSING"
                       or "WORLD_UPDATED_STABLE_LOG_MISSING")))
                    return last;
                await Task.Delay(450,deadline.Token);
            }
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
        {
            return last??new(false,false,false,false,false,false,
                "SAVE_COMPLETION_TIMEOUT");
        }
    }
}
