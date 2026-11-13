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
  useEffect(()=>{
    let active=true;
    finished.current=false;
    setChecked(false);setRecovery(null);setPreview(null);setPhrase("");
    setReviewPreview(null);setReviewPhrase("");
    setJobId("");setJob(null);setNotice("");setError("");
    setDurableSave(null);setDurableUnknown(false);setDurableChecked(false);
    managerBridge.historicalSaveWorldEvidence(mapId).then(result=>{
      if(active){setDurableSave(result);setDurableChecked(true);}
    }).catch(()=>{
      if(active){setDurableUnknown(true);setDurableChecked(true);}
    });
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
          try{
            setRecovery(await managerBridge.historicalGracefulStopRecovery(mapId));
            const receipt=await managerBridge.historicalSaveWorldEvidence(mapId);
            setDurableSave(receipt);
            if(receipt.phase!=="completed"||!receipt.localSaveCompleteObserved||
               receipt.productionRestoreAllowed||receipt.bothHostsSaveCompleteCertified)
              throw new Error("SaveComplete receipt is missing or over-certified");
            setNotice("Helyi SaveComplete napló tartósan igazolt: új ASA naplójelzés és stabil .ark. "+
              "A szerver tovább fut; fsync és játékbeli karakterutazás külön igazolandó.");
            await onSaved();
          }catch{
            setDurableUnknown(true);
            setError("A művelet után a tartós helyi SaveComplete bizonyíték nem igazolható. "+
              "Ne ismételd meg a SaveWorld parancsot; kézi ellenőrzés szükséges.");
          }
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
    if(busy||!checked||!durableChecked||durableUnknown||
       durableSave?.phase==="prepared"||durableSave?.phase==="needsOperator"||
       !recovery||recovery.blocked||!running||jobId)return;
    setBusy(true);setPreview(null);setPhrase("");setError("");setNotice("");
    try{setPreview(await managerBridge.historicalSaveWorldPreview(mapId));}
    catch{setError("A helyi Store-v1 profil, PID, RCON-port vagy forrásadat nem ellenőrizhető. SaveWorld parancs nem indult; a másik host régi korábbi Manager-jelölője önálló Obsidian-profilnál nem előfeltétel.");}
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
  const reviewPreviousSave=async()=>{
    if(busy||running||!durableChecked||durableUnknown||
       !durableSave||!["prepared","needsOperator","reviewed"].includes(
         durableSave.phase))return;
    setBusy(true);setReviewPreview(null);setReviewPhrase("");
    setError("");setNotice("");
    try{setReviewPreview(await managerBridge.historicalSaveWorldReviewPreview(mapId));}
    catch{setError("A korábbi SaveWorld nem feloldható: az ASA leállítása, "+
      "eredeti Store-session/forrás és tartós napló újraellenőrzése szükséges.");}
    finally{setBusy(false);}
  };
  const approveReview=async()=>{
    if(busy||!reviewPreview||reviewPhrase!=="REVIEW STORE SAVE")return;
    const token=reviewPreview.token;
    setBusy(true);setReviewPreview(null);setReviewPhrase("");
    setError("");setNotice("");
    try{
      const result=await managerBridge.historicalSaveWorldReviewApply(
        token,"REVIEW STORE SAVE");
      if(result.phase!=="reviewed"||!result.oldReceiptArchived||
         result.originalStoreFilesWritten||!result.nextOperationRequiresNewApproval)
        throw new Error("Native review receipt invalid");
      const [newEvidence,newRecovery]=await Promise.all([
        managerBridge.historicalSaveWorldEvidence(mapId),
        managerBridge.historicalGracefulStopRecovery(mapId)
      ]);
      setDurableSave(newEvidence);setRecovery(newRecovery);
      if(newRecovery.blocked)throw new Error("Other map operation still pending");
      setNotice("A korábbi bizonytalan helyi SaveWorld tartósan archiválva, "+
        "a műveleti napló operátori ellenőrzéssel feloldva. "+
        "A következő SaveWorld új, külön jóváhagyást igényel.");
    }catch{setError("A Store-v1 mentési naplók nem oldhatók fel biztonságosan. "+
      "Új SaveWorld nem indítható, amíg az állapot nem tisztázott.");}
    finally{setBusy(false);}
  };
  return <section className="glass panel historical-store-saveonly"
      aria-label="Store-v1 SaveWorld leállítás nélkül">
    <div className="panel-head">
      <h2><Save size={18}/> Store-v1 · SaveWorld mentés</h2>
      <span className="mod-tag green">ASA tovább fut</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">A megőrzött Store-v1 adatokat változatlanul használja;
        önálló Obsidian-profilnál a régi korábbi Manager ACTIVE/DONE peer-jelölő nem szükséges.
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
            "Önálló Obsidian helyi mentés: nem szükséges a távoli régi korábbi Manager peer-jelölő."}</p>
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
                  disabled={busy||reviewPhrase!=="REVIEW STORE SAVE"}
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
