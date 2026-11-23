using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using UntamedManager.Core;

// #2966 live regression: native original Start must accept one DONE peer
// 5/5/5 and another 6/6/5 without changing the historical writer selection.
// Pure read-only test data: no UNC or production profile is ever opened.
internal static class OriginalStoreExpandedDonePeerTests
{
    private const string Shared=@"\\synthetic-store\original-cluster";
    private static void Check(bool ok,string name)
    {
        if(!ok)throw new InvalidOperationException(
            "EXPANDED DONE PEER: "+name);
        Console.WriteLine("PASS expanded DONE peer: "+name);
    }
    private static bool Denied(Action check)
    {
        try{check();return false;}
        catch(InvalidDataException){return true;}
    }
    private static JsonElement[] Members(string name,int count)=>
        Enumerable.Range(0,count).Select(i=>JsonSerializer.SerializeToElement(new{
            id=Guid.NewGuid().ToString("D"),
            map=name+"_"+i+"_WP",
            root=@"C:\synthetic-"+name+"-"+i
        })).ToArray();
    private static string[] Historical(JsonElement[] members)=>
        members.Take(5).Select(x=>x.GetProperty("id").GetString()!).ToArray();
    private static JsonElement Done(string device,string session,
        JsonElement[] members,string[] selected,
        JsonElement[]? configMembers=null,string? share=null,
        string phase="done",bool corruptHash=false)
    {
        var config=JsonSerializer.SerializeToElement(new{
            id=VaultProtocol.ClusterId,mode="shared",path=share??Shared,
            members=configMembers??members
        });
        return JsonSerializer.SerializeToElement(new{
            id=device,session,phase,config,
            configHash=corruptHash?new string('0',64):
                StoreV1Fingerprint.Compute(config),
            members,selected
        });
    }
    private static LegacyStoreV1PeerTopology.Peer Verified(JsonElement peer,
        string device,string session)=>
        LegacyStoreV1LiveReadGate.InspectOriginalDonePeer(
            peer,device,session,Shared);

