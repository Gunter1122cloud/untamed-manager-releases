import {useEffect,useMemo,useState} from "react";
import {
  AlertTriangle, ArrowDownUp, CheckCircle2, ChevronDown, Filter, GripVertical,
  Layers3, Package, Plus, Search, Shield, SlidersHorizontal
} from "lucide-react";
import {managerBridge,type ConfiguredMods,type CurseForgeProject,type ModDraftMode} from "./bridge";

export type ConfiguredRow={
  id:string;position:number;mode:"active"|"passive"|"disabled";
};

type Props={
  info:ConfiguredMods|null;mapName:string;
  onStage:(id:string,mode:ModDraftMode)=>void;
  onOpenCatalog:()=>void;onOpenEditor:()=>void;
  draftChanges:number;
};

export function PremiumModBoard({info,mapName,onStage,onOpenCatalog,onOpenEditor,draftChanges}:Props){
  const [term,setTerm]=useState("");
  const [filter,setFilter]=useState<"all"|"active"|"passive"|"disabled">("all");
  const [selected,setSelected]=useState<string|null>(null);
  const [details,setDetails]=useState<CurseForgeProject|null>(null);
  const [loadingDetail,setLoadingDetail]=useState(false);
  const [detailMessage,setDetailMessage]=useState("");
  const [orderDesc,setOrderDesc]=useState(false);
  const rows=useMemo<ConfiguredRow[]>(()=>{
    if(!info)return [];
    return [
      ...info.active.map((id,position)=>({id,position,mode:"active" as const})),
      ...info.passive.map((id,position)=>({id,position,mode:"passive" as const})),
      ...info.disabled.map((id,position)=>({id,position,mode:"disabled" as const}))
    ];
  },[info]);
  const visible=useMemo(()=>{
    const needle=term.trim().toLocaleLowerCase("hu-HU");
    const filtered=rows.filter(r=>(filter==="all"||r.mode===filter)&&
      (!needle||r.id.includes(needle)||details?.mod?.id===Number(r.id)&&
        details.mod.name.toLocaleLowerCase("hu-HU").includes(needle)));
    return orderDesc?[...filtered].reverse():filtered;
  },[term,filter,rows,orderDesc,details]);
  useEffect(()=>{
    setSelected(null);setTerm("");setFilter("all");setDetails(null);setDetailMessage("");
  },[mapName]);
  useEffect(()=>{
    if(rows.length && (!selected||!rows.some(r=>r.id===selected)))setSelected(rows[0].id);
    if(!rows.length)setSelected(null);
  },[rows,selected]);
  useEffect(()=>{
    let active=true;
    setDetails(null);setDetailMessage("");
    if(!selected)return;
    setLoadingDetail(true);
    managerBridge.catalogProject(Number(selected)).then(data=>{
      if(active)setDetails(data);
    }).catch(()=>{
      if(active)setDetailMessage("CurseForge-metaadat nem érhető el; a helyi modállapot változatlan.");
    }).finally(()=>{if(active)setLoadingDetail(false);});
    return()=>{active=false;};
  },[selected]);
  const focus=rows.find(r=>r.id===selected)??null;
  const status=(mode:ConfiguredRow["mode"])=>mode==="active"?"Aktív":mode==="passive"?"Passzív":"Letiltva";
  return <>
    <div className="premium-mod-toolbar">
      <label className="premium-mod-search"><Search size={18}/>
        <input aria-label="Konfigurált mod keresése" placeholder="Mod vagy projektazonosító keresése..."
          value={term} onChange={e=>setTerm(e.target.value)}/>
      </label>
      <span className="premium-mod-count"><Layers3 size={17}/> Konfigurált ({rows.length})</span>
      <button type="button" className="secondary" onClick={onOpenCatalog}>
        <Search size={16}/> CurseForge kereső
      </button>
      <label className="premium-mod-filter"><Filter size={16}/>
        <span className="sr-only">Mod állapot szűrő</span>
        <select aria-label="Mod állapot szűrő" value={filter}
          onChange={e=>setFilter(e.target.value as typeof filter)}>
          <option value="all">Összes mód</option>
          <option value="active">Aktív</option>
          <option value="passive">Passzív</option>
          <option value="disabled">Letiltva</option>
        </select><ChevronDown size={14} aria-hidden="true"/>
      </label>
      <button type="button" className="primary premium-mod-add" onClick={onOpenEditor}>
        <Plus size={17}/> Mod hozzáadása
      </button>
    </div>
    <div className="premium-mod-grid">
      <section className="premium-panel premium-mod-list-panel">
        <header className="premium-panel-heading">
          <h2><Layers3 size={19}/> Modlista <span>({mapName})</span></h2>
          <span className="premium-small-muted">{visible.length} / {rows.length} mod</span>
        </header>
        <div className="premium-mod-table" role="table" aria-label="Konfigurált modlista">
          <div className="premium-mod-column-head" role="row">
            <span>MOD</span><span>ÁLLAPOT</span>
            <button type="button" onClick={()=>setOrderDesc(v=>!v)}
              aria-label="Mod sorrend megfordítása">SORREND <ArrowDownUp size={13}/></button>
            <span>CÉLÁLLAPOT</span>
          </div>
          {!info&&<div className="premium-mod-empty">Modkonfiguráció beolvasása...</div>}
          {info&&rows.length===0&&<div className="premium-mod-empty">
            <Package size={28}/> Ezen a mapon nincs konfigurált mod.
            <button type="button" className="secondary" onClick={onOpenEditor}>Mod hozzáadása</button>
          </div>}
          {info&&rows.length>0&&visible.length===0&&<div className="premium-mod-empty">
            Nincs megfelelő találat. Módosítsd a keresést vagy a szűrőt.
          </div>}
          {visible.map((row,i)=><div className={`premium-mod-row ${selected===row.id?"selected":""}`}
            key={row.id+"-"+row.mode+"-"+row.position} role="row">
            <button type="button" role="cell" className="premium-mod-pick"
              onClick={()=>setSelected(row.id)} aria-label={row.id+" mod részletei"}>
              <span className="premium-mod-drag" aria-hidden="true"><GripVertical size={15}/></span>
              <span className="premium-mod-ordinal">{i+1}</span>
              {selected===row.id&&details?.mod.thumbnailUrl?
                <img className="premium-mod-thumb" src={details.mod.thumbnailUrl}
                  alt="" loading="lazy" referrerPolicy="no-referrer"/>:
                <span className="premium-mod-thumb placeholder"><Package size={23}/></span>}
              <span className="premium-mod-identity">
                <b>{selected===row.id&&details?.mod.name?details.mod.name:"Projekt · "+row.id}</b>
                <small>CurseForge ID: {row.id} · helyi konfiguráció</small>
              </span>
            </button>
            <span role="cell" className={`premium-mod-status mode-${row.mode}`}>{status(row.mode)}</span>
            <span role="cell" className="premium-mod-position">
              {row.mode==="disabled"?"—":row.position+1}
            </span>
            <button type="button" role="cell" className={`premium-mod-state-trigger ${row.mode==="active"?"enabled":""}`}
              onClick={()=>onStage(row.id,row.mode==="active"?"disabled":"active")}
              aria-label={row.id+" módjának előkészítése"}>
              <span className="premium-toggle" aria-hidden="true"><span/></span>
              <span>{row.mode==="active"?"Letiltás":"Aktiválás"}</span>
            </button>
          </div>)}
        </div>
        <div className="premium-panel-footnote"><Shield size={14}/>
          A lista indítási konfiguráció, nem igazolt ASA-betöltés. Sorrendváltoztatás a piszkozatban lehetséges.
        </div>
      </section>
      <aside className="premium-panel premium-mod-details">
        <header className="premium-panel-heading"><h2><SlidersHorizontal size={19}/>
          {details?.mod.name|| (selected?"Mod részletei":"Válassz modot")}
        </h2></header>
        {focus?<div className="premium-mod-details-body">
          <div className="premium-mod-feature">
            {details?.mod.thumbnailUrl?<img src={details.mod.thumbnailUrl}
              alt="" loading="lazy" referrerPolicy="no-referrer"/>:
              <span className="premium-mod-feature-icon"><Package size={34}/></span>}
            <div><h3>{details?.mod.name||"Projekt · "+focus.id}</h3>
              <small>{details?.mod.authors?.join(", ")||"CurseForge projekt"} · ID: {focus.id}</small>
            </div>
          </div>
          {loadingDetail&&<p className="premium-small-muted">Hivatalos modadatok betöltése...</p>}
          {detailMessage&&<div className="premium-mod-advisory"><AlertTriangle size={14}/>{detailMessage}</div>}
          <p className="premium-mod-description">{details?.mod.summary||
            "A mod a helyi indítási konfigurációban szerepel. A részletes leírás az érvényes CurseForge API-kapcsolaton keresztül érhető el."}</p>
          <div className="premium-mod-mode-heading">Célállapot · piszkozat előkészítése</div>
          <div className="premium-mod-mode-pills">
            {(["active","passive","disabled"] as const).map(mode=>
              <button type="button" key={mode} className={focus.mode===mode?"active":""}
                aria-label={focus.id+" "+status(mode)+" mód előkészítése"}
                onClick={()=>onStage(focus.id,mode)}>{status(mode)}</button>)}
          </div>
          <div className="premium-mod-facts">
            <div><span>Helyi állapot</span><b>{status(focus.mode)}</b></div>
            <div><span>Modpozíció</span><b>{focus.mode==="disabled"?"—":focus.position+1}</b></div>
            <div><span>CurseForge verzió</span><b>{details?.mod.latestFile||"Nem ismert"}</b></div>
            <div><span>Függőségek</span><b>{details?.files?.[0]?.dependencies.length ?? "Nem ellenőrzött"}</b></div>
            <div><span>Telepített fájlok</span><b>Nem ellenőrzött</b></div>
          </div>
          <button type="button" className="secondary premium-details-action" onClick={onOpenCatalog}>
            <Search size={16}/> Keresés a CurseForge-ban
          </button>
        </div>:<div className="premium-mod-empty">Válassz egy konfigurált modot a listából.</div>}
      </aside>
    </div>
    <div className="premium-mod-bottom-bar">
      <div className="premium-draft-indicator">
        {draftChanges>0?<AlertTriangle size={19}/>:<CheckCircle2 size={19}/>}
        <div><b>{draftChanges>0?draftChanges+" függőben lévő módosítás":"Nincs függőben lévő piszkozat"}</b>
          <small>Védett konfiguráció · mentés csak kifejezett jóváhagyással</small></div>
      </div>
      <div className="premium-draft-buttons">
        <button type="button" className="secondary" onClick={onOpenEditor}>
          <SlidersHorizontal size={16}/> Változások előnézete
        </button>
        <button type="button" className="primary" onClick={onOpenEditor}
          disabled={draftChanges===0}>
          <CheckCircle2 size={17}/> Jóváhagyás a szerkesztőben
        </button>
      </div>
    </div>
  </>;
}
