import { ArrowRight, Monitor, Server, Shield } from "lucide-react";
import { type PublicMap } from "./bridge";
import { MapCover } from "./MapArtwork";

export function observedAt(raw: string | null | undefined): string {
  if(!raw)return "Nincs korábbi mérés";
  const d = new Date(raw);
  return Number.isNaN(d.getTime()) ? "Ismeretlen" : d.toLocaleTimeString("hu-HU");
}
export function State({ running, status }: { running: boolean; status: string }) {
  return <span className={running ? "state online" : "state stopped"}>
    <i className="status-dot"/>{status || (running ? "Fut" : "Leállítva")}
  </span>;
}

export function MapTable({ maps, host, onPick, filter = "" }: {
  maps: PublicMap[]; host: string; onPick?: (id: string) => void; filter?: string
}) {
  const needle=filter.trim().toLocaleLowerCase("hu-HU");
  const rows=maps.filter(m=>m.name.toLocaleLowerCase("hu-HU").includes(needle));
  if(!rows.length)return <div className="premium-map-empty">
    <Server size={27}/>
    <b>{needle?"Nem található megfelelő map":"Nincs regisztrált map"}</b>
    <span>{needle?"Módosítsd a keresési kifejezést.":"A helyi szerverek a fenti importálással vehetők fel; az éles fájlokhoz nem nyúlunk."}</span>
  </div>;
  return <div className="premium-map-table" role="table" aria-label="Szerverek">
    <div className="premium-map-table-heading" role="row">
      <span role="columnheader">MAP / SZERVER</span>
      <span role="columnheader">GAZDAGÉP</span>
      <span role="columnheader">ÁLLAPOT</span>
      <span role="columnheader">GAME UDP</span>
      <span role="columnheader">RÉSZLETEK</span>
    </div>
    {rows.map(map=><div className={"premium-map-line"+(onPick?" is-local":" is-remote")}
        role="row" key={map.id} tabIndex={onPick?0:undefined}
        aria-label={map.name+" · "+host+" · "+(map.observedReadOnlyImported?
          (map.observedRuntimeState==="observedRunning"?"Megfigyelt · fut":"Megfigyelés: nem igazolt"):
          (map.running?"Fut":"Leállítva"))}
        onClick={onPick?()=>onPick(map.id):undefined}
        onKeyDown={onPick?(e)=>{
          if(e.key==="Enter"||e.key===" "){e.preventDefault();onPick(map.id);}
        }:undefined}>
      <span className="premium-map-title" role="cell">
        <MapCover name={map.name}/>
        <span><b>{map.name}</b>
          <small>ARK: Survival Ascended · {map.steamBuildId?
            "Build "+map.steamBuildId:"Build nem ellenőrzött"}</small>
        </span>
      </span>
      <span className="premium-map-host" role="cell">{host}</span>
      <span className="premium-map-status" role="cell">
        {map.observedReadOnlyImported?
          <span className="state stopped"><Shield size={13}/>{
            map.observedRuntimeState==="observedRunning"?
              "Megfigyelt · fut":"Megfigyelés: nem igazolt"
          }</span>:
          <State running={map.running} status={map.status}/>}

      </span>
      <span className="premium-map-port mono" role="cell">{map.gamePort||"—"}</span>
      <span className="premium-map-actions" role="cell">
        {onPick?<span className="premium-map-action-symbol" title="Map részleteinek megnyitása">
          <ArrowRight size={17}/></span>:
          <span className="premium-map-readonly"><Shield size={16}/> Megfigyelés</span>}
      </span>
    </div>)}
  </div>;
}
export function HostCard({ host, local = false }: {
  host: { hostName: string; maps: PublicMap[] }; local?: boolean
}) {
  const running = host.maps.filter(m => m.running).length;
  return <div className="host">
    <div className="host-symbol"><Monitor size={21}/></div>
    <div className="host-copy"><b>{host.hostName}</b><span className="muted tiny">{local ? "Ez a Windows-gazdagép" : "Hitelesített LAN-peer"}</span></div>
    <span className="host-count">{running}/{host.maps.length} fut</span>
  </div>;
}
