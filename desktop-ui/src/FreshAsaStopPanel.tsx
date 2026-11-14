import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,Octagon,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type FreshAsaStopPreview,type FreshAsaStopStatus} from "./bridge";

// Nonsecret operator-visible receipt stays on the map after a dashboard refresh.
// Never store a password, launch command or character metadata in the browser.
const recentStopReceipts=new Map<string,string>();

export function FreshAsaStopPanel({mapId,running,onStopped,journalBlocked}:{
  mapId:string;running:boolean;onStopped:()=>Promise<void>;journalBlocked:boolean
}){
  const [preview,setPreview]=useState<FreshAsaStopPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [jobId,setJobId]=useState<string|null>(null);
  const [status,setStatus]=useState<FreshAsaStopStatus|null>(null);
  const [message,setMessage]=useState(()=>recentStopReceipts.get(mapId)??"");
  const [error,setError]=useState("");
  const [reconciling,setReconciling]=useState(true);
  const [reconcileFailed,setReconcileFailed]=useState(false);
  const [reconcileNonce,setReconcileNonce]=useState(0);
  const mapRef=useRef(mapId);
  mapRef.current=mapId;
  const mounted=useRef(false);
  const actionRevision=useRef(0);
  useEffect(()=>{mounted.current=true;return()=>{mounted.current=false;};},[]);
  const belongs=(requested:string)=>mounted.current&&mapRef.current===requested;

  useEffect(()=>{
    ++actionRevision.current;
    setPreview(null);setApproved(false);setError("");setBusy(false);
    setJobId(null);setStatus(null);
    setMessage(recentStopReceipts.get(mapId)??"");
  },[mapId]);
  // Reattach only a verified, in-memory running job. A finished Stop is NOT
  // resubmitted and never causes a second onStopped callback on page reopen.
  useEffect(()=>{
    let active=true;
    const revision=actionRevision.current;
    setReconciling(true);setReconcileFailed(false);
    managerBridge.freshAsaStopCurrent(mapId).then(current=>{
      if(!active||!belongs(mapId)||revision!==actionRevision.current)return;
      if(current.mapId.toLowerCase()!==mapId.toLowerCase()||
         (current.active&&!current.jobId))
        throw new Error("UM-ASA-STOP-RECONNECT-MISMATCH");
      if(current.active&&current.jobId)setJobId(current.jobId);
    }).catch(e=>{
      if(active&&belongs(mapId)&&revision===actionRevision.current){
        setReconcileFailed(true);
        setError("A futó leállítás natív állapota nem olvasható ("+
          (e instanceof Error?e.message:"ismeretlen")+
          "). Új Stop előtt csak állapotellenőrzés engedélyezett.");
      }
    }).finally(()=>{
      if(active&&belongs(mapId)&&revision===actionRevision.current)
        setReconciling(false);
    });
    return()=>{active=false;};
  },[mapId,reconcileNonce]);
  useEffect(()=>{
    if(!jobId)return;
    let active=true;
    const poll=async()=>{
      try{
        const result=await managerBridge.freshAsaStopStatus(jobId);
        if(!active||!belongs(mapId))return;
        setError("");setStatus(result);
        if(result.state==="completed"){
          setJobId(null);
          const text=result.preExitSaveEvidenceObserved?
            "Az ASA leállt. Az új SaveComplete naplósor és stabil .ark megfigyelve; a végső lemezmentés és visszatöltés nem tanúsított.":
            result.preExitWorldMetadataObserved?
            "Az ASA szabályosan leállt. A megváltozott .ark fájl stabilnak látszott; a SaveComplete naplósor nem jelent meg, ezért annak igazolása külön hiányzik.":
            "Az ASA-folyamat kilépett; a SaveWorld/DoExit lemezmentése nincs igazolva.";
          recentStopReceipts.set(mapId,text);
          setMessage(text);
          onStopped().catch(()=>{});
        }else if(result.state==="needsOperator"){
          setJobId(null);
          setError("A leállítás elakadt. Ellenőrizd a folyamatot és a mentési naplót; nem történt automatikus Kill vagy ismétlés.");
        }
      }catch{
        if(active&&belongs(mapId)){
          // An unreachable status endpoint is NOT proof the native job ended.
          // Retain the job ID and keep polling; never unlock a second Stop.
          setError("A leállítási feladat állapota átmenetileg nem követhető. A már elindított műveletet nem ismételjük meg.");
        }
      }
    };
    void poll();
    const interval=window.setInterval(()=>{void poll();},2500);
    return ()=>{active=false;window.clearInterval(interval);};
  },[jobId,mapId,onStopped]);

  const check=async()=>{
    if(!running||busy||jobId||journalBlocked||reconciling||reconcileFailed)return;
    const requested=mapId;
    setBusy(true);setPreview(null);setApproved(false);setError("");setMessage("");
    try{
      const proof=await managerBridge.freshAsaStopPreview(requested);
      if(!belongs(requested))return;
      if(proof.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-ASA-STOP-PREVIEW-MAP-MISMATCH");
      setPreview(proof);
    }
    catch{
      if(belongs(requested))setError("A leállítás előellenőrzése sikertelen: csak saját helyi, pontosan azonosított RCON-os szerver állítható le.");
    }
    finally{if(belongs(requested))setBusy(false);}
  };
  const stop=async()=>{
    if(!preview||preview.mapId.toLowerCase()!==mapId.toLowerCase()||
       !approved||busy||jobId||journalBlocked||reconciling||reconcileFailed)return;
    const token=preview.token,requested=mapId;
    ++actionRevision.current;
    setBusy(true);setError("");setPreview(null);setApproved(false);
    try{
      const job=await managerBridge.freshAsaStopApply(token);
      if(!belongs(requested))return;
      setJobId(job.jobId);
      setStatus({jobId:job.jobId,state:"queued",step:"preflight",
        changedUtc:new Date().toISOString(),confirmedStopped:false,
        saveCompleteProven:false,preExitSaveEvidenceObserved:false,
        finalDoExitSaveCertified:false,gameClientRejoinVerified:false,
        killPerformed:false,restartPerformed:false,storeTouched:false});
    }catch{
      if(belongs(requested)){
        setReconcileFailed(true);
        setError("A Stop eredménye nem igazolható. A feladat a Windows oldalon már elindulhatott; ne küldd újra. Ellenőrizd a futó műveletet.");
      }
    }finally{if(belongs(requested))setBusy(false);}
  };

  return <section className="premium-panel" aria-label="Valódi ASA biztonságos leállítása" style={{marginBlock:18,padding:20}}>
    <header className="premium-panel-heading"><h2><Octagon size={18}/> ASA szerver normál leállítása</h2></header>
    <p>A helyi C# Manager tényleges RCON SaveWorld és DoExit műveletet küld, megvárja a Windows-folyamat kilépését. A folyamatot nem lövi ki.</p>
    <button type="button" className="secondary" disabled={!running||busy||!!jobId||journalBlocked||reconciling||reconcileFailed}
      onClick={()=>{void check();}}><Shield size={16}/> Normál leállítás előellenőrzése</button>
    {reconciling&&<p role="status">Korábbi Stop-feladat ellenőrzése…</p>}
    {reconcileFailed&&<button type="button" className="secondary"
      onClick={()=>setReconcileNonce(n=>n+1)}>Leállítási állapot újraellenőrzése</button>}
    {preview&&<div className="historical-preview" role="region" aria-label="ASA leállítás jóváhagyása">
      <h3>{preview.mapName} – PID: {preview.pid}</h3>
      <p>RCON: SaveWorld → új, stabil .ark megfigyelése (SaveComplete naplósor, ha létezik) → DoExit → PID-ellenőrzés. Fájlbizonyíték nélkül nincs DoExit vagy automatikus újrapróbálás.</p>
      <label className="historical-approval"><input type="checkbox" checked={approved}
        disabled={busy} onChange={e=>setApproved(e.target.checked)}/>
        Jóváhagyom a kiválasztott helyi ASA szerver tényleges leállítását.
      </label>
      <button type="button" className="primary" disabled={!approved||busy||journalBlocked||reconciling||reconcileFailed}
        onClick={()=>{void stop();}}>Szerver szabályos leállítása</button>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>setPreview(null)}>Mégsem</button>
    </div>}
    {status&&<div className="warning-inline" role="status"><RefreshCw size={16}/>
      <span>Leállítás: {status.state} – {status.step}
        {status.state==="completed"&&" · a PID megszűnése igazolt"}
        {" · Explicit SaveWorld napló+fájl: "}
        {status.preExitSaveEvidenceObserved?"naplóval igazolt":status.preExitWorldMetadataObserved?
          "stabil .ark, napló nélkül":"nem igazolt"}</span></div>}
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{message}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
    <p className="muted tiny"><Shield size={14}/> A történeti Store-v1 átvételt nem módosítja. A szerverleállítás sikeressége önmagában nem bizonyít karakterutazást vagy független SaveComplete-jelet.</p>
  </section>;
}
