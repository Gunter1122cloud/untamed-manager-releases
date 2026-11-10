import { test, expect } from "@playwright/test";

const first = "00000000-0000-4000-8000-000000000001";
const second = "00000000-0000-4000-8000-000000000002";

test.beforeEach(async ({ page }) => {
  await page.addInitScript(({ one, two }) => {
    type Handler = (e: { data: { id: string; ok: boolean; data?: unknown; error?: string } }) => void;
    const handlers: Handler[] = [];
    const state = {
      mode: "LIVE_READ_ONLY",
      observedUtc: "2026-11-05T12:00:00Z",
      local: {
        hostId: "laboratory-1",
        hostName: "LAB-WIN-01",
        cpuPercent: 22,
        memoryUsedKiB: 20_971_520,
        memoryTotalKiB: 67_108_864,
        maps: [
          { id: one, name: "The Island", running: true, status: "Fut", gamePort: 7777, steamBuildId: null, steamUpdateState: "Nem ellenőrizhető" },
          { id: two, name: "Astraeos", running: false, status: "Leállítva", gamePort: 7847, steamBuildId: null, steamUpdateState: "Nem ellenőrizhető" }
        ]
      },
      remote: {
        hostId: "laboratory-2",
        hostName: "LAB-WIN-02",
        observedUtc: "2026-11-05T12:00:00Z",
        maps: [{ id: "peer-1", name: "Ragnarok", running: true, status: "Fut", gamePort: 7827, steamBuildId: null, steamUpdateState: "Nem ellenőrizhető" }]
      },
      remotes: null as Array<any>|null,
      peerMessage: ""
    };
    (window as typeof window & {__UNTAMED_EMPTY_FIXTURE__?:()=>void})
      .__UNTAMED_EMPTY_FIXTURE__=()=>{
        state.local.maps=[];
        state.remote=null;
        state.peerMessage="Peer nincs beállítva";
      };
    let freshId = "";
    let initialBlocked=false;
    let customInitialLevel=false;
    (window as typeof window & {__UNTAMED_CUSTOM_INITIAL_LEVEL__?:(value:boolean)=>void})
      .__UNTAMED_CUSTOM_INITIAL_LEVEL__=(value:boolean)=>{customInitialLevel=value;};
    let nextRconStatusRefreshFails=false;
    let rconApplyCalls=0;
    (window as typeof window & {__UNTAMED_RCON_REFRESH_FAIL_ONCE__?:()=>void})
      .__UNTAMED_RCON_REFRESH_FAIL_ONCE__=()=>{nextRconStatusRefreshFails=true;};
    (window as typeof window & {__UNTAMED_RCON_APPLY_COUNT__?:()=>number})
      .__UNTAMED_RCON_APPLY_COUNT__=()=>rconApplyCalls;
    (window as typeof window & {__UNTAMED_INITIAL_BLOCKED__?:(value:boolean)=>void})
      .__UNTAMED_INITIAL_BLOCKED__=(value:boolean)=>{initialBlocked=value;};
    let keySaved = false;
    let stagedId = "";
    let redoId = "";
    let pluginEnabled = true;
    let pluginZipInstalled = false;
    let apiCoreRolledBack = false;
    let steamUpdateStarted = false;
    let steamBatchApplyCount=0;
    let steamBatchStarted=false;
    (window as typeof window & {__UNTAMED_STEAM_BATCH_APPLIES__?:()=>number})
      .__UNTAMED_STEAM_BATCH_APPLIES__=()=>steamBatchApplyCount;
    let freshStarted=false;
    let restartInterrupted=false;
    (window as typeof window & {__UNTAMED_RESTART_INTERRUPTED__?:()=>void})
      .__UNTAMED_RESTART_INTERRUPTED__=()=>{restartInterrupted=true;};
    let lifecycleInterrupted=false;
    (window as typeof window & {__UNTAMED_LIFECYCLE_INTERRUPTED__?:()=>void})
      .__UNTAMED_LIFECYCLE_INTERRUPTED__=()=>{lifecycleInterrupted=true;};
    let steamInterrupted=false;
    (window as typeof window & {__UNTAMED_STEAM_INTERRUPTED__?:()=>void})
      .__UNTAMED_STEAM_INTERRUPTED__=()=>{steamInterrupted=true;};
    let localBackupMap=one;
    let localBackupSaved=false;
    let crashRecovery=false;
    (window as typeof window & {__UNTAMED_CRASH_MARKER_TEST__?:()=>void})
      .__UNTAMED_CRASH_MARKER_TEST__=()=>{
        crashRecovery=true;localBackupMap=two;localBackupSaved=true;
      };
    (window as typeof window & {__UNTAMED_PROTECT_TEST_MAP__?:()=>void})
      .__UNTAMED_PROTECT_TEST_MAP__=()=>{
        Object.assign(state.local.maps[1],{storeProtected:true,
          storeLifecycleState:"preflightRequired"});
        localBackupMap=two;localBackupSaved=true;
      };
    let originalFiveBackupCopies=0;
    (window as typeof window & {__UNTAMED_FIVE_BACKUP_FIXTURE__?:()=>void;
      __UNTAMED_FIVE_BACKUP_COPIES__?:()=>number})
      .__UNTAMED_FIVE_BACKUP_FIXTURE__=()=>{
        Object.assign(state.local.maps[0],{running:false,status:"Leállítva",
          storeProtected:true});
        Object.assign(state.local.maps[1],{running:false,status:"Leállítva",
          storeProtected:true});
        for(let index=0;index<3;index++)
          state.local.maps.push({
            id:`00000000-0000-4000-8000-${String(index+21).padStart(12,"0")}`,
            name:["The Center","Scorched Earth","Extinction"][index],
            running:false,status:"Leállítva",gamePort:7817+index*20,
            storeProtected:true,steamBuildId:null,
            steamUpdateState:"Nem ellenőrizhető"
          } as typeof state.local.maps[number]);
        state.local.maps.push({
          id:"00000000-0000-4000-8000-000000000029",
          name:"Independent Sixth Store Map",running:true,status:"Fut",
          gamePort:7957,storeProtected:true,steamBuildId:null,
          steamUpdateState:"Nem ellenőrizhető"
        } as typeof state.local.maps[number]);
      };
    (window as typeof window & {__UNTAMED_FIVE_BACKUP_COPIES__?:()=>number})
      .__UNTAMED_FIVE_BACKUP_COPIES__=()=>originalFiveBackupCopies;
    let historicalRconAttached=false;
    const historicalRconToken="00000000-0000-4000-8000-000000001699";
    const historicalStartToken="00000000-0000-4000-8000-000000001698";
    let historicalActiveStarted=false;
    (window as typeof window & {__UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void})
      .__UNTAMED_ORIGINAL_RCON_TEST_READY__=()=>{
        Object.assign(state.local.maps[1],{storeProtected:true,rconConfigured:true,
          running:true,status:"Fut",storeLifecycleState:"running"});
        historicalRconAttached=true;historicalActiveStarted=true;
      };
    (window as typeof window & {__UNTAMED_STORE_OPERATOR_REVIEW__?:()=>void})
      .__UNTAMED_STORE_OPERATOR_REVIEW__=()=>{
        Object.assign(state.local.maps[1],{storeProtected:true,
          storeLifecycleState:"needsOperator"});
      };
    let originalRconSourceValid=true;
    let originalRconPortOwner=true;
    const originalStopToken="00000000-0000-4000-8000-000000001696";
    const originalStopJob="00000000-0000-4000-8000-000000001697";
    const originalRestartToken="00000000-0000-4000-8000-000000001688";
    const originalRestartJob="00000000-0000-4000-8000-000000001689";
    let originalRestartApproved=false;
    let originalRestartQueued=false;
    let originalRestartFail=false;
    let originalRestartSends=0;
    (window as typeof window & {
      __UNTAMED_RESTART_FAIL__?:()=>void;
      __UNTAMED_RESTART_SENDS__?:()=>number;
    }).__UNTAMED_RESTART_FAIL__=()=>{originalRestartFail=true;};
    (window as typeof window & {
      __UNTAMED_RESTART_SENDS__?:()=>number;
    }).__UNTAMED_RESTART_SENDS__=()=>originalRestartSends;
    let originalStopQueued=false;
    const originalSaveToken="00000000-0000-4000-8000-000000001692";
    const originalSaveJob="00000000-0000-4000-8000-000000001693";
    let originalSaveQueued=false;
    let originalSavePending=false;
    let originalSaveDispatches=0;
    let originalSaveReattached=false;
    let originalSaveHold=false;
    let originalSaveEvidenceTampered=false;
    let originalSaveRecoveryFixture=false;
    let originalSaveReviewed=false;
    (window as typeof window & {__UNTAMED_SAVE_REVIEW_FIXTURE__?:()=>void})
      .__UNTAMED_SAVE_REVIEW_FIXTURE__=()=>{
        originalSaveRecoveryFixture=true;
        Object.assign(state.local.maps[1],{storeProtected:true,
          running:false,status:"Leállítva",storeLifecycleState:"needsOperator"});
      };
    (window as typeof window & {
      __UNTAMED_SAVE_EVIDENCE_TAMPER__?:()=>void;
    }).__UNTAMED_SAVE_EVIDENCE_TAMPER__=()=>{
      originalSaveEvidenceTampered=true;
    };
    (window as typeof window & {
      __UNTAMED_ORIGINAL_SAVE_REATTACH__?:()=>void;
      __UNTAMED_ORIGINAL_SAVE_DISPATCHES__?:()=>number;
    }).__UNTAMED_ORIGINAL_SAVE_REATTACH__=()=>{
      originalSaveQueued=true;
      originalSaveReattached=true;
      originalSaveHold=true;
      originalSaveDispatches=1; // a native task was already dispatched in another UI mount
    };
    (window as typeof window & {
      __UNTAMED_ORIGINAL_SAVE_DISPATCHES__?:()=>number;
    }).__UNTAMED_ORIGINAL_SAVE_DISPATCHES__=()=>originalSaveDispatches;
    (window as typeof window & {
      __UNTAMED_ORIGINAL_SAVE_RELEASE__?:()=>void;
    }).__UNTAMED_ORIGINAL_SAVE_RELEASE__=()=>{originalSaveHold=false;};
    const historicalAdminToken="00000000-0000-4000-8000-000000001695";
    let historicalAdminPending=false;
    let historicalAdminCommand="";
    let oldStopNeedsOperator=false;
    (window as typeof window & {
      __UNTAMED_ORIGINAL_STOP_CRASH_RECOVERY__?:()=>void
    }).__UNTAMED_ORIGINAL_STOP_CRASH_RECOVERY__=()=>{
      oldStopNeedsOperator=true;
    };
    let lanListen=0;
    let localPhysicalProbeRequested=false;
    const lanPeers:{hostId:string;hostName:string;address:string;port:number;keyConfigured:boolean}[]=[];
    let lanApproval="";
    let lanPending:{action:"upsert"|"remove";listenPort:number;hostId:string;hostName:string;address:string;port:number}|null=null;
    let clusterRevision=1;
    let clusterClusters=[
      {clusterId:"fixture-alpha",name:"Fixture Alpha",transferDirectory:"C:\\Synthetic\\Alpha\\Transfer",backupDirectory:"C:\\Synthetic\\Alpha\\Backup",createdUtc:"2026-11-08T00:00:00Z",updatedUtc:"2026-11-08T00:00:00Z"},
      {clusterId:"fixture-beta",name:"Fixture Beta",transferDirectory:"C:\\Synthetic\\Beta\\Transfer",backupDirectory:"C:\\Synthetic\\Beta\\Backup",createdUtc:"2026-11-08T00:00:00Z",updatedUtc:"2026-11-08T00:00:00Z"},
      {clusterId:"fixture-gamma",name:"Fixture Gamma",transferDirectory:"C:\\Synthetic\\Gamma\\Transfer",backupDirectory:"C:\\Synthetic\\Gamma\\Backup",createdUtc:"2026-11-08T00:00:00Z",updatedUtc:"2026-11-08T00:00:00Z"}
    ];
    let clusterAssignments:Array<{mapId:string;clusterId:string}>=[
      {mapId:one,clusterId:"fixture-alpha"},{mapId:two,clusterId:"fixture-beta"},
      {mapId:"peer-1",clusterId:"fixture-gamma"}
    ];
    let freshStoreEnrolled=false;
    let freshStoreApprovalIssued=false;
    let freshStoreLoaderEnabled=false;
    let freshStoreLoaderApprovalIssued=false;
    const freshStoreLoaderToken="00000000-0000-4000-8000-000000009911";
    const freshStoreToken="00000000-0000-4000-8000-000000009891";
    let clusterApproval="";
    let clusterPending:{clusterId:string;mapIds:string[];preview:any}|null=null;
    let sourceKovaClusterToken="";
    let sourceKovaClusterAssigned=false;
    let sourceKovaClusterApplies=0;
    (window as typeof window & {
      __UNTAMED_KOVA_REGISTRY_APPLIES__?:()=>number
    }).__UNTAMED_KOVA_REGISTRY_APPLIES__=()=>sourceKovaClusterApplies;
    const peerRows=()=>Array.isArray(state.remotes)?state.remotes:(state.remote?[state.remote]:[]);
    const allFixtureMaps=()=>[
      ...state.local.maps.map(map=>({map,hostId:state.local.hostId,hostName:state.local.hostName,local:true})),
      ...peerRows().flatMap((peer:any)=>peer.maps.map((map:any)=>({map,hostId:peer.hostId,hostName:peer.hostName,local:false})))
    ];
    const clusterRegistrySnapshot=()=>({schemaVersion:1,revision:clusterRevision,
      hostRegistry:{schemaVersion:1,localHostId:state.local.hostId,hosts:[
        {hostId:state.local.hostId,hostName:state.local.hostName,isLocal:true,isPaired:true,registeredUtc:"2026-11-08T00:00:00Z"},
        ...peerRows().map((peer:any)=>({hostId:peer.hostId,hostName:peer.hostName,isLocal:false,
          isPaired:peer.fresh!==false,registeredUtc:"2026-11-08T00:00:00Z"}))
      ],maps:allFixtureMaps().map(({map,hostId,local}:any)=>({mapId:map.id,hostId,mapName:map.name,
        localConfigurationPresent:local,registeredUtc:"2026-11-08T00:00:00Z"}))},
      clusterRegistry:{schemaVersion:1,revision:clusterRevision,clusters:clusterClusters,
        mapAssignments:clusterAssignments}});
    const applyLargeHostFixture=()=>{
      const makeId=(n:number)=>`00000000-0000-4000-8000-${String(n).padStart(12,"0")}`;
      const makeMap=(id:string,name:string,index:number,protectedStore=false)=>({id,name,
        running:index%2===0,status:index%2===0?"Fut":"Leállítva",gamePort:7700+(index%200),
        steamBuildId:null,steamUpdateState:"Nem ellenőrizhető",...(protectedStore?{storeProtected:true}:{})});
      state.local.hostId="fixture-host-1";state.local.hostName="LAB-NODE-1";
      state.local.maps=Array.from({length:26},(_,i)=>makeMap(makeId(i+1),
        i===0?"The Island":`Local Map ${i+1}`,i,i===0));
      state.remotes=Array.from({length:3},(_,hostIndex)=>({
        hostId:`fixture-host-${hostIndex+2}`,hostName:`LAB-NODE-${hostIndex+2}`,
        observedUtc:hostIndex===2?null:"2026-11-08T00:00:00Z",
        lastSuccessUtc:hostIndex===2?"2026-11-07T23:30:00Z":"2026-11-08T00:00:00Z",
        fresh:hostIndex!==2,stateCode:hostIndex===2?"UM-PEER-UNAVAILABLE":"OK",
        maps:Array.from({length:26},(_,i)=>makeMap(makeId(27+hostIndex*26+i),
          `Node ${hostIndex+2} Map ${i+1}`,i))
      }));
      state.remote=state.remotes[0];
      clusterClusters=["alpha","beta","gamma"].map(name=>({clusterId:`fixture-${name}`,
        name:`Fixture ${name[0].toUpperCase()}${name.slice(1)}`,
        transferDirectory:`C:\\Synthetic\\${name}\\Transfer`,backupDirectory:`C:\\Synthetic\\${name}\\Backup`,
        createdUtc:"2026-11-08T00:00:00Z",updatedUtc:"2026-11-08T00:00:00Z"}));
      clusterAssignments=allFixtureMaps().map((entry:any,index:number)=>({
        mapId:entry.map.id,clusterId:clusterClusters[index%clusterClusters.length].clusterId}));
      clusterRevision=1;
    };
    const fixtureMode=new URLSearchParams(window.location.search).get("fixture");
    if(fixtureMode==="large"||fixtureMode==="modLarge")applyLargeHostFixture();
    if(fixtureMode==="modLarge"){
      Object.assign(state.local.maps[0],{storeProtected:false});
      for(let i=27;i<=65;i++)
        state.local.maps.push({
          id:`00000000-0000-4000-8000-${String(i+400).padStart(12,"0")}`,
          name:`Local Map ${i}`,running:false,status:"Leállítva",
          gamePort:8000+i,storeProtected:false,steamBuildId:null,
          steamUpdateState:"Nem ellenőrizhető"
        } as typeof state.local.maps[number]);
    }
    let rconAllowed=false;
    (window as typeof window & {__UNTAMED_RCON_ENABLE__?:()=>void})
      .__UNTAMED_RCON_ENABLE__=()=>{rconAllowed=true;};
    const pluginJsonToken="00000000-0000-4000-8000-000000001262";
    let pluginJsonModified=false;
    const restoreLabToken="00000000-0000-4000-8000-000000001263";
    const restoreLabJob="00000000-0000-4000-8000-000000001264";
    let restoreLabApproved=false;
    const offlineArchiveToken="00000000-0000-4000-8000-000000001260";
    const offlineArchiveJob="00000000-0000-4000-8000-000000001261";
    let offlineArchiveApproved=false;
    const portableProfileToken="00000000-0000-4000-8000-000000001265";
    let portableProfileApplied=false;
    const modCopyToken="00000000-0000-4000-8000-000000001258";
    let modCopyApproved=false;
    const draftToken = "00000000-0000-4000-8000-000000001234";
    const draftState = () => ({
      draftId:draftToken,mapId:one,active:stagedId?["123456",stagedId]:["123456"],
      passive:["789012"],disabled:[],pending:!!stagedId,dirty:!!stagedId,
      canUndo:!!stagedId,canRedo:!!redoId,evidence:"IN_MEMORY_MOD_DRAFT",
      differences:stagedId?[{modId:stagedId,beforeMode:null,beforePosition:null,afterMode:"active",afterPosition:2}]:[]
    });
    const iniToken="00000000-0000-4000-8000-000000003333";
    const nativeSchedules:Record<string,any[]>={};
    const opsJobId="00000000-0000-4000-8000-000000009988";
    let opsJobCreated=false;
    let opsJobEnabled=false;
    const answers = (m: { method: string; mapId?: string; term?: string; page?: number; credential?: string; draftId?: string; modId?: string | number; mode?: string; file?: string; token?: string; content?: string; name?: string; root?: string; importToken?: string; categoryId?: number; sort?: number; descending?: boolean; mapName?: string; sessionName?: string; gamePort?: number; queryPort?: number; archive?: string; command?: string; coordinationArchive?: string; localMetadataArchive?: string; plugin?: string; enabled?: boolean; confirm?: boolean; frequency?:string;minuteOfDay?:number;daysMask?:number; channel?: string; transactionId?:string; point?: string; action?: string; message?: string; typedApproval?: string; confirmation?:string; listenPort?:number; peerAddress?:string; peerPort?:number; pairingKey?:string; hostId?:string;hostName?:string;peerAction?:string;kind?:string;intervalMinutes?:number;maintenance?:boolean;jobId?:string;clusterId?:string;mapIds?:string[];transferDirectory?:string;backupDirectory?:string }) => {
      switch (m.method) {
        case "store.inspectArchive": return {
          batch:"b8d0c49f-cc87-42e0-b312-a0485c82f101",
          maps:10,indexScannedMaps:9,needsReviewMaps:1,
          archiveFilesSha256Verified:true,liveConsistencyCertified:false,
          productionRestoreAllowed:false,characterTravelVerified:false,
          evidence:"OFFLINE_TEN_MAP_WORLD_STORE_INDEX_AUDIT_ONLY",
          results:Array.from({length:10},(_,i)=>({
            host:i<5?"HP":"RYZEN",
            mapId:"00000000-0000-4000-8000-"+String(i+1).padStart(12,"0"),
            package:"synthetic-map-"+i,
            status:i===9?"EMBEDDED_INDEX_NOT_READABLE":"INDEX_SCANNED",
            profileCandidates:i===9?0:2,tribeCandidates:i===9?0:1,
            unclassifiedCandidates:0,supersededEntries:0,
            indexBoundsVerified:i!==9,gameplayVerified:false,
            characterTravelVerified:false
          }))
        };
        case "store.compareArchives": return {
          beforeBatch:"b8d0c49f-cc87-42e0-b312-a0485c82f101",
          afterBatch:"b8d0c49f-cc87-42e0-b312-a0485c82f102",
          maps:10,transferFilesBefore:2,transferFilesAfter:3,
          transferAdded:1,transferRemoved:0,transferModified:0,
          zeroByteTransferFilesPreserved:1,configurationAdded:0,
          configurationRemoved:0,configurationModified:0,
          configurationCoverageChanged:false,anyChanges:true,
          bothArchivesSha256Verified:true,liveConsistencyCertified:false,
          storeWriterCertified:false,characterTravelVerified:false,
          productionRestoreAllowed:false,
          evidence:"TWO_SEALED_OFFLINE_ARCHIVE_FILE_DIFFERENCES_ONLY",
          results:Array.from({length:10},(_,i)=>({
            host:i<5?"HP":"RYZEN",
            mapId:"00000000-0000-4000-8000-"+String(i+1).padStart(12,"0"),
            name:"synthetic-map-"+i,filesAdded:0,filesRemoved:0,
            filesModified:0,worldChanged:false,
            worldFilesBefore:1,worldFilesAfter:1
          }))
        };
        case "store.inspectCopy": return {
          fileName:"TheIsland_WP.ark",state:"INDEX_SCANNED",
          format:1,storeBytes:412,sections:2,profileCandidates:2,
          tribeCandidates:1,unclassifiedCandidates:0,supersededEntries:1,
          duplicateKeys:0,indexBoundsVerified:true,sourceUnchanged:true,
          gameplayVerified:false,characterTravelVerified:false,
          evidence:"OFFLINE_SQLITE_EMBEDDED_STORE_INDEX_ONLY"
        };
        case "travel.capture": {
          const names=["Astraeos","Genesis Part 1","Valguero","The Island","Extinction"];
          const list=names.map((name,i)=>({
            mapId:i===3?one:"00000000-0000-4000-8000-00000000001"+i,
            name,confirmedStopped:true,saveDirectoryFound:true,
            ambiguousLayout:false,profiles:i===3?1:0,tribes:0,
            zeroByteDetected:false,unstable:false,
            files:i===3?[{id:"a".repeat(64),kind:"profile",bytes:1240,
              sha256:"b".repeat(64)}]:[]
          }));
          return {
            hostId:"00000000-0000-4000-8000-000000008881",
            snapshot:JSON.stringify({
              schema:1,hostId:"00000000-0000-4000-8000-000000008881",
              utc:"2026-11-06T07:00:00Z",
              nonce:"00000000000040008000000000008889",maps:list
            }),
            maps:list.map(x=>({
              mapId:x.mapId,mapName:x.name,confirmedStopped:true,
              saveDirectoryFound:true,ambiguousLayout:false,
              profileCount:x.profiles,tribeCount:x.tribes,
              zeroByteDetected:false,unstable:false,
              profileFiles:x.files.filter(y=>y.kind==="profile").map(y=>({id:y.id,bytes:y.bytes}))
            })),
            complete:true,readOnly:true,writerCertified:false,
            evidence:"LOCAL_QUIESCENT_FILE_HASH_SNAPSHOT_ONLY"
          };
        }
        case "travel.compare": return {
          passed:true,maps:10,hosts:2,beforeOnSource:true,
          afterOnTarget:true,uniqueDestination:true,zeroByteFiles:0,
          findings:[],liveStoreWriterCertified:false,asaSaveCompleteCertified:false,
          productionUnlockAllowed:false,
          evidence:"FOUR_QUIESCENT_HOST_FILE_HASH_SNAPSHOTS_ONLY"
        };
        case "cluster.multiHostBackupRecoveryPreview": return {
          token:"00000000-0000-4000-8000-000000009621",
          batchId:"00000000-0000-4000-8000-000000009622",
          archiveName:"Untamed-MultiHost-20261109-fixture",
          verifiedFiles:64,components:151,needsOperator:true,
          requiresExplicitApproval:true,previousStageWillBeRetained:true,
          automaticResume:false,sourceFilesWillBeRevalidated:true,
          originalStoreTouched:false,productionRestoreAllowed:false,
          evidence:"VERIFIED_INTERRUPTED_MULTIHOST_OFFLINE_ARCHIVE_JOURNAL"
        };
        case "cluster.multiHostBackupRecoveryApply":
          if(!m.confirm||m.token!=="00000000-0000-4000-8000-000000009621")
            throw new Error("UM-MULTIHOST-RECOVERY-APPROVAL");
          return {
            jobId:"00000000-0000-4000-8000-000000009623",
            started:true,previousStageWillBeRetained:true,
            productionRestoreAllowed:false,rconUsed:false,
            serverProcessTouched:false,
            evidence:"OPERATOR_APPROVED_EXISTING_MULTIHOST_ARCHIVE_REBUILD"
          };
        case "cluster.multiHostBackupStatus": return {
          jobId:m.jobId,kind:"backup",state:"completed",
          stage:"ARCHIVE_SHA256_VERIFIED_AFTER_RECOVERY",
          artifactName:"Untamed-MultiHost-20261109-fixture",
          rollbackArtifactName:"Untamed-MultiHost-20261109-fixture.retained-abc",
          hosts:2,clusters:1,maps:10,components:151,bytes:123456789,
          changedUtc:"2026-11-09T19:50:00Z",
          productionRestoreAllowed:false,liveConsistencyCertified:false,
          storeWriterCertified:false,sqlUsed:false,rconUsed:false,
          serverProcessTouched:false,evidence:"OFFLINE_MULTI_HOST_COPY_SHA256_VERIFIED"
        };
        case "cluster.multiHostHostStagePreview": return {
          token:"00000000-0000-4000-8000-000000009631",
          hostId:"laboratory-1",batchId:"00000000-0000-4000-8000-000000009632",
          maps:5,clusters:1,components:61,bytes:123456789,
          stageDirectoryName:"Untamed-HostRestore-laboratory-1-fixture",
          requiresExplicitApproval:true,sourceSandboxVerified:true,
          allSharedClusterFilesIncluded:true,
          originalStoreTouched:false,productionRestoreAllowed:false,
          remoteHostWritten:false,liveCutoverCertified:false,
          evidence:"EXACT_LOCAL_HOST_AND_SHARED_CLUSTER_SANDBOX_SHA256_PREFLIGHT"
        };
        case "cluster.multiHostHostStageApply":
          if(!m.confirm||m.typedApproval!=="HOST RESTORE TEST"||
             m.token!=="00000000-0000-4000-8000-000000009631")
            throw new Error("UM-HOSTRESTORE-TYPED-APPROVAL-REQUIRED");
          return {jobId:"00000000-0000-4000-8000-000000009633",
            started:true,hostId:"laboratory-1",originalStoreTouched:false,
            remoteHostWritten:false,productionRestoreAllowed:false,
            evidence:"APPROVED_LOCAL_HOST_OFFLINE_RESTORE_STAGE_STARTED"
          };
        case "cluster.multiHostHostStageStatus": return {
          jobId:m.jobId,kind:"hoststage",state:"completed",
          stage:"LOCAL_HOST_STAGE_SHA256_VERIFIED",
          artifactName:"Untamed-HostRestore-laboratory-1-fixture",
          rollbackArtifactName:"",hosts:1,clusters:1,maps:5,components:61,
          bytes:123456789,changedUtc:"2026-11-09T20:00:00Z",
          productionRestoreAllowed:false,liveConsistencyCertified:false,
          storeWriterCertified:false,sqlUsed:false,rconUsed:false,
          serverProcessTouched:false,
          evidence:"LOCAL_HOST_FULL_MAP_STORE_TRANSFER_STAGE_SHA256_VERIFIED_NO_LIVE_CUTOVER"
        };
        case "cluster.multiHostCatalogPreview": return {
          token:"00000000-0000-4000-8000-000000009611",
          hosts:2,clusters:1,maps:10,components:151,bytes:123456789,
          copiedTransferIncluded:true,copiedProfilesIncluded:true,
          copiedTribesIncluded:true,requiresExplicitApproval:true,
          offlineCopiesOnly:true,liveConsistencyCertified:false,
          storeWriterCertified:false,productionRestoreAllowed:false,
          originalStoreTouched:false,
          evidence:"CURRENT_NHOST_REGISTRY_AND_COMPLETE_OFFLINE_SOURCE_INVENTORY"
        };
        case "cluster.multiHostCatalogApply":
          if(!m.confirm||m.token!=="00000000-0000-4000-8000-000000009611")
            throw new Error("UM-MULTIHOST-AUTOCATALOG-APPROVAL");
          return {
            created:true,batchId:"00000000-0000-4000-8000-000000009612",
            hosts:2,clusters:1,maps:10,components:151,bytes:123456789,
            catalogName:"multi-host-backup-catalog.json",offlineCopiesOnly:true,
            originalStoreTouched:false,productionRestoreAllowed:false,
            evidence:"EXISTING_MULTIHOST_ARCHIVE_V1_VALIDATED_CATALOG_CREATED"
          };
        case "cluster.multiHostRestoreReplacePreview": return {
          token:"00000000-0000-4000-8000-000000009601",
          batchId:"00000000-0000-4000-8000-000000009602",
          archiveName:"synthetic-archive",
          hosts:2,clusters:1,maps:10,components:78,
          previousBatchId:"00000000-0000-4000-8000-000000009603",
          targetDirectoryName:"Untamed-MultiHost-Restore-fixture",
          requiresExplicitApproval:true,
          restoreTarget:"EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK",
          productionRestoreAllowed:false,liveConsistencyCertified:false,
          storeWriterCertified:false,evidence:"SYNTHETIC_TEMP_SANDBOX_ONLY"
        };
        case "cluster.multiHostRestoreReplaceApply": return {
          jobId:"00000000-0000-4000-8000-000000009604",started:true,
          target:"EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK",
          productionRestoreAllowed:false,rconUsed:false,serverProcessTouched:false,
          evidence:"SYNTHETIC_TEMP_LEASE_CONTENTION"
        };
        case "cluster.multiHostRestoreStatus": return {
          jobId:m.jobId,kind:"restore",state:"needsOperator",
          stage:"TEMP_SANDBOX_CONCURRENT_MANAGER_NO_WRITES",
          artifactName:"",rollbackArtifactName:"",
          hosts:2,clusters:1,maps:10,components:78,bytes:0,
          changedUtc:"2026-11-09T10:00:00Z",
          productionRestoreAllowed:false,liveConsistencyCertified:false,
          storeWriterCertified:false,sqlUsed:false,rconUsed:false,
          serverProcessTouched:false,evidence:"OS_HANDLE_LEASE_COLLISION_NO_WRITE"
        };
        case "travel.compareRoundTrip": return {
          passed:false,maps:10,hosts:2,
          outwardPlacementVerified:typeof m.middleA==="string"&&
            typeof m.middleB==="string"&&m.sourceMap!==m.destinationMap,
          returnPlacementVerified:typeof m.afterA==="string"&&typeof m.afterB==="string",
          uniqueAtAllStages:true,exactOriginalProfileBytes:false,
          unrelatedLooseFilesUnchanged:true,storeModeDetected:false,
          embeddedSurvivorVerified:false,gameClientRejoinVerified:false,
          liveStoreWriterCertified:false,asaSaveCompleteCertified:false,
          productionUnlockAllowed:false,
          evidence:"SIX_QUIESCENT_TWO_HOST_LOOSE_FILE_BYTE_PARITY_ONLY"
        };
        case "rcon.status": return {
          mapId:m.mapId,mapName:"The Island",configured:rconAllowed,
          processVerified:true,permitted:rconAllowed,maintenance:false,
          allowedCommands:["ListPlayers","GetGameLog","GetChat","SaveWorld"],
          evidence:"LOCAL_RCON_READ_ONLY_PREFLIGHT_NO_COMMAND"
        };
        case "rcon.adminPreview": return {
          token:"00000000-0000-4000-8000-00000000e001",
          mapId:m.mapId,mapName:"The Island",action:m.action,
          command:m.action==="wildWipe"?"DestroyWildDinos":
            m.action==="broadcast"?"Broadcast "+m.message:
            m.action==="serverChat"?"ServerChat "+m.message:m.message,
          dangerous:m.action==="wildWipe"||m.action==="custom",
          approvalWord:m.action==="wildWipe"?"DINÓTÖRLÉS":
            m.action==="custom"?"ADMIN":null,
          requiresApproval:true,expiresUtc:"2026-11-06T10:00:00Z",
          dispatched:false,credentialExposed:false,
          evidence:"LOCAL_OS_VERIFIED_ADMIN_RCON_PREVIEW"
        };
        case "rcon.adminSend": return {
          mapId:one,mapName:"The Island",action:"broadcast",
          command:"Broadcast Synthetic operator message",
          response:"Synthetic RCON command accepted",
          commandSent:true,observedUtc:"2026-11-06T09:00:00Z",
          oneShot:true,saveCompleteProven:false,storeWriterTouched:false,
          evidence:"LIVE_LOCAL_ADMIN_RCON_SINGLE_SEND"
        };
        case "rcon.gameLog":
          if(!rconAllowed)throw new Error("RCON requires owned local process");
          return {
            mapId:one,mapName:"The Island",command:"GetGameLog",
            response:"Synthetic ASA GameLog entry",observedUtc:"2026-11-06T09:00:00Z",
            readOnly:true,saveCompleteProven:false,
            evidence:"LOCAL_VERIFIED_READ_ONLY_RCON"
          };
        case "rcon.chatLog":
          if(!rconAllowed)throw new Error("RCON requires owned local process");
          return {
            mapId:one,mapName:"The Island",command:"GetChat",
            response:"Synthetic ASA chat entry",observedUtc:"2026-11-06T09:00:00Z",
            readOnly:true,saveCompleteProven:false,
            evidence:"LOCAL_VERIFIED_READ_ONLY_RCON"
          };
        case "rcon.listPlayers":
          throw new Error("Only verified local process/port can query players");
        case "backup.history": return {
          mapId:m.mapId,
          mapName:m.mapId===two?"Astraeos":"The Island",
          recoveryRequired:crashRecovery,
          points:localBackupSaved&&m.mapId===localBackupMap?[{
            id:"00000000-0000-4000-8000-00000000b002",
            createdUtc:"2026-11-06T08:12:00Z",kind:"manual",
            files:4,bytes:12799,characters:1,tribes:1,verified:true
          }]:[],
          source:"LOCAL_SAVEDARKS_MAP_FOLDER",
          storeWriterTouched:false,evidence:"REAL_SHA256_BACKUP_PACKAGES"
        };
        case "backup.originalBatchPreview":
          if(state.local.maps.filter((x:any)=>x.storeProtected&&!x.running).length!==5)
            throw new Error("UM-BACKUP-ORIGINAL-FIVE-STOPPED-REQUIRED");
          return {token:"00000000-0000-4000-8000-00000000b051",
            localMaps:5,worldBytes:5*1024*1024*1024,
            mapNames:["The Island","Astraeos","The Center",
              "Scorched Earth","Extinction"],
            originalSourcePinned:true,allMapsConfirmedStopped:true,
            requiresOperatorConfirmation:true,
            originalKovaFilesWritten:false,serverStarted:false,
            sharedTransferIncluded:false,otherHostIncluded:false,
            evidence:"EXISTING_SHA256_WORLD_ENGINE_ORIGINAL_FIVE_MAP_PREFLIGHT"
          };
        case "backup.originalBatchCreate":
          if(!m.confirm||m.token!=="00000000-0000-4000-8000-00000000b051")
            throw new Error("UM-BACKUP-ORIGINAL-FIVE-APPROVAL-REQUIRED");
          originalFiveBackupCopies++;
          return {jobId:"00000000-0000-4000-8000-00000000b053",
            started:true,localMaps:5,
            originalKovaFilesWritten:false,serverStarted:false,
            sharedTransferIncluded:false,otherHostIncluded:false,
            evidence:"FIVE_VERIFIED_EXISTING_PER_MAP_WORLD_BACKUPS_JOB_QUEUED"
          };
        case "backup.originalBatchStatus":
          if(!originalFiveBackupCopies)
            throw new Error("UM-BACKUP-ORIGINAL-FIVE-NO-JOB");
          return {jobId:"00000000-0000-4000-8000-00000000b053",
            state:"completed",step:"verified",
            changedUtc:"2026-11-09T17:00:00Z",localMaps:5,completedMaps:5,
            points:state.local.maps.slice(0,5).map((map:any)=>
              ({mapId:map.id,mapName:map.name,
                point:"00000000-0000-4000-8000-00000000b052",
                files:4,bytes:4096})),
            totalBytes:20480,allFiveVerified:true,
            originalKovaFilesWritten:false,serverStarted:false,
            sharedTransferIncluded:false,otherHostIncluded:false,
            evidence:"FIVE_VERIFIED_EXISTING_PER_MAP_WORLD_BACKUPS_NO_SHARED_QUORUM"
          };
        case "backup.preview":
          localBackupMap=m.mapId??one;
          return {
            token:"00000000-0000-4000-8000-00000000b001",
            mapId:m.mapId,
            mapName:localBackupMap===two?"Astraeos":"The Island",
            mapKey:localBackupMap===two?"Astraeos_WP":"TheIsland_WP",
            point:null,confirmedStopped:true,willCreateProtectiveBackup:false,
            originalWorldRetained:false,storeWriterTouched:false,
            action:"BACKUP",evidence:"NATIVE_STOPPED_WORLD_BACKUP_PREFLIGHT"
          };
        case "backup.create":
          localBackupSaved=true;
          return {
            mapId:localBackupMap,
            mapName:localBackupMap===two?"Astraeos":"The Island",
            point:"00000000-0000-4000-8000-00000000b002",
            createdUtc:"2026-11-06T08:12:00Z",
            files:4,bytes:12799,characters:1,tribes:1,
            backupVerified:true,asaSaveCompleteProven:false,storeWriterTouched:false,
            evidence:"REAL_VERIFIED_STOPPED_MAP_WORLD_BACKUP"
          };
        case "backup.restorePreview": return {
          token:"00000000-0000-4000-8000-00000000b003",
          mapId:m.mapId,mapName:m.mapId===two?"Astraeos":"The Island",
          mapKey:m.mapId===two?"Astraeos_WP":"TheIsland_WP",
          point:m.point,confirmedStopped:true,
          willCreateProtectiveBackup:true,originalWorldRetained:true,
          storeWriterTouched:false,action:"RESTORE",
          evidence:"NATIVE_STOPPED_WORLD_BACKUP_PREFLIGHT"
        };
        case "backup.restore": return {
          mapId:localBackupMap,
          mapName:localBackupMap===two?"Astraeos":"The Island",
          point:"00000000-0000-4000-8000-00000000b002",
          protectiveBackup:"00000000-0000-4000-8000-00000000b004",
          restored:true,oldWorldRetained:true,worldRestoreVerified:true,
          storeWriterTouched:false,
          evidence:"VERIFIED_STOPPED_MAP_KOVA_STYLE_RESTORE_WITH_PROTECTIVE_COPY"
        };
        case "updates.status": return {
          repository:"Gunter1122cloud/untamed-manager-releases",
          runningVersion:"2.0.0.0",installedTag:"v2.0.0-obsidian-preview.1478",
          provider:"GITHUB_RELEASES",updateCheckPerformed:false,
          installationPermitted:false,updaterVerified:false,
          evidence:"LOCAL_RELEASE_IDENTITY_NO_NETWORK"
        };
        case "updates.check": return {
          repository:"Gunter1122cloud/untamed-manager-releases",
          runningVersion:"2.0.0.0",installedTag:"v2.0.0-obsidian-preview.1478",
          channel:m.channel??"stable",found:true,newer:true,
          tag:"v9.0.0-obsidian-preview.1",version:"9.0.0",
          assetName:"Untamed_Obsidian_Desktop_Preview_Setup.exe",
          sha256:"a".repeat(64),
          releaseNotes:"Synthetic signed-release metadata only",
          installationPermitted:false,updaterVerified:false,
          evidence:"GITHUB_RELEASE_METADATA_NOT_AN_INSTALLER"
        };
        case "updates.prepare": return {
          token:"00000000-0000-4000-8000-00000000b005",
          tag:"v9.0.0-obsidian-preview.1",version:"9.0.0",
          assetName:"Untamed_Obsidian_Desktop_Preview_Setup.exe",
          sha256:"a".repeat(64),fileBytes:15250100,verified:true,
          requiresFinalApproval:true,updaterPackaged:true,
          automaticInstallationStarted:false,
          evidence:"OBSIDIAN_INSTALLER_SHA256_STAGED_ONLY"
        };
        case "updates.install": return {
          externalUpdaterStarted:true,needsManagerExit:true,processId:7654,
          tag:"v9.0.0-obsidian-preview.1",serverProcessesTouched:false,
          evidence:"EXTERNAL_ROLLBACK_UPDATER_HANDOFF"
        };
        case "historical.launchEvidence": return {
          mapStatus:"MENTIONED",gamePortStatus:"MATCH",queryPortStatus:"MATCH",
          modOrderStatus:"MATCH",passiveModOrderStatus:"MATCH",clusterIdPresent:!!m.command?.includes("-clusterid="),
          clusterIdStatus:m.command?.includes("-clusterid=KovaCluster")?"MATCH":"MISMATCH",
          clusterPathPresent:!!m.command?.includes("-ClusterDirOverride="),clusterPathVerified:false,
          useStorePresent:true,
          storeFlagCount:(m.command?.match(/-usestore(?=\\s|$)/gi)||[]).length,
          conversionFlagPresent:!!m.command?.match(/-convert(?:to|from)store/i),
          canonicalSourceFlagsObserved:!!m.command?.includes("?listen")&&
            !!m.command?.match(/-log(?=\\s|$)/i)&&
            (m.command?.match(/-usestore(?=\\s|$)/gi)||[]).length===1 &&
            !m.command?.match(/-convert(?:to|from)store/i),
          findings:m.command?.match(/-convert(?:to|from)store/i)?["CONVERSION_FLAG_PRESENT"]:[],
          sourceAuthenticated:false,fullLaunchArgumentsVerified:false,
          peerWriterVerified:false,productionUseAllowed:false,serverStartAllowed:false,
          evidence:"OPERATOR_TEXT_READ_ONLY_UNAUTHENTICATED"
        };
        case "historical.localParity": return {
          archiveVerified:true,localStoreActive:true,
          allFourExactlyMatch:true,liveTwoHostVerified:false,
          characterTravelVerified:false,productionUseAllowed:false,
          files:["cluster","cluster-store","shared-store-device","shared-store-lock"]
            .map(kind=>({kind,exactlySame:true})),
          evidence:"ORIGINAL_LOCAL_FOUR_FILE_BYTE_PARITY_OFFLINE_NO_LIVE_WRITER"
        };
        case "historical.rconStatus": return {
          mapId:two,mapName:"Astraeos",configured:historicalRconAttached,
          port:historicalRconAttached?37145:0,
          verifiedIni:historicalRconAttached,
          originalStoreTouched:false,
          evidence:"HISTORICAL_RCON_MANAGER_PROFILE_STATUS_ONLY"
        };
        case "historical.rconPreview": {
          if(m.mapId!==two||m.rconPort!==37145||
             m.password!=="HistoricalRconPass!2026")
            throw new Error("Synthetic original RCON INI mismatch");
          return {
            token:historicalRconToken,mapId:two,mapName:"Astraeos",
            rconPort:37145,existingIniVerified:true,
            originalActiveDoneVerified:true,
            originalStoreTouched:false,serverStarted:false,
            evidence:"HISTORICAL_RCON_EXISTING_INI_PREVIEW_ONLY"
          };
        }
        case "historical.rconApply": {
          if(m.token!==historicalRconToken||m.confirm!==true||historicalRconAttached)
            throw new Error("Synthetic one-shot RCON approval required");
          historicalRconAttached=true;
          state.local.maps[1].rconConfigured=true;
          return {mapId:two,mapName:"Astraeos",configured:true,rconPort:37145,
            originalStoreTouched:false,iniModified:false,serverStarted:false,
            evidence:"HISTORICAL_RCON_DPAPI_PROFILE_ATTACHED"};
        }
        case "historical.activeStartStatus": return {
          mapId:two,mapName:"Astraeos",running:historicalActiveStarted,
          confirmedStopped:!historicalActiveStarted,
          pid:historicalActiveStarted?42101:null,
          rconConfigured:historicalRconAttached,
          originalStartAutomatic:false,saveCompleteProven:false,
          gameplayTravelVerified:false,
          evidence:"HISTORICAL_ORIGINAL_PROCESS_READ_ONLY_STATUS"
        };
        case "historical.activeStartPreview": {
          if(!historicalRconAttached||historicalActiveStarted||m.mapId!==two)
            throw new Error("Original Store ACTIVE/DONE source proof required");
          return {
            token:historicalStartToken,mapId:two,mapName:"Astraeos",
            gamePort:7847,queryPort:27025,rconPort:37145,
            originalTwoPeersVerified:true,conversionAllowed:false,
            automaticRetryAllowed:false,serverStarted:false,readyCertified:false,
            gameplayTravelVerified:false,
            evidence:"ORIGINAL_ACTIVE_STORE_NATIVE_START_PREFLIGHT"
          };
        }
        case "historical.activeStartApply": {
          if(!historicalRconAttached||historicalActiveStarted||
             m.token!==historicalStartToken||m.confirm!==true||
             m.typedApproval!=="STORE-V1 ACTIVE START")
            throw new Error("Original Store approval denied");
          historicalActiveStarted=true;
          state.local.maps[1].running=true;state.local.maps[1].status="Fut";
          return {
            mapId:two,mapName:"Astraeos",pid:42101,
            createdUtc:"2026-11-07T14:00:00Z",startRequested:true,
            readyCertified:false,originalStoreMetadataChanged:false,
            conversionPerformed:false,gameClientRejoinVerified:false,
            automaticRetryAllowed:false,
            evidence:"ORIGINAL_STORE_V1_REAL_LOCAL_ASA_START_PID_OBSERVED"
          };
        }
        case "historical.adminPreview": {
          historicalAdminPending=false;
          if(m.mapId!==two||!historicalActiveStarted||
             !historicalRconAttached||!originalRconSourceValid||
             !originalRconPortOwner||oldStopNeedsOperator)
            throw new Error("Synthetic original Store admin source not ready");
          const act=m.action as string;
          if(!["broadcast","serverChat","wildWipe","custom"].includes(act))
            throw new Error("Synthetic original Store admin action denied");
          if(act==="custom"&&/saveworld|doexit|restart/i.test(m.message))
            throw new Error("Original Store lifecycle belongs to a separate path");
          historicalAdminCommand=act==="broadcast"?"Broadcast "+m.message:
            act==="serverChat"?"ServerChat "+m.message:
            act==="wildWipe"?"DestroyWildDinos":m.message;
          historicalAdminPending=true;
          return {token:historicalAdminToken,mapId:two,mapName:"Astraeos",
            action:act,command:historicalAdminCommand,
            approvalWord:act==="wildWipe"?"STORE-V1 DINÓTÖRLÉS":"STORE-V1 ADMIN",
            dangerous:act==="wildWipe"||act==="custom",
            expiresUtc:"2026-11-07T23:00:00Z",originalStoreActive:true,
            dispatched:false,gameClientVerified:false,
            originalStoreMetadataWritten:false,
            evidence:"ORIGINAL_STORE_ACTIVE_ADMIN_RCON_PREVIEW"};
        }
        case "historical.adminSend": {
          const expected=historicalAdminCommand==="DestroyWildDinos"
            ?"STORE-V1 DINÓTÖRLÉS":"STORE-V1 ADMIN";
          if(!historicalAdminPending||m.token!==historicalAdminToken||
             m.confirm!==true||m.typedApproval!==expected)
            throw new Error("Synthetic original Store admin approval denied");
          historicalAdminPending=false;
          return {mapId:two,mapName:"Astraeos",action:"original",
            command:historicalAdminCommand,response:"Synthetic original RCON admin dispatched",
            commandSent:true,oneShot:true,originalStoreMetadataWritten:false,
            saveCompleteProven:false,gameplayEffectVerified:false,
            evidence:"ORIGINAL_STORE_ACTIVE_ADMIN_RCON_SINGLE_SEND"};
        }
        case "historical.rconLiveStatus": return {
          mapId:two,mapName:"Astraeos",
          configured:historicalRconAttached,
          processVerified:historicalActiveStarted&&originalRconSourceValid,
          portOwned:originalRconPortOwner&&historicalActiveStarted,
          pid:historicalActiveStarted?42101:null,
          permitted:historicalActiveStarted&&historicalRconAttached&&
            originalRconSourceValid&&originalRconPortOwner,
          allowedCommands:historicalActiveStarted&&originalRconSourceValid&&
            originalRconPortOwner?["ListPlayers","GetGameLog","GetChat"]:[],
          storeTouched:false,saveCompleteProven:false,
          evidence:"ORIGINAL_ACTIVE_SOURCE_WMI_PID_TCP_OWNER_READY"
        };
        case "historical.rconPlayers":
        case "historical.rconGameLog":
        case "historical.rconChatLog": {
          if(!historicalActiveStarted||!historicalRconAttached||
             !originalRconSourceValid||!originalRconPortOwner||m.mapId!==two)
            throw new Error("Original Store PID/source/RCON owner not verified");
          const command=m.method==="historical.rconPlayers"?"ListPlayers":
            m.method==="historical.rconGameLog"?"GetGameLog":"GetChat";
          return {mapId:two,mapName:"Astraeos",command,
            response:"Synthetic original ASA "+command,observedUtc:"2026-11-07T17:00:00Z",
            readOnly:true,storeTouched:false,saveCompleteProven:false,
            evidence:"ORIGINAL_STORE_V1_VERIFIED_LOCAL_READ_ONLY_RCON"};
        }
        case "historical.restartRecovery": {
          return {mapId:two,
            phase:originalRestartFail&&originalRestartQueued?"NeedsOperator":
              originalRestartQueued?"Completed":"None",
            entryBelongsToMap:originalRestartQueued,
            blocked:oldStopNeedsOperator||
              (originalRestartFail&&originalRestartQueued),
            requiresOperator:oldStopNeedsOperator||
              (originalRestartFail&&originalRestartQueued),
            inMemoryRunning:false,jobId:null,
            oldPid:originalRestartQueued?42101:null,
            newPid:originalRestartQueued&&!originalRestartFail?42102:0,
            automaticRecovery:false,automaticRetry:false,
            originalStoreTouched:false,lockCleared:false,
            evidence:"ORIGINAL_STORE_V1_DURABLE_RESTART_RECOVERY_READ_ONLY"};
        }
        case "historical.restartPreview": {
          if(m.mapId!==two||!historicalRconAttached||
             !historicalActiveStarted||oldStopNeedsOperator||
             !originalRconSourceValid||!originalRconPortOwner||
             originalRestartQueued||originalRestartApproved)
            throw new Error("Original Store Restart blocked");
          originalRestartApproved=true;
          return {token:originalRestartToken,mapId:two,mapName:"Astraeos",
            pid:42101,rconPort:37145,usesSaveWorld:true,
            usesDoExit:true,launchesOriginalActive:true,
            conversionAllowed:false,automaticRetry:false,
            gameplayReadinessCertified:false,originalStoreTouched:false,
            evidence:"ORIGINAL_STORE_V1_RESTART_TWO_STAGE_PREFLIGHT"};
        }
        case "historical.restartApply": {
          if(m.token!==originalRestartToken||m.confirm!==true||
             m.typedApproval!=="STORE-V1 SAVEWORLD DOEXIT START"||
             !originalRestartApproved||originalRestartQueued)
            throw new Error("Original Store Restart approval mismatch");
          originalRestartQueued=true;
          originalRestartApproved=false;originalRestartSends++;
          return {jobId:originalRestartJob,started:true,
            automaticRetry:false,forceKill:false,storeTouched:false,
            evidence:"ORIGINAL_STORE_V1_MANUAL_RESTART_JOB_QUEUED"};
        }
        case "historical.restartStatus": {
          if(!originalRestartQueued||m.jobId!==originalRestartJob)
            throw new Error("Original Store Restart job missing");
          if(originalRestartFail)
            return {jobId:originalRestartJob,mapId:two,state:"needsOperator",
              step:"Synthetic new spawn failed",changedUtc:"2026-11-08T18:40:00Z",
              oldPid:42101,newPid:0,oldProcessStopped:true,
              preExitSaveLogAndWorldObserved:true,
              readyCertified:false,gameClientRejoinVerified:false,
              finalDoExitSaveCertified:false,automaticRetry:false,
              forceKill:false,storeTouched:false,
              evidence:"ORIGINAL_STORE_V1_NATIVE_MANUAL_RESTART_JOB"};
          return {jobId:originalRestartJob,mapId:two,state:"completed",
            step:"Új eredeti ASA PID létrejött",changedUtc:"2026-11-08T18:40:00Z",
            oldPid:42101,newPid:42102,oldProcessStopped:true,
            preExitSaveLogAndWorldObserved:true,
            readyCertified:false,gameClientRejoinVerified:false,
            finalDoExitSaveCertified:false,automaticRetry:false,
            forceKill:false,storeTouched:false,
            evidence:"ORIGINAL_STORE_V1_NATIVE_MANUAL_RESTART_JOB"};
        }
        case "historical.gracefulStopRecovery": return {
          mapId:two,
          operationId:oldStopNeedsOperator?originalStopJob:null,
          phase:originalSaveRecoveryFixture?
            (originalSaveReviewed?"OperatorReviewed":"NeedsOperator"):
            oldStopNeedsOperator?"NeedsOperator":
            originalSaveReattached?"Mutating":
            originalStopQueued?"Completed":"None",
          operationKind:originalSaveReattached?"Backup":
            oldStopNeedsOperator||originalStopQueued?"Launch":"None",
          updatedUtc:oldStopNeedsOperator||originalStopQueued
            ?"2026-11-07T18:35:00Z":null,
          // A native task can be queued before its durable journal changes phase.
          blocked:oldStopNeedsOperator||
            (originalSaveRecoveryFixture&&!originalSaveReviewed),
          requiresOperator:oldStopNeedsOperator||
            (originalSaveRecoveryFixture&&!originalSaveReviewed),
          inMemoryRunning:originalSaveReattached,
          jobId:originalSaveReattached?originalSaveJob:null,
          activeJobKind:originalSaveReattached?"SaveWorldOnly":null,
          automaticRecovery:false,automaticRetry:false,
          sharedStoreMutated:false,lockCleared:false,
          evidence:"ORIGINAL_STORE_V1_DURABLE_LOCAL_OPERATION_RECOVERY_READ_ONLY"
        };
        case "historical.saveWorldPreview": {
          if(!historicalRconAttached||!historicalActiveStarted||
             !originalRconSourceValid||!originalRconPortOwner||m.mapId!==two||
             oldStopNeedsOperator||originalSavePending)
            throw new Error("Original Store SaveWorld owner/session unavailable");
          originalSavePending=true;
          return {
            token:originalSaveToken,mapId:two,mapName:"Astraeos",
            pid:42101,rconPort:37145,originalTwoPeersVerified:true,
            usesSaveWorld:true,usesDoExit:false,forceKillAllowed:false,
            automaticRestart:false,originalStoreTouched:false,
            evidence:"ORIGINAL_STORE_V1_SAVEWORLD_ONLY_PREFLIGHT"
          };
        }
        case "historical.saveWorldApply": {
          if(m.token!==originalSaveToken||m.confirm!==true||
             m.typedApproval!=="STORE-V1 SAVEWORLD"||originalSaveQueued||
             !originalSavePending||!historicalActiveStarted||oldStopNeedsOperator)
            throw new Error("Original Store SaveWorld approval denied");
          originalSavePending=false;
          originalSaveQueued=true;originalSaveDispatches++;
          return {jobId:originalSaveJob,started:true,
            automaticRetry:false,forceKill:false,storeTouched:false,
            evidence:"ORIGINAL_STORE_V1_REAL_SAVEWORLD_ONLY_JOB_QUEUED"};
        }
        case "historical.saveWorldEvidence": {
          if(m.mapId!==two||originalSaveEvidenceTampered)
            throw new Error("UM-ORIGINAL-SAVE-JOURNAL-TAMPERED");
          const complete=originalSaveQueued&&!(originalSaveReattached&&originalSaveHold);
          return {
            mapId:two,phase:originalSaveRecoveryFixture?
              (originalSaveReviewed?"reviewed":"needsOperator"):
              originalSaveQueued?(complete?"completed":"prepared"):"none",
            operationId:originalSaveRecoveryFixture?originalSaveJob:
              originalSaveQueued?originalSaveJob:null,
            pid:originalSaveQueued?42101:null,
            pidStartedUtc:originalSaveQueued?"2026-11-08T18:00:00Z":null,
            saveObservedUtc:complete?"2026-11-08T18:35:00Z":null,
            localSaveCompleteObserved:complete,
            bothHostsSaveCompleteCertified:false,sharedStoreWriterCertified:false,
            gameClientRejoinVerified:false,productionRestoreAllowed:false,
            fsyncCertified:false,sourceStateCertifiedForCurrentWriter:false,
            evidence:"MANAGER_LOCAL_DURABLE_SAVE_LOG_WITNESS_NO_SHARED_STORE_QUORUM"
          };
        }
        case "historical.saveWorldReviewPreview": {
          if(!originalSaveRecoveryFixture||originalSaveReviewed||m.mapId!==two)
            throw new Error("Store Save review source not verified");
          return {token:"00000000-0000-4000-8000-000000001694",
            mapId:two,phase:"needsOperator",operationId:originalSaveJob,
            coordinatorPhase:"NeedsOperator",originalSourceRevalidated:true,
            confirmedStopped:true,preservesOriginalReceipt:true,
            originalStoreFilesWritten:false,automaticRetry:false,
            requiresTypedApproval:true,
            evidence:"OPERATOR_REVIEW_STABLE_STOPPED_ORIGINAL_STORE_SAVE"};
        }
        case "historical.saveWorldReviewApply": {
          if(!originalSaveRecoveryFixture||originalSaveReviewed||
             m.token!=="00000000-0000-4000-8000-000000001694"||
             !m.confirm||m.confirmation!=="REVIEW STORE SAVE")
            throw new Error("Store Save review approval required");
          originalSaveReviewed=true;
          return {mapId:two,phase:"reviewed",reviewed:true,
            oldReceiptArchived:true,originalStoreFilesWritten:false,
            autoRetry:false,serverStarted:false,
            nextOperationRequiresNewApproval:true,
            evidence:"OPERATOR_REVIEW_DURABLE_LOCAL_JOURNALS_NO_STORE_MUTATION"};
        }
        case "historical.saveWorldStatus": {
          if(!originalSaveQueued||m.jobId!==originalSaveJob)
            throw new Error("Original Store SaveWorld job unknown");
          const held=originalSaveReattached&&originalSaveHold;
          if(!held)originalSaveReattached=false;
          return {jobId:originalSaveJob,kind:"SaveWorldOnly",
            state:held?"running":"completed",
            step:held?"Natív mentési művelet megfigyelése":
              "Store-v1 SaveWorld igazolva; az ASA tovább fut",
            changedUtc:"2026-11-08T18:35:00Z",
            confirmedStopped:false,preExitSaveLogAndWorldObserved:!held,
            finalDoExitSaveCertified:false,gameClientRejoinVerified:false,
            automaticRetry:false,forceKill:false,storeTouched:false,
            evidence:"ORIGINAL_STORE_V1_SAVEWORLD_ONLY_NO_DOEXIT_STATE"};
        }
        case "historical.gracefulStopPreview": {
          if(!historicalRconAttached||!historicalActiveStarted||
             !originalRconSourceValid||!originalRconPortOwner||m.mapId!==two||
             oldStopNeedsOperator)
            throw new Error("Original RCON owner or Store session invalid");
          return {
            token:originalStopToken,mapId:two,mapName:"Astraeos",
            pid:42101,rconPort:37145,originalTwoPeersVerified:true,
            usesSaveWorld:true,usesDoExit:true,forceKillAllowed:false,
            automaticRestart:false,originalStoreTouched:false,
            evidence:"ORIGINAL_STORE_V1_SAVEWORLD_DOEXIT_PREFLIGHT_ONLY"
          };
        }
        case "historical.gracefulStopApply": {
          if(m.token!==originalStopToken||m.confirm!==true||
             m.typedApproval!=="STORE-V1 SAVEWORLD DOEXIT"||originalStopQueued||
             !historicalActiveStarted)
            throw new Error("Original Store Stop approval invalid");
          originalStopQueued=true;
          return {jobId:originalStopJob,started:true,
            automaticRetry:false,forceKill:false,storeTouched:false,
            evidence:"ORIGINAL_STORE_V1_REAL_STOP_JOB_QUEUED"};
        }
        case "historical.gracefulStopStatus": {
          if(!originalStopQueued||m.jobId!==originalStopJob)
            throw new Error("Original Store Stop job unknown");
          historicalActiveStarted=false;
          state.local.maps[1].running=false;state.local.maps[1].status="Leállítva";
          return {jobId:originalStopJob,state:"completed",step:"Régi ASA PID kilépett",
            changedUtc:"2026-11-07T18:35:00Z",
            confirmedStopped:true,preExitSaveLogAndWorldObserved:true,
            finalDoExitSaveCertified:false,gameClientRejoinVerified:false,
            automaticRetry:false,forceKill:false,storeTouched:false,
            evidence:"ORIGINAL_STORE_V1_REAL_SAVEWORLD_DOEXIT_STATE"};
        }
        case "historical.localStatus": return {
          found:true,localFilesVerified:true,configHashVerified:true,
          localLockVerified:true,allLocalMapsActive:true,
          localSnapshotReady:true,liveTwoHostVerified:false,
          characterTravelVerified:false,productionUseAllowed:false,
          maps:["TheIsland_WP","Astraeos_WP","Valguero_WP","Genesis_WP","Extinction_WP"]
            .map(mapKey=>({mapKey,active:true,sessionMatches:true,
              rootMatches:true,selected:true,backupIdentityPresent:true})),
          evidence:"ORIGINAL_LOCAL_STORE_V1_FOUR_FILES_READ_ONLY_NO_PEER_CERTIFICATION"
        };
        case "historical.pairedEvidence": return {
          files:12,maps:10,peers:2,sessionConsistent:true,
          configHashesVerified:true,activeStoreRecords:true,
          completeTwoHostEvidence:false,historicalConsistent:false,
          historicalActiveProtocolShape:false,
          historicalStartBlockers:["STORE_CONFIG_HASH_MISMATCH"],
          findings:["LocalLock"],
          hosts:[{number:1,maps:5,localEvidence:true,sessionMatches:true,
            configHashMatches:false,lockMatches:false,activeMaps:true},
            {number:2,maps:5,localEvidence:true,sessionMatches:true,
            configHashMatches:true,lockMatches:true,activeMaps:true}],
          productionUseAllowed:false,
          evidence:"PAIRED_COPIES_OFFLINE_READ_ONLY_NO_WRITER_CERTIFICATION"
        };
        case "historical.originalList": return {
          sourceVerified:true,originalFourFilesUnchanged:true,
          evidence:"ORIGINAL_KOVA_FIVE_LOCAL_MAPS_READ_ONLY",
          maps:[{mapId:"00000000-0000-4000-8000-000000009001",
            mapKey:"TheIsland_WP",name:"The Island",
            folderName:"OriginalIsland",gamePort:7777,queryPort:27015,
            enabledMods:22,passiveMods:3,registered:false}]
        };
        case "historical.originalClusterPreview": {
          sourceKovaClusterToken="00000000-0000-4000-8000-000000009555";
          return {
            token:sourceKovaClusterToken,clusterId:"KovaCluster",
            fiveOriginalMaps:5,alreadyAssigned:sourceKovaClusterAssigned?5:0,
            missingAssignments:sourceKovaClusterAssigned?0:5,
            registryRevision:clusterRevision,sourceIdentityPreserved:true,
            originalStoreFilesWritten:false,asaProcessTouched:false,
            writerSessionLockTouched:false,requiresExactApproval:true,
            evidence:"ORIGINAL_FIVE_MAP_METADATA_BOOTSTRAP_PREVIEW_ONLY"
          };
        }
        case "historical.originalClusterApply": {
          if(!sourceKovaClusterToken||m.token!==sourceKovaClusterToken||
             m.confirm!==true||m.typedApproval!=="EREDETI KOVA REGISZTER")
            throw new Error("UM-ORIGINAL-CLUSTER-APPROVAL-REQUIRED");
          sourceKovaClusterToken="";sourceKovaClusterApplies++;
          const missing=sourceKovaClusterAssigned?0:5;
          sourceKovaClusterAssigned=true;
          if(missing)clusterRevision++;
          return {
            clusterId:"kovacluster",revision:clusterRevision,
            addedCount:missing,removedCount:0,movedCount:0,
            originalFilesWritten:false,transferFilesWritten:false,
            storeWriterTouched:false,serverStarted:false,
            evidence:"ORIGINAL_FIVE_MAP_MANAGER_REGISTRY_METADATA_ONLY"
          };
        }
        case "historical.originalBatchPreview": return {
          token:"00000000-0000-4000-8000-000000009003",
          localMaps:1,mapNames:["The Island"],
          sourceIdentityPreserved:true,originalFilesWritten:false,
          processTouched:false,requiresExplicitApproval:true,
          evidence:"ORIGINAL_KOVA_ATOMIC_LOCAL_BATCH_PREVIEW"
        };
        case "historical.originalBatchRegister":
          state.local.maps.push({
            id:"00000000-0000-4000-8000-000000009001",
            name:"The Island",running:false,status:"Védett Store-v1",
            gamePort:7777,steamBuildId:null,steamUpdateState:"Nem ellenőrizhető"
          });
          return {
            importedMaps:1,mapIds:["00000000-0000-4000-8000-000000009001"],
            sourceIdentityPreserved:true,originalFilesWritten:false,
            serverStarted:false,requiresOriginalRconAttach:true,
            evidence:"ORIGINAL_KOVA_ATOMIC_LOCAL_BATCH_REGISTERED"
          };
        case "historical.originalPreview": return {
          token:"00000000-0000-4000-8000-000000009002",
          mapId:m.mapId,mapName:"The Island",folderName:"OriginalIsland",
          gamePort:m.gamePort,queryPort:m.queryPort,
          originalEnabledMods:22,originalPassiveMods:3,
          originalLaunchProfileVerified:true,
          originalIdsPreserved:true,originalFilesWritten:false,
          serverStarted:false,requiresOriginalRconAttach:true,
          evidence:"ORIGINAL_KOVA_DIRECT_IMPORT_PREVIEW"
        };
        case "historical.originalRegister":
          state.local.maps.push({
            id:"00000000-0000-4000-8000-000000009001",
            name:"The Island",running:false,status:"Védett Store-v1",
            gamePort:7777,steamBuildId:null,steamUpdateState:"Nem ellenőrizhető"});
          return {mapId:"00000000-0000-4000-8000-000000009001",
            mapName:"The Island",registered:true,originalIdsPreserved:true,
            originalFilesWritten:false,serverStarted:false,
            requiresOriginalRconAttach:true,originalLaunchProfileVerified:true,
            requiresExactModOrderVerification:false,
            evidence:"ORIGINAL_KOVA_DIRECT_LOCAL_MAP_REGISTERED"};
        case "historical.list": return {
          sourceVerified:true,completeLaunchArguments:false,liveStoreVerified:false,
          allowServerStart:false,evidence:"HISTORICAL_LOCAL_ARCHIVE_READ_ONLY",
          maps:[{mapId:"00000000-0000-4000-8000-000000003888",
            mapKey:"ScorchedEarth_WP",gamePort:7777,queryPort:27015,
            enabledMods:22,passiveMods:3}]
        };
        case "historical.preview": return {
          token:"00000000-0000-4000-8000-000000003889",
          mapId:m.mapId,mapKey:"ScorchedEarth_WP",name:"Scorched Earth",
          gamePort:7777,queryPort:27015,enabledMods:22,passiveMods:3,
          folderName:"TestExistingMap",historicalActive:true,
          originalMapIdPreserved:true,fullLaunchArgumentsVerified:false,
          peerWriterVerified:false,serverStartAllowed:false,
          evidence:"HISTORICAL_STORE_V1_QUARANTINE_PREVIEW"
        };
        case "historical.register":
          state.local.maps.push({
            id:"00000000-0000-4000-8000-000000003888",
            name:"Scorched Earth",running:false,status:"Store-v1 zárolt",
            gamePort:7777,steamBuildId:null,steamUpdateState:"Nem ellenőrizhető"});
          return {mapId:"00000000-0000-4000-8000-000000003888",
            mapName:"Scorched Earth",registered:true,storeProtected:true,
            requiresOriginalLaunchVerification:true,serverStarted:false,
            worldFilesChanged:false,evidence:"HISTORICAL_STORE_V1_REGISTERED_QUARANTINED"};
        case "steam.freshCurrent": return {current:null};
        case "steam.freshBrowse": return {selected:true,installRoot:"D:\\ASA-NEW"};
        case "steam.freshPreflight":
          if(m.customLevel && !/^[A-Za-z][A-Za-z0-9_]{0,79}$/.test(m.customLevel))
            throw new Error("UM-FRESH-MAP-LEVEL-INVALID");
          if(m.installRoot==="D:\\ASA-Servers")
            throw new Error("UM-FRESH-ROOT-OVERLAP|map=The Island|path=D:\\ASA-Servers\\TheIsland");
          return {
          token:"00000000-0000-4000-8000-000000008888",
          mapName:m.mapName||"Valguero",mapLevel:m.customLevel||"Valguero_WP",folderName:"ASA-NEW",
          steamCmdPresent:true,usingManagerDefaultSteamCmd:true,needsOfficialSteamCmdDownload:false,
          existingInterruptedInstall:false,minimumFreeGiB:35,
          evidence:"FRESH_INSTALL_PREFLIGHT_NO_FILE_MUTATION"
        };
        case "steam.freshStart":
          freshId="00000000-0000-4000-8000-000000008889";
          return {jobId:freshId,started:true,evidence:"EXPLICIT_FRESH_INSTALL_STARTED"};
        case "steam.freshStatus":
          return {jobId:freshId,mapName:"Valguero",stage:"verified",
            detail:"ASA EXE és Steam appmanifest ellenőrizve",percent:100,
            verified:true,registered:false,errorCode:null,evidence:"NATIVE_STEAMCMD_JOB_STATE"};
        case "steam.freshRegister":
          state.local.maps.push({id:"00000000-0000-4000-8000-000000008881",
            name:"Valguero",running:false,status:"Nincs beállítva",gamePort:0,
            steamBuildId:"123456",steamUpdateState:"Nem ellenőrizhető"});
          return {mapId:"00000000-0000-4000-8000-000000008881",
            mapName:"Valguero",needsConfiguration:true,serverStarted:false,
            evidence:"VERIFIED_STEAM_FILES_REGISTERED_MANAGER_PROFILE_ONLY"};
        case "steam.freshCancel": return {cancelRequested:true};
        case "steam.freshDiscard": return {discarded:true};
        case "maps.maintenanceStatus": {
          const map=state.local.maps.find(x=>x.id===m.mapId);
          if(!map)throw new Error("UM-MAP-NOT-OWNED");
          return {mapId:map.id,mapName:map.name,maintenance:Boolean((map as any).maintenance),
            permitted:!map.running&&!(map as any).storeProtected,
            storeProtected:Boolean((map as any).storeProtected),requiresStopped:true,
            serverProcessModified:false,evidence:"PERSISTED_LOCAL_MANAGER_MAINTENANCE_ONLY"};
        }
        case "maps.maintenanceSet": {
          const map=state.local.maps.find(x=>x.id===m.mapId);
          if(!m.confirm||typeof m.maintenance!=="boolean")
            throw new Error("UM-MAP-MAINTENANCE-CONFIRM");
          if(!map||map.running||(map as any).storeProtected)
            throw new Error("UM-MAP-MAINTENANCE-STOPPED-NONSTORE-ONLY");
          (map as any).maintenance=m.maintenance;
          return {mapId:map.id,mapName:map.name,maintenance:m.maintenance,
            permitted:true,storeProtected:false,requiresStopped:true,
            serverProcessModified:false,evidence:"PERSISTED_LOCAL_MANAGER_MAINTENANCE_ONLY"};
        }
        case "rcon.configStatus": {
          const map=state.local.maps.find(x=>x.id===m.mapId);
          if(nextRconStatusRefreshFails && (map as any)?.rconConfigured){
            nextRconStatusRefreshFails=false;
            throw new Error("SYNTHETIC-POSTCOMMIT-STATUS-UNAVAILABLE");
          }
          return {mapId:m.mapId,mapName:map?.name||"Ismeretlen map",
            rconPort:m.mapId===two?37085:37015,
            configured:Boolean((map as any)?.rconConfigured),
            permitted:Boolean(map)&&map.gamePort>0&&!map.running&&!(map as any).storeProtected&&!(map as any).rconConfigured,
            evidence:"LOCAL_RCON_CONFIGURATION_STATUS_NO_WRITE"};
        }
        case "rcon.configPreview": {
          if(m.password?.length<12 || !m.createIni)throw new Error("UM-RCON-CONFIG-FIELDS");
          return {token:"00000000-0000-4000-8000-000000008879",
            mapId:m.mapId,mapName:"Genesis Part 1",rconPort:m.rconPort,
            createsNewIni:true,existingIniVerified:false,
            evidence:"STOPPED_LOCAL_RCON_PREVIEW_NO_WRITES"};
        }
        case "rcon.configApply": {
          if(!m.confirm)throw new Error("UM-RCON-CONFIG-CONFIRMATION");
          rconApplyCalls++;
          const map=state.local.maps.find(x=>x.name==="Genesis Part 1");
          if(map)(map as any).rconConfigured=true;
          return {mapId:map?.id||"",mapName:"Genesis Part 1",
            configured:true,rconPort:37015,createsNewIni:true,serverStarted:false,
            evidence:"STOPPED_LOCAL_RCON_CONFIG_COMMITTED"};
        }
        case "servers.initialStatus": {
          const map = state.local.maps.find(x=>x.id===m.mapId);
          if(!map)throw new Error("UM-MAP-NOT-OWNED");
          return {mapId:map.id,mapName:map.name,level:customInitialLevel?null:"Genesis_WP",
            requiresCustomLevel:customInitialLevel,
            initialSetupAvailable:map.gamePort===0&&!initialBlocked,
            confirmedStopped:!map.running&&!initialBlocked,
            blocker:initialBlocked?"PROCESS_STOP_NOT_VERIFIED":
              map.gamePort>0?"PROFILE_ALREADY_CONFIGURED_OR_PROTECTED":"NONE",
            processStatus:initialBlocked?"Synthetic uncertain WMI process observation":"Synthetic stopped",
            hasServerExecutable:true,gamePort:map.gamePort,queryPort:0,
            suggestion:{game:7797,query:27035},
            evidence:"LOCAL_MANAGER_PROFILE_FIRST_SETUP_STATUS"};
        }
        case "servers.initialPreview": return {
          token:"00000000-0000-4000-8000-000000008877",mapId:m.mapId,
          mapName:"Genesis Part 1",sessionName:m.sessionName||"Untamed Kingdom",
          mapLevel:customInitialLevel?m.customLevel:"Genesis_WP",
          requiresCustomLevel:customInitialLevel,
          gamePort:m.gamePort||7797,queryPort:m.queryPort||27035,
          rconEnabled:false,serverStartRequested:false,configuredArgumentsLength:93,
          evidence:"INITIAL_MAP_PROFILE_PREVIEW_NO_WRITES"
        };
        case "servers.initialApply": {
          const map=state.local.maps.find(x=>x.name==="Genesis Part 1");
          if(map)map.gamePort=7797;
          return {mapId:map?.id||"",mapName:"Genesis Part 1",
            configured:true,gamePort:7797,queryPort:27035,
            rconEnabled:false,serverStarted:false,
            evidence:"ATOMIC_MANAGER_PROFILE_INITIAL_SETUP_ONLY"};
        }
        case "servers.inspectLocal": return {importToken:"00000000-0000-4000-8000-000000009999",name:m.name||"The Island",folderName:"ASA-LAB",exeFound:true,apiLoaderDetected:false,portsConfigured:false,evidence:"LOCAL_BINARY_LAYOUT_PROBED_NO_MUTATION"};
        case "servers.importLocal":
          state.local.maps.push({id:"00000000-0000-4000-8000-000000009998",name:"Genesis Part 1",running:false,status:"Nincs beállítva",gamePort:0,steamBuildId:null,steamUpdateState:"Nem ellenőrizhető"});
          return {mapId:"00000000-0000-4000-8000-000000009998",mapName:"Genesis Part 1",exeRegistered:true,needsConfiguration:true,evidence:"MANAGER_PROFILE_ONLY_NO_ASA_CHANGE"};
        case "schedule.nativeOverview":
          return {hostName:state.local.hostName,runnerState:"fixture-no-asareal",
            jobs:state.local.maps.flatMap(map=>nativeSchedules[map.id]??[]),
            remoteExecutionAllowed:false,
            evidence:"NATIVE_LOCAL_SCHEDULE_SUMMARY_READ_ONLY"};
        case "schedule.nativeStatus": {
          const map=state.local.maps.find(x=>x.id===m.mapId);
          if(!map)throw new Error("UM-ASA-SCHEDULE-NOT-LOCAL");
          const jobs=nativeSchedules[m.mapId??""]??[];
          const reboot=jobs.find(x=>x.kind==="Restart"&&x.enabled);
          const restartConflicts=!reboot?[]:state.local.maps
            .filter(x=>x.id!==m.mapId)
            .flatMap(x=>(nativeSchedules[x.id]??[])
              .filter(j=>j.kind==="Restart"&&j.enabled&&
                j.minuteOfDay===reboot.minuteOfDay&&Boolean(j.daysMask&reboot.daysMask))
              .map(()=>x.name));
          return {mapId:m.mapId,runnerState:"fixture-no-asareal",jobs,history:[],restartConflicts,
            protectedStore:Boolean(map.storeProtected),autoUpdateAvailable:false,
            remoteExecutionAllowed:false};
        }
        case "schedule.nativeSet": {
          const map=state.local.maps.find(x=>x.id===m.mapId);
          if(!map||map.storeProtected||!m.confirm||
            !["SaveWorld","Restart"].includes(m.kind??"")||
            !["Interval","Daily"].includes(m.frequency??"")||
            (m.kind==="Restart"&&m.frequency!=="Daily")||
            !Number.isInteger(m.minuteOfDay)||m.minuteOfDay!<0||m.minuteOfDay!>1439||
            !Number.isInteger(m.daysMask)||m.daysMask!<1||m.daysMask!>127)
            throw new Error("UM-ASA-SCHEDULE-NATIVE-DENIED");
          const id=m.mapId??"";
          const jobs=nativeSchedules[id]??[];
          const next={mapId:id,kind:m.kind,frequency:m.frequency,
            intervalMinutes:m.intervalMinutes??120,minuteOfDay:m.minuteOfDay,
            daysMask:m.daysMask,enabled:true,nextDueUtc:"2026-11-09T03:00:00Z",
            lastClaimedUtc:null,lastStatus:"scheduled",lastEvidence:"",
            evidence:"DURABLE_NATIVE_LOCAL_SCHEDULE_NO_AUTO_REPLAY"};
          nativeSchedules[id]=[...jobs.filter(x=>x.kind!==m.kind),next];
          return {job:next,restartConflicts:[]};
        }
        case "schedule.nativeDisable": {
          if(!m.confirm)throw new Error("UM-ASA-SCHEDULE-APPROVAL");
          const jobs=nativeSchedules[m.mapId??""]??[];
          const job=jobs.find(x=>x.kind===m.kind);
          if(!job)throw new Error("UM-ASA-SCHEDULE-NOT-FOUND");
          const disabled={...job,enabled:false,lastStatus:"disabled"};
          nativeSchedules[m.mapId??""]=jobs.map(x=>x.kind===m.kind?disabled:x);
          return disabled;
        }
        case "operations.scheduleCreate": {
          if(!m.confirm || !state.local.maps.some(x=>x.id===m.mapId)||
             !["Observation","ModCheck","ApiCheck","SteamCheck"].includes(m.kind??"")||
             !m.intervalMinutes || m.intervalMinutes<5 || m.intervalMinutes>10080 ||
             opsJobEnabled)throw new Error("UM-OPS-SCHEDULE-DENIED");
          opsJobCreated=true;opsJobEnabled=true;
          return {jobId:opsJobId,mapId:m.mapId,kind:m.kind,
            intervalMinutes:m.intervalMinutes,enabled:true,
            autoLifecycleAllowed:false,remoteCommandsAllowed:false,
            evidence:"PERSISTED_LOCAL_READ_ONLY_SCHEDULE_ONLY"};
        }
        case "operations.scheduleDisable": {
          if(!m.confirm || m.jobId!==opsJobId || !opsJobCreated)
            throw new Error("UM-OPS-SCHEDULE-DENIED");
          opsJobEnabled=false;
          return {jobId:opsJobId,enabled:false,autoLifecycleAllowed:false,
            evidence:"LOCAL_READ_ONLY_SCHEDULE_DISABLED"};
        }
        case "operations.snapshot":
        case "operations.map": {
          const maps=state.local.maps.filter(x=>
            m.method==="operations.snapshot"||m.mapId===x.id).map(x=>({
              mapId:x.id,mapName:x.name,
              running:x.running,confirmedStopped:!x.running,status:x.status,
              pid:x.running?12345:null,startedUtc:x.running?"2026-11-06T09:00:00Z":null,
              uptimeSeconds:x.running?7200:null,
              memoryMiB:x.running?9234.5:null,memoryVerified:x.running,
              maintenance:false,storeProtected:false,gamePort:x.gamePort,queryPort:27015,
              rconConfigured:x.running,rconPort:x.running?37015:null,
              autoStartConfigured:false,lifecycleActionsVerified:false,
              evidence:"EXACT_LOCAL_PROCESS_IDENTITY_NO_NETWORK_OR_FILE_WRITE"
            }));
          return {hostName:state.local.hostName,observedUtc:"2026-11-06T11:00:00Z",
            maps,schedules:[{
              id:"00000000-0000-4000-8000-000000009987",mapId:one,
              mapName:"The Island",kind:"Observation",frequency:"Daily",
              enabled:false,nextUtc:"2026-11-07T06:30:00Z",
              state:"disabled",autoExecutionPermitted:false
            },...(opsJobCreated?[{
              id:opsJobId,mapId:one,mapName:"The Island",
              kind:"Observation",frequency:"IntervalMinutes",enabled:opsJobEnabled,
              nextUtc:"2026-11-07T12:00:00Z",
              state:opsJobEnabled?"read-only-preview":"disabled",
              autoExecutionPermitted:false
            }]:[])].filter(job=>m.method==="operations.snapshot"||job.mapId===m.mapId),schedulingIsReadOnly:true,
            supervisorState:"active",
            scheduleReceipts:opsJobCreated?[{
              job:opsJobId,map:one,slotUtc:"2026-11-07T11:30:00Z",
              observedUtc:"2026-11-07T11:30:01Z",
              status:"succeeded",code:"read-only-complete"
            }]:[],
            remoteCommandsAllowed:false,
            evidence:"LOCAL_OS_PROCESS_AND_SAVED_SCHEDULE_READ_ONLY"};
        }
        case "lan.status": return {
          originalStorePair:{
            bothOriginalProfilesPresent:false,
            sameSharedSession:false,sameSharedPath:false,distinctDeviceIds:false,
            bothHaveFiveActiveMaps:false,copiedMetadataPairMatches:false,
            bothPhysicalWitnessesFresh:false,samePhysicalTransferDirectory:false,
            bothHistoricalLocalSaveWitnessesFresh:false,
            bothHaveFiveRecentHistoricalLocalSaveReceipts:false,
            liveWriterCertified:false,characterTravelVerified:false,
            statusCode:"STORE_PEER_NOT_FRESH"
          },
          hostName:state.local.hostName,
          localHostId:"00000000-0000-4000-8000-000000000001",
          expectedPeerHostId:lanPeers[0]?.hostId||"",
          listenPort:lanListen,peerAddress:lanPeers[0]?.address||"",peerPort:lanPeers[0]?.port||0,
          pairingKeyConfigured:lanPeers.some(x=>x.keyConfigured),remoteFresh:false,
          peers:lanPeers.map(x=>({...x,online:false,lastSuccessUtc:null,stateCode:"UM-PEER-UNAVAILABLE",mapCount:0})),
          remoteState:"Nem érhető el friss, hitelesített peer-állapot",
          secretReturned:false,peerWritesAllowed:false,
          evidence:"DPAPI_LOCAL_LAN_SETTINGS_READ_ONLY"
        };
        case "lan.historicalSaveProbe":
          return {localHistoricalReceiptsReadOnly:true,
            observedUtc:"2026-11-09T10:00:00Z",
            recentCompletedMapReceipts:4,allFiveRecentLocalReceipts:false,
            crossHostSaveCompleteCertified:false,
            sharedStoreWriterCertified:false,productionRestoreAllowed:false,
            originalStoreFilesWritten:false,rconUsed:false,
            evidence:"ON_DEMAND_MANAGER_LOCAL_HISTORICAL_RECEIPTS_NO_WRITER_QUORUM"};
        case "lan.physicalStoreProbe":
          localPhysicalProbeRequested=true;
          return {localPhysicalDirectoryIdVerified:true,
            localHostId:"00000000-0000-4000-8000-000000000001",
            observedUtc:"2026-11-09T10:00:00Z",
            originalMetadataUnchanged:true,rawUncPathReturned:false,
            originalStoreFilesWritten:false,
            liveStoreWriterCertified:false,productionRestoreAllowed:false,
            evidence:"EXPLICIT_READ_ONLY_WINDOWS_UNC_FILE_ID_LOCAL_ONLY"};
        case "lan.preview":
          if(m.peerAction==="remove"){
            if(!lanPeers.some(x=>x.hostId.toLowerCase()===m.hostId?.toLowerCase()))
              throw new Error("UM-LAN-PEER-NOT-FOUND");
            if(lanPeers.length===1&&m.listenPort!==0)
              throw new Error("UM-LAN-LAST-PEER-LISTENER-MUST-DISABLE");
          }else if((m.peerAddress!=="192.168.1.200"&&m.peerAddress!=="10.44.1.23") ||
             !m.hostName || !m.hostId || (m.pairingKey||"").length<24)
            throw new Error("UM-LAN-INVALID-PRIVATE-PEER");
          lanApproval="00000000-0000-4000-8000-000000000955";
          lanPending={action:m.peerAction==="remove"?"remove":"upsert",listenPort:m.listenPort||0,
            hostId:m.hostId||"",hostName:m.hostName||"",address:m.peerAddress||"",port:m.peerPort||0};
          return {token:lanApproval,action:lanPending.action,listenPort:lanPending.listenPort,
            hostId:lanPending.hostId,hostName:lanPending.hostName,
            peerAddress:lanPending.address,peerPort:lanPending.port,
            pairingKeyWillChange:true,requiresManagerRestart:true,
            remoteWritesAllowed:false,
            evidence:"LOCAL_LAN_PAIRING_PREFLIGHT_NO_NETWORK_OR_FILE_WRITE"};
        case "lan.apply":
          if(m.token!==lanApproval||m.confirm!==true||!lanPending)
            throw new Error("UM-LAN-EXPLICIT-APPROVAL-REQUIRED");
          lanApproval="";
          lanListen=lanPending.listenPort;
          if(lanPending.action==="remove"){
            const index=lanPeers.findIndex(x=>x.hostId.toLowerCase()===lanPending!.hostId.toLowerCase());
            if(index>=0)lanPeers.splice(index,1);
          }else{
            const next={hostId:lanPending.hostId,hostName:lanPending.hostName,address:lanPending.address,
              port:lanPending.port,keyConfigured:true};
            const index=lanPeers.findIndex(x=>x.hostId.toLowerCase()===next.hostId.toLowerCase());
            if(index<0)lanPeers.push(next);else lanPeers[index]=next;
          }
          const applied=lanPending;lanPending=null;
          return {saved:true,action:applied.action,listenPort:lanListen,
            hostId:applied.hostId,peerCount:lanPeers.length,
            requiresManagerRestart:true,secretReturned:false,
            remoteWritesAllowed:false,
            evidence:"ATOMIC_LOCAL_LAN_PAIRING_DPAPI_PROFILE_ONLY"};
        case "freshStore.enrollmentStatus": return {
          mapId:m.mapId,mapName:m.mapId===two?"Astraeos":"The Island",
          enrolled:freshStoreEnrolled,historicalProtected:false,
          clusterId:freshStoreEnrolled?"fixture-beta":"",
          startsServer:false,originalStoreTouched:false,
          evidence:"LOCAL_MAP_NEW_STORE_ENROLLMENT_STATUS"
        };
        case "freshStore.enrollmentPreview": {
          if(m.mapId!==two||m.clusterId!=="fixture-beta"||freshStoreEnrolled)
            throw new Error("UM-FRESH-STORE-PREFLIGHT-OR-ENROLLMENT-FAILED");
          freshStoreApprovalIssued=true;
          return {token:freshStoreToken,mapId:two,mapName:"Astraeos",
            clusterId:"fixture-beta",gamePort:7847,queryPort:27047,
            steamBuild:"999001",transferIdentityCheckedLocally:true,
            remoteIdentityCertified:false,conversionPerformed:false,
            serverStarted:false,originalStoreTouched:false,
            automaticRetry:false,evidence:"NEW_EMPTY_STORE_ENROLLMENT_PREVIEW_ONLY"};
        }
        case "freshStore.enrollmentApply": {
          if(!freshStoreApprovalIssued||m.token!==freshStoreToken||
             m.confirm!==true||m.typedApproval!=="NEW EMPTY STORE")
            throw new Error("UM-FRESH-STORE-EXPLICIT-APPROVAL");
          freshStoreApprovalIssued=false;freshStoreEnrolled=true;
          Object.assign(state.local.maps[1],{
            storeProtected:true,freshStoreEnrolled:true,running:false,
            status:"Leállítva",storeLifecycleState:null});
          return {mapId:two,mapName:"Astraeos",enrolled:true,
            clusterId:"fixture-beta",conversionPerformed:false,serverStarted:false,
            originalStoreTouched:false,requiresManualStart:true,
            evidence:"NEW_EMPTY_STORE_ATOMIC_LOCAL_MANAGER_PROFILE_ONLY"};
        }
        case "freshStore.loaderModeStatus": {
          if(m.mapId!==two||!freshStoreEnrolled)
            throw new Error("UM-FRESH-STORE-API-NOT-ENROLLED");
          return {mapId:two,mapName:"Astraeos",useApi:freshStoreLoaderEnabled,
            mode:freshStoreLoaderEnabled?"asaApiLoader":"directAsa",
            confirmedStopped:true,canChangeMode:true,
            cacheState:freshStoreLoaderEnabled?"UM-API-CACHE-NO-LOCAL-KEY":"UM-API-CACHE-NOT-APPLICABLE",
            cacheRuntimeCertified:false,serverStarted:false,originalStoreTouched:false,
            evidence:"NEW_STORE_LOCAL_LAUNCH_MODE_READ_ONLY"};
        }
        case "freshStore.loaderModePreview": {
          if(m.mapId!==two||!freshStoreEnrolled||m.useApi===freshStoreLoaderEnabled)
            throw new Error("UM-FRESH-STORE-API-PROTECTED");
          freshStoreLoaderApprovalIssued=true;
          return {token:freshStoreLoaderToken,mapId:two,mapName:"Astraeos",
            useApi:m.useApi,mode:m.useApi?"asaApiLoader":"directAsa",
            cacheState:m.useApi?"UM-API-CACHE-NO-LOCAL-KEY":"UM-API-CACHE-NOT-APPLICABLE",
            cacheRuntimeCertified:false,startsServer:false,changesStore:false,
            requiresStoppedProcess:true,evidence:"NEW_STORE_LOCAL_API_MODE_PREVIEW_NO_WRITE"};
        }
        case "freshStore.loaderModeApply": {
          if(!freshStoreLoaderApprovalIssued||m.token!==freshStoreLoaderToken||
             m.confirm!==true||m.typedApproval!=="FRESH STORE API MODE")
            throw new Error("UM-FRESH-STORE-API-APPROVAL");
          freshStoreLoaderApprovalIssued=false;
          freshStoreLoaderEnabled=!freshStoreLoaderEnabled;
          return {mapId:two,mapName:"Astraeos",useApi:freshStoreLoaderEnabled,
            mode:freshStoreLoaderEnabled?"asaApiLoader":"directAsa",
            serverStarted:false,originalStoreTouched:false,requiresManualStart:true,
            evidence:"NEW_STORE_LOCAL_API_MODE_CONFIG_ONLY"};
        }
        case "cluster.registry.snapshot": return clusterRegistrySnapshot();
        case "cluster.historicalExpansionInspect": {
          const current=allFixtureMaps().find((item:any)=>
            item.map.id.toLowerCase()===String(m.mapId||"").toLowerCase());
          if(!current||current.hostId!==state.local.hostId)
            throw new Error("UM-EXPANSION-NOT-LOCAL");
          return {
            mapId:m.mapId,mapName:current.map.name,
            requestedClusterId:m.clusterId||"",sourceClusterId:"kovacluster",
            originalSourceVerified:true,originalFivePreserved:true,
            samePhysicalTransferDirectory:true,nativeLocalSetupVerified:false,
            localPreflightReady:false,sourceEvidenceTag:"A".repeat(64),
            blockers:["UM-EXPANSION-FRESH-ASA-SETUP-REQUIRED"],
            originalStoreFilesWritten:false,storeWriterTouched:false,
            originalTenMapIdentitiesChanged:false,remoteWriterSessionCertified:false,
            characterTravelVerified:false,newMapStoreEnrollmentAllowed:false,
            serverStarted:false,
            evidence:"READ_ONLY_ORIGINAL_STORE_EXPANSION_PREFLIGHT_NO_WRITER_AUTHORITY"
          };
        }
        case "cluster.create": {
          if(clusterClusters.some(cluster=>cluster.clusterId===m.clusterId))throw new Error("UM-CLUSTER-ALREADY-EXISTS");
          const created={clusterId:m.clusterId||"",name:m.name||"",transferDirectory:m.transferDirectory||"",
            backupDirectory:m.backupDirectory||"",createdUtc:"2026-11-08T00:00:00Z",updatedUtc:"2026-11-08T00:00:00Z"};
          clusterClusters=[...clusterClusters,created];clusterRevision++;
          return created;
        }
        case "cluster.update": {
          const index=clusterClusters.findIndex(cluster=>cluster.clusterId===m.clusterId);
          if(index<0)throw new Error("UM-CLUSTER-NOT-FOUND");
          const updated={...clusterClusters[index],name:m.name||"",transferDirectory:m.transferDirectory||"",
            backupDirectory:m.backupDirectory||"",updatedUtc:"2026-11-08T00:00:01Z"};
          clusterClusters=clusterClusters.map((cluster,i)=>i===index?updated:cluster);clusterRevision++;
          return updated;
        }
        case "cluster.membership.preview": {
          const clusterId=m.clusterId||"";
          if(!clusterClusters.some(cluster=>cluster.clusterId===clusterId))throw new Error("UM-CLUSTER-NOT-FOUND");
          const requested=[...new Set((m.mapIds||[]).map(id=>id.toLowerCase()))];
          const metadata=new Map(allFixtureMaps().map((entry:any)=>[entry.map.id.toLowerCase(),entry]));
          const change=(mapId:string,previous:string|null,target:string|null)=>{
            const entry=metadata.get(mapId.toLowerCase()) as any;
            return {mapId:entry.map.id,mapName:entry.map.name,hostId:entry.hostId,
              hostName:entry.hostName,previousClusterId:previous,targetClusterId:target};
          };
          const additions:any[]=[],moves:any[]=[],removals:any[]=[],errors:string[]=[];
          const requestedSet=new Set(requested);
          for(const assignment of clusterAssignments.filter(item=>item.clusterId===clusterId))
            if(!requestedSet.has(assignment.mapId.toLowerCase()))
              removals.push(change(assignment.mapId,clusterId,null));
          for(const mapId of requested){
            const entry=metadata.get(mapId) as any;
            if(!entry){errors.push("UNKNOWN_MAP:"+mapId);continue;}
            const previous=clusterAssignments.find(item=>item.mapId.toLowerCase()===mapId)?.clusterId??null;
            if(entry.map.storeProtected&&previous!==clusterId){errors.push("STORE_V1_PROTECTED:"+mapId);continue;}
            if(previous!==clusterId){
              const item=change(mapId,previous,clusterId);
              if(previous)moves.push(item);else additions.push(item);
            }
          }
          const preview={baseRevision:clusterRevision,clusterId,requestedMapIds:requested,
            additions,removals,moves,errors,isValid:errors.length===0};
          clusterApproval=preview.isValid?"fixture-cluster-approval":"";
          clusterPending=preview.isValid?{clusterId,mapIds:requested,preview}:null;
          return {token:clusterApproval||null,preview};
        }
        case "cluster.membership.apply": {
          if(m.token!==clusterApproval||m.confirm!==true||!clusterPending)
            throw new Error("UM-CLUSTER-APPROVAL-REQUIRED");
          const pending=clusterPending;
          const selected=new Set(pending.mapIds);
          clusterAssignments=clusterAssignments.filter(item=>
            item.clusterId!==pending.clusterId&&!selected.has(item.mapId.toLowerCase()));
          clusterAssignments.push(...pending.mapIds.map(mapId=>({mapId,clusterId:pending.clusterId})));
          clusterRevision++;clusterApproval="";clusterPending=null;
          return {clusterId:pending.clusterId,revision:clusterRevision,
            addedCount:pending.preview.additions.length,removedCount:pending.preview.removals.length,
            movedCount:pending.preview.moves.length,appliedUtc:"2026-11-08T00:00:02Z"};
        }
        case "dashboard.snapshot": return JSON.parse(JSON.stringify(state));
        case "ini.open": return {token:iniToken,mapId:one,file:m.file||"Game.ini",content:"[ServerSettings]\\nExample=1\\n",encoding:"utf-8",sha256:"AABBCC",bytes:28,evidence:"STOPPED_LOCAL_INI_SNAPSHOT"};
        case "ini.preview": return {previewToken:iniToken,changed:true,oldLines:3,newLines:3,bytes:28,originalSha256:"AABBCC",nextSha256:"FFEE11",evidence:"HASHED_IN_MEMORY_PREVIEW"};
        case "ini.save": return {saved:true,backupCreated:true,sha256:"FFEE11",evidence:"ATOMIC_LOCAL_INI_SAVE_ETAG_VERIFIED"};
        case "ini.close": return {closed:true};
        case "plugins.jsonOpen":
          if(m.plugin!=="ExamplePlugin")throw new Error("UM-PLUGIN-JSON-INVALID-NAME");
          return {token:pluginJsonToken,mapId:m.mapId,plugin:m.plugin,
            content:'{"enabled":true,"slot":1}',sha256:"AA".repeat(32),
            encoding:"utf-8",bytes:25,
            evidence:"STOPPED_OWNED_PLUGIN_JSON_SNAPSHOT"};
        case "plugins.jsonPreview":
          if(m.token!==pluginJsonToken ||
             String(m.content).includes('"enabled":true,"enabled":false'))
            throw new Error("UM-PLUGIN-JSON-SAFETY-CHECK-FAILED");
          return {previewToken:pluginJsonToken,changed:true,
            oldSha256:"AA".repeat(32),newSha256:"BB".repeat(32),
            oldLines:1,newLines:1,evidence:"VALIDATED_PLUGIN_JSON_PREVIEW_NO_WRITES"};
        case "plugins.jsonSave":
          if(m.token!==pluginJsonToken||m.confirm!==true||pluginJsonModified)
            throw new Error("UM-PLUGIN-JSON-APPROVAL-REQUIRED");
          pluginJsonModified=true;
          return {saved:true,backupCreated:true,newSha256:"BB".repeat(32),
            asaPluginReloaded:false,requiresRestart:true,storeTouched:false,
            sqlUsed:false,evidence:"ATOMIC_STOPPED_LOCAL_PLUGIN_JSON_WITH_PROTECTIVE_BAK"};
        case "plugins.jsonClose":return {closed:true};
        case "cluster.restoreLabPreview":
          restoreLabApproved=false;
          return {token:restoreLabToken,archiveName:"Untamed-ASA-Offline-synthetic",
            verified:false,requiresExplicitApproval:true,
            target:"FRESH_ISOLATED_TEMP_FOLDER_ONLY",sqlUsed:false,
            productionRestoreAllowed:false,evidence:"NATIVE_ARCHIVE_PICKER_NO_DISK_CHANGES"};
        case "cluster.restoreLabApply":
          if(m.token!==restoreLabToken||m.confirm!==true||restoreLabApproved)
            throw new Error("UM-ASARESTORE-APPROVAL-REQUIRED");
          restoreLabApproved=true;
          return {jobId:restoreLabJob,started:true,sqlUsed:false,
            productionRestoreAllowed:false,
            evidence:"ISOLATED_RESTORE_LAB_JOB_STARTED"};
        case "cluster.restoreLabStatus":
          if(m.jobId!==restoreLabJob||!restoreLabApproved)
            throw new Error("UM-ASARESTORE-JOB-UNKNOWN");
          return {jobId:restoreLabJob,state:"completed",stage:"LAB_FILE_HASHES_VERIFIED",
            labName:"Untamed-AsaRestore-Lab-synthetic",maps:10,files:12,
            changedUtc:"2026-11-07T00:00:00Z",
            gameplayRestoredVerified:false,storeWriterVerified:false,
            productionRestoreAllowed:false,sqlUsed:false,
            evidence:"ISOLATED_TEMP_RESTORE_FILE_INTEGRITY_ONLY"};
        case "cluster.offlinePreview":
          offlineArchiveApproved=false;
          return {token:offlineArchiveToken,hpCopies:5,ryzenCopies:5,
            transferMetadataPresent:true,configurationCopiesPresent:true,hpFolderName:"HP-offline",
            ryzenFolderName:"Ryzen-offline",transferFolderName:"Store-copy",
            destinationFolderName:"Test-artifacts",requiresExplicitApproval:true,
            originalStoreTouched:false,sqlUsed:false,
            liveSnapshotConsistencyCertified:false,
            evidence:"NATIVE_OFFLINE_FOLDER_SELECTION_NO_WRITES"};
        case "cluster.offlineApply":
          if(m.token!==offlineArchiveToken || m.confirm!==true || offlineArchiveApproved)
            throw new Error("UM-CLUSTER-ARCHIVE-APPROVAL-REQUIRED");
          offlineArchiveApproved=true;
          return {jobId:offlineArchiveJob,started:true,sqlUsed:false,
            productionRestoreAllowed:false,originalStoreTouched:false,
            evidence:"OFFLINE_ASA_ONLY_ARCHIVE_JOB_STARTED"};
        case "cluster.offlineStatus":
          if(m.jobId!==offlineArchiveJob || !offlineArchiveApproved)
            throw new Error("UM-CLUSTER-ARCHIVE-JOB-NOT-FOUND");
          return {jobId:offlineArchiveJob,state:"completed",code:"SHA256_VERIFIED",
            archiveName:"Untamed-ASA-Offline-synthetic",verifiedMaps:10,transferFiles:3,
            configurationFiles:20,
            changedUtc:"2026-11-06T22:00:00Z",liveConsistencyCertified:false,
            storeWriterCertified:false,productionRestoreAllowed:false,sqlUsed:false,
            evidence:"OFFLINE_FILE_INTEGRITY_ONLY_NO_LIVE_WRITER"};
        case "mods.profileExport":
          return {exported:true,mapId:m.mapId,profileName:"Astraeos-mods.json",
            enabled:2,passive:1,disabled:0,sha256:"AA".repeat(32),
            remoteWritesAllowed:false,storeTouched:false,sqlUsed:false,
            evidence:"MOD_IDS_ONLY_NATIVE_LOCAL_FILE_EXPORT"};
        case "mods.profileImportPreview":
          if(!Array.isArray(m.targets)||m.targets.length===0)
            throw new Error("UM-MODPROFILE-INVALID-TARGETS");
          portableProfileApplied=false;
          return {token:portableProfileToken,label:"Astraeos",sha256:"AA".repeat(32),
            mode:m.mode,enabled:2,passive:1,disabled:0,
            targets:m.targets.map((mapId:string)=>({mapId,mapName:"Astraeos",
              changed:true,before:{active:["928548"],passive:[],disabled:[]},
              after:{active:["928548","940975"],passive:["940975"],disabled:[]}})),
            operationCount:2,changed:true,productionLoadedVerified:false,
            remoteWritesAllowed:false,storeTouched:false,sqlUsed:false,
            evidence:"PORTABLE_PROFILE_IMPORT_PREVIEW_NO_WRITES"};
        case "mods.profileImportApply":
          if(m.token!==portableProfileToken||m.confirm!==true||portableProfileApplied)
            throw new Error("UM-MODPROFILE-APPROVAL-REQUIRED");
          portableProfileApplied=true;
          return {saved:true,changedMaps:[two],operationsApplied:2,
            requiresRestart:true,productionLoadedVerified:false,
            remoteWritesAllowed:false,storeTouched:false,sqlUsed:false,
            evidence:"LOCAL_ATOMIC_PROFILE_IMPORT_PENDING_RESTART"};
        case "mods.copyPreview":
          if(fixtureMode==="modLarge"){
            if(!m.sourceMapId||!Array.isArray(m.targets)||
               m.targets.length<1||m.targets.length>25||
               !m.targets.every((id:string)=>state.local.maps.some(map=>map.id===id)))
              throw new Error("UM-MOD-COPY-INVALID-TARGET");
            return {token:modCopyToken,sourceMapId:m.sourceMapId,mode:m.mode,
              targets:m.targets.map((id:string)=>{
                const entry=state.local.maps.find(map=>map.id===id)!;
                return {mapId:id,mapName:entry.name,changed:true,
                  before:{active:[],passive:[],disabled:[]},
                  after:{active:["123456"],passive:[],disabled:[]}};
              }),operationCount:m.targets.length,changed:true,requiresRestart:true,
              remoteWritesAllowed:false,storeWriterTouched:false,
              evidence:"LOCAL_MOD_COPY_PREVIEW_NO_WRITES"};
          }
          if(!m.sourceMapId || !Array.isArray(m.targets)||m.targets.length!==1 ||
             m.targets[0]!==two)throw new Error("UM-MOD-COPY-INVALID-TARGET");
          modCopyApproved=false;
          return {token:modCopyToken,sourceMapId:one,mode:m.mode,
            targets:[{mapId:two,mapName:"Astraeos",changed:true,
              before:{active:["928548"],passive:[],disabled:[]},
              after:{active:["928548","123456"],passive:["789012"],disabled:[]}}],
            operationCount:2,changed:true,requiresRestart:true,
            remoteWritesAllowed:false,storeWriterTouched:false,
            evidence:"LOCAL_MOD_COPY_PREVIEW_NO_WRITES"};
        case "mods.copyApply":
          if(m.token!==modCopyToken || m.confirm!==true || modCopyApproved)
            throw new Error("UM-MOD-COPY-APPROVAL-REQUIRED");
          modCopyApproved=true;
          return {saved:true,sourceMapId:one,mode:"merge",changedMaps:[two],
            operationsApplied:2,requiresRestart:true,modsLoadedVerified:false,
            remoteWritesAllowed:false,storeWriterTouched:false,
            evidence:"LOCAL_MANAGER_CONFIG_ATOMIC_BATCH_PENDING_RESTART"};
        case "mods.draftStart": stagedId="";return draftState();
        case "mods.draftStage": stagedId=String(m.modId||"");redoId="";return draftState();
        case "mods.draftUndo": redoId=stagedId;stagedId="";return draftState();
        case "mods.draftRedo": stagedId=redoId;redoId="";return draftState();
        case "mods.draftDiscard": stagedId="";return {discarded:true};
        case "mods.draftSave": return {saved:true,pending:true,active:["123456",stagedId],passive:["789012"],disabled:[],evidence:"CONFIGURED_ONLY_PENDING_REAL_RESTART"};
        case "curseforge.credentialStatus": return {configured:keySaved};
        case "curseforge.credentialSave":
          if(!m.credential || m.credential.length<3) throw new Error("invalid");
          keySaved = true; return {configured:true,verifiedNow:true};
        case "curseforge.credentialClear": keySaved=false;return {configured:false,verifiedNow:false};
        case "mods.sharedInventory": return {
          mapCount:state.local.maps.length,configuredMapCount:2,
          sharedProjectCount:1,unreadableMaps:[],
          projects:[
            {projectId:"928548",enabledMapCount:2,shared:true,
              assignments:[
                {mapId:one,mapName:"The Island",mode:"active",position:0,historicalProtected:false},
                {mapId:two,mapName:"Astraeos",mode:"passive",position:0,historicalProtected:false}
              ]},
            {projectId:"940975",enabledMapCount:1,shared:false,
              assignments:[
                {mapId:one,mapName:"The Island",mode:"active",position:1,historicalProtected:false}
              ]}
          ],
          evidence:"LOCAL_CONFIG_AND_QUARANTINED_ARCHIVE_METADATA_ONLY"
        };
        case "mods.configured": return {mapId: m.mapId, mapName: "The Island", pending: false, active: ["123456"], passive: ["789012"], disabled: [], evidence:"CONFIGURED_ONLY"};
        case "api.coreRollbackCandidates": return {
          candidates:apiCoreRolledBack?[]:[{
            transactionId:"11111111111141118111111111111111",
            releaseTag:"2.03",binaries:4
          }],
          restartsServer:false,evidence:"COMMITTED_OWNED_API_CORE_TRANSACTIONS_ONLY"
        };
        case "api.coreRollbackPreview": return {
          token:"00000000-0000-4000-8000-000000006666",
          mapId:m.mapId,mapName:"The Island",
          transactionId:m.transactionId,releaseTag:"2.03",binaries:4,
          approved:false,doesNotRestart:true,
          evidence:"STOPPED_OWNED_COMMITTED_ROLLBACK_REQUIRES_BYTE_REVALIDATION"
        };
        case "api.coreRollbackApply":
          if(!m.confirm||m.token!=="00000000-0000-4000-8000-000000006666")
            throw new Error("Rollback approval required");
          apiCoreRolledBack=true;
          return {
            mapId:one,transactionId:"11111111111141118111111111111111",
            filesRestored:1,filesRemoved:3,bytesVerified:true,serverRestarted:false,
            evidence:"STOPPED_OWNED_OFFICIAL_API_CORE_SHA256_ROLLBACK_ONLY"
          };
        case "plugins.zipSelect": return {
          token:"00000000-0000-4000-8000-000000005555",
          mapId:m.mapId,mapName:"The Island",archiveName:"SampleZip-package.zip",
          plugin:"SampleZip",binaryCount:1,skippedFiles:1,
          previousExists:false,evidence:"NATIVE_PLUGIN_ZIP_PREFLIGHT_NO_WRITE"
        };
        case "plugins.zipApply":
          if(!m.confirm)throw new Error("Approval required");
          pluginZipInstalled=true;
          return {mapId:one,plugin:"SampleZip",installedBinaries:1,
            backupCreated:false,requiresRestart:true,runtimeLoadedVerified:false,
            evidence:"VERIFIED_GUARDED_PLUGIN_ZIP_INSTALL"};
        case "servers.lifecycleJournal": return {
          mapId:m.mapId,mapName:m.mapId===two?"Astraeos":"The Island",
          journalExists:lifecycleInterrupted,
          phase:lifecycleInterrupted?"NeedsOperator":null,
          kind:lifecycleInterrupted?"Launch":null,
          changedUtc:lifecycleInterrupted?"2026-11-06T11:00:00Z":null,
          needsOperatorReview:lifecycleInterrupted,
          autoResumeAllowed:false,storeTouched:false,
          evidence:"DURABLE_INSTALLATION_OPERATION_JOURNAL_READ_ONLY"
        };
        case "servers.freshStartStatus": return {
          mapId:m.mapId,mapName:"Astraeos",running:freshStarted,confirmedStopped:!freshStarted,
          pid:freshStarted?45678:null,readyCertified:false,
          status:freshStarted?"Fut · szintetikus tesztfolyamat":"Leállítva",
          evidence:"LOCAL_OS_PROCESS_OBSERVATION_NOT_ASA_READINESS"
        };
        case "servers.freshStartPreview": return {
          token:"00000000-0000-4000-8000-000000009333",
          mapId:m.mapId,mapName:"Astraeos",steamBuild:"123456",
          gamePort:7847,queryPort:27085,rconConfigured:true,
          confirmedStopped:true,serverStarted:false,
          evidence:"FRESH_LOCAL_ASA_START_PREVIEW_NO_WRITE"
        };
        case "servers.freshReadiness": return {
          mapId:m.mapId,mapName:"Astraeos",stage:"RconAuthenticated",
          observedUtc:"2026-11-06T11:00:00Z",loaderObserved:false,
          apiInitialized:false,joinAdvertised:false,
          rconAuthenticated:true,productionReady:false,
          description:"Synthetic test only: RCON authenticated; game join unverified",
          storeTouched:false,
          evidence:"READ_ONLY_LOCAL_ASA_PROCESS_AND_RCON_READINESS"
        };
        case "servers.freshStartApply":
          if(!m.confirm)throw new Error("approval required");
          freshStarted=true;
          return {mapId:two,mapName:"Astraeos",pid:45678,
            createdUtc:"2026-11-06T10:01:00Z",startRequested:true,
            readyCertified:false,requiresRconReadiness:true,storeTouched:false,
            evidence:"LOCAL_REAL_ASA_PROCESS_STARTED_NOT_READY"};
        case "servers.restartCurrent": return {
          exists:restartInterrupted,belongsToMap:true,
          phase:restartInterrupted?"NeedsOperator":null,
          oldPid:restartInterrupted?44117:null,
          newPid:restartInterrupted?0:null,
          changedUtc:restartInterrupted?"2026-11-06T11:51:00Z":null,
          needsOperatorReview:restartInterrupted,
          autoResumeAllowed:false,
          evidence:"DURABLE_MACHINE_LOCAL_MANUAL_ASA_RESTART"
        };
        case "servers.restartPreview": return {
          token:"00000000-0000-4000-8000-000000009666",
          mapId:m.mapId,mapName:"The Island",currentPid:44117,
          saveWorldBeforeExit:true,verifiesOldProcessExit:true,
          autoRetry:false,killPerformed:false,storeTouched:false,
          evidence:"REAL_LOCAL_NONSTORE_RESTART_PREVIEW_NO_WRITES"
        };
        case "servers.restartApply":
          if(!m.confirm)throw new Error("Approval required");
          return {jobId:"00000000-0000-4000-8000-000000009777",
            started:true,autoRetry:false,killPerformed:false,
            storeTouched:false,evidence:"REAL_LOCAL_ASA_RESTART_JOB_QUEUED"};
        case "servers.restartStatus": return {
          jobId:"00000000-0000-4000-8000-000000009777",
          state:"completed",stage:"New Windows process started; readiness unverified",
          oldPid:44117,newPid:44118,changedUtc:"2026-11-06T11:30:00Z",
          saveCompleteProven:false,readinessCertified:false,
          killPerformed:false,storeTouched:false,autoRetry:false
        };
        case "servers.ownedKillPreview": {
          const entry=state.local.maps.find(x=>x.id===m.mapId);
          if(!entry||!entry.running||(entry as any).storeProtected)
            throw new Error("UM-KILL-NOT-OWNED-OR-STORE-PROTECTED");
          return {token:"00000000-0000-4000-8000-000000009222",
            mapId:entry.id,mapName:entry.name,pid:44117,
            saveWorldPerformed:false,processOnly:true,serverDataLossPossible:true,
            originalStoreTouched:false,remoteHostTouched:false,requiresTypedApproval:true,
            evidence:"EXACT_LOCAL_ASA_PID_OS_IDENTITY_RCON_KILL_PREFLIGHT"};
        }
        case "servers.ownedKillApply": {
          if(!m.confirm||m.typedApproval!=="KILL LOCAL ASA"||
             m.token!=="00000000-0000-4000-8000-000000009222")
            throw new Error("UM-KILL-TYPED-APPROVAL-REQUIRED");
          state.local.maps[0].running=false;
          state.local.maps[0].status="Leállítva";
          return {mapId:one,pid:44117,confirmedStopped:true,
            saveWorldPerformed:false,serverDataLossPossible:true,
            serverRestarted:false,originalStoreTouched:false,
            remoteHostTouched:false,evidence:"EXPLICIT_LOCAL_NONSTORE_OS_KILL_WITH_CONFIRMED_EXIT"};
        }
        case "servers.gracefulStopPreview": return {
          token:"00000000-0000-4000-8000-000000009444",
          mapId:m.mapId,mapName:"The Island",pid:44117,
          usesSaveWorld:true,usesDoExit:true,killPermitted:false,
          autoRestart:false,evidence:"REAL_LOCAL_GRACEFUL_STOP_PREVIEW_NO_DISPATCH"
        };
        case "servers.gracefulStopApply":
          if(!m.confirm)throw new Error("Approval required");
          return {jobId:"00000000-0000-4000-8000-000000009555",
            started:true,killPerformed:false,autoRestart:false,
            evidence:"REAL_LOCAL_GRACEFUL_STOP_JOB_QUEUED"};
        case "servers.gracefulStopStatus": return {
          jobId:"00000000-0000-4000-8000-000000009555",
          state:"completed",step:"Windows process verified stopped",
          changedUtc:"2026-11-06T11:00:00Z",confirmedStopped:true,
          saveCompleteProven:false,killPerformed:false,
          restartPerformed:false,storeTouched:false
        };
        case "steam.stoppedDiagnostics": {
          const entry=state.local.maps.find(x=>x.id===m.mapId);
          if(!entry)throw new Error("UM-STEAM-MAP");
          return {mapId:entry.id,mapName:entry.name,
            installRoot:"D:\\ASA\\Servers\\"+entry.name,
            processes:[{mapName:entry.name,confirmedStopped:!entry.running,
              running:entry.running,pid:entry.running?44117:null,
              loaderPid:null,reason:entry.running?"Native ASA PID active":"Verified stopped"}],
            overlaps:[],steamCmdFound:true,steamCmdConfigured:true,
            availableBytes:53687091200,requiredBytes:10737418240,
            journalBlocked:steamInterrupted,historicalBlocked:Boolean((entry as any).storeProtected),
            safeInstallation:!entry.running&&!(entry as any).storeProtected,
            canPreview:!entry.running&&!steamInterrupted&&!(entry as any).storeProtected,
            noProcessAutoStop:true,remoteWrites:false,
            evidence:"REAL_STOPPED_MAP_STEAM_DIAGNOSTICS_READ_ONLY"};
        }
        case "steam.batchCurrent": return {
          activeJob:null,journalExists:false,journalPhase:null,
          journalNextIndex:null,journalTotal:null,needsOperatorReview:false,
          automaticRetry:false,remoteWritesAllowed:false
        };
        case "steam.batchPreview": {
          if(!Array.isArray(m.mapIds)||m.mapIds.length<1||
             m.mapIds.length>25||new Set(m.mapIds).size!==m.mapIds.length)
            throw new Error("UM-STEAM-BATCH-INVALID-MAPS");
          const local=m.mapIds.map((id:string)=>
            state.local.maps.find(x=>x.id===id&&!x.running&&!x.storeProtected));
          if(local.some((x:any)=>!x))throw new Error("UM-STEAM-BATCH-PROTECTED-OR-REMOTE");
          return {token:"00000000-0000-4000-8000-000000008880",
            requestedMaps:local.length,installations:local.length,
            details:local.map((x:any)=>({
              mapId:x.id,mapName:x.name,installedBuild:"123456",
              sharedMapCount:1,availableBytes:30000000000,requiredReserveBytes:10000000000
            })),
            requiresTypedApproval:true,serverStopRequested:false,
            storeWriterTouched:false,remoteHostTouched:false,
            evidence:"LOCAL_STOPPED_STEAM_SERIAL_BATCH_PREFLIGHT_NO_WRITES"};
        }
        case "steam.batchApply":
          if(m.token!=="00000000-0000-4000-8000-000000008880" ||
             m.confirm!==true||m.typedApproval!=="HELYI SOROS STEAMCMD"||
             steamBatchStarted)throw new Error("UM-STEAM-BATCH-CONFIRMATION-REQUIRED");
          steamBatchStarted=true;steamBatchApplyCount++;
          return {jobId:"00000000-0000-4000-8000-000000008881",
            started:true,serverStopRequested:false,remoteHostTouched:false,
            storeWriterTouched:false,automaticRetry:false,
            evidence:"EXPLICIT_LOCAL_STOPPED_SERIAL_STEAM_BATCH_STARTED"};
        case "steam.batchStatus":
          if(!steamBatchStarted)throw new Error("UM-STEAM-BATCH-JOB-MISSING");
          return {jobId:"00000000-0000-4000-8000-000000008881",
            state:"completed",stage:"verified",position:1,total:1,
            activeMapName:"Local Map 60",completedInstallations:1,
            changedUtc:"2026-11-10T09:30:00Z",
            serverStopRequested:false,remoteHostTouched:false,
            storeWriterTouched:false,automaticRetry:false};
        case "steam.publishedBuild": return {
          mapId:m.mapId,mapName:"Astraeos",
          installedBuild:"123456",availableBuild:"123457",
          state:"UpdateAvailable",checkedUtc:"2026-11-10T09:00:00Z",
          explanation:"Újabb Steam-frissítés elérhető",
          updateAvailable:true,downloadStarted:false,serverStopped:false,
          storeWriterTouched:false,remoteHostTouched:false,
          evidence:"STEAM_PUBLIC_BRANCH_APPINFO_READ_ONLY_NO_UPDATE"
        };
        case "steam.stoppedPreview": return {
          token:"00000000-0000-4000-8000-000000006666",mapId:m.mapId,
          mapName:"Astraeos",maps:["Astraeos"],installedBuild:"123456",
          reserveBytes:10737418240,availableBytes:53687091200,
          evidence:"REAL_LOCAL_STOPPED_STEAM_PREFLIGHT_NO_WRITE"
        };
        case "steam.stoppedApply":
          if(!m.confirm)throw new Error("Approval required");
          steamUpdateStarted=true;
          return {jobId:"00000000-0000-4000-8000-000000007777",started:true,
            restartPerformed:false,remoteHostTouched:false,
            evidence:"REAL_LOCAL_STOPPED_STEAM_UPDATE_STARTED"};
        case "steam.stoppedCurrent": return new URLSearchParams(window.location.search)
          .get("fixture")==="steamReattach"?{
            exists:false,needsOperatorReview:false,autoResumeAllowed:false,
            activeJob:{jobId:"00000000-0000-4000-8000-000000007777",
              state:"running",stage:"steamcmd",changedUtc:"2026-11-10T08:40:00Z",
              oldBuild:"123456",newBuild:"",protectedFilesIntact:false,
              restartPerformed:false,storeTouched:false}
          }:steamInterrupted?{
          exists:true,queueId:"00000000-0000-4000-8000-000000008888",
          phase:"NeedsOperator",activeMapName:"Astraeos",
          nextIndex:0,total:1,changedUtc:"2026-11-06T09:10:00Z",
          summary:"Interrupted fixture",needsOperatorReview:true,
          autoResumeAllowed:false,hostName:"LAB-WIN-01",
          evidence:"DURABLE_MACHINE_LOCAL_STEAM_QUEUE_JOURNAL"
        }:{exists:false,needsOperatorReview:false,autoResumeAllowed:false};
        case "steam.stoppedStatus":
          if(new URLSearchParams(window.location.search).get("fixture")==="steamReattach")
            return {jobId:"00000000-0000-4000-8000-000000007777",
              state:"completed",stage:"verified",changedUtc:"2026-11-10T08:42:00Z",
              oldBuild:"123456",newBuild:"123457",protectedFilesIntact:true,
              restartPerformed:false,storeTouched:false};
          return {jobId:"00000000-0000-4000-8000-000000007777",
            state:steamUpdateStarted?"completed":"queued",
            stage:steamUpdateStarted?"verified":"preflight",
            changedUtc:"2026-11-06T10:00:00Z",
            oldBuild:"123456",newBuild:"123457",
            protectedFilesIntact:steamUpdateStarted,
            restartPerformed:false,storeTouched:false};
        case "plugins.state": return {
          mapId:m.mapId,mapName:"The Island",historicalProtected:false,
          plugins:[{name:"ExamplePlugin",enabled:pluginEnabled,runtimeLoadedVerified:false}],
          evidence:"LOCAL_PLUGIN_DISK_STATE_ONLY"
        };
        case "plugins.togglePreview": return {
          allowed:true,token:"00000000-0000-4000-8000-000000002222",
          plugin:m.plugin??"ExamplePlugin",targetEnabled:m.enabled??false,
          checkedLocalMaps:2,reason:"Minden megosztott telepítésű helyi map igazoltan leállítva.",
          requiresRestart:true,productionRuntimeVerified:false,
          evidence:"LOCAL_PLUGIN_TOGGLE_PREFLIGHT_NO_WRITES"
        };
        case "plugins.toggleApply":
          pluginEnabled=!pluginEnabled;
          return {mapId:one,plugin:"ExamplePlugin",enabled:pluginEnabled,
            backupCreated:true,requiresRestart:true,runtimeLoadedVerified:false,
            evidence:"VERIFIED_LOCAL_PLUGIN_DIRECTORY_TOGGLE"};
        case "plugins.inventory": return {mapId:m.mapId,mapName:"The Island",evidence:"READ_ONLY_LOCAL_FILE_INVENTORY",plugins:[{name:"ExamplePlugin",protectedData:false,binaries:[{name:"ExamplePlugin.dll",version:"1.0",bytes:1234}]}]};
        case "backups.summary": return {evidence:"FILE_INTEGRITY_ONLY_NOT_ASA_SAVE_COMPLETE",maps:[{mapId:one,mapName:"The Island",validPackages:1,invalidPackages:0,verifiedBytes:1250000,latestVerifiedUtc:"2026-11-05T12:00:00Z"}]};
        case "steam.installations": return {evidence:"READ_ONLY_STEAM_INSTALLATION_PREFLIGHT",installations:[{id:0,maps:["The Island","Astraeos"],executablePresent:true,installedSteamBuild:"123456",updateBlocked:true,reason:"Read-only test fixture: upgrade blocked"}]};
        case "servers.suggestPorts": return {game:7797,query:27035,rcon:37035,evidence:"PROFILE_ONLY_NO_NETWORK_PROBE"};
        case "logs.read": return {mapId:m.mapId,mapName:"The Island",lastModifiedUtc:"2026-11-05T12:00:00Z",totalBytes:1900,truncated:false,evidence:"SANITIZED_LOCAL_LOG_TAIL_NOT_SERVER_HEALTH",lines:[{number:1,level:"info",content:"Synthetic ASA startup line"},{number:2,level:"error",content:"Synthetic failure detected"}]};
        case "curseforge.categories": return {
          evidence:"OFFICIAL_ASA_CATEGORY_METADATA",
          categories:[{id:101,name:"Structures",isClass:false,parentCategoryId:0},{id:102,name:"Creatures",isClass:false,parentCategoryId:0}]
        };
        case "curseforge.featured": return {
          evidence:"OFFICIAL_ASA_FEATURED_METADATA",gameId:1234,
          featured:[{id:123456,name:"Featured Mod",summary:"Synthetic featured mod",website:null,
            thumbnailUrl:null,authors:["Lab"],categories:["Structures"],screenshots:[],isAvailable:true,
            latestFile:"v1.0",modifiedUtc:null,downloadCount:650}],
          popular:[],recentlyUpdated:[]
        };
        case "curseforge.summary": return {
          evidence:"OFFICIAL_ASA_PROJECT_SUMMARY_ONLY",
          mod:{id:m.modId||123456,
            name:m.modId===789012?"Featured Mod":"Example Search Result",
            summary:"Synthetic official metadata only",
            website:null,thumbnailUrl:null,authors:["Lab"],
            categories:["Structures"],screenshots:[],isAvailable:true,
            latestFile:"v2.0",modifiedUtc:null,downloadCount:1000}
        };
        case "curseforge.project": return {
          evidence:"OFFICIAL_ASA_PROJECT_METADATA_ONLY",
          mod:{id:m.modId||123456,name:(m.modId===123456?"Example Search Result":"Featured Mod"),
            summary:"Synthetic detailed description",website:null,thumbnailUrl:null,authors:["Lab"],
            categories:["Structures"],screenshots:[],isAvailable:true,latestFile:"v2.0",
            modifiedUtc:null,downloadCount:1000},
          description:"Synthetic provider description, inert plain text",
          files:[{fileId:11111,name:"Example provider file",published:"2026-11-01T10:00:00Z",
            available:true,sizeBytes:125000,gameVersions:["ASA"],
            dependencies:[{modId:456789,type:"RequiredDependency"}],
            distributionState:"File available"}],
          filesTotal:30,filesHasMore:true
        };
        case "curseforge.files": return {
          evidence:"OFFICIAL_ASA_FILE_METADATA_ONLY",modId:m.modId||123456,
          page:m.page||0,hasMore:false,totalCount:30,
          files:[{fileId:22222,name:"Example older version",published:null,
            available:true,sizeBytes:200000,gameVersions:["ASA"],dependencies:[],distributionState:"File available"}]
        };
        case "curseforge.browse": return {
          gameId: 1234,totalCount: 25,page:m.page||0,hasMore:false,evidence:"OFFICIAL_CATALOG_METADATA_ONLY",
          mods:[{id:123456,name:"Example Search Result",summary:"Synthetic catalog response for UI test",website:null,thumbnailUrl:null,authors:["Lab"],categories:["Structures"],screenshots:[],isAvailable:true,latestFile:null,modifiedUtc:null,downloadCount:15}]
        };
      }
      throw new Error("Denied");
    };
    Object.assign(window, {
      __UNTAMED_TEST_FIXTURE__: true,
      chrome: { webview: {
        addEventListener: (_name: string, fn: Handler) => { handlers.push(fn); },
        postMessage: (msg: {id:string;method:string}) => {
          queueMicrotask(() => {
            try {
              const data = answers(msg);
              handlers.forEach(fn=>fn({ data: {id:msg.id,ok:true,data} }));
            } catch(e) {
              // Preserve the concrete synthetic installer error code, just as
              // the native C# reply does; other mocked denials remain generic.
              const error=e instanceof Error&&
                e.message.startsWith("UM-FRESH-ROOT-OVERLAP|")
                ?e.message:"UM-METHOD-DENIED";
              handlers.forEach(fn=>fn({ data: {id:msg.id,ok:false,error} }));
            }
          });
        }
      }}
    });
  }, { one: first, two: second });
});

