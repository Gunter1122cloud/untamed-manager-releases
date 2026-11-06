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
      peerMessage: ""
    };
    (window as typeof window & {__UNTAMED_EMPTY_FIXTURE__?:()=>void})
      .__UNTAMED_EMPTY_FIXTURE__=()=>{
        state.local.maps=[];
        state.remote=null;
        state.peerMessage="Peer nincs beállítva";
      };
    let freshId = "";
    let keySaved = false;
    let stagedId = "";
    let redoId = "";
    let pluginEnabled = true;
    let pluginZipInstalled = false;
    let steamUpdateStarted = false;
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
        Object.assign(state.local.maps[1],{storeProtected:true});
        localBackupMap=two;localBackupSaved=true;
      };
    let lanListen=0,lanPeer="",lanPeerPort=0,lanConfigured=false;
    let lanApproval="";
    let rconAllowed=false;
    (window as typeof window & {__UNTAMED_RCON_ENABLE__?:()=>void})
      .__UNTAMED_RCON_ENABLE__=()=>{rconAllowed=true;};
    const draftToken = "00000000-0000-4000-8000-000000001234";
    const draftState = () => ({
      draftId:draftToken,mapId:one,active:stagedId?["123456",stagedId]:["123456"],
      passive:["789012"],disabled:[],pending:!!stagedId,dirty:!!stagedId,
      canUndo:!!stagedId,canRedo:!!redoId,evidence:"IN_MEMORY_MOD_DRAFT",
      differences:stagedId?[{modId:stagedId,beforeMode:null,beforePosition:null,afterMode:"active",afterPosition:2}]:[]
    });
    const iniToken="00000000-0000-4000-8000-000000003333";
    const answers = (m: { method: string; mapId?: string; term?: string; page?: number; credential?: string; draftId?: string; modId?: string | number; mode?: string; file?: string; token?: string; content?: string; name?: string; root?: string; importToken?: string; categoryId?: number; sort?: number; descending?: boolean; mapName?: string; sessionName?: string; gamePort?: number; queryPort?: number; archive?: string; command?: string; coordinationArchive?: string; localMetadataArchive?: string; plugin?: string; enabled?: boolean; confirm?: boolean; channel?: string; point?: string; action?: string; message?: string; typedApproval?: string; listenPort?:number; peerAddress?:string; peerPort?:number; pairingKey?:string }) => {
      switch (m.method) {
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
          runningVersion:"1.4.0",installedTag:"v1.4.0",
          provider:"GITHUB_RELEASES",updateCheckPerformed:false,
          installationPermitted:false,updaterVerified:false,
          evidence:"LOCAL_RELEASE_IDENTITY_NO_NETWORK"
        };
        case "updates.check": return {
          repository:"Gunter1122cloud/untamed-manager-releases",
          runningVersion:"1.4.0",installedTag:"v1.4.0",
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
          useStorePresent:true,conversionFlagPresent:!!m.command?.includes("-converttostore"),
          findings:m.command?.includes("-converttostore")?["CONVERSION_FLAG_PRESENT"]:[],
          sourceAuthenticated:false,fullLaunchArgumentsVerified:false,
          peerWriterVerified:false,productionUseAllowed:false,serverStartAllowed:false,
          evidence:"OPERATOR_TEXT_READ_ONLY_UNAUTHENTICATED"
        };
        case "historical.pairedEvidence": return {
          files:12,maps:10,peers:2,sessionConsistent:true,
          configHashesVerified:true,activeStoreRecords:true,
          completeTwoHostEvidence:false,historicalConsistent:false,
          findings:["LocalLock"],
          hosts:[{number:1,maps:5,localEvidence:true,sessionMatches:true,
            configHashMatches:false,lockMatches:false,activeMaps:true},
            {number:2,maps:5,localEvidence:true,sessionMatches:true,
            configHashMatches:true,lockMatches:true,activeMaps:true}],
          productionUseAllowed:false,
          evidence:"PAIRED_COPIES_OFFLINE_READ_ONLY_NO_WRITER_CERTIFICATION"
        };
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
        case "steam.freshPreflight": return {
          token:"00000000-0000-4000-8000-000000008888",
          mapName:m.mapName||"Valguero",mapLevel:"Valguero_WP",folderName:"ASA-NEW",
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
        case "servers.initialStatus": {
          const map = state.local.maps.find(x=>x.id===m.mapId);
          if(!map)throw new Error("UM-MAP-NOT-OWNED");
          return {mapId:map.id,mapName:map.name,level:"Genesis_WP",
            initialSetupAvailable:map.gamePort===0,confirmedStopped:!map.running,
            hasServerExecutable:true,gamePort:map.gamePort,queryPort:0,
            suggestion:{game:7797,query:27035},
            evidence:"LOCAL_MANAGER_PROFILE_FIRST_SETUP_STATUS"};
        }
        case "servers.initialPreview": return {
          token:"00000000-0000-4000-8000-000000008877",mapId:m.mapId,
          mapName:"Genesis Part 1",sessionName:m.sessionName||"Untamed Kingdom",
          mapLevel:"Genesis_WP",gamePort:m.gamePort||7797,queryPort:m.queryPort||27035,
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
            maps,schedules:m.method==="operations.map"?[]:[{
              id:"00000000-0000-4000-8000-000000009987",mapId:one,
              mapName:"The Island",kind:"Observation",frequency:"Daily",
              enabled:false,nextUtc:"2026-11-07T06:30:00Z",
              state:"disabled",autoExecutionPermitted:false
            }],schedulingIsReadOnly:true,remoteCommandsAllowed:false,
            evidence:"LOCAL_OS_PROCESS_AND_SAVED_SCHEDULE_READ_ONLY"};
        }
        case "lan.status": return {
          hostName:state.local.hostName,listenPort:lanListen,
          peerAddress:lanPeer,peerPort:lanPeerPort,
          pairingKeyConfigured:lanConfigured,remoteFresh:false,
          remoteState:"Nem érhető el friss, hitelesített peer-állapot",
          secretReturned:false,peerWritesAllowed:false,
          evidence:"DPAPI_LOCAL_LAN_SETTINGS_READ_ONLY"
        };
        case "lan.preview":
          if(m.peerAddress!=="192.168.1.200" || (m.pairingKey||"").length<24)
            throw new Error("UM-LAN-INVALID-PRIVATE-PEER");
          lanApproval="00000000-0000-4000-8000-000000000955";
          return {token:lanApproval,listenPort:m.listenPort,
            peerAddress:m.peerAddress,peerPort:m.peerPort,
            pairingKeyWillChange:true,requiresManagerRestart:true,
            remoteWritesAllowed:false,
            evidence:"LOCAL_LAN_PAIRING_PREFLIGHT_NO_NETWORK_OR_FILE_WRITE"};
        case "lan.apply":
          if(m.token!==lanApproval||m.confirm!==true)
            throw new Error("UM-LAN-EXPLICIT-APPROVAL-REQUIRED");
          lanApproval="";
          lanListen=39290;lanPeer="192.168.1.200";lanPeerPort=39291;
          lanConfigured=true;
          return {saved:true,listenPort:lanListen,
            peerAddress:lanPeer,peerPort:lanPeerPort,
            requiresManagerRestart:true,secretReturned:false,
            remoteWritesAllowed:false,
            evidence:"ATOMIC_LOCAL_LAN_PAIRING_DPAPI_PROFILE_ONLY"};
        case "dashboard.snapshot": return JSON.parse(JSON.stringify(state));
        case "ini.open": return {token:iniToken,mapId:one,file:m.file||"Game.ini",content:"[ServerSettings]\\nExample=1\\n",encoding:"utf-8",sha256:"AABBCC",bytes:28,evidence:"STOPPED_LOCAL_INI_SNAPSHOT"};
        case "ini.preview": return {previewToken:iniToken,changed:true,oldLines:3,newLines:3,bytes:28,originalSha256:"AABBCC",nextSha256:"FFEE11",evidence:"HASHED_IN_MEMORY_PREVIEW"};
        case "ini.save": return {saved:true,backupCreated:true,sha256:"FFEE11",evidence:"ATOMIC_LOCAL_INI_SAVE_ETAG_VERIFIED"};
        case "ini.close": return {closed:true};
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
        case "steam.stoppedCurrent": return steamInterrupted?{
          exists:true,queueId:"00000000-0000-4000-8000-000000008888",
          phase:"NeedsOperator",activeMapName:"Astraeos",
          nextIndex:0,total:1,changedUtc:"2026-11-06T09:10:00Z",
          summary:"Interrupted fixture",needsOperatorReview:true,
          autoResumeAllowed:false,hostName:"LAB-WIN-01",
          evidence:"DURABLE_MACHINE_LOCAL_STEAM_QUEUE_JOURNAL"
        }:{exists:false,needsOperatorReview:false,autoResumeAllowed:false};
        case "steam.stoppedStatus":
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
            } catch {
              handlers.forEach(fn=>fn({ data: {id:msg.id,ok:false,error:"UM-METHOD-DENIED"} }));
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
  await expect(page.locator(".ops-map")).toHaveCount(2);
  await expect(page.locator(".ops-map").first()).toContainText("12345");
  await expect(page.locator(".ops-map").first()).toContainText(/9\\s?234,5 MiB|9234,5 MiB/);
  await expect(page.locator(".ops-map").last()).toContainText("Nem mérhető");
  await expect(page.locator(".ops-schedule-table tbody tr")).toHaveCount(1);
  await expect(page.getByText("Kikapcsolva",{exact:true})).toBeVisible();
  await expect(page.locator(".ops-center").getByText("Ragnarok")).toHaveCount(0);
  await page.locator(".ops-map").first().getByRole("button",{name:"Map megnyitása"}).click();
  await expect(page.locator(".map-detail-hero")).toContainText("The Island");
  await page.getByRole("tab",{name:"Üzemeltetés"}).click();
  await expect(page.locator(".ops-map")).toHaveCount(1);
  await expect(page.locator(".ops-map").first()).toContainText("12345");
  await expect(page.locator(".ops-schedules")).toHaveCount(0);
});

