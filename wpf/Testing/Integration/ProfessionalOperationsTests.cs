using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class ProfessionalOperationsTests
{
    private static void Check(bool passed,string name)
    {
        if(!passed)throw new Exception("INTEGRATION TEST FAILED v1.6: "+name);
        Console.WriteLine("PASS v1.6 "+name);
    }
    public static async Task Run(string root,string fakeAsa)
    {
        string archiveRoot=Path.Combine(root,"v16-prepared");
        string hpRoot=Path.Combine(archiveRoot,"hp");
        string ryzenRoot=Path.Combine(archiveRoot,"ryzen");
        string sqlRoot=Path.Combine(archiveRoot,"sql");
        Directory.CreateDirectory(hpRoot);
        Directory.CreateDirectory(ryzenRoot);
        Directory.CreateDirectory(sqlRoot);
        var hpMaps=new[]{"Astraeos_WP","Genesis_WP","Valguero_WP",
            "TheIsland_WP","Extinction_WP"};
        var ryzenMaps=new[]{"ScorchedEarth_WP","LostColony_WP","Ragnarok_WP",
            "TheCenter_WP","Aberration_WP"};
        Guid[] hpIds=Enumerable.Range(0,5).Select(_=>Guid.NewGuid()).ToArray();
        Guid[] ryzenIds=Enumerable.Range(0,5).Select(_=>Guid.NewGuid()).ToArray();

        string[] MakeHost(string directory,string[] names,Guid[] ids)
        {
            var packages=new string[5];
            for(int i=0;i<5;i++)
            {
                string source=Path.Combine(root,"v16-staging-"+names[i]);
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source,"world-test.txt"),"synthetic "+names[i]);
                packages[i]=Path.Combine(directory,names[i]);
                TestBackupPackage.Create(source,packages[i],ids[i],
                    "FakeAsa stopped map data fixture; NOT live ASA consistency");
            }
            return packages;
        }
        var h=MakeHost(hpRoot,hpMaps,hpIds);
        var b=MakeHost(ryzenRoot,ryzenMaps,ryzenIds);
        string sqlInputs=Path.Combine(root,"v16-sql-exports");
        Directory.CreateDirectory(sqlInputs);
        foreach(var db in OfflineSqlArchive.SupportedDatabases)
        {
            string path=Path.Combine(sqlInputs,db+".sql");
            File.WriteAllText(path,"-- simulated exported "+db+"\nSELECT 1;\n");
            string created=OfflineSqlArchive.ImportFixture(
                Path.Combine(root,"v16-sql-profile"),path,db,Guid.NewGuid()).Package;
            Directory.Move(created,Path.Combine(sqlRoot,db));
        }

        string output=Path.Combine(root,"v16-10map-3sql-offline-cluster");
        var combined=OfflineClusterBackup.BuildFromFolders(
            hpRoot,ryzenRoot,sqlRoot,output);
        Check(combined.Items.Length==13 &&
            combined.Items.Count(x=>x.Host=="HP")==5 &&
            combined.Items.Count(x=>x.Host=="RYZEN")==5 &&
            combined.Items.Count(x=>x.Host=="SQL")==3,
            "two-host physical file copy packages exactly 10 maps and 3 SQL offline exports");
        Check(!combined.RealAsaConsistencyProven &&
            !combined.KovaStoreIncluded &&
            !combined.MariaDbSnapshotConsistencyProven &&
            OfflineClusterBackup.Verify(output).Batch==combined.Batch,
            "offline bundle verifies hashes but never claims live ASA/Kova/SQL consistency");
        Check(File.ReadAllText(Path.Combine(h[0],"data","world-test.txt"))==
            "synthetic "+hpMaps[0] &&
            File.Exists(Path.Combine(sqlRoot,"arkshop","complete")),
            "offline coordinator retains original HP and SQL package data unchanged");
        File.AppendAllText(Path.Combine(output,"hp",hpIds[0].ToString("N"),
            "data","world-test.txt"),"TAMPERED");
        bool corruptRejected=false;
        try{OfflineClusterBackup.Verify(output);}
        catch(InvalidDataException){corruptRejected=true;}
        Check(corruptRejected,"verified offline bundle refuses any staged map byte tampering");
        bool missingHostRejected=false;
        try{OfflineClusterBackup.BuildFromFolders(hpRoot,hpRoot,sqlRoot,
            Path.Combine(root,"must-not-create-bundle"));}
        catch(InvalidDataException){missingHostRejected=true;}
        Check(missingHostRejected,"two-host backup refuses same host root twice");

        string audits=Path.Combine(root,"v16-kova-sanitized");
        Directory.CreateDirectory(audits);
        string hpFile=Path.Combine(audits,"hp-cluster-store.json");
        string ryzenFile=Path.Combine(audits,"ryzen-cluster-store.json");
        object hpExport=new{
            ClusterId="KovaCluster",Mode="shared",PeerCount=2,ShareReadable=true,
            LocalLockSessionMatches=true,LocalPathMatchesOperation=true,
            LocalClusterMatchesOperation=true,SelectedLocalProfilesMatch=true,
            LocalLockConfigIdMatches=true,LocalLockConfigPathMatches=true,
            LocalStoreMaps=hpMaps.Select((name,i)=>new{
                Profile=hpIds[i].ToString(),Map=name,
                Root=@"D:\offline\HP-"+i,Phase="active",
                Confirmed=true,SessionMatchesOperation=true}).ToArray(),
            Peers=new[]{new{Local=true,ReadOK=true,Phase="done",MemberCount=5,
                SelectedCount=5,SessionMatches=true},
                new{Local=false,ReadOK=true,Phase="done",MemberCount=5,
                SelectedCount=5,SessionMatches=true}},
            Markers=new[]{
                new{Name="operation.json",Exists=true},
                new{Name="running.json",Exists=false},
                new{Name="mutation.lock",Exists=false}
            }};
        object ryzenExport=new{
            ClusterID="KovaCluster",Mode="shared",
            ReadAccessOnlyInThisAudit=true,
            LocalConfigHashMatches=true,SessionAgreement=true,
            StoreMaps=ryzenMaps.Select((name,i)=>new{
                Profile=ryzenIds[i].ToString(),Map=name,
                Root=@"C:\offline\RYZEN-"+i,Phase="active",HasSharedSession=true}).ToArray(),
            PhysicalPeerChecks=new[]{
                new{Role="LOCAL",Phase="done",Members=5,Selected=5,
                    SessionMatches=true,ConfigHashMatches=true,PathMatches=true,
                    SelectedAreMembers=true},
                new{Role="PEER",Phase="done",Members=5,Selected=5,
                    SessionMatches=true,ConfigHashMatches=true,PathMatches=true,
                    SelectedAreMembers=true}
            },
            Markers=new{Operation=true,Running=false,MutationLock=false}
        };
        string originalHp=JsonSerializer.Serialize(hpExport);
        string originalRyzen=JsonSerializer.Serialize(ryzenExport);
        File.WriteAllText(hpFile,originalHp);
        File.WriteAllText(ryzenFile,originalRyzen);
        var hp=KovaAuditBridge.InspectCopy(hpFile,KovaAuditHost.Hp);
        var ryzen=KovaAuditBridge.InspectCopy(ryzenFile,KovaAuditHost.Ryzen);
        var pair=KovaAuditBridge.Evaluate(hp,ryzen);
        Check(pair.HistoricalSnapshotConsistent&&pair.MapNames.Length==10 &&
            File.ReadAllText(hpFile)==originalHp &&
            File.ReadAllText(ryzenFile)==originalRyzen,
            "original HP and Ryzen sanitized audit formats validate ten unique maps without source writes");
        Check(pair.SafetyStatement.Contains("NOT proven") &&
            hp.SourceHash.Length==64 &&ryzen.SourceHash.Length==64,
            "Kova historical snapshots never unlock live Store compatibility or ownership");
        using(var doc=JsonDocument.Parse(originalRyzen))
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(originalRyzen)!;
            node["Markers"]!["MutationLock"]=true;
            File.WriteAllText(ryzenFile,node.ToJsonString());
        }
        var bad=KovaAuditBridge.InspectCopy(ryzenFile,KovaAuditHost.Ryzen);
        Check(!bad.SnapshotInternallyConsistent &&
            !KovaAuditBridge.Evaluate(hp,bad).HistoricalSnapshotConsistent,
            "Kova active mutation lock blocks a two-host matching audit");
        bool switched=false;
        try{KovaAuditBridge.Evaluate(ryzen,hp);}
        catch(InvalidDataException){switched=true;}
        Check(switched,"Kova host role swap rejected");

        // The real subprocess is the harmless FakeAsa Windows binary and
        // only the integration test's own TEMP directory can be controlled.
        string life=Path.Combine(root,"v16-lifecycle-fixture");
        using(var session=new IsolatedFakeAsaSession(fakeAsa,life))
        {
            session.Start();
            var first=session.Check();
            Check(first.Running && first.Identity!=null,
                "actual isolated FakeAsa Windows process launched with exact PID/starttime identity");
            await SafeLifecycle.Stop(session,true,TimeSpan.FromSeconds(15));
            var restarted=session.Check();
            Check(restarted.Running && restarted.Identity!=null &&
                restarted.Identity.Pid!=first.Identity!.Pid,
                "real OS fixture SaveWorld ack + DoExit + verified stop + restart completes once");
            await SafeLifecycle.Stop(session,false,TimeSpan.FromSeconds(15));
            Check(session.Check().ConfirmedStopped,
                "real OS fixture SaveWorld + DoExit exits cleanly without forced kill");
        }
        using(var denied=new IsolatedFakeAsaSession(fakeAsa,
            Path.Combine(root,"v16-lifecycle-no-proof"),denySaveProof:true))
        {
            denied.Start();bool refused=false;
            try{await SafeLifecycle.Stop(denied,false,TimeSpan.FromSeconds(10));}
            catch(InvalidOperationException){refused=true;}
            Check(refused && denied.Check().Running,
                "missing SaveWorld proof blocks DoExit while real FakeAsa test process stays running");
        }
        bool realExecutableBlocked=false;
        try{using var rejected=new IsolatedFakeAsaSession(
            Path.Combine(root,"steamcmd.exe"),Path.Combine(root,"v16-invalid-lifecycle"));}
        catch(InvalidDataException){realExecutableBlocked=true;}
        Check(realExecutableBlocked,"isolated lifecycle refuses any binary except FakeAsa.exe");

        string digest=new string('a',64);
        string releaseJson=JsonSerializer.Serialize(new{
            tag_name="2.03",html_url="https://github.com/ArkServerApi/AsaApi/releases/tag/2.03",
            published_at="2026-07-29T10:30:00Z",draft=false,prerelease=false,
            assets=new[]{new{
                name="AsaApi-v2.03.zip",size=456789L,
                browser_download_url="https://github.com/ArkServerApi/AsaApi/releases/download/2.03/AsaApi-v2.03.zip",
                digest="sha256:"+digest}}
        });
        var upstream=AsaApiReleaseCatalog.Parse(releaseJson);
        Check(upstream.Tag=="2.03" &&upstream.Assets.Length==1 &&
            upstream.Assets[0].OfficialSha256==digest.ToUpperInvariant() &&
            !upstream.BinaryUpdateAuthorized,
            "official API release metadata and SHA parse without permitting auto-install");
        bool forgedAssetBlocked=false;
        try{AsaApiReleaseCatalog.Parse(releaseJson.Replace(
            "https://github.com/ArkServerApi/AsaApi/releases/download/",
            "https://example.org/forged/"));}
        catch(InvalidDataException){forgedAssetBlocked=true;}
        Check(forgedAssetBlocked,"ASA API release parser rejects third-party asset URLs");
    }
}
