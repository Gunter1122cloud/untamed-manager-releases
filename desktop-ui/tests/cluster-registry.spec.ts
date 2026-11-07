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
      name?:string;transferDirectory?:string;backupDirectory?:string};
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
