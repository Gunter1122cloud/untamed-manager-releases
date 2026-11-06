import {useCallback,useEffect,useMemo,useState} from "react";
import {Activity,AlertTriangle,CalendarClock,LockKeyhole,RefreshCw,Server,Shield,Terminal,WifiOff} from "lucide-react";
import {managerBridge,type OperationsMap,type OperationsSnapshot} from "./bridge";
import "./operations-fidelity.css";

function elapsed(seconds:number|null){
  if(seconds===null || seconds<0)return "Nem ellenőrizhető";
  const hours=Math.floor(seconds/3600);
  const days=Math.floor(hours/24);
  return (days?days+" nap ":"")+(hours%24)+" ó "+Math.floor(seconds%3600/60)+" p";
}
function localDate(iso:string|null){
  if(!iso)return "Nem ellenőrizhető";
  const d=new Date(iso);
  return Number.isNaN(d.getTime())?"Nem ellenőrizhető":d.toLocaleString("hu-HU");
}
function OperationCard({map,onOpen}:{map:OperationsMap;onOpen:(id:string)=>void}){
  return <article className="ops-map">
    <div className="ops-map-top">
      <div><strong>{map.mapName}</strong><span>{map.status||"Ismeretlen folyamatállapot"}</span></div>
      <span className={"ops-state "+(map.running?"running":map.confirmedStopped?"stopped":"unknown")}>
        {map.running?"FUT":map.confirmedStopped?"LEÁLLÍTVA":"NEM IGAZOLT"}
      </span>
    </div>
    <dl className="ops-values">
      <div><dt>PID</dt><dd>{map.pid??"Nem mérhető"}</dd></div>
      <div><dt>Futási idő</dt><dd>{elapsed(map.uptimeSeconds)}</dd></div>
      <div><dt>ASA RAM</dt><dd>{map.memoryVerified&&map.memoryMiB!==null?map.memoryMiB.toLocaleString("hu-HU")+" MiB":"Nem mérhető"}</dd></div>
      <div><dt>Game / Query</dt><dd>{map.gamePort||"—"} / {map.queryPort||"—"}</dd></div>
      <div><dt>RCON TCP</dt><dd>{map.rconConfigured&&map.rconPort?map.rconPort:"Nincs konfigurálva"}</dd></div>
      <div><dt>Indítva</dt><dd>{localDate(map.startedUtc)}</dd></div>
    </dl>
    {(map.storeProtected||map.maintenance)&&<div className="ops-tags">
      {map.storeProtected&&<span><LockKeyhole size={13}/> Store-v1 védelem</span>}
      {map.maintenance&&<span><Shield size={13}/> Karbantartási mód</span>}
    </div>}
    <div className="ops-card-foot">
      <span><Shield size={14}/> Csak helyi ellenőrzés</span>
      <button type="button" className="secondary" onClick={()=>onOpen(map.mapId)}>
        <Terminal size={15}/> Map megnyitása
      </button>
    </div>
  </article>;
}

