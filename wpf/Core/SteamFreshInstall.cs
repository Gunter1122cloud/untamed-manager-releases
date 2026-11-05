using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public sealed record SteamInstallProgress(string Stage,string Detail,double? Percent);
public sealed record SteamFreshInstallRequest(Guid MapId,string MapName,string SessionName,
    string InstallRoot,string SteamCmdExecutable,bool DownloadSteamCmd,
    bool ExplicitRetry=false,long MinimumFreeBytes=35L*1024*1024*1024);
public sealed record SteamFreshInstallReceipt(Guid MapId,string InstallationRoot,
    string ServerExecutable,string? BuildId,DateTimeOffset InstalledUtc,
    string SteamCmdExecutable,string MapLevel);

public static class SteamFreshInstall
{
    public const string OfficialSteamCmdZip="https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    private static string Canonical(string input)
    {
        if(string.IsNullOrWhiteSpace(input)||!Path.IsPathFullyQualified(input) ||
           input.StartsWith(@"\\",StringComparison.Ordinal) ||
           input.StartsWith("//",StringComparison.Ordinal) ||
           input.Contains('~'))
            throw new InvalidDataException("Only an explicit, non-aliased fixed local Windows path is allowed");
        string full=Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Steam installation must be on a fixed local disk");
        for(var dir=new DirectoryInfo(full);dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Steam installation cannot traverse junctions/links");
        return full;
    }
    private static bool Same(string a,string b)=>string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),StringComparison.OrdinalIgnoreCase);
    private static bool Overlap(string a,string b)=>
        Same(a,b) || a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
        b.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static string OperationPath(string install)
    {
        string key=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(install.ToUpperInvariant())));
        string folder=Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),"UntamedManager","FreshSteamInstall-v1");
        return Path.Combine(folder,key+".json");
    }
    private sealed record Journal(Guid MapId,string InstallRoot,string SteamExe,
        string Phase,DateTimeOffset ChangedUtc);

    private static void SaveJournal(string file,Journal value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var fs=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(fs,value);fs.Flush(true);}
            File.Move(tmp,file,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }

    /// <summary>
    /// Official Valve ZIP is offered explicitly, never silently downloaded.
    /// Archives are bounded; only the named steamcmd.exe is extracted; neither
    /// arbitrary ZIP paths nor scripts can be written to user directories.
    /// </summary>
    public static async Task<string> PrepareSteamCmdAsync(string executable,
        bool allowOfficialDownload,IProgress<SteamInstallProgress>? progress=null,
        CancellationToken cancellationToken=default)
    {
        string exe=Canonical(executable);
        if(!Path.GetFileName(exe).Equals("steamcmd.exe",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SteamCMD path must end in steamcmd.exe");
        if(File.Exists(exe))
        {
            SteamCmdPreflight.Inspect(exe);
            progress?.Report(new("steamcmd","Meglévő steamcmd.exe ellenőrizve",null));
            return exe;
        }
        if(!allowOfficialDownload)
            throw new FileNotFoundException("SteamCMD missing: approve official Valve download",exe);
        var folder=Path.GetDirectoryName(exe)!;
        if(Directory.Exists(folder)&&Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException("Refusing to install SteamCMD into a nonempty unknown directory");
        Directory.CreateDirectory(folder);
        var zip=new Uri(OfficialSteamCmdZip);
        using var handler=new HttpClientHandler{AllowAutoRedirect=false};
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromMinutes(4)};
        progress?.Report(new("steamcmd","Valve SteamCMD HTTPS-csomag letöltése",null));
        using var resp=await client.GetAsync(zip,HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if(resp.StatusCode!=HttpStatusCode.OK ||
           resp.Content.Headers.ContentLength is > 50_000_000)
            throw new IOException("Official SteamCMD download refused or oversized");
        await using var archiveBytes=new MemoryStream();
        await using(var network=await resp.Content.ReadAsStreamAsync(cancellationToken))
        {
            byte[] buffer=new byte[81920];
            int length;
            while((length=await network.ReadAsync(buffer,cancellationToken))>0)
            {
                if(archiveBytes.Length+length>50_000_000)
                    throw new InvalidDataException("SteamCMD ZIP exceeds size limit");
                await archiveBytes.WriteAsync(buffer.AsMemory(0,length),cancellationToken);
            }
        }
        archiveBytes.Position=0;
        using var archive=new ZipArchive(archiveBytes,ZipArchiveMode.Read,true);
        var candidates=archive.Entries.Where(e=>e.FullName.Equals("steamcmd.exe",
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if(candidates.Length!=1 || candidates[0].Length is <=0 or > 50_000_000)
            throw new InvalidDataException("Valve archive does not contain exactly one SteamCMD executable");
        string temp=exe+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            await using(var target=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            await using(var source=candidates[0].Open())
            {
                await source.CopyToAsync(target,cancellationToken);
                await target.FlushAsync(cancellationToken);
                target.Flush(true);
            }
            if(File.Exists(exe))throw new IOException("SteamCMD executable appeared during installation");
            File.Move(temp,exe);
            SteamCmdPreflight.Inspect(exe);
            progress?.Report(new("steamcmd","SteamCMD előkészítve; a fájl SHA-256 ellenőrizve",null));
            return exe;
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    /// <summary>
    /// Real SteamCMD installation is permitted only into a brand-new local root
    /// or an explicitly retried, owned incomplete root. No map is added to
    /// Manager config until both executable and manifest are verified.
    /// It NEVER starts the ASA dedicated server.
    /// </summary>
    public static Task<SteamFreshInstallReceipt> InstallAsync(
        SteamFreshInstallRequest request,LocalMap[] registeredLocalMaps,
        IProgress<SteamInstallProgress>? progress=null,CancellationToken cancellationToken=default) =>
        RunCoreAsync(request,registeredLocalMaps,progress,null,cancellationToken);

    // Isolated real-child CI adapter. It cannot execute arbitrary files or be
    // invoked through the WPF Manager production button.
    internal static Task<SteamFreshInstallReceipt> RunFakeFixtureAsync(
        SteamFreshInstallRequest request,LocalMap[] registeredLocalMaps,
        string fakeSteamCmd,IProgress<SteamInstallProgress>? progress=null,
        CancellationToken cancellationToken=default)
    {
        string root=Path.GetFullPath(request.InstallRoot);
        if(!root.StartsWith(Path.GetFullPath(Path.GetTempPath()),
            StringComparison.OrdinalIgnoreCase) ||
           !root.Contains("Untamed-Integration-",StringComparison.OrdinalIgnoreCase) ||
           !Path.GetFileName(fakeSteamCmd).Equals("FakeSteamCMD.exe",
               StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Fake Steam fresh installation must be isolated");
        return RunCoreAsync(request,registeredLocalMaps,progress,fakeSteamCmd,cancellationToken);
    }

    private static async Task<SteamFreshInstallReceipt> RunCoreAsync(
        SteamFreshInstallRequest request,LocalMap[] registeredLocalMaps,
        IProgress<SteamInstallProgress>? progress,string? fakeExecutable,
        CancellationToken cancellationToken)
    {
        if(request.MapId==Guid.Empty)throw new InvalidDataException("Missing local map ID");
        string level=AsaSteamMapCatalog.LevelFor(request.MapName);
        string root=Canonical(request.InstallRoot);
        string steam=Canonical(request.SteamCmdExecutable);
        if(Overlap(root,Path.GetDirectoryName(steam)!))
            throw new InvalidDataException("SteamCMD and ASA installation must use separate folders");
        if(registeredLocalMaps.Any(m=>m==null || (
            !string.IsNullOrWhiteSpace(m.InstallationPath) &&
            Overlap(root,Canonical(m.InstallationPath)))))
            throw new InvalidOperationException("New install overlaps an existing local map installation");
        if(request.MinimumFreeBytes<5L*1024*1024*1024 ||
           request.MinimumFreeBytes>500L*1024*1024*1024)
            throw new InvalidDataException("Explicit Steam download free-space reserve is invalid");
        var disk=new DriveInfo(Path.GetPathRoot(root)!);
        if(disk.AvailableFreeSpace<request.MinimumFreeBytes)
            throw new IOException("Insufficient free disk space to install ASA");
        string journal=OperationPath(root);
        string key=Path.GetFileNameWithoutExtension(journal);
        using var mutex=new Mutex(false,@"Global\Untamed-FreshSteam-v1-"+key);
        bool held=false;
        try
        {
            try{held=mutex.WaitOne(0);}
            catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("Another Manager owns this Steam installation");
            Journal? previous=null;
            if(File.Exists(journal))
            {
                if((File.GetAttributes(journal)&FileAttributes.ReparsePoint)!=0 ||
                   new FileInfo(journal).Length>16384)
                    throw new InvalidDataException("Fresh-install journal tampered");
                previous=JsonSerializer.Deserialize<Journal>(File.ReadAllText(journal)) ??
                    throw new InvalidDataException("Fresh-install journal invalid");
            }
            bool rootHasFiles=Directory.Exists(root) &&
                Directory.EnumerateFileSystemEntries(root).Any();
            if(previous==null && rootHasFiles)
                throw new IOException("New installation root is not empty; import it instead");
            if(previous!=null)
            {
                if(previous.MapId!=request.MapId || !Same(previous.InstallRoot,root)||
                   !Same(previous.SteamExe,steam) || previous.Phase=="complete" ||
                   !request.ExplicitRetry)
                    throw new InvalidOperationException(
                        "Incomplete Steam install needs explicit review/retry for the same local map");
                string saved=Path.Combine(root,"ShooterGame","Saved");
                if(Directory.Exists(saved) &&
                   Directory.EnumerateFileSystemEntries(saved,"*",SearchOption.AllDirectories).Any())
                    throw new InvalidOperationException(
                        "Retry blocked: save/config files appeared in incomplete installation");
            }
            cancellationToken.ThrowIfCancellationRequested();
            _=AsaSteamMapCatalog.LaunchArguments(request.MapName,request.SessionName,7777,27015);
            SaveJournal(journal,new(request.MapId,root,steam,"prepared",DateTimeOffset.UtcNow));
            steam=await PrepareSteamCmdAsync(steam,request.DownloadSteamCmd,progress,cancellationToken);
            Directory.CreateDirectory(root);
            // Test that the exact new root is writable; never touch a historical install.
            string probe=Path.Combine(root,".untamed-write-check-"+Guid.NewGuid().ToString("N"));
            try{File.WriteAllText(probe,"write-check");}
            finally{if(File.Exists(probe))File.Delete(probe);}
            cancellationToken.ThrowIfCancellationRequested();
            SaveJournal(journal,new(request.MapId,root,steam,"mutating",DateTimeOffset.UtcNow));
            progress?.Report(new("download","ASA 2430930 telepítése / ellenőrzése",null));
            var psi=new ProcessStartInfo(fakeExecutable??steam)
            {
                WorkingDirectory=Path.GetDirectoryName(steam)!,
                UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true
            };
            foreach(string arg in new[]{"+force_install_dir",root,"+login","anonymous",
                "+app_update","2430930","validate","+quit"})
                psi.ArgumentList.Add(arg);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromHours(5));
            if(fakeExecutable!=null)
            {
                psi.Environment["FAKE_STEAM_NEW_INSTALL"]="1";
                progress?.Report(new("test","Izolált FakeSteamCMD teszt, nem éles Steam",null));
            }
            using var process=Process.Start(psi)??throw new IOException("SteamCMD launch failed");
            Task<string> stdout=process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> stderr=process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                string output=await stdout;
                _=await stderr; // untrusted log output is never persisted
                if(output.Length>3_000_000 || process.ExitCode!=0 ||
                   !output.Contains("Success! App '2430930' fully installed.",
                       StringComparison.Ordinal))
                    throw new InvalidDataException("SteamCMD did not confirm ASA 2430930 installation");
            }
            catch
            {
                if(!process.HasExited)
                    try{process.Kill(entireProcessTree:true);}catch{}
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var probeInstall=LocalAsaInstallLocator.Probe(root);
            var build=AsaSteamBuildCatalog.InstalledBuild(root);
            if(!probeInstall.HasServerExecutable || build==null)
                throw new InvalidDataException("SteamCMD exited but ASA EXE / appmanifest is not verified");
            SaveJournal(journal,new(request.MapId,root,steam,"complete",DateTimeOffset.UtcNow));
            progress?.Report(new("complete","ASA szerverfájlok ellenőrizve; szerver nincs elindítva",100));
            return new(request.MapId,root,probeInstall.ServerExecutable!,build,
                DateTimeOffset.UtcNow,steam,level);
        }
        finally{if(held)mutex.ReleaseMutex();}
    }
}