test("native Operations Center displays local PID RAM uptime and saved schedules only",async({page})=>{
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.getByRole("button",{name:"Üzemeltetés",exact:true}).click();
  await expect(page.getByRole("heading",{name:"Üzemeltetés"})).toBeVisible();
  await expect(page.locator(".ops-compact-table tbody tr")).toHaveCount(2);
  await expect(page.locator(".ops-compact-table tbody tr").first()).toContainText("12345");
  await expect(page.locator(".ops-compact-table tbody tr").first()).toContainText(/9\\s?234,5 MiB|9234,5 MiB/);
  await expect(page.locator(".ops-compact-table tbody tr").last()).toContainText("Nem mérhető");
  await expect(page.locator(".ops-schedule-table")).toHaveCount(0);
  await expect(page.locator(".ops-compact-table")).toContainText("Nincs beállítva");
  await expect(page.locator(".ops-center").getByText("Ragnarok")).toHaveCount(0);
  await page.locator(".ops-compact-table tbody tr").first().getByRole("button",{name:"Map megnyitása"}).click();
  await expect(page.locator(".map-detail-hero")).toContainText("The Island");
  await page.getByRole("tab",{name:"Üzemeltetés"}).click();
  await expect(page.locator(".ops-map")).toHaveCount(1);
  await expect(page.locator(".ops-map").first()).toContainText("12345");
  await expect(page.locator(".ops-schedules")).toHaveCount(0);
});

