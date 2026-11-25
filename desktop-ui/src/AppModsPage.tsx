import { useCallback, useEffect, useMemo, useState } from "react";
import { Panel } from "./Panel";
import { ModPlatformAdvisory } from "./ModPlatformAdvisory";
import { AlertTriangle, ArrowRight, CheckCircle2, ChevronRight, FileText, Layers3, Copy, Package, Plus, RefreshCw, Search, Shield } from "lucide-react";
import { managerBridge, type ConfiguredMods, type CurseForgeResult, type ModDraftState, type ModDraftSave, type ModDraftMode, type CurseForgeCategory, type CurseForgeFeatured, type CurseForgeProject, type CurseForgeFile, type SharedModInventory, type DashboardSnapshot } from "./bridge";
import { PremiumModBoard } from "./PremiumModBoard";
import { ModRedownloadPanel } from "./ModRedownloadPanel";
import { LocalModCopyPanel } from "./LocalModCopyPanel";
import { PortableModProfilePanel } from "./PortableModProfilePanel";

type CatalogCartRow={id:string;name:string;mode:"active"|"passive"|"disabled"};
function CurseForgeExplorer({onPrepare,mapId,mapName,onBatchSaved,onCopyToMaps}:{
  onPrepare?:(id:string)=>void;
  mapId?:string;mapName?:string;onBatchSaved?:(saved:ModDraftSave)=>void;
  onCopyToMaps?:()=>void;
}) {
  const [term, setTerm] = useState("");
  const [categoryId, setCategoryId] = useState(0);
  const [sort, setSort] = useState(3);
  const [descending, setDescending] = useState(true);
  const [feed, setFeed] = useState<"featured" | "popular" | "recentlyUpdated">("featured");
  const [categories, setCategories] = useState<CurseForgeCategory[]>([]);
  const [featured, setFeatured] = useState<CurseForgeFeatured | null>(null);
  const [result, setResult] = useState<CurseForgeResult | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [details, setDetails] = useState<CurseForgeProject | null>(null);
  const [files, setFiles] = useState<CurseForgeFile[]>([]);
  const [filesPage, setFilesPage] = useState(0);
  const [filesHasMore, setFilesHasMore] = useState(false);
  const [busy, setBusy] = useState(false);
  const [projectBusy, setProjectBusy] = useState(false);
  const [filesBusy, setFilesBusy] = useState(false);
  const [notice, setNotice] = useState("");
  const [detailNotice, setDetailNotice] = useState("");
  const [cart,setCart]=useState<CatalogCartRow[]>([]);
  const [batchPreview,setBatchPreview]=useState<ModDraftState|null>(null);
  const [batchApproved,setBatchApproved]=useState(false);
  const [batchBusy,setBatchBusy]=useState(false);
  const [batchError,setBatchError]=useState("");
  const [batchNotice,setBatchNotice]=useState("");
  useEffect(()=>{
    setCart([]);setBatchPreview(null);setBatchApproved(false);
    setBatchError("");setBatchNotice("");
  },[mapId]);
  const addToCart=()=>{
    const mod=(details?.mod?.id===selected?details.mod:null) ?? selectedBasicForCart();
    if(!mod || !/^\d{5,12}$/.test(String(mod.id)) || batchPreview)return;
    setCart(old=>old.some(x=>x.id===String(mod.id))?old:
      [...old,{id:String(mod.id),name:mod.name,mode:"active"}]);
    setBatchError("");setBatchNotice("");
  };
  function selectedBasicForCart(){
    return result?.mods.find(m=>m.id===selected) ??
      featured?.featured.find(m=>m.id===selected) ??
      featured?.popular.find(m=>m.id===selected) ??
      featured?.recentlyUpdated.find(m=>m.id===selected);
  }
  const changeCart=(id:string,update:(row:CatalogCartRow)=>CatalogCartRow)=>{
    if(batchPreview)return;
    setCart(old=>old.map(x=>x.id===id?update(x):x));
  };
  const moveCart=(index:number,delta:number)=>{
    if(batchPreview)return;
    setCart(old=>{
      const destination=index+delta;
      if(index<0||destination<0||destination>=old.length)return old;
      const next=[...old];
      [next[index],next[destination]]=[next[destination],next[index]];
      return next;
    });
  };
  const prepareCart=async()=>{
    if(!mapId||!cart.length||batchBusy||batchPreview)return;
    setBatchBusy(true);setBatchError("");setBatchNotice("");
    let draftId="";
    try{
      let draft=await managerBridge.startModDraft(mapId);
      draftId=draft.draftId;
      for(const row of cart){
        draft=await managerBridge.stageModDraft(draftId,row.id,row.mode);
      }
      setBatchPreview(draft);setBatchApproved(false);
      if(!draft.dirty)setBatchNotice("Minden kijelölt mod már ebben az állapotban szerepel a map konfigurációjában.");
    }catch{
      if(draftId)try{await managerBridge.discardModDraft(draftId);}catch{/* In-memory draft expires; no write. */}
      setBatchError("A többmodos előnézetet a natív Manager elutasította. Nem történt konfigurációmentés.");
    }finally{setBatchBusy(false);}
  };
  const discardCartPreview=async()=>{
    const draft=batchPreview;
    if(!draft||batchBusy)return;
    setBatchBusy(true);setBatchError("");
    try{await managerBridge.discardModDraft(draft.draftId);}
    catch{setBatchError("A piszkozat lejárt; a konfiguráció változatlan. Friss előnézet szükséges.");}
    finally{setBatchPreview(null);setBatchApproved(false);setBatchBusy(false);}
  };
  const saveCart=async()=>{
    if(!batchPreview?.dirty||!batchApproved||batchBusy)return;
    setBatchBusy(true);setBatchError("");setBatchNotice("");
    try{
      const saved=await managerBridge.saveModDraft(batchPreview.draftId);
      setBatchPreview(null);setBatchApproved(false);setCart([]);
      setBatchNotice("A modlista mentve a helyi map indítási konfigurációjába. Az ASA letöltés és modbetöltés még nincs igazolva.");
      onBatchSaved?.(saved);
    }catch{
      setBatchPreview(null);setBatchApproved(false);
      setBatchError("A modlista nem menthető: változott a map állapota vagy a konfiguráció. Ellenőrizd a natív naplót; új előnézet kell.");
    }finally{setBatchBusy(false);}
  };

  useEffect(() => {
    let active = true;
    const initialize = async () => {
      const [categoryResponse, featuredResponse] = await Promise.allSettled([
        managerBridge.catalogCategories(), managerBridge.catalogFeatured()
      ]);
      if (!active) return;
      if (categoryResponse.status === "fulfilled") setCategories(categoryResponse.value.categories);
      if (featuredResponse.status === "fulfilled") {
        setFeatured(featuredResponse.value);
        setSelected(featuredResponse.value.featured[0]?.id ??
          featuredResponse.value.popular[0]?.id ?? null);
      }
      if (categoryResponse.status === "rejected" && featuredResponse.status === "rejected")
        setNotice("A CurseForge katalógus nem érhető el. Ellenőrizd a Beállítások menüben az API-kulcsot és az internetkapcsolatot.");
    };
    void initialize();
    return () => { active = false; };
  }, []);

  const runSearch = async (page = 0) => {
    const query = term.trim();
    if (query.length > 80 || (query.length > 0 && query.length < 2)) {
      setNotice("Adj meg 2–80 karakteres keresőkifejezést, vagy böngéssz üres keresőmezővel.");
      return;
    }
    setBusy(true); setNotice("");
    try {
      const data = await managerBridge.catalogBrowse({term: query, categoryId, sort, descending, page});
      setResult(data);
      setSelected(data.mods[0]?.id ?? null);
    } catch (e) {
      const code = e instanceof Error ? e.message : "UM-CURSEFORGE-REQUEST-FAILED";
      setNotice(code === "UM-CURSEFORGE-NO-KEY"
        ? "Először add meg a CurseForge API-kulcsot a Beállítások menüben."
        : "A hivatalos CurseForge keresés sikertelen (" + code + ").");
    } finally { setBusy(false); }
  };

  useEffect(() => {
    let active = true;
    setDetails(null); setFiles([]); setFilesPage(0); setFilesHasMore(false); setDetailNotice("");
    if (selected === null) return;
    setProjectBusy(true);
    managerBridge.catalogProject(selected).then(info => {
      if (!active) return;
      setDetails(info);
      setFiles(info.files);
      setFilesHasMore(info.filesHasMore);
    }).catch(() => {
      if (active) setDetailNotice("A projekt részletes adatai vagy verziólistája jelenleg nem érhető el.");
    }).finally(() => { if (active) setProjectBusy(false); });
    return () => { active = false; };
  }, [selected]);

  const loadMore = async () => {
    if (selected === null || filesBusy || !filesHasMore) return;
    setFilesBusy(true); setDetailNotice("");
    const nextPage = filesPage + 1;
    try {
      const response = await managerBridge.catalogFiles(selected, nextPage);
      setFiles(current => [...current, ...response.files]);
      setFilesPage(nextPage); setFilesHasMore(response.hasMore);
    } catch { setDetailNotice("A további fájlverziók betöltése nem sikerült. A meglévő lista megmaradt."); }
    finally { setFilesBusy(false); }
  };

  const displayed = result?.mods ?? featured?.[feed] ?? [];
  const selectedBasic = displayed.find(m => m.id === selected) ??
    featured?.featured.find(m => m.id === selected) ??
    featured?.popular.find(m => m.id === selected) ??
    featured?.recentlyUpdated.find(m => m.id === selected);
  const title = details?.mod.name ?? selectedBasic?.name ?? "Mod kiválasztása";

  return <Panel title="CurseForge ASA Mod Galaxy" action={<span className="muted tiny">Hivatalos API · kategóriák · fájlverziók</span>}>
    <form className="cf-filters" onSubmit={e => { e.preventDefault(); void runSearch(0); }}>
      <label className="cf-search"><span>Keresés modnév vagy készítő szerint</span>
        <span className="cf-input"><Search size={18} aria-hidden="true"/>
          <input aria-label="CurseForge mod keresése" placeholder="Shiny, S-Dino, Arkitect..." value={term}
            onChange={e => setTerm(e.target.value)} maxLength={80}/></span>
      </label>
      <label><span>Kategória</span>
        <select aria-label="CurseForge kategória" value={categoryId}
          onChange={e => setCategoryId(Number(e.target.value))}>
          <option value={0}>Összes kategória</option>
          {categories.filter(c => !c.isClass).map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
      <label><span>Rendezés</span>
        <select aria-label="CurseForge rendezés" value={sort}
          onChange={e => setSort(Number(e.target.value))}>
          <option value={3}>Legutóbb frissített</option>
          <option value={2}>Népszerűség</option>
          <option value={6}>Összes letöltés</option>
          <option value={4}>Név</option>
          <option value={0}>Szolgáltatói alapértelmezés</option>
        </select>
      </label>
      <button type="button" className="cf-order" aria-label="Rendezési irány"
        onClick={() => setDescending(v => !v)} title="A CurseForge által támogatott rendezési irány">
        {descending ? "Csökkenő" : "Növekvő"}
      </button>
      <button className="primary" type="submit" disabled={busy}>{busy ? "Lekérdezés..." : "Keresés"}</button>
    </form>
    {notice && <div role="status" className="warning-inline"><AlertTriangle size={17}/>{notice}</div>}
    {!result && <div className="cf-feed-tabs" role="tablist" aria-label="CurseForge ajánlók">
      <button role="tab" aria-selected={feed==="featured"} className={feed==="featured"?"active":""}
        onClick={() => { setFeed("featured"); setSelected(featured?.featured[0]?.id ?? null); }}>Kiemelt modok</button>
      <button role="tab" aria-selected={feed==="popular"} className={feed==="popular"?"active":""}
        onClick={() => { setFeed("popular"); setSelected(featured?.popular[0]?.id ?? null); }}>Népszerű modok</button>
      <button role="tab" aria-selected={feed==="recentlyUpdated"} className={feed==="recentlyUpdated"?"active":""}
        onClick={() => { setFeed("recentlyUpdated"); setSelected(featured?.recentlyUpdated[0]?.id ?? null); }}>Frissítettek</button>
    </div>}
    {result && <div className="cf-results-head">
      <span>{result.totalCount.toLocaleString("hu-HU")} találat · {result.page+1}. oldal</span>
      <button className="quiet" onClick={() => {
        setResult(null); setSelected(featured?.[feed][0]?.id ?? null); setNotice("");
      }}>Kiemelt katalógus visszaállítása</button>
    </div>}
    <div className="cf-galaxy">
      <div className="cf-catalog-pane">
        {displayed.length === 0 && <div className="empty">
          {busy ? "Hivatalos találatok betöltése..." : !result && !featured ?
            "Kiemelt modok lekérése vagy kategória szerinti keresés..." : "Nincs megjeleníthető mod ebben a listában."}
        </div>}
        {displayed.map(mod => <button key={mod.id} className={selected===mod.id?"cf-result selected":"cf-result"}
          onClick={() => setSelected(mod.id)}>
          {mod.thumbnailUrl ? <img src={mod.thumbnailUrl} alt="" loading="lazy" referrerPolicy="no-referrer"/> :
            <span className="cf-mod-fallback"><Package size={22}/></span>}
          <span className="cf-result-copy"><strong>{mod.name}</strong>
            <small>{mod.authors.join(", ") || "Ismeretlen készítő"}</small>
            <small>ID {mod.id} · {mod.downloadCount.toLocaleString("hu-HU")} letöltés</small></span>
          <ChevronRight size={15} className="cf-chevron"/>
        </button>)}
        {result && <div className="catalog-pagination">
          <button disabled={busy || result.page===0} onClick={() => { void runSearch(result.page-1); }}>Előző</button>
          <span className="muted tiny">{result.page+1}. oldal</span>
          <button disabled={busy || !result.hasMore} onClick={() => { void runSearch(result.page+1); }}>Következő</button>
        </div>}
      </div>
      <div className="cf-project-pane">
        {selected === null ? <div className="empty">Válassz egy modot a részletekhez.</div> : <>
          <span className="eyebrow">MODPROJEKT / {selected}</span>
          <h3>{title}</h3>
          {selectedBasic && <div className="cf-tags">
            {(details?.mod.categories ?? selectedBasic.categories ?? []).slice(0,5).map((c,i)=>
              <span key={i}>{c}</span>)}
          </div>}
          {projectBusy && <div className="muted small">Részletes leírás és hivatalos fájlverziók lekérése...</div>}
          {detailNotice && <div role="status" className="warning-inline"><AlertTriangle size={16}/>{detailNotice}</div>}
          {details ? <>
            <p className="cf-summary">{details.mod.summary || "Nincs nyilvános összefoglaló."}</p>
            {details.description && <div className="cf-description" aria-label="CurseForge mod leírás">
              {details.description}</div>}
            {details.mod.screenshots.length > 0 && <div className="cf-screenshots">
              {details.mod.screenshots.slice(0,3).map((src,i)=><img key={i} alt={details.mod.name+" · illusztráció "+(i+1)}
                src={src} loading="lazy" referrerPolicy="no-referrer"/>)}
            </div>}
            <div className="details-line"><span>Legutóbbi verzió</span><b>{details.mod.latestFile || "Nem ismert"}</b></div>
            <div className="details-line"><span>Letöltések</span><b>{details.mod.downloadCount.toLocaleString("hu-HU")}</b></div>
            <div className="cf-file-header"><h4>Hivatalos fájlverziók</h4><span>{details.filesTotal} fájl</span></div>
            {files.length===0 && <div className="empty">Nem érhető el publikus fájlmetaadat.</div>}
            <div className="cf-files">
              {files.map(file=><div className="cf-file" key={file.fileId}>
                <div><b>{file.name}</b><small>{file.published ? new Date(file.published).toLocaleDateString("hu-HU") : "Dátum nélkül"} ·
                  {file.sizeBytes>0 ? " "+(file.sizeBytes/1048576).toFixed(1)+" MiB" : " méret ismeretlen"}</small></div>
                <span className={file.available?"mod-tag green":"mod-tag amber"}>{file.available?"Elérhető":"Nem igazolt"}</span>
                {file.gameVersions.length>0 && <small className="cf-versions">{file.gameVersions.join(" · ")}</small>}
                <ModPlatformAdvisory versions={file.gameVersions} fileId={file.fileId}/>
                {file.dependencies.length>0 && <small className="cf-deps">
                  Függőségek: {file.dependencies.map(d => d.modId+" ("+d.type+")").join(", ")}</small>}
              </div>)}
            </div>
            {filesHasMore && <button className="secondary cf-more" disabled={filesBusy}
              onClick={() => { void loadMore(); }}>{filesBusy?"Betöltés...":"További fájlverziók"}</button>}
          </> : selectedBasic && <p className="muted">{selectedBasic.summary}</p>}
          <div className="cf-project-actions">
            {mapId && <button className="primary" disabled={projectBusy||batchBusy||Boolean(batchPreview)||selected===null||cart.some(x=>x.id===String(selected))}
              onClick={addToCart}><Plus size={16}/> {cart.some(x=>x.id===String(selected))?"Már a kosárban":"Hozzáadás a modkosárhoz"}</button>}
            {onPrepare && <button className="secondary" disabled={projectBusy} onClick={()=>onPrepare(String(selected))}>
              <Layers3 size={16}/> Projekt ID előkészítése a modpiszkozatban</button>}
          </div>
          <div className="warning-inline"><Shield size={17}/>
            A CurseForge-fájlmetaadat nem igazolja a crossplayt, letöltési jogosultságot vagy a mod ASA-betöltését. A piszkozat nem telepít modot.</div>
        </>}
      </div>
    </div>
    {mapId&&<section className="premium-panel" aria-label="CurseForge többmodos kosár"
      style={{marginTop:16,padding:18}}>
      <header className="premium-panel-heading"><h3><Layers3 size={18}/> {mapName||"Helyi map"} · modkosár ({cart.length})</h3></header>
      <p className="muted small">A kijelölések megmaradnak keresés és lapozás közben. Előbb teljes natív piszkozat-előnézet, utána külön jóváhagyott mentés. Nincs automatikus modtelepítés.</p>
      {cart.length===0&&<p className="muted">Jelölj ki modokat a CurseForge katalógusból.</p>}
      {cart.map((item,i)=><div className="details-line" key={item.id}>
        <span><b>{i+1}. {item.name}</b> <small>#{item.id}</small></span>
        <label><span className="sr-only">{item.name} modállapot</span>
          <select aria-label={item.id+" kosár célállapot"} value={item.mode} disabled={!!batchPreview||batchBusy}
            onChange={e=>changeCart(item.id,x=>({...x,mode:e.target.value as CatalogCartRow["mode"]}))}>
            <option value="active">Aktív</option><option value="passive">Passzív</option>
            <option value="disabled">Letiltva</option>
          </select>
        </label>
        <button type="button" className="quiet" disabled={!!batchPreview||batchBusy||i===0}
          aria-label={item.id+" feljebb"} onClick={()=>moveCart(i,-1)}>↑</button>
        <button type="button" className="quiet" disabled={!!batchPreview||batchBusy||i===cart.length-1}
          aria-label={item.id+" lejjebb"} onClick={()=>moveCart(i,1)}>↓</button>
        <button type="button" className="quiet" disabled={!!batchPreview||batchBusy}
          aria-label={item.id+" eltávolítása a kosárból"}
          onClick={()=>setCart(old=>old.filter(x=>x.id!==item.id))}>Eltávolítás</button>
      </div>)}
      {!batchPreview&&<button type="button" className="primary" disabled={!cart.length||batchBusy}
        onClick={()=>{void prepareCart();}}>Teljes modlista ellenőrzése</button>}
      {batchPreview&&<div role="region" aria-label="Többmodos natív előnézet">
        <h3>Natív változás-előnézet ({batchPreview.differences.length})</h3>
        {batchPreview.differences.length===0?<p>Nincs mentendő változás.</p>:
          batchPreview.differences.map(x=><div className="details-line" key={x.modId}>
            <span>{x.modId}</span>
            <b>{x.beforeMode??"Nincs"} → {x.afterMode??"Törölve"} #{x.afterPosition??"—"}</b>
          </div>)}
        <label><input type="checkbox" checked={batchApproved} disabled={batchBusy||!batchPreview.dirty}
          onChange={e=>setBatchApproved(e.target.checked)}/>
          Jóváhagyom a kijelölt helyi map konfigurációjának egyszeri mentését.
        </label>
        <button type="button" className="primary" disabled={!batchApproved||batchBusy||!batchPreview.dirty}
          onClick={()=>{void saveCart();}}>Modlista mentése</button>
        <button type="button" className="secondary" disabled={batchBusy}
          onClick={()=>{void discardCartPreview();}}>Előnézet elvetése</button>
      </div>}
      {batchNotice&&<div role="status" className="success-inline">
        <CheckCircle2 size={16}/>{batchNotice}
        {onCopyToMaps&&<button type="button" className="secondary" onClick={onCopyToMaps}>Átvitel más helyi mapokra</button>}
      </div>}
      {batchError&&<div role="alert" className="error-inline"><AlertTriangle size={16}/>{batchError}</div>}
      <p className="muted small"><Shield size={14}/> Kijelölve ≠ konfigurálva ≠ ASA által letöltve/betöltve. Távoli mapot kizárólag a saját Managere írhat.</p>
    </section>}
  </Panel>;
}
function ModDraftEditor({ mapId, onSaved, prefillId, prefillMode, onDraftProgress }: {
  mapId: string; onSaved: (saved: ModDraftSave) => void; prefillId: string;
  prefillMode?: ModDraftMode; onDraftProgress?: (changes:number)=>void
}) {
  const [draft, setDraft] = useState<ModDraftState | null>(null);
  const [modId, setModId] = useState("");
  const [mode, setMode] = useState<ModDraftMode>("active");
  const [position, setPosition] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  useEffect(() => { setDraft(null); setError(""); setNotice(""); setModId(""); }, [mapId]);
  useEffect(() => {
    if (!prefillId) return;
    setModId(prefillId);
    if(prefillMode)setMode(prefillMode);
    // A catalog selection opens only an in-memory draft. No Manager file is
    // modified until the separate, explicitly confirmed Save operation.
    if (!draft && mapId) {
      let active = true;
      setBusy(true);
      managerBridge.startModDraft(mapId)
        .then(data => { if (active) setDraft(data); })
        .catch(() => {
          if (active) setError("Nem sikerült biztonságos modpiszkozatot nyitni a kiválasztott maphoz.");
        })
        .finally(() => { if (active) setBusy(false); });
      return () => { active = false; };
    }
  }, [prefillId, prefillMode, mapId]);
  useEffect(()=>{onDraftProgress?.(draft?.differences.length??0);},[draft,onDraftProgress]);
  const start = async () => {
    if (!mapId) return;
    setBusy(true); setError(""); setNotice("");
    try { setDraft(await managerBridge.startModDraft(mapId)); }
    catch { setError("Nem sikerült a map eredeti modkonfigurációját előkészíteni."); }
    finally { setBusy(false); }
  };
  const stage = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!draft) return;
    if (!/^\d{5,12}$/.test(modId)) { setError("A projektazonosító 5–12 számjegyből áll."); return; }
    let index: number | undefined;
    if (position.trim()) {
      const requested = Number(position);
      if (!Number.isInteger(requested) || requested < 1 || requested > 501) {
        setError("A pozíció pozitív egész szám (1–501)."); return;
      }
      index = requested - 1;
    }
    setBusy(true); setError(""); setNotice("");
    try { setDraft(await managerBridge.stageModDraft(draft.draftId,modId,mode,index)); setModId(""); setPosition(""); }
    catch { setError("A modmódosítás nem érvényes, vagy a piszkozat megváltozott. Nem történt mentés."); }
    finally { setBusy(false); }
  };
  const history = async (type: "undo" | "redo") => {
    if (!draft) return;
    setBusy(true); setError("");
    try { setDraft(type === "undo"
      ? await managerBridge.undoModDraft(draft.draftId)
      : await managerBridge.redoModDraft(draft.draftId)); }
    catch { setError("A piszkozat előzménye nem érhető el."); }
    finally { setBusy(false); }
  };
  const discard = async () => {
    if (!draft) return;
    setBusy(true); setError("");
    try { await managerBridge.discardModDraft(draft.draftId); setDraft(null); setNotice("Piszkozat elvetve. A konfiguráció változatlan."); }
    catch { setError("A piszkozat már nem érvényes; töltsd újra a modlistát."); }
    finally { setBusy(false); }
  };
  const save = async () => {
    if (!draft || !draft.dirty) return;
    setBusy(true); setError("");
    try {
      const saved = await managerBridge.saveModDraft(draft.draftId);
      onSaved(saved); setDraft(null);
      setNotice("Mentve a helyi -mods indítási konfigurációba. A modok letöltését/frissítését az ASA indításkor végzi; a betöltés még nem igazolt.");
    } catch { setError("Nem sikerült a konfiguráció biztonságos mentése. Lehetséges versengő módosítás, az eredeti adatokat ellenőrizni kell."); }
    finally { setBusy(false); }
  };
  return <Panel title="Modváltoztatások · előnézet és mentés" action={<span className="mod-tag amber">Csak helyi indítási konfiguráció</span>}>
    {!draft ? <div className="draft-intro">
      <p className="muted small">Nyiss egy piszkozatot az aktuális map eredeti konfigurációjából. A lépések előbb csak memóriában változnak, majd kifejezett jóváhagyással, ellenőrzött C# tranzakcióban menthetők.</p>
      <button className="primary" disabled={busy || !mapId} onClick={() => { void start(); }}>Piszkozat indítása</button>
    </div> : <>
      <form className="draft-form" onSubmit={e => { void stage(e); }}>
        <label>CurseForge projektazonosító (5-12 számjegy)
          <input aria-label="Modprojekt ID" value={modId} onChange={e=>setModId(e.target.value)} maxLength={12} inputMode="numeric" placeholder="pl. 123456"/>
        </label>
        <label>Célállapot
          <select aria-label="Mod célállapota" value={mode} onChange={e=>setMode(e.target.value as ModDraftMode)}>
            <option value="active">Aktív</option><option value="passive">Passzív</option>
            <option value="disabled">Letiltva</option><option value="remove">Eltávolítás a konfigurációból</option>
          </select>
        </label>
        <label>Pozíció (opcionális)
          <input aria-label="Modpozíció" value={position} onChange={e=>setPosition(e.target.value)} placeholder="1-től" inputMode="numeric"/>
        </label>
        <button className="primary" type="submit" disabled={busy}>Lépés hozzáadása</button>
      </form>
      <div className="draft-differences">
        <h3>Függőben lévő módosítások</h3>
        {draft.differences.length===0 ? <p className="muted small">A jelenlegi piszkozat az eredeti konfigurációval egyezik.</p> :
          draft.differences.map(d=><div className="draft-difference" key={d.modId}>
            <b>{d.modId}</b><span>{d.beforeMode ? d.beforeMode+" #"+d.beforePosition : "Nincs a listában"}</span>
            <ArrowRight size={16}/><strong>{d.afterMode ? d.afterMode+" #"+d.afterPosition : "Eltávolítva"}</strong>
          </div>)}
      </div>
      <div className="draft-actions">
        <button className="secondary" disabled={busy || !draft.canUndo} onClick={() => { void history("undo"); }}>Visszavonás</button>
        <button className="secondary" disabled={busy || !draft.canRedo} onClick={() => { void history("redo"); }}>Újra</button>
        <button className="secondary" disabled={busy} onClick={() => { void discard(); }}>Piszkozat elvetése</button>
        <button className="primary" disabled={busy || !draft.dirty} onClick={() => { void save(); }}>Jóváhagyás és mentés</button>
      </div>
    </>}
    {notice && <div className="success-inline" role="status"><CheckCircle2 size={17}/>{notice}</div>}
    {error && <div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
    <div className="warning-inline"><Shield size={17}/> A mentés nem tölt le fájlt és nem indítja újra az ASA-t. Az eredeti -mods/-passivemods sorrendet a C# ModDraftSession és ModStateController kezeli.</div>
  </Panel>;
}

