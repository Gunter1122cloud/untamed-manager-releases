using System;
using System.Collections.Generic;
using System.Linq;

namespace UntamedManager.Core;

// v1.3.7 synthetic proof format for FakeAsa fixtures only. An RCON success,
// changed timestamp, file size, or generic log line is never sufficient.
public enum SyntheticSaveSignalKind { Started, Completed }
public sealed record SyntheticSaveSignal(Guid Map,string OperationToken,SyntheticSaveSignalKind Kind,
    ProcessIdentity Server,DateTimeOffset Utc);
public sealed record SyntheticSaveResult(bool Proven,string Reason,DateTimeOffset? CompletedUtc);

public static class SaveWorldValidator
{
    public static SyntheticSaveResult VerifySynthetic(Guid ownedMap,ProcessIdentity expected,
        string operationToken,DateTimeOffset commandUtc,DateTimeOffset now,
        IEnumerable<SyntheticSaveSignal> observations)
    {
        if(ownedMap==Guid.Empty || expected==null || expected.Pid<=0 ||
           string.IsNullOrWhiteSpace(operationToken) || operationToken.Length>128)
            throw new ArgumentException("Explicit owned map, process and test token required");
        if(observations==null)throw new ArgumentNullException(nameof(observations));
        bool SameProcess(ProcessIdentity p)=>p!=null && p.Pid==expected.Pid &&
            p.StartedUtc==expected.StartedUtc &&
            string.Equals(p.ExecutablePath,expected.ExecutablePath,StringComparison.OrdinalIgnoreCase) &&
            p.Arguments==expected.Arguments;
        if(now<commandUtc || now-commandUtc>TimeSpan.FromMinutes(3))
            return new(false,"Synthetic proof expired or time reversed",null);
        var signals=observations.Where(x=>x.OperationToken==operationToken).ToArray();
        if(signals.Any(x=>x.Map!=ownedMap))
            return new(false,"Synthetic save evidence belongs to a different map",null);
        if(signals.Length!=2 || signals.Count(x=>x.Kind==SyntheticSaveSignalKind.Started)!=1 ||
           signals.Count(x=>x.Kind==SyntheticSaveSignalKind.Completed)!=1)
            return new(false,"Expected exactly one correlated start and completion",null);
        var start=signals.Single(x=>x.Kind==SyntheticSaveSignalKind.Started);
        var end=signals.Single(x=>x.Kind==SyntheticSaveSignalKind.Completed);
        if(!SameProcess(start.Server)||!SameProcess(end.Server))
            return new(false,"Synthetic server process identity changed",null);
        if(start.Utc<commandUtc || end.Utc<=start.Utc || end.Utc>now ||
           end.Utc-start.Utc>TimeSpan.FromMinutes(2))
            return new(false,"Synthetic save signals are stale, reversed or out of window",null);
        return new(true,"FakeAsa synthetic completion correlated to the exact process",end.Utc);
    }

    // T03: authenticated operation-specific witness protocol. These receipts
    // must originate from a future, independently validated ASA save-completion
    // callback. A signed fixture alone does not prove a real world save.
    public enum WitnessKind { SaveStarted, SaveCompleted }

    public sealed record SaveWitness(Guid MapId,Guid OperationId,WitnessKind Kind,
        ProcessIdentity Process,DateTimeOffset Utc,string WorldDigest,string MacHex);

    public sealed record WitnessCheck(bool Correlated,string Reason,DateTimeOffset? CompletedUtc);

    // Deterministic length-prefix encoding prevents delimiter-substitution attacks.
    // The corresponding ASA-side witness must independently implement this v1 frame.
    private static byte[] WitnessFrame(SaveWitness e)
    {
        using var stream=new System.IO.MemoryStream();
        using var writer=new System.IO.BinaryWriter(stream,System.Text.Encoding.UTF8,true);
        writer.Write("UNTAMED-ASA-SAVE-WITNESS-V1");
        writer.Write(e.MapId.ToByteArray());
        writer.Write(e.OperationId.ToByteArray());
        writer.Write((int)e.Kind);
        writer.Write(e.Process.Pid);
        writer.Write(e.Process.StartedUtc.ToUniversalTime().Ticks);
        writer.Write(e.Process.ExecutablePath??"");
        writer.Write(e.Process.Arguments??"");
        writer.Write(e.Utc.ToUniversalTime().Ticks);
        writer.Write(e.WorldDigest??"");
        writer.Flush();
        return stream.ToArray();
    }

