/* Explicit allowlisted Windows host contract; only guarded native methods may mutate local Manager configuration. */
export type LanStatus = {
  originalStorePair:{
    bothOriginalProfilesPresent:boolean;
    sameSharedSession:boolean;sameSharedPath:boolean;distinctDeviceIds:boolean;
    bothHaveFiveActiveMaps:boolean;copiedMetadataPairMatches:boolean;
    liveWriterCertified:false;characterTravelVerified:false;statusCode:string;
  };
  hostName:string; localHostId:string; expectedPeerHostId:string;
  listenPort:number; peerAddress:string; peerPort:number;
  pairingKeyConfigured:boolean; remoteFresh:boolean; remoteState:string;
  peers:LanPeerStatus[];
  secretReturned:false; peerWritesAllowed:false; evidence:string;
};
export type LanPeerStatus = {
  hostId:string;hostName:string;address:string;port:number;keyConfigured:boolean;
  online:boolean;lastSuccessUtc:string|null;stateCode:string;mapCount:number;
};
export type LanPreview = {
  token:string;action:"upsert"|"remove";listenPort:number;hostId:string;
  hostName?:string;peerAddress?:string;peerPort:number;
  pairingKeyWillChange:boolean;requiresManagerRestart:true;remoteWritesAllowed:false;
  evidence:string;
};
export type LanSaved = {
  saved:true;action:"upsert"|"remove";listenPort:number;hostId:string;peerCount:number;
  requiresManagerRestart:true;secretReturned:false;remoteWritesAllowed:false;
  evidence:string;
};

