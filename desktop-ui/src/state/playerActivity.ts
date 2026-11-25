export type Player={id:string;name:string};
export type PlayerSample={utc:number;players:Player[]};
export type PlayerRecord={mapId:string;id:string;name:string;firstSeen:number;lastSeen:number;seconds:number;samples:number};
export type PlayerHistory={records:PlayerRecord[]};
const MAX_RECORDS=1000,MAX_GAP=90000,RETENTION=30*86400000;
const validId=(v:unknown):v is string=>typeof v==="string"&&/^[A-Za-z0-9]{16,64}$/.test(v);
const validMap=(v:unknown):v is string=>typeof v==="string"&&/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(v);
export function parsePlayerList(raw:string):Player[]|null{
  const text=raw.trim();
  if(/^No Players Connected\.?$/i.test(text))return [];
  const lines=text.split(/\r?\n/).map(x=>x.trim()).filter(Boolean);
  if(!lines.length||lines.length>100)return null;
  const result:Player[]=[],ids=new Set<string>();
  const first=Number(/^\d+/.exec(lines[0])?.[0]);
  if(first!==0&&first!==1)return null;
  for(let i=0;i<lines.length;i++){
    const m=/^(\d+)\.\s+(.+),\s*([A-Za-z0-9]{16,64})$/.exec(lines[i]);
    if(!m||Number(m[1])!==i+first||!validId(m[3])||m[2].length>120||/[\x00-\x1f\x7f]/.test(m[2]))return null;
    const id=m[3].toLowerCase();
    if(ids.has(id))return null;
    ids.add(id);result.push({id,name:m[2]});
  }
  return result;
}
function sanitizeRecords(records:PlayerRecord[],now:number){
  return records.filter(r=>validMap(r.mapId)&&validId(r.id)&&typeof r.name==="string"&&r.name.length<=120&&
    Number.isFinite(r.firstSeen)&&Number.isFinite(r.lastSeen)&&r.firstSeen<=r.lastSeen&&r.lastSeen<=now+5000&&now-r.lastSeen<=RETENTION&&
    Number.isFinite(r.seconds)&&r.seconds>=0&&r.seconds<=r.lastSeen/1000-r.firstSeen/1000+1&&
    Number.isSafeInteger(r.samples)&&r.samples>=1)
    .sort((a,b)=>b.lastSeen-a.lastSeen).slice(0,MAX_RECORDS)
    .map(r=>({mapId:r.mapId,id:r.id,name:r.name,firstSeen:r.firstSeen,lastSeen:r.lastSeen,seconds:r.seconds,samples:r.samples}));
}
export function observePlayerList(history:PlayerHistory,mapId:string,previous:PlayerSample|undefined,current:PlayerSample):PlayerHistory{
  if(!validMap(mapId)||!Number.isFinite(current.utc))return history;
  const records=sanitizeRecords(history.records,current.utc);
  const map=new Map(records.map(r=>[r.mapId+":"+r.id,r]));
  const delta=previous?current.utc-previous.utc:0;
  const continuous=delta>0&&delta<=MAX_GAP;
  const prevIds=new Set(continuous?previous!.players.map(p=>p.id):[]);
  for(const p of current.players){
    const key=mapId+":"+p.id,old=map.get(key);
    if(old&&old.lastSeen>=current.utc)continue;
    const seconds=old&&continuous&&old.lastSeen===previous!.utc&&prevIds.has(p.id)?delta/1000:0;
    map.set(key,{mapId,id:p.id,name:p.name,firstSeen:old?.firstSeen??current.utc,lastSeen:current.utc,
      seconds:(old?.seconds??0)+seconds,samples:(old?.samples??0)+1});
  }
  return {records:sanitizeRecords([...map.values()],current.utc)};
}
const storageKey=(host:string)=>"obsidian.player-observations.v1."+host;
export function loadPlayerHistory(host:string):PlayerHistory{
  try{
    const raw=localStorage.getItem(storageKey(host));
    if(!raw||raw.length>1000000)return {records:[]};
    const parsed=JSON.parse(raw);
    return {records:Array.isArray(parsed.records)?sanitizeRecords(parsed.records.filter((r:unknown)=>r!==null&&typeof r==="object").slice(0,MAX_RECORDS),Date.now()):[]};
  }catch{return {records:[]};}
}
export function savePlayerHistory(host:string,history:PlayerHistory):boolean{
  try{localStorage.setItem(storageKey(host),JSON.stringify({records:sanitizeRecords(history.records,Date.now())}));return true;}catch{return false;}
}
