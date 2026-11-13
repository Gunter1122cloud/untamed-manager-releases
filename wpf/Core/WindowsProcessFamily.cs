using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace UntamedManager.Core;
public sealed partial class WindowsProcessInspector
{
    public ProcessCheck InspectFamily(LocalMap map)
    {
        if(string.IsNullOrWhiteSpace(map.ExecutablePath)||!File.Exists(map.ExecutablePath))
            return new(false,"ASA server executable unavailable",null);
        // The API Loader is optional. A direct-mode map with no persisted
        // Loader parent must remain fully observable even if the optional
        // Loader path is configured but its binary was never installed.
        bool hasLoader= !string.IsNullOrWhiteSpace(map.ApiLoaderPath) &&
            File.Exists(map.ApiLoaderPath);
        if(!hasLoader)
        {
            if(map.StartWithAsaApi||map.LastLoader!=null)
                return new(false,"API Loader missing or previous Loader identity cannot be verified",null);
            return Inspect(map);
        }
        var observed=new List<ObservedProcess>();
        bool uncertain=false;
        string loader=Path.GetFullPath(map.ApiLoaderPath),server=Path.GetFullPath(map.ExecutablePath);
        try
        {
            using var query=new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine FROM Win32_Process");
            using var rows=query.Get();
            foreach(ManagementObject row in rows)
            {
                string path=Convert.ToString(row["ExecutablePath"])??"";
                if(string.IsNullOrEmpty(path))
                {
                    string name=Convert.ToString(row["Name"])??"";
                    if(name.Equals(Path.GetFileName(loader),StringComparison.OrdinalIgnoreCase)||name.Equals(Path.GetFileName(server),StringComparison.OrdinalIgnoreCase))uncertain=true;
                    continue;
                }
                bool isLoader=path.Equals(loader,StringComparison.OrdinalIgnoreCase),isServer=path.Equals(server,StringComparison.OrdinalIgnoreCase);
                if(!isLoader&&!isServer)continue;
                string command=Convert.ToString(row["CommandLine"])??"";
                bool loaderArgs=isLoader&&CommandMatches(command,loader,map.ApiLoaderArguments);
                bool serverArgs=isServer&&CommandMatches(command,server,map.Arguments);
                if(loaderArgs==serverArgs){uncertain=true;continue;}
                using var process=Process.GetProcessById(Convert.ToInt32(row["ProcessId"]));
                observed.Add(new(process.Id,Convert.ToInt32(row["ParentProcessId"]),new DateTimeOffset(process.StartTime.ToUniversalTime()),path,loaderArgs?map.ApiLoaderArguments:map.Arguments));
            }
            if(uncertain)return new(false,"Incomplete loader/server observation",null);
            var loaders=observed.Where(x=>x.ExecutablePath.Equals(loader,StringComparison.OrdinalIgnoreCase)&&x.Arguments==map.ApiLoaderArguments).ToArray();
            var children=observed.Where(x=>x.ExecutablePath.Equals(server,StringComparison.OrdinalIgnoreCase)&&x.Arguments==map.Arguments).ToArray();
            if(loaders.Length==0&&children.Length==0)
            {
                if(map.LastLoader!=null && DateTimeOffset.UtcNow-map.LastLoader.StartedUtc<TimeSpan.FromSeconds(60))
                    return new(false,"Loader startup grace period; duplicate launch blocked",null);
                return new(false,"Loader and server verified stopped",null,true);
            }
            if(loaders.Length==1&&children.Length==0)return new(false,"Loader running; server starting or unverified",null);
            if(children.Length!=1||loaders.Length>1)return new(false,"Ambiguous loader/server family",null);
            ProcessIdentity Identity(ObservedProcess p)=>new(){Pid=p.Pid,StartedUtc=p.StartedUtc,ExecutablePath=p.ExecutablePath,Arguments=p.Arguments};
            var child=Identity(children[0]);
            if(map.LastProcess?.Pid==child.Pid && map.LastProcess.StartedUtc!=child.StartedUtc)return new(false,"Reused server PID",null);
            if(loaders.Length==1)
            {
                var parent=Identity(loaders[0]);
                if(!ProcessFamily.IsVerifiedPair(observed,parent,child))return new(false,"Loader-child relationship unverified",null);
                return new(true,"Loader and server family verified",child,false,parent);
            }
            // Direct ASA is a supported launch mode even when an optional
            // Loader binary exists on disk. Never downgrade a known Loader
            // child to direct mode: that requires matching original parent
            // and child identities, even if the parent has already exited.
            if(loaders.Length==0 && ProcessFamily.IsVerifiedDirectServer(
                children[0],map.LastProcess,map.LastLoader,
                map.StartWithAsaApi,server,map.Arguments))
                return new(true,"Direct ASA server verified without Loader",child);
            // A loader may exit while its child remains alive. Windows does not
            // guarantee the orphan's ParentProcessId still equals the original
            // loader PID; use the exact persisted verified identities instead.
            if(ProcessFamily.IsPersistedSurvivor(children[0],map.LastProcess,map.LastLoader,
                server,map.Arguments,loader,map.ApiLoaderArguments))
                return new(true,"Verified server survives loader exit",child,false,map.LastLoader);
            return new(false,"Orphan server has no persisted family evidence",null);
        }
        catch(Exception ex){return new(false,"Family inspection failed: "+ex.Message,null);}
    }
}