export type MapMaintenanceStatus={
  mapId:string;mapName:string;maintenance:boolean;permitted:boolean;
  storeProtected:boolean;requiresStopped:true;serverProcessModified:false;
  evidence:"PERSISTED_LOCAL_MANAGER_MAINTENANCE_ONLY";
};
export type NativeScheduleKind = "SaveWorld"|"Restart";
export type NativeScheduleFrequency = "Interval"|"Daily";
export type NativeAsaScheduleTask = {
  mapId:string;kind:NativeScheduleKind;frequency:NativeScheduleFrequency;
  intervalMinutes:number;minuteOfDay:number;daysMask:number;
  enabled:boolean;nextDueUtc:string;lastClaimedUtc:string|null;
  lastStatus:string;lastEvidence:string;
  evidence:"DURABLE_NATIVE_LOCAL_SCHEDULE_NO_AUTO_REPLAY";
};
export type NativeAsaExecutionEntry={
  runId:string;mapId:string;kind:NativeScheduleKind;
  scheduledUtc:string;claimedUtc:string;finishedUtc:string|null;
  status:string;evidence:string;
};
export type NativeAsaScheduleOverview={
  hostName:string;runnerState:string;jobs:NativeAsaScheduleTask[];
  remoteExecutionAllowed:false;
  evidence:"NATIVE_LOCAL_SCHEDULE_SUMMARY_READ_ONLY";
};
export type NativeAsaScheduleStatus={
  mapId:string;runnerState:string;jobs:NativeAsaScheduleTask[];
  history:NativeAsaExecutionEntry[];
  restartConflicts:string[];protectedStore:boolean;
  autoUpdateAvailable:false;remoteExecutionAllowed:false;
};
export type NativeAsaScheduleSaved={
  job:NativeAsaScheduleTask;restartConflicts:string[];
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
export type OperationsReceipt = {
  job:string;map:string;slotUtc:string;observedUtc:string;
  status:string;code:string;
};
export type ReadOnlyScheduleKind = "Observation"|"ModCheck"|"ApiCheck"|"SteamCheck";
export type OperationsSnapshot = {
  hostName:string; observedUtc:string; maps:OperationsMap[];
  schedules:OperationsSchedule[]; schedulingIsReadOnly:true;
  scheduleReceipts?:OperationsReceipt[];supervisorState?:string;
  remoteCommandsAllowed:false; evidence:string;
};
export type ReadOnlyScheduleSaved = {
  jobId:string;mapId:string;kind:ReadOnlyScheduleKind;
  intervalMinutes:number;enabled:true;
  autoLifecycleAllowed:false;remoteCommandsAllowed:false;evidence:string;
};
export type ReadOnlyScheduleDisabled = {
  jobId:string;enabled:false;autoLifecycleAllowed:false;evidence:string;
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
  /** Read-only native state, never Start/Stop or Store-session authorization. */
  storeLifecycleState?: "running"|"preflightRequired"|"needsConfiguration"|
    "needsOperator"|"restartPending"|"maintenance"|"protected";
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
  observedUtc: string | null;
  lastSuccessUtc?: string | null;
  fresh?: boolean;
  stateCode?: string;
  maps: PublicMap[];
};

export type RegisteredHost = {
  hostId:string;hostName:string;isLocal:boolean;isPaired:boolean;registeredUtc:string;
};
export type RegisteredMap = {
  mapId:string;hostId:string;mapName:string;localConfigurationPresent:boolean;registeredUtc:string;
};
export type RegisteredCluster = {
  clusterId:string;name:string;transferDirectory:string;backupDirectory:string;
  createdUtc:string;updatedUtc:string;
};
export type ClusterMapAssignment = {mapId:string;clusterId:string};
export type ClusterRegistrySnapshot = {
  schemaVersion:number;revision:number;
  hostRegistry:{schemaVersion:number;localHostId:string;hosts:RegisteredHost[];maps:RegisteredMap[]};
  clusterRegistry:{schemaVersion:number;revision:number;clusters:RegisteredCluster[];mapAssignments:ClusterMapAssignment[]};
};
export type ClusterMembershipChange = {
  mapId:string;mapName:string;hostId:string;hostName:string;
  previousClusterId:string|null;targetClusterId:string|null;
};
export type ClusterMembershipPreview = {
  baseRevision:number;clusterId:string;requestedMapIds:string[];
  additions:ClusterMembershipChange[];removals:ClusterMembershipChange[];
  moves:ClusterMembershipChange[];errors:string[];isValid:boolean;
};
export type ClusterMembershipPreviewResult = {token:string|null;preview:ClusterMembershipPreview};
export type ClusterMembershipReceipt = {
  clusterId:string;revision:number;addedCount:number;removedCount:number;
  movedCount:number;appliedUtc:string;
};

export type DashboardSnapshot = {
  mode: "LIVE_READ_ONLY";
  observedUtc: string;
  local: LocalHost;
  /** All configured peers, including stale/offline peers with their last safe observation. */
  remotes?: RemoteHost[];
  /** Compatibility projection for older native bridge builds. */
  remote: RemoteHost | null;
  peerMessage: string;
};

export function dashboardRemotes(snapshot:DashboardSnapshot):RemoteHost[]{
  if(Array.isArray(snapshot.remotes))return snapshot.remotes;
  return snapshot.remote?[snapshot.remote]:[];
}

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

export type EmbeddedStoreArchiveMap = {
  host:"HP"|"RYZEN";mapId:string;package:string;status:string;
  profileCandidates:number;tribeCandidates:number;
  unclassifiedCandidates:number;supersededEntries:number;
  indexBoundsVerified:boolean;gameplayVerified:false;characterTravelVerified:false;
};
export type EmbeddedStoreArchiveReport = {
  batch:string;maps:10;indexScannedMaps:number;needsReviewMaps:number;
  archiveFilesSha256Verified:true;liveConsistencyCertified:false;
  productionRestoreAllowed:false;characterTravelVerified:false;
  results:EmbeddedStoreArchiveMap[];
  evidence:"OFFLINE_TEN_MAP_WORLD_STORE_INDEX_AUDIT_ONLY";
};

export type OfflineAsaArchiveDiffMap = {
  host:"HP"|"RYZEN";mapId:string;name:string;
  filesAdded:number;filesRemoved:number;filesModified:number;
  worldChanged:boolean;worldFilesBefore:number;worldFilesAfter:number;
};
export type OfflineAsaArchiveDiff = {
  beforeBatch:string;afterBatch:string;maps:10;
  transferFilesBefore:number;transferFilesAfter:number;
  transferAdded:number;transferRemoved:number;transferModified:number;
  zeroByteTransferFilesPreserved:number;
  configurationAdded:number;configurationRemoved:number;configurationModified:number;
  configurationCoverageChanged:boolean;anyChanges:boolean;
  bothArchivesSha256Verified:true;liveConsistencyCertified:false;
  storeWriterCertified:false;characterTravelVerified:false;
  productionRestoreAllowed:false;results:OfflineAsaArchiveDiffMap[];
  evidence:"TWO_SEALED_OFFLINE_ARCHIVE_FILE_DIFFERENCES_ONLY";
};

export type EmbeddedStoreCopyReport = {
  fileName:string;state:string;format:number;storeBytes:number;
  sections:number;profileCandidates:number;tribeCandidates:number;
  unclassifiedCandidates:number;supersededEntries:number;duplicateKeys:number;
  indexBoundsVerified:boolean;sourceUnchanged:boolean;
  gameplayVerified:false;characterTravelVerified:false;
  evidence:"OFFLINE_SQLITE_EMBEDDED_STORE_INDEX_ONLY";
};

export type TravelMapProof = {
  mapId:string;mapName:string;confirmedStopped:boolean;
  saveDirectoryFound:boolean;ambiguousLayout:boolean;
  profileCount:number;tribeCount:number;zeroByteDetected:boolean;unstable:boolean;
  storeMode:boolean;worldPresent:boolean;worldBytes:number;worldUnstable:boolean;
  embeddedSurvivorVerified:false;
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
  storeModeDetected:boolean;embeddedSurvivorVerified:false;
  gameClientRejoinVerified:false;worldFilesPresentForStore:boolean;
  liveStoreWriterCertified:false;asaSaveCompleteCertified:false;
  productionUnlockAllowed:false;
  evidence:"FOUR_QUIESCENT_HOST_FILE_HASH_SNAPSHOTS_ONLY"|
    "STORE_V1_EMBEDDED_WORLD_METADATA_NO_CHARACTER_CONTENT_PROOF";
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
  preExitSaveEvidenceObserved:boolean;finalDoExitSaveCertified:false;
  gameClientRejoinVerified:false;
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
  saveCompleteProven:false;preExitSaveEvidenceObserved:boolean;
  finalDoExitSaveCertified:false;gameClientRejoinVerified:false;
  killPerformed:false;restartPerformed:false;
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
  mapId:string;mapName:string;rconPort:number;configured:boolean;permitted:boolean;
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
  watchId:string|null;nativeWitnessAvailable:boolean;
  rconAcknowledged:false;rconCommandDispatched:true;
  diskSaveCompletionProven:false;
  worldFilesTouchedByManager:false;
  evidence:"REAL_FIXED_RCON_SAVEWORLD_ACK_NOT_DISK_COMPLETION";
};
export type RconSaveStatus = {
  mapId:string;watchId:string;rconCommandDispatched:true;
  logSavingStarted:boolean;logSaveCompleteObserved:boolean;
  worldFileUpdated:boolean;worldFileStable:boolean;
  combinedSaveEvidenceObserved:boolean;diskFsyncCertified:false;
  gameClientRejoinVerified:false;sourceIsLocalAsaLog:true;
  stage:string;evidence:"LOCAL_ASA_POST_DISPATCH_LOG_AND_WORLD_METADATA";
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

export type ScheduledStoppedBackupState={
  mapId:string;mapName:string;enabled:boolean;
  intervalMinutes:number|null;nextDueUtc:string|null;
  lastAttemptUtc:string|null;lastStatus:string;lastPoint:string|null;
  runnerState:string;storeProtected:boolean;
  automaticRestoreAllowed:false;automaticServerStopAllowed:false;
  evidence:"PERSISTED_OPT_IN_STOPPED_NONSTORE_WORLD_BACKUPS";
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
  embeddedSurvivorCountUnknown:boolean;
  backupVerified:true;asaSaveCompleteProven:false;storeWriterTouched:false;
  evidence:"REAL_VERIFIED_STOPPED_MAP_WORLD_BACKUP";
};
export type WorldBackupRestored={
  mapId:string;mapName:string;point:string;protectiveBackup:string;
  restored:true;oldWorldRetained:true;bytesRestoredAndHashesVerified:true;
  worldRestoreVerified:false;gameClientLoadVerified:false;
  tribeOwnershipVerified:false;characterRejoinVerified:false;
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

export type AsaApiCoreRelease = {
  tag:string;publishedUtc:string|null;htmlUrl:string;
  assets:Array<{name:string;bytes:number;publisherDigestAvailable:boolean}>;
  installationStarted:false;
  evidence:"OFFICIAL_ASA_API_GITHUB_RELEASE_METADATA_ONLY";
};
export type AsaApiCorePreview = {
  token:string;mapId:string;mapName:string;releaseTag:string;
  assetName:string;bytes:number;officialSha256VerifiedInMetadata:true;
  approved:false;serverStopped:true;storeProtected:false;
  createsSelectiveRollback:true;restartsServer:false;
  runtimeLoadCertified:false;
  evidence:"VERIFIED_UPSTREAM_RELEASE_PREFLIGHT_NO_WRITE";
};
export type AsaApiCoreReceipt = {
  mapId:string;releaseTag:string;transactionId:string;binaries:number;
  bytesVerified:true;rollbackSnapshot:true;
  runtimeLoadedVerified:false;serverRestarted:false;
  evidence:"OFFICIAL_SHA256_STOPPED_API_CORE_SELECTIVE_ROLLBACK_SNAPSHOT";
};

export type AsaApiCoreRollbackCandidates = {
  candidates:Array<{transactionId:string;releaseTag:string;binaries:number}>;
  restartsServer:false;evidence:"COMMITTED_OWNED_API_CORE_TRANSACTIONS_ONLY";
};
export type AsaApiCoreRollbackPreview = {
  token:string;mapId:string;mapName:string;transactionId:string;
  releaseTag:string;binaries:number;approved:false;doesNotRestart:true;
  evidence:"STOPPED_OWNED_COMMITTED_ROLLBACK_REQUIRES_BYTE_REVALIDATION";
};
export type AsaApiCoreRollbackReceipt = {
  mapId:string;transactionId:string;filesRestored:number;filesRemoved:number;
  bytesVerified:true;serverRestarted:false;
  evidence:"STOPPED_OWNED_OFFICIAL_API_CORE_SHA256_ROLLBACK_ONLY";
};

export type HistoricalLiveGuard = {
  mapId:string;originalActiveReady:boolean;bothPeersDone:boolean;
  sharedSessionMatches:boolean;noPendingConversion:boolean;
  noMutationMarker:boolean;localMapBindingMatches:boolean;
  activeMaps:number;donePeers:number;readOnly:true;
  serverStarted:false;gameClientRejoinVerified:false;
  productionLaunchEnabled:false;
  evidence:"ORIGINAL_LIVE_TWO_HOST_ACTIVE_DONE_SHARED_GUARD_READ_ONLY";
};

export type HistoricalRconStatus = {
  mapId:string;mapName:string;configured:boolean;port:number;
  verifiedIni:boolean;originalStoreTouched:false;
  evidence:"HISTORICAL_RCON_MANAGER_PROFILE_STATUS_ONLY";
};
export type HistoricalRconPreview = {
  token:string;mapId:string;mapName:string;rconPort:number;
  existingIniVerified:true;originalActiveDoneVerified:true;
  originalStoreTouched:false;serverStarted:false;
  evidence:"HISTORICAL_RCON_EXISTING_INI_PREVIEW_ONLY";
};
export type HistoricalRconReceipt = {
  mapId:string;mapName:string;configured:true;rconPort:number;
  originalStoreTouched:false;iniModified:false;serverStarted:false;
  evidence:"HISTORICAL_RCON_DPAPI_PROFILE_ATTACHED";
};
export type HistoricalActiveStartStatus = {
  mapId:string;mapName:string;running:boolean;confirmedStopped:boolean;
  pid:number|null;rconConfigured:boolean;originalStartAutomatic:false;
  saveCompleteProven:false;gameplayTravelVerified:false;
  evidence:"HISTORICAL_ORIGINAL_PROCESS_READ_ONLY_STATUS";
};
export type HistoricalActiveStartPreview = {
  token:string;mapId:string;mapName:string;gamePort:number;
  queryPort:number;rconPort:number;originalTwoPeersVerified:true;
  conversionAllowed:false;automaticRetryAllowed:false;
  serverStarted:false;readyCertified:false;gameplayTravelVerified:false;
  evidence:"ORIGINAL_ACTIVE_STORE_NATIVE_START_PREFLIGHT";
};
export type HistoricalActiveStartReceipt = {
  mapId:string;mapName:string;pid:number;createdUtc:string;startRequested:true;
  readyCertified:false;originalStoreMetadataChanged:false;
  conversionPerformed:false;gameClientRejoinVerified:false;
  automaticRetryAllowed:false;
  evidence:"ORIGINAL_STORE_V1_REAL_LOCAL_ASA_START_PID_OBSERVED";
};
export type HistoricalRconLiveStatus = {
  mapId:string;mapName:string;configured:boolean;
  processVerified:boolean;portOwned:boolean;pid:number|null;
  permitted:boolean;allowedCommands:string[];
  storeTouched:false;saveCompleteProven:false;
  evidence:"ORIGINAL_ACTIVE_SOURCE_WMI_PID_TCP_OWNER_READY" |
    "ORIGINAL_RCON_NOT_VERIFIED_NO_COMMAND";
};
export type HistoricalRconReadResponse = {
  mapId:string;mapName:string;command:"ListPlayers"|"GetGameLog"|"GetChat";
  response:string;observedUtc:string;readOnly:true;
  storeTouched:false;saveCompleteProven:false;
  evidence:"ORIGINAL_STORE_V1_VERIFIED_LOCAL_READ_ONLY_RCON";
};
export type HistoricalAdminRconPreview = {
  token:string;mapId:string;mapName:string;
  action:"broadcast"|"serverChat"|"wildWipe"|"custom";
  command:string;approvalWord:string;dangerous:boolean;
  expiresUtc:string;originalStoreActive:true;dispatched:false;
  gameClientVerified:false;originalStoreMetadataWritten:false;
  evidence:"ORIGINAL_STORE_ACTIVE_ADMIN_RCON_PREVIEW";
};
export type HistoricalAdminRconReceipt = {
  mapId:string;mapName:string;action:string;command:string;
  response:string;commandSent:true;oneShot:true;
  originalStoreMetadataWritten:false;saveCompleteProven:false;
  gameplayEffectVerified:false;
  evidence:"ORIGINAL_STORE_ACTIVE_ADMIN_RCON_SINGLE_SEND";
};

export type HistoricalRestartPreview = {
  token:string;mapId:string;mapName:string;pid:number;rconPort:number;
  usesSaveWorld:true;usesDoExit:true;launchesOriginalActive:true;
  conversionAllowed:false;automaticRetry:false;
  gameplayReadinessCertified:false;originalStoreTouched:false;
  evidence:"ORIGINAL_STORE_V1_RESTART_TWO_STAGE_PREFLIGHT";
};
export type HistoricalRestartQueued = {
  jobId:string;started:true;automaticRetry:false;forceKill:false;storeTouched:false;
  evidence:"ORIGINAL_STORE_V1_MANUAL_RESTART_JOB_QUEUED";
};
export type HistoricalRestartRecovery = {
  mapId:string;phase:"None"|"Stopping"|"Starting"|"Completed"|"NeedsOperator";
  entryBelongsToMap:boolean;blocked:boolean;requiresOperator:boolean;
  inMemoryRunning:boolean;jobId:string|null;oldPid:number|null;newPid:number|null;
  automaticRecovery:false;automaticRetry:false;
  originalStoreTouched:false;lockCleared:false;
  evidence:"ORIGINAL_STORE_V1_DURABLE_RESTART_RECOVERY_READ_ONLY";
};
export type HistoricalRestartStatus = {
  jobId:string;mapId:string;state:"queued"|"running"|"completed"|"needsOperator";
  step:string;changedUtc:string;oldPid:number;newPid:number;
  oldProcessStopped:boolean;preExitSaveLogAndWorldObserved:boolean;
  readyCertified:false;gameClientRejoinVerified:false;
  finalDoExitSaveCertified:false;automaticRetry:false;
  forceKill:false;storeTouched:false;
  evidence:"ORIGINAL_STORE_V1_NATIVE_MANUAL_RESTART_JOB";
};
export type HistoricalGracefulStopPreview = {
  token:string;mapId:string;mapName:string;pid:number;rconPort:number;
  originalTwoPeersVerified:true;usesSaveWorld:true;usesDoExit:true;
  forceKillAllowed:false;automaticRestart:false;originalStoreTouched:false;
  evidence:"ORIGINAL_STORE_V1_SAVEWORLD_DOEXIT_PREFLIGHT_ONLY";
};
export type HistoricalSaveWorldPreview = {
  token:string;mapId:string;mapName:string;pid:number;rconPort:number;
  originalTwoPeersVerified:true;usesSaveWorld:true;usesDoExit:false;
  forceKillAllowed:false;automaticRestart:false;originalStoreTouched:false;
  evidence:"ORIGINAL_STORE_V1_SAVEWORLD_ONLY_PREFLIGHT";
};
export type HistoricalSaveWorldQueued={
  jobId:string;started:true;automaticRetry:false;
  forceKill:false;storeTouched:false;
  evidence:"ORIGINAL_STORE_V1_REAL_SAVEWORLD_ONLY_JOB_QUEUED";
};
export type HistoricalGracefulStopQueued = {
  jobId:string;started:true;automaticRetry:false;
  forceKill:false;storeTouched:false;
  evidence:"ORIGINAL_STORE_V1_REAL_STOP_JOB_QUEUED";
};
export type HistoricalGracefulStopRecovery = {
  mapId:string;operationId:string|null;
  phase:"None"|"Prepared"|"Mutating"|"Completed"|
    "CancelledBeforeMutation"|"NeedsOperator";
  operationKind:string;updatedUtc:string|null;
  blocked:boolean;requiresOperator:boolean;
  inMemoryRunning:boolean;jobId:string|null;
  activeJobKind:"Stop"|"SaveWorldOnly"|null;
  automaticRecovery:false;automaticRetry:false;
  sharedStoreMutated:false;lockCleared:false;
  evidence:"ORIGINAL_STORE_V1_DURABLE_LOCAL_OPERATION_RECOVERY_READ_ONLY";
};
export type HistoricalGracefulStopStatus = {
  jobId:string;kind:"Stop"|"SaveWorldOnly";state:"queued"|"running"|"completed"|"needsOperator";
  step:string;changedUtc:string;confirmedStopped:boolean;
  preExitSaveLogAndWorldObserved:boolean;
  finalDoExitSaveCertified:false;gameClientRejoinVerified:false;
  automaticRetry:false;forceKill:false;storeTouched:false;
  evidence:"ORIGINAL_STORE_V1_REAL_SAVEWORLD_DOEXIT_STATE"|
    "ORIGINAL_STORE_V1_SAVEWORLD_ONLY_NO_DOEXIT_STATE";
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
  blocker:"NONE"|"PROFILE_ALREADY_CONFIGURED_OR_PROTECTED"|"MAP_LEVEL_UNRECOGNIZED"|"SERVER_EXECUTABLE_MISSING"|"PROCESS_RUNNING"|"PROCESS_STOP_NOT_VERIFIED";
  processStatus:string; hasServerExecutable:boolean; gamePort:number; queryPort:number;
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

export type OfflineAsaRestorePreview = {
  token:string;archiveName:string;verified:false;
  requiresExplicitApproval:true;target:"FRESH_ISOLATED_TEMP_FOLDER_ONLY";
  sqlUsed:false;productionRestoreAllowed:false;evidence:string;
};
export type OfflineAsaRestoreStarted = {
  jobId:string;started:true;sqlUsed:false;
  productionRestoreAllowed:false;evidence:string;
};
export type OfflineAsaRestoreStatus = {
  jobId:string;state:"queued"|"running"|"completed"|"needsOperator";
  stage:string;labName:string;maps:number;files:number;changedUtc:string;
  gameplayRestoredVerified:false;storeWriterVerified:false;
  productionRestoreAllowed:false;sqlUsed:false;evidence:string;
};

export type PluginJsonDocument = {
  token:string;mapId:string;plugin:string;content:string;sha256:string;
  encoding:string;bytes:number;evidence:"STOPPED_OWNED_PLUGIN_JSON_SNAPSHOT";
};
export type PluginJsonPreview = {
  previewToken:string;changed:boolean;oldSha256:string;newSha256:string;
  oldLines:number;newLines:number;evidence:"VALIDATED_PLUGIN_JSON_PREVIEW_NO_WRITES";
};
export type PluginJsonApplied = {
  saved:boolean;backupCreated:boolean;newSha256?:string;
  asaPluginReloaded?:false;requiresRestart?:true;storeTouched?:false;
  sqlUsed?:false;evidence?:string;reason?:string;
};

export type OfflineAsaArchivePreview = {
  token:string;hpCopies:5;ryzenCopies:5;transferMetadataPresent:true;
  configurationCopiesPresent:boolean;
  hpFolderName:string;ryzenFolderName:string;
  transferFolderName:string;destinationFolderName:string;
  requiresExplicitApproval:true;originalStoreTouched:false;sqlUsed:false;
  liveSnapshotConsistencyCertified:false;evidence:string;
};
export type OfflineAsaArchiveStarted = {
  jobId:string;started:true;sqlUsed:false;productionRestoreAllowed:false;
  originalStoreTouched:false;evidence:string;
};
export type OfflineAsaArchiveStatus = {
  jobId:string;state:"queued"|"running"|"completed"|"needsOperator";
  code:string;archiveName:string;verifiedMaps:number;transferFiles:number;
  configurationFiles:number;
  changedUtc:string;liveConsistencyCertified:false;storeWriterCertified:false;
  productionRestoreAllowed:false;sqlUsed:false;evidence:string;
};

export type MultiHostClusterBackupPreview = {
  token:string;batchId:string;archiveName:string;
  hosts:number;clusters:number;maps:number;components:number;bytes:number;
  requiresExplicitApproval:true;offlineCopiesOnly:true;
  liveConsistencyCertified:false;storeWriterCertified:false;
  sqlUsed:false;rconUsed:false;serverProcessTouched:false;
  productionRestoreAllowed:false;evidence:string;
};
export type MultiHostClusterBackupStarted = {
  jobId:string;started:true;productionRestoreAllowed:false;
  rconUsed:false;serverProcessTouched:false;evidence:string;
};
export type MultiHostClusterBackupStatus = {
  jobId:string;kind:"backup"|"restore";
  state:"queued"|"running"|"completed"|"needsOperator";
  stage:string;artifactName:string;rollbackArtifactName:string;hosts:number;clusters:number;maps:number;
  components:number;bytes:number;changedUtc:string;
  productionRestoreAllowed:false;liveConsistencyCertified:false;
  storeWriterCertified:false;sqlUsed:false;rconUsed:false;
  serverProcessTouched:false;evidence:string;
};
export type MultiHostClusterRestorePreview = {
  token:string;batchId:string;archiveName:string;
  hosts:number;clusters:number;maps:number;components:number;
  previousBatchId?:string|null;targetDirectoryName?:string|null;
  requiresExplicitApproval:true;
  restoreTarget:"NEW TEMP SANDBOX ONLY"|"EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK";
  productionRestoreAllowed:false;liveConsistencyCertified:false;
  storeWriterCertified:false;evidence:string;
};
export type MultiHostClusterRestoreStarted = {
  jobId:string;started:true;
  target:"NEW TEMP SANDBOX ONLY"|"EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK";
  productionRestoreAllowed:false;rconUsed:false;
  serverProcessTouched:false;evidence:string;
};
export type MultiHostRestoreRecoveryPreview = {
  token:string|null;targetDirectoryName:string;transactionId:string|null;
  phase:string;needsOperator:boolean;canRollback:boolean;
  requiresExplicitApproval:boolean;quarantinedReplacementName:string|null;
  protectiveCopyName:string|null;reason:string;productionRestoreAllowed:false;
  rconUsed:false;serverProcessTouched:false;evidence:string;
};
export type MultiHostRestoreRecoveryCandidate = {
  targetDirectoryName:string;transactionId:string|null;phase:string;
  needsOperator:boolean;canRollback:boolean;reason:string;
};
export type MultiHostRestoreRecoveryListing = {
  truncated:boolean;transactions:MultiHostRestoreRecoveryCandidate[];
};
export type MultiHostRestoreRecoveryStarted = {
  jobId:string;started:true;target:"TEMP SANDBOX ROLLBACK ONLY";
  productionRestoreAllowed:false;rconUsed:false;serverProcessTouched:false;evidence:string;
};

export type PortableModProfileExport = {
  exported:true;mapId:string;profileName:string;enabled:number;passive:number;
  disabled:number;sha256:string;remoteWritesAllowed:false;storeTouched:false;
  sqlUsed:false;evidence:string;
};
export type PortableModProfilePreview = {
  token:string;label:string;sha256:string;mode:"merge"|"replace";
  enabled:number;passive:number;disabled:number;
  targets:Array<{mapId:string;mapName:string;changed:boolean;
    before:{active:string[];passive:string[];disabled:string[]};
    after:{active:string[];passive:string[];disabled:string[]}}>;
  operationCount:number;changed:boolean;
  productionLoadedVerified:false;remoteWritesAllowed:false;
  storeTouched:false;sqlUsed:false;evidence:string;
};
export type PortableModProfileApplied = {
  saved:true;changedMaps:string[];operationsApplied:number;requiresRestart:true;
  productionLoadedVerified:false;remoteWritesAllowed:false;
  storeTouched:false;sqlUsed:false;evidence:string;
};

export type ModCopyMode = "merge" | "replace";
export type ModCopyPreview = {
  token:string;sourceMapId:string;mode:ModCopyMode;
  targets:Array<{
    mapId:string;mapName:string;changed:boolean;
    before:{active:string[];passive:string[];disabled:string[]};
    after:{active:string[];passive:string[];disabled:string[]};
  }>;
  operationCount:number;changed:boolean;requiresRestart:boolean;
  remoteWritesAllowed:false;storeWriterTouched:false;evidence:string;
};
export type ModCopyApplied = {
  saved:true;sourceMapId:string;mode:ModCopyMode;changedMaps:string[];
  operationsApplied:number;requiresRestart:true;modsLoadedVerified:false;
  remoteWritesAllowed:false;storeWriterTouched:false;evidence:string;
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

type WireRequest = { id: string; method: "lan.status" | "lan.preview" | "lan.apply" | "operations.snapshot" | "operations.map" |
  "operations.scheduleCreate" | "operations.scheduleDisable" |
  "schedule.nativeStatus" | "schedule.nativeSet" | "schedule.nativeDisable" |
  "schedule.nativeOverview" |
  "maps.maintenanceStatus" | "maps.maintenanceSet" | "dashboard.snapshot" | "mods.configured" | "mods.sharedInventory" | "curseforge.search" | "plugins.inventory" | "plugins.state" | "plugins.togglePreview" | "plugins.toggleApply" | "backups.summary" | "logs.read" | "steam.installations" | "servers.suggestPorts" |
  "curseforge.credentialStatus" | "curseforge.credentialSave" | "curseforge.credentialClear" |
  "mods.draftStart" | "mods.draftStage" | "mods.draftUndo" |
  "mods.draftRedo" | "mods.draftDiscard" | "mods.draftSave" |
  "mods.copyPreview" | "mods.copyApply" |
  "mods.profileExport" | "mods.profileImportPreview" | "mods.profileImportApply" |
  "cluster.offlinePreview" | "cluster.offlineApply" | "cluster.offlineStatus" |
  "cluster.multiHostBackupPreview" | "cluster.multiHostBackupApply" | "cluster.multiHostBackupStatus" |
  "cluster.multiHostRestorePreview" | "cluster.multiHostRestoreApply" | "cluster.multiHostRestoreStatus" |
  "cluster.multiHostRestoreReplacePreview" | "cluster.multiHostRestoreReplaceApply" |
  "cluster.multiHostRestoreRecoveryList" | "cluster.multiHostRestoreRecoveryPreview" |
  "cluster.multiHostRestoreRecoveryApply" |
  "plugins.jsonOpen" | "plugins.jsonPreview" | "plugins.jsonSave" | "plugins.jsonClose" |
  "cluster.restoreLabPreview" | "cluster.restoreLabApply" | "cluster.restoreLabStatus" |
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
  "historical.pairedEvidence" | "historical.launchEvidence" | "historical.liveGuard" |
   "historical.rconStatus" | "historical.rconPreview" | "historical.rconApply" |
   "historical.activeStartStatus" | "historical.activeStartPreview" | "historical.activeStartApply" |
   "historical.rconLiveStatus" | "historical.rconPlayers" |
   "historical.rconGameLog" | "historical.rconChatLog" |
   "historical.gracefulStopPreview" | "historical.gracefulStopApply" |
   "historical.gracefulStopStatus" | "historical.gracefulStopRecovery" |
   "historical.saveWorldPreview" | "historical.saveWorldApply" |
   "historical.saveWorldStatus" |
   "historical.restartPreview" | "historical.restartApply" |
   "historical.restartStatus" | "historical.restartRecovery" |
   "historical.adminPreview" | "historical.adminSend" |
  "api.coreRelease" | "api.corePreview" | "api.coreApply" |
  "api.coreRollbackCandidates" | "api.coreRollbackPreview" | "api.coreRollbackApply" |
  "updates.status" | "updates.check" | "updates.prepare" | "updates.install" | "rcon.configStatus" | "rcon.configPreview" | "rcon.configApply" | "rcon.status" | "rcon.listPlayers" | "rcon.gameLog" | "rcon.chatLog" | "rcon.saveWorld" | "rcon.saveStatus" | "rcon.adminPreview" | "rcon.adminSend" |
  "backup.history" | "backup.preview" | "backup.create" |
  "backup.restorePreview" | "backup.restore" |
  "backup.scheduleStatus" | "backup.scheduleCreate" | "backup.scheduleDisable" |
  "cluster.registry.snapshot" | "cluster.host.register" | "cluster.map.register" |
  "cluster.create" | "cluster.update" | "cluster.membership.preview" | "cluster.membership.apply" |
  "travel.capture" | "travel.compare" | "store.inspectCopy" | "store.inspectArchive" | "store.compareArchives";
  listenPort?:number; peerAddress?:string; peerPort?:number; pairingKey?:string;
  peerAction?:"upsert"|"remove";
  mapId?: string; term?: string; page?: number; credential?: string;
  draftId?: string; modId?: string | number; mode?: ModDraftMode | ModCopyMode;
  position?: number; sourceMapId?:string;targets?:string[]; expectedPeerHostId?:string;
  token?: string; file?: IniFile; content?: string;
  name?: string; root?: string; importToken?: string;
  categoryId?: number; sort?: number; descending?: boolean;
  mapName?: string; installRoot?: string; steamExe?: string;
  allowSteamCmdDownload?: boolean; explicitRetry?: boolean;
   confirm?: boolean; maintenance?:boolean; jobId?: string; watchId?: string; sessionName?: string;
   targetDirectoryName?:string;
  kind?:ReadOnlyScheduleKind|NativeScheduleKind;frequency?:NativeScheduleFrequency;
  intervalMinutes?:number;minuteOfDay?:number;daysMask?:number;
  gamePort?: number; queryPort?: number; archive?: string; command?: string;
  coordinationArchive?: string; localMetadataArchive?: string;
  plugin?: string; enabled?: boolean; channel?: "stable"|"preview";
  assetName?:string;transactionId?:string;
  point?:string; action?:string; message?:string; typedApproval?:string;
  rconPort?:number; password?:string; createIni?:boolean;
  beforeA?: string; beforeB?: string; afterA?: string; afterB?: string;
  sourceMap?: string;destinationMap?: string; profileId?: string;
  hostId?:string;hostName?:string;mapIds?:string[];
  clusterId?:string;transferDirectory?:string;backupDirectory?:string };
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

// Every request must settle. Previously a failed/closed native handler could
// leave its Promise and map entry alive forever, freezing the UI. A timeout
// does NOT imply that a mutating operation was rolled back or safe to retry.
const pending = new Map<string, {
  resolve(value: unknown): void; reject(reason: Error): void;
  timer: ReturnType<typeof setTimeout>;
}>();
const webview = window.chrome?.webview;

function timeoutFor(method: WireRequest["method"]): number {
  if (method === "updates.prepare") return 10 * 60_000; // verified GitHub installer download
  if (method === "store.inspectArchive" || method === "store.compareArchives" ||
      method === "store.inspectCopy" || method === "historical.localParity" ||
      method === "historical.pairedEvidence" || method === "backup.create" ||
      method === "backup.restore") return 60 * 60_000; // bounded large-file workflows
  return 120_000;
}

webview?.addEventListener("message", (event) => {
  const msg = event.data;
  if (!msg || typeof msg.id !== "string") return;
  const waiting = pending.get(msg.id);
  if (!waiting) return;
  pending.delete(msg.id);
  clearTimeout(waiting.timer);
  if (msg.ok) waiting.resolve(msg.data);
  else waiting.reject(new Error(msg.error || "UM-UNKNOWN"));
});

function request<T>(method: WireRequest["method"], args: Partial<WireRequest> = {}): Promise<T> {
  if (!webview) return Promise.reject(new Error("UM-NATIVE-UNAVAILABLE"));
  const id = crypto.randomUUID();
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      if (!pending.has(id)) return;
      pending.delete(id);
      reject(new Error("UM-NATIVE-TIMEOUT-OPERATION-STATUS-UNKNOWN"));
    }, timeoutFor(method));
    pending.set(id, { resolve: (value) => resolve(value as T), reject, timer });
    // Native route and correlation ID are assigned LAST: a forwarded payload
    // may contain an untyped "id" or "method" at runtime. It must never
    // replace the allowlisted operation chosen by this client bridge.
    try { webview.postMessage({ ...args, id, method }); }
    catch {
      pending.delete(id);
      clearTimeout(timer);
      reject(new Error("UM-NATIVE-UNAVAILABLE"));
    }
  });
}

export const managerBridge = {
  clusterRegistrySnapshot: () =>
    request<ClusterRegistrySnapshot>("cluster.registry.snapshot"),
  clusterRegisterHost: (hostId:string,hostName:string) =>
    request<RegisteredHost>("cluster.host.register",{hostId,hostName}),
  clusterRegisterMap: (hostId:string,mapId:string,mapName:string) =>
    request<RegisteredMap>("cluster.map.register",{hostId,mapId,mapName}),
  clusterCreate: (clusterId:string,name:string,transferDirectory:string,backupDirectory:string) =>
    request<RegisteredCluster>("cluster.create",{clusterId,name,transferDirectory,backupDirectory}),
  clusterUpdate: (clusterId:string,name:string,transferDirectory:string,backupDirectory:string) =>
    request<RegisteredCluster>("cluster.update",{clusterId,name,transferDirectory,backupDirectory}),
  clusterMembershipPreview: (clusterId:string,mapIds:string[]) =>
    request<ClusterMembershipPreviewResult>("cluster.membership.preview",{clusterId,mapIds}),
  clusterMembershipApply: (token:string) =>
    request<ClusterMembershipReceipt>("cluster.membership.apply",{token,confirm:true}),
  lanStatus: () => request<LanStatus>("lan.status"),
  lanPreview: (listenPort:number,hostId:string,hostName:string,peerAddress:string,
    peerPort:number,pairingKey:string,peerAction:"upsert"|"remove"="upsert") =>
    request<LanPreview>("lan.preview",{
      listenPort,hostId,hostName,peerAddress,peerPort,pairingKey,peerAction
    }),
  lanApply: (token:string) => request<LanSaved>("lan.apply",{token,confirm:true}),
  operationsSnapshot: () => request<OperationsSnapshot>("operations.snapshot"),
  operationsMap: (mapId:string) => request<OperationsSnapshot>("operations.map",{mapId}),
  operationsScheduleCreate: (mapId:string,kind:ReadOnlyScheduleKind,intervalMinutes:number) =>
    request<ReadOnlyScheduleSaved>("operations.scheduleCreate",{
      mapId,kind,intervalMinutes,confirm:true
    }),
  operationsScheduleDisable: (jobId:string) =>
    request<ReadOnlyScheduleDisabled>("operations.scheduleDisable",{jobId,confirm:true}),
  dashboard: () => request<DashboardSnapshot>("dashboard.snapshot"),
  embeddedStoreInspectCopy: () => request<EmbeddedStoreCopyReport>("store.inspectCopy"),
  embeddedStoreInspectArchive: () => request<EmbeddedStoreArchiveReport>("store.inspectArchive"),
  offlineArchiveCompare: () => request<OfflineAsaArchiveDiff>("store.compareArchives"),
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
  rconSaveStatus: (mapId:string,watchId:string) =>
    request<RconSaveStatus>("rcon.saveStatus",{mapId,watchId}),
  rconAdminPreview: (mapId:string,action:string,message:string) =>
    request<RconAdminPreview>("rcon.adminPreview",{mapId,action,message}),
  rconAdminSend: (token:string,typedApproval:string) =>
    request<RconAdminResult>("rcon.adminSend",{token,typedApproval,confirm:true}),
  updateCheck: (channel:"stable"|"preview") => request<UpdateCheck>("updates.check",{channel}),
  configuredMods: (mapId: string) => request<ConfiguredMods>("mods.configured", { mapId }),
  sharedModInventory: () => request<SharedModInventory>("mods.sharedInventory"),
  catalogSearch: (term: string, page = 0) => request<CurseForgeResult>("curseforge.search", { term, page }),
  pluginInventory: (mapId: string) => request<PluginInventory>("plugins.inventory", { mapId }),
  apiCoreRelease: () => request<AsaApiCoreRelease>("api.coreRelease"),
  apiCorePreview: (mapId:string,assetName:string) =>
    request<AsaApiCorePreview>("api.corePreview",{mapId,assetName}),
  apiCoreApply: (token:string) =>
    request<AsaApiCoreReceipt>("api.coreApply",{token,confirm:true}),
  apiCoreRollbackCandidates: (mapId:string) =>
    request<AsaApiCoreRollbackCandidates>("api.coreRollbackCandidates",{mapId}),
  apiCoreRollbackPreview: (mapId:string,transactionId:string) =>
    request<AsaApiCoreRollbackPreview>("api.coreRollbackPreview",{mapId,transactionId}),
  apiCoreRollbackApply: (token:string) =>
    request<AsaApiCoreRollbackReceipt>("api.coreRollbackApply",{token,confirm:true}),
  pluginZipSelect: (mapId:string) =>
    request<PluginZipPreview>("plugins.zipSelect",{mapId}),
  pluginZipApply: (token:string) =>
    request<PluginZipInstalled>("plugins.zipApply",{token,confirm:true}),
  pluginStates: (mapId:string) => request<PluginState>("plugins.state",{mapId}),
  pluginTogglePreview: (mapId:string,plugin:string,enabled:boolean) =>
    request<PluginTogglePreview>("plugins.togglePreview",{mapId,plugin,enabled}),
  pluginToggleApply: (token:string) =>
    request<PluginToggleReceipt>("plugins.toggleApply",{token,confirm:true}),
  mapMaintenanceStatus: (mapId:string) =>
    request<MapMaintenanceStatus>("maps.maintenanceStatus",{mapId}),
  mapMaintenanceSet: (mapId:string,maintenance:boolean) =>
    request<MapMaintenanceStatus>("maps.maintenanceSet",{mapId,maintenance,confirm:true}),
  nativeScheduleOverview: () =>
    request<NativeAsaScheduleOverview>("schedule.nativeOverview"),
  nativeScheduleStatus: (mapId:string) =>
    request<NativeAsaScheduleStatus>("schedule.nativeStatus",{mapId}),
  nativeScheduleSet: (mapId:string,kind:NativeScheduleKind,
      frequency:NativeScheduleFrequency,intervalMinutes:number,
      minuteOfDay:number,daysMask:number) =>
    request<NativeAsaScheduleSaved>("schedule.nativeSet",{
      mapId,kind,frequency,intervalMinutes,minuteOfDay,daysMask,confirm:true
    }),
  nativeScheduleDisable: (mapId:string,kind:NativeScheduleKind) =>
    request<NativeAsaScheduleTask>("schedule.nativeDisable",{
      mapId,kind,confirm:true
    }),
  backupScheduleStatus: (mapId:string) =>
    request<ScheduledStoppedBackupState>("backup.scheduleStatus",{mapId}),
  backupScheduleCreate: (mapId:string,intervalMinutes:number) =>
    request<ScheduledStoppedBackupState>("backup.scheduleCreate",{mapId,intervalMinutes,confirm:true}),
  backupScheduleDisable: (mapId:string) =>
    request<ScheduledStoppedBackupState>("backup.scheduleDisable",{mapId,confirm:true}),
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
  modCopyPreview: (sourceMapId:string,targets:string[],mode:ModCopyMode) =>
    request<ModCopyPreview>("mods.copyPreview",{sourceMapId,targets,mode}),
  modCopyApply: (token:string) =>
    request<ModCopyApplied>("mods.copyApply",{token,confirm:true}),
  modProfileExport: (mapId:string) =>
    request<PortableModProfileExport>("mods.profileExport",{mapId}),
  modProfileImportPreview: (targets:string[],mode:ModCopyMode) =>
    request<PortableModProfilePreview>("mods.profileImportPreview",{targets,mode}),
  modProfileImportApply: (token:string) =>
    request<PortableModProfileApplied>("mods.profileImportApply",{token,confirm:true}),
  offlineArchivePreview: () =>
    request<OfflineAsaArchivePreview>("cluster.offlinePreview"),
  offlineArchiveApply: (token:string) =>
    request<OfflineAsaArchiveStarted>("cluster.offlineApply",{token,confirm:true}),
  offlineArchiveStatus: (jobId:string) =>
    request<OfflineAsaArchiveStatus>("cluster.offlineStatus",{jobId}),
  multiHostBackupPreview: () =>
    request<MultiHostClusterBackupPreview>("cluster.multiHostBackupPreview"),
  multiHostBackupApply: (token:string) =>
    request<MultiHostClusterBackupStarted>("cluster.multiHostBackupApply",{token,confirm:true}),
  multiHostBackupStatus: (jobId:string) =>
    request<MultiHostClusterBackupStatus>("cluster.multiHostBackupStatus",{jobId}),
  multiHostRestorePreview: () =>
    request<MultiHostClusterRestorePreview>("cluster.multiHostRestorePreview"),
  multiHostRestoreApply: (token:string) =>
    request<MultiHostClusterRestoreStarted>("cluster.multiHostRestoreApply",{token,confirm:true}),
  multiHostRestoreReplacePreview: () =>
    request<MultiHostClusterRestorePreview>("cluster.multiHostRestoreReplacePreview"),
  multiHostRestoreReplaceApply: (token:string) =>
    request<MultiHostClusterRestoreStarted>("cluster.multiHostRestoreReplaceApply",{token,confirm:true}),
  multiHostRestoreRecoveryList: () =>
    request<MultiHostRestoreRecoveryListing>("cluster.multiHostRestoreRecoveryList"),
  multiHostRestoreRecoveryPreview: (targetDirectoryName:string) =>
    request<MultiHostRestoreRecoveryPreview>("cluster.multiHostRestoreRecoveryPreview",{targetDirectoryName}),
  multiHostRestoreRecoveryApply: (token:string) =>
    request<MultiHostRestoreRecoveryStarted>("cluster.multiHostRestoreRecoveryApply",{token,confirm:true}),
  multiHostRestoreStatus: (jobId:string) =>
    request<MultiHostClusterBackupStatus>("cluster.multiHostRestoreStatus",{jobId}),
  pluginJsonOpen: (mapId:string,plugin:string) =>
    request<PluginJsonDocument>("plugins.jsonOpen",{mapId,plugin}),
  pluginJsonPreview: (token:string,content:string) =>
    request<PluginJsonPreview>("plugins.jsonPreview",{token,content}),
  pluginJsonSave: (token:string) =>
    request<PluginJsonApplied>("plugins.jsonSave",{token,confirm:true}),
  pluginJsonClose: (token:string) =>
    request<{closed:true}>("plugins.jsonClose",{token}),
  offlineRestoreLabPreview: () =>
    request<OfflineAsaRestorePreview>("cluster.restoreLabPreview"),
  offlineRestoreLabApply: (token:string) =>
    request<OfflineAsaRestoreStarted>("cluster.restoreLabApply",{token,confirm:true}),
  offlineRestoreLabStatus: (jobId:string) =>
    request<OfflineAsaRestoreStatus>("cluster.restoreLabStatus",{jobId}),
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
  historicalAdminPreview: (mapId:string,
      action:"broadcast"|"serverChat"|"wildWipe"|"custom",message:string) =>
    request<HistoricalAdminRconPreview>("historical.adminPreview",{
      mapId,action,message
    }),
  historicalAdminSend: (token:string,typedApproval:string) =>
    request<HistoricalAdminRconReceipt>("historical.adminSend",{
      token,confirm:true,typedApproval
    }),
  historicalRestartRecovery: (mapId:string) =>
    request<HistoricalRestartRecovery>("historical.restartRecovery",{mapId}),
  historicalRestartPreview: (mapId:string) =>
    request<HistoricalRestartPreview>("historical.restartPreview",{mapId}),
  historicalRestartApply: (token:string,typedApproval:string) =>
    request<HistoricalRestartQueued>("historical.restartApply",{
      token,typedApproval,confirm:true
    }),
  historicalRestartStatus: (jobId:string) =>
    request<HistoricalRestartStatus>("historical.restartStatus",{jobId}),
  historicalGracefulStopRecovery: (mapId:string) =>
    request<HistoricalGracefulStopRecovery>("historical.gracefulStopRecovery",{mapId}),
  historicalGracefulStopPreview: (mapId:string) =>
    request<HistoricalGracefulStopPreview>("historical.gracefulStopPreview",{mapId}),
  historicalGracefulStopApply: (token:string,typedApproval:string) =>
    request<HistoricalGracefulStopQueued>("historical.gracefulStopApply",{
      token,typedApproval,confirm:true
    }),
  historicalGracefulStopStatus: (jobId:string) =>
    request<HistoricalGracefulStopStatus>("historical.gracefulStopStatus",{jobId}),
  historicalSaveWorldPreview: (mapId:string) =>
    request<HistoricalSaveWorldPreview>("historical.saveWorldPreview",{mapId}),
  historicalSaveWorldApply: (token:string,typedApproval:string) =>
    request<HistoricalSaveWorldQueued>("historical.saveWorldApply",{
      token,typedApproval,confirm:true
    }),
  historicalSaveWorldStatus: (jobId:string) =>
    request<HistoricalGracefulStopStatus>("historical.saveWorldStatus",{jobId}),
  historicalRconLiveStatus: (mapId:string) =>
    request<HistoricalRconLiveStatus>("historical.rconLiveStatus",{mapId}),
  historicalRconPlayers: (mapId:string) =>
    request<HistoricalRconReadResponse>("historical.rconPlayers",{mapId}),
  historicalRconGameLog: (mapId:string) =>
    request<HistoricalRconReadResponse>("historical.rconGameLog",{mapId}),
  historicalRconChatLog: (mapId:string) =>
    request<HistoricalRconReadResponse>("historical.rconChatLog",{mapId}),
  historicalActiveStartStatus: (mapId:string) =>
    request<HistoricalActiveStartStatus>("historical.activeStartStatus",{mapId}),
  historicalActiveStartPreview: (mapId:string) =>
    request<HistoricalActiveStartPreview>("historical.activeStartPreview",{mapId}),
  historicalActiveStartApply: (token:string,typedApproval:string) =>
    request<HistoricalActiveStartReceipt>("historical.activeStartApply",{
      token,typedApproval,confirm:true
    }),
  historicalRconStatus: (mapId:string) =>
    request<HistoricalRconStatus>("historical.rconStatus",{mapId}),
  historicalRconPreview: (mapId:string,rconPort:number,password:string) =>
    request<HistoricalRconPreview>("historical.rconPreview",{mapId,rconPort,password}),
  historicalRconApply: (token:string) =>
    request<HistoricalRconReceipt>("historical.rconApply",{token,confirm:true}),
  historicalLocalStatus: () => request<HistoricalLocalStoreStatus>("historical.localStatus"),
  historicalLiveGuard: (mapId:string) => request<HistoricalLiveGuard>("historical.liveGuard",{mapId}),
  historicalLocalParity: (archive:string) => request<HistoricalLocalStoreParity>("historical.localParity",{archive}),
  historicalList: (archive:string) => request<HistoricalStoreList>("historical.list", {archive}),
  historicalPreview: (archive:string, mapId:string, root:string) =>
    request<HistoricalStorePreview>("historical.preview", {archive,mapId,root}),
  historicalRegister: (token:string) =>
    request<HistoricalStoreRegistration>("historical.register", {token,confirm:true}),
  historicalPairedEvidence: (coordinationArchive:string, localMetadataArchive:string) =>
    request<HistoricalPairEvidence>("historical.pairedEvidence", {coordinationArchive,localMetadataArchive}),
};
