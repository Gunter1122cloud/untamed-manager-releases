using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class VerifiedRestartTests
{
    private static void Check(bool condition,string message)
    {
        if(!condition)throw new Exception("Verified restart failed: "+message);
        Console.WriteLine("PASS verified-restart "+message);
    }

    private sealed class Fixture : IVerifiedRestartSession
    {
        public Guid MapId {get;}=Guid.NewGuid();
        public bool MissingApi,MissingJoin,WrongMap,StaleProof,NoExit,ReusedProcess,SwapAfterProbe;
        public int StartCalls,ReadyCalls;
        public readonly List<string> Commands=new();
        private bool exitRequested,started,checkChanged;
        private readonly ProcessIdentity original=new(){
            Pid=1006,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-3),
            ExecutablePath=@"C:\isolated\ArkAscendedServer.exe",
            Arguments="TheIsland_WP -log"
        };
        private ProcessIdentity? replacement;
        public ProcessCheck Check()
        {
            if(started)
            {
                if(SwapAfterProbe&&checkChanged)
                    return new(true,"different process",new ProcessIdentity {
                        Pid=replacement!.Pid+1,StartedUtc=replacement.StartedUtc,
                        ExecutablePath=replacement.ExecutablePath,Arguments=replacement.Arguments
                    });
                return new(true,"new running",replacement!);
            }
            if(exitRequested&&!NoExit)return new(false,"verified exited",null,true);
            return new(true,"old running",original);
        }
        public Task Command(string command,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(command!="SaveWorld"&&command!="DoExit"&&
               !command.StartsWith("ServerChat ",StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected synthetic command");
            Commands.Add(command);
            if(command=="DoExit")exitRequested=true;
            return Task.CompletedTask;
        }
        public Task<bool> SaveCompleted(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
        public void Start()
        {
            if(!exitRequested||NoExit)throw new InvalidOperationException("Cannot launch before exit");
            StartCalls++;
            started=true;
            replacement=ReusedProcess?new ProcessIdentity{
                Pid=original.Pid,StartedUtc=original.StartedUtc,
                ExecutablePath=original.ExecutablePath,Arguments=original.Arguments
            }:new ProcessIdentity {
                Pid=1007,StartedUtc=DateTimeOffset.UtcNow,
                ExecutablePath=original.ExecutablePath,Arguments=original.Arguments
            };
        }
        public Task<AsaReadinessSnapshot> ProbeReadiness(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReadyCalls++;
            checkChanged=true;
            return Task.FromResult(new AsaReadinessSnapshot(
                WrongMap?Guid.NewGuid():MapId,
                StaleProof?DateTimeOffset.UtcNow.AddMinutes(-10):DateTimeOffset.UtcNow,
                AsaRuntimeStage.RconAuthenticated,false,!MissingApi,!MissingJoin,
                "isolated synthetic ready proof"));
        }
    }

    public static async Task Run()
    {
        var success=new Fixture();
        var result=await SafeLifecycle.RestartVerified(success,TimeSpan.FromSeconds(2));
        Check(result.ProductionReady&&success.StartCalls==1&&success.ReadyCalls==1&&
            success.Commands.Count==3&&success.Commands[1]=="SaveWorld"&&
            success.Commands[2]=="DoExit",
            "Start called exactly once after save and verified exit; fresh API/RCON/join required");

        foreach(string mode in new[]{"api","join","map","stale","process","swap","noexit"})
        {
            var scenario=new Fixture {
                MissingApi=mode=="api",MissingJoin=mode=="join",
                WrongMap=mode=="map",StaleProof=mode=="stale",
                ReusedProcess=mode=="process",SwapAfterProbe=mode=="swap",
                NoExit=mode=="noexit"
            };
            bool failed=false;
            using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            try{await SafeLifecycle.RestartVerified(scenario,TimeSpan.FromMilliseconds(600),deadline.Token);}
            catch(OperationCanceledException){failed=true;}
            Check(failed&&(mode!="noexit"||scenario.StartCalls==0),
                "restart fails closed on "+mode+" without duplicate Start or forced Kill");
        }

        var cancelled=new Fixture();
        using var stop=new CancellationTokenSource();stop.Cancel();
        bool refused=false;
        try{await SafeLifecycle.RestartVerified(cancelled,TimeSpan.FromSeconds(1),stop.Token);}
        catch(OperationCanceledException){refused=true;}
        Check(refused&&cancelled.StartCalls==0&&cancelled.Commands.Count==0,
            "pre-cancelled restart cannot send RCON or start processes");
    }
}