test("read-only operations scheduler requires approval, shows durable receipt and can be disabled",async({page})=>{
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").first().click();
  await page.getByRole("tab",{name:"Ütemező"}).click();
  const add=page.getByRole("button",{name:"Ellenőrzés bekapcsolása"});
  await expect(page.getByRole("combobox",{name:"Ütemezett ellenőrzés mapja"})).toHaveCount(0);
  await expect(page.locator(".ops-scheduler-map")).toContainText("The Island");
  await expect(add).toBeDisabled();
  await page.getByRole("combobox",{name:"Csak olvasó ellenőrzés típusa"}).selectOption("Observation");
  await page.getByRole("spinbutton",{name:"Ellenőrzési időköz percben"}).fill("5");
  await expect(add).toBeDisabled();
  await page.getByRole("checkbox",{name:/Jóváhagyom a helyi, csak olvasó/}).check();
  await add.click();
  await expect(page.getByText(/Mentve: a helyi, csak olvasó ellenőrzés/)).toBeVisible();
  await expect(page.locator(".ops-schedule-table").first().locator("tbody tr")).toHaveCount(2);
  await expect(page.getByText("read-only-complete")).toBeVisible();
  await expect(page.locator(".ops-schedules").getByRole("button",{name:"Kikapcsolás"})).toBeDisabled();
  await page.getByRole("checkbox",{name:/Jóváhagyom a helyi, csak olvasó/}).check();
  await page.locator(".ops-schedules").getByRole("button",{name:"Kikapcsolás"}).click();
  await expect(page.getByText("Az automatikus helyi ellenőrzés kikapcsolva.")).toBeVisible();
  await expect(page.locator(".ops-schedules").getByRole("button",{name:"Kikapcsolás"})).toHaveCount(0);
  await expect(page.locator(".ops-schedule-table").first().locator("tbody tr")).toHaveCount(2);
  await expect(page.locator(".ops-schedules")).not.toContainText("Szerver automatikus újraindítása");
});