function SharedModOverview({onPick,revision}: {
  onPick:(mapId:string)=>void; revision:number
}) {
  const [inventory,setInventory]=useState<SharedModInventory|null>(null);
  const [error,setError]=useState("");
  const [loading,setLoading]=useState(false);
  const [refresh,setRefresh]=useState(0);
  const [search,setSearch]=useState("");
  const [sharedOnly,setSharedOnly]=useState(true);
  useEffect(()=>{
    let active=true;
    setLoading(true);setError("");
    managerBridge.sharedModInventory().then(data=>{if(active)setInventory(data);})
      .catch(()=>{if(active)setError("A helyi modleltár nem olvasható. Nem módosítottunk konfigurációt.");})
      .finally(()=>{if(active)setLoading(false);});
    return ()=>{active=false;};
  },[revision,refresh]);
  const filtered=useMemo(()=>{
    const term=search.trim().toLocaleLowerCase("hu-HU");
    return (inventory?.projects??[]).filter(project=>
      (!sharedOnly||project.shared) &&
      (!term||project.projectId.includes(term)||
       project.assignments.some(a=>a.mapName.toLocaleLowerCase("hu-HU").includes(term))));
  },[inventory,sharedOnly,search]);
  return <Panel title="Közös modok · helyi mapok" action={
    <button type="button" className="quiet" disabled={loading}
      onClick={()=>setRefresh(x=>x+1)}><RefreshCw size={15}/> Frissítés</button>
  }>
    <p className="muted small">Konfigurált modkiosztás a helyi mapok között; az archivált Store-v1 listák külön jelölve. Nem fájlellenőrzés, nem futó ASA modállapot és nem változtatás.</p>
    <div className="toolbar shared-mod-toolbar">
      <Search size={17}/>
      <input aria-label="Közös modok keresése" placeholder="Projektazonosító vagy map neve"
        value={search} onChange={e=>setSearch(e.target.value)}/>
      <label><input type="checkbox" checked={sharedOnly}
        onChange={e=>setSharedOnly(e.target.checked)}/> Csak több mapon</label>
    </div>
    {inventory && <p className="muted small">
      {inventory.mapCount} helyi map · {inventory.configuredMapCount} konfigurált ·
      {inventory.sharedProjectCount} megosztott mod · {inventory.projects.length} projekt
    </p>}
    {error && <div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
    {loading && !inventory && <div className="empty">Modkiosztások beolvasása...</div>}
    {inventory?.unreadableMaps.length ? <div className="warning-inline">
      <AlertTriangle size={17}/> Nem olvasható modkonfiguráció: {inventory.unreadableMaps.join(", ")}
    </div> : null}
    {inventory && <div className="mods-list" aria-label="Közös modkiosztások">
      {filtered.length===0 ? <div className="empty">Nincs a szűrőnek megfelelő mod.</div> :
        filtered.slice(0,80).map(project=><div className="mod-item" key={project.projectId}>
          <span className="mod-mark"><Layers3 size={18}/></span>
          <div className="mod-title">
            <strong>Projekt #{project.projectId}</strong>
            <small>{project.enabledMapCount} helyi map · {project.assignments.map(a=>
              a.mapName+(a.historicalProtected?" (védett archívum)":"")).join(", ")}</small>
            <div className="row-wrap">
              {project.assignments.map((a,i)=><button className="quiet" type="button"
                key={a.mapId+"-"+a.mode+"-"+i} onClick={()=>onPick(a.mapId)}>
                {a.mapName} · {a.mode==="active"?"Aktív":a.mode==="passive"?"Passzív":"Letiltva"} #{a.position+1}
              </button>)}
            </div>
          </div>
          <span className={project.shared?"mod-tag green":"mod-tag grey"}>
            {project.shared?"Közös":"Egy map"}
          </span>
        </div>)}
      {filtered.length>80 && <p className="muted small">
        Az első 80 találat látható; szűkíts projektazonosítóra vagy mapnévre.
      </p>}
    </div>}
  </Panel>;
}
export function Mods({ snapshot, selected, setSelected, mapLocked=false }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (value: string) => void;
  mapLocked?:boolean
}) {
  const [info, setInfo] = useState<ConfiguredMods | null>(null);
  const [error, setError] = useState("");
  const [catalogPicked, setCatalogPicked] = useState("");
  const [prefillMode,setPrefillMode]=useState<ModDraftMode>("active");
  const [draftChanges,setDraftChanges]=useState(0);
  const [inventoryRevision,setInventoryRevision]=useState(0);
  const [workspaceTab,setWorkspaceTab]=useState<"installed"|"draft"|"galaxy"|"shared"|"copy"|"profile">("installed");
  const [savedNotice,setSavedNotice]=useState("");
  const currentName=info?.mapName||
    snapshot.local.maps.find(m=>m.id===selected)?.name||"Helyi map";
  useEffect(()=>{
    setCatalogPicked("");setDraftChanges(0);setPrefillMode("active");
    // Changing the SOURCE map in Copy To / Portable Profile must not
    // silently close the active workflow and hide its next action.
    // Drafts, catalog and per-map editors still reset on map change.
    setWorkspaceTab(previous=>
      previous==="copy"||previous==="profile"?previous:"installed");
    setSavedNotice("");
  },[selected]);
  useEffect(() => {
    let active = true;
    setInfo(null); setError("");
    if (!selected) return;
    managerBridge.configuredMods(selected).then(data => { if(active) setInfo(data); })
      .catch(e => { if(active) setError(e instanceof Error ? e.message : "UM-READ-UNAVAILABLE"); });
    return () => { active = false; };
  }, [selected]);
  const scrollTo=(id:string)=>{
    document.getElementById(id)?.scrollIntoView({behavior:"smooth",block:"start"});
  };
  const prepare=(id:string,mode:ModDraftMode)=>{
    setCatalogPicked(id);setPrefillMode(mode);
    setWorkspaceTab("draft");
    requestAnimationFrame(()=>scrollTo("mod-workspace-tabs"));
  };
  const onDraftProgress=useCallback((changes:number)=>setDraftChanges(changes),[]);
  return <div className="premium-mod-page">
    <div className="page-intro premium-mod-intro">
      <span className="eyebrow">CLUSTER / {currentName.toLocaleUpperCase("hu-HU")} / MODOK</span>
      <h1>Modok – {currentName}</h1>
      <p>CurseForge modkeresés, aktív/passzív modok és sorrend kezelése. Az ASA a mentett <code>-mods</code> projektazonosítókat az indítás során maga tölti le és frissíti. A tényleges modbetöltést nem igazolja önmagában a konfiguráció.</p>
    </div>
    <div className="premium-mod-map-picker">
      <Layers3 size={18}/>
      {mapLocked
        ?<strong aria-label="Modok forrásmapja">{currentName}</strong>
        :<select aria-label="Helyi map" value={selected} onChange={e=>setSelected(e.target.value)}>
          {snapshot.local.maps.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}
        </select>}
      <span className="premium-small-muted">{mapLocked
        ?"Ennek a mapnak a modlistáját szerkeszted. Más mapokra a Másolás fülön alkalmazhatod."
        :"Az eredeti indítási paraméterek megőrzésével"}</span>
    </div>
    {error&&<div role="alert" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
    {savedNotice&&<div role="status" className="success-inline premium-mod-save-toast">
      <CheckCircle2 size={17}/>{savedNotice}
      <button type="button" className="quiet" onClick={()=>setSavedNotice("")}
        aria-label="Modmentés értesítés bezárása">Bezárás</button>
    </div>}
    <div id="mod-workspace-tabs" className="premium-workspace-tabs" role="tablist"
      aria-label="Mod Center munkaterületek">
      {([
        {id:"installed",name:"Map modjai",icon:Package},
        {id:"draft",name:"Modpiszkozat",icon:FileText},
        {id:"galaxy",name:"CurseForge katalógus",icon:Search},
        {id:"shared",name:"Közös modok",icon:Layers3},
        {id:"copy",name:"Másolás több mapra",icon:Copy},
        {id:"profile",name:"Modprofilok – N host",icon:Layers3}
      ] as const).map(tab=><button type="button" key={tab.id} role="tab"
        aria-selected={workspaceTab===tab.id}
        className={workspaceTab===tab.id?"active":""}
        onClick={()=>setWorkspaceTab(tab.id)}>
        <tab.icon size={17}/>{tab.name}
        {tab.id==="draft"&&draftChanges>0?<span className="premium-tab-count">{draftChanges}</span>:null}
      </button>)}
    </div>
    <section className={workspaceTab==="installed"?"premium-tab-surface":"premium-tab-surface is-hidden"}
      aria-hidden={workspaceTab!=="installed"}>
    <PremiumModBoard info={info} mapName={currentName} draftChanges={draftChanges}
      onStage={prepare}
      onOpenEditor={()=>setWorkspaceTab("draft")}
      onOpenCatalog={()=>setWorkspaceTab("galaxy")}/>
    {info&&<ModRedownloadPanel key={selected} mapId={selected} ids={[...info.active,...info.passive]}/>}
    </section>
    <div id="mod-draft-editor" className={workspaceTab==="draft"?
      "premium-editor-area premium-tab-surface":"premium-editor-area premium-tab-surface is-hidden"}
      aria-hidden={workspaceTab!=="draft"}>
      <ModDraftEditor mapId={selected} prefillId={catalogPicked} prefillMode={prefillMode}
        onDraftProgress={onDraftProgress}
        onSaved={saved => {
          setInventoryRevision(n=>n+1);
          setSavedNotice("Mentve a helyi indítási konfigurációba. A futó ASA modbetöltéséhez ellenőrzött újraindítás szükséges.");
          setInfo(current=>current?{...current,pending:saved.pending,
            active:saved.active,passive:saved.passive,disabled:saved.disabled}:current);
        }}/>
    </div>
    <div className={workspaceTab==="shared"?"premium-tab-surface":"premium-tab-surface is-hidden"}
      aria-hidden={workspaceTab!=="shared"}>
      <SharedModOverview revision={inventoryRevision} onPick={setSelected}/>
    </div>
    <div className={workspaceTab==="copy"?"premium-tab-surface":"premium-tab-surface is-hidden"}
      aria-hidden={workspaceTab!=="copy"}>
      <LocalModCopyPanel maps={snapshot.local.maps} sourceMapId={selected}
        onApplied={()=>{
          setInventoryRevision(n=>n+1);
          setSavedNotice("A kijelölt helyi mapok modkonfigurációja mentve; tényleges betöltésük még nem igazolt.");
          managerBridge.configuredMods(selected).then(setInfo).catch(()=>setInfo(null));
        }}/>
    </div>
    <div className={workspaceTab==="profile"?"premium-tab-surface":"premium-tab-surface is-hidden"}
      aria-hidden={workspaceTab!=="profile"}>
      <PortableModProfilePanel maps={snapshot.local.maps} sourceMapId={selected}
        onApplied={()=>{
          setInventoryRevision(n=>n+1);
          setSavedNotice("A modprofil importja mentve. A tényleges ASA modbetöltés nem igazolt.");
          managerBridge.configuredMods(selected).then(setInfo).catch(()=>setInfo(null));
        }}/>
    </div>
    <div id="curseforge-catalog" className={workspaceTab==="galaxy"?
      "premium-catalog-area premium-tab-surface":"premium-catalog-area premium-tab-surface is-hidden"}
      aria-hidden={workspaceTab!=="galaxy"}>
      <CurseForgeExplorer mapId={selected} mapName={currentName}
        onPrepare={id=>prepare(id,"active")}
        onCopyToMaps={()=>setWorkspaceTab("copy")}
        onBatchSaved={saved=>{
          setInventoryRevision(n=>n+1);
          setDraftChanges(0);
          setSavedNotice("A CurseForge-kosár modlistája mentve. Az ASA betöltés külön igazolandó.");
          setInfo(current=>current?{...current,pending:saved.pending,
            active:saved.active,passive:saved.passive,disabled:saved.disabled}:current);
        }}/>
    </div>
  </div>;
}
