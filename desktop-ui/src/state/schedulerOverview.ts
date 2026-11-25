import type {OperationsSnapshot,NativeAsaScheduleOverview} from "../bridge";
export type ScheduleRow={id:string;mapId:string;mapName:string;kind:string;source:"native"|"inspection";enabled:boolean;nextUtc:string|null;status:string};
const names:Record<string,string>={SaveWorld:"SaveWorld",Restart:"Újraindítás",WildWipe:"Dinowipe",Observation:"PID ellenőrzés",ModCheck:"Mod ellenőrzés",ApiCheck:"ASA API ellenőrzés",SteamCheck:"Korábbi SteamCheck"};
export function scheduleRows(snapshot:OperationsSnapshot,native:NativeAsaScheduleOverview|null):ScheduleRow[]{
  const maps=new Map(snapshot.maps.map(m=>[m.mapId,m.mapName]));
  const rows:ScheduleRow[]=[];
  for(const job of native?.jobs??[]){
    if(!maps.has(job.mapId))continue;
    rows.push({id:"native:"+job.mapId+":"+job.kind,mapId:job.mapId,mapName:maps.get(job.mapId)!,
      kind:names[job.kind]??job.kind,source:"native",enabled:job.enabled,nextUtc:job.enabled?job.nextDueUtc:null,status:job.lastStatus});
  }
  for(const job of snapshot.schedules){
    if(!maps.has(job.mapId))continue;
    const enabled=job.enabled&&["Observation","ModCheck","ApiCheck"].includes(job.kind);
    rows.push({id:"inspection:"+job.id,mapId:job.mapId,mapName:maps.get(job.mapId)!,kind:names[job.kind]??job.kind,
      source:"inspection",enabled,nextUtc:enabled?job.nextUtc:null,status:job.kind==="SteamCheck"?"Korábbi inaktív ütemezés":job.state});
  }
  const time=(r:ScheduleRow)=>{const t=Date.parse(r.nextUtc??"");return Number.isFinite(t)?t:Infinity;};
  return rows.sort((a,b)=>Number(b.enabled)-Number(a.enabled)||time(a)-time(b)||a.mapName.localeCompare(b.mapName)||a.id.localeCompare(b.id));
}
