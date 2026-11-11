import {useEffect,useMemo,useState} from "react";
import {
  Activity, AlertTriangle, ArrowRight, Boxes, CheckCircle2, Cpu, Plus,
  HardDrive, Map as MapIcon, Monitor, Search, Server, Shield, Users, WifiOff, Grid2X2, List
} from "lucide-react";
import {dashboardRemotes,managerBridge,type DashboardSnapshot,type PublicMap} from "./bridge";
import {MapCover} from "./MapArtwork";
import {ClusterMapGallery} from "./ClusterMapGallery";

type Props={
  snapshot:DashboardSnapshot;
  onServers:()=>void;
  onInstall:(mapName?:string)=>void;
  onCluster:()=>void;
  onSelect:(id:string)=>void;
};

function Metric({icon:Icon,value,label,detail,tone="mint"}:{
  icon:typeof Server;value:string|number;label:string;detail:string;tone?:"mint"|"cyan"|"gold"
}){
  return <div className={`premium-metric tone-${tone}`}>
    <div className="premium-metric-symbol"><Icon size={31} strokeWidth={1.9}/></div>
    <div className="premium-metric-copy"><strong>{value}</strong>
      <span>{label}</span><small>{detail}</small>
    </div>
  </div>;
}

/** ASA ListPlayers: never guess zero on errors, unsupported output or stale peers. */
export function parseAsaPlayerCount(raw:string):number|null {
  const response=raw.trim();
  if(/^No Players Connected\\.?$/i.test(response))return 0;
  const lines=response.split(/\\r?\\n/).map(line=>line.trim()).filter(Boolean);
  if(!lines.length||lines.length>100)return null;
  // Names may contain punctuation/commas. The server-provided player ID
  // follows the final comma; no identifiers are stored or rendered here.
  if(!lines.every(line=>/^\\d+\\.\\s+.+,\\s*[A-Za-z0-9]{16,64}$/.test(line)))return null;
  return lines.length;
}

function ServerRow({map,host,local,onSelect,playerCount}:{
  map:PublicMap;host:string;local:boolean;onSelect:(id:string)=>void;
  playerCount:number|null;
}){
  return <div className="premium-server-row" role="row"
    tabIndex={local?0:-1}
    onClick={()=>{if(local)onSelect(map.id);}}
    onKeyDown={e=>{if(local&&(e.key==="Enter"||e.key===" ")){
      e.preventDefault();onSelect(map.id);
    }}}>
    <div className="premium-server-identity" role="cell">
      <MapCover name={map.name}/>
      <div><b>{map.name}</b><small>ASA · {map.steamBuildId?"Build "+map.steamBuildId:"Build nem ellenőrzött"}</small></div>
    </div>
    <span className="premium-server-host" role="cell">{host}</span>
    <span role="cell" className={`premium-server-state ${map.running&&!map.observedReadOnlyImported?"is-running":"is-stopped"}`}>
      <i aria-hidden="true"/>{map.observedReadOnlyImported?
        (map.observedRuntimeState==="observedRunning"?"Megfigyelt · fut":"Megfigyelés: nem igazolt"):
        (map.running?"Fut":"Leállítva")}
    </span>
    <span className="premium-server-players" role="cell"
      title={playerCount===null?"Nincs hitelesített, aktuális RCON játékoslista":"Hitelesített helyi RCON ListPlayers"}>
      {playerCount===null?"—":playerCount}
    </span>
    <span className="premium-server-port" role="cell">
      {map.gamePort?map.gamePort:"—"}
    </span>
    <div className="premium-server-action" role="cell">
      {local?<button type="button" className="premium-icon-action"
          aria-label={map.name+" részletei"} onClick={e=>{e.stopPropagation();onSelect(map.id);}}
          title="Helyi map részletei"><ArrowRight size={17}/></button>:
        <span className="premium-remote-lock"><Shield size={16}/> Csak olvasás</span>}
    </div>
  </div>;
}

