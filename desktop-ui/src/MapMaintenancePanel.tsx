import {useCallback,useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type MapMaintenanceStatus} from "./bridge";

export function MapMaintenancePanel({mapId,onConfigured}:{
  mapId:string;onConfigured:()=>Promise<void>;
}){
  const [status,setStatus]=useState<MapMaintenanceStatus|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const reload=useCallback(async()=>{
    setBusy(true);setError("");
    try{setStatus(await managerBridge.mapMaintenanceStatus(mapId));}
    catch{setError("A map karbantartási beállítása nem ellenőrizhető.");setStatus(null);}
    finally{setBusy(false);}
  },[mapId]);
  useEffect(()=>{void reload();},[reload]);
  const save=async()=>{
    if(!status?.permitted||!approved||busy)return;
    const next=!status.maintenance;
    setBusy(true);setError("");setNotice("");
    try{
      const changed=await managerBridge.mapMaintenanceSet(mapId,next);
      setStatus(changed);setApproved(false);
      setNotice(next?
        "A map karbantartási módja bekapcsolva; a Manager nem indított vagy állított le ASA-t.":
        "A map karbantartási módja kikapcsolva; a Manager nem indított vagy állított le ASA-t.");
      await onConfigured();
    }catch{
      setError("A natív Manager elutasította a módosítást. Ellenőrizd, hogy a map leállt-e, és nincs-e Store-v1 védelem.");
    }finally{setBusy(false);}
  };
  return <section className="premium-panel" aria-label="Map karbantartási beállítása">
    <header className="premium-panel-heading">
      <h2><Shield size={18}/> Map karbantartási módja</h2>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>{void reload();}}><RefreshCw size={16}/> Frissítés</button>
    </header>
    <div className="panel-inner">
      <p className="muted small">Ez a map saját, tartós Manager-beállítása. Az állítás csak igazoltan leállított, nem Store-v1 helyi mapon lehetséges. Nem végez RCON-t, SteamCMD-műveletet vagy szerverindítást.</p>
      {status&&<>
        <div className="map-info-line"><span>Karbantartási mód</span>
          <strong>{status.maintenance?"Bekapcsolva":"Kikapcsolva"}</strong></div>
        {!status.permitted&&<p className="warning-inline"><AlertTriangle size={16}/>
          A beállítás nem módosítható: a map fut, a leállítás nem igazolt, vagy történeti Store-v1 védelem aktív.</p>}
        <label className="native-schedule-consent">
          <input type="checkbox" checked={approved} disabled={busy||!status.permitted}
            onChange={e=>setApproved(e.target.checked)}/>
          Jóváhagyom ennek az egy helyi mapnak a karbantartási beállításának módosítását.
        </label>
        <button type="button" className="primary" disabled={!approved||!status.permitted||busy}
          onClick={()=>{void save();}}>
          {status.maintenance?"Karbantartási mód kikapcsolása":"Karbantartási mód bekapcsolása"}
        </button>
      </>}
      {error&&<p className="error-inline" role="alert">{error}</p>}
      {notice&&<p className="success-inline" role="status"><CheckCircle2 size={16}/>{notice}</p>}
    </div>
  </section>;
}
