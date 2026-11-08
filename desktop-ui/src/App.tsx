import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  AlertTriangle, Archive, ArrowRight, Boxes, CheckCircle2, ChevronRight,
  FileText, HardDrive, LayoutDashboard, Layers3, Monitor, CloudDownload, Activity,
  Grid2X2, List, CalendarClock, Copy,
  Package, Plug, Plus, RefreshCw, Search, Server, Settings2, Shield, WifiOff
} from "lucide-react";
import {
  managerBridge, dashboardRemotes, type BackupOverview, type ConfiguredMods, type CurseForgeResult,
  type LogTail, type ModDraftState, type ModDraftSave, type ModDraftMode,
  type IniFile, type IniDocument, type IniPreview, type LocalImportPreview,
  type SteamInstallations, type SuggestedPorts,
  type CurseForgeCategory, type CurseForgeFeatured, type CurseForgeProject, type CurseForgeFile,
  type PluginInventory, type PluginState, type PluginTogglePreview, type SharedModInventory, type DashboardSnapshot,
  type PublicMap
} from "./bridge";
import { FreshAsaSteamWizard } from "./FreshAsaSteamWizard";
import { InitialMapSetup } from "./InitialMapSetup";
import { HistoricalStoreImport } from "./HistoricalStoreImport";
import { HistoricalRconAttach } from "./HistoricalRconAttach";
import { HistoricalActiveStartPanel } from "./HistoricalActiveStartPanel";
import { HistoricalRconLivePanel } from "./HistoricalRconLivePanel";
import { HistoricalAdminRconPanel } from "./HistoricalAdminRconPanel";
import { HistoricalGracefulStopPanel } from "./HistoricalGracefulStopPanel";
import { PremiumDashboard } from "./PremiumDashboard";
import { ClusterMapGallery } from "./ClusterMapGallery";
import { MapCover } from "./MapArtwork";
import { PremiumModBoard } from "./PremiumModBoard";
import { UpdateCenterPage } from "./UpdateCenterPage";
import { StoppedSteamUpdate } from "./StoppedSteamUpdate";
import { FreshAsaStartPanel } from "./FreshAsaStartPanel";
import { FreshAsaStopPanel } from "./FreshAsaStopPanel";
import { FreshAsaRestartPanel } from "./FreshAsaRestartPanel";
import type { AsaLifecycleJournal, AsaRestartJournalStatus } from "./bridge";
import { PluginZipInstaller } from "./PluginZipInstaller";
import { AsaApiCoreInstaller } from "./AsaApiCoreInstaller";
import { RconReadOnlyPanel } from "./RconReadOnlyPanel";
import { LocalModCopyPanel } from "./LocalModCopyPanel";
import { PortableModProfilePanel } from "./PortableModProfilePanel";
import { OfflineAsaArchivePanel } from "./OfflineAsaArchivePanel";
import { OfflineAsaRestorePanel } from "./OfflineAsaRestorePanel";
import { PluginJsonEditor } from "./PluginJsonEditor";
import { StoreTravelWorkbench } from "./StoreTravelWorkbench";
import { WorldBackupControls } from "./WorldBackupControls";
import { OperationsCenter } from "./OperationsCenter";
import { LanSettingsPanel } from "./LanSettingsPanel";
import { ClusterRegistryPanel } from "./ClusterRegistryPanel";
import { MultiHostBackupPanel } from "./MultiHostBackupPanel";

type Page = "operations" | "dashboard" | "servers" | "mods" | "cluster" | "plugins" | "backups" | "steam" | "logs" | "settings" | "ini" | "updates" | "travel" | "map";
const tabs: Array<{ page: Page; label: string; icon: typeof LayoutDashboard }> = [
  { page: "dashboard", label: "Áttekintés", icon: LayoutDashboard },
  { page: "servers", label: "Szerverek", icon: Server },
  { page: "cluster", label: "Cluster", icon: Boxes },
  { page: "mods", label: "Modok", icon: Layers3 },
  { page: "plugins", label: "Pluginok", icon: Plug },
  { page: "backups", label: "Mentések", icon: Archive },
  { page: "operations", label: "Üzemeltetés", icon: CalendarClock },
  { page: "steam", label: "SteamCMD", icon: HardDrive },
  { page: "updates", label: "Frissítések", icon: CloudDownload },
  { page: "travel", label: "Karakterutazás teszt", icon: Activity },
  { page: "logs", label: "Naplók", icon: FileText },
  { page: "ini", label: "INI szerkesztő", icon: FileText },
  { page: "settings", label: "Beállítások", icon: Settings2 },
];

