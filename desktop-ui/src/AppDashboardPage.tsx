import { type DashboardSnapshot } from "./bridge";
import { PremiumDashboard } from "./PremiumDashboard";
import { type Page } from "./AppNavigation";

export function Dashboard({ snapshot, onPage, onSelect, onInstall }: {
  snapshot: DashboardSnapshot;
  onPage: (page: Page) => void; onSelect: (id: string) => void;
  onInstall: (mapName?:string) => void
}) {
  return <PremiumDashboard snapshot={snapshot} onSelect={onSelect}
    onServers={()=>onPage("servers")} onInstall={onInstall}
    onCluster={()=>onPage("cluster")}/>;
}
// Shared suggestions: unknown map names are still accepted by native import.
