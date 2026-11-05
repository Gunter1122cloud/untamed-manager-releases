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

export type LogTail = {
  mapId: string; mapName: string; lastModifiedUtc: string;
  totalBytes: number; truncated: boolean;
  evidence: "SANITIZED_LOCAL_LOG_TAIL_NOT_SERVER_HEALTH";
  lines: Array<{ number: number; level: "info" | "warning" | "error"; content: string }>;
};
export type SteamInstallations = {
  evidence: "READ_ONLY_STEAM_INSTALLATION_PREFLIGHT";
  installations: Array<{
    id: number; maps: string[]; executablePresent: boolean;
    installedSteamBuild: string | null; updateBlocked: boolean; reason: string;
  }>;
};
export type SuggestedPorts = {
  game: number; query: number; rcon: number;
  evidence: "PROFILE_ONLY_NO_NETWORK_PROBE";
};

export type CurseForgeCredentialStatus = { configured: boolean; verifiedNow?: boolean };

export type ModDraftMode = "active" | "passive" | "disabled" | "remove";
export type ModDraftState = {
  draftId: string; mapId: string; active: string[]; passive: string[];
  disabled: string[]; pending: boolean; dirty: boolean;
  canUndo: boolean; canRedo: boolean; evidence: "IN_MEMORY_MOD_DRAFT";
  differences: Array<{
    modId: string; beforeMode: string | null; beforePosition: number | null;
    afterMode: string | null; afterPosition: number | null;
  }>;
};
export type ModDraftSave = {
  saved: true; pending: boolean; active: string[]; passive: string[];
  disabled: string[]; evidence: "CONFIGURED_ONLY_PENDING_REAL_RESTART";
};

export type IniFile = "Game.ini" | "GameUserSettings.ini";
export type IniDocument = {
  token: string; mapId: string; file: IniFile; content: string;
  encoding: string; sha256: string; bytes: number;
  evidence: "STOPPED_LOCAL_INI_SNAPSHOT";
};
export type IniPreview = {
  previewToken: string; changed: boolean; oldLines: number; newLines: number;
  bytes: number; originalSha256: string; nextSha256: string;
  evidence: "HASHED_IN_MEMORY_PREVIEW";
};
export type IniSaveReceipt = {
  saved: true; backupCreated: boolean; sha256: string;
  evidence: "ATOMIC_LOCAL_INI_SAVE_ETAG_VERIFIED";
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

type WireRequest = { id: string; method: "dashboard.snapshot" | "mods.configured" | "curseforge.search" | "plugins.inventory" | "backups.summary" | "logs.read" | "steam.installations" | "servers.suggestPorts" |
  "curseforge.credentialStatus" | "curseforge.credentialSave" | "curseforge.credentialClear" |
  "mods.draftStart" | "mods.draftStage" | "mods.draftUndo" |
  "mods.draftRedo" | "mods.draftDiscard" | "mods.draftSave" |
  "ini.open" | "ini.preview" | "ini.save" | "ini.close";
  mapId?: string; term?: string; page?: number; credential?: string;
  draftId?: string; modId?: string; mode?: ModDraftMode; position?: number;
  token?: string; file?: IniFile; content?: string };
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
  logTail: (mapId: string) => request<LogTail>("logs.read", { mapId }),
  steamInstallations: () => request<SteamInstallations>("steam.installations"),
  suggestedPorts: () => request<SuggestedPorts>("servers.suggestPorts"),
  curseForgeKeyStatus: () => request<CurseForgeCredentialStatus>("curseforge.credentialStatus"),
  saveCurseForgeKey: (credential: string) => request<CurseForgeCredentialStatus>("curseforge.credentialSave", { credential }),
  clearCurseForgeKey: () => request<CurseForgeCredentialStatus>("curseforge.credentialClear"),
  startModDraft: (mapId: string) => request<ModDraftState>("mods.draftStart", { mapId }),
  stageModDraft: (draftId: string, modId: string, mode: ModDraftMode, position?: number) =>
    request<ModDraftState>("mods.draftStage", { draftId, modId, mode, ...(position===undefined?{}:{position}) }),
  undoModDraft: (draftId: string) => request<ModDraftState>("mods.draftUndo", { draftId }),
  redoModDraft: (draftId: string) => request<ModDraftState>("mods.draftRedo", { draftId }),
  discardModDraft: (draftId: string) => request<{discarded:true}>("mods.draftDiscard", { draftId }),
  saveModDraft: (draftId: string) => request<ModDraftSave>("mods.draftSave", { draftId }),
  iniOpen: (mapId: string, file: IniFile) => request<IniDocument>("ini.open", { mapId, file }),
  iniPreview: (token: string, content: string) => request<IniPreview>("ini.preview", { token, content }),
  iniSave: (token: string) => request<IniSaveReceipt>("ini.save", { token }),
  iniClose: (token: string) => request<{closed:true}>("ini.close", { token }),
};
