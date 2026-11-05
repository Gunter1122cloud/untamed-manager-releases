import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Activity, AlertTriangle, ArrowRight, Boxes, CheckCircle2, ChevronRight,
  Cpu, Database, HardDrive, LayoutDashboard, Layers3, Monitor,
  Package, RefreshCw, Search, Server, Shield, WifiOff
} from "lucide-react";
import {
  managerBridge, type ConfiguredMods, type CurseForgeResult, type DashboardSnapshot,
  type PublicMap, type RemoteHost
} from "./bridge";

type Page = "dashboard" | "servers" | "mods" | "cluster";
const tabs: Array<{ page: Page; label: string; icon: typeof LayoutDashboard }> = [
  { page: "dashboard", label: "Áttekintés", icon: LayoutDashboard },
  { page: "servers", label: "Szerverek", icon: Server },
  { page: "cluster", label: "Cluster", icon: Boxes },
  { page: "mods", label: "Modok", icon: Layers3 },
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
    <CurseForgeExplorer/>
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
    <aside className="sidebar">
      <div className="brand">
        <div className="brand-symbol"><Activity size={31} strokeWidth={1.8}/></div>
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
        </>}
      </main>
      <footer className="footer"><span className="footer-dot"/> <b>Untamed Kingdom</b> · Natív .NET 10 / WebView2
        <span className="footer-spacer"/><span>Állapot: {snapshot ? observedAt(snapshot.observedUtc) : "nem elérhető"}</span>
        <span className="footer-separator"/><span>Migrációs kiadás · Nem produkciós</span>
      </footer>
    </div>
  </div>;
}