export function OperationsCenter({onOpenMap,selectedMapId}:{onOpenMap:(id:string)=>void;selectedMapId?:string}){
  const [snapshot,setSnapshot]=useState<OperationsSnapshot|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [filter,setFilter]=useState<"all"|"running"|"attention">("all");
  const reload=useCallback(async()=>{
    setBusy(true);setError("");
    try{
      const result=selectedMapId
        ?await managerBridge.operationsMap(selectedMapId)
        :await managerBridge.operationsSnapshot();
      setSnapshot(result);
    }catch(e){
      setError("A natív üzemeltetési állapot nem ellenőrizhető ("+
        (e instanceof Error?e.message:"UM-OPS-UNAVAILABLE")+").");
      setSnapshot(null);
    }finally{setBusy(false);}
  },[selectedMapId]);
  useEffect(()=>{void reload();},[reload]);
  const visible=useMemo(()=>(snapshot?.maps??[]).filter(x=>
    filter==="all"||(filter==="running"?x.running:
    x.maintenance||x.storeProtected||(!x.running&&!x.confirmedStopped))
  ),[snapshot,filter]);
  return <section className="ops-center">
    {!selectedMapId&&<div className="page-intro">
      <span className="eyebrow">NATIVE / HELYI ÜZEMELTETÉS</span><h1>Üzemeltetés</h1>
      <p>Valódi helyi PID, folyamatindítási idő, memória és mentett ütemezések. A távoli gépek itt nem vezérelhetők.</p>
    </div>}
    <div className="ops-toolbar">
      <div className="ops-observed"><Activity size={17}/>
        {snapshot?snapshot.hostName+" · mérés: "+localDate(snapshot.observedUtc):
          "Windows-állapot lekérése"}</div>
      {!selectedMapId&&<div className="ops-filters" role="group" aria-label="Üzemeltetési szűrő">
        {([["all","Összes"],["running","Futók"],["attention","Védelem / vizsgálat"]] as const)
          .map(([key,label])=><button type="button" key={key}
            className={filter===key?"is-active":""} aria-pressed={filter===key}
            onClick={()=>setFilter(key)}>{label}</button>)}
      </div>}
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>{void reload();}}><RefreshCw size={16} className={busy?"spin":""}/>
        Frissítés</button>
    </div>
    {error&&<div role="alert" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
    {!snapshot&&!error&&<div role="status" className="empty">Helyi Windows-folyamatok ellenőrzése...</div>}
    {snapshot&&<>
      <div className="ops-summary">
        <div><Server size={20}/><strong>{snapshot.maps.length}</strong><span>Helyi map</span></div>
        <div><Activity size={20}/><strong>{snapshot.maps.filter(x=>x.running).length}</strong><span>Igazoltan fut</span></div>
        <div><Shield size={20}/><strong>{snapshot.maps.filter(x=>x.storeProtected).length}</strong><span>Store-v1 védett</span></div>
        <div><CalendarClock size={20}/><strong>{snapshot.schedules.length}</strong><span>Mentett ütemezés</span></div>
      </div>
      {visible.length===0?<div className="empty">A szűrőnek megfelelő helyi map nem található.</div>:
        <div className="ops-grid">
          {visible.map(map=><OperationCard key={map.mapId} map={map} onOpen={onOpenMap}/>)}
        </div>}
      {!selectedMapId&&<section className="premium-panel ops-schedules">
        <header className="premium-panel-heading"><h2><CalendarClock size={19}/> Meglévő ütemezések</h2>
          <span className="premium-small-muted">Core / Budapest időzóna</span></header>
        <p>Az itt látható események a Manager mentett konfigurációjából származnak. Az előnézet nem indít restartot, mentést vagy RCON-parancsot; a felügyeleti végrehajtás külön engedélyhez kötött.</p>
        {snapshot.schedules.length===0?<p className="ops-empty-schedule">
          Még nincs mentett ismétlődő ütemezés ezen a gépen.</p>:
          <div className="ops-schedule-scroll">
            <table className="ops-schedule-table"><thead><tr>
              <th>Map</th><th>Művelet</th><th>Ismétlés</th><th>Következő időpont</th><th>Állapot</th>
            </tr></thead><tbody>{snapshot.schedules.map(s=><tr key={s.id}>
              <td>{s.mapName}</td><td>{s.kind}</td><td>{s.frequency}</td>
              <td>{localDate(s.nextUtc)}</td>
              <td>{!s.enabled?"Kikapcsolva":s.state==="read-only-preview"?
                "Megfigyelési előnézet":s.state==="operator-approval-required"?
                "Jóváhagyás szükséges":s.state}</td>
            </tr>)}</tbody></table>
          </div>}
      </section>}
      <div className="ops-disclaimer"><WifiOff size={17}/>
        A CPU/RAM és PID kizárólag pontos helyi Windows-azonosság esetén jelenik meg.
        A 0 vagy ismeretlen értékekből nem számolunk becsült adatot.
        A Store-v1 utazás és SaveComplete továbbra is külön igazolást igényel.</div>
    </>}
  </section>;
}
