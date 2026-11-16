import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,Shield} from "lucide-react";
import {managerBridge,type PublicMap} from "./bridge";

type ModeState={
  useApi:boolean;
  canChange:boolean;
  confirmedStopped:boolean;
  approvalRequired:boolean;
};
type ModePreview={token:string;mapName:string;useApi:boolean};

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
  const [preview,setPreview]=useState<ModePreview|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");

  useEffect(()=>{
    let active=true;
    setStatus(null);setPreview(null);setError("");setNotice("");
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

  const prepare=async(next:boolean)=>{
    if(!status?.canChange||busy||preview)return;
    if(status.useApi===next&&!status.approvalRequired)return;
    setBusy(true);setError("");setNotice("");
    try{
      const result=historical
        ?await managerBridge.historicalLoaderModePreview(map.id,next)
        :freshStore
          ?await managerBridge.freshStoreLoaderModePreview(map.id,next)
          :await managerBridge.localAsaApiModePreview(map.id,next);
      setPreview({token:result.token,mapName:result.mapName,useApi:next});
    }catch{
      setError("A natív módváltási ellenőrzés sikertelen. Az ASA/Loader "+
        "folyamatokat le kell állítani, és a helyi Loader/API fájloknak "+
        "hitelesen egyezniük kell. Nem változott indítási mód.");
    }finally{setBusy(false);}
  };
  const save=async()=>{
    if(!preview||busy)return;
    const request=preview;
    setPreview(null);setBusy(true);setError("");setNotice("");
    let committed=false;
    try{
      const receipt=historical
        ?await managerBridge.historicalLoaderModeApply(request.token)
        :freshStore
          ?await managerBridge.freshStoreLoaderModeApply(request.token)
          :await managerBridge.localAsaApiModeApply(request.token);
      committed=true;
      setStatus({useApi:receipt.useApi,canChange:true,
        confirmedStopped:true,approvalRequired:false});
      setNotice("Elmentve: "+(receipt.useApi?"ASA API Loader":"Közvetlen ASA")+
        ". A következő külön jóváhagyott Start ezt használja. "+
        "Nem indult szerver.");
      try{await onSaved?.();}
      catch{setNotice("Az indítási mód elmentve, de a maplista frissítése "+
        "sikertelen. Ne küldd újra a módváltást.");}
    }catch{
      if(!committed)setError("Az indítási mód mentése nem igazolható. "+
        "Nincs automatikus újrapróbálás vagy szerverindítás.");
    }finally{setBusy(false);}
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
          disabled={!status?.canChange||busy||!!preview}
          onChange={e=>{void prepare(e.target.checked);}}/>
        Indítás ASA API-val
      </label>
      <p className="muted small">
        {status?.useApi?"Beállítva: AsaApiLoader.exe":"Beállítva: közvetlen ArkAscendedServer.exe"}.
        A kapcsoló a kiválasztott maphoz tartozik, és csak a következő
        indítást érinti; az aktuális szervert nem állítja le és nem indítja újra.
      </p>
      {status?.approvalRequired&&<div className="warning-inline">
        A korábbi korábbi Manager indítási módját még külön jóvá kell hagyni.
        {!status.useApi&&<button type="button" className="secondary"
          disabled={!status.canChange||busy||!!preview}
          onClick={()=>{void prepare(false);}}>
          Közvetlen ASA mód jóváhagyása
        </button>}
      </div>}
      {!status?.canChange&&<p className="warning-inline" role="status">
        {observedOnly?
          "A megfigyelt mapot előbb regisztrálni és átvenni kell.":
          "A mód csak igazoltan leállított helyi mapon, ellenőrzött Loader/API "+
          "fájlokkal és rendezett műveleti naplóval módosítható."}
      </p>}
      {preview&&<div className="warning-inline" role="region"
        aria-label="ASA API indítási mód jóváhagyása">
        <b>{preview.mapName} – {preview.useApi?"ASA API Loader":"Közvetlen ASA"}</b>
        <p>Ez kizárólag a Manager maponkénti indítási beállítását menti.
          A következő Start külön művelet, a Store és karakteradatok változatlanok.</p>
        <div className="settings-actions">
          <button type="button" className="primary" disabled={busy}
            onClick={()=>{void save();}}>Indítási mód jóváhagyása</button>
          <button type="button" className="secondary" disabled={busy}
            onClick={()=>setPreview(null)}>Mégse</button>
        </div>
      </div>}
      {notice&&<div role="status" className="success-inline">
        <CheckCircle2 size={16}/>{notice}</div>}
      {error&&<div role="alert" className="error-inline">
        <AlertTriangle size={16}/>{error}</div>}
    </div>
  </section>;
}