test("two-host LAN pairing is guarded, native-only, private and never exposes key",async({page})=>{
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.getByRole("button",{name:"Beállítások"}).click();
  await expect(page.getByRole("heading",{name:"Kétgépes LAN-párosítás"})).toBeVisible();
  await expect(page.getByText("nincs beállítva",{exact:true})).toBeVisible();
  await page.getByRole("textbox",{name:"Helyi LAN port"}).fill("39290");
  await page.getByRole("textbox",{name:"Másik LAN cím"}).fill("192.168.1.200");
  await page.getByRole("textbox",{name:"Távoli LAN port"}).fill("39291");
  await page.getByRole("textbox",{name:"LAN közös kulcs"}).fill("SYNTHETIC-ONLY-LAN-SHARED-KEY-36");
  await page.getByRole("button",{name:"LAN beállítások ellenőrzése"}).click();
  await expect(page.getByRole("region",{name:"LAN párosítás jóváhagyása"})).toBeVisible();
  await expect(page.getByRole("textbox",{name:"LAN közös kulcs"})).toHaveValue("");
  await page.getByRole("button",{name:"Párosítás helyi mentése"}).click();
  await expect(page.getByText(/A helyi párosítás mentve/)).toBeVisible();
  await expect(page.getByText(/helyileg, védetten beállítva/)).toBeVisible();
  await expect(page.getByRole("textbox",{name:"LAN közös kulcs"})).toHaveValue("");
  await expect(page.locator(".lan-pairing-panel")).not.toContainText("SYNTHETIC-ONLY-LAN-SHARED-KEY-36");
  await page.screenshot({path:"test-results/uk-two-host-lan-pairing-1366.png",fullPage:false});
});

