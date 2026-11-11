import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,Package,Shield,Upload} from "lucide-react";
import {managerBridge,type PluginZipPreview,type PluginZipRollbackPreview} from "./bridge";

export function PluginZipInstaller({mapId,onInstalled}:{
  mapId:string;onInstalled:()=>void
}){
  const [preview,setPreview]=useState<PluginZipPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [rollbackPreview,setRollbackPreview]=useState<PluginZipRollbackPreview|null>(null);
  const [rollbackApproved,setRollbackApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [message,setMessage]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    setPreview(null);setApproved(false);
    setRollbackPreview(null);setRollbackApproved(false);
    setMessage("");setError("");
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
  const chooseRollback=async()=>{
    if(!mapId||busy)return;
    setBusy(true);setRollbackPreview(null);setRollbackApproved(false);
    setMessage("");setError("");
    try{setRollbackPreview(await managerBridge.pluginZipRollbackSelect(mapId));}
    catch{setError("A korábbi plugin-mentés nem található, sérült, vagy a helyi ASA/Loader nem igazoltan leállított.");}
    finally{setBusy(false);}
  };
  const applyRollback=async()=>{
    if(!rollbackPreview||!rollbackApproved||busy)return;
    const token=rollbackPreview.token;
    setRollbackPreview(null);setRollbackApproved(false);
    setBusy(true);setMessage("");setError("");
    try{
      const receipt=await managerBridge.pluginZipRollbackApply(token);
      setMessage(receipt.plugin+" – "+receipt.restoredBinaries+
        " eredeti DLL/PDB visszaállítva. A jelenlegi pluginról új, teljes védőmentés készült. A következő ASA indításkor töltődik be.");
      onInstalled();
    }catch{setError("A natív rollback nem igazolható. Az eredeti plugin-mentés és a Manager tranzakciós naplója ellenőrzendő.");}
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
    <div className="historical-preview" aria-label="Plugin ZIP visszaállítása" style={{marginTop:18}}>
      <h3>Korábbi plugin DLL/PDB visszaállítása</h3>
      <p className="muted small">
        Válassz a Windows mappaválasztójával egy korábbi, Manager által
        ellenőrzött plugin ZIP védőmentést. Csak a kiválasztott map pluginbinarisai
        állnak vissza; a jelenlegi config, licenc és játékosadatok megmaradnak.
      </p>
      <button type="button" className="secondary" disabled={!mapId||busy}
        onClick={()=>{void chooseRollback();}}>
        Korábbi pluginmentés kiválasztása
      </button>
      {rollbackPreview&&<div className="historical-preview" role="region"
        aria-label="Plugin ZIP rollback jóváhagyása">
        <h4>{rollbackPreview.mapName} · {rollbackPreview.plugin}</h4>
        <p>Korábbi mentés: {rollbackPreview.backupName}.
          Visszaállítandó binárisok: {rollbackPreview.binaryCount}.</p>
        <label className="historical-approval">
          <input type="checkbox" checked={rollbackApproved} disabled={busy}
            onChange={e=>setRollbackApproved(e.target.checked)}/>
          Jóváhagyom a régi DLL/PDB fájlok visszaállítását külön teljes védőmentéssel.
        </label>
        <button type="button" className="primary"
          disabled={!rollbackApproved||busy}
          onClick={()=>{void applyRollback();}}>Ellenőrzött plugin rollback végrehajtása</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>{setRollbackPreview(null);setRollbackApproved(false);}}>Mégsem</button>
      </div>}
    </div>
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={18}/>{message}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={18}/>{error}</div>}
    <p className="muted tiny"><Shield size={14}/> Az eredeti Kova Store-v1 mapok ZIP-pluginfrissítése is végrehajtható, ha a natív Windows-folyamatellenőrzés igazolja minden érintett ASA/Loader leállását. A korábbi DLL és konfiguráció megmarad az ellenőrzött védőmentésben. Futó map és idegen gazdagép nem módosítható; a plugin az új ASA-indításkor töltődik be.</p>
  </section>;
}
