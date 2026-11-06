import {useCallback,useEffect,useMemo,useState} from "react";
import {Archive,AlertTriangle,CheckCircle2,HardDrive,
  RefreshCcw,ShieldCheck,Save,RotateCcw,LockKeyhole} from "lucide-react";
import {managerBridge,type PublicMap,type WorldBackupCreated,
  type WorldBackupHistory,type WorldBackupPoint,type WorldBackupPreview,
  type WorldBackupRestored} from "./bridge";

export function WorldBackupControls({maps,selectedMap}:{maps:PublicMap[];selectedMap?:string}){
  const [chosen,setChosen]=useState(selectedMap??maps[0]?.id??"");
  const [history,setHistory]=useState<WorldBackupHistory|null>(null);
  const [pending,setPending]=useState<WorldBackupPreview|null>(null);
  const [restoreTarget,setRestoreTarget]=useState<WorldBackupPoint|null>(null);
  const [confirmName,setConfirmName]=useState("");
  const [receipt,setReceipt]=useState<WorldBackupCreated|WorldBackupRestored|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  useEffect(()=>{
    if(selectedMap)setChosen(selectedMap);
  },[selectedMap]);
  const current=useMemo(()=>maps.find(m=>m.id===chosen),[maps,chosen]);
  const reload=useCallback(async()=>{
    if(!chosen)return;
    setError("");setBusy(true);setHistory(null);setPending(null);setRestoreTarget(null);
    try{setHistory(await managerBridge.backupHistory(chosen));}
    catch{setError("A mentési előzmények nem ellenőrizhetők ezen a helyi mapon.");}
    finally{setBusy(false);}
  },[chosen]);
  useEffect(()=>{void reload();},[reload]);
  const preview=async()=>{
    if(!chosen||busy)return;
    setPending(null);setReceipt(null);setRestoreTarget(null);setConfirmName("");
    setBusy(true);setError("");
    try{setPending(await managerBridge.backupPreview(chosen));}
    catch{setError("Mentés nem indítható: igazoltan leállított helyi map és érvényes világfájl szükséges.");}
    finally{setBusy(false);}
  };
  const create=async()=>{
    if(!pending||pending.action!=="BACKUP"||busy)return;
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
    if(!chosen||busy)return;
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
       confirmName!==current.name||busy)return;
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
            onChange={e=>{setChosen(e.target.value);setReceipt(null);}}>
            {maps.map(m=><option key={m.id} value={m.id}>{m.name}
              {m.running?" · fut":" · leállítva"}</option>)}
          </select>
        </label>
        <button className="secondary" type="button" disabled={!chosen||busy}
          onClick={()=>{void reload();}}><RefreshCcw size={15}/> Előzmények frissítése</button>
        <button className="primary" type="button" disabled={!chosen||busy}
          onClick={()=>{void preview();}}><Save size={15}/> Világmentés készítése</button>
      </div>
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
          "A map visszaállítása elkészült; az előző világ megmaradt."}</b>
          <p>{"backupVerified" in receipt?
            `${receipt.files} fájl · ${receipt.characters} karakter · ${receipt.tribes} törzs · ${(receipt.bytes/1024/1024).toFixed(1)} MiB`:
            `Visszaállított pont: ${receipt.point.slice(0,8)}… · Védőmentés: ${receipt.protectiveBackup.slice(0,8)}…`}
          </p>
          <small>Ez fájlszintű mentés és visszaállítás, nem kétgépes Store-írjog-igazolás.</small>
        </div>
      </div>}
      <div className="uk-world-backup-history">
        <div className="uk-world-backup-heading"><span><Archive size={16}/> Mentési pontok</span>
          <span>{history?.points.length??0} ellenőrzött csomag</span></div>
        {history?.points.length ? history.points.map(p=><div className="uk-world-backup-line" key={p.id}>
          <span className="uk-world-backup-point-icon"><HardDrive size={17}/></span>
          <span className="uk-world-backup-point">
            <b>{new Date(p.createdUtc).toLocaleString("hu-HU")} · {p.kind==="protective"?"Védőmentés":"Kézi mentés"}</b>
            <small>{p.files} fájl · {p.characters} karakter · {p.tribes} tribe · {(p.bytes/1024/1024).toFixed(1)} MiB</small>
          </span>
          <span className="uk-world-backup-verified"><CheckCircle2 size={14}/> SHA-256</span>
          <button type="button" className="secondary" disabled={busy}
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
          <button type="button" className="danger" disabled={busy||confirmName!==current?.name}
            onClick={()=>{void restore();}}><RotateCcw size={14}/> Védett visszaállítás</button>
          <button type="button" className="secondary" disabled={busy}
            onClick={()=>{setPending(null);setRestoreTarget(null);}}>Mégsem</button>
        </div>
      </div>}
      {error&&<div role="alert" className="uk-world-backup-error"><AlertTriangle size={16}/>{error}</div>}
    </div>
  </section>;
}