test("map-only scheduling never exposes another local map's tasks",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("tab",{name:"Ütemező"}).click();
  await expect(page.locator(".ops-scheduler-map")).toContainText("Astraeos");
  await expect(page.locator(".ops-schedule-table").first().locator("tbody tr")).toHaveCount(0);
  await expect(page.getByRole("combobox",{name:"Ütemezett ellenőrzés mapja"})).toHaveCount(0);
  await expect(page.getByRole("button",{name:"Ellenőrzés bekapcsolása"})).toBeDisabled();
});

test("real native per-map SaveWorld and Restart schedule controls require opt-in",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("tab",{name:"Ütemező"}).click();
  const panel=page.locator(".native-schedule-panel");
  await expect(panel.getByText("Maponkénti szerverütemező")).toBeVisible();
  await panel.getByText("Végrehajtási napló (0 bejegyzés)").click();
  await expect(panel.getByText("Még nincs végrehajtási esemény ezen a mapon.")).toBeVisible();
  const save=panel.getByRole("region",{name:"SaveWorld ütemezése"});
  const restart=panel.getByRole("region",{name:"Újraindítás ütemezése"});
  await expect(save.getByRole("button",{name:"Ütemezés mentése"})).toBeDisabled();
  await save.getByRole("checkbox",{name:/Kifejezetten jóváhagyom/}).check();
  await save.getByRole("button",{name:"Ütemezés mentése"}).click();
  await expect(save.getByText("Bekapcsolva")).toBeVisible();
  await expect(restart.getByRole("button",{name:"Ütemezés mentése"})).toBeDisabled();
  await restart.getByRole("checkbox",{name:/Kifejezetten jóváhagyom/}).check();
  await restart.getByRole("button",{name:"Ütemezés mentése"}).click();
  await expect(restart.getByText("Bekapcsolva")).toBeVisible();
  await expect(panel.getByText(/Automatikus SteamCMD-frissítés még nem engedélyezett/)).toBeVisible();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await page.getByLabel("Fő navigáció").getByRole("button",{name:"Üzemeltetés",exact:true}).click();
  const row=page.locator(".ops-compact-table tbody tr").nth(1);
  await expect(row).toContainText("Astraeos");
  await expect(row).toContainText(/SaveWorld|Újraindítás/);
  await expect(page.locator(".ops-summary")).toContainText("Natív ASA feladat");
});

test("original Store physical UNC probe is explicit and never grants transfer or restore rights",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:/Be.*ll/}).click();
  const proof=page.getByTestId("lan-store-proof");
  await expect(proof).toContainText("Kétoldali ellenőrzés szükséges");
  await proof.getByRole("button",{
    name:"Helyi Store transferkönyvtár fizikai ellenőrzése"
  }).click();
  await expect(page.locator(".lan-pairing-panel")).toContainText(
    "Helyi fizikai UNC fájlazonosság ellenőrizve");
  await expect(proof).toContainText("Kétoldali ellenőrzés szükséges");
  await expect(proof).toContainText("Ez továbbra sem Store-írójog");
  await expect(proof).not.toContainText("Éles restore engedélyezve");
});

test("original Store five-map SaveComplete receipts are explicitly local and never certify a two-host writer",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:/Be.*ll/}).click();
  const proof=page.getByTestId("lan-store-proof");
  await expect(proof).toContainText("Nincs kétoldali friss történeti bizonyíték");
  await proof.getByRole("button",{
    name:"Helyi 5-map SaveComplete-naplók ellenőrzése"
  }).click();
  await expect(page.locator(".lan-pairing-panel")).toContainText(
    "Helyi történeti SaveComplete naplók: 4/5");
  await expect(proof).toContainText("Nincs kétoldali friss történeti bizonyíték");
  await expect(proof).toContainText("valódi kétgépes Store-writer quiescence");
  await expect(proof).not.toContainText("Production restore engedélyezve");
});

