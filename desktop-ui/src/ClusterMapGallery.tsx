import {useMemo,useState} from "react";
import {ArrowRight,Plus,ShieldCheck,Server,Search,X,ChevronDown,Grid2X2,LockKeyhole,Wrench,RadioTower} from "lucide-react";
import type {DashboardSnapshot,PublicMap} from "./bridge";
import {MapCover} from "./MapArtwork";

export const UK_MAPS=[
  "Astraeos","Lost Colony","Ragnarok","The Island","The Center",
  "Valguero","Aberration","Scorched Earth","Extinction","Genesis Part 1"
] as const;

type LocatedMap={map:PublicMap;host:string;local:boolean};
type Props={
  snapshot:DashboardSnapshot;
  query:string;
  onSelect:(id:string)=>void;
  onImport:()=>void;
  onInstall:(mapName?:string)=>void;
};

function StateLabel({running,needsSetup}:{running:boolean;needsSetup:boolean}){
  return <span className={"uk-gallery-state "+(running?"is-running":needsSetup?"is-setup":"is-stopped")}>
    <span aria-hidden="true"/>{running?"FUT":needsSetup?"BEÁLLÍTÁS":"LEÁLLÍTVA"}
  </span>;
}

function MapTile({entry,onSelect}:{entry:LocatedMap;onSelect:(id:string)=>void}){
  const {map,host,local}=entry;
  const body=<>
    <span className="uk-gallery-art"><MapCover name={map.name}/>
      <span className="uk-gallery-art-overlay"/>
      <span className="uk-gallery-state-wrap"><StateLabel running={map.running} needsSetup={map.gamePort===0}/></span>
    </span>
    <span className="uk-gallery-tile-body">
      <b className="uk-gallery-name">{map.name}</b>
      <span className="uk-gallery-meta"><Server size={12}/>{host}</span>
      {local&&(map.storeProtected||map.maintenance||map.rconConfigured)&&
        <span className="uk-gallery-profile-flags">
          {map.storeProtected&&<span className="uk-flag locked"><LockKeyhole size={12}/>
            STORE-V1 ZÁROLT</span>}
          {map.maintenance&&<span className="uk-flag maintenance"><Wrench size={12}/>
            KARBANTARTÁS</span>}
          {map.rconConfigured&&<span className="uk-flag rcon" title="RCON-adatok konfigurálva, kapcsolat nem ellenőrzött">
            <RadioTower size={12}/> RCON BEÁLLÍTVA</span>}
        </span>}
      <span className="uk-gallery-footer">
        <span>UDP <b>{map.gamePort||"—"}</b></span>
        <span className={local?"uk-gallery-local":"uk-gallery-remote"}>
          {local?<><span>{map.gamePort===0?"BEÁLLÍTÁS":"KEZELÉS"}</span><ArrowRight size={14}/></>:
            <><ShieldCheck size={14}/> CSAK OLVASÁS</>}
        </span>
      </span>
    </span>
  </>;
  return local?
    <button type="button" className="uk-gallery-tile is-local" onClick={()=>onSelect(map.id)}
      aria-label={map.name+" szerver megnyitása · "+host}>{body}</button>:
    <div className="uk-gallery-tile is-remote" aria-label={map.name+" távoli megfigyelés · "+host}>
      {body}
    </div>;
}

