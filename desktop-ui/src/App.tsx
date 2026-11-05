import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Activity, AlertTriangle, Archive, ArrowRight, Boxes, CheckCircle2, ChevronRight,
  Cpu, Database, FileText, HardDrive, LayoutDashboard, Layers3, Monitor,
  Package, Plug, RefreshCw, Search, Server, Settings2, Shield, WifiOff
} from "lucide-react";
import {
  managerBridge, type BackupOverview, type ConfiguredMods, type CurseForgeResult,
  type LogTail, type ModDraftState, type ModDraftSave, type ModDraftMode,
  type SteamInstallations, type SuggestedPorts,
  type PluginInventory, type DashboardSnapshot,
  type PublicMap, type RemoteHost
} from "./bridge";

type Page = "dashboard" | "servers" | "mods" | "cluster" | "plugins" | "backups" | "steam" | "logs" | "settings";
const tabs: Array<{ page: Page; label: string; icon: typeof LayoutDashboard }> = [
  { page: "dashboard", label: "Áttekintés", icon: LayoutDashboard },
  { page: "servers", label: "Szerverek", icon: Server },
  { page: "cluster", label: "Cluster", icon: Boxes },
  { page: "mods", label: "Modok", icon: Layers3 },
  { page: "plugins", label: "Pluginok", icon: Plug },
  { page: "backups", label: "Mentések", icon: Archive },
  { page: "steam", label: "SteamCMD", icon: HardDrive },
  { page: "logs", label: "Naplók", icon: FileText },
  { page: "settings", label: "Beállítások", icon: Settings2 },
];

