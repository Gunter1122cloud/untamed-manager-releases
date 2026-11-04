using System;
using System.Linq;
using UntamedManager.Core;

public static class ClusterTransferConsistencyTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new Exception("Transfer consistency failed: "+reason);
        Console.WriteLine("PASS transfer-consistency "+reason);
    }

    public static void Run()
    {
        Guid hp=Guid.NewGuid(),ryzen=Guid.NewGuid(),epoch=Guid.NewGuid();
        var maps=Enumerable.Range(0,10).Select(i=>
            new TransferMapOwner(Guid.NewGuid(),i<5?hp:ryzen)).ToArray();
        const string survivor="fixture-character",tribe="fixture-tribe";
        var character=new CharacterPlacement(survivor,tribe,maps[0].MapId,hp,
            false,new string('A',64),new string('B',64));
        var other=new CharacterPlacement("another-survivor",tribe,maps[2].MapId,hp,
            false,new string('C',64),new string('B',64));
        var initial=new TransferSnapshot(epoch,new[]{character,other});
        var relocated=character with {
            MapId=maps[5].MapId,HostId=ryzen,
            ProfileSha256=new string('D',64),TribeSha256=new string('E',64)
        };
        var completed=new TransferSnapshot(epoch,new[]{relocated,other});
        var travel=new ExpectedTravel(epoch,survivor,tribe,maps[0].MapId,maps[5].MapId);
        var accepted=ClusterTransferConsistency.VerifyTwoHostTransfer(maps,initial,completed,travel);
        Check(accepted.Valid&&accepted.VerifiedCharacters==2,
            "exactly-once test survivor travels between two 5-map hosts with preserved tribe ID");

        var duplicated=new TransferSnapshot(epoch,new[]{relocated,relocated,other});
        Check(!ClusterTransferConsistency.VerifyTwoHostTransfer(maps,initial,duplicated,travel).Valid,
            "duplicate character on destination map cannot pass transfer acceptance");
        var missing=new TransferSnapshot(epoch,new[]{other});
        Check(!ClusterTransferConsistency.VerifyTwoHostTransfer(maps,initial,missing,travel).Valid,
            "missing survivor after interrupted transfer fails closed");
        var alteredTribe=new TransferSnapshot(epoch,new[]{
            relocated with {TribeId="other-tribe"},other});
        Check(!ClusterTransferConsistency.VerifyTwoHostTransfer(maps,initial,alteredTribe,travel).Valid,
            "unexpected survivor tribe ownership change blocks acceptance");
        var alteredOwner=new TransferSnapshot(epoch,new[]{
            relocated with {HostId=hp},other});
        Check(!ClusterTransferConsistency.VerifyTwoHostTransfer(maps,initial,alteredOwner,travel).Valid,
            "foreign host cannot claim an owned map");
        var changedUnrelated=new TransferSnapshot(epoch,new[]{
            relocated,other with {ProfileSha256=new string('F',64)}});
        Check(!ClusterTransferConsistency.VerifyTwoHostTransfer(maps,initial,changedUnrelated,travel).Valid,
            "unrelated player data changed during transfer and invalidates frozen snapshot");
        var partial=new TransferSnapshot(epoch,new[]{
            relocated with {InTransit=true},other});
        Check(!ClusterTransferConsistency.VerifyTwoHostTransfer(maps,initial,partial,travel).Valid,
            "destination survivor still in-transit is not accepted as completed");
        Check(!ClusterTransferConsistency.VerifyTwoHostTransfer(maps,initial,
            completed with {Epoch=Guid.NewGuid()},travel).Valid,
            "stale session/epoch cannot be replayed");
        Check(!ClusterTransferConsistency.VerifyTwoHostTransfer(maps.Take(9).ToArray(),
            initial,completed,travel).Valid,
            "incomplete cluster map ownership rejects transfer certification");
        Check(initial.Characters[0]==character && completed.Characters[0]==relocated,
            "verifier does not mutate the input fixtures");
    }
}
