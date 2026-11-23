using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using UntamedManager.Core;

// In-memory metadata planner is deliberately NOT connected to any live
// server, RCON, Store writer or UNC mutation. Temp files are only used to
// exercise the real read-only original Store parser against the staged bytes.
internal static class FutureStoreAppendPlannerTests
{
    private static void Check(bool value,string reason)
    {
        if(!value)throw new InvalidOperationException("FUTURE STORE APPEND: "+reason);
        Console.WriteLine("PASS future Store append: "+reason);
    }
    private static bool Denied(Action action)
    {
        try{action();return false;}
        catch(InvalidDataException){return true;}
    }

    public static void Run(string sandbox)
    {
        string root=Path.Combine(sandbox,"future-store-append-offline");
        Directory.CreateDirectory(root);
        string local=Path.Combine(root,"original-local-source");
        Directory.CreateDirectory(local);
        string[] ids=Enumerable.Range(0,5).Select(_=>Guid.NewGuid().ToString("D")).ToArray();
        string[] levels={"TheIsland_WP","Valguero_WP","Extinction_WP",
            "Genesis_WP","Astraeos_WP"};
        string session=Guid.NewGuid().ToString("D");
        string device=Guid.NewGuid().ToString("D");
        var members=ids.Select((id,i)=>new{
            id,map=levels[i],root=Path.Combine(root,"old-server-"+i)
        }).ToArray();
        var cluster=new{
            id=VaultProtocol.ClusterId,mode="shared",
            path=@"\\synthetic-not-real\UKClusterRoot\clusters\KovaCluster",
            members
        };
        var states=new Dictionary<string,object>();
        for(int i=0;i<5;i++)
            states.Add(ids[i],new{
                phase="active",sharedSession=session,
                root=members[i].root,map=levels[i],
                backupId=Guid.NewGuid().ToString("D")});
        string clusterText=JsonSerializer.Serialize(cluster);
        string storeText=JsonSerializer.Serialize(new{schema=1,maps=states});
        string hash=StoreV1Fingerprint.Compute(
            JsonSerializer.SerializeToElement(cluster));
        string lockText=JsonSerializer.Serialize(new{
            schema=1,session,selected=ids,configHash=hash,config=cluster});
        string launchText=JsonSerializer.Serialize(new{
            version=1,rows=ids.Select((id,i)=>new{
                id,root=members[i].root,launch=new{
                    map=levels[i],gamePort=7777+10*i,
                    queryPort=27015+10*i,
                    mods=new[]{"123456789"},passiveMods=new[]{"123456789"}
                }
            }).ToArray()
        });
        string peerText=JsonSerializer.Serialize(new{
            schema=1,id=device,session,phase="done",configHash=hash,
            config=cluster,members,selected=ids
        });
        Guid extraId=Guid.NewGuid(),backupId=Guid.NewGuid();
        string extraRoot=Path.Combine(root,"later-future-map");
        var now=DateTimeOffset.UtcNow;
        var witness=new FutureStoreConversionWitness(
            extraId,"FutureGen2_WP",extraRoot,backupId,
            now.AddMinutes(-3),now.AddMinutes(-2),now.AddMinutes(-1),
            new string('A',64),new string('B',64));
        FutureStoreAppendBundle Build(
            string? clusterRaw=null,string? storeRaw=null,string? lockRaw=null,
            string? launchRaw=null,string? peerRaw=null,string? sourceDevice=null,
            FutureStoreConversionWitness? evidence=null,int port=7897,int query=27197)=>
            FutureStoreAppendPlanner.Stage(clusterRaw??clusterText,storeRaw??storeText,
                lockRaw??lockText,launchRaw??launchText,peerRaw??peerText,
                sourceDevice??device,evidence??witness,port,query,
                new[]{"123456789"},new[]{"123456789"});

        var before=string.Join("\n",new[]{clusterText,storeText,lockText,
            launchText,peerText});
        var result=Build();
        Check(!result.StoreWriteAuthorized&&!result.ConversionExecuted&&
              !result.CharacterTravelCertified,
            "offline bundle never grants writer rights, conversion or character travel");
        using(var clusterDoc=JsonDocument.Parse(result.ClusterJson))
        using(var storeDoc=JsonDocument.Parse(result.StoreJson))
        using(var lockDoc=JsonDocument.Parse(result.LockJson))
        using(var launchDoc=JsonDocument.Parse(result.LaunchJson))
        using(var peerDoc=JsonDocument.Parse(result.LocalDonePeerJson))
        {
            var newMembers=clusterDoc.RootElement.GetProperty("members");
            var oldMembers=JsonDocument.Parse(clusterText).RootElement
                .GetProperty("members");
            Check(newMembers.GetArrayLength()==6&&
                newMembers.EnumerateArray().Take(5).Select(x=>x.GetRawText())
                  .SequenceEqual(oldMembers.EnumerateArray().Select(x=>x.GetRawText())),
                "appended sixth map preserves each original member, in exact order");
            Check(lockDoc.RootElement.GetProperty("selected").EnumerateArray()
                  .Select(x=>x.GetString()).SequenceEqual(ids)&&
                  peerDoc.RootElement.GetProperty("selected").EnumerateArray()
                  .Select(x=>x.GetString()).SequenceEqual(ids),
                "original five selected owners remain unchanged in lock and DONE peer");
            var record=storeDoc.RootElement.GetProperty("maps")
                .GetProperty(extraId.ToString("D"));
            Check(!record.TryGetProperty("sharedSession",out _)&&
                record.GetProperty("backupId").GetString()==backupId.ToString("D")&&
                launchDoc.RootElement.GetProperty("rows").GetArrayLength()==6,
                "new converted map has independent Store chronology and source launch facts");
            Check(StoreV1Fingerprint.Matches(clusterDoc.RootElement,
                lockDoc.RootElement.GetProperty("configHash").GetString()??"")&&
                StoreV1Fingerprint.Matches(peerDoc.RootElement.GetProperty("config"),
                peerDoc.RootElement.GetProperty("configHash").GetString()??""),
                "exact old Store fingerprint algorithm protects both new configs");
            _=LegacyStoreV1LiveReadGate.InspectOriginalDonePeer(
                peerDoc.RootElement,device,session,cluster.path);
        }

        // Read the staged four files with the unmodified native parser;
        // never copy them onto a real installation or coordination share.
        File.WriteAllText(Path.Combine(local,"cluster-v1.json"),result.ClusterJson);
        File.WriteAllText(Path.Combine(local,"cluster-store-v1.json"),result.StoreJson);
        File.WriteAllText(Path.Combine(local,"shared-store-lock-v1.json"),result.LockJson);
        File.WriteAllText(Path.Combine(local,"shared-store-device-v1.json"),
            JsonSerializer.Serialize(new{schema=1,id=device}));
        File.WriteAllText(Path.Combine(local,"launch-profiles-v1.json"),result.LaunchJson);
        var parsed=LegacyStoreV1LocalReader.Inspect(local);
        Check(parsed.LocalSnapshotReady&&parsed.Maps.Length==5&&
            parsed.AdditionalMaps.Length==1&&
            parsed.AdditionalMaps[0].MapId==extraId&&
            parsed.AdditionalMaps[0].SessionBinding=="missing-shared-session"&&
            parsed.AdditionalMaps[0].IndependentActivationMetadataComplete,
            "real Store reader recognizes independent sixth map without borrowing session");
        using(var expandedDoc=JsonDocument.Parse(result.ClusterJson))
        {
            var owners=parsed.Maps.Select(m=>new LocalMap{
                Id=m.MapId,Name=m.MapKey,
                HistoricalStoreProtected=true,
                HistoricalMapKey=m.MapKey,
                HistoricalArchiveSha256=parsed.OriginalFourFilesTag,
                InstallationPath=m.OriginalRoot
            }).ToArray();
            Check(DesktopClusterRegistryBridge.SelectedOriginalFivePreserved(
                    parsed,expandedDoc.RootElement,owners,root),
                "future-map inspector accepts expanded 6+ current roster while protecting original five");
            owners[0].InstallationPath=extraRoot;
            Check(!DesktopClusterRegistryBridge.SelectedOriginalFivePreserved(
                    parsed,expandedDoc.RootElement,owners,root),
                "future-map inspector refuses a changed original map installation path");
        }
        var current=LegacyStoreV1OriginalImport.Inspect(local);
        Check(current.Maps.Length==5&&current.AdditionalMaps.Length==1&&
            current.AdditionalMaps[0].LaunchState=="source-launch-matched",
            "real launch-source parser preserves original owners and verifies new map launch");
        Check(string.Join("\n",new[]{clusterText,storeText,lockText,
              launchText,peerText})==before&&
              result.OriginalRosterHash!=result.NewRosterHash,
            "the original inputs stay unchanged and new roster has distinct fingerprint");
        // The durable conversion journal is a LOCAL crash-safety guard,
        // not a license to execute an actual ASA conversion. A second
        // invocation must never repeat an uncertain one-shot conversion.
        string manager=Path.Combine(root,"manager-profile");
        Directory.CreateDirectory(manager);
        var entry=FutureStoreConversionJournal.Begin(manager,extraId,
            VaultProtocol.ClusterId,new string('C',64));
        Check(entry.Phase=="prepared"&&!entry.AutoRetryAllowed&&
            !entry.StoreWriterCertified,
            "prepared journal never grants automatic retry or Store writer authority");
        Check(FutureStoreConversionJournal.Inspect(manager,extraId)?.OperationId==
              entry.OperationId,
            "conversion intent survives a Manager process restart");
        bool duplicateBeginDenied=false;
        try{_=FutureStoreConversionJournal.Begin(manager,extraId,
                VaultProtocol.ClusterId,new string('C',64));}
        catch(InvalidOperationException){duplicateBeginDenied=true;}
        Check(duplicateBeginDenied,
            "interrupted or already-created conversion intent cannot be silently restarted");
        var backup=FutureStoreConversionJournal.Advance(manager,extraId,
            entry.OperationId,"prepared","backupVerified",new string('D',64));
        var attempted=FutureStoreConversionJournal.Advance(manager,extraId,
            entry.OperationId,"backupVerified","conversionAttempted");
        Check(backup.BackupManifestSha256==new string('D',64)&&
              attempted.Phase=="conversionAttempted"&&
              !attempted.AutoRetryAllowed,
            "one-shot conversion attempt is durably recorded before external Process.Start");
        bool replayDenied=false;
        try{_=FutureStoreConversionJournal.Advance(manager,extraId,
                entry.OperationId,"backupVerified","conversionAttempted");}
        catch(InvalidOperationException){replayDenied=true;}
        Check(replayDenied,"conversion command cannot be replayed after attempt");
        var uncertain=FutureStoreConversionJournal.Advance(manager,extraId,
            entry.OperationId,"conversionAttempted","needsOperator");
        Check(uncertain.Phase=="needsOperator"&&
              FutureStoreConversionJournal.Inspect(manager,extraId)?.Phase=="needsOperator",
            "uncertain Store conversion remains blocked durably for operator review");
        bool recoveredWithoutWitness=false;
        try{_=FutureStoreConversionJournal.Advance(manager,extraId,
                entry.OperationId,"needsOperator","conversionVerified",new string('F',64));}
        catch(InvalidOperationException){recoveredWithoutWitness=true;}
        Check(recoveredWithoutWitness,
            "uncertain conversion cannot jump directly to a claimed success");
        // A second independent candidate may finish the isolated state
        // machine; this proves journal transitions, not conversion itself.
        Guid another=Guid.NewGuid();
        var completedIntent=FutureStoreConversionJournal.Begin(manager,another,
            VaultProtocol.ClusterId,new string('1',64));
        var sequence=new[]{
            ("prepared","backupVerified",new string('2',64)),
            ("backupVerified","conversionAttempted",""),
            ("conversionAttempted","conversionVerified",new string('3',64)),
            ("conversionVerified","appendStaged",new string('4',64)),
            ("appendStaged","appendCommitted",new string('5',64))
        };
        foreach(var (beforePhase,afterPhase,sha) in sequence)
            FutureStoreConversionJournal.Advance(manager,another,
                completedIntent.OperationId,beforePhase,afterPhase,sha);
        var committed=FutureStoreConversionJournal.Inspect(manager,another);
        Check(committed?.Phase=="appendCommitted"&&
              committed.ResultingSourceSha256==new string('5',64)&&
              !committed.StoreWriterCertified,
            "stage receipts never self-certify actual ASA conversion or Store ownership");
        bool invalidTransitionDenied=false;
        try{_=FutureStoreConversionJournal.Advance(manager,another,
                completedIntent.OperationId,"appendCommitted","prepared");}
        catch(InvalidOperationException){invalidTransitionDenied=true;}
        Check(invalidTransitionDenied,
            "completed journal cannot revert to conversion intent");
        File.WriteAllText(Path.Combine(manager,"future-store-conversions",
            another.ToString("N")+".json.next"),"interrupted");
        bool interruptedDenied=false;
        try{_=FutureStoreConversionJournal.Inspect(manager,another);}
        catch(IOException){interruptedDenied=true;}
        Check(interruptedDenied,
            "crashed staged journal publication refuses to treat old bytes as current");

        Check(Denied(()=>Build(evidence:witness with{
                PreConversionBackupSha256="unknown"})),
            "missing verifiable pre-conversion backup evidence denied");
        Check(Denied(()=>Build(evidence:witness with{
                AttemptedUtc=witness.PreparedUtc.AddMinutes(-1)})),
            "forged conversion chronology denied");
        Check(Denied(()=>Build(evidence:witness with{
                MapId:Guid.Parse(ids[0])})),
            "original map GUID cannot be reused");
        Check(Denied(()=>Build(evidence:witness with{
                MapKey:levels[0]})),
            "original map level cannot silently be rebound");
        Check(Denied(()=>Build(port:7777)),
            "original ASA Game port collision denied");
        Check(Denied(()=>Build(query:27015)),
            "original ASA Query port collision denied");
        Check(Denied(()=>Build(sourceDevice:Guid.NewGuid().ToString("D"))),
            "foreign DONE peer device cannot issue local append bundle");
        Check(Denied(()=>Build(clusterRaw:clusterText.Replace(
                "KovaCluster","OtherCluster",StringComparison.Ordinal))),
            "changed cluster UNC identity denied");
        Check(Denied(()=>Build(peerRaw:peerText.Replace(
                "\"done\"","\"active\"",StringComparison.Ordinal))),
            "ACTIVE peer cannot masquerade as completed DONE peer");
        Check(Denied(()=>Build(lockRaw:lockText.Replace(
                "\"configHash\":\""+hash+"\"",
                "\"configHash\":\""+new string('0',64)+"\"",
                StringComparison.Ordinal))),
            "tampered original lock config hash denied");
    }
}
