using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public enum RconAction { ListPlayers, Broadcast, ServerChat, SaveWorld, WildDinoWipe, Custom }
public sealed record RconDraft(Guid Map,RconAction Action,string Command,
    bool Destructive,bool RequiresAdmin,bool LiveAllowed,string Warning);
public sealed record RconTestReceipt(Guid Operation,Guid Map,string Kind,string Status,
    DateTimeOffset Utc);

// The command compiler cannot execute a network request; SendFakeOnceAsync
// uses a caller-supplied test adapter and durable claim-before-execution receipt.
public static class RconCommandPolicy
{
    public static RconDraft Preview(Guid localMap,RconAction kind,string? parameter,
        bool owned,bool maintenance,bool confirmedRunning)
    {
        if(localMap==Guid.Empty||!owned)
            throw new UnauthorizedAccessException("Foreign or invalid map RCON blocked");
        if(!Enum.IsDefined(kind))throw new InvalidDataException("Unknown RCON action");
        var message=parameter??"";
        if(message.Length>200 || message.Any(c=>char.IsControl(c)) ||
           message.Contains(';'))
            throw new InvalidDataException("Unsafe RCON parameter");
        string command=kind switch{
            RconAction.ListPlayers=>"ListPlayers",
            RconAction.Broadcast=>Message("Broadcast",message),
            RconAction.ServerChat=>Message("ServerChat",message),
            RconAction.SaveWorld=>"SaveWorld",
            RconAction.WildDinoWipe=>"DestroyWildDinos",
            RconAction.Custom=>throw new InvalidOperationException(
                "Arbitrary RCON commands require a separately certified production policy"),
            _=>throw new InvalidDataException("Unknown RCON action")
        };
        bool mutable=kind!=RconAction.ListPlayers;
        bool live=!mutable && confirmedRunning && !maintenance;
        return new(localMap,kind,command,kind==RconAction.WildDinoWipe,
            mutable,live,
            !confirmedRunning?"Process is not verified running":
            maintenance?"Maintenance prevents RCON action":
            mutable?"Mutating command preview only – production send not authorized":
            "Read-only ListPlayers authorized on verified local process");
    }
    private static string Message(string verb,string message)
    {
        if(string.IsNullOrWhiteSpace(message))
            throw new InvalidDataException("Broadcast/Chat text required");
        return verb+" "+message.Trim();
    }
}

public sealed class FakeRconQueue
{
    private readonly string path;
    public FakeRconQueue(string localProfile)
    {
        if(string.IsNullOrWhiteSpace(localProfile) ||
           !Path.IsPathFullyQualified(localProfile) || localProfile.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit local fake RCON profile required");
        var root=Path.GetFullPath(localProfile);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Fake RCON profile must be local");
        Directory.CreateDirectory(root);
        path=Path.Combine(root,"fake-rcon-transactions.json");
    }

    public async Task<RconTestReceipt> ExecuteOnceAsync(Guid operation,RconDraft draft,
        Func<string,CancellationToken,Task<string>> fakeAdapter,CancellationToken ct=default)
    {
        if(operation==Guid.Empty||draft==null||fakeAdapter==null)
            throw new InvalidDataException("Explicit synthetic transaction required");
        // With a named single-process mutex, two Managers cannot claim the
        // same transaction. Failed/interrupted calls retain the claim.
        string lockName="Local\\Untamed-FakeRcon-"+
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex=new System.Threading.Mutex(false,lockName);
        bool held=false;
        try
        {
            try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}
            catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("RCON transaction log busy");
            List<RconTestReceipt> items=File.Exists(path)?
                JsonSerializer.Deserialize<List<RconTestReceipt>>(File.ReadAllText(path))??new():
                new();
            if(items.Any(x=>x.Operation==operation))
                throw new InvalidOperationException("RCON command already claimed (no auto-replay)");
            var claiming=new RconTestReceipt(operation,draft.Map,draft.Action.ToString(),
                "claimed-no-auto-retry",DateTimeOffset.UtcNow);
            items.Add(claiming);
            string tmp=path+".tmp";
            using(var fs=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(fs,items);fs.Flush(true);}
            File.Move(tmp,path,true);
        }
        finally{if(held)mutex.ReleaseMutex();}
        try
        {
            await fakeAdapter(draft.Command,ct);
            return new(operation,draft.Map,draft.Action.ToString(),
                "fake-rcon-succeeded",DateTimeOffset.UtcNow);
        }
        catch
        {
            // Claim is intentionally not cleared. The next Manager instance
            // cannot send a potentially non-idempotent command again.
            throw;
        }
    }
}
