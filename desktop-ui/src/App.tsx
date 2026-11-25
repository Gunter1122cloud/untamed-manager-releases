import { useCallback, useEffect, useMemo, useState } from "react";
import { createSnapshotRefreshQueue } from "./state/snapshotRefreshQueue";
import { describeNativeError } from "./state/nativeErrors";
import { IniCenter } from "./IniCenter";
import { AlertTriangle, Archive, Boxes, ChevronRight, FileText, HardDrive, LayoutDashboard, Layers3, Monitor, CloudDownload, CalendarClock, Plug, RefreshCw, Server, Settings2, Shield } from "lucide-react";
import { managerBridge, type DashboardSnapshot } from "./bridge";
import { UpdateCenterPage } from "./UpdateCenterPage";
import { OperationsCenter } from "./OperationsCenter";
import { type Page } from "./AppNavigation";
import { observedAt } from "./AppSharedMapViews";
import { Dashboard } from "./AppDashboardPage";
import { Servers } from "./AppServersPage";
import { Mods } from "./AppModsPage";
import { PluginCenter } from "./AppPluginPage";
import { BackupCenter } from "./AppBackupPage";
import { SteamCenter } from "./AppSteamPage";
import { LogsCenter } from "./AppLogsPage";
import { SettingsCenter } from "./AppSettingsPage";
import { Cluster } from "./AppClusterPage";
import { MapDetails } from "./AppMapDetailsPage";

const tabs: Array<{ page: Page; label: string; icon: typeof LayoutDashboard }> = [
  { page: "dashboard", label: "Áttekintés", icon: LayoutDashboard },
  { page: "servers", label: "Szerverek", icon: Server },
  { page: "cluster", label: "Cluster", icon: Boxes },
  { page: "mods", label: "Modok", icon: Layers3 },
  { page: "plugins", label: "Pluginok", icon: Plug },
  { page: "backups", label: "Mentések", icon: Archive },
  { page: "operations", label: "Üzemeltetés", icon: CalendarClock },
  { page: "steam", label: "Új map", icon: HardDrive },
  { page: "updates", label: "Frissítések", icon: CloudDownload },
  { page: "logs", label: "Naplók", icon: FileText },
  { page: "ini", label: "INI szerkesztő", icon: FileText },
  { page: "settings", label: "Beállítások", icon: Settings2 },
];

export function App() {
  const [page, setPage] = useState<Page>("dashboard");
  const [steamMap, setSteamMap] = useState("The Island");
  const [snapshot, setSnapshot] = useState<DashboardSnapshot | null>(null);
  const [error, setError] = useState<string>("");
  const [loading, setLoading] = useState(false);
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState("");
  // Confirmed operations and explicit refresh invalidate the open map’s read-only evidence.
  const [refreshRevision,setRefreshRevision]=useState(0);
  const refreshQueue = useMemo(() => createSnapshotRefreshQueue(async (background) => {
    if (!background) setLoading(true);
    try {
      const response = await managerBridge.dashboard();
      setSnapshot(response);
      setSelected(old => response.local.maps.some(m => m.id === old)
        ? old : (response.local.maps[0]?.id ?? ""));
      setError("");
    } catch (e) {
      setError(describeNativeError(e instanceof Error ? e.message : "UM-READ-UNAVAILABLE"));
    } finally {
      if (!background) {setLoading(false);setRefreshRevision(n=>n+1);}
    }
  }), []);
  const refresh = useCallback((background = false) => refreshQueue.request(background), [refreshQueue]);
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
        {tabs.map(t => <button key={t.page} type="button"
          className={page === t.page ? "nav-link active" : "nav-link"}
          aria-label={t.label} title={t.label}
          onClick={() => setPage(t.page)}
          aria-current={page === t.page ? "page" : undefined}>
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
          {page === "map" && <MapDetails snapshot={snapshot} mapId={selected} setSelected={setSelected} onBack={()=>setPage("servers")} onConfigured={refresh} onOpenCluster={()=>setPage("cluster")} refreshRevision={refreshRevision}/>}
          {page === "servers" && <Servers snapshot={snapshot} search={search} setSearch={setSearch} onSelect={selectMap} onImported={refresh} onCluster={()=>setPage("cluster")}/>} 
          {page === "cluster" && <Cluster snapshot={snapshot}/>}
          {page === "mods" && <Mods snapshot={snapshot} selected={selected} setSelected={setSelected}/>}
          {page === "plugins" && <PluginCenter snapshot={snapshot} selected={selected} setSelected={setSelected}/>}
          {page === "backups" && <BackupCenter snapshot={snapshot}/>}
          {page === "operations" && <OperationsCenter onOpenMap={selectMap}/>}
          {page === "updates" && <UpdateCenterPage/>}
          {page === "steam" && <SteamCenter snapshot={snapshot} onRegistered={refresh}
            initialMapName={steamMap} onOpenMap={selectMap}/>}
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
