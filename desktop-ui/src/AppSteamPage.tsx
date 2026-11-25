import { useCallback, useEffect, useState } from "react";
import { Panel } from "./Panel";
import { DownloadsOverview } from "./DownloadsOverview";
import { AlertTriangle, HardDrive, RefreshCw, Shield } from "lucide-react";
import { managerBridge, type SteamInstallations, type SuggestedPorts, type DashboardSnapshot } from "./bridge";
import { FreshAsaSteamWizard } from "./FreshAsaSteamWizard";

export function SteamCenter({ snapshot, onRegistered, initialMapName, onOpenMap }: {
  snapshot: DashboardSnapshot; onRegistered:()=>Promise<void>;
  initialMapName:string; onOpenMap:(mapId:string)=>void
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
    <div className="page-intro"><span className="eyebrow">VALVE STEAMCMD / INSTALLED BUILD</span><h1>Új ASA map telepítése</h1>
      <p>Valódi C#-os helyi telepítés és külön jóváhagyott SteamCMD-frissítés leállított mapokon. Az éles Store-v1 szervervezérlés ettől független.</p></div>
    <DownloadsOverview key={snapshot.local.hostId} maps={snapshot.local.maps}/>
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
    <FreshAsaSteamWizard onRegistered={onRegistered} initialMapName={initialMapName}
      onOpenMap={onOpenMap}/>
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