test("N-host LAN pairing adds, lists and removes peers without exposing keys",async({page})=>{
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.getByRole("button",{name:/Be.*ll/}).click();
  await expect(page.getByRole("heading",{name:/LAN-p/})).toBeVisible();
  await expect(page.getByTestId("lan-store-proof")).toContainText("Store-session");
  const secretA="SYNTHETIC-ONLY-LAN-SHARED-KEY-36";
  await page.getByTestId("lan-listen-port").fill("39290");
  await page.getByTestId("lan-host-id").fill("00000000-0000-4000-8000-000000000002");
  await page.getByTestId("lan-host-name").fill("Synthetic Manager B");
  await page.getByTestId("lan-address").fill("192.168.1.200");
  await page.getByTestId("lan-peer-port").fill("39291");
  await page.getByTestId("lan-key").fill(secretA);
  await page.getByTestId("lan-preview-button").click();
  await expect(page.getByTestId("lan-preview")).toBeVisible();
  await expect(page.getByTestId("lan-key")).toHaveValue("");
  await page.getByTestId("lan-save-button").click();
  await expect(page.getByTestId("lan-peer-list")).toContainText("Synthetic Manager B");
  await expect(page.locator(".lan-pairing-panel")).not.toContainText(secretA);

  const secretB="SYNTHETIC-ONLY-LAN-SECOND-KEY-42";
  await page.getByTestId("lan-host-id").fill("00000000-0000-4000-8000-000000000003");
  await page.getByTestId("lan-host-name").fill("Synthetic Manager C");
  await page.getByTestId("lan-address").fill("10.44.1.23");
  await page.getByTestId("lan-peer-port").fill("39292");
  await page.getByTestId("lan-key").fill(secretB);
  await page.getByTestId("lan-preview-button").click();
  await page.getByTestId("lan-save-button").click();
  const peers=page.getByTestId("lan-peer-list");
  await expect(peers).toContainText("Synthetic Manager B");
  await expect(peers).toContainText("Synthetic Manager C");
  await expect(peers.locator(".lan-peer-row")).toHaveCount(2);
  await expect(page.locator(".lan-pairing-panel")).not.toContainText(secretB);

  await page.getByTestId("lan-action").selectOption("remove");
  await page.getByTestId("lan-peer-select").selectOption("00000000-0000-4000-8000-000000000002");
  await page.getByTestId("lan-preview-button").click();
  await expect(page.getByTestId("lan-preview")).toContainText(/Peer/);
  await page.getByTestId("lan-save-button").click();
  await expect(peers.locator(".lan-peer-row")).toHaveCount(1);
  await expect(peers).not.toContainText("Synthetic Manager B");
  await expect(peers).toContainText("Synthetic Manager C");
  await page.getByTestId("lan-action").selectOption("remove");
  await page.getByTestId("lan-peer-select").selectOption("00000000-0000-4000-8000-000000000003");
  await page.getByTestId("lan-preview-button").click();
  await expect(page.getByRole("alert")).toBeVisible();
  await expect(page.getByTestId("lan-preview")).toHaveCount(0);
  await page.getByTestId("lan-listen-port").fill("0");
  await page.getByTestId("lan-preview-button").click();
  await expect(page.getByTestId("lan-preview")).toBeVisible();
  await page.getByTestId("lan-save-button").click();
  await expect(page.getByTestId("lan-peer-list")).toHaveCount(0);
  await page.screenshot({path:"test-results/uk-n-host-lan-pairing-1366.png",fullPage:false});
});

test("N-host dashboard and map gallery render four hosts, 104 maps, per-host filters and stale peer state",async({page})=>{
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/?fixture=large");
  await expect(page.getByText("4 host · 2 online peer")).toBeVisible();
  await expect(page.getByText("Helyi + 3 LAN-peer")).toBeVisible();
  await expect(page.getByRole("table",{name:"Cluster szerverlista"}).getByRole("row")).toHaveCount(105);
  await page.getByRole("button",{name:"Kártyák",exact:true}).click();
  const gallery=page.getByRole("region",{name:"Szervergaléria"});
  const hostFilter=page.getByRole("combobox",{name:"Gazdagép szűrő"});
  await expect(hostFilter.locator("option")).toHaveCount(6);
  await hostFilter.selectOption("host:fixture-host-3");
  await expect(gallery.locator(".uk-gallery-tile")).toHaveCount(26);
  await expect(gallery.locator(".uk-host-map-group")).toContainText("LAB-NODE-3");
  await hostFilter.selectOption("remote");
  await expect(gallery.locator(".uk-gallery-tile.is-remote")).toHaveCount(78);
  await expect(gallery.getByText("ELAVULT / OFFLINE")).toBeVisible();
  await expect(gallery.locator(".uk-gallery-catalog")).toHaveCount(0);
});

test("multi-cluster membership filters 100+ maps, protects Store-v1 and applies moves only after preview approval",async({page})=>{
  await page.setViewportSize({width:1440,height:900});
  await page.goto("/?fixture=large");
  const navigation=page.getByRole("navigation",{name:"Fő navigáció"});
  await navigation.getByRole("button",{name:"Cluster",exact:true}).click();
  await expect(page.locator(".cluster-registry-metrics > div").nth(0)).toContainText("4");
  await expect(page.locator(".cluster-registry-metrics > div").nth(1)).toContainText("104");
  await expect(page.locator(".cluster-registry-map")).toHaveCount(104);
  const hostFilter=page.getByRole("combobox",{name:"Cluster gazdagép szűrő"});
  await hostFilter.selectOption("fixture-host-3");
  await expect(page.locator(".cluster-registry-map")).toHaveCount(26);
  await expect(page.locator(".cluster-registry-map-group legend")).toContainText("LAB-NODE-3");
  await hostFilter.selectOption("all");

  await page.getByRole("combobox",{name:"Kiválasztott cluster"}).selectOption("fixture-beta");
  await page.getByRole("textbox",{name:"Cluster map keresése"}).fill("The Island");
  const membershipCheckboxes=page.locator(".cluster-registry-map-groups");
  const protectedMap=membershipCheckboxes.getByRole("checkbox",{name:/The Island/});
  await expect(protectedMap).toBeDisabled();
  await expect(page.getByText("Kova Store-v1 védett")).toBeVisible();

  await page.getByRole("textbox",{name:"Cluster map keresése"}).fill("Local Map 4");
  const movedMap=membershipCheckboxes.getByRole("checkbox",{name:/Local Map 4/});
  await expect(movedMap).not.toBeChecked();
  await movedMap.check();
  await page.getByRole("button",{name:"Tagságváltozás előnézete"}).click();
  const preview=page.getByRole("region",{name:"Map-tagság előnézete"});
  await expect(preview).toContainText("1 áthelyezés");
  await page.getByRole("button",{name:"Előnézet jóváhagyása és mentése"}).click();
  await expect(page.getByRole("status")).toContainText("1 áthelyezés");
  await page.getByRole("combobox",{name:"Kiválasztott cluster"}).selectOption("fixture-alpha");
  await expect(membershipCheckboxes.getByRole("checkbox",{name:/Local Map 4/})).not.toBeChecked();
});

test("historical Kova new-map compatibility check is native, read-only and never enables writer or live travel",async({page})=>{
  await page.goto("/?fixture=large");
  await page.getByRole("navigation",{name:"Fő navigáció"})
    .getByRole("button",{name:"Cluster",exact:true}).click();
  const candidate=page.getByRole("combobox",{name:"Bővítendő helyi map"});
  expect(await candidate.locator("option").count()).toBeGreaterThan(1);
  await candidate.selectOption({label:"Local Map 4"});
  const revision=await page.locator(".cluster-registry-metrics > div").nth(3).locator("b").innerText();
  await page.getByRole("button",{name:"Eredeti cluster-kompatibilitás ellenőrzése"}).click();
  const inspected=page.getByRole("region",{name:"Történeti Store-map ellenőrzése"});
  await expect(inspected).toContainText("Eredeti Store-forrás: igazolt");
  await expect(inspected).toContainText("Eredeti 5 map: megőrizve");
  await expect(inspected).toContainText("Transfer fizikai azonossága: igazolt");
  await expect(inspected).toContainText("UM-EXPANSION-FRESH-ASA-SETUP-REQUIRED");
  await expect(inspected).toContainText("még nincs engedélyezve");
  await expect(inspected.getByRole("button",{name:/csatlakoztat|Store-profil mentése/i})).toHaveCount(0);
  await expect(page.locator(".cluster-registry-metrics > div").nth(3).locator("b")).toHaveText(revision);
});

test("cluster screen creates and edits a cluster through the native bridge contract",async({page})=>{
  await page.goto("/?fixture=large");
  await page.getByRole("navigation",{name:"Fő navigáció"})
    .getByRole("button",{name:"Cluster",exact:true}).click();
  await page.getByRole("button",{name:"Új cluster"}).click();
  await page.getByRole("textbox",{name:"Clusterazonosító"}).fill("fixture-delta");
  await page.getByRole("textbox",{name:"Megjelenített név"}).fill("Fixture Delta");
  await page.getByRole("textbox",{name:"Közös transferkönyvtár"}).fill("C:\\Synthetic\\Delta\\Transfer");
  await page.getByRole("textbox",{name:"Cluster-mentések könyvtára"}).fill("C:\\Synthetic\\Delta\\Backup");
  await page.getByRole("button",{name:"Cluster létrehozása"}).click();
  const selected=page.getByRole("combobox",{name:"Kiválasztott cluster"});
  await expect(selected).toHaveValue("fixture-delta");
  await page.getByRole("textbox",{name:"Megjelenített név"}).fill("Fixture Delta Updated");
  await page.getByRole("button",{name:"Cluster beállításainak mentése"}).click();
  await expect(selected.locator("option:checked")).toContainText("Fixture Delta Updated");
});

test("Cluster page owns character travel evidence without unlocking Store",async({page})=>{
  await page.goto("/");
  await expect(page.getByLabel("Fő navigáció").getByRole("button",{name:"Karakterutazás teszt"})).toHaveCount(0);
  await page.getByLabel("Fő navigáció").getByRole("button",{name:"Cluster",exact:true}).click();
  await expect(page.getByRole("heading",{name:"Karakterutazás tesztközpont"})).toBeVisible();
  const mandatory=page.getByRole("region",{name:"Referenciával igazolt, kötelező működés"});
  await expect(mandatory).toContainText("REFERENCIÁVAL IGAZOLT – NÁLUNK IS KÖTELEZŐEN MŰKÖDNIE KELL");
  await expect(mandatory).toContainText("Saját éles Store-v1 igazolás: még nincs");
  await expect(mandatory).toContainText("két map egy Windows gépen");
  const embedded=page.getByRole("region",{name:"Beágyazott Store-v1 ellenőrzése"});
  await embedded.getByRole("button",{name:"Offline .ark másolat megvizsgálása"}).click();
  await expect(embedded).toContainText("2 karakterindex-bejegyzés");
  await expect(embedded).toContainText("1 tribe-index-bejegyzés");
  await expect(embedded).toContainText("a Store-v1 írásvédelem változatlan követelmény");
  await embedded.getByRole("button",{name:"Teljes 10-map archívum Store-indexeinek vizsgálata"}).click();
  const inventory=embedded.getByRole("region",{name:"Tízmapos beágyazott Store-leltár"});
  await expect(inventory).toContainText("9 olvasható index");
  await expect(inventory).toContainText("1 további vizsgálatot igényel");
  await expect(inventory.locator("tbody tr")).toHaveCount(10);
  await expect(inventory).toContainText("EMBEDDED_INDEX_NOT_READABLE");
  await expect(inventory).toContainText("Nem igazol");
  await embedded.getByRole("button",{
    name:"Két teljes 10-map archívum változásainak összevetése"
  }).click();
  const changes=embedded.getByRole("region",{name:"Két offline clusterarchívum változásai"});
  await expect(changes).toContainText("Mindkét archívum SHA-256-tal ellenőrizve");
  await expect(changes).toContainText("1 hozzáadott transferfájl");
  await expect(changes).toContainText("1 változatlan nulla bájtos fájl");
  await expect(changes.locator("tbody tr")).toHaveCount(10);
  await expect(changes).toContainText("Ez fájlkülönbség-leltár, nem sikeres karakterutazás");
  await expect(changes).not.toContainText("old-zero.arkprofile");

  await page.getByRole("button",{name:"Helyi 5 map ellenőrzése"}).click();
  await expect(page.getByText("Az öt helyi map fájlleltára nyugalomban készült; ez nem karakterigazolás.")).toBeVisible();
  await expect(page.locator(".uk-travel-map-results>div")).toHaveCount(5);
  await expect(page.getByRole("textbox",{name:"Helyi mentéslenyomat JSON"})).not.toBeEmpty();
  await expect(page.getByText(/nem oldja fel a védett mapok indítási zárolását/)).toBeVisible();
  await page.screenshot({path:"test-results/untamed-store-travel-workbench-1600.png",fullPage:false});
});

test("Store A-B-A six snapshots require both return hosts and never unlock production",async({page})=>{
  await page.goto("/");
  await page.getByRole("navigation",{name:"Fő navigáció"})
    .getByRole("button",{name:"Cluster",exact:true}).click();
  const panel=page.getByRole("region",{name:"Hat pillanatképes oda-vissza utazás"});
  const action=panel.getByRole("button",{name:"Hat mentéslenyomat (A → B → A) ellenőrzése"});
  await expect(action).toBeDisabled();
  const survivor="a".repeat(64);
  const mkMap=(name:string,mapId:string,withProfile:boolean)=>({
    name,mapId,storeMode:false,confirmedStopped:true,worldPresent:false,
    worldBytes:0,saveDirectoryFound:true,ambiguousLayout:false,
    unstable:false,zeroByteDetected:false,
    files:withProfile?[{id:survivor,kind:"profile",bytes:1444,sha256:"b".repeat(64)}]:[]
  });
  const sourceId="00000000-0000-4000-8000-000000002001";
  const targetId="00000000-0000-4000-8000-000000002006";
  const namesA=["The Island","Astraeos","Valguero","Extinction","Genesis1"];
  const namesB=["Ragnarok","The Center","Aberration","Scorched","Lost Colony"];
  const snapshot=(host:"A"|"B",stage:number)=>{
    const names=host==="A"?namesA:namesB;
    const maps=names.map((name,i)=>{
      const mapId="00000000-0000-4000-8000-"+
        String(2001+i+(host==="B"?5:0)).padStart(12,"0");
      return mkMap(name,mapId,i===0&&
        (host==="A"?stage!==1:stage===1));
    });
    return JSON.stringify({schema:2,hostId:host==="A"?
      "00000000-0000-4000-8000-000000008881":
      "00000000-0000-4000-8000-000000008882",
      utc:new Date(Date.UTC(2026,10,9,10,stage,host==="A"?0:1)).toISOString(),
      nonce:"00000000000040008000"+String(stage*2+(host==="B"?2:1)).padStart(12,"0"),
      maps});
  };
  const fill=async(name:string,value:string)=>{
    await page.getByRole("textbox",{name}).fill(value);
  };
  await fill("Előtte · UK-HOME-01 JSON",snapshot("A",0));
  await fill("Előtte · UK-HOME-02 JSON",snapshot("B",0));
  await fill("Utána · UK-HOME-01 JSON",snapshot("A",1));
  await fill("Utána · UK-HOME-02 JSON",snapshot("B",1));
  await page.getByRole("combobox",{name:"Induló map"}).selectOption(sourceId);
  await page.getByRole("combobox",{name:"Célmap"}).selectOption(targetId);
  await page.getByRole("combobox",{name:"Vizsgált karakterfájl"}).selectOption(survivor);
  await expect(action).toBeDisabled();
  await fill("Visszaút után · UK-HOME-01 JSON",snapshot("A",2));
  await expect(action).toBeDisabled();
  await fill("Visszaút után · UK-HOME-02 JSON",snapshot("B",2));
  await expect(action).toBeEnabled();
  await action.click();
  await expect(panel).toContainText("Odaút: fájlelrendezés egyezik");
  await expect(panel).toContainText("Visszaút: fájlelrendezés egyezik");
  await expect(panel).toContainText("Eredeti profil SHA: nem igazolt");
  await expect(panel).toContainText("nem oldja fel");
  await expect(panel).not.toContainText("Éles restore engedélyezve");
});

test("N-host offline full Store/transfer catalog requires user consent and retains all ten map classes",async({page})=>{
  await page.goto("/");
  await page.getByRole("navigation",{name:"Fő navigáció"})
    .getByRole("button",{name:"Cluster",exact:true}).click();
  const panel=page.getByRole("region",{name:"N-host mentés és izolált restore"});
  await panel.getByRole("button",{
    name:"Teljes offline Store/transfer katalógus összeállítása"
  }).click();
  const approval=panel.getByRole("region",{
    name:"Teljes offline Store/transfer katalógus jóváhagyása"
  });
  await expect(approval).toContainText("2 host, 1 cluster");
  await expect(approval).toContainText("10 map");
  await expect(approval).toContainText("151 fájl");
  await expect(approval).toContainText("Transfer fájlok:");
  await expect(approval).toContainText("inventory-, tribute- és dinoadatokat");
  const commit=approval.getByRole("button",{name:"Ellenőrzött katalógus létrehozása"});
  await expect(commit).toBeDisabled();
  await approval.getByRole("checkbox").check();
  await expect(commit).toBeEnabled();
  await commit.click();
  await expect(panel).toContainText("Teljes offline katalógus elkészült.");
  await expect(panel).toContainText("Az éles visszaállítást ez még nem engedélyezi");
});

test("interrupted N-host offline archive recovery requires consent and retains old stage",async({page})=>{
  await page.goto("/");
  await page.getByRole("navigation",{name:"Fő navigáció"})
    .getByRole("button",{name:"Cluster",exact:true}).click();
  const panel=page.getByRole("region",{name:"N-host mentés és izolált restore"});
  await panel.getByRole("button",{
    name:"Félbeszakadt offline archívum helyreállítása"
  }).click();
  const approval=panel.getByRole("region",{
    name:"Félbeszakadt multi-host archívum helyreállításának jóváhagyása"
  });
  await expect(approval).toContainText("64/151");
  await expect(approval).toContainText("a teljes .incomplete könyvtár megőrzött");
  await expect(approval).toContainText("nem érinti a futó ASA-t");
  const apply=approval.getByRole("button",{
    name:"Megőrzés és ellenőrzött újraépítés indítása"
  });
  await expect(apply).toBeDisabled();
  await approval.getByRole("checkbox").check();
  await expect(apply).toBeEnabled();
  await apply.click();
  await expect(panel).toContainText("Offline backup állapot: completed");
  await expect(panel).toContainText("Megőrzött félbeszakadt archívum");
  await expect(panel).toContainText("151 fájl SHA-256 ellenőrizve");
  await expect(panel).not.toContainText("Éles restore engedélyezve");
});

test("complete local host restore staging uses typed approval and displays native SHA256 receipt",async({page})=>{
  await page.goto("/");
  await page.getByRole("navigation",{name:"Fő navigáció"})
    .getByRole("button",{name:"Cluster",exact:true}).click();
  const panel=page.getByRole("region",{name:"N-host mentés és izolált restore"});
  await panel.getByRole("button",{
    name:"Helyi host teljes restore állomány előkészítése"
  }).click();
  const approve=panel.getByRole("region",{
    name:"Helyi host teljes restore állomány jóváhagyása"
  });
  await expect(approve).toContainText("5 saját map");
  await expect(approve).toContainText("Store/transfer");
  await expect(approve).toContainText("Nem történik éles restore");
  const commit=approve.getByRole("button",{
    name:"Ellenőrzött helyi restore állomány létrehozása"
  });
  await expect(commit).toBeDisabled();
  await approve.getByRole("checkbox").check();
  await approve.getByRole("textbox",{name:"Host restore jóváhagyó szövege"})
    .fill("HOST RESTORE TEST");
  await commit.click();
  await expect(panel).toContainText("Helyi host restore állomány állapot: completed");
  await expect(panel).toContainText("LOCAL_HOST_STAGE_SHA256_VERIFIED");
  await expect(panel).toContainText("61 fájl SHA-256 ellenőrizve");
});

test("N-host TEMP restore displays a competing Manager lease denial without false partial writes",async({page})=>{
  await page.goto("/");
  await page.getByRole("navigation",{name:"Fő navigáció"})
    .getByRole("button",{name:"Cluster",exact:true}).click();
  const panel=page.getByRole("region",{name:"N-host mentés és izolált restore"});
  await expect(panel).toBeVisible();
  await panel.getByRole("button",{name:"TEMP sandbox cseréje / rollback előnézet"}).click();
  const approval=panel.getByRole("region",{name:"TEMP restore jóváhagyása"});
  await expect(approval).toContainText("rollback-másolata megmarad");
  await approval.getByRole("checkbox",{
    name:/Jóváhagyom ennek az ellenőrzött TEMP sandboxnak a cseréjét/
  }).check();
  await approval.getByRole("button",{name:"TEMP sandbox cseréje (rollback-másolat megtartásával)"}).click();
  await expect(panel).toContainText("TEMP restore állapot: needsOperator");
  await expect(panel).toContainText("TEMP_SANDBOX_CONCURRENT_MANAGER_NO_WRITES");
  await expect(panel).toContainText("Másik Manager-folyamat már birtokolja");
  await expect(panel).toContainText("Az ütköző művelet nem írt fájlt");
  await expect(panel).not.toContainText("A részleges archívum vagy TEMP stage");
  await expect(panel).toContainText("Az élő SaveComplete, Store-zár");
});

test("Mentésközpont calls real native preview/create and displays verified characters",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Mentések",exact:true}).click();
  await expect(page.getByRole("heading",{name:"Mentésközpont"})).toBeVisible();
  await expect(page.getByRole("heading",{name:"Valódi világmentés és védett visszaállítás"})).toBeVisible();
  await expect(page.getByRole("button",{name:"Világmentés készítése"})).toBeDisabled();
  await page.getByRole("combobox",{name:"Mentendő helyi map"}).selectOption(second);
  await expect(page.getByRole("button",{name:"Világmentés készítése"})).toBeEnabled();
  await page.getByRole("button",{name:"Világmentés készítése"}).click();
  await expect(page.getByRole("region",{name:"Világmentés jóváhagyása"})).toBeVisible();
  await expect(page.getByText(/Astraeos_WP/)).toBeVisible();
  await page.getByRole("button",{name:"Mentés indítása"}).click();
  await expect(page.getByText("A világmentés elkészült és a fájlok SHA-256 ellenőrzése sikeres.")).toBeVisible();
  await expect(page.locator(".uk-world-backup-success").getByText(/4 fájl · 1 külön profil · 1 külön tribe fájl/)).toBeVisible();
  await expect(page.getByText("1 ellenőrzött csomag")).toBeVisible();
  await page.screenshot({path:"test-results/uk-real-world-backup-1600.png",fullPage:false});
});

test("backup approval and completed receipt never follow another selected map",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Mentések",exact:true}).click();
  const picker=page.getByRole("combobox",{name:"Mentendő helyi map"});
  await picker.selectOption(second);
  await page.getByRole("button",{name:"Világmentés készítése"}).click();
  await expect(page.getByRole("region",{name:"Világmentés jóváhagyása"})).toBeVisible();
  await picker.selectOption(first);
  await expect(page.getByRole("region",{name:"Világmentés jóváhagyása"})).toHaveCount(0);
  await expect(page.getByRole("button",{name:"Mentés indítása"})).toHaveCount(0);
  await picker.selectOption(second);
  await expect(page.getByRole("region",{name:"Világmentés jóváhagyása"})).toHaveCount(0);
  await page.getByRole("button",{name:"Világmentés készítése"}).click();
  await page.getByRole("button",{name:"Mentés indítása"}).click();
  await expect(page.getByText("A világmentés elkészült és a fájlok SHA-256 ellenőrzése sikeres.")).toBeVisible();
  await picker.selectOption(first);
  await expect(page.locator(".uk-world-backup-success")).toHaveCount(0);
});

test("historical Store-v1 map allows backup inspection but disables unilateral world restore",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>{
    (window as typeof window & {__UNTAMED_PROTECT_TEST_MAP__?:()=>void})
      .__UNTAMED_PROTECT_TEST_MAP__?.();
  });
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Mentések",exact:true}).click();
  await page.getByRole("combobox",{name:"Mentendő helyi map"}).selectOption(second);
  await expect(page.getByText(/Store-v1 vagy történeti Store-kapcsolat észlelve/)).toBeVisible();
  await expect(page.getByRole("button",{name:"Visszaállítás"})).toBeDisabled();
  await expect(page.getByRole("button",{name:"Világmentés készítése"})).toBeEnabled();
});

test("crash restore marker blocks new snapshot and restore actions in native backed UI",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>{
    (window as typeof window & {__UNTAMED_CRASH_MARKER_TEST__?:()=>void})
      .__UNTAMED_CRASH_MARKER_TEST__?.();
  });
  await page.getByRole("button",{name:"Mentések",exact:true}).click();
  await page.getByRole("combobox",{name:"Mentendő helyi map"}).selectOption(second);
  await expect(page.getByText(/Megszakadt világ-visszaállítás jelzője található/)).toBeVisible();
  await expect(page.getByRole("button",{name:"Világmentés készítése"})).toBeDisabled();
  await expect(page.getByRole("button",{name:"Visszaállítás"})).toBeDisabled();
});

test("dropped native WebView response expires its Promise and rejects without an automatic mutation retry",async({page})=>{
  await page.clock.install();
  await page.goto("/");
  await page.evaluate(async()=>{
    const host=window as unknown as {
      chrome:{webview:{postMessage:(message:{id:string;method:string})=>void}};
      __rpcFailure?:Promise<string>;__droppedNativeRequests?:number
    };
    const webview=host.chrome.webview;
    const original=webview.postMessage.bind(webview);
    host.__droppedNativeRequests=0;
    webview.postMessage=(message)=>{
      if(message.method==="updates.status"){
        host.__droppedNativeRequests=(host.__droppedNativeRequests??0)+1;
        return; // Synthetic abandoned native RPC; no response ever arrives.
      }
      original(message);
    };
    const bridge=await import("/src/bridge.ts");
    host.__rpcFailure=bridge.managerBridge.updateStatus()
      .then(()=>"unexpected-success")
      .catch((error:Error)=>error.message);
  });
  await page.clock.fastForward(121_000);
  const observed=await page.evaluate(async()=>{
    const host=window as unknown as {
      __rpcFailure:Promise<string>;__droppedNativeRequests:number
    };
    return {error:await host.__rpcFailure,dropped:host.__droppedNativeRequests};
  });
  expect(observed).toEqual({
    error:"UM-NATIVE-TIMEOUT-OPERATION-STATUS-UNKNOWN",dropped:1
  });
});

test("Update Center verifies Obsidian-only installer and requires explicit two-stage native approval",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Frissítések"}).click();
  await expect(page.getByRole("heading",{name:"Frissítési központ"})).toBeVisible();
  await expect(page.getByText("2.0.0 Preview")).toBeVisible();
  await expect(page.getByText("Build #1478")).toBeVisible();
  await expect(page.getByText(/egyetlen támogatott kiadási ág/)).toBeVisible();
  await expect(page.getByRole("combobox",{name:"Frissítési csatorna"})).toHaveCount(0);
  await page.getByRole("button",{name:"Frissítések keresése"}).click();
  await expect(page.getByText("Új kiadás érhető el")).toBeVisible();
  await expect(page.getByText("9.0.0 Preview")).toBeVisible();
  await expect(page.getByText("Untamed_Obsidian_Desktop_Preview_Setup.exe")).toBeVisible();
  await page.getByRole("button",{name:/Telepítő letöltése és SHA-256 ellenőrzése/}).click();
  await expect(page.getByText("Telepítő SHA-256 ellenőrizve")).toBeVisible();
  await expect(page.getByRole("button",{name:"Ellenőrzött frissítés telepítése"})).toBeDisabled();
  await page.getByRole("checkbox").check();
  await expect(page.getByRole("button",{name:"Ellenőrzött frissítés telepítése"})).toBeEnabled();
  await page.screenshot({path:"test-results/uk-update-center-1600.png",fullPage:false});
});

test("full-HD premium cluster dashboard fits without horizontal clipping",async({page})=>{
  await page.setViewportSize({width:1920,height:1080});
  await page.goto("/");
  await expect(page.getByRole("table",{name:"Cluster szerverlista"})).toBeVisible();
  await expect(page.getByRole("button",{name:"The Island részletei"})).toBeVisible();
  await expect(page.locator(".premium-dashboard-banner.hero")).toBeVisible();
  const grid=page.locator(".premium-dashboard-grid");
  const server=page.locator(".premium-server-panel");
  const aside=page.locator(".premium-dashboard-aside");
  const widths=await Promise.all([server,aside].map(x=>x.boundingBox()));
  expect(widths[0]&&widths[1]&&widths[0].x<widths[1].x).toBeTruthy();
  expect(widths[0]!.width/ (widths[0]!.width+widths[1]!.width)).toBeGreaterThan(0.61);
  const overflow=await page.evaluate(()=>document.documentElement.scrollWidth-window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await page.screenshot({path:"test-results/uk-rex-cluster-fullhd-1920.png",fullPage:false});
});

test("first-run premium gallery shows exactly ten discoverable maps with no invented running state",async({page})=>{
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.evaluate(()=>{
    (window as typeof window & {__UNTAMED_EMPTY_FIXTURE__?:()=>void})
      .__UNTAMED_EMPTY_FIXTURE__?.();
  });
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Kártyák"}).click();
  const catalog=page.getByLabel("ASA map választék");
  await expect(catalog).toBeVisible();
  await expect(catalog.locator(".uk-gallery-tile")).toHaveCount(10);
  await expect(page.locator(".uk-gallery-state.is-running")).toHaveCount(0);
  await expect(page.getByText("Választék · nem regisztrált szerverállapot")).toBeVisible();
  await page.getByRole("button",{name:"Astraeos · új szerver telepítése"}).click();
  await expect(page.getByRole("heading",{name:"Új ASA map telepítése"})).toBeVisible();
  await page.screenshot({path:"test-results/uk-first-run-steamcmd-1366.png",fullPage:false});
});

test("native Store-v1 gallery uses process and durable journal state, never a permanent locked badge",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_PROTECT_TEST_MAP__?:()=>void
  }).__UNTAMED_PROTECT_TEST_MAP__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Kártyák"}).click();
  const tile=page.getByRole("button",{name:/Astraeos szerver megnyitása/});
  await expect(tile).toContainText("STORE-V1 · ELŐELLENŐRZÉS");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await expect(tile).toContainText("STORE-V1 · ASA FUT");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_STORE_OPERATOR_REVIEW__?:()=>void
  }).__UNTAMED_STORE_OPERATOR_REVIEW__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await expect(tile).toContainText("STORE-V1 · KÉZI ELLENŐRZÉS");
  await expect(tile).not.toContainText("STORE-V1 ZÁROLT");
});

