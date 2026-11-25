import { useMemo } from "react";
import { Panel } from "./Panel";
import { WifiOff } from "lucide-react";
import { dashboardRemotes, type DashboardSnapshot } from "./bridge";
import { StoppedSteamBatchPanel } from "./StoppedSteamBatchPanel";
import { StoreTravelWorkbench } from "./StoreTravelWorkbench";
import { LanSettingsPanel } from "./LanSettingsPanel";
import { ClusterRegistryPanel } from "./ClusterRegistryPanel";
import { MultiHostBackupPanel } from "./MultiHostBackupPanel";
import { observedAt, MapTable, HostCard } from "./AppSharedMapViews";

export function Cluster({ snapshot }: { snapshot: DashboardSnapshot }) {
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
    <LanSettingsPanel/>
    <ClusterRegistryPanel protectedMapIds={protectedMapIds}/>
    <StoppedSteamBatchPanel maps={snapshot.local.maps}/>
    <MultiHostBackupPanel/>
    <StoreTravelWorkbench/>
  </>;
}
