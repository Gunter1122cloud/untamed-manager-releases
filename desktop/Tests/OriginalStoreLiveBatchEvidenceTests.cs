using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UntamedDesktop;
using UntamedManager.Core;

/// <summary>
/// Exact original Kova five-map running import regression in isolated TEMP.
/// Pure WMI sample classification; no real ASA, UNC, RCON, Store writer, or
/// Kova source is accessed. The production bridge separately re-reads real
/// Windows WMI and BOTH original shared DONE peers before registration.
/// </summary>
internal static class OriginalStoreLiveBatchEvidenceTests
{
    private static void Check(bool valid,string what)
    {
        if(!valid)throw new InvalidOperationException(
            "ORIGINAL LIVE STORE FIVE TEST FAILED: "+what);
        Console.WriteLine("PASS original live Store-v1 five: "+what);
    }
    private static bool Denies(Action act)
    {
        try{act();return false;}
        catch(Exception ex) when(ex is InvalidDataException or IOException
            or ArgumentException or UnauthorizedAccessException){return true;}
    }
    public static void Run(string testRoot)
    {
        string root=Path.Combine(testRoot,"isolated-original-live-five-no-writes");
        Directory.CreateDirectory(root);
        string[] names={"TheIsland_WP","Extinction_WP","Valguero_WP",
            "Astraeos_WP","Genesis_WP"};
        var maps=new List<LegacyStoreV1OriginalMap>();
        var sample=new List<LocalRunningAsaDiscovery.Sample>();
        string unc=@"\\SYNTHETIC-NOT-REAL\SyntheticCluster";
        var started=DateTimeOffset.UtcNow.AddMinutes(-11);
        int childPid=23000;
        foreach(var (key,i) in names.Select((x,i)=>(x,i)))
        {
            string install=Path.Combine(root,"asa-"+i);
            string bin=Path.Combine(install,"ShooterGame","Binaries","Win64");
            Directory.CreateDirectory(bin);
            string exe=Path.Combine(bin,"ArkAscendedServer.exe");
            File.WriteAllBytes(exe,new byte[]{77,90,2,0});
            int game=7777+i*10,query=27100+i*10;
            var map=new LegacyStoreV1OriginalMap(Guid.NewGuid(),key,install,
                game,query,Array.Empty<string>(),Array.Empty<string>());
            maps.Add(map);
            string args=string.Join(" ",LegacyStoreV1ActiveLaunch.Build(
                new LegacyStoreV1ActiveLaunch.Input(key,game,query,
                    Array.Empty<string>(),Array.Empty<string>(),
                    VaultProtocol.ClusterId,unc,"active","")).Arguments);
            int loaderPid=30000+i;
            if(i==4)
            {
                string loader=Path.Combine(bin,"AsaApiLoader.exe");
                File.WriteAllBytes(loader,new byte[]{77,90,4,0});
                sample.Add(new(loaderPid,400,started,"AsaApiLoader.exe",
                    loader,"\""+loader+"\" "+args));
            }
            sample.Add(new(childPid+i,i==4?loaderPid:400,started.AddSeconds(4),
                "ArkAscendedServer.exe",exe,"\""+exe+"\" "+args));
        }
        var selection=new LegacyStoreV1OriginalLocalSelection(
            new string('A',64),new string('B',64),maps.ToArray());
        var good=OriginalStoreLiveBatchEvidence.Classify(selection,sample);
        Check(good.Maps.Length==5 && good.DirectProcesses==4 &&
              good.ApiLoaderPairs==1 &&
              good.Maps.Single(m=>m.HistoricalMapKey=="Genesis_WP")
                  .StartWithAsaApi &&
              good.Maps.All(m=>m.HistoricalStoreProtected &&
                  m.Arguments=="" && m.ApiLoaderArguments=="" &&
                  !m.AutoStart && !m.MonitorRcon && m.RconPort==0 &&
                  m.LastProcess is {Pid:>0}),
            "original five profiles preserve IDs, original launch and API relationship");
        Check(good.Maps.Select(m=>m.Id).SequenceEqual(maps.Select(m=>m.MapId)) &&
              good.Maps[4].LastLoader?.Pid==30004 &&
              good.Maps[4].LastProcess?.Pid==23004,
            "exact original GUID, loader parent and ASA child PID retained");
        Check(Denies(()=>OriginalStoreLiveBatchEvidence.Classify(selection,
            sample.Where(x=>x.Pid!=23002).ToArray())),
            "missing third original Store writer is never imported as stopped");
        Check(Denies(()=>OriginalStoreLiveBatchEvidence.Classify(selection,
            sample.Append(sample[1] with{Pid=43999}).ToArray())),
            "duplicate original server PID candidate is rejected");
        Check(Denies(()=>OriginalStoreLiveBatchEvidence.Classify(selection,
            sample.Select(x=>x.Pid==23000?x with{
                CommandLine=x.CommandLine+" -converttostore"}:x).ToArray())),
            "Store conversion flag cannot be accepted as original ACTIVE");
        Check(Denies(()=>OriginalStoreLiveBatchEvidence.Classify(selection,
            sample.Select(x=>x.Pid==23001?x with{StartedUtc=null}:x).ToArray())),
            "unreadable Windows process creation time fails closed");
        Check(Denies(()=>OriginalStoreLiveBatchEvidence.Classify(selection,
            sample.Select(x=>x.Pid==23004?x with{ParentPid=99881}:x).ToArray())),
            "unverified ASA API parent-child relationship denied");
        Check(Denies(()=>OriginalStoreLiveBatchEvidence.Classify(selection,
            sample.Select(x=>x.Pid==23003?x with{
                CommandLine=x.CommandLine!.Replace("-Port=7807","-Port=7999")}:x).ToArray())),
            "changed original Game port is denied");
        Check(Denies(()=>OriginalStoreLiveBatchEvidence.Classify(selection,
            sample.Append(sample[0] with{
                Pid=55555,ExecutablePath=null}).ToArray())),
            "unreadable Store-class process executable path denies safely");
        var replay=OriginalStoreLiveBatchEvidence.Classify(selection,
            sample.Select(x=>x.Pid==23000?x with{
                StartedUtc=x.StartedUtc!.Value.AddSeconds(8)}:x).ToArray());
        Check(replay.ProcessSha256!=good.ProcessSha256,
            "PID creation-time change invalidates native approval fingerprint");
        Check(sample.All(x=>x.CommandLine!.Contains("-usestore") &&
              !x.CommandLine.Contains("-converttostore")),
            "original source grammar remains conversion-free after all regressions");
    }
}
