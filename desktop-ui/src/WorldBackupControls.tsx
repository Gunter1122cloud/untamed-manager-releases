import {useCallback,useEffect,useMemo,useState} from "react";
import {Archive,AlertTriangle,CheckCircle2,HardDrive,
  RefreshCcw,ShieldCheck,Save,RotateCcw,LockKeyhole} from "lucide-react";
import {managerBridge,type PublicMap,type WorldBackupCreated,
  type WorldBackupHistory,type WorldBackupPoint,type WorldBackupPreview,
  type ScheduledStoppedBackupState,
  type WorldBackupRestored} from "./bridge";

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
  useEffect(()=>{
    if(selectedMap)setChosen(selectedMap);
  },[selectedMap]);
  const current=useMemo(()=>maps.find(m=>m.id===chosen),[maps,chosen]);
  const reloadSchedule=useCallback(async()=>{
    if(!chosen)return;
    const answer=await managerBridge.backupScheduleStatus(chosen);
    setScheduled(answer);
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
    setScheduleBusy(true);setScheduleNotice("");
    try{
      const result=enable
        ?await managerBridge.backupScheduleCreate(chosen,scheduleMinutes)
        :await managerBridge.backupScheduleDisable(chosen);
      setScheduled(result);setScheduleConfirm(false);
      setScheduleNotice(enable?
        "Ütemezés bekapcsolva. Mentés csak igazoltan leállított, nem Store-v1 mapnál készül.":
        "Az ütemezés kikapcsolva. Meglévő archívum nem törlődött.");
    }catch(e){
      setScheduleNotice("Ütemezés nem módosítható: "+
        (e instanceof Error?e.message:"natív védelem megtagadta"));
    }finally{setScheduleBusy(false);}
  };
  const reload=useCallback(async()=>{
    if(!chosen)return;
    setError("");setBusy(true);setHistory(null);setPending(null);setRestoreTarget(null);
    try{setHistory(await managerBridge.backupHistory(chosen));}
    catch{setError("A mentési előzmények nem ellenőrizhetők ezen a helyi mapon.");}
    finally{setBusy(false);}
  },[chosen]);
  useEffect(()=>{void reload();},[reload]);
  const preview=async()=>{
    if(!chosen||busy||current?.running||history?.recoveryRequired)return;
    setPending(null);setReceipt(null);setRestoreTarget(null);setConfirmName("");
    setBusy(true);setError("");
    try{setPending(await managerBridge.backupPreview(chosen));}
    catch{setError("Mentés nem indítható: igazoltan leállított helyi map és érvényes világfájl szükséges.");}
    finally{setBusy(false);}
  };
  const create=async()=>{
    if(!pending||pending.action!=="BACKUP"||busy||current?.running||history?.recoveryRequired||pending.mapId!==chosen)return;
    setBusy(true);setError("");
    const token=pending.token;setPending(null);
    try{
      const result=await managerBridge.backupCreate(token);
      setReceipt(result);setHistory(await managerBridge.backupHistory(result.mapId));
    }catch{
      setError("A mentés nem készült el ellenőrzötten; az esetleges .pending csomag nem állítható vissza.");
    }finally{setBusy(false);}
  };
  const restorePreview=async(point:WorldBackupPoint)=>{
    if(!chosen||busy||current?.running||current?.storeProtected||history?.recoveryRequired)return;
    setPending(null);setRestoreTarget(null);setConfirmName("");setReceipt(null);
    setBusy(true);setError("");
    try{
      const ticket=await managerBridge.backupRestorePreview(chosen,point.id);
      setPending(ticket);setRestoreTarget(point);
    }catch{
      setError("Visszaállítás zárolva: a mapnak leállítva kell lennie, a mentésnek ellenőrzöttnek. A Store-v1 karakterútvonal nem írható felül.");
    }finally{setBusy(false);}
  };
  const restore=async()=>{
    if(!pending||pending.action!=="RESTORE"||!current||
       confirmName!==current.name||busy||current.running||current.storeProtected||history?.recoveryRequired||pending.mapId!==chosen)return;
    const token=pending.token;setPending(null);setRestoreTarget(null);
    setBusy(true);setError("");
    try{
      const result=await managerBridge.backupRestore(token,confirmName);
      setReceipt(result);setHistory(await managerBridge.backupHistory(result.mapId));
      setConfirmName("");
    }catch{
      setError("A világ visszaállítása elakadt. Ellenőrizd a Manager eseménynaplóját és a megőrzött védőmentést; ne indítsd el automatikusan a mapot.");
    }finally{setBusy(false);}
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
            onChange={e=>{setChosen(e.target.value);setReceipt(null);
              setPending(null);setRestoreTarget(null);setConfirmName("");}}>
            {maps.map(m=><option key={m.id} value={m.id}>{m.name}
              {m.running?" · fut":" · leállítva"}</option>)}
          </select>
        </label>
        <button className="secondary" type="button" disabled={!chosen||busy}
          onClick={()=>{void reload();}}><RefreshCcw size={15}/> Előzmények frissítése</button>
        <button className="primary" type="button" disabled={!chosen||busy||current?.running||history?.recoveryRequired}
          onClick={()=>{void preview();}}><Save size={15}/> Világmentés készítése</button>
      </div>
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
