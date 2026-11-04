using UntamedManager.Core;

public static class DashboardSummaryTests
{
    private static void Check(bool condition,string description)
    {
        if(!condition)throw new Exception("Dashboard summary: "+description);
        Console.WriteLine("PASS dashboard "+description);
    }

    public static void Run()
    {
        var empty=new DashboardSummary(0,null,0,0,false);
        Check(empty.MapCount==0 && empty.HostCount==1 &&
            empty.FreshLanPeers==0 && empty.ModCountDisplay=="0",
            "empty first-use profile never shows ten historical maps or a connected peer");
        var partial=new DashboardSummary(5,null,12,2,false);
        Check(partial.MapCount==5 && partial.HostCount==1 &&
            partial.ModCountDisplay=="12+" && partial.ModCoverageDisplay.Contains("2 / 5"),
            "partial mod inventory is explicitly marked incomplete");
        var stale=new DashboardSummary(5,5,21,5,false);
        Check(stale.MapCount==10 && stale.HostCount==2 &&
            stale.FreshLanPeers==0 && stale.ModCountDisplay=="21",
            "stale peer may be counted as historically known, not as a live LAN connection");
        var fresh=new DashboardSummary(5,5,21,5,true);
        Check(fresh.FreshLanPeers==1,
            "authenticated fresh LAN peer is counted exactly once");
        bool rejected=false;
        try{_ = new DashboardSummary(1,null,0,2,true);}
        catch(ArgumentOutOfRangeException){rejected=true;}
        Check(rejected,"inconsistent observations are rejected rather than fabricated");
    }
}
