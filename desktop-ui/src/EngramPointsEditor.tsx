import {useState} from "react";
import {readEngramPoints,writeEngramPoints} from "./state/engramPoints";
export function EngramPointsEditor({content,disabled,onDraft}:{content:string;disabled:boolean;onDraft:(next:string)=>void}){
  const [draft,setDraft]=useState<{base:string;rows:string[]}|null>(null);
  const [error,setError]=useState("");
  const active=draft?.base===content?draft:null;
  const open=()=>{
    try{setDraft({base:content,rows:readEngramPoints(content).map(String)});setError("");}
    catch(e){setError(e instanceof Error?e.message:"Nem értelmezhető Game.ini.");}
  };
  const apply=()=>{
    if(!active||disabled)return;
    if(active.rows.some(v=>! /^(?:0|[1-9]\d{0,6})$/.test(v.trim()))){
      setError("Minden szinthez nemnegatív egész pontérték szükséges.");return;
    }
    try{onDraft(writeEngramPoints(content,active.rows.map(Number)));setDraft(null);setError("");}
    catch(e){setError(e instanceof Error?e.message:"Engram-pont módosítás sikertelen.");}
  };
  return <details className="ini-preview" style={{display:"block",marginBottom:12}}>
    <summary>Szintenkénti engram pontok</summary>
    <p className="muted small">A Game.ini OverridePlayerLevelEngramPoints sorainak sorrendje adja a szinteket.
      Csak a megnyitott INI-piszkozat változik. A játék alapértékei nem kerülnek automatikusan kitöltésre.</p>
    {!active?<button type="button" className="secondary" disabled={disabled} onClick={open}>Engram pontsorok betöltése</button>:<>
      <p>{active.rows.length} megadott szint · összesen {active.rows.reduce((sum,v)=>sum+(Number(v)||0),0)} EP</p>
      <div style={{maxHeight:280,overflowY:"auto"}}>
        {active.rows.map((v,i)=><label key={i} style={{display:"flex",alignItems:"center",gap:12,marginBottom:4}}>
          <span style={{minWidth:90}}>Szint {i+1}</span>
          <input aria-label={"Engram pont, szint "+(i+1)} inputMode="numeric" maxLength={7} value={v} disabled={disabled}
            onChange={e=>setDraft({...active,rows:active.rows.map((old,j)=>j===i?e.target.value:old)})}/>
        </label>)}
      </div>
      <button type="button" className="secondary" disabled={disabled||active.rows.length>=2000}
        onClick={()=>setDraft({...active,rows:[...active.rows,"0"]})}>Következő szint hozzáadása</button>
      <button type="button" className="secondary" disabled={disabled||active.rows.length===0}
        onClick={()=>setDraft({...active,rows:active.rows.slice(0,-1)})}>Utolsó szint eltávolítása</button>
      <button type="button" className="primary" disabled={disabled} onClick={apply}>Engram pontok beillesztése az INI-piszkozatba</button>
      <button type="button" className="quiet" onClick={()=>{setDraft(null);setError("");}}>Elvetés</button>
    </>}
    {draft&&!active&&<p className="warning-inline">Az INI-piszkozat megváltozott. Töltsd be újra a pontsorokat.</p>}
    {error&&<p role="alert" className="warning-inline">{error}</p>}
  </details>;
}
