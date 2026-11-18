import {useCallback,useEffect,useMemo,useRef,useState} from "react";
import {Archive,AlertTriangle,CheckCircle2,HardDrive,
  RefreshCcw,ShieldCheck,Save,RotateCcw,LockKeyhole} from "lucide-react";
import {managerBridge,type PublicMap,type WorldBackupCreated,
  type WorldBackupHistory,type WorldBackupPoint,type WorldBackupPreview,
  type ScheduledStoppedBackupState,
  type WorldBackupRestored,type OriginalFiveMapBackupPreview,
  type OriginalFiveMapBackupReceipt,type OriginalFiveMapBackupStatus,
  type OriginalFiveMapJournalRepairPreview} from "./bridge";

function backupResourceError(error:unknown,fallback:string):string {
  const code=error instanceof Error?error.message:"";
  switch(code){
    case "UM-BACKUP-MAP-NOT-LOCAL":
      return "A kiválasztott map nem helyi, kezelhető Manager-profil.";
    case "UM-BACKUP-INVALID-MAP":
    case "UM-BACKUP-INVALID-POINT":
      return "Érvénytelen map vagy mentési pont. Frissítsd a listát.";
    case "UM-BACKUP-PATHS-OVERLAP":
      return "A visszaállítási cél átfed egy szerver telepítési könyvtárával.";
    case "UM-BACKUP-CONFIRMED-STOP-REQUIRED":
      return "A map fut, vagy leállított állapota nem igazolt. Az Obsidian nem állítja le automatikusan.";
    case "UM-BACKUP-STORE-V1-RESTORE-LOCKED":
    case "UM-BACKUP-STORE-CHARACTER-RESTORE-REQUIRES-TWO-HOST-TRANSACTION":
      return "Store-v1: az egyoldalú éles restore nem megengedett. Készíthetsz ellenőrzött visszaállítási másolatot külön mappába.";
    case "UM-BACKUP-INSUFFICIENT-RESTORE-CAPACITY":
      return "Nincs elegendő szabad tárhely a visszaállítási másolathoz.";
    case "UM-BACKUP-DIGEST-MISMATCH":
    case "UM-BACKUP-MANIFEST-OWNER-MISMATCH":
    case "UM-BACKUP-INCOMPLETE":
      return "A mentési csomag integritása vagy mapazonossága nem igazolt.";
    case "UM-BACKUP-RESTORE-RECOVERY-REQUIRED":
      return "Egy korábbi visszaállítás helyreállítási naplója ellenőrzést igényel.";
    case "UM-BACKUP-RESOURCE-FILE-LIMIT":
      return "A mentés fájlszáma túllépi a beállított maponkénti korlátot. "+
        "Ellenőrizd az Obsidian backup-resource-limits-v1.json beállítását.";
    case "UM-BACKUP-RESOURCE-BYTE-LIMIT":
      return "A mentési adatméret meghaladja a beállított maponkénti korlátot. "+
        "Módosítsd a helyi Manager mentési szabályzatát, vagy csökkentsd a mentési hatókört.";
    case "UM-BACKUP-PREVIEW-RESOURCE-POLICY-MISSING":
      return "A natív Manager mentési előnézete nem tartalmaz ellenőrzött erőforráskorlátot. "+
        "A mentés jóváhagyása tiltva marad; frissítsd együtt a felületet és a Windows Managert.";
    case "UM-BACKUP-RESOURCE-POLICY-INVALID":
    case "UM-BACKUP-RESOURCE-POLICY-NOT-FILE":
      return "Hibás Obsidian backup-resource-limits-v1.json szabályzat; "+
        "érvénytelen fájl esetén a mentés biztonsági okból tiltva marad.";
    default:return fallback+(code.startsWith("UM-")?" ("+code+")":"");
  }
}

// A stale renderer/native pairing must never display an approval button
// using guessed backup limits. In particular, omitted JSON fields must not
// crash React's render or result in unbounded copy approval.
function verifiedBackupBudget(value:{
  maxFilesPerMap:number;maxBytesPerMap:number;
  resourcePolicyCustomized:boolean;actualCopySizeRecheckedAtCreate:true
}):boolean {
  return Number.isSafeInteger(value.maxFilesPerMap)&&value.maxFilesPerMap>0&&
    Number.isSafeInteger(value.maxBytesPerMap)&&value.maxBytesPerMap>=1048576&&
    typeof value.resourcePolicyCustomized==="boolean"&&
    value.actualCopySizeRecheckedAtCreate===true;
}

// Completed native backup/restore receipts are non-secret, read-only UI state.
// Keep them during card navigation, never the one-time approval token.
const recentWorldReceipts=new Map<string,WorldBackupCreated|WorldBackupRestored>();
function retainWorldReceipt(mapId:string,receipt:WorldBackupCreated|WorldBackupRestored){
  if(!recentWorldReceipts.has(mapId)&&recentWorldReceipts.size>=128){
    const first=recentWorldReceipts.keys().next().value;
    if(first)recentWorldReceipts.delete(first);
  }
  recentWorldReceipts.set(mapId,receipt);
}

