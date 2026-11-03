using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public sealed record FakeSteamReceipt(Guid Operation,string InstallRoot,
    int ExitCode,bool OutputConfirmed,bool IniPreserved,string State,
    DateTimeOffset EndedUtc);

// Execute a REAL child process but *only* our isolated FakeSteamCMD.exe test
// fixture. A SteamCMD.exe or actual ASA game directory is always rejected.
public static class SteamCmdTestRunner
{
    public static async Task<FakeSteamReceipt> RunAsync(string fakeExe,
        string installation,string localProfile,TimeSpan timeout,
        bool injectFailure=false,bool injectHang=false,bool injectIniChange=false,
        CancellationToken cancellationToken=default)
    {
        if(timeout<TimeSpan.FromSeconds(1)||timeout>TimeSpan.FromSeconds(30))
            throw new InvalidDataException("FakeSteamCMD timeout out of bounds");
        if(string.IsNullOrWhiteSpace(fakeExe) ||
           !Path.GetFileName(fakeExe).Equals("FakeSteamCMD.exe",
               StringComparison.OrdinalIgnoreCase) || !File.Exists(fakeExe) ||
           !Path.IsPathFullyQualified(fakeExe) || fakeExe.StartsWith(@"\\"))
            throw new InvalidDataException("Only local FakeSteamCMD.exe may be executed");
        string exe=Path.GetFullPath(fakeExe),root=Path.GetFullPath(installation);
        string profile=Path.GetFullPath(localProfile);
        if(!root.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase) ||
           !root.Contains("Untamed-Integration-",StringComparison.OrdinalIgnoreCase) ||
           !Directory.Exists(root) || root.StartsWith(@"\\") ||
           profile.StartsWith(@"\\"))
            throw new UnauthorizedAccessException("Only isolated TEMP ASA fixture may be updated");
        foreach(string path in new[]{exe,root,profile})
        {
            for(var d=new DirectoryInfo(Path.GetDirectoryName(path)!);d!=null;d=d.Parent)
                if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Linked FakeSteamCMD path rejected");
        }
        string configRoot=Path.Combine(root,"ShooterGame","Saved","Config","WindowsServer");
        var iniFiles=new[]{"Game.ini","GameUserSettings.ini"}
            .Select(x=>Path.Combine(configRoot,x)).Where(File.Exists).ToArray();
        var backups=iniFiles.ToDictionary(x=>x,
            x=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))),
            StringComparer.OrdinalIgnoreCase);
        var psi=new ProcessStartInfo(exe){
            WorkingDirectory=Path.GetDirectoryName(exe)!,
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"+force_install_dir",root,"+login","anonymous",
            "+app_update","2430930","validate","+quit"})psi.ArgumentList.Add(arg);
        if(injectFailure)psi.Environment["FAKE_STEAM_FAIL"]="1";
        if(injectHang)psi.Environment["FAKE_STEAM_TIMEOUT"]="1";
        if(injectIniChange)psi.Environment["FAKE_STEAM_TOUCH_INI"]="1";
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process=Process.Start(psi)??throw new IOException("FakeSteamCMD launch failed");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        bool timedOut=false;
        try{await process.WaitForExitAsync(deadline.Token);}
        catch(OperationCanceledException)
        {
            timedOut=true;
            if(!process.HasExited)try{process.Kill(true);}catch{}
            await process.WaitForExitAsync();
        }
        string output=await stdout;
        string error=await stderr;
        int code=process.ExitCode;
        bool success=!timedOut && code==0 &&
            output.Contains("Success! App '2430930' fully installed.",
                StringComparison.Ordinal);
        bool iniOk=backups.All(x=>File.Exists(x.Key) &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x.Key)))==x.Value);
        var receipt=new FakeSteamReceipt(Guid.NewGuid(),root,code,success,iniOk,
            timedOut?"timeout":!iniOk?"ini-mismatch":success?"fake-update-verified":
            "fake-update-failed",DateTimeOffset.UtcNow);
        string dir=Path.Combine(profile,"fake-steamcmd-operations");
        Directory.CreateDirectory(dir);
        string log=Path.Combine(dir,receipt.Operation.ToString("N")+".json");
        File.WriteAllText(log,JsonSerializer.Serialize(receipt));
        // Never leak raw process output or arbitrary local file contents to log.
        return receipt;
    }
}
