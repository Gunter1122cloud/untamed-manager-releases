/* Strict, read-only Windows host contract. No mutation methods are exported. */
export type PublicMap = {
  id: string;
  name: string;
  running: boolean;
  status: string;
  gamePort: number;
  steamBuildId: string | null;
  steamUpdateState: string;
};

export type LocalHost = {
  hostName: string;
  hostId: string;
  maps: PublicMap[];
  cpuPercent: number | null;
  memoryUsedKiB: number | null;
  memoryTotalKiB: number | null;
};

export type RemoteHost = {
  hostName: string;
  hostId: string;
  observedUtc: string;
  maps: PublicMap[];
};

export type DashboardSnapshot = {
  mode: "LIVE_READ_ONLY";
  observedUtc: string;
  local: LocalHost;
  remote: RemoteHost | null;
  peerMessage: string;
};

export type CurseForgeResult = {
  gameId: number;
  totalCount: number;
  page: number;
  hasMore: boolean;
  evidence: "OFFICIAL_CATALOG_METADATA_ONLY";
  mods: Array<{
    id: number; name: string; summary: string; website: string | null;
    thumbnailUrl: string | null; authors: string[]; latestFile: string | null;
    modifiedUtc: string | null; downloadCount: number;
  }>;
};

export type PluginInventory = {
  mapId: string;
  mapName: string;
  evidence: "READ_ONLY_LOCAL_FILE_INVENTORY";
  plugins: Array<{ name: string; protectedData: boolean;
    binaries: Array<{ name: string; version: string; bytes: number }> }>;
};

export type BackupOverview = {
  evidence: "FILE_INTEGRITY_ONLY_NOT_ASA_SAVE_COMPLETE";
  maps: Array<{ mapId: string; mapName: string; validPackages: number;
    invalidPackages: number; verifiedBytes: number; latestVerifiedUtc: string | null }>;
};

export type ConfiguredMods = {
  mapId: string;
  mapName: string;
  pending: boolean;
  active: string[];
  passive: string[];
  disabled: string[];
  evidence: "CONFIGURED_ONLY";
};

type WireRequest = { id: string; method: "dashboard.snapshot" | "mods.configured" | "curseforge.search" | "plugins.inventory" | "backups.summary"; mapId?: string; term?: string; page?: number };
type WireResponse = { id: string | null; ok: boolean; data?: unknown; error?: string };
type NativeWebView = {
  postMessage: (message: WireRequest) => void;
  addEventListener: (name: "message", handler: (event: MessageEvent<WireResponse>) => void) => void;
};
declare global {
  interface Window {
    chrome?: { webview?: NativeWebView };
  }
}

const pending = new Map<string, { resolve(value: unknown): void; reject(reason: Error): void }>();
const webview = window.chrome?.webview;

webview?.addEventListener("message", (event) => {
  const msg = event.data;
  if (!msg || typeof msg.id !== "string") return;
  const waiting = pending.get(msg.id);
  if (!waiting) return;
  pending.delete(msg.id);
  if (msg.ok) waiting.resolve(msg.data);
  else waiting.reject(new Error(msg.error || "UM-UNKNOWN"));
});

function request<T>(method: WireRequest["method"], args: Partial<WireRequest> = {}): Promise<T> {
  if (!webview) return Promise.reject(new Error("UM-NATIVE-UNAVAILABLE"));
  const id = crypto.randomUUID();
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: (value) => resolve(value as T), reject });
    try { webview.postMessage({ id, method, ...args }); }
    catch { pending.delete(id); reject(new Error("UM-NATIVE-UNAVAILABLE")); }
  });
}

export const managerBridge = {
  dashboard: () => request<DashboardSnapshot>("dashboard.snapshot"),
  configuredMods: (mapId: string) => request<ConfiguredMods>("mods.configured", { mapId }),
  catalogSearch: (term: string, page = 0) => request<CurseForgeResult>("curseforge.search", { term, page }),
  pluginInventory: (mapId: string) => request<PluginInventory>("plugins.inventory", { mapId }),
  backupOverview: () => request<BackupOverview>("backups.summary"),
};
