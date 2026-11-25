using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Strictly in-memory Windows ASA command-line proposal for a later
/// independently authorized one-shot conversion of ONE NEW local map.
/// Never invokes Process.Start, RCON, ASA API or a Store/UNC writer.
/// </summary>
public sealed record FutureStoreConversionLaunchPlan(
    Guid MapId,Guid OperationId,string Arguments,string MapToken,
    string ClusterId,string TransferDirectory,string SourceSha256,
    string BackupManifestSha256,string Fingerprint)
{
    public bool ProcessStartAuthorized=>false;
    public bool GameSaveConfirmed=>false;
    public bool StoreOwnerCertified=>false;
    public bool RetryAllowed=>false;
}

public static class FutureStoreConversionLaunchPlanner
{
    private static void Deny(string code)=>throw new InvalidDataException(
        "UM-FUTURE-CONVERSION-LAUNCH-"+code);

    private static string Read(JsonElement obj,string key)
    {
        if(!obj.TryGetProperty(key,out var value)||
           value.ValueKind!=JsonValueKind.String)
            throw new InvalidDataException("UM-FUTURE-CONVERSION-LAUNCH-FIELD-"+key);
        return value.GetString()!;
    }

    private static JsonElement Prop(JsonElement obj,string key)
    {
        if(!obj.TryGetProperty(key,out var value))
            throw new InvalidDataException("UM-FUTURE-CONVERSION-LAUNCH-FIELD-"+key);
        return value;
    }

