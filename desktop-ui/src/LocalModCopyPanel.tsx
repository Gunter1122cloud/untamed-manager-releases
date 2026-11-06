import {useEffect,useMemo,useState} from "react";
import {AlertTriangle,CheckCircle2,Copy,Layers3,Shield} from "lucide-react";
import {managerBridge,type ModCopyMode,type ModCopyPreview,type PublicMap} from "./bridge";

export function LocalModCopyPanel({maps,sourceMapId,onApplied}:{
  maps:PublicMap[];sourceMapId:string;onApplied:()=>void
}){
  const [targets,setTargets]=useState<string[]>([]);
  const [mode,setMode]=useState<ModCopyMode>("merge");
  const [preview,setPreview]=useState<ModCopyPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const eligible=useMemo(()=>maps.filter(m=>
    m.id!==sourceMapId && !m.storeProtected && !m.maintenance),[maps,sourceMapId]);
  const source=maps.find(m=>m.id===sourceMapId);
  const sourceAllowed=Boolean(source && !source.storeProtected && !source.maintenance);
  useEffect(()=>{
    setTargets([]);setPreview(null);setApproved(false);
    setError("");setNotice("");
  },[sourceMapId]);
  const invalidate=()=>{
    setPreview(null);setApproved(false);setError("");setNotice("");
  };
  const toggle=(id:string)=>{
    setTargets(previous=>previous.includes(id)?
      previous.filter(x=>x!==id):[...previous,id]);invalidate();
  };
  const prepare=async()=>{
    if(!sourceAllowed||targets.length===0||busy)return;
    setBusy(true);invalidate();
    try{setPreview(await managerBridge.modCopyPreview(sourceMapId,targets,mode));}
    catch{setError("A másolás előnézetét a natív Manager elutasította. A forrás, célmapok és modlisták ellenőrzendők.");}
    finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!preview||!preview.changed||!approved||busy)return;
    const token=preview.token;
    setBusy(true);setPreview(null);setApproved(false);setError("");
    try{
      const result=await managerBridge.modCopyApply(token);
      setNotice(result.changedMaps.length+
        " helyi map modkonfigurációja mentve. Az ASA-modok tényleges betöltése csak későbbi, ellenőrzött indításkor történik.");
      setTargets([]);onApplied();
    }catch{
      setError("A modmásolás nem lett igazolva. Lehetséges időközbeni konfigurációváltozás; frissítsd a listát és készíts új előnézetet.");
    }finally{setBusy(false);}
  };
  return <section className="premium-panel" aria-label="Modlista másolása több mapra">
    <div className="panel-head"><h3><Layers3 size={18}/> Copy to – többmapos modlista</h3>
      <span className="mod-tag amber">Csak helyi Manager</span></div>
    <p className="muted small">A kijelölt mapok indítási modkonfigurációja módosul.
      A másik gép, az eredeti Store-v1, az ASA-szerverek és a játékosmentések érintetlenek maradnak.</p>
    {!sourceAllowed&&<div className="warning-inline"><Shield size={16}/>
      Védett Store-v1 vagy karbantartás alatt álló forrásmapból nem módosíthatók az indítási beállítások.</div>}
    <label>Másolási mód
      <select aria-label="Modmásolási mód" value={mode} disabled={busy}
        onChange={e=>{setMode(e.target.value as ModCopyMode);invalidate();}}>
        <option value="merge">Merge – hiányzó modok hozzáadása, meglévők megőrzése</option>
        <option value="replace">Replace – cél modlistájának teljes cseréje</option>
      </select>
    </label>
    <div className="premium-mod-copy-targets" role="group" aria-label="Célmapok kiválasztása">
      {eligible.length===0?<p className="muted small">Nincs biztonságosan szerkeszthető további helyi map.</p>:
        eligible.map(map=><label key={map.id} className="plugin-row">
          <input type="checkbox" checked={targets.includes(map.id)} disabled={busy}
            onChange={()=>toggle(map.id)}/>
          <span><strong>{map.name}</strong><small>{map.running?
            "Futó ASA – csak a következő indítási konfiguráció módosul":
            "Helyi map"}</small></span>
        </label>)}
    </div>
    <div className="draft-actions">
      <button className="secondary" type="button" disabled={busy||!sourceAllowed||targets.length===0}
        onClick={()=>{void prepare();}}><Copy size={16}/> Változások előnézete</button>
    </div>
    {preview&&<div className="historical-preview" role="region" aria-label="Többmapos modmásolás előnézete">
      <h3>{mode==="replace"?"Teljes csere":"Hiányzók hozzáadása"} · {preview.targets.length} map</h3>
      {preview.targets.map(target=><div key={target.mapId} className="draft-difference">
        <b>{target.mapName}</b>
        <span>Előtte: {target.before.active.length} aktív, {target.before.passive.length} passzív, {target.before.disabled.length} letiltott</span>
        <span>Utána: {target.after.active.length} aktív, {target.after.passive.length} passzív, {target.after.disabled.length} letiltott</span>
        <strong>{target.changed?"Változik":"Változatlan"}</strong>
        {target.changed&&<small>Végső mod ID-k: {[...target.after.active,...target.after.passive].join(", ")||"nincs"}</small>}
      </div>)}
      <p className="muted small">Műveletek: {preview.operationCount}. A változtatás egyszerre kerül mentésre a helyi Manager konfigurációjában; nem indítja újra az ASA-t.</p>
      {preview.changed&&<label><input type="checkbox" checked={approved}
        onChange={e=>setApproved(e.target.checked)} disabled={busy}/>
        Megvizsgáltam a célmapokat, és jóváhagyom a konfigurációmódosítást.</label>}
      <div className="draft-actions">
        <button type="button" className="primary" disabled={!approved||!preview.changed||busy}
          onClick={()=>{void apply();}}>Jóváhagyás és mentés</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={invalidate}>Mégsem</button>
      </div>
    </div>}
    {notice&&<div className="success-inline" role="status"><CheckCircle2 size={16}/>{notice}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={16}/>{error}</div>}
  </section>;
}
