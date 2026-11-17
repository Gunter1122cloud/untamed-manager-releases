import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,Shield} from "lucide-react";
import {managerBridge,type PublicMap} from "./bridge";

type ModeState={
  useApi:boolean;
  canChange:boolean;
  confirmedStopped:boolean;
  approvalRequired:boolean;
};

/**
 * One per selected LOCAL map in Plugin Center / ASA API.
 * Native state owns the persisted value. Clicking the checkbox creates only
 * a short-lived approval preview; the checkbox reflects the OLD setting until
 * the native one-shot apply succeeds. No ASA/Store/INI writes here.
 */
export function AsaApiLaunchModeSwitch({map,onSaved}:{
  map:PublicMap;onSaved?:()=>Promise<void>
}){
  const historical=!!map.storeProtected&&!map.freshStoreEnrolled;
  const freshStore=!!map.freshStoreEnrolled;
  const observedOnly=!!map.observedReadOnlyImported;
  const [status,setStatus]=useState<ModeState|null>(null);
  const [busy,setBusy]=useState(false);
  const [needsReconcile,setNeedsReconcile]=useState(false);
  const inFlight=useRef(false);
  const selectedMap=useRef(map.id);selectedMap.current=map.id;
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");

  useEffect(()=>{
    let active=true;
    setStatus(null);setNeedsReconcile(false);setError("");setNotice("");
    if(observedOnly){
      setStatus({useApi:false,canChange:false,confirmedStopped:false,
        approvalRequired:false});
      return ()=>{active=false;};
    }
    const request=historical
      ?managerBridge.historicalLoaderModeStatus(map.id).then(x=>({
          useApi:x.useApi,
          canChange:x.canChangeMode,
          confirmedStopped:x.confirmedStopped,
          approvalRequired:x.approvalRequired
        }))
      :freshStore
        ?managerBridge.freshStoreLoaderModeStatus(map.id).then(x=>({
            useApi:x.useApi,canChange:x.canChangeMode,
            confirmedStopped:x.confirmedStopped,
            approvalRequired:false
          }))
        :managerBridge.localAsaApiModeStatus(map.id).then(x=>({
            useApi:x.useApi,canChange:x.canChangeMode,
            confirmedStopped:x.canChangeMode,approvalRequired:false
          }));
    request.then(next=>{if(active)setStatus(next);})
      .catch(()=>{if(active)setError("Az indítási mód nem ellenőrizhető. "+
        "Nem módosult a Manager-profil.");});
    return ()=>{active=false;};
  },[map.id,historical,freshStore,observedOnly]);

  const nativeStatus=async(requested:string)=>historical
    ?managerBridge.historicalLoaderModeStatus(requested).then(x=>({
      useApi:x.useApi,canChange:x.canChangeMode,confirmedStopped:x.confirmedStopped,
      approvalRequired:x.approvalRequired
    }))
    :freshStore?managerBridge.freshStoreLoaderModeStatus(requested).then(x=>({
      useApi:x.useApi,canChange:x.canChangeMode,confirmedStopped:x.confirmedStopped,
      approvalRequired:false
    }))
    :managerBridge.localAsaApiModeStatus(requested).then(x=>({
      useApi:x.useApi,canChange:x.canChangeMode,
      confirmedStopped:x.canChangeMode,approvalRequired:false
    }));
  const refresh=async()=>{
    if(busy||inFlight.current||observedOnly)return;
    const requested=map.id;
    setBusy(true);setError("");
    try{
      const next=await nativeStatus(requested);
      if(selectedMap.current!==requested)return;
      setStatus(next);setNeedsReconcile(false);
      setNotice("Indítási mód natívan újraellenőrizve; nem indult ASA szerver.");
    }catch{
      if(selectedMap.current===requested)
        setError("Nem igazolható az indítási mód. Újabb módváltás továbbra is tiltott.");
    }finally{if(selectedMap.current===requested)setBusy(false);}
  };
  const prepare=async(next:boolean)=>{
    if(!status?.canChange||busy||inFlight.current||needsReconcile)return;
    if(status.useApi===next&&!status.approvalRequired)return;
    inFlight.current=true;
    const requested=map.id;
    setBusy(true);setError("");setNotice("");
    let applyAttempted=false;
    try{
      // One checkbox change performs the full native preflight + single-use
      // map-bound token Apply, no second modal or checkbox.
      const proof=historical
        ?await managerBridge.historicalLoaderModePreview(requested,next)
        :freshStore
          ?await managerBridge.freshStoreLoaderModePreview(requested,next)
          :await managerBridge.localAsaApiModePreview(requested,next);
      if(selectedMap.current!==requested)return;
      if(!proof.token||proof.mapId.toLowerCase()!==requested.toLowerCase()||
         proof.useApi!==next||proof.mode!==(next?"asaApiLoader":"directAsa")||
         proof.startsServer||proof.changesStore||!proof.requiresStoppedProcess)
        throw new Error("UM-ASA-API-MODE-PREVIEW-MISMATCH");
      applyAttempted=true;
      const receipt=historical
        ?await managerBridge.historicalLoaderModeApply(proof.token)
        :freshStore
          ?await managerBridge.freshStoreLoaderModeApply(proof.token)
          :await managerBridge.localAsaApiModeApply(proof.token);
      if(selectedMap.current!==requested)return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         receipt.useApi!==next||receipt.mode!==proof.mode||
         receipt.serverStarted||receipt.originalStoreTouched)
        throw new Error("UM-ASA-API-MODE-RECEIPT-MISMATCH");
      setStatus(current=>current?{...current,useApi:next,
        confirmedStopped:true,approvalRequired:false}:current);
      setNotice("Elmentve: "+(next?"ASA API Loader":"Közvetlen ASA")+
        ". A következő Start külön művelet; nem indult szerver.");
      try{await onSaved?.();}
      catch{if(selectedMap.current===requested)setNotice(
        "Az indítási mód elmentve, de a maplista frissítése nem sikerült. Ne küldd újra.");}
    }catch(e){
      if(selectedMap.current===requested){
        if(applyAttempted)setNeedsReconcile(true);
        setError(applyAttempted?
          "A módváltás kimenetele nem igazolható ("+
            (e instanceof Error?e.message:"UM-ASA-API-MODE-APPLY-UNKNOWN")+"). "+
            "Nem ismételjük meg; olvasd vissza a natív állapotot.":
          "A módváltási előellenőrzés sikertelen ("+
            (e instanceof Error?e.message:"UM-ASA-API-MODE-PREVIEW-DENIED")+"). "+
            "Leállított szerver és ellenőrzött API/Loader fájlok szükségesek.");
      }
    }finally{
      inFlight.current=false;
      if(selectedMap.current===requested)setBusy(false);
    }
  };

  return <section className="glass panel" aria-label="Maponkénti ASA API indítási kapcsoló">
    <div className="panel-head">
      <h2><Shield size={18}/> ASA API – szerverindítás</h2>
      <span className="mod-tag amber">{map.name} · maponként</span>
    </div>
    <div className="panel-inner">
      <label className="historical-approval">
        <input type="checkbox" aria-label="Indítás ASA API-val"
          checked={status?.useApi??false}
          disabled={!status?.canChange||busy||needsReconcile}
          onChange={e=>{void prepare(e.target.checked);}}/>
        Indítás ASA API-val
      </label>
      <p className="muted small">
        {status?.useApi?"Beállítva: AsaApiLoader.exe":"Beállítva: közvetlen ArkAscendedServer.exe"}.
        A kapcsoló a kiválasztott maphoz tartozik, és csak a következő
        indítást érinti; az aktuális szervert nem állítja le és nem indítja újra.
      </p>
      {status?.approvalRequired&&<div className="warning-inline">
        A korábbi kezelő indítási módját külön natív művelettel kell rögzíteni.
        {!status.useApi&&<button type="button" className="secondary"
          disabled={!status.canChange||busy||needsReconcile}
          onClick={()=>{void prepare(false);}}>
          Közvetlen ASA mód rögzítése
        </button>}
      </div>}
      {!status?.canChange&&<p className="warning-inline" role="status">
        {observedOnly?
          "A megfigyelt mapot előbb regisztrálni és átvenni kell.":
          "A mód csak igazoltan leállított helyi mapon, ellenőrzött Loader/API "+
          "fájlokkal és rendezett műveleti naplóval módosítható."}
      </p>}
      {needsReconcile&&<button type="button" className="secondary"
        disabled={busy} onClick={()=>{void refresh();}}>
        Indítási mód natív újraellenőrzése
      </button>}
      {notice&&<div role="status" className="success-inline">
        <CheckCircle2 size={16}/>{notice}</div>}
      {error&&<div role="alert" className="error-inline">
        <AlertTriangle size={16}/>{error}</div>}
    </div>
  </section>;
}
