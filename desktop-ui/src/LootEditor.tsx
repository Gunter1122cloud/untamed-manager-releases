import {useState} from "react";
import {editCrate,inspectCrates,newCrate,newSet,newEntry,type LootCrate,type LootRow,type LootSet,type LootEntry} from "./state/lootCrates";
export function LootEditor({content,disabled,onDraft}:{content:string;disabled:boolean;onDraft:(next:string)=>void}){
  const [loaded,setLoaded]=useState<{base:string;rows:LootRow[];unknown:number}|null>(null);
  const [choice,setChoice]=useState<LootRow|null>(null);
  const [crate,setCrate]=useState<LootCrate>(newCrate);
  const [error,setError]=useState("");
  const active=loaded?.base===content?loaded:null;
  const load=()=>{try{const x=inspectCrates(content);setLoaded({base:content,rows:x.rows,unknown:x.unknown.length});setChoice(null);setCrate(newCrate());setError("");}
    catch(e){setError(e instanceof Error?e.message:"Loot olvasási hiba.");}};
  const apply=()=>{if(!active||disabled)return;try{onDraft(editCrate(content,choice,crate));setLoaded(null);setError("");}
    catch(e){setError(e instanceof Error?e.message:"Loot szerkesztési hiba.");}};
  const num=(label:string,value:number,change:(v:number)=>void)=><label style={{display:"inline-flex",gap:6,alignItems:"center",margin:4}}>
    {label}<input aria-label={label} type="number" step="any" style={{width:90}} value={Number.isFinite(value)?value:""} disabled={disabled}
      onChange={e=>change(e.target.value===""?NaN:Number(e.target.value))}/></label>;
  const flag=(label:string,value:boolean,change:(v:boolean)=>void)=><label style={{margin:6}}><input type="checkbox" checked={value} disabled={disabled} onChange={e=>change(e.target.checked)}/>{label}</label>;
  const setAt=(i:number,patch:Partial<LootSet>)=>setCrate(old=>({...old,sets:old.sets.map((s,j)=>i===j?{...s,...patch}:s)}));
  const entryAt=(i:number,j:number,patch:Partial<LootEntry>)=>setCrate(old=>({...old,sets:old.sets.map((s,k)=>k===i?{...s,entries:s.entries.map((e,l)=>l===j?{...e,...patch}:e)}:s)}));
  return <details className="ini-preview" style={{display:"block",marginBottom:12}}>
    <summary>Loot Editor · Supply Crate</summary>
    <p className="muted small">Többszintű loot: láda, item set, súlyozott item entry, mennyiség, minőség és blueprint esély.
      Ez a változat explicit egy class/entry, egyenletes count power és teljes mezőkészlet szerkesztését támogatja.
      Más formátumú meglévő sorokat nem ír felül. Csak az aktuális Game.ini piszkozat módosul.</p>
    {!active?<button type="button" className="secondary" disabled={disabled} onClick={load}>Loot bejegyzések betöltése</button>:<>
      <p>{active.rows.length} szerkeszthető láda · {active.unknown} nem támogatott sor</p>
      {active.unknown>0&&<p className="warning-inline">Nem támogatott sorok vannak. A menthető classazonosság nem igazolt; a nyers INI változatlan.</p>}
      <select aria-label="Loot láda kiválasztása" disabled={disabled} value={choice?String(choice.index):""}
        onChange={e=>{const row=active.rows.find(r=>String(r.index)===e.target.value)??null;setChoice(row);setCrate(row?structuredClone(row.crate):newCrate());setError("");}}>
        <option value="">Új láda</option>{active.rows.map(r=><option key={r.index} value={r.index}>{r.crate.className}</option>)}
      </select>
      <label>Láda class <input aria-label="Loot láda class" value={crate.className} disabled={disabled||!!choice} maxLength={180} onChange={e=>setCrate({...crate,className:e.target.value})}/></label>
      <div>{num("Min. set",crate.minSets,v=>setCrate({...crate,minSets:v}))}{num("Max. set",crate.maxSets,v=>setCrate({...crate,maxSets:v}))}
      {flag("Set ismétlés nélkül",crate.unique,v=>setCrate({...crate,unique:v}))}{flag("Hozzáfűzés az alap loothoz",crate.append,v=>setCrate({...crate,append:v}))}</div>
      {crate.sets.map((s,i)=><fieldset key={i} style={{margin:8,padding:10,border:"1px solid #555"}} disabled={disabled}>
        <legend>Item set {i+1}</legend>
        {num("Set súly",s.weight,v=>setAt(i,{weight:v}))}{num("Min. item",s.minItems,v=>setAt(i,{minItems:v}))}{num("Max. item",s.maxItems,v=>setAt(i,{maxItems:v}))}
        {flag("Item ismétlés nélkül",s.unique,v=>setAt(i,{unique:v}))}
        {s.entries.map((e,j)=><div key={j} style={{borderTop:"1px solid #555",padding:8}}>
          <label>Item class <input aria-label={"Item class "+(i+1)+"."+(j+1)} maxLength={180} value={e.item} disabled={disabled} onChange={x=>entryAt(i,j,{item:x.target.value})}/></label>
          <div>{num("Entry súly",e.weight,v=>entryAt(i,j,{weight:v}))}{num("Min. mennyiség",e.minQuantity,v=>entryAt(i,j,{minQuantity:v}))}{num("Max. mennyiség",e.maxQuantity,v=>entryAt(i,j,{maxQuantity:v}))}</div>
          <div>{num("Min. minőség",e.minQuality,v=>entryAt(i,j,{minQuality:v}))}{num("Max. minőség",e.maxQuality,v=>entryAt(i,j,{maxQuality:v}))}{num("BP esély (0–1)",e.blueprintChance,v=>entryAt(i,j,{blueprintChance:v}))}
          {flag("Mindig blueprint",e.forceBlueprint,v=>entryAt(i,j,{forceBlueprint:v}))}</div>
          <button type="button" className="secondary" disabled={disabled||s.entries.length===1} onClick={()=>setAt(i,{entries:s.entries.filter((_,k)=>k!==j)})}>Item eltávolítása</button>
        </div>)}
        <button type="button" className="secondary" disabled={disabled||s.entries.length>=32} onClick={()=>setAt(i,{entries:[...s.entries,newEntry()]})}>Item hozzáadása</button>
        <button type="button" className="secondary" disabled={disabled||crate.sets.length===1} onClick={()=>setCrate({...crate,sets:crate.sets.filter((_,k)=>k!==i)})}>Set eltávolítása</button>
      </fieldset>)}
      <button type="button" className="secondary" disabled={disabled||crate.sets.length>=12} onClick={()=>setCrate({...crate,sets:[...crate.sets,newSet()]})}>Item set hozzáadása</button>
      <button type="button" className="primary" disabled={disabled||active.unknown>0} onClick={apply}>Loot beillesztése az INI-piszkozatba</button>
      <button type="button" className="quiet" onClick={()=>setLoaded(null)}>Elvetés</button>
    </>}
    {loaded&&!active&&<p className="warning-inline">A piszkozat megváltozott. Töltsd be újra.</p>}
    {error&&<p role="alert" className="warning-inline">{error}</p>}
  </details>;
}
