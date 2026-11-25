using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UntamedDesktop;

internal static class AsaShutdownCountdownTests
{
    private static void Check(bool ok,string label)
    {
        if(!ok)throw new InvalidOperationException("ASA COUNTDOWN: "+label);
        Console.WriteLine("PASS ASA countdown: "+label);
    }
    internal static async Task Run()
    {
        var messages=new List<string>();
        var waits=new List<TimeSpan>();
        await AsaShutdownCountdown.RunAsync(false,()=>true,(message,_)=>{
            messages.Add(message);return Task.CompletedTask;
        },CancellationToken.None,(delay,_)=>{waits.Add(delay);return Task.CompletedTask;});
        Check(messages.Count==5&&messages.SequenceEqual(Enumerable.Range(1,5).Reverse()
            .Select(n=>AsaShutdownCountdown.Message(n,false)))&&waits.Count==5&&
            waits.All(t=>t==TimeSpan.FromMinutes(1)),"exactly five restart messages with minute spacing");
        Check(AsaShutdownCountdown.Message(1,true).Contains("Szerverfrissites"),
            "rolling update has a distinct warning");
        messages.Clear();bool approved=true;bool denied=false;
        try
        {
            await AsaShutdownCountdown.RunAsync(false,()=>approved,(message,_)=>{
                messages.Add(message);return Task.CompletedTask;
            },CancellationToken.None,(_,_)=>{approved=false;return Task.CompletedTask;});
        }
        catch(InvalidOperationException ex) when(ex.Message=="UM-SHUTDOWN-COUNTDOWN-CANCELLED"){denied=true;}
        Check(denied&&messages.Count==1,"changed approval aborts without remaining messages or shutdown");
        int attempted=0;bool failed=false;
        try
        {
            await AsaShutdownCountdown.RunAsync(true,()=>true,(_,_)=>{
                attempted++;throw new InvalidOperationException("synthetic RCON failure");
            },CancellationToken.None,(_,_)=>Task.CompletedTask);
        }
        catch(InvalidOperationException ex) when(ex.Message=="synthetic RCON failure"){failed=true;}
        Check(failed&&attempted==1,"uncertain send never retries or advances to shutdown");
    }
}
