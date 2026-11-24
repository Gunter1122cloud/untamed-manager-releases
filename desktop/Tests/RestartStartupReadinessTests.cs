using System;
using System.Threading;
using System.Threading.Tasks;
using UntamedDesktop;

// No network or ASA is started. The fixture feeds exact current-startup
// evidence directly to the production polling state machine.
internal static class RestartStartupReadinessTests
{
    private static void Check(bool value,string label)
    {
        if(!value)throw new InvalidOperationException(
            "RESTART STARTUP READINESS: "+label);
        Console.WriteLine("PASS restart readiness: "+label);
    }
    private static RestartStartupProof Proof(bool loader,bool asa,bool dll,
        bool plugins,bool rcon,bool join,string reason="WAITING")=>
        new(42887,DateTimeOffset.UtcNow,loader,asa,dll,plugins,rcon,join,reason);

    public static async Task Run()
    {
        var pidOnly=Proof(true,true,false,false,false,false);
        Check(!pidOnly.Ready(true)&&!pidOnly.Ready(false),
            "an ASA child PID alone never certifies restart");
        Check(!Proof(true,true,true,false,true,true).Ready(true),
            "API plugin loading is independently required");
        Check(!Proof(true,true,false,true,true,true).Ready(true),
            "AsaApi.dll evidence is independently required");
        Check(!Proof(true,true,true,true,false,true).Ready(true),
            "late RCON is not readiness");
        Check(!Proof(true,true,true,true,true,false).Ready(true),
            "no advertising-for-join means not ready");
        Check(!Proof(false,true,true,true,true,true).Ready(true),
            "orphaned Loader never certifies API mode");
        int attempts=0;
        var late=await RestartStartupReadiness.WaitAsync(_=>
        {
            attempts++;
            return Task.FromResult(attempts<3?
                Proof(true,true,true,true,false,true):
                Proof(true,true,true,true,true,true,"UM-RESTART-READY"));
        },true,TimeSpan.FromSeconds(3),TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Check(late.Ready(true)&&attempts==3,
            "late authenticated RCON becomes ready without a second Start");
        attempts=0;
        var earlyExit=await RestartStartupReadiness.WaitAsync(_=>
        {
            attempts++;
            return Task.FromResult(attempts==1?pidOnly:
                Proof(true,false,false,false,false,false,
                    "UM-RESTART-NEW-ASA-EXITED-OR-REPLACED"));
        },true,TimeSpan.FromSeconds(3),TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Check(!earlyExit.Ready(true)&&attempts==2&&
              earlyExit.Reason=="UM-RESTART-NEW-ASA-EXITED-OR-REPLACED",
            "ASA exit before join is terminal, not an automatic restart");
        attempts=0;
        var orphan=await RestartStartupReadiness.WaitAsync(_=>
        {
            attempts++;
            return Task.FromResult(Proof(false,true,false,false,false,false,
                "UM-RESTART-API-LOADER-EXITED-OR-REPLACED"));
        },true,TimeSpan.FromSeconds(3),TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Check(!orphan.Ready(true)&&attempts==1,
            "orphan loader is immediately terminal for API restart");
        Check(Proof(false,true,true,true,true,true,"UM-RESTART-READY").Ready(false),
            "direct ASA mode requires join and RCON but not an API loader");
    }
}
