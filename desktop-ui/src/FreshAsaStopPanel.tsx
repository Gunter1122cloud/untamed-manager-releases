import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,Octagon,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type FreshAsaStopPreview,type FreshAsaStopStatus} from "./bridge";

// Nonsecret operator-visible receipt stays on the map after a dashboard refresh.
// Never store a password, launch command or character metadata in the browser.
const recentStopReceipts=new Map<string,string>();

export function FreshAsaStopPanel({mapId,running,onStopped}:{
  mapId:string;running:boolean;onStopped:()=>Promise<void>
}){
  const [preview,setPreview]=useState<FreshAsaStopPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [jobId,setJobId]=useState<string|null>(null);
  const [status,setStatus]=useState<FreshAsaStopStatus|null>(null);
  const [message,setMessage]=useState(()=>recentStopReceipts.get(mapId)??"");
  const [error,setError]=useState("");

  useEffect(()=>{
    setPreview(null);setApproved(false);setError("");
    setMessage(recentStopReceipts.get(mapId)??"");
  },[mapId]);
  useEffect(()=>{
    if(!jobId)return;
    let active=true;
    const poll=async()=>{
      try{
        const result=await managerBridge.freshAsaStopStatus(jobId);
        if(!active)return;
        setStatus(result);
        if(result.state==="completed"){
          setJobId(null);
          const text="Az ASA-folyamat szabályosan kilépett. A DoExit saját mentési folyamatot indít; független lemezmentési bizonyíték nincs.";
          recentStopReceipts.set(mapId,text);
          setMessage(text);
          onStopped().catch(()=>{});
        }else if(result.state==="needsOperator"){
          setJobId(null);
          setError("A leállítás elakadt. Ellenőrizd a folyamatot és a mentési naplót; nem történt automatikus Kill vagy ismétlés.");
        }
      }catch{
        if(active){
          setJobId(null);
          setError("A leállítási feladat nem követhető. Ellenőrizd az ASA állapotát, mielőtt újabb műveletet kezdeményezel.");
        }
      }
    };
    void poll();
    const interval=window.setInterval(()=>{void poll();},2500);
    return ()=>{active=false;window.clearInterval(interval);};
  },[jobId,mapId,onStopped]);

  const check=async()=>{
    if(!running||busy||jobId)return;
    setBusy(true);setPreview(null);setApproved(false);setError("");setMessage("");
    try{setPreview(await managerBridge.freshAsaStopPreview(mapId));}
    catch{setError("A leállítás előellenőrzése sikertelen: csak saját helyi, pontosan azonosított RCON-os, nem Store-v1 szerver állítható le.");}
    finally{setBusy(false);}
  };
  const stop=async()=>{
    if(!preview||!approved||busy||jobId)return;
    const token=preview.token;
    setBusy(true);setError("");setPreview(null);setApproved(false);
    try{
      const job=await managerBridge.freshAsaStopApply(token);
      setJobId(job.jobId);
      setStatus({jobId:job.jobId,state:"queued",step:"preflight",
        changedUtc:new Date().toISOString(),confirmedStopped:false,
        saveCompleteProven:false,killPerformed:false,
        restartPerformed:false,storeTouched:false});
    }catch{setError("A natív leállítás nem indult el. A PID vagy a RCON-kapcsolat megváltozhatott.");}
    finally{setBusy(false);}
  };

  return <section className="premium-panel" aria-label="Valódi ASA biztonságos leállítása" style={{marginBlock:18,padding:20}}>
    <header className="premium-panel-heading"><h2><Octagon size={18}/> ASA szerver normál leállítása</h2></header>
    <p>A helyi C# Manager tényleges RCON SaveWorld és DoExit műveletet küld, megvárja a Windows-folyamat kilépését. A folyamatot nem lövi ki.</p>
    <button type="button" className="secondary" disabled={!running||busy||!!jobId}
      onClick={()=>{void check();}}><Shield size={16}/> Normál leállítás előellenőrzése</button>
    {preview&&<div className="historical-preview" role="region" aria-label="ASA leállítás jóváhagyása">
      <h3>{preview.mapName} – PID: {preview.pid}</h3>
      <p>RCON: SaveWorld → DoExit → ellenőrzött folyamatkilépés. Nincs kényszerített leállítás vagy automatikus restart.</p>
      <label className="historical-approval"><input type="checkbox" checked={approved}
        disabled={busy} onChange={e=>setApproved(e.target.checked)}/>
        Jóváhagyom a kiválasztott helyi ASA szerver tényleges leállítását.
      </label>
      <button type="button" className="primary" disabled={!approved||busy}
        onClick={()=>{void stop();}}>Szerver szabályos leállítása</button>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>setPreview(null)}>Mégsem</button>
    </div>}
    {status&&<div className="warning-inline" role="status"><RefreshCw size={16}/>
      <span>Leállítás: {status.state} – {status.step}
        {status.state==="completed"&&" · a PID megszűnése igazolt"}</span></div>}
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{message}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
    <p className="muted tiny"><Shield size={14}/> A történeti Store-v1 átvételt nem módosítja. A szerverleállítás sikeressége önmagában nem bizonyít karakterutazást vagy független SaveComplete-jelet.</p>
  </section>;
}