function observedAt(raw: string | null | undefined): string {
  if(!raw)return "Nincs korábbi mérés";
  const d = new Date(raw);
  return Number.isNaN(d.getTime()) ? "Ismeretlen" : d.toLocaleTimeString("hu-HU");
}
function State({ running, status }: { running: boolean; status: string }) {
  return <span className={running ? "state online" : "state stopped"}>
    <i className="status-dot"/>{status || (running ? "Fut" : "Leállítva")}
  </span>;
}
function Panel({ title, children, className = "", action }: {
  title: string; children: React.ReactNode; className?: string; action?: React.ReactNode
}) {
  return <section className={`glass panel ${className}`}>
    <div className="panel-head"><h2>{title}</h2>{action}</div>
    <div className="panel-inner">{children}</div>
  </section>;
}
function MapTable({ maps, host, onPick, filter = "" }: {
  maps: PublicMap[]; host: string; onPick?: (id: string) => void; filter?: string
}) {
  const needle=filter.trim().toLocaleLowerCase("hu-HU");
  const rows=maps.filter(m=>m.name.toLocaleLowerCase("hu-HU").includes(needle));
  if(!rows.length)return <div className="premium-map-empty">
    <Server size={27}/>
    <b>{needle?"Nem található megfelelő map":"Nincs regisztrált map"}</b>
    <span>{needle?"Módosítsd a keresési kifejezést.":"A helyi szerverek a fenti importálással vehetők fel; az éles fájlokhoz nem nyúlunk."}</span>
  </div>;
  return <div className="premium-map-table" role="table" aria-label="Szerverek">
    <div className="premium-map-table-heading" role="row">
      <span role="columnheader">MAP / SZERVER</span>
      <span role="columnheader">GAZDAGÉP</span>
      <span role="columnheader">ÁLLAPOT</span>
      <span role="columnheader">GAME UDP</span>
      <span role="columnheader">RÉSZLETEK</span>
    </div>
    {rows.map(map=><div className={"premium-map-line"+(onPick?" is-local":" is-remote")}
        role="row" key={map.id} tabIndex={onPick?0:undefined}
        aria-label={map.name+" · "+host+" · "+(map.running?"Fut":"Leállítva")}
        onClick={onPick?()=>onPick(map.id):undefined}
        onKeyDown={onPick?(e)=>{
          if(e.key==="Enter"||e.key===" "){e.preventDefault();onPick(map.id);}
        }:undefined}>
      <span className="premium-map-title" role="cell">
        <MapCover name={map.name}/>
        <span><b>{map.name}</b>
          <small>ARK: Survival Ascended · {map.steamBuildId?
            "Build "+map.steamBuildId:"Build nem ellenőrzött"}</small>
        </span>
      </span>
      <span className="premium-map-host" role="cell">{host}</span>
      <span className="premium-map-status" role="cell">
        <State running={map.running} status={map.status}/>
      </span>
      <span className="premium-map-port mono" role="cell">{map.gamePort||"—"}</span>
      <span className="premium-map-actions" role="cell">
        {onPick?<span className="premium-map-action-symbol" title="Map részleteinek megnyitása">
          <ArrowRight size={17}/></span>:
          <span className="premium-map-readonly"><Shield size={16}/> Megfigyelés</span>}
      </span>
    </div>)}
  </div>;
}
function HostCard({ host, local = false }: {
  host: { hostName: string; maps: PublicMap[] }; local?: boolean
}) {
  const running = host.maps.filter(m => m.running).length;
  return <div className="host">
    <div className="host-symbol"><Monitor size={21}/></div>
    <div className="host-copy"><b>{host.hostName}</b><span className="muted tiny">{local ? "Ez a Windows-gazdagép" : "Hitelesített LAN-peer"}</span></div>
    <span className="host-count">{running}/{host.maps.length} fut</span>
  </div>;
}
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
    const mod=details?.mod ?? selectedBasicForCart();
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
function Dashboard({ snapshot, onPage, onSelect, onInstall }: {
  snapshot: DashboardSnapshot;
  onPage: (page: Page) => void; onSelect: (id: string) => void;
  onInstall: (mapName?:string) => void
}) {
  return <PremiumDashboard snapshot={snapshot} onSelect={onSelect}
    onServers={()=>onPage("servers")} onInstall={onInstall}
    onCluster={()=>onPage("cluster")}/>;
}
const asaMapNames = [
  "The Island","The Center","Scorched Earth","Aberration","Extinction",
  "Genesis Part 1","Valguero","Ragnarok","Astraeos","Lost Colony"
];
function LocalAsaImport({onImported}: {onImported:()=>Promise<void>}) {
  const [name,setName]=useState("The Island");
  const [root,setRoot]=useState("");
  const [preview,setPreview]=useState<LocalImportPreview | null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const validate = async (e:React.FormEvent) => {
    e.preventDefault(); setError(""); setNotice("");setPreview(null);
    setBusy(true);
    try { setPreview(await managerBridge.inspectLocalAsa(name,root)); }
    catch(e) {
      const code=e instanceof Error?e.message:"UM-IMPORT-VALIDATION-FAILED";
      setError(code==="UM-ASA-EXE-MISSING"
        ? "A megadott helyen nincs meg az ASA ShooterGame/Binaries/Win64/ArkAscendedServer.exe."
        : code==="UM-EXISTING-WORLD-REQUIRES-PRESERVED-IMPORT"
          ? "Ez a telepítés már világmentést tartalmaz. Ne importáld üres szerverként; használd a külön Store-v1 átvételt."
          : "A kiválasztott helyi telepítés nem importálható ("+code+"). Ellenőrizd a mappát és a már regisztrált mapokat.");
    } finally {setBusy(false);}
  };
  const confirm=async()=>{
    if(!preview)return;
    setBusy(true);setError("");setNotice("");
    try {
      const result=await managerBridge.importLocalAsa(preview.importToken);
      setPreview(null);
      setNotice(result.mapName+" bekerült a helyi Manager profiljába. A portok, RCON, modok és indítás később konfigurálhatók.");
      await onImported();
    } catch(e) {
      const code=e instanceof Error?e.message:"UM-IMPORT-VALIDATION-FAILED";
      setPreview(null);
      setError("A natív Manager megtagadta a regisztrációt ("+code+"). Lehetséges versengő konfigurációmódosítás; ellenőrizd a maplistát.");
    } finally{setBusy(false);}
  };
  const updateName=(v:string)=>{setName(v);setPreview(null);setNotice("");};
  const updateRoot=(v:string)=>{setRoot(v);setPreview(null);setNotice("");};
  return <Panel title="Helyi ASA-telepítés felvétele" action={<span className="mod-tag green">Telepítés változatlan</span>}>
    <p className="muted small">Meglévő ASA-szerver EXE importálása a saját Manager profilba. Nem másol, nem tölt le és nem indít fájlt. A friss SteamCMD telepítés külön művelet.</p>
    <div className="map-name-suggestions" aria-label="ASA mapválasztó">
      {asaMapNames.map(item=><button key={item} type="button" className={name===item?"map-choice selected":"map-choice"}
        onClick={()=>updateName(item)}>{item}</button>)}
    </div>
    <form onSubmit={e=>{void validate(e);}} className="import-form">
      <label>Map neve
        <input aria-label="Map neve" value={name} maxLength={100} onChange={e=>updateName(e.target.value)} required/>
      </label>
      <label>Helyi ASA telepítési gyökérkönyvtár (Windows)
        <input aria-label="Helyi ASA mappa" value={root} maxLength={520} onChange={e=>updateRoot(e.target.value)}
          placeholder="C:\\ASA\\TheIsland" autoComplete="off" required/>
      </label>
      <div className="import-actions">
        <button type="submit" disabled={busy || !root.trim() || !name.trim()} className="secondary">
          {busy?"Ellenőrzés...":"Helyi telepítés ellenőrzése"}
        </button>
        <button type="button" disabled={busy || !preview} className="primary" onClick={()=>{void confirm();}}>
          Ellenőrzött map regisztrálása
        </button>
      </div>
    </form>
    {preview && <div className="import-preview">
      <CheckCircle2 size={22}/><div><b>{preview.name} · ASA EXE megtalálva</b>
        <p>Mappa: {preview.folderName || "Kiválasztott helyi gyökér"} · ASA API loader: {preview.apiLoaderDetected?"észlelve, de nincs engedélyezve":"nem észlelhető"}</p>
        <p>Portok, RCON és modok: később beállíthatók. Nem indul ASA.</p>
      </div>
    </div>}
    {notice && <div className="success-inline" role="status"><CheckCircle2 size={16}/>{notice}</div>}
    {error && <div className="error-inline" role="alert"><AlertTriangle size={16}/>{error}</div>}
    <div className="warning-inline"><Shield size={17}/> Csak helyi, létező ASA EXE regisztrálható. Az új Managerben egy map hozzáadásával semmilyen világ- vagy karakterfájl nem módosul.</div>
  </Panel>;
}

