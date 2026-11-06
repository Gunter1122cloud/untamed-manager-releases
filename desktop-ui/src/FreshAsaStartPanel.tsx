import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,Play,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type FreshAsaStartPreview,type FreshAsaStartStatus} from "./bridge";

// Nonsecret process receipt survives a dashboard refresh/remount. Never persist
// launch arguments, credentials or Store data in WebView2 storage.
const recentStartReceipts=new Map<string,string>();

export function FreshAsaStartPanel({mapId,onStarted,journalBlocked}:{
  mapId:string;onStarted:()=>Promise<void>;journalBlocked:boolean
}){
  const [observed,setObserved]=useState<FreshAsaStartStatus|null>(null);
  const [preview,setPreview]=useState<FreshAsaStartPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [message,setMessage]=useState(()=>recentStartReceipts.get(mapId)??"");
  const [error,setError]=useState("");
  useEffect(()=>{
    let active=true;
    setPreview(null);setApproved(false);setObserved(null);setError("");
    setMessage(recentStartReceipts.get(mapId)??"");
    managerBridge.freshAsaStartStatus(mapId)
      .then(s=>{if(active)setObserved(s);})
      .catch(()=>{});
    return ()=>{active=false;};
  },[mapId]);

  const observe=async()=>{
    setError("");
    try{setObserved(await managerBridge.freshAsaStartStatus(mapId));}
    catch{setError("A helyi ASA-folyamat állapotának beolvasása sikertelen.");}
  };
  const check=async()=>{
    if(busy || !mapId || journalBlocked)return;
    setBusy(true);setError("");setMessage("");setPreview(null);setApproved(false);
    try{setPreview(await managerBridge.freshAsaStartPreview(mapId));}
    catch{setError("Indítás nem engedélyezett: új, érvényesen konfigurált ASA-telepítés, leállított folyamat és sértetlen mentésállapot szükséges. Store-v1 történeti mapok nem indíthatók ezzel a művelettel.");}
    finally{setBusy(false);}
  };
  const start=async()=>{
    if(!preview || !approved || busy || journalBlocked)return;
    const token=preview.token;
    setBusy(true);setError("");setMessage("");setPreview(null);setApproved(false);
    try{
      const receipt=await managerBridge.freshAsaStartApply(token);
      const summary=receipt.mapName+" · ASA indítás elindítva, PID: "+receipt.pid+
        ". A folyamatindítás még nem bizonyítja a játék és az RCON készenlétét.";
      recentStartReceipts.set(mapId,summary);
      setMessage(summary);
      try{await onStarted();}catch{/* Process receipt remains visible even if dashboard refresh fails. */}
      await observe();
    }catch{setError("Az ASA elindítása nem igazolható. Ellenőrizd a helyi naplókat és a folyamatállapotot; automatikus ismétlés nem történt.");}
    finally{setBusy(false);}
  };

  return <section className="premium-panel" aria-label="Valódi ASA első indítása" style={{marginBlock:18,padding:20}}>
    <header className="premium-panel-heading">
      <h2><Play size={19}/> Új ASA-szerver indítása</h2>
      <button type="button" className="quiet" onClick={()=>{void observe();}} disabled={busy}>
        <RefreshCw size={16}/> Állapotfrissítés
      </button>
    </header>
    <p>Valódi Windows ASA-indítás a meglévő C# Managerből. Kizárólag az újonnan telepített, helyi, konfigurált szerverhez, külön megerősítéssel. A történeti Store-v1 mapok külön átvételi folyamatot igényelnek.</p>
    {observed&&<div role="status" className="details-line">
      <span>Windows folyamat</span>
      <b>{observed.running?"Fut · PID "+(observed.pid??"?"):
        observed.confirmedStopped?"Igazoltan leállítva":"Nem igazolt állapot"}</b>
    </div>}
    <button type="button" className="secondary" disabled={busy||journalBlocked||Boolean(observed?.running)}
      onClick={()=>{void check();}}>Valódi ASA-indítás előellenőrzése</button>
    {preview&&<div className="historical-preview" role="region" aria-label="Valódi ASA indítás jóváhagyása">
      <h3>{preview.mapName} – Steam build: {preview.steamBuild}</h3>
      <div className="details-line"><span>Game / Query UDP</span>
        <b>{preview.gamePort} / {preview.queryPort}</b></div>
      <div className="details-line"><span>RCON konfigurálva</span>
        <b>{preview.rconConfigured?"Igen":"Nem"}</b></div>
      <label className="historical-approval"><input type="checkbox"
        checked={approved} disabled={busy}
        onChange={e=>setApproved(e.target.checked)}/>
        Jóváhagyom az új, saját helyi ASA szerver tényleges Windows-folyamatának elindítását.
      </label>
      <button type="button" className="primary" disabled={busy||!approved||journalBlocked}
        onClick={()=>{void start();}}><Play size={16}/> ASA szerver indítása</button>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>setPreview(null)}>Mégsem</button>
    </div>}
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={18}/>{message}</div>}
    {error&&<div className="warning-inline" role="alert"><AlertTriangle size={18}/>{error}</div>}
    <p className="muted tiny"><Shield size={14}/> Nem használ karakterkonverziót, nem ír a Store-v1-be, nem indít másik hostot, és nincs automatikus második Start-kísérlet.</p>
  </section>;
}
