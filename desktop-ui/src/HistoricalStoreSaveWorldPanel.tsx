import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Save,Shield} from "lucide-react";
import {managerBridge,type HistoricalSaveWorldPreview,
  type HistoricalGracefulStopRecovery,type HistoricalGracefulStopStatus,
  type HistoricalSaveEvidenceStatus,type HistoricalSaveReviewPreview} from "./bridge";

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
  const [phrase,setPhrase]=useState("");
  const [reviewPreview,setReviewPreview]=useState<HistoricalSaveReviewPreview|null>(null);
  const [reviewPhrase,setReviewPhrase]=useState("");
  const [busy,setBusy]=useState(false);
  const [jobId,setJobId]=useState("");
  const [job,setJob]=useState<HistoricalGracefulStopStatus|null>(null);
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");
  const finished=useRef(false);
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const epoch=useRef(0);
  const belongs=(requested:string,revision:number)=>
    currentMap.current===requested&&epoch.current===revision;
  useEffect(()=>{
    let active=true;
    const revision=++epoch.current;
    finished.current=false;
    setChecked(false);setRecovery(null);setPreview(null);setPhrase("");setBusy(false);
    setReviewPreview(null);setReviewPhrase("");
    setJobId("");setJob(null);setNotice("");setError("");
    setDurableSave(null);setDurableUnknown(false);setDurableChecked(false);
    managerBridge.historicalSaveWorldEvidence(mapId).then(result=>{
      if(!active||!belongs(mapId,revision))return;
      if(result.mapId.toLowerCase()!==mapId.toLowerCase()||
         result.productionRestoreAllowed||result.bothHostsSaveCompleteCertified||
         result.sharedStoreWriterCertified)
        throw new Error("UM-STORE-SAVE-EVIDENCE-MAP-MISMATCH");
      setDurableSave(result);setDurableChecked(true);
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
        setError("Befejezetlen Store-v1 műveleti napló: kézi ellenőrzésig nincs új SaveWorld.");
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
  const inspect=async()=>{
    if(busy||!checked||!durableChecked||durableUnknown||
       durableSave?.phase==="prepared"||durableSave?.phase==="needsOperator"||
       !recovery||recovery.blocked||!running||jobId)return;
    const requested=mapId,revision=epoch.current;
    setBusy(true);setPreview(null);setPhrase("");setError("");setNotice("");
    try{
      const result=await managerBridge.historicalSaveWorldPreview(requested);
      if(!belongs(requested,revision))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         !result.usesSaveWorld||result.usesDoExit||result.originalStoreTouched)
        throw new Error("UM-STORE-SAVE-PREVIEW-MAP-MISMATCH");
      setPreview(result);
    }catch{
      if(belongs(requested,revision))
        setError("A kiválasztott Store-v1 map PID/RCON-ja vagy előellenőrzése nem igazolható. SaveWorld nem indult.");
    }finally{if(belongs(requested,revision))setBusy(false);}
  };
  const save=async()=>{
    if(busy||!preview||preview.mapId.toLowerCase()!==mapId.toLowerCase()||
       phrase!=="STORE-V1 SAVEWORLD")return;
    const token=preview.token,requested=mapId,revision=epoch.current;
    setPreview(null);setPhrase("");setBusy(true);setError("");
    try{
      const queued=await managerBridge.historicalSaveWorldApply(token,"STORE-V1 SAVEWORLD");
      if(!belongs(requested,revision))return;
      if(!queued.jobId||!queued.started||queued.storeTouched||
         queued.forceKill||queued.automaticRetry)
        throw new Error("UM-STORE-SAVE-QUEUED-RECEIPT-MISMATCH");
      setJobId(queued.jobId);
      setNotice("A natív SaveWorld elindult. Az ASA mentésnaplója és a .ark fájl "+
        "változása külön ellenőrzés alatt áll.");
    }catch{
      if(belongs(requested,revision))
        setError("A Store-v1 SaveWorld eredménye nem igazolható. A mentés már elindulhatott; "+
          "ne küldd újra a napló és a feladat állapotának ellenőrzése nélkül.");
    }finally{if(belongs(requested,revision))setBusy(false);}
  };
  const reviewPreviousSave=async()=>{
    if(busy||running||!durableChecked||durableUnknown||
       !durableSave||!["prepared","needsOperator","reviewed"].includes(
         durableSave.phase))return;
    const requested=mapId,revision=epoch.current;
    setBusy(true);setReviewPreview(null);setReviewPhrase("");
    setError("");setNotice("");
    try{
      const proof=await managerBridge.historicalSaveWorldReviewPreview(requested);
      if(!belongs(requested,revision))return;
      if(proof.mapId.toLowerCase()!==requested.toLowerCase()||
         proof.originalStoreFilesWritten||!proof.confirmedStopped)
        throw new Error("UM-STORE-SAVE-REVIEW-PREVIEW-MISMATCH");
      setReviewPreview(proof);
    }catch{
      if(belongs(requested,revision))
        setError("A korábbi SaveWorld nem feloldható: helyi ASA, forrás és napló ellenőrzése szükséges.");
    }finally{if(belongs(requested,revision))setBusy(false);}
  };
  const approveReview=async()=>{
    if(busy||!reviewPreview||
       reviewPreview.mapId.toLowerCase()!==mapId.toLowerCase()||
       reviewPhrase!=="REVIEW STORE SAVE")return;
    const token=reviewPreview.token,requested=mapId,revision=epoch.current;
    setBusy(true);setReviewPreview(null);setReviewPhrase("");
    setError("");setNotice("");
    try{
      const result=await managerBridge.historicalSaveWorldReviewApply(
        token,"REVIEW STORE SAVE");
      if(!belongs(requested,revision))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         result.phase!=="reviewed"||!result.oldReceiptArchived||
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
      setNotice("A korábbi bizonytalan helyi SaveWorld tartósan archiválva, "+
        "a műveleti napló operátori ellenőrzéssel feloldva. "+
        "A következő SaveWorld új, külön jóváhagyást igényel.");
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
      <button type="button" className="secondary" disabled={busy||!running||
          !checked||!durableChecked||durableUnknown||
          durableSave?.phase==="prepared"||durableSave?.phase==="needsOperator"||
          !recovery||recovery.blocked||!!preview||!!jobId||!!error}
        onClick={()=>{void inspect();}}><Shield size={16}/> Mentés előellenőrzése</button>
      {preview&&<div className="warning-inline" role="region"
          aria-label="Store-v1 SaveWorld jóváhagyása">
        <div><strong>{preview.mapName} · PID {preview.pid} · RCON {preview.rconPort}</strong>
          <p>{preview.originalPeerCheckRequiredForLocalOperation?
            "A még át nem vett régi eredeti session két peerének ellenőrzése szükséges.":
            "Önálló Obsidian helyi mentés: nem szükséges a távoli korábbi kezelőalkalmazás peer-jelölő."}</p>
          <p>Valódi RCON SaveWorld következik. Írd be pontosan: STORE-V1 SAVEWORLD</p>
          <input aria-label="Store-v1 mentés jóváhagyó szöveg" value={phrase}
            spellCheck={false} autoComplete="off" maxLength={28}
            onChange={e=>setPhrase(e.target.value)}/>
          <div className="settings-actions">
            <button type="button" className="primary"
              disabled={busy||preview.mapId.toLowerCase()!==mapId.toLowerCase()||
                phrase!=="STORE-V1 SAVEWORLD"}
              onClick={()=>{void save();}}><Save size={16}/> SaveWorld végrehajtása</button>
            <button type="button" className="secondary" disabled={busy}
              onClick={()=>{setPreview(null);setPhrase("");}}>Mégsem</button>
          </div>
        </div>
      </div>}
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
        <p>A helyi mentésnapló nem igazolja a jelenlegi Store-írójogot,
          a második gazdagép SaveComplete állapotát, az fsync-et vagy a teljes
          cluster visszaállíthatóságát. Production restore továbbra is tiltott.</p>
      </div>
      {!running&&durableChecked&&!durableUnknown&&durableSave&&
        ["prepared","needsOperator","reviewed"].includes(durableSave.phase)&&
        <div className="historical-preview" role="region"
          aria-label="Korábbi Store-v1 mentés felülvizsgálata">
          <h3>Megszakadt mentés operátori felülvizsgálata</h3>
          <p className="muted small">Csak igazoltan leállított ASA és változatlan
            eredeti Store-session esetén engedélyezett. A régi helyi
            napló másolata megmarad. Nincs automatikus SaveWorld, Start,
            Store-lock törlés vagy eredeti fájlmódosítás.</p>
          {!reviewPreview&&<button className="secondary" type="button"
            disabled={busy} onClick={()=>{void reviewPreviousSave();}}>
            <Shield size={16}/> Korábbi SaveWorld operátori ellenőrzése
          </button>}
          {reviewPreview&&<div className="warning-inline">
            <Shield size={16}/>
            <div><strong>Az eredeti map leállított állapota és forrása igazolt</strong>
              <p>Korábbi állapot: {reviewPreview.phase}.
                A napló archiválását és a következő külön jóváhagyásos
                mentés lehetőségét hagyod jóvá.</p>
              <label>Írd be pontosan: REVIEW STORE SAVE
                <input aria-label="Store-v1 mentés felülvizsgálati megerősítése"
                  value={reviewPhrase} spellCheck={false} autoComplete="off"
                  onChange={e=>setReviewPhrase(e.target.value)} maxLength={30}/>
              </label>
              <div className="settings-actions">
                <button className="primary" type="button"
                  disabled={busy||reviewPreview.mapId.toLowerCase()!==mapId.toLowerCase()||
                    reviewPhrase!=="REVIEW STORE SAVE"}
                  onClick={()=>{void approveReview();}}>
                  Korábbi mentési napló felülvizsgálatának jóváhagyása
                </button>
                <button className="secondary" type="button" disabled={busy}
                  onClick={()=>{setReviewPreview(null);setReviewPhrase("");}}>Mégsem</button>
              </div>
            </div>
          </div>}
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
