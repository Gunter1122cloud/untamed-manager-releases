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
  useEffect(()=>{
    let alive=true;
    finished.current=false;setChecked(false);setRecovery(null);
    setPreview(null);setPhrase("");setBusy(false);setJobId("");setJob(null);
    setError("");setNotice("");
    managerBridge.historicalRestartRecovery(mapId).then(result=>{
      if(!alive)return;
      setChecked(true);setRecovery(result);
      if(result.inMemoryRunning&&result.jobId){
        setJobId(result.jobId);
        setNotice("A meglévő eredeti Store-v1 újraindítás állapotához visszacsatlakozva; nincs új RCON-küldés.");
      }else if(result.requiresOperator||result.blocked){
        setError("Befejezetlen Store-v1 újraindítás vagy másik natív művelet: "+
          "a PID és a helyi napló kézi ellenőrzéséig nincs új indítás.");
      }
    }).catch(()=>{
      if(alive){setChecked(true);setError("A helyi Store-v1 újraindítási napló nem ellenőrizhető; újraindítás tiltva.");}
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
        if(!alive)return;
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
          await onRestarted();
        }else if(result.state==="needsOperator"&&!finished.current){
          finished.current=true;
          setError("A Store-v1 Restart megszakadt. Nincs automatikus ismétlés. "+
            "Ellenőrizd az eredeti ASA PID-et és az újraindítási naplót.");
        }
      }catch{
        if(alive)setError("A natív Store-v1 Restart állapota nem olvasható; ne indítsd újra.");
      }finally{polling=false;}
    };
    void poll();
    const interval=window.setInterval(()=>{void poll();},1800);
    return ()=>{alive=false;window.clearInterval(interval);};
  },[jobId,mapId,onRestarted]);
  const inspect=async()=>{
    if(busy||!running||!checked||!recovery||recovery.blocked||
       recovery.inMemoryRunning||jobId)return;
    setBusy(true);setError("");setNotice("");setPreview(null);setPhrase("");
    try{setPreview(await managerBridge.historicalRestartPreview(mapId));}
    catch{setError("Az eredeti ACTIVE/DONE session, PID/RCON-tulajdonlás, "+
      "az indítási konfiguráció vagy a műveleti napló nem igazolható. "+
      "Az ASA szerverhez nem nyúltunk.");}
    finally{setBusy(false);}
  };
  const apply=async()=>{
    if(busy||!preview||phrase!=="STORE-V1 SAVEWORLD DOEXIT START")return;
    const token=preview.token;setPreview(null);setPhrase("");setBusy(true);
    setError("");setNotice("");
    try{
      const result=await managerBridge.historicalRestartApply(
        token,"STORE-V1 SAVEWORLD DOEXIT START");
      setJobId(result.jobId);
      setNotice("Natív, naplózott Store-v1 Restart elindult. SaveWorld/DoExit "+
        "mentésigazolás és régi PID-kilépés nélkül nincs új ASA Process.Start.");
    }catch{
      setError("A Store-v1 Restart jóváhagyása vagy előellenőrzése meghiúsult. "+
        "Nincs automatikus RCON-küldés, retry vagy naplófeloldás.");
    }finally{setBusy(false);}
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
        indítás újbóli ellenőrzése → új helyi ASA PID. A kétgépes Store session/lock
        változatlan. ASA API Loader, automatikus újrapróbálás és konverzió nincs.</p>
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
          <b>{preview.mapName} · régi PID {preview.pid} · RCON {preview.rconPort}</b>
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
