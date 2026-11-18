import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RotateCcw,Shield} from "lucide-react";
import {managerBridge,type FreshAsaRestartStatus,
  type AsaRestartJournalStatus} from "./bridge";

const latestRestart=new Map<string,string>();

export function FreshAsaRestartPanel({mapId,running,journalBlocked,onRestarted}:{
  mapId:string;running:boolean;journalBlocked:boolean;
  onRestarted:()=>Promise<void>
}){
  const [busy,setBusy]=useState(false);
  const [jobId,setJobId]=useState<string|null>(null);
  const [status,setStatus]=useState<FreshAsaRestartStatus|null>(null);
  const [message,setMessage]=useState(()=>latestRestart.get(mapId)??"");
  const [error,setError]=useState("");
  const [needsLocalReview,setNeedsLocalReview]=useState(false);
  const [durable,setDurable]=useState<AsaRestartJournalStatus|null>(null);
  const [auditConfirmed,setAuditConfirmed]=useState(false);
  const reviewInFlight=useRef(false);
  const mutationInFlight=useRef(false);
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const mounted=useRef(false);
  const actionRevision=useRef(0);
  const [reconciling,setReconciling]=useState(true);
  const [reconcileFailed,setReconcileFailed]=useState(false);
  const [reconcileNonce,setReconcileNonce]=useState(0);
  useEffect(()=>{mounted.current=true;return()=>{mounted.current=false;};},[]);
  const belongs=(requested:string)=>mounted.current&&currentMap.current===requested;
  useEffect(()=>{
    ++actionRevision.current;
    setError("");setBusy(false);
    setJobId(null);setStatus(null);
    setNeedsLocalReview(false);setDurable(null);setAuditConfirmed(false);
    setMessage(latestRestart.get(mapId)??"");
  },[mapId]);
  // Reattach running jobs only. A terminal native receipt is historical
  // state, not a fresh completed event that should re-trigger onRestarted.
  useEffect(()=>{
    let active=true;
    const revision=actionRevision.current;
    setReconciling(true);setReconcileFailed(false);
    managerBridge.freshAsaRestartCurrent(mapId)
      .then(current=>{
        if(!active||!belongs(mapId)||revision!==actionRevision.current)return;
        if(current.evidence!=="DURABLE_MACHINE_LOCAL_MANUAL_ASA_RESTART"||
           (current.exists&&!current.belongsToMap))
          throw new Error("UM-ASA-RESTART-CURRENT-MAP-MISMATCH");
        setDurable(current);
        if(current.inMemoryRunning&&!current.inMemoryJobId)
          throw new Error("UM-ASA-RESTART-CURRENT-MISSING-ID");
        if(current.inMemoryRunning&&current.inMemoryJobId)
          setJobId(current.inMemoryJobId);
      })
      .catch(e=>{
        if(active&&belongs(mapId)&&revision===actionRevision.current){
          setReconcileFailed(true);
          setError("A Restart-feladat vagy napló natív állapota nem olvasható ("+
            (e instanceof Error?e.message:"ismeretlen")+
            "). Új Restart helyett ellenőrizd az állapotot.");
        }
      }).finally(()=>{
        if(active&&belongs(mapId)&&revision===actionRevision.current)
          setReconciling(false);
      });
    return ()=>{active=false;};
  },[mapId,reconcileNonce]);
  useEffect(()=>{
    if(!jobId)return;
    let active=true;
    const poll=async()=>{
      try{
        const state=await managerBridge.freshAsaRestartStatus(jobId);
        if(!active||!belongs(mapId))return;
        if(state.jobId!==jobId||state.killPerformed!==false||
           state.storeTouched!==false||state.autoRetry!==false||
           (state.state==="completed"&&(!Number.isSafeInteger(state.newPid)||
             state.newPid<=0||!Number.isSafeInteger(state.oldPid)||
             state.oldPid<=0))){
          setReconcileFailed(true);
          setError("UM-ASA-RESTART-STATUS-INVALID: A natív Restart jobazonosítója vagy új PID-je nem igazolt. A régi műveletet nem ismételjük meg.");
          return;
        }
        setReconcileFailed(false);setError("");setStatus(state);
        if(state.state==="completed"){
          setJobId(null);
          const summary=state.newProcessRole==="loaderParent"?
            "ASA API Loaderrel végzett újraindítás: régi ASA PID "+state.oldPid+
              " → új Loader PID "+state.newPid+
              " → új ASA gyermek PID "+(state.newAsaServerPid??"nem igazolt")+
              (state.apiChildAndRconVerified?
               ". A gyermek és az RCON tulajdonlása igazolt; játékkészültség és fsync nem.":
               ". A gyermek / RCON és a játékosbelépés külön ellenőrizendő."):
            "Normál ASA újraindítás befejeződött: "+
              state.oldPid+" → "+state.newPid+
              ". A játék/RCON készenlét és a lemezre mentés független bizonyítása külön ellenőrzést igényel.";
          latestRestart.set(mapId,summary);
          setMessage(summary);
          onRestarted().catch(()=>{});
        }else if(state.state==="needsOperator"){
          setJobId(null);setNeedsLocalReview(true);
          setError("A szerver újraindítása félbeszakadt. Automatikus ismétlés nem történt. Ellenőrizd a Windows-folyamatot és a natív műveleti naplót.");
        }
      }catch{
        if(active&&belongs(mapId)){
          // Unknown status cannot release the in-progress job lock or
          // authorize a new Restart. Keep polling the same job ID.
          setError("A már elindított Restart állapota átmenetileg nem olvasható. A Manager nem ismétel meg Start/Stop műveletet.");
        }
      }
    };
    void poll();
    const timer=window.setInterval(()=>{void poll();},2500);
    return ()=>{active=false;window.clearInterval(timer);};
  },[jobId,mapId,onRestarted]);

  const pendingJournalReview=durable?.needsOperatorReview===true&&!auditConfirmed;
  const run=async()=>{
    if(mutationInFlight.current||!mapId||!running||busy||jobId||journalBlocked||
       needsLocalReview||pendingJournalReview||reconciling||reconcileFailed)return;
    mutationInFlight.current=true;
    const requested=mapId;
    setBusy(true);setError("");setMessage("");
    let applyAttempted=false;
    try{
      const proof=await managerBridge.freshAsaRestartPreview(requested);
      if(!belongs(requested))return;
      if(proof.mapId.toLowerCase()!==requested.toLowerCase()||!proof.token||
         !Number.isSafeInteger(proof.currentPid)||proof.currentPid<=0||
         !proof.saveWorldBeforeExit||!proof.verifiesOldProcessExit||
         proof.autoRetry||proof.killPerformed||proof.storeTouched)
        throw new Error("UM-ASA-RESTART-PREVIEW-MAP-OR-SCOPE-MISMATCH");
      ++actionRevision.current;
      applyAttempted=true;
      const job=await managerBridge.freshAsaRestartApply(proof.token);
      if(!belongs(requested))return;
      if(!job.started||typeof job.jobId!=="string"||
         !/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(job.jobId)||
         job.autoRetry!==false||job.killPerformed!==false||
         job.storeTouched!==false||job.evidence!=="REAL_LOCAL_ASA_RESTART_JOB_QUEUED")
        throw new Error("UM-ASA-RESTART-APPLY-RECEIPT-INVALID");
      setJobId(job.jobId);
      setStatus({jobId:job.jobId,state:"queued",stage:"preflight",
        oldPid:proof.currentPid,newPid:0,changedUtc:new Date().toISOString(),
        saveCompleteProven:false,readinessCertified:false,
        preExitSaveEvidenceObserved:false,finalDoExitSaveCertified:false,
        gameClientRejoinVerified:false,
        killPerformed:false,storeTouched:false,autoRetry:false});
    }catch(e){
      if(belongs(requested)){
        if(applyAttempted)setReconcileFailed(true);
        setError(applyAttempted?
          "UM-ASA-RESTART-APPLY-UNKNOWN: A Stop/Start már elindulhatott. "+
            "Ne ismételd meg, ellenőrizd a natív műveleti naplót.":
          "A kiválasztott map Restart előellenőrzése elutasítva ("+
            (e instanceof Error?e.message:"UM-ASA-RESTART-PREFLIGHT-FAILED")+
            "). Nem ment ki SaveWorld, DoExit vagy Start.");
      }
    }finally{
      mutationInFlight.current=false;
      if(belongs(requested))setBusy(false);
    }
  };

  // A stale Restart-v1 is recoverable only if its ORIGINAL ASA process is
  // demonstrably still running with the same PID/start and owned RCON.
  // One user click performs native preview+apply; neither step sends ASA
  // commands. No browser preference or checkbox can override the OS proof.
  const recover=async()=>{
    if(busy||jobId||reconciling||reconcileFailed||reviewInFlight.current)return;
    const requested=mapId;
    reviewInFlight.current=true;
    setBusy(true);setError("");
    try{
      const proof=await managerBridge.freshAsaRestartReviewPreview(requested);
      if(!belongs(requested))return;
      if(proof.mapId.toLowerCase()!==mapId.toLowerCase()||
         proof.oldPid<=0||!proof.exactOldProcessVerified||
         !proof.rconOwnerVerified||!proof.previousOutcomeUnknown||
         !proof.restartWillNotReplay)
        throw new Error("UM-ASA-RESTART-REVIEW-PROOF-INCOMPLETE");
      const saved=await managerBridge.freshAsaRestartReviewApply(proof.token);
      if(!belongs(requested))return;
      if(saved.mapId.toLowerCase()!==requested.toLowerCase()||
         saved.oldPid!==proof.oldPid||
         saved.state!=="operatorReviewed"||
         !saved.previousOutcomeUnknown||!saved.originalJournalAudited||
         saved.newRestartSucceeded||saved.serverStarted||
         saved.serverStopped||saved.rconSent)
        throw new Error("UM-ASA-RESTART-REVIEW-RECEIPT-INVALID");
      // A late read-only restartCurrent response can still describe the
      // PRE-AUDIT journal. Once a verified native receipt confirms its
      // exact transition, do not resurrect the stale blocker in React.
      // Reopening the map reobserves the actual durable journal again.
      setAuditConfirmed(true);setNeedsLocalReview(false);setStatus(null);
      setDurable(previous=>previous?{...previous,
        phase:"OperatorReviewed",needsOperatorReview:false}:previous);
      const summary="Az eredeti, még futó ASA PID "+proof.oldPid+
        " igazolt. A megszakadt Restart naplója auditálva; sem Stop, "+
        "sem Start, sem RCON-parancs nem történt. Új művelet külön jóváhagyással indítható.";
      latestRestart.set(mapId,summary);
      setMessage(summary);
      try{await onRestarted();}
      catch{setError("A helyi naplórendezés kész, de az állapotfrissítés sikertelen. Ne ismételd meg az auditot.");}
    }catch(e){
      if(!belongs(requested))return;
      const code=e instanceof Error?e.message:"UM-ASA-RESTART-REVIEW-FAILED";
      const hints:Record<string,string>={
        "UM-ASA-RESTART-REVIEW-OLD-PID-NOT-RUNNING":
          "Az eredeti ASA PID vagy az RCON-tulajdonos már nem igazolható. A régi Restart nem ismételhető vakon; a napló változatlan.",
        "UM-ASA-RESTART-REVIEW-GENERAL-PENDING":
          "Másik megszakadt helyi műveleti napló is blokkol. Ezt nem lehet a Restart napló felülírásával kikerülni.",
        "UM-RESTART-REVIEW-TOO-RECENT":
          "A korábbi művelet még túl friss. Az ASA szabályos kilépése folyamatban lehet.",
        "UM-ASA-RESTART-REVIEW-SOURCE-CHANGED":
          "A régi PID, a Manager-profil vagy a tartós napló változott. Nem történt helyreállítás."
      };
      setError(hints[code]??"A korábbi Restart naplóját nem lehetett igazoltan rendezni ("+
        code+"). Nem küldtünk régi parancsot és nem indítottunk szervert.");
    }finally{
      reviewInFlight.current=false;
      if(belongs(requested))setBusy(false);
    }
  };

  const showRecovery=!jobId&&!reconciling&&!reconcileFailed&&!auditConfirmed&&
    !!(durable?.needsOperatorReview||needsLocalReview)&&
    (durable===null||durable.belongsToMap);
  return <section className="premium-panel" aria-label="Valódi ASA normál újraindítása" style={{padding:20,marginBlock:18}}>
    <header className="premium-panel-heading">
      <h2><RotateCcw size={19}/> ASA normál újraindítása</h2>
    </header>
    <p>A C# Manager a régi szerveren új, stabil .ark mentési bizonyítékot figyel, naplójelzéssel, ha az ASA naplózza. DoExit és a régi PID megszűnése után egyszer indítja el az új folyamatot. ASA API módban a Loader gyermekének és az RCON tulajdonosának ellenőrzése is szükséges. Csak saját, helyi normál vagy újonnan regisztrált Store-mapokhoz; a történeti korábbi Manager Store-mapok külön védett műveleteket használnak.</p>
    <p className="muted small">Kizárólag a kiválasztott map ({mapId}): SaveWorld → DoExit → régi PID megszűnésének natív igazolása → új indítás. Nincs Kill vagy automatikus ismétlés.</p>
    <button type="button" className="primary"
      disabled={!running||busy||!!jobId||journalBlocked||needsLocalReview||pendingJournalReview||reconciling||reconcileFailed}
      onClick={()=>{void run();}}><RotateCcw size={16}/> Szerver újraindítása</button>
    {reconciling&&<p role="status">Korábbi Restart-feladat ellenőrzése…</p>}
    {reconcileFailed&&<button type="button" className="secondary"
      onClick={()=>setReconcileNonce(n=>n+1)}>Restart állapot újraellenőrzése</button>}
    {showRecovery&&<div className="historical-preview" role="region"
      aria-label="Megszakadt helyi Restart rendezése">
      <p><b>Korábbi Restart megszakadt.</b> Régi PID: {durable?.oldPid??"ismeretlen"},
        fázis: {durable?.phase??"bizonytalan"}. A gomb kizárólag akkor rendezi a
        Manager helyi naplóját, ha az eredeti ASA még pontosan azonosítható
        és az RCON-portot is ugyanaz a PID birtokolja.</p>
      <button type="button" className="primary" disabled={busy||!!jobId}
        onClick={()=>{void recover();}}>
        {busy?"Régi ASA és napló ellenőrzése…":
          "Futó régi ASA ellenőrzése és Restart-napló rendezése"}
      </button>
      <p className="muted small">Nincs régi parancs újraküldése, automatikus újraindítás
        vagy sikeresnek minősített korábbi Restart.</p>
    </div>}
    {status&&<div className="warning-inline" role="status"><Shield size={17}/>
      <span>Újraindítás: {status.state} · {status.stage}</span>
    </div>}
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{message}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
    <p className="muted tiny">A Manager nem végez kényszerített Kill-t, nem módosít karakter- vagy Store-v1-fájlokat, és hiba után nem indít másodszor automatikusan.</p>
  </section>;
}