    internal static SaveWitness SignWitnessForFixture(SaveWitness value,byte[] key)
    {
        if(key==null||key.Length!=32)throw new ArgumentException("A 256-bit test witness key is required");
        var signature=System.Security.Cryptography.HMACSHA256.HashData(key,WitnessFrame(value));
        return value with {MacHex=Convert.ToHexString(signature)};
    }

    private static bool Authentic(SaveWitness e,byte[] key)
    {
        if(key==null||key.Length!=32 || e.MacHex==null || e.MacHex.Length!=64)return false;
        try
        {
            byte[] actual=Convert.FromHexString(e.MacHex);
            byte[] expected=System.Security.Cryptography.HMACSHA256.HashData(key,WitnessFrame(e));
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual,expected);
        }
        catch(FormatException){return false;}
    }

    // Correlation only. Do NOT wire this into DoExit until the actual ASA API
    // event producer and its post-flush semantics pass isolated real-server tests.
    public static WitnessCheck VerifyAttestedPair(Guid mapId,Guid operationId,
        ProcessIdentity expected,DateTimeOffset requestedUtc,DateTimeOffset nowUtc,
        System.Collections.Generic.IEnumerable<SaveWitness> receipts,byte[] key)
    {
        if(mapId==Guid.Empty||operationId==Guid.Empty||expected==null||expected.Pid<=0||
           receipts==null||key==null||key.Length!=32)
            throw new ArgumentException("Owned map, operation, process, receipts and key required");
        if(nowUtc<requestedUtc || nowUtc-requestedUtc>TimeSpan.FromMinutes(5))
            return new(false,"Save witness request expired",null);
        var items=receipts.Take(3).ToArray();
        if(items.Length!=2 || items.Count(x=>x.Kind==WitnessKind.SaveStarted)!=1 ||
           items.Count(x=>x.Kind==WitnessKind.SaveCompleted)!=1)
            return new(false,"Exactly one start and one completion receipt required",null);
        bool SameProcess(ProcessIdentity p)=>p!=null && p.Pid==expected.Pid &&
            p.StartedUtc==expected.StartedUtc &&
            string.Equals(p.ExecutablePath,expected.ExecutablePath,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Arguments,expected.Arguments,StringComparison.Ordinal);
        if(items.Any(x=>x.MapId!=mapId || x.OperationId!=operationId ||
                       !SameProcess(x.Process) || !Authentic(x,key)))
            return new(false,"Foreign, altered or unsigned witness refused",null);
        var start=items.Single(x=>x.Kind==WitnessKind.SaveStarted);
        var end=items.Single(x=>x.Kind==WitnessKind.SaveCompleted);
        if(start.Utc<requestedUtc||end.Utc<=start.Utc||end.Utc>nowUtc||
           end.Utc-start.Utc>TimeSpan.FromMinutes(3) ||
           !string.IsNullOrEmpty(start.WorldDigest) ||
           end.WorldDigest.Length!=64 ||
           !end.WorldDigest.All(c=>Uri.IsHexDigit(c)))
            return new(false,"Witness chronology or completed digest invalid",null);
        return new(true,"Signed ASA save witness pair correlated; real post-flush producer not certified",
            end.Utc);
    }

    // Intentionally fail closed. A real ASA adapter requires verified authoritative
    // semantics and server tests; this method must not accept generic RCON/log cues.
    public static SyntheticSaveResult VerifyLiveUnavailable(string? rconReply,DateTimeOffset? fileModifiedUtc)
        => new(false,"Real ASA SaveWorld completion adapter not validated",null);
}