function HostPanel({snapshot,onCluster}:{snapshot:DashboardSnapshot;onCluster:()=>void}){
  const local=snapshot.local;
  const remotes=dashboardRemotes(snapshot);
  const ram=local.memoryTotalKiB&&local.memoryUsedKiB!=null
    ?Math.min(100,Math.max(0,local.memoryUsedKiB/local.memoryTotalKiB*100)):null;
  const cpu=local.cpuPercent!=null?Math.min(100,Math.max(0,local.cpuPercent)):null;
  const present=(v:number|null)=>v==null?"Nem mérhető":v.toFixed(0)+"%";
  return <section className="premium-panel premium-hosts">
    <header className="premium-panel-heading"><h2><Monitor size={19}/> Gazdagépek</h2>
      <span className="premium-small-muted">{1+remotes.length} host · {remotes.filter(x=>x.fresh!==false).length} online peer</span></header>
    <div className="premium-host-body">
      <div className="premium-host-record">
        <div className="premium-host-heading">
          <span className="premium-host-device"><Server size={20}/></span>
          <div><b>{local.hostName}</b><small><i className="online-indicator"/> Helyi Manager</small></div>
          <span className="premium-count-pill">{local.maps.filter(x=>x.running).length}/{local.maps.length} fut</span>
        </div>
        <div className="premium-resources">
          <div><label><Cpu size={13}/> CPU <b>{present(cpu)}</b></label>
            <span className="premium-bar"><span style={{width:(cpu??0)+"%"}}/></span></div>
          <div><label><HardDrive size={13}/> RAM <b>{present(ram)}</b></label>
            <span className="premium-bar cyan"><span style={{width:(ram??0)+"%"}}/></span></div>
        </div>
      </div>
      {remotes.length?remotes.map(remote=><div className="premium-host-record" key={remote.hostId}>
        <div className="premium-host-heading">
          <span className="premium-host-device"><Server size={20}/></span>
          <div><b>{remote.hostName}</b><small><i className={remote.fresh===false?"offline-indicator":"online-indicator"}/>
            {remote.fresh===false?"Elavult / offline LAN-adat":"Hitelesített LAN-adat"}</small></div>
          <span className="premium-count-pill">{remote.maps.filter(x=>x.running).length}/{remote.maps.length} fut</span>
        </div>
        <p className="premium-small-muted">Távoli CPU/RAM nincs hitelesítve · csak megfigyelés
          {remote.lastSuccessUtc?` · utolsó sikeres mérés ${new Date(remote.lastSuccessUtc).toLocaleTimeString("hu-HU")}`:""}
          {remote.stateCode?` · ${remote.stateCode}`:""}</p>
      </div>):<div className="premium-host-offline"><WifiOff size={15}/>
        <div>{snapshot.peerMessage||"Távoli peer még nincs csatlakoztatva"}
          <button className="primary" type="button" onClick={onCluster}>
            Másik szervergép párosítása
          </button>
        </div></div>}
    </div>
  </section>;
}

