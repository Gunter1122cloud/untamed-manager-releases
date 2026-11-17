import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,Octagon,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type FreshAsaStopStatus} from "./bridge";

// Nonsecret operator-visible receipt stays on the map after a dashboard refresh.
// Never store a password, launch command or character metadata in the browser.
const recentStopReceipts=new Map<string,string>();

export function FreshAsaStopPanel({mapId,running,onStopped,journalBlocked}:{
  mapId:string;running:boolean;onStopped:()=>Promise<void>;journalBlocked:boolean
}){
  const [busy,setBusy]=useState(false);
  const [jobId,setJobId]=useState<string|null>(null);
  const [status,setStatus]=useState<FreshAsaStopStatus|null>(null);
  const [message,setMessage]=useState(()=>recentStopReceipts.get(mapId)??"");
  const [error,setError]=useState("");
  const [reconciling,setReconciling]=useState(true);
  const [reconcileFailed,setReconcileFailed]=useState(false);
  const [reconcileNonce,setReconcileNonce]=useState(0);
  const [reviewJournalId,setReviewJournalId]=useState<string|null>(null);
  const mapRef=useRef(mapId);
  mapRef.current=mapId;
  const mounted=useRef(false);
  const actionRevision=useRef(0);
  const mutationInFlight=useRef(false);
  useEffect(()=>{mounted.current=true;return()=>{mounted.current=false;};},[]);
  const belongs=(requested:string)=>mounted.current&&mapRef.current===requested;

  useEffect(()=>{
    ++actionRevision.current;
    setError("");setBusy(false);
    setJobId(null);setStatus(null);setReviewJournalId(null);
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
         current.evidence!=="LOCAL_ASA_STOP_MEMORY_AND_DURABLE_JOURNAL_OBSERVATION_NO_DISPATCH"||
         (current.active&&!current.jobId))
        throw new Error("UM-ASA-STOP-RECONNECT-MISMATCH");
      // Interrupted SaveWorld/DoExit survives even a native Manager restart.
      if(current.state==="needsOperator"){
        setReviewJournalId(current.journalId);
        setReconcileFailed(true);
        setError("UM-ASA-STOP-NEEDS-OPERATOR: A korábbi Stop eredménye bizonytalan. Új Stop nem indítható. A felülvizsgálat csak igazoltan leállított ASA mellett engedélyezett.");
        return;
      }
      setReviewJournalId(null);
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
        if(result.jobId!==jobId||result.killPerformed!==false||
           result.restartPerformed!==false||result.storeTouched!==false||
           (result.state==="completed"&&result.confirmedStopped!==true)){
          // An unrelated task or an unproven PID exit is NOT completed Stop.
          // Keep the original job ID pinned; never dispatch Stop again.
          setReconcileFailed(true);
          setError("UM-ASA-STOP-STATUS-INVALID: A natív Stop-bizonylat jobazonosítója vagy PID-kilépési igazolása nem egyezik. A feladatot nem ismételjük meg.");
          return;
        }
        setReconcileFailed(false);setError("");setStatus(result);
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
          setReconcileFailed(true);
          setError("UM-ASA-STOP-NEEDS-OPERATOR: A leállítás elakadt. Ellenőrizd a folyamatot és a mentési naplót; új Stop, automatikus Kill vagy ismétlés nem engedélyezett.");
          setReconcileNonce(n=>n+1);
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

  const stop=async()=>{
    if(mutationInFlight.current||!running||busy||jobId||journalBlocked||
       reconciling||reconcileFailed)return;
    mutationInFlight.current=true;
    const requested=mapId;
    setBusy(true);setError("");setMessage("");
    let applyAttempted=false;
    try{
      // Exactly one named user gesture creates and consumes a fresh,
      // map/PID/port-scoped native preflight token, with no UI checkbox.
      const proof=await managerBridge.freshAsaStopPreview(requested);
      if(!belongs(requested))return;
      if(proof.mapId.toLowerCase()!==requested.toLowerCase()||
         !proof.token||!Number.isSafeInteger(proof.pid)||proof.pid<=0||
         !proof.usesSaveWorld||!proof.usesDoExit||proof.killPermitted||
         proof.autoRestart)
        throw new Error("UM-ASA-STOP-PREVIEW-MAP-OR-SCOPE-MISMATCH");
      applyAttempted=true;
      ++actionRevision.current;
      const job=await managerBridge.freshAsaStopApply(proof.token);
      if(!belongs(requested))return;
      if(!job.started||typeof job.jobId!=="string"||
         !/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(job.jobId)||
         job.killPerformed!==false||job.autoRestart!==false||
         job.evidence!=="REAL_LOCAL_GRACEFUL_STOP_JOB_QUEUED")
        throw new Error("UM-ASA-STOP-APPLY-RECEIPT-INVALID");
      setJobId(job.jobId);
      setStatus({jobId:job.jobId,state:"queued",step:"preflight",
        changedUtc:new Date().toISOString(),confirmedStopped:false,
        saveCompleteProven:false,preExitSaveEvidenceObserved:false,
        finalDoExitSaveCertified:false,gameClientRejoinVerified:false,
        killPerformed:false,restartPerformed:false,storeTouched:false});
    }catch(e){
      if(belongs(requested)){
        if(applyAttempted)setReconcileFailed(true);
        setError(applyAttempted?
          "UM-ASA-STOP-APPLY-UNKNOWN: A Stop már elindulhatott. "+
            "Nincs automatikus RCON-ismétlés; ellenőrizd a natív naplót.":
          "A kiválasztott map Stop előellenőrzése elutasítva ("+
            (e instanceof Error?e.message:"UM-ASA-STOP-PREFLIGHT-FAILED")+
            "). Nem indult SaveWorld vagy DoExit.");
      }
    }finally{
      mutationInFlight.current=false;
      if(belongs(requested))setBusy(false);
    }
  };

  const reviewStopped=async()=>{
    if(!reviewJournalId||
       busy||jobId||reconciling)return;
    const requested=mapId,reviewId=reviewJournalId;
    setBusy(true);setError("");
    try{
      const receipt=await managerBridge.freshAsaStopReview(
        requested,reviewId,"REVIEW STOPPED ASA");
      if(!belongs(requested))return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         receipt.journalId.toLowerCase()!==reviewId.toLowerCase()||
         receipt.reviewed!==true||receipt.rconDispatched!==false||
         receipt.serverStarted!==false||receipt.storeTouched!==false||
         receipt.evidence!=="LOCAL_ASA_STOP_JOURNAL_OPERATOR_REVIEW_NO_DISPATCH")
        throw new Error("UM-ASA-STOP-REVIEW-RECEIPT-MISMATCH");
      setReviewJournalId(null);
      setReconcileNonce(n=>n+1);
      setMessage("A korábbi Stop-napló felülvizsgálata rögzítve. Nem történt új SaveWorld, DoExit, Kill vagy Start. A mentés épségét ez nem tanúsítja.");
    }catch{
      if(belongs(requested)){
        setReconcileFailed(true);
        setError("UM-ASA-STOP-REVIEW-UNVERIFIED: A natív felülvizsgálat nem igazolt. Nem küldünk új Stopot; ellenőrizd a Windows-folyamatot és a naplót.");
      }
    }finally{if(belongs(requested))setBusy(false);}
  };

  return <section className="premium-panel" aria-label="Valódi ASA biztonságos leállítása" style={{marginBlock:18,padding:20}}>
    <header className="premium-panel-heading"><h2><Octagon size={18}/> ASA szerver normál leállítása</h2></header>
    <p>A helyi C# Manager tényleges RCON SaveWorld és DoExit műveletet küld, megvárja a Windows-folyamat kilépését. A folyamatot nem lövi ki.</p>
    <p className="muted small">Kizárólag a kiválasztott map ({mapId}) saját RCON-tulajdonosához: SaveWorld → szabályos DoExit → PID-kilépés. Nem Kill.</p>
    <button type="button" className="primary" disabled={!running||busy||!!jobId||journalBlocked||reconciling||reconcileFailed}
      onClick={()=>{void stop();}}><Octagon size={16}/> Szerver szabályos leállítása</button>
    {reconciling&&<p role="status">Korábbi Stop-feladat ellenőrzése…</p>}
    {reconcileFailed&&<button type="button" className="secondary"
      onClick={()=>setReconcileNonce(n=>n+1)}>Leállítási állapot újraellenőrzése</button>}
    {reviewJournalId&&!jobId&&<div className="historical-preview" role="region"
      aria-label="Megszakadt Stop-napló felülvizsgálata">
      <p><b>Korábbi Stop nem igazolható.</b> Csak akkor lehet felülvizsgálni,
      ha a natív Windows-folyamatellenőrzés igazoltan leállított állapotot ad.
      Ez nem küld SaveWorld, DoExit, Kill vagy Start parancsot, és nem igazolja
      a lemezre mentett világot.</p>
      <p>Az igazoltan leállított ASA-naplót az alábbi külön
         gombbal jóváhagyhatod. RCON-parancs és Start nem indul.</p>
       <button type="button" className="secondary"
        disabled={busy||reconciling}
        onClick={()=>{void reviewStopped();}}>Igazoltan leállított Stop-napló felülvizsgálata</button>
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
