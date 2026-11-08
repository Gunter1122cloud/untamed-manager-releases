import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Save,Shield} from "lucide-react";
import {managerBridge,type HistoricalSaveWorldPreview,
  type HistoricalGracefulStopRecovery,type HistoricalGracefulStopStatus} from "./bridge";

/** Real, explicitly approved original ACTIVE/DONE SaveWorld. Never DoExit. */
export function HistoricalStoreSaveWorldPanel({mapId,running,onSaved}:{
  mapId:string;running:boolean;onSaved:()=>Promise<void>
}){
  const [recovery,setRecovery]=useState<HistoricalGracefulStopRecovery|null>(null);
  const [checked,setChecked]=useState(false);
  const [preview,setPreview]=useState<HistoricalSaveWorldPreview|null>(null);
  const [phrase,setPhrase]=useState("");
  const [busy,setBusy]=useState(false);
  const [jobId,setJobId]=useState("");
  const [job,setJob]=useState<HistoricalGracefulStopStatus|null>(null);
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");
  const finished=useRef(false);
  useEffect(()=>{
    let active=true;
    finished.current=false;
    setChecked(false);setRecovery(null);setPreview(null);setPhrase("");
    setJobId("");setJob(null);setNotice("");setError("");
    managerBridge.historicalGracefulStopRecovery(mapId).then(result=>{
      if(!active)return;
      setRecovery(result);setChecked(true);
      if(result.inMemoryRunning&&result.jobId&&
         result.activeJobKind==="SaveWorldOnly"){
        // Reattach to the SAME native SaveWorld job. Polling only: never
        // send a second SaveWorld command or reconstruct an approval token.
        setJobId(result.jobId);
        setNotice("Meglévő Store-v1 SaveWorld megfigyeléséhez visszacsatlakozva; nincs új RCON-küldés.");
      }else if(result.inMemoryRunning){
        setError("Másik Store-v1 művelet fut. Mentést nem indítunk és nem küldünk újra.");
      }else if(result.blocked){
        setError("Befejezetlen Store-v1 műveleti napló: kézi ellenőrzésig nincs új SaveWorld.");
      }
    }).catch(()=>{
      if(active){setChecked(true);setError("A tartós Store-v1 műveleti napló nem olvasható; mentés tiltva.");}
    });
    return ()=>{active=false;};
  },[mapId]);
  useEffect(()=>{
    if(!jobId)return;
    let active=true,polling=false;
    const poll=async()=>{
      if(polling||finished.current)return;
      polling=true;
      try{
        const result=await managerBridge.historicalSaveWorldStatus(jobId);
        if(!active)return;
        if(result.kind!=="SaveWorldOnly")throw new Error("Wrong Store operation");
        setJob(result);
        if(result.state==="completed"&&!finished.current){
          finished.current=true;
          setNotice("SaveWorld befejezés: új ASA naplójelzés és stabil .ark megfigyelve. "+
            "A szerver tovább fut; fsync és játékbeli karakterutazás külön igazolandó.");
          try{setRecovery(await managerBridge.historicalGracefulStopRecovery(mapId));}
          catch{/* Do not claim the journal is clean if this read fails. */}
          await onSaved();
        }else if(result.state==="needsOperator"&&!finished.current){
          finished.current=true;
          setError("A SaveWorld státusza bizonytalan. Az eredeti PID és napló kézi ellenőrzése "+
            "kötelező; automatikus újraküldés és feloldás nincs.");
        }
      }catch{if(active)setError("A natív Store-v1 SaveWorld állapota nem olvasható. Ne küldd újra.");}
      finally{polling=false;}
    };
    void poll();
    const handle=window.setInterval(()=>{void poll();},1700);
    return ()=>{active=false;window.clearInterval(handle);};
  },[jobId,mapId,onSaved]);
  const inspect=async()=>{
    if(busy||!checked||!recovery||recovery.blocked||!running||jobId)return;
    setBusy(true);setPreview(null);setPhrase("");setError("");setNotice("");
    try{setPreview(await managerBridge.historicalSaveWorldPreview(mapId));}
    catch{setError("A Store ACTIVE/DONE session, helyi PID, RCON-port vagy forrásadat nem "+
      "ellenőrizhető. SaveWorld parancs nem indult.");}
    finally{setBusy(false);}
  };
  const save=async()=>{
    if(busy||!preview||phrase!=="STORE-V1 SAVEWORLD")return;
    const token=preview.token;
    setPreview(null);setPhrase("");setBusy(true);setError("");
    try{
      const queued=await managerBridge.historicalSaveWorldApply(token,"STORE-V1 SAVEWORLD");
      setJobId(queued.jobId);
      setNotice("A natív SaveWorld elindult. Az ASA mentésnaplója és a .ark fájl "+
        "változása külön ellenőrzés alatt áll.");
    }catch{setError("A Store-v1 SaveWorld nem igazolható. Ne ismételd meg a műveletet "+
      "a PID, a napló és a tartós műveleti állapot ellenőrzése nélkül.");}
    finally{setBusy(false);}
  };
  return <section className="glass panel historical-store-saveonly"
      aria-label="Store-v1 SaveWorld leállítás nélkül">
    <div className="panel-head">
      <h2><Save size={18}/> Store-v1 · SaveWorld mentés</h2>
      <span className="mod-tag green">ASA tovább fut</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">Az eredeti kétgépes ACTIVE/DONE sessiont és Store-v1 lockokat
        változatlanul használja. Az ellenőrzött helyi ASA PID-nek egyszer küldi a SaveWorld
        parancsot. A log és világfájl ellenőrzése után sincs DoExit, konverzió vagy
        karakter-, tribe- és tribute-fájlmódosítás a Managerből.</p>
      <button type="button" className="secondary" disabled={busy||!running||
          !checked||!recovery||recovery.blocked||!!preview||!!jobId||!!error}
        onClick={()=>{void inspect();}}><Shield size={16}/> Mentés előellenőrzése</button>
      {preview&&<div className="warning-inline" role="region"
          aria-label="Store-v1 SaveWorld jóváhagyása">
        <div><strong>{preview.mapName} · PID {preview.pid} · RCON {preview.rconPort}</strong>
          <p>Valódi RCON SaveWorld következik. Írd be pontosan: STORE-V1 SAVEWORLD</p>
          <input aria-label="Store-v1 mentés jóváhagyó szöveg" value={phrase}
            spellCheck={false} autoComplete="off" maxLength={28}
            onChange={e=>setPhrase(e.target.value)}/>
          <div className="settings-actions">
            <button type="button" className="primary"
              disabled={busy||phrase!=="STORE-V1 SAVEWORLD"}
              onClick={()=>{void save();}}><Save size={16}/> SaveWorld végrehajtása</button>
            <button type="button" className="secondary" disabled={busy}
              onClick={()=>{setPreview(null);setPhrase("");}}>Mégsem</button>
          </div>
        </div>
      </div>}
      {job&&<div className="details-line" role="status">
        <span><RefreshCw size={15}/> {job.state==="running"?"Mentés folyamatban":job.state}</span>
        <strong>{job.step}</strong>
      </div>}
      {notice&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{notice}</div>}
      {error&&<div className="warning-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
      {recovery?.blocked&&<p className="muted small">Tartós journal: {recovery.phase}.
        Új mutáló művelet csak operátori ellenőrzés után lehetséges.</p>}
    </div>
  </section>;
}