export function PremiumDashboard({snapshot,onServers,onInstall,onCluster,onSelect}:Props){
  const [query,setQuery]=useState("");
  // Approved Dashboard reference: compact table + right-side host/alerts.
  // The full artwork gallery remains an explicit toggle, not the default.
  const [view,setView]=useState<"gallery"|"table">("table");
  const remotes=useMemo(()=>dashboardRemotes(snapshot),[snapshot.remotes,snapshot.remote]);
  const hosts=1+remotes.length;
  const local=snapshot.local.maps;
  const peer=useMemo(()=>remotes.flatMap(remote=>remote.maps),[remotes]);
  const [playerCounts,setPlayerCounts]=useState<Record<string,number|null>>({});
  const eligiblePlayers=local.filter(map=>map.running&&!map.observedReadOnlyImported&&
    map.rconConfigured&&!map.maintenance)
    .map(map=>map.id+":"+(map.storeProtected?"historical":"local"))
    .sort().join("|");
  useEffect(()=>{
    // Only explicitly registered running local maps. Remote peers are not
    // queried through this Manager, and failures must never display zero.
    let closed=false,busy=false;
    const maps=eligiblePlayers?eligiblePlayers.split("|").map(item=>{
      const colon=item.lastIndexOf(":");
      return {id:item.slice(0,colon),historical:item.slice(colon+1)==="historical"};
    }):[];
    setPlayerCounts({});
    const refresh=async()=>{
      if(closed||busy||!maps.length)return;
      busy=true;
      try {
        const counts=await Promise.all(maps.map(async map=>{
          try{
            const answer=map.historical
              ?await managerBridge.historicalRconPlayers(map.id)
              :await managerBridge.rconListPlayers(map.id);
            return [map.id,parseAsaPlayerCount(answer.response)] as const;
          }catch{return [map.id,null] as const;}
        }));
        if(!closed)setPlayerCounts(Object.fromEntries(counts));
      }finally{busy=false;}
    };
    void refresh();
    const poll=window.setInterval(()=>{void refresh();},45000);
    return ()=>{closed=true;window.clearInterval(poll);};
  },[eligiblePlayers]);
  const maps=useMemo(()=>{
    const term=query.trim().toLocaleLowerCase("hu-HU");
    return [
      ...local.map(map=>({map,host:snapshot.local.hostName,isLocal:true})),
      ...remotes.flatMap(remote=>remote.maps.map(map=>({map,host:remote.hostName,isLocal:false})))
    ].filter(x=>x.map.name.toLocaleLowerCase("hu-HU").includes(term));
  },[query,local,remotes,snapshot.local.hostName]);
  const running=[...local,...peer].filter(m=>m.running).length;
  return <div className="premium-dashboard">
    <section className="hero premium-dashboard-banner">
      <div className="premium-banner-content">
        <span className="premium-kicker">UNTAMED KINGDOM / CLUSTER OVERVIEW</span>
        <strong className="uk-hero-title">UNTAMED KINGDOM <span>ASA CLUSTER</span></strong>
        <p className="premium-overview-subtitle">Szerverüzemeltetés · {snapshot.local.hostName} ·
          {remotes.length?` ${hosts} gazdagépes LAN-megfigyelés`:" helyi Manager"}</p>
      </div>
      <span className="premium-banner-mark" aria-hidden="true">UNTAMED <b>MANAGER</b></span>
    </section>
    <div className="premium-metrics">
      <Metric icon={MapIcon} value={local.length+peer.length} label="Map"
        detail="Regisztrált, megfigyelt szerverek"/>
      <Metric icon={Activity} value={running} label="Fut"
        detail="Ténylegesen megfigyelt állapot"/>
      <Metric icon={Users}
        value={local.filter(map=>map.running&&playerCounts[map.id]!==null&&
          playerCounts[map.id]!==undefined).length
          ?local.reduce((sum,map)=>sum+(playerCounts[map.id]??0),0):"—"}
        label="Játékosok" detail="Igazolt helyi RCON-adatok; távoli mapok nélkül"
        tone="cyan"/>
      <Metric icon={Monitor} value={hosts} label="Gazdagép"
        detail={remotes.length?`Helyi + ${remotes.length} LAN-peer`:"Helyi gép"} tone="cyan"/>
    </div>
    <div className="premium-dashboard-grid">
      <section className="premium-panel premium-server-panel">
        <header className="premium-panel-heading premium-servers-heading">
          <h2><Server size={19}/> Szerverek <span>({running} / {local.length+peer.length})</span></h2>
          <div className="premium-panel-controls">
            <label className="premium-search"><Search size={15}/>
              <input aria-label="Áttekintés map keresése" placeholder="Map keresése..."
                value={query} onChange={e=>setQuery(e.target.value)}/></label>
            <div className="premium-view-switcher" role="group" aria-label="Szerver nézet">
              <button type="button" className={view==="gallery"?"is-active":""}
                aria-pressed={view==="gallery"} onClick={()=>setView("gallery")}>
                <Grid2X2 size={14}/> Kártyák</button>
              <button type="button" className={view==="table"?"is-active":""}
                aria-pressed={view==="table"} onClick={()=>setView("table")}>
                <List size={14}/> Lista</button>
            </div>
            <button className="primary" type="button" onClick={onServers}>
              <ArrowRight size={15}/> Mapok kezelése</button>
          </div>
        </header>
        {view==="gallery" ?
          <ClusterMapGallery snapshot={snapshot} query={query} onSelect={onSelect}
            onImport={onServers} onInstall={onInstall}/>:
        <div className="premium-server-list" role="table" aria-label="Cluster szerverlista">
          <div className="premium-server-table-head" role="row">
            <span>MAP / SZERVER</span><span>GAZDAGÉP</span>
            <span>ÁLLAPOT</span><span>JÁTÉKOSOK</span><span>GAME UDP</span><span>RÉSZLETEK</span>
          </div>
          {maps.map(({map,host,isLocal})=><ServerRow key={host+map.id}
            map={map} host={host} local={isLocal} onSelect={onSelect}
            playerCount={isLocal?(playerCounts[map.id]??null):null}/>)}
          {maps.length===0 && <div className="premium-dashboard-empty">
            <Boxes size={30}/><b>{query?"Nincs találat":"A helyi Manager még nem tartalmaz mapot"}</b>
            <p>{query?"Próbálj másik keresést.":"A már telepített ASA-mapokat az eredeti konfigurációjuk megőrzésével veheted fel. Az alábbiak elérhető mapválasztékok, nem futó vagy regisztrált szerverek."}</p>
            {!query&&<div className="premium-onboarding-maps" aria-label="Felvehető mapok mintái">
              {["The Island","Astraeos","Ragnarok","Aberration"].map(name=>
                <button type="button" key={name} onClick={onServers}>
                  <MapCover name={name}/><span>{name}</span><Plus size={15}/>
                </button>)}
            </div>}
            {!query&&<div className="premium-onboarding-actions">
              <button type="button" className="secondary" onClick={onServers}>
                Meglévő ASA importálása <ArrowRight size={15}/></button>
              <button type="button" className="primary" onClick={()=>onInstall()}>
                Új ASA letöltése · SteamCMD <Plus size={15}/></button>
            </div>}
          </div>}
        </div>}
        <div className="premium-panel-footnote"><Shield size={14}/> A távoli szerverek csak megfigyelhetők; nem jelenítünk meg kitalált játékosadatokat.</div>
      </section>
      <div className="premium-dashboard-aside">
        <HostPanel snapshot={snapshot} onCluster={onCluster}/>
        <section className="premium-panel premium-events">
          <header className="premium-panel-heading"><h2><Activity size={19}/> Állapotfigyelés</h2>
            <button className="quiet" type="button" onClick={onCluster}>Cluster <ArrowRight size={15}/></button>
          </header>
          <div className="premium-event-row"><CheckCircle2 size={17}/>
            <span>Utolsó helyi állapotmérés</span>
            <b>{new Date(snapshot.observedUtc).toLocaleTimeString("hu-HU")}</b></div>
          <div className="premium-event-row"><Activity size={17}/>
            <span>Futó mapok</span><b>{running}</b></div>
          <div className="premium-event-row"><Monitor size={17}/>
            <span>Távoli állapot</span><b>{remotes.length?`${remotes.filter(remote=>remote.fresh!==false).length}/${remotes.length} online`:"Nincs peer"}</b></div>
          <p className="premium-small-muted">Ezek állapotadatok, nem kitalált naplóesemények.</p>
        </section>
        <section className="premium-panel premium-security">
          <header className="premium-panel-heading"><h2><Shield size={19}/> Biztonsági állapot</h2></header>
          <div className="premium-security-line"><CheckCircle2 size={16}/> Natív Manager adatkapcsolat aktív</div>
          <div className="premium-security-line"><CheckCircle2 size={16}/> Távoli LAN-parancsok nincsenek engedélyezve</div>
          <div className="premium-security-line caution"><AlertTriangle size={16}/> Store-v1 éles utazás még nem igazolt</div>
        </section>
      </div>
    </div>
  </div>;
}