function Servers({ snapshot, search, setSearch, onSelect, onImported }: {
  snapshot: DashboardSnapshot; search: string; setSearch: (v: string) => void; onSelect: (v: string) => void;
  onImported:()=>Promise<void>
}) {
  const [importMode,setImportMode]=useState<"none"|"new"|"existing"|"store">("none");
  // A first-run Windows test machine should show real map choices instead of
  // a half-page empty table. Existing cluster installs retain compact list.
  const [serverView,setServerView]=useState<"table"|"gallery">(
    snapshot.local.maps.length===0?"gallery":"table");
  const [installMap,setInstallMap]=useState("The Island");
  const openInstall=(mapName?:string)=>{if(mapName)setInstallMap(mapName);setImportMode("new");};
  return <><div className="page-intro"><span className="eyebrow">LOCAL HOST / LAN</span><h1>Szerverek</h1><p>A helyi mapok megfigyelése; távoli mapok kizárólag olvashatók.</p></div>
    <div className="server-toolbar">
      <div className="toolbar"><Search size={19}/><input aria-label="Map keresése" placeholder="Map keresése..." value={search} onChange={e=>setSearch(e.target.value)}/></div>
      <div className="server-import-buttons">
        <div className="premium-view-switcher" role="group" aria-label="Szervernézet">
          <button type="button" className={serverView==="table"?"is-active":""}
            aria-pressed={serverView==="table"} onClick={()=>setServerView("table")}><List size={15}/> Lista</button>
          <button type="button" className={serverView==="gallery"?"is-active":""}
            aria-pressed={serverView==="gallery"} onClick={()=>setServerView("gallery")}><Grid2X2 size={15}/> Kártyák</button>
        </div>
        <button className="secondary" onClick={()=>setImportMode(v=>v==="store"?"none":"store")}>
          {importMode==="store"?"Store-v1 import bezárása":"Store-v1 átvétel"}
        </button>
        <button className="secondary" onClick={()=>setImportMode(v=>v==="existing"?"none":"existing")}>
          {importMode==="existing"?"Import bezárása":"Meglévő ASA importálása"}
        </button>
        <button className="primary" onClick={()=>setImportMode(v=>v==="new"?"none":"new")}>
          {importMode==="new"?"Letöltő bezárása":"Új ASA letöltése"}
        </button>
      </div>
    </div>
    {importMode==="new" && <FreshAsaSteamWizard onRegistered={onImported} initialMapName={installMap}/>}
    {importMode==="existing" && <LocalAsaImport onImported={onImported}/>}
    {importMode==="store" && <HistoricalStoreImport onImported={onImported}/>}
    {serverView==="gallery"?<Panel title="Mapkártyák · helyi kezelés / távoli megfigyelés">
      <ClusterMapGallery snapshot={snapshot} query={search} onSelect={onSelect}
        onInstall={openInstall} onImport={()=>setImportMode("existing")}/>
    </Panel>:<>
      <Panel title={`Helyi mapok · ${snapshot.local.hostName}`}><MapTable maps={snapshot.local.maps} host={snapshot.local.hostName} filter={search} onPick={onSelect}/></Panel>
      {dashboardRemotes(snapshot).map(remote=><Panel key={remote.hostId}
        title={`Távoli mapok · ${remote.hostName}`}>
        <p className="muted small">{remote.fresh===false?"Elavult / offline megfigyelés":"Hitelesített LAN-adat"}
          {remote.lastSuccessUtc?` · utolsó sikeres mérés: ${observedAt(remote.lastSuccessUtc)}`:""}</p>
        <MapTable maps={remote.maps} host={remote.hostName} filter={search}/>
      </Panel>)}
    </>}
  </>;
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
function Mods({ snapshot, selected, setSelected }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (value: string) => void
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
    setWorkspaceTab("installed");setSavedNotice("");
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
      <select aria-label="Helyi map" value={selected} onChange={e=>setSelected(e.target.value)}>
        {snapshot.local.maps.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}
      </select>
      <span className="premium-small-muted">Az eredeti indítási paraméterek megőrzésével</span>
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
        {id:"profile",name:"Modprofilok HP / Ryzen",icon:Layers3}
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
function PluginCenter({ snapshot, selected, setSelected }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (v: string) => void
}) {
  const [info, setInfo] = useState<PluginInventory | null>(null);
  const [error, setError] = useState("");
  const [pluginStates,setPluginStates]=useState<PluginState|null>(null);
  const [preview,setPreview]=useState<PluginTogglePreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [working,setWorking]=useState(false);
  const [notice,setNotice]=useState("");
  const [revision,setRevision]=useState(0);
  const [jsonPlugin,setJsonPlugin]=useState("");
  const actionEpoch=useRef(0);
  useEffect(()=>{setJsonPlugin("");},[selected]);
  useEffect(() => {
    let active = true;
    setInfo(null); setError("");
    if (!selected) return;
    managerBridge.pluginInventory(selected).then(data => { if (active) setInfo(data); })
      .catch(e => { if (active) setError(e instanceof Error ? e.message : "UM-READ-UNAVAILABLE"); });
    return () => { active = false; };
  }, [selected,revision]);
  useEffect(()=>{
    let active=true;
    setPluginStates(null);
    if(!selected)return;
    managerBridge.pluginStates(selected)
      .then(data=>{if(active)setPluginStates(data);})
      .catch(()=>{if(active)setNotice("A pluginmappák állapota nem ellenőrizhető.");});
    return ()=>{active=false;};
  },[selected,revision]);
  const inspectToggle=async(plugin:string,enabled:boolean)=>{
    if(!selected || working)return;
    const epoch=++actionEpoch.current;
    setWorking(true);setPreview(null);setApproved(false);setNotice("");
    try{
      const result=await managerBridge.pluginTogglePreview(selected,plugin,enabled);
      if(epoch===actionEpoch.current)setPreview(result);
    }catch{
      if(epoch===actionEpoch.current)setNotice("A plugin előellenőrzése sikertelen. Nem történt módosítás.");
    }finally{if(epoch===actionEpoch.current)setWorking(false);}
  };
  const applyToggle=async()=>{
    if(!preview?.allowed || !preview.token || !approved || working)return;
    setWorking(true);setNotice("");
    try{
      const result=await managerBridge.pluginToggleApply(preview.token);
      setPreview(null);setApproved(false);
      setNotice(result.plugin+" · "+(result.enabled?"engedélyezve":"letiltva")+
        ". A teljes könyvtár mentése elkészült; a változás a következő ASA-indításkor lép érvénybe.");
      setRevision(n=>n+1);
    }catch{
      setPreview(null);setApproved(false);
      setNotice("A pluginművelet nem igazolható. Ellenőrizd a fájlokat és a helyi mapok állapotát, majd készíts új előnézetet.");
    }finally{setWorking(false);}
  };
  return <div className="premium-plugin-page">
    <div className="page-intro"><span className="eyebrow">ASA API / LOCAL INSTALLATION</span><h1>Plugin Center</h1>
      <p>Helyi pluginleltár és ellenőrzött engedélyezés/tiltás. A módosítás csak leállított szervereken, előnézet és jóváhagyás után történik.</p></div>
    <div className="premium-mod-map-picker"><Plug size={19}/><select aria-label="Pluginok mapja" value={selected}
      disabled={working} onChange={e=>{
        actionEpoch.current++;setPreview(null);setApproved(false);
        setNotice("");setSelected(e.target.value);
      }}>
      {snapshot.local.maps.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}</select>
    </div>
    <div className="premium-plugin-grid">
    <Panel className="premium-plugin-inventory" title={info ? `Helyi pluginok · ${info.mapName}` : "Pluginok"}>
      {error && <div className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      {!error && !selected && <div className="empty">Nincs kiválasztható helyi map.</div>}
      {!error && selected && !info && <div className="empty">Helyi telepítés vizsgálata...</div>}
      {info?.plugins.length === 0 && <div className="empty">Nem található plugin a regisztrált map telepítési helyén.</div>}
      {info?.plugins.map((plugin,i) => <div className="plugin-row" key={plugin.name + i}>
        <span className="plugin-symbol"><Plug size={21}/></span>
        <div className="plugin-copy"><b>{plugin.name}</b><small>{plugin.binaries.map(b => b.name + " · " + (b.version || "verzió nélkül")).join(", ") || "Nincs DLL-adat"}</small></div>
        <span className={plugin.protectedData ? "mod-tag amber" : "mod-tag green"}>{plugin.protectedData ? "Védett" : "Helyi leltár"}</span>
        <button type="button" className="secondary" disabled={working||
          Boolean(snapshot.local.maps.find(m=>m.id===selected)?.running)||
          Boolean(snapshot.local.maps.find(m=>m.id===selected)?.storeProtected)}
          onClick={()=>setJsonPlugin(plugin.name)}>
          config.json szerkesztése
        </button>
      </div>)}
      <div className="warning-inline"><Shield size={17}/> Csak fájlleltár. Nem bizonyítja, hogy a futó ASA betöltötte a plugint.</div>
    </Panel>
    <Panel className="premium-plugin-actions" title="Pluginállapot · biztonságos műveletek" action={<button type="button"
      className="quiet" disabled={working} onClick={()=>{
        actionEpoch.current++;setPreview(null);setApproved(false);
        setNotice("");setRevision(n=>n+1);
      }}><RefreshCw size={15}/> Mappák frissítése</button>}>
      <p className="muted small">Az eredeti C# PluginStateManager az egész pluginmappát mozgatja; beállítások, licencfájlok és helyi adatállományok ellenőrzött mentésben megmaradnak. Az API futás közbeni betöltöttsége nem következtethető ki a lemezállapotból.</p>
      {pluginStates?.historicalProtected && <div className="warning-inline">
        <Shield size={17}/> Ez a Store-v1 map védett. Pluginművelet tiltva.
      </div>}
      {!pluginStates && selected && <div className="empty">Pluginmappák állapotának olvasása...</div>}
      {pluginStates && pluginStates.plugins.length===0 && <div className="empty">
        Nincs engedélyezett vagy letiltott pluginmappa.
      </div>}
      {pluginStates?.plugins.map(plugin=><div className="plugin-row" key={plugin.name}>
        <span className="plugin-symbol"><Plug size={19}/></span>
        <div className="plugin-copy"><b>{plugin.name}</b>
          <small>{plugin.enabled?"Engedélyezett mappa":"Letiltott mappa"} · ASA futásidőben nem igazolt</small>
        </div>
        <button type="button" className="secondary" disabled={working||Boolean(pluginStates?.historicalProtected)}
          onClick={()=>{void inspectToggle(plugin.name,!plugin.enabled);}}>
          {plugin.enabled?"Letiltás előnézete":"Engedélyezés előnézete"}
        </button>
      </div>)}
      {preview && <div className="historical-preview" role="status">
        <h3>{preview.plugin} · {preview.targetEnabled?"Engedélyezés":"Letiltás"}</h3>
        <p className="muted small">{preview.reason}</p>
        <div className="details-line"><span>Megvizsgált helyi mapok</span>
          <b>{preview.checkedLocalMaps}</b></div>
        <div className="details-line"><span>Éles API-runtime betöltés</span><b>Nem igazolt</b></div>
        {preview.allowed && preview.token ? <>
          <label className="historical-approval"><input type="checkbox" checked={approved}
            disabled={working} onChange={e=>setApproved(e.target.checked)}/>
            Jóváhagyom a helyi pluginmappa áthelyezését, teljes ellenőrzött biztonsági mentéssel.
          </label>
          <button type="button" className="primary" disabled={!approved||working}
            onClick={()=>{void applyToggle();}}>Pluginállapot módosításának jóváhagyása</button>
        </> : <div className="warning-inline"><Shield size={17}/> A módosítás zárolva marad.</div>}
      </div>}
      {notice && <div role="status" className="warning-inline"><AlertTriangle size={17}/>{notice}</div>}
    </Panel>
    </div>
    <AsaApiCoreInstaller mapId={selected} onInstalled={()=>setRevision(n=>n+1)}/>
    <PluginZipInstaller mapId={selected} onInstalled={()=>setRevision(n=>n+1)}/>
    {jsonPlugin&&<PluginJsonEditor key={selected+"-"+jsonPlugin} mapId={selected}
      plugin={jsonPlugin} onClose={()=>setJsonPlugin("")}
      onSaved={()=>setRevision(n=>n+1)}/>} 
  </div>;
}
function BackupCenter({mapId,snapshot}:{mapId?:string;snapshot:DashboardSnapshot}) {
  const [info,setInfo]=useState<BackupOverview|null>(null);
  const [error,setError]=useState("");
  const [busy,setBusy]=useState(false);
  const [filter,setFilter]=useState("");
  const reload=useCallback(async()=>{
    setBusy(true);
    try{setInfo(await managerBridge.backupOverview());setError("");}
    catch(e){setError(e instanceof Error?e.message:"UM-READ-UNAVAILABLE");}
    finally{setBusy(false);}
  },[]);
  useEffect(()=>{void reload();},[reload]);
  const scope=useMemo(()=>(info?.maps??[]).filter(m=>!mapId||m.mapId===mapId),
    [info,mapId]);
  const visible=useMemo(()=>{
    const term=filter.trim().toLocaleLowerCase("hu-HU");
    return scope.filter(m=>m.mapName.toLocaleLowerCase("hu-HU").includes(term));
  },[scope,filter]);
  const totals=useMemo(()=>scope.reduce((all,m)=>({
    valid:all.valid+m.validPackages,
    invalid:all.invalid+m.invalidPackages,
    bytes:all.bytes+m.verifiedBytes
  }),{valid:0,invalid:0,bytes:0}),[scope]);
  const recent=useMemo(()=>scope.filter(m=>m.latestVerifiedUtc&&
    !Number.isNaN(new Date(m.latestVerifiedUtc).getTime()))
    .sort((a,b)=>new Date(b.latestVerifiedUtc!).getTime()-
      new Date(a.latestVerifiedUtc!).getTime())[0], [scope]);
  return <div className="premium-backup-page">
    <div className="page-intro"><span className="eyebrow">BACKUP / FILE INTEGRITY</span>
      <h1>Mentésközpont</h1>
      <p>Valódi, ellenőrzött világ-, karakter- és törzsmentés az adott map leállítása
        után; külön védőmentéssel és az eredeti világ megőrzésével történő
        visszaállítás. Az éles kétgépes Store-v1 konzisztencia ettől külön igazolandó.</p>
    </div>
    <WorldBackupControls maps={snapshot.local.maps} selectedMap={mapId}/>
    <OfflineAsaArchivePanel/>
    <OfflineAsaRestorePanel/>
    <div className="premium-backup-metrics">
      <div><span className="premium-backup-icon"><Archive size={24}/></span>
        <b>{scope.length}</b><span>Map</span><small>Mentésleltárban</small></div>
      <div><span className="premium-backup-icon"><CheckCircle2 size={24}/></span>
        <b>{totals.valid}</b><span>Rendben</span><small>Igazolt integritás</small></div>
      <div className={totals.invalid?"has-errors":""}>
        <span className="premium-backup-icon"><AlertTriangle size={24}/></span>
        <b>{totals.invalid}</b><span>Hibás</span><small>Fájlcsomag</small></div>
      <div><span className="premium-backup-icon"><HardDrive size={24}/></span>
        <b>{(totals.bytes/1073741824).toFixed(2)}</b><span>GiB</span>
        <small>Ellenőrzött bájtok</small></div>
    </div>
    <div className="premium-backup-grid">
      <section className="premium-panel premium-backup-list">
        <header className="premium-panel-heading">
          <h2><Archive size={19}/> Mapok mentései</h2>
          <div className="premium-backup-controls">
            <label className="premium-search"><Search size={15}/>
              <input aria-label="Mentés map keresése" placeholder="Map keresése..."
                value={filter} onChange={e=>setFilter(e.target.value)}/></label>
            <button type="button" className="secondary" onClick={()=>{void reload();}}
              disabled={busy}><RefreshCw size={15}/> Frissítés</button>
          </div>
        </header>
        {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
        {!info&&!error&&<div className="premium-map-empty">Mentésadatok ellenőrzése...</div>}
        {info&&visible.length===0&&<div className="premium-map-empty">
          <Archive size={28}/>
          <b>{filter?"Nincs találat":"Nincs regisztrált ellenőrzött mentés"}</b>
          <span>{filter?"Próbálj másik mapnevet.":"A mentéslistát a helyi Managerből olvassuk. Nem jelenítünk meg szintetikus csomagokat."}</span>
        </div>}
        {visible.map(m=><div className="premium-backup-row" key={m.mapId}>
          <div className="premium-backup-map"><MapCover name={m.mapName}/>
            <div><b>{m.mapName}</b><small>Legutóbbi integritás-ellenőrzés:
              {" "}{m.latestVerifiedUtc?new Date(m.latestVerifiedUtc).toLocaleString("hu-HU"):
              "Nem ismert"}</small></div>
          </div>
          <div className="premium-backup-row-status">
            <strong className="premium-backup-ok">{m.validPackages} rendben</strong>
            <span className={m.invalidPackages?"premium-backup-bad":"muted"}>
              {m.invalidPackages} hibás</span>
            <small>{(m.verifiedBytes/1073741824).toFixed(2)} GiB</small>
          </div>
        </div>)}
      </section>
      <aside className="premium-panel premium-backup-integrity">
        <header className="premium-panel-heading">
          <h2><Shield size={19}/> Mentésbiztonság</h2>
        </header>
        <div className="premium-backup-integrity-body">
          <div className="premium-backup-evidence">
            <CheckCircle2 size={20}/>
            <div><b>Fájlintegritás</b>
              <span>Helyileg ellenőrzött csomagok, kizárólag olvasás</span></div>
          </div>
          <div className="premium-backup-fact"><span>Utolsó ismert ellenőrzés</span>
            <strong>{recent?.latestVerifiedUtc?
              new Date(recent.latestVerifiedUtc).toLocaleString("hu-HU"):"Nincs adat"}</strong></div>
          <div className="premium-backup-fact"><span>Mentésállományok</span>
            <strong>{totals.valid+totals.invalid} csomag</strong></div>
          <div className="premium-backup-fact"><span>Visszaállítás</span>
            <strong>Leállított, nem Store-v1 mapon · védőmentéssel</strong></div>
          <div className="premium-backup-caution"><AlertTriangle size={18}/>
            <span>Az ellenőrzött fájlméret és hash <b>nem bizonyítja</b> az ASA
              SaveComplete vagy a kétgépes Store-v1 karakterutazás biztonságát.</span>
          </div>
        </div>
      </aside>
    </div>
  </div>;
}
function SteamCenter({ snapshot, onRegistered, initialMapName }: {
  snapshot: DashboardSnapshot; onRegistered:()=>Promise<void>;
  initialMapName:string
}) {
  const [inventory, setInventory] = useState<SteamInstallations | null>(null);
  const [ports, setPorts] = useState<SuggestedPorts | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const reload = useCallback(async () => {
    setBusy(true); setError("");
    const [installs, suggestion] = await Promise.allSettled([
      managerBridge.steamInstallations(), managerBridge.suggestedPorts()
    ]);
    setInventory(installs.status === "fulfilled" ? installs.value : null);
    setPorts(suggestion.status === "fulfilled" ? suggestion.value : null);
    if (installs.status === "rejected" || suggestion.status === "rejected")
      setError("A helyi telepítési leltár vagy a portjavaslat nem érhető el. Egyetlen fájlt sem módosítottunk.");
    setBusy(false);
  }, []);
  useEffect(() => { void reload(); }, [reload]);
  return <>
    <div className="page-intro"><span className="eyebrow">VALVE STEAMCMD / INSTALLED BUILD</span><h1>SteamCMD Center</h1>
      <p>Valódi C#-os helyi telepítés és külön jóváhagyott SteamCMD-frissítés leállított mapokon. Az éles Store-v1 szervervezérlés ettől független.</p></div>
    <Panel title="Regisztrált ASA mapok" action={<button className="quiet" onClick={() => { void reload(); }} disabled={busy}><RefreshCw size={16}/> Ellenőrzés</button>}>
      {snapshot.local.maps.length===0 && <div className="empty">Nincs hozzáadott helyi telepítés.</div>}
      {snapshot.local.maps.map(m => <div className="steam-row" key={m.id}>
        <div className="plugin-copy"><b>{m.name}</b><small>Game UDP: {m.gamePort || "nem ismert"}</small></div>
        <div className="backup-stats"><strong>Build: {m.steamBuildId || "nem ismert"}</strong><span>{m.steamUpdateState}</span></div>
      </div>)}
    </Panel>
    <Panel title="Steam telepítési könyvtárak · ellenőrzött leltár">
      {error && <div role="status" className="warning-inline"><AlertTriangle size={17}/>{error}</div>}
      {!inventory && !error && <div className="empty">Helyi telepítési adatok olvasása...</div>}
      {inventory?.installations.length===0 && <div className="empty">Nincs vizsgálható helyi ASA telepítés.</div>}
      {inventory?.installations.map(entry => <div className="installation-row" key={entry.id}>
        <span className="plugin-symbol"><HardDrive size={22}/></span>
        <div className="plugin-copy"><b>{entry.maps.join(", ")}</b>
          <small>ASA EXE: {entry.executablePresent ? "megtalálva" : "hiányzik"} · Build: {entry.installedSteamBuild || "ismeretlen"}</small>
          <small>{entry.reason}</small>
        </div>
        <span className="mod-tag amber">Kézi előellenőrzés</span>
      </div>)}
    </Panel>
    <StoppedSteamUpdate maps={snapshot.local.maps}/>
    <FreshAsaSteamWizard onRegistered={onRegistered} initialMapName={initialMapName}/>
    <Panel title="Új map · konfigurációs előkészítés">
      <p className="muted small">Az új telepítés után a szerver nevének és portjainak ellenőrzött beállítása a map Áttekintés oldalán érhető el. A javaslat önmagában nem indít SteamCMD-műveletet.</p>
      {ports && <div className="port-strip">
        <div><small>Game UDP</small><strong>{ports.game}</strong></div>
        <div><small>Query UDP</small><strong>{ports.query}</strong></div>
        <div><small>RCON TCP</small><strong>{ports.rcon}</strong></div>
      </div>}
      <div className="warning-inline"><Shield size={17}/> A portjavaslat csak a helyi profil ütközéseit ellenőrzi. Nem vizsgálja a foglalt OS-portokat és a routert.</div>
    </Panel>
  </>;
}
function LogsCenter({ snapshot, selected, setSelected }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (value: string) => void
}) {
  const [result, setResult] = useState<LogTail | null>(null);
  const [error, setError] = useState("");
  const [query, setQuery] = useState("");
  const [level, setLevel] = useState<"all" | "info" | "warning" | "error">("all");
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    let active = true;
    setResult(null); setError("");
    if (!selected) return;
    managerBridge.logTail(selected)
      .then(data => { if (active) setResult(data); })
      .catch(() => { if (active) setError("A regisztrált helyi ASA napló nem olvasható vagy nincs konfigurálva."); });
    return () => { active = false; };
  }, [selected, generation]);
  const filtered = useMemo(() => (result?.lines ?? []).filter(row =>
    (level === "all" || row.level === level) &&
    row.content.toLocaleLowerCase("hu-HU").includes(query.toLocaleLowerCase("hu-HU"))
  ), [result, query, level]);
  const logStats=useMemo(()=>{
    const rows=result?.lines??[];
    return {
      loaded:rows.length,
      warnings:rows.filter(line=>line.level==="warning").length,
      errors:rows.filter(line=>line.level==="error").length,
      filtered:filtered.length
    };
  },[result,filtered]);
  return <div className="premium-log-page">
    <div className="page-intro"><span className="eyebrow">LOCAL ASA / SAFE LOG TAIL</span><h1>Naplóközpont</h1>
      <p>Legfeljebb 300, a meglévő C# által titokszűrt naplósor — csak regisztrált helyi mapról. Nem jelent ASA állapot- vagy mentési bizonyítékot.</p></div>
    <div className="premium-log-metrics">
      <div><span><FileText size={19}/></span><b>{logStats.loaded}</b><small>Olvasott naplósor</small></div>
      <div><span><Search size={19}/></span><b>{logStats.filtered}</b><small>Szűrésben látható</small></div>
      <div><span><AlertTriangle size={19}/></span><b>{logStats.warnings}</b><small>Figyelmeztetés</small></div>
      <div><span><Shield size={19}/></span><b>{logStats.errors}</b><small>Hiba</small></div>
    </div>
    <div className="log-toolbar premium-log-toolbar">
      <label className="log-select-label">Helyi map
        <select aria-label="Napló mapja" value={selected} onChange={e => setSelected(e.target.value)}>
          {snapshot.local.maps.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
        </select>
      </label>
      <label className="log-select-label">Szint
        <select aria-label="Naplószint" value={level} onChange={e => setLevel(e.target.value as typeof level)}>
          <option value="all">Összes</option><option value="info">Információ</option>
          <option value="warning">Figyelmeztetés</option><option value="error">Hiba</option>
        </select>
      </label>
      <label className="log-select-label log-search-label">Keresés
        <input aria-label="Keresés a naplóban" value={query} maxLength={128}
          onChange={e => setQuery(e.target.value)} placeholder="Szöveg szűrése..."/>
      </label>
      <button className="icon-button" aria-label="Napló újraolvasása" onClick={() => setGeneration(x => x+1)}><RefreshCw size={18}/></button>
    </div>
    <div className="premium-log-view">
    <Panel title={result ? `Naplórészlet · ${result.mapName}` : "Naplórészlet"}
      action={result && <span className="muted tiny">Módosítva: {observedAt(result.lastModifiedUtc)} · {result.truncated ? "Részlet (levágva)" : "Teljes olvasott rész"}</span>}>
      {error && <div role="status" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      {!selected && <div className="empty">Nincs kiválasztott helyi map.</div>}
      {selected && !result && !error && <div className="empty">Naplósorok betöltése...</div>}
      {result && <div className="log-list premium-log-list" role="log" aria-label="Titokszűrt helyi napló">
        {filtered.length===0 && <div className="empty">Nincs a szűrőnek megfelelő naplósor.</div>}
        {filtered.map(row => <div key={row.number} className={`log-row log-${row.level}`}>
          <span className="log-number">{row.number}</span><span className="log-level">{row.level}</span>
          <span className="log-message">{row.content}</span>
        </div>)}
      </div>}
      <div className="warning-inline"><Shield size={17}/> Titokszűrt, korlátozott méretű naplórészlet. A bejegyzés önmagában nem igazolja a SaveComplete vagy RCON művelet sikerét.</div>
    </Panel>
    </div>
  </div>;
}

function IniCenter({ snapshot, selected, setSelected }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (v: string) => void
}) {
  const [file, setFile] = useState<IniFile>("Game.ini");
  const [opened, setOpened] = useState<IniDocument | null>(null);
  const [content, setContent] = useState("");
  const [preview, setPreview] = useState<IniPreview | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");
  useEffect(() => {
    setOpened(null); setContent(""); setPreview(null);
    setNotice(""); setError("");
  }, [selected, file]);
  const load = async () => {
    if (!selected) return;
    setBusy(true); setError(""); setNotice(""); setPreview(null);
    try {
      const fresh = await managerBridge.iniOpen(selected, file);
      setOpened(fresh); setContent(fresh.content);
    } catch(e) {
      const code = e instanceof Error ? e.message : "UM-INI-OPERATION-FAILED";
      const message:Record<string,string>={
        "UM-INI-STOP-REQUIRED":"A map nincs igazoltan leállítva, vagy karbantartás folyik. Az INI-szerkesztés tiltva.",
        "UM-INI-INSTALLATION-MISSING":"A regisztrált ASA telepítési gyökér hiányzik vagy nem helyi útvonal. Ellenőrizd a map regisztrációját.",
        "UM-INI-SERVER-EXE-MISSING":"A regisztrált telepítésben nem található az elvárt ArkAscendedServer.exe. Ellenőrizd a SteamCMD telepítés útvonalát.",
        "UM-INI-CONFIG-DIRECTORY-MISSING":"Az ASA még nem hozta létre a Saved/Config/WindowsServer mappát. Első indítás előtt ez normális lehet. Állítsd be a mapot, majd készítsd elő az első RCON-konfigurációt.",
        "UM-INI-GAME-MISSING":"A Game.ini még nem létezik. Ez önmagában nem telepítési hiba; az ASA első indítása után próbáld újra.",
        "UM-INI-GUS-MISSING":"A GameUserSettings.ini még nem létezik. Új, leállított mapnál az első RCON-előkészítő jóváhagyással hozhatja létre.",
        "UM-INI-ACCESS-DENIED":"A Windows megtagadta az INI olvasását. Ellenőrizd a jogosultságokat.",
        "UM-INI-PATH-OR-CONTENT-UNSAFE":"Az INI útvonala, linkje vagy tartalma nem felelt meg a biztonsági ellenőrzésnek.",
        "UM-INI-CONFLICT-OR-IO-ERROR":"Az INI fájl zárolt, megváltozott vagy nem olvasható biztonságosan.",
        "UM-HISTORICAL-STORE-LOCKED":"A történeti Store-v1 map konfigurációja védett."
      };
      setError(message[code]??"A natív INI-ellenőrzés megtagadta a megnyitást ("+code+").");
    } finally { setBusy(false); }
  };
  const showPreview = async () => {
    if (!opened) return;
    setBusy(true); setError(""); setNotice("");
    try { setPreview(await managerBridge.iniPreview(opened.token, content)); }
    catch { setPreview(null); setError("Az INI előnézet érvénytelen vagy lejárt. Nem írtunk fájlba."); }
    finally { setBusy(false); }
  };
  const save = async () => {
    if (!opened || !preview?.changed) return;
    setBusy(true); setError(""); setNotice("");
    try {
      const result = await managerBridge.iniSave(opened.token);
      setOpened(null); setPreview(null); setContent("");
      setNotice(result.backupCreated
        ? "Az INI mentése atomikusan befejeződött, visszaállítási .bak másolattal."
        : "Az INI mentése befejeződött.");
    } catch(e) {
      const code = e instanceof Error ? e.message : "UM-INI-OPERATION-FAILED";
      setError("Az INI-mentést a natív biztonsági ellenőrzés elutasította ("+code+"). Ellenőrizd, hogy a map leállt-e, és nem változott-e közben a fájl.");
      setPreview(null);
    } finally { setBusy(false); }
  };
  const close = async () => {
    if (!opened) return;
    setBusy(true);setError("");
    try { await managerBridge.iniClose(opened.token); }
    catch { /* Local draft expires; discarding UI text never writes a file. */ }
    finally { setOpened(null); setContent(""); setPreview(null); setBusy(false); }
  };
  return <>
    <div className="page-intro"><span className="eyebrow">LOCAL CONFIGURATION / ETAG PROTECTION</span><h1>INI szerkesztő</h1>
      <p>A valódi Game.ini és GameUserSettings.ini szerkesztése, eredeti kódolás és sortörés megőrzésével. Csak igazoltan leállított, helyi ASA mapnál.</p></div>
    <div className="ini-toolbar">
      <label>Helyi map
        <select aria-label="INI mapja" value={selected} onChange={e=>setSelected(e.target.value)}>
          {snapshot.local.maps.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}
        </select>
      </label>
      <label>INI-fájl
        <select aria-label="INI fájl" value={file} onChange={e=>setFile(e.target.value as IniFile)}>
          <option value="Game.ini">Game.ini</option><option value="GameUserSettings.ini">GameUserSettings.ini</option>
        </select>
      </label>
      <button className="primary" onClick={() => { void load(); }} disabled={busy || !selected}>Fájl biztonságos megnyitása</button>
    </div>
    <Panel title={opened ? `Szerkesztés · ${opened.file}` : "Helyi konfiguráció"}
      action={opened && <span className="muted tiny">Kódolás: {opened.encoding} · {opened.bytes.toLocaleString("hu-HU")} byte</span>}>
      {!opened && <div className="empty">Válaszd ki a regisztrált helyi mapot és az INI-fájlt. Megnyitás csak igazolt leállítás esetén lehetséges.</div>}
      {opened && <>
        <label className="ini-editor-label" htmlFor="ini-contents">Az eredeti fájl szerkeszthető tartalma</label>
        <textarea id="ini-contents" className="ini-editor" spellCheck={false} value={content}
          onChange={e=>{setContent(e.target.value);setPreview(null);}} disabled={busy}/>
        <div className="ini-actions">
          <button className="secondary" disabled={busy} onClick={() => { void close(); }}>Mégse · piszkozat elvetése</button>
          <button className="primary" disabled={busy || content===opened.content} onClick={() => { void showPreview(); }}>Hash és változások előnézete</button>
          <button className="primary" disabled={busy || !preview?.changed} onClick={() => { void save(); }}>Jóváhagyás és atomikus mentés</button>
        </div>
        {preview && <div className="ini-preview">
          <span>Változás: <b>{preview.changed ? "Igen" : "Nincs"}</b></span>
          <span>Sorok: <b>{preview.oldLines} → {preview.newLines}</b></span>
          <span>Eredeti SHA-256: <code>{preview.originalSha256.slice(0,16)}…</code></span>
          <span>Új SHA-256: <code>{preview.nextSha256.slice(0,16)}…</code></span>
        </div>}
      </>}
      {notice && <div role="status" className="success-inline"><CheckCircle2 size={18}/>{notice}</div>}
      {error && <div role="alert" className="error-inline"><AlertTriangle size={18}/>{error}</div>}
      <div className="warning-inline"><Shield size={17}/> Az INI jelszavakat tartalmazhat: kizárólag a helyi, védett Windows WebView2 felületen szerkeszd. Mentéskor a natív C# újraellenőrzi a leállítást és az eredeti fájl SHA-256 lenyomatát.</div>
    </Panel>
  </>;
}

function SettingsCenter() {
  const [status, setStatus] = useState<boolean | null>(null);
  const [credential, setCredential] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const statusReload = useCallback(async () => {
    try { const result = await managerBridge.curseForgeKeyStatus(); setStatus(result.configured); setError(""); }
    catch { setError("A natív hitelesítőadat-tároló nem olvasható."); }
  }, []);
  useEffect(() => { void statusReload(); }, [statusReload]);
  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    if (credential.length === 0 || credential.length > 512) {
      setError("Az API-kulcs hossza 1–512 karakter lehet."); return;
    }
    setBusy(true); setError(""); setMessage("");
    try {
      const response = await managerBridge.saveCurseForgeKey(credential);
      setStatus(response.configured);
      setCredential("");
      setMessage(response.verifiedNow
        ? "A CurseForge hivatalos API-ja elfogadta a kulcsot; a natív Manager Windows DPAPI-val tárolta."
        : "A kulcs tárolása megváltozott.");
    } catch (e) {
      const code = e instanceof Error ? e.message : "UM-CREDENTIAL-OPERATION-FAILED";
      setError(code === "UM-CURSEFORGE-VERIFY-FAILED" ? "A CurseForge nem fogadta el vagy nem tudta ellenőrizni a kulcsot. A régi beállítás megmaradt."
        : code === "UM-CURSEFORGE-TIMEOUT" ? "Időtúllépés a hivatalos CurseForge szerver ellenőrzése közben. A régi kulcs megmaradt."
        : "Nem sikerült biztonságosan menteni a kulcsot (" + code + ").");
    } finally { setBusy(false); }
  };
  const clear = async () => {
    if (!deleteConfirm) { setDeleteConfirm(true); return; }
    setBusy(true); setDeleteConfirm(false); setError(""); setMessage("");
    try {
      const response = await managerBridge.clearCurseForgeKey();
      setStatus(response.configured);
      setMessage("A helyi CurseForge API-kulcs beállítása törölve lett.");
    } catch { setError("Nem sikerült törölni a kulcsot; a korábbi konfiguráció megmaradhatott."); }
    finally { setBusy(false); }
  };
  return <>
    <div className="page-intro"><span className="eyebrow">SECURITY / NATIVE WINDOWS SETTINGS</span><h1>Beállítások</h1>
      <p>A CurseForge API-kulcs kizárólag a Windows-profilodban, DPAPI-védelemmel tárolódik. Nincs GitHub-feltöltés vagy frontend-tárolás.</p></div>
    <Panel title="CurseForge API integráció" action={<span className="mod-tag grey">Natív Windows tároló</span>}>
      <div className="settings-state"><Shield size={20}/><div>
        <strong>{status === null ? "Állapot lekérdezése..." : status ? "API-kulcs helyileg beállítva" : "Nincs beállított API-kulcs"}</strong>
        <p className="muted small">A tárolt kulcsot a felület soha nem jeleníti meg. Régi kulcs sikeres ellenőrzés nélkül nem tekinthető hitelesítettnek.</p>
      </div></div>
      <form className="settings-form" onSubmit={e => { void save(e); }}>
        <label htmlFor="cf-key">CurseForge API-kulcs (nem kerül megjelenítésre)</label>
        <input id="cf-key" type="password" autoComplete="off" spellCheck={false} maxLength={512}
          value={credential} onChange={e=>setCredential(e.target.value)} placeholder="Helyi API-kulcs megadása"/>
        <div className="settings-actions">
          <button className="primary" type="submit" disabled={busy || credential.length===0}>
            <Shield size={16}/>{busy ? "Hivatalos ellenőrzés..." : "Kulcs ellenőrzése és mentése"}
          </button>
          <button className="secondary" type="button" onClick={() => { void clear(); }} disabled={busy || status===false}>
            {deleteConfirm ? "Megerősítés: kulcs törlése" : "Mentett kulcs törlése"}
          </button>
          {deleteConfirm && <button className="quiet" type="button" onClick={()=>setDeleteConfirm(false)}>Mégse</button>}
        </div>
      </form>
      {message && <div className="success-inline" role="status"><CheckCircle2 size={18}/>{message}</div>}
      {error && <div className="error-inline" role="alert"><AlertTriangle size={18}/>{error}</div>}
      <div className="warning-inline"><Shield size={18}/> Kizárólag az API-kulcs kerül mentésre. Semmilyen ASA/Store/SteamCMD művelet nem indul.</div>
    </Panel>
    <LanSettingsPanel/>
    <Panel title="Manager integrációk">
      <div className="details-line"><span>Frontend</span><b>React / TypeScript · offline</b></div>
      <div className="details-line"><span>Natív Windows-felület</span><b>WebView2 / WinForms</b></div>
      <div className="details-line"><span>ASA műveletek</span><b>Friss helyi mapokon külön jóváhagyás; történeti Store-v1 zárolva</b></div>
    </Panel>
  </>;
}

