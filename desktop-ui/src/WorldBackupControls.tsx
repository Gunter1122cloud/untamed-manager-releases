import {useCallback,useEffect,useMemo,useRef,useState} from "react";
import {Archive,AlertTriangle,CheckCircle2,HardDrive,
  RefreshCcw,ShieldCheck,Save,RotateCcw,LockKeyhole} from "lucide-react";
import {managerBridge,type PublicMap,type WorldBackupCreated,
  type WorldBackupHistory,type WorldBackupPoint,type WorldBackupPreview,
  type ScheduledStoppedBackupState,
  type WorldBackupRestored,type OriginalFiveMapBackupPreview,
  type OriginalFiveMapBackupReceipt,type OriginalFiveMapBackupStatus} from "./bridge";

export function WorldBackupControls({maps,selectedMap}:{maps:PublicMap[];selectedMap?:string}){
  const [chosen,setChosen]=useState(selectedMap??maps[0]?.id??"");
  const [history,setHistory]=useState<WorldBackupHistory|null>(null);
  const [pending,setPending]=useState<WorldBackupPreview|null>(null);
  const [restoreTarget,setRestoreTarget]=useState<WorldBackupPoint|null>(null);
  const [confirmName,setConfirmName]=useState("");
  const [receipt,setReceipt]=useState<WorldBackupCreated|WorldBackupRestored|null>(null);
  const [busy,setBusy]=useState(false);
  const [scheduled,setScheduled]=useState<ScheduledStoppedBackupState|null>(null);
  const [scheduleMinutes,setScheduleMinutes]=useState(120);
  const [scheduleConfirm,setScheduleConfirm]=useState(false);
  const [scheduleBusy,setScheduleBusy]=useState(false);
  const [scheduleNotice,setScheduleNotice]=useState("");
  const [error,setError]=useState("");
  const [originalFivePreview,setOriginalFivePreview]=useState<OriginalFiveMapBackupPreview|null>(null);
  const [originalFiveConsent,setOriginalFiveConsent]=useState(false);
  const [originalFiveReceipt,setOriginalFiveReceipt]=useState<OriginalFiveMapBackupReceipt|null>(null);
  const [originalFiveJob,setOriginalFiveJob]=useState<OriginalFiveMapBackupStatus|null>(null);
  const lastFiveCompleted=useRef("");
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
    setConfirmName("");setReceipt(null);setError("");
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
    setScheduled(null);setScheduleConfirm(false);setScheduleNotice("");
    void managerBridge.backupScheduleStatus(chosen).then(result=>{
      if(active)setScheduled(result);
    }).catch(()=>{
      if(active)setScheduleNotice("Az automatikus mentések állapota nem olvasható.");
    });
    return ()=>{active=false;};
  },[chosen]);
  const changeSchedule=async(enable:boolean)=>{
    if(!current||!scheduleConfirm||scheduleBusy)return;
    const requestedMap=chosen;
    setScheduleBusy(true);setScheduleNotice("");
    try{
      const result=enable
        ?await managerBridge.backupScheduleCreate(requestedMap,scheduleMinutes)
        :await managerBridge.backupScheduleDisable(requestedMap);
      if(selectedRef.current!==requestedMap)return;
      setScheduled(result);setScheduleConfirm(false);
      setScheduleNotice(enable?
        "Ütemezés bekapcsolva. Mentés csak igazoltan leállított, nem Store-v1 mapnál készül.":
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
    setPending(null);setReceipt(null);setRestoreTarget(null);setConfirmName("");
    setBusy(true);setError("");
    try{
      const ticket=await managerBridge.backupPreview(requestedMap);
      if(selectedRef.current===requestedMap && ticket.mapId===requestedMap)setPending(ticket);
    }catch{if(selectedRef.current===requestedMap)
      setError("Mentés nem indítható: igazoltan leállított helyi map és érvényes világfájl szükséges.");}
    finally{if(selectedRef.current===requestedMap)setBusy(false);}
  };
  const create=async()=>{
    if(!pending||pending.action!=="BACKUP"||busy||current?.running||history?.recoveryRequired||pending.mapId!==chosen)return;
    setBusy(true);setError("");
    const token=pending.token;const requestedMap=chosen;setPending(null);
    try{
      const result=await managerBridge.backupCreate(token);
      if(selectedRef.current!==requestedMap || result.mapId!==requestedMap)return;
      setReceipt(result);
      const nextHistory=await managerBridge.backupHistory(requestedMap);
      if(selectedRef.current===requestedMap)setHistory(nextHistory);
    }catch{
      if(selectedRef.current===requestedMap)
        setError("A mentés nem készült el ellenőrzötten; az esetleges .pending csomag nem állítható vissza.");
    }finally{if(selectedRef.current===requestedMap)setBusy(false);}
  };
  const restorePreview=async(point:WorldBackupPoint)=>{
    if(!chosen||busy||current?.running||current?.storeProtected||history?.recoveryRequired)return;
    setPending(null);setRestoreTarget(null);setConfirmName("");setReceipt(null);
    setBusy(true);setError("");
    const requestedMap=chosen;
    try{
      const ticket=await managerBridge.backupRestorePreview(requestedMap,point.id);
      if(selectedRef.current===requestedMap && ticket.mapId===requestedMap){
        setPending(ticket);setRestoreTarget(point);
      }
    }catch{
      if(selectedRef.current===requestedMap)
        setError("Visszaállítás zárolva: a mapnak leállítva kell lennie, a mentésnek ellenőrzöttnek. A Store-v1 karakterútvonal nem írható felül.");
    }finally{if(selectedRef.current===requestedMap)setBusy(false);}
  };
  const restore=async()=>{
    if(!pending||pending.action!=="RESTORE"||!current||
       confirmName!==current.name||busy||current.running||current.storeProtected||history?.recoveryRequired||pending.mapId!==chosen)return;
    const token=pending.token;const requestedMap=chosen;
    setPending(null);setRestoreTarget(null);
    setBusy(true);setError("");
    try{
      const result=await managerBridge.backupRestore(token,confirmName);
      if(selectedRef.current!==requestedMap || result.mapId!==requestedMap)return;
      setReceipt(result);
      const nextHistory=await managerBridge.backupHistory(requestedMap);
      if(selectedRef.current===requestedMap)setHistory(nextHistory);
      if(selectedRef.current===requestedMap)setConfirmName("");
    }catch{
      if(selectedRef.current===requestedMap)
        setError("A világ visszaállítása elakadt. Ellenőrizd a Manager eseménynaplóját és a megőrzött védőmentést; ne indítsd el automatikusan a mapot.");
    }finally{if(selectedRef.current===requestedMap)setBusy(false);}
  };
  const originalLocalFive=maps.filter(m=>m.storeProtected);
  const originalFiveRunning=originalFiveJob?.state==="queued"||
    originalFiveJob?.state==="running";
  const reviewOriginalFive=async()=>{
    if(originalFiveBusy||originalFiveRunning||busy||originalLocalFive.length<5)return;
    setOriginalFiveBusy(true);setOriginalFivePreview(null);
    setOriginalFiveReceipt(null);setOriginalFiveConsent(false);
    setOriginalFiveError("");
    try{
      const candidate=await managerBridge.backupOriginalFivePreview();
      if(candidate.localMaps!==5||!candidate.originalSourcePinned||
         !candidate.allMapsConfirmedStopped||candidate.originalKovaFilesWritten||
         candidate.serverStarted||candidate.sharedTransferIncluded||
         candidate.otherHostIncluded)
        throw new Error("Eredeti helyi Store-forrás nem érvényes");
      setOriginalFivePreview(candidate);
    }catch(e){
      setOriginalFiveError("Az eredeti ötmapos mentés nem készíthető elő. "+
        "Mind az öt importált mapnak igazoltan leállítva kell lennie, "+
        "változatlan Kova metaadatokkal és érvényes világfájlokkal. "+
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
      <span className="premium-small-muted">KOVA-MINTA · SHA-256 · ATOMIKUS</span>
    </header>
    <div className="uk-world-backup-inner">
      <div className="uk-world-backup-toolbar">
        <label>Helyi map
          <select aria-label="Mentendő helyi map"
            value={chosen} disabled={busy||Boolean(selectedMap)}
            onChange={e=>{selectedRef.current=e.target.value;setChosen(e.target.value);
              setReceipt(null);setPending(null);setRestoreTarget(null);setConfirmName("");}}>
            {maps.map(m=><option key={m.id} value={m.id}>{m.name}
              {m.running?" · fut":" · leállítva"}</option>)}
          </select>
        </label>
        <button className="secondary" type="button" disabled={!chosen||busy}
          onClick={()=>{void reload();}}><RefreshCcw size={15}/> Előzmények frissítése</button>
        <button className="primary" type="button" disabled={!chosen||busy||current?.running||history?.recoveryRequired}
          onClick={()=>{void preview();}}><Save size={15}/> Világmentés készítése</button>
      </div>
      {originalLocalFive.length>=5&&<section className="uk-world-backup-confirm"
        aria-label="Eredeti Store-v1 ötmapos helyi mentés">
        <div>
          <b>Az öt eredeti helyi Store-v1 map mentése együtt</b>
          <p>A már működő, SHA-256 ellenőrzött mapmentési motor mind az öt
            eredeti helyi mapról mentést készít. Nem indít vagy állít le
            szervert, nem küld RCON-t, és nem módosít Kova-sessiont, lockot
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
                  A Kova Store/session/transfer fájlok változatlanok.
                  Ellenőrizd a mapok leállított állapotát és a mentési előzményeket.</p>}
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
            nem indítja újra és nem állítja vissza a szervert. Store-v1 mapokra nem engedélyezhető.
            A mentések külön SHA-256 archívumba kerülnek, automatikus törlés nélkül.</p>
          {scheduled&&<p role="status">
            Állapot: <b>{scheduled.enabled?"Bekapcsolva":"Kikapcsolva"}</b>
            {" · "}{scheduled.lastStatus}
            {scheduled.nextDueUtc?" · Következő: "+new Date(scheduled.nextDueUtc).toLocaleString("hu-HU"):""}
            {scheduled.lastPoint?" · Utolsó pont: "+scheduled.lastPoint.slice(0,8)+"…":""}
            {" · Motor: "}{scheduled.runnerState}
          </p>}
          <label>Ismétlés percben (60–10080)
            <input aria-label="Automatikus mentés időköze percben" type="number"
              min={60} max={10080} step={30} value={scheduleMinutes}
              disabled={scheduleBusy||Boolean(current?.storeProtected)}
              onChange={e=>setScheduleMinutes(Number(e.target.value))}/>
          </label>
          <label><input aria-label="Automatikus mentés jóváhagyása" type="checkbox"
            checked={scheduleConfirm} disabled={scheduleBusy}
            onChange={e=>setScheduleConfirm(e.target.checked)}/>
            Kifejezetten jóváhagyom ennek a helyi mapnak az ütemezését vagy kikapcsolását.</label>
          {scheduleNotice&&<p role="status">{scheduleNotice}</p>}
        </div>
        <button type="button" className="primary" disabled={scheduleBusy||!scheduleConfirm||
          !scheduled||Boolean(current?.storeProtected)||!Number.isInteger(scheduleMinutes)||
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
        <label>Írd be pontosan a map nevét a jóváhagyáshoz:
          <input aria-label="Visszaállítás mapnév megerősítés"
            value={confirmName} onChange={e=>setConfirmName(e.target.value)}
            placeholder={current?.name??""}/>
        </label>
        <div className="uk-world-backup-restore-actions">
          <button type="button" className="danger" disabled={busy||current?.storeProtected||current?.running||history?.recoveryRequired||confirmName!==current?.name}
            onClick={()=>{void restore();}}><RotateCcw size={14}/> Védett visszaállítás</button>
          <button type="button" className="secondary" disabled={busy}
            onClick={()=>{setPending(null);setRestoreTarget(null);}}>Mégsem</button>
        </div>
      </div>}
      {error&&<div role="alert" className="uk-world-backup-error"><AlertTriangle size={16}/>{error}</div>}
    </div>
  </section>;
}