function prettyMemory(kiB: number | null): string {
  return kiB === null ? "Nem mérhető" : `${(kiB / 1048576).toFixed(1)} GiB`;
}
function observedAt(raw: string): string {
  const d = new Date(raw);
  return Number.isNaN(d.getTime()) ? "Ismeretlen" : d.toLocaleTimeString("hu-HU");
}
function Stat({ label, value, note, icon: Icon }: {
  label: string; value: string | number; note: string; icon: typeof Server
}) {
  return <div className="stat glass">
    <span className="stat-icon"><Icon size={25}/></span>
    <div><div className="stat-label">{label}</div><strong className="stat-value">{value}</strong><div className="muted tiny">{note}</div></div>
  </div>;
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
  const rows = maps.filter(m => m.name.toLocaleLowerCase("hu-HU").includes(filter.toLocaleLowerCase("hu-HU")));
  if (!rows.length) return <div className="empty">Nincs megjeleníthető, regisztrált map.</div>;
  return <div className="map-table" role="table" aria-label="Szerverek">
    <div className="map-row map-heading" role="row"><span>MAP / SZERVER</span><span>GAZDAGÉP</span><span>ÁLLAPOT</span><span>PORT</span></div>
    {rows.map(map => <button className="map-row" role="row" key={map.id}
      onClick={onPick ? () => onPick(map.id) : undefined}
      disabled={!onPick} title={onPick ? "Helyi szerver részletei" : "Távoli megfigyelés"}>
      <span className="map-name"><span className="map-symbol"><Server size={18}/></span><span><b>{map.name}</b><small>ASA · {map.steamBuildId ? "build " + map.steamBuildId : "build ismeretlen"}</small></span></span>
      <span className="muted">{host}</span><State running={map.running} status={map.status}/>
      <span className="mono muted">{map.gamePort || "—"}</span>
    </button>)}
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
function CurseForgeExplorer() {
  const [term, setTerm] = useState("");
  const [result, setResult] = useState<CurseForgeResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState("");
  const [selected, setSelected] = useState<number | null>(null);
  const runSearch = async (page = 0) => {
    const query = term.trim();
    if (query.length < 2 || query.length > 80) {
      setNotice("Adj meg 2–80 karakteres keresőkifejezést.");
      return;
    }
    setBusy(true);
    setNotice("");
    try {
      const data = await managerBridge.catalogSearch(query, page);
      setResult(data);
      setSelected(data.mods[0]?.id ?? null);
    } catch (e) {
      const code = e instanceof Error ? e.message : "UM-READ-UNAVAILABLE";
      setNotice(code === "UM-CURSEFORGE-NO-KEY"
        ? "A helyi Managerben még nincs beállítva CurseForge API-kulcs."
        : "A CurseForge keresés nem sikerült (" + code + ").");
    } finally { setBusy(false); }
  };
  const chosen = result?.mods.find(m => m.id === selected);
  return <Panel title="Hivatalos CurseForge ASA katalógus" action={<span className="muted tiny">Csak metaadat · nincs telepítés</span>}>
    <form className="catalog-search" onSubmit={e => { e.preventDefault(); void runSearch(0); }}>
      <Search size={20} aria-hidden="true"/>
      <input aria-label="CurseForge mod keresése" placeholder="Modkeresés név vagy készítő alapján..." value={term}
        onChange={e => setTerm(e.target.value)} maxLength={80} minLength={2}/>
      <button className="primary" disabled={busy} type="submit">{busy ? "Keresés..." : "Keresés"}</button>
    </form>
    {notice && <div role="status" className="warning-inline"><AlertTriangle size={17}/>{notice}</div>}
    {result && <div className="catalog-layout">
      <div className="catalog-list">
        <p className="muted small">{result.totalCount} találat · {result.page+1}. oldal · hivatalos katalógusadat</p>
        {result.mods.map(mod => <button key={mod.id} className={selected===mod.id?"catalog-item selected":"catalog-item"} onClick={() => setSelected(mod.id)}>
          {mod.thumbnailUrl ? <img src={mod.thumbnailUrl} alt="" loading="lazy" referrerPolicy="no-referrer"/> : <div className="catalog-fallback"><Package size={22}/></div>}
          <span><b>{mod.name}</b><small>{mod.authors.join(", ") || "Ismeretlen szerző"} · ID {mod.id}</small></span>
        </button>)}
        <div className="catalog-pagination">
          <button disabled={busy || result.page===0} onClick={() => { void runSearch(result.page-1); }}>Előző</button>
          <span className="muted tiny">{result.page+1}. oldal</span>
          <button disabled={busy || !result.hasMore} onClick={() => { void runSearch(result.page+1); }}>Következő</button>
        </div>
      </div>
      <div className="catalog-detail">
        {chosen ? <>
          <h3>{chosen.name}</h3>
          <p className="muted small">{chosen.authors.join(", ") || "Szerző: ismeretlen"} · Projekt ID: {chosen.id}</p>
          <p>{chosen.summary || "Nincs katalógus-összefoglaló."}</p>
          <div className="details-line"><span>Legújabb fájl</span><b>{chosen.latestFile || "Nincs adat"}</b></div>
          <div className="details-line"><span>Letöltésszám</span><b>{chosen.downloadCount.toLocaleString("hu-HU")}</b></div>
          <div className="warning-inline"><Shield size={17}/> A katalógus nem igazolja a mod telepítését vagy ASA-betöltését.</div>
          {chosen.website && <span className="muted tiny">CurseForge-adatlap: {chosen.website}</span>}
        </> : <p className="muted">Nincs kiválasztott mod.</p>}
      </div>
    </div>}
  </Panel>;
}

function Dashboard({ snapshot, onPage, onSelect }: {
  snapshot: DashboardSnapshot;
  onPage: (page: Page) => void; onSelect: (id: string) => void
}) {
  const local = snapshot.local;
  const remote = snapshot.remote;
  const all = local.maps.length + (remote?.maps.length || 0);
  const running = [...local.maps, ...(remote?.maps || [])].filter(m => m.running).length;
  const ramUsed = prettyMemory(local.memoryUsedKiB);
  const ramTotal = prettyMemory(local.memoryTotalKiB);
  return <>
    <div className="hero">
      <div className="obelisks"><span/><span/><span/></div>
      <div className="hero-copy"><span className="eyebrow">UNTAMED KINGDOM / OBSIDIAN COMMAND</span>
        <h1>Cluster Command Center</h1>
        <p>Helyi szerverfelügyelet és hitelesített LAN-megfigyelés</p>
        <button className="primary hero-button" onClick={() => onPage("servers")}>Szerverek megtekintése <ArrowRight size={16}/></button>
      </div>
      <div className="hero-caption">ARK: SURVIVAL ASCENDED</div>
    </div>
    <div className="stats">
      <Stat label="REGISZTRÁLT MAPOK" value={all} note="Helyi és friss LAN" icon={Layers3}/>
      <Stat label="MEGFIGYELT FUTÓ MAPOK" value={running} note="Igazolt folyamatállapot" icon={Activity}/>
      <Stat label="HELYI CPU" value={local.cpuPercent === null ? "—" : local.cpuPercent.toFixed(0) + "%"} note="Windows-mérés" icon={Cpu}/>
      <Stat label="HELYI RAM" value={ramUsed} note={ramTotal === "Nem mérhető" ? "Nincs mérés" : `Összes: ${ramTotal}`} icon={HardDrive}/>
    </div>
    <div className="overview-grid">
      <Panel title={`Szerverek (${all})`} className="servers-panel" action={<button className="quiet" onClick={() => onPage("servers")}>Összes szerver <ChevronRight size={16}/></button>}>
        <MapTable maps={local.maps} host={local.hostName} onPick={onSelect}/>
        {remote && <><p className="subhead">TÁVOLI MAPOK · CSAK MEGFIGYELÉS</p><MapTable maps={remote.maps} host={remote.hostName}/></>}
      </Panel>
      <div className="overview-side">
        <Panel title="Gazdagépek">
          <HostCard host={local} local/>
          {remote ? <HostCard host={remote}/> : <div className="warning-inline"><WifiOff size={18}/><span>{snapshot.peerMessage || "Nincs friss LAN-peer adat"}</span></div>}
        </Panel>
        <Panel title="Biztonsági állapot">
          <div className="status-line"><Shield size={18}/><span>A szerverek itt csak megfigyelhetők</span></div>
          <div className="status-line"><Database size={18}/><span>Store és SQL: nincs írási jog a React bridge-ben</span></div>
          <div className="status-line"><CheckCircle2 size={18}/><span>Éles lifecycle biztonsági kapuk változatlanok</span></div>
        </Panel>
      </div>
    </div>
  </>;
}
function Servers({ snapshot, search, setSearch, onSelect }: {
  snapshot: DashboardSnapshot; search: string; setSearch: (v: string) => void; onSelect: (v: string) => void
}) {
  return <><div className="page-intro"><span className="eyebrow">LOCAL HOST / LAN</span><h1>Szerverek</h1><p>A helyi mapok megfigyelése; távoli mapok kizárólag olvashatók.</p></div>
    <div className="toolbar"><Search size={19}/><input aria-label="Map keresése" placeholder="Map keresése..." value={search} onChange={e => setSearch(e.target.value)}/></div>
    <Panel title={`Helyi mapok · ${snapshot.local.hostName}`}><MapTable maps={snapshot.local.maps} host={snapshot.local.hostName} filter={search} onPick={onSelect}/></Panel>
    {snapshot.remote && <Panel title={`Távoli mapok · ${snapshot.remote.hostName}`}><MapTable maps={snapshot.remote.maps} host={snapshot.remote.hostName} filter={search}/></Panel>}
  </>;
}
function ModDraftEditor({ mapId, onSaved }: {
  mapId: string; onSaved: (saved: ModDraftSave) => void
}) {
  const [draft, setDraft] = useState<ModDraftState | null>(null);
  const [modId, setModId] = useState("");
  const [mode, setMode] = useState<ModDraftMode>("active");
  const [position, setPosition] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  useEffect(() => { setDraft(null); setError(""); setNotice(""); setModId(""); }, [mapId]);
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
      setNotice("Mentve a helyi indítási konfigurációba. A futó ASA modbetöltése NEM igazolt; ellenőrzött újraindítás szükséges.");
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

function Mods({ snapshot, selected, setSelected }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (value: string) => void
}) {
  const [info, setInfo] = useState<ConfiguredMods | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    let active = true;
    setInfo(null); setError("");
    if (!selected) return;
    managerBridge.configuredMods(selected).then(data => { if(active) setInfo(data); })
      .catch(e => { if(active) setError(e instanceof Error ? e.message : "UM-READ-UNAVAILABLE"); });
    return () => { active = false; };
  }, [selected]);
  const entries = info ? [
    ...info.active.map((id, position) => ({ id, position, state: "Aktív", cls: "green" })),
    ...info.passive.map((id, position) => ({ id, position, state: "Passzív", cls: "amber" })),
    ...info.disabled.map((id, position) => ({ id, position, state: "Letiltva", cls: "grey" }))
  ] : [];
  return <><div className="page-intro"><span className="eyebrow">CURSEFORGE / HELYI KONFIGURÁCIÓ</span><h1>Mod Center</h1>
    <p>Modkiosztás a valódi indítási konfigurációból — a fájlok betöltöttségét nem állítjuk bizonyítottnak.</p></div>
    <div className="toolbar mod-toolbar"><Layers3 size={19}/><select aria-label="Helyi map" value={selected} onChange={e => setSelected(e.target.value)}>
      {snapshot.local.maps.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
    </select><span className="muted small">Az új UI-ban egyelőre csak megfigyelés</span></div>
    <div className="mods-grid">
      <Panel title={info ? `Modlista · ${info.mapName}` : "Modlista"}>
        {error ? <div className="error-inline"><AlertTriangle size={17}/>{error}</div> :
        !selected ? <div className="empty">Először adj hozzá helyi mapot a meglévő Managerben.</div> :
        !info ? <div className="empty">Konfiguráció betöltése...</div> :
        entries.length===0 ? <div className="empty">Ehhez a maphoz nincs konfigurált mod.</div> :
        <div className="mods-list">
          {entries.map((m,i) => <div className="mod-item" key={m.state + m.id}>
            <span className="mod-index">{i+1}</span><span className="mod-mark"><Package size={19}/></span>
            <div className="mod-title"><strong>Modprojekt · {m.id}</strong><small>CurseForge projektazonosító · {m.state === "Letiltva" ? "Nincs betöltési pozíció" : `Helyi sorszám ${m.position+1}`}</small></div>
            <span className={`mod-tag ${m.cls}`}>{m.state}</span>
          </div>)}
        </div>}
      </Panel>
      <Panel title="Modinformáció">
        <div className="mod-details-icon"><Layers3 size={31}/></div>
        <h3>Konfigurációs leltár</h3>
        <p className="muted">Az aktív, passzív és letiltott modok az eredeti C# ModStateController alapján jelennek meg.</p>
        <div className="detail-divider"/>
        <div className="details-line"><span>Forrás</span><b>Helyi mapkonfiguráció</b></div>
        <div className="details-line"><span>Valós ASA betöltés</span><b>Nem igazolt</b></div>
        <div className="details-line"><span>Függő változtatások</span><b>{info?.pending ? "Vannak" : "Nincsenek"}</b></div>
        <div className="warning-inline"><Shield size={17}/><span>Modmódosítás az új felületen még nem engedélyezett. A meglévő C# védelem változatlan.</span></div>
      </Panel>
    </div>
    <ModDraftEditor mapId={selected} onSaved={saved => {
      setInfo(current => current ? { ...current, pending: saved.pending,
        active: saved.active, passive: saved.passive, disabled: saved.disabled } : current);
    }}/>
    <CurseForgeExplorer/>
  </>;
}
function PluginCenter({ snapshot, selected, setSelected }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (v: string) => void
}) {
  const [info, setInfo] = useState<PluginInventory | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    let active = true;
    setInfo(null); setError("");
    if (!selected) return;
    managerBridge.pluginInventory(selected).then(data => { if (active) setInfo(data); })
      .catch(e => { if (active) setError(e instanceof Error ? e.message : "UM-READ-UNAVAILABLE"); });
    return () => { active = false; };
  }, [selected]);
  return <>
    <div className="page-intro"><span className="eyebrow">ASA API / LOCAL INSTALLATION</span><h1>Plugin Center</h1>
      <p>Helyi DLL-leltár a meglévő C# pluginolvasóval. Nem engedélyez vagy frissít pluginokat.</p></div>
    <div className="toolbar"><Plug size={19}/><select aria-label="Pluginok mapja" value={selected} onChange={e => setSelected(e.target.value)}>
      {snapshot.local.maps.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}</select>
    </div>
    <Panel title={info ? `Helyi pluginok · ${info.mapName}` : "Pluginok"}>
      {error && <div className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      {!error && !selected && <div className="empty">Nincs kiválasztható helyi map.</div>}
      {!error && selected && !info && <div className="empty">Helyi telepítés vizsgálata...</div>}
      {info?.plugins.length === 0 && <div className="empty">Nem található plugin a regisztrált map telepítési helyén.</div>}
      {info?.plugins.map((plugin,i) => <div className="plugin-row" key={plugin.name + i}>
        <span className="plugin-symbol"><Plug size={21}/></span>
        <div className="plugin-copy"><b>{plugin.name}</b><small>{plugin.binaries.map(b => b.name + " · " + (b.version || "verzió nélkül")).join(", ") || "Nincs DLL-adat"}</small></div>
        <span className={plugin.protectedData ? "mod-tag amber" : "mod-tag green"}>{plugin.protectedData ? "Védett" : "Helyi leltár"}</span>
      </div>)}
      <div className="warning-inline"><Shield size={17}/> Csak fájlleltár. Nem bizonyítja, hogy a futó ASA betöltötte a plugint.</div>
    </Panel>
  </>;
}
function BackupCenter() {
  const [info, setInfo] = useState<BackupOverview | null>(null);
  const [error, setError] = useState("");
  const reload = useCallback(async () => {
    try { setInfo(await managerBridge.backupOverview()); setError(""); }
    catch(e){ setError(e instanceof Error ? e.message : "UM-READ-UNAVAILABLE"); }
  }, []);
  useEffect(() => { void reload(); }, [reload]);
  return <>
    <div className="page-intro"><span className="eyebrow">BACKUP / FILE INTEGRITY</span><h1>Mentésközpont</h1>
      <p>Meglévő helyi csomagok integritásának ellenőrzött áttekintése, bármiféle restore vagy SQL-írás nélkül.</p></div>
    <Panel title="Helyi mentésleltár" action={<button className="quiet" onClick={() => { void reload(); }}><RefreshCw size={16}/> Frissítés</button>}>
      {error && <div className="error-inline">{error}</div>}
      {!info && !error && <div className="empty">Mentések ellenőrzése...</div>}
      {info?.maps.length === 0 && <div className="empty">Nincs regisztrált helyi map.</div>}
      {info?.maps.map(m => <div className="backup-row" key={m.mapId}>
        <span className="plugin-symbol"><Archive size={21}/></span>
        <div className="plugin-copy"><b>{m.mapName}</b>
          <small>Legutóbbi igazolt csomag: {m.latestVerifiedUtc ? new Date(m.latestVerifiedUtc).toLocaleString("hu-HU") : "Nincs adat"}</small></div>
        <div className="backup-stats"><strong>{m.validPackages} rendben</strong><span>{m.invalidPackages} hibás · {(m.verifiedBytes / 1073741824).toFixed(2)} GiB</span></div>
      </div>)}
      <div className="warning-inline"><Shield size={17}/> A fájlintegritás nem bizonyítja az ASA mentésének, a Store-nak vagy az SQL-nek az alkalmazásszintű konzisztenciáját.</div>
    </Panel>
  </>;
}
function SteamCenter({ snapshot }: { snapshot: DashboardSnapshot }) {
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
      <p>Valódi C#-os helyi telepítés-ellenőrzés és portjavaslat. A telepítés, frissítés és indítás a migrációs UI-ban még tiltott.</p></div>
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
        <span className="mod-tag amber">Frissítés tiltva</span>
      </div>)}
    </Panel>
    <Panel title="Új map · konfigurációs előkészítés">
      <p className="muted small">A hivatalos SteamCMD letöltést és a részletes szerverbeállításokat külön lépésekben fogjuk kezelni. Ez a panel nem indít SteamCMD-műveletet.</p>
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
  return <>
    <div className="page-intro"><span className="eyebrow">LOCAL ASA / SAFE LOG TAIL</span><h1>Naplóközpont</h1>
      <p>Legfeljebb 300, a meglévő C# által titokszűrt naplósor — csak regisztrált helyi mapról. Nem jelent ASA állapot- vagy mentési bizonyítékot.</p></div>
    <div className="log-toolbar">
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
    <Panel title={result ? `Naplórészlet · ${result.mapName}` : "Naplórészlet"}
      action={result && <span className="muted tiny">Módosítva: {observedAt(result.lastModifiedUtc)} · {result.truncated ? "Részlet (levágva)" : "Teljes olvasott rész"}</span>}>
      {error && <div role="status" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      {!selected && <div className="empty">Nincs kiválasztott helyi map.</div>}
      {selected && !result && !error && <div className="empty">Naplósorok betöltése...</div>}
      {result && <div className="log-list" role="log" aria-label="Titokszűrt helyi napló">
        {filtered.length===0 && <div className="empty">Nincs a szűrőnek megfelelő naplósor.</div>}
        {filtered.map(row => <div key={row.number} className={`log-row log-${row.level}`}>
          <span className="log-number">{row.number}</span><span className="log-level">{row.level}</span>
          <span className="log-message">{row.content}</span>
        </div>)}
      </div>}
      <div className="warning-inline"><Shield size={17}/> Titokszűrt, korlátozott méretű naplórészlet. A bejegyzés önmagában nem igazolja a SaveComplete vagy RCON művelet sikerét.</div>
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
    <Panel title="Manager integrációk">
      <div className="details-line"><span>Frontend</span><b>React / TypeScript · offline</b></div>
      <div className="details-line"><span>Natív Windows-felület</span><b>WebView2 / WinForms</b></div>
      <div className="details-line"><span>ASA írási műveletek</span><b>Nem engedélyezettek az új UI-n</b></div>
    </Panel>
  </>;
}

