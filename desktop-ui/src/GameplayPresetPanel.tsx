import {useState} from "react";
import {presetItems,planPreset,type PresetId} from "./state/gameplayPresets";
export function GameplayPresetPanel({content,disabled,onDraft}:{content:string;disabled:boolean;onDraft:(next:string)=>void}){
  const [id,setId]=useState<PresetId>("viking");
  const [selected,setSelected]=useState<string[]>(()=>presetItems("viking").map(x=>x.className));
  const [custom,setCustom]=useState("");
  const [review,setReview]=useState<{base:string;result:ReturnType<typeof planPreset>}|null>(null);
  const [error,setError]=useState("");
  const preview=review?.base===content?review.result:null;
  const prepare=()=>{
    setReview(null);setError("");
    try{const additional=custom.split(/\r?\n/).map(x=>x.trim()).filter(Boolean);
      setReview({base:content,result:planPreset(content,[...selected,...additional])});}
    catch(e){setError(e instanceof Error?e.message:"Preset előnézeti hiba.");}
  };
  return <details className="ini-preview" style={{display:"block",marginBottom:12}}>
    <summary>Primitive / Viking játékmenet-profilok</summary>
    <p className="muted small">Szerkeszthető alap technológiai tiltólista. Nem teljes Primitive Plus vagy Viking modkonverzió,
      nem tiltja automatikusan a DLC-ket, nem töröl tárgyakat, és nem változtatja meg a meglévő lootot.
      A Viking alaplistában a Crossbow nincs tiltva. A pontos mod classokat saját listából add meg.</p>
    <label>Profil <select aria-label="Játékmenet preset" disabled={disabled} value={id} onChange={e=>{
      const next=e.target.value as PresetId;setId(next);setSelected(presetItems(next).map(x=>x.className));setReview(null);setError("");
    }}><option value="primitive">Primitive · szigorúbb alaplista</option><option value="viking">Viking · középkori alaplista</option></select></label>
    <div style={{maxHeight:220,overflowY:"auto"}}>
      {presetItems(id).map(x=><label key={x.className} style={{display:"block"}}>
        <input type="checkbox" disabled={disabled} checked={selected.includes(x.className)} onChange={e=>{
          setSelected(old=>e.target.checked?[...old,x.className]:old.filter(y=>y!==x.className));setReview(null);
        }}/> {x.label} · <small>{x.className}</small></label>)}
    </div>
    <label>További ellenőrzött mod engram classok (soronként egy)
      <textarea aria-label="Egyedi preset engram classok" maxLength={18000} value={custom} disabled={disabled}
        onChange={e=>{setCustom(e.target.value);setReview(null);}}/>
    </label>
    <button type="button" className="secondary" disabled={disabled} onClick={prepare}>Preset változtatások előnézete</button>
    {preview&&<div>
      <p>{preview.changes.filter(x=>x.action!=="unchanged").length} módosítás · {preview.changes.filter(x=>x.action==="unchanged").length} már tiltott</p>
      {preview.warnings.map(w=><p key={w} className="warning-inline">{w}</p>)}
      <div style={{maxHeight:160,overflowY:"auto"}}>{preview.changes.map(x=><div key={x.className}>{x.className} · {x.action==="add"?"Új tiltás":x.action==="hide"?"Elrejtés":"Változatlan"}</div>)}</div>
      <button type="button" className="primary" disabled={disabled||preview.next===content} onClick={()=>{onDraft(preview.next);setReview(null);}}>Preset beillesztése a Game.ini piszkozatba</button>
    </div>}
    {review&&!preview&&<p className="warning-inline">Az INI megváltozott. Új előnézet szükséges.</p>}
    {error&&<p className="warning-inline" role="alert">{error}</p>}
  </details>;
}
