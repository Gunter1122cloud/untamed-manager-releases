import {describeNativeError} from "./state/nativeErrors";
import {useState} from "react";
import {managerBridge,type RelocationPreview} from "./bridge";
export function InstallationRelocationPanel({mapId,running,onConfigured}:{mapId:string;running:boolean;onConfigured:()=>Promise<void>}){
  const [target,setTarget]=useState("");
  const [preview,setPreview]=useState<RelocationPreview|null>(null);
  const [busy,setBusy]=useState(false);
  const [notice,setNotice]=useState("");
  const run=async(apply:boolean)=>{
    if(busy||running||apply&&!preview)return;
    setBusy(true);setNotice("");
    const approved=preview;setPreview(null);
    try{
      if(apply&&approved){
        const reply=await managerBridge.relocationApply(approved.token);
        if(reply.mapId!==mapId||!reply.profileSwitched||!reply.sourceRetained||reply.serverStarted)throw Error("UM-MOVE-RECEIPT");
        setNotice("Ellenőrzött másolat elkészült, a profil átállítva. Az eredeti telepítés megmaradt. A szerver nem indult el.");
        try{await onConfigured();}catch{setNotice("A profil átállt, de a felület frissítése nem sikerült. Ne ismételd meg a költöztetést.");}
      }else{
        const reply=await managerBridge.relocationPreview(mapId,target);
        if(reply.mapId!==mapId||!reply.sourceRetained)throw Error("UM-MOVE-PREVIEW");
        setPreview(reply);
      }
    }catch(e){setNotice(describeNativeError(e instanceof Error?e.message:"UM-MOVE-UNAVAILABLE","A költöztetés eredménye nem igazolható. Az eredeti fájlok megmaradnak; ellenőrizd a profil útvonalát és az új mappát."));}
    finally{setBusy(false);}
  };
  return <details className="premium-panel" style={{padding:16}}>
    <summary>Telepítés költöztetése másik SSD-re</summary>
    <p className="muted small">Különálló, nem Store/cluster maphoz. Teljes másolat, SHA-256 ellenőrzés, majd helyi profil-átállítás. Az eredeti telepítés megmarad; ne indítsd el külön a régi példányt. Futó mapot nem állít le. Az útvonalhoz kötött korábbi ütemezéseket külön ellenőrizd.</p>
    <label>Új, még nem létező telepítési könyvtár <input aria-label="Költöztetés célmappája" value={target} maxLength={500} disabled={busy} placeholder="D:\\ASA\\Map" onChange={e=>{setTarget(e.target.value);setPreview(null);}}/></label>
    <button className="secondary" type="button" disabled={running||busy||!target.trim()} onClick={()=>void run(false)}>Tárhely és fájllista ellenőrzése</button>
    {preview&&<><p>{preview.files.toLocaleString("hu-HU")} fájl · {(preview.bytes/1073741824).toFixed(2)} GiB · szabad {(preview.freeBytes/1073741824).toFixed(2)} GiB</p>
      <button className="primary" type="button" disabled={running||busy} onClick={()=>void run(true)}>Ellenőrzött másolás és profil-átállítás</button></>}
    {busy&&<p role="status">Natív ellenőrzés / másolás folyamatban. Ne zárd be a Managert.</p>}
    {notice&&<p role="status">{notice}</p>}
  </details>;
}
