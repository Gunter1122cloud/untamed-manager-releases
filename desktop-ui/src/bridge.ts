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

export type ConfiguredMods = {
  mapId: string;
  mapName: string;
  pending: boolean;
  active: string[];
  passive: string[];
  disabled: string[];
  evidence: "CONFIGURED_ONLY";
};

type WireRequest = { id: string; method: "dashboard.snapshot" | "mods.configured"; mapId?: string };
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

function request<T>(method: WireRequest["method"], mapId?: string): Promise<T> {
  if (!webview) return Promise.reject(new Error("UM-NATIVE-UNAVAILABLE"));
  const id = crypto.randomUUID();
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: (value) => resolve(value as T), reject });
    try { webview.postMessage({ id, method, ...(mapId ? { mapId } : {}) }); }
    catch { pending.delete(id); reject(new Error("UM-NATIVE-UNAVAILABLE")); }
  });
}

export const managerBridge = {
  dashboard: () => request<DashboardSnapshot>("dashboard.snapshot"),
  configuredMods: (mapId: string) => request<ConfiguredMods>("mods.configured", mapId),
};
