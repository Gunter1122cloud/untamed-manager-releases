import {useCallback,useEffect,useMemo,useState} from "react";
import {Activity,AlertTriangle,CalendarClock,LockKeyhole,RefreshCw,Server,Shield,Terminal,WifiOff} from "lucide-react";
import {managerBridge,type OperationsMap,type OperationsSnapshot,
  type NativeAsaScheduleOverview,type ReadOnlyScheduleKind} from "./bridge";
import "./operations-fidelity.css";
import {LocalMapLifecycleBar} from "./LocalMapLifecycleBar";

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
function OperationCard({map,onOpen,onRefresh}:{
  map:OperationsMap;onOpen:(id:string)=>void;onRefresh:()=>Promise<void>
}){
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
    <LocalMapLifecycleBar mapId={map.mapId} mapName={map.mapName}
      running={map.running} historical={map.storeProtected}
      onRefresh={onRefresh} onConfigure={()=>onOpen(map.mapId)}/>
    <div className="ops-card-foot">
      <span><Shield size={14}/> Csak helyi ellenőrzés</span>
      <button type="button" className="secondary" onClick={()=>onOpen(map.mapId)}>
        <Terminal size={15}/> Map megnyitása
      </button>
    </div>
  </article>;
}

export function OperationsCenter({onOpenMap,selectedMapId,showScheduler=false}:{onOpenMap:(id:string)=>void;selectedMapId?:string;showScheduler?:boolean}){
  const [snapshot,setSnapshot]=useState<OperationsSnapshot|null>(null);
  const [nativeOverview,setNativeOverview]=useState<NativeAsaScheduleOverview|null>(null);
  const [nativeUnreadable,setNativeUnreadable]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [filter,setFilter]=useState<"all"|"running"|"attention">("all");
  const [pageIndex,setPageIndex]=useState(0);
  const pageSize=50;
  const [scheduleMap,setScheduleMap]=useState("");
  useEffect(()=>{if(selectedMapId)setScheduleMap(selectedMapId);},[selectedMapId]);
  const [scheduleKind,setScheduleKind]=useState<ReadOnlyScheduleKind>("Observation");
  const [scheduleMinutes,setScheduleMinutes]=useState(30);
  const [scheduleApproved,setScheduleApproved]=useState(false);
  const [scheduleBusy,setScheduleBusy]=useState(false);
  const [scheduleNotice,setScheduleNotice]=useState("");
  const reload=useCallback(async()=>{
    setBusy(true);setError("");
    try{
      // Native schedules come from the existing Windows C# runner, not the
      // separate read-only inspection timetable. One bulk RPC for N maps.
      const [operations,native]=await Promise.allSettled([
        selectedMapId?managerBridge.operationsMap(selectedMapId):
          managerBridge.operationsSnapshotAll(),
        selectedMapId?Promise.resolve(null):managerBridge.nativeScheduleOverview()
      ]);
      if(operations.status!=="fulfilled")throw new Error("UM-OPS-UNAVAILABLE");
      setSnapshot(operations.value);
      setNativeOverview(native.status==="fulfilled"?native.value:null);
      setNativeUnreadable(!selectedMapId&&native.status!=="fulfilled");
    }catch(e){
      setError("A natív üzemeltetési állapot nem ellenőrizhető ("+
        (e instanceof Error?e.message:"UM-OPS-UNAVAILABLE")+").");
      setSnapshot(null);setNativeOverview(null);
    }finally{setBusy(false);}
  },[selectedMapId]);
  useEffect(()=>{void reload();},[reload]);
  const addReadOnlySchedule=async()=>{
    setScheduleBusy(true);setScheduleNotice("");
    try{
      if(!scheduleApproved||!scheduleMap||(selectedMapId&&scheduleMap!==selectedMapId)||scheduleMinutes<5||
         scheduleMinutes>10080||!Number.isInteger(scheduleMinutes))
        throw new Error("UM-OPS-INVALID-READONLY-SCHEDULE");
      const saved=await managerBridge.operationsScheduleCreate(
        scheduleMap,scheduleKind,scheduleMinutes);
      setScheduleNotice(saved.enabled?
        "Mentve: a helyi, csak olvasó ellenőrzés automatikusan naplózódik.":"Nem menthető");
      setScheduleApproved(false);
      await reload();
    }catch(e){
      setScheduleNotice("Az ellenőrzés beállítása nem sikerült: "+
        (e instanceof Error?e.message:"UM-OPS-UNAVAILABLE"));
    }finally{setScheduleBusy(false);}
  };
  const disableReadOnlySchedule=async(jobId:string)=>{
    setScheduleBusy(true);setScheduleNotice("");
    try{
      if(!scheduleApproved)throw new Error("UM-OPS-CONFIRMATION-REQUIRED");
      await managerBridge.operationsScheduleDisable(jobId);
      setScheduleNotice("Az automatikus helyi ellenőrzés kikapcsolva.");
      setScheduleApproved(false);
      await reload();
    }catch(e){
      setScheduleNotice("A kikapcsolás nem sikerült: "+
        (e instanceof Error?e.message:"UM-OPS-UNAVAILABLE"));
    }finally{setScheduleBusy(false);}
  };
  const visible=useMemo(()=>(snapshot?.maps??[]).filter(x=>
    filter==="all"||(filter==="running"?x.running:
    x.maintenance||x.storeProtected||(!x.running&&!x.confirmedStopped))
  ),[snapshot,filter]);
  const pageCount=Math.max(1,Math.ceil(visible.length/pageSize));
  const safePage=Math.min(pageIndex,pageCount-1);
  const displayed=useMemo(()=>visible.slice(safePage*pageSize,
    (safePage+1)*pageSize),[visible,safePage]);
  const mapSchedules=useMemo(()=>(snapshot?.schedules??[]).filter(x=>
    !selectedMapId||x.mapId===selectedMapId),[snapshot,selectedMapId]);
  const mapReceipts=useMemo(()=>(snapshot?.scheduleReceipts??[]).filter(x=>
    !selectedMapId||x.map===selectedMapId),[snapshot,selectedMapId]);
  return <section className="ops-center">
    {!selectedMapId&&<div className="page-intro">
      <span className="eyebrow">NATIVE / HELYI ÜZEMELTETÉS</span><h1>Üzemeltetés</h1>
      <p>Valódi helyi PID, ASA memória és a maponkénti natív SaveWorld/Restart következő időpontjai. A távoli gépek itt nem vezérelhetők.</p>
    </div>}
    <div className="ops-toolbar">
      <div className="ops-observed"><Activity size={17}/>
        {snapshot?snapshot.hostName+" · mérés: "+localDate(snapshot.observedUtc):
          "Windows-állapot lekérése"}</div>
      {!selectedMapId&&<div className="ops-filters" role="group" aria-label="Üzemeltetési szűrő">
        {([["all","Összes"],["running","Futók"],["attention","Védelem / vizsgálat"]] as const)
          .map(([key,label])=><button type="button" key={key}
            className={filter===key?"is-active":""} aria-pressed={filter===key}
            onClick={()=>{setFilter(key);setPageIndex(0);}}>{label}</button>)}
      </div>}
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>{void reload();}}><RefreshCw size={16} className={busy?"spin":""}/>
        Frissítés</button>
    </div>
    {error&&<div role="alert" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
    {nativeUnreadable&&<div role="status" className="warning-inline">
      <AlertTriangle size={16}/> A natív ASA ütemező összesítése nem olvasható. A folyamatállapot külön továbbra is ellenőrizhető.
    </div>}
    {!snapshot&&!error&&<div role="status" className="empty">Helyi Windows-folyamatok ellenőrzése...</div>}
    {snapshot&&<>
      {!selectedMapId&&<div className="ops-summary">
        <div><Server size={20}/><strong>{snapshot.maps.length}</strong><span>Helyi map</span></div>
        <div><Activity size={20}/><strong>{snapshot.maps.filter(x=>x.running).length}</strong><span>Igazoltan fut</span></div>
        <div><Shield size={20}/><strong>{snapshot.maps.filter(x=>x.storeProtected).length}</strong><span>Store-v1 védett</span></div>
        <div><CalendarClock size={20}/><strong>{nativeOverview?
          nativeOverview.jobs.filter(x=>x.enabled).length:"—"}</strong><span>Natív ASA feladat</span></div>
      </div>}
      {(!selectedMapId||!showScheduler)&&(visible.length===0?<div className="empty">A szűrőnek megfelelő helyi map nem található.</div>:
        selectedMapId?<div className="ops-grid">
          {visible.map(map=><OperationCard key={map.mapId} map={map}
              onOpen={onOpenMap} onRefresh={reload}/>)}
        </div>:<div className="ops-compact-scroll">
          <table className="ops-compact-table" aria-label="Helyi mapok összesített állapota">
            <thead><tr><th>Map</th><th>Állapot</th><th>PID</th><th>ASA RAM</th>
              <th>Következő ASA-feladat</th><th>Részletek</th></tr></thead>
            <tbody>{displayed.map(map=>{
              const next=nativeOverview?.jobs.filter(s=>s.mapId===map.mapId&&s.enabled)
                .sort((a,b)=>Date.parse(a.nextDueUtc)-Date.parse(b.nextDueUtc))[0];
              return <tr key={map.mapId}>
                <td><strong>{map.mapName}</strong></td>
                <td><span className={"ops-state "+(map.running?"running":map.confirmedStopped?"stopped":"unknown")}>
                  {map.running?"Fut":map.confirmedStopped?"Leállítva":"Nem igazolt"}</span></td>
                <td>{map.pid??"Nem mérhető"}</td>
                <td>{map.memoryVerified&&map.memoryMiB!==null?
                  map.memoryMiB.toLocaleString("hu-HU")+" MiB":"Nem mérhető"}</td>
                <td>{next?<><strong>{next.kind==="SaveWorld"?"SaveWorld":"Újraindítás"}</strong>
                  <span> · {localDate(next.nextDueUtc)}</span></>:
                  nativeOverview?"Nincs beállítva":"Nem ellenőrizhető"}</td>
                <td><button type="button" className="secondary" onClick={()=>onOpenMap(map.mapId)}>
                  <Terminal size={15}/> Map megnyitása</button></td>
              </tr>;
            })}</tbody>
          </table>
          {pageCount>1&&<div className="ops-page-nav" role="group"
            aria-label="Helyi mapok lapozása">
            <button type="button" className="secondary" disabled={safePage===0}
              onClick={()=>setPageIndex(p=>Math.max(0,p-1))}>Előző mapoldal</button>
            <span>{safePage*pageSize+1}–{Math.min((safePage+1)*pageSize,visible.length)}
              / {visible.length} helyi map · {safePage+1}/{pageCount}. oldal</span>
            <button type="button" className="secondary"
              disabled={safePage>=pageCount-1}
              onClick={()=>setPageIndex(p=>Math.min(pageCount-1,p+1))}>
              Következő mapoldal</button>
          </div>}
        </div>)}
      {selectedMapId&&showScheduler&&<section className="premium-panel ops-schedules">
        <header className="premium-panel-heading"><h2><CalendarClock size={19}/> Meglévő ütemezések</h2>
          <span className="premium-small-muted">Core / Budapest időzóna</span></header>
        <p>Itt kizárólag a kiválasztott map automatikus, csak olvasó ellenőrzéseit állíthatod. A SaveWorld és az automatikus újraindítás külön natív végrehajtást igényel, ezért ezeket ez a panel még nem kínálja. A kimaradt ellenőrzések nem futnak újra.</p>
        <p className="premium-small-muted" role="status">
          Helyi ellenőrző motor: {snapshot.supervisorState==="active"?
            "AKTÍV":snapshot.supervisorState==="journal-unavailable"?
              "Napló nem olvasható":snapshot.supervisorState==="journal-or-inspection-error"?
                "Ellenőrzési hiba – manuális vizsgálat":snapshot.supervisorState??"Ismeretlen"}
        </p>
        <div className="ops-schedule-editor">
          <div className="ops-scheduler-map"><span>Kiválasztott helyi map</span>
            <strong>{snapshot.maps.find(m=>m.mapId===selectedMapId)?.mapName??"Nem található"}</strong>
          </div>
          <label>Ellenőrzés
            <select aria-label="Csak olvasó ellenőrzés típusa"
              value={scheduleKind} disabled={scheduleBusy}
              onChange={e=>setScheduleKind(e.target.value as ReadOnlyScheduleKind)}>
              <option value="Observation">Folyamat / PID megfigyelése</option>
              <option value="ModCheck">Konfigurált modok ellenőrzése</option>
              <option value="ApiCheck">ASA API helyi fájlellenőrzés</option>
              <option value="SteamCheck">ASA Steam-verzió (telepített + publikus)</option>
            </select>
          </label>
          <label>Ismétlés (perc)
            <input type="number" aria-label="Ellenőrzési időköz percben"
              min={5} max={10080} step={1} value={scheduleMinutes}
              disabled={scheduleBusy}
              onChange={e=>setScheduleMinutes(Number(e.target.value))}/>
          </label>
          <label className="ops-schedule-approval">
            <input type="checkbox" checked={scheduleApproved}
              disabled={scheduleBusy}
              onChange={e=>setScheduleApproved(e.target.checked)}/>
            Jóváhagyom a helyi, csak olvasó ütemezés módosítását.
          </label>
          <button type="button" className="secondary"
            disabled={!scheduleApproved||!scheduleMap||scheduleMap!==selectedMapId||scheduleBusy||
              !Number.isInteger(scheduleMinutes)||scheduleMinutes<5||
              scheduleMinutes>10080}
            onClick={()=>{void addReadOnlySchedule();}}>
            <CalendarClock size={15}/> Ellenőrzés bekapcsolása
          </button>
        </div>
        {scheduleNotice&&<p role="status">{scheduleNotice}</p>}
        {mapSchedules.length===0?<p className="ops-empty-schedule">
          Ezen a mapon még nincs mentett, csak olvasó ellenőrzés.</p>:
          <div className="ops-schedule-scroll">
            <table className="ops-schedule-table"><thead><tr>
              <th>Map</th><th>Művelet</th><th>Ismétlés</th><th>Következő időpont</th><th>Állapot</th>
            </tr></thead><tbody>{mapSchedules.map(s=><tr key={s.id}>
              <td>{s.mapName}</td><td>{s.kind}</td><td>{s.frequency}</td>
              <td>{localDate(s.nextUtc)}</td>
              <td>{!s.enabled?"Kikapcsolva":s.state==="read-only-preview"?
                "Csak olvasó ütemezés":s.state==="operator-approval-required"?
                "Jóváhagyás szükséges":s.state}
                {s.enabled&&["Observation","ModCheck","ApiCheck","SteamCheck"].includes(s.kind)&&
                  <button type="button" className="secondary"
                    disabled={!scheduleApproved||scheduleBusy}
                    onClick={()=>{void disableReadOnlySchedule(s.id);}}>
                    Kikapcsolás
                  </button>}
              </td>
            </tr>)}</tbody></table>
          </div>}
        <h3>Végrehajtási napló (legutóbbi 40 esemény)</h3>
        {mapReceipts.length===0?
          <p className="ops-empty-schedule">Még nincs végrehajtott helyi ellenőrzés. Az új ütemezés az első alkalommal csak a következő időpontot rögzíti.</p>:
          <div className="ops-schedule-scroll">
            <table className="ops-schedule-table"><thead><tr>
              <th>Map</th><th>Esedékesség</th><th>Állapot</th><th>Bizonyíték</th>
            </tr></thead><tbody>{mapReceipts.slice().reverse()
              .map((r,i)=><tr key={r.job+"-"+r.slotUtc+"-"+i}>
                <td>{snapshot.maps.find(m=>m.mapId===r.map)?.mapName??"Ismeretlen map"}</td>
                <td>{localDate(r.slotUtc)}</td>
                <td>{r.status==="succeeded"?"Olvasás végrehajtva":
                  r.status==="failed-not-retried"?"Ellenőrzés sikertelen – nincs ismétlés":
                  r.status==="claimed"?"Esedékesség lefoglalva":r.status}</td>
                <td>{r.code}</td>
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
