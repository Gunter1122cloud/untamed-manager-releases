using System;
using UntamedManager.Core;

internal static class FreshStoreMultiHostTests
{
    internal static void Run()
    {
        string owner=Guid.NewGuid().ToString("N"),a=Guid.NewGuid().ToString("N"),b=Guid.NewGuid().ToString("N");
        Guid local=Guid.NewGuid(),ma=Guid.NewGuid(),mb=Guid.NewGuid();
        string tag=new('A',64);
        var registry=new ClusterRegistrySnapshot{
            HostRegistry=new HostRegistry{Hosts=new(){new(){HostId=owner,IsLocal=true},new(){HostId=a,IsPaired=true},new(){HostId=b,IsPaired=true}},
                Maps=new(){new(){MapId=local,HostId=owner},new(){MapId=ma,HostId=a},new(){MapId=mb,HostId=b}}},
            ClusterRegistry=new ClusterRegistry{MapAssignments=new(){new(){MapId=local,ClusterId="fresh"},new(){MapId=ma,ClusterId="fresh"},new(){MapId=mb,ClusterId="fresh"}}}
        };
        var now=DateTimeOffset.UtcNow;
        LanPeerObservation Peer(string host,Guid map,string hash,DateTimeOffset utc)=>new(){
            HostId=host,IsFresh=true,StateCode="UM-PEER-ONLINE",
            Snapshot=new(){HostId=host,ObservedUtc=utc,Maps=new(){new(){Id=map.ToString("D")}},
                FreshStoreTransfers=new[]{new FreshStorePeerTransfer("fresh",hash,new[]{map})}}};
        var pa=Peer(a,ma,tag,now);var pb=Peer(b,mb,tag,now);
        bool Match(params LanPeerObservation[] peers)=>FreshStoreMultiHostTransfer.Matches(registry,"fresh",owner,tag,peers,now);
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("FRESH N-HOST: "+label);Console.WriteLine("PASS fresh N-host: "+label);}
        Check(Match(pa,pb),"three hosts agree on exact transfer and owners");
        Check(!Match(pa),"missing third host denied");
        Check(!Match(pa,Peer(b,mb,new string('B',64),now)),"different transfer denied");
        Check(!Match(pa,Peer(b,mb,tag,now.AddHours(-1))),"stale proof denied");
        Check(!Match(pa,Peer(b,ma,tag,now)),"wrong member denied");
        Check(!Match(pa,pb,pb),"duplicate peer evidence denied");
        Check(FreshStoreMultiHostTransfer.Pin("fresh",tag)!=FreshStoreMultiHostTransfer.Pin("other",tag),"pin bound to cluster");
    }
}