    private static byte[] UdpOwnerTable(int rowSize,int portOffset,int pidOffset,
        int port,int pid)
    {
        var bytes=new byte[4+rowSize];
        BitConverter.TryWriteBytes(bytes.AsSpan(0,4),1);
        bytes[4+portOffset]=(byte)(port>>8);
        bytes[4+portOffset+1]=(byte)port;
        BitConverter.TryWriteBytes(bytes.AsSpan(4+pidOffset,4),pid);
        return bytes;
    }
    public static void Run()
    {
        // Windows UDP owner tables are not interchangeable between IPv4
        // (12-byte rows) and IPv6 (28-byte rows); synthetics exercise both.
        var empty=BitConverter.GetBytes(0);
        var own4=UdpOwnerTable(12,4,8,27046,32123);
        var own6=UdpOwnerTable(28,20,24,27046,32123);
        var foreign6=UdpOwnerTable(28,20,24,27046,32124);
        Check(RconPortOwnership.InspectSyntheticUdp(own4,empty,27046,32123)=="Owned",
            "IPv4 Query UDP socket binds to the correct ASA PID");
        Check(RconPortOwnership.InspectSyntheticUdp(empty,own6,27046,32123)=="Owned",
            "IPv6-only Query UDP socket is a valid owner proof");
        Check(RconPortOwnership.InspectSyntheticUdp(own4,foreign6,27046,32123)=="ForeignOrAmbiguous",
            "a different IPv6 query owner blocks even when IPv4 binds to the target PID");
        Check(RconPortOwnership.InspectSyntheticUdp(empty,empty,27046,32123)=="Missing",
            "absent Query UDP owner remains unverified and cannot be promoted");
        Check(RconPortOwnership.InspectSyntheticUdp(new byte[5],empty,27046,32123)=="Unverified",
            "corrupt native UDP table fails closed");

        string localDevice=Guid.NewGuid().ToString("D");
        string remoteDevice=Guid.NewGuid().ToString("D");
        string session=Guid.NewGuid().ToString("D");
        var remoteFive=Members("Remote",5);
        var five=Members("Original",5);
        string[] historical=Historical(five),remoteHistorical=Historical(remoteFive);

        foreach(int count in new[]{5,6,12,32})
        {
            var additional=Members("Added"+count,count-5);
            var expanded=five.Concat(additional).ToArray();
            var left=Done(localDevice,session,expanded,historical);
            var right=Done(remoteDevice,session,remoteFive,remoteHistorical);
            var inspectedLocal=Verified(left,localDevice,session);
            var inspectedRemote=Verified(right,remoteDevice,session);
            LegacyStoreV1PeerTopology.Validate(localDevice,
                new[]{inspectedLocal,inspectedRemote});
            LegacyStoreV1PeerTopology.RequireOriginalLocalSelection(
                JsonSerializer.SerializeToElement(historical),
                inspectedLocal.SelectedIds);
            Check(inspectedLocal.MemberIds.Count==count&&
                inspectedLocal.SelectedIds.Count==5&&
                inspectedRemote.MemberIds.Count==5&&
                inspectedLocal.SelectedIds.SequenceEqual(historical)&&
                inspectedLocal.SelectedIds.All(id=>
                    inspectedLocal.MemberIds.Contains(id))&&
                inspectedLocal.SelectedIds.Count+
                    inspectedRemote.SelectedIds.Count==10,
                count+" current local members preserve original five selected and two DONE peers");

            // Either peer may contain independent later maps. The owner of
            // the imported historical map is always the five-map selection.
            if(count==6)
            {
                var otherExtra=Members("RemoteEvent",1);
                var extraRemote=Done(remoteDevice,session,
                    remoteFive.Concat(otherExtra).ToArray(),remoteHistorical);
                LegacyStoreV1PeerTopology.Validate(localDevice,new[]{
                    Verified(Done(localDevice,session,five,historical),
                        localDevice,session),
                    Verified(extraRemote,remoteDevice,session)
                });
                Check(true,"remote expanded 6/6/5 peer works alongside local 5/5/5");
            }
        }

        var six=five.Concat(Members("Extra",1)).ToArray();
        var independentId=Guid.Parse(six[5].GetProperty("id").GetString()!);
        string independentMap=six[5].GetProperty("map").GetString()!;
        string independentRoot=six[5].GetProperty("root").GetString()!;
        Check(LegacyStoreV1LiveReadGate.MatchesIndependentLocalPeerOwner(
            JsonSerializer.SerializeToElement(six),independentId,independentMap,independentRoot),
            "independently converted extra map belongs to the exact local expanded DONE peer");
        Check(!LegacyStoreV1LiveReadGate.MatchesIndependentLocalPeerOwner(
            JsonSerializer.SerializeToElement(five),independentId,independentMap,independentRoot),
            "old five-map DONE peer cannot claim separately converted sixth map");
        Check(!LegacyStoreV1LiveReadGate.MatchesIndependentLocalPeerOwner(
            JsonSerializer.SerializeToElement(six),independentId,independentMap,@"C:\foreign-root"),
            "mismatched independent root is never an owned peer map");
        Check(!LegacyStoreV1LiveReadGate.MatchesIndependentLocalPeerOwner(
            JsonSerializer.SerializeToElement(six),independentId,independentMap+"_FORGED",independentRoot),
            "mismatched independent level is never an owned peer map");
        JsonElement Roster(JsonElement[] members)=>
            JsonSerializer.SerializeToElement(new{
                id=VaultProtocol.ClusterId,mode="shared",path=Shared,members
            });
        var originalPin=Roster(five);
        var sixCurrent=Roster(six);
        Check(LegacyStoreV1LocalReader.MatchesAppendOnlyRoster(
            sixCurrent,originalPin,historical),
            "later independent sixth map leaves the original five local owners immutable");
        var seventh=Members("Future",1);
        var seven=six.Concat(seventh).ToArray();
        Check(LegacyStoreV1LocalReader.MatchesAppendOnlyRoster(
            Roster(seven),sixCurrent,historical),
            "new seventh map does not break the already pinned sixth map");
        Check(!LegacyStoreV1LocalReader.MatchesAppendOnlyRoster(
            Roster(seven.Skip(1).Concat(new[]{seven[0]}).ToArray()),sixCurrent,historical),
            "reordering existing owners is not an append-only Store change");
        var rewritten=six.ToArray();
        rewritten[5]=JsonSerializer.SerializeToElement(new{
            id=six[5].GetProperty("id").GetString(),
            map=six[5].GetProperty("map").GetString(),
            root=@"C:\forged-owner-root"});
        Check(!LegacyStoreV1LocalReader.MatchesAppendOnlyRoster(
            Roster(rewritten.Concat(seventh).ToArray()),sixCurrent,historical),
            "rewriting an earlier independently pinned root blocks later additions");
        Check(!LegacyStoreV1LocalReader.MatchesAppendOnlyRoster(
            Roster(seven),sixCurrent,new[]{
                historical[0],historical[0],historical[2],historical[3],historical[4]}),
            "duplicate original selection cannot authorize append-only roster");
        var valid=Done(localDevice,session,six,historical);
        var verified=Verified(valid,localDevice,session);
        var remote=Verified(Done(remoteDevice,session,remoteFive,
            remoteHistorical),remoteDevice,session);
        Check(Denied(()=>Verified(Done(localDevice,
            Guid.NewGuid().ToString("D"),six,historical),
            localDevice,session)),
            "changed session rejects expanded DONE peer");
        Check(Denied(()=>Verified(Done(localDevice,session,six,
            historical,corruptHash:true),localDevice,session)),
            "incorrect current configHash rejects expanded DONE peer");
        Check(Denied(()=>Verified(Done(localDevice,session,six,
            historical,phase:"active"),localDevice,session)),
            "non-DONE phase rejects historical Start");
        Check(Denied(()=>Verified(Done(localDevice,session,six,
            historical,configMembers:five),localDevice,session)),
            "stale five-member config cannot authorize expanded six-member peer");
        Check(Denied(()=>Verified(Done(localDevice,session,six,
            historical,share:"not-a-UNC-share"),localDevice,session)),
            "peer from a different shared root cannot authorize original session");
        Check(Denied(()=>Verified(Done(localDevice,session,six,
            new[]{historical[0],historical[0],historical[2],
                historical[3],historical[4]}),localDevice,session)),
            "duplicate original five selections are rejected");
        Check(Denied(()=>Verified(Done(localDevice,session,six,
            new[]{historical[0],historical[1],historical[2],
                historical[3],Guid.NewGuid().ToString("D")}),localDevice,session)),
            "foreign selected map not owned by DONE peer is rejected");
        Check(Denied(()=>LegacyStoreV1PeerTopology.RequireOriginalLocalSelection(
            JsonSerializer.SerializeToElement(historical),
            new[]{historical[0],historical[1],historical[2],
                historical[3],six[5].GetProperty("id").GetString()!})),
            "additional event map cannot replace historical selected writer");
        Check(Denied(()=>LegacyStoreV1PeerTopology.Validate(localDevice,new[]{
            verified,remote with{
                MemberIds=remote.MemberIds.Concat(new[]{historical[0]}).ToArray(),
                MapNames=remote.MapNames.Concat(new[]{"OtherName_WP"}).ToArray()
            }})),
            "extra map GUID colliding across the two peers is rejected");
        Check(Denied(()=>LegacyStoreV1PeerTopology.Validate(localDevice,new[]{
            verified,remote with{
                MemberIds=remote.MemberIds.Concat(new[]{Guid.NewGuid().ToString("D")}).ToArray(),
                MapNames=remote.MapNames.Concat(new[]{
                    six[5].GetProperty("map").GetString()!}).ToArray()
            }})),
            "extra map name colliding across peers is rejected");
        var tooMany=five.Concat(Members("TooMany",28)).ToArray();
        Check(Denied(()=>Verified(Done(localDevice,session,
            tooMany,historical),localDevice,session)),
            "33 local map members fail closed, preserving the existing 32-member bound");
    }
}
