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
    bool ExplicitRetry=false,long MinimumFreeBytes=35L*1024*1024*1024,
    string? CustomMapLevel=null);
public sealed record SteamFreshInstallReceipt(Guid MapId,string InstallationRoot,
    string ServerExecutable,string? BuildId,DateTimeOffset InstalledUtc,
    string SteamCmdExecutable,string MapLevel);
public sealed record InterruptedSteamInstall(Guid MapId,string MapName,
    string InstallRoot,string SteamCmdExecutable,string Phase,DateTimeOffset ChangedUtc,
    string? MapLevel=null);
public sealed record CompletedSteamInstall(Guid MapId,string MapName,
    string InstallRoot,string ServerExecutable,string SteamCmdExecutable,
    string MapLevel,string BuildId,DateTimeOffset CompletedUtc);

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
    private static async Task<string> CaptureSteamOutput(StreamReader source,
        IProgress<SteamInstallProgress>? updates,bool stderr,CancellationToken ct)
    {
        var builder=new StringBuilder();
        char[] chunk=new char[2048];
        while(true)
        {
            int count=await source.ReadAsync(chunk.AsMemory(),ct);
            if(count==0)break;
            if(builder.Length+count>3_000_000)
                throw new InvalidDataException("SteamCMD output exceeded safety bound");
            builder.Append(chunk,0,count);
            if(!stderr)
            {
                // SteamCMD publishes percentages intermittently. Expose only
                // a numeric, bounded provider value; never invent progress.
                string tail=builder.ToString(Math.Max(0,builder.Length-4096),
                    Math.Min(builder.Length,4096));
                var matches=System.Text.RegularExpressions.Regex.Matches(tail,
                    @"progress:\s*(?<pct>\d+(?:\.\d+)?)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if(matches.Count>0 &&
                   double.TryParse(matches[matches.Count-1].Groups["pct"].Value,
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var percent) && percent is >=0 and <=100)
                    updates?.Report(new("download","SteamCMD letöltés / ellenőrzés",percent));
            }
        }
        // Callers may inspect Steam's success acknowledgement but must never
        // log these untrusted bytes (which may include credentials/paths).
        return builder.ToString();
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
        string Phase,DateTimeOffset ChangedUtc,string? MapName=null,
        string? MapLevel=null,string? BinarySha256=null,
        string? ManifestSha256=null);

    /// <summary>
    /// Read an interrupted SteamCMD transaction without mutating files.
    /// A new wizard can recover the original map ID only after the operator
    /// explicitly confirms the same root, map name and SteamCMD executable.
    /// </summary>
    public static InterruptedSteamInstall? InspectIncomplete(string chosenRoot)
    {
        string root=Canonical(chosenRoot);
        string file=OperationPath(root);
        if(!File.Exists(file))return null;
        if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0 ||
           new FileInfo(file).Length is <=0 or >16384)
            throw new InvalidDataException("Steam installation journal is corrupt");
        var entry=JsonSerializer.Deserialize<Journal>(File.ReadAllText(file)) ??
            throw new InvalidDataException("Steam installation journal is invalid");
        if(entry.MapId==Guid.Empty || !Same(entry.InstallRoot,root) ||
           entry.Phase is not ("prepared" or "mutating" or "complete"))
            throw new InvalidDataException("Steam installation journal has invalid state");
        if(entry.Phase=="complete")return null;
        if(string.IsNullOrWhiteSpace(entry.MapName))
            throw new InvalidDataException(
                "Legacy unfinished Steam journal has no map name; review manually");
        return new(entry.MapId,entry.MapName,root,entry.SteamExe,
            entry.Phase,entry.ChangedUtc,entry.MapLevel);
    }

    /// <summary>
    /// Recover a verified but not-yet-registered new ASA installation after
    /// the Windows Manager exited. This performs READS ONLY. It never runs
    /// SteamCMD, creates a map, edits an INI or touches the old Kova cluster.
    /// Registration must still be explicitly approved in the native bridge.
    /// A prepared/mutating journal is NOT a completed install.
    /// </summary>
    private static string FileSha256(string path)
    {
        using var bytes=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.Read,1024*1024,FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static CompletedSteamInstall? InspectCompleted(string chosenRoot)
    {
        string root=Canonical(chosenRoot);
        string file=OperationPath(root);
        if(!File.Exists(file))return null;
        if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||
           new FileInfo(file).Length is <=0 or >16384)
            throw new InvalidDataException("UM-FRESH-RECOVERY-JOURNAL-INVALID");
        Journal entry;
        try
        {
            entry=JsonSerializer.Deserialize<Journal>(File.ReadAllText(file)) ??
                throw new InvalidDataException("UM-FRESH-RECOVERY-JOURNAL-INVALID");
        }
        catch(JsonException ex)
        {
            throw new InvalidDataException("UM-FRESH-RECOVERY-JOURNAL-INVALID",ex);
        }
        if(entry.Phase!="complete")return null;
        if(entry.MapId==Guid.Empty||!Same(entry.InstallRoot,root)||
           string.IsNullOrWhiteSpace(entry.MapName)||
           string.IsNullOrWhiteSpace(entry.MapLevel)||
           string.IsNullOrWhiteSpace(entry.SteamExe))
            throw new InvalidDataException("UM-FRESH-RECOVERY-JOURNAL-INVALID");
        string originalLevel;
        try{originalLevel=AsaSteamMapCatalog.LevelFor(entry.MapName);}
        catch(InvalidDataException)
        {
            originalLevel=AsaSteamMapCatalog.ValidateCustomLevel(entry.MapLevel);
        }
        if(!string.Equals(originalLevel,entry.MapLevel,StringComparison.Ordinal))
            throw new InvalidDataException("UM-FRESH-RECOVERY-LEVEL-MISMATCH");
        string steam=Canonical(entry.SteamExe);
        // Both must still exist in the SAME fixed local installation root.
        var probe=LocalAsaInstallLocator.Probe(root);
        var build=AsaSteamBuildCatalog.InstalledBuild(root);
        if(!probe.HasServerExecutable || string.IsNullOrWhiteSpace(probe.ServerExecutable) ||
           string.IsNullOrWhiteSpace(build))
            throw new InvalidDataException("UM-FRESH-RECOVERY-FILES-UNVERIFIED");
        string binary=Canonical(probe.ServerExecutable);
        string manifest=Path.Combine(root,"steamapps","appmanifest_2430930.acf");
        // A completion journal from older builds without hashes is NOT
        // sufficient evidence to adopt an executable after a crash. The
        // operator may instead use the existing manual ASA import workflow.
        if(entry.BinarySha256 is not {Length:64} ||
           entry.ManifestSha256 is not {Length:64} ||
           !entry.BinarySha256.All(Uri.IsHexDigit) ||
           !entry.ManifestSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("UM-FRESH-RECOVERY-UNPINNED-COMPLETION");
        if(!string.Equals(FileSha256(binary),entry.BinarySha256,
                StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(FileSha256(manifest),entry.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-FRESH-RECOVERY-COMPLETED-FILES-CHANGED");
        if(!binary.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+
            Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
           !File.Exists(binary) ||
           (File.GetAttributes(binary)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-FRESH-RECOVERY-BINARY-OUTSIDE-ROOT");
        return new(entry.MapId,entry.MapName,root,binary,steam,
            originalLevel,build,entry.ChangedUtc);
    }

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
        byte[] validated=await SteamCmdZipValidator.ExtractExecutableAsync(
            archiveBytes,cancellationToken);
        string temp=exe+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            await using(var target=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                await target.WriteAsync(validated,cancellationToken);
                await target.FlushAsync(cancellationToken);
                target.Flush(true);
            }
            if(File.Exists(exe))throw new IOException("SteamCMD executable appeared during installation");
            File.Move(temp,exe);
            SteamCmdPreflight.Inspect(exe);
            progress?.Report(new("steamcmd","SteamCMD előkészítve; SHA-256 ujjlenyomat kiszámítva",null));
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
        string level=AsaSteamMapCatalog.LevelForNewInstallation(
            request.MapName,request.CustomMapLevel);
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
        using var mutex=new Semaphore(1,1,@"Global\Untamed-FreshSteam-v1-"+key);
        bool held=false;
        try
        {
            held=mutex.WaitOne(0);
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
                   !string.Equals(previous.MapName,request.MapName,
                       StringComparison.OrdinalIgnoreCase) ||
                   // A failed custom-map install can never resume with a
                   // different Unreal level under the same map/root identity.
                   !string.Equals(previous.MapLevel ??
                       AsaSteamMapCatalog.LevelFor(request.MapName),level,
                       StringComparison.Ordinal) ||
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
            // SteamCMD downloads shared ASA application files. The public
            // session name, launch arguments and ports are configured later,
            // after the binary and manifest have been verified.
            SaveJournal(journal,new(request.MapId,root,steam,"prepared",DateTimeOffset.UtcNow,
                request.MapName,level));
            steam=await PrepareSteamCmdAsync(steam,request.DownloadSteamCmd,progress,cancellationToken);
            Directory.CreateDirectory(root);
            // Test that the exact new root is writable; never touch a historical install.
            string probe=Path.Combine(root,".untamed-write-check-"+Guid.NewGuid().ToString("N"));
            try{File.WriteAllText(probe,"write-check");}
            finally{if(File.Exists(probe))File.Delete(probe);}
            cancellationToken.ThrowIfCancellationRequested();
            SaveJournal(journal,new(request.MapId,root,steam,"mutating",DateTimeOffset.UtcNow,
                request.MapName,level));
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
            Task<string> stdout=CaptureSteamOutput(
                process.StandardOutput,progress,false,deadline.Token);
            Task<string> stderr=CaptureSteamOutput(
                process.StandardError,null,true,deadline.Token);
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
            SaveJournal(journal,new(request.MapId,root,steam,"complete",DateTimeOffset.UtcNow,
                request.MapName,level,FileSha256(probeInstall.ServerExecutable!),
                FileSha256(Path.Combine(root,"steamapps","appmanifest_2430930.acf"))));
            progress?.Report(new("complete","ASA szerverfájlok ellenőrizve; szerver nincs elindítva",100));
            return new(request.MapId,root,probeInstall.ServerExecutable!,build,
                DateTimeOffset.UtcNow,steam,level);
        }
        finally{if(held)mutex.Release();}
    }
}
