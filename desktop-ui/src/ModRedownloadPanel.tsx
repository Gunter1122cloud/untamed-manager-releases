import {useState} from "react";
import {managerBridge,type ModRepairPreview,type ModRepairStatus} from "./bridge";
export function ModRedownloadPanel({mapId,ids}:{mapId:string;ids:string[]}){
  const [choice,setChoice]=useState("");
  const [preview,setPreview]=useState<ModRepairPreview|null>(null);
  const [status,setStatus]=useState<ModRepairStatus|null>(null);
  const [busy,setBusy]=useState(false);
  const [message,setMessage]=useState("");
  const project=ids.includes(choice)?choice:"";
  const read=async(prepare:boolean)=>{
    if(!project||busy)return;
    setBusy(true);setPreview(null);setStatus(null);setMessage("");
    try{
      const result=prepare?await managerBridge.modRepairPreview(mapId,project):await managerBridge.modRepairStatus(mapId,project);
      if(result.mapId.toLowerCase()!==mapId.toLowerCase()||result.modId!==project||!result.diskOnly||result.runtimeVerified)throw Error("UM-MOD-REPAIR-SCOPE");
      setStatus(result);if(prepare&&"token" in result)setPreview(result as ModRepairPreview);
    }catch(e){setMessage("A mod helyi állapota nem igazolható: "+(e instanceof Error?e.message:"UM-MOD-REPAIR"));}
    finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!preview||preview.modId!==project||busy)return;
    setBusy(true);setPreview(null);setMessage("");
    try{
      const receipt=await managerBridge.modRepairApply(preview.token);
      if(receipt.mapId.toLowerCase()!==mapId.toLowerCase()||receipt.modId!==project||receipt.serverStarted||receipt.remainingFolders!==0||receipt.downloadVerified)throw Error("UM-MOD-REPAIR-RECEIPT");
      setStatus(null);setMessage(receipt.quarantined+" mappa biztonságosan karanténban. Következő indításkor az ASA töltheti le a modot. A letöltés még nem igazolt.");
    }catch(e){setMessage("A karantén eredménye nem igazolt. Ellenőrizd a lemezállapotot; ne ismételd meg automatikusan. "+(e instanceof Error?e.message:""));}
    finally{setBusy(false);}
  };
  return <details className="premium-panel" style={{padding:12}}>
    <summary>Helyi modfájlok · ellenőrzés és újraletöltés előkészítése</summary>
    <p className="muted small">Csak az engedélyezett mod saját ASA cache-mappáit kezeli, igazoltan leállított, külön telepítésű mapnál.
      A régi fájlok nem törlődnek: .obsidian-mod-quarantine mappába kerülnek a telepítésen belül.
      Nem indítja újra a szervert. Indítás után ellenőrizhető a helyi fájlok újbóli megjelenése, ami nem bizonyít sikeres játékbeli betöltést.</p>
    <select aria-label="Újraletöltendő mod" value={project} disabled={busy} onChange={e=>{setChoice(e.target.value);setPreview(null);setStatus(null);setMessage("");}}>
      <option value="">Válassz projektet</option>{ids.map(id=><option key={id} value={id}>{id}</option>)}
    </select>
    <button type="button" className="secondary" disabled={!project||busy} onClick={()=>{void read(false);}}>Helyi modfájlok ellenőrzése</button>
    <button type="button" className="secondary" disabled={!project||busy} onClick={()=>{void read(true);}}>Karantén előellenőrzése</button>
    {status?.modId===project&&<p>{status.folders.length?status.folders.join(" · "):"Nincs azonosított helyi project/file mappa."}</p>}
    {preview?.modId===project&&<button type="button" className="primary" disabled={busy} onClick={()=>{void apply();}}>Karantén és újraletöltés előkészítése</button>}
    {message&&<p role="status">{message}</p>}
  </details>;
}
