import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,HardDrive,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type PublicMap,type StoppedSteamStatus,type DurableSteamQueue,type StoppedSteamDiagnostics,type SteamPublishedBuild} from "./bridge";
import {ScheduledStoppedSteamSettings} from "./ScheduledStoppedSteamSettings";

const steamReason=(code?:string|null)=>code==="UM-STEAMCMD-NOT-CONFIGURED"?"Nincs SteamCMD beállítva. Válaszd ki a steamcmd.exe fájlt.":
  code==="UM-STEAMCMD-NOT-FOUND"?"A beállított steamcmd.exe nem található.":
  code==="UM-STEAMCMD-INVALID"?"Érvénytelen vagy nem olvasható helyi SteamCMD-programútvonal.":
  code==="UM-STEAMCMD-PREPARE-FAILED"?"A hivatalos SteamCMD előkészítése sikertelen. Ellenőrizd a kapcsolatot és a helyi Tools könyvtárat, vagy válassz meglevő steamcmd.exe fájlt.":
  code==="UM-STEAM-ASA-BINARY-INVALID"?"A kiválasztott map ASA szerverbinárisa nem igazolható.":null;

export function StoppedSteamUpdate({maps,fixedMapId}:{maps:PublicMap[];fixedMapId?:string}) {
  const [selected,setSelected]=useState("");
  const [diagnostics,setDiagnostics]=useState<StoppedSteamDiagnostics|null>(null);
  const [published,setPublished]=useState<SteamPublishedBuild|null>(null);
  const [job,setJob]=useState<StoppedSteamStatus|null>(null);
  const [jobId,setJobId]=useState<string|null>(null);
  const [journal,setJournal]=useState<DurableSteamQueue|null>(null);
  const dispatching=useRef(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [steamNotice,setSteamNotice]=useState("");
  const [reconnecting,setReconnecting]=useState(true);
  const [reconnectFailed,setReconnectFailed]=useState(false);
  const [reconnectNonce,setReconnectNonce]=useState(0);
  const [otherMapUpdating,setOtherMapUpdating]=useState(false);
  const mapRef=useRef("");
  const mapEpoch=useRef(0);
  // A map page locks selection to its own locally registered map. Never
  // silently fall back to a different map if the ID disappeared.
  const mapId=fixedMapId!==undefined
    ?(maps.some(m=>m.id===fixedMapId)?fixedMapId:"")
    :(maps.some(m=>m.id===selected)?selected:(maps[0]?.id??""));

  mapRef.current=mapId;
  const belongs=(requested:string,epoch:number)=>
    mapRef.current===requested&&mapEpoch.current===epoch;
  useEffect(()=>{
    ++mapEpoch.current;
    setDiagnostics(null);setPublished(null);
    setJobId(null);setJob(null);setOtherMapUpdating(false);setBusy(false);
  },[mapId]);
  useEffect(()=>{
    let active=true;
    setReconnecting(true);setReconnectFailed(false);
    setOtherMapUpdating(false);
    managerBridge.steamStoppedCurrent()
      .then(value=>{
        if(!active)return;
        if(value.autoResumeAllowed)
          throw new Error("UM-STEAM-AUTOMATIC-RESUME-UNEXPECTED");
        setJournal(value);
        // Reattach read-only to the ONE previously accepted native job.
        // WebView2 remount is not a new SteamCMD approval.
        if(value.activeJob&&
           (value.activeJob.state==="queued"||value.activeJob.state==="running")){
          if(!value.activeJob.jobId||!value.activeJob.mapId||
             value.activeJob.restartPerformed||value.activeJob.storeTouched)
            throw new Error("UM-STEAM-CURRENT-INVALID-JOB");
          if(value.activeJob.mapId.toLowerCase()!==mapId.toLowerCase()){
            setOtherMapUpdating(true);
          }else{
            setJob(value.activeJob);setJobId(value.activeJob.jobId);
          }
        }
      })
      .catch(()=>{
        if(active){
          setReconnectFailed(true);
          setError("A futó kézi SteamCMD művelet állapota nem olvasható. Új frissítés előtt ellenőrizd a natív naplót.");
        }
      }).finally(()=>{if(active)setReconnecting(false);});
    return ()=>{active=false;};
  },[reconnectNonce,mapId]);
  // A global host-level SteamCMD job for another map blocks this map
  // temporarily, not forever. Poll only native read-only current-status;
  // never issue a second SteamCMD apply, and fail closed on lost evidence.
  useEffect(()=>{
    if(!otherMapUpdating)return;
    let active=true,inFlight=false;
    const poll=async()=>{
      if(!active||inFlight)return;
      inFlight=true;
      try{
        const value=await managerBridge.steamStoppedCurrent();
        if(!active)return;
        if(value.autoResumeAllowed)
          throw new Error("UM-STEAM-AUTO-RESUME-FORBIDDEN");
        setJournal(value);
        const current=value.activeJob;
        if(!current||current.state==="completed"||current.state==="needsOperator"){
          setOtherMapUpdating(false);
        }else if(!current.mapId||!current.jobId||
                 current.restartPerformed||current.storeTouched){
          throw new Error("UM-STEAM-FOREIGN-JOB-INVALID");
        }else if(current.mapId.toLowerCase()===mapId.toLowerCase()){
          setOtherMapUpdating(false);
          setJob(current);setJobId(current.jobId);
        }
      }catch{
        if(active){
          setReconnectFailed(true);
          setError("A másik helyi map SteamCMD-művelete nem ellenőrizhető. Új frissítés nem indítható.");
        }
      }finally{inFlight=false;}
    };
    const timer=window.setInterval(()=>{void poll();},2500);
    return ()=>{active=false;window.clearInterval(timer);};
  },[otherMapUpdating,mapId]);

  useEffect(()=>{
    if(!jobId)return;
    let active=true,inFlight=false;
    const check=async()=>{
      if(!active||inFlight)return;
      inFlight=true;
      try{
        const status=await managerBridge.steamStoppedStatus(jobId);
        if(!active)return;
        if(status.jobId!==jobId||
           status.mapId.toLowerCase()!==mapId.toLowerCase()||
           status.restartPerformed||status.storeTouched||
           (status.state==="completed"&&!status.protectedFilesIntact))
          throw new Error("UM-STEAM-STATUS-RECEIPT-INVALID");
        setError("");setJob(status);
        if(status.state==="completed"||status.state==="needsOperator"){
          try{
            const value=await managerBridge.steamStoppedCurrent();
            if(active)setJournal(value);
            const report=await managerBridge.steamStoppedDiagnostics(mapId);
            if(active&&report.mapId.toLowerCase()===mapId.toLowerCase())setDiagnostics(report);
          }catch{if(active)setError("A frissítés eredménye megmaradt, de az állapot újraolvasása sikertelen.");}
          finally{if(active)setJobId(null);}
        }
      }catch{
        if(active){
          // A missing response does not mean the native SteamCMD child
          // finished. Retain the job ID and poll again; no second update.
          setError("A helyi SteamCMD feladat állapota átmenetileg nem ellenőrizhető. Az elindított frissítést nem ismételjük meg.");
        }
      }finally{inFlight=false;}
    };
    void check();
    const timer=window.setInterval(()=>{void check();},2500);
    return ()=>{active=false;window.clearInterval(timer);};
  },[jobId,mapId]);

  const blocked=busy||!!jobId||otherMapUpdating||reconnecting||reconnectFailed||
    Boolean(journal?.needsOperatorReview);
  const selectSteam=async(prepare=false)=>{
    if(!mapId||blocked)return;
    const requested=mapId,epoch=mapEpoch.current;
    setBusy(true);setError("");setSteamNotice("");
    try{
      const result=await (prepare?managerBridge.steamPrepareExecutable():managerBridge.steamSelectExecutable());
      if(!belongs(requested,epoch))return;
      if(result.updateStarted!==false||result.serverStopped!==false||(!result.cancelled&&!result.configured))
        throw new Error("UM-STEAMCMD-SETTINGS-RECEIPT-INVALID");
      if(result.cancelled)return;
      setDiagnostics(null);setPublished(null);
      setSteamNotice("SteamCMD elérési útja mentve a helyi Manager profiljába. ASA-frissítés nem indult. Ellenőrizd újra a verziót és a frissítési előfeltételeket.");
    }catch(e){if(belongs(requested,epoch))setError(steamReason(e instanceof Error?e.message:"")??"A SteamCMD beállítása nem igazolható.");}
    finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const checkPublished=async()=>{
    if(!mapId||blocked)return;
    const requested=mapId,epoch=mapEpoch.current;
    setBusy(true);setPublished(null);setError("");
    try{
      const proof=await managerBridge.steamPublishedBuild(requested);
      if(!belongs(requested,epoch))return;
      if(proof.mapId.toLowerCase()!==requested.toLowerCase()||
         proof.downloadStarted||proof.serverStopped||proof.storeWriterTouched||
         proof.remoteHostTouched)
        throw new Error("UM-STEAM-PUBLISHED-MAP-MISMATCH");
      setPublished(proof);
    }catch(e){
      if(belongs(requested,epoch))
        setError((steamReason(e instanceof Error?e.message:"")??("A Steam ASA buildje nem ellenőrizhető ("+
          (e instanceof Error?e.message:"UM-STEAM-PUBLISHED-UNAVAILABLE")+")."))+
          " Nem indítottunk frissítést.");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const check=async()=>{
    if(!mapId||blocked)return;
    const requested=mapId,epoch=mapEpoch.current;
    setBusy(true);setError("");
    try{
      const report=await managerBridge.steamStoppedDiagnostics(requested);
      if(!belongs(requested,epoch))return;
      if(report.mapId.toLowerCase()!==requested.toLowerCase()||
         report.noProcessAutoStop!==true||report.remoteWrites!==false)
        throw new Error("UM-STEAM-DIAGNOSTICS-OTHER-MAP");
      setDiagnostics(report);
      if(!report.canPreview){
        setError(steamReason(report.steamCmdError)??(report.asaBinaryRegistered===false?steamReason("UM-STEAM-ASA-BINARY-INVALID")!:"A frissítési előfeltételek nem teljesülnek. A diagnosztika jelzi a blokkoló mapot, SteamCMD-t, tárhelyet vagy naplót."));
        return;
      }

    }catch(e){
      if(belongs(requested,epoch))
        setError("A kézi SteamCMD előellenőrzés nem igazolható ("+
          (e instanceof Error?e.message:"nem olvasható")+").");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const update=async()=>{
    if(!mapId||blocked||dispatching.current||!diagnostics?.canPreview||
       diagnostics.mapId.toLowerCase()!==mapId.toLowerCase())return;
    const requested=mapId,epoch=mapEpoch.current;
    dispatching.current=true;setBusy(true);setError("");
    try{
      const started=await managerBridge.steamStoppedStart(requested,diagnostics.processes.map(p=>p.mapName));
      if(!belongs(requested,epoch))return;
      if(!started.started||!started.jobId||started.restartPerformed||
         started.remoteHostTouched)
        throw new Error("UM-STEAM-START-RECEIPT-INVALID");
      setJob({
        jobId:started.jobId,mapId:requested,state:"queued",stage:"preflight",
        changedUtc:new Date().toISOString(),oldBuild:"",newBuild:"",
        protectedFilesIntact:false,restartPerformed:false,storeTouched:false
      });
      setJobId(started.jobId);
    }catch{
      if(belongs(requested,epoch)){
        setReconnectFailed(true);
        setError("A SteamCMD frissítés indításának eredménye nem igazolható. A natív frissítő már elindulhatott; ne ismételd meg. Ellenőrizd a feladatot.");
      }
    }finally{dispatching.current=false;if(belongs(requested,epoch))setBusy(false);}
  };

  return <section className="premium-panel" aria-label="Leállított map SteamCMD-frissítése" style={{padding:20,marginBlock:18}}>
    <header className="premium-panel-heading">
      <h2><HardDrive size={19}/> Valódi SteamCMD-frissítés – leállított mapok</h2>
    </header>
    <div className="map-overview-actions">
      <button type="button" className="secondary" disabled={!mapId||blocked} onClick={()=>{void selectSteam(true);}}>SteamCMD előkészítése (Valve)</button>
      <button type="button" className="secondary" disabled={!mapId||blocked} onClick={()=>{void selectSteam();}}>SteamCMD program kiválasztása</button>
    </div>
    <p className="muted small">Csak a helyi SteamCMD eszközt készíti elő, nem telepít és nem frissít ASA mapot.</p>
    {steamNotice&&<p role="status" className="success-inline">{steamNotice}</p>}
    {reconnecting&&<p role="status" className="muted small">Korábbi kézi SteamCMD-feladat ellenőrzése…</p>}
    {otherMapUpdating&&<p role="status" className="warning-inline">
      Másik helyi map SteamCMD-frissítése folyamatban van. A saját map frissítése a másik feladat befejezéséig nem indítható.</p>}
    {reconnectFailed&&<button type="button" className="secondary"
      onClick={()=>setReconnectNonce(n=>n+1)}>SteamCMD művelet újraellenőrzése</button>}
    <p>A meglévő C# Core készít védőmentést, ellenőrzi a fájlokat és gépenként sorban frissít. A másik gazdagép teljesen független. Az előellenőrzés a helyi buildet és a biztonságos frissítés feltételeit ellenőrzi; önmagában nem igazolja, hogy újabb Steam build jelent meg.</p>
    <div className="uk-rcon-admin-grid">
      {fixedMapId!==undefined
        ?<div className="map-info-line"><span>Helyi map</span><strong>{maps.find(m=>m.id===mapId)?.name??"Nem található"}</strong></div>
        :<label>Helyi map
          <select aria-label="SteamCMD frissítés map" value={mapId} disabled={busy||!!jobId||!mapId}
            onChange={e=>setSelected(e.target.value)}>
            {maps.map(map=><option key={map.id} value={map.id}>{map.name}</option>)}
          </select>
        </label>}
      <button type="button" className="secondary" disabled={!mapId||blocked}
        onClick={()=>{void checkPublished();}}><RefreshCw size={16}/> Elérhető ASA verzió ellenőrzése</button>
      <button type="button" className="secondary" disabled={!mapId||blocked}
        onClick={()=>{void check();}}><RefreshCw size={16}/> Helyi állapot és telepített build újraellenőrzése</button>
    </div>
    {published&&<div className="historical-preview" role="region"
      aria-label="ASA Steam build összehasonlítása">
      <b>{published.mapName} – {published.updateAvailable?
        "Új Steam build elérhető":published.state==="Current"?
          "A telepített build naprakész":"A publikált build nem ellenőrizhető"}</b>
      <p>Telepített ASA build: {published.installedBuild??"ismeretlen"}.
        Nyilvános Steam build: {published.availableBuild??"ismeretlen"}.</p>
      <p>{published.explanation}{published.diagnosticCode&&" · "+published.diagnosticCode}</p>
      <p className="muted small">Ez kizárólag a Steam 2430930 publikus buildadatait
        vizsgálja; nem állít le szervert, nem indít letöltést vagy újraindítást.
        Ismeretlen publikus build esetén a kézi frissítés külön, helyi előfeltételekkel indítható. Sikerét a telepített build és a védett fájlok ellenőrzése igazolja.</p>
    </div>}
    {journal?.exists&&<div role="status" className={journal.needsOperatorReview?"warning-inline":"uk-rcon-receipt"}>
      <Shield size={18}/>
      <span>Helyi SteamCMD-napló: {journal.phase}
        {journal.activeMapName&&" · "+journal.activeMapName}
        {journal.total!==undefined&&" · "+(journal.nextIndex??0)+"/"+journal.total}
        {journal.needsOperatorReview&&" · korábban félbeszakadt művelet: kézi ellenőrzés szükséges, az automatikus újrakezdés tiltva"}
      </span>
    </div>}
    {diagnostics&&<div role="region" aria-label="SteamCMD natív előfeltétel diagnosztika"
      className="historical-preview">
      <b>{diagnostics.mapName} · {diagnostics.canPreview?"Előellenőrzés engedélyezhető":"Blokkolt frissítés"}</b>
      <p>Telepítés: {diagnostics.installRoot}</p>
      <p>Helyi telepített build: {diagnostics.installedBuild??"ismeretlen"}. A publikus build státusza ettől független.</p>
      <p>Szabad tárhely: {Math.floor(diagnostics.availableBytes/1073741824)} GiB;
        szükséges tartalék: {Math.ceil(diagnostics.requiredBytes/1073741824)} GiB.</p>
      <p>SteamCMD: {diagnostics.steamCmdFound?"Helyi elérési út ellenőrizve":steamReason(diagnostics.steamCmdError)??(!diagnostics.steamCmdConfigured?"Nincs konfigurálva":"Hiányzik vagy az elérési út érvénytelen")}.
        Napló: {diagnostics.journalBlocked?"kézi ellenőrzés szükséges":"nem blokkol"}.
        Store-v1/karbantartás: {diagnostics.historicalBlocked?"védett":"nem blokkol"}.
        Közös telepítés tulajdonjoga: {diagnostics.safeInstallation?
          "ellenőrzött, a helyi, leállított telepítés mapjai":
          "nem igazolható – a közös telepítés vagy a Store-védelem blokkol"}.</p>
      {diagnostics.processes.map(p=><p key={p.mapName}>
        {p.mapName}: {p.confirmedStopped&&!p.running?"Leállítás igazolva":"NEM igazoltan leállítva"}
        {p.pid!=null?" · ASA PID "+p.pid:""}
        {p.loaderPid!=null?" · Loader PID "+p.loaderPid:""}
        {" · "+p.reason}
      </p>)}
      {diagnostics.overlaps.map(p=><p key={p.mapName} role="alert">
        Ütköző map: {p.mapName} · {p.installRoot}
      </p>)}
    </div>}
    <button type="button" className="primary" disabled={blocked||!diagnostics?.canPreview||diagnostics.mapId.toLowerCase()!==mapId.toLowerCase()}
        onClick={()=>{void update();}}>{maps.find(m=>m.id===mapId)?.name??"Helyi map"} frissítése</button>
    <p className="muted small">Az érintett telepítés mapjai a fenti diagnosztikában láthatók. A gomb friss natív előellenőrzést és védőmentést indít, nem állít le vagy indít szervert.</p>
    {job&&<div className={job.state==="completed"?"uk-rcon-admin-success":"warning-inline"} role="status">
      {job.state==="completed"?<CheckCircle2 size={18}/>:<Shield size={18}/>}
      <span>Frissítés: {job.state} · {job.stage}{job.errorCode&&" · "+job.errorCode}
        {job.state==="completed"&&(" · Build "+job.oldBuild+" → "+job.newBuild+" · fájlellenőrzés sikeres")}
        {job.state==="needsOperator"&&" · ellenőrzés szükséges; nincs automatikus ismétlés"}
      </span>
    </div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={18}/>{error}</div>}
    <ScheduledStoppedSteamSettings mapId={mapId}/>
    <p className="muted tiny">A frissítő nem állít le futó ASA-t, nem indít újra mapot, nem ír a Store-v1-be és nem kezeli a másik gépet.</p>
  </section>;
}
