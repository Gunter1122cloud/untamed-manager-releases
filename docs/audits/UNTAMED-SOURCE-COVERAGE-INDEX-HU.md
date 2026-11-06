# Untamed teljes szöveges forráslefedési index

Bázis: `3b8b448fb906a02d45615a466ef2900ca40200f0`. 139 szöveges fájl leltározva; 64 Core fájl nyilvános metódusai és lexikális hivatkozásai indexelve.
A forrás- és hívóindex statikus bizonyíték, nem végrehajtott teszt. A fájlnévvel azonos típusnévre kereső hivatkozásindex osztályaliasok miatt nem teljes call graph.
Különösen ClusterBackend.cs több típust tartalmaz (ClusterService/WindowsProcessInspector/SecureStore); a GAP explicit metódus- és GUI-hivatkozásai elsőbbséget élveznek.
Az üres lexikális találatot nem állítjuk automatikusan „nincs hívó”-nak. A tesztbeli hivatkozás nem élő működés.

## 64 Core fájl

| Core fájl | Nyilvános belépési pontok (sor) | GUI lexikális referenciák | Supervisor lexikális referenciák | Teszt lexikális referenciák |
| --- | --- | --- | --- | --- |
| AsaApiInspector.cs | 19: public static AsaApiInspection Inspect(string installation,ProcessCheck? mapState=null); 73: public static bool IsPe64(string path) | wpf/MainWindow.ApiAndPlugins.cs:32 | wpf/Supervisor/Worker.cs:152 | wpf/Testing/Integration/OperationalFeaturesTests.cs:138; wpf/Testing/Integration/OperationalFeaturesTests.cs:142 |
| AsaApiReleaseCatalog.cs | 24: public static AsaApiReleaseInfo Parse(string json); 89: public static async Task<AsaApiReleaseInfo> FetchLatestAsync( | wpf/MainWindow.ApiAndPlugins.cs:59 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ProfessionalOperationsTests.cs:286; wpf/Testing/Integration/ProfessionalOperationsTests.cs:292 |
| AsaHistoricalLogReader.cs | 35: public static AsaHistoricalEvidence InspectFiles(string asaPath,string? apiPath); 39: public static AsaHistoricalEvidence InspectLines( | wpf/MainWindow.AsaLogEvidence.cs:32 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/AsaHistoricalEvidenceTests.cs:28; wpf/Testing/Integration/AsaHistoricalEvidenceTests.cs:48; wpf/Testing/Integration/AsaHistoricalEvidenceTests.cs:53; wpf/Testing/Integration/AsaHistoricalEvidenceTests.cs:61; wpf/Testing/Integration/AsaHistoricalEvidenceTests.cs:68; wpf/Testing/Integration/AsaHistoricalEvidenceTests.cs:72 |
| AsaInstallationInventory.cs | 19: public static IReadOnlyList<AsaInstallationInfo> Inspect( | wpf/MainWindow.AsaInstall.cs:22 | wpf/Supervisor/Worker.cs:156 | wpf/Testing/Integration/Program.cs:474; wpf/Testing/Integration/Program.cs:479 |
| BackupManifest.cs | 17: public static BackupManifest Create(string directory, Guid mapId, string scope); 23: public static void Verify(string directory, BackupManifest manifest, Guid mapId); 32: public static string Serialize(BackupManifest manifest) => JsonSerializer.Serialize(manifest); | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:604 |
| BackupRetention.cs | 20: public static RetentionPlan Preview(IEnumerable<RetainedBackup> source,; 55: public static string Quarantine(RetentionChoice choice,string managedRoot, | wpf/MainWindow.OperationalCalendar.cs:276 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:280; wpf/Testing/Integration/OperationalFeaturesTests.cs:285 |
| ClusterBackend.cs | 94: public static string Protect(string plain); 100: public static string Unprotect(string encrypted); 111: public ConfigStore(string profile = "default", string? rootOverride = null); 120: public ManagerConfig Read(); 126: public void Save(ManagerConfig cfg); 154: public ProcessCheck Inspect(LocalMap map); 208: public static bool CommandMatches(string commandLine, string exe, string requiredArguments); 228: public ProcessIdentity Launch(LocalMap map); 256: public PeerStatusListener(int port, string sharedSecret, Func<HostStatus> getStatus); 264: public void Start() { _listener.Start(); _loop=Task.Run(AcceptAsync); }; 313: public async ValueTask DisposeAsync(); 322: public static async Task<byte[]> Read(Stream stream,CancellationToken ct); 330: public static async Task Write(Stream stream,byte[] data,CancellationToken ct); 339: public static async Task<HostStatus> Query(string host,int port,string sharedSecret,; 379: public ClusterService(string profile = "default",string? rootOverride=null,; 386: public void Save() => Store.Save(Config);; 387: public void UpdateSettings(Action<ManagerConfig> update) {update(Config);Save();}; 388: public void AddMap(LocalMap map); 395: public void RemoveMap(Guid id); 400: public ProcessCheck Check(LocalMap map); 421: public ProcessIdentity LaunchLocal(Guid id); 446: public HostStatus LocalStatus(); 457: public async Task BeginLanAsync(); 467: public async Task<HostStatus?> PollPeerAsync(CancellationToken ct=default); 484: public async ValueTask DisposeAsync() | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat |
| ClusterVaultArchiveInspector.cs | 36: public static ClusterVaultArchiveReport Inspect(string archivePath) | wpf/MainWindow.ClusterVault.cs:47 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ClusterVaultTests.cs:95; wpf/Testing/Integration/ClusterVaultTests.cs:117; wpf/Testing/Integration/ClusterVaultTests.cs:126; wpf/Testing/Integration/ClusterVaultTests.cs:134; wpf/Testing/StoreV1/Program.cs:82; wpf/Testing/StoreV1/Program.cs:95 |
| CurseForgeCatalog.cs | 33: public CurseForgeCatalog(HttpClient httpClient) =>; 36: public async Task<CurseForgeSearchResult> SearchAsaAsync(string key,string term,int index=0,; 86: public async Task<CurseForgeFilesResult> GetModFilesAsync(string key,int modId, | wpf/MainWindow.CurseForge.cs:29; wpf/MainWindow.ModUpdatesAndRcon.cs:50 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:267 |
| DirectoryUpdateTransaction.cs | 12: public sealed record State(string Application, string Phase, Dictionary<string,string> Hashes);; 15: public DirectoryUpdateTransaction(string backupRoot) { root=Path.GetFullPath(backupRoot); }; 71: public void Prepare(string application); 85: public void Commit(); 90: public void Rollback() | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:1064; wpf/Testing/Integration/Program.cs:1069; wpf/Testing/Integration/Program.cs:1076; wpf/Testing/Integration/Program.cs:1079; wpf/Testing/Integration/Program.cs:1085; wpf/Testing/Integration/Program.cs:1091 |
| DiscordStatusPublisher.cs | 27: public static bool ValidWebhook(string? value); 43: public static string Compose(SupervisorSnapshot snapshot,; 83: public static async Task<DiscordPostReceipt> SendAsync( | wpf/MainWindow.Discord.cs:38; wpf/MainWindow.Discord.cs:73; wpf/MainWindow.Discord.cs:96; wpf/MainWindow.Discord.cs:111 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/DiscordStatusTests.cs:43; wpf/Testing/Integration/DiscordStatusTests.cs:44; wpf/Testing/Integration/DiscordStatusTests.cs:46; wpf/Testing/Integration/DiscordStatusTests.cs:48; wpf/Testing/Integration/DiscordStatusTests.cs:50; wpf/Testing/Integration/DiscordStatusTests.cs:62 |
| EventJournal.cs | 15: public void Append(string host,Guid? map,string severity,string code); 31: public MonitorEvent[] Read(string? host=null,Guid? map=null,string? severity=null,DateTimeOffset? since=null) | wpf/MainWindow.Supervision.cs:21; wpf/MainWindow.Supervision.cs:111 | wpf/Supervisor/Worker.cs:18 | wpf/Testing/Integration/Program.cs:676; wpf/Testing/Integration/Program.cs:679 |
| HistoricalLaunchProfileReader.cs | 23: public static HistoricalLaunchBundle Inspect(string copiedZip) | wpf/MainWindow.ManagerMigration.cs:31 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ManagerMigrationArchiveTests.cs:187 |
| IsolatedFakeAsaSession.cs | 27: public IsolatedFakeAsaSession(string executable,string root,bool denySaveProof=false); 51: public ProcessCheck Check(); 68: public void Start(); 85: public async Task Command(string command,CancellationToken ct); 122: public Task<bool> SaveCompleted(CancellationToken ct); 131: public void Dispose() | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ProfessionalOperationsTests.cs:247; wpf/Testing/Integration/ProfessionalOperationsTests.cs:262; wpf/Testing/Integration/ProfessionalOperationsTests.cs:272 |
| KovaAuditBridge.cs | 26: public static KovaAuditSnapshot InspectCopy(string file,KovaAuditHost host); 156: public static KovaAuditPair Evaluate(KovaAuditSnapshot hp,KovaAuditSnapshot ryzen) | wpf/MainWindow.KovaAudit.cs:38; wpf/MainWindow.KovaAudit.cs:39; wpf/MainWindow.KovaAudit.cs:40 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ProfessionalOperationsTests.cs:130; wpf/Testing/Integration/ProfessionalOperationsTests.cs:131; wpf/Testing/Integration/ProfessionalOperationsTests.cs:132; wpf/Testing/Integration/ProfessionalOperationsTests.cs:146; wpf/Testing/Integration/ProfessionalOperationsTests.cs:148; wpf/Testing/Integration/ProfessionalOperationsTests.cs:151 |
| KovaFixtureAudit.cs | 14: public static KovaFixtureReport Inspect(string testRoot,Guid testOwner); 35: public static string Snapshot(string fixtureRoot,Guid testOwner,string destination) | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:160; wpf/Testing/Integration/Program.cs:164; wpf/Testing/Integration/Program.cs:170 |
| KovaProfileImporter.cs | 25: public static KovaProfileReport InspectCopy(string profileJson,string? storeJson=null); 97: public static ManagerConfig BuildInertLocalDraft(KovaProfileReport report,string localHostId) | wpf/MainWindow.OperationalCalendar.cs:226; wpf/MainWindow.OperationalCalendar.cs:249 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:306; wpf/Testing/Integration/OperationalFeaturesTests.cs:307; wpf/Testing/Integration/OperationalFeaturesTests.cs:319 |
| KovaStoreV1CopyReader.cs | 19: public static KovaStoreV1CopyObservation Inspect( | wpf/MainWindow.KovaStoreV1.cs:40 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ProfessionalOperationsTests.cs:216; wpf/Testing/Integration/ProfessionalOperationsTests.cs:227; wpf/Testing/Integration/ProfessionalOperationsTests.cs:233; wpf/Testing/Integration/ProfessionalOperationsTests.cs:241; wpf/Testing/StoreV1/Program.cs:204; wpf/Testing/StoreV1/Program.cs:206 |
| LocalBackupCatalog.cs | 15: public static IReadOnlyList<LocalBackupHistoryEntry> Inspect(string localRoot,Guid mapId, | wpf/MainWindow.Backup.cs:73 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:311; wpf/Testing/Integration/Program.cs:315; wpf/Testing/Integration/Program.cs:319 |
| LocalBackupOverview.cs | 16: public static IReadOnlyList<LocalBackupMapSummary> Inspect( | wpf/MainWindow.BackupOverview.cs:25 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:328; wpf/Testing/Integration/Program.cs:335; wpf/Testing/Integration/Program.cs:340 |
| LocalBackupWorkflow.cs | 18: public static LocalBackupResult Create(LocalMap map,ProcessCheck check,string destination, | wpf/MainWindow.Backup.cs:44 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:299; wpf/Testing/Integration/Program.cs:344; wpf/Testing/Integration/Program.cs:348 |
| LocalContentOperations.cs | 14: public static IReadOnlyList<string> ReadMods(string? existing); 29: public static string ReorderMods(string existing,IEnumerable<string> order); 44: public static string ModifyMods(string existing,IEnumerable<string> input,bool add); 62: public static string InstallLocalPluginFolder(LocalMap map,string sourceFolder) | wpf/MainWindow.Bulk.cs:38; wpf/MainWindow.Bulk.cs:71; wpf/MainWindow.Bulk.cs:85 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:130; wpf/Testing/Integration/Program.cs:212; wpf/Testing/Integration/Program.cs:214; wpf/Testing/Integration/Program.cs:217; wpf/Testing/Integration/Program.cs:218; wpf/Testing/Integration/Program.cs:227 |
| LocalModAssignments.cs | 22: public static LocalModInventory Inspect(IEnumerable<LocalMap> ownedMaps) | wpf/MainWindow.ModInventory.cs:21 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:247; wpf/Testing/Integration/Program.cs:255; wpf/Testing/Integration/Program.cs:260 |
| LocalPluginInventory.cs | 17: public static IReadOnlyList<LocalPluginInfo> Scan(LocalMap map) | wpf/MainWindow.PluginInventory.cs:28 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:454 |
| LocalProfileTransfer.cs | 19: public static string Export(ManagerConfig config); 29: public static SafeProfilePreview Preview(ManagerConfig current,string json); 50: public static void Apply(ManagerConfig current,string json); 63: public static void ExportToFile(ManagerConfig config,string path) | wpf/MainWindow.Complete.cs:211; wpf/MainWindow.Complete.cs:225; wpf/MainWindow.Complete.cs:238; wpf/MainWindow.Complete.cs:244 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:107; wpf/Testing/Integration/Program.cs:112; wpf/Testing/Integration/Program.cs:115; wpf/Testing/Integration/Program.cs:120; wpf/Testing/Integration/Program.cs:125 |
| LocalStoreProbe.cs | 10: public static StoreAssessment Inspect(string root, string clusterId, string arguments) | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:629; wpf/Testing/Integration/Program.cs:630; wpf/Testing/Integration/Program.cs:631 |
| ModStateController.cs | 27: public static ModSet Read(LocalMap map); 69: public static ModPreview Preview(LocalMap map,ModEdit edit); 110: public static IReadOnlyList<ModPreview> ApplyLocalBatch(ClusterService cluster,; 150: public static ModPreview PreviewMove(LocalMap map,string id,ModMode mode,int newIndex); 153: public static void ConfirmAppliedAfterVerifiedRestart(ClusterService cluster,LocalMap map) | wpf/MainWindow.ModState.cs:49; wpf/MainWindow.ModState.cs:71; wpf/MainWindow.ModState.cs:84; wpf/MainWindow.ModState.cs:91; wpf/MainWindow.ModState.cs:105 | wpf/Supervisor/Worker.cs:149 | wpf/Testing/Integration/OperationalFeaturesTests.cs:27; wpf/Testing/Integration/OperationalFeaturesTests.cs:31; wpf/Testing/Integration/OperationalFeaturesTests.cs:43; wpf/Testing/Integration/OperationalFeaturesTests.cs:46; wpf/Testing/Integration/OperationalFeaturesTests.cs:53; wpf/Testing/Integration/OperationalFeaturesTests.cs:59 |
| ModUpdatePlanner.cs | 18: public static LocalModEvidence InspectCandidate(string installRoot,string projectId); 49: public static ModUpdateAssessment Assess(LocalModEvidence local, | wpf/MainWindow.ModUpdatesAndRcon.cs:32; wpf/MainWindow.ModUpdatesAndRcon.cs:52; wpf/MainWindow.ModUpdatesAndRcon.cs:53 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:332; wpf/Testing/Integration/OperationalFeaturesTests.cs:333; wpf/Testing/Integration/OperationalFeaturesTests.cs:335; wpf/Testing/Integration/OperationalFeaturesTests.cs:338 |
| NetworkLatencyDiagnostics.cs | 22: public static bool ValidTarget(string? text); 37: public static LatencyReport Analyze(string target,; 65: public static async Task<LatencyReport> MeasureAsync( | wpf/MainWindow.Network.cs:27; wpf/MainWindow.Network.cs:28; wpf/MainWindow.Network.cs:32; wpf/MainWindow.Network.cs:33 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/NetworkLatencyTests.cs:14; wpf/Testing/Integration/NetworkLatencyTests.cs:15; wpf/Testing/Integration/NetworkLatencyTests.cs:16; wpf/Testing/Integration/NetworkLatencyTests.cs:17; wpf/Testing/Integration/NetworkLatencyTests.cs:18; wpf/Testing/Integration/NetworkLatencyTests.cs:19 |
| OfflineClusterBackup.cs | 30: public static OfflineClusterBackupManifest BuildFromFolders(; 80: public static OfflineClusterBackupManifest Build(OfflineClusterBackupRequest request,; 166: public static OfflineClusterBackupManifest Verify(string package) | wpf/MainWindow.OfflineClusterBackup.cs:46; wpf/MainWindow.OfflineClusterBackup.cs:82 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ProfessionalOperationsTests.cs:59; wpf/Testing/Integration/ProfessionalOperationsTests.cs:69; wpf/Testing/Integration/ProfessionalOperationsTests.cs:78; wpf/Testing/Integration/ProfessionalOperationsTests.cs:82 |
| OfflineSqlArchive.cs | 17: public static bool IsSupported(string name) =>; 20: public static OfflineSqlArchiveResult ImportFixture(; 64: public static OfflineSqlArchiveResult Verify(string package,string database,Guid owner); 80: public static string SimulateRestore(string package,string database,Guid owner) | wpf/MainWindow.Complete.cs:63; wpf/MainWindow.Complete.cs:83; wpf/MainWindow.Complete.cs:97; wpf/MainWindow.Complete.cs:108 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ProfessionalOperationsTests.cs:49; wpf/Testing/Integration/ProfessionalOperationsTests.cs:53; wpf/Testing/Integration/Program.cs:135; wpf/Testing/Integration/Program.cs:137; wpf/Testing/Integration/Program.cs:139; wpf/Testing/Integration/Program.cs:143 |
| OperationalScheduler.cs | 39: public static ScheduleDue Next(RecurringSchedule job,DateTimeOffset afterUtc,; 95: public static RecurringSchedule[] UntamedTemplates(IEnumerable<Guid> localMapIds); 104: public static IReadOnlyList<ScheduleDue> Upcoming(IEnumerable<RecurringSchedule> jobs, | wpf/MainWindow.OperationalCalendar.cs:41; wpf/MainWindow.OperationalCalendar.cs:75; wpf/MainWindow.OperationalCalendar.cs:85; wpf/MainWindow.OperationalCalendar.cs:110 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:213; wpf/Testing/Integration/OperationalFeaturesTests.cs:219; wpf/Testing/Integration/OperationalFeaturesTests.cs:223; wpf/Testing/Integration/OperationalFeaturesTests.cs:227 |
| PersistentScheduler.cs | 25: public PersistentScheduler(string root); 37: public async Task RunDue(IEnumerable<ScheduledTask> tasks,IReadOnlySet<Guid> owned,DateTimeOffset now,Func<ScheduledTask,Task> execute) | wpf/MainWindow.Supervision.cs:142 | wpf/Supervisor/Worker.cs:19 | wpf/Testing/Integration/Program.cs:684; wpf/Testing/Integration/Program.cs:688; wpf/Testing/Integration/Program.cs:691; wpf/Testing/Integration/Program.cs:693; wpf/Testing/Integration/Program.cs:704; wpf/Testing/Integration/Program.cs:705 |
| PluginStateManager.cs | 18: public static IReadOnlyList<PluginStateItem> Inventory(LocalMap map); 34: public static PluginToggleResult Toggle(LocalMap map,ProcessCheck state, | wpf/MainWindow.ApiAndPlugins.cs:33; wpf/MainWindow.ApiAndPlugins.cs:92 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:117; wpf/Testing/Integration/OperationalFeaturesTests.cs:123 |
| ProcessFamily.cs | 15: public static bool IsPersistedSurvivor(ObservedProcess observedChild,; 35: public static bool IsVerifiedPair(IReadOnlyList<ObservedProcess> snapshot, | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:529; wpf/Testing/Integration/Program.cs:532; wpf/Testing/Integration/Program.cs:535; wpf/Testing/Integration/Program.cs:540; wpf/Testing/Integration/Program.cs:543; wpf/Testing/Integration/Program.cs:594 |
| RconCommandPolicy.cs | 22: public static RconDraft Preview(Guid localMap,RconAction kind,string? parameter,; 62: public FakeRconQueue(string localProfile); 74: public async Task<RconTestReceipt> ExecuteOnceAsync(Guid operation,RconDraft draft, | wpf/MainWindow.ModUpdatesAndRcon.cs:88 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:231; wpf/Testing/Integration/OperationalFeaturesTests.cs:236; wpf/Testing/Integration/OperationalFeaturesTests.cs:241 |
| RconProtocol.cs | 18: public static async Task<string> Execute(string host,int port,string password,string command, | wpf/MainWindow.Cluster.cs:279 | wpf/Supervisor/Worker.cs:66 | wpf/Testing/Integration/Program.cs:645; wpf/Testing/Integration/Program.cs:653; wpf/Testing/Integration/RconTransportTests.cs:75 |
| RecoverySimulation.cs | 31: public RecoverySimulationJournal(string profileRoot); 84: public RecoveryTransition[] Read()=>Locked(()=>Load().ToArray());; 85: public RecoveryTransition Begin(Guid ownedMap,DateTimeOffset now); 106: public RecoveryTransition Advance(Guid attempt,RecoveryPhase next,DateTimeOffset now); 134: public static RecoverySimulationResult Run(RecoverySimulationJournal journal, | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat |
| RecurringReadOnlyRunner.cs | 31: public RecurringReadOnlyRunner(string root); 81: public RecurringJobReceipt[] Tick(IEnumerable<RecurringSchedule> schedules,; 143: public RecurringJobReceipt[] Recent(int max=100) | wpf/MainWindow.OperationalCalendar.cs:140 | wpf/Supervisor/Worker.cs:21 | wpf/Testing/Integration/OperationalFeaturesTests.cs:148; wpf/Testing/Integration/OperationalFeaturesTests.cs:163 |
| ReleaseCapabilityGate.cs | 21: public static OperationReadiness Check(ManagerOperation operation,bool owned,; 49: public static void DemandFakeAsaStart(ClusterService cluster,LocalMap map) | wpf/MainWindow.Cluster.cs:186; wpf/MainWindow.Cluster.cs:191; wpf/MainWindow.Cluster.cs:276 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:366; wpf/Testing/Integration/Program.cs:66; wpf/Testing/Integration/Program.cs:70; wpf/Testing/Integration/Program.cs:72; wpf/Testing/Integration/Program.cs:74; wpf/Testing/Integration/Program.cs:85 |
| ReleaseCatalog.cs | 13: public int CompareTo(ReleaseOrder? other); 21: public static ReleaseOrder? Parse(string tag); 33: public static UpdateRelease? Select(string json,string repository,UpdateChannel channel); 58: public static bool IsNewer(UpdateRelease release,Version running,string installedTag) | wpf/MainWindow.Updater.cs:30; wpf/MainWindow.Updater.cs:81 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:913; wpf/Testing/Integration/Program.cs:914; wpf/Testing/Integration/Program.cs:916; wpf/Testing/Integration/Program.cs:917; wpf/Testing/Integration/Program.cs:919; wpf/Testing/Integration/Program.cs:921 |
| SafeConfigEditor.cs | 21: public static ConfigSnapshot Read(string path,string mapInstallation,ConfigKind kind,; 36: public static ConfigPreview Preview(ConfigSnapshot snapshot,string edited); 48: public static string Save(ConfigSnapshot baseline,string edited,string mapInstallation,; 77: public static string SetIniValue(ConfigSnapshot baseline,string section,string key,; 115: public static string[] FindIniKeys(ConfigSnapshot baseline,string section) | wpf/MainWindow.ConfigEditor.cs:51; wpf/MainWindow.ConfigEditor.cs:65; wpf/MainWindow.ConfigEditor.cs:79; wpf/MainWindow.ConfigEditor.cs:85; wpf/MainWindow.ConfigEditor.cs:87 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:82; wpf/Testing/Integration/OperationalFeaturesTests.cs:83; wpf/Testing/Integration/OperationalFeaturesTests.cs:84; wpf/Testing/Integration/OperationalFeaturesTests.cs:88; wpf/Testing/Integration/OperationalFeaturesTests.cs:94; wpf/Testing/Integration/OperationalFeaturesTests.cs:96 |
| SafeLifecycle.cs | 19: public static async Task Stop(ILocalServerSession session, bool restart, TimeSpan timeout, | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ProfessionalOperationsTests.cs:253; wpf/Testing/Integration/ProfessionalOperationsTests.cs:258; wpf/Testing/Integration/ProfessionalOperationsTests.cs:266; wpf/Testing/Integration/Program.cs:636 |
| SafePluginZip.cs | 41: public static PluginZipInfo Inspect(string zipPath); 48: public static PluginZipPreview Preview(LocalMap map,ProcessCheck check,string zipPath,string backupRoot); 67: public static PluginInstallResult Install(LocalMap map,ProcessCheck check,string zipPath,string backupRoot); 140: public static PluginRollbackPreview PreviewRollback(LocalMap map,ProcessCheck check,; 184: public static PluginRollbackResult Rollback(LocalMap map,ProcessCheck check, | wpf/MainWindow.PluginRollback.cs:35; wpf/MainWindow.PluginRollback.cs:74; wpf/MainWindow.PluginZip.cs:34; wpf/MainWindow.PluginZip.cs:52; wpf/MainWindow.PluginZip.cs:74 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:363; wpf/Testing/Integration/Program.cs:367; wpf/Testing/Integration/Program.cs:370; wpf/Testing/Integration/Program.cs:377; wpf/Testing/Integration/Program.cs:386; wpf/Testing/Integration/Program.cs:388 |
| SafeSupportBundle.cs | 61: public static void AssertHonest(); 104: public static SafeSupportBundleResult Create(string destination, | wpf/MainWindow.Readiness.cs:61 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/SafeSupportBundleTests.cs:47; wpf/Testing/Integration/SafeSupportBundleTests.cs:76; wpf/Testing/Integration/SafeSupportBundleTests.cs:80 |
| SaveWorldValidator.cs | 16: public static SyntheticSaveResult VerifySynthetic(Guid ownedMap,ProcessIdentity expected,; 48: public static SyntheticSaveResult VerifyLiveUnavailable(string? rconReply,DateTimeOffset? fileModifiedUtc) | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:721; wpf/Testing/Integration/Program.cs:724; wpf/Testing/Integration/Program.cs:726; wpf/Testing/Integration/Program.cs:729; wpf/Testing/Integration/Program.cs:732; wpf/Testing/Integration/Program.cs:739 |
| StatusCipher.cs | 11: public static byte[] Encrypt(byte[] pairingKey, byte[] plaintext); 25: public static byte[] Decrypt(byte[] pairingKey, byte[] frame) | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:924; wpf/Testing/Integration/Program.cs:925; wpf/Testing/Integration/Program.cs:926; wpf/Testing/Integration/Program.cs:929; wpf/Testing/Integration/Program.cs:932 |
| SteamCmdPreflight.cs | 14: public static SteamCmdFileStatus Inspect(string executablePath) | wpf/MainWindow.Steam.cs:33 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:45; wpf/Testing/Integration/Program.cs:52; wpf/Testing/Integration/Program.cs:56 |
| SteamCmdTestRunner.cs | 21: public static async Task<FakeSteamReceipt> RunAsync(string fakeExe, | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/OperationalFeaturesTests.cs:349; wpf/Testing/Integration/OperationalFeaturesTests.cs:353; wpf/Testing/Integration/OperationalFeaturesTests.cs:357; wpf/Testing/Integration/OperationalFeaturesTests.cs:361 |
| SteamUpdateDryRun.cs | 19: public static SteamUpdatePreview Plan(string steamCmdPath,string installRoot,; 52: public static SteamDryRunReceipt Simulate(SteamUpdatePreview plan,string profileRoot) | wpf/MainWindow.Complete.cs:150; wpf/MainWindow.Complete.cs:181; wpf/MainWindow.Complete.cs:185 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:183; wpf/Testing/Integration/Program.cs:190; wpf/Testing/Integration/Program.cs:195; wpf/Testing/Integration/Program.cs:202 |
| StoreV1Fingerprint.cs | 17: public static string Compute(JsonElement config); 26: public static bool Matches(JsonElement config,string expected) | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ProfessionalOperationsTests.cs:175; wpf/Testing/Integration/ProfessionalOperationsTests.cs:176; wpf/Testing/StoreV1/Program.cs:22 |
| SupervisorChannel.cs | 23: public SupervisorChannel(string profileRoot); 50: public void Write(SupervisorSnapshot snapshot); 58: public SupervisorSnapshot Read(string hostId) | wpf/App.xaml.cs:125; wpf/MainWindow.Supervision.cs:15 | wpf/Supervisor/Worker.cs:18 | wpf/Testing/Integration/Program.cs:659; wpf/Testing/Integration/Program.cs:663; wpf/Testing/Integration/Program.cs:1028; wpf/Testing/Integration/Program.cs:1042; wpf/Testing/Integration/Program.cs:1047 |
| SupervisorPolicy.cs | 11: public SupervisionState Observe(Guid map,ProcessCheck check,bool maintenance,DateTimeOffset now,int limit=3); 19: public bool DueNotification(Guid map,DateTimeOffset due,DateTimeOffset now,bool maintenance); 25: public static bool IsFresh(DateTimeOffset observed,DateTimeOffset now)=>observed<=now.AddSeconds(5) && observed>=now.AddSeconds(-30); | Nincs fájlnév-alapú találat | wpf/Supervisor/Worker.cs:19 | wpf/Testing/Integration/Program.cs:898 |
| SyntheticRestore.cs | 16: public SyntheticRestore(); 23: public void Restore(string package, bool interruptAfterSwap=false); 48: public void Recover() | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:572 |
| TelemetryHistory.cs | 17: public TelemetryHistory(string localProfile); 36: public void Observe(MapObservation observation,DateTimeOffset now); 65: public IReadOnlyList<TelemetrySample> Read(Guid map,DateTimeOffset now,int max=500) | wpf/MainWindow.OperationalCalendar.cs:171 | wpf/Supervisor/Worker.cs:20 | wpf/Testing/Integration/OperationalFeaturesTests.cs:256 |
| TestBackupPackage.cs | 10: public static void Create(string source, string destination, Guid owner, string scope); 34: public static BackupManifest Verify(string directory, Guid owner) | wpf/MainWindow.Backup.cs:60 | wpf/Supervisor/Worker.cs:123 | wpf/Testing/Integration/OperationalFeaturesTests.cs:274; wpf/Testing/Integration/OperationalFeaturesTests.cs:275; wpf/Testing/Integration/OperationalFeaturesTests.cs:276; wpf/Testing/Integration/ProfessionalOperationsTests.cs:40; wpf/Testing/Integration/Program.cs:165; wpf/Testing/Integration/Program.cs:302 |
| UntamedManagerArchiveInspector.cs | 33: public static UntamedManagerArchiveReport Inspect(string filename) | wpf/MainWindow.ManagerMigration.cs:29 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/ManagerMigrationArchiveTests.cs:173; wpf/Testing/Integration/ManagerMigrationArchiveTests.cs:199; wpf/Testing/Integration/ManagerMigrationArchiveTests.cs:209; wpf/Testing/StoreV1/Program.cs:226 |
| UpdateCenter.cs | 34: public static bool ValidRepository(string repo) =>; 37: public static Task<UpdateRelease?> CheckStableAsync(string repository,CancellationToken ct=default); 39: public static async Task<UpdateRelease?> CheckAsync(string repository,UpdateChannel channel,CancellationToken ct=default); 46: public static async Task<string> DownloadAndVerifyAsync(UpdateRelease release,; 84: public static void CheckAuthenticodePublisher(string path,string configuredThumbprint); 99: public static string StageUpdater(string installedHelperPath) | wpf/App.xaml.cs:49; wpf/App.xaml.cs:52; wpf/MainWindow.Supervision.cs:111; wpf/MainWindow.Updater.cs:29; wpf/MainWindow.Updater.cs:30 | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat |
| UpdateHistory.cs | 10: public static void Append(string phase,string version); 20: public static string ReadRecent() | wpf/App.xaml.cs:56; wpf/MainWindow.Supervision.cs:115; wpf/MainWindow.Updater.cs:89; wpf/MainWindow.Updater.cs:105; wpf/MainWindow.Updater.cs:131 | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat |
| UpdateRecovery.cs | 9: public static string? FindPending(string application,string updateRoot) | wpf/App.xaml.cs:49 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:1092; wpf/Testing/Integration/Program.cs:1093; wpf/Testing/Integration/Program.cs:1095 |
| VaultReadSnapshot.cs | 30: public static string LocalPath(string path,bool directory=false); 44: public byte[] Read(string path,int limit); 68: public static string EntryName(string name); 78: public static Dictionary<string,byte[]> Archive(byte[] bytes,string? unlistedReadme=null); 132: public static JsonDocument Json(IReadOnlyDictionary<string,byte[]> files,string name); 158: public static string Text(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&; 160: public void Dispose(){foreach(var handle in handles)handle.Dispose();} | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | wpf/Testing/StoreV1/Program.cs:197; wpf/Testing/StoreV1/Program.cs:209 |
| WanChangeTracker.cs | 23: public WanAssessment Observe(WanProbe probe); 89: public static async Task<WanProbe> CaptureAsync(HttpClient http,CancellationToken ct=default) | wpf/MainWindow.Recovery.cs:15; wpf/MainWindow.Recovery.cs:161 | Nincs fájlnév-alapú találat | wpf/Testing/Integration/Program.cs:863 |
| WatchdogDiagnostics.cs | 29: public WatchdogDiagnosis Observe(WatchdogEvidence e,int threshold=3); 70: public static string EventCode(WatchdogDiagnosis diagnosis) | Nincs fájlnév-alapú találat | wpf/Supervisor/Worker.cs:24; wpf/Supervisor/Worker.cs:93 | wpf/Testing/Integration/Program.cs:748 |
| WindowsProcessFamily.cs | 11: public ProcessCheck InspectFamily(LocalMap map) | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat | Nincs fájlnév-alapú találat |

## Teljes 139-fájlos szöveges forrásleltár

A módszer minden fájlt beolvasott a Git-blob összevetéshez és a forrásindexhez. A funkcionális következtetéseket a GAP csoportjai és a meglévő kutatási dokumentumok kódellenőrzései bontják ki.
Nem állítjuk, hogy a teljes program minden lehetséges kódútját futással auditáltuk. A preview/asset hiányokat az egyeztetési dokumentum jelzi.

| Fájl | Sor | PR #3-hoz viszonyított állapot |
| --- | --- | --- |
| README.md | 29 | FINAL_NEWLINE_ONLY |
| REQUIREMENTS-AUDIT-2026-11-03.md | 60 | FINAL_NEWLINE_ONLY |
| .github/workflows/build-untamed-wpf.yml | 201 | EXACT |
| wpf/App.xaml | 164 | FINAL_NEWLINE_ONLY |
| wpf/App.xaml.cs | 139 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.ApiAndPlugins.cs | 105 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.AsaInstall.cs | 40 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.AsaLogEvidence.cs | 69 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Backup.cs | 98 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.BackupOverview.cs | 52 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Bulk.cs | 106 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Cluster.cs | 422 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.ClusterVault.cs | 71 | EXACT |
| wpf/MainWindow.Complete.cs | 253 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.ConfigEditor.cs | 118 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.CurseForge.cs | 136 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Discord.cs | 134 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.KovaAudit.cs | 60 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.KovaStoreV1.cs | 51 | EXACT |
| wpf/MainWindow.ManagerMigration.cs | 92 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.ModInventory.cs | 43 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.ModState.cs | 122 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.ModUpdatesAndRcon.cs | 126 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Network.cs | 68 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.OfflineClusterBackup.cs | 91 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.OperationalCalendar.cs | 290 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.PluginInventory.cs | 41 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.PluginRollback.cs | 85 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.PluginZip.cs | 101 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Readiness.cs | 77 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Recovery.cs | 192 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Steam.cs | 47 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Supervision.cs | 148 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.Updater.cs | 138 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.xaml | 246 | FINAL_NEWLINE_ONLY |
| wpf/MainWindow.xaml.cs | 446 | FINAL_NEWLINE_ONLY |
| wpf/P0-STORE-V1-AUDIT-2026-11-04-HU.md | 93 | EXACT |
| wpf/PrepareArtwork.ps1 | 37 | FINAL_NEWLINE_ONLY |
| wpf/README.md | 25 | FINAL_NEWLINE_ONLY |
| wpf/RELEASE-1.3.1.md | 20 | FINAL_NEWLINE_ONLY |
| wpf/RELEASE-1.3.4.md | 31 | FINAL_NEWLINE_ONLY |
| wpf/RELEASE-1.3.5.md | 45 | FINAL_NEWLINE_ONLY |
| wpf/RELEASE-1.3.6.md | 35 | FINAL_NEWLINE_ONLY |
| wpf/RELEASE-1.3.7.md | 62 | FINAL_NEWLINE_ONLY |
| wpf/RELEASE-1.4.0.md | 43 | FINAL_NEWLINE_ONLY |
| wpf/RELEASE-1.5.0.md | 54 | FINAL_NEWLINE_ONLY |
| wpf/RELEASE-1.6.0.md | 69 | FINAL_NEWLINE_ONLY |
| wpf/RELEASE-1.6.1.md | 46 | FINAL_NEWLINE_ONLY |
| wpf/RESEARCH-2026-11-03-UNWIRED-FEATURES.md | 167 | FINAL_NEWLINE_ONLY |
| wpf/UntamedManager.csproj | 24 | FINAL_NEWLINE_ONLY |
| wpf/visual-patch-note.txt | 1 | FINAL_NEWLINE_ONLY |
| wpf/Core/AsaApiInspector.cs | 90 | FINAL_NEWLINE_ONLY |
| wpf/Core/AsaApiReleaseCatalog.cs | 124 | FINAL_NEWLINE_ONLY |
| wpf/Core/AsaHistoricalLogReader.cs | 149 | FINAL_NEWLINE_ONLY |
| wpf/Core/AsaInstallationInventory.cs | 90 | FINAL_NEWLINE_ONLY |
| wpf/Core/BackupManifest.cs | 55 | FINAL_NEWLINE_ONLY |
| wpf/Core/BackupRetention.cs | 101 | FINAL_NEWLINE_ONLY |
| wpf/Core/ClusterBackend.cs | 490 | FINAL_NEWLINE_ONLY |
| wpf/Core/ClusterVaultArchiveInspector.cs | 225 | EXACT |
| wpf/Core/CurseForgeCatalog.cs | 198 | FINAL_NEWLINE_ONLY |
| wpf/Core/DirectoryUpdateTransaction.cs | 106 | FINAL_NEWLINE_ONLY |
| wpf/Core/DiscordStatusPublisher.cs | 125 | FINAL_NEWLINE_ONLY |
| wpf/Core/EventJournal.cs | 48 | FINAL_NEWLINE_ONLY |
| wpf/Core/HistoricalLaunchProfileReader.cs | 124 | EXACT |
| wpf/Core/IsolatedFakeAsaSession.cs | 148 | FINAL_NEWLINE_ONLY |
| wpf/Core/KovaAuditBridge.cs | 196 | FINAL_NEWLINE_ONLY |
| wpf/Core/KovaFixtureAudit.cs | 45 | FINAL_NEWLINE_ONLY |
| wpf/Core/KovaProfileImporter.cs | 187 | FINAL_NEWLINE_ONLY |
| wpf/Core/KovaStoreV1CopyReader.cs | 80 | EXACT |
| wpf/Core/LocalBackupCatalog.cs | 61 | FINAL_NEWLINE_ONLY |
| wpf/Core/LocalBackupOverview.cs | 42 | FINAL_NEWLINE_ONLY |
| wpf/Core/LocalBackupWorkflow.cs | 137 | FINAL_NEWLINE_ONLY |
| wpf/Core/LocalContentOperations.cs | 83 | FINAL_NEWLINE_ONLY |
| wpf/Core/LocalModAssignments.cs | 60 | FINAL_NEWLINE_ONLY |
| wpf/Core/LocalPluginInventory.cs | 75 | FINAL_NEWLINE_ONLY |
| wpf/Core/LocalProfileTransfer.cs | 73 | FINAL_NEWLINE_ONLY |
| wpf/Core/LocalStoreProbe.cs | 35 | FINAL_NEWLINE_ONLY |
| wpf/Core/ModStateController.cs | 160 | FINAL_NEWLINE_ONLY |
| wpf/Core/ModUpdatePlanner.cs | 83 | FINAL_NEWLINE_ONLY |
| wpf/Core/NetworkLatencyDiagnostics.cs | 90 | FINAL_NEWLINE_ONLY |
| wpf/Core/OfflineClusterBackup.cs | 288 | FINAL_NEWLINE_ONLY |
| wpf/Core/OfflineSqlArchive.cs | 119 | FINAL_NEWLINE_ONLY |
| wpf/Core/OperationalScheduler.cs | 109 | FINAL_NEWLINE_ONLY |
| wpf/Core/PersistentScheduler.cs | 52 | FINAL_NEWLINE_ONLY |
| wpf/Core/PluginStateManager.cs | 115 | FINAL_NEWLINE_ONLY |
| wpf/Core/ProcessFamily.cs | 50 | FINAL_NEWLINE_ONLY |
| wpf/Core/RconCommandPolicy.cs | 119 | FINAL_NEWLINE_ONLY |
| wpf/Core/RconProtocol.cs | 121 | FINAL_NEWLINE_ONLY |
| wpf/Core/RecoverySimulation.cs | 187 | FINAL_NEWLINE_ONLY |
| wpf/Core/RecurringReadOnlyRunner.cs | 158 | FINAL_NEWLINE_ONLY |
| wpf/Core/ReleaseCapabilityGate.cs | 70 | FINAL_NEWLINE_ONLY |
| wpf/Core/ReleaseCatalog.cs | 68 | FINAL_NEWLINE_ONLY |
| wpf/Core/SafeConfigEditor.cs | 201 | FINAL_NEWLINE_ONLY |
| wpf/Core/SafeLifecycle.cs | 59 | FINAL_NEWLINE_ONLY |
| wpf/Core/SafePluginZip.cs | 335 | FINAL_NEWLINE_ONLY |
| wpf/Core/SafeSupportBundle.cs | 166 | FINAL_NEWLINE_ONLY |
| wpf/Core/SaveWorldValidator.cs | 51 | FINAL_NEWLINE_ONLY |
| wpf/Core/StatusCipher.cs | 40 | FINAL_NEWLINE_ONLY |
| wpf/Core/SteamCmdPreflight.cs | 38 | FINAL_NEWLINE_ONLY |
| wpf/Core/SteamCmdTestRunner.cs | 93 | FINAL_NEWLINE_ONLY |
| wpf/Core/SteamUpdateDryRun.cs | 80 | FINAL_NEWLINE_ONLY |
| wpf/Core/StoreV1Fingerprint.cs | 111 | EXACT |
| wpf/Core/SupervisorChannel.cs | 69 | FINAL_NEWLINE_ONLY |
| wpf/Core/SupervisorPolicy.cs | 27 | FINAL_NEWLINE_ONLY |
| wpf/Core/SyntheticRestore.cs | 69 | FINAL_NEWLINE_ONLY |
| wpf/Core/TelemetryHistory.cs | 79 | FINAL_NEWLINE_ONLY |
| wpf/Core/TestBackupPackage.cs | 58 | FINAL_NEWLINE_ONLY |
| wpf/Core/UntamedManagerArchiveInspector.cs | 279 | EXACT |
| wpf/Core/UpdateCenter.cs | 112 | FINAL_NEWLINE_ONLY |
| wpf/Core/UpdateHistory.cs | 26 | FINAL_NEWLINE_ONLY |
| wpf/Core/UpdateRecovery.cs | 26 | FINAL_NEWLINE_ONLY |
| wpf/Core/VaultReadSnapshot.cs | 161 | EXACT |
| wpf/Core/WanChangeTracker.cs | 117 | FINAL_NEWLINE_ONLY |
| wpf/Core/WatchdogDiagnostics.cs | 77 | FINAL_NEWLINE_ONLY |
| wpf/Core/WindowsProcessFamily.cs | 70 | FINAL_NEWLINE_ONLY |
| wpf/installer/UntamedManager.iss | 37 | FINAL_NEWLINE_ONLY |
| wpf/Supervisor/Program.cs | 26 | FINAL_NEWLINE_ONLY |
| wpf/Supervisor/UntamedSupervisor.csproj | 9 | FINAL_NEWLINE_ONLY |
| wpf/Supervisor/Worker.cs | 189 | FINAL_NEWLINE_ONLY |
| wpf/Testing/UpdaterE2E.ps1 | 75 | FINAL_NEWLINE_ONLY |
| wpf/Testing/FakeAsa/FakeAsa.csproj | 4 | FINAL_NEWLINE_ONLY |
| wpf/Testing/FakeAsa/Program.cs | 58 | FINAL_NEWLINE_ONLY |
| wpf/Testing/FakeSteamCMD/FakeSteamCMD.csproj | 10 | FINAL_NEWLINE_ONLY |
| wpf/Testing/FakeSteamCMD/Program.cs | 41 | FINAL_NEWLINE_ONLY |
| wpf/Testing/Integration/AsaHistoricalEvidenceTests.cs | 81 | FINAL_NEWLINE_ONLY |
| wpf/Testing/Integration/ClusterVaultTests.cs | 139 | EXACT |
| wpf/Testing/Integration/DiscordStatusTests.cs | 113 | FINAL_NEWLINE_ONLY |
| wpf/Testing/Integration/Integration.csproj | 5 | FINAL_NEWLINE_ONLY |
| wpf/Testing/Integration/ManagerMigrationArchiveTests.cs | 216 | FINAL_NEWLINE_ONLY |
| wpf/Testing/Integration/NetworkLatencyTests.cs | 47 | FINAL_NEWLINE_ONLY |
| wpf/Testing/Integration/OperationalFeaturesTests.cs | 371 | FINAL_NEWLINE_ONLY |
| wpf/Testing/Integration/ProfessionalOperationsTests.cs | 299 | EXACT |
| wpf/Testing/Integration/Program.cs | 1188 | FINAL_NEWLINE_ONLY |
| wpf/Testing/Integration/RconTransportTests.cs | 94 | FINAL_NEWLINE_ONLY |
| wpf/Testing/Integration/SafeSupportBundleTests.cs | 87 | FINAL_NEWLINE_ONLY |
| wpf/Testing/StoreV1/Program.cs | 246 | EXACT |
| wpf/Testing/StoreV1/StoreV1.csproj | 16 | EXACT |
| wpf/Updater/Program.cs | 195 | FINAL_NEWLINE_ONLY |
| wpf/Updater/UntamedUpdater.csproj | 13 | FINAL_NEWLINE_ONLY |
