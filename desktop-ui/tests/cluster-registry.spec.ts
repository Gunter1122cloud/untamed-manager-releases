import { test, expect } from "@playwright/test";

const firstMapId="00000000-0000-4000-8000-000000000001";
const secondMapId="00000000-0000-4000-8000-000000000002";

test.beforeEach(async({page})=>{
  await page.addInitScript(({firstMapId,secondMapId}:{firstMapId:string;secondMapId:string})=>{
    type Host={hostId:string;hostName:string;isLocal:boolean;isPaired:boolean;registeredUtc:string};
    type MapRow={mapId:string;hostId:string;mapName:string;localConfigurationPresent:boolean;registeredUtc:string};
    type Cluster={clusterId:string;name:string;transferDirectory:string;backupDirectory:string;createdUtc:string;updatedUtc:string};
    type Assignment={mapId:string;clusterId:string};
    type Request={id:string;method:string;clusterId?:string;mapIds?:string[];token?:string;
      name?:string;transferDirectory?:string;backupDirectory?:string;
      confirm?:boolean;jobId?:string};
    type Listener=(event:{data:{id:string;ok:boolean;data?:unknown;error?:string}})=>void;
    const calls:string[]=[];
    const listeners:Listener[]=[];
    const now="2026-11-07T00:00:00Z";
    const hosts:Host[]=Array.from({length:12},(_,index)=>({
      hostId:`00000000-0000-4000-8000-${String(101+index).padStart(12,"0")}`,
      hostName:index===0?"LAB-WIN-01":`LAB-WIN-${String(index+1).padStart(2,"0")}`,
      isLocal:index===0,isPaired:false,registeredUtc:now
    }));
    const maps:MapRow[]=Array.from({length:100},(_,index)=>({
      mapId:`00000000-0000-4000-8000-${String(index+1).padStart(12,"0")}`,
      hostId:hosts[index<2?0:1+((index-2)%11)].hostId,
      mapName:`Test Map ${String(index+1).padStart(3,"0")}`,
      localConfigurationPresent:index<2,registeredUtc:now
    }));
    const clusters:Cluster[]=[
      {clusterId:"untamed-kingdom",name:"Untamed Kingdom",
        transferDirectory:"C:\\Test\\Untamed\\Transfer",
        backupDirectory:"C:\\Test\\Untamed\\Backups",createdUtc:now,updatedUtc:now},
      {clusterId:"event-cluster",name:"Event Cluster",
        transferDirectory:"C:\\Test\\Event\\Transfer",
        backupDirectory:"C:\\Test\\Event\\Backups",createdUtc:now,updatedUtc:now}
    ];
    const assignments:Assignment[]=[
      {mapId:firstMapId,clusterId:"untamed-kingdom"},
      {mapId:secondMapId,clusterId:"event-cluster"}
    ];
    let revision=10;
    let pending:{token:string;clusterId:string;mapIds:string[];revision:number}|null=null;
    let nextToken=1;
    let multiHostJob:"backup"|"restore"|null=null;
    let multiHostReplacement=false;
    let multiHostRecovery=false;
    const multiHostStatus=(request:Request)=>({jobId:request.jobId??"synthetic-job",
      kind:multiHostJob??"backup",state:"completed",stage:multiHostJob==="restore"?
        (multiHostRecovery?"TEMP_SANDBOX_ROLLED_BACK":
          multiHostReplacement?"TEMP_SANDBOX_REPLACED_ROLLBACK_RETAINED":"TEMP_SANDBOX_SHA256_VERIFIED"):
        "ARCHIVE_SHA256_VERIFIED",
      artifactName:multiHostJob==="restore"?"Untamed-MultiHost-Restore-synthetic":"Untamed-MultiHost-synthetic",
      rollbackArtifactName:multiHostRecovery?"Untamed-MultiHost-Restore-quarantined-synthetic":
        multiHostReplacement?"Untamed-MultiHost-Restore-rollback-synthetic":"",
      hosts:12,clusters:4,maps:100,components:813,bytes:65536,
      changedUtc:now,productionRestoreAllowed:false,liveConsistencyCertified:false,
      storeWriterCertified:false,sqlUsed:false,rconUsed:false,
      serverProcessTouched:false,evidence:"SYNTHETIC_TEST_FIXTURE"});
    const snapshot=()=>({schemaVersion:1,revision,
      hostRegistry:{schemaVersion:1,localHostId:hosts[0].hostId,hosts,maps},
      clusterRegistry:{schemaVersion:1,revision,clusters,mapAssignments:assignments}});
    const respond=(request:Request):unknown=>{
      switch(request.method){
        case "dashboard.snapshot":return {
          mode:"LIVE_READ_ONLY",observedUtc:now,
          local:{hostId:hosts[0].hostId,hostName:hosts[0].hostName,
            cpuPercent:null,memoryUsedKiB:null,memoryTotalKiB:null,
            maps:[
              {id:firstMapId,name:"Test Map 001",running:false,status:"Stopped",
                gamePort:0,steamBuildId:null,steamUpdateState:"Unknown"},
              {id:secondMapId,name:"Test Map 002",running:false,status:"Stopped",
                gamePort:0,steamBuildId:null,steamUpdateState:"Unknown"}
            ]},remote:null,peerMessage:"No paired peer"
        };
        case "cluster.registry.snapshot":return snapshot();
        case "cluster.create":{
          const created:Cluster={clusterId:request.clusterId??"",name:request.name??"",
            transferDirectory:request.transferDirectory??"",backupDirectory:request.backupDirectory??"",
            createdUtc:now,updatedUtc:now};
          clusters.push(created);revision++;
          return created;
        }
        case "cluster.update":{
          const current=clusters.find(item=>item.clusterId===request.clusterId);
          if(!current)throw new Error("Unknown cluster");
          Object.assign(current,{name:request.name,transferDirectory:request.transferDirectory,
            backupDirectory:request.backupDirectory,updatedUtc:now});
          revision++;return current;
        }
        case "cluster.membership.preview":{
          const clusterId=request.clusterId??"";
          const mapIds=request.mapIds??[];
          const current=assignments.filter(item=>item.clusterId===clusterId);
          const additions:object[]=[];const moves:object[]=[];const removals:object[]=[];
          for(const mapId of mapIds){
            const map=maps.find(item=>item.mapId===mapId);
            const owner=assignments.find(item=>item.mapId===mapId);
            if(!map)throw new Error("Unknown map");
            if(owner?.clusterId===clusterId)continue;
            const host=hosts.find(item=>item.hostId===map.hostId)!;
            const change={mapId,mapName:map.mapName,hostId:map.hostId,hostName:host.hostName,
              previousClusterId:owner?.clusterId??null,targetClusterId:clusterId};
            if(owner)moves.push(change);else additions.push(change);
          }
          for(const owner of current.filter(item=>!mapIds.includes(item.mapId))){
            const map=maps.find(item=>item.mapId===owner.mapId)!;
            const host=hosts.find(item=>item.hostId===map.hostId)!;
            removals.push({mapId:map.mapId,mapName:map.mapName,hostId:map.hostId,
              hostName:host.hostName,previousClusterId:clusterId,targetClusterId:null});
          }
          const token=`00000000-0000-4000-8000-${String(9000+nextToken++).padStart(12,"0")}`;
          pending={token,clusterId,mapIds:[...mapIds],revision};
          return {token,preview:{baseRevision:revision,clusterId,requestedMapIds:mapIds,
            additions,removals,moves,errors:[],isValid:true}};
        }
        case "cluster.membership.apply":{
          const reviewed=pending;
          if(!reviewed||request.token!==reviewed.token||reviewed.revision!==revision)
            throw new Error("Expired or stale preview");
          pending=null;
          const adds=reviewed.mapIds.filter(mapId=>!assignments.some(item=>item.mapId===mapId)).length;
          const moves=reviewed.mapIds.filter(mapId=>assignments.some(item=>item.mapId===mapId&&
            item.clusterId!==reviewed.clusterId)).length;
          const removes=assignments.filter(item=>item.clusterId===reviewed.clusterId&&
            !reviewed.mapIds.includes(item.mapId)).length;
          for(let index=assignments.length-1;index>=0;index--)
            if(reviewed.mapIds.includes(assignments[index].mapId)||
              (assignments[index].clusterId===reviewed.clusterId&&
                !reviewed.mapIds.includes(assignments[index].mapId)))assignments.splice(index,1);
          for(const mapId of reviewed.mapIds)
            assignments.push({mapId,clusterId:reviewed.clusterId});
          revision++;
          return {clusterId:reviewed.clusterId,revision,addedCount:adds,
            movedCount:moves,removedCount:removes,appliedUtc:now};
        }
        case "cluster.multiHostBackupPreview":return {token:"synthetic-backup-token",
          batchId:"synthetic-batch",archiveName:"Untamed-MultiHost-synthetic",
          hosts:12,clusters:4,maps:100,components:813,bytes:65536,
          requiresExplicitApproval:true,offlineCopiesOnly:true,
          liveConsistencyCertified:false,storeWriterCertified:false,sqlUsed:false,
          rconUsed:false,serverProcessTouched:false,productionRestoreAllowed:false,
          evidence:"SYNTHETIC_TEST_FIXTURE"};
        case "cluster.multiHostBackupApply":
          if(request.token!=="synthetic-backup-token"||request.confirm!==true)
            throw new Error("Backup confirmation required");
          multiHostJob="backup";
          return {jobId:"synthetic-backup-job",started:true,productionRestoreAllowed:false,
            rconUsed:false,serverProcessTouched:false,evidence:"SYNTHETIC_TEST_FIXTURE"};
        case "cluster.multiHostBackupStatus":return multiHostStatus(request);
        case "cluster.multiHostRestorePreview":return {token:"synthetic-restore-token",
          batchId:"synthetic-batch",archiveName:"Untamed-MultiHost-synthetic",
          hosts:12,clusters:4,maps:100,components:813,requiresExplicitApproval:true,
          restoreTarget:"NEW TEMP SANDBOX ONLY",productionRestoreAllowed:false,
          liveConsistencyCertified:false,storeWriterCertified:false,
          evidence:"SYNTHETIC_TEST_FIXTURE"};
        case "cluster.multiHostRestoreApply":
          if(request.token!=="synthetic-restore-token"||request.confirm!==true)
            throw new Error("Restore confirmation required");
          multiHostJob="restore";
          return {jobId:"synthetic-restore-job",started:true,target:"NEW TEMP SANDBOX ONLY",
            productionRestoreAllowed:false,rconUsed:false,serverProcessTouched:false,
            evidence:"SYNTHETIC_TEST_FIXTURE"};
        case "cluster.multiHostRestoreStatus":return multiHostStatus(request);
        case "cluster.multiHostRestoreReplacePreview":return {token:"synthetic-replace-token",
          batchId:"synthetic-batch",archiveName:"Untamed-MultiHost-synthetic",
          hosts:12,clusters:4,maps:100,components:813,requiresExplicitApproval:true,
          previousBatchId:"synthetic-prior-batch",targetDirectoryName:"Untamed-MultiHost-Restore-existing",
          restoreTarget:"EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK",
          productionRestoreAllowed:false,liveConsistencyCertified:false,storeWriterCertified:false,
          evidence:"SYNTHETIC_TEST_FIXTURE"};
        case "cluster.multiHostRestoreReplaceApply":
          if(request.token!=="synthetic-replace-token"||request.confirm!==true)
            throw new Error("Sandbox replacement confirmation required");
          multiHostJob="restore";multiHostReplacement=true;
          return {jobId:"synthetic-replace-job",started:true,
            target:"EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK",
            productionRestoreAllowed:false,rconUsed:false,serverProcessTouched:false,
            evidence:"SYNTHETIC_TEST_FIXTURE"};
        case "cluster.multiHostRestoreRecoveryList":return {truncated:false,transactions:[{
          targetDirectoryName:"Untamed-MultiHost-Restore-existing",transactionId:"synthetic-transaction",
          phase:"previousMoved",needsOperator:true,canRollback:true,
          reason:"Interrupted transaction artifacts are verified; explicit rollback is available"}]};
        case "cluster.multiHostRestoreRecoveryPreview":
          if(request.targetDirectoryName!=="Untamed-MultiHost-Restore-existing")
            throw new Error("A listed TEMP recovery candidate is required");
          return {token:"synthetic-recovery-token",
          targetDirectoryName:"Untamed-MultiHost-Restore-existing",transactionId:"synthetic-transaction",
          phase:"previousMoved",needsOperator:true,canRollback:true,requiresExplicitApproval:true,
          quarantinedReplacementName:null,protectiveCopyName:"Untamed-MultiHost-Restore-previous",
          reason:"Interrupted transaction artifacts are verified; explicit rollback is available",
          productionRestoreAllowed:false,rconUsed:false,serverProcessTouched:false,
          evidence:"SYNTHETIC_TEST_FIXTURE"};
        case "cluster.multiHostRestoreRecoveryApply":
          if(request.token!=="synthetic-recovery-token"||request.confirm!==true)
            throw new Error("TEMP rollback confirmation required");
          multiHostJob="restore";multiHostRecovery=true;multiHostReplacement=false;
          return {jobId:"synthetic-recovery-job",started:true,target:"TEMP SANDBOX ROLLBACK ONLY",
            productionRestoreAllowed:false,rconUsed:false,serverProcessTouched:false,
            evidence:"SYNTHETIC_TEST_FIXTURE"};
        default:throw new Error("Unexpected test RPC: "+request.method);
      }
    };
    Object.assign(window,{
      __UNTAMED_TEST_FIXTURE__:true,
      __UNTAMED_CLUSTER_RPC__:calls,
      chrome:{webview:{
        addEventListener:(_name:string,listener:Listener)=>{listeners.push(listener);},
        postMessage:(request:Request)=>{
          calls.push(request.method);
          queueMicrotask(()=>{
            try{
              const data=respond(request);
              listeners.forEach(listener=>listener({data:{id:request.id,ok:true,data}}));
            }catch{
              listeners.forEach(listener=>listener({data:{id:request.id,ok:false,error:"UM-TEST-DENIED"}}));
            }
          });
        }
      }}
    });
  },{firstMapId,secondMapId});
});

