import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Save,Shield} from "lucide-react";
import {managerBridge,type HistoricalSaveWorldPreview,
  type HistoricalGracefulStopRecovery,type HistoricalGracefulStopStatus,
  type HistoricalSaveEvidenceStatus,type HistoricalSaveReviewPreview,
  type HistoricalLateSaveEvidence} from "./bridge";

// Match GUIDs from native JSON independently of N/D/braced serialization.
// A missing or invalid ID never authorizes correlation to a SaveWorld attempt.
function canonicalSaveId(value:string|null|undefined):string|null{
  if(!value)return null;
  const normalized=value.trim().replace(/[{}-]/g,"").toLowerCase();
  return /^[a-f0-9]{32}$/.test(normalized)?normalized:null;
}

/** Real, explicitly approved original ACTIVE/DONE SaveWorld. Never DoExit. */
export function HistoricalStoreSaveWorldPanel({mapId,running,onSaved}:{
  mapId:string;running:boolean;onSaved:()=>Promise<void>
}){
  const [recovery,setRecovery]=useState<HistoricalGracefulStopRecovery|null>(null);
  const [durableSave,setDurableSave]=useState<HistoricalSaveEvidenceStatus|null>(null);
  const [durableChecked,setDurableChecked]=useState(false);
  const [durableUnknown,setDurableUnknown]=useState(false);
  const [checked,setChecked]=useState(false);
  const [preview,setPreview]=useState<HistoricalSaveWorldPreview|null>(null);
  const [exportedEvidence,setExportedEvidence]=useState("");
  const [lateSave,setLateSave]=useState<HistoricalLateSaveEvidence|null>(null);
  const [busy,setBusy]=useState(false);
  const [jobId,setJobId]=useState("");
  const [job,setJob]=useState<HistoricalGracefulStopStatus|null>(null);
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");
  const finished=useRef(false);
  // Synchronous click guard, before React can commit busy=true.
  const mutationInFlight=useRef(false);
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const epoch=useRef(0);
  const belongs=(requested:string,revision:number)=>
    currentMap.current===requested&&epoch.current===revision;
  useEffect(()=>{
    let active=true;
    const revision=++epoch.current;
    finished.current=false;
    setChecked(false);setRecovery(null);setPreview(null);setBusy(false);
    setExportedEvidence("");setLateSave(null);
    setJobId("");setJob(null);setNotice("");setError("");
    setDurableSave(null);setDurableUnknown(false);setDurableChecked(false);
    managerBridge.historicalSaveWorldEvidence(mapId).then(result=>{
      if(!active||!belongs(mapId,revision))return;
      if(result.mapId.toLowerCase()!==mapId.toLowerCase()||
         result.productionRestoreAllowed||result.bothHostsSaveCompleteCertified||
         result.sharedStoreWriterCertified)
        throw new Error("UM-STORE-SAVE-EVIDENCE-MAP-MISMATCH");
      setDurableSave(result);setDurableChecked(true);
      if(result.unreadable||result.phase==="unreadable")
        {setDurableUnknown(true);setError("A Store-v1 SaveWorld-bizonylat sérült vagy félbeszakadt "+
          "(UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE). A napló változatlan; "+
          "új mentés és automatikus feloldás tiltva.");}
    }).catch(()=>{
      if(active&&belongs(mapId,revision))
        {setDurableUnknown(true);setDurableChecked(true);}
    });
    managerBridge.historicalGracefulStopRecovery(mapId).then(result=>{
      if(!active||!belongs(mapId,revision))return;
      if(result.mapId.toLowerCase()!==mapId.toLowerCase()||
         result.sharedStoreMutated||result.lockCleared)
        throw new Error("UM-STORE-SAVE-RECOVERY-MAP-MISMATCH");
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
        setError(result.operationJournalUnreadable||result.restartJournalUnreadable?
          "A helyi Operations-v1 / Restart-v1 napló nem olvasható: "+
          (result.operationJournalErrorCode??result.restartJournalErrorCode??"UM-JOURNAL-UNREADABLE")+
          ". A naplók megmaradnak; nincs új SaveWorld vagy automatikus feloldás.":
          result.saveEvidenceUnreadable?
          "Sérült vagy félbeszakadt helyi SaveWorld-bizonylat "+
          "(UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE). A map naplója megmarad; "+
          "helyreállítás csak külön operátori ellenőrzéssel lehetséges.":
          "Befejezetlen Store-v1 műveleti napló: kézi ellenőrzésig nincs új SaveWorld.");
      }
    }).catch(()=>{
      if(active&&belongs(mapId,revision))
        {setChecked(true);setError("A tartós Store-v1 műveleti napló nem olvasható; mentés tiltva.");}
    });
    return ()=>{active=false;++epoch.current;};
  },[mapId]);
  useEffect(()=>{
    if(!jobId)return;
    let active=true,polling=false;
    const revision=epoch.current,requested=mapId,expectedJob=jobId;
    const poll=async()=>{
      if(polling||finished.current||!belongs(requested,revision))return;
      polling=true;
      try{
        const result=await managerBridge.historicalSaveWorldStatus(expectedJob);
        if(!active||!belongs(requested,revision))return;
        if(result.jobId!==expectedJob||result.kind!=="SaveWorldOnly"||
           result.storeTouched||result.forceKill||result.confirmedStopped||
           result.finalDoExitSaveCertified)
          throw new Error("UM-STORE-SAVE-STATUS-RECEIPT-MISMATCH");
        setJob(result);
        if(result.state==="completed"&&!finished.current){
          finished.current=true;
          try{
            const [recovered,receipt]=await Promise.all([
              managerBridge.historicalGracefulStopRecovery(requested),
              managerBridge.historicalSaveWorldEvidence(requested)
            ]);
            if(!active||!belongs(requested,revision))return;
            if(recovered.mapId.toLowerCase()!==requested.toLowerCase()||
               recovered.sharedStoreMutated||recovered.lockCleared||
               receipt.mapId.toLowerCase()!==requested.toLowerCase()||
               receipt.phase!=="completed"||receipt.operationId!==expectedJob||
               !receipt.localSaveCompleteObserved||receipt.productionRestoreAllowed||
               receipt.bothHostsSaveCompleteCertified||receipt.sharedStoreWriterCertified)
              throw new Error("UM-STORE-SAVE-DURABLE-RECEIPT-MISMATCH");
            setRecovery(recovered);setDurableSave(receipt);setDurableUnknown(false);
            setNotice("Helyi SaveComplete napló tartósan igazolt: új ASA naplójelzés és stabil .ark. "+
              "A szerver tovább fut; fsync és játékbeli karakterutazás külön igazolandó.");
          }catch{
            if(active&&belongs(requested,revision)){
              setDurableUnknown(true);
              setError("A natív SaveWorld után a tartós helyi SaveComplete bizonyíték nem igazolható. "+
                "Ne ismételd meg a parancsot; kézi ellenőrzés szükséges.");
            }
            return;
          }
          try{await onSaved();}
          catch{
            if(active&&belongs(requested,revision))
              setError("A SaveWorld tartósan igazolt, de a szerverlista frissítése nem sikerült. "+
                "A mentési parancsot nem ismételjük meg.");
          }
        }else if(result.state==="needsOperator"&&!finished.current){
          finished.current=true;
          setError("A SaveWorld státusza bizonytalan. Az eredeti PID és napló kézi ellenőrzése "+
            "kötelező; automatikus újraküldés és feloldás nincs.");
        }
      }catch{
        if(active&&belongs(requested,revision))
          setError("A natív Store-v1 SaveWorld státusza vagy feladatazonossága nem igazolható. Ne küldd újra.");
      }
      finally{polling=false;}
    };
    void poll();
    const handle=window.setInterval(()=>{void poll();},1700);
    return ()=>{active=false;window.clearInterval(handle);};
  },[jobId,mapId,onSaved]);
  const refreshDurable=async()=>{
    // Read-only evidence refresh after the operator has inspected the
    // local journal externally. Never retry SaveWorld or clear a receipt.
    if(busy||(jobId&&job?.state!=="completed"&&job?.state!=="needsOperator"))
      return;
    const requested=mapId,revision=epoch.current;
    setBusy(true);setPreview(null);
    setError("");setNotice("");
    try{
      const [evidence,review]=await Promise.all([
        managerBridge.historicalSaveWorldEvidence(requested),
        managerBridge.historicalGracefulStopRecovery(requested)
      ]);
      if(!belongs(requested,revision))return;
      if(evidence.mapId.toLowerCase()!==requested.toLowerCase()||
         review.mapId.toLowerCase()!==requested.toLowerCase()||
         evidence.productionRestoreAllowed||
         evidence.bothHostsSaveCompleteCertified||
         evidence.sharedStoreWriterCertified||
         review.sharedStoreMutated||review.lockCleared)
        throw new Error("UM-ORIGINAL-SAVE-READONLY-MAP-MISMATCH");
      setDurableSave(evidence);setDurableChecked(true);
      setDurableUnknown(evidence.unreadable===true||evidence.phase==="unreadable");
      setRecovery(review);setChecked(true);
      // Clear the previous UI job only after matching, independent durable
      // evidence and unblocked native recovery. This never changes its
      // saved journal or reissues a command.
      const completedVerified=job?.state==="completed"&&
        evidence.phase==="completed"&&evidence.localSaveCompleteObserved&&
        evidence.operationId===jobId;
      const manuallyReviewed=(job?.state==="needsOperator"||
          job?.state==="reviewed")&&evidence.phase==="reviewed"&&
        evidence.operationId===jobId;
      if(jobId&&!review.blocked&&!evidence.unreadable&&
         (completedVerified||manuallyReviewed)){
        setJobId("");setJob(null);finished.current=false;
      }
      if(review.operationJournalUnreadable||review.restartJournalUnreadable)
        setError("Helyi műveleti napló olvasási hiba: "+
          (review.operationJournalErrorCode??review.restartJournalErrorCode??"UM-JOURNAL-UNREADABLE")+
          ". Új SaveWorld nem indult; a napló megmaradt.");
      else if(evidence.unreadable||review.saveEvidenceUnreadable)
        setError("Sérült vagy félbeszakadt SaveWorld-bizonylat "+
          "(UM-ORIGINAL-SAVE-JOURNAL-UNREADABLE). A helyi fájlok változatlanok; "+
          "automatikus feloldás nincs.");
      else if(review.blocked)
        setError("A helyi műveleti napló még függő állapotban van. "+
          "Külön operátori ellenőrzés szükséges; nem küldtünk új SaveWorld parancsot.");
      else
        setNotice("A helyi Store-v1 naplók újraellenőrizve, új SaveWorld nem indult. "+
          "Az ASA állapotának és az eredeti Store-forrásnak az új művelet előtt "+
          "ismét külön igazoltnak kell lennie.");
    }catch{
      if(belongs(requested,revision)){
        setRecovery(null);setDurableSave(null);setDurableUnknown(true);
        setChecked(true);setDurableChecked(true);
        setError("A helyi Store-v1 naplók nem ellenőrizhetők. Új mentés és "+
          "automatikus RCON-ismétlés továbbra is tiltva.");
      }
    }finally{if(belongs(requested,revision))setBusy(false);}
  };
  const inspectLateEvidence=async()=>{
    if(busy||!running||!durableChecked||!durableSave||
       !["prepared","needsOperator","reviewed"].includes(durableSave.phase)||
       recovery?.inMemoryRunning)return;
    const requested=mapId,revision=epoch.current;
    setBusy(true);setLateSave(null);setError("");
    try{
      // A review, a new save or a second Manager can replace the receipt
      // while this React panel still holds the preceding operationId.
      // Read the native receipt on BOTH sides of the late metadata request.
      // At most one read-only reconciliation is allowed; never redispatch.
      let reconciled=false;
      for(let attempt=0;attempt<2;attempt++){
        const before=await managerBridge.historicalSaveWorldEvidence(requested);
        if(!belongs(requested,revision))return;
        if(before.mapId.toLowerCase()!==requested.toLowerCase()||
           before.productionRestoreAllowed||before.bothHostsSaveCompleteCertified||
           before.sharedStoreWriterCertified)
          throw new Error("UM-ASA-SAVE-LATE-MAP-OR-AUTHORITY");
        if(before.unreadable||before.phase==="unreadable"||
           !canonicalSaveId(before.operationId)||
           !["prepared","needsOperator","reviewed"].includes(before.phase)){
          setDurableSave(before);setDurableUnknown(Boolean(before.unreadable));
          throw new Error("UM-ASA-SAVE-LATE-RECEIPT-NOT-ELIGIBLE");
        }
        const inspected=await managerBridge.historicalSaveWorldLateEvidence(requested);
        const after=await managerBridge.historicalSaveWorldEvidence(requested);
        if(!belongs(requested,revision))return;
        if(inspected.mapId.toLowerCase()!==requested.toLowerCase()||
           after.mapId.toLowerCase()!==requested.toLowerCase())
          throw new Error("UM-ASA-SAVE-LATE-MAP-MISMATCH");
        if(inspected.completionCertified||inspected.rconSent||
           inspected.grantsOperation||inspected.oldSaveWorldRetried||
           after.productionRestoreAllowed||after.sharedStoreWriterCertified||
           after.bothHostsSaveCompleteCertified)
          throw new Error("UM-ASA-SAVE-LATE-READONLY-CONTRACT");
        const stable=canonicalSaveId(before.operationId)!==null&&
          canonicalSaveId(before.operationId)===canonicalSaveId(after.operationId)&&
          canonicalSaveId(before.operationId)===canonicalSaveId(inspected.operationId)&&
          before.phase===after.phase&&before.phase===inspected.receiptPhase&&
          before.beganUtc===after.beganUtc&&
          before.beganUtc===inspected.receiptBeganUtc&&
          before.pid===after.pid&&before.pidStartedUtc===after.pidStartedUtc;
        // Always reconcile the UI's stale receipt from the durable source,
        // but never display metadata from a different operation as its result.
        setDurableSave(after);setDurableChecked(true);
        setDurableUnknown(Boolean(after.unreadable));
        if(stable){
          setLateSave(inspected);
          setNotice("A jelenlegi SaveWorld-bizonylat késői fájlállapotát csak olvasással ellenőriztem. "+
            "Az előző parancs eredménye továbbra is ISMERETLEN; új RCON nem ment ki.");
          reconciled=true;
          break;
        }
      }
      if(!reconciled)
        throw new Error("UM-ASA-SAVE-LATE-RECEIPT-CHANGED");
    }catch(e){
      if(belongs(requested,revision)){
        const code=e instanceof Error&&/^UM-ASA-SAVE-LATE-[A-Z-]+$/.test(e.message)?
          e.message:"UM-ASA-SAVE-LATE-READONLY-FAILED";
        setError("A késői mentésnyom nem kapcsolható biztonságosan ugyanahhoz a "+
          "tartós művelethez: "+code+". Nem indult SaveWorld és nincs naplófeloldás.");
      }
    }finally{if(belongs(requested,revision))setBusy(false);}
  };
  const exportUncertainReceipt=async()=>{
    if(busy||!durableChecked||!durableUnknown||recovery?.inMemoryRunning)
      return;
    const requested=mapId,revision=epoch.current;
    setBusy(true);setError("");
    try{
      const receipt=await managerBridge.historicalSaveWorldForensicExport(requested);
      if(!belongs(requested,revision))return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         !receipt.originalUntouched||receipt.sendsRcon||
         receipt.grantsRecovery||receipt.automaticRetry||
         !/^[a-f0-9-]+\.(json|next)\.bin$/i.test(receipt.fileName))
        throw new Error("UM-ORIGINAL-SAVE-FORENSIC-RECEIPT-INVALID");
      setExportedEvidence(receipt.fileName+" · SHA-256: "+receipt.sha256);
      setNotice("A sérült helyi napló egy másolata elmentve az Obsidian "+
        "saját historical-save-evidence-forensic-v1 mappájába. "+
        "Az eredeti fájl és a műveleti tiltás változatlan. Nem küldtünk RCON-t.");
    }catch(e){
      if(belongs(requested,revision))
        setError("A sérült napló bájtpontos exportja nem sikerült. "+
          "Az eredeti változatlan, nincs feloldás. "+
          (e instanceof Error?e.message:"Ellenőrizd a fájlhozzáférést."));
    }finally{if(belongs(requested,revision))setBusy(false);}
  };
  const inspect=async()=>{
    if(mutationInFlight.current||busy||!checked||!durableChecked||durableUnknown||
       durableSave?.phase==="prepared"||durableSave?.phase==="needsOperator"||
       !recovery||recovery.blocked||!running||jobId||error)return;
    mutationInFlight.current=true;
    const requested=mapId,revision=epoch.current;
    setBusy(true);setPreview(null);setError("");setNotice("");
    try{
      const result=await managerBridge.historicalSaveWorldPreview(requested);
      if(!belongs(requested,revision))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         !result.usesSaveWorld||result.usesDoExit||result.originalStoreTouched)
        throw new Error("UM-STORE-SAVE-PREVIEW-MAP-MISMATCH");
      // One click: consume this map/PID/source-bound, short-lived native
      // preflight token immediately. No second UI approval and no retry.
      await save(result);
    }catch(e){
      if(belongs(requested,revision)){
        const code=e instanceof Error?e.message:"";
        setError(code==="UM-ORIGINAL-SAVE-STARTUP-NOT-ADVERTISED"?
          "Az ASA még nem igazoltan fejezte be az indulást és nem hirdeti a szervert. SaveWorld nem indult; ellenőrizd a friss ShooterGame.log naplót és az ASA PID-et.":
          "A kiválasztott Store-v1 map PID/RCON-ja vagy előellenőrzése nem igazolható. SaveWorld nem indult.");
      }
    }finally{
      mutationInFlight.current=false;
      if(belongs(requested,revision))setBusy(false);
    }
  };
  const save=async(proof:HistoricalSaveWorldPreview)=>{
    if(!proof.token||proof.mapId.toLowerCase()!==mapId.toLowerCase())return;
    const token=proof.token,requested=mapId,revision=epoch.current;
    setPreview(null);setBusy(true);setError("");
    try{
      const queued=await managerBridge.historicalSaveWorldApply(token);
      if(!belongs(requested,revision))return;
      if(!queued.jobId||!queued.started||queued.storeTouched||
         queued.forceKill||queued.automaticRetry)
        throw new Error("UM-STORE-SAVE-QUEUED-RECEIPT-MISMATCH");
      setJobId(queued.jobId);
      setNotice("A natív SaveWorld elindult. Az ASA mentésnaplója és a .ark fájl "+
        "változása külön ellenőrzés alatt áll.");
    }catch(e){
      if(belongs(requested,revision)){
        const code=e instanceof Error?e.message:"";
        if(code==="UM-ORIGINAL-STOP-INSTALLATION-BUSY")
          setError("Másik natív művelet már zárolja ezt az ASA-telepítést. "+
            "Az új SaveWorld-kérelem elutasítva; nem küldtünk RCON-parancsot. "+
            "Ellenőrizd a műveleti naplót, majd kérj új előellenőrzést.");
        else if(code==="UM-ORIGINAL-SAVE-HISTORY-REVIEW-REQUIRED")
          setError("A korábbi SaveWorld-bizonylat archívumának megőrzése "+
            "nem igazolható. Új mentés nem indult; a helyi naplókat "+
            "kézzel ellenőrizni kell, automatikus feloldás nincs.");
        else
          setError("A Store-v1 SaveWorld eredménye nem igazolható. A mentés már elindulhatott; "+
            "ne küldd újra a napló és a feladat állapotának ellenőrzése nélkül.");
      }
    }finally{if(belongs(requested,revision))setBusy(false);}
  };
  const reviewPreviousSave=async()=>{
    if(mutationInFlight.current||busy||recovery?.inMemoryRunning||!durableChecked||durableUnknown||
       !durableSave||!["prepared","needsOperator","reviewed"].includes(
         durableSave.phase))return;
    mutationInFlight.current=true;
    const requested=mapId,revision=epoch.current;
    setBusy(true);
    setError("");setNotice("");
    try{
      const proof=await managerBridge.historicalSaveWorldReviewPreview(requested);
      if(!belongs(requested,revision))return;
      if(proof.mapId.toLowerCase()!==requested.toLowerCase()||
         proof.originalStoreFilesWritten||
         proof.confirmedStopped===proof.runningOwned||
         !proof.previousOutcomeStillUnknown)
        throw new Error("UM-STORE-SAVE-REVIEW-PREVIEW-MISMATCH");
      // Same click performs the journal-only, audited native review.
      await approveReview(proof);
    }catch{
      if(belongs(requested,revision))
        setError("A korábbi SaveWorld nem feloldható: helyi ASA, forrás és napló ellenőrzése szükséges.");
    }finally{
      mutationInFlight.current=false;
      if(belongs(requested,revision))setBusy(false);
    }
  };
  const approveReview=async(proof:HistoricalSaveReviewPreview)=>{
    if(!proof.token||proof.mapId.toLowerCase()!==mapId.toLowerCase())return;
    const token=proof.token,requested=mapId,revision=epoch.current;
    setBusy(true);
    setError("");setNotice("");
    try{
      const result=await managerBridge.historicalSaveWorldReviewApply(token);
      if(!belongs(requested,revision))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         result.phase!=="reviewed"||!result.oldReceiptArchived||
         !result.previousOutcomeStillUnknown||result.rconSent||
         result.originalStoreFilesWritten||!result.nextOperationRequiresNewApproval)
        throw new Error("UM-STORE-SAVE-REVIEW-RECEIPT-MISMATCH");
      const [newEvidence,newRecovery]=await Promise.all([
        managerBridge.historicalSaveWorldEvidence(requested),
        managerBridge.historicalGracefulStopRecovery(requested)
      ]);
      if(!belongs(requested,revision))return;
      if(newEvidence.mapId.toLowerCase()!==requested.toLowerCase()||
         newRecovery.mapId.toLowerCase()!==requested.toLowerCase()||
         newRecovery.blocked||newRecovery.sharedStoreMutated||newRecovery.lockCleared)
        throw new Error("UM-STORE-SAVE-REVIEW-STATUS-MISMATCH");
      setDurableSave(newEvidence);setRecovery(newRecovery);
      if(jobId&&newEvidence.operationId===jobId){
        setJobId("");setJob(null);finished.current=false;
      }
      setNotice("A korábbi SaveWorld kimenetele továbbra is ISMERETLEN. "+
        "A helyi mentésbizonylat és a műveleti napló változatlan előzménye "+
        "auditáltan archiválva; nem küldtünk új parancsot. "+
        "A következő SaveWorld vagy Stop új előellenőrzést és külön jóváhagyást igényel.");
    }catch{
      if(belongs(requested,revision))
        setError("A Store-v1 naplófelülvizsgálat eredménye nem igazolható. "+
          "Ne ismételd meg jóváhagyás nélkül, ellenőrizd az állapotát.");
    }finally{if(belongs(requested,revision))setBusy(false);}
  };
  return <section className="glass panel historical-store-saveonly"
      aria-label="Store-v1 SaveWorld leállítás nélkül">
    <div className="panel-head">
      <h2><Save size={18}/> Store-v1 · SaveWorld mentés</h2>
      <span className="mod-tag green">ASA tovább fut</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">A megőrzött Store-v1 adatokat változatlanul használja;
        önálló Obsidian-profilnál a korábbi kezelőalkalmazás ACTIVE/DONE peer-jelölő nem szükséges.
        Az ellenőrzött helyi ASA PID-nek egyszer küldi a SaveWorld
        parancsot. A log és világfájl ellenőrzése után sincs DoExit, konverzió vagy
        karakter-, tribe- és tribute-fájlmódosítás a Managerből.</p>
      <button type="button" className="secondary" disabled={busy||
          (!!jobId&&job?.state!=="completed"&&job?.state!=="needsOperator")}
        onClick={()=>{void refreshDurable();}}>
        <RefreshCw size={16}/> Store-v1 mentési napló újraellenőrzése
      </button>
      {running&&durableChecked&&durableSave&&
        ["prepared","needsOperator","reviewed"].includes(durableSave.phase)&&
        <div className="historical-preview" role="region"
          aria-label="Késői SaveWorld fájlbizonyíték csak olvasás">
          <p>A korábbi 90 másodperces figyelésen túl is ellenőrizhető a
            világfájl és a ShooterGame.log újabb metaadata – új parancs nélkül.
            Régi dispatch-határ hiányában ez önmagában nem SaveComplete.</p>
          <button type="button" className="secondary" disabled={busy||
              !!recovery?.inMemoryRunning}
            onClick={()=>{void inspectLateEvidence();}}>
            <RefreshCw size={16}/> Késői mentésnyomok ellenőrzése (RCON nélkül)
          </button>
          {lateSave&&<p className="muted small" role="status">
            Fájlbizonyíték: {lateSave.stage}.
            {lateSave.worldBytes!==null?" Világfájl méret: "+lateSave.worldBytes+" bájt.":""}
            {lateSave.worldWriteUtc?" Módosult: "+lateSave.worldWriteUtc+".":""}
            {lateSave.asaLogPresent?" ASA napló létezik.":" ASA napló nem igazolt."}
            A korábbi művelet továbbra is ISMERETLEN; SaveComplete nincs hitelesítve.
          </p>}
        </div>}
      {durableUnknown&&durableChecked&&
        <div className="warning-inline" role="region"
          aria-label="Sérült SaveWorld napló biztonságos exportja">
          <AlertTriangle size={16}/>
          <div>
            <p>Az ismeretlen mentési kimenetelt nem oldjuk fel.
              Ha az eredeti Manager-bizonylat olvasható bájtszinten,
              külön kattintásra készül róla helyi bizonyítékmásolat.
              A hiányzó jogosultság vagy zárolás nem jelent tartalmi sérülést.</p>
            <button type="button" className="secondary"
              disabled={busy||!!recovery?.inMemoryRunning}
              onClick={()=>{void exportUncertainReceipt();}}>
              <Shield size={16}/> Sérült helyi napló forenzikus exportja
            </button>
            {exportedEvidence&&<p className="muted small" role="status">
              Export: {exportedEvidence}
            </p>}
          </div>
        </div>}
      <p className="muted small">Egy kattintás kizárólag a kiválasztott map
        ({mapId}) SaveWorld parancsára. Nem Stop, DoExit, Store-konverzió vagy restore.
        PID, indulási idő, forrás, RCON-port és tartós napló előzetes natív
        ellenőrzése automatikusan megtörténik.</p>
      <button type="button" className="primary" disabled={busy||!running||
          !checked||!durableChecked||durableUnknown||
          durableSave?.phase==="prepared"||durableSave?.phase==="needsOperator"||
          !recovery||recovery.blocked||!!preview||!!jobId||!!error}
        onClick={()=>{void inspect();}}><Save size={16}/> SaveWorld mentés indítása</button>
      <div className="muted small" role="status" data-testid="store-durable-save-evidence">
        <strong>Helyi SaveComplete mentési bizonyíték: </strong>
        {!durableChecked?"Tartós napló ellenőrzése folyamatban":
          durableUnknown?"Tartós napló nem ellenőrizhető – további mentés igazolása tilos":
          durableSave?.phase==="completed"&&durableSave.localSaveCompleteObserved?
            "Korábbi helyi ASA naplójelzés + stabil világfájl rögzítve":
          durableSave?.phase==="prepared"||durableSave?.phase==="needsOperator"?
            "Függőben / bizonytalan – kézi ellenőrzést igényel":
          durableSave?.phase==="reviewed"?
            "Operátori felülvizsgálat megtörtént; az előző napló archiválva":
            "Nincs igazolt helyi SaveComplete napló"}
        {durableSave?.diagnosis&&durableSave.diagnosis.state!=="valid"&&
          durableSave.diagnosis.state!=="missing"&&
          <p data-testid="store-save-journal-diagnosis">
            Naplóállapot: {durableSave.diagnosis.state}.
            Kód: {durableSave.diagnosis.reasonCode}.
            {durableSave.diagnosis.byteLength!==null?
              " Bájt: "+durableSave.diagnosis.byteLength+".":""}
            {durableSave.diagnosis.sha256?
              " SHA-256: "+durableSave.diagnosis.sha256+".":""}
            Az eredeti bájtok megmaradnak; jogosultsági vagy zárolási hibánál
            nem feltételezhető tartalmi sérülés. Nincs automatikus feloldás.
          </p>}
        <p>A helyi mentésnapló nem igazolja a jelenlegi Store-írójogot,
          a második gazdagép SaveComplete állapotát, az fsync-et vagy a teljes
          cluster visszaállíthatóságát. Production restore továbbra is tiltott.</p>
      </div>
      {durableChecked&&!durableUnknown&&durableSave&&
        ["prepared","needsOperator","reviewed"].includes(durableSave.phase)&&
        <div className="historical-preview" role="region"
          aria-label="Korábbi Store-v1 mentés felülvizsgálata">
          <h3>Megszakadt mentés operátori felülvizsgálata</h3>
          <p className="muted small">Futó ASA esetén a korábbi saját
            PID, indulási idő, RCON-tulajdon, forrás és az aktív worker hiánya
            külön igazolandó; leállított ASA esetén külön stopped-proof kell.
            A korábbi mentés kimenetele ISMERETLEN marad. Az eredeti naplók
            archiválva maradnak; nincs RCON, újrapróbálás, Store-lock
            törlés vagy eredeti fájlmódosítás.</p>
          <button className="secondary" type="button"
            disabled={busy||!!recovery?.inMemoryRunning}
            onClick={()=>{void reviewPreviousSave();}}>
            <Shield size={16}/> Napló rendezése (korábbi SaveWorld, RCON nélkül)
          </button>
        </div>}
      {job&&<div className="details-line" role="status">
        <span><RefreshCw size={15}/> {job.state==="running"?"Mentés folyamatban":job.state}</span>
        <strong>{job.step}</strong>
      </div>}
      {notice&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{notice}</div>}
      {error&&<div className="warning-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
      {recovery?.blocked&&<p className="muted small">Tartós journal: {recovery.phase}.
        {recovery.saveEvidenceUnreadable?
          " A SaveWorld-bizonylat nem olvasható, a sérült napló megőrzendő; automatikus feloldás nincs.":
          " Új mutáló művelet csak operátori ellenőrzés után lehetséges."}</p>}
    </div>
  </section>;
}
