using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UntamedManager.Core;

/// <summary>
/// Native Core integration: partial previous Store imports + running original
/// five-map handoff use ONE atomic Manager profile write. These are synthetic
/// files under isolated TEMP, not real Kova, SMB, ASA, RCON or game data.
/// Production bridge must separately prove current Windows WMI + two peers
/// under the native cross-process installation leases.
/// </summary>
internal static class OriginalStoreLiveReconcileTests
{
    private static void Check(bool passed,string label)
    {
        if(!passed)throw new InvalidOperationException(
            "ORIGINAL STORE LIVE RECONCILE: "+label);
        Console.WriteLine("PASS Store-v1 mixed running reconcile: "+label);
    }
    private static bool Denies(Action action)
    {
        try{action();return false;}
        catch(Exception ex) when(ex is InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {return true;}
    }
    public static async Task Run(string testsRoot)
    {
        string root=Path.Combine(testsRoot,"original-store-live-reconcile-only");
        Directory.CreateDirectory(root);
        string[] mapNames={"The Island","Extinction","Valguero",
            "Astraeos","Genesis Part 1"};
        string[] mapKeys={"TheIsland_WP","Extinction_WP","Valguero_WP",
            "Astraeos_WP","Genesis_WP"};
        var live=new List<LocalMap>();
        var originalFiles=new Dictionary<string,byte[]>();
        DateTimeOffset now=DateTimeOffset.UtcNow.AddMinutes(-17);
        for(int i=0;i<5;i++)
        {
            string install=Path.Combine(root,"original-asa-"+i);
            string bin=Path.Combine(install,"ShooterGame","Binaries","Win64");
            Directory.CreateDirectory(bin);
            string exe=Path.Combine(bin,"ArkAscendedServer.exe");
            File.WriteAllBytes(exe,new byte[]{0x4d,0x5a,1,2});
            originalFiles[exe]=File.ReadAllBytes(exe);
            string loader=Path.Combine(bin,"AsaApiLoader.exe");
            if(i==4)
            {
                File.WriteAllBytes(loader,new byte[]{0x4d,0x5a,2,2});
                originalFiles[loader]=File.ReadAllBytes(loader);
            }
            var map=new LocalMap{
                Id=Guid.NewGuid(),Name=mapNames[i],
                InstallationPath=install,ExecutablePath=exe,
                GamePort=7787+10*i,QueryPort=27115+10*i,
                HistoricalStoreProtected=true,
                HistoricalArchiveSha256=new string('A',64),
                HistoricalMapKey=mapKeys[i],
                HistoricalEnabledMods=new List<string>{"123456","987654"},
                HistoricalPassiveMods=new List<string>{"123456"},
                Arguments="",ApiLoaderArguments="",AutoStart=false,
                MonitorRcon=false,StartWithAsaApi=i==4,
                ApiLoaderPath=i==4?loader:"",RconPort=0,
                LastProcess=new ProcessIdentity{
                    Pid=69000+i,
                    StartedUtc=now.AddSeconds(i*7),
                    ExecutablePath=exe,Arguments=i==4?
                        "HISTORICAL_STORE_V1_API_CHILD_VERIFIED":
                        "HISTORICAL_STORE_V1_READ_ONLY_OBSERVATION"
                },
                LastLoader=i==4?new ProcessIdentity{
                    Pid=70004,StartedUtc=now.AddSeconds(28),
                    ExecutablePath=loader,
                    Arguments="HISTORICAL_STORE_V1_API_LOADER_VERIFIED"
                }:null
            };
            live.Add(map);
        }
        await using var cluster=new ClusterService(
            "synthetic-five-Kova-mixed",
            Path.Combine(root,"manager-profile"),
            _=>new ProcessCheck(false,"Synthetic - no real process",null,false));
        // Simulate maps imported by an OLDER release: RCON has already
        // been configured on one, others lack live PID/Loader discovery.
        int[] registered={0,1,4};
        const int rconPort=37110;
        const string pass="OriginalSyntheticRcon_2026";
        string ini=AsaInitialRconSettings.NewIniPath(live[0].InstallationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+rconPort+"\r\nServerAdminPassword="+pass+"\r\n");
        originalFiles[ini]=File.ReadAllBytes(ini);
        foreach(var i in registered)
        {
            var current=System.Text.Json.JsonSerializer.Deserialize<LocalMap>(
                System.Text.Json.JsonSerializer.Serialize(live[i]))!;
            current.StartWithAsaApi=false;
            current.ApiLoaderPath="";
            current.LastLoader=null;
            current.LastProcess=null;
            if(i==0)
            {
                current.RconHost="127.0.0.1";
                current.RconPort=rconPort;
                current.RconPasswordProtected=SecureStore.Protect(pass);
            }
            cluster.AddMap(current);
        }
        var before=cluster.Config.LocalMaps.ToArray();
        string secret=before[0].RconPasswordProtected;
        var preview=cluster.PreviewOriginalStoreLiveFive(live);
        Check(preview.Added==2&&preview.Reattached==3,
            "two missing map profiles and three existing original map IDs recognized");

        Check(Denies(()=>cluster.ReconcileOriginalStoreLiveFive(live,()=>false))&&
              cluster.Config.LocalMaps.Count==3&&
              cluster.Config.LocalMaps.SequenceEqual(before),
            "failed under-lease physical/source proof never changes the Manager registry");
        File.WriteAllText(ini,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+rconPort+"\r\nServerAdminPassword=TAMPERED\r\n");
        Check(Denies(()=>cluster.PreviewOriginalStoreLiveFive(live))&&
              cluster.Config.LocalMaps.Count==3,
            "existing encrypted RCON credentials must still match unchanged original INI");
        File.WriteAllBytes(ini,originalFiles[ini]);

        var promoted=cluster.ReconcileOriginalStoreLiveFive(live,()=>true);
        Check(promoted.Added==2&&promoted.Reattached==3&&
              cluster.Config.LocalMaps.Count==5,
            "all original five map GUIDs preserved in one verified Manager CAS");
        Check(live.All(source=>{
            var actual=cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==source.Id);
            return actual!=null&&
                actual.HistoricalArchiveSha256==source.HistoricalArchiveSha256&&
                actual.HistoricalMapKey==source.HistoricalMapKey&&
                actual.GamePort==source.GamePort&&actual.QueryPort==source.QueryPort&&
                actual.HistoricalEnabledMods.SequenceEqual(source.HistoricalEnabledMods)&&
                actual.HistoricalPassiveMods.SequenceEqual(source.HistoricalPassiveMods)&&
                actual.LastProcess?.Pid==source.LastProcess?.Pid&&
                actual.LastProcess?.StartedUtc==source.LastProcess?.StartedUtc&&
                actual.StartWithAsaApi==source.StartWithAsaApi&&
                actual.LastLoader?.Pid==source.LastLoader?.Pid;
        }),"source identity, mod order, PID/start and verified ASA API mode preserved");
        Check(cluster.Config.LocalMaps.Single(m=>m.Id==live[0].Id)
              .RconPasswordProtected==secret&&
              cluster.Config.LocalMaps.Single(m=>m.Id==live[0].Id)
              .RconPort==rconPort,
            "preexisting DPAPI RCON credential and port preserved, not replaced");
        Check(cluster.Config.LocalMaps.Single(m=>m.Id==live[4].Id)
              .StartWithAsaApi&&
              cluster.Config.LocalMaps.Single(m=>m.Id==live[4].Id)
              .LastLoader?.Pid==70004,
            "historical stopped-mode Genesis profile correctly reattaches to its live Loader");
        Check(!ReferenceEquals(cluster.Config.LocalMaps.Single(m=>
                  m.Id==live[2].Id),live[2]),
            "newly imported live process evidence cannot alias Manager-owned profile");
        var again=cluster.ReconcileOriginalStoreLiveFive(live,()=>true);
        Check(again.Unchanged==5&&again.Added==0,
            "same process IDs reattach idempotently without map duplication");
        foreach(var (file,bytes) in originalFiles)
            Check(File.ReadAllBytes(file).SequenceEqual(bytes),
                "the synthetic original ASA/INI source file is byte-identical");
        var foreign=cluster.Config.LocalMaps.Single(m=>m.Id==live[2].Id);
        foreign.HistoricalMapKey="WrongMap_WP";
        Check(Denies(()=>cluster.PreviewOriginalStoreLiveFive(live)),
            "wrong original Store map key is rejected on an already owned GUID");
        foreign.HistoricalMapKey=live[2].HistoricalMapKey;
        Check(cluster.Config.LocalMaps.Count==5&&
              cluster.Config.LocalMaps.Select(m=>m.Id).Distinct().Count()==5,
            "no partial map regrowth after denied owner conflict");
    }
}
