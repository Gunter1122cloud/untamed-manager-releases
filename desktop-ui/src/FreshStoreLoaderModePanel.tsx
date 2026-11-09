import {useEffect,useState} from "react";
import {Shield,RefreshCw,AlertTriangle,CheckCircle2} from "lucide-react";
import {managerBridge,type FreshStoreLoaderModePreview,
  type FreshStoreLoaderModeStatus} from "./bridge";

/**
 * Only for a locally owned, NEW enrolled Store map. Never render for
 * historical Kova imports. Mode choice is not an ASA Start or cache install.
 */
export function FreshStoreLoaderModePanel({mapId,onConfigured}:{
  mapId:string;onConfigured:()=>Promise<void>
}){
  const [status,setStatus]=useState<FreshStoreLoaderModeStatus|null>(null);
  const [preview,setPreview]=useState<FreshStoreLoaderModePreview|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  useEffect(()=>{
    let active=true;
    setStatus(null);setPreview(null);setError("");setNotice("");
    managerBridge.freshStoreLoaderModeStatus(mapId).then(next=>{
      if(active)setStatus(next);
    }).catch(()=>{
      if(active)setError("A helyi Store-map indítási módja nem ellenőrizhető.");
    });
    return ()=>{active=false;};
  },[mapId]);
  const change=async()=>{
    if(!status?.canChangeMode||busy)return;
    setBusy(true);setError("");setNotice("");setPreview(null);
    try{
      setPreview(await managerBridge.freshStoreLoaderModePreview(
        mapId,!status.useApi));
    }catch{
      setError("A Loader módváltás előfeltétele nem igazolt. Ellenőrizd az API Core "+
        "telepítését, a map leállított állapotát és a clusterazonosságot.");
    }finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!preview||busy)return;
    setBusy(true);setError("");setNotice("");
    const token=preview.token;
    setPreview(null);
    try{
      const receipt=await managerBridge.freshStoreLoaderModeApply(token);
      setNotice(receipt.mode==="asaApiLoader"?
        "ASA API Loader indítási mód mentve. A map nem indult el.":
        "Közvetlen ASA indítási mód mentve. A map nem indult el.");
      setStatus(await managerBridge.freshStoreLoaderModeStatus(mapId));
      try{await onConfigured();}
      catch{setError("A profil mentve, de a nézet frissítése nem sikerült. Ne ismételd a műveletet.");}
    }catch{
      setError("A jóváhagyás eredménye nem igazolható. Nincs automatikus ismétlés vagy indítás.");
    }finally{setBusy(false);}
  };
  return <section className="glass panel" aria-label="Új Store-map ASA API indítási módja">
    <div className="panel-head"><h2><Shield size={18}/> ASA API – új Store-map</h2>
      <span className="mod-tag amber">Maponként, külön jóváhagyással</span></div>
    <div className="panel-inner">
      <div className="details-line"><span>Beállított indítás</span>
        <b>{status?.mode==="asaApiLoader"?"ASA API Loader":
          status?.mode==="directAsa"?"Közvetlen ArkAscendedServer":"Nem igazolt"}</b>
      </div>
      <div className="details-line"><span>Folyamat / módváltás</span>
        <b>{status?.confirmedStopped?"Leállítás igazolva":"Nem engedélyezett vagy nem igazolt"}</b>
      </div>
      {status?.useApi&&<p className="muted small">
        Hivatalos ASA API cache: {status.cacheState}. Ez nem igazolja az API
        tényleges betöltését, az ASA gyermekfolyamatot vagy a játékbeli készenlétet.
      </p>}
      <p className="muted small">
        Az API Core telepítését az ASA API fülön, leállított szervernél lehet
        elvégezni. Az indítási mód önmagában nem indít szervert és nem módosít
        karakter-, tribe-, tribute- vagy Store-transfer adatot.
      </p>
      <div className="settings-actions">
        <button type="button" className="secondary"
          disabled={!status?.canChangeMode||busy||!!preview}
          onClick={()=>{void change();}}>
          <RefreshCw size={16}/>
          {status?.useApi?"Váltás közvetlen ASA indításra":"ASA API Loader választása"}
        </button>
      </div>
      {preview&&<div className="warning-inline" role="region"
        aria-label="Új Store-map ASA API módváltási előnézete">
        <Shield size={17}/>
        <div>
          <b>{preview.useApi?"ASA API Loader":"Közvetlen ASA"} mód</b>
          <p>Csak a helyi Manager profilja változik, teljesen leállított mapnál.
            A következő Start külön jóváhagyást igényel. Nem történik
            szerverindítás, Store-konverzió vagy cache-fájl módosítás.</p>
          {preview.useApi&&<p className="muted small">
            Cache-állapot: {preview.cacheState}. Az API futásidejű kompatibilitása még nem igazolt.
          </p>}
          <div className="settings-actions">
            <button type="button" className="primary" disabled={busy}
              onClick={()=>{void apply();}}>Módváltás jóváhagyása</button>
            <button type="button" className="secondary" disabled={busy}
              onClick={()=>setPreview(null)}>Mégse</button>
          </div>
        </div>
      </div>}
      {notice&&<div role="status" className="success-inline">
        <CheckCircle2 size={16}/>{notice}</div>}
      {error&&<div role="alert" className="error-inline">
        <AlertTriangle size={16}/>{error}</div>}
    </div>
  </section>;
}