test("versioned Cluster UI creates a cluster and approves large multi-host map assignments",async({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.locator(".sidebar .nav-link").filter({hasText:"Cluster"}).click();
  const workbench=page.locator(".cluster-registry-workbench");
  await expect(workbench.getByRole("heading",{name:"Cluster-regiszter"})).toBeVisible();
  await expect(workbench.locator(".cluster-registry-host")).toHaveCount(12);
  await expect(workbench.locator(".cluster-registry-map")).toHaveCount(100);
  await expect(workbench.getByText("Nincs párosítva")).toHaveCount(11);

  await workbench.getByRole("button",{name:/Új cluster/}).click();
  const clusterForm=workbench.locator(".cluster-registry-columns .cluster-registry-form").first();
  await clusterForm.locator("input").nth(0).fill("event-2026");
  await clusterForm.locator("input").nth(1).fill("Event 2026");
  await clusterForm.locator("input").nth(2).fill("C:\\Test\\Event2026\\Transfer");
  await clusterForm.locator("input").nth(3).fill("C:\\Test\\Event2026\\Backups");
  await clusterForm.getByRole("button").click();
  const clusterPicker=workbench.getByRole("combobox",{name:"Kiválasztott cluster"});
  await expect(clusterPicker).toHaveValue("event-2026");

  const localMap=workbench.locator(".cluster-registry-map").filter({hasText:"Test Map 002"});
  const remoteMap=workbench.locator(".cluster-registry-map").filter({hasText:"Test Map 003"});
  await localMap.getByRole("checkbox").check();
  await remoteMap.getByRole("checkbox").check();
  await workbench.locator(".cluster-registry-actions button").click();
  const preview=workbench.locator(".cluster-membership-preview");
  await expect(preview).toBeVisible();
  await expect(preview.locator(".cluster-membership-change")).toHaveCount(2);
  await expect(preview).toContainText("Test Map 002");
  await expect(preview).toContainText("Test Map 003");
  await expect(preview.getByRole("button")).toBeEnabled();
  await preview.getByRole("button").click();

  await expect(workbench.getByRole("status")).toContainText("Jóváhagyva");
  await expect(localMap.getByRole("checkbox")).toBeChecked();
  await expect(remoteMap.getByRole("checkbox")).toBeChecked();
  const methods=await page.evaluate(()=>
    (window as typeof window & {__UNTAMED_CLUSTER_RPC__?:string[]})
      .__UNTAMED_CLUSTER_RPC__??[]);
  expect(methods).toContain("cluster.create");
  expect(methods.indexOf("cluster.membership.preview"))
    .toBeLessThan(methods.indexOf("cluster.membership.apply"));
  expect(methods.some(method=>method.startsWith("rcon.")||
    /^(servers\.(freshStart|gracefulStop|restart))/.test(method))).toBe(false);
});

test("multi-host backup and restore UI requires confirmation and keeps restore sandbox-only",async({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.locator(".sidebar .nav-link").filter({hasText:"Cluster"}).click();
  const panel=page.getByRole("region",{name:"N-host mentés és izolált restore"});
  await expect(panel).toBeVisible();
  await expect(panel).toContainText("multi-host-backup-catalog.json");
  await panel.getByRole("button",{name:"Offline mentés előnézete"}).click();
  const backup=panel.getByRole("region",{name:"Multi-host mentés jóváhagyása"});
  await expect(backup).toContainText("12 host · 100 map · 4 cluster");
  await backup.getByRole("checkbox").check();
  await backup.getByRole("button",{name:"Mentés létrehozása"}).click();
  await expect(panel.locator(".uk-rcon-receipt")).toContainText("backup állapot: completed");
  await panel.getByRole("button",{name:"Új művelet"}).click();

  await panel.getByRole("button",{name:"Archívum ellenőrzése / TEMP restore"}).click();
  const restore=panel.getByRole("region",{name:"TEMP restore jóváhagyása"});
  await expect(restore).toContainText("Cél: új TEMP sandbox");
  await restore.getByRole("checkbox").check();
  await restore.getByRole("button",{name:"TEMP restore indítása"}).click();
  await expect(panel.locator(".uk-rcon-receipt")).toContainText("restore állapot: completed");
  await expect(panel).toContainText("Cél: Untamed-MultiHost-Restore-synthetic");
  await panel.getByRole("button",{name:"Új művelet"}).click();
  await panel.getByRole("button",{name:"TEMP sandbox cseréje / rollback előnézet"}).click();
  const replacement=panel.getByRole("region",{name:"TEMP restore jóváhagyása"});
  await expect(replacement).toContainText("Cél: Untamed-MultiHost-Restore-existing TEMP sandbox");
  await expect(replacement).toContainText("Az előző ellenőrzött állapot rollback-másolata megmarad");
  const replaceButton=replacement.getByRole("button",{name:"TEMP sandbox cseréje (rollback-másolat megtartásával)"});
  await expect(replaceButton).toBeDisabled();
  await replacement.getByRole("checkbox").check();
  await expect(replaceButton).toBeEnabled();
  await replaceButton.click();
  await expect(panel.locator(".uk-rcon-receipt")).toContainText("TEMP_SANDBOX_REPLACED_ROLLBACK_RETAINED");
  await expect(panel).toContainText("Rollback-másolat: Untamed-MultiHost-Restore-rollback-synthetic");
  await panel.getByRole("button",{name:"Új művelet"}).click();
  await panel.getByRole("button",{name:"TEMP restore tranzakciók keresése / rollback"}).click();
  const recoveryList=panel.getByRole("region",{name:"TEMP restore tranzakciók"});
  await recoveryList.getByLabel("Helyreállítandó TEMP tranzakció").selectOption("Untamed-MultiHost-Restore-existing");
  await recoveryList.getByRole("button",{name:"Kiválasztott napló teljes ellenőrzése"}).click();
  const recovery=panel.getByRole("region",{name:"TEMP restore recovery jóváhagyása"});
  await expect(recovery).toContainText("previousMoved");
  await expect(recovery).toContainText("Untamed-MultiHost-Restore-previous");
  const rollbackButton=recovery.getByRole("button",{name:"TEMP sandbox rollback indítása"});
  await expect(rollbackButton).toBeDisabled();
  await recovery.getByRole("checkbox").check();
  await expect(rollbackButton).toBeEnabled();
  await rollbackButton.click();
  await expect(panel.locator(".uk-rcon-receipt")).toContainText("TEMP_SANDBOX_ROLLED_BACK");
  await expect(panel).toContainText("Rollback-másolat: Untamed-MultiHost-Restore-quarantined-synthetic");
  const methods=await page.evaluate(()=>(window as typeof window &
    {__UNTAMED_CLUSTER_RPC__?:string[]}).__UNTAMED_CLUSTER_RPC__??[]);
  expect(methods).toContain("cluster.multiHostBackupPreview");
  expect(methods.indexOf("cluster.multiHostBackupPreview"))
    .toBeLessThan(methods.indexOf("cluster.multiHostBackupApply"));
  expect(methods).toContain("cluster.multiHostRestorePreview");
  expect(methods.indexOf("cluster.multiHostRestorePreview"))
    .toBeLessThan(methods.indexOf("cluster.multiHostRestoreApply"));
  expect(methods).toContain("cluster.multiHostRestoreReplacePreview");
  expect(methods.indexOf("cluster.multiHostRestoreReplacePreview"))
    .toBeLessThan(methods.indexOf("cluster.multiHostRestoreReplaceApply"));
  expect(methods).toContain("cluster.multiHostRestoreRecoveryPreview");
  expect(methods.indexOf("cluster.multiHostRestoreRecoveryList"))
    .toBeLessThan(methods.indexOf("cluster.multiHostRestoreRecoveryPreview"));
  expect(methods.indexOf("cluster.multiHostRestoreRecoveryPreview"))
    .toBeLessThan(methods.indexOf("cluster.multiHostRestoreRecoveryApply"));
  expect(methods.some(method=>method.startsWith("rcon.")||method.startsWith("servers."))).toBe(false);
});
