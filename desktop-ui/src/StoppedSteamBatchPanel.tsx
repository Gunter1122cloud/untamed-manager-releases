import {useEffect,useMemo,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Shield} from "lucide-react";
import {managerBridge, type PublicMap,
  type StoppedSteamBatchJob, type StoppedSteamBatchCurrent} from "./bridge";

const pageSize=25;

/**
 * Local host only. A selection may span arbitrarily many registered maps
 * across pages, but each explicit native batch admits at most 25 map IDs.
 * No running or uncertain ASA server may be stopped from this UI.
 * Original Store-v1 maps require a separate native stopped-family check.
 */
export function StoppedSteamBatchPanel({maps}:{maps:PublicMap[]}) {
  const [selected,setSelected]=useState<string[]>([]);
  const [useCache,setUseCache]=useState(false);
  const [query,setQuery]=useState("");
  const [page,setPage]=useState(0);
  const dispatching=useRef(false);
  const [job,setJob]=useState<StoppedSteamBatchJob|null>(null);
  const [jobId,setJobId]=useState("");
  const [current,setCurrent]=useState<StoppedSteamBatchCurrent|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const [reconnecting,setReconnecting]=useState(true);
  const [reconnectFailed,setReconnectFailed]=useState(false);
  const [reconnectNonce,setReconnectNonce]=useState(0);
  // Enrolled local Store maps are eligible for strict native preflight, not
  // for bypassing enrollment, transfer identity or stopped-owner proof.
  const candidates=useMemo(()=>maps.filter(m=>
    !m.observedReadOnlyImported&&
    !m.maintenance&&!m.running),
    [maps]);
  const filtered=useMemo(()=>candidates.filter(m=>
    !query.trim()||m.name.toLocaleLowerCase("hu-HU").includes(
      query.trim().toLocaleLowerCase("hu-HU"))||
    m.id.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase())
  ),[candidates,query]);
  const pages=Math.max(1,Math.ceil(filtered.length/pageSize));
  const safePage=Math.min(page,pages-1);
  const visible=filtered.slice(safePage*pageSize,(safePage+1)*pageSize);
  const running=job?.state==="running"||job?.state==="queued"||Boolean(jobId);

  useEffect(()=>{
    let active=true;
    setReconnecting(true);setReconnectFailed(false);
    managerBridge.steamBatchCurrent().then(value=>{
      if(!active)return;
      setCurrent(value);
      if(value.activeJob&&
         (value.activeJob.state==="queued"||value.activeJob.state==="running")){
        if(!value.activeJob.jobId)
          throw new Error("UM-STEAM-BATCH-CURRENT-MISSING-ID");
        setJob(value.activeJob);setJobId(value.activeJob.jobId);
      }
    }).catch(()=>{
      if(!active)return;
      setReconnectFailed(true);
      setError("A natív SteamCMD soros művelet állapota nem ellenőrizhető. Új frissítés nem indítható, amíg nem sikerül az újracsatlakozás.");
    }).finally(()=>{if(active)setReconnecting(false);});
    return ()=>{active=false;};
  },[reconnectNonce]);

  useEffect(()=>{
    if(!jobId)return;
    let active=true,inFlight=false;
    const poll=async()=>{
      if(!active||inFlight)return;
      inFlight=true;
      try{
        const state=await managerBridge.steamBatchStatus(jobId);
        if(!active)return;
        if(state.jobId!==jobId||state.serverStopRequested||
           state.remoteHostTouched||state.storeWriterTouched||
           state.automaticRetry)
          throw new Error("UM-STEAM-BATCH-STATUS-OTHER-JOB");
        setReconnectFailed(false);setError("");
        setJob(state);
        if(state.state==="completed"||state.state==="needsOperator"){
          setJobId("");
          managerBridge.steamBatchCurrent().then(value=>{
            if(active)setCurrent(value);
          }).catch(()=>{
            if(active)setError("A kész SteamCMD-feladat naplója nem olvasható.");
          });
        }
      }catch{
        if(active){
          // A lost status response is NOT evidence that SteamCMD stopped.
          // Keep the exact job ID, keep polling, never dispatch a second batch.
          setError("A natív SteamCMD-feladat állapota átmenetileg nem ellenőrizhető. Az elindított feladatot nem ismételjük meg.");
        }
      }finally{inFlight=false;}
    };
    void poll();
    const timer=window.setInterval(()=>{void poll();},3000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[jobId]);

  const invalidate=()=>{setNotice("");setError("");};
  const toggle=(mapId:string,checked:boolean)=>{
    invalidate();
    if(checked&&selected.length>=25){
      setError("Egy jóváhagyással legfeljebb 25 helyi map frissíthető. A további mapokat egy következő, külön műveletbe válaszd.");
      return;
    }
    setSelected(old=>checked?[...old.filter(x=>x!==mapId),mapId]:
      old.filter(x=>x!==mapId));
  };
  const start=async()=>{
    if(unavailable||blocked||dispatching.current||selected.length===0)return;
    const requested=[...selected];
    dispatching.current=true;setBusy(true);setError("");setNotice("");
    try{
      const started=await managerBridge.steamBatchStart(requested,useCache);
      if(!started.started||!started.jobId||started.serverStopRequested||
         started.remoteHostTouched||started.storeWriterTouched||started.automaticRetry)
        throw new Error("UM-STEAM-BATCH-START-RECEIPT-INVALID");
      setJob({jobId:started.jobId,state:"queued",stage:"preflight",
        position:0,total:requested.length,activeMapName:"",
        completedInstallations:0,changedUtc:new Date().toISOString(),
        serverStopRequested:false,remoteHostTouched:false,
        storeWriterTouched:false,automaticRetry:false});
      setJobId(started.jobId);
      setNotice("A helyi, soros SteamCMD-feladat elindult. A következő telepítés kizárólag az előző igazolt lezárása után indulhat.");
    }catch(e){
      // A one-shot approval might already have launched SteamCMD on the
      // Windows host when its response was interrupted. Reconcile first.
      setReconnectFailed(true);
      setError("A soros SteamCMD indítás eredménye nem igazolt ("+
        (e instanceof Error?e.message:"UM-STEAM-BATCH-FAILED")+
        "). Ne ismételd meg a jóváhagyást; ellenőrizd a natív feladatot.");
    }finally{dispatching.current=false;setBusy(false);}
  };
  const blocked=Boolean(current?.needsOperatorReview);
  const unavailable=busy||running||reconnecting||reconnectFailed;
  return <section className="glass panel cluster-registry-panel"
    aria-label="Helyi mapok soros SteamCMD frissítése">
    <div className="panel-head"><h2>Helyi mapok soros SteamCMD frissítése</h2></div>
    <div className="panel-inner">
      {reconnecting&&<p role="status" className="muted small">Natív SteamCMD-feladat ellenőrzése…</p>}
      {reconnectFailed&&<button type="button" className="secondary"
        onClick={()=>setReconnectNonce(n=>n+1)}>Soros SteamCMD állapotának újraellenőrzése</button>}
      <p className="muted small">Csak az ezen a Windows gépen regisztrált, bizonyítottan
        leállított ASA mapok, köztük az átvett eredeti Store-v1 mapok.
        Azonos ASA telepítés csak egyszer frissül,
        natív mentéssel és műveleti naplóval. Futó szervert sosem állít le;
        sikertelen vagy félbeszakadt műveletet nem ismétel automatikusan.</p>
      <label><input type="checkbox" checked={useCache} disabled={unavailable||blocked} onChange={e=>{setUseCache(e.target.checked);invalidate();}}/> Közös helyi SteamCMD build-cache használata</label>
      <p className="muted small">Első letöltés külön tiszta staging telepítésbe (cache missnél legalább 100 GiB szabad hely szükséges a Manager profilmeghajtóján). Az ellenőrzött publikus Windows buildből csak a fő ASA bináris és az alapcsomagok kerülnek át; minden célon SteamCMD validate fut. Teljes bináris rollback nincs igazolva.</p>
      <label>Helyi célmapok keresése
        <input aria-label="Soros SteamCMD célmap keresése" type="search"
          disabled={unavailable} value={query} placeholder="Mapnév vagy azonosító"
          onChange={e=>{setQuery(e.target.value);setPage(0);invalidate();}}/>
      </label>
      <div className="draft-actions">
        <button type="button" className="secondary" disabled={unavailable||visible.length===0}
          onClick={()=>{
            setSelected(old=>[...new Set([...old,...visible.map(m=>m.id)])].slice(0,25));
            invalidate();
          }}>Látható mapok kijelölése (max. 25)</button>
        <button type="button" className="quiet"
          disabled={unavailable||safePage===0}
          onClick={()=>setPage(p=>Math.max(0,p-1))}>Előző SteamCMD-oldal</button>
        <button type="button" className="quiet"
          disabled={unavailable||safePage>=pages-1}
          onClick={()=>setPage(p=>Math.min(pages-1,p+1))}>Következő SteamCMD-oldal</button>
        <span className="muted small">{safePage+1}. / {pages} oldal ·
          {filtered.length} helyi jelölt · {selected.length} kijelölt</span>
      </div>
      <div className="premium-mod-copy-targets" role="group"
        aria-label="Soros SteamCMD célmapok">
        {visible.length===0?<p className="muted small">Nincs a keresésnek megfelelő, leállított helyi map.</p>:
        visible.map(map=><label className="plugin-row" key={map.id}>
          <input type="checkbox" checked={selected.includes(map.id)}
            disabled={unavailable}
            onChange={e=>toggle(map.id,e.target.checked)}/>
          <span>{map.name}</span><small>{map.steamBuildId??"ismeretlen build"}</small>
        </label>)}
      </div>
      <p className="muted small">Kijelölt mapok: {maps.filter(m=>selected.includes(m.id)).map(m=>m.name).join(", ")||"nincs"}. Közös telepítési gyökér esetén minden hozzá tartozó mapot jelölj ki; a natív ellenőrzés nem bővíti automatikusan a kiválasztást.</p>
      <button type="button" className="primary"
        disabled={unavailable||blocked||selected.length===0}
        onClick={()=>{void start();}}>
        <RefreshCw size={16}/> {selected.length} kijelölt map frissítése
      </button>
      {job&&<div className="historical-preview" role="status">
        <b>SteamCMD: {job.state}</b>
        <p>{job.position}. / {job.total} · {job.activeMapName||"Előkészítés"} ·
          {job.stage}{job.errorCode&&" · "+job.errorCode}</p>
        {job.results?.map(result=><p key={result.mapId}>{result.mapName}: {result.oldBuild} → {result.newBuild} · {result.protectedFilesIntact?"védett fájlok ellenőrizve":"nem igazolt"}</p>)}
        {job.state==="completed"&&<p><CheckCircle2 size={16}/> Igazoltan frissített
          telepítések: {job.completedInstallations}</p>}
        {job.state==="needsOperator"&&<p className="error-inline">
          <AlertTriangle size={16}/> A soros művelet megállt. Ellenőrizd a natív
          naplót; automatikus ismétlés nincs.</p>}
      </div>}
      {blocked&&<div className="warning-inline"><Shield size={16}/>
        Félbeszakadt SteamCMD műveleti napló: csak külön kézi helyreállítás után
        indítható új feladat.</div>}
      {notice&&<div className="success-inline" role="status">{notice}</div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={16}/>{error}</div>}
      <div className="warning-inline"><Shield size={16}/>
        A távoli gazdagépek függetlenek: ezen a gépen csak helyi ASA telepítések
        módosíthatók. A Store-v1 mapokhoz ugyanaz a leállított állapot,
        natív Windows folyamatazonosítás és kézi jóváhagyás szükséges.</div>
    </div>
  </section>;
}