function Cluster({ snapshot }: { snapshot: DashboardSnapshot }) {
  const remote: RemoteHost | null = snapshot.remote;
  return <><div className="page-intro"><span className="eyebrow">LAN / OWNERSHIP</span><h1>Cluster</h1><p>Gazdagépenként saját Manager, közös státusznézet, távoli parancsok nélkül.</p></div>
    <div className="cluster-grid">
      <Panel title="Ez a gazdagép"><HostCard host={snapshot.local} local/><MapTable maps={snapshot.local.maps} host={snapshot.local.hostName}/></Panel>
      <Panel title="Távoli gazdagép">{remote ? <><HostCard host={remote}/><p className="muted small">Legutóbbi hitelesített mérés: {observedAt(remote.observedUtc)}</p><MapTable maps={remote.maps} host={remote.hostName}/></> : <div className="warning-inline"><WifiOff size={19}/>{snapshot.peerMessage || "Nincs friss távoli adat"}</div>}</Panel>
    </div>
  </>;
}
export function App() {
  const [page, setPage] = useState<Page>("dashboard");
  const [snapshot, setSnapshot] = useState<DashboardSnapshot | null>(null);
  const [error, setError] = useState<string>("");
  const [loading, setLoading] = useState(false);
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState("");
  const refresh = useCallback(async () => {
    setLoading(true); setError("");
    try {
      const response = await managerBridge.dashboard();
      setSnapshot(response);
      setSelected(old => response.local.maps.some(m => m.id === old) ? old : (response.local.maps[0]?.id ?? ""));
    } catch (e) {
      setError(e instanceof Error ? e.message : "UM-READ-UNAVAILABLE");
    } finally { setLoading(false); }
  }, []);
  useEffect(() => { void refresh(); }, [refresh]);
  const title = useMemo(() => tabs.find(t => t.page === page)?.label ?? "Áttekintés", [page]);
  const selectMap = (id: string) => { setSelected(id); setPage("mods"); };

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
        <p>Az éles Start/Stop/Store műveletek nem érhetők el az új UI-bridge-en.</p></div>
    </aside>
    <div className="main-area">
      <header className="topbar"><div className="topbar-title"><span className="topbar-kicker">UNTAMED KINGDOM</span><b>{title}</b></div>
        <div className="topbar-right">
          <span className="pill subtle"><Monitor size={15}/>{snapshot?.local.hostName || "Gazdagép betöltése"}</span>
          <span className="pill amber"><Shield size={15}/> ÉLŐ / CSAK OLVASÁS</span>
          <button className="icon-button" onClick={() => { void refresh(); }} disabled={loading} aria-label="Állapot frissítése"><RefreshCw size={19} className={loading ? "spin" : ""}/></button>
        </div>
      </header>
      <main className="main-content">
        {error && <div className="error-banner"><AlertTriangle size={22}/><div><b>Nem érhető el a natív Manager-adatkapcsolat</b><p>{error}. Ez a felület nem jelenít meg kitalált szerveradatokat.</p></div><button onClick={() => { void refresh(); }}>Újra</button></div>}
        {!snapshot && !error && <div className="loading"><RefreshCw className="spin"/> Valós helyi állapot betöltése...</div>}
        {snapshot && <>
          {page === "dashboard" && <Dashboard snapshot={snapshot} onPage={setPage} onSelect={selectMap}/>}
          {page === "servers" && <Servers snapshot={snapshot} search={search} setSearch={setSearch} onSelect={selectMap}/>}
          {page === "cluster" && <Cluster snapshot={snapshot}/>}
          {page === "mods" && <Mods snapshot={snapshot} selected={selected} setSelected={setSelected}/>}
          {page === "plugins" && <PluginCenter snapshot={snapshot} selected={selected} setSelected={setSelected}/>}
          {page === "backups" && <BackupCenter/>}
          {page === "steam" && <SteamCenter snapshot={snapshot}/>}
          {page === "logs" && <LogsCenter snapshot={snapshot} selected={selected} setSelected={setSelected}/>}
          {page === "settings" && <SettingsCenter/>} 
        </>}
      </main>
      <footer className="footer"><span className="footer-dot"/> <b>Untamed Kingdom</b> · Natív .NET 10 / WebView2
        <span className="footer-spacer"/><span>Állapot: {snapshot ? observedAt(snapshot.observedUtc) : "nem elérhető"}</span>
        <span className="footer-separator"/><span>Migrációs kiadás · Nem produkciós</span>
      </footer>
    </div>
  </div>;
}