function Cluster({ snapshot }: { snapshot: DashboardSnapshot }) {
  const remotes=dashboardRemotes(snapshot);
  const protectedMapIds=useMemo(()=>new Set(snapshot.local.maps
    .filter(map=>map.storeProtected).map(map=>map.id.toLowerCase())),[snapshot.local.maps]);
  return <><div className="page-intro"><span className="eyebrow">LAN / OWNERSHIP</span><h1>Cluster</h1><p>Gazdagépenként saját Manager, közös státusznézet, távoli parancsok nélkül.</p></div>
    <div className="cluster-grid">
      <Panel title="Ez a gazdagép"><HostCard host={snapshot.local} local/><MapTable maps={snapshot.local.maps} host={snapshot.local.hostName}/></Panel>
      <Panel title={`Távoli gazdagépek · ${remotes.length}`}>
        {remotes.length?remotes.map(remote=><section className="cluster-remote-host" key={remote.hostId}>
          <HostCard host={remote}/>
          <p className={remote.fresh===false?"warning-inline":"muted small"}>
            {remote.fresh===false?"Elavult / offline · nincs friss hitelesített mérés":"Legutóbbi hitelesített mérés"}
            {remote.lastSuccessUtc?` · ${observedAt(remote.lastSuccessUtc)}`:remote.observedUtc?` · ${observedAt(remote.observedUtc)}`:""}
            {remote.stateCode?` · ${remote.stateCode}`:""}
          </p>
          <MapTable maps={remote.maps} host={remote.hostName}/>
        </section>):<div className="warning-inline"><WifiOff size={19}/>{snapshot.peerMessage||"Nincs párosított távoli adat"}</div>}
      </Panel>
    </div>
    <ClusterRegistryPanel protectedMapIds={protectedMapIds}/>
    <MultiHostBackupPanel/>
  </>;
}
type MapSection = "overview" | "monitor" | "mods" | "plugins" | "ini" | "backups" | "logs" | "rcon";
function MapDetails({snapshot,mapId,onBack,setSelected,onConfigured}: {
  snapshot:DashboardSnapshot;mapId:string;onBack:()=>void;
  setSelected:(value:string)=>void;onConfigured:()=>Promise<void>
}) {
  const [section,setSection]=useState<MapSection>("overview");
  const [initialSetupSaved,setInitialSetupSaved]=useState(false);
  const [lifecycle,setLifecycle]=useState<AsaLifecycleJournal|null>(null);
  const [restartJournal,setRestartJournal]=useState<AsaRestartJournalStatus|null>(null);
  const [journalsVerifiedFor,setJournalsVerifiedFor]=useState("");
  const [journalReadError,setJournalReadError]=useState(false);
  useEffect(()=>{setSection("overview");setInitialSetupSaved(false);},[mapId]);
  useEffect(()=>{
    let active=true;
    setLifecycle(null);setRestartJournal(null);
    setJournalsVerifiedFor("");setJournalReadError(false);
    Promise.all([
      managerBridge.asaLifecycleJournal(mapId),
      managerBridge.freshAsaRestartCurrent(mapId)
    ]).then(([lifecycleState,restartState])=>{
      if(!active)return;
      setLifecycle(lifecycleState);
      setRestartJournal(restartState);
      setJournalsVerifiedFor(mapId);
    }).catch(()=>{
      if(active)setJournalReadError(true);
    });
    return ()=>{active=false;};
  },[mapId]);
  const lifecycleBlocked=journalsVerifiedFor!==mapId||
    Boolean(lifecycle?.needsOperatorReview||restartJournal?.needsOperatorReview);
  const map=snapshot.local.maps.find(m=>m.id===mapId);
  if(!map)return <div className="empty">A kiválasztott map már nem található a helyi Managerben.</div>;
  const sections: Array<{id:MapSection;name:string}>=[
    {id:"overview",name:"Áttekintés"},{id:"monitor",name:"Üzemeltetés"},{id:"mods",name:"Modok"},
    {id:"plugins",name:"Pluginok / ASA API"},{id:"ini",name:"INI"},
    {id:"backups",name:"Mentések"},{id:"logs",name:"Naplók"},
    {id:"rcon",name:"RCON / Konzol"}
  ];
  return <div className="map-detail-page">
    <button className="quiet back-to-servers" onClick={onBack}>
      <ArrowRight size={16}/> Vissza a szerverlistához
    </button>
    <div className="map-detail-hero">
      <MapCover name={map.name}/>
      <div className="premium-map-detail-copy">
        <span className="eyebrow">UNTAMED KINGDOM / HELYI MAP</span>
        <h1>{map.name}</h1>
        <p>{snapshot.local.hostName} · ARK: Survival Ascended</p>
      </div>
      <State running={map.running} status={map.status}/>
    </div>
    {journalReadError&&<div className="warning-inline" role="alert">
      <Shield size={17}/> A helyi ASA-műveleti naplók nem olvashatók.
      A Start, Stop és Restart gombok tiltva maradnak, amíg a naplók ellenőrzése nem sikerül.
    </div>}
    {restartJournal?.needsOperatorReview&&<div className="warning-inline" role="alert">
      <Shield size={17}/> A manuális ASA újraindítás megszakadt ({restartJournal.phase??"ismeretlen"}).
      Régi PID: {restartJournal.oldPid??"?"}; új PID: {restartJournal.newPid||"nem igazolt"}.
      A korábbi művelet nem folytatódik automatikusan; kézi Windows/RCON ellenőrzés szükséges.
    </div>}
    {lifecycle?.needsOperatorReview&&<div className="warning-inline" role="alert">
      <Shield size={17}/> Félbeszakadt helyi ASA-művelet ({lifecycle.kind??"ismeretlen"} / {lifecycle.phase??"ismeretlen"}).
      A korábbi művelet naplója megmaradt; automatikus Start, Stop és ismétlés helyett kézi ellenőrzés szükséges.
    </div>}
    <div className="map-detail-tabs" role="tablist" aria-label="Map funkciói">
      {sections.map(item=><button type="button" key={item.id} role="tab"
        aria-selected={section===item.id}
        className={section===item.id?"map-detail-tab selected":"map-detail-tab"}
        onClick={()=>setSection(item.id)}>{item.name}</button>)}

    </div>
    {section==="overview" && <>
      <div className="map-overview-grid">
        <Panel title="Helyi folyamatállapot">
          <div className="map-info-line"><span>Megfigyelt állapot</span><State running={map.running} status={map.status}/></div>
          <div className="map-info-line"><span>Gazdagép</span><strong>{snapshot.local.hostName}</strong></div>
          <div className="map-info-line"><span>Game UDP</span><strong>{map.gamePort || "Nem ismert"}</strong></div>
          <div className="map-info-line"><span>Steam build</span><strong>{map.steamBuildId || "Nem ellenőrizhető"}</strong></div>
          <div className="map-info-line"><span>Frissítési állapot</span><strong>{map.steamUpdateState}</strong></div>
        </Panel>
        <Panel title="Biztonságos karbantartási útvonal">
          <p className="muted small">A regisztrált helyi maphoz már elérhető a modkonfiguráció, az INI, a pluginleltár, a mentéstörténet és a titokszűrt napló.</p>
          <div className="map-overview-actions">
            <button className="primary" onClick={()=>setSection("mods")}><Layers3 size={17}/> Modok kezelése</button>
            <button className="secondary" onClick={()=>setSection("ini")}><FileText size={17}/> INI szerkesztése</button>
            <button className="secondary" onClick={()=>setSection("monitor")}><Activity size={17}/> PID / RAM / állapot</button>
          </div>
          <div className="warning-inline"><Shield size={17}/> Frissen telepített helyi map indítása jóváhagyással elérhető. A történeti Store-v1 indítás külön eredeti ACTIVE/DONE ellenőrzéssel és kétlépcsős natív jóváhagyással használható. A karakterutazás, SaveComplete és közös Store-restore igazolása továbbra is külön követelmény.</div>
        </Panel>
      </div>
      {map.storeProtected&&<HistoricalRconAttach mapId={mapId} onConfigured={onConfigured}/>}
      {map.storeProtected&&<HistoricalActiveStartPanel mapId={mapId} rconAttached={!!map.rconConfigured} onStarted={onConfigured}/>}
      {map.storeProtected&&<HistoricalGracefulStopPanel mapId={mapId} running={map.running} onStopped={onConfigured}/>}
      {map.gamePort>0 && !map.storeProtected && <FreshAsaStartPanel mapId={mapId} onStarted={onConfigured} journalBlocked={lifecycleBlocked}/>}
      {map.gamePort>0 && !map.storeProtected && <FreshAsaStopPanel mapId={mapId} running={map.running} onStopped={onConfigured} journalBlocked={lifecycleBlocked}/>}
      {map.gamePort>0 && !map.storeProtected && <FreshAsaRestartPanel mapId={mapId} running={map.running} onRestarted={onConfigured} journalBlocked={lifecycleBlocked}/>}
      {map.gamePort===0 && <InitialMapSetup mapId={mapId} onConfigured={async()=>{
        await onConfigured();
        setInitialSetupSaved(true);
      }}/>}
      {map.gamePort>0 && initialSetupSaved && <div role="status" className="success-inline">
        <CheckCircle2 size={17}/>{map.name} indítási profilja mentve. A szerver nem indult el; RCON és modok később konfigurálhatók.
      </div>}
    </>}
    {section==="monitor" && <OperationsCenter selectedMapId={mapId} onOpenMap={()=>setSection("overview")}/>}
    {section==="mods" && <Mods snapshot={snapshot} selected={mapId} setSelected={setSelected}/>} 
    {section==="plugins" && <PluginCenter snapshot={snapshot} selected={mapId} setSelected={setSelected}/>}
    {section==="ini" && <IniCenter snapshot={snapshot} selected={mapId} setSelected={setSelected}/>}
    {section==="backups" && <BackupCenter mapId={mapId} snapshot={snapshot}/>}
    {section==="logs" && <LogsCenter snapshot={snapshot} selected={mapId} setSelected={setSelected}/>}
    {section==="rcon" && (map.storeProtected
      ?<><HistoricalRconLivePanel mapId={mapId} running={map.running}/>
        <HistoricalAdminRconPanel mapId={mapId} running={map.running}/></>
      :<RconReadOnlyPanel mapId={mapId}/>)}
  </div>;
}

