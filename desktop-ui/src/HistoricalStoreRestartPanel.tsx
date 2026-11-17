import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Shield,RotateCw} from "lucide-react";
import {managerBridge,type HistoricalRestartPreview,
  type HistoricalRestartStatus,type HistoricalRestartRecovery} from "./bridge";

/** Native original ACTIVE Store-v1 Restart; no WebView arguments or RCON bytes. */
export function HistoricalStoreRestartPanel({mapId,running,onRestarted}:{
  mapId:string;running:boolean;onRestarted:()=>Promise<void>
}){
  const [recovery,setRecovery]=useState<HistoricalRestartRecovery|null>(null);
  const [checked,setChecked]=useState(false);
  const [busy,setBusy]=useState(false);
  const [jobId,setJobId]=useState("");
  const [job,setJob]=useState<HistoricalRestartStatus|null>(null);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const finished=useRef(false);
  const actionInFlight=useRef(false);
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const belongs=(requested:string)=>currentMap.current===requested;
  useEffect(()=>{
    let alive=true;
    finished.current=false;setChecked(false);setRecovery(null);
    setBusy(false);setJobId("");setJob(null);
    setError("");setNotice("");
    managerBridge.historicalRestartRecovery(mapId).then(result=>{
      if(!alive||!belongs(mapId))return;
      if(result.mapId.toLowerCase()!==mapId.toLowerCase())
        throw new Error("UM-STORE-RESTART-RECOVERY-WRONG-MAP");
      setChecked(true);setRecovery(result);
      if(result.inMemoryRunning&&result.jobId){
        setJobId(result.jobId);
        setNotice("A meglévő eredeti Store-v1 újraindítás állapotához visszacsatlakozva; nincs új RCON-küldés.");
      }else if(result.requiresOperator||result.blocked){
        setError(result.operationJournalUnreadable||result.restartJournalUnreadable
          ? "A helyi Operations-v1 vagy Restart-v1 napló nem olvasható ("+
            (result.operationJournalErrorCode??result.restartJournalErrorCode??"UM-JOURNAL-UNREADABLE")+
            "). A helyi eredeti napló ellenőrzéséig nincs Restart vagy RCON."
          : result.saveEvidenceUnreadable
          ? "Sérült vagy félbeszakadt Store-v1 SaveWorld-bizonylat "+
            "(UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE). Az eredeti napló megmarad; "+
            "nincs Restart, RCON-küldés vagy automatikus feloldás."
          : result.saveEvidencePending
            ? "Befejezetlen vagy bizonytalan Store-v1 SaveWorld-bizonylat: "+
              "előbb a SaveWorld panelen ellenőrizd a mentés állapotát. "+
              "Nincs újraindítás, RCON vagy automatikus ismétlés."
            : "Befejezetlen Store-v1 újraindítás vagy másik natív művelet: "+
              "a PID és a helyi napló kézi ellenőrzéséig nincs új indítás.");
      }
    }).catch(()=>{
      if(alive&&belongs(mapId)){setChecked(true);setError("A helyi Store-v1 újraindítási napló nem ellenőrizhető; újraindítás tiltva.");}
    });
    return ()=>{alive=false;};
  },[mapId]);
  useEffect(()=>{
    if(!jobId)return;
    let alive=true,polling=false;
    const poll=async()=>{
      if(polling||finished.current)return;
      polling=true;
      try{
        const result=await managerBridge.historicalRestartStatus(jobId);
        if(!alive||!belongs(mapId))return;
        if(result.mapId.toLowerCase()!==mapId.toLowerCase())
          throw new Error("Wrong native map");
        setJob(result);
        if(result.state==="completed"&&!finished.current){
          finished.current=true;
          setNotice("Új eredeti ASA PID: "+result.newPid+
            ". A régi folyamat kilépése és az előző mentésjelzés ellenőrizve. "+
            "A játékbeli csatlakozás és karakterutazás külön igazolandó.");
          try{setRecovery(await managerBridge.historicalRestartRecovery(mapId));}
          catch{/* No inferred unlock when native recovery is unavailable. */}
          try{await onRestarted();}
          catch{
            if(alive&&belongs(mapId))
              setError("Az új ASA PID natív visszaigazolása megvan, de a dashboard nem frissült; nem indítjuk újra a műveletet.");
          }
        }else if(result.state==="needsOperator"&&!finished.current){
          finished.current=true;
          setError("A Store-v1 Restart megszakadt. Nincs automatikus ismétlés. "+
            "Ellenőrizd az eredeti ASA PID-et és az újraindítási naplót.");
        }
      }catch{
        if(alive&&belongs(mapId))setError("A natív Store-v1 Restart állapota nem olvasható; ne indítsd újra.");
      }finally{polling=false;}
    };
    void poll();
    const interval=window.setInterval(()=>{void poll();},1800);
    return ()=>{alive=false;window.clearInterval(interval);};
  },[jobId,mapId,onRestarted]);
  const refreshRecovery=async()=>{
    // Read-only check: clearing a SaveWorld recovery receipt in its own
    // panel must not require closing/restarting this Manager or repeating RCON.
    if(busy)return;
    const requested=mapId;
    setBusy(true);setChecked(false);setError("");setNotice("");
    
    try{
      const result=await managerBridge.historicalRestartRecovery(requested);
      if(!belongs(requested))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-STORE-RESTART-RECOVERY-WRONG-MAP");
      setRecovery(result);setChecked(true);
      if(result.inMemoryRunning&&result.jobId){
        setJobId(result.jobId);
        setNotice("A már futó újraindítás állapotához visszacsatlakozva; nincs új RCON.");
      }else if(result.operationJournalUnreadable||result.restartJournalUnreadable){
        setError("Operations-v1 / Restart-v1 naplóellenőrzési hiba: "+
          (result.operationJournalErrorCode??result.restartJournalErrorCode??"UM-JOURNAL-UNREADABLE")+
          ". Nincs automatikus feloldás vagy RCON.");
      }else if(result.saveEvidenceUnreadable){
        setError("Sérült vagy félbeszakadt SaveWorld-bizonylat "+
          "(UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE). Kézi naplóellenőrzés szükséges; "+
          "nincs automatikus feloldás.");
      }else if(result.saveEvidencePending){
        setError("Befejezetlen vagy bizonytalan Store-v1 SaveWorld-bizonylat. "+
          "Ellenőrizd a SaveWorld panelen, majd frissítsd itt az állapotot.");
      }else if(result.blocked||result.requiresOperator){
        setError("A tartós Store-v1 napló további kézi ellenőrzést igényel.");
      }else{
        setNotice("A helyi naplók nem jeleznek függő műveletet. "+
          "Ez még nem igazolja a szerver játékbeli készenlétét.");
      }
    }catch{
      if(belongs(requested)){
        setRecovery(null);setError("A helyi Store-v1 műveleti naplók nem "+
          "ellenőrizhetők; Restart továbbra is tiltva.");
      }
    }finally{if(belongs(requested))setBusy(false);}
  };
  const inspect=async()=>{
    if(actionInFlight.current||busy||!running||!checked||!recovery||
       recovery.blocked||recovery.inMemoryRunning||jobId||error)return;
    actionInFlight.current=true;
    const requested=mapId;
    setBusy(true);setError("");setNotice("");
    let applyAttempted=false;
    try{
      const proof=await managerBridge.historicalRestartPreview(requested);
      if(!belongs(requested))return;
      if(!proof.token||proof.mapId.toLowerCase()!==requested.toLowerCase()||
         !proof.usesSaveWorld||!proof.usesDoExit||!proof.launchesOriginalActive||
         proof.originalStoreTouched||proof.conversionAllowed)
        throw new Error("UM-STORE-RESTART-PREVIEW-MAP-MISMATCH");
      applyAttempted=true;
      const result=await managerBridge.historicalRestartApply(proof.token);
      if(!belongs(requested))return;
      if(!result.jobId||!result.started||result.forceKill||
         result.storeTouched||result.automaticRetry)
        throw new Error("UM-STORE-RESTART-JOB-RECEIPT-MISMATCH");
      setJobId(result.jobId);
      setNotice("Store-v1 Restart elindult a kiválasztott mapon. "+
        "Új ASA csak igazolt régi PID-kilépés után indulhat.");
    }catch(e){
      if(belongs(requested)){
        const code=e instanceof Error?e.message:"";
        setError(!applyAttempted?
          "A natív Restart előellenőrzés elutasította a mapot ("+
            (/^UM-[A-Z0-9-]+$/.test(code)?code:"UM-STORE-RESTART-PREFLIGHT")+"). Nincs RCON.":
          "A Restart kimenetele bizonytalan ("+
            (/^UM-[A-Z0-9-]+$/.test(code)?code:"UM-STORE-RESTART-APPLY-UNKNOWN")+"). "+
            "Ne indíts új műveletet; a tartós naplót ellenőrizni kell.");
      }
    }finally{
      actionInFlight.current=false;
      if(belongs(requested))setBusy(false);
    }
  };
  return <section className="glass panel historical-store-restart"
    aria-label="Eredeti Store-v1 manuális újraindítás">
    <div className="panel-head">
      <h2><RotateCw size={18}/> Eredeti Store-v1 · Restart</h2>
      <span className="mod-tag amber"><Shield size={13}/> Natív egyszeri művelet</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">SaveWorld → új ASA mentésnapló és stabil .ark →
        DoExit → a régi Windows PID kilépése → az eredeti ACTIVE -usestore
        indítás újbóli ellenőrzése → új helyi ASA PID. Az opcionális ASA API Loader
        módnál a gyermek ASA PID és RCON-tulajdonlás külön igazolandó.
        A kétgépes Store session/lock változatlan. Nincs automatikus újrapróbálás vagy konverzió.</p>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>{void refreshRecovery();}}>
        <RefreshCw size={16}/> Store-v1 napló újraellenőrzése
      </button>
      <p className="muted small">Hatókör: kizárólag a kiválasztott map ({mapId}).
        A gomb SaveWorld, szabályos DoExit, igazolt régi PID-kilépés,
        majd ugyanennek a mapnak új indítását kéri; nem Kill vagy konverzió.</p>
      <button type="button" className="primary"
        disabled={busy||!running||!checked||!recovery||recovery.blocked||
          recovery.inMemoryRunning||!!jobId||!!error}
        onClick={()=>{void inspect();}}>
        <RotateCw size={16}/> Store-v1 Restart indítása (SaveWorld + DoExit + Start)
      </button>
      {recovery?.blocked&&<div className="warning-inline" role="status">
        <AlertTriangle size={17}/>
        {recovery.operationJournalUnreadable||recovery.restartJournalUnreadable
          ? <>Helyi Operations-v1 / Restart-v1 napló nem olvasható.
              Hibakód: {recovery.operationJournalErrorCode??recovery.restartJournalErrorCode}.
              Nincs újraindítás, az eredeti napló érintetlen.</>
          : recovery.saveEvidenceUnreadable
          ? <>Sérült Store-v1 SaveWorld-bizonylat ({recovery.saveEvidencePhase}).
              A napló megmarad; kézi ellenőrzés és külön helyreállítás szükséges.</>
          : recovery.saveEvidencePending
          ? <>Függő Store-v1 SaveWorld-bizonylat ({recovery.saveEvidencePhase}).
              Előbb a SaveWorld panelen ellenőrizd; nincs automatikus folytatás.</>
          : <>Tartós újraindítási vagy másik műveleti napló:
              {recovery.phase} · automatikus folytatás tiltva.</>}
      </div>}
      {job&&<div className="details-line" role="status">
        <span><RefreshCw size={15}/> {job.state==="running"?"Folyamatban":
          job.state==="completed"?"Új folyamat elindult":
          job.state==="needsOperator"?"Kézi ellenőrzés szükséges":"Sorban áll"}</span>
        <b>{job.step}</b>
      </div>}
      {job?.state==="completed"&&<div className="success-inline">
        <CheckCircle2 size={17}/> Régi PID {job.oldPid} → új PID {job.newPid}
      </div>}
      {notice&&<div className="success-inline" role="status">
        <CheckCircle2 size={17}/>{notice}</div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={17}/>{error}</div>}
      <div className="warning-inline"><Shield size={16}/>
        A Windows Process.Start önmagában nem bizonyít kész szervert,
        megőrzött karaktert, tribe-ot, inventoryt vagy tribute-utazást.
      </div>
    </div>
  </section>;
}
