// Only fixed action identifiers are persisted, never messages, custom
// command arguments, player responses, tokens or credentials.
export const readCommands=["ListPlayers","GetGameLog","GetChat"] as const;
export type ReadCommand=typeof readCommands[number];
const actions=[...readCommands,"SaveWorld","broadcast","serverChat","wildWipe","custom"] as const;
type Action=typeof actions[number];
type Entry={action:Action;kind:"reply"|"dispatch";utc:string};
export type History={entries:Entry[];favorites:ReadCommand[]};
const prefix="obsidian.rcon.history.v1.";
const validMap=(id:string)=>/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(id);
const isRead=(x:unknown):x is ReadCommand=>readCommands.includes(x as ReadCommand);
const isAction=(x:unknown):x is Action=>actions.includes(x as Action);
export function decodeHistory(raw:string|null):History{
  try{
    const parsed=JSON.parse(raw??"{}");
    const entries:Entry[]=[];
    if(Array.isArray(parsed.entries))for(const x of parsed.entries.slice(0,100)){
      if(!x||!isAction(x.action)||!["reply","dispatch"].includes(x.kind)||typeof x.utc!=="string"||!Number.isFinite(Date.parse(x.utc)))continue;
      if((x.kind==="reply")!==isRead(x.action))continue;
      entries.push({action:x.action,kind:x.kind,utc:new Date(x.utc).toISOString()});
    }
    return {entries,favorites:Array.isArray(parsed.favorites)?[...new Set<ReadCommand>(parsed.favorites.filter(isRead))]:[]};
  }catch{return {entries:[],favorites:[]};}
}
export function loadHistory(mapId:string):History{
  if(!validMap(mapId))return decodeHistory(null);
  try{return decodeHistory(localStorage.getItem(prefix+mapId.toLowerCase()));}catch{return decodeHistory(null);}
}
function write(mapId:string,history:History):boolean{
  if(!validMap(mapId))return false;
  try{
    localStorage.setItem(prefix+mapId.toLowerCase(),JSON.stringify(history));
    window.dispatchEvent(new CustomEvent("obsidian-rcon-history",{detail:mapId.toLowerCase()}));
    return true;
  }catch{return false;}
}
export function recordRconEvent(mapId:string,action:unknown,kind:Entry["kind"]):boolean{
  if(!isAction(action)||(kind==="reply")!==isRead(action))return false;
  const old=loadHistory(mapId);
  return write(mapId,{...old,entries:[{action,kind,utc:new Date().toISOString()},...old.entries].slice(0,100)});
}
export function toggleFavorite(mapId:string,action:ReadCommand):boolean{
  if(!isRead(action))return false;
  const old=loadHistory(mapId);
  return write(mapId,{...old,favorites:old.favorites.includes(action)?old.favorites.filter(x=>x!==action):[...old.favorites,action]});
}
export function clearRconHistory(mapId:string):boolean{return write(mapId,{...loadHistory(mapId),entries:[]});}