function ManagedRetention({map}:{map:PublicMap}){
  const [keep,setKeep]=useState(3);
  const [plan,setPlan]=useState<Awaited<ReturnType<typeof managerBridge.backupRetentionPreview>>|null>(null);
  const [confirm,setConfirm]=useState(false);
  const [busy,setBusy]=useState(false);
  const [notice,setNotice]=useState("");
  const flight=useRef(false);
  const valid=Number.isInteger(keep)&&keep>=1&&keep<=500;
  const reload=async()=>{
    const result=await managerBridge.backupRetentionPreview(map.id,keep);
    if(result.mapId.toLowerCase()!==map.id.toLowerCase()||result.keepLatest!==keep||
       result.evidence!=="VERIFIED_MANAGED_BACKUP_RETENTION_PREVIEW")throw new Error("UM-BACKUP-RETENTION-RECEIPT");
    setPlan(result);
  };
  const run=async(kind?:"Protect"|"Quarantine"|"Return",point?:string,protect?:boolean)=>{
    if(flight.current||!valid||kind&&(!confirm||!point||plan?.keepLatest!==keep))return;
    flight.current=true;setBusy(true);setConfirm(false);setNotice("");
    let committed=false;
    try{
      if(kind&&point){
        const result=await managerBridge.backupRetentionAction(map.id,keep,kind,point,protect);
        if(result.mapId.toLowerCase()!==map.id.toLowerCase()||result.point!==point||
           !result.completed||result.evidence!=="MANAGED_BACKUP_CATALOG_ONLY")throw new Error("UM-BACKUP-RETENTION-RECEIPT");
        committed=true;setNotice("A mentéskatalógus művelete elkészült.");
      }
      await reload();
    }catch(e){setPlan(null);setNotice(committed?"A művelet elkészült, de a katalógus frissítése sikertelen.":"A retenciós művelet nem igazolható: "+(e instanceof Error?e.message:"ismeretlen hiba"));}
    finally{flight.current=false;setBusy(false);}
  };
  return <section className="uk-world-backup-confirm" aria-label="Helyi mentések megőrzése">
    <div><b>Helyi mentések megőrzése és karanténja</b>
      <p>A legutóbbi ép mentések, védett és hibás pontok megmaradnak. Az áthelyezés külön helyi karanténba történik; nem végleges törlés és nem éles restore.</p>
      <label>Megőrzendő legutóbbi ép pontok száma <input type="number" min={1} max={500} value={keep} disabled={busy}
        onChange={e=>{setKeep(Number(e.target.value));setPlan(null);setConfirm(false);}}/></label>
      <button className="secondary" type="button" disabled={busy||!valid} onClick={()=>void run()}>Megőrzési előnézet</button>
      {plan&&<><label><input type="checkbox" checked={confirm} disabled={busy} onChange={e=>setConfirm(e.target.checked)}/> Jóváhagyom a következő kijelölt katalógusműveletet.</label>
      {plan.points.map(p=><div key={p.point} className="settings-actions">
        <span>{p.point} · {p.protected?"Védett":p.verified?(p.eligible?"Karanténjelölt":"Megőrzendő"):"Nem igazolt"}</span>
        <button type="button" className="secondary" disabled={busy||!confirm||!p.verified} onClick={()=>void run("Protect",p.point,!p.protected)}>{p.protected?"Védelem feloldása":"Védelem"}</button>
        <button type="button" className="secondary" disabled={busy||!confirm||!p.eligible} onClick={()=>void run("Quarantine",p.point)}>Karanténba</button>
      </div>)}
      {plan.quarantine.map(p=><div key={p.point} className="settings-actions"><span>{p.point} · {p.status}</span>
        <button type="button" className="secondary" disabled={busy||!confirm||p.status!=="verified-quarantine"} onClick={()=>void run("Return",p.point)}>Vissza a mentéskatalógusba</button></div>)}</>}
      {notice&&<p role="status">{notice}</p>}
    </div>
  </section>;
}

function FullLocalBackup({map}:{map:PublicMap}){
  const [points,setPoints]=useState<Array<{point:string;files:number;bytes:number;createdUtc:string}>>([]);
  const [busy,setBusy]=useState(false);
  const [notice,setNotice]=useState("");
  const flight=useRef(false);
  const mounted=useRef(true);
  useEffect(()=>{mounted.current=true;return ()=>{mounted.current=false;};},[]);
  const action=async(kind:"list"|"create"|"stage",point?:string)=>{
    if(flight.current||kind==="create"&&map.running)return;
    flight.current=true;setBusy(true);setNotice("");
    try{
      if(kind==="create"){
        const result=await managerBridge.backupFullLocalCreate(map.id);
        if(result.mapId.toLowerCase()!==map.id.toLowerCase()||
           result.sharedTransferIncluded!==false||result.databaseConsistencyCertified!==false||
           result.evidence!=="VERIFIED_STOPPED_LOCAL_INSTALLATION_FILES")throw new Error("UM-BACKUP-FULL-RECEIPT");
        if(mounted.current)setNotice("Ellenőrzött helyi mentés elkészült: "+result.files+" fájl.");
      }else if(kind==="stage"){
        if(!point)return;
        const result=await managerBridge.backupFullLocalStage(map.id,point);
        if(result.mapId.toLowerCase()!==map.id.toLowerCase()||result.point!==point||
           result.productionRestoreAllowed!==false||
           result.evidence!=="VERIFIED_NEW_LOCAL_RESTORE_FOLDER_NO_LIVE_REPLACEMENT")throw new Error("UM-BACKUP-FULL-RECEIPT");
        if(mounted.current)setNotice("Ellenőrzött visszaállítási másolat a Manager profiljában: "+result.relativeFolder);
      }
      const history=await managerBridge.backupFullLocalHistory(map.id);
      if(history.mapId.toLowerCase()!==map.id.toLowerCase()||
         history.evidence!=="VERIFIED_LOCAL_INSTALLATION_FILE_ARCHIVES")throw new Error("UM-BACKUP-FULL-HISTORY");
      if(mounted.current)setPoints(history.points);
    }catch(e){if(mounted.current)setNotice(backupResourceError(e,"A helyi mentési művelet nem igazolható. Éles restore vagy automatikus Stop nem indult."));}
    finally{flight.current=false;if(mounted.current)setBusy(false);}
  };
  return <section className="uk-world-backup-confirm" aria-label="Teljes helyi map fájlmentése">
    <div><b>Teljes helyi map fájlmentése</b>
      <p>Leállított, külön telepítésű map SavedArks/SaveGames, Game.ini, GameUserSettings.ini és meglévő ASA API/pluginfájljai. A közös transfer és a külső adatbázis konzisztenciája külön mentendő.</p>
      <button className="primary" type="button" disabled={busy||map.running||map.observedReadOnlyImported} onClick={()=>void action("create")}>Teljes helyi fájlmentés készítése</button>
      <button className="secondary" type="button" disabled={busy} onClick={()=>void action("list")}>Teljes mentések ellenőrzése</button>
      {points.map(p=><div key={p.point} className="settings-actions"><span>{new Date(p.createdUtc).toLocaleString("hu-HU")} · {p.files} fájl</span>
        <button type="button" className="secondary" disabled={busy} onClick={()=>void action("stage",p.point)}>Visszaállítás külön mappába</button></div>)}
      {notice&&<p role="status">{notice}</p>}
      <small>Nem írja felül az éles mapot, nem indít vagy állít le szervert.</small>
    </div>
  </section>;
}

