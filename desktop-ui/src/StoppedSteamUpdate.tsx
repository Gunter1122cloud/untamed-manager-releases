import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,HardDrive,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type PublicMap,type StoppedSteamPreview,type StoppedSteamStatus,type DurableSteamQueue,type StoppedSteamDiagnostics} from "./bridge";

export function StoppedSteamUpdate({maps,fixedMapId}:{maps:PublicMap[];fixedMapId?:string}) {
  const [selected,setSelected]=useState("");
  const [preview,setPreview]=useState<StoppedSteamPreview|null>(null);
  const [diagnostics,setDiagnostics]=useState<StoppedSteamDiagnostics|null>(null);
  const [job,setJob]=useState<StoppedSteamStatus|null>(null);
  const [jobId,setJobId]=useState<string|null>(null);
  const [journal,setJournal]=useState<DurableSteamQueue|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  // A map page locks selection to its own locally registered map. Never
  // silently fall back to a different map if the ID disappeared.
  const mapId=fixedMapId!==undefined
    ?(maps.some(m=>m.id===fixedMapId)?fixedMapId:"")
    :(maps.some(m=>m.id===selected)?selected:(maps[0]?.id??""));

  useEffect(()=>{setPreview(null);setDiagnostics(null);setApproved(false);},[mapId]);
  useEffect(()=>{
    let active=true;
    managerBridge.steamStoppedCurrent()
      .then(value=>{if(active)setJournal(value);})
      .catch(()=>{/* Older isolated UI fixtures may lack the journal endpoint. */});
    return ()=>{active=false;};
  },[]);
  useEffect(()=>{
    if(!jobId)return;
    let active=true;
    const check=async()=>{
      try{
        const status=await managerBridge.steamStoppedStatus(jobId);
        if(!active)return;
        setJob(status);
        if(status.state==="completed"||status.state==="needsOperator"){
          setJobId(null);
          managerBridge.steamStoppedCurrent().then(setJournal).catch(()=>{});
        }
      }catch{
        if(active){
          setError("A helyi frissítési folyamat státusza megszakadt. A SteamCMD újraindítása előtt ellenőrizd a natív naplót.");
          setJobId(null);
        }
      }
    };
    void check();
    const timer=window.setInterval(()=>{void check();},2500);
    return ()=>{active=false;window.clearInterval(timer);};
  },[jobId]);

  const check=async()=>{
    if(!mapId||busy||jobId||journal?.needsOperatorReview)return;
    setBusy(true);setPreview(null);setApproved(false);setError("");
    try{
      const report=await managerBridge.steamStoppedDiagnostics(mapId);
      setDiagnostics(report);
      if(!report.canPreview){
        setError("A frissítési előfeltételek nem teljesülnek. Az alábbi diagnosztika "+
          "pontosan jelzi a blokkoló mapot, SteamCMD-t, tárhelyet vagy naplót.");
        return;
      }
      setPreview(await managerBridge.steamStoppedPreview(mapId));
    }catch(e){setError("Az előellenőrzés nem sikerült ("+
      (e instanceof Error?e.message:"nem olvasható")+
      "). A natív SteamCMD/ASA napló további ellenőrzése szükséges.");}
    finally{setBusy(false);}
  };
  const update=async()=>{
    if(!preview||!approved||busy||jobId)return;
    const token=preview.token;
    setPreview(null);setApproved(false);setBusy(true);setError("");
    try{
      const started=await managerBridge.steamStoppedApply(token);
      setJob({
        jobId:started.jobId,state:"queued",stage:"preflight",
        changedUtc:new Date().toISOString(),oldBuild:"",newBuild:"",
        protectedFilesIntact:false,restartPerformed:false,storeTouched:false
      });
      setJobId(started.jobId);
    }catch{setError("A SteamCMD frissítés nem indult el. Ellenőrizd a map és a helyi folyamat állapotát.");}
    finally{setBusy(false);}
  };

  return <section className="premium-panel" aria-label="Leállított map SteamCMD-frissítése" style={{padding:20,marginBlock:18}}>
    <header className="premium-panel-heading">
      <h2><HardDrive size={19}/> Valódi SteamCMD-frissítés – leállított mapok</h2>
    </header>
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
      <button type="button" className="secondary" disabled={!mapId||busy||!!jobId||Boolean(journal?.needsOperatorReview)}
        onClick={()=>{void check();}}><RefreshCw size={16}/> Frissítés előellenőrzése</button>
    </div>
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
      <p>Szabad tárhely: {Math.floor(diagnostics.availableBytes/1073741824)} GiB;
        szükséges tartalék: {Math.ceil(diagnostics.requiredBytes/1073741824)} GiB.</p>
      <p>SteamCMD: {diagnostics.steamCmdFound?"Telepítve":"Hiányzik vagy az elérési út érvénytelen"}.
        Napló: {diagnostics.journalBlocked?"kézi ellenőrzés szükséges":"nem blokkol"}.
        Store-v1/karbantartás: {diagnostics.historicalBlocked?"védett":"nem blokkol"}.</p>
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
    {preview&&<div className="uk-rcon-admin-review" role="region" aria-label="SteamCMD frissítés jóváhagyása">
      <b>{preview.mapName} – jelenlegi build: {preview.installedBuild}</b>
      <p>Érintett mapok: {preview.maps.join(", ")}. Szabad hely:
        {" "+Math.round(preview.availableBytes/1073741824)} GiB.
        Előírt helytartalék: {Math.round(preview.reserveBytes/1073741824)} GiB.</p>
      <label><input type="checkbox" checked={approved} onChange={e=>setApproved(e.target.checked)}/>
        Megerősítem, hogy a kiválasztott és a közös telepítést használó mapok leállítva maradnak.</label>
      <div className="uk-rcon-admin-actions">
        <button type="button" className="primary" disabled={!approved||busy}
          onClick={()=>{void update();}}>Valódi frissítés indítása</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>setPreview(null)}>Mégsem</button>
      </div>
    </div>}
    {job&&<div className={job.state==="completed"?"uk-rcon-admin-success":"warning-inline"} role="status">
      {job.state==="completed"?<CheckCircle2 size={18}/>:<Shield size={18}/>}
      <span>Frissítés: {job.state} · {job.stage}
        {job.state==="completed"&&(" · Build "+job.oldBuild+" → "+job.newBuild+" · fájlellenőrzés sikeres")}
        {job.state==="needsOperator"&&" · ellenőrzés szükséges; nincs automatikus ismétlés"}
      </span>
    </div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={18}/>{error}</div>}
    <p className="muted tiny">A frissítő nem állít le futó ASA-t, nem indít újra mapot, nem ír a Store-v1-be és nem kezeli a másik gépet.</p>
  </section>;
}
