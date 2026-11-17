import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Shield,Square,LockKeyhole} from "lucide-react";
import {managerBridge,type HistoricalGracefulStopStatus,
  type HistoricalGracefulStopRecovery} from "./bridge";

/**
 * Original ACTIVE/DONE Store shutdown is intentional and one-shot:
 * real SaveWorld, new ASA SaveComplete log and stable .ark, then DoExit,
 * then Windows confirms old PID exited. This never fabricates durable
 * fsync, game-client rejoin, tribe/inventory travel or shared restore.
 */
export function HistoricalGracefulStopPanel({mapId,running,onStopped}:{
  mapId:string;running:boolean;onStopped:()=>Promise<void>
}){
  const [job,setJob]=useState<HistoricalGracefulStopStatus|null>(null);
  const [jobId,setJobId]=useState("");
  const [recovery,setRecovery]=useState<HistoricalGracefulStopRecovery|null>(null);
  const [recoveryChecked,setRecoveryChecked]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const notified=useRef(false);
  const actionInFlight=useRef(false);
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const belongs=(requested:string)=>currentMap.current===requested;
  useEffect(()=>{
    let active=true;
    setJob(null);setJobId("");
    setRecovery(null);setRecoveryChecked(false);
    setError("");setNotice("");notified.current=false;
    managerBridge.historicalGracefulStopRecovery(mapId).then(result=>{
      if(!active||!belongs(mapId))return;
      if(result.mapId.toLowerCase()!==mapId.toLowerCase())
        throw new Error("UM-STORE-STOP-RECOVERY-WRONG-MAP");
      setRecovery(result);setRecoveryChecked(true);
      if(result.inMemoryRunning&&result.jobId&&result.activeJobKind==="Stop")
        setJobId(result.jobId); // only reattach to the original Stop job
      else if(result.inMemoryRunning)
        setError("Másik Store-v1 művelet fut. Nem indítunk új leállítást.");
      if(result.requiresOperator)
        setError(result.operationJournalUnreadable||result.restartJournalUnreadable
          ? "Helyi műveleti napló olvashatatlan: "+
            (result.operationJournalErrorCode??result.restartJournalErrorCode??"UM-JOURNAL-UNREADABLE")+
            ". Sem Stop, sem RCON nem indul, az eredeti fájlok megmaradnak."
          : result.saveEvidenceUnreadable
          ? "Sérült vagy félbeszakadt Store-v1 SaveWorld-bizonylat "+
            "(UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE). A helyi naplók megmaradnak; "+
            "Stop, RCON és automatikus feloldás tiltva."
          : "Korábban megszakadt művelet tartós naplója található. "+
            "Ne indíts új SaveWorld/DoExit vagy Start műveletet. "+
            "Ellenőrizd az eredeti ASA PID-et, mentésnaplót és a helyi művelet állapotát.");
    }).catch(()=>{
      if(active&&belongs(mapId)){
        setRecoveryChecked(true);
        setError("A tartós Store-v1 műveleti napló nem ellenőrizhető. "+
          "Biztonsági okból az új leállítás tiltott.");
      }
    });
    return ()=>{active=false;};
  },[mapId]);
  useEffect(()=>{
    if(!jobId)return;
    let alive=true;
    let polling=false;
    const poll=async()=>{
      if(polling)return;
      polling=true;
      try{
        const result=await managerBridge.historicalGracefulStopStatus(jobId);
        if(!alive||!belongs(mapId))return;
        setJob(result);
        if(result.state==="completed"&&!notified.current){
          notified.current=true;
          setNotice("A régi ASA-folyamat szabályosan kilépett. Az új SaveWorld "+
            "napló- és világfájl-bizonyíték rendelkezésre áll; "+
            "a végső DoExit fsync és játékbeli karakterutazás még külön vizsgálandó.");
          try{
            setRecovery(await managerBridge.historicalGracefulStopRecovery(mapId));
          }catch{/* Keep original job receipt; never infer unlock from a failed read. */}
          try{await onStopped();}
          catch{
            if(alive&&belongs(mapId))setError("A Stop natív visszaigazolása megvan, de a szerverlista frissítése sikertelen. Nem küldünk új DoExit parancsot.");
          }
        }
        if(result.state==="needsOperator"&&!notified.current){
          notified.current=true;
          setError("A Store-v1 Stop megszakadt. A Manager nem próbálja újra: "+
            "ellenőrizd a valódi PID-et és a tartós ASA műveleti naplót.");
        }
      }catch{
        if(alive&&belongs(mapId))setError("A Stop-folyamat állapota átmenetileg nem olvasható; "+
          "ne indíts új Stop/Restart műveletet.");
      }finally{polling=false;}
    };
    void poll();
    const interval=window.setInterval(()=>{
      if(!notified.current)void poll();
    },1800);
    return ()=>{alive=false;window.clearInterval(interval);};
  },[jobId,onStopped]);
  const refreshRecovery=async()=>{
    // Read-only: neither this button nor a past Stop job authorizes any
    // new SaveWorld/DoExit, process stop or journal transition.
    if(busy||(jobId&&job?.state!=="completed"&&job?.state!=="needsOperator"))
      return;
    const requested=mapId;
    setBusy(true);setError("");setNotice("");
    try{
      const result=await managerBridge.historicalGracefulStopRecovery(requested);
      if(!belongs(requested))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         result.sharedStoreMutated||result.lockCleared)
        throw new Error("UM-STORE-STOP-RECOVERY-WRONG-MAP");
      setRecovery(result);setRecoveryChecked(true);
      if(result.operationJournalUnreadable||result.restartJournalUnreadable)
        setError("Helyi Operations-v1 / Restart-v1 napló nem olvasható: "+
          (result.operationJournalErrorCode??result.restartJournalErrorCode??"UM-JOURNAL-UNREADABLE")+
          ". Kézi vizsgálat szükséges, új RCON-parancs nem indult.");
      else if(result.saveEvidenceUnreadable)
        setError("Sérült vagy félbeszakadt SaveWorld-bizonylat "+
          "(UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE). Nincs automatikus feloldás.");
      else if(result.blocked||result.requiresOperator)
        setError("A Store-v1 műveleti napló még függő állapotban van. "+
          "Külön operátori ellenőrzés szükséges, új RCON-parancsot nem küldtünk.");
      else{
        if(jobId&&job?.state==="completed"){
          setJobId("");setJob(null);notified.current=false;
        }
        setNotice("A helyi Stop-napló újraellenőrizve, parancs nem indult. "+
          "Új művelethez új, külön PID/RCON/Store-forrás jóváhagyás szükséges.");
      }
    }catch{
      if(belongs(requested)){
        setRecovery(null);setRecoveryChecked(true);
        setError("A Store-v1 Stop-napló nem ellenőrizhető; "+
          "leállítás és automatikus ismétlés továbbra is tiltva.");
      }
    }finally{if(belongs(requested))setBusy(false);}
  };
  const inspect=async()=>{
    if(actionInFlight.current||busy||jobId||!running||!recoveryChecked||
       !recovery||recovery.blocked||recovery.inMemoryRunning||error)return;
    actionInFlight.current=true;
    const requested=mapId;
    setBusy(true);setError("");setNotice("");
    let applyAttempted=false;
    try{
      const proof=await managerBridge.historicalGracefulStopPreview(requested);
      if(!belongs(requested))return;
      if(!proof.token||proof.mapId.toLowerCase()!==requested.toLowerCase()||
         !proof.usesSaveWorld||!proof.usesDoExit||proof.originalStoreTouched)
        throw new Error("UM-STORE-STOP-PREVIEW-MAP-MISMATCH");
      // One user gesture, map/PID/source-bound native preflight and one-use apply.
      applyAttempted=true;
      const result=await managerBridge.historicalGracefulStopApply(proof.token);
      if(!belongs(requested))return;
      if(!result.jobId||!result.started||result.forceKill||
         result.storeTouched||result.automaticRetry)
        throw new Error("UM-STORE-STOP-JOB-RECEIPT-MISMATCH");
      setJobId(result.jobId);
      setNotice("SaveWorld + szabályos DoExit elindult a kiválasztott mapon. "+
        "Nincs Kill, Store-konverzió vagy automatikus újraküldés.");
    }catch(e){
      if(belongs(requested)){
        const code=e instanceof Error?e.message:"";
        setError(!applyAttempted?
          "A helyi PID/forrás/RCON előellenőrzése sikertelen; Stop nem indult ("+
            (/^UM-[A-Z0-9-]+$/.test(code)?code:"UM-STORE-STOP-PREFLIGHT-FAILED")+").":
          "A SaveWorld/DoExit feladat már elindulhatott ("+
            (/^UM-[A-Z0-9-]+$/.test(code)?code:"UM-STORE-STOP-APPLY-UNKNOWN")+"). "+
            "Ne indíts újra; a natív műveleti naplót kell egyeztetni.");
      }
    }finally{
      actionInFlight.current=false;
      if(belongs(requested))setBusy(false);
    }
  };
  return <section className="glass panel historical-graceful-stop"
    aria-label="Eredeti Store-v1 SaveWorld és DoExit">
    <div className="panel-head">
      <h2><Square size={18}/> Eredeti Store-v1 mentés és leállítás</h2>
      <span className="mod-tag amber"><LockKeyhole size={13}/> Manuális · biztonságos</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">Hatókör: kizárólag a kiválasztott map ({mapId}).
        Ez SaveWorld és szabályos DoExit, nem Kill vagy konverzió.
        Valódi RCON SaveWorld csak a saját ASA-folyamatnak,
        új „World Save Complete” naplóbejegyzés és stabil világfájl ellenőrzése,
        majd szabályos DoExit és Windows PID-kilépés. Nincs Kill, automatikus
        újraindítás, Store-konverzió vagy shared lock törlése.</p>
      <div className="settings-actions">
        <button className="secondary" type="button"
          disabled={busy||
            (!!jobId&&job?.state!=="completed"&&job?.state!=="needsOperator")}
          onClick={()=>{void refreshRecovery();}}>
          <RefreshCw size={16}/> Store-v1 leállítási napló újraellenőrzése
        </button>
        <button className="primary" type="button"
          disabled={busy||!running||!!jobId||!!error||
            !recoveryChecked||!recovery||recovery.blocked||recovery.inMemoryRunning}
          onClick={()=>{void inspect();}}>
          <Square size={16}/> SaveWorld + szabályos DoExit indítása
        </button>
      </div>
      {recovery?.blocked&&<div className="warning-inline" role="region"
        aria-label="Tartós Store-v1 műveleti helyreállítás">
        <AlertTriangle size={18}/>
        <div>
          <b>{recovery.operationJournalUnreadable||recovery.restartJournalUnreadable?
            "Helyi napló olvashatatlan · "+
              (recovery.operationJournalErrorCode??recovery.restartJournalErrorCode):
            recovery.saveEvidenceUnreadable?
            "Sérült Store-v1 SaveWorld-bizonylat · unreadable":
            "Korábbi műveleti napló · "+recovery.phase}</b>
          <p>{recovery.inMemoryRunning
            ?"A natív Stop-folyamat még fut. A napló és az eredeti session érintetlen."
            :"A művelet lezárása nincs biztonságosan igazolva. "+
              "A Manager nem oldja fel automatikusan a zárolást és nem "+
              "küld új SaveWorld/DoExit parancsot."}</p>
          <p>{recovery.requiresOperator
            ?"Kézi ellenőrzés szükséges az újabb műveletek előtt."
            :"A folyamat állapotát a natív háttérfeladatból figyeljük."}</p>
        </div>
      </div>}
      {job&&<div className="details-line" role="status">
        <span><RefreshCw size={15}/> {job.state==="running"?"Folyamatban":
          job.state==="completed"?"Befejeződött":
          job.state==="needsOperator"?"Kézi ellenőrzés szükséges":"Előkészítés"}</span>
        <b>{job.step}</b>
      </div>}
      {job?.state==="completed"&&<div className="success-inline">
        <CheckCircle2 size={17}/> ASA leállítva ·
        {job.preExitSaveLogAndWorldObserved?" mentésjelzés megfigyelve":" mentésjelzés nincs igazolva"}
      </div>}
      {notice&&<div className="success-inline" role="status">
        <CheckCircle2 size={17}/>{notice}</div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={17}/>{error}</div>}
      <div className="warning-inline"><Shield size={16}/>
        A SaveComplete log és a stabil .ark nem azonos a végső fsync, a kétgépes
        karakter/tribe/tribute utazás vagy a restore játékbeli bizonyításával.
      </div>
    </div>
  </section>;
}