export function WorldBackupControls({maps,selectedMap}:{maps:PublicMap[];selectedMap?:string}){
  const [chosen,setChosen]=useState(selectedMap??maps[0]?.id??"");
  const [history,setHistory]=useState<WorldBackupHistory|null>(null);
  const [pending,setPending]=useState<WorldBackupPreview|null>(null);
  const [restoreTarget,setRestoreTarget]=useState<WorldBackupPoint|null>(null);
  const [restoreConfirmed,setRestoreConfirmed]=useState(false);
  const [receipt,setReceipt]=useState<WorldBackupCreated|WorldBackupRestored|null>(()=>recentWorldReceipts.get(selectedMap??maps[0]?.id??"")??null);
  const [busy,setBusy]=useState(false);
  const [scheduled,setScheduled]=useState<ScheduledStoppedBackupState|null>(null);
  const [scheduleMinutes,setScheduleMinutes]=useState(120);
  const [scheduleFullLocal,setScheduleFullLocal]=useState(false);
  const [scheduleConfirm,setScheduleConfirm]=useState(false);
  const [scheduleBusy,setScheduleBusy]=useState(false);
  const [scheduleNotice,setScheduleNotice]=useState("");
  const [error,setError]=useState("");
  const [originalFivePreview,setOriginalFivePreview]=useState<OriginalFiveMapBackupPreview|null>(null);
  const [originalFiveConsent,setOriginalFiveConsent]=useState(false);
  const [originalFiveReceipt,setOriginalFiveReceipt]=useState<OriginalFiveMapBackupReceipt|null>(null);
  const [originalFiveJob,setOriginalFiveJob]=useState<OriginalFiveMapBackupStatus|null>(null);
  const lastFiveCompleted=useRef("");
  const [repairPreview,setRepairPreview]=useState<OriginalFiveMapJournalRepairPreview|null>(null);
  const [repairBusy,setRepairBusy]=useState(false);
  const [originalFiveBusy,setOriginalFiveBusy]=useState(false);
  const [originalFiveError,setOriginalFiveError]=useState("");
  const selectedRef=useRef(chosen);
  // A stale response may finish after the operator chooses another map.
  // It must never display the previous map's backup/restore approval.
  useEffect(()=>{
    if(selectedMap)setChosen(selectedMap);
  },[selectedMap]);
  useEffect(()=>{
    selectedRef.current=chosen;
    setHistory(null);setPending(null);setRestoreTarget(null);
    setRestoreConfirmed(false);setReceipt(recentWorldReceipts.get(chosen)??null);setError("");
    setScheduleConfirm(false);setScheduleNotice("");
  },[chosen]);
  const current=useMemo(()=>maps.find(m=>m.id===chosen),[maps,chosen]);
  const reloadSchedule=useCallback(async()=>{
    if(!chosen)return;
    const requestedMap=chosen;
    const answer=await managerBridge.backupScheduleStatus(requestedMap);
    if(selectedRef.current===requestedMap)setScheduled(answer);
    return answer;
  },[chosen]);
  useEffect(()=>{
    let active=true;
    setScheduled(null);setScheduleConfirm(false);setScheduleNotice("");setScheduleFullLocal(false);
    void managerBridge.backupScheduleStatus(chosen).then(result=>{
      if(active){setScheduled(result);setScheduleFullLocal(result.fullLocal===true);}
    }).catch(()=>{
      if(active)setScheduleNotice("Az automatikus mentések állapota nem olvasható.");
    });
    return ()=>{active=false;};
  },[chosen]);
  const changeSchedule=async(enable:boolean)=>{
    if(!current||!scheduleConfirm||scheduleBusy||(enable&&!(scheduled?.canSchedule??!current.storeProtected)))return;
    const requestedMap=chosen;
    setScheduleBusy(true);setScheduleNotice("");
    try{
      const result=enable
        ?await managerBridge.backupScheduleCreate(requestedMap,scheduleMinutes,scheduleFullLocal)
        :await managerBridge.backupScheduleDisable(requestedMap);
      if(selectedRef.current!==requestedMap)return;
      setScheduled(result);setScheduleConfirm(false);
      setScheduleNotice(enable?
        "Ütemezés bekapcsolva. Mentés csak igazoltan leállított, helyben azonosított mapnál készül.":
        "Az ütemezés kikapcsolva. Meglévő archívum nem törlődött.");
    }catch(e){
      if(selectedRef.current===requestedMap)setScheduleNotice("Ütemezés nem módosítható: "+
        (e instanceof Error?e.message:"natív védelem megtagadta"));
    }finally{setScheduleBusy(false);}
  };
  const reload=useCallback(async()=>{
    if(!chosen)return;
    const requestedMap=chosen;
    setError("");setBusy(true);setHistory(null);setPending(null);setRestoreTarget(null);
    try{
      const answer=await managerBridge.backupHistory(requestedMap);
      if(selectedRef.current===requestedMap)setHistory(answer);
    }catch{if(selectedRef.current===requestedMap)
      setError("A mentési előzmények nem ellenőrizhetők ezen a helyi mapon.");}
    finally{if(selectedRef.current===requestedMap)setBusy(false);}
  },[chosen]);
  useEffect(()=>{void reload();},[reload]);
  // The native copy runs independently of WebView RPCs. Reattach to the
  // current in-process job after navigating away and report real Core points.
  useEffect(()=>{
    let mounted=true;
    const poll=()=>{
      void managerBridge.backupOriginalFiveStatus().then(status=>{
        if(!mounted)return;
        setOriginalFiveJob(status);
        if(status.state==="completed"&&status.localMaps===5&&
           status.allFiveVerified&&status.completedMaps===5&&
           status.points.length===5&&lastFiveCompleted.current!==status.jobId){
          lastFiveCompleted.current=status.jobId;
          setOriginalFiveReceipt({...status,allFiveVerified:true});
          void reload();
        }
      }).catch(()=>{/* No original backup job exists on this Manager yet. */});
    };
    poll();
    const timer=window.setInterval(poll,2500);
    return ()=>{mounted=false;window.clearInterval(timer);};
  },[reload]);
  const preview=async()=>{
    if(!chosen||busy||current?.running||history?.recoveryRequired)return;
    const requestedMap=chosen;
    setPending(null);setReceipt(null);setRestoreTarget(null);setRestoreConfirmed(false);
    setBusy(true);setError("");
    try{
      const ticket=await managerBridge.backupPreview(requestedMap);
      if(!verifiedBackupBudget(ticket))
        throw new Error("UM-BACKUP-PREVIEW-RESOURCE-POLICY-MISSING");
      if(selectedRef.current===requestedMap && ticket.mapId===requestedMap)setPending(ticket);
    }catch(e){if(selectedRef.current===requestedMap)
      setError(backupResourceError(e,
        "Mentés nem indítható: igazoltan leállított helyi map és érvényes világfájl szükséges."));}
    finally{if(selectedRef.current===requestedMap)setBusy(false);}
  };
  const create=async()=>{
    if(!pending||pending.action!=="BACKUP"||busy||current?.running||history?.recoveryRequired||pending.mapId!==chosen)return;
    setBusy(true);setError("");
    const token=pending.token;const requestedMap=chosen;setPending(null);
    try{
      const result=await managerBridge.backupCreate(token);
      if(result.mapId!==requestedMap || !result.backupVerified)
        throw new Error("UM-BACKUP-COMMIT-RECEIPT-INVALID");
      retainWorldReceipt(requestedMap,result);
      if(selectedRef.current!==requestedMap)return;
      setReceipt(result);
      // The destructive/expensive native operation is already complete.
      // A read-only history refresh failure cannot turn that into a failed
      // backup or prompt another mutating apply using a consumed token.
      try{
        const nextHistory=await managerBridge.backupHistory(requestedMap);
        if(selectedRef.current===requestedMap)setHistory(nextHistory);
      }catch{
        if(selectedRef.current===requestedMap)
          setError("A világmentés sikeresen elkészült és ellenőrzött, de az előzmények frissítése sikertelen. Ne indítsd el újra a mentést; frissítsd csak a listát.");
      }
    }catch(e){
      if(selectedRef.current===requestedMap)
        setError(backupResourceError(e,
          "A mentési kérelem eredménye nem igazolt. Ellenőrizd a mentésnaplót és az előzményeket, mielőtt új műveletet indítasz."));
    }finally{if(selectedRef.current===requestedMap)setBusy(false);}
  };
  const restorePreview=async(point:WorldBackupPoint)=>{
    if(!chosen||busy||current?.running||current?.storeProtected||history?.recoveryRequired)return;
    setPending(null);setRestoreTarget(null);setRestoreConfirmed(false);setReceipt(null);
    setBusy(true);setError("");
    const requestedMap=chosen;
    try{
      const ticket=await managerBridge.backupRestorePreview(requestedMap,point.id);
      if(!verifiedBackupBudget(ticket))
        throw new Error("UM-BACKUP-PREVIEW-RESOURCE-POLICY-MISSING");
      if(selectedRef.current===requestedMap && ticket.mapId===requestedMap){
        setPending(ticket);setRestoreTarget(point);
      }
    }catch(e){
      if(selectedRef.current===requestedMap)
        setError(backupResourceError(e,"A visszaállítás előellenőrzése sikertelen."));
    }finally{if(selectedRef.current===requestedMap)setBusy(false);}
  };
  const stageWorld=async(p:WorldBackupPoint)=>{
    if(busy||!current||current.observedReadOnlyImported)return;
    const requestedMap=current.id;
    setBusy(true);setError("");
    try{
      const result=await managerBridge.backupWorldStage(requestedMap,p.id);
      if(result.mapId.toLowerCase()!==requestedMap.toLowerCase()||
         result.point.toLowerCase()!==p.id.toLowerCase()||result.productionRestoreAllowed!==false||
         result.storeWriterTouched!==false||result.evidence!=="VERIFIED_ISOLATED_WORLD_BACKUP_RESTORE")
        throw new Error("UM-BACKUP-STAGE-RECEIPT");
      if(selectedRef.current===requestedMap)setError("Ellenőrzött visszaállítási másolat elkészült: "+result.files+" fájl. Manager profil: "+result.relativeFolder+". Éles világ nem módosult.");
    }catch(e){if(selectedRef.current===requestedMap)setError(backupResourceError(e,"Az elkülönített visszaállítás nem sikerült."));}
    finally{if(selectedRef.current===requestedMap)setBusy(false);}
  };
  const restore=async()=>{
    if(!pending||pending.action!=="RESTORE"||!current||
       !restoreConfirmed||busy||current.running||current.storeProtected||history?.recoveryRequired||pending.mapId!==chosen)return;
    const token=pending.token;const requestedMap=chosen;
    setPending(null);setRestoreTarget(null);
    setBusy(true);setError("");
    try{
      const result=await managerBridge.backupRestore(token,current.name);
      if(result.mapId!==requestedMap || !result.restored || !result.bytesRestoredAndHashesVerified || !result.oldWorldRetained)
        throw new Error("UM-BACKUP-RESTORE-RECEIPT-INVALID");
      retainWorldReceipt(requestedMap,result);
      if(selectedRef.current!==requestedMap)return;
      setReceipt(result);setRestoreConfirmed(false);
      // Once Core has committed a verified restore, only refresh the view.
      // Do not represent a failed list request as a failed world restore.
      try{
        const nextHistory=await managerBridge.backupHistory(requestedMap);
        if(selectedRef.current===requestedMap)setHistory(nextHistory);
      }catch{
        if(selectedRef.current===requestedMap)
          setError("A világfájlok visszaállítása és SHA-256 ellenőrzése sikeres, de a mentéslista frissítése sikertelen. Az ASA-beli betöltés még nem igazolt; a Restore műveletet ne ismételd meg.");
      }
    }catch{
      if(selectedRef.current===requestedMap)
        setError("A visszaállítás eredménye nem igazolt. Ellenőrizd a Manager naplóját és a védőmentést; ne ismételd meg automatikusan és ne indítsd el a mapot.");
    }finally{if(selectedRef.current===requestedMap)setBusy(false);}
  };
  const originalLocalFive=maps.filter(m=>m.storeProtected);
  const originalFiveRunning=originalFiveJob?.state==="queued"||
    originalFiveJob?.state==="running";
  const repairJournalPreview=async()=>{
    if(repairBusy||originalFiveRunning)return;
    setRepairBusy(true);setOriginalFiveError("");setRepairPreview(null);
    try{
      setRepairPreview(await managerBridge.backupOriginalFiveJournalRepairPreview());
      
    }catch{
      setOriginalFiveError("A napló felülvizsgálata nem indítható. "+
        "Az eredeti öt mapnak igazoltan leállítva kell lennie és a eredeti forrásnak "+
        "változatlannak. A korábbi mentéseket nem töröljük.");
    }finally{setRepairBusy(false);}
  };
  const repairJournalApply=async()=>{
    if(!repairPreview||repairBusy)return;
    const token=repairPreview.token;
    setRepairPreview(null);setRepairBusy(true);setOriginalFiveError("");
    try{
      const receipt=await managerBridge.backupOriginalFiveJournalRepairApply(
        token,"REVIEW FIVE BACKUPS");
      if(!receipt.reviewed||!receipt.quarantined||
         !receipt.oldBackupPackagesPreserved)
        throw new Error("Naplókarantén nincs igazolva");
      setOriginalFiveJob(null);setOriginalFiveReceipt(null);
      
      await reload();
    }catch{
      setOriginalFiveError("A sérült helyi Manager-napló elkülönítése nem sikerült. "+
        "A mentések és az eredeti Store-adatok megmaradtak.");
    }finally{setRepairBusy(false);}
  };
  const reviewOriginalFive=async()=>{
    if(originalFiveBusy||originalFiveRunning||busy||originalLocalFive.length<5)return;
    setOriginalFiveBusy(true);setOriginalFivePreview(null);
    setOriginalFiveReceipt(null);setOriginalFiveConsent(false);
    setOriginalFiveError("");
    try{
      const candidate=await managerBridge.backupOriginalFivePreview();
      if(!verifiedBackupBudget(candidate))
        throw new Error("UM-BACKUP-PREVIEW-RESOURCE-POLICY-MISSING");
      if(candidate.localMaps!==5||!candidate.originalSourcePinned||
         !candidate.allMapsConfirmedStopped||candidate.originalKovaFilesWritten||
         candidate.serverStarted||candidate.sharedTransferIncluded||
         candidate.otherHostIncluded)
        throw new Error("Eredeti helyi Store-forrás nem érvényes");
      setOriginalFivePreview(candidate);
    }catch(e){
      setOriginalFiveError("Az eredeti ötmapos mentés nem készíthető elő. "+
        "Mind az öt importált mapnak igazoltan leállítva kell lennie, "+
        "változatlan korábbi kezelőalkalmazás metaadatokkal és érvényes világfájlokkal. "+
        (e instanceof Error?e.message:""));
    }finally{setOriginalFiveBusy(false);}
  };
  const createOriginalFive=async()=>{
    if(originalFiveBusy||originalFiveRunning||!originalFivePreview||!originalFiveConsent)return;
    const token=originalFivePreview.token;
    setOriginalFiveBusy(true);setOriginalFivePreview(null);
    setOriginalFiveConsent(false);setOriginalFiveError("");
    try{
      const started=await managerBridge.backupOriginalFiveCreate(token);
      if(!started.started||started.localMaps!==5||
         started.originalKovaFilesWritten||started.serverStarted||
         started.sharedTransferIncluded||started.otherHostIncluded)
        throw new Error("A natív ötmapos mentési feladatot nem sikerült elindítani");
      const status=await managerBridge.backupOriginalFiveStatus(started.jobId);
      if(status.jobId!==started.jobId)
        throw new Error("A mentés műveletazonosítója nem egyezik");
      setOriginalFiveJob(status);
      // A queued response is not success: only the five independently
      // SHA-verified Core packages may produce a completed receipt.
    }catch(e){
      setOriginalFiveError("Nem igazolható mind az öt helyi mentési pont. "+
        "Ellenőrizd az egyes mapok mentési előzményeit. "+
        "Korábbi, már ellenőrzött mentéseket nem töröltünk. "+
        (e instanceof Error?e.message:""));
    }finally{setOriginalFiveBusy(false);}
  };
  return <section className="uk-world-backup premium-panel">
    <header className="premium-panel-heading">
      <h2><ShieldCheck size={19}/> Valódi világmentés és védett visszaállítás</h2>
      <span className="premium-small-muted">OBSIDIAN MINTA · SHA-256 · ATOMIKUS</span>
    </header>
    <div className="uk-world-backup-inner">
      <div className="uk-world-backup-toolbar">
        <label>Helyi map
          <select aria-label="Mentendő helyi map"
            value={chosen} disabled={busy||Boolean(selectedMap)}
            onChange={e=>{selectedRef.current=e.target.value;setChosen(e.target.value);
              setReceipt(null);setPending(null);setRestoreTarget(null);setRestoreConfirmed(false);}}>
            {maps.map(m=><option key={m.id} value={m.id}>{m.name}
              {m.running?" · fut":" · leállítva"}</option>)}
          </select>
        </label>
        <button className="secondary" type="button" disabled={!chosen||busy}
          onClick={()=>{void reload();}}><RefreshCcw size={15}/> Előzmények frissítése</button>
        <button className="primary" type="button" disabled={!chosen||busy||current?.running||history?.recoveryRequired}
          onClick={()=>{void preview();}}><Save size={15}/> Világmentés készítése</button>
      </div>
      {current&&<FullLocalBackup key={current.id} map={current}/>}
      {current&&!current.observedReadOnlyImported&&<ManagedRetention key={current.id} map={current}/>}
      {originalLocalFive.length>=5&&<section className="uk-world-backup-confirm"
        aria-label="Eredeti Store-v1 ötmapos helyi mentés">
        <div>
          <b>Az öt eredeti helyi Store-v1 map mentése együtt</b>
          <p>A már működő, SHA-256 ellenőrzött mapmentési motor mind az öt
            eredeti helyi mapról mentést készít. Nem indít vagy állít le
            szervert, nem küld RCON-t, és nem módosít eredeti sessiont, lockot
            vagy a közös transferkönyvtárat. A másik gépen külön futtasd.</p>
          {!originalFivePreview&&<button type="button" className="secondary"
            disabled={originalFiveBusy||originalFiveRunning||busy}
            onClick={()=>{void reviewOriginalFive();}}>
            <ShieldCheck size={15}/>
            {originalFiveBusy?"Eredeti mentés ellenőrzése vagy készítése…":
              "Mind az 5 eredeti helyi map mentésének ellenőrzése"}
          </button>}
          {originalFivePreview&&<div role="region"
            aria-label="Eredeti Store-v1 ötmapos mentés jóváhagyása">
            <p><b>Mind az öt map leállított és a forrásváltozat ellenőrzött.</b></p>
            <p>{originalFivePreview.mapNames.join(", ")}</p>
            <p className="muted small">A világfájlok összes mérete legalább
              {" "}{(originalFivePreview.worldBytes/1024/1024/1024).toFixed(2)} GiB.
              A teljes mentés tartalmazhat további profil- és törzsfájlokat.</p>
            <p className="muted small">Erőforráskorlát maponként:
              {" "}{originalFivePreview.maxFilesPerMap.toLocaleString("hu-HU")} fájl,
              {" "}{(originalFivePreview.maxBytesPerMap/1024/1024/1024).toFixed(0)} GiB.
              {originalFivePreview.resourcePolicyCustomized?
                " Egyedi, ellenőrzött Obsidian-szabály.":" Alapértelmezett Obsidian-szabály."}
              {" "}A pontos fájlszámot és méretet a Core másolás előtt újból ellenőrzi;
              a kétgépes csomag korlátai külön értendők.</p>
            <label><input type="checkbox" checked={originalFiveConsent}
              disabled={originalFiveBusy}
              onChange={e=>setOriginalFiveConsent(e.target.checked)}/>
              Jóváhagyom az öt eredeti helyi map SHA-256-os mentését. Ez nem
              a teljes kétgépes transfer-pillanatkép.</label>
            <div className="settings-actions">
              <button className="primary" type="button"
                disabled={originalFiveBusy||originalFiveRunning||!originalFiveConsent}
                onClick={()=>{void createOriginalFive();}}>
                <Save size={15}/> Öt map mentésének indítása
              </button>
              <button className="secondary" type="button"
                disabled={originalFiveBusy}
                onClick={()=>{setOriginalFivePreview(null);setOriginalFiveConsent(false);}}>
                Mégsem
              </button>
            </div>
          </div>}
          {originalFiveJob&&originalFiveJob.state!=="completed"&&
            <div role="status" aria-label="Eredeti ötmapos mentés állapota">
              <b>{originalFiveJob.state==="needsOperator"
                ?"Az ötmapos mentés megszakadt – ellenőrzés szükséges."
                :"Az ötmapos mentés folyamatban van."}</b>
              <p>{originalFiveJob.completedMaps}/5 ellenőrzött világmentés ·
                {" "}{originalFiveJob.step}</p>
              {originalFiveJob.points.length>0&&
                <p>{originalFiveJob.points.map(p=>p.mapName).join(", ")}</p>}
              {originalFiveJob.state==="needsOperator"&&
                <p role="alert">A már sikeres mapmentések megmaradtak a helyi előzményekben.
                  A korábbi kezelőalkalmazás Store/session/transfer fájlok változatlanok.
                  Ellenőrizd a mapok leállított állapotát és a mentési előzményeket.</p>}
              {originalFiveJob.step==="journal-integrity-repair-required"&&
                <button type="button" className="secondary" disabled={repairBusy}
                  onClick={()=>{void repairJournalPreview();}}>
                  Sérült helyi Manager-napló elkülönítésének előnézete
                </button>}
            </div>}
          {repairPreview&&<div role="region"
            aria-label="Sérült mentési napló jóváhagyása"
            className="uk-world-backup-confirm">
              <p><b>Csak a Manager saját sérült műveleti naplójának
                elkülönítése.</b> Az eredeti ASA/korábbi kezelőalkalmazás világ, karakter, session,
                lock és transfer változatlan marad. Eltérő korábbi mentési
                pontok nem kerülnek törlésre.</p>
              <p>A Manager sérült naplójának ellenőrzött elkülönítése a
                 következő külön jóváhagyó gombbal indítható.</p>
               <div className="settings-actions">
                <button type="button" className="primary"
                  disabled={repairBusy}
                  onClick={()=>{void repairJournalApply();}}>
                  Sérült napló elkülönítése
                </button>
                <button type="button" className="secondary" disabled={repairBusy}
                  onClick={()=>{setRepairPreview(null);}}>Mégsem</button>
              </div>
            </div>}
          {originalFiveReceipt&&<div role="status">
            <b>Mind az öt eredeti helyi map ellenőrzött mentése elkészült.</b>
            <p>{originalFiveReceipt.points.map(p=>p.mapName).join(", ")}</p>
            <small>{(originalFiveReceipt.totalBytes/1024/1024/1024).toFixed(2)}
              {" "}GiB SHA-256-tal ellenőrzött fájl. A közös transfer és
              a másik Manager mentése még külön szükséges.</small>
          </div>}
          {originalFiveError&&<p role="alert">{originalFiveError}</p>}
        </div>
      </section>}
      <section className="uk-world-backup-confirm" aria-label="Ütemezett világmentés">
        <div>
          <b>Automatikus mentés – csak leállított helyi mapról</b>
          <p>Az engedélyezett időpontban a Manager ellenőrzi, hogy az ASA map igazoltan leállt-e.
            Futó vagy bizonytalan állapotú mapot kihagy; nem küld SaveWorld/RCON parancsot, nem állítja le,
            nem indítja újra és nem állítja vissza a szervert. Átvett Store-v1 mapnál a helyi, jóváhagyott forrásazonosság is szükséges. Választható világmentés vagy teljes helyi fájlmentés (világ, INI, meglévő API/pluginfájlok); egyik sem teljes clustermentés.
            A mentések külön SHA-256 archívumba kerülnek, automatikus törlés nélkül.</p>
          {scheduled&&<p role="status">
            Állapot: <b>{scheduled.enabled?"Bekapcsolva":"Kikapcsolva"}</b>
            {" · "}{scheduled.fullLocal?"Teljes helyi fájlmentés":"Világmentés"}{" · "}{scheduled.lastStatus}
            {scheduled.nextDueUtc?" · Következő: "+new Date(scheduled.nextDueUtc).toLocaleString("hu-HU"):""}
            {scheduled.lastPoint?" · Utolsó pont: "+scheduled.lastPoint.slice(0,8)+"…":""}
            {" · Motor: "}{scheduled.runnerState}
          </p>}
          <label><input type="checkbox" aria-label="Teljes helyi mentés ütemezése"
            checked={scheduleFullLocal} disabled={scheduleBusy||!scheduled}
            onChange={e=>{setScheduleFullLocal(e.target.checked);setScheduleConfirm(false);}}/>
            Teljes helyi fájlmentés (világ, INI, meglévő API/pluginfájlok)</label>
          <label>Ismétlés percben (60–10080)
            <input aria-label="Automatikus mentés időköze percben" type="number"
              min={60} max={10080} step={30} value={scheduleMinutes}
              disabled={scheduleBusy||!(scheduled?.canSchedule??!current?.storeProtected)}
              onChange={e=>setScheduleMinutes(Number(e.target.value))}/>
          </label>
          <label><input aria-label="Automatikus mentés jóváhagyása" type="checkbox"
            checked={scheduleConfirm} disabled={scheduleBusy}
            onChange={e=>setScheduleConfirm(e.target.checked)}/>
            Kifejezetten jóváhagyom ennek a helyi mapnak az ütemezését vagy kikapcsolását.</label>
          {scheduleNotice&&<p role="status">{scheduleNotice}</p>}
        </div>
        <button type="button" className="primary" disabled={scheduleBusy||!scheduleConfirm||
          !scheduled||!(scheduled.canSchedule??!current?.storeProtected)||!Number.isInteger(scheduleMinutes)||
          scheduleMinutes<60||scheduleMinutes>10080}
          onClick={()=>{void changeSchedule(true);}}>Mentés ütemezése</button>
        <button type="button" className="secondary" disabled={scheduleBusy||!scheduleConfirm||!scheduled?.enabled}
          onClick={()=>{void changeSchedule(false);}}>Ütemezés kikapcsolása</button>
        <button type="button" className="secondary" disabled={scheduleBusy}
          onClick={()=>{void reloadSchedule().catch(()=>setScheduleNotice("Az ütemezés nem olvasható."));}}>Állapot frissítése</button>
      </section>
      {history?.recoveryRequired&&<div className="uk-world-backup-error" role="alert"><AlertTriangle size={19}/>
        Megszakadt világ-visszaállítás jelzője található. Ne indítsd a mapot és
        ne készíts új mentést, amíg a megőrzött előző világ és a védőmentés
        ellenőrzése meg nem történt. A Manager nem írja felül automatikusan.</div>}
      {current?.storeProtected&&<div className="uk-world-backup-notice"><LockKeyhole size={18}/>
        Store-v1 vagy történeti Store-kapcsolat észlelve: a mentések olvashatók,
        de a map egyoldalú visszaállítása zárolva van. A másik gép karakteradatait
        nem írhatjuk felül.</div>}
      {current?.running&&<div className="uk-world-backup-notice"><LockKeyhole size={18}/>
        A map jelenleg fut. A teljes, karakter- és törzsadatokat is tartalmazó
        konzisztens másolathoz előbb szabályosan állítsd le, és ellenőrizd a mentés elkészültét.</div>}
      {pending?.action==="BACKUP"&&<div role="region" aria-label="Világmentés jóváhagyása"
          className="uk-world-backup-confirm">
        <div><b>{pending.mapName}</b> · {pending.mapKey}
          <p>Az eredeti fájlok változatlanok maradnak. Külön, SHA-256-tal
            ellenőrzött mentéscsomag készül a helyi Manager profilban.</p>
          <p className="muted small">Mentési felső korlát:
            {" "}{pending.maxFilesPerMap.toLocaleString("hu-HU")} fájl,
            {" "}{(pending.maxBytesPerMap/1024/1024/1024).toFixed(0)} GiB.
            {" "}{pending.resourcePolicyCustomized?"Egyedi":"Alapértelmezett"} beállítás.
            A Core a teljes karakter-/tribe-adatmennyiséget újra ellenőrzi.
            A korlát az Obsidian helyi profiljának
            {" "}<code>backup-resource-limits-v1.json</code> fájljával csökkenthető.</p>
        </div>
        <button type="button" className="primary" disabled={busy}
          onClick={()=>{void create();}}><CheckCircle2 size={15}/> Mentés indítása</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>setPending(null)}>Mégsem</button>
      </div>}
      {receipt&&<div role="status" className="uk-world-backup-success">
        <CheckCircle2 size={20}/>
        <div><b>{"backupVerified" in receipt?
          "A világmentés elkészült és a fájlok SHA-256 ellenőrzése sikeres.":
          "A visszaállított fájlok SHA-256 szerint egyeznek; ASA játékbeli betöltés még nincs igazolva."}</b>
          <p>{"backupVerified" in receipt?
            `${receipt.files} fájl · ${receipt.characters} külön profil · ${receipt.tribes} külön tribe fájl · ${(receipt.bytes/1024/1024).toFixed(1)} MiB`:
            `Visszaállított pont: ${receipt.point.slice(0,8)}… · Védőmentés: ${receipt.protectiveBackup.slice(0,8)}…`}
          </p>
          <small>{"backupVerified" in receipt&&receipt.embeddedSurvivorCountUnknown?
            "Store-v1: a karakterek és tribe-adatok a .ark fájlban lehetnek; a külön fájlok száma nem játékosszám.":
            "SHA-256-fájlellenőrzés történt; valódi ASA visszatöltés, karakter-belépés és Store-írójog nincs igazolva."}</small>
        </div>
      </div>}
      <div className="uk-world-backup-history">
        <div className="uk-world-backup-heading"><span><Archive size={16}/> Mentési pontok</span>
          <span>{history?.points.length??0} ellenőrzött csomag</span></div>
        {history?.points.length ? history.points.map(p=><div className="uk-world-backup-line" key={p.id}>
          <span className="uk-world-backup-point-icon"><HardDrive size={17}/></span>
          <span className="uk-world-backup-point">
            <b>{new Date(p.createdUtc).toLocaleString("hu-HU")} · {p.kind==="protective"?"Védőmentés":p.id===scheduled?.lastPoint?"Ütemezett mentés":"Kézi mentés"}</b>
            <small>{p.files} fájl · {p.characters} külön profil · {p.tribes} külön tribe fájl · {(p.bytes/1024/1024).toFixed(1)} MiB</small>
          </span>
          <span className="uk-world-backup-verified"><CheckCircle2 size={14}/> SHA-256</span>
          <button type="button" className="secondary" disabled={busy||current?.running||current?.storeProtected||history?.recoveryRequired}
            title={current?.storeProtected?"Store-v1 map: egyoldalú visszaállítás tiltva":"Védett visszaállítás"}
            onClick={()=>{void restorePreview(p);}}><RotateCcw size={14}/> Visszaállítás</button>
          <button type="button" className="secondary" disabled={busy||current?.observedReadOnlyImported}
            title="Csak a mentésből másol külön helyi mappába, az éles maphoz nem nyúl"
            onClick={()=>{void stageWorld(p);}}><Archive size={14}/> Másolat külön mappába</button>
        </div>):
          <div className="uk-world-backup-empty">Még nincs ellenőrzött mentési pont ezen a mapon.</div>}
      </div>
      {pending?.action==="RESTORE"&&<div role="region" aria-label="Védett világvisszaállítás"
          className="uk-world-backup-restore">
        <div><AlertTriangle size={20}/><b>A visszaállítás a világfájlokat módosítja!</b></div>
        <p>A teljes jelenlegi világ előbb védőmentésre kerül, majd az eredeti
          mappa megőrzésével történik a csere. A karakterutazáshoz csatlakozó
          aktív Store-v1 mapokon ez a művelet tiltott.</p>
        <p>Kiválasztott pont: {restoreTarget?.createdUtc?
          new Date(restoreTarget.createdUtc).toLocaleString("hu-HU"):"—"}</p>
        <label><input type="checkbox" checked={restoreConfirmed} disabled={busy}
             onChange={e=>setRestoreConfirmed(e.target.checked)}/>
             Jóváhagyom a kiválasztott <b>{current?.name??"ismeretlen"}</b>
             map világfájljainak védett visszaállítását.</label>
         <div className="uk-world-backup-restore-actions">
          <button type="button" className="danger" disabled={busy||current?.storeProtected||current?.running||history?.recoveryRequired||!restoreConfirmed}
            onClick={()=>{void restore();}}><RotateCcw size={14}/> Védett visszaállítás</button>
          <button type="button" className="secondary" disabled={busy}
            onClick={()=>{setPending(null);setRestoreTarget(null);}}>Mégsem</button>
        </div>
      </div>}
      {error&&<div role="alert" className="uk-world-backup-error"><AlertTriangle size={16}/>{error}</div>}
    </div>
  </section>;
}
