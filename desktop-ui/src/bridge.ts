/* Explicit allowlisted Windows host contract; only guarded native methods may mutate local Manager configuration. */
export type LanStatus = {
  originalStorePair:{
    bothOriginalProfilesPresent:boolean;
    sameSharedSession:boolean;sameSharedPath:boolean;distinctDeviceIds:boolean;
    bothHaveFiveActiveMaps:boolean;copiedMetadataPairMatches:boolean;
    liveWriterCertified:false;characterTravelVerified:false;statusCode:string;
  };
  hostName:string; listenPort:number; peerAddress:string; peerPort:number;
  pairingKeyConfigured:boolean; remoteFresh:boolean; remoteState:string;
  secretReturned:false; peerWritesAllowed:false; evidence:string;
};
export type LanPreview = {
  token:string;listenPort:number;peerAddress:string;peerPort:number;
  pairingKeyWillChange:boolean;requiresManagerRestart:true;remoteWritesAllowed:false;
  evidence:string;
};
export type LanSaved = {
  saved:true;listenPort:number;peerAddress:string;peerPort:number;
  requiresManagerRestart:true;secretReturned:false;remoteWritesAllowed:false;
  evidence:string;
};

export type OperationsMap = {
  mapId:string; mapName:string; running:boolean; confirmedStopped:boolean;
  status:string; pid:number|null; startedUtc:string|null; uptimeSeconds:number|null;
  memoryMiB:number|null; memoryVerified:boolean; maintenance:boolean;
  storeProtected:boolean; gamePort:number; queryPort:number; rconConfigured:boolean;
  rconPort:number|null; autoStartConfigured:boolean; lifecycleActionsVerified:false;
  evidence:string;
};
export type OperationsSchedule = {
  id:string; mapId:string; mapName:string; kind:string; frequency:string;
  enabled:boolean; nextUtc:string|null; state:string; autoExecutionPermitted:false;
};
export type OperationsSnapshot = {
  hostName:string; observedUtc:string; maps:OperationsMap[];
  schedules:OperationsSchedule[]; schedulingIsReadOnly:true;
  remoteCommandsAllowed:false; evidence:string;
};

