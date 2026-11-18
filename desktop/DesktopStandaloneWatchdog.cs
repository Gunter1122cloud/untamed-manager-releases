using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using UntamedManager.Core;

namespace UntamedDesktop;

// Only a packaged, fixed local, read-only supervisor is launched. The copy
// is outside the Manager installation, so a Manager update does not stop it
// or overwrite its running code. No game-process control is performed here.
internal static class DesktopStandaloneWatchdog
{
    internal static SupervisorSnapshot? Read(ClusterService cluster)
    {
        try
        {
            if(!File.Exists(Path.Combine(cluster.Store.Root,"supervision","status.signed.json")))return null;
            var snapshot=new SupervisorChannel(cluster.Store.Root).Read(cluster.Config.LocalHostId);
            using var process=Process.GetProcessById(snapshot.Pid);
            if(process.HasExited||new DateTimeOffset(process.StartTime.ToUniversalTime())!=snapshot.StartedUtc)return null;
            if(!string.Equals(process.MainModule?.FileName,snapshot.Executable,StringComparison.OrdinalIgnoreCase))return null;
            return snapshot;
        }
        catch{return null;}
    }

    internal static int Start(ClusterService cluster)
    {
        var existing=Read(cluster);
        if(existing!=null)return existing.Pid;
        string source=Path.Combine(AppContext.BaseDirectory,"UntamedSupervisor.exe");
        _=UntamedOperationCoordinator.ResourceKey(Path.GetDirectoryName(source)!);
        if(!File.Exists(source)||(File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("UM-OPS-WATCHDOG-BINARY-MISSING");
        using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length<1||input.Length>256L*1024*1024)throw new InvalidDataException("UM-OPS-WATCHDOG-BINARY-SIZE");
        string hash=Convert.ToHexString(SHA256.HashData(input));
        string directory=Path.Combine(cluster.Store.Root,"watchdog-runtime",hash);
        _=UntamedOperationCoordinator.ResourceKey(directory);
        Directory.CreateDirectory(directory);
        string target=Path.Combine(directory,"UntamedSupervisor.exe");
        if(!File.Exists(target))
        {
            string temp=target+"."+Guid.NewGuid().ToString("N")+".part";
            try
            {
                input.Position=0;
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {input.CopyTo(output);output.Flush(true);}
                File.Move(temp,target,false);
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        using(var verify=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if((File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0||
               Convert.ToHexString(SHA256.HashData(verify))!=hash)
                throw new InvalidDataException("UM-OPS-WATCHDOG-BINARY-CHANGED");
        }
        var start=new ProcessStartInfo(target){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=false};
        start.ArgumentList.Add("--root");start.ArgumentList.Add(cluster.Store.Root);
        start.ArgumentList.Add("--watchdog-only");
        using var process=Process.Start(start)??throw new IOException("UM-OPS-WATCHDOG-START");
        return process.Id;
    }
}
