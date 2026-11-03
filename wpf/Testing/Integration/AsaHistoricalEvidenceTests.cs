using System;
using System.IO;
using System.Linq;
using UntamedManager.Core;

public static class AsaHistoricalEvidenceTests
{
    private static void Assert(bool b,string label)
    {
        if(!b)throw new Exception("HISTORICAL LOG TEST FAILED: "+label);
        Console.WriteLine("PASS historical log "+label);
    }
    public static void Run()
    {
        // Fully synthetic records, same timestamp/ordering semantics as an
        // operator-provided copy. Never commit actual user logs or player data.
        var start=new[]{
            "1: [2026.11.02-07.02.15:841][  0]Log file open, 11/02/26 08:02:15",
            "163: [2026.11.02-07.02.56:147][ 26]Server: \"TEST\" has successfully started!",
            "285: [2026.11.02-07.03.23:171][250]Server has completed startup and is now advertising for join."
        };
        var api=new[]{
            "2: 11/02/26 08:02 [API][info] ARK:SA Api V2.03",
            "15: 11/02/26 08:02 [API][info] Initialized hooks",
            "17: 11/02/26 08:02 [API][info] API was successfully loaded",
            "23: 11/02/26 08:02 [API][info] Loaded plugin SyntheticPlugin"
        };
        var ok=AsaHistoricalLogReader.InspectLines(start,api);
        Assert(ok.LogOpened &&ok.ServerStarted &&ok.JoinAdvertised &&
            ok.ApiLoaded &&ok.ApiHooksInitialized &&ok.ApiSameStartWindow &&
            ok.ApiPluginLines==1,"paired API startup and late advertise");
        Assert(ok.StartedToAdvertising.HasValue &&
            ok.StartedToAdvertising.Value.TotalSeconds>26 &&
            ok.StartedToAdvertising.Value.TotalSeconds<28,
            "separate started and advertising timestamps, not early-ready");
        Assert(!ok.LiveControlReady && !ok.SaveCompleted &&
            !ok.ShutdownSequenceConfirmed,
            "log evidence never becomes live RCON, SaveComplete, or shutdown proof");

        var earlier=new[]{
            "1: [2026.11.01-06.15.31:508][ 0]Log file open, 11/01/26 07:15:31",
            "155: [2026.11.01-06.16.20:461][31]Server: \"TEST\" has successfully started!",
            "277: [2026.11.01-06.16.50:025][255]Server has completed startup and is now advertising for join.",
            "470: [2026.11.02-05.20.27:662][78]Closing by request",
            "471: [2026.11.02-05.20.27:662][78]DestroyASAClustersFolderMutex",
            "472: [2026.11.02-05.20.42:730][79]Log file closed, 11/02/26 06:20:42"
        };
        var closure=AsaHistoricalLogReader.InspectLines(earlier,null);
        Assert(closure.ShutdownSequenceConfirmed&&closure.LogClosed&&
            !closure.ApiSameStartWindow&&!closure.SaveCompleted,
            "prior shutdown only proves its own graceful log closure");

        var mix=AsaHistoricalLogReader.InspectLines(new[]{
            start[0],start[1],
            "1: [2026.11.02-08.00.00:000][ 0]Log file open, 11/02/26 09:00:00",
            "2: [2026.11.02-08.00.10:000][ 0]Server has completed startup and is now advertising for join."
        },api);
        Assert(!mix.ServerStarted&&!mix.JoinAdvertised&&!mix.ApiSameStartWindow,
            "evidence from different start sessions cannot be combined");

        var wrongApi=AsaHistoricalLogReader.InspectLines(start,new[]{
            "12/03/26 08:02 [API][info] Initialized hooks",
            "12/03/26 08:02 [API][info] API was successfully loaded"});
        Assert(wrongApi.ServerStarted&&wrongApi.JoinAdvertised&&
            !wrongApi.ApiSameStartWindow,
            "unmatched ArkApi date cannot certify same session");

        var early=AsaHistoricalLogReader.InspectLines(start.Take(2),api);
        Assert(!early.JoinAdvertised&&!early.LiveControlReady&&!early.SaveCompleted,
            "successfully started is not join advertising or SaveComplete");

        var reverse=AsaHistoricalLogReader.InspectLines(new[]{
            start[0],
            "2: [2026.11.02-07.02.20:000][ 2]Server has completed startup and is now advertising for join.",
            start[1]
        },null);
        Assert(reverse.ServerStarted&&!reverse.JoinAdvertised,
            "out-of-order advertising is never accepted");
    }
}
