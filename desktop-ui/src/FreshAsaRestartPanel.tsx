import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,RotateCcw,Shield} from "lucide-react";
import {managerBridge,type FreshAsaRestartPreview,type FreshAsaRestartStatus,
  type AsaRestartJournalStatus} from "./bridge";

const latestRestart=new Map<string,string>();

export function FreshAsaRestartPanel({mapId,running,journalBlocked,onRestarted}:{
  mapId:string;running:boolean;journalBlocked:boolean;
  onRestarted:()=>Promise<void>
}){
  const [preview,setPreview]=useState<FreshAsaRestartPreview|null>(null);
  const [busy,setBusy]=useState(false);
  const [approved,setApproved]=useState(false);
  const [jobId,setJobId]=useState<string|null>(null);
  const [status,setStatus]=useState<FreshAsaRestartStatus|null>(null);
  const [message,setMessage]=useState(()=>latestRestart.get(mapId)??"");
  const [error,setError]=useState("");
  const [needsLocalReview,setNeedsLocalReview]=useState(false);
  const [durable,setDurable]=useState<AsaRestartJournalStatus|null>(null);
  const [auditConfirmed,setAuditConfirmed]=useState(false);
  const reviewInFlight=useRef(false);
  useEffect(()=>{
    setPreview(null);setApproved(false);setError("");
    setNeedsLocalReview(false);setDurable(null);setAuditConfirmed(false);
    setMessage(latestRestart.get(mapId)??"");
  },[mapId]);
  useEffect(()=>{
    let active=true;
    managerBridge.freshAsaRestartCurrent(mapId)
      .then(current=>{if(active)setDurable(current);})
      .catch(()=>{/* A failure is never interpreted as a completed journal. */});
    return ()=>{active=false;};
  },[mapId,needsLocalReview]);
  useEffect(()=>{
    if(!jobId)return;
    let active=true;
    const poll=async()=>{
      try{
        const state=await managerBridge.freshAsaRestartStatus(jobId);
        if(!active)return;
        setStatus(state);
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
        if(active){
          setJobId(null);setNeedsLocalReview(true);
          setError("Nem érhető el a natív újraindítási feladat. Ellenőrizd a helyi naplót új művelet előtt.");
        }
      }
    };
    void poll();
    const timer=window.setInterval(()=>{void poll();},2500);
    return ()=>{active=false;window.clearInterval(timer);};
  },[jobId,mapId,onRestarted]);

  const check=async()=>{
    if(!mapId||!running||busy||jobId||journalBlocked||needsLocalReview)return;
    setBusy(true);setError("");setMessage("");setPreview(null);setApproved(false);
    try{setPreview(await managerBridge.freshAsaRestartPreview(mapId));}
    catch{setError("Az újraindítás előellenőrzése sikertelen. Csak saját, RCON-nal azonosított, nem Store-v1 ASA folyamat engedélyezett, a Managerrel egyező GameUserSettings.ini konfigurációval.");}
    finally{setBusy(false);}
  };
  const run=async()=>{
    if(!preview||!approved||busy||jobId||journalBlocked||needsLocalReview)return;
    const token=preview.token;
    setBusy(true);setPreview(null);setApproved(false);setError("");
    try{
      const job=await managerBridge.freshAsaRestartApply(token);
      setJobId(job.jobId);
      setStatus({
        jobId:job.jobId,state:"queued",stage:"preflight",
        oldPid:preview.currentPid,newPid:0,changedUtc:new Date().toISOString(),
        saveCompleteProven:false,readinessCertified:false,
        preExitSaveEvidenceObserved:false,finalDoExitSaveCertified:false,
        gameClientRejoinVerified:false,
        killPerformed:false,storeTouched:false,autoRetry:false
      });
    }catch{setError("A helyi újraindítás nem indult el. A szerverállapot megváltozhatott.");}
    finally{setBusy(false);}
  };

  // A stale Restart-v1 is recoverable only if its ORIGINAL ASA process is
  // demonstrably still running with the same PID/start and owned RCON.
  // One user click performs native preview+apply; neither step sends ASA
  // commands. No browser preference or checkbox can override the OS proof.
  const recover=async()=>{
    if(busy||jobId||reviewInFlight.current)return;
    reviewInFlight.current=true;
    setBusy(true);setError("");
    try{
      const proof=await managerBridge.freshAsaRestartReviewPreview(mapId);
      if(proof.mapId.toLowerCase()!==mapId.toLowerCase()||
         proof.oldPid<=0||!proof.exactOldProcessVerified||
         !proof.rconOwnerVerified||!proof.previousOutcomeUnknown||
         !proof.restartWillNotReplay)
        throw new Error("UM-ASA-RESTART-REVIEW-PROOF-INCOMPLETE");
      const saved=await managerBridge.freshAsaRestartReviewApply(proof.token);
      if(saved.mapId.toLowerCase()!==mapId.toLowerCase()||
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
      reviewInFlight.current=false;setBusy(false);
    }
  };

  const showRecovery=!auditConfirmed&&
    !!(durable?.needsOperatorReview||needsLocalReview)&&
    (durable===null||durable.belongsToMap);
  return <section className="premium-panel" aria-label="Valódi ASA normál újraindítása" style={{padding:20,marginBlock:18}}>
    <header className="premium-panel-heading">
      <h2><RotateCcw size={19}/> ASA normál újraindítása</h2>
    </header>
    <p>A C# Manager a régi szerveren új, stabil .ark mentési bizonyítékot figyel, naplójelzéssel, ha az ASA naplózza. DoExit és a régi PID megszűnése után egyszer indítja el az új folyamatot. ASA API módban a Loader gyermekének és az RCON tulajdonosának ellenőrzése is szükséges. Csak saját, helyi normál vagy újonnan regisztrált Store-mapokhoz; a történeti Kova Store-mapok külön védett műveleteket használnak.</p>
    <button type="button" className="secondary"
      disabled={!running||busy||!!jobId||journalBlocked||needsLocalReview}
      onClick={()=>{void check();}}><RefreshCw size={16}/> Újraindítás előellenőrzése</button>
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
    {preview&&<div className="historical-preview" role="region" aria-label="ASA újraindítás jóváhagyása">
      <h3>{preview.mapName} – jelenlegi PID: {preview.currentPid}</h3>
      <p>A rendszer mentési parancsot küld és szabályosan kilépteti a szervert. A folyamatkilépés nem független SaveComplete-bizonyíték.</p>
      <label className="historical-approval">
        <input type="checkbox" checked={approved}
          onChange={e=>setApproved(e.target.checked)} disabled={busy}/>
        Jóváhagyom a helyi ASA szerver tényleges leállítását és újraindítását.
      </label>
      <button type="button" className="primary" disabled={!approved||busy||journalBlocked||needsLocalReview}
        onClick={()=>{void run();}}>Szerver újraindítása</button>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>setPreview(null)}>Mégsem</button>
    </div>}
    {status&&<div className="warning-inline" role="status"><Shield size={17}/>
      <span>Újraindítás: {status.state} · {status.stage}</span>
    </div>}
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{message}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
    <p className="muted tiny">A Manager nem végez kényszerített Kill-t, nem módosít karakter- vagy Store-v1-fájlokat, és hiba után nem indít másodszor automatikusan.</p>
  </section>;
}