export type PublicMap = {
  id: string;
  name: string;
  running: boolean;
  status: string;
  gamePort: number;
  steamBuildId: string | null;
  steamUpdateState: string;
  storeProtected?: boolean;  // only populated for locally owned maps
  maintenance?: boolean;     // local configuration, not runtime health
  rconConfigured?: boolean;  // credentials set, not connection verification
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

export type CurseForgeMod = {
  id: number; name: string; summary: string; website: string | null;
  thumbnailUrl: string | null; authors: string[]; latestFile: string | null;
  modifiedUtc: string | null; downloadCount: number; categories: string[];
  screenshots: string[]; isAvailable: boolean | null;
};
export type CurseForgeCategory = {
  id: number; name: string; isClass: boolean; parentCategoryId: number;
};
export type CurseForgeCategories = {
  categories: CurseForgeCategory[]; evidence: "OFFICIAL_ASA_CATEGORY_METADATA";
};
export type CurseForgeFeatured = {
  gameId: number; featured: CurseForgeMod[]; popular: CurseForgeMod[];
  recentlyUpdated: CurseForgeMod[]; evidence: "OFFICIAL_ASA_FEATURED_METADATA";
};
export type CurseForgeFile = {
  fileId: number; name: string; published: string | null; available: boolean;
  sizeBytes: number; gameVersions: string[];
  dependencies: Array<{ modId: number; type: string }>; distributionState: string;
};
export type CurseForgeSummary = {mod:CurseForgeMod; evidence:"OFFICIAL_ASA_PROJECT_SUMMARY_ONLY"};
export type CurseForgeProject = {
  mod: CurseForgeMod; description: string; files: CurseForgeFile[];
  filesTotal: number; filesHasMore: boolean;
  evidence: "OFFICIAL_ASA_PROJECT_METADATA_ONLY";
};
export type CurseForgeFilePage = {
  modId: number; page: number; hasMore: boolean; totalCount: number;
  files: CurseForgeFile[]; evidence: "OFFICIAL_ASA_FILE_METADATA_ONLY";
};
export type CurseForgeBrowseOptions = {
  term: string; categoryId?: number; sort?: number; descending?: boolean; page?: number;
};
export type CurseForgeResult = {
  gameId: number;
  totalCount: number;
  page: number;
  hasMore: boolean;
  evidence: "OFFICIAL_CATALOG_METADATA_ONLY";
  mods: CurseForgeMod[];
};

export type PluginState = {
  mapId:string; mapName:string;historicalProtected:boolean;
  plugins:Array<{name:string;enabled:boolean;runtimeLoadedVerified:false}>;
  evidence:"LOCAL_PLUGIN_DISK_STATE_ONLY";
};
export type PluginTogglePreview = {
  allowed:boolean;token:string|null;plugin:string;targetEnabled:boolean;
  checkedLocalMaps:number;reason:string;requiresRestart:true;
  productionRuntimeVerified:false;
  evidence:"LOCAL_PLUGIN_TOGGLE_PREFLIGHT_NO_WRITES";
};
export type PluginToggleReceipt = {
  mapId:string;plugin:string;enabled:boolean;backupCreated:true;
  requiresRestart:boolean;runtimeLoadedVerified:false;
  evidence:"VERIFIED_LOCAL_PLUGIN_DIRECTORY_TOGGLE";
};

export type PluginInventory = {
  mapId: string;
  mapName: string;
  evidence: "READ_ONLY_LOCAL_FILE_INVENTORY";
  plugins: Array<{ name: string; protectedData: boolean;
    binaries: Array<{ name: string; version: string; bytes: number }> }>;
};

export type TravelMapProof = {
  mapId:string;mapName:string;confirmedStopped:boolean;
  saveDirectoryFound:boolean;ambiguousLayout:boolean;
  profileCount:number;tribeCount:number;zeroByteDetected:boolean;unstable:boolean;
  profileFiles:Array<{id:string;bytes:number}>;
};
export type TravelCapture = {
  snapshot:string;hostId:string;maps:TravelMapProof[];
  complete:boolean;writerCertified:false;readOnly:true;
  evidence:"LOCAL_QUIESCENT_FILE_HASH_SNAPSHOT_ONLY";
};
export type TravelComparison = {
  passed:boolean;maps:10;hosts:2;
  beforeOnSource:boolean;afterOnTarget:boolean;uniqueDestination:boolean;
  zeroByteFiles:number;findings:string[];
  liveStoreWriterCertified:false;asaSaveCompleteCertified:false;
  productionUnlockAllowed:false;
  evidence:"FOUR_QUIESCENT_HOST_FILE_HASH_SNAPSHOTS_ONLY";
};

export type PluginZipPreview = {
  token:string;mapId:string;mapName:string;archiveName:string;plugin:string;
  binaryCount:number;skippedFiles:number;previousExists:boolean;
  evidence:"NATIVE_PLUGIN_ZIP_PREFLIGHT_NO_WRITE";
};
export type PluginZipInstalled = {
  mapId:string;plugin:string;installedBinaries:number;backupCreated:boolean;
  requiresRestart:true;runtimeLoadedVerified:false;
  evidence:"VERIFIED_GUARDED_PLUGIN_ZIP_INSTALL";
};
export type DurableSteamQueue = {
  exists:boolean;queueId?:string;phase?:string;activeMapName?:string|null;
  nextIndex?:number;total?:number;changedUtc?:string;summary?:string;
  needsOperatorReview:boolean;autoResumeAllowed:false;hostName?:string;
  evidence?:"DURABLE_MACHINE_LOCAL_STEAM_QUEUE_JOURNAL";
};
export type AsaLifecycleJournal = {
  mapId:string;mapName:string;journalExists:boolean;
  phase?:string|null;kind?:string|null;changedUtc?:string|null;
  needsOperatorReview:boolean;autoResumeAllowed:false;
  storeTouched:false;evidence:"DURABLE_INSTALLATION_OPERATION_JOURNAL_READ_ONLY";
};
export type AsaRestartJournalStatus = {
  exists:boolean;belongsToMap:boolean;phase?:string|null;
  oldPid?:number|null;newPid?:number|null;changedUtc?:string|null;
  needsOperatorReview:boolean;autoResumeAllowed:false;
  evidence:"DURABLE_MACHINE_LOCAL_MANUAL_ASA_RESTART";
};
export type FreshAsaRestartPreview = {
  token:string;mapId:string;mapName:string;currentPid:number;
  saveWorldBeforeExit:true;verifiesOldProcessExit:true;
  autoRetry:false;killPerformed:false;storeTouched:false;
  evidence:"REAL_LOCAL_NONSTORE_RESTART_PREVIEW_NO_WRITES";
};
export type FreshAsaRestartStarted = {
  jobId:string;started:true;autoRetry:false;killPerformed:false;storeTouched:false;
  evidence:"REAL_LOCAL_ASA_RESTART_JOB_QUEUED";
};
export type FreshAsaRestartStatus = {
  jobId:string;state:"queued"|"running"|"completed"|"needsOperator";
  stage:string;oldPid:number;newPid:number;changedUtc:string;
  saveCompleteProven:false;readinessCertified:false;
  killPerformed:false;storeTouched:false;autoRetry:false;
};
export type FreshAsaReadiness = {
  mapId:string;mapName:string;
  stage:"Unconfigured"|"ExecutableVerified"|"ProcessVerified"|"RconAuthenticated";
  observedUtc:string;loaderObserved:boolean;apiInitialized:boolean;
  joinAdvertised:boolean;rconAuthenticated:boolean;productionReady:boolean;
  description:string;storeTouched:false;
  evidence:"READ_ONLY_LOCAL_ASA_PROCESS_AND_RCON_READINESS";
};
export type FreshAsaStopPreview = {
  token:string;mapId:string;mapName:string;pid:number;
  usesSaveWorld:true;usesDoExit:true;killPermitted:false;
  autoRestart:false;evidence:"REAL_LOCAL_GRACEFUL_STOP_PREVIEW_NO_DISPATCH";
};
export type FreshAsaStopQueued = {
  jobId:string;started:true;killPerformed:false;autoRestart:false;
  evidence:"REAL_LOCAL_GRACEFUL_STOP_JOB_QUEUED";
};
export type FreshAsaStopStatus = {
  jobId:string;state:"queued"|"running"|"completed"|"needsOperator";
  step:string;changedUtc:string;confirmedStopped:boolean;
  saveCompleteProven:false;killPerformed:false;restartPerformed:false;
  storeTouched:false;
};
export type FreshAsaStartPreview = {
  token:string;mapId:string;mapName:string;steamBuild:string;
  gamePort:number;queryPort:number;rconConfigured:boolean;
  confirmedStopped:true;serverStarted:false;
  evidence:"FRESH_LOCAL_ASA_START_PREVIEW_NO_WRITE";
};
export type FreshAsaStartReceipt = {
  mapId:string;mapName:string;pid:number;createdUtc:string;
  startRequested:true;readyCertified:false;requiresRconReadiness:true;
  storeTouched:false;evidence:"LOCAL_REAL_ASA_PROCESS_STARTED_NOT_READY";
};
export type FreshAsaStartStatus = {
  mapId:string;mapName:string;running:boolean;confirmedStopped:boolean;
  pid?:number|null;readyCertified:false;status:string;
  evidence:"LOCAL_OS_PROCESS_OBSERVATION_NOT_ASA_READINESS";
};
export type StoppedSteamPreview = {
  token:string;mapId:string;mapName:string;maps:string[];
  installedBuild:string;reserveBytes:number;availableBytes:number;
  evidence:"REAL_LOCAL_STOPPED_STEAM_PREFLIGHT_NO_WRITE";
};
export type StoppedSteamStarted = {
  jobId:string;started:true;restartPerformed:false;remoteHostTouched:false;
  evidence:"REAL_LOCAL_STOPPED_STEAM_UPDATE_STARTED";
};
export type StoppedSteamStatus = {
  jobId:string;state:"queued"|"running"|"completed"|"needsOperator";
  stage:string;changedUtc:string;oldBuild:string;newBuild:string;
  protectedFilesIntact:boolean;restartPerformed:false;storeTouched:false;
};
export type RconConfigStatus = {
  mapId:string;mapName:string;configured:boolean;permitted:boolean;
  evidence:"LOCAL_RCON_CONFIGURATION_STATUS_NO_WRITE";
};
export type RconConfigPreview = {
  token:string;mapId:string;mapName:string;rconPort:number;
  createsNewIni:boolean;existingIniVerified:boolean;
  evidence:"STOPPED_LOCAL_RCON_PREVIEW_NO_WRITES";
};
export type RconConfigReceipt = {
  mapId:string;mapName:string;configured:true;rconPort:number;
  createsNewIni:boolean;serverStarted:false;
  evidence:"STOPPED_LOCAL_RCON_CONFIG_COMMITTED";
};
export type RconStatus = {
  mapId:string;mapName:string;configured:boolean;processVerified:boolean;
  permitted:boolean;maintenance:boolean;allowedCommands:["ListPlayers","GetGameLog","GetChat","SaveWorld"];
  evidence:"LOCAL_RCON_READ_ONLY_PREFLIGHT_NO_COMMAND";
};
export type RconAdminPreview = {
  token:string;mapId:string;mapName:string;action:string;command:string;
  requiresApproval:true;dangerous:boolean;approvalWord:string|null;
  expiresUtc:string;dispatched:false;credentialExposed:false;
  evidence:"LOCAL_OS_VERIFIED_ADMIN_RCON_PREVIEW";
};
export type RconAdminResult = {
  mapId:string;mapName:string;action:string;command:string;response:string;
  commandSent:true;observedUtc:string;oneShot:true;saveCompleteProven:false;
  storeWriterTouched:false;evidence:"LIVE_LOCAL_ADMIN_RCON_SINGLE_SEND";
};
export type RconWorldSave = {
  mapId:string;mapName:string;response:string;observedUtc:string;
  rconAcknowledged:false;rconCommandDispatched:true;
  diskSaveCompletionProven:false;
  worldFilesTouchedByManager:false;
  evidence:"REAL_FIXED_RCON_SAVEWORLD_ACK_NOT_DISK_COMPLETION";
};
export type RconReadLog = {
  mapId:string;mapName:string;command:"GetGameLog"|"GetChat";
  response:string;observedUtc:string;readOnly:true;
  saveCompleteProven:false;evidence:"LOCAL_VERIFIED_READ_ONLY_RCON";
};
export type RconPlayers = {
  mapId:string;mapName:string;response:string;observedUtc:string;
  readOnly:true;saveCompleteProven:false;
  evidence:"LOCAL_VERIFIED_READ_ONLY_LISTPLAYERS";
};

export type UpdatePrepared = {
  token:string;version:string;tag:string;assetName:string;
  sha256:string;fileBytes:number;verified:true;
  requiresFinalApproval:true;updaterPackaged:true;
  automaticInstallationStarted:false;
  evidence:"OBSIDIAN_INSTALLER_SHA256_STAGED_ONLY";
};
export type UpdateInstallReceipt = {
  externalUpdaterStarted:true;needsManagerExit:true;
  processId:number;tag:string;serverProcessesTouched:false;
  evidence:"EXTERNAL_ROLLBACK_UPDATER_HANDOFF";
};

export type UpdateStatus = {
  repository:string; runningVersion:string; installedTag:string;
  provider:"GITHUB_RELEASES";updateCheckPerformed:false;
  installationPermitted:false; updaterVerified:false;
  evidence:"LOCAL_RELEASE_IDENTITY_NO_NETWORK";
};
export type UpdateCheck = {
  repository:string;runningVersion:string;installedTag:string;
  channel:"stable"|"preview";found:boolean;newer:boolean;
  tag:string|null;version:string|null;assetName:string|null;
  sha256:string|null;releaseNotes:string|null;
  installationPermitted:false;updaterVerified:false;
  evidence:"GITHUB_RELEASE_METADATA_NOT_AN_INSTALLER";
};

export type WorldBackupPoint={
  id:string;createdUtc:string;kind:"manual"|"protective";
  files:number;bytes:number;characters:number;tribes:number;verified:true;
};
export type WorldBackupHistory={
  mapId:string;mapName:string;points:WorldBackupPoint[];
  recoveryRequired?:boolean;
  source:"LOCAL_SAVEDARKS_MAP_FOLDER";storeWriterTouched:false;
  evidence:"REAL_SHA256_BACKUP_PACKAGES";
};
export type WorldBackupPreview={
  token:string;mapId:string;mapName:string;mapKey:string;point:string|null;
  confirmedStopped:true;willCreateProtectiveBackup:boolean;
  originalWorldRetained:boolean;storeWriterTouched:false;
  action:"BACKUP"|"RESTORE";
  evidence:"NATIVE_STOPPED_WORLD_BACKUP_PREFLIGHT";
};
export type WorldBackupCreated={
  mapId:string;mapName:string;point:string;createdUtc:string;
  files:number;bytes:number;characters:number;tribes:number;
  backupVerified:true;asaSaveCompleteProven:false;storeWriterTouched:false;
  evidence:"REAL_VERIFIED_STOPPED_MAP_WORLD_BACKUP";
};
export type WorldBackupRestored={
  mapId:string;mapName:string;point:string;protectiveBackup:string;
  restored:true;oldWorldRetained:true;worldRestoreVerified:true;
  storeWriterTouched:false;
  evidence:"VERIFIED_STOPPED_MAP_KOVA_STYLE_RESTORE_WITH_PROTECTIVE_COPY";
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

export type HistoricalLaunchEvidence = {
  mapStatus: "MENTIONED"|"UNKNOWN";
  gamePortStatus: "MATCH"|"MISMATCH"|"UNKNOWN"|"CONFLICT";
  queryPortStatus: "MATCH"|"MISMATCH"|"UNKNOWN"|"CONFLICT";
  modOrderStatus: "MATCH"|"MISMATCH"|"UNKNOWN"|"CONFLICT"|"LEGACY_SYNTAX";
  passiveModOrderStatus:"MATCH"|"MISMATCH"|"UNKNOWN"|"CONFLICT";
  clusterIdPresent:boolean; clusterIdStatus:"MATCH"|"MISMATCH"|"UNKNOWN"|"CONFLICT"; clusterPathPresent:boolean; clusterPathVerified:false;
  useStorePresent:boolean; storeFlagCount:number; conversionFlagPresent:boolean;
  canonicalSourceFlagsObserved:boolean;
  findings:string[]; sourceAuthenticated:false;
  fullLaunchArgumentsVerified:false; peerWriterVerified:false;
  productionUseAllowed:false; serverStartAllowed:false;
  evidence:"OPERATOR_TEXT_READ_ONLY_UNAUTHENTICATED";
};
export type HistoricalLocalStoreParity = {
  archiveVerified:boolean;localStoreActive:boolean;allFourExactlyMatch:boolean;
  liveTwoHostVerified:false;characterTravelVerified:false;productionUseAllowed:false;
  files:Array<{kind:string;exactlySame:boolean}>;
  evidence:"ORIGINAL_LOCAL_FOUR_FILE_BYTE_PARITY_OFFLINE_NO_LIVE_WRITER";
};

export type HistoricalLocalStoreStatus = {
  found:true;localFilesVerified:boolean;configHashVerified:boolean;
  localLockVerified:boolean;allLocalMapsActive:boolean;
  localSnapshotReady:boolean;liveTwoHostVerified:false;
  characterTravelVerified:false;productionUseAllowed:false;
  maps:Array<{mapKey:string;active:boolean;sessionMatches:boolean;
    rootMatches:boolean;selected:boolean;backupIdentityPresent:boolean}>;
  evidence:string;
};

export type HistoricalPairEvidence = {
  files:number; maps:number; peers:number;
  sessionConsistent:boolean; configHashesVerified:boolean;
  activeStoreRecords:boolean; completeTwoHostEvidence:boolean;
  historicalConsistent:boolean; historicalActiveProtocolShape:boolean;
  historicalStartBlockers:string[]; findings:string[];
  hosts:Array<{number:number;maps:number;localEvidence:boolean;
    sessionMatches:boolean;configHashMatches:boolean;
    lockMatches:boolean;activeMaps:boolean}>;
  productionUseAllowed:false;
  evidence:"PAIRED_COPIES_OFFLINE_READ_ONLY_NO_WRITER_CERTIFICATION";
};

export type HistoricalStoreList = {
  maps: Array<{mapId:string;mapKey:string;gamePort:number;queryPort:number;
    enabledMods:number;passiveMods:number}>;
  sourceVerified:boolean;completeLaunchArguments:false;
  liveStoreVerified:false;allowServerStart:false;
  evidence:"HISTORICAL_LOCAL_ARCHIVE_READ_ONLY";
};
export type HistoricalStorePreview = {
  token:string;mapId:string;mapKey:string;name:string;
  gamePort:number;queryPort:number;enabledMods:number;passiveMods:number;
  folderName:string;historicalActive:true;originalMapIdPreserved:true;
  fullLaunchArgumentsVerified:false;peerWriterVerified:false;
  serverStartAllowed:false;
  evidence:"HISTORICAL_STORE_V1_QUARANTINE_PREVIEW";
};
export type HistoricalStoreRegistration = {
  mapId:string;mapName:string;registered:true;storeProtected:true;
  requiresOriginalLaunchVerification:true;serverStarted:false;
  worldFilesChanged:false;
  evidence:"HISTORICAL_STORE_V1_REGISTERED_QUARANTINED";
};

export type InitialMapStatus = {
  mapId:string; mapName:string; level:string | null;
  initialSetupAvailable:boolean; confirmedStopped:boolean;
  hasServerExecutable:boolean; gamePort:number; queryPort:number;
  suggestion:{game:number;query:number};
  evidence:"LOCAL_MANAGER_PROFILE_FIRST_SETUP_STATUS";
};
export type InitialMapPreview = {
  token:string; mapId:string; mapName:string; sessionName:string;
  mapLevel:string; gamePort:number; queryPort:number;
  rconEnabled:false; serverStartRequested:false; configuredArgumentsLength:number;
  evidence:"INITIAL_MAP_PROFILE_PREVIEW_NO_WRITES";
};
export type InitialMapReceipt = {
  mapId:string; mapName:string; configured:true;
  gamePort:number;queryPort:number;rconEnabled:false;serverStarted:false;
  evidence:"ATOMIC_MANAGER_PROFILE_INITIAL_SETUP_ONLY";
};

export type FreshInstallPreflight = {
  token:string; mapName:string; mapLevel:string; folderName:string;
  steamCmdPresent:boolean; usingManagerDefaultSteamCmd:boolean; needsOfficialSteamCmdDownload:boolean;
  existingInterruptedInstall:boolean; minimumFreeGiB:number;
  evidence:"FRESH_INSTALL_PREFLIGHT_NO_FILE_MUTATION";
};
export type FreshInstallJob = {
  jobId:string; mapName:string; stage:string; detail:string;
  percent:number | null; verified:boolean; registered:boolean;
  errorCode:string | null; evidence:"NATIVE_STEAMCMD_JOB_STATE";
};
export type FreshInstallReceipt = {
  mapId:string; mapName:string; needsConfiguration:true;
  serverStarted:false; evidence:"VERIFIED_STEAM_FILES_REGISTERED_MANAGER_PROFILE_ONLY";
};

export type LocalImportPreview = {
  importToken:string; name:string; folderName:string;
  exeFound:true; apiLoaderDetected:boolean; portsConfigured:false;
  evidence:"LOCAL_BINARY_LAYOUT_PROBED_NO_MUTATION";
};
export type LocalImportReceipt = {
  mapId:string; mapName:string; exeRegistered:true;
  needsConfiguration:true; evidence:"MANAGER_PROFILE_ONLY_NO_ASA_CHANGE";
};

export type SharedModInventory = {
  mapCount:number; configuredMapCount:number; sharedProjectCount:number;
  unreadableMaps:string[];
  projects:Array<{
    projectId:string; enabledMapCount:number; shared:boolean;
    assignments:Array<{
      mapId:string; mapName:string; mode:"active"|"passive"|"disabled"|"unknown";
      position:number; historicalProtected:boolean;
    }>;
  }>;
  evidence:"LOCAL_CONFIG_AND_QUARANTINED_ARCHIVE_METADATA_ONLY";
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

type WireRequest = { id: string; method: "lan.status" | "lan.preview" | "lan.apply" | "operations.snapshot" | "operations.map" | "dashboard.snapshot" | "mods.configured" | "mods.sharedInventory" | "curseforge.search" | "plugins.inventory" | "plugins.state" | "plugins.togglePreview" | "plugins.toggleApply" | "backups.summary" | "logs.read" | "steam.installations" | "servers.suggestPorts" |
  "curseforge.credentialStatus" | "curseforge.credentialSave" | "curseforge.credentialClear" |
  "mods.draftStart" | "mods.draftStage" | "mods.draftUndo" |
  "mods.draftRedo" | "mods.draftDiscard" | "mods.draftSave" |
  "ini.open" | "ini.preview" | "ini.save" | "ini.close" |
  "servers.inspectLocal" | "servers.importLocal" |
  "curseforge.categories" | "curseforge.featured" | "curseforge.browse" |
  "curseforge.project" | "curseforge.summary" | "curseforge.files" |
  "steam.freshPreflight" | "steam.freshStart" | "steam.freshStatus" |
  "steam.freshCurrent" | "steam.freshCancel" | "steam.freshDiscard" |
  "steam.freshRegister" | "steam.freshBrowse" | "servers.freshStartPreview" | "servers.freshStartApply" | "servers.freshStartStatus" | "servers.gracefulStopPreview" | "servers.gracefulStopApply" | "servers.gracefulStopStatus" | "servers.restartPreview" | "servers.restartApply" | "servers.restartStatus" | "servers.restartCurrent" | "servers.freshReadiness" | "servers.lifecycleJournal" | "plugins.zipSelect" | "plugins.zipApply" | "steam.stoppedPreview" | "steam.stoppedApply" | "steam.stoppedStatus" | "steam.stoppedCurrent" |
  "servers.initialStatus" | "servers.initialPreview" |
  "servers.initialApply" | "historical.localStatus" | "historical.localParity" | "historical.list" |
  "historical.preview" | "historical.register" |
  "historical.pairedEvidence" | "historical.launchEvidence" |
  "updates.status" | "updates.check" | "updates.prepare" | "updates.install" | "rcon.configStatus" | "rcon.configPreview" | "rcon.configApply" | "rcon.status" | "rcon.listPlayers" | "rcon.gameLog" | "rcon.chatLog" | "rcon.saveWorld" | "rcon.adminPreview" | "rcon.adminSend" |
  "backup.history" | "backup.preview" | "backup.create" |
  "backup.restorePreview" | "backup.restore" |
  "travel.capture" | "travel.compare";
  listenPort?:number; peerAddress?:string; peerPort?:number; pairingKey?:string;
  mapId?: string; term?: string; page?: number; credential?: string;
  draftId?: string; modId?: string | number; mode?: ModDraftMode; position?: number;
  token?: string; file?: IniFile; content?: string;
  name?: string; root?: string; importToken?: string;
  categoryId?: number; sort?: number; descending?: boolean;
  mapName?: string; installRoot?: string; steamExe?: string;
  allowSteamCmdDownload?: boolean; explicitRetry?: boolean;
  confirm?: boolean; jobId?: string; sessionName?: string;
  gamePort?: number; queryPort?: number; archive?: string; command?: string;
  coordinationArchive?: string; localMetadataArchive?: string;
  plugin?: string; enabled?: boolean; channel?: "stable"|"preview";
  point?:string; action?:string; message?:string; typedApproval?:string;
  rconPort?:number; password?:string; createIni?:boolean;
  beforeA?: string; beforeB?: string; afterA?: string; afterB?: string;
  sourceMap?: string;destinationMap?: string; profileId?: string };
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
  lanStatus: () => request<LanStatus>("lan.status"),
  lanPreview: (listenPort:number,peerAddress:string,peerPort:number,pairingKey:string) =>
    request<LanPreview>("lan.preview",{listenPort,peerAddress,peerPort,pairingKey}),
  lanApply: (token:string) => request<LanSaved>("lan.apply",{token,confirm:true}),
  operationsSnapshot: () => request<OperationsSnapshot>("operations.snapshot"),
  operationsMap: (mapId:string) => request<OperationsSnapshot>("operations.map",{mapId}),
  dashboard: () => request<DashboardSnapshot>("dashboard.snapshot"),
  travelCapture: () => request<TravelCapture>("travel.capture"),
  travelCompare: (p:{
    beforeA:string;beforeB:string;afterA:string;afterB:string;
    sourceMap:string;destinationMap:string;profileId:string
  }) => request<TravelComparison>("travel.compare",p),
  updatePrepare: (channel:"stable"|"preview") => request<UpdatePrepared>("updates.prepare",{channel,confirm:true}),
  updateInstall: (token:string) => request<UpdateInstallReceipt>("updates.install",{token,confirm:true}),
  updateStatus: () => request<UpdateStatus>("updates.status"),
  asaLifecycleJournal: (mapId:string) =>
    request<AsaLifecycleJournal>("servers.lifecycleJournal",{mapId}),
  freshAsaStopPreview: (mapId:string) =>
    request<FreshAsaStopPreview>("servers.gracefulStopPreview",{mapId}),
  freshAsaStopApply: (token:string) =>
    request<FreshAsaStopQueued>("servers.gracefulStopApply",{token,confirm:true}),
  freshAsaStopStatus: (jobId:string) =>
    request<FreshAsaStopStatus>("servers.gracefulStopStatus",{jobId}),
  freshAsaRestartCurrent: (mapId:string) =>
    request<AsaRestartJournalStatus>("servers.restartCurrent",{mapId}),
  freshAsaRestartPreview: (mapId:string) =>
    request<FreshAsaRestartPreview>("servers.restartPreview",{mapId}),
  freshAsaRestartApply: (token:string) =>
    request<FreshAsaRestartStarted>("servers.restartApply",{token,confirm:true}),
  freshAsaRestartStatus: (jobId:string) =>
    request<FreshAsaRestartStatus>("servers.restartStatus",{jobId}),
  freshAsaReadiness: (mapId:string) =>
    request<FreshAsaReadiness>("servers.freshReadiness",{mapId}),
  freshAsaStartPreview: (mapId:string) =>
    request<FreshAsaStartPreview>("servers.freshStartPreview",{mapId}),
  freshAsaStartApply: (token:string) =>
    request<FreshAsaStartReceipt>("servers.freshStartApply",{token,confirm:true}),
  freshAsaStartStatus: (mapId:string) =>
    request<FreshAsaStartStatus>("servers.freshStartStatus",{mapId}),
  steamStoppedCurrent: () =>
    request<DurableSteamQueue>("steam.stoppedCurrent"),
  steamStoppedPreview: (mapId:string) =>
    request<StoppedSteamPreview>("steam.stoppedPreview",{mapId}),
  steamStoppedApply: (token:string) =>
    request<StoppedSteamStarted>("steam.stoppedApply",{token,confirm:true}),
  steamStoppedStatus: (jobId:string) =>
    request<StoppedSteamStatus>("steam.stoppedStatus",{jobId}),
  rconConfigStatus: (mapId:string) => request<RconConfigStatus>("rcon.configStatus",{mapId}),
  rconConfigPreview: (mapId:string,rconPort:number,password:string,createIni:boolean) =>
    request<RconConfigPreview>("rcon.configPreview",{mapId,rconPort,password,createIni}),
  rconConfigApply: (token:string) =>
    request<RconConfigReceipt>("rcon.configApply",{token,confirm:true}),
  rconStatus: (mapId:string) => request<RconStatus>("rcon.status",{mapId}),
  rconListPlayers: (mapId:string) => request<RconPlayers>("rcon.listPlayers",{mapId}),
  rconGameLog: (mapId:string) => request<RconReadLog>("rcon.gameLog",{mapId}),
  rconChatLog: (mapId:string) => request<RconReadLog>("rcon.chatLog",{mapId}),
  rconSaveWorld: (mapId:string) => request<RconWorldSave>("rcon.saveWorld",{mapId,confirm:true}),
  rconAdminPreview: (mapId:string,action:string,message:string) =>
    request<RconAdminPreview>("rcon.adminPreview",{mapId,action,message}),
  rconAdminSend: (token:string,typedApproval:string) =>
    request<RconAdminResult>("rcon.adminSend",{token,typedApproval,confirm:true}),
  updateCheck: (channel:"stable"|"preview") => request<UpdateCheck>("updates.check",{channel}),
  configuredMods: (mapId: string) => request<ConfiguredMods>("mods.configured", { mapId }),
  sharedModInventory: () => request<SharedModInventory>("mods.sharedInventory"),
  catalogSearch: (term: string, page = 0) => request<CurseForgeResult>("curseforge.search", { term, page }),
  pluginInventory: (mapId: string) => request<PluginInventory>("plugins.inventory", { mapId }),
  pluginZipSelect: (mapId:string) =>
    request<PluginZipPreview>("plugins.zipSelect",{mapId}),
  pluginZipApply: (token:string) =>
    request<PluginZipInstalled>("plugins.zipApply",{token,confirm:true}),
  pluginStates: (mapId:string) => request<PluginState>("plugins.state",{mapId}),
  pluginTogglePreview: (mapId:string,plugin:string,enabled:boolean) =>
    request<PluginTogglePreview>("plugins.togglePreview",{mapId,plugin,enabled}),
  pluginToggleApply: (token:string) =>
    request<PluginToggleReceipt>("plugins.toggleApply",{token,confirm:true}),
  backupHistory: (mapId:string) =>
    request<WorldBackupHistory>("backup.history",{mapId}),
  backupPreview: (mapId:string) =>
    request<WorldBackupPreview>("backup.preview",{mapId}),
  backupCreate: (token:string) =>
    request<WorldBackupCreated>("backup.create",{token,confirm:true}),
  backupRestorePreview: (mapId:string,point:string) =>
    request<WorldBackupPreview>("backup.restorePreview",{mapId,point}),
  backupRestore: (token:string,mapName:string) =>
    request<WorldBackupRestored>("backup.restore",{token,mapName,confirm:true}),
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
  inspectLocalAsa: (name: string, root: string) =>
    request<LocalImportPreview>("servers.inspectLocal", { name, root }),
  importLocalAsa: (importToken: string) =>
    request<LocalImportReceipt>("servers.importLocal", { importToken }),
  catalogCategories: () => request<CurseForgeCategories>("curseforge.categories"),
  catalogFeatured: () => request<CurseForgeFeatured>("curseforge.featured"),
  catalogBrowse: (options: CurseForgeBrowseOptions) => request<CurseForgeResult>("curseforge.browse", options),
  catalogSummary: (modId: number) => request<CurseForgeSummary>("curseforge.summary", { modId }),
  catalogProject: (modId: number) => request<CurseForgeProject>("curseforge.project", { modId }),
  catalogFiles: (modId: number, page: number) => request<CurseForgeFilePage>("curseforge.files", { modId, page }),
  freshBrowse: () => request<{selected:boolean;installRoot?:string}>("steam.freshBrowse"),
  freshPreflight: (args: {
    mapName:string; installRoot:string; steamExe:string;
    allowSteamCmdDownload:boolean; explicitRetry:boolean;
  }) => request<FreshInstallPreflight>("steam.freshPreflight", args),
  freshStart: (token:string) =>
    request<{jobId:string;started:true;evidence:"EXPLICIT_FRESH_INSTALL_STARTED"}>("steam.freshStart", {token, confirm:true}),
  freshCurrent: () => request<{current:FreshInstallJob|null}>("steam.freshCurrent"),
  freshStatus: (jobId:string) => request<FreshInstallJob>("steam.freshStatus", {jobId}),
  freshCancel: (jobId:string) => request<{cancelRequested:true}>("steam.freshCancel", {jobId}),
  freshDiscard: (jobId:string) => request<{discarded:true}>("steam.freshDiscard", {jobId, confirm:true}),
  freshRegister: (jobId:string) => request<FreshInstallReceipt>("steam.freshRegister", {jobId, confirm:true}),
  initialStatus: (mapId:string) =>
    request<InitialMapStatus>("servers.initialStatus", {mapId}),
  initialPreview: (mapId:string, sessionName:string, gamePort:number, queryPort:number) =>
    request<InitialMapPreview>("servers.initialPreview", {mapId, sessionName, gamePort, queryPort}),
  initialApply: (token:string) =>
    request<InitialMapReceipt>("servers.initialApply", {token, confirm:true}),
  historicalLaunchEvidence: (archive:string,mapId:string,command:string) =>
    request<HistoricalLaunchEvidence>("historical.launchEvidence",{archive,mapId,command}),
  historicalLocalStatus: () => request<HistoricalLocalStoreStatus>("historical.localStatus"),
  historicalLocalParity: (archive:string) => request<HistoricalLocalStoreParity>("historical.localParity",{archive}),
  historicalList: (archive:string) => request<HistoricalStoreList>("historical.list", {archive}),
  historicalPreview: (archive:string, mapId:string, root:string) =>
    request<HistoricalStorePreview>("historical.preview", {archive,mapId,root}),
  historicalRegister: (token:string) =>
    request<HistoricalStoreRegistration>("historical.register", {token,confirm:true}),
  historicalPairedEvidence: (coordinationArchive:string, localMetadataArchive:string) =>
    request<HistoricalPairEvidence>("historical.pairedEvidence", {coordinationArchive,localMetadataArchive}),
};