test("gallery filters local/remote machines without exposing peer control actions",async({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Kártyák"}).click();
  const gallery=page.getByRole("region",{name:"Szervergaléria"});
  await expect(gallery.locator(".uk-gallery-tile")).toHaveCount(3);
  await expect(gallery.getByLabel(/Ragnarok távoli megfigyelés/)).toBeVisible();
  await expect(gallery.getByRole("button",{name:/The Island szerver megnyitása/})).toBeVisible();
  await page.getByRole("combobox",{name:"Gazdagép szűrő"}).selectOption("remote");
  await expect(gallery.locator(".uk-gallery-tile")).toHaveCount(1);
  await expect(gallery.locator(".uk-gallery-tile.is-local")).toHaveCount(0);
  await page.getByRole("combobox",{name:"Gazdagép szűrő"}).selectOption("all");
  await page.getByRole("button",{name:"Futók"}).click();
  await expect(gallery.locator(".uk-gallery-tile")).toHaveCount(2);
  await page.getByRole("button",{name:"Leállítottak"}).click();
  await expect(gallery.locator(".uk-gallery-tile")).toHaveCount(1);
  await expect(gallery.getByRole("button",{name:/Astraeos szerver megnyitása/})).toBeVisible();
  await page.getByRole("button",{name:"Mind",exact:true}).click();
  await page.screenshot({path:"test-results/uk-rex-ten-map-gallery-1600.png",fullPage:false});
});

test("P0-1 gallery keeps missing map installers accessible on a partly configured cluster",async({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Kártyák"}).click();
  // Two local maps and one read-only remote map are already registered.
  // A user must still be able to install the other seven maps.
  const catalog=page.getByLabel("ASA map választék");
  await expect(catalog.locator(".is-discovery")).toHaveCount(7);
  await expect(catalog.getByRole("button",{name:"The Island · új szerver telepítése"})).toHaveCount(0);
  await expect(catalog.getByRole("button",{name:"Astraeos · új szerver telepítése"})).toHaveCount(0);
  await expect(catalog.getByRole("button",{name:"Ragnarok · új szerver telepítése"})).toHaveCount(0);
  await page.getByRole("combobox",{name:"Gazdagép szűrő"}).selectOption("remote");
  await expect(catalog).toHaveCount(0);
  await page.getByRole("combobox",{name:"Gazdagép szűrő"}).selectOption("all");
  await catalog.getByRole("button",{name:"Valguero · új szerver telepítése"}).click();
  await expect(page.getByRole("combobox",{name:"Új ASA map"})).toHaveValue("Valguero");
});

test("P0-2 Servers switches between keyboard-accessible list and premium 2-host map gallery",async({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await expect(page.locator(".premium-map-line.is-local")).toHaveCount(2);
  await expect(page.locator(".premium-map-line.is-remote")).toHaveCount(1);
  await page.getByRole("group",{name:"Szervernézet"}).getByRole("button",{name:"Kártyák"}).click();
  const gallery=page.getByRole("region",{name:"Szervergaléria"});
  await expect(gallery.locator(".uk-gallery-tile.is-local")).toHaveCount(2);
  await expect(gallery.locator(".uk-gallery-tile.is-remote")).toHaveCount(1);
  await expect(gallery.getByLabel(/Ragnarok távoli megfigyelés/)).toBeVisible();
  await expect(gallery.getByRole("button",{name:/Ragnarok szerver megnyitása/})).toHaveCount(0);
  await gallery.getByRole("button",{name:/Astraeos szerver megnyitása/}).click();
  await expect(page.locator(".map-detail-hero")).toContainText("Astraeos");
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await page.getByRole("group",{name:"Szervernézet"}).getByRole("button",{name:"Lista"}).click();
  await expect(page.locator(".premium-map-line.is-local")).toHaveCount(2);
});

test("P0-1 click an Untamed map artwork on an empty host to preselect its real SteamCMD installer",async({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_EMPTY_FIXTURE__?:()=>void
  }).__UNTAMED_EMPTY_FIXTURE__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("group",{name:"Szervernézet"}).getByRole("button",{name:"Kártyák"}).click();
  await expect(page.getByLabel("ASA map választék").locator(".is-discovery")).toHaveCount(10);
  await page.getByRole("button",{name:"Valguero · új szerver telepítése"}).click();
  await expect(page.getByText("Új ASA szerver · SteamCMD telepítés")).toBeVisible();
  await expect(page.getByRole("combobox",{name:"Új ASA map"})).toHaveValue("Valguero");
  await expect(page.getByRole("button",{name:"ASA letöltés indítása"})).toBeEnabled();
  // Empty root can never dispatch a native download, even with map selected.
  await page.getByRole("button",{name:"ASA letöltés indítása"}).click();
  await expect(page.getByRole("alert")).toContainText("Válassz egy üres, helyi telepítési mappát.");
});

test("real UI contract renders the synthetic dashboard and responsive navigation", async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Áttekintés" })).toBeVisible();
  await expect(page.locator(".premium-metrics .premium-metric")).toHaveCount(4);
  await expect(page.getByRole("table",{name:"Cluster szerverlista"})).toBeVisible();
  await expect(page.locator(".premium-dashboard-aside")).toBeVisible();
  await expect(page.getByRole("button",{name:"Lista"})).toHaveAttribute("aria-pressed","true");
  await page.getByRole("button",{name:"Kártyák"}).click();
  await expect(page.getByRole("region",{name:"Szervergaléria"})).toBeVisible();
  await expect(page.getByRole("button",{name:/The Island szerver megnyitása/})).toBeVisible();
  await expect(page.getByLabel(/Ragnarok távoli megfigyelés/)).toBeVisible();
  await expect(page.getByRole("button",{name:"Kártyák"})).toHaveAttribute("aria-pressed","true");
  await page.getByRole("button",{name:"Lista"}).click();
  await expect(page.getByRole("table",{name:"Cluster szerverlista"})).toBeVisible();
  await page.getByRole("button",{name:"Kártyák"}).click();
  await expect(page.locator(".premium-dashboard-grid")).toBeVisible();
  const mapArt=page.locator(".premium-map-cover.premium-map-island").first();
  await expect(mapArt).toBeVisible();
  expect(await mapArt.evaluate(node=>getComputedStyle(node).backgroundImage))
    .toContain("uk-reference-map-atlas.jpg");
  const atlas=await page.request.get("/uk-reference-map-atlas.jpg");
  expect(atlas.ok()).toBeTruthy();
  expect((await atlas.body()).byteLength).toBeGreaterThan(80000);

  await expect(page.getByText("KIZÁRÓLAG SZINTETIKUS UI-TESZTADAT")).toBeVisible();
  await expect(page.getByText("LAB-WIN-01").first()).toBeVisible();
  await expect(page.getByText("The Island").first()).toBeVisible();
  await page.screenshot({ path: "test-results/dashboard-1600.png", fullPage: true });

  await page.getByRole("button", { name: "Modok", exact: true }).click();
  await expect(page.getByRole("heading", { name: /^Modok –/ })).toBeVisible();
  await expect(page.getByRole("table",{name:"Konfigurált modlista"}).getByText("CurseForge ID: 123456")).toBeVisible();
  await expect(page.getByRole("table",{name:"Konfigurált modlista"})).toBeVisible();
  await expect(page.locator(".premium-mod-details")).toBeVisible();
  await expect(page.locator(".premium-mod-bottom-bar")).toBeVisible();
  await expect(page.getByRole("tab",{name:"Map modjai"})).toHaveAttribute("aria-selected","true");
  await expect(page.locator("#curseforge-catalog")).toBeHidden();
  await expect(page.locator("#mod-draft-editor")).toBeHidden();
  await page.screenshot({path:"test-results/mod-center-workspace-1600.png",fullPage:false});

  await page.getByRole("tab",{name:"CurseForge katalógus"}).click();
  await page.getByRole("textbox", { name: "CurseForge mod keresése" }).fill("Shiny");
  await page.getByRole("button", { name: "Keresés", exact: true }).click();
  await expect(page.locator("#curseforge-catalog").getByRole("heading", { name: "Example Search Result" }).first()).toBeVisible();
  await page.screenshot({ path: "test-results/mod-center-1600.png", fullPage: true });

  await page.getByRole("button", { name: "Pluginok" }).click();
  await expect(page.getByText("ExamplePlugin").first()).toBeVisible();
  await page.screenshot({path:"test-results/plugin-center-premium-1600.png",fullPage:false});
  await page.getByRole("button", { name: "Mentések" }).click();
  await expect(page.getByText("1 rendben")).toBeVisible();
  await expect(page.locator(".premium-backup-metrics>div")).toHaveCount(4);
  await expect(page.locator(".premium-backup-grid")).toBeVisible();
  await expect(page.locator(".premium-backup-list .premium-map-cover")).toHaveCount(1);
  await page.screenshot({path:"test-results/backup-center-premium-1600.png",fullPage:false});
  await page.screenshot({path:"test-results/backups-premium-1600.png",fullPage:false});
});

test("1366px viewport preserves keyboard focus and page scrolling", async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Áttekintés" })).toBeVisible();
  const sidebar = page.locator(".sidebar");
  await expect(sidebar).toBeVisible();
  await page.getByRole("button", { name: "Szerverek", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Szerverek" })).toBeVisible();
  const search = page.getByRole("textbox", { name: "Map keresése" });
  await search.focus();
  await expect(search).toBeFocused();
  await search.fill("Astraeos");
  await expect(page.getByText("Astraeos").first()).toBeVisible();
  await page.screenshot({ path: "test-results/server-list-1366.png", fullPage: true });
});

test("premium server browser opens local maps by keyboard and never issues remote navigation",async({page})=>{
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  const local=page.locator(".premium-map-line.is-local").first();
  await expect(local).toBeVisible();
  await expect(local).toHaveAttribute("tabindex","0");
  await local.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading",{name:"The Island",exact:true})).toBeVisible();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  const peer=page.locator(".premium-map-line.is-remote").first();
  await expect(peer).toBeVisible();
  await expect(peer).not.toHaveAttribute("tabindex","0");
  await expect(peer.getByText("Megfigyelés")).toBeVisible();
  await peer.click();
  await expect(page.getByRole("heading",{name:"Szerverek",exact:true})).toBeVisible();
  await page.screenshot({path:"test-results/premium-server-browser-1366.png",fullPage:false});
});

test("Steam installation preflight and redacted log center are read-only and filterable", async ({page}) => {
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.getByRole("button", { name: "Új map", exact:true }).click();
  await expect(page.getByRole("heading", {name:"Új ASA map telepítése"})).toBeVisible();
  await expect(page.getByText("Build: 123456")).toBeVisible();
  await expect(page.getByText("2797", {exact:true})).toHaveCount(0);
  await expect(page.getByText("7797", {exact:true})).toBeVisible();
  await expect(page.getByText("Kézi előellenőrzés")).toBeVisible();
  await expect(page.getByRole("button", { name: "Frissítés előellenőrzése" })).toHaveCount(0);
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("tab",{name:"Frissítés"}).click();
  await expect(page.getByRole("region",{name:"Leállított map SteamCMD-frissítése"})).toBeVisible();
  await expect(page.getByRole("combobox",{name:"SteamCMD frissítés map"})).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Frissítés előellenőrzése" })).toBeVisible();
  await page.screenshot({path:"test-results/steam-preflight-1366.png",fullPage:true});

  await page.getByRole("button", { name: "Naplók" }).click();
  await expect(page.getByRole("heading", {name:"Naplóközpont"})).toBeVisible();
  await expect(page.locator(".premium-log-metrics>div")).toHaveCount(4);
  await expect(page.locator(".premium-log-list")).toBeVisible();
  await expect(page.getByText("Synthetic ASA startup line")).toBeVisible();
  await page.getByRole("combobox", {name:"Naplószint"}).selectOption("error");
  await expect(page.getByText("Synthetic failure detected")).toBeVisible();
  await expect(page.getByText("Synthetic ASA startup line")).toHaveCount(0);
  await page.getByRole("textbox", {name:"Keresés a naplóban"}).fill("no match");
  await expect(page.getByText("Nincs a szűrőnek megfelelő naplósor.")).toBeVisible();
  await page.screenshot({path:"test-results/log-center-filter-1366.png",fullPage:true});
});

test("credential settings keeps API secret hidden and only saves on explicit submission", async ({page}) => {
  await page.goto("/");
  await page.getByRole("button",{name:"Beállítások"}).click();
  await expect(page.getByRole("heading",{name:"Beállítások"})).toBeVisible();
  await expect(page.getByText("Nincs beállított API-kulcs")).toBeVisible();
  const secret = page.getByLabel("CurseForge API-kulcs (nem kerül megjelenítésre)");
  await expect(secret).toHaveAttribute("type","password");
  await secret.fill("synthetic-credential-for-ui-test");
  await page.getByRole("button",{name:"Kulcs ellenőrzése és mentése"}).click();
  await expect(page.getByText("API-kulcs helyileg beállítva")).toBeVisible();
  await expect(secret).toHaveValue("");
  await expect(page.getByText("synthetic-credential-for-ui-test")).toHaveCount(0);
  await page.getByRole("button",{name:"Mentett kulcs törlése"}).click();
  await expect(page.getByRole("button",{name:"Megerősítés: kulcs törlése"})).toBeVisible();
  await page.getByRole("button",{name:"Megerősítés: kulcs törlése"}).click();
  await expect(page.getByText("Nincs beállított API-kulcs")).toBeVisible();
});

test("configured mods enrich official provider titles only when API credential is configured",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await expect(page.locator(".premium-mod-list-panel").getByText("Projekt · 789012")).toBeVisible();
  await page.getByRole("button",{name:"Beállítások"}).click();
  const secret=page.getByLabel("CurseForge API-kulcs (nem kerül megjelenítésre)");
  await secret.fill("synthetic-mod-metadata-key");
  await page.getByRole("button",{name:"Kulcs ellenőrzése és mentése"}).click();
  await expect(page.getByText("API-kulcs helyileg beállítva")).toBeVisible();
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await expect(page.locator(".premium-mod-list-panel").getByText("Featured Mod")).toBeVisible();
  await expect(page.locator(".premium-mod-list-panel")).not.toContainText("synthetic-mod-metadata-key");
});

test("native mod draft contract stages in memory and saves only on explicit click",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await expect(page.getByRole("heading",{name:/^Modok –/})).toBeVisible();
  await page.getByRole("tab",{name:"Modpiszkozat"}).click();
  await page.getByRole("button",{name:"Piszkozat indítása"}).click();
  await page.getByRole("textbox",{name:"Modprojekt ID"}).fill("567890");
  await page.getByRole("button",{name:"Lépés hozzáadása"}).click();
  await expect(page.locator(".draft-difference").getByText("567890")).toBeVisible();
  await page.screenshot({path:"test-results/mod-draft-1600.png",fullPage:true});
  await page.getByRole("button",{name:"Visszavonás"}).click();
  await expect(page.locator(".draft-difference")).toHaveCount(0);
  await page.getByRole("button",{name:"Újra",exact:true}).click();
  await expect(page.locator(".draft-difference").getByText("567890")).toBeVisible();
  await page.getByRole("button",{name:"Jóváhagyás és mentés"}).click();
  await page.getByRole("tab",{name:"Map modjai"}).click();
  await expect(page.getByRole("table",{name:"Konfigurált modlista"}).getByText("CurseForge ID: 567890")).toBeVisible();
  await expect(page.locator(".premium-mod-save-toast")).toContainText("Mentve a helyi indítási konfigurációba");
});

test("INI editor requires explicit open, preview and save in test contract",async ({page})=>{
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.getByRole("button",{name:"INI szerkesztő"}).click();
  await expect(page.getByRole("heading",{name:"INI szerkesztő"})).toBeVisible();
  await page.getByRole("button",{name:"Fájl biztonságos megnyitása"}).click();
  const editor=page.getByRole("textbox",{name:"Az eredeti fájl szerkeszthető tartalma"});
  await expect(editor).toBeVisible();
  await expect(page.getByRole("button",{name:"Jóváhagyás és atomikus mentés"})).toBeDisabled();
  await editor.fill("[ServerSettings]\\nExample=2\\n");
  await page.getByRole("button",{name:"Hash és változások előnézete"}).click();
  await expect(page.getByText(/FFEE11/).first()).toBeVisible();
  await expect(page.getByRole("button",{name:"Jóváhagyás és atomikus mentés"})).toBeEnabled();
  await page.screenshot({path:"test-results/ini-preview-1366.png",fullPage:true});
  await page.getByRole("button",{name:"Jóváhagyás és atomikus mentés"}).click();
  await expect(page.getByText(/visszaállítási \.bak másolattal/)).toBeVisible();
});

test("720px desktop sidebar scroll reaches Settings and INI without clipping", async ({page})=>{
  await page.setViewportSize({width:1280,height:720});
  await page.goto("/");
  const nav=page.getByRole("navigation",{name:"Fő navigáció"});
  await expect(nav).toBeVisible();
  await expect.poll(async()=>nav.evaluate(el=>el.scrollHeight>el.clientHeight)).toBeTruthy();
  await page.getByRole("button",{name:"Beállítások"}).click();
  await expect(page.getByRole("heading",{name:"Beállítások"})).toBeVisible();
  await page.getByRole("button",{name:"INI szerkesztő"}).click();
  await expect(page.getByRole("heading",{name:"INI szerkesztő"})).toBeVisible();
});

test("map-owned Mod Center retains its source map and uses existing native multi-map copy",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"The Island részletei"}).click();
  await page.getByRole("tab",{name:"Modok",exact:true}).click();
  await expect(page.getByRole("heading",{name:"Modok – The Island"})).toBeVisible();
  await expect(page.getByLabel("Modok forrásmapja")).toHaveText("The Island");
  await expect(page.getByRole("combobox",{name:"Helyi map"})).toHaveCount(0);
  await page.getByRole("tab",{name:"Másolás több mapra"}).click();
  const copy=page.getByRole("region",{name:"Modlista másolása több mapra"});
  await expect(copy.getByRole("group",{name:"Célmapok kiválasztása"})).toContainText("Astraeos");
  await copy.getByRole("group",{name:"Célmapok kiválasztása"}).getByRole("checkbox").first().check();
  await copy.getByRole("button",{name:"Változások előnézete"}).click();
  await expect(copy.getByRole("region",{name:"Többmapos modmásolás előnézete"})).toContainText("Astraeos");
  await expect(page.getByRole("heading",{name:"Modok – The Island"})).toBeVisible();
  await expect(page.getByRole("region",{name:"Többmapos modmásolás előnézete"}).getByRole("button",{name:"Jóváhagyás és mentés"})).toBeDisabled();
});

test("map detail INI and Logs remain bound to their own map while global selectors remain available",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"The Island részletei"}).click();
  await page.getByRole("tab",{name:"INI",exact:true}).click();
  await expect(page.getByLabel("INI forrásmapja")).toHaveText("The Island");
  await expect(page.getByRole("combobox",{name:"INI mapja"})).toHaveCount(0);
  await page.getByRole("tab",{name:"Naplók",exact:true}).click();
  await expect(page.getByLabel("Napló forrásmapja")).toHaveText("The Island");
  await expect(page.getByRole("combobox",{name:"Napló mapja"})).toHaveCount(0);
  await expect(page.getByRole("heading",{name:"The Island",exact:true})).toBeVisible();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("tab",{name:"INI",exact:true}).click();
  await expect(page.getByLabel("INI forrásmapja")).toHaveText("Astraeos");
  await page.getByRole("tab",{name:"Naplók",exact:true}).click();
  await expect(page.getByLabel("Napló forrásmapja")).toHaveText("Astraeos");
  await page.getByLabel("Fő navigáció").getByRole("button",{name:"INI szerkesztő"}).click();
  await expect(page.getByRole("combobox",{name:"INI mapja"})).toBeVisible();
  await page.getByLabel("Fő navigáció").getByRole("button",{name:"Naplók"}).click();
  await expect(page.getByRole("combobox",{name:"Napló mapja"})).toBeVisible();
});

test("local map card opens map-specific details, functional tabs, back navigation and blocks RCON", async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"The Island részletei"}).click();
  await expect(page.getByRole("heading",{name:"The Island",exact:true})).toBeVisible();
  await expect(page.getByRole("tablist",{name:"Map funkciói"})).toBeVisible();
  await expect(page.locator(".map-detail-hero .premium-map-cover")).toBeVisible();
  await page.screenshot({path:"test-results/map-detail-premium-1600.png",fullPage:false});
  await page.getByRole("tab",{name:"RCON / Konzol"}).click();
  await expect(page.getByRole("heading",{name:"RCON konzol",exact:true})).toBeVisible();
  await expect(page.getByRole("button",{name:"Játékoslista lekérdezése"})).toBeDisabled();
  await expect(page.getByText(/Nem küldhető RCON parancs/)).toBeVisible();
  await page.getByRole("tab",{name:"Áttekintés"}).click();
  await page.getByRole("tab",{name:"Modok"}).click();
  await expect(page.getByRole("heading",{name:/^Modok –/})).toBeVisible();
  await page.getByRole("tab",{name:"INI"}).click();
  await expect(page.getByRole("heading",{name:"INI szerkesztő"})).toBeVisible();
  await page.getByRole("tab",{name:"Naplók"}).click();
  await expect(page.getByRole("heading",{name:"Naplóközpont"})).toBeVisible();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await expect(page.getByRole("heading",{name:"Szerverek"})).toBeVisible();
  await page.screenshot({path:"test-results/map-detail-navigation-1600.png",fullPage:true});
});

test("RCON settings use the selected local map port and never retain another map password",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  await page.getByRole("tab",{name:"RCON / Konzol"}).click();
  await expect(page.getByRole("spinbutton",{name:"RCON beállítás port"})).toHaveValue("37085");
  await expect(page.getByRole("textbox",{name:"RCON beállítás jelszó"})).toBeEnabled();
  await page.getByRole("textbox",{name:"RCON beállítás jelszó"}).fill("Synthetic-Astraeos-Only-Password");
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await page.locator(".premium-map-line.is-local").first().click();
  await expect(page.getByRole("heading",{name:"The Island",exact:true})).toBeVisible();
  await page.getByRole("tab",{name:"RCON / Konzol"}).click();
  await expect(page.getByRole("spinbutton",{name:"RCON beállítás port"})).toHaveValue("37015");
  await expect(page.getByRole("textbox",{name:"RCON beállítás jelszó"})).toHaveValue("");
});

test("map detail maintenance setting persists with approval and blocks running maps",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Map karbantartási beállítása"});
  await expect(panel.getByText("Kikapcsolva")).toBeVisible();
  const enable=panel.getByRole("button",{name:"Karbantartási mód bekapcsolása"});
  await expect(enable).toBeDisabled();
  await panel.getByRole("checkbox",{name:/Jóváhagyom ennek az egy helyi mapnak/}).check();
  await enable.click();
  await expect(panel.getByText("Bekapcsolva",{exact:true})).toBeVisible();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  const reopened=page.getByRole("region",{name:"Map karbantartási beállítása"});
  await expect(reopened.getByText("Bekapcsolva",{exact:true})).toBeVisible();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await page.locator(".premium-map-line.is-local").first().click();
  const running=page.getByRole("region",{name:"Map karbantartási beállítása"});
  await expect(running.getByRole("button",{name:"Karbantartási mód bekapcsolása"})).toBeDisabled();
});

test("historical Store RCON attaches only existing INI with native approval and without any Store write",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_PROTECT_TEST_MAP__?:()=>void
  }).__UNTAMED_PROTECT_TEST_MAP__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const attach=page.getByRole("region",{name:"Történeti Store-v1 RCON csatlakoztatás"});
  await expect(attach).toBeVisible();
  await expect(attach.getByText(/Eredeti fájlok érintetlenek/)).toBeVisible();
  await attach.getByLabel("Meglévő ASA RCON-port").fill("37145");
  await attach.getByLabel("Eredeti ASA ServerAdminPassword").fill("HistoricalRconPass!2026");
  await attach.getByRole("button",{name:"Eredeti RCON előellenőrzése"}).click();
  const confirm=attach.getByRole("region",{name:"Store-v1 RCON jóváhagyása"});
  await expect(confirm).toBeVisible();
  const apply=attach.getByRole("button",{name:"RCON átvétel véglegesítése"});
  await expect(apply).toBeDisabled();
  await expect(attach.locator('input[type="password"]')).toHaveValue("");
  await confirm.getByText(/Jóváhagyom az eredeti RCON-kapcsolat/).click();
  await expect(apply).toBeEnabled();
  await apply.click();
  await expect(attach.getByText(/RCON-port: 37145/)).toBeVisible();
  await expect(attach.getByText(/Store-fájlok nem módosultak/)).toBeVisible();
  await expect(attach.getByRole("button",{name:"RCON átvétel véglegesítése"})).toHaveCount(0);
});

test("original two-host ACTIVE Store-v1 ASA Start is real native approved operation, not a fresh-demo button",async({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_PROTECT_TEST_MAP__?:()=>void
  }).__UNTAMED_PROTECT_TEST_MAP__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const historic=page.getByRole("region",{name:"Történeti Store-v1 ASA indítás"});
  await expect(historic).toBeVisible();
  await expect(historic.getByRole("button",{name:"Eredeti Store-v1 indítás előellenőrzése"})).toBeDisabled();
  await expect(page.getByRole("button",{name:"Friss ASA indítása"})).toHaveCount(0);
  const rcon=page.getByRole("region",{name:"Történeti Store-v1 RCON csatlakoztatás"});
  await rcon.getByLabel("Meglévő ASA RCON-port").fill("37145");
  await rcon.getByLabel("Eredeti ASA ServerAdminPassword").fill("HistoricalRconPass!2026");
  await rcon.getByRole("button",{name:"Eredeti RCON előellenőrzése"}).click();
  await rcon.getByRole("region",{name:"Store-v1 RCON jóváhagyása"})
    .getByText(/Jóváhagyom az eredeti RCON-kapcsolat/).click();
  await rcon.getByRole("button",{name:"RCON átvétel véglegesítése"}).click();
  await expect(rcon.getByText(/RCON-port: 37145/)).toBeVisible();
  // Profile change refreshes map detail; the native Start probe re-queries
  // original manager profile, two peers and exact ASA process state.
  const preflight=historic.getByRole("button",{name:"Eredeti Store-v1 indítás előellenőrzése"});
  await expect(preflight).toBeEnabled();
  await preflight.click();
  const approval=historic.getByRole("region",{name:"Eredeti Store-v1 ASA indítás jóváhagyása"});
  await expect(approval).toBeVisible();
  const spawn=approval.getByRole("button",{name:"Eredeti Store-v1 ASA indítása"});
  await expect(spawn).toBeDisabled();
  await approval.locator('input[id="historical-start-phrase"]').fill("START");
  await expect(spawn).toBeDisabled();
  await approval.locator('input[id="historical-start-phrase"]').fill("STORE-V1 ACTIVE START");
  await expect(spawn).toBeEnabled();
  await spawn.click();
  await expect(historic.getByText(/valódi Windows ASA PID: 42101/)).toBeVisible();
  await expect(historic.getByText(/Nincs automatikus Start\/Restart/)).toBeVisible();
  await expect(spawn).toHaveCount(0);
});

test("new empty Store-map uses existing cluster membership, one-shot approval and distinct non-Kova UI",async({page})=>{
  await page.goto("/");
  await page.getByRole("navigation",{name:"Fő navigáció"})
    .getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local")
    .filter({hasText:"Astraeos"}).click();
  const region=page.getByRole("region",{name:"Új üres Store-map"});
  await expect(region).toBeVisible();
  await expect(region.getByRole("combobox",{name:"Új Store-map cluster"})).toHaveValue("fixture-beta");
  await expect(region.getByRole("button",{name:"Új Store-profil mentése"})).toHaveCount(0);
  await region.getByRole("button",{name:"Üres Store-map előellenőrzése"}).click();
  await expect(region.getByRole("region",{name:"Üres Store-map előnézete"})).toContainText("Másik host átviteli azonossága");
  await expect(region.getByRole("button",{name:"Új Store-profil mentése"})).toBeDisabled();
  await region.getByRole("checkbox",{name:/Jóváhagyom az üres map/}).check();
  await region.getByRole("button",{name:"Új Store-profil mentése"}).click();
  await expect(region).toContainText("Store-profil regisztrálva: fixture-beta");
  await expect(region).toContainText("Nem fut -converttostore");
  await expect(page.getByRole("button",{name:/ASA indítási mód választása/})).toHaveCount(0);
  await expect(page.getByRole("region",{name:"Új üres Store-map"})).toContainText("játékbeli utazás még nem igazolt");
});

test("new Store API Loader mode needs separate approval and permits direct opt-out without start",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const enrollment=page.getByRole("region",{name:"Új üres Store-map"});
  await enrollment.getByRole("button",{name:"Üres Store-map előellenőrzése"}).click();
  await enrollment.getByRole("checkbox",{name:/Jóváhagyom az üres map/}).check();
  await enrollment.getByRole("button",{name:"Új Store-profil mentése"}).click();
  await expect(enrollment).toContainText("Store-profil regisztrálva");
  const mode=page.getByRole("region",{name:"Új Store-map ASA API indítási módja"});
  await expect(mode).toBeVisible();
  await expect(mode).toContainText("Közvetlen ArkAscendedServer");
  await mode.getByRole("button",{name:"ASA API Loader választása"}).click();
  await expect(mode.getByRole("region",{name:"Új Store-map ASA API módváltási előnézete"})).toContainText("Nem történik");
  await mode.getByRole("button",{name:"Módváltás jóváhagyása"}).click();
  await expect(mode).toContainText("ASA API Loader indítási mód mentve");
  await expect(mode).toContainText("UM-API-CACHE-NO-LOCAL-KEY");
  await mode.getByRole("button",{name:"Váltás közvetlen ASA indításra"}).click();
  await mode.getByRole("button",{name:"Módváltás jóváhagyása"}).click();
  await expect(mode).toContainText("Közvetlen ASA indítási mód mentve");
  await expect(mode).toContainText("Közvetlen ArkAscendedServer");
  await expect(page.getByRole("button",{name:/Eredeti Store-v1 API Loader indítása/})).toHaveCount(0);
});

test("original Store-v1 RCON console actually queries local player, game and chat logs and never exposes write commands",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  await page.getByRole("tab",{name:"RCON / Konzol"}).click();
  const panel=page.getByRole("region",{name:"Eredeti Store-v1 RCON konzol"});
  await expect(panel).toBeVisible();
  await expect(panel.getByText(/Igazolt · PID 42101/)).toBeVisible();
  await expect(panel.getByRole("button",{name:"Játékoslista lekérdezése"})).toBeEnabled();
  await panel.getByRole("button",{name:"Játékoslista lekérdezése"}).click();
  await expect(panel.getByText("Synthetic original ASA ListPlayers")).toBeVisible();
  await panel.getByRole("button",{name:"ASA játéknapló"}).click();
  await expect(panel.getByText("Synthetic original ASA GetGameLog")).toBeVisible();
  await panel.getByRole("button",{name:"ASA chatnapló"}).click();
  await expect(panel.getByText("Synthetic original ASA GetChat")).toBeVisible();
  await expect(panel.getByRole("button",{name:/SaveWorld|DoExit|Parancs elküldése/})).toHaveCount(0);
  await expect(panel.getByText(/közös Store-restore biztonságát/)).toBeVisible();
});

test("original Store-v1 admin RCON requires exact one-time native approval and excludes lifecycle commands",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  await page.getByRole("tab",{name:"RCON / Konzol"}).click();
  const panel=page.getByRole("region",{name:"Eredeti Store-v1 admin RCON"});
  await expect(panel).toBeVisible();
  await panel.getByLabel("Küldendő üzenet").fill("Hello Store");
  await panel.getByRole("button",{name:"Eredeti adminparancs ellenőrzése"}).click();
  const approval=panel.getByRole("region",{name:"Store-v1 adminparancs végső jóváhagyása"});
  await expect(approval).toBeVisible();
  await expect(approval.getByText("Broadcast Hello Store")).toBeVisible();
  const send=approval.getByRole("button",{name:"Adminparancs egyszeri elküldése"});
  await expect(send).toBeDisabled();
  await approval.getByLabel("Jóváhagyom a helyi Store-v1 RCON műveletet.").check();
  await approval.getByLabel(/Írd be pontosan: STORE-V1 ADMIN/).fill("ADMIN");
  await expect(send).toBeDisabled();
  await approval.getByLabel(/Írd be pontosan: STORE-V1 ADMIN/).fill("STORE-V1 ADMIN");
  await expect(send).toBeEnabled();
  await send.click();
  await expect(panel.getByText(/Egyszeri RCON-parancs elküldve: Broadcast Hello Store/))
    .toBeVisible();
  await expect(send).toHaveCount(0);
  await panel.getByLabel("Admin RCON parancstípus").selectOption("wildWipe");
  await panel.getByRole("button",{name:"Eredeti adminparancs ellenőrzése"}).click();
  const danger=panel.getByRole("region",{name:"Store-v1 adminparancs végső jóváhagyása"});
  await expect(danger.getByText("DestroyWildDinos")).toBeVisible();
  const sendWipe=danger.getByRole("button",{name:"Adminparancs egyszeri elküldése"});
  await danger.getByLabel("Jóváhagyom a helyi Store-v1 RCON műveletet.").check();
  await danger.getByLabel(/Írd be pontosan: STORE-V1 DINÓTÖRLÉS/).fill("STORE-V1 ADMIN");
  await expect(sendWipe).toBeDisabled();
  await danger.getByLabel(/Írd be pontosan: STORE-V1 DINÓTÖRLÉS/).fill("STORE-V1 DINÓTÖRLÉS");
  await sendWipe.click();
  await expect(panel.getByText(/Egyszeri RCON-parancs elküldve: DestroyWildDinos/))
    .toBeVisible();
});

test("original Store-v1 SaveWorld-only runs the real native map action without stopping ASA",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Store-v1 SaveWorld leállítás nélkül"});
  await expect(panel).toBeVisible();
  const inspect=panel.getByRole("button",{name:"Mentés előellenőrzése"});
  await expect(inspect).toBeEnabled();
  await inspect.click();
  const approve=panel.getByRole("region",{name:"Store-v1 SaveWorld jóváhagyása"});
  await expect(approve).toContainText("PID 42101");
  await expect(approve).toContainText("RCON 37145");
  const save=approve.getByRole("button",{name:"SaveWorld végrehajtása"});
  await expect(save).toBeDisabled();
  await approve.getByLabel("Store-v1 mentés jóváhagyó szöveg").fill("STORE-V1 SAVEWORLD DOEXIT");
  await expect(save).toBeDisabled();
  await approve.getByLabel("Store-v1 mentés jóváhagyó szöveg").fill("STORE-V1 SAVEWORLD");
  await expect(save).toBeEnabled();
  await save.click();
  await expect(panel.getByText(/A szerver tovább fut; fsync és játékbeli karakterutazás/)).toBeVisible();
  await expect(panel).toContainText("ASA tovább fut");
  await expect(page.getByRole("region",{name:"Eredeti Store-v1 SaveWorld és DoExit"})).toBeVisible();
  await expect(page.getByRole("heading",{name:"Astraeos",exact:true})).toBeVisible();
  await expect(save).toHaveCount(0);
});

test("Store SaveComplete UI distinguishes durable local log evidence from live cross-host writer certification",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Store-v1 SaveWorld leállítás nélkül"});
  const evidence=panel.getByTestId("store-durable-save-evidence");
  await expect(evidence).toContainText("Nincs igazolt helyi SaveComplete napló");
  await expect(evidence).toContainText("Production restore továbbra is tiltott");
  await panel.getByRole("button",{name:"Mentés előellenőrzése"}).click();
  const approval=panel.getByRole("region",{name:"Store-v1 SaveWorld jóváhagyása"});
  await approval.getByLabel("Store-v1 mentés jóváhagyó szöveg").fill("STORE-V1 SAVEWORLD");
  await approval.getByRole("button",{name:"SaveWorld végrehajtása"}).click();
  await expect(evidence).toContainText("Korábbi helyi ASA naplójelzés + stabil világfájl rögzítve");
  await expect(panel).toContainText("Helyi SaveComplete napló tartósan igazolt");
  await expect(evidence).not.toContainText("Két gép SaveComplete igazolva");
  await expect(evidence).toContainText("nem igazolja a jelenlegi Store-írójogot");
});

test("corrupt native SaveComplete receipt fails closed without a SaveWorld button",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void;
    __UNTAMED_SAVE_EVIDENCE_TAMPER__?:()=>void;
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_SAVE_EVIDENCE_TAMPER__?:()=>void
  }).__UNTAMED_SAVE_EVIDENCE_TAMPER__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Store-v1 SaveWorld leállítás nélkül"});
  await expect(panel.getByTestId("store-durable-save-evidence"))
    .toContainText("Tartós napló nem ellenőrizhető");
  await expect(panel.getByRole("button",{name:"Mentés előellenőrzése"})).toBeDisabled();
  await expect(panel.getByRole("button",{name:"SaveWorld végrehajtása"})).toHaveCount(0);
});

test("Store-v1 SaveWorld reattaches to exactly the original native job without redispatch after React remount",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void;
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_SAVE_REATTACH__?:()=>void;
  }).__UNTAMED_ORIGINAL_SAVE_REATTACH__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const save=page.getByRole("region",{name:"Store-v1 SaveWorld leállítás nélkül"});
  const stop=page.getByRole("region",{name:"Eredeti Store-v1 SaveWorld és DoExit"});
  await expect(save.getByText(/visszacsatlakozva; nincs új RCON-küldés/)).toBeVisible();
  await expect(stop.getByText(/Másik Store-v1 művelet fut/)).toBeVisible();
  await expect(stop.getByRole("button",{name:"Eredeti SaveWorld\/DoExit előellenőrzése"}))
    .toBeDisabled();
  await expect(save.getByRole("button",{name:"Mentés előellenőrzése"})).toBeDisabled();
  const dispatches=await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_SAVE_DISPATCHES__?:()=>number;
  }).__UNTAMED_ORIGINAL_SAVE_DISPATCHES__?.());
  expect(dispatches).toBe(1);
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_SAVE_RELEASE__?:()=>void;
  }).__UNTAMED_ORIGINAL_SAVE_RELEASE__?.());
  await expect(save.getByText(/Helyi SaveComplete napló tartósan igazolt/)).toBeVisible();
  await expect(save.getByTestId("store-durable-save-evidence"))
    .toContainText("Korábbi helyi ASA naplójelzés + stabil világfájl rögzítve");
  await expect(save).toContainText("Production restore továbbra is tiltott.");
  expect(await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_SAVE_DISPATCHES__?:()=>number;
  }).__UNTAMED_ORIGINAL_SAVE_DISPATCHES__?.())).toBe(1);
});

test("Store-v1 interrupted SaveWorld can be explicitly reviewed when stopped without auto resending RCON",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_SAVE_REVIEW_FIXTURE__?:()=>void;
  }).__UNTAMED_SAVE_REVIEW_FIXTURE__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Store-v1 SaveWorld leállítás nélkül"});
  await expect(panel.getByTestId("store-durable-save-evidence"))
    .toContainText("Függőben / bizonytalan");
  await expect(panel.getByRole("button",{name:"Mentés előellenőrzése"})).toBeDisabled();
  await panel.getByRole("button",{name:"Korábbi SaveWorld operátori ellenőrzése"}).click();
  const review=panel.getByRole("region",{name:"Korábbi Store-v1 mentés felülvizsgálata"});
  const accept=review.getByRole("button",{name:"Korábbi mentési napló felülvizsgálatának jóváhagyása"});
  await expect(accept).toBeDisabled();
  await review.getByLabel("Store-v1 mentés felülvizsgálati megerősítése")
    .fill("REVIEW STORE SAVE");
  await expect(accept).toBeEnabled();
  await accept.click();
  await expect(panel).toContainText("a műveleti napló operátori ellenőrzéssel feloldva");
  await expect(panel.getByTestId("store-durable-save-evidence"))
    .toContainText("Operátori felülvizsgálat megtörtént");
  await expect(panel.getByRole("button",{name:"SaveWorld végrehajtása"})).toHaveCount(0);
});

test("original Store-v1 SaveWorld-only refuses unresolved durable journal without RCON dispatch",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_STOP_CRASH_RECOVERY__?:()=>void
  }).__UNTAMED_ORIGINAL_STOP_CRASH_RECOVERY__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Store-v1 SaveWorld leállítás nélkül"});
  await expect(panel.getByText(/Befejezetlen Store-v1 műveleti napló/)).toBeVisible();
  await expect(panel.getByRole("button",{name:"Mentés előellenőrzése"})).toBeDisabled();
  await expect(panel.getByRole("button",{name:"SaveWorld végrehajtása"})).toHaveCount(0);
});

