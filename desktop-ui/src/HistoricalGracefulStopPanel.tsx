import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Shield,Square,LockKeyhole} from "lucide-react";
import {managerBridge,type HistoricalGracefulStopPreview,
  type HistoricalGracefulStopStatus,type HistoricalGracefulStopRecovery} from "./bridge";

/**
 * Original ACTIVE/DONE Store shutdown is intentional and one-shot:
 * real SaveWorld, new ASA SaveComplete log and stable .ark, then DoExit,
 * then Windows confirms old PID exited. This never fabricates durable
 * fsync, game-client rejoin, tribe/inventory travel or shared restore.
 */
export function HistoricalGracefulStopPanel({mapId,running,onStopped}:{
  mapId:string;running:boolean;onStopped:()=>Promise<void>
}){
  const [preview,setPreview]=useState<HistoricalGracefulStopPreview|null>(null);
  const [phrase,setPhrase]=useState("");
  const [job,setJob]=useState<HistoricalGracefulStopStatus|null>(null);
  const [jobId,setJobId]=useState("");
  const [recovery,setRecovery]=useState<HistoricalGracefulStopRecovery|null>(null);
  const [recoveryChecked,setRecoveryChecked]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const notified=useRef(false);
  useEffect(()=>{
    let active=true;
    setPreview(null);setPhrase("");setJob(null);setJobId("");
    setRecovery(null);setRecoveryChecked(false);
    setError("");setNotice("");notified.current=false;
    managerBridge.historicalGracefulStopRecovery(mapId).then(result=>{
      if(!active)return;
      setRecovery(result);setRecoveryChecked(true);
      if(result.inMemoryRunning&&result.jobId)
        setJobId(result.jobId); // resume polling after React view remount
      if(result.requiresOperator)
        setError("Korábban megszakadt művelet tartós naplója található. "+
          "Ne indíts új SaveWorld/DoExit vagy Start műveletet. "+
          "Ellenőrizd az eredeti ASA PID-et, mentésnaplót és a helyi művelet állapotát.");
    }).catch(()=>{
      if(active){
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
        if(!alive)return;
        setJob(result);
        if(result.state==="completed"&&!notified.current){
          notified.current=true;
          setNotice("A régi ASA-folyamat szabályosan kilépett. Az új SaveWorld "+
            "napló- és világfájl-bizonyíték rendelkezésre áll; "+
            "a végső DoExit fsync és játékbeli karakterutazás még külön vizsgálandó.");
          try{
            setRecovery(await managerBridge.historicalGracefulStopRecovery(mapId));
          }catch{/* Keep original job receipt; never infer unlock from a failed read. */}
          await onStopped();
        }
        if(result.state==="needsOperator"&&!notified.current){
          notified.current=true;
          setError("A Store-v1 Stop megszakadt. A Manager nem próbálja újra: "+
            "ellenőrizd a valódi PID-et és a tartós ASA műveleti naplót.");
        }
      }catch{
        if(alive)setError("A natív Stop-folyamat állapota átmenetileg nem olvasható; "+
          "ne indíts új Stop/Restart műveletet.");
      }finally{polling=false;}
    };
    void poll();
    const interval=window.setInterval(()=>{
      if(!notified.current)void poll();
    },1800);
    return ()=>{alive=false;window.clearInterval(interval);};
  },[jobId,onStopped]);
  const inspect=async()=>{
    if(busy||jobId||!running||!recoveryChecked||
       !recovery||recovery.blocked)return;
    setBusy(true);setError("");setNotice("");setPreview(null);setPhrase("");
    try{setPreview(await managerBridge.historicalGracefulStopPreview(mapId));}
    catch{
      setError("Az eredeti Store-v1 kétpeer-es ACTIVE/DONE session, a "+
        "Windows ASA PID vagy a hozzá tartozó RCON TCP-port nem igazolható. "+
        "Nem küldtünk SaveWorld parancsot.");
    }finally{setBusy(false);}
  };
  const stop=async()=>{
    if(!preview||busy||phrase!=="STORE-V1 SAVEWORLD DOEXIT")return;
    setBusy(true);setError("");setNotice("");
    const token=preview.token;setPreview(null);setPhrase("");
    try{
      const receipt=await managerBridge.historicalGracefulStopApply(
        token,"STORE-V1 SAVEWORLD DOEXIT");
      setJobId(receipt.jobId);
      setNotice("A natív SaveWorld/DoExit feladat elindult. "+
        "Új mentésigazolás nélkül a DoExit nem kerül elküldésre.");
    }catch{
      setError("A jóváhagyás előtt az eredeti session/PID/port megváltozott. "+
        "A natív leállítás nem engedélyezett.");
    }finally{setBusy(false);}
  };
  return <section className="glass panel historical-graceful-stop"
    aria-label="Eredeti Store-v1 SaveWorld és DoExit">
    <div className="panel-head">
      <h2><Square size={18}/> Eredeti Store-v1 mentés és leállítás</h2>
      <span className="mod-tag amber"><LockKeyhole size={13}/> Manuális · biztonságos</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">Valódi RCON SaveWorld csak a saját ASA-folyamatnak,
        új „World Save Complete” naplóbejegyzés és stabil világfájl ellenőrzése,
        majd szabályos DoExit és Windows PID-kilépés. Nincs Kill, automatikus
        újraindítás, Store-konverzió vagy shared lock törlése.</p>
      <div className="settings-actions">
        <button className="secondary" type="button"
          disabled={busy||!running||!!preview||!!jobId||
            !recoveryChecked||!recovery||recovery.blocked}
          onClick={()=>{void inspect();}}>
          <Shield size={16}/> Eredeti SaveWorld/DoExit előellenőrzése
        </button>
      </div>
      {recovery?.blocked&&<div className="warning-inline" role="region"
        aria-label="Tartós Store-v1 műveleti helyreállítás">
        <AlertTriangle size={18}/>
        <div>
          <b>Korábbi műveleti napló · {recovery.phase}</b>
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
      {preview&&<div className="warning-inline" role="region"
          aria-label="Eredeti Store-v1 leállítás jóváhagyása">
        <AlertTriangle size={18}/>
        <div>
          <b>{preview.mapName} · PID {preview.pid} · TCP {preview.rconPort}</b>
          <p>A program a jelenlegi mentést SaveWorld paranccsal kezdi, és
            csak új mentésjelzés után küld DoExit-et. Írd be a biztonsági
            jóváhagyást pontosan: STORE-V1 SAVEWORLD DOEXIT</p>
          <label htmlFor="historical-doexit-phrase">Pontos jóváhagyó szöveg</label>
          <input id="historical-doexit-phrase" value={phrase} autoComplete="off"
            maxLength={40} spellCheck={false}
            onChange={e=>setPhrase(e.target.value)}/>
          <div className="settings-actions">
            <button type="button" className="primary"
              disabled={busy||phrase!=="STORE-V1 SAVEWORLD DOEXIT"}
              onClick={()=>{void stop();}}>
              <Square size={16}/> SaveWorld és szabályos DoExit
            </button>
            <button type="button" className="secondary" disabled={busy}
              onClick={()=>{setPreview(null);setPhrase("");}}>Mégse</button>
          </div>
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
