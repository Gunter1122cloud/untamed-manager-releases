using System;
using System.Collections.Generic;
using System.Linq;

namespace UntamedManager.Core;

// T06: immutable consistency boundary for independently captured test-cluster
// inventories. This does not parse or alter live ASA .arkprofile/.arktribe files.
// Character-byte changes during legitimate travel are not treated as corruption.
// Actual data restoration and end-to-end real ASA travel need separate tests.
public sealed record TransferMapOwner(Guid MapId,Guid HostId);
public sealed record CharacterPlacement(string SurvivorId,string TribeId,Guid MapId,
    Guid HostId,bool InTransit,string ProfileSha256,string TribeSha256);
public sealed record TransferSnapshot(Guid Epoch,IReadOnlyList<CharacterPlacement> Characters);
public sealed record ExpectedTravel(Guid Epoch,string SurvivorId,string TribeId,
    Guid SourceMap,Guid DestinationMap);
public sealed record TransferConsistencyResult(bool Valid,string Reason,int VerifiedCharacters);

public static class ClusterTransferConsistency
{
    private static bool ValidHex(string s)=>s!=null&&s.Length==64&&s.All(Uri.IsHexDigit);

    public static TransferConsistencyResult VerifyTwoHostTransfer(
        IReadOnlyList<TransferMapOwner> maps,TransferSnapshot before,
        TransferSnapshot after,ExpectedTravel travel)
    {
        if(maps==null||before==null||after==null||travel==null)
            throw new ArgumentNullException(nameof(maps));
        TransferConsistencyResult Denied(string reason)=>new(false,reason,0);
        if(travel.Epoch==Guid.Empty||before.Epoch!=travel.Epoch||after.Epoch!=travel.Epoch)
            return Denied("Transfer epoch mismatch or missing");
        if(maps.Count!=10||maps.Select(m=>m.MapId).Distinct().Count()!=10||
           maps.Any(m=>m.MapId==Guid.Empty||m.HostId==Guid.Empty)||
           maps.Select(m=>m.HostId).Distinct().Count()!=2||
           maps.GroupBy(m=>m.HostId).Any(g=>g.Count()!=5))
            return Denied("Expected an explicit, unambiguous 5+5 two-host map inventory");
        var owner=maps.ToDictionary(m=>m.MapId,m=>m.HostId);
        if(travel.SourceMap==travel.DestinationMap||
           !owner.ContainsKey(travel.SourceMap)||!owner.ContainsKey(travel.DestinationMap)||
           string.IsNullOrWhiteSpace(travel.SurvivorId)||string.IsNullOrWhiteSpace(travel.TribeId))
            return Denied("Transfer maps or survivor/tribe identity invalid");
        bool SafeList(IReadOnlyList<CharacterPlacement> chars) =>
            chars!=null&&chars.Count is >=1 and <=100_000&&
            chars.All(c=>!string.IsNullOrWhiteSpace(c.SurvivorId)&&
                !string.IsNullOrWhiteSpace(c.TribeId)&&
                owner.TryGetValue(c.MapId,out var host)&&host==c.HostId&&
                ValidHex(c.ProfileSha256)&&ValidHex(c.TribeSha256))&&
            chars.Select(c=>c.SurvivorId).Distinct(StringComparer.Ordinal).Count()==chars.Count;
        if(!SafeList(before.Characters)||!SafeList(after.Characters))
            return Denied("Duplicate, missing, tampered or unowned survivor evidence");
        var original=before.Characters.ToDictionary(c=>c.SurvivorId,StringComparer.Ordinal);
        var result=after.Characters.ToDictionary(c=>c.SurvivorId,StringComparer.Ordinal);
        if(!original.Keys.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(result.Keys))
            return Denied("Unexpected survivor loss, duplication or new identity");
        if(!original.TryGetValue(travel.SurvivorId,out var from)||
           !result.TryGetValue(travel.SurvivorId,out var to)||
           from.MapId!=travel.SourceMap||to.MapId!=travel.DestinationMap||
           from.InTransit||to.InTransit||
           from.TribeId!=travel.TribeId||to.TribeId!=travel.TribeId)
            return Denied("Transfer survivor/tribe continuity not verified");
        foreach(var previous in original.Values)
        {
            var next=result[previous.SurvivorId];
            if(previous.TribeId!=next.TribeId)return Denied("Survivor tribe changed across snapshot");
            if(previous.SurvivorId==travel.SurvivorId)continue;
            if(previous.MapId!=next.MapId||previous.HostId!=next.HostId||
               previous.InTransit!=next.InTransit||
               previous.ProfileSha256!=next.ProfileSha256||
               previous.TribeSha256!=next.TribeSha256)
                return Denied("Unrelated survivor/profile/tribe bytes changed during transfer window");
        }
        return new(true,
            "Two immutable inventories correlate an exactly-once test transfer; live ASA travel not certified",
            original.Count);
    }
}
