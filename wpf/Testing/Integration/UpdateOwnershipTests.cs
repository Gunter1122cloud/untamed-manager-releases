using System.Text;
using System.Text.Json;
using UntamedManager.Core;

public static class UpdateOwnershipTests
{
    public static void VerifyToolVersions(string supervisor,string updater)
    {
        var manager=typeof(UntamedManager.MainWindow).Assembly.GetName().Version!;
        foreach(string executable in new[]{supervisor,updater})
        {
            var text=System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileVersion;
            Check(Version.TryParse(text,out var version)&&version.Major==manager.Major&&
                version.Minor==manager.Minor&&version.Build==manager.Build,
                "shipped companion executable version matches the actual Manager assembly");
        }
    }
    static void Check(bool value,string reason)
    {if(!value)throw new Exception("Update ownership: "+reason);Console.WriteLine("PASS update-owner "+reason);}
    static bool Refused(Action action)
    {try{action();return false;}catch(IOException){return true;}}
    public static void Run(string root)
    {
        string app=Path.Combine(root,"bound-update-app"),snapshots=Path.Combine(root,"bound-update-history");
        Directory.CreateDirectory(app);File.WriteAllText(Path.Combine(app,"owned.dll"),"original bytes");
        string foreign=Path.Combine(root,"foreign-app");Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign,"owned.dll"),"unrelated protected bytes");
        Check(Refused(()=>new DirectoryUpdateTransaction(app.ToUpperInvariant()).Prepare(app)),
            "case-only aliases cannot put a snapshot inside the application tree");
        string rollback=Path.Combine(snapshots,"rollback-owned");
        var transaction=new DirectoryUpdateTransaction(rollback,app);transaction.Prepare(app);
        string owner=Path.Combine(rollback,"update-owner.dat"),journal=Path.Combine(rollback,"update-transaction.json");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(owner)).Contains(app,StringComparison.Ordinal),
            "transaction owner binding is DPAPI protected for the creating Windows account");
        var original=transaction.InspectState();
        File.WriteAllText(Path.Combine(app,"owned.dll"),"changed bytes");
        File.WriteAllText(journal,JsonSerializer.Serialize(original with {Application=foreign}));
        Check(Refused(()=>new DirectoryUpdateTransaction(rollback).Rollback())&&
            File.ReadAllText(Path.Combine(foreign,"owned.dll"))=="unrelated protected bytes"&&Directory.Exists(app),
            "altered journal cannot redirect rollback into another existing application");
        File.WriteAllText(journal,JsonSerializer.Serialize(original));
        string snapshotDll=Path.Combine(rollback,"snapshot","owned.dll");
        File.WriteAllText(snapshotDll,"forged snapshot");
        var forgedHashes=original.Hashes.ToDictionary(x=>x.Key,x=>x.Value);
        forgedHashes["owned.dll"]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(snapshotDll)));
        File.WriteAllText(journal,JsonSerializer.Serialize(original with {Hashes=forgedHashes}));
        Check(Refused(()=>transaction.Rollback())&&File.ReadAllText(Path.Combine(app,"owned.dll"))=="changed bytes",
            "changing snapshot bytes and their public hash together cannot forge protected inventory");
        File.WriteAllText(snapshotDll,"original bytes");File.WriteAllText(journal,JsonSerializer.Serialize(original));
        Check(Refused(()=>new DirectoryUpdateTransaction(rollback,foreign).Rollback()),
            "explicit expected application rejects a foreign recovery invocation");
        string copied=Path.Combine(snapshots,"rollback-copied");Directory.CreateDirectory(copied);
        File.Copy(owner,Path.Combine(copied,"update-owner.dat"));File.Copy(journal,Path.Combine(copied,"update-transaction.json"));
        Check(Refused(()=>new DirectoryUpdateTransaction(copied).InspectState()),
            "copying another transaction's owner and journal does not transfer root ownership");
        Directory.Delete(copied,true);
        File.WriteAllText(journal,JsonSerializer.Serialize(original with {Phase="unknown"}));
        Check(Refused(()=>transaction.Rollback()),"unknown recovery phase never authorizes a directory move");
        File.WriteAllText(journal,JsonSerializer.Serialize(original));
        byte[] protectedOwner=File.ReadAllBytes(owner),tampered=protectedOwner.ToArray();tampered[^1]^=1;File.WriteAllBytes(owner,tampered);
        Check(Refused(()=>transaction.Rollback()),"corrupt protected ownership metadata blocks restore before mutation");
        File.WriteAllBytes(owner,protectedOwner);
        Check(UpdateRecovery.FindPending(app,snapshots)==rollback,"read-only pending discovery verifies protected ownership");
        new DirectoryUpdateTransaction(rollback,app).Rollback();
        Check(File.ReadAllText(Path.Combine(app,"owned.dll"))=="original bytes"&&
            transaction.InspectState().Phase=="rolledback","valid reconstructed transaction actually restores original file bytes");
        Check(Refused(()=>transaction.Commit()),"a rolled-back transaction cannot later be committed");
        foreach(string alias in new[]{snapshots+".\\other",snapshots+":stream",snapshots+" \\other"})
            Check(Refused(()=>new DirectoryUpdateTransaction(alias)),"raw Windows path alias refused before normalization");
    }
}
