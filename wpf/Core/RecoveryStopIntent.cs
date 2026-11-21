using System;
using System.IO;
using System.Text.Json;

namespace UntamedManager.Core;

// Durable intent belongs to the installation, not to a renderer tab or
// Manager process. Writers call Mark under the existing installation lease,
// BEFORE any SaveWorld/DoExit/kill can affect the approved process.
internal static class RecoveryStopIntent
{
    internal sealed record Intent(Guid MapId,int Pid,DateTimeOffset StartedUtc,DateTimeOffset MarkedUtc,Guid? RestartId=null);
    private static string PathFor(LocalMap map)
    {
        string dir=Path.Combine(UntamedOperationCoordinator.DefaultRoot,"RecoveryStopIntent-v1");
        _=UntamedOperationCoordinator.ResourceKey(dir);
        string path=Path.Combine(dir,UntamedOperationCoordinator.ResourceKey(map.InstallationPath)+".json");
        if(Directory.Exists(path)||(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0))
            throw new InvalidDataException("UM-WATCHDOG-STOP-INTENT-LINK");
        return path;
    }
    internal static Intent? Read(LocalMap map)
    {
        string path=PathFor(map);
        FileAttributes attrs;
        try{attrs=File.GetAttributes(path);}
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
        if((attrs&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0||
            new FileInfo(path).Length is <2 or >8192)
            throw new InvalidDataException("UM-WATCHDOG-STOP-INTENT-INVALID");
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var intent=JsonSerializer.Deserialize<Intent>(input);
        if(intent==null||intent.MapId==Guid.Empty||intent.Pid<=0||
            intent.StartedUtc==default||intent.MarkedUtc<intent.StartedUtc)
            throw new InvalidDataException("UM-WATCHDOG-STOP-INTENT-INVALID");
        return intent;
    }
    internal static void Mark(LocalMap map,ProcessIdentity process,Guid? restartId=null)
    {
        if(map.Id==Guid.Empty||process.Pid<=0||process.StartedUtc==default)
            throw new InvalidDataException("UM-WATCHDOG-STOP-IDENTITY");
        string path=PathFor(map);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,
                FileShare.None,4096,FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output,new Intent(map.Id,process.Pid,process.StartedUtc,DateTimeOffset.UtcNow,restartId));
                output.Flush(true);
            }
            File.Move(tmp,path,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    internal sealed record NativeStart(Guid MapId,int Pid,DateTimeOffset StartedUtc,
        string ExecutablePath,DateTimeOffset RecordedUtc,Guid? RestartId,Guid? RecoveryClaim=null);
    private static string StartPath(LocalMap map)
    {
        string path=PathFor(map)+"."+map.Id.ToString("N")+".start";
        if(Directory.Exists(path)||(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0))
            throw new InvalidDataException("UM-WATCHDOG-START-INTENT-LINK");
        return path;
    }
    // Called under the native installation lease AFTER saved process identity
    // and successful native launch. No renderer or process discovery may mint it.
    internal static void RecordNativeStart(LocalMap map,ProcessIdentity process,Guid? restartId)
    {
        if(map.Id==Guid.Empty||process.Pid<=0||process.StartedUtc==default||
           string.IsNullOrWhiteSpace(process.ExecutablePath))
            throw new InvalidDataException("UM-WATCHDOG-START-IDENTITY");
        string path=StartPath(map);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output,new NativeStart(map.Id,process.Pid,process.StartedUtc,
                    process.ExecutablePath,DateTimeOffset.UtcNow,restartId,RecoveryNativeStartBoundary.Claim));output.Flush(true);
            }
            File.Move(tmp,path,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    internal static NativeStart? ReadNativeStart(LocalMap map)
    {
        string path=StartPath(map);
        if(!File.Exists(path))return null;
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(input.Length is <2 or >16384)throw new InvalidDataException("UM-WATCHDOG-START-INTENT-INVALID");
        var row=JsonSerializer.Deserialize<NativeStart>(input);
        if(row==null||row.MapId!=map.Id||row.Pid<=0||row.StartedUtc==default||
            row.RecordedUtc<row.StartedUtc||string.IsNullOrWhiteSpace(row.ExecutablePath))
            throw new InvalidDataException("UM-WATCHDOG-START-INTENT-INVALID");
        return row;
    }
    internal static bool MatchesNativeStart(NativeStart receipt,ProcessIdentity? process)=>
        process!=null&&receipt.Pid==process.Pid&&receipt.StartedUtc==process.StartedUtc&&
        string.Equals(receipt.ExecutablePath,process.ExecutablePath,StringComparison.OrdinalIgnoreCase);
    internal static bool Suppresses(LocalMap map,int pid,DateTimeOffset started,DateTimeOffset armed)
    {
        var intent=Read(map);
        // Also suppress a later intentional stop on the same installation,
        // even if a new process ran between watchdog observations.
        return intent!=null&&((intent.Pid==pid&&intent.StartedUtc==started)||
            intent.MarkedUtc>=armed);
    }
}
