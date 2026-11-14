import {useEffect,useMemo,useState} from "react";
import {AlertTriangle,CheckCircle2,FileDown,FileUp,Shield} from "lucide-react";
import {managerBridge,type ModCopyMode,type PortableModProfilePreview,type PublicMap} from "./bridge";

export function PortableModProfilePanel({maps,sourceMapId,onApplied}:{
  maps:PublicMap[];sourceMapId:string;onApplied:()=>void
}){
  const [targets,setTargets]=useState<string[]>([]);
  const [query,setQuery]=useState("");
  const [page,setPage]=useState(0);
  const pageSize=25;
  const [mode,setMode]=useState<ModCopyMode>("merge");
  const [preview,setPreview]=useState<PortableModProfilePreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [working,setWorking]=useState(false);
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");
  const eligible=useMemo(()=>maps.filter(m=>!m.maintenance&&
    !m.observedReadOnlyImported&&(!m.storeProtected||!m.freshStoreEnrolled)),[maps]);
  const filtered=useMemo(()=>eligible.filter(m=>{
    const needle=query.trim().toLocaleLowerCase("hu-HU");
    return !needle||m.name.toLocaleLowerCase("hu-HU").includes(needle)||
      m.id.toLocaleLowerCase("hu-HU").includes(needle);
  }),[eligible,query]);
  const pageCount=Math.max(1,Math.ceil(filtered.length/pageSize));
  const safePage=Math.min(page,pageCount-1);
  const visible=filtered.slice(safePage*pageSize,(safePage+1)*pageSize);
  const source=maps.find(m=>m.id===sourceMapId);
  const canExport=Boolean(source&&!source.maintenance&&
    !source.observedReadOnlyImported&&
    (!source.storeProtected||!source.freshStoreEnrolled));
  useEffect(()=>{setPreview(null);setTargets([]);setQuery("");setPage(0);
    setApproved(false);setNotice("");setError("");},
    [sourceMapId]);
  const invalidate=()=>{setPreview(null);setApproved(false);setNotice("");setError("");};
  async function exportFile(){
    if(working||!canExport)return;
    setWorking(true);invalidate();
    try{
      const result=await managerBridge.modProfileExport(sourceMapId);
      setNotice("Exportálva: "+result.profileName+" · "+result.enabled+
        " engedélyezett mod. Másold át a JSON-t a másik Manager gépére, majd ott importáld.");
    }catch{setError("A modprofil exportja megszakadt, vagy a helyi Manager megtagadta.");}
    finally{setWorking(false);}
  }
  async function importFile(){
    if(working||targets.length===0)return;
    setWorking(true);invalidate();
    try{setPreview(await managerBridge.modProfileImportPreview(targets,mode));}
    catch{setError("A kiválasztott modprofil sérült, a hash hibás, vagy valamely célmap nem szerkeszthető.");}
    finally{setWorking(false);}
  }
  async function apply(){
    if(working||!preview||!preview.changed||!approved)return;
    setWorking(true);setError("");setNotice("");
    const token=preview.token;setPreview(null);setApproved(false);
    try{
      const receipt=await managerBridge.modProfileImportApply(token);
      setNotice(receipt.changedMaps.length+
        " map módosítva. A profil nem indítja újra az ASA-t, és nem bizonyítja a modok tényleges betöltését.");
      setTargets([]);onApplied();
    }catch{setError("A modprofil mentése nem igazolt. A mapkonfigurációk megváltozhattak; nyiss új előnézetet.");}
    finally{setWorking(false);}
  }
  return <section className="premium-panel" aria-label="Többhostos modprofil átadás">
    <div className="panel-head"><h3><FileUp size={18}/> Modprofil – helyi és távoli hostok között</h3>
      <span className="mod-tag amber">Minden célhost saját jóváhagyásával</span></div>
    <p className="muted small">Exportálj mod ID-kat és load ordert a saját helyi mapodból.
      Az exportált JSON-profilt másik Windows hostra is átviheted; ott kizárólag
      a fogadó host saját Managere nyithatja meg, és készíthet Merge/Replace előnézetet
      saját helyi mapjaira. <strong>Nincs távoli fájlírás vagy automatikus LAN-szinkron.</strong>
      Jelszó, Store-v1, SQL és pluginfájl nem kerül a profilba.</p>
    <div className="draft-actions">
      <button className="secondary" type="button" disabled={working||!canExport}
        onClick={()=>{void exportFile();}}><FileDown size={16}/> Kijelölt map profiljának exportja</button>
    </div>
    <div className="warning-inline"><Shield size={16}/>
      Az eredeti eredeti Store-v1 mapok modlistája is exportálható és
      importálható, de importáláskor a helyi ASA/Loader folyamatoknak
      igazoltan leállított állapotban kell lenniük. A közös Store,
      karakterek és nyers indítási kapcsolók nem módosulnak.
    </div>
    <label>Import mód
      <select aria-label="Modprofil import módja" disabled={working} value={mode}
        onChange={e=>{setMode(e.target.value as ModCopyMode);invalidate();}}>
        <option value="merge">Merge – meglévő modok megőrzése</option>
        <option value="replace">Replace – teljes modlista csere</option>
      </select>
    </label>
    <div className="draft-actions">
      <button type="button" className="secondary" disabled={working||eligible.length===0}
        onClick={()=>{setTargets(eligible.slice(0,25).map(map=>map.id));invalidate();}}>
        {eligible.length>25?"Első 25 jogosult helyi map":"Összes jogosult helyi map"} kijelölése ({Math.min(eligible.length,25)})
      </button>
      <button type="button" className="quiet" disabled={working||targets.length===0}
        onClick={()=>{setTargets([]);invalidate();}}>Kijelölések törlése</button>
      <span className="muted small">{targets.length} / {eligible.length} helyi map</span>
    </div>
    {eligible.length>25&&<p className="warning-inline">A natív előnézet legfeljebb 25 célmapot fogad egyszerre. A többi helyi map külön, jóváhagyott kötegben állítható be; ez nem globális host/map korlát.</p>}
    <label>Modprofil célmap keresése
      <input type="search" aria-label="Modprofil célmap keresése"
        value={query} disabled={working} placeholder="Mapnév vagy azonosító"
        onChange={e=>{setQuery(e.target.value);setPage(0);invalidate();}}/>
    </label>
    <div className="draft-actions">
      <button type="button" className="secondary"
        disabled={working||visible.length===0}
        onClick={()=>{
          setTargets(old=>[...new Set([...old,...visible.map(m=>m.id)])].slice(0,25));
          invalidate();
        }}>Látható modprofil célmapok kijelölése (max. 25)</button>
      <button type="button" className="quiet" disabled={working||safePage===0}
        onClick={()=>setPage(p=>Math.max(0,p-1))}>Előző modprofil-oldal</button>
      <button type="button" className="quiet" disabled={working||safePage>=pageCount-1}
        onClick={()=>setPage(p=>Math.min(pageCount-1,p+1))}>Következő modprofil-oldal</button>
      <span className="muted small">{safePage+1}. / {pageCount} oldal · {filtered.length} találat</span>
    </div>
    <div className="premium-mod-copy-targets" role="group" aria-label="Modprofil célmapok">
      {visible.length===0?<p className="muted small">Nincs a keresésnek megfelelő helyi map.</p>:
        visible.map(map=><label key={map.id} className="plugin-row">
          <input type="checkbox" checked={targets.includes(map.id)} disabled={working||(!targets.includes(map.id)&&targets.length>=25)}
            onChange={()=>{
              setTargets(old=>old.includes(map.id)?
                old.filter(x=>x!==map.id):old.length>=25?old:[...old,map.id]);invalidate();
            }}/>
          <span><strong>{map.name}</strong><small>{
            map.storeProtected?
              (map.running?"Eredeti Store-v1 – import előtt leállítás kötelező":
                "Eredeti Store-v1 – helyi modlista importálható"):
              (map.running?"Futó map – csak a következő indítás modlistája változik":
                "Helyi célmap")
          }</small></span>
        </label>)}
    </div>
    <button className="secondary" type="button" disabled={working||targets.length===0}
      onClick={()=>{void importFile();}}><FileUp size={16}/>
      JSON-profil kiválasztása és előnézete
    </button>
    {preview&&<div className="historical-preview" role="region" aria-label="Modprofil import előnézete">
      <h4>{preview.label} · {preview.mode==="merge"?"Merge":"Replace"}</h4>
      <p>Profil: {preview.enabled} engedélyezett, {preview.passive} passzív,
        {preview.disabled} letiltott ID. SHA-256: {preview.sha256.slice(0,16)}…</p>
      {preview.targets.map(t=><div key={t.mapId} className="draft-difference">
        <strong>{t.mapName} – {t.changed?"Változik":"Változatlan"}</strong>
        <span>Előtte: {t.before.active.length} aktív, {t.before.passive.length} passzív</span>
        <span>Utána: {t.after.active.length} aktív, {t.after.passive.length} passzív</span>
        {t.changed&&<small>ID-k: {[...t.after.active,...t.after.passive].join(", ")}</small>}
      </div>)}
      <p className="muted small">{preview.operationCount} előkészített modművelet.
        Az új konfiguráció még nincs elmentve.</p>
      {preview.changed&&<label><input type="checkbox" checked={approved}
        disabled={working} onChange={e=>setApproved(e.target.checked)}/>
        Megvizsgáltam az új load ordert és a célmapokat. Jóváhagyom a helyi konfiguráció mentését.</label>}
      <div className="draft-actions">
        <button className="primary" type="button" disabled={working||!approved||!preview.changed}
          onClick={()=>{void apply();}}>Jóváhagyás és mentés</button>
        <button className="secondary" type="button" disabled={working}
          onClick={invalidate}>Mégsem</button>
      </div>
    </div>}
    {notice&&<p className="success-inline" role="status"><CheckCircle2 size={16}/>{notice}</p>}
    {error&&<p className="error-inline" role="alert"><AlertTriangle size={16}/>{error}</p>}
  </section>;
}