test("two-host character travel workbench captures exactly five local maps without unlocking Store",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Karakterutazás teszt"}).click();
  await expect(page.getByRole("heading",{name:"Karakterutazás tesztközpont"})).toBeVisible();
  await page.getByRole("button",{name:"Helyi 5 map ellenőrzése"}).click();
  await expect(page.getByText("Az öt helyi map leltára teljes és nyugalomban készült.")).toBeVisible();
  await expect(page.locator(".uk-travel-map-results>div")).toHaveCount(5);
  await expect(page.getByRole("textbox",{name:"Helyi mentéslenyomat JSON"})).not.toBeEmpty();
  await expect(page.getByText(/nem oldja fel a védett mapok indítási zárolását/)).toBeVisible();
  await page.screenshot({path:"test-results/untamed-store-travel-workbench-1600.png",fullPage:false});
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
  await expect(page.getByText(/4 fájl · 1 karakter · 1 törzs/)).toBeVisible();
  await expect(page.getByText("1 ellenőrzött csomag")).toBeVisible();
  await page.screenshot({path:"test-results/uk-real-world-backup-1600.png",fullPage:false});
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

test("Update Center verifies Obsidian-only installer and requires explicit two-stage native approval",async({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Frissítések"}).click();
  await expect(page.getByRole("heading",{name:"Frissítési központ"})).toBeVisible();
  await expect(page.getByText("v1.4.0")).toBeVisible();
  await page.getByRole("combobox",{name:"Frissítési csatorna"}).selectOption("preview");
  await page.getByRole("button",{name:"Frissítések keresése"}).click();
  await expect(page.getByText("Új kiadás érhető el")).toBeVisible();
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
  await expect(page.getByRole("heading",{name:"SteamCMD Center"})).toBeVisible();
  await page.screenshot({path:"test-results/uk-first-run-steamcmd-1366.png",fullPage:false});
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
  await page.getByRole("button", { name: "SteamCMD" }).click();
  await expect(page.getByRole("heading", {name:"SteamCMD Center"})).toBeVisible();
  await expect(page.getByText("Build: 123456")).toBeVisible();
  await expect(page.getByText("2797", {exact:true})).toHaveCount(0);
  await expect(page.getByText("7797", {exact:true})).toBeVisible();
  await expect(page.getByText("Kézi előellenőrzés")).toBeVisible();
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

test("SteamCMD new ASA: choose map and Windows folder, one click starts download and registers verified files",async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"SteamCMD"}).click();
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
  await page.getByRole("button",{name:"SteamCMD"}).click();
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
  await expect(page.getByText("TILTOTT")).toBeVisible();
  await expect(page.getByRole("button",{name:"Zárolt Store-v1 map regisztrálása"})).toBeDisabled();
  await page.getByText(/Jóváhagyom a kizárólag Manager-profilba történő, zárolt regisztrációt/).click();
  await page.getByRole("button",{name:"Zárolt Store-v1 map regisztrálása"}).click();
  await expect(page.getByText(/Store-v1 zárolt: a szerver nem indulhat/)).toBeVisible();
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
  await expect(page.getByRole("button",{name:"Zárolt Store-v1 map regisztrálása"})).toHaveCount(0);
  await page.getByRole("button",{name:"Map és Store-adatok előellenőrzése"}).click();
  await expect(page.getByText("Teljes launch argumentumok")).toBeVisible();
  await expect(page.getByRole("button",{name:"Zárolt Store-v1 map regisztrálása"})).toBeDisabled();
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
  await page.getByRole("button",{name:"Zárolt Store-v1 map regisztrálása"}).click();
  await expect(page.getByRole("textbox",{name:"Korábbi Manager archívum"})).toBeDisabled();
  await expect(page.getByRole("textbox",{name:"Korábbi ASA map mappája"})).toBeDisabled();
  await expect(page.getByRole("combobox",{name:"Archivált Store-v1 map"})).toBeDisabled();
  await page.evaluate(()=>{(window as unknown as {releaseRegistration:()=>void}).releaseRegistration();});
  await expect(page.getByText(/Store-v1 zárolt: a szerver nem indulhat/)).toBeVisible();
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
  await page.getByRole("button",{name:"SteamCMD"}).click();
  await expect(page.getByRole("region",{name:"Leállított map SteamCMD-frissítése"})).toBeVisible();
  await page.getByRole("combobox",{name:"SteamCMD frissítés map"}).selectOption(second);
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

test("interrupted SteamCMD queue survives UI restart and blocks accidental retry",async({page})=>{
  await page.goto("/");
  await page.evaluate(()=>(window as typeof window & {
    __UNTAMED_STEAM_INTERRUPTED__?:()=>void
  }).__UNTAMED_STEAM_INTERRUPTED__?.());
  await page.getByRole("button",{name:"SteamCMD"}).click();
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
  await expect(page.getByText(/SaveWorld → DoExit → ellenőrzött/)).toBeVisible();
  const confirm=page.getByRole("button",{name:"Szerver szabályos leállítása"});
  await expect(confirm).toBeDisabled();
  await page.getByText(/Jóváhagyom a kiválasztott helyi ASA szerver/).click();
  await confirm.click();
  await expect(page.locator(".success-inline").filter({hasText:"ASA-folyamat szabályosan kilépett"})).toBeVisible({timeout:12000});
  await expect(page.locator(".success-inline").filter({hasText:"független lemezmentési bizonyíték nincs"})).toBeVisible();
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