    public static FutureStoreConversionLaunchPlan Prepare(
        string managerProfileRoot,LocalMap map,
        FutureStoreConversionReservation reservation,string[] originalLocalImages)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(originalLocalImages);
        if(originalLocalImages.Length!=5||
           originalLocalImages.Any(x=>string.IsNullOrWhiteSpace(x)||x.Length>1_048_576)||
           map.Id==Guid.Empty||map.Id!=reservation.MapId||
           map.HistoricalStoreProtected||
           !string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)||
           !string.IsNullOrWhiteSpace(map.FreshStoreClusterId)||
           map.GamePort is <1024 or >65535||
           map.QueryPort is <1024 or >65535||
           map.GamePort==map.QueryPort||
           map.RconPort is <1024 or >65535 ||
           map.Maintenance||map.AutoStart||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(map.Arguments))
            Deny("CANDIDATE-INVALID");
        var journal=FutureStoreConversionJournal.Inspect(managerProfileRoot,map.Id);
        if(journal==null||journal.Phase!="conversionAttempted"||
           journal.OperationId!=reservation.OperationId||
           journal.MapId!=reservation.MapId||
           journal.ClusterId!=VaultProtocol.ClusterId||
           journal.OriginalSourceSha256!=reservation.OriginalSourceSha256||
           journal.BackupManifestSha256!=reservation.BackupManifestSha256)
            Deny("DURABLE-RESERVATION-MISMATCH");
        var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n",originalLocalImages))));
        if(digest!=reservation.OriginalSourceSha256)
            Deny("ORIGINAL-SOURCE-CHANGED");
        using var cluster=JsonDocument.Parse(originalLocalImages[0]);
        using var store=JsonDocument.Parse(originalLocalImages[1]);
        using var locked=JsonDocument.Parse(originalLocalImages[2]);
        using var launch=JsonDocument.Parse(originalLocalImages[3]);
        using var peer=JsonDocument.Parse(originalLocalImages[4]);
        var source=cluster.RootElement;
        // Strict original Store/source/lock/DONE peer parity is independent
        // of the SHA used to pin these exact bytes to the one-shot journal.
        var originalAudit=FutureStoreConversionSourceAudit.Inspect(originalLocalImages);
        if(originalAudit.OriginalSourceSha256!=reservation.OriginalSourceSha256)
            Deny("ORIGINAL-SOURCE-NOT-PINNED");
        string path=Read(source,"path");
        if(!string.Equals(path,originalAudit.ClusterPath,StringComparison.Ordinal))
            Deny("ORIGINAL-CLUSTER-PATH-CHANGED");
        if(Read(source,"id")!=VaultProtocol.ClusterId||
           Read(source,"mode")!="shared"||
           !path.StartsWith(@"\\",StringComparison.Ordinal)||
           path.StartsWith(@"\\?",StringComparison.Ordinal)||
           path.StartsWith(@"\\.",StringComparison.Ordinal)||
           path.Length>500||path.Any(c=>char.IsControl(c)||c is '"' or '<' or '>' or '|' or '?' or '*')||
           path[2..].Split('\\').Any(x=>x.Length==0||x is "." or ".."))
            Deny("SHARED-PATH-UNTRUSTED");
        var members=Prop(source,"members");
        var maps=Prop(store.RootElement,"maps");
        var rows=Prop(launch.RootElement,"rows");
        var selected=Prop(locked.RootElement,"selected");
        var peerSelected=Prop(peer.RootElement,"selected");
        if(members.ValueKind!=JsonValueKind.Array||
           members.GetArrayLength() is <5 or >31||
           maps.ValueKind!=JsonValueKind.Object||
           rows.ValueKind!=JsonValueKind.Array||
           maps.EnumerateObject().Count()!=members.GetArrayLength()||
           rows.GetArrayLength()!=members.GetArrayLength()||
           selected.ValueKind!=JsonValueKind.Array||selected.GetArrayLength()!=5||
           peerSelected.ValueKind!=JsonValueKind.Array||
           peerSelected.GetArrayLength()!=5||
           Read(peer.RootElement,"phase")!="done"||
           !selected.EnumerateArray().Select(x=>x.GetString()).SequenceEqual(
               peerSelected.EnumerateArray().Select(x=>x.GetString()))||
           Read(Prop(locked.RootElement,"config"),"path")!=path||
           Read(Prop(peer.RootElement,"config"),"path")!=path||
           !StoreV1Fingerprint.Matches(source,
               Read(locked.RootElement,"configHash"))||
           !StoreV1Fingerprint.Matches(source,
               Read(peer.RootElement,"configHash")))
            Deny("HISTORICAL-SOURCE-NOT-CONSISTENT");
        string root=FutureStoreInstallationPath.Canonical(map.InstallationPath);
        if(!Path.IsPathFullyQualified(root)||
           root.StartsWith(@"\\",StringComparison.Ordinal)||
           string.IsNullOrWhiteSpace(map.Arguments))
            Deny("LOCAL-MAP-ROOT");
        string key=AsaSaveLayoutResolver.MapToken(map);
        foreach(var member in members.EnumerateArray())
        {
            if(Read(member,"id")==map.Id.ToString("D")||
               Read(member,"map").Equals(key,StringComparison.OrdinalIgnoreCase)||
               FutureStoreInstallationPath.Overlaps(Read(member,"root"),root))
                Deny("MAP-ALREADY-PRESENT");
        }
        foreach(var row in rows.EnumerateArray())
        {
            var launchRow=Prop(row,"launch");
            foreach(string port in new[]{"gamePort","queryPort"})
            {
                int used=Prop(launchRow,port).GetInt32();
                if(used==map.GamePort||used==map.QueryPort)
                    Deny("PORT-ALREADY-ASSIGNED");
            }
        }
        string baseArgs=FreshStoreEnrollmentPolicy.ValidateBaseArguments(
            map,map.Arguments);
        string quotePath=path.Contains(' ')?"\""+path+"\"":path;
        string args=baseArgs+" -clusterid="+VaultProtocol.ClusterId+
            " -ClusterDirOverride="+quotePath+" -usestore -converttostore -log";
        if(args.Length>8192||!AsaStoreArgumentSwitches.ContainsStoreWriter(args)||
           !AsaStoreArgumentSwitches.ContainsConversion(args))
            Deny("ARGUMENTS-INVALID");
        string fingerprint=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(map.Id.ToString("D")+"\n"+
            reservation.OperationId+"\n"+digest+"\n"+reservation.BackupManifestSha256+
            "\n"+args)));
        return new(map.Id,reservation.OperationId,args,key,
            VaultProtocol.ClusterId,path,digest,reservation.BackupManifestSha256,
            fingerprint);
    }
}