export function App() {
  const [page, setPage] = useState<Page>("dashboard");
  const [steamMap, setSteamMap] = useState("The Island");
  const [snapshot, setSnapshot] = useState<DashboardSnapshot | null>(null);
  const [error, setError] = useState<string>("");
  const [loading, setLoading] = useState(false);
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState("");
  const snapshotFlight = useRef<Promise<void> | null>(null);
  const refresh = useCallback(async (background = false) => {
    // Manual profile changes must not be lost if a background LAN poll is
    // already in progress; wait for that poll and read the newly saved config.
    if (snapshotFlight.current) {
      if (background) return;
      await snapshotFlight.current;
    }
    const task = (async () => {
      if (!background) setLoading(true);
      setError("");
      try {
        const response = await managerBridge.dashboard();
        setSnapshot(response);
        setSelected(old => response.local.maps.some(m => m.id === old)
          ? old : (response.local.maps[0]?.id ?? ""));
      } catch (e) {
        setError(e instanceof Error ? e.message : "UM-READ-UNAVAILABLE");
      } finally {
        if (!background) setLoading(false);
      }
    })();
    snapshotFlight.current = task;
    try { await task; } finally {
      if (snapshotFlight.current === task) snapshotFlight.current = null;
    }
  }, []);
  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => { void refresh(true); }, 15000);
    return () => window.clearInterval(timer);
  }, [refresh]);
  const title = useMemo(() => page==="map" ? "Map részletei" :
    tabs.find(t => t.page === page)?.label ?? "Áttekintés", [page]);
  const selectMap = (id: string) => { setSelected(id); setPage("map"); };
  const openSteamInstall = (mapName?:string) => {
    if(mapName)setSteamMap(mapName);
    setPage("steam");
  };

  return <div className="app-layout">
    {(window as typeof window & { __UNTAMED_TEST_FIXTURE__?: boolean }).__UNTAMED_TEST_FIXTURE__ &&
      <div className="synthetic-fixture-label">KIZÁRÓLAG SZINTETIKUS UI-TESZTADAT</div>}
    <aside className="sidebar">
      <div className="brand">
        <div className="brand-symbol"><img src="/UntamedManager.ico" alt="Untamed Kingdom ikon"/></div>
        <strong>UNTAMED</strong><span>MANAGER</span><small>OBSIDIAN COMMAND</small>
      </div>
      <div className="sidebar-label">KÖZPONT</div>
      <nav aria-label="Fő navigáció">
        {tabs.map(t => <button key={t.page} className={page === t.page ? "nav-link active" : "nav-link"} onClick={() => setPage(t.page)} aria-current={page === t.page ? "page" : undefined}>
          <t.icon size={21}/><span>{t.label}</span>{page === t.page && <ChevronRight size={16}/>}
        </button>)}
      </nav>
      <div className="sidebar-bottom"><div className="sidebar-shield"><Shield size={16}/><span>Biztonságos megfigyelés</span></div>
        <p>A történeti Store-v1 átvétel külön védett. A szervervezérlés és a karakterutazás eltérő biztonsági szint.</p></div>
    </aside>
    <div className="main-area">
      <header className="topbar"><div className="topbar-title"><span className="topbar-kicker">UNTAMED KINGDOM</span>{page==="dashboard"?<h1>{title}</h1>:<b>{title}</b>}</div>
        <div className="topbar-right">
          <span className="pill subtle"><Monitor size={15}/>{snapshot?.local.hostName || "Gazdagép betöltése"}</span>
          <span className="pill amber"><Shield size={15}/> HELYI / VÉDETT</span>
          <button className="icon-button" onClick={() => { void refresh(); }} disabled={loading} aria-label="Állapot frissítése"><RefreshCw size={19} className={loading ? "spin" : ""}/></button>
        </div>
      </header>
      <main className="main-content">
        {error && <div className="error-banner"><AlertTriangle size={22}/><div><b>Nem érhető el a natív Manager-adatkapcsolat</b><p>{error}. Ez a felület nem jelenít meg kitalált szerveradatokat.</p></div><button onClick={() => { void refresh(); }}>Újra</button></div>}
        {!snapshot && !error && <div className="loading"><RefreshCw className="spin"/> Valós helyi állapot betöltése...</div>}
        {snapshot && <>
          {page === "dashboard" && <Dashboard snapshot={snapshot} onPage={setPage} onSelect={selectMap} onInstall={openSteamInstall}/>}
          {page === "map" && <MapDetails snapshot={snapshot} mapId={selected} setSelected={setSelected} onBack={()=>setPage("servers")} onConfigured={refresh}/>}
          {page === "servers" && <Servers snapshot={snapshot} search={search} setSearch={setSearch} onSelect={selectMap} onImported={refresh}/>} 
          {page === "cluster" && <Cluster snapshot={snapshot}/>}
          {page === "mods" && <Mods snapshot={snapshot} selected={selected} setSelected={setSelected}/>}
          {page === "plugins" && <PluginCenter snapshot={snapshot} selected={selected} setSelected={setSelected}/>}
          {page === "backups" && <BackupCenter snapshot={snapshot}/>}
          {page === "operations" && <OperationsCenter onOpenMap={selectMap}/>}
          {page === "updates" && <UpdateCenterPage/>}
          {page === "travel" && <StoreTravelWorkbench/>}
          {page === "steam" && <SteamCenter snapshot={snapshot} onRegistered={refresh} initialMapName={steamMap}/>}
          {page === "logs" && <LogsCenter snapshot={snapshot} selected={selected} setSelected={setSelected}/>}
          {page === "settings" && <SettingsCenter/>}
          {page === "ini" && <IniCenter snapshot={snapshot} selected={selected} setSelected={setSelected}/>} 
        </>}
      </main>
      <footer className="footer"><span className="footer-dot"/> <b>Untamed Kingdom</b> · Natív .NET 10 / WebView2
        <span className="footer-spacer"/><span>Állapot: {snapshot ? observedAt(snapshot.observedUtc) : "nem elérhető"}</span>
        <span className="footer-separator"/><span>Migrációs kiadás · Nem produkciós</span>
      </footer>
    </div>
  </div>;
}
