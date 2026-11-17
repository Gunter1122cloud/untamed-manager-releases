import {useEffect,useRef,useState} from "react";
import {Shield,RefreshCw,AlertTriangle,CheckCircle2} from "lucide-react";
import {managerBridge,type FreshStoreLoaderModeStatus} from "./bridge";

/**
 * Only for a locally owned, NEW enrolled Store map. Never render for
 * historical korábbi Manager imports. Mode choice is not an ASA Start or cache install.
 */
export function FreshStoreLoaderModePanel({mapId,onConfigured}:{
  mapId:string;onConfigured:()=>Promise<void>
}){
  const [status,setStatus]=useState<FreshStoreLoaderModeStatus|null>(null);
  const [busy,setBusy]=useState(false);
  const [reconcileRequired,setReconcileRequired]=useState(false);
  const inFlight=useRef(false);
  const selectedMap=useRef(mapId);selectedMap.current=mapId;
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  useEffect(()=>{
    let active=true;
    setStatus(null);setError("");setNotice("");setReconcileRequired(false);
    managerBridge.freshStoreLoaderModeStatus(mapId).then(next=>{
      if(active&&next.mapId.toLowerCase()===mapId.toLowerCase()&&
         !next.serverStarted&&!next.originalStoreTouched)setStatus(next);
      else if(active)setError("UM-STORE-API-MODE-STATUS-MAP-MISMATCH");
    }).catch(()=>{
      if(active)setError("A helyi Store-map indítási módja nem ellenőrizhető.");
    });
    return ()=>{active=false;};
  },[mapId]);
  const refresh=async()=>{
    if(busy||inFlight.current)return;
    const requested=mapId;
    setBusy(true);setError("");
    try{
      const observed=await managerBridge.freshStoreLoaderModeStatus(requested);
      if(selectedMap.current!==requested)return;
      if(observed.mapId.toLowerCase()!==requested.toLowerCase()||
         observed.serverStarted||observed.originalStoreTouched)
        throw new Error("UM-STORE-API-MODE-STATUS-MAP-MISMATCH");
      setStatus(observed);setReconcileRequired(false);
      setNotice("Az indítási mód friss natív, csak olvasásos ellenőrzése elkészült.");
    }catch{
      if(selectedMap.current===requested)
        setError("A módváltás natív állapota nem olvasható. Újabb módosítás nem indul.");
    }finally{if(selectedMap.current===requested)setBusy(false);}
  };
  const change=async()=>{
    if(!status?.canChangeMode||busy||reconcileRequired||inFlight.current)return;
    inFlight.current=true;
    const requested=mapId,next=!status.useApi;
    setBusy(true);setError("");setNotice("");
    let applied=false;
    try{
      // One named click: fresh native preview and immediately consumed
      // map-scoped one-use token. No separate UI approval or server launch.
      const proof=await managerBridge.freshStoreLoaderModePreview(requested,next);
      if(selectedMap.current!==requested)return;
      if(!proof.token||proof.mapId.toLowerCase()!==requested.toLowerCase()||
         proof.useApi!==next||proof.mode!==(next?"asaApiLoader":"directAsa")||
         proof.startsServer||proof.changesStore||!proof.requiresStoppedProcess)
        throw new Error("UM-STORE-API-MODE-PREVIEW-MISMATCH");
      applied=true;
      const receipt=await managerBridge.freshStoreLoaderModeApply(proof.token);
      if(selectedMap.current!==requested)return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         receipt.useApi!==next||receipt.mode!==proof.mode||
         receipt.serverStarted||receipt.originalStoreTouched||
         !receipt.requiresManualStart)
        throw new Error("UM-STORE-API-MODE-RECEIPT-MISMATCH");
      setStatus(previous=>previous?{...previous,useApi:next,mode:receipt.mode}:previous);
      setNotice(next?"ASA API Loader indítási mód mentve. A map nem indult el.":
        "Közvetlen ASA indítási mód mentve. A map nem indult el.");
      try{await onConfigured();}
      catch{if(selectedMap.current===requested)setError(
        "A profil mentve, de a nézet frissítése nem sikerült. Ne ismételd a műveletet.");}
    }catch(e){
      if(selectedMap.current===requested){
        if(applied)setReconcileRequired(true);
        setError(applied?
          "Az indítási mód módosításának kimenetele bizonytalan ("+
            (e instanceof Error?e.message:"UM-STORE-API-MODE-APPLY-UNKNOWN")+"). "+
            "Ne ismételd meg; ellenőrizd a natív státuszt.":
          "A Loader módváltás natív előellenőrzése elutasítva ("+
            (e instanceof Error?e.message:"UM-STORE-API-MODE-PREFLIGHT")+"). "+
            "A mapot le kell állítani, és ellenőrizni az API Core fájlokat.");
      }
    }finally{
      inFlight.current=false;
      if(selectedMap.current===requested)setBusy(false);
    }
  };
  return <section className="glass panel" aria-label="Új Store-map ASA API indítási módja">
    <div className="panel-head"><h2><Shield size={18}/> ASA API – új Store-map</h2>
      <span className="mod-tag amber">Maponkénti, egyszeri beállítás</span></div>
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
          disabled={!status?.canChangeMode||busy||reconcileRequired}
          onClick={()=>{void change();}}>
          <RefreshCw size={16}/>
          {status?.useApi?"Váltás közvetlen ASA indításra":"ASA API Loader választása"}
        </button>
      </div>
      {reconcileRequired&&<button type="button" className="secondary"
        disabled={busy} onClick={()=>{void refresh();}}>
        <RefreshCw size={16}/> Indítási mód natív újraellenőrzése
      </button>}
      {notice&&<div role="status" className="success-inline">
        <CheckCircle2 size={16}/>{notice}</div>}
      {error&&<div role="alert" className="error-inline">
        <AlertTriangle size={16}/>{error}</div>}
    </div>
  </section>;
}
