using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UntamedManager.Core;
public sealed record MapObservation(Guid Map,string Name,bool? Running,string Loader,string Rcon,
    double? CpuPercent,long? MemoryBytes,double? UptimeSeconds,DateTimeOffset ObservedUtc,
    int? Players,bool Maintenance,string Watchdog,string Log,string Operation,string Error);
public sealed record SupervisorSnapshot(string HostId,int Pid,DateTimeOffset StartedUtc,string Executable,
    DateTimeOffset ObservedUtc,MapObservation[] Maps);
public sealed record SignedObservation(string Payload,string Signature);
public sealed class SupervisorChannel
{
    public string Root { get; }
    private readonly byte[] key;
    public string SnapshotPath=>Path.Combine(Root,"status.signed.json");
    public SupervisorChannel(string profileRoot)
    {
        string full=Path.GetFullPath(profileRoot);
        if(full.StartsWith(@"\\")||new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)throw new IOException("Local IPC requires fixed disk");
        var ancestors=new System.Collections.Generic.Stack<DirectoryInfo>();
        for(var directory=new DirectoryInfo(full);directory!=null;directory=directory.Parent)ancestors.Push(directory);
        foreach(var directory in ancestors)if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked IPC root rejected");
        Root=Path.Combine(full,"supervision");
        if(Directory.Exists(Root)&&(File.GetAttributes(Root)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked IPC directory rejected");
        Directory.CreateDirectory(Root);
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        var sid=WindowsIdentity.GetCurrent().User??throw new UnauthorizedAccessException();
        acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
        using var gate=new Mutex(false,"Local\\Untamed-IpcKey-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant()))));
        bool entered=false;
        try
        {
            try{entered=gate.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("IPC key busy");
            string file=Path.Combine(Root,"key.dpapi");
            if(!File.Exists(file))File.WriteAllText(file,SecureStore.Protect(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked key rejected");
            key=Convert.FromHexString(SecureStore.Unprotect(File.ReadAllText(file)));
        }
        finally{if(entered)gate.ReleaseMutex();}
    }
    public void Write(SupervisorSnapshot snapshot)
    {
        string payload=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(snapshot));
        string signature=Convert.ToHexString(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(payload)));
        string temp=SnapshotPath+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(new SignedObservation(payload,signature)));
        File.Move(temp,SnapshotPath,true);
    }
    public SupervisorSnapshot Read(string hostId)
    {
        var file=new FileInfo(SnapshotPath);
        if(file.Length>1_000_000||(file.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Invalid IPC snapshot");
        var frame=JsonSerializer.Deserialize<SignedObservation>(File.ReadAllText(SnapshotPath))??throw new IOException("Missing IPC frame");
        if(!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(frame.Payload)),Convert.FromHexString(frame.Signature)))throw new CryptographicException("IPC authentication failed");
        var snapshot=JsonSerializer.Deserialize<SupervisorSnapshot>(Convert.FromBase64String(frame.Payload))??throw new IOException("Invalid observation");
        if(snapshot.HostId!=hostId||!StatusFreshness.IsFresh(snapshot.ObservedUtc,DateTimeOffset.UtcNow))throw new IOException("IPC host mismatch or stale heartbeat");
        return snapshot;
    }
}
