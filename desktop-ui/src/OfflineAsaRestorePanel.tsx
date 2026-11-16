import {useCallback,useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,FolderOpen,HardDriveDownload,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type OfflineAsaRestorePreview,type OfflineAsaRestoreStatus} from "./bridge";

/** Strictly offline: selects one verified archive folder with a native
 * Windows picker, restores only to a random TEMP laboratory, no SQL/Store write. */
export function OfflineAsaRestorePanel(){
  const [preview,setPreview]=useState<OfflineAsaRestorePreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [job,setJob]=useState("");
  const [status,setStatus]=useState<OfflineAsaRestoreStatus|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const currentJob=useRef(job);
  currentJob.current=job;
  const inspect=useCallback(async(id:string)=>{
    if(currentJob.current!==id)return;
    try{
      const next=await managerBridge.offlineRestoreLabStatus(id);
      if(currentJob.current!==id)return;
      if(next.jobId!==id){
        setError("A próba-visszaállítás státusza másik feladathoz tartozik. Nem igazolt.");
        return;
      }
      setStatus(next);
    }catch{
      if(currentJob.current===id)
        setError("A próba-visszaállítás státusza nem elérhető. A labor nem tekinthető igazoltnak.");
    }
  },[]);
  useEffect(()=>{
    if(!job||status?.state==="completed"||status?.state==="needsOperator")return;
    let active=true,inFlight=false;
    const poll=()=>{
      if(!active||inFlight||currentJob.current!==job)return;
      inFlight=true;
      void inspect(job).finally(()=>{inFlight=false;});
    };
    poll();
    const interval=window.setInterval(poll,1500);
    return ()=>{active=false;window.clearInterval(interval);};
  },[job,status?.state,inspect]);
  async function prepare(){
    if(busy||job)return;
    setBusy(true);setError("");setNotice("");setPreview(null);setApproved(false);
    try{setPreview(await managerBridge.offlineRestoreLabPreview());}
    catch{setError("Az archívum kiválasztása megszakadt, vagy a csomag formátuma nem megfelelő.");}
    finally{setBusy(false);}
  }
  async function apply(){
    if(busy||!preview||!approved)return;
    setBusy(true);setError("");setStatus(null);setNotice("");
    const token=preview.token;setPreview(null);setApproved(false);
    try{
      const started=await managerBridge.offlineRestoreLabApply(token);
      if(!started.started||!started.jobId||started.productionRestoreAllowed||
         started.sqlUsed)
        throw new Error("UM-OFFLINE-RESTORE-START-RECEIPT-MISMATCH");
      setJob(started.jobId);
      setNotice("A próba-visszaállítás külön TEMP laborba indult. A sikerhez minden fájl lenyomatának egyeznie kell.");
    }catch{setError("A próba-visszaállítást a natív Manager visszautasította.");}
    finally{setBusy(false);}
  }
  const finished=status?.state==="completed"||status?.state==="needsOperator";
  return <section className="premium-panel" aria-label="SQL-mentes próba-visszaállítás">
    <div className="panel-head"><h3><HardDriveDownload size={18}/> Tízmapos archívum próba-visszaállítása</h3>
      <span className="mod-tag amber">Elkülönített Windows TEMP</span></div>
    <p className="muted small">Az előzőleg létrehozott 10-map + Store/transfer
      offline archívum fájljait egy új, elkülönített laborba bontja ki, SHA-256
      ellenőrzéssel. A célkönyvtárat a Manager generálja, nem írható át a felületről.</p>
    <div className="warning-inline"><Shield size={16}/>
      Nem éles restore: nem módosít ASA telepítést, karaktert, SQL-t, közös Store mappát,
      és nem bizonyítja az in-game utazás vagy karakter-visszalépés sikerét.
    </div>
    {!job&&<button className="secondary" type="button" disabled={busy}
      onClick={()=>{void prepare();}}><FolderOpen size={16}/> Offline archívum kiválasztása</button>}
    {preview&&<div className="historical-preview" role="region"
      aria-label="Próba-visszaállítás jóváhagyása">
      <h4>Kiválasztott csomag: {preview.archiveName}</h4>
      <p className="muted small">A kiválasztás még nem igazolja a csomag integritását.
        A tényleges SHA-256 ellenőrzés a művelet során történik.</p>
      <label><input type="checkbox" checked={approved} disabled={busy}
        onChange={e=>setApproved(e.target.checked)}/>
        Tudomásul veszem, hogy csak elkülönített TEMP laborba történik másolás,
        éles cluster és adatbázis visszaállítás nélkül.</label>
      <div className="draft-actions">
        <button type="button" className="primary" disabled={!approved||busy}
          onClick={()=>{void apply();}}>Próba-visszaállítás jóváhagyása</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>setPreview(null)}>Mégsem</button>
      </div>
    </div>}
    {job&&<div className="uk-rcon-receipt" role="status">
      <strong>Labor állapota: {status?.state||"indítás ellenőrzése"}</strong>
      <p>{status?.stage||"SOURCE_SHA256_VERIFICATION"}</p>
      {status?.state==="completed"&&<p><CheckCircle2 size={16}/>
        {status.maps} map, {status.files} világfájl SHA-256 ellenőrizve.
        Labor: {status.labName}. Játékbeli működés nem igazolt.</p>}
      {status?.state==="needsOperator"&&<p><AlertTriangle size={16}/>
        A helyreállítás nem igazolt. A félbemaradt .incomplete labor kézi ellenőrzést igényel.</p>}
      <button className="secondary" type="button"
        onClick={()=>{void inspect(job);}}>
        <RefreshCw size={16}/> Státusz frissítése
      </button>
      {finished&&<button className="secondary" type="button"
        onClick={()=>{setJob("");setStatus(null);setNotice("");setError("");}}>
        Új próba-visszaállítás
      </button>}
    </div>}
    {notice&&<p className="muted small">{notice}</p>}
    {error&&<p className="error-inline" role="alert"><AlertTriangle size={16}/>{error}</p>}
  </section>;
}
