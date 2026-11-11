using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UntamedDesktop;
using UntamedManager.Core;

/// <summary>
/// Entirely synthetic, pure evidence-policy tests. No UK-HOME hosts, Kova,
/// real UNC share, Windows services, RCON or running ASA processes accessed.
/// </summary>
internal static class OriginalStoreHandoffReadinessTests
{
    private static void Check(bool pass,string label)
    {
        if(!pass)throw new InvalidOperationException(
            "ORIGINAL STORE READINESS POLICY FAILED: "+label);
        Console.WriteLine("PASS Store-v1 read-only evidence: "+label);
    }

    public static void Run()
    {
        var original=new List<LegacyStoreV1OriginalMap>();
        var maps=new List<LocalMap>();
        var snapshot=new ClusterRegistrySnapshot {
            HostRegistry=new HostRegistry {LocalHostId=Guid.NewGuid().ToString("D")},
            ClusterRegistry=new ClusterRegistry {
                Clusters=new List<RegisteredCluster>{
                    new() {ClusterId="KovaCluster",
                        TransferDirectory=OriginalStoreHandoffReadiness.RequiredTransferRoot}
                }
            }
        };
        string fingerprint=new string('A',64);
        for(int i=0;i<5;i++)
        {
            var id=Guid.NewGuid();
            string root=Path.Combine(Path.GetTempPath(),
                "Untamed-Integration-Readiness-NEVER-USED",i.ToString());
            var enabled=new[]{"1234567890","1234567891"};
            var passive=new[]{"1234567890"};
            var source=new LegacyStoreV1OriginalMap(id,"Map"+i+"_WP",
                root,7777+i*10,27015+i*10,enabled,passive);
            original.Add(source);
            maps.Add(new LocalMap {
                Id=id,Name="Map"+i,
                HistoricalStoreProtected=true,
                HistoricalArchiveSha256=fingerprint,
                HistoricalMapKey=source.MapKey,
                InstallationPath=root,
                GamePort=source.GamePort,QueryPort=source.QueryPort,
                HistoricalEnabledMods=enabled.ToList(),
                HistoricalPassiveMods=passive.ToList()
            });
            snapshot.HostRegistry.Maps.Add(new RegisteredMap {
                MapId=id,HostId=snapshot.HostRegistry.LocalHostId,
                MapName="Map"+i,LocalConfigurationPresent=true
            });
            snapshot.ClusterRegistry.MapAssignments.Add(
                new ClusterMapAssignment {MapId=id,ClusterId="KovaCluster"});
        }
        var sourceFive=new LegacyStoreV1OriginalLocalSelection(
            fingerprint,new string('B',64),original.ToArray());
        OriginalStoreHandoffReadiness.Report Inspect(
            IReadOnlyList<LocalMap>? chosen=null,
            bool native=true,string? originalPath=null,int direct=4,int loaders=1)=>
            OriginalStoreHandoffReadiness.Evaluate(sourceFive,chosen??maps,
                snapshot,native,originalPath ??
                    OriginalStoreHandoffReadiness.RequiredTransferRoot,
                direct,loaders);

        var good=Inspect();
        Check(good.ExactlyFiveOriginal&&good.OriginalSourceMapCount==5,
            "exactly five Kova originals, no inferred sixth");
        Check(good.ExactLocalRegistrations==5&&
              good.HostRegistryRegistrations==5&&good.ClusterAssignments==5&&
              good.Maps.All(m=>m.ExactLocalRegistration),
            "all five original GUID/ports/root/mod order/cluster members");
        Check(good.NativeFiveProcessProof&&good.DirectAsaProcesses==4&&
              good.VerifiedLoaderAsaPairs==1&&good.ClusterConfigurationCompliant,
            "synthetic native WMI and KovaCluster UNC evidence separated");
        Check(good.RconProfileConfiguredCount==0&&
              good.Blockers.Contains("OBSIDIAN_RCON_PROFILE_MISSING_OR_INCOMPLETE"),
            "five running and registered maps with port zero have no authorized Obsidian RCON");
        Check(!good.ExclusiveControllerHandoffVerified&&
              !good.KovaWatchdogDisabledVerified&&
              !good.ScheduledTasksReviewed&&
              !good.OriginalStoreSteamUpdateAuthorized,
            "a correct registry never grants Kova watchdog, control or Steam rights");
        Check(!good.EmbeddedCharacterIntegrityVerified&&
              !good.GameClientRejoinVerified&&
              !good.RoundTripGameplayVerified,
            "synthetic A-to-B-to-A cannot certify real gameplay or embedded survivor");

        var configuredRcon=maps[0];
        configuredRcon.RconPort=37055;
        configuredRcon.RconPasswordProtected="SYNTHETIC-CREDENTIAL-PLACEHOLDER";
        Check(Inspect().RconProfileConfiguredCount==1,
            "a saved RCON port and credential may be counted, never confused with authentication");
        configuredRcon.RconPort=0;
        configuredRcon.RconPasswordProtected="";
        var missing=Inspect(maps.Take(4).ToArray());
        Check(missing.ExactLocalRegistrations==4&&
              !missing.ClusterConfigurationCompliant&&
              missing.Blockers.Contains("ORIGINAL_LOCAL_REGISTRATION_INCOMPLETE"),
            "missing one original map fails closed");
        var wrongMods=maps.Select(m=>m).ToList();
        var bad=maps[1];
        wrongMods[1]=new LocalMap {
            Id=bad.Id,Name=bad.Name,HistoricalStoreProtected=true,
            HistoricalArchiveSha256=bad.HistoricalArchiveSha256,
            HistoricalMapKey=bad.HistoricalMapKey,
            InstallationPath=bad.InstallationPath,
            GamePort=bad.GamePort,QueryPort=bad.QueryPort,
            HistoricalEnabledMods=new List<string>{"1234567891","1234567890"},
            HistoricalPassiveMods=bad.HistoricalPassiveMods.ToList()
        };
        Check(Inspect(wrongMods).ExactLocalRegistrations==4,
            "changed original mod order is not registered parity");
        var wrongCluster=OriginalStoreHandoffReadiness.Evaluate(
            sourceFive,maps,snapshot,true,@"\\192.168.1.200\WrongRoot",4,1);
        Check(!wrongCluster.VerifiedSourceRootMatches&&
              !wrongCluster.ClusterConfigurationCompliant,
            "different source UNC path always blocks compliance");
        var unknownWmi=Inspect(native:false);
        Check(!unknownWmi.NativeFiveProcessProof&&
              !unknownWmi.ClusterConfigurationCompliant,
            "unknown ASA Loader/PID proof cannot be inferred from green status");
        var countMismatch=Inspect(native:true,direct:5,loaders:1);
        Check(!countMismatch.NativeFiveProcessProof,
            "extra Loader process count rejects exactly-five physical proof");
        var fourSource=sourceFive with { Maps=original.Take(4).ToArray() };
        var count=OriginalStoreHandoffReadiness.Evaluate(
            fourSource,maps,snapshot,false);
        Check(!count.ExactlyFiveOriginal&&
              count.Blockers.Contains("ORIGINAL_SOURCE_NOT_EXACTLY_FIVE"),
            "four-map Kova source cannot be silently accepted");
    }
}
