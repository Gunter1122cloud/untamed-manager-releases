import {useCallback,useEffect,useRef,useState} from "react";
import {Archive,AlertTriangle,CheckCircle2,FolderOpen,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type OfflineAsaArchivePreview,type OfflineAsaArchiveStatus} from "./bridge";

/**
 * Uses FOUR native Windows folder selection dialogs: copied HP map packages,
 * copied Ryzen map packages, copied shared Store/transfer data, destination.
 * The browser never receives full filesystem paths, secret data or a Store writer.
 */
export function OfflineAsaArchivePanel(){
  const [prepared,setPrepared]=useState<OfflineAsaArchivePreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [jobId,setJobId]=useState("");
  const [status,setStatus]=useState<OfflineAsaArchiveStatus|null>(null);
  const [busy,setBusy]=useState(false);
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");
  const currentJob=useRef(jobId);
  currentJob.current=jobId;
  const inspect=useCallback(async(id:string)=>{
    if(currentJob.current!==id)return;
    try{
      const next=await managerBridge.offlineArchiveStatus(id);
      if(currentJob.current!==id)return;
      if(next.jobId!==id){
        setError("A kapott archívumstátusz nem a kiválasztott feladathoz tartozik. Ellenőrzés szükséges.");
        return;
      }
      setStatus(next);
    }catch{
      if(currentJob.current===id)
        setError("Az archívum műveletének állapota nem ellenőrizhető. A fájlokat ne tekintsd késznek.");
    }
  },[]);
  useEffect(()=>{
    if(!jobId || status?.state==="completed" || status?.state==="needsOperator")return;
    let active=true,inFlight=false;
    const poll=()=>{
      if(!active||inFlight||currentJob.current!==jobId)return;
      inFlight=true;
      void inspect(jobId).finally(()=>{inFlight=false;});
    };
    poll();
    const handle=window.setInterval(poll,1500);
    return ()=>{active=false;window.clearInterval(handle);};
  },[jobId,status?.state,inspect]);
  const preview=async()=>{
    if(busy||jobId)return;
    setBusy(true);setApproved(false);setPrepared(null);setError("");setNotice("");
    try{setPrepared(await managerBridge.offlineArchivePreview());}
    catch{setError("A négy helyi mappa kiválasztása megszakadt, vagy az öt-öt offline mapcsomag / Store-másolat hiányos.");}
    finally{setBusy(false);}
  };
  const apply=async()=>{
    if(busy||!prepared||!approved)return;
    const token=prepared.token;
    setBusy(true);setPrepared(null);setApproved(false);setError("");
    try{
      const started=await managerBridge.offlineArchiveApply(token);
      if(!started.started||!started.jobId||started.productionRestoreAllowed||
         started.originalStoreTouched||started.sqlUsed)
        throw new Error("UM-OFFLINE-ARCHIVE-START-RECEIPT-MISMATCH");
      setJobId(started.jobId);
      setStatus(null);
      setNotice("Az offline archívum elkészítése elkezdődött. Csak a SHA-256 ellenőrzés után jelenhet meg sikeres állapot.");
    }catch{setError("A helyi archívum műveletét a natív ellenőrzés elutasította.");}
    finally{setBusy(false);}
  };
  const finished=status?.state==="completed"||status?.state==="needsOperator";
  return <section className="premium-panel" aria-label="SQL-mentes clusterarchívum" >
    <div className="panel-head"><h3><Archive size={18}/> Teljes cluster – offline ASA archívum</h3>
      <span className="mod-tag amber">10 map · SQL nélkül</span></div>
    <p className="muted small">Öt HP- és öt Ryzen-map ellenőrzött, leállított szerverről
      előzetesen elkészített mentéscsomagjából, valamint a közös Store/transfer könyvtár
      <strong> külön helyi másolatából</strong> állít össze egységes SHA-256 archívumot.
      A négy mappát natív Windows mappaválasztóval adod meg. Az eredeti
      konfigurációkat is megőrzi, ha mindkét gép offline mapcsomagjai mellett
      külön <code>CONFIG/&lt;map-GUID&gt;/WindowsServer/</code> mappában ott vannak
      a <code>GameUserSettings.ini</code> másolatok. A <code>Game.ini</code>
      opcionális: ha valamelyik mapon nem létezik, a mentés és a tesztvisszaállítás
      az eredeti hiányát is megőrzi. A többi hiányos konfigurációt elutasítja.</p>
    <div className="warning-inline"><Shield size={17}/>
      Ez fájlintegritási archívum, nem élő többgépes pillanatkép. Nem állítja le
      vagy indítja újra a mapokat, nem nyúl az eredeti SMB/Store-hoz, és nem teszi
      lehetővé a teljes cluster automatikus éles visszaállítását.
    </div>
    {!jobId&&<button type="button" className="secondary" disabled={busy}
      onClick={()=>{void preview();}}><FolderOpen size={16}/>
      A négy offline mappa kiválasztása</button>}
    {prepared&&<div className="historical-preview" role="region"
      aria-label="SQL-mentes clusterarchívum előnézete">
      <h4>Választott offline források</h4>
      <p>HP: {prepared.hpCopies} map · {prepared.hpFolderName}</p>
      <p>Ryzen: {prepared.ryzenCopies} map · {prepared.ryzenFolderName}</p>
      <p>Store/transfer másolat: {prepared.transferFolderName}</p>
      <p>Szerverkonfigurációk: {prepared.configurationCopiesPresent?
          "Mindkét gépről mellékelve – kötelező SHA-256 ellenőrzés":
          "Nincsenek mellékelve; ez csak világ + transfer archívum"}</p>
      <p>Új csomag szülőmappája: {prepared.destinationFolderName}</p>
      <label><input type="checkbox" disabled={busy} checked={approved}
        onChange={e=>setApproved(e.target.checked)}/>
        Megerősítem, hogy ezek előre elkészített offline másolatok,
        nem élő ASA/Store könyvtárak. Az archívum nem igazol éles konzisztenciát.</label>
      <div className="draft-actions">
        <button className="primary" type="button" disabled={busy||!approved}
          onClick={()=>{void apply();}}>Offline archívum készítése</button>
        <button className="secondary" type="button" disabled={busy}
          onClick={()=>setPrepared(null)}>Mégsem</button>
      </div>
    </div>}
    {jobId&&<div className="uk-rcon-receipt" role="status">
      <strong>Archívum állapota: {status?.state??"indítás ellenőrzése"}</strong>
      <p>{status?.code??"PREPARED"}</p>
      {status?.archiveName&&<p>Helyi csomag neve: {status.archiveName}</p>}
      {status?.state==="completed"&&<p><CheckCircle2 size={16}/>
        {status.verifiedMaps} map, {status.transferFiles} transferállomány és
        {status.configurationFiles} konfigurációs állomány SHA-256-tal igazolva.</p>}
      {status?.state==="needsOperator"&&<p><AlertTriangle size={16}/>
        Az archívum nem teljes. A félbemaradt <code>.incomplete</code> mappa kézi ellenőrzést igényel.</p>}
      <button className="secondary" type="button" disabled={busy}
        onClick={()=>{void inspect(jobId);}}><RefreshCw size={15}/> Állapot ellenőrzése</button>
      {finished&&<button className="secondary" type="button"
        onClick={()=>{setJobId("");setStatus(null);setNotice("");setError("");}}>
        Új offline archívum</button>}
    </div>}
    {notice&&<p className="muted small">{notice}</p>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={16}/>{error}</div>}
  </section>;
}