export function ClusterMapGallery({snapshot,query,onSelect,onImport,onInstall}:Props){
  const [state,setState]=useState<"all"|"running"|"stopped">("all");
  const [host,setHost]=useState<"all"|"local"|"remote">("all");
  const all=useMemo<LocatedMap[]>(()=>[
    ...snapshot.local.maps.map(map=>({map,host:snapshot.local.hostName,local:true})),
    ...(snapshot.remote?.maps??[]).map(map=>({
      map,host:snapshot.remote?.hostName??"",local:false
    }))
  ],[snapshot.local.maps,snapshot.local.hostName,snapshot.remote]);
  const visible=useMemo(()=>all.filter(entry=>{
    const term=query.trim().toLocaleLowerCase("hu-HU");
    return (!term||entry.map.name.toLocaleLowerCase("hu-HU").includes(term)||
        entry.host.toLocaleLowerCase("hu-HU").includes(term))&&
      (state==="all"||(state==="running"?entry.map.running:!entry.map.running))&&
      (host==="all"||(host==="local"?entry.local:!entry.local));
  }),[all,query,state,host]);
  const catalog=UK_MAPS.filter(name=>!all.some(entry=>
    entry.map.name.toLocaleLowerCase("en-US")===name.toLocaleLowerCase("en-US")));
  return <div className="uk-cluster-gallery">
    <div className="uk-gallery-controls">
      <div className="uk-gallery-tabs" role="group" aria-label="Map állapotszűrő">
        {([["all","Mind"],["running","Futók"],["stopped","Leállítottak"]] as const)
          .map(([key,label])=><button type="button" key={key}
            className={state===key?"is-active":""} aria-pressed={state===key}
            onClick={()=>setState(key)}>{label}</button>)}
      </div>
      <label className="uk-gallery-host-filter">
        <Server size={15}/>
        <select aria-label="Gazdagép szűrő" value={host}
          onChange={e=>setHost(e.target.value as typeof host)}>
          <option value="all">Mindkét gazdagép</option>
          <option value="local">Csak helyi</option>
          <option value="remote">Csak távoli</option>
        </select><ChevronDown size={14}/>
      </label>
      <span className="uk-gallery-result-count">{visible.length} MAP</span>
    </div>
    {visible.length>0?<div className="uk-host-map-groups" role="region"
      aria-label="Szervergaléria">
      {([["local","HELYI GAZDAGÉP",visible.filter(entry=>entry.local)],
        ["remote","LAN / CSAK MEGFIGYELÉS",visible.filter(entry=>!entry.local)]] as const)
        .filter(group=>group[2].length>0)
        .map(([kind,label,entries])=><section className={"uk-host-map-group "+kind}
          key={kind}>
          <header className="uk-host-map-heading">
            <span className="uk-host-map-heading-label">
              {kind==="local"?<Server size={15}/>:<ShieldCheck size={15}/>}
              {label}</span>
            <b>{entries[0].host}</b>
            <span>{entries.filter(entry=>entry.map.running).length}/{entries.length} FUT</span>
          </header>
          <div className="uk-map-gallery-grid">
            {entries.map(entry=><MapTile key={entry.host+entry.map.id}
              entry={entry} onSelect={onSelect}/>)}
          </div>
        </section>)}
    </div>:<div className="uk-gallery-empty">
      <Search size={28}/><b>{all.length?"Nincs ilyen szerver a szűrőben":
          "Nincs regisztrált ASA-szerver ezen a gépen"}</b>
      <p>{all.length?"Válassz másik állapotot vagy gazdagépet.":
          "Az alábbi térképek telepítési választékot jelentenek, nem futó vagy regisztrált szervereket."}</p>
      {(state!=="all"||host!=="all")&&
        <button type="button" className="secondary" onClick={()=>{setState("all");setHost("all");}}>
          <X size={15}/> Szűrők törlése
        </button>}
      {all.length===0&&!query&&<div className="uk-gallery-get-started">
        <button type="button" className="secondary" onClick={onImport}>
          <Plus size={16}/> Meglévő ASA átvétele
        </button>
        <button type="button" className="primary" onClick={()=>onInstall()}>
          <Plus size={16}/> ASA telepítése
        </button>
      </div>}
    </div>}
    {/* Keep uninstalled cluster maps selectable after the first local map
        has been registered. Remote-only filtering never offers local writes. */}
    {catalog.length>0&&!query.trim()&&state==="all"&&host!=="remote"&&<div className="uk-gallery-catalog">
      <div className="uk-gallery-catalog-heading">
        <span><Grid2X2 size={17}/> AZ UNTAMED KINGDOM 10 MAPJA</span>
        <small>Választék · nem regisztrált szerverállapot</small>
      </div>
      <div className="uk-map-gallery-grid is-discover" aria-label="ASA map választék">
        {catalog.map(name=><button key={name} type="button"
          className="uk-gallery-tile is-discovery" onClick={()=>onInstall(name)}
          aria-label={name+" · új szerver telepítése"}>
          <span className="uk-gallery-art"><MapCover name={name}/>
            <span className="uk-gallery-art-overlay"/></span>
          <span className="uk-gallery-tile-body"><b className="uk-gallery-name">{name}</b>
            <span className="uk-gallery-footer">
              <span>NINCS TELEPÍTVE</span>
              <span className="uk-gallery-local"><Plus size={14}/> TELEPÍTÉS</span>
            </span>
          </span>
        </button>)}
      </div>
    </div>}
  </div>;
}
