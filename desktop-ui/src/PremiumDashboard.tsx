import {useMemo,useState} from "react";
import {
  Activity, AlertTriangle, ArrowRight, Boxes, CheckCircle2, Cpu, Plus,
  HardDrive, Map as MapIcon, Monitor, Search, Server, Shield, Users, WifiOff
} from "lucide-react";
import type {DashboardSnapshot,PublicMap} from "./bridge";

type Props={
  snapshot:DashboardSnapshot;
  onServers:()=>void;
  onInstall:()=>void;
  onCluster:()=>void;
  onSelect:(id:string)=>void;
};

const mapThemes:Record<string,string>={
  "the island":"island","the center":"center","scorched earth":"desert",
  "aberration":"aberration","extinction":"extinction",
  "genesis part 1":"genesis","valguero":"valguero",
  "ragnarok":"ragnarok","astraeos":"astraeos","lost colony":"colony"
};

export function MapCover({name}:{name:string}){
  const theme=mapThemes[name.trim().toLocaleLowerCase("en-US")]??"unknown";
  return <span className={`premium-map-cover premium-map-${theme}`} aria-hidden="true">
    <span className="premium-cover-ridge"/><span className="premium-cover-light"/>
  </span>;
}

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

function ServerRow({map,host,local,onSelect}:{
  map:PublicMap;host:string;local:boolean;onSelect:(id:string)=>void
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
    <span role="cell" className={`premium-server-state ${map.running?"is-running":"is-stopped"}`}>
      <i aria-hidden="true"/>{map.running?"Fut":"Leállítva"}
    </span>
    <span className="premium-server-port" role="cell">
      {map.gamePort?map.gamePort:"—"}
    </span>
    <div className="premium-server-action" role="cell">
      {local?<button type="button" className="premium-icon-action"
          aria-label={map.name+" részletei"} onClick={()=>onSelect(map.id)}
          title="Helyi map részletei"><ArrowRight size={17}/></button>:
        <span className="premium-remote-lock"><Shield size={16}/> Csak olvasás</span>}
    </div>
  </div>;
}

function HostPanel({snapshot}:{snapshot:DashboardSnapshot}){
  const local=snapshot.local;
  const remote=snapshot.remote;
  const ram=local.memoryTotalKiB&&local.memoryUsedKiB!=null
    ?Math.min(100,Math.max(0,local.memoryUsedKiB/local.memoryTotalKiB*100)):null;
  const cpu=local.cpuPercent!=null?Math.min(100,Math.max(0,local.cpuPercent)):null;
  const present=(v:number|null)=>v==null?"Nem mérhető":v.toFixed(0)+"%";
  return <section className="premium-panel premium-hosts">
    <header className="premium-panel-heading"><h2><Monitor size={19}/> Gazdagépek</h2>
      <span className="premium-small-muted">{remote?"2 host adat":"1 helyi host"}</span></header>
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
      {remote?<div className="premium-host-record">
        <div className="premium-host-heading">
          <span className="premium-host-device"><Server size={20}/></span>
          <div><b>{remote.hostName}</b><small><i className="online-indicator"/> Hitelesített LAN-adat</small></div>
          <span className="premium-count-pill">{remote.maps.filter(x=>x.running).length}/{remote.maps.length} fut</span>
        </div>
        <p className="premium-small-muted">Távoli CPU/RAM nincs hitelesítve · megfigyelés</p>
      </div>:<div className="premium-host-offline"><WifiOff size={15}/>
        {snapshot.peerMessage||"Távoli peer még nincs csatlakoztatva"}</div>}
    </div>
  </section>;
}

export function PremiumDashboard({snapshot,onServers,onInstall,onCluster,onSelect}:Props){
  const [query,setQuery]=useState("");
  const hosts=1+(snapshot.remote?1:0);
  const local=snapshot.local.maps;
  const peer=snapshot.remote?.maps??[];
  const maps=useMemo(()=>{
    const term=query.trim().toLocaleLowerCase("hu-HU");
    return [
      ...local.map(map=>({map,host:snapshot.local.hostName,isLocal:true})),
      ...peer.map(map=>({map,host:snapshot.remote!.hostName,isLocal:false}))
    ].filter(x=>x.map.name.toLocaleLowerCase("hu-HU").includes(term));
  },[query,local,peer,snapshot.local.hostName,snapshot.remote]);
  const running=[...local,...peer].filter(m=>m.running).length;
  return <div className="premium-dashboard">
    <section className="hero premium-dashboard-banner">
      <div className="premium-banner-content">
        <span className="premium-kicker">UNTAMED KINGDOM / CLUSTER OVERVIEW</span>
        <p className="premium-overview-subtitle">Szerverüzemeltetés · {snapshot.local.hostName} ·
          {snapshot.remote?" kétgépes LAN-megfigyelés":" helyi Manager"}</p>
      </div>
      <span className="premium-banner-mark" aria-hidden="true">UNTAMED <b>MANAGER</b></span>
    </section>
    <div className="premium-metrics">
      <Metric icon={MapIcon} value={local.length+peer.length} label="Map"
        detail="Regisztrált, megfigyelt szerverek"/>
      <Metric icon={Activity} value={running} label="Fut"
        detail="Ténylegesen megfigyelt állapot"/>
      <Metric icon={Users} value="—" label="Játékosok"
        detail="Még nincs igazolt RCON-adat" tone="cyan"/>
      <Metric icon={Monitor} value={hosts} label="Gazdagép"
        detail={snapshot.remote?"Helyi + hitelesített LAN":"Helyi gép"} tone="cyan"/>
    </div>
    <div className="premium-dashboard-grid">
      <section className="premium-panel premium-server-panel">
        <header className="premium-panel-heading premium-servers-heading">
          <h2><Server size={19}/> Szerverek <span>({running} / {local.length+peer.length})</span></h2>
          <div className="premium-panel-controls">
            <label className="premium-search"><Search size={15}/>
              <input aria-label="Áttekintés map keresése" placeholder="Map keresése..."
                value={query} onChange={e=>setQuery(e.target.value)}/></label>
            <button className="primary" type="button" onClick={onServers}>
              <ArrowRight size={15}/> Mapok kezelése</button>
          </div>
        </header>
        <div className="premium-server-list" role="table" aria-label="Cluster szerverlista">
          <div className="premium-server-table-head" role="row">
            <span>MAP / SZERVER</span><span>GAZDAGÉP</span>
            <span>ÁLLAPOT</span><span>GAME UDP</span><span>RÉSZLETEK</span>
          </div>
          {maps.map(({map,host,isLocal})=><ServerRow key={host+map.id}
            map={map} host={host} local={isLocal} onSelect={onSelect}/>)}
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
              <button type="button" className="primary" onClick={onInstall}>
                Új ASA letöltése · SteamCMD <Plus size={15}/></button>
            </div>}
          </div>}
        </div>
        <div className="premium-panel-footnote"><Shield size={14}/> A távoli szerverek csak megfigyelhetők; nem jelenítünk meg kitalált játékosadatokat.</div>
      </section>
      <div className="premium-dashboard-aside">
        <HostPanel snapshot={snapshot}/>
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
            <span>Távoli állapot</span><b>{snapshot.remote?"LAN-adat elérhető":"Nincs peer"}</b></div>
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
