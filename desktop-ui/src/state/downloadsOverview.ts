import type {DurableSteamQueue,FreshInstallJob,StoppedSteamBatchCurrent} from "../bridge";
export type RollingStatus={current:{id:string;mapId:string;state:string;stage:string;oldBuild:string;targetBuild:string;error?:string|null;nativeSteamStage?:string|null}|null;busy:boolean};
export type DownloadRow={id:string;label:string;subject:string;state:string;stage:string;progress:number|null;detail:string};
export function checkedPercent(value:number|null|undefined):number|null{
  return typeof value==="number"&&Number.isFinite(value)&&value>=0&&value<=100?value:null;
}
export function describeDownloads(input:{
  fresh:FreshInstallJob|null;single:DurableSteamQueue|null;batch:StoppedSteamBatchCurrent|null;
  rolling:RollingStatus|null;mapNames:Record<string,string>;
}):DownloadRow[]{
  const rows:DownloadRow[]=[];
  const {fresh,single,batch,rolling,mapNames}=input;
  if(fresh)rows.push({id:"fresh:"+fresh.jobId,label:"Új telepítés",subject:fresh.mapName,
    state:fresh.errorCode?"Hiba":fresh.registered?"Regisztrálva":fresh.verified?"Ellenőrizve":"Folyamatban",
    stage:fresh.stage,progress:checkedPercent(fresh.percent),detail:fresh.errorCode??fresh.detail});
  if(single?.activeJob){const job=single.activeJob;rows.push({id:"single:"+job.jobId,label:"Egyedi frissítés",
    subject:mapNames[job.mapId]??"Helyi map",state:job.state,stage:job.stage,progress:null,
    detail:job.errorCode??[job.oldBuild,job.newBuild].filter(Boolean).join(" → ")});}
  else if(single?.exists)rows.push({id:"single:journal",label:"Soros frissítési napló",subject:single.activeMapName??"Helyi telepítés",
    state:single.needsOperatorReview?"Eredmény nem igazolt":single.phase??"Ismeretlen",
    stage:single.phase??"Ismeretlen",progress:null,detail:"Tartós natív napló; nem bizonyít aktív letöltést."});
  if(batch?.activeJob){const job=batch.activeJob;rows.push({id:"batch:"+job.jobId,label:"Kötegelt frissítés",subject:job.activeMapName||"Helyi mapok",
    state:job.state,stage:job.stage,progress:null,detail:job.errorCode??job.completedInstallations+" / "+job.total+" telepítés lezárva"});}
  else if(batch?.journalExists)rows.push({id:"batch:journal",label:"Kötegelt művelet naplója",subject:"Helyi mapok",state:batch.needsOperatorReview?"Eredmény nem igazolt":batch.journalPhase??"Ismeretlen",stage:batch.journalPhase??"Ismeretlen",progress:null,detail:"Tartós natív napló; nem letöltési százalék."});
  if(rolling?.current){const job=rolling.current;rows.push({id:"rolling:"+job.id,label:"Ütemezett frissítés",subject:mapNames[job.mapId]??"Helyi map",state:job.state,stage:job.nativeSteamStage??job.stage,progress:null,detail:job.error??[job.oldBuild,job.targetBuild].filter(Boolean).join(" → ")});}
  return rows;
}