test("original Store-v1 manual Restart requires exact two-stage approval and displays new PID without claiming gameplay readiness",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Eredeti Store-v1 manuális újraindítás"});
  await expect(panel.getByRole("button",{name:"Store-v1 újraindítás előellenőrzése"}))
    .toBeEnabled();
  await panel.getByRole("button",{name:"Store-v1 újraindítás előellenőrzése"}).click();
  const approval=panel.getByRole("region",{name:"Store-v1 újraindítás jóváhagyása"});
  await expect(approval).toContainText("régi PID 42101");
  const apply=approval.getByRole("button",{name:"Store-v1 Restart végrehajtása"});
  await expect(apply).toBeDisabled();
  await approval.getByLabel("Store-v1 újraindítás jóváhagyó szöveg")
    .fill("STORE-V1 SAVEWORLD DOEXIT");
  await expect(apply).toBeDisabled();
  await approval.getByLabel("Store-v1 újraindítás jóváhagyó szöveg")
    .fill("STORE-V1 SAVEWORLD DOEXIT START");
  await expect(apply).toBeEnabled();
  await apply.click();
  await expect(panel.getByText(/Új eredeti ASA PID: 42102/)).toBeVisible();
  await expect(panel.getByText(/Régi PID 42101 → új PID 42102/)).toBeVisible();
  await expect(panel).toContainText("játékbeli csatlakozás és karakterutazás");
  const sent=await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_RESTART_SENDS__?:()=>number
  }).__UNTAMED_RESTART_SENDS__?.());
  expect(sent).toBe(1);
  await expect(panel.getByRole("button",{name:"Store-v1 újraindítás előellenőrzése"}))
    .toBeDisabled();
});

test("original Store-v1 Restart fails closed after synthetic Stop-to-Start crash and never auto-retries",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_RESTART_FAIL__?:()=>void
  }).__UNTAMED_RESTART_FAIL__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Eredeti Store-v1 manuális újraindítás"});
  await panel.getByRole("button",{name:"Store-v1 újraindítás előellenőrzése"}).click();
  const approval=panel.getByRole("region",{name:"Store-v1 újraindítás jóváhagyása"});
  await approval.getByLabel("Store-v1 újraindítás jóváhagyó szöveg")
    .fill("STORE-V1 SAVEWORLD DOEXIT START");
  await approval.getByRole("button",{name:"Store-v1 Restart végrehajtása"}).click();
  await expect(panel.getByText(/A Store-v1 Restart megszakadt/)).toBeVisible();
  await expect(panel.getByRole("button",{name:"Store-v1 újraindítás előellenőrzése"}))
    .toBeDisabled();
  expect(await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_RESTART_SENDS__?:()=>number
  }).__UNTAMED_RESTART_SENDS__?.())).toBe(1);
});

test("original Store-v1 real SaveWorld then DoExit requires exact typed approval, shows durable result and no Kill",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Eredeti Store-v1 SaveWorld és DoExit"});
  await expect(panel).toBeVisible();
  await panel.getByRole("button",{name:"Eredeti SaveWorld/DoExit előellenőrzése"}).click();
  const approval=panel.getByRole("region",{name:"Eredeti Store-v1 leállítás jóváhagyása"});
  await expect(approval).toBeVisible();
  const apply=approval.getByRole("button",{name:"SaveWorld és szabályos DoExit"});
  await expect(apply).toBeDisabled();
  await approval.getByLabel("Pontos jóváhagyó szöveg").fill("DOEXIT");
  await expect(apply).toBeDisabled();
  await approval.getByLabel("Pontos jóváhagyó szöveg").fill("STORE-V1 SAVEWORLD DOEXIT");
  await expect(apply).toBeEnabled();
  await apply.click();
  await expect(panel.getByText(/A régi ASA-folyamat szabályosan kilépett/)).toBeVisible();
  await expect(panel.getByText(/mentésjelzés megfigyelve/)).toBeVisible();
  await expect(panel.getByRole("button",{name:"Eredeti SaveWorld/DoExit előellenőrzése"})).toBeDisabled();
  await expect(panel.getByRole("button",{name:/Kill|Erőltetett|Újraindítás/})).toHaveCount(0);
});

test("old original Store Stop NEEDS_OPERATOR journal survives UI remount and disables SaveWorld/DoExit without resetting old locks",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_RCON_TEST_READY__?:()=>void
  }).__UNTAMED_ORIGINAL_RCON_TEST_READY__?.());
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_ORIGINAL_STOP_CRASH_RECOVERY__?:()=>void
  }).__UNTAMED_ORIGINAL_STOP_CRASH_RECOVERY__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const panel=page.getByRole("region",{name:"Eredeti Store-v1 SaveWorld és DoExit"});
  const journal=panel.getByRole("region",{name:"Tartós Store-v1 műveleti helyreállítás"});
  await expect(journal).toBeVisible();
  await expect(journal.getByText(/Korábbi műveleti napló/)).toBeVisible();
  await expect(journal.getByText(/Kézi ellenőrzés szükséges/)).toBeVisible();
  await expect(panel.getByRole("button",{name:"Eredeti SaveWorld/DoExit előellenőrzése"}))
    .toBeDisabled();
  await expect(panel.getByRole("button",{name:"SaveWorld és szabályos DoExit"}))
    .toHaveCount(0);
});

test("real admin RCON UI requires two-stage approval and exact typed phrase for destructive actions",async({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.evaluate(()=>
    (window as typeof window & {__UNTAMED_RCON_ENABLE__?:()=>void})
      .__UNTAMED_RCON_ENABLE__?.());
  await page.getByRole("button",{name:"The Island részletei"}).click();
  await page.getByRole("tab",{name:"RCON / Konzol"}).click();
  await expect(page.getByRole("region",{name:"Helyi admin RCON parancsok"})).toBeVisible();
  await page.getByRole("textbox",{name:"Admin RCON üzenet vagy parancs"})
    .fill("Synthetic operator message");
  await page.getByRole("button",{name:"Éles RCON-parancs ellenőrzése"}).click();
  await expect(page.getByText("Broadcast Synthetic operator message")).toBeVisible();
  await expect(page.getByRole("button",{name:"Parancs elküldése"})).toBeDisabled();
  await page.getByRole("region",{name:"Adminparancs végső jóváhagyása"})
    .getByRole("checkbox").check();
  await page.getByRole("button",{name:"Parancs elküldése"}).click();
  await expect(page.getByText("Adminparancs egyszer elküldve")).toBeVisible();
  await page.getByRole("combobox",{name:"Admin RCON parancstípus"}).selectOption("wildWipe");
  await page.getByRole("button",{name:"Éles RCON-parancs ellenőrzése"}).click();
  await expect(page.getByText("DestroyWildDinos")).toBeVisible();
  await page.getByRole("region",{name:"Adminparancs végső jóváhagyása"})
    .getByRole("checkbox").check();
  await page.getByRole("textbox",{name:"Adminparancs megerősítő szó"}).fill("NEM");
  await expect(page.getByRole("button",{name:"Parancs elküldése"})).toBeDisabled();
  await page.getByRole("textbox",{name:"Adminparancs megerősítő szó"}).fill("DINÓTÖRLÉS");
  await expect(page.getByRole("button",{name:"Parancs elküldése"})).toBeEnabled();
  await page.screenshot({path:"test-results/untamed-admin-rcon-confirm-1600.png",fullPage:true});
});

test("real local RCON game and chat log buttons are read-only and show separate responses",async({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"The Island részletei"}).click();
  await page.getByRole("tab",{name:"RCON / Konzol"}).click();
  await expect(page.getByRole("button",{name:"ASA játéknapló (GetGameLog)"})).toBeDisabled();
  await expect(page.getByRole("button",{name:"ASA chat (GetChat)"})).toBeDisabled();
  await page.evaluate(()=>(
    window as typeof window & {__UNTAMED_RCON_ENABLE__?:()=>void}
  ).__UNTAMED_RCON_ENABLE__?.());
  await page.locator(".uk-rcon-console").getByRole("button",{name:"Állapot frissítése"}).click();
  const game=page.getByRole("button",{name:"ASA játéknapló (GetGameLog)"});
  const chat=page.getByRole("button",{name:"ASA chat (GetChat)"});
  await expect(game).toBeEnabled();
  await game.click();
  await expect(page.getByRole("log",{name:"RCON ListPlayers válasz"}))
    .toContainText("Synthetic ASA GameLog entry");
  await expect(page.getByText(/GetGameLog – olvasási válasz/)).toBeVisible();
  await chat.click();
  await expect(page.getByRole("log",{name:"RCON ListPlayers válasz"}))
    .toContainText("Synthetic ASA chat entry");
  await expect(page.getByText(/GetChat – olvasási válasz/)).toBeVisible();
  await expect(page.getByRole("log",{name:"RCON ListPlayers válasz"}))
    .not.toContainText("Synthetic ASA GameLog entry");
});

test("local ASA import wizard requires preview and confirmation, then refreshes registered maps",async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Meglévő ASA importálása"}).click();
  await expect(page.getByText("Helyi ASA-telepítés felvétele")).toBeVisible();
  await expect(page.getByRole("button",{name:"Ellenőrzött map regisztrálása"})).toBeDisabled();
  await page.getByRole("button",{name:"Genesis Part 1",exact:true}).click();
  await page.getByRole("textbox",{name:"Helyi ASA mappa"}).fill("C:\\ASA-LAB");
  await page.getByRole("button",{name:"Helyi telepítés ellenőrzése"}).click();
  await expect(page.getByText(/Genesis Part 1 · ASA EXE megtalálva/)).toBeVisible();
  await page.getByRole("button",{name:"Ellenőrzött map regisztrálása"}).click();
  await expect(page.getByText(/bekerült a helyi Manager profiljába/)).toBeVisible();
  await expect(page.getByRole("row",{name:/Genesis Part 1/})).toBeVisible();
  await page.screenshot({path:"test-results/map-import-1600.png",fullPage:true});
});

test("CurseForge official Galaxy browses categories, projects, versions, and pre-fills guarded mod editor", async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await page.getByRole("tab",{name:"CurseForge katalógus"}).click();
  await expect(page.getByText("CurseForge ASA Mod Galaxy")).toBeVisible();
  await expect(page.getByRole("tab",{name:"Kiemelt modok"})).toBeVisible();
  await expect(page.getByText("Featured Mod").first()).toBeVisible();
  await page.getByRole("combobox",{name:"CurseForge kategória"}).selectOption("101");
  await page.getByRole("combobox",{name:"CurseForge rendezés"}).selectOption("2");
  await page.getByRole("textbox",{name:"CurseForge mod keresése"}).fill("Shiny");
  await page.getByRole("button",{name:"Keresés",exact:true}).click();
  await expect(page.locator("#curseforge-catalog").getByRole("heading",{name:"Example Search Result"}).first()).toBeVisible();
  await expect(page.getByText("Synthetic provider description, inert plain text")).toBeVisible();
  await expect(page.getByText("Example provider file")).toBeVisible();
  await expect(page.getByText(/456789 \(RequiredDependency\)/)).toBeVisible();
  await page.getByRole("button",{name:"További fájlverziók"}).click();
  await expect(page.getByText("Example older version")).toBeVisible();
  await page.getByRole("button",{name:/Projekt ID előkészítése/}).click();
  await expect(page.getByRole("textbox",{name:"Modprojekt ID"})).toHaveValue("123456");
  await page.screenshot({path:"test-results/curseforge-galaxy-1600.png",fullPage:true});
});

test("map header always shows four real lifecycle actions and Kill requires native typed approval",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"The Island részletei"}).click();
  const bar=page.getByRole("region",{name:"Helyi map életciklus vezérlése"});
  for(const action of ["Indítás","Újraindítás","Leállítás","Kill"])
    await expect(bar.getByRole("button",{name:action,exact:true})).toBeVisible();
  await expect(bar.getByRole("button",{name:"Indítás",exact:true})).toBeDisabled();
  await expect(bar.getByRole("button",{name:"Kill",exact:true})).toBeEnabled();
  await bar.getByRole("button",{name:"Kill",exact:true}).click();
  const approval=bar.getByRole("region",{name:"Kill jóváhagyása"});
  await expect(approval).toContainText("nem küld SaveWorld parancsot");
  const apply=approval.getByRole("button",{name:"Művelet végrehajtása"});
  await expect(apply).toBeDisabled();
  await approval.getByRole("checkbox").check();
  await approval.getByRole("textbox",{name:"Életciklus művelet jóváhagyó szövege"})
    .fill("KILL LOCAL ASA");
  await apply.click();
  await expect(bar).toContainText("bizonyított helyi ASA PID megszűnt");
  await expect(bar.getByRole("button",{name:"Indítás",exact:true})).toBeEnabled();
});
test("original Kova Store maps retain four buttons but disable destructive Kill",async({page})=>{
  await page.addInitScript(()=>{(window as any).__UNTAMED_PROTECT_TEST_MAP__?.();});
  await page.goto("/");
  await page.getByRole("button",{name:"Astraeos részletei"}).click();
  const bar=page.getByRole("region",{name:"Helyi map életciklus vezérlése"});
  await expect(bar.getByRole("button",{name:"Indítás",exact:true})).toBeVisible();
  await expect(bar.getByRole("button",{name:"Kill",exact:true})).toBeDisabled();
  await expect(bar.getByRole("button",{name:"Indítás",exact:true})).toBeEnabled();
});
test("SteamCMD install shows exact overlapping map and offers existing ASA import",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Új ASA letöltése"}).click();
  await page.getByRole("textbox",{name:"Új ASA célmappa"}).fill("D:\\ASA-Servers");
  await page.getByRole("button",{name:"ASA letöltés indítása"}).click();
  await expect(page.getByRole("alert").filter({hasText:"Ütköző összetevő: The Island"}))
    .toBeVisible();
  await expect(page.getByRole("alert").filter({hasText:"Foglalt könyvtár:"})).toContainText("ASA-Servers");
  await page.getByRole("button",{name:"Már telepített ASA-könyvtár importálása"}).click();
  await expect(page.getByText(/Meglévő ASA/).first()).toBeVisible();
});
test("stopped SteamCMD update displays exact local process and reserve evidence",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("tab",{name:"Frissítés"}).click();
  await page.getByRole("button",{name:"Frissítés előellenőrzése"}).click();
  const diagnostics=page.getByRole("region",{name:"SteamCMD natív előfeltétel diagnosztika"});
  await expect(diagnostics).toContainText("Astraeos");
  await expect(diagnostics).toContainText("Leállítás igazolva");
  await expect(diagnostics).toContainText("Szabad tárhely: 50 GiB");
  await expect(diagnostics).toContainText("SteamCMD: Telepítve");
  await expect(diagnostics).toContainText(
    "Közös telepítés tulajdonjoga: ellenőrzött, kizárólag leállított nem Store mapok");
});

test("cluster bulk SteamCMD queues only explicitly approved stopped local map beyond page 1",async({page})=>{
  await page.goto("/?fixture=modLarge");
  await page.getByRole("navigation",{name:"Fő navigáció"})
    .getByRole("button",{name:"Cluster",exact:true}).click();
  const panel=page.getByRole("region",{name:"Helyi mapok soros SteamCMD frissítése"});
  const targets=panel.getByRole("group",{name:"Soros SteamCMD célmapok"});
  await expect(targets.getByRole("checkbox")).toHaveCount(25);
  await panel.getByRole("button",{name:"Következő SteamCMD-oldal"}).click();
  await expect(targets).toContainText("Local Map 60");
  await panel.getByRole("searchbox",{name:"Soros SteamCMD célmap keresése"})
    .fill("Local Map 60");
  await expect(targets.getByRole("checkbox")).toHaveCount(1);
  await targets.getByRole("checkbox").check();
  await panel.getByRole("button",{name:/map natív frissítési előellenőrzése/}).click();
  const consent=panel.getByRole("region",{name:"Soros SteamCMD jóváhagyása"});
  await expect(consent).toContainText("Local Map 60");
  const apply=consent.getByRole("button",{name:"Helyi soros SteamCMD frissítés indítása"});
  await expect(apply).toBeDisabled();
  await consent.getByRole("textbox",{name:"Soros SteamCMD jóváhagyó szöveg"})
    .fill("HELYI SOROS");
  await expect(apply).toBeDisabled();
  await consent.getByRole("textbox",{name:"Soros SteamCMD jóváhagyó szöveg"})
    .fill("HELYI SOROS STEAMCMD");
  await apply.click();
  await expect(panel).toContainText("SteamCMD: completed");
  expect(await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_STEAM_BATCH_APPLIES__?:()=>number
  }).__UNTAMED_STEAM_BATCH_APPLIES__?.())).toBe(1);
  await expect(consent).toHaveCount(0);
});

test("per-map Steam build check compares installed/public IDs without starting update or stopping ASA",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("tab",{name:"Frissítés"}).click();
  await page.getByRole("button",{name:"Elérhető ASA verzió ellenőrzése"}).click();
  const release=page.getByRole("region",{name:"ASA Steam build összehasonlítása"});
  await expect(release).toContainText("Astraeos");
  await expect(release).toContainText("Új Steam build elérhető");
  await expect(release).toContainText("Telepített ASA build: 123456");
  await expect(release).toContainText("Nyilvános Steam build: 123457");
  await expect(release).toContainText("nem állít le szervert");
  await expect(page.getByRole("region",{name:"SteamCMD frissítés jóváhagyása"}))
    .toHaveCount(0);
});

test("SteamCMD new ASA: choose map and Windows folder, one click starts download and registers verified files",async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Új map",exact:true}).click();
  await expect(page.getByText("Új ASA szerver · SteamCMD telepítés")).toBeVisible();
  await page.getByRole("combobox",{name:"Új ASA map"}).selectOption("Valguero");
  await page.getByRole("button",{name:"Mappa kiválasztása"}).click();
  await expect(page.getByRole("textbox",{name:"Új ASA célmappa"}))
    .toHaveValue("D:\\ASA-NEW");
  await expect(page.getByRole("button",{name:"ASA letöltés indítása"})).toBeEnabled();
  await page.getByRole("button",{name:"ASA letöltés indítása"}).click();
  await expect(page.getByText(/ASA EXE és Steam appmanifest ellenőrizve/))
    .toBeVisible({timeout:10000});
  await expect(page.getByText(/letöltve és hozzáadva a Managerhez/))
    .toBeVisible({timeout:10000});
  await expect(page.getByText("Valguero").first()).toBeVisible();
  await expect(page.getByRole("textbox",{name:"SteamCMD EXE"})).toHaveCount(0);
  await page.screenshot({path:"test-results/steam-fresh-install-1600.png",fullPage:true});
});

test("custom ASA map SteamCMD installer requires exact level, rejects injected flags, then downloads base files only",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Új map",exact:true}).click();
  await page.getByRole("combobox",{name:"Új ASA map"}).selectOption("__custom__");
  await expect(page.getByRole("textbox",{name:"Egyedi ASA map neve"})).toBeVisible();
  await page.getByRole("textbox",{name:"Egyedi ASA map neve"}).fill("Synthetic Atlantis");
  await page.getByRole("textbox",{name:"Új ASA célmappa"}).fill("D:\\CustomASA");
  await page.getByRole("textbox",{name:"Egyedi ASA level azonosító"})
    .fill("Bad?listen -admincheat");
  await page.getByRole("button",{name:"ASA letöltés indítása"}).click();
  await expect(page.getByRole("alert")).toContainText("pontos, legfeljebb 80 karakteres Unreal level");
  await page.getByRole("textbox",{name:"Egyedi ASA level azonosító"})
    .fill("SyntheticAtlantis_WP");
  await page.getByRole("button",{name:"ASA letöltés indítása"}).click();
  await expect(page.getByText(/ASA EXE és Steam appmanifest ellenőrizve/))
    .toBeVisible({timeout:12000});
  await expect(page.getByText(/letöltve és hozzáadva a Managerhez/))
    .toBeVisible({timeout:12000});
  await expect(page.getByText(/Az egyedi pálya modjait később a Mod Centerben/))
    .toBeVisible();
});

test("Servers page offers immediate real ASA download separately from importing an existing server",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await expect(page.getByRole("button",{name:"Új ASA letöltése"})).toBeVisible();
  await expect(page.getByRole("button",{name:"Meglévő ASA importálása"})).toBeVisible();
  await page.getByRole("button",{name:"Új ASA letöltése"}).click();
  await expect(page.getByText("Új ASA szerver · SteamCMD telepítés")).toBeVisible();
  await page.getByRole("combobox",{name:"Új ASA map"}).selectOption("Ragnarok");
  await page.getByRole("textbox",{name:"Új ASA célmappa"}).fill("D:\\ASA-Servers\\Ragnarok");
  await page.getByRole("button",{name:"ASA letöltés indítása"}).click();
  await expect(page.getByText(/letöltve és hozzáadva a Managerhez/))
    .toBeVisible({timeout:12000});
});

test("SteamCMD simple path entry starts installation without advanced options or extra confirmation",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Új map",exact:true}).click();
  await page.getByRole("textbox",{name:"Új ASA célmappa"}).fill("D:\\ASA-Servers\\TheIsland");
  await expect(page.getByRole("button",{name:"ASA letöltés indítása"})).toBeEnabled();
  await page.getByRole("button",{name:"ASA letöltés indítása"}).click();
  await expect(page.getByText(/letöltve és hozzáadva a Managerhez/))
    .toBeVisible({timeout:12000});
  await expect(page.getByRole("checkbox",{name:/Korábban megszakadt telepítés/})).toHaveCount(0);
});

test("Newly imported local map first setup saves session and UDP ports only after preview and confirmation",async ({page})=>{
  await page.setViewportSize({width:1500,height:850});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Meglévő ASA importálása"}).click();
  await page.getByRole("button",{name:"Genesis Part 1",exact:true}).click();
  await page.getByRole("textbox",{name:"Helyi ASA mappa"}).fill("C:\\ASA-LAB");
  await page.getByRole("button",{name:"Helyi telepítés ellenőrzése"}).click();
  await page.getByRole("button",{name:"Ellenőrzött map regisztrálása"}).click();
  await page.getByRole("row",{name:/Genesis Part 1/}).click();
  await expect(page.getByText("Első mapbeállítások")).toBeVisible();
  await page.getByRole("textbox",{name:"ASA szerver neve"}).fill("Untamed Kingdom Valued Genesis");
  await page.getByRole("textbox",{name:"Első Game UDP"}).fill("7797");
  await page.getByRole("textbox",{name:"Első Query UDP"}).fill("27035");
  await page.getByRole("button",{name:"Beállítások ellenőrzése"}).click();
  await expect(page.getByText("Konfigurációs előnézet")).toBeVisible();
  await expect(page.getByText("Genesis_WP")).toBeVisible();
  await expect(page.getByRole("button",{name:"Jóváhagyás és első konfiguráció mentése"})).toBeDisabled();
  await page.getByText(/Jóváhagyom a regisztrált helyi map Manager-profiljának mentését/).click();
  await page.getByRole("button",{name:"Jóváhagyás és első konfiguráció mentése"}).click();
  await expect(page.getByText(/indítási profilja mentve/)).toBeVisible();
  await expect(page.getByText("7797").first()).toBeVisible();
  await page.screenshot({path:"test-results/asa-first-map-setup-1500.png",fullPage:true});
});

test("Imported custom map requires a safe explicit Unreal level before initial Manager profile save",async({page})=>{
  await page.setViewportSize({width:1500,height:850});
  await page.goto("/");
  await page.evaluate(()=>{
    (window as typeof window & {__UNTAMED_CUSTOM_INITIAL_LEVEL__?:(v:boolean)=>void})
      .__UNTAMED_CUSTOM_INITIAL_LEVEL__?.(true);
  });
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Meglévő ASA importálása"}).click();
  await page.getByRole("button",{name:"Genesis Part 1",exact:true}).click();
  await page.getByRole("textbox",{name:"Helyi ASA mappa"}).fill("C:\\ASA-LAB");
  await page.getByRole("button",{name:"Helyi telepítés ellenőrzése"}).click();
  await page.getByRole("button",{name:"Ellenőrzött map regisztrálása"}).click();
  await page.getByRole("row",{name:/Genesis Part 1/}).click();
  await expect(page.getByRole("textbox",{name:"Egyedi ASA pályaszint"})).toBeVisible();
  await expect(page.getByText(/nem engedélyez Store-v1 transfert/)).toBeVisible();
  await page.getByRole("textbox",{name:"ASA szerver neve"}).fill("Untamed Custom Test");
  await page.getByRole("textbox",{name:"Első Game UDP"}).fill("7797");
  await page.getByRole("textbox",{name:"Első Query UDP"}).fill("27035");
  const level=page.getByRole("textbox",{name:"Egyedi ASA pályaszint"});
  const inspect=page.getByRole("button",{name:"Beállítások ellenőrzése"});
  await expect(inspect).toBeDisabled();
  await level.fill("Custom_WP?listen");
  await expect(inspect).toBeDisabled();
  await level.fill("Atlantis_Custom_WP");
  await expect(inspect).toBeEnabled();
  await inspect.click();
  await expect(page.getByText("Konfigurációs előnézet")).toBeVisible();
  await expect(page.getByText("Atlantis_Custom_WP")).toBeVisible();
  await expect(page.getByRole("button",{name:"Jóváhagyás és első konfiguráció mentése"})).toBeDisabled();
  await page.getByText(/Jóváhagyom a regisztrált helyi map Manager-profiljának mentését/).click();
  await page.getByRole("button",{name:"Jóváhagyás és első konfiguráció mentése"}).click();
  await expect(page.getByText(/indítási profilja mentve/)).toBeVisible();
});

test("first setup follows one RCON bootstrap route and returns to native Start without automatic launch",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Meglévő ASA importálása"}).click();
  await page.getByRole("button",{name:"Genesis Part 1",exact:true}).click();
  await page.getByRole("textbox",{name:"Helyi ASA mappa"}).fill("C:\\ASA-LAB");
  await page.getByRole("button",{name:"Helyi telepítés ellenőrzése"}).click();
  await page.getByRole("button",{name:"Ellenőrzött map regisztrálása"}).click();
  await page.getByRole("row",{name:/Genesis Part 1/}).click();
  await page.getByRole("button",{name:"Beállítások ellenőrzése"}).click();
  await page.getByText(/Jóváhagyom a regisztrált helyi map Manager-profiljának mentését/).click();
  await page.getByRole("button",{name:"Jóváhagyás és első konfiguráció mentése"}).click();
  const bootstrap=page.getByRole("button",{name:"Első RCON beállítása"});
  await expect(bootstrap).toBeVisible();
  await bootstrap.click();
  await expect(page.getByRole("heading",{name:"RCON konzol",exact:true})).toBeVisible();
  await page.getByRole("textbox",{name:"RCON beállítás jelszó"}).fill("SyntheticSafePassword2026!");
  await page.getByRole("button",{name:"Beállítás ellenőrzése"}).click();
  await expect(page.getByRole("region",{name:"RCON beállítás jóváhagyás"})).toBeVisible();
  await page.getByRole("button",{name:"Beállítás véglegesítése"}).click();
  await expect(page.getByRole("button",{name:"Vissza az ASA Start előnézethez"})).toBeVisible();
  await page.getByRole("button",{name:"Vissza az ASA Start előnézethez"}).click();
  await expect(page.getByRole("region",{name:"Valódi ASA első indítása"})).toBeVisible();
  await expect(page.getByRole("button",{name:"ASA szerver indítása",exact:true})).toHaveCount(0);
  await expect(page.getByRole("button",{name:"Első RCON beállítása"})).toHaveCount(0);
});

test("RCON native commit receipt survives a failed subsequent status refresh without reapplying INI",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Meglévő ASA importálása"}).click();
  await page.getByRole("button",{name:"Genesis Part 1",exact:true}).click();
  await page.getByRole("textbox",{name:"Helyi ASA mappa"}).fill("C:\\ASA-LAB");
  await page.getByRole("button",{name:"Helyi telepítés ellenőrzése"}).click();
  await page.getByRole("button",{name:"Ellenőrzött map regisztrálása"}).click();
  await page.getByRole("row",{name:/Genesis Part 1/}).click();
  await page.getByRole("button",{name:"Beállítások ellenőrzése"}).click();
  await page.getByText(/Jóváhagyom a regisztrált helyi map Manager-profiljának mentését/).click();
  await page.getByRole("button",{name:"Jóváhagyás és első konfiguráció mentése"}).click();
  await page.getByRole("button",{name:"Első RCON beállítása"}).click();
  await page.getByRole("textbox",{name:"RCON beállítás jelszó"}).fill("SyntheticSafePassword2026!");
  await page.getByRole("button",{name:"Beállítás ellenőrzése"}).click();
  await page.evaluate(()=>(window as typeof window&{
    __UNTAMED_RCON_REFRESH_FAIL_ONCE__?:()=>void
  }).__UNTAMED_RCON_REFRESH_FAIL_ONCE__?.());
  await page.getByRole("button",{name:"Beállítás véglegesítése"}).click();
  await expect(page.getByText(/natív RCON-konfiguráció mentve, de az állapotfrissítés nem sikerült/i)).toBeVisible();
  await expect(page.getByRole("button",{name:"Vissza az ASA Start előnézethez"})).toBeVisible();
  await expect(page.getByText(/A natív RCON-beállítás nem fejeződött be/)).toHaveCount(0);
  const applyCount=await page.evaluate(()=>(window as typeof window&{
    __UNTAMED_RCON_APPLY_COUNT__?:()=>number
  }).__UNTAMED_RCON_APPLY_COUNT__?.());
  expect(applyCount).toBe(1);
  await page.getByRole("button",{name:"Vissza az ASA Start előnézethez"}).click();
  await expect(page.getByRole("region",{name:"Valódi ASA első indítása"})).toBeVisible();
  await expect(page.getByRole("button",{name:"ASA szerver indítása",exact:true})).toHaveCount(0);
});

test("original local Store-v1 five-map source proof is explicit and never enables launch",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  await expect(page.getByRole("heading",{name:"Eredeti helyi Store-v1 állapot · közvetlen, csak olvasási ellenőrzés"})).toBeVisible();
  await page.getByRole("button",{name:"Eredeti helyi Store állapotának ellenőrzése"}).click();
  const proof=page.getByRole("region",{name:"Korábbi helyi Store-v1 állapot"});
  await expect(proof).toContainText("TheIsland_WP");
  await expect(proof).toContainText("Extinction_WP");
  await expect(proof).toContainText("Mind az öt map ACTIVE");
  await expect(proof).toContainText("Kétgépes élő állapot / karakterutazás");
  await expect(proof).toContainText("NEM IGAZOLT");
  await expect(proof.getByRole("button",{name:/regisztrálása/})).toHaveCount(0);
});

test("verified historical archive and original local four Store files show byte parity only",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  const source=page.getByRole("textbox",{name:"Korábbi Manager archívum"});
  const match=page.getByRole("button",{name:"Helyi Store és archivált ZIP egyeztetése"});
  await expect(match).toBeDisabled();
  await source.fill("C:\\Backups\\OriginalSource.zip");
  await expect(match).toBeEnabled();
  await match.click();
  const panel=page.getByRole("region",{name:"Korábbi helyi Store-v1 állapot"});
  await expect(panel).toContainText("BÁJT-PONTOSAN EGYEZIK");
  await expect(panel).toContainText("39 fájl ellenőrizve");
  await expect(panel).toContainText("Éles karakterutazás");
  await expect(panel).toContainText("NEM IGAZOLT");
  await source.fill("C:\\Backups\\ChangedArchive.zip");
  await expect(panel.getByText("BÁJT-PONTOSAN EGYEZIK")).toHaveCount(0);
});

test("Store-v1 direct original local Kova map registration is operator-approved and preserves identity",async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  const section=page.getByRole("region",{name:"Közvetlen Kova Store-v1 mapimport"});
  await section.getByRole("button",{name:"Öt eredeti Kova-map felismerése"}).click();
  await expect(section.getByRole("combobox",{name:"Közvetlen Kova-map"})).toBeVisible();
  await section.getByRole("combobox",{name:"Közvetlen Kova-map"})
    .selectOption("00000000-0000-4000-8000-000000009001");
  await expect(section.getByRole("spinbutton",{name:"Közvetlen Game port"}))
    .toHaveValue("7777");
  await expect(section.getByRole("spinbutton",{name:"Közvetlen Query port"}))
    .toHaveValue("27015");
  await section.getByRole("spinbutton",{name:"Közvetlen Query port"}).fill("7777");
  await section.getByRole("button",{
    name:"Eredeti map regisztrációjának előellenőrzése"
  }).click();
  await expect(section.getByRole("alert")).toContainText("két különböző");
  await section.getByRole("spinbutton",{name:"Közvetlen Query port"}).fill("27015");
  await section.getByRole("button",{
    name:"Eredeti map regisztrációjának előellenőrzése"
  }).click();
  const confirm=section.getByRole("region",{name:"Közvetlen Kova-import jóváhagyása"});
  await expect(confirm).toContainText("Megmarad");
  await expect(confirm).toContainText("sorrend megőrizve");
  await expect(confirm).toContainText("Változatlanok");
  await expect(confirm.getByRole("button",{name:"Eredeti map hozzáadása"})).toBeDisabled();
  await confirm.getByRole("checkbox").check();
  await confirm.getByRole("button",{name:"Eredeti map hozzáadása"}).click();
  await expect(section).toContainText("The Island felvéve az eredeti GUID-dal");
  await expect(section.getByRole("combobox",{name:"Közvetlen Kova-map"}))
    .toHaveValue("");
});

test("original Kova Cluster registry adoption requires exact typed approval and never writes Store data",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  const panel=page.getByRole("region",{name:"Eredeti Kova Cluster-regiszter átvétele"});
  await panel.getByRole("button",{name:"Eredeti Cluster-regiszter előellenőrzése"}).click();
  const confirmation=panel.getByRole("region",{name:"Eredeti Cluster-regiszter jóváhagyása"});
  await expect(confirmation).toContainText("KovaCluster");
  await expect(confirmation).toContainText("Hozzáadandó");
  const approval=confirmation.getByRole("button",{name:"Eredeti map-tagság rögzítése"});
  await expect(approval).toBeDisabled();
  await confirmation.getByRole("textbox",{name:"Eredeti Cluster-regiszter jóváhagyó szöveg"})
    .fill("EREDETI KOVA");
  await expect(approval).toBeDisabled();
  await confirmation.getByRole("textbox",{name:"Eredeti Cluster-regiszter jóváhagyó szöveg"})
    .fill("EREDETI KOVA REGISZTER");
  await expect(approval).toBeEnabled();
  await approval.click();
  await expect(panel).toContainText("5 eredeti map-tagság átvezetve");
  expect(await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_KOVA_REGISTRY_APPLIES__?:()=>number
  }).__UNTAMED_KOVA_REGISTRY_APPLIES__?.())).toBe(1);
  await expect(confirmation).toHaveCount(0);
  await panel.getByRole("button",{name:"Eredeti Cluster-regiszter előellenőrzése"}).click();
  await expect(panel.getByRole("region",{name:"Eredeti Cluster-regiszter jóváhagyása"}))
    .toContainText("0");
});

