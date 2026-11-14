import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Shield,RotateCw} from "lucide-react";
import {managerBridge,type HistoricalRestartPreview,
  type HistoricalRestartStatus,type HistoricalRestartRecovery} from "./bridge";

/** Native original ACTIVE Store-v1 Restart; no WebView arguments or RCON bytes. */
export function HistoricalStoreRestartPanel({mapId,running,onRestarted}:{
  mapId:string;running:boolean;onRestarted:()=>Promise<void>
}){
  const [recovery,setRecovery]=useState<HistoricalRestartRecovery|null>(null);
  const [checked,setChecked]=useState(false);
  const [preview,setPreview]=useState<HistoricalRestartPreview|null>(null);
  const [phrase,setPhrase]=useState("");
  const [busy,setBusy]=useState(false);
  const [jobId,setJobId]=useState("");
  const [job,setJob]=useState<HistoricalRestartStatus|null>(null);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const finished=useRef(false);
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const belongs=(requested:string)=>currentMap.current===requested;
  useEffect(()=>{
    let alive=true;
    finished.current=false;setChecked(false);setRecovery(null);
    setPreview(null);setPhrase("");setBusy(false);setJobId("");setJob(null);
    setError("");setNotice("");
    managerBridge.historicalRestartRecovery(mapId).then(result=>{
      if(!alive||!belongs(mapId))return;
      if(result.mapId.toLowerCase()!==mapId.toLowerCase())
        throw new Error("UM-STORE-RESTART-RECOVERY-WRONG-MAP");
      setChecked(true);setRecovery(result);
      if(result.inMemoryRunning&&result.jobId){
        setJobId(result.jobId);
        setNotice("A meglévő eredeti Store-v1 újraindítás állapotához visszacsatlakozva; nincs új RCON-küldés.");
      }else if(result.requiresOperator||result.blocked){
        setError("Befejezetlen Store-v1 újraindítás vagy másik natív művelet: "+
          "a PID és a helyi napló kézi ellenőrzéséig nincs új indítás.");
      }
    }).catch(()=>{
      if(alive&&belongs(mapId)){setChecked(true);setError("A helyi Store-v1 újraindítási napló nem ellenőrizhető; újraindítás tiltva.");}
    });
    return ()=>{alive=false;};
  },[mapId]);
  useEffect(()=>{
    if(!jobId)return;
    let alive=true,polling=false;
    const poll=async()=>{
      if(polling||finished.current)return;
      polling=true;
      try{
        const result=await managerBridge.historicalRestartStatus(jobId);
        if(!alive||!belongs(mapId))return;
        if(result.mapId.toLowerCase()!==mapId.toLowerCase())
          throw new Error("Wrong native map");
        setJob(result);
        if(result.state==="completed"&&!finished.current){
          finished.current=true;
          setNotice("Új eredeti ASA PID: "+result.newPid+
            ". A régi folyamat kilépése és az előző mentésjelzés ellenőrizve. "+
            "A játékbeli csatlakozás és karakterutazás külön igazolandó.");
          try{setRecovery(await managerBridge.historicalRestartRecovery(mapId));}
          catch{/* No inferred unlock when native recovery is unavailable. */}
          try{await onRestarted();}
          catch{
            if(alive&&belongs(mapId))
              setError("Az új ASA PID natív visszaigazolása megvan, de a dashboard nem frissült; nem indítjuk újra a műveletet.");
          }
        }else if(result.state==="needsOperator"&&!finished.current){
          finished.current=true;
          setError("A Store-v1 Restart megszakadt. Nincs automatikus ismétlés. "+
            "Ellenőrizd az eredeti ASA PID-et és az újraindítási naplót.");
        }
      }catch{
        if(alive&&belongs(mapId))setError("A natív Store-v1 Restart állapota nem olvasható; ne indítsd újra.");
      }finally{polling=false;}
    };
    void poll();
    const interval=window.setInterval(()=>{void poll();},1800);
    return ()=>{alive=false;window.clearInterval(interval);};
  },[jobId,mapId,onRestarted]);
  const inspect=async()=>{
    if(busy||!running||!checked||!recovery||recovery.blocked||
       recovery.inMemoryRunning||jobId)return;
    const requested=mapId;
    setBusy(true);setError("");setNotice("");setPreview(null);setPhrase("");
    try{
      const result=await managerBridge.historicalRestartPreview(requested);
      if(!belongs(requested))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-STORE-RESTART-PREVIEW-MAP-MISMATCH");
      setPreview(result);
    }catch{
      if(belongs(requested))setError("A helyi Store-v1 ASA PID/RCON, indítási konfiguráció vagy műveleti napló nem igazolható. Nem küldtünk RCON-parancsot.");
    }finally{if(belongs(requested))setBusy(false);}
  };
  const apply=async()=>{
    if(busy||!preview||preview.mapId.toLowerCase()!==mapId.toLowerCase()||
       phrase!=="STORE-V1 SAVEWORLD DOEXIT START")return;
    const requested=mapId,token=preview.token;
    setPreview(null);setPhrase("");setBusy(true);setError("");setNotice("");
    try{
      const result=await managerBridge.historicalRestartApply(
        token,"STORE-V1 SAVEWORLD DOEXIT START");
      if(!belongs(requested))return;
      if(!result.jobId)throw new Error("UM-STORE-RESTART-JOB-MISSING");
      setJobId(result.jobId);
      setNotice("Store-v1 Restart elindult. SaveWorld/DoExit mentésigazolás és régi PID-kilépés nélkül nem indít új ASA-t.");
    }catch{
      if(belongs(requested))setError("A Restart eredménye nem igazolt; a háttérfeladat már elindulhatott. Ne indítsd újra, ellenőrizd a naplót.");
    }finally{if(belongs(requested))setBusy(false);}
  };
  return <section className="glass panel historical-store-restart"
    aria-label="Eredeti Store-v1 manuális újraindítás">
    <div className="panel-head">
      <h2><RotateCw size={18}/> Eredeti Store-v1 · Restart</h2>
      <span className="mod-tag amber"><Shield size={13}/> Egyszeri manuális jóváhagyás</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">SaveWorld → új ASA mentésnapló és stabil .ark →
        DoExit → a régi Windows PID kilépése → az eredeti ACTIVE -usestore
        indítás újbóli ellenőrzése → új helyi ASA PID. Az opcionális ASA API Loader
        módnál a gyermek ASA PID és RCON-tulajdonlás külön igazolandó.
        A kétgépes Store session/lock változatlan. Nincs automatikus újrapróbálás vagy konverzió.</p>
      <button type="button" className="secondary"
        disabled={busy||!running||!checked||!recovery||recovery.blocked||
          recovery.inMemoryRunning||!!jobId||!!preview||!!error}
        onClick={()=>{void inspect();}}>
        <Shield size={16}/> Store-v1 újraindítás előellenőrzése
      </button>
      {recovery?.blocked&&<div className="warning-inline" role="status">
        <AlertTriangle size={17}/> Tartós újraindítási vagy másik műveleti napló:
        {recovery.phase} · automatikus folytatás tiltva.
      </div>}
      {preview&&<div className="warning-inline" role="region"
        aria-label="Store-v1 újraindítás jóváhagyása">
        <AlertTriangle size={18}/>
        <div>
          <b>{preview.mapName} · régi PID {preview.pid} · RCON {preview.rconPort} · {preview.launchMode==="asaApiLoader"?"ASA API Loader":"Közvetlen ASA"}</b>
          <p>Ez valódi leállítás, majd új ASA-indítás. Az indítás új előellenőrzése
            a leállítás után is meghiúsulhat, ekkor kézi beavatkozás szükséges.
            Pontosan írd be: STORE-V1 SAVEWORLD DOEXIT START</p>
          <input aria-label="Store-v1 újraindítás jóváhagyó szöveg"
            value={phrase} spellCheck={false} autoComplete="off" maxLength={40}
            onChange={e=>setPhrase(e.target.value)}/>
          <div className="settings-actions">
            <button type="button" className="primary"
              disabled={busy||phrase!=="STORE-V1 SAVEWORLD DOEXIT START"}
              onClick={()=>{void apply();}}><RotateCw size={16}/> Store-v1 Restart végrehajtása</button>
            <button type="button" className="secondary" disabled={busy}
              onClick={()=>{setPreview(null);setPhrase("");}}>Mégse</button>
          </div>
        </div>
      </div>}
      {job&&<div className="details-line" role="status">
        <span><RefreshCw size={15}/> {job.state==="running"?"Folyamatban":
          job.state==="completed"?"Új folyamat elindult":
          job.state==="needsOperator"?"Kézi ellenőrzés szükséges":"Sorban áll"}</span>
        <b>{job.step}</b>
      </div>}
      {job?.state==="completed"&&<div className="success-inline">
        <CheckCircle2 size={17}/> Régi PID {job.oldPid} → új PID {job.newPid}
      </div>}
      {notice&&<div className="success-inline" role="status">
        <CheckCircle2 size={17}/>{notice}</div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={17}/>{error}</div>}
      <div className="warning-inline"><Shield size={16}/>
        A Windows Process.Start önmagában nem bizonyít kész szervert,
        megőrzött karaktert, tribe-ot, inventoryt vagy tribute-utazást.
      </div>
    </div>
  </section>;
}
