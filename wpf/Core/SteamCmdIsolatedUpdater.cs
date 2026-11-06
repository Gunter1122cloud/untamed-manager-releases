using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// T11: genuine SteamCMD child-process adapter confined to an isolated Windows
// lab installation. It never changes the historical Store, live servers or DB.
// The production LiveSteamUpdate gate remains separate and closed.
public sealed record SteamLabUpdateReceipt(Guid Operation,DateTimeOffset CompletedUtc,
    string InstallRoot,string? BuildBefore,string? BuildAfter,
    int ExitCode,bool ProtectedFilesPreserved,bool SteamAcknowledged,
    string State)
{
    public bool ProductionUpdateCertified=>false;
}

public static class SteamCmdIsolatedUpdater
{
    private static readonly string[] ProtectedRoots={
        Path.Combine("ShooterGame","Saved"),
        Path.Combine("ShooterGame","Binaries","Win64","ArkApi","Plugins")
    };
    private static string Fixed(string raw,bool mustExist)
    {
        if(string.IsNullOrWhiteSpace(raw)||!Path.IsPathFullyQualified(raw)||
           raw.StartsWith(@"\\",StringComparison.Ordinal)||raw.StartsWith("//",StringComparison.Ordinal))
            throw new InvalidDataException("Steam lab requires an absolute local path");
        string path=Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar);
        if(path.Contains('~')||new DriveInfo(Path.GetPathRoot(path)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Steam lab refuses aliases and non-local disks");
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(path)!);dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked Steam lab parent refused");
        if(mustExist&&!File.Exists(path)&&!Directory.Exists(path))
            throw new InvalidDataException("Required Steam lab file/directory missing");
        if((File.Exists(path)||Directory.Exists(path))&&
           (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked Steam lab target refused");
        return path;
    }

    private static bool IsLabInstallation(string install)
    {
        string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if(!install.StartsWith(temp+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            return false;
        string remainder=install[(temp.Length+1)..];
        string first=remainder.Split(Path.DirectorySeparatorChar)[0];
        return remainder.Contains(Path.DirectorySeparatorChar)&&
            (first.StartsWith("Untamed-Integration-",StringComparison.Ordinal)||
             first.StartsWith("Untamed-SteamLab-",StringComparison.Ordinal));
    }

    private static string Digest(string file)
    {
        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Dictionary<string,string> ProtectedSnapshot(string install)
    {
        var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string relative in ProtectedRoots)
        {
            string dir=Path.Combine(install,relative);
            if(!Directory.Exists(dir))continue;
            var pending=new Stack<string>();pending.Push(dir);
            while(pending.Count>0)
            {
                string current=pending.Pop();
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Protected Steam lab directory is linked");
                foreach(string child in Directory.GetDirectories(current))pending.Push(child);
                foreach(string file in Directory.GetFiles(current))
                {
                    if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                        throw new InvalidDataException("Protected Steam lab file is linked");
                    if(files.Count>=50000)throw new InvalidDataException("Protected file inventory exceeds safety bound");
                    files.Add(Path.GetRelativePath(install,file),Digest(file));
                }
            }
        }
        return files;
    }
    private static bool Preserved(Dictionary<string,string> before,Dictionary<string,string> after)=>
        before.Count==after.Count&&before.All(x=>
            after.TryGetValue(x.Key,out string? digest)&&digest==x.Value);

    public static Task<SteamLabUpdateReceipt> RunAsync(
        string steamCmdExecutable,string installRoot,string profileRoot,
        LocalMap[] registeredLocalMaps,Func<LocalMap,ProcessCheck> inspect,
        long expectedDownloadBytes,TimeSpan timeout,
        CancellationToken cancellationToken=default) =>
        RunCoreAsync(steamCmdExecutable,steamCmdExecutable,installRoot,profileRoot,
            registeredLocalMaps,inspect,expectedDownloadBytes,timeout,false,cancellationToken);

    // The CI entry exercises the same process/commit/journal path against the
    // separately compiled harmless FakeSteamCMD binary. No production UI calls it.
    internal static Task<SteamLabUpdateReceipt> RunFakeFixtureAsync(
        string steamCmdPlaceholder,string fakeExecutable,string installRoot,string profileRoot,
        LocalMap[] registeredLocalMaps,Func<LocalMap,ProcessCheck> inspect,
        long expectedDownloadBytes,TimeSpan timeout,
        CancellationToken cancellationToken=default) =>
        RunCoreAsync(steamCmdPlaceholder,fakeExecutable,installRoot,profileRoot,
            registeredLocalMaps,inspect,expectedDownloadBytes,timeout,true,cancellationToken);

    private static async Task<SteamLabUpdateReceipt> RunCoreAsync(
        string preflightExe,string processExe,string installRoot,string profileRoot,
        LocalMap[] localMaps,Func<LocalMap,ProcessCheck> inspect,
        long estimate,TimeSpan timeout,bool fixture,CancellationToken cancellationToken)
    {
        if(timeout<TimeSpan.FromSeconds(2)||timeout>TimeSpan.FromHours(2))
            throw new InvalidDataException("Steam lab timeout outside approved bounds");
        cancellationToken.ThrowIfCancellationRequested();
        string install=Fixed(installRoot,true);
        string executable=Fixed(processExe,true);
        string profile=Fixed(profileRoot,true);
        if(!IsLabInstallation(install)||!IsLabInstallation(profile)||
           !Path.GetFileName(executable).Equals(fixture?"FakeSteamCMD.exe":"steamcmd.exe",
               StringComparison.OrdinalIgnoreCase)||
           !File.Exists(executable))
            throw new UnauthorizedAccessException("Only an isolated Steam lab installation may be updated");
        if(install.StartsWith(profile+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
           profile.StartsWith(install+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
           string.Equals(install,profile,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Steam lab installation and profile overlap");

        // Recompute the real safety preflight here: callers cannot supply an
        // editable preview, a fabricated 'stopped' flag, or a fake map count.
        var plan=SteamUpdateDryRun.Plan(preflightExe,install,localMaps,inspect,estimate);
        if(!plan.DryRunAllowed||plan.Maps.Length==0)
            throw new InvalidOperationException("The registered local map group is not safely stopped");
        if(localMaps.Any(m=>m==null||
           !string.Equals(Path.GetFullPath(m.InstallationPath).TrimEnd(Path.DirectorySeparatorChar),
               install,StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("All update participants must belong to the exact lab installation");

        using var operation=new UntamedOperationCoordinator(
            Path.Combine(profile,"steam-lab-operations")).Acquire(install,UntamedOperationKind.Update);
        // The second process observation is after the durable exclusive lease.
        foreach(var map in localMaps)
        {
            var state=inspect(map);
            if(!state.ConfirmedStopped||state.Running||state.Identity!=null||
               state.LoaderIdentity!=null)
                throw new InvalidOperationException("Steam lab process started after preflight");
        }
        var protectedBefore=ProtectedSnapshot(install);
        string buildBefore=plan.InstalledBuild??"";
        var psi=new ProcessStartInfo(executable){
            WorkingDirectory=Path.GetDirectoryName(executable)!,
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            RedirectStandardInput=false
        };
        foreach(var arg in new[]{"+force_install_dir",install,"+login","anonymous",
                  "+app_update","2430930","validate","+quit"})
            psi.ArgumentList.Add(arg);
        foreach(string variable in new[]{"STEAM_USERNAME","STEAM_PASSWORD","MYSQL_PWD",
            "MARIADB_PWD","UNTAMED_RCON_PASSWORD"})psi.Environment.Remove(variable);

        cancellationToken.ThrowIfCancellationRequested();
        operation.MarkMutationStarted();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process=Process.Start(psi) ??
            throw new IOException("SteamCMD could not start in the isolated lab");
        async Task<(string Content,bool Truncated)> Capture(StreamReader reader)
        {
            var sb=new StringBuilder();
            char[] buffer=new char[2048];bool truncated=false;
            for(;;)
            {
                int count=await reader.ReadAsync(buffer.AsMemory(),deadline.Token);
                if(count==0)break;
                int take=Math.Min(count,Math.Max(0,65536-sb.Length));
                if(take>0)sb.Append(buffer,0,take);
                if(take!=count)truncated=true;
            }
            return(sb.ToString(),truncated);
        }
        (string Content,bool Truncated) stdout;
        try
        {
            var outTask=Capture(process.StandardOutput);
            var errTask=Capture(process.StandardError);
            await process.WaitForExitAsync(deadline.Token);
            stdout=await outTask;
            var stderr=await errTask;
            if(stdout.Truncated||stderr.Truncated)
                throw new InvalidDataException("SteamCMD output exceeded bounded transport capture");
        }
        catch
        {
            if(!process.HasExited)
            {
                try{process.Kill(entireProcessTree:true);}catch{}
                try{await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));}
                catch{}
            }
            throw;
        }
        // Never log the untrusted stdout/stderr: either may contain accounts,
        // tokens, private paths or mod metadata.
        bool acknowledged=process.ExitCode==0&&
            stdout.Content.Contains("Success! App '2430930' fully installed.",StringComparison.Ordinal);
        var protectedAfter=ProtectedSnapshot(install);
        bool unchanged=Preserved(protectedBefore,protectedAfter);
        foreach(var map in localMaps)
        {
            var state=inspect(map);
            if(state.Running||!state.ConfirmedStopped||
               state.Identity!=null||state.LoaderIdentity!=null)
                throw new InvalidOperationException("Steam lab map process changed during update");
        }
        if(!acknowledged||!unchanged)
            throw new InvalidDataException("Steam lab update did not pass output and persistent-file integrity checks");
        // Final manifest is evidence of the local CLI test only; not a verified
        // live Steam build or authorization to restart ASA.
        string? buildAfter=null;
        try
        {
            var updated=SteamUpdateDryRun.Plan(preflightExe,install,localMaps,inspect,estimate);
            buildAfter=updated.InstalledBuild;
        }
        catch(Exception){/* Build metadata is optional, never a readiness assertion. */ }
        var receipt=new SteamLabUpdateReceipt(Guid.NewGuid(),DateTimeOffset.UtcNow,install,
            string.IsNullOrWhiteSpace(buildBefore)?null:buildBefore,buildAfter,
            process.ExitCode,unchanged,acknowledged,"isolated-steamcmd-acknowledged");
        string outputFolder=Path.Combine(profile,"steam-lab-results");
        Directory.CreateDirectory(outputFolder);
        string record=Path.Combine(outputFolder,receipt.Operation.ToString("N")+".json");
        using(var stream=new FileStream(record,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            JsonSerializer.Serialize(stream,receipt);
            stream.Flush(true);
        }
        operation.Complete();
        return receipt;
    }
}
