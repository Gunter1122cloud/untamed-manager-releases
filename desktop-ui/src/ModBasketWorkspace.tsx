import {useEffect,useState} from "react";
import {AlertTriangle,ArrowDown,ArrowUp,CheckCircle2,Layers3,Shield,Trash2} from "lucide-react";
import {managerBridge,type ModDraftMode,type ModDraftSave,type ModDraftState} from "./bridge";

export type BasketMod={id:string;name:string;mode:Exclude<ModDraftMode,"remove">};

export function ModBasketWorkspace({mapId,mapName,items,onUpdate,onClear,onSaved,onCopy}:{
  mapId:string;mapName:string;items:BasketMod[];
  onUpdate:(next:BasketMod[])=>void;onClear:()=>void;
  onSaved:(saved:ModDraftSave)=>void;onCopy:()=>void
}){
  const [preview,setPreview]=useState<ModDraftState|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const [saved,setSaved]=useState(false);
  // Preview tokens are native in-memory drafts. A change to the basket
  // invalidates its approval; no configuration is written until save.
  const invalidate=()=>{
    if(preview)void managerBridge.discardModDraft(preview.draftId).catch(()=>{});
    setPreview(null);setApproved(false);setError("");setNotice("");setSaved(false);
  };
  useEffect(()=>{setPreview(null);setApproved(false);setError("");setNotice("");setSaved(false);},[mapId]);
  const change=(next:BasketMod[])=>{if(busy)return;invalidate();onUpdate(next);};
  const move=(index:number,delta:number)=>{
    const next=[...items],destination=index+delta;
    if(destination<0||destination>=next.length)return;
    [next[index],next[destination]]=[next[destination],next[index]];
    change(next);
  };
  const prepare=async()=>{
    if(!mapId||!items.length||busy)return;
    setBusy(true);setError("");setNotice("");setApproved(false);
    let token="";
    try{
      if(preview){await managerBridge.discardModDraft(preview.draftId);setPreview(null);}
      const initial=await managerBridge.startModDraft(mapId);
      token=initial.draftId;
      let result=initial;
      for(const item of items){
        if(!/^\\d{5,12}$/.test(item.id))throw new Error("INVALID_MOD_ID");
        result=await managerBridge.stageModDraft(token,item.id,item.mode);
      }
      setPreview(result);
      setNotice(result.dirty?"Minden kiválasztott mod bekerült az ellenőrizhető natív piszkozatba.":"A kiválasztott modok már a kívánt állapotban szerepelnek.");
      token=""; // keep the valid draft for explicit approval
    }catch{
      if(token)await managerBridge.discardModDraft(token).catch(()=>{});
      setPreview(null);
      setError("A natív modpiszkozat nem készíthető el. A map konfigurációja változatlan.");
    }finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!preview?.dirty||!approved||busy)return;
    setBusy(true);setError("");
    try{
      const receipt=await managerBridge.saveModDraft(preview.draftId);
      if(!receipt.saved)throw new Error("NOT_VERIFIED");
      onSaved(receipt);
      setPreview(null);setApproved(false);setSaved(true);
      setNotice("A map modkonfigurációja ellenőrzötten mentve. ASA-betöltés még nem igazolt; a modlistát most helyi célmapokra is másolhatod.");
      onClear();
    }catch{
      setPreview(null);setApproved(false);
      setError("A mentés nem igazolható; ellenőrizd az eredeti map modlistáját, és készíts új előnézetet.");
    }finally{setBusy(false);}
  };
  return <section className="premium-panel" aria-label="Többmodos CurseForge kosár">
    <header className="premium-panel-heading"><h2><Layers3 size={18}/> Modkosár · {mapName} ({items.length})</h2>
      <span className="premium-small-muted">Kiválasztva · még nincs mentve</span></header>
    <p className="muted small">Keresés és lapozás közben gyűjts össze több modot. A kosár nem módosítja a mapot; minden elem egyetlen natív piszkozatba kerül, majd jóváhagyással menthető.</p>
    {items.length===0&&!saved&&<p className="empty">Válassz modokat a fenti CurseForge katalógusból.</p>}
    {items.map((item,index)=><div className="mod-item" key={item.id}>
      <div className="mod-title"><strong>{index+1}. {item.name}</strong><small>CurseForge ID: {item.id}</small></div>
      <label>Állapot
        <select aria-label={item.id+" kosárállapota"} value={item.mode} disabled={busy}
          onChange={e=>change(items.map(m=>m.id===item.id?{...m,mode:e.target.value as BasketMod["mode"]}:m))}>
          <option value="active">Aktív</option><option value="passive">Passzív</option>
          <option value="disabled">Letiltva</option>
        </select>
      </label>
      <button type="button" className="quiet" aria-label={item.id+" feljebb"} disabled={busy||index===0}
        onClick={()=>move(index,-1)}><ArrowUp size={16}/></button>
      <button type="button" className="quiet" aria-label={item.id+" lejjebb"} disabled={busy||index===items.length-1}
        onClick={()=>move(index,1)}><ArrowDown size={16}/></button>
      <button type="button" className="quiet" aria-label={item.id+" eltávolítása"} disabled={busy}
        onClick={()=>change(items.filter(m=>m.id!==item.id))}><Trash2 size={16}/></button>
    </div>)}
    <div className="draft-actions">
      <button type="button" className="secondary" disabled={busy||!items.length}
        onClick={()=>{invalidate();onClear();}}>Kosár ürítése</button>
      <button type="button" className="primary" disabled={busy||!items.length}
        onClick={()=>{void prepare();}}>Teljes modlista natív előnézete</button>
      {saved&&<button type="button" className="secondary" onClick={onCopy}>Mentett lista alkalmazása több mapra</button>}
    </div>
    {preview&&<div className="draft-differences" role="region" aria-label="Modkosár mentési előnézete">
      <h3>Natív piszkozat · {preview.differences.length} módosítás</h3>
      {preview.differences.map(d=><div className="draft-difference" key={d.modId}>
        <b>{d.modId}</b><span>{d.beforeMode??"Nincs"}</span><strong>{d.afterMode??"Eltávolítva"}</strong>
      </div>)}
      <label><input type="checkbox" checked={approved} disabled={busy||!preview.dirty}
        onChange={e=>setApproved(e.target.checked)}/> Jóváhagyom {mapName} modkonfigurációjának mentését.</label>
      <div className="draft-actions">
        <button type="button" className="primary" disabled={busy||!approved||!preview.dirty}
          onClick={()=>{void apply();}}>Jóváhagyás és modlista mentése</button>
        <button type="button" className="secondary" disabled={busy} onClick={invalidate}>Előnézet elvetése</button>
      </div>
    </div>}
    {notice&&<div role="status" className="success-inline"><CheckCircle2 size={16}/>{notice}</div>}
    {error&&<div role="alert" className="error-inline"><AlertTriangle size={16}/>{error}</div>}
    <div className="warning-inline"><Shield size={16}/> A konfiguráció mentése nem jelent CurseForge-fájltelepítést vagy igazolt ASA-betöltést. Távoli map nem módosítható.</div>
  </section>;
}
