import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,Package,Shield,Upload} from "lucide-react";
import {managerBridge,type PluginZipPreview} from "./bridge";

export function PluginZipInstaller({mapId,onInstalled}:{
  mapId:string;onInstalled:()=>void
}){
  const [preview,setPreview]=useState<PluginZipPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [message,setMessage]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    setPreview(null);setApproved(false);setMessage("");setError("");
  },[mapId]);
  const choose=async()=>{
    if(!mapId||busy)return;
    setBusy(true);setPreview(null);setApproved(false);setMessage("");setError("");
    try{setPreview(await managerBridge.pluginZipSelect(mapId));}
    catch{setError("Nem sikerült a helyi ZIP előellenőrzése. A kiválasztás megszakadhatott, vagy az érintett mapok nem állnak le igazoltan.");}
    finally{setBusy(false);}
  };
  const install=async()=>{
    if(!preview||!approved||busy)return;
    const token=preview.token;
    setPreview(null);setApproved(false);setBusy(true);setMessage("");setError("");
    try{
      const receipt=await managerBridge.pluginZipApply(token);
      setMessage(receipt.plugin+" – "+receipt.installedBinaries+
        " DLL telepítve; korábbi verzió: "+(receipt.backupCreated?"mentve":"nem volt")+
        ". A plugin új szerverindításkor töltődik be.");
      onInstalled();
    }catch{setError("A natív ZIP plugintelepítés nem igazolható. A meglévő C# mentési és visszaállítási naplót ellenőrizd.");}
    finally{setBusy(false);}
  };
  return <section className="premium-panel" aria-label="Helyi ASA API plugin ZIP telepítése" style={{marginTop:18,padding:20}}>
    <header className="premium-panel-heading"><h2><Package size={19}/> Plugin telepítése vagy frissítése ZIP-ből</h2></header>
    <p>A Windows saját fájlválasztója nyílik meg. Csak a kiválasztott, igazoltan leállított helyi ASA-telepítés DLL/PDB fájljai módosulnak, a korábbi plugin teljes mentésével. A React nem kap fájlrendszer-hozzáférést.</p>
    <button type="button" className="secondary" disabled={!mapId||busy}
      onClick={()=>{void choose();}}><Upload size={16}/> Plugin ZIP kiválasztása és ellenőrzése</button>
    {preview&&<div className="historical-preview" role="region" aria-label="Plugin ZIP telepítés jóváhagyása">
      <h3>{preview.plugin} – {preview.previousExists?"meglévő plugin frissítése":"új plugin"}</h3>
      <div className="details-line"><span>Csomag</span><b>{preview.archiveName}</b></div>
      <div className="details-line"><span>DLL-ek</span><b>{preview.binaryCount}</b></div>
      <div className="details-line"><span>Megőrzött nem bináris fájlok</span><b>{preview.skippedFiles}</b></div>
      <label className="historical-approval"><input type="checkbox" checked={approved}
        disabled={busy} onChange={e=>setApproved(e.target.checked)}/>
        Jóváhagyom a kiválasztott helyi plugin biztonságos frissítését.</label>
      <button type="button" className="primary" disabled={!approved||busy}
        onClick={()=>{void install();}}>Plugin telepítésének véglegesítése</button>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>setPreview(null)}>Mégsem</button>
    </div>}
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={18}/>{message}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={18}/>{error}</div>}
    <p className="muted tiny"><Shield size={14}/> A Store-v1 történeti mapok, futó mapok és idegen gazdagépek módosítása tiltott. A plugin runtime állapota csak egy későbbi valós ASA indítással ellenőrizhető.</p>
  </section>;
}
