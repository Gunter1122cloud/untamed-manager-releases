import {useEffect,useMemo,useState} from "react";
import {AlertTriangle,CheckCircle2,Copy,Layers3,Shield} from "lucide-react";
import {managerBridge,type ModCopyMode,type ModCopyPreview,type PublicMap} from "./bridge";

export function LocalModCopyPanel({maps,sourceMapId,onApplied}:{
  maps:PublicMap[];sourceMapId:string;onApplied:()=>void
}){
  const [targets,setTargets]=useState<string[]>([]);
  const [query,setQuery]=useState("");
  const [page,setPage]=useState(0);
  const pageSize=25;
  const [mode,setMode]=useState<ModCopyMode>("merge");
  const [preview,setPreview]=useState<ModCopyPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const eligible=useMemo(()=>maps.filter(m=>
    m.id!==sourceMapId && !m.maintenance && !m.observedReadOnlyImported &&
    !(m.freshStoreEnrolled&&m.running)),[maps,sourceMapId]);
  const filtered=useMemo(()=>eligible.filter(m=>{
    const needle=query.trim().toLocaleLowerCase("hu-HU");
    return !needle||m.name.toLocaleLowerCase("hu-HU").includes(needle)||
      m.id.toLocaleLowerCase("hu-HU").includes(needle);
  }),[eligible,query]);
  const pageCount=Math.max(1,Math.ceil(filtered.length/pageSize));
  const safePage=Math.min(page,pageCount-1);
  const visible=filtered.slice(safePage*pageSize,(safePage+1)*pageSize);
  const source=maps.find(m=>m.id===sourceMapId);
  const sourceAllowed=Boolean(source && !source.maintenance &&
    !source.observedReadOnlyImported &&
    !(source.freshStoreEnrolled&&source.running));
  useEffect(()=>{
    setTargets([]);setQuery("");setPage(0);setPreview(null);setApproved(false);
    setError("");setNotice("");
  },[sourceMapId]);
  const invalidate=()=>{
    setPreview(null);setApproved(false);setError("");setNotice("");
  };
  const toggle=(id:string)=>{
    setTargets(previous=>previous.includes(id)?
      previous.filter(x=>x!==id):previous.length>=25?previous:[...previous,id]);invalidate();
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
    <p className="muted small">A kijelölt mapok következő indításához használt
      modlistája módosul. Az eredeti eredeti Store-v1 mapoknál kizárólag a
      Manager dedikált modlistája változik, igazoltan leállított ASA/Loader mellett.
      A másik gép, a közös Store és a játékosmentések érintetlenek maradnak.</p>
    {!sourceAllowed&&<div className="warning-inline"><Shield size={16}/>
      Nem helyi, még át nem vett Store vagy karbantartás alatt álló forrásmap nem szerkeszthető.</div>}
    <label>Másolási mód
      <select aria-label="Modmásolási mód" value={mode} disabled={busy}
        onChange={e=>{setMode(e.target.value as ModCopyMode);invalidate();}}>
        <option value="merge">Merge – hiányzó modok hozzáadása, meglévők megőrzése</option>
        <option value="replace">Replace – cél modlistájának teljes cseréje</option>
      </select>
    </label>
    <div className="draft-actions">
      <button type="button" className="secondary" disabled={busy||!sourceAllowed||eligible.length===0}
        onClick={()=>{setTargets(eligible.slice(0,25).map(m=>m.id));invalidate();}}>
        {eligible.length>25?"Első 25 jogosult helyi célmap":"Összes jogosult helyi célmap"} kijelölése ({Math.min(eligible.length,25)})
      </button>
      <button type="button" className="quiet" disabled={busy||targets.length===0}
        onClick={()=>{setTargets([]);invalidate();}}>Kijelölések törlése</button>
      <span className="muted small">{targets.length} / {eligible.length} helyi célmap</span>
    </div>
    {eligible.length>25&&<p className="warning-inline">A natív előnézet legfeljebb 25 célmapot fogad egyszerre. A többi helyi map külön, jóváhagyott kötegben állítható be; ez nem globális host/map korlát.</p>}
    <label>Helyi célmap keresése
      <input aria-label="Modmásolás célmap keresése" type="search"
        disabled={busy} value={query}
        placeholder="Mapnév vagy azonosító"
        onChange={e=>{setQuery(e.target.value);setPage(0);invalidate();}}/>
    </label>
    <div className="draft-actions">
      <button type="button" className="secondary" disabled={busy||!sourceAllowed||visible.length===0}
        onClick={()=>{
          setTargets(old=>[...new Set([...old,...visible.map(m=>m.id)])].slice(0,25));
          invalidate();
        }}>Látható célmapok kijelölése (max. 25)</button>
      <button type="button" className="quiet" disabled={busy||safePage===0}
        onClick={()=>setPage(p=>Math.max(0,p-1))}>Előző célmap-oldal</button>
      <button type="button" className="quiet" disabled={busy||safePage>=pageCount-1}
        onClick={()=>setPage(p=>Math.min(pageCount-1,p+1))}>Következő célmap-oldal</button>
      <span className="muted small">{safePage+1}. / {pageCount} oldal · {filtered.length} találat</span>
    </div>
    <div className="premium-mod-copy-targets" role="group" aria-label="Célmapok kiválasztása">
      {visible.length===0?<p className="muted small">Nincs a keresésnek megfelelő helyi célmap.</p>:
        visible.map(map=><label key={map.id} className="plugin-row">
          <input type="checkbox" checked={targets.includes(map.id)} disabled={busy||(!targets.includes(map.id)&&targets.length>=25)}
            onChange={()=>toggle(map.id)}/>
          <span><strong>{map.name}</strong><small>{
            map.freshStoreEnrolled?
              "Új Store-map – csak leállítva, Registry-azonossággal szerkeszthető":
            map.storeProtected?
              (map.running?"Eredeti Store-v1 – előbb állítsd le az ASA/Loadert":
                "Eredeti Store-v1 – leállított helyi map"):
              (map.running?"Futó ASA – csak a következő indítási konfiguráció módosul":
                "Helyi map")
          }</small></span>
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