test("original Kova batch import requires one consent and registers all missing local maps atomically",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  const section=page.getByRole("region",{name:"Közvetlen Kova Store-v1 mapimport"});
  await section.getByRole("button",{name:"Öt eredeti Kova-map felismerése"}).click();
  await section.getByRole("button",{
    name:"Összes hiányzó helyi map átvétele egy lépésben"
  }).click();
  const approval=section.getByRole("region",{
    name:"Összes eredeti Kova-map importálásának jóváhagyása"
  });
  await expect(approval).toContainText("The Island");
  await expect(approval.getByRole("button",{name:"1 map hozzáadása"})).toBeDisabled();
  await approval.getByRole("checkbox").check();
  await approval.getByRole("button",{name:"1 map hozzáadása"}).click();
  await expect(section).toContainText("1 eredeti map atomikusan felvéve");
  await expect(section.getByRole("combobox",{name:"Közvetlen Kova-map"})).toHaveValue("");
});

test("Store-v1 historic import preserves map identity and never permits launch before real validation",async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  await expect(page.getByText(/Untamed Store-v1 · meglévő map átvétele/)).toBeVisible();
  await page.getByRole("textbox",{name:"Korábbi Manager archívum"}).fill("C:\\Backups\\KovaManager-Fontos.zip");
  await page.getByRole("button",{name:"Archívum ellenőrzése"}).click();
  await expect(page.getByRole("combobox",{name:"Archivált Store-v1 map"})).toBeVisible();
  await page.getByRole("textbox",{name:"Korábbi ASA map mappája"}).fill("C:\\ASA\\TestExistingMap");
  await page.getByRole("button",{name:"Map és Store-adatok előellenőrzése"}).click();
  await expect(page.getByText(/Teljes launch argumentumok/)).toBeVisible();
  await expect(page.getByText("Külön ellenőrzés és jóváhagyás")).toBeVisible();
  await expect(page.getByRole("button",{name:"Védett Store-v1 map regisztrálása"})).toBeDisabled();
  await page.getByText(/Jóváhagyom a kizárólag Manager-profilba történő, zárolt regisztrációt/).click();
  await page.getByRole("button",{name:"Védett Store-v1 map regisztrálása"}).click();
  await expect(page.getByText(/A map saját vezérlőiben RCON-átvétel és natív Start-előellenőrzés után indítható/)).toBeVisible();
  await expect(page.getByRole("row",{name:/Scorched Earth/})).toBeVisible();
  await page.screenshot({path:"test-results/store-v1-locked-import-1600.png",fullPage:true});
});

test("Store-v1 cross-host evidence distinguishes historical files from live writer authorization", async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  await page.getByRole("textbox",{name:"Store koordináció ZIP"}).fill("D:\\Store-Coordination.zip");
  await page.getByRole("textbox",{name:"Másik Store host ZIP"}).fill("D:\\Other-Host-Local.zip");
  await page.getByRole("button",{name:"Két host Store-adatainak összevetése"}).click();
  const report=page.getByRole("status").filter({hasText:"Csak offline bizonyíték"});
  await expect(report).toBeVisible();
  await expect(report.getByText("NEM IGAZOLT").first()).toBeVisible();
  await expect(report.getByText("LocalLock")).toBeVisible();
  await expect(report.getByText("TILTOTT")).toBeVisible();
  await page.screenshot({path:"test-results/store-v1-peer-evidence-1600.png",fullPage:true});
});


test("Store-v1 ignores stale preview replies after the ASA root changes",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  await page.getByRole("textbox",{name:"Korábbi Manager archívum"}).fill("C:\\Backups\\KovaManager-Fontos.zip");
  await page.getByRole("button",{name:"Archívum ellenőrzése"}).click();
  await expect(page.getByRole("combobox",{name:"Archivált Store-v1 map"})).toBeVisible();
  await page.getByRole("textbox",{name:"Korábbi ASA map mappája"}).fill("C:\\ASA\\Original");
  await page.evaluate(()=>{
    const host=window as unknown as {chrome:{webview:{postMessage:(message:{method:string})=>void}},releasePreview?:()=>void};
    const bridge=host.chrome.webview;
    const original=bridge.postMessage.bind(bridge);
    let delayed:{method:string}|undefined;
    let intercepted=false;
    bridge.postMessage=(message)=>{
      if(message.method==="historical.preview"&&!intercepted){intercepted=true;delayed=message;return;}
      original(message);
    };
    host.releasePreview=()=>{if(delayed){const message=delayed;delayed=undefined;original(message);}};
  });
  await page.getByRole("button",{name:"Map és Store-adatok előellenőrzése"}).click();
  await page.getByRole("textbox",{name:"Korábbi ASA map mappája"}).fill("C:\\ASA\\Changed");
  await page.evaluate(()=>{(window as unknown as {releasePreview:()=>void}).releasePreview();});
  await expect(page.getByText("Teljes launch argumentumok")).toHaveCount(0);
  await expect(page.getByRole("button",{name:"Védett Store-v1 map regisztrálása"})).toHaveCount(0);
  await page.getByRole("button",{name:"Map és Store-adatok előellenőrzése"}).click();
  await expect(page.getByText("Teljes launch argumentumok")).toBeVisible();
  await expect(page.getByRole("button",{name:"Védett Store-v1 map regisztrálása"})).toBeDisabled();
});

test("Store-v1 registration locks source inputs until native receipt",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  await page.getByRole("textbox",{name:"Korábbi Manager archívum"}).fill("C:\\Backups\\KovaManager-Fontos.zip");
  await page.getByRole("button",{name:"Archívum ellenőrzése"}).click();
  await expect(page.getByRole("combobox",{name:"Archivált Store-v1 map"})).toBeVisible();
  await page.getByRole("textbox",{name:"Korábbi ASA map mappája"}).fill("C:\\ASA\\TestExistingMap");
  await page.getByRole("button",{name:"Map és Store-adatok előellenőrzése"}).click();
  await expect(page.getByText("Teljes launch argumentumok")).toBeVisible();
  await page.evaluate(()=>{
    const host=window as unknown as {chrome:{webview:{postMessage:(message:{method:string})=>void}},releaseRegistration?:()=>void};
    const bridge=host.chrome.webview;
    const original=bridge.postMessage.bind(bridge);
    let delayed:{method:string}|undefined;
    bridge.postMessage=(message)=>{
      if(message.method==="historical.register"){delayed=message;return;}
      original(message);
    };
    host.releaseRegistration=()=>{if(delayed){const message=delayed;delayed=undefined;original(message);}};
  });
  await page.getByText(/Jóváhagyom a kizárólag Manager-profilba történő, zárolt regisztrációt/).click();
  await page.getByRole("button",{name:"Védett Store-v1 map regisztrálása"}).click();
  await expect(page.getByRole("textbox",{name:"Korábbi Manager archívum"})).toBeDisabled();
  await expect(page.getByRole("textbox",{name:"Korábbi ASA map mappája"})).toBeDisabled();
  await expect(page.getByRole("combobox",{name:"Archivált Store-v1 map"})).toBeDisabled();
  await page.evaluate(()=>{(window as unknown as {releaseRegistration:()=>void}).releaseRegistration();});
  await expect(page.getByText(/A map saját vezérlőiben RCON-átvétel és natív Start-előellenőrzés után indítható/)).toBeVisible();
  await expect(page.getByRole("textbox",{name:"Korábbi Manager archívum"})).toBeEnabled();
});

test("Store-v1 original ASA launch text stays read-only and redacted",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Store-v1 átvétel"}).click();
  await page.getByRole("textbox",{name:"Korábbi Manager archívum"}).fill("C:\\Backups\\KovaManager-Fontos.zip");
  await page.getByRole("button",{name:"Archívum ellenőrzése"}).click();
  await expect(page.getByRole("combobox",{name:"Archivált Store-v1 map"})).toBeVisible();
  const secret="SYNTHETIC_PRIVATE_ADMIN_SECRET";
  const launch=page.getByRole("textbox",{name:"Eredeti ASA indítási sor"});
  await launch.fill("ArkAscendedServer.exe ScorchedEarth_WP?ServerAdminPassword="+secret+
    " -port=7777 -QueryPort=27015 -mods=928548 -clusterid=test -ClusterDirOverride=C:\\Cluster -usestore -converttostore");
  await page.getByRole("button",{name:"Indítási paraméterek összevetése"}).click();
  const report=page.getByRole("status").filter({hasText:"Game UDP"});
  await expect(report.getByText("MATCH").first()).toBeVisible();
  await expect(report.getByText("VESZÉLY: szerepel")).toBeVisible();
  await expect(report.getByText("TILTOTT")).toBeVisible();
  // The original command is sensitive. The native classification reply must
  // not echo its password, and the form must clear the local input on success.
  await expect(launch).toHaveValue("");
  await expect(report).not.toContainText(secret);
  await launch.fill("ArkAscendedServer.exe ScorchedEarth_WP -port=9999");
  await expect(report).toHaveCount(0);
});

test("map detail keeps Plugin Center and ASA API controls bound to its own local map",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"The Island részletei"}).click();
  await page.getByRole("tab",{name:"Pluginok / ASA API"}).click();
  await expect(page.getByRole("heading",{name:"Plugin Center"})).toBeVisible();
  await expect(page.getByLabel("Pluginok forrásmapja")).toHaveText("The Island");
  await expect(page.getByRole("combobox",{name:"Pluginok mapja"})).toHaveCount(0);
  await expect(page.getByRole("tablist",{name:"Map funkciói"})).toBeVisible();
  await expect(page.getByRole("heading",{name:"The Island",exact:true})).toBeVisible();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("tab",{name:"Pluginok / ASA API"}).click();
  await expect(page.getByLabel("Pluginok forrásmapja")).toHaveText("Astraeos");
  await expect(page.getByRole("combobox",{name:"Pluginok mapja"})).toHaveCount(0);
});

test("Plugin Center uses native stopped-map JSON editing with preview and protective save",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Pluginok",exact:true}).click();
  await page.getByRole("combobox",{name:"Pluginok mapja"}).selectOption(second);
  await page.getByRole("button",{name:"config.json szerkesztése"}).click();
  const editor=page.getByRole("region",{name:"Plugin JSON szerkesztő"});
  await expect(editor).toBeVisible();
  await editor.getByRole("textbox",{name:"Plugin JSON tartalma"}).fill('{"enabled":false,"slot":2}');
  await editor.getByRole("button",{name:"JSON és változások ellenőrzése"}).click();
  const approval=editor.getByRole("region",{name:"Plugin JSON módosítás jóváhagyása"});
  await expect(approval).toContainText("Változott: igen");
  await expect(approval.getByRole("button",{name:"Mentés véglegesítése"})).toBeDisabled();
  await approval.getByRole("checkbox").check();
  await approval.getByRole("button",{name:"Mentés véglegesítése"}).click();
  await expect(editor).toContainText("védőmásolattal");
});

test("SQL-free restore always targets isolated TEMP lab and requires explicit consent",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Mentések",exact:true}).click();
  const panel=page.getByRole("region",{name:"SQL-mentes próba-visszaállítás"});
  await expect(panel).toBeVisible();
  await panel.getByRole("button",{name:"Offline archívum kiválasztása"}).click();
  const approval=panel.getByRole("region",{name:"Próba-visszaállítás jóváhagyása"});
  await expect(approval).toContainText("Untamed-ASA-Offline-synthetic");
  await expect(approval).toContainText("TEMP laborba");
  await expect(approval.getByRole("button",{name:"Próba-visszaállítás jóváhagyása"})).toBeDisabled();
  await approval.getByRole("checkbox").check();
  await approval.getByRole("button",{name:"Próba-visszaállítás jóváhagyása"}).click();
  await panel.getByRole("button",{name:"Státusz frissítése"}).click();
  await expect(panel).toContainText("10 map, 12 világfájl SHA-256 ellenőrizve");
  await expect(panel).toContainText("Játékbeli működés nem igazolt");
});

test("original five-map Kova backup requires one confirmed user action and uses verified per-map points",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_FIVE_BACKUP_FIXTURE__?:()=>void;
  }).__UNTAMED_FIVE_BACKUP_FIXTURE__?.());
  await page.getByRole("button",{name:"Állapot frissítése"}).click();
  await page.getByRole("button",{name:"Mentések",exact:true}).click();
  const section=page.getByRole("region",{name:"Eredeti Store-v1 ötmapos helyi mentés"});
  await expect(section).toContainText("Mind az 5 eredeti helyi map");
  await section.getByRole("button",{
    name:"Mind az 5 eredeti helyi map mentésének ellenőrzése"
  }).click();
  const approval=section.getByRole("region",{
    name:"Eredeti Store-v1 ötmapos mentés jóváhagyása"
  });
  await expect(approval).toContainText("Scorched Earth");
  const start=approval.getByRole("button",{name:"Öt map mentésének indítása"});
  await expect(start).toBeDisabled();
  await approval.getByRole("checkbox").check();
  await expect(start).toBeEnabled();
  await start.click();
  await expect(section).toContainText("Mind az öt eredeti helyi map ellenőrzött mentése elkészült");
  await expect(section).toContainText("a másik Manager mentése még külön szükséges");
  const count=await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_FIVE_BACKUP_COPIES__?:()=>number;
  }).__UNTAMED_FIVE_BACKUP_COPIES__?.());
  expect(count).toBe(1);
});

test("SQL-free offline cluster archive requires four native folders and explicit approval",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Mentések",exact:true}).click();
  const panel=page.getByRole("region",{name:"SQL-mentes clusterarchívum"});
  await expect(panel).toBeVisible();
  await expect(panel).toContainText("10 map · SQL nélkül");
  await expect(panel).toContainText("Game.ini");
  await expect(panel).toContainText("opcionális");
  await panel.getByRole("button",{name:"A négy offline mappa kiválasztása"}).click();
  const prepared=panel.getByRole("region",{name:"SQL-mentes clusterarchívum előnézete"});
  await expect(prepared).toContainText("HP: 5 map");
  await expect(prepared).toContainText("Ryzen: 5 map");
  await expect(prepared).toContainText("Mindkét gépről mellékelve");
  await expect(prepared.getByRole("button",{name:"Offline archívum készítése"})).toBeDisabled();
  await prepared.getByRole("checkbox").check();
  await prepared.getByRole("button",{name:"Offline archívum készítése"}).click();
  await panel.getByRole("button",{name:"Állapot ellenőrzése"}).click();
  await expect(panel).toContainText("10 map, 3 transferállomány és");
  await expect(panel).toContainText("20 konfigurációs állomány SHA-256-tal igazolva");
  await expect(panel).toContainText("nem élő többgépes pillanatkép");
  await expect(panel).not.toContainText("SQL mentés kész");
});

test("HP/Ryzen portable mod profiles require native selection and per-host local approval",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await page.getByRole("tab",{name:"Modprofilok – N host"}).click();
  const panel=page.getByRole("region",{name:"Többhostos modprofil átadás"});
  await expect(panel).toBeVisible();
  await panel.getByRole("button",{name:"Kijelölt map profiljának exportja"}).click();
  await expect(panel).toContainText("Astraeos-mods.json");
  await panel.getByRole("group",{name:"Modprofil célmapok"}).getByRole("checkbox").first().check();
  await panel.getByRole("button",{name:"JSON-profil kiválasztása és előnézete"}).click();
  const approval=panel.getByRole("region",{name:"Modprofil import előnézete"});
  await expect(approval).toContainText("Astraeos");
  await expect(approval).toContainText("SHA-256");
  await expect(approval.getByRole("button",{name:"Jóváhagyás és mentés"})).toBeDisabled();
  await approval.getByRole("checkbox").check();
  await approval.getByRole("button",{name:"Jóváhagyás és mentés"}).click();
  await expect(panel).toContainText("map módosítva");
  await expect(panel).toContainText("tényleges betöltését");
});

test("Mod Center Copy To merges selected local maps only after native diff approval",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await page.getByRole("tab",{name:"Másolás több mapra"}).click();
  const panel=page.getByRole("region",{name:"Modlista másolása több mapra"});
  await expect(panel).toBeVisible();
  await panel.getByRole("group",{name:"Célmapok kiválasztása"}).getByRole("checkbox").first().check();
  await panel.getByRole("button",{name:"Változások előnézete"}).click();
  const preview=panel.getByRole("region",{name:"Többmapos modmásolás előnézete"});
  await expect(preview).toContainText("Astraeos");
  await expect(preview).toContainText("Előtte:");
  await expect(preview).toContainText("Utána:");
  await expect(preview.getByRole("button",{name:"Jóváhagyás és mentés"})).toBeDisabled();
  await preview.getByRole("checkbox").check();
  await preview.getByRole("button",{name:"Jóváhagyás és mentés"}).click();
  await expect(panel).toContainText("helyi map modkonfigurációja mentve");
});

test("mod copy supports more than 25 local maps through paged selection without bypassing the native batch cap",async({page})=>{
  await page.goto("/?fixture=modLarge");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await page.getByRole("tab",{name:"Másolás több mapra"}).click();
  const panel=page.getByRole("region",{name:"Modlista másolása több mapra"});
  const targetGroup=panel.getByRole("group",{name:"Célmapok kiválasztása"});
  await expect(targetGroup.getByRole("checkbox")).toHaveCount(25);
  await panel.getByRole("button",{name:"Következő célmap-oldal"}).click();
  await expect(targetGroup).toContainText("Local Map 30");
  await panel.getByRole("searchbox",{name:"Modmásolás célmap keresése"}).fill("Local Map 60");
  await expect(targetGroup.getByRole("checkbox")).toHaveCount(1);
  await targetGroup.getByRole("checkbox").check();
  await panel.getByRole("button",{name:"Változások előnézete"}).click();
  await expect(panel.getByRole("region",{name:"Többmapos modmásolás előnézete"}))
    .toContainText("Local Map 60");
  await expect(panel).toContainText("1 / 64 helyi célmap");
  await expect(panel.getByRole("button",{name:"Jóváhagyás és mentés"})).toBeDisabled();
});

test("portable mod profile exposes the full local map roster by filtering and paging",async({page})=>{
  await page.goto("/?fixture=modLarge");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await page.getByRole("tab",{name:"Modprofilok – N host"}).click();
  const panel=page.getByRole("region",{name:"Többhostos modprofil átadás"});
  const targets=panel.getByRole("group",{name:"Modprofil célmapok"});
  await expect(targets.getByRole("checkbox")).toHaveCount(25);
  await panel.getByRole("button",{name:"Következő modprofil-oldal"}).click();
  await expect(targets).toContainText("Local Map 30");
  await panel.getByRole("searchbox",{name:"Modprofil célmap keresése"}).fill("Local Map 65");
  await expect(targets.getByRole("checkbox")).toHaveCount(1);
  await targets.getByRole("checkbox").check();
  await expect(panel).toContainText("1 / 65 helyi map");
  await expect(panel.getByRole("button",{name:"JSON-profil kiválasztása és előnézete"})).toBeEnabled();
});

test("cross-map mod inventory uses actual native contract, filtering and map selection",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await expect(page.getByRole("heading",{name:/^Modok –/})).toBeVisible();
  // Panel is a section with a heading; identify via its text instead of
  // depending on fragile CSS selectors.
  await page.getByRole("tab",{name:"Közös modok"}).click();
  const shared=page.getByRole("heading",{name:"Közös modok · helyi mapok"});
  await expect(shared).toBeVisible();
  await expect(page.getByText("Projekt #928548")).toBeVisible();
  await expect(page.getByText("Projekt #940975")).toHaveCount(0);
  await page.getByText("Csak több mapon").click();
  await expect(page.getByText("Projekt #940975")).toBeVisible();
  await page.getByRole("textbox",{name:"Közös modok keresése"}).fill("928548");
  await expect(page.getByText("Projekt #928548")).toBeVisible();
  await expect(page.getByText("Projekt #940975")).toHaveCount(0);
  await page.getByRole("button",{name:"Astraeos · Passzív #1"}).click();
  await expect(page.getByRole("combobox",{name:"Helyi map"})).toHaveValue("00000000-0000-4000-8000-000000000002");
});

test("plugin center requires native preview and explicit operator approval before toggle",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Pluginok"}).click();
  await expect(page.getByRole("heading",{name:"Plugin Center"})).toBeVisible();
  await expect(page.getByText("Engedélyezett mappa")).toBeVisible();
  await expect(page.getByRole("button",{name:"Pluginállapot módosításának jóváhagyása"})).toHaveCount(0);
  await page.getByRole("button",{name:"Letiltás előnézete"}).click();
  await expect(page.getByText("Megvizsgált helyi mapok")).toBeVisible();
  await expect(page.getByRole("button",{name:"Pluginállapot módosításának jóváhagyása"})).toBeDisabled();
  await page.getByText(/Jóváhagyom a helyi pluginmappa áthelyezését/).click();
  await page.getByRole("button",{name:"Pluginállapot módosításának jóváhagyása"}).click();
  await expect(page.getByText("Letiltott mappa")).toBeVisible();
  await expect(page.getByText(/teljes könyvtár mentése elkészült/)).toBeVisible();
  await expect(page.getByRole("button",{name:"Engedélyezés előnézete"})).toBeVisible();
});

test("API CORE rollback is a native opt-in two-stage workflow, never an automatic retry",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Pluginok"}).click();
  await page.getByRole("button",{name:"Korábbi API CORE telepítések lekérdezése"}).click();
  await expect(page.getByRole("combobox",{name:"Korábbi API CORE tranzakció"})).toBeVisible();
  await page.getByRole("button",{name:"Visszaállítási jóváhagyás előkészítése"}).click();
  const rollback=page.getByRole("region",{name:"API CORE visszaállítás jóváhagyása"});
  await expect(rollback).toBeVisible();
  const apply=page.getByRole("button",{name:"Eredeti API CORE binárisok visszaállítása"});
  await expect(apply).toBeDisabled();
  await rollback.getByText(/Kifejezetten jóváhagyom a visszaállítást/).click();
  await expect(apply).toBeEnabled();
  await apply.click();
  await expect(page.getByText(/API CORE visszaállítva: 1 eredeti bináris/)).toBeVisible();
  await expect(rollback).toHaveCount(0);
});

test("native plugin ZIP install has explicit preview, approval, and verified result",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Pluginok"}).click();
  const choose=page.getByRole("button",{name:"Plugin ZIP kiválasztása és ellenőrzése"});
  await expect(choose).toBeVisible();
  await choose.click();
  await expect(page.getByRole("region",{name:"Plugin ZIP telepítés jóváhagyása"})).toBeVisible();
  await expect(page.getByText("SampleZip-package.zip")).toBeVisible();
  const apply=page.getByRole("button",{name:"Plugin telepítésének véglegesítése"});
  await expect(apply).toBeDisabled();
  await page.getByText(/Jóváhagyom a kiválasztott helyi plugin/).click();
  await expect(apply).toBeEnabled();
  await apply.click();
  await expect(page.getByText(/SampleZip – 1 DLL telepítve/)).toBeVisible();
  await expect(page.getByRole("region",{name:"Plugin ZIP telepítés jóváhagyása"})).toHaveCount(0);
});

test("real stopped-map SteamCMD update is opt-in and reports verified outcome",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("button",{name:"Frissítés ellenőrzése"}).click();
  await expect(page.getByRole("region",{name:"Leállított map SteamCMD-frissítése"})).toBeVisible();
  await expect(page.getByRole("combobox",{name:"SteamCMD frissítés map"})).toHaveCount(0);
  await page.getByRole("button",{name:"Frissítés előellenőrzése"}).click();
  await expect(page.getByRole("region",{name:"SteamCMD frissítés jóváhagyása"})).toBeVisible();
  await expect(page.getByText(/Astraeos – jelenlegi build: 123456/)).toBeVisible();
  const start=page.getByRole("button",{name:"Valódi frissítés indítása"});
  await expect(start).toBeDisabled();
  await page.getByText(/Megerősítem, hogy a kiválasztott/).click();
  await expect(start).toBeEnabled();
  await start.click();
  await expect(page.getByText(/Build 123456 → 123457/)).toBeVisible({timeout:15000});
  await expect(page.getByRole("region",{name:"SteamCMD frissítés jóváhagyása"})).toHaveCount(0);
});

test("SteamCMD monitor reattaches to existing native job after WebView2 reload without another Steam update",async({page})=>{
  await page.goto("/?fixture=steamReattach");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("tab",{name:"Frissítés"}).click();
  const panel=page.getByRole("region",{name:"Leállított map SteamCMD-frissítése"});
  await expect(panel).toContainText("Frissítés: completed");
  await expect(panel).toContainText("Build 123456 → 123457");
  await expect(panel.getByRole("region",{name:"SteamCMD frissítés jóváhagyása"}))
    .toHaveCount(0);
});

test("interrupted SteamCMD queue survives UI restart and blocks accidental retry",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_STEAM_INTERRUPTED__?:()=>void
  }).__UNTAMED_STEAM_INTERRUPTED__?.());
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await page.getByRole("tab",{name:"Frissítés"}).click();
  await expect(page.getByText(/Helyi SteamCMD-napló: NeedsOperator/)).toBeVisible();
  await expect(page.getByText(/kézi ellenőrzés szükséges/)).toBeVisible();
  await expect(page.getByRole("button",{name:"Frissítés előellenőrzése"})).toBeDisabled();
});

test("fresh ASA map starts a real local process only after explicit approval",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await expect(page.getByRole("region",{name:"Valódi ASA első indítása"})).toBeVisible();
  await page.getByRole("button",{name:"Valódi ASA-indítás előellenőrzése"}).click();
  await expect(page.getByRole("region",{name:"Valódi ASA indítás jóváhagyása"})).toBeVisible();
  const start=page.getByRole("button",{name:"ASA szerver indítása",exact:true});
  await expect(start).toBeDisabled();
  await page.getByText(/Jóváhagyom az új, saját helyi ASA szerver/).click();
  await expect(start).toBeEnabled();
  await start.click();
  // Surface the actual visible native-bridge error when a UI regression
  // occurs, rather than a generic locator timeout with no diagnosis.
  await expect(page.locator(".success-inline").filter({hasText:"PID: 45678"}))
    .toBeVisible({timeout:12000});
  await expect(page.locator(".success-inline").filter({hasText:"folyamatindítás még nem bizonyítja"})).toBeVisible();
  await page.getByRole("button",{name:"ASA / RCON készenlét ellenőrzése"}).click();
  await expect(page.getByText("ASA készenléti szint: RconAuthenticated")).toBeVisible();
  await expect(page.getByText("RCON hitelesítve", {exact:true})).toBeVisible();
  await expect(page.getByText("Még nincs igazolva", {exact:true})).toBeVisible();
  await expect(page.getByRole("region",{name:"Valódi ASA indítás jóváhagyása"})).toHaveCount(0);
});

test("ASA normal Stop requires approval and never force-kills or claims SaveComplete",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").first().click();
  await expect(page.getByRole("region",{name:"Valódi ASA biztonságos leállítása"})).toBeVisible();
  await page.getByRole("button",{name:"Normál leállítás előellenőrzése"}).click();
  await expect(page.getByRole("region",{name:"ASA leállítás jóváhagyása"})).toBeVisible();
  await expect(page.getByText(/SaveWorld → új World Save Complete \+ stabil \.ark → DoExit → PID-ellenőrzés/)).toBeVisible();
  const confirm=page.getByRole("button",{name:"Szerver szabályos leállítása"});
  await expect(confirm).toBeDisabled();
  await page.getByText(/Jóváhagyom a kiválasztott helyi ASA szerver/).click();
  await confirm.click();
  await expect(page.locator(".success-inline").filter({hasText:"ASA-folyamat kilépett"})).toBeVisible({timeout:12000});
  await expect(page.locator(".success-inline").filter({hasText:"független lemezmentése nincs igazolva"})).toBeVisible();
  await expect(page.getByRole("region",{name:"ASA leállítás jóváhagyása"})).toHaveCount(0);
});

test("interrupted lifecycle journal disables fresh ASA Start and Stop in map Overview",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_LIFECYCLE_INTERRUPTED__?:()=>void
  }).__UNTAMED_LIFECYCLE_INTERRUPTED__?.());
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").first().click();
  await expect(page.getByText(/Félbeszakadt helyi ASA-művelet/)).toBeVisible();
  await expect(page.getByRole("button",{name:"Normál leállítás előellenőrzése"})).toBeDisabled();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await expect(page.getByText(/Félbeszakadt helyi ASA-művelet/)).toBeVisible();
  await expect(page.getByRole("button",{name:"Valódi ASA-indítás előellenőrzése"})).toBeDisabled();
});

test("real ASA manual Restart waits for approval and reports old and new PIDs",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").first().click();
  await expect(page.getByRole("region",{name:"Valódi ASA normál újraindítása"})).toBeVisible();
  await page.getByRole("button",{name:"Újraindítás előellenőrzése"}).click();
  await expect(page.getByRole("region",{name:"ASA újraindítás jóváhagyása"})).toBeVisible();
  const start=page.getByRole("button",{name:"Szerver újraindítása",exact:true});
  await expect(start).toBeDisabled();
  await page.getByText(/Jóváhagyom a helyi ASA szerver tényleges leállítását és újraindítását/).click();
  await expect(start).toBeEnabled();
  await start.click();
  await expect(page.locator(".success-inline").filter({hasText:"44117 → 44118"}))
    .toBeVisible({timeout:12000});
  await expect(page.getByText(/nem végez kényszerített Kill-t/)).toBeVisible();
  await expect(page.getByRole("region",{name:"ASA újraindítás jóváhagyása"})).toHaveCount(0);
});

test("crashed manual Restart journal blocks all ASA lifecycle buttons after UI reload",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_RESTART_INTERRUPTED__?:()=>void
  }).__UNTAMED_RESTART_INTERRUPTED__?.());
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.locator(".premium-map-line.is-local").first().click();
  await expect(page.getByText(/manuális ASA újraindítás megszakadt/)).toBeVisible();
  await expect(page.getByRole("button",{name:"Normál leállítás előellenőrzése"})).toBeDisabled();
  await expect(page.getByRole("button",{name:"Újraindítás előellenőrzése"})).toBeDisabled();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await page.locator(".premium-map-line.is-local").nth(1).click();
  await expect(page.getByText(/manuális ASA újraindítás megszakadt/)).toBeVisible();
  await expect(page.getByRole("button",{name:"Valódi ASA-indítás előellenőrzése"})).toBeDisabled();
});

test("CurseForge cart survives searches and uses native draft preview before a single save",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await page.getByRole("tab",{name:"CurseForge katalógus"}).click();
  const cart=page.getByRole("region",{name:"CurseForge többmodos kosár"});
  await expect(cart).toContainText("modkosár (0)");
  await page.getByRole("button",{name:"Hozzáadás a modkosárhoz"}).click();
  await expect(cart).toContainText("modkosár (1)");
  await page.getByRole("textbox",{name:"CurseForge mod keresése"}).fill("Shiny");
  await page.getByRole("button",{name:"Keresés",exact:true}).click();
  await expect(cart).toContainText("modkosár (1)");
  await expect(page.getByRole("button",{name:"Már a kosárban"})).toBeDisabled();
  await cart.getByRole("combobox",{name:"123456 kosár célállapot"}).selectOption("passive");
  await cart.getByRole("button",{name:"Teljes modlista ellenőrzése"}).click();
  await expect(page.getByRole("region",{name:"Többmodos natív előnézet"})).toBeVisible();
  await expect(cart.getByRole("button",{name:"Modlista mentése"})).toBeDisabled();
  await cart.getByText("Jóváhagyom a kijelölt helyi map konfigurációjának egyszeri mentését.").click();
  await expect(cart.getByRole("button",{name:"Modlista mentése"})).toBeEnabled();
  await cart.getByRole("button",{name:"Modlista mentése"}).click();
  await expect(cart).toContainText("modkosár (0)");
  await expect(cart).toContainText("ASA letöltés és modbetöltés még nincs igazolva");
  await cart.getByRole("button",{name:"Átvitel más helyi mapokra"}).click();
  await expect(page.getByRole("tab",{name:"Másolás több mapra"})).toHaveAttribute("aria-selected","true");
});

test("first ASA setup can retry an uncertain native process state without skipping safety",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Meglévő ASA importálása"}).click();
  await page.getByRole("button",{name:"Genesis Part 1",exact:true}).click();
  await page.getByRole("textbox",{name:"Helyi ASA mappa"}).fill("C:\\ASA-LAB");
  await page.getByRole("button",{name:"Helyi telepítés ellenőrzése"}).click();
  await page.getByRole("button",{name:"Ellenőrzött map regisztrálása"}).click();
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_INITIAL_BLOCKED__?:(v:boolean)=>void
  }).__UNTAMED_INITIAL_BLOCKED__?.(true));
  await page.getByRole("row",{name:/Genesis Part 1/}).click();
  await expect(page.getByRole("button",{name:"Beállítások ellenőrzése"})).toBeDisabled();
  await expect(page.getByText(/Igazolt leállítás: nem/)).toBeVisible();
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_INITIAL_BLOCKED__?:(v:boolean)=>void
  }).__UNTAMED_INITIAL_BLOCKED__?.(false));
  await page.getByRole("button",{name:"Első beállítások állapotának újraellenőrzése"}).click();
  await expect(page.getByRole("button",{name:"Beállítások ellenőrzése"})).toBeEnabled();
  await expect(page.getByText(/Igazolt leállítás: igen/)).toBeVisible();
});

test("N-host profile and local copy select-all are limited to local eligible maps",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await page.getByRole("tab",{name:"Modprofilok – N host"}).click();
  const profile=page.getByRole("region",{name:"Többhostos modprofil átadás"});
  await profile.getByRole("button",{name:/Összes jogosult helyi map kijelölése/}).click();
  await expect(profile.getByRole("group",{name:"Modprofil célmapok"}).getByRole("checkbox",{checked:true})).toHaveCount(2);
  await expect(profile).toContainText("2 / 2 helyi map");
  await profile.getByRole("button",{name:"Kijelölések törlése"}).click();
  await expect(profile.getByRole("group",{name:"Modprofil célmapok"}).getByRole("checkbox",{checked:true})).toHaveCount(0);
  await expect(profile).toContainText("Nincs távoli fájlírás vagy automatikus LAN-szinkron");
  await page.getByRole("tab",{name:"Másolás több mapra"}).click();
  const copy=page.getByRole("region",{name:"Modlista másolása több mapra"});
  await copy.getByRole("button",{name:/Összes jogosult helyi célmap kijelölése/}).click();
  await expect(copy.getByRole("group",{name:"Célmapok kiválasztása"}).getByRole("checkbox",{checked:true})).toHaveCount(1);
  await expect(copy).toContainText("1 / 1 helyi célmap");
  await copy.getByRole("button",{name:"Kijelölések törlése"}).click();
  await expect(copy.getByRole("group",{name:"Célmapok kiválasztása"}).getByRole("checkbox",{checked:true})).toHaveCount(0);
});
