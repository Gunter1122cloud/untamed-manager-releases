import {useState} from "react";
import {emptyEngram,inspectNamedEngrams,editNamedEngram,type NamedEngram,type NamedRow} from "./state/namedEngrams";
export function NamedEngramEditor({content,disabled,onDraft}:{content:string;disabled:boolean;onDraft:(next:string)=>void}){
  const [state,setState]=useState<{base:string;rows:NamedRow[];unknown:number}|null>(null);
  const [choice,setChoice]=useState<NamedRow|null>(null);
  const [draft,setDraft]=useState<NamedEngram>(emptyEngram);
  const [error,setError]=useState("");
  const [query,setQuery]=useState("");
  const active=state?.base===content?state:null;
  const load=()=>{try{
    const found=inspectNamedEngrams(content);
    setState({base:content,rows:found.rows,unknown:found.unknown.length});
    setChoice(null);setDraft(emptyEngram());setError("");
  }catch(e){setError(e instanceof Error?e.message:"Engram beolvasási hiba.");}};
  const apply=(remove=false)=>{
    if(!active||disabled||remove&&!choice)return;
    try{onDraft(editNamedEngram(content,choice,remove?null:draft));setState(null);setChoice(null);setDraft(emptyEngram());setError("");}
    catch(e){setError(e instanceof Error?e.message:"Engram szerkesztési hiba.");}
  };
  const boolField=(name:"hidden"|"prereq",label:string)=><label>{label}
    <select aria-label={label} disabled={disabled} value={draft[name]}
      onChange={e=>setDraft({...draft,[name]:e.target.value as NamedEngram["hidden"]})}>
      <option value="">Nincs felülírás</option><option value="True">True</option><option value="False">False</option>
    </select></label>;
  return <details className="ini-preview" style={{display:"block",marginBottom:12}}>
    <summary>Engram Editor · név szerinti felülírások</summary>
    <p className="muted small">Csak a megnyitott Game.ini piszkozatát szerkeszti. Add meg a valódi EngramClassName azonosítót;
      a modok classneveit nem találjuk ki. Ismeretlen mezős sorok és index alapú felülírások érintetlenek.</p>
    {!active?<button type="button" className="secondary" disabled={disabled} onClick={load}>Engram bejegyzések betöltése</button>:<>
      <p>{active.rows.length} értelmezett név szerinti sor · {active.unknown} nem értelmezett sor</p>
      {active.unknown>0&&<p className="warning-inline">A nem értelmezett sorokat csak a nyers INI-ben kezeld. Új felülírás és módosítás blokkolva, amíg a classazonosság nem ellenőrizhető.</p>}
      <input aria-label="Engram class keresése" value={query} onChange={e=>setQuery(e.target.value)} placeholder="Class keresése"/>
      <div style={{maxHeight:180,overflowY:"auto"}}>
        {active.rows.filter(row=>row.value.className.toLowerCase().includes(query.toLowerCase())).map(row=>
          <button type="button" key={row.index} className="secondary" disabled={disabled}
            aria-pressed={choice?.index===row.index} onClick={()=>{setChoice(row);setDraft({...row.value});setError("");}}>
            {row.value.className}
          </button>)}
      </div>
      <button type="button" className="secondary" disabled={disabled} onClick={()=>{setChoice(null);setDraft(emptyEngram());setError("");}}>Új név szerinti engram</button>
      <div style={{display:"grid",gap:8}}>
        <label>Engram class
          <input aria-label="Engram class" value={draft.className} disabled={disabled||!!choice} maxLength={180}
            onChange={e=>setDraft({...draft,className:e.target.value})} placeholder="EngramEntry_..._C"/>
        </label>
        {boolField("hidden","Engram elrejtése")}
        <label>Engram pontköltség <input aria-label="Engram pontköltség" value={draft.cost} disabled={disabled}
          inputMode="numeric" maxLength={7} onChange={e=>setDraft({...draft,cost:e.target.value})}/></label>
        <label>Feloldási szint <input aria-label="Engram feloldási szint" value={draft.level} disabled={disabled}
          inputMode="numeric" maxLength={7} onChange={e=>setDraft({...draft,level:e.target.value})}/></label>
        {boolField("prereq","Előfeltételek eltávolítása")}
      </div>
      <button type="button" className="primary" disabled={disabled||active.unknown>0} onClick={()=>apply()}>Engram felülírás beillesztése a piszkozatba</button>
      {choice&&<button type="button" className="secondary" disabled={disabled} onClick={()=>apply(true)}>Kiválasztott felülírás eltávolítása a piszkozatból</button>}
      <button type="button" className="quiet" onClick={()=>{setState(null);setError("");}}>Elvetés</button>
    </>}
    {state&&!active&&<p className="warning-inline">Az INI-piszkozat megváltozott. Töltsd be újra az engramokat.</p>}
    {error&&<p role="alert" className="warning-inline">{error}</p>}
  </details>;
}
