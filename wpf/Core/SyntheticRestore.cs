using System;
using System.IO;
using System.Text.Json;

namespace UntamedManager.Core;

// Deliberately confined to a newly generated synthetic workspace, never a registered map root.
public sealed class SyntheticRestore
{
    public string Root { get; }
    public Guid Owner { get; }
    private string Target => Path.Combine(Root,"target");
    private string Previous => Path.Combine(Root,"previous");
    private string Staging => Path.Combine(Root,"staging");
    private string Journal => Path.Combine(Root,"transaction.json");
    public SyntheticRestore()
    {
        Root=Path.Combine(Path.GetTempPath(),"Untamed-Restore-Synthetic-"+Guid.NewGuid().ToString("N"));
        Owner=Guid.NewGuid();
        Directory.CreateDirectory(Target);
    }
    public string TargetDirectory => Target;
    public void Restore(string package, bool interruptAfterSwap=false)
    {
        if(File.Exists(Journal))throw new InvalidOperationException("Unresolved restore transaction");
        var incoming=TestBackupPackage.Verify(package,Owner);
        string protection=Path.Combine(Root,"protection-"+Guid.NewGuid().ToString("N"));
        TestBackupPackage.Create(Target,protection,Owner,"Synthetic pre-restore protection");
        var original=TestBackupPackage.Verify(protection,Owner);
        Directory.CreateDirectory(Staging);
        foreach(var file in incoming.Files)
        {
            string destination=Path.Combine(Staging,file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(package,"data",file.Path),destination);
        }
        BackupIntegrity.Verify(Staging,incoming,Owner);
        WriteJournal(original);
        Directory.Move(Target,Previous);
        Directory.Move(Staging,Target);
        if(interruptAfterSwap)throw new IOException("Synthetic interruption after swap");
        try { BackupIntegrity.Verify(Target,incoming,Owner); }
        catch { Recover();throw; }
        File.Move(Journal,Journal+".committed-"+Guid.NewGuid().ToString("N"));
        Directory.Move(Previous,Path.Combine(Root,"previous-committed-"+Guid.NewGuid().ToString("N")));
    }
    public bool RecoveryRequired => File.Exists(Journal);
    public void Recover()
    {
        if(!RecoveryRequired)return;
        var original=JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Journal))
            ?? throw new InvalidDataException("Invalid recovery journal");
        if(Directory.Exists(Previous))
        {
            if(Directory.Exists(Target))Directory.Move(Target,Path.Combine(Root,"failed-"+Guid.NewGuid().ToString("N")));
            Directory.Move(Previous,Target);
        }
        BackupIntegrity.Verify(Target,original,Owner);
        if(Directory.Exists(Staging))Directory.Move(Staging,Path.Combine(Root,"abandoned-"+Guid.NewGuid().ToString("N")));
        File.Move(Journal,Journal+".rolledback-"+Guid.NewGuid().ToString("N"));
    }
    private void WriteJournal(BackupManifest original)
    {
        using var stream=new FileStream(Journal,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(stream,original);
        stream.Flush(true);
    }
}
