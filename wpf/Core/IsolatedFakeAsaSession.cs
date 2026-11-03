using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// ACTUAL Windows child-process lifecycle integration fixture, not an ASA
// runtime adapter. All writes are confined to an Untamed-Integration-* TEMP
// mailbox; only an executable exactly named FakeAsa.exe may be started/killed.
// This proves the state machine mechanics, NOT real ASA SaveWorld semantics.
public sealed class IsolatedFakeAsaSession : ILocalServerSession, IDisposable
{
    private sealed record LifeAck(string Token,string Command,int Pid,
        bool SaveProof,DateTimeOffset CompletedUtc);
    private readonly string executable,root;
    private readonly bool denySaveProof;
    private Process? process;
    private ProcessIdentity? identity;
    private LifeAck? saveAck;
    private string? lastSaveToken;
    private DateTimeOffset saveRequestedUtc;
    private bool started;

    public IsolatedFakeAsaSession(string executable,string root,bool denySaveProof=false)
    {
        if(string.IsNullOrWhiteSpace(executable)||!Path.IsPathFullyQualified(executable)||
           !Path.GetFileName(executable).Equals("FakeAsa.exe",
               StringComparison.OrdinalIgnoreCase)||!File.Exists(executable)||
           (File.GetAttributes(executable)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Only explicit local FakeAsa.exe test executable permitted");
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\"))
            throw new InvalidDataException("Isolated FakeAsa test mailbox required");
        this.root=Path.GetFullPath(root);
        string temp=Path.GetFullPath(Path.GetTempPath());
        if(!this.root.StartsWith(temp,StringComparison.OrdinalIgnoreCase) ||
           !this.root.Contains("Untamed-Integration-",StringComparison.OrdinalIgnoreCase)||
           new DriveInfo(Path.GetPathRoot(this.root)!).DriveType!=DriveType.Fixed)
            throw new UnauthorizedAccessException("FakeAsa lifecycle can only run in a CI TEMP fixture");
        for(var dir=new DirectoryInfo(this.root);dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked FakeAsa fixture root rejected");
        this.executable=Path.GetFullPath(executable);
        this.denySaveProof=denySaveProof;
        Directory.CreateDirectory(this.root);
    }

    public ProcessCheck Check()
    {
        if(process==null)return new(false,"FakeAsa test not yet started",null,true);
        try
        {
            if(process.HasExited)return new(false,"FakeAsa test exited",null,true);
            var startedUtc=new DateTimeOffset(process.StartTime.ToUniversalTime());
            if(identity==null ||identity.Pid!=process.Id ||
               identity.StartedUtc!=startedUtc ||
               !identity.ExecutablePath.Equals(executable,
                   StringComparison.OrdinalIgnoreCase))
                return new(false,"Unverified FakeAsa process identity",null);
            return new(true,"FakeAsa test running",identity);
        }
        catch(InvalidOperationException){return new(false,"FakeAsa process unknown",null);}
    }

    public void Start()
    {
        if(Check().Running)throw new InvalidOperationException("FakeAsa already running");
        if(process!=null && !process.HasExited)
            throw new InvalidOperationException("FakeAsa previous process state uncertain");
        process?.Dispose();
        var psi=new ProcessStartInfo(executable){
            CreateNoWindow=true,UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable)!};
        psi.ArgumentList.Add("--lifecycle-fixture");
        psi.ArgumentList.Add(root);
        if(denySaveProof)psi.ArgumentList.Add("--no-save-proof");
        process=Process.Start(psi)??throw new IOException("Failed to launch isolated FakeAsa");
        identity=new(){Pid=process.Id,StartedUtc=new DateTimeOffset(process.StartTime.ToUniversalTime()),
            ExecutablePath=executable,Arguments="--lifecycle-fixture"};
        saveAck=null;lastSaveToken=null;started=true;
    }

    public async Task Command(string command,CancellationToken ct)
    {
        if(command is not "SaveWorld" and not "DoExit" &&
           !command.StartsWith("ServerChat Server maintenance: ",
               StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Only lifecycle FakeAsa commands are accepted");
        if(!Check().Running)throw new InvalidOperationException("FakeAsa test not running");
        if(command.Length>256)throw new InvalidDataException("FakeAsa test command too long");
        string token=Guid.NewGuid().ToString("N");
        var commandFile=Path.Combine(root,"cmd-"+token+".json");
        var temp=commandFile+".tmp";
        DateTimeOffset requested=DateTimeOffset.UtcNow;
        File.WriteAllText(temp,JsonSerializer.Serialize(new{Token=token,Command=command}));
        File.Move(temp,commandFile);
        if(command=="SaveWorld")
        {
            saveRequestedUtc=requested;
            lastSaveToken=token;saveAck=null;
        }
        string response=Path.Combine(root,"ack-"+token+".json");
        var startedAt=DateTimeOffset.UtcNow;
        while(!File.Exists(response))
        {
            ct.ThrowIfCancellationRequested();
            if(DateTimeOffset.UtcNow-startedAt>TimeSpan.FromSeconds(7))
                throw new IOException("FakeAsa command did not acknowledge");
            await Task.Delay(30,ct);
        }
        var ack=JsonSerializer.Deserialize<LifeAck>(await File.ReadAllTextAsync(response,ct))
            ??throw new InvalidDataException("FakeAsa command acknowledgement malformed");
        if(ack.Token!=token||ack.Command!=command||ack.Pid!=identity!.Pid||
           ack.CompletedUtc<requested.AddSeconds(-1) ||
           ack.CompletedUtc>DateTimeOffset.UtcNow.AddSeconds(2))
            throw new InvalidDataException("FakeAsa command acknowledgement identity mismatch");
        if(command=="SaveWorld")saveAck=ack;
    }

    public Task<bool> SaveCompleted(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(started && saveAck!=null &&
            lastSaveToken!=null && saveAck.Token==lastSaveToken &&
            saveAck.SaveProof &&saveAck.Pid==identity?.Pid &&
            saveAck.CompletedUtc>=saveRequestedUtc);
    }

    public void Dispose()
    {
        try
        {
            if(process!=null && !process.HasExited &&
               Path.GetFileName(executable).Equals("FakeAsa.exe",
                   StringComparison.OrdinalIgnoreCase))
            {
                // Test harness cleanup only; NEVER applied to real ASA.
                process.Kill(entireProcessTree:true);
                process.WaitForExit(2000);
            }
        }
        catch{}
        finally{process?.Dispose();}
    }
}
