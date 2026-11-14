using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UntamedDesktop;
using UntamedManager.Core;

// Real independent Windows child-process fixture. This short-circuits all
// ordinary native test setup; it never opens an ASA installation or Kova data.
if (args.Length > 0 && args[0] == LegacyStoreSaveEvidenceProcessTests.ChildSwitch)
    Environment.Exit(LegacyStoreSaveEvidenceProcessTests.RunChild(args));
if (args.Length > 0 && args[0] == OriginalKovaMutationProcessTests.ChildSwitch)
    Environment.Exit(OriginalKovaMutationProcessTests.RunChild(args));
if (args.Length > 0 && args[0] == MultiHostTempProcessLeaseTests.ChildSwitch)
    Environment.Exit(MultiHostTempProcessLeaseTests.RunChild(args));
if (args.Length > 0 && args[0] == MultiHostArchiveProcessLeaseTests.ChildSwitch)
    Environment.Exit(MultiHostArchiveProcessLeaseTests.RunChild(args));
if (args.Length > 0 && args[0] == OperationJournalIsolationTests.ChildSwitch)
    Environment.Exit(OperationJournalIsolationTests.RunChild(args));

static void Check(bool value, string reason)
{
    if (!value) throw new InvalidOperationException("NATIVE BRIDGE TEST FAILED: " + reason);
    Console.WriteLine("PASS native bridge: " + reason);
}
static async Task<JsonElement> Call(Func<string,Task<string>> handler,string method,
    object? args=null)
{
    var body=new Dictionary<string,object?>{
        ["id"]=Guid.NewGuid().ToString("D"),["method"]=method
    };
    if(args!=null)
    {
        var extra=JsonSerializer.SerializeToElement(args);
        foreach(var field in extra.EnumerateObject())body[field.Name]=field.Value.Clone();
    }
    string response=await handler(JsonSerializer.Serialize(body));
    using var document=JsonDocument.Parse(response);
    return document.RootElement.Clone();
}
static int FindFreeTestPort(bool udp,params int[] excluded)
{
    var network=System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
    var active=udp?network.GetActiveUdpListeners():network.GetActiveTcpListeners();
    var used=active.Select(endpoint=>endpoint.Port).ToHashSet();
    return Enumerable.Range(40000,25000).First(port=>
        !used.Contains(port)&&!excluded.Contains(port));
}
static void IsOk(JsonElement value,string context)
{
    bool ok=value.GetProperty("ok").GetBoolean();
    string code=value.TryGetProperty("error",out var error)?error.GetString()??"unknown":"unknown";
    Check(ok,ok?context:context+" ["+code+"]");
}
static bool IsError(JsonElement value,string code)=>
    !value.GetProperty("ok").GetBoolean() &&
    value.GetProperty("error").GetString()==code;

AsaInitialRconSettings.ValidateExistingAdminPassword("Old7!xx");
Check(true,"existing ASA INI credentials may be shorter than new credentials");
bool weakNewDenied=false;
try{AsaInitialRconSettings.ValidateAdminPassword("Old7!xx");}
catch(InvalidDataException){weakNewDenied=true;}
Check(weakNewDenied,"newly created ASA RCON passwords still require twelve characters");

Check(SteamFreshInstall.IsTransientMissingSteamConfiguration(
      "ERROR! Failed to install app '2430930' (Missing configuration)"),
    "SteamCMD first-run missing configuration triggers bounded retry");
Check(!SteamFreshInstall.IsTransientMissingSteamConfiguration(
      "ERROR! Failed to install app '2430930' (Disk write failure)") &&
      !SteamFreshInstall.IsTransientMissingSteamConfiguration(
      "Failed to install app '999999' (Missing configuration)"),
    "SteamCMD never retries unrelated app IDs or disk failures");

// Valve's first-run SteamCMD self-update can exit nonzero before app_update.
Check(SteamFreshInstall.NeedsSteamBootstrapRestart(8,
      "Update complete, launching SteamCMD"),
    "SteamCMD fresh self-update permits one bootstrap retry");
Check(SteamFreshInstall.NeedsSteamBootstrapRestart(8,
      "Restarting SteamCMD"),
    "SteamCMD alternative first-run restart message recognized");
Check(!SteamFreshInstall.NeedsSteamBootstrapRestart(0,
      "Update complete, launching SteamCMD") &&
      !SteamFreshInstall.NeedsSteamBootstrapRestart(1,
      "Failed to install app '2430930' (Missing configuration)"),
    "SteamCMD bootstrap does not retry unrelated failures or successful exit");
Check(SteamStoppedMapUpdater.ShouldRetryMissingConfiguration(0,8,
      "Failed to install app '2430930' (Missing configuration)",false) &&
      SteamStoppedMapUpdater.ShouldRetryMissingConfiguration(1,8,
      "ERROR! Failed to install app '2430930' (Missing configuration)",false) &&
      !SteamStoppedMapUpdater.ShouldRetryMissingConfiguration(2,8,
      "Failed to install app '2430930' (Missing configuration)",false),
    "manually approved SteamCMD update permits only two exact first-run configuration retries");
Check(!SteamStoppedMapUpdater.ShouldRetryMissingConfiguration(0,0,
      "Failed to install app '2430930' (Missing configuration)",false) &&
      !SteamStoppedMapUpdater.ShouldRetryMissingConfiguration(0,8,
      "Failed to install app '2430930' (Disk write failure)",false) &&
      !SteamStoppedMapUpdater.ShouldRetryMissingConfiguration(0,8,
      "Failed to install app '999999' (Missing configuration)",false) &&
      !SteamStoppedMapUpdater.ShouldRetryMissingConfiguration(0,8,
      "Failed to install app '2430930' (Missing configuration)",true),
    "manual updater rejects retries after success, for unrelated failures and in synthetic CI");

// API Loader is optional: direct ASA with an installed but unused Loader
// must remain observable, while persisted Loader-child relationships remain
// guarded against misclassification or PID reuse.
{
    var time=DateTimeOffset.UtcNow;
    var direct=new ObservedProcess(44117,0,time,
        @"C:\ASA\ShooterGame\Binaries\Win64\ArkAscendedServer.exe",
        "TheIsland_WP?listen -Port=7777 -QueryPort=27015");
    var saved=new ProcessIdentity{
        Pid=direct.Pid,StartedUtc=direct.StartedUtc,
        ExecutablePath=direct.ExecutablePath,Arguments=direct.Arguments};
    var oldLoader=new ProcessIdentity{
        Pid=44116,StartedUtc=time.AddSeconds(-1),
        ExecutablePath=@"C:\ASA\ShooterGame\Binaries\Win64\AsaApiLoader.exe",
        Arguments=direct.Arguments};
    Check(ProcessFamily.IsVerifiedDirectServer(direct,null,null,false,
          direct.ExecutablePath,direct.Arguments),
        "direct ASA without API Loader history remains recognizable");
    Check(ProcessFamily.IsVerifiedDirectServer(direct,saved,null,false,
          direct.ExecutablePath,direct.Arguments),
        "direct ASA exact persisted process may reattach without Loader");
    Check(!ProcessFamily.IsVerifiedDirectServer(direct,saved,oldLoader,false,
          direct.ExecutablePath,direct.Arguments),
        "previous Loader child cannot be downgraded to direct ASA");
    Check(!ProcessFamily.IsVerifiedDirectServer(direct,saved,null,true,
          direct.ExecutablePath,direct.Arguments),
        "API-enabled map cannot bypass Loader parent check");
    Check(!ProcessFamily.IsVerifiedDirectServer(direct,new ProcessIdentity{
          Pid=saved.Pid,StartedUtc=time.AddMinutes(-2),
          ExecutablePath=saved.ExecutablePath,Arguments=saved.Arguments
          },null,false,
          direct.ExecutablePath,direct.Arguments),
        "direct ASA rejects reused PID with changed process start time");
    Check(!ProcessFamily.IsVerifiedDirectServer(direct,saved,null,false,
          direct.ExecutablePath,direct.Arguments+" -changed"),
        "direct ASA rejects unmatched server arguments");
}

Check(FreshStoreApiPairObserver.StartupObservationWindow==TimeSpan.FromMinutes(10),
    "ASA API Loader child reattach observation matches original ten-minute startup window");

// Two owned map profiles may share the same ASA binaries. The exact
// registered sibling command may be ignored, but an unknown or duplicate
// command must never be interpreted as a verified stopped process.
{
    string exe=@"C:\\ASA\\ShooterGame\\Binaries\\Win64\\ArkAscendedServer.exe";
    string loader=@"C:\\ASA\\ShooterGame\\Binaries\\Win64\\AsaApiLoader.exe";
    var island=new LocalMap{Id=Guid.NewGuid(),Name="Island",
        ExecutablePath=exe,Arguments="TheIsland_WP?listen -Port=7777 -QueryPort=27015"};
    var center=new LocalMap{Id=Guid.NewGuid(),Name="Center",
        ExecutablePath=exe,Arguments="TheCenter_WP?listen -Port=7817 -QueryPort=27055",
        StartWithAsaApi=true,ApiLoaderPath=loader,
        ApiLoaderArguments="TheCenter_WP?listen -Port=7817 -QueryPort=27055"};
    var peers=new[]{island,center};
    Check(WindowsProcessInspector.MatchesOtherRegisteredMap(island,exe,
          "\"" + exe + "\" "+center.Arguments,peers),
        "a known second map sharing the ASA EXE does not block first map observation");
    Check(WindowsProcessInspector.MatchesOtherRegisteredMap(island,loader,
          "\"" + loader + "\" "+center.ApiLoaderArguments,peers),
        "a known API Loader sibling with different map ports is recognized");
    Check(!WindowsProcessInspector.MatchesOtherRegisteredMap(island,exe,
          "\"" + exe + "\" Ragnarok_WP?listen -Port=9001",peers),
        "an unregistered ASA command remains ambiguous and cannot authorize launch");
    Check(!WindowsProcessInspector.MatchesOtherRegisteredMap(island,exe,
          "\"" + exe + "\" "+island.Arguments,peers),
        "current map process is never mistaken for a sibling");
    Check(!WindowsProcessInspector.MatchesOtherRegisteredMap(island,exe,
          "\"" + exe + "\" "+center.Arguments,new[]{island,center,new LocalMap{Id=Guid.NewGuid(),ExecutablePath=exe,Arguments=center.Arguments}}),
        "duplicate known ownership remains ambiguous");
}

string root=Path.Combine(Path.GetTempPath(),
    "Untamed-Obsidian-Native-Bridge-Tests-"+Guid.NewGuid().ToString("N"));
using var operationJournalScope=UntamedOperationCoordinator.UseIsolatedRootForTests(
    Path.Combine(root,"operation-journals"));
IDisposable? testSecretProtector=null;
try
{
    const string probe="ISOLATED-DPAPI-PROBE";
    if(SecureStore.Unprotect(SecureStore.Protect(probe))!=probe)
        throw new CryptographicException("DPAPI round-trip failed");
}
catch(CryptographicException)
{
    // The sandbox account has no loaded Windows DPAPI profile. Keep tests
    // isolated with reversible synthetic ciphertext; production remains DPAPI.
    testSecretProtector=SecureStore.UseTestProtectorForTests(
        plain=>"TEST-PROTECTED:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)),
        encoded=>encoded.StartsWith("TEST-PROTECTED:",StringComparison.Ordinal)
            ?Encoding.UTF8.GetString(Convert.FromBase64String(encoded[15..]))
            :throw new CryptographicException("Unexpected test secret format"));
}
using var testSecretProtectorScope=testSecretProtector;
string profile=Path.Combine(root,"profile");
string installation=Path.Combine(root,"asa-install");
Directory.CreateDirectory(installation);
await using var cluster=new ClusterService("obsidian-ci",profile,
    _=>new ProcessCheck(false,"Confirmed stopped synthetic test only",null,true));
var map=new LocalMap{
    Id=Guid.NewGuid(),Name="The Island",InstallationPath=installation,
    Arguments="-mods=123456",ExecutablePath=Path.Combine(installation,
        "ShooterGame","Binaries","Win64","ArkAscendedServer.exe")
};
cluster.AddMap(map);
await LocalAsaApiModeTests.Run(root);
await ManualOnlySteamScheduleTests.Run(root);
 await HistoricalAsaApiModeTests.Run(root);
 SteamProtectedFileCoverageTests.Run(root);
 await HistoricalLocalConfigGuardTests.Run(root);
 await HistoricalPluginZipTests.Run(root);
 await HistoricalPluginToggleTests.Run(root);
 await HistoricalModCopyTests.Run(root);
 await HistoricalModProfileTests.Run(root);
// The travel capture endpoint must explain an incomplete Manager registry,
// not silently collapse it into a generic evidence error.
var incompleteFiveCapture=await Call(
    new DesktopStoreTravelEvidenceBridge(cluster).HandleAsync,"travel.capture");
Check(IsError(incompleteFiveCapture,"UM-TRAVEL-REQUIRES-FIVE-OWNED-MAPS"),
    "travel capture reports the exact five-local-map prerequisite without reading ASA files");
await SteamPublicBuildParsingTests.RunAsync(root);
NativeWebViewDispatchContractTests.Run();
LocalRunningAsaDiscoveryTests.Run();
LocalAsaLaunchFactsTests.Run();
await RunningAsaRegistrationTests.Run(root);
await ClusterRegistryTests.Run(root);
await LegacyStorePhysicalTransferTests.Run(root);
LegacyStoreOriginalLinkTests.Run(root);
OriginalStoreLiveBatchEvidenceTests.Run(root);
await OriginalStoreLiveReconcileTests.Run(root);
await MapScopedNativeLifecycleJobsTests.Run(root);
OperationJournalInventoryTests.Run(root);
await OperationJournalIsolationTests.Run(root);
await OriginalStoreApiRconAttachTests.Run(root);
await OriginalKovaDirectImportTests.Run(root);
await LegacyStoreSaveEvidenceTests.Run(root);
await HistoricalStoreWorldBackupTests.Run(root);
await OriginalStoreFiveMapBackupTests.Run(root);
MultiHostOfflineCatalogAssemblerTests.Run(root);
await MultiHostArchiveProcessLeaseTests.Run(root);
await LegacyStoreSaveReviewBridgeTests.Run(root);
await LegacyStoreSaveEvidenceProcessTests.Run(root);
await OriginalKovaMutationProcessTests.Run(root);
await EmbeddedStorePartialIndexTests.Run(root);
await LegacyStoreSaveReceiptPairTests.Run(root);
await FreshStoreEnrollmentTests.Run(root);
await OwnedAsaKillBridgeTests.Run(root);
// A real SQLite .ark COPY fixture tests the actual metadata reader, not a
// mocked parser. No server process, production Store writer, or network share.
{
    string lab=Path.Combine(root,"offline-embedded-lab");
    Directory.CreateDirectory(lab);
    string world=Path.Combine(lab,"TheIsland_WP.ark");

    static byte[] PackedEntry(string className)
    {
        byte[] raw=new byte[128];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0,4),7);
        byte[] name=Encoding.ASCII.GetBytes(className);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(32,4),name.Length);
        name.CopyTo(raw.AsSpan(36));
        return raw;
    }
    static void PackedSection(BinaryWriter writer,string className,ulong key,bool superseded)
    {
        byte[] raw=PackedEntry(className);
        writer.Write(raw.Length); // dataSize
        writer.Write(superseded?2:1); // count
        writer.Write(raw);
        writer.Write(key);
        writer.Write(0); // offset into section
        writer.Write(raw.Length);
        if(superseded)
        {
            writer.Write(0UL); // append-only superseded index key
            writer.Write(0);
            writer.Write(raw.Length);
        }
    }
    byte[] blob;
    using(var bytes=new MemoryStream())
    using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true))
    {
        writer.Write(1); // known embedded-store format
        PackedSection(writer,"PrimalTribeData",1001,true);
        PackedSection(writer,"PrimalPlayerData",1002,false);
        writer.Write(0);writer.Write(0); // format terminator
        writer.Flush();
        blob=bytes.ToArray();
    }
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{
            DataSource=world,Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,Pooling=false
        }.ToString()))
    {
        db.Open();
        using var create=db.CreateCommand();
        create.CommandText="CREATE TABLE custom(key TEXT PRIMARY KEY,value BLOB)";
        create.ExecuteNonQuery();
        using var insert=db.CreateCommand();
        insert.CommandText="INSERT INTO custom(key,value) VALUES($key,$value)";
        insert.Parameters.AddWithValue("$key","GameModeCustomBytes");
        insert.Parameters.AddWithValue("$value",blob);
        insert.ExecuteNonQuery();
    }
    string shaBefore=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(world)));
    var report=EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(world);
    Check(report.State=="INDEX_SCANNED" && report.ProfileCandidates==1&&
        report.TribeCandidates==1&&report.SupersededEntries==1&&
        report.Sections==2&&report.IndexBoundsVerified&&report.SourceUnchanged&&
        !report.GameplayVerified&&!report.CharacterTravelVerified,
        "offline SQLite -usestore inspector identifies one tribe and one profile without gameplay certification");
    using(var bridge=new DesktopEmbeddedStoreBridge(()=>world))
    {
        var ok=await Call(bridge.HandleAsync,"store.inspectCopy");
        IsOk(ok,"native offline world-copy picker invokes actual indexed Store reader");
        var data=ok.GetProperty("data");
        Check(data.GetProperty("profileCandidates").GetInt32()==1&&
            data.GetProperty("tribeCandidates").GetInt32()==1&&
            !data.GetProperty("characterTravelVerified").GetBoolean()&&
            !ok.ToString().Contains(lab,StringComparison.OrdinalIgnoreCase),
            "Obsidian renderer receives only bounded record counts, not local paths or survivor bytes");
        var malicious=await Call(bridge.HandleAsync,"store.inspectCopy",
            new{path=world});
        Check(IsError(malicious,"UM-STORE-COPY-PATH-DENIED"),
            "web renderer cannot inject arbitrary filesystem source path");
    }
    Check(shaBefore==Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(world))),
        "embedded Store inspection leaves world SQLite bytes unchanged");
    // An invalid index must never become a positive character or tribe proof.
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{
            DataSource=world,Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,Pooling=false
        }.ToString()))
    {
        db.Open();
        using var broken=db.CreateCommand();
        broken.CommandText="UPDATE custom SET value=$blob WHERE key='GameModeCustomBytes'";
        broken.Parameters.AddWithValue("$blob",new byte[]{1,0,0,0,255,255,255,127,1,0,0,0});
        broken.ExecuteNonQuery();
    }
    bool invalidDenied=false;
    try{EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(world);}
    catch(InvalidDataException){invalidDenied=true;}
    Check(invalidDenied,"truncated embedded index is rejected without inventing survivors");
}

// The shipping desktop owns no SQL database or MariaDB GUI. Historical WPF sources
// must never be linked into or activated by the deployed app.
{
    var application=typeof(DesktopReadOnlyBridge).Assembly;
    foreach(string forbidden in new[]{
        "UntamedManager.Core.MariaDbCompatibility",
        "UntamedManager.Core.MariaDbDumpAdapter",
        "UntamedManager.Core.OfflineSqlArchive",
        "UntamedManager.Core.OfflineClusterBackup",
        "UntamedManager.Core.ClusterRestoreCoordinator",
        "UntamedManager.Core.DemoWorkspace",
        "UntamedManager.Core.IsolatedFakeAsaSession",
        "UntamedManager.Core.SyntheticRestore",
        "UntamedManager.Core.SteamCmdTestRunner",
        "UntamedManager.Core.RecoverySimulator",
        "UntamedManager.Core.VaultFixtureAudit"})
        Check(application.GetType(forbidden,false)==null,
            "shipping Obsidian desktop excludes SQL, legacy WPF demos and fake test harness: "+forbidden);
}
// The real dashboard once compared N-format LAN IDs with D-format owned map
// IDs and silently hid maintenance / Store protection / RCON flags.
Check(cluster.LocalStatus().Maps.Single().Id==map.Id.ToString("D") &&
      DesktopReadOnlyBridge.MatchesPublishedMapId(map.Id,map.Id.ToString("D")) &&
      DesktopReadOnlyBridge.MatchesPublishedMapId(map.Id,map.Id.ToString("N")) &&
      !DesktopReadOnlyBridge.MatchesPublishedMapId(map.Id,Guid.NewGuid().ToString("D")),
    "dashboard map ownership resolves D/N GUID formats but never a foreign map");
// Original Store-v1 process reattach must validate the entire active
// source launch grammar. It may classify an OS process but NEVER own,
// start, stop or write the original Store session.
{
    string observedExe=Path.Combine(root,"physical-original-process-grammar",
        "ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
    var historicCommandMap=new LocalMap{
        Id=Guid.NewGuid(),Name="Aberration",
        InstallationPath=Path.Combine(root,"physical-original-process-grammar"),
        ExecutablePath=observedExe,HistoricalStoreProtected=true,
        HistoricalMapKey="Aberration_WP",GamePort=7797,QueryPort=27015,
        HistoricalEnabledMods=new List<string>{"123456","789012"},
        HistoricalPassiveMods=new List<string>{"123456"}
    };
    var exactArgs=LegacyStoreV1ActiveLaunch.Build(
        new LegacyStoreV1ActiveLaunch.Input("Aberration_WP",7797,27015,
            historicCommandMap.HistoricalEnabledMods,
            historicCommandMap.HistoricalPassiveMods,
            VaultProtocol.ClusterId,@"\\original-test\store","active","")
    ).Arguments;
    string originalCommand="\""+observedExe+"\" "+string.Join(" ",exactArgs);
    bool Match(string command)=>LegacyStoreV1ProcessObserver.MatchesOriginalActiveCommand(
        command,observedExe,historicCommandMap);
    Check(Match(originalCommand),
        "historical physical process accepts source-compatible ACTIVE-only canonical launch string");
    Check(!Match(originalCommand+" -converttostore") &&
          !Match(originalCommand+" -convertfromstore") &&
          !Match(originalCommand+" -usestore") &&
          !Match(originalCommand+" -UnknownExtraFlag") &&
          !Match(originalCommand.Replace("-Port=7797","-Port=7798",StringComparison.Ordinal)) &&
          !Match(originalCommand.Replace("-clusterid=KovaCluster",
              "-clusterid=UntamedKingdom",StringComparison.Ordinal)) &&
          !Match(originalCommand.Replace("-mods=123456,789012",
              "-mods=789012,123456",StringComparison.Ordinal)),
        "historical observer denies reconversion, repeated Store flag, unknown switches, changed ports, cluster ID or mod order");
    Check(!LegacyStoreV1ProcessObserver.MatchesOriginalActiveCommand(
        originalCommand,Path.Combine(root,"different","ArkAscendedServer.exe"),
        historicCommandMap) &&
        !Match(originalCommand.Replace("Aberration_WP?listen","Aberration_WP")),
        "historical observer requires exact executable ownership and ?listen map switch");
    string otherMapCommand=originalCommand.Replace(
        "Aberration_WP?listen","TheIsland_WP?listen",StringComparison.Ordinal);
    Check(LegacyStoreV1ProcessObserver.DefinitelyDifferentMap(
              otherMapCommand,observedExe,"Aberration_WP") &&
          !LegacyStoreV1ProcessObserver.DefinitelyDifferentMap(
              originalCommand,observedExe,"Aberration_WP") &&
          !LegacyStoreV1ProcessObserver.DefinitelyDifferentMap(
              originalCommand.Replace("-Port=7797","-Port=7798",StringComparison.Ordinal),
              observedExe,"Aberration_WP"),
        "five original maps sharing one ASA EXE are distinguished by their canonical first map token, while corrupt same-map ports stay uncertain");
    Directory.CreateDirectory(Path.GetDirectoryName(observedExe)!);
    File.WriteAllText(observedExe,"SYNTHETIC TEST ONLY NOT AN ASA EXECUTABLE");
    var physical=LegacyStoreV1ProcessObserver.Observe(historicCommandMap);
    var registeredPhysical=LegacyStoreV1ProcessObserver.Observe(
        historicCommandMap,new[]{historicCommandMap});
    Check(!physical.Running&&!physical.ConfirmedStopped&&physical.Identity==null &&
          !registeredPhysical.Running&&!registeredPhysical.ConfirmedStopped&&
          registeredPhysical.Identity==null,
        "both standalone and registered original Store observers fail closed without process evidence");
    var markerRoot=Path.Combine(root,"synthetic-store-marker-evidence");
    Directory.CreateDirectory(markerRoot);
    Check(LegacyStoreV1SharedMarkerProbe.IsDefinitelyAbsent(markerRoot,"mutation.lock"),
        "truly missing original coordination marker is recognized read-only");
    string mutationFile=Path.Combine(markerRoot,"mutation.lock");
    File.WriteAllText(mutationFile,"SYNTHETIC-DO-NOT-UNLOCK");
    Check(!LegacyStoreV1SharedMarkerProbe.IsDefinitelyAbsent(markerRoot,"mutation.lock"),
        "existing durable Store mutation lock always blocks live readiness");
    Directory.CreateDirectory(Path.Combine(markerRoot,"running.json"));
    Check(!LegacyStoreV1SharedMarkerProbe.IsDefinitelyAbsent(markerRoot,"running.json"),
        "even a malformed directory in place of a running marker blocks live readiness");
    bool unknownMarkerRefused=false;
    try{LegacyStoreV1SharedMarkerProbe.IsDefinitelyAbsent(markerRoot,"not-allowed.json");}
    catch(InvalidDataException){unknownMarkerRefused=true;}
    Check(unknownMarkerRefused,
        "shared Store marker probe rejects an arbitrary injected SMB name");
    Directory.Delete(Path.Combine(markerRoot,"running.json"));
    File.Delete(mutationFile);
    Directory.Delete(markerRoot);
    bool missingShareDenied=false;
    try{LegacyStoreV1SharedMarkerProbe.IsDefinitelyAbsent(markerRoot,"mutation.lock");}
    catch(InvalidDataException){missingShareDenied=true;}
    Check(missingShareDenied,
        "missing/inaccessible coordination parent is not interpreted as an absent Store lock");
}
// A live historical Store-v1 server must NOT appear stopped merely
// because its START command is locked. Monitoring is READ-ONLY and cannot
// confer Start/Stop, Store write or remote lifecycle authority.
{
    string historicRoot=Path.Combine(root,"historical-status-only");
    int observations=0;
    // The test runner itself is the safe synthetic observed PID. This is
    // NOT a fake claim of ASA game readiness; only local OS identity is used.
    using var observedSelf=System.Diagnostics.Process.GetCurrentProcess();
    var identity=new ProcessIdentity{
        Pid=observedSelf.Id,
        StartedUtc=new DateTimeOffset(observedSelf.StartTime.ToUniversalTime()),
        ExecutablePath=Environment.ProcessPath??"",Arguments="SYNTHETIC-PROCESS-TEST"
    };
    await using var history=new ClusterService("historical-observer",
        Path.Combine(historicRoot,"profile"),
        _=>{
            Interlocked.Increment(ref observations);
            return new ProcessCheck(true,"Existing ASA process observed (synthetic)",identity);
        });
    var historic=new LocalMap {
        Id=Guid.NewGuid(),Name="Aberration",InstallationPath=historicRoot,
        Arguments="Aberration_WP?listen -Port=7797"
    };
    history.AddMap(historic);
    historic.HistoricalStoreProtected=true;
    var status=history.LocalStatus().Maps.Single();
    int initialReads=observations;
    status.Running=false; // External UI mutating a snapshot must not taint the cache.
    var cached=history.LocalStatus().Maps.Single();
    Check(cached.Running && observations==initialReads,
        "LAN dashboard snapshot cache is defensive-copy and avoids repeated WMI scans");
    Check(cached.Running &&
          status.Status.Contains("Store-v1 védett",StringComparison.Ordinal) &&
          status.Status.Contains("Existing ASA process observed",StringComparison.Ordinal) &&
          !history.Check(historic).Running,
        "historical Store map is observed as running but remains lifecycle-locked");
    var historicalOps=new DesktopOperationsCenterBridge(history);
    var observed=await Call(historicalOps.HandleAsync,"operations.map",
        new{mapId=historic.Id});
    IsOk(observed,"native Operations Center accepts protected Store map observation");
    var observedMap=observed.GetProperty("data").GetProperty("maps")[0];
    Check(observedMap.GetProperty("running").GetBoolean() &&
          observedMap.GetProperty("pid").GetInt32()==identity.Pid &&
          observedMap.GetProperty("memoryVerified").GetBoolean() &&
          observedMap.GetProperty("storeProtected").GetBoolean() &&
          !observedMap.GetProperty("lifecycleActionsVerified").GetBoolean() &&
          observedMap.GetProperty("evidence").GetString()==
            "HISTORICAL_STORE_V1_PROCESS_OBSERVATION_NO_LIFECYCLE_RIGHTS" &&
          !history.Check(historic).Running,
        "protected historical map shows verified physical PID/RAM while actual Start/Stop remains blocked");
    var unauthorized=await Call(historicalOps.HandleAsync,"operations.start",
        new{mapId=historic.Id});
    Check(IsError(unauthorized,"UM-METHOD-DENIED"),
        "historical status never becomes a command bridge despite running PID");
}
try
{
    // WebView cannot bypass native approval or introduce arbitrary API CORE
    // rollback paths. A newly registered but unverified ASA is not writable.
    {
        using var recoveryHttp=new HttpClient();
        using var recoveryBridge=new DesktopAsaApiCoreBridge(cluster,recoveryHttp);
        Check(IsError(await Call(recoveryBridge.HandleAsync,
                "api.coreRollbackApply",new {token=Guid.NewGuid().ToString("D"),confirm=true}),
                "UM-API-CORE-ROLLBACK-APPROVAL"),
            "CORE rollback denies invented or replayed approval token");
        Check(IsError(await Call(recoveryBridge.HandleAsync,
                "api.coreRollbackCandidates",new {mapId=map.Id.ToString("D")}),
                "UM-API-CORE-ROLLBACK-NOT-STOPPED"),
            "CORE rollback candidate list refuses unverified ASA binary ownership");
        Check(IsError(await Call(recoveryBridge.HandleAsync,
                "api.coreRollbackPreview",new {
                    mapId=map.Id.ToString("D"),transactionId=Guid.NewGuid().ToString("N")}),
                "UM-API-CORE-ROLLBACK-NOT-STOPPED"),
            "CORE rollback preview denies unverified installation without granting an approval");
    }

    // Real-world save discovery is shared by backup/restore/travel/RCON.
    // Custom ASA map names are NOT tied to a ten-map hardcoded list.
    {
        string alternateRoot=Path.Combine(root,"custom-save-layout");
        var customMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Synthetic Custom Biome",
            InstallationPath=alternateRoot,
            Arguments="CustomBiome_WP?listen?AltSaveDirectoryName=AlternativeSlot"
        };
        var layout=AsaSaveLayoutResolver.Resolve(customMap);
        Check(layout.MapToken=="CustomBiome_WP"&&
              layout.SaveDirectoryName=="AlternativeSlot"&&
              layout.ExplicitAlternate&&
              layout.WorldFile.EndsWith(
                  Path.Combine("AlternativeSlot","CustomBiome_WP.ark"),
                  StringComparison.OrdinalIgnoreCase),
            "custom ASA mod map resolves AltSaveDirectoryName without hardcoding");
        Directory.CreateDirectory(layout.WorldDirectory);
        File.WriteAllText(layout.WorldFile,"ORIGINAL SYNTHETIC CUSTOM ASA WORLD");
        string customProfile=Path.Combine(root,"custom-save-profile");
        Func<ProcessCheck> stopped=()=>
            new ProcessCheck(false,"synthetic confirmed stopped",null,true);
        var customBackup=UntamedWorldBackup.Create(customMap,customProfile,stopped);
        Check(UntamedWorldBackup.Verify(customMap,customProfile,customBackup.Id).Files==1,
            "custom map backup copies and SHA-verifies resolved alternate save directory");
        File.WriteAllText(layout.WorldFile,"CHANGED SYNTHETIC WORLD");
        var restoredCustom=UntamedWorldBackup.Restore(
            customMap,customProfile,customBackup.Id,stopped);
        Check(restoredCustom.Restored&&restoredCustom.OldWorldRetained&&
              File.ReadAllText(layout.WorldFile)=="ORIGINAL SYNTHETIC CUSTOM ASA WORLD",
            "custom map restores exactly the verified alternate-folder world bytes");
        var duplicate=new LocalMap{
            Id=Guid.NewGuid(),Name="Another synthetic map",
            InstallationPath=alternateRoot,
            Arguments="AnotherMap_WP?listen?AltSaveDirectoryName=AlternativeSlot"
        };
        bool overlapRefused=false;
        try{AsaSaveLayoutResolver.DemandUniqueOwner(customMap,new[]{customMap,duplicate});}
        catch(InvalidOperationException){overlapRefused=true;}
        Check(overlapRefused,"two maps cannot claim the same alternate save folder");
        var traversal=new LocalMap{
            Id=Guid.NewGuid(),Name="bad custom",
            InstallationPath=alternateRoot,
            Arguments="CustomBiome_WP?listen?AltSaveDirectoryName=../other"
        };
        bool traversalRefused=false;
        try{AsaSaveLayoutResolver.Resolve(traversal);}
        catch(InvalidDataException){traversalRefused=true;}
        Check(traversalRefused,"malformed or path-traversing ASA alternate save folder is refused");
        var mismatch=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",InstallationPath=alternateRoot,
            Arguments="Ragnarok_WP?listen"
        };
        bool conflictingMapRefused=false;
        try{AsaSaveLayoutResolver.Resolve(mismatch);}
        catch(InvalidDataException){conflictingMapRefused=true;}
        Check(conflictingMapRefused,"registered map name cannot silently save into a different map token");
    }

    // Store-v1 two-host writer identities are exclusive, not just ten names.
    // A valid local + remote DONE snapshot must contain two distinct device
    // identities, ten unique map GUIDs and exact per-peer selected membership.
    {
        string localDevice=Guid.NewGuid().ToString("D");
        string remoteDevice=Guid.NewGuid().ToString("D");
        string[] localMaps=Enumerable.Range(0,5)
            .Select(_=>Guid.NewGuid().ToString("D")).ToArray();
        string[] remoteMaps=Enumerable.Range(0,5)
            .Select(_=>Guid.NewGuid().ToString("D")).ToArray();
        var valid=new[]{
            new LegacyStoreV1PeerTopology.Peer(localDevice,localMaps,
                Enumerable.Range(0,5).Select(i=>"Map_A_"+i).ToArray(),localMaps),
            new LegacyStoreV1PeerTopology.Peer(remoteDevice,remoteMaps,
                Enumerable.Range(0,5).Select(i=>"Map_B_"+i).ToArray(),remoteMaps)
        };
        Check(LegacyStoreV1PeerTopology.IsOriginalSourceUuid(localDevice)&&
              LegacyStoreV1PeerTopology.IsOriginalSourceUuid(remoteDevice),
            "original Store device/session UUID accepts source lowercase canonical GUID");
        Check(!LegacyStoreV1PeerTopology.IsOriginalSourceUuid(localDevice.ToUpperInvariant())&&
              !LegacyStoreV1PeerTopology.IsOriginalSourceUuid("{"+localDevice+"}")&&
              !LegacyStoreV1PeerTopology.IsOriginalSourceUuid(" "+localDevice)&&
              !LegacyStoreV1PeerTopology.IsOriginalSourceUuid(localDevice.Replace("-",""))&&
              !LegacyStoreV1PeerTopology.IsOriginalSourceUuid(Guid.Empty.ToString("D")),
            "uppercase, braced, padded, empty or unhyphenated Store device/session IDs must be denied");
        LegacyStoreV1PeerTopology.Validate(localDevice,valid);
        Check(true,"two unique original Store-v1 peers with five disjoint owned and selected maps pass read-only topology verification");

        // Original source preserves the ordered FIVE GUID selections in the
        // local durable shared-store-lock-v1.json AND the matching DONE peer.
        // Membership equality alone is insufficient for writer/session reuse.
        var durableLockSelection=JsonSerializer.SerializeToElement(localMaps);
        LegacyStoreV1PeerTopology.RequireOriginalLocalSelection(
            durableLockSelection,valid[0].SelectedIds);
        Check(true,"original durable local lock and DONE peer have exact ordered writer map selections");
        void RejectLocalSelection(string[] candidate,string description)
        {
            bool denied=false;
            try
            {
                LegacyStoreV1PeerTopology.RequireOriginalLocalSelection(
                    JsonSerializer.SerializeToElement(candidate),valid[0].SelectedIds);
            }
            catch(InvalidDataException){denied=true;}
            Check(denied,description);
        }
        RejectLocalSelection(localMaps.Reverse().ToArray(),
            "reordered local lock map selections cannot silently reuse the original completed DONE peer session");
        RejectLocalSelection(new[]{localMaps[0],localMaps[0],
            localMaps[2],localMaps[3],localMaps[4]},
            "duplicated local lock selection is rejected before any original writer lifecycle");
        RejectLocalSelection(new[]{remoteMaps[0],localMaps[1],
            localMaps[2],localMaps[3],localMaps[4]},
            "foreign peer map cannot enter the local original durable writer lock");
        RejectLocalSelection(localMaps[..4],
            "an incomplete original local selected map list blocks ACTIVE Store operations");
        RejectLocalSelection(new[]{"not-a-guid",localMaps[1],
            localMaps[2],localMaps[3],localMaps[4]},
            "malformed selected local map GUID is never converted into writer authority");
        RejectLocalSelection(new[]{localMaps[0].ToUpperInvariant(),
            localMaps[1],localMaps[2],localMaps[3],localMaps[4]},
            "original source ordered JSON selection digest rejects silently normalized GUID casing");
        bool missingLocalSelectionDenied=false;
        try
        {
            LegacyStoreV1PeerTopology.RequireOriginalLocalSelection(
                JsonSerializer.SerializeToElement(new{invalid=true}),valid[0].SelectedIds);
        }
        catch(InvalidDataException){missingLocalSelectionDenied=true;}
        Check(missingLocalSelectionDenied,
            "malformed original local lock selected field is not treated as an empty/unlocked Store");
        void Refuse(LegacyStoreV1PeerTopology.Peer[] peers,string description,
            string? device=null)
        {
            bool denied=false;
            try{LegacyStoreV1PeerTopology.Validate(device??localDevice,peers);}
            catch(InvalidDataException){denied=true;}
            Check(denied,description);
        }
        Refuse(new[]{valid[0],valid[1] with{DeviceId=remoteDevice.ToUpperInvariant()}},
            "uppercase original Store DONE peer ID cannot be used as canonical writer identity");
        Refuse(valid,"uppercase original local device cannot claim a completed Store session",
            localDevice.ToUpperInvariant());
        Refuse(new[]{valid[0],valid[1] with{DeviceId=localDevice}},
            "duplicate original device ID rejects shared writer topology");
        Refuse(new[]{valid[0],valid[1]},"original remote device cannot be treated as an unregistered third owner",
            Guid.NewGuid().ToString("D"));
        Refuse(new[]{valid[0],valid[1] with{
            MemberIds=new[]{localMaps[0],remoteMaps[1],remoteMaps[2],remoteMaps[3],remoteMaps[4]}}},
            "the same map GUID on two different hosts is a blocked writer collision");
        Refuse(new[]{valid[0],valid[1] with{
            MapNames=new[]{"Map_A_0","Map_B_1","Map_B_2","Map_B_3","Map_B_4"}}},
            "different GUIDs with the same map name cannot silently share two writers");
        Refuse(new[]{valid[0],valid[1] with{
            SelectedIds=new[]{remoteMaps[0],remoteMaps[0],remoteMaps[2],remoteMaps[3],remoteMaps[4]}}},
            "duplicated selected map in a DONE peer is rejected");
        Refuse(new[]{valid[0],valid[1] with{
            SelectedIds=new[]{remoteMaps[0],remoteMaps[1],remoteMaps[2],remoteMaps[3],Guid.NewGuid().ToString("D")}}},
            "selected map not owned by the remote peer is rejected");
        Refuse(new[]{valid[0],valid[1] with{
            MemberIds=new[]{"not-a-guid",remoteMaps[1],remoteMaps[2],remoteMaps[3],remoteMaps[4]}}},
            "invalid map identity cannot enter the original Store ownership table");
        // The original peer member-list parity hashes canonical ordered JSON,
        // rather than comparing incidental on-disk whitespace formatting.
        using var memberCanonical=JsonDocument.Parse(
            """[{"id":"abc","map":"Island"},{"id":"def","map":"Center"}]""");
        using var memberFormatted=JsonDocument.Parse(
            """[ { "id" : "abc" , "map" : "Island" }, { "id" : "def" , "map" : "Center" } ]""");
        using var memberReordered=JsonDocument.Parse(
            """[{"id":"def","map":"Center"},{"id":"abc","map":"Island"}]""");
        Check(StoreV1Fingerprint.Compute(memberCanonical.RootElement)==
              StoreV1Fingerprint.Compute(memberFormatted.RootElement),
            "original Store DONE member source hash accepts same ordered member data with different JSON whitespace");
        Check(StoreV1Fingerprint.Compute(memberCanonical.RootElement)==
              "E98B421DF586505CAADFC7709A5738B36826C7B35F17F09ED4D2BD6181A9F8BF",
            "original Store DONE members hash matches Node.js JSON.stringify SHA-256 known-good vector");
        Check(StoreV1Fingerprint.Compute(memberCanonical.RootElement)!=
              StoreV1Fingerprint.Compute(memberReordered.RootElement),
            "original Store DONE member source hash rejects changed original map order");

        var sourceConfig=JsonSerializer.SerializeToElement(new{
            id="KovaCluster",mode="shared",path=@"\\192.0.2.1\synthetic",
            members=localMaps
        });
        var matchingConfig=JsonSerializer.SerializeToElement(new{
            id="KovaCluster",mode="shared",path=@"\\192.0.2.1\synthetic",
            members=localMaps
        });
        LegacyStoreV1PeerTopology.RequireSameOriginalConfiguration(
            sourceConfig,matchingConfig);
        Check(true,"source local lock and local shared DONE peer have exactly matching original configuration");
        string originalLocalConfigHash=StoreV1Fingerprint.Compute(sourceConfig);
        Check(StoreV1Fingerprint.Matches(sourceConfig,originalLocalConfigHash),
            "original local lock configHash exactly matches its current local cluster configuration");
        var staleLocalCluster=JsonSerializer.SerializeToElement(new{
            id="KovaCluster",mode="shared",path=@"\\192.0.2.1\synthetic",
            members=localMaps.Reverse().ToArray()
        });
        Check(!StoreV1Fingerprint.Matches(staleLocalCluster,originalLocalConfigHash),
            "stale original lock configHash must block changed member ordering despite self-consistent saved lock");
        var aliasChanged=JsonSerializer.SerializeToElement(new{
            id="KovaCluster",mode="shared",path=@"\\other-host\synthetic",
            members=localMaps
        });
        bool aliasDenied=false;
        try{LegacyStoreV1PeerTopology.RequireSameOriginalConfiguration(
            sourceConfig,aliasChanged,(_,_)=>false);}
        catch(InvalidDataException){aliasDenied=true;}
        Check(aliasDenied,"UNC hostname alias mismatch is not silently promoted to Store writer ownership");
        Check(!LegacyStoreV1PeerTopology.SameOriginalUncDirectory(
                @"C:\\synthetic",@"C:\\synthetic"),
            "a local drive cannot pose as an original SMB UNC Store root");
        // Inject a synthetic *filesystem-identity decision* only in this
        // isolated test; production uses Windows volume+file-ID comparison.
        LegacyStoreV1PeerTopology.RequireSameOriginalConfiguration(
            sourceConfig,aliasChanged,(left,right)=>
                left.StartsWith(@"\\",StringComparison.Ordinal)&&
                right.StartsWith(@"\\",StringComparison.Ordinal));
        Check(true,"physically verified original UNC alias may differ in text without rewriting source hashes");
        var changedOtherField=JsonSerializer.SerializeToElement(new{
            id="KovaCluster",mode="shared",path=@"\\other-host\synthetic",
            members=localMaps.Reverse().ToArray()
        });
        bool otherMismatchDenied=false;
        try{LegacyStoreV1PeerTopology.RequireSameOriginalConfiguration(
            sourceConfig,changedOtherField,(_,_)=>true);}
        catch(InvalidDataException){otherMismatchDenied=true;}
        Check(otherMismatchDenied,"a proven UNC alias never permits changed owner/member order or other original config fields");
    }

    // The original shared-store.mjs coordinator is fail-closed:
    // permanent local lock + 2 DONE peers + ACTIVE Store record,
    // no orphaned running or mutation markers and no TTL auto-release.
    {
        var valid=new LegacyStoreV1PhaseGate.Evidence(
            MapPhase:"active",LocalPeerPhase:"done",OtherPeerPhase:"done",
            LocalLockPresent:true,LocalLockMatchesSession:true,
            PeerSessionsMatch:true,ConfigurationHashesMatch:true,
            MapIdentityAndRootMatch:true,SelectedByLocalPeer:true,
            RunningMarkerPresent:false,MutationLockPresent:false,
            CancelledMarkerPresent:false,AttemptedMarkerPresent:true);
        var healthy=LegacyStoreV1PhaseGate.Evaluate(valid);
        Check(healthy.HistoricalNormalStartShape && !healthy.MaintenanceLocked &&
              !healthy.AutoRecoveryAllowed && !healthy.LiveWriterCertified &&
              !healthy.ConvertToStoreAllowed && !healthy.ConvertFromStoreAllowed,
            "two DONE peers and ten active worlds reconstruct only the historical source guard");
        void Block(LegacyStoreV1PhaseGate.Evidence changed,string expected)
        {
            var verdict=LegacyStoreV1PhaseGate.Evaluate(changed);
            Check(!verdict.HistoricalNormalStartShape && verdict.MaintenanceLocked &&
                  !verdict.AutoRecoveryAllowed && !verdict.ConvertToStoreAllowed &&
                  !verdict.LiveWriterCertified && verdict.Blockers.Contains(expected),
                "durable Store-v1 source phase denies unsafe operation: "+expected);
        }
        Block(valid with{MapPhase="review"},"STORE_MAP_NOT_ACTIVE");
        Block(valid with{MapPhase="prepared"},"STORE_MAP_NOT_ACTIVE");
        Block(valid with{OtherPeerPhase="backed"},"STORE_BOTH_PEERS_NOT_DONE");
        Block(valid with{LocalPeerPhase="frozen"},"STORE_BOTH_PEERS_NOT_DONE");
        Block(valid with{LocalLockPresent=false},"STORE_LOCAL_LOCK_MISSING");
        Block(valid with{LocalLockMatchesSession=false},"STORE_LOCAL_LOCK_SESSION_MISMATCH");
        Block(valid with{PeerSessionsMatch=false},"STORE_PEER_SESSION_MISMATCH");
        Block(valid with{ConfigurationHashesMatch=false},"STORE_CONFIG_HASH_MISMATCH");
        Block(valid with{MapIdentityAndRootMatch=false},"STORE_MAP_BINDING_MISMATCH");
        Block(valid with{SelectedByLocalPeer=false},"STORE_MAP_NOT_SELECTED");
        Block(valid with{RunningMarkerPresent=true},"STORE_RUNNING_MARKER");
        Block(valid with{MutationLockPresent=true},"STORE_DURABLE_MUTATION_LOCK");
        Block(valid with{CancelledMarkerPresent=true},"STORE_CANCELLED_MARKER");
        Block(valid with{AttemptedMarkerPresent=false},"STORE_ATTEMPTED_MARKER_MISSING");
    }

    // Original source port: historical active Store-v1 never permits a
    // fresh conversion, and its mod/port/cluster flags match source grammar.
    {
        const string safeIni="[ServerSettings]\nPort=7777\nQueryPort=27015\n";
        var input=new LegacyStoreV1ActiveLaunch.Input(
            "ScorchedEarth_WP",7777,27015,
            new[]{"123456","654321"},new[]{"654321"},
            "KovaCluster", @"\\192.0.2.1\SyntheticCluster","active",safeIni);
        var active=LegacyStoreV1ActiveLaunch.Build(input);
        Check(active.Arguments.SequenceEqual(new[]{
            "ScorchedEarth_WP?listen","-Port=7777","-QueryPort=27015",
            "-mods=123456,654321","-passivemods=654321",
            "-clusterid=KovaCluster",@"-ClusterDirOverride=\\192.0.2.1\SyntheticCluster",
            "-usestore","-log"})&&!active.ConversionRequested,
            "original active Store-v1 source grammar preserves the exact enabled mod order with separate passive flags and forbids conversion");
        // Kova 1.8.3 accepts UTF-8 and BOM-marked UTF-16 LE/BE
        // GameUserSettings.ini. The historical launcher must parse these
        // equivalently, without normalizing or rewriting original bytes.
        static byte[] WithBom(Encoding encoding,string text)=>
            encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var bomVariants=new[]{
            Encoding.UTF8.GetBytes(safeIni),
            WithBom(new UTF8Encoding(true,true),safeIni),
            WithBom(new UnicodeEncoding(false,true,true),safeIni),
            WithBom(new UnicodeEncoding(true,true,true),safeIni)
        };
        foreach(byte[] encoded in bomVariants)
        {
            string parsed=LegacyStoreV1ActiveLaunch.DecodeOriginalIni(encoded);
            Check(parsed==safeIni &&
                  LegacyStoreV1ActiveLaunch.Build(input with{
                      GameUserSettingsIni=parsed
                  }).Arguments.SequenceEqual(active.Arguments),
                "original Store-v1 UTF-8/UTF-16 INI leaves active launch arguments unchanged");
        }
        bool wrongUnicodeDenied=false;
        try{LegacyStoreV1ActiveLaunch.DecodeOriginalIni(
            new byte[]{0xFF,0xFE,0x5B});}
        catch(InvalidDataException){wrongUnicodeDenied=true;}
        Check(wrongUnicodeDenied,
            "truncated UTF-16 Store INI cannot silently bypass startup validation");
        bool nulDenied=false;
        try{LegacyStoreV1ActiveLaunch.DecodeOriginalIni(
            Encoding.UTF8.GetBytes("[ServerSettings]\0Port=7777"));}
        catch(InvalidDataException){nulDenied=true;}
        Check(nulDenied,"binary NUL in original INI is rejected without source changes");
        void Reject(LegacyStoreV1ActiveLaunch.Input changed,string label)
        {
            bool denied=false;
            try{LegacyStoreV1ActiveLaunch.Build(changed);}
            catch(InvalidDataException){denied=true;}
            Check(denied,label);
        }
        Reject(input with{OriginalPhase="prepared"},
            "historical prepared phase cannot request a second Store conversion");
        Reject(input with{OriginalPhase="review"},
            "incomplete conversion review never qualifies as active Store launch");
        Reject(input with{ClusterId="UntamedKingdom"},
            "branding cannot silently change original KovaCluster wire identity");
        Reject(input with{ClusterPath=@"\\?\C:\Windows"},
            "Windows device path cannot replace original shared Store path");
        Reject(input with{GameUserSettingsIni="[ServerSettings]\nPort=7778\nQueryPort=27015"},
            "INI GamePort mismatch blocks old-store startup before any process spawn");
        Reject(input with{GameUserSettingsIni="[ServerSettings]\nQueryPort=27016"},
            "INI QueryPort mismatch blocks old-store startup");
        Reject(input with{GamePort=27015},
            "Game and Query UDP ports cannot collide");
        Reject(input with{PassiveMods=new[]{"999999"}},
            "passive mod cannot be newly injected into the original enabled order");
        Reject(input with{Mods=new[]{"123456","654321","123456"}},
            "ambiguous duplicated original mod order is never silently rewritten during native startup");
        Reject(input with{Mods=Enumerable.Repeat("123456",101).ToArray()},
            "over-limit legacy mod list fails closed");
        Check(!active.Arguments.Any(x=>x.Contains("converttostore",StringComparison.OrdinalIgnoreCase)),
            "source-compatible active mode never emits -converttostore");
    }

    // Native operational center must expose only verified local OS evidence.
    // No WebView message may turn a status read into a scheduler or ASA write.
    {
        var operations = new DesktopOperationsCenterBridge(cluster);
        var firstSnapshot=await Call(operations.HandleAsync,"operations.snapshot");
        IsOk(firstSnapshot,"native operations center reads real local map snapshots");
        var mapped=firstSnapshot.GetProperty("data").GetProperty("maps");
        Check(mapped.GetArrayLength()==1 &&
              mapped[0].GetProperty("mapName").GetString()=="The Island" &&
              mapped[0].GetProperty("pid").ValueKind==JsonValueKind.Null &&
              mapped[0].GetProperty("memoryMiB").ValueKind==JsonValueKind.Null &&
              !mapped[0].GetProperty("running").GetBoolean(),
            "unverified synthetic ASA reports unknown PID and RAM, never fabricated metrics");
        var missing=await Call(operations.HandleAsync,"operations.map",
            new{mapId=Guid.NewGuid()});
        Check(IsError(missing,"UM-MAP-NOT-OWNED"),
            "native process detail refuses unowned or remote map IDs");
        var methodDenied=await Call(operations.HandleAsync,"operations.start",
            new{mapId=map.Id});
        Check(IsError(methodDenied,"UM-METHOD-DENIED"),
            "operational diagnostics cannot dispatch lifecycle methods");
        cluster.Config.RecurringSchedules.Add(new RecurringSchedule {
            Map=map.Id,Kind=OperationalTaskKind.Observation,
            Frequency=ScheduleFrequency.Daily,LocalClock=new TimeSpan(7,30,0),
            Enabled=false
        });
        cluster.Save(); // Synthetic test owns this isolated Manager profile.
        var configured=await Call(operations.HandleAsync,"operations.snapshot");
        IsOk(configured,"persisted local schedules shown through real Core preview");
        var schedules=configured.GetProperty("data").GetProperty("schedules");
        Check(schedules.GetArrayLength()==1 &&
              !schedules[0].GetProperty("enabled").GetBoolean() &&
              !schedules[0].GetProperty("autoExecutionPermitted").GetBoolean() &&
              schedules[0].GetProperty("state").GetString()=="disabled",
            "disabled persisted observation stays disabled and cannot execute from UI");
        var oneMap=await Call(operations.HandleAsync,"operations.map",
            new{mapId=map.Id});
        IsOk(oneMap,"owned map details return one verified local diagnostic snapshot");
        Check(oneMap.GetProperty("data").GetProperty("maps").GetArrayLength()==1 &&
              oneMap.GetProperty("data").GetProperty("schedules").GetArrayLength()==1 &&
              !oneMap.GetProperty("data").GetProperty("schedules")[0].GetProperty("enabled").GetBoolean(),
            "map inspection reads only this map persisted schedule, never creates recurring jobs");
    }

    // P0/P1 N-map scaling: 125 isolated local identities must all be visible.
    // Paginated telemetry remains read-only; a changing registry invalidates
    // a later page instead of returning a truncated or mixed roster.
    {
        var center=new DesktopOperationsCenterBridge(cluster);
        var extras=Enumerable.Range(0,124).Select(i=>{
            string home=Path.Combine(root,"ops-roster-page-fixture",i.ToString("D3"));
            return new LocalMap{
                Id=Guid.NewGuid(),Name="Paginated synthetic map "+i.ToString("D3"),
                InstallationPath=home,
                ExecutablePath=Path.Combine(home,"ShooterGame","Binaries",
                    "Win64","ArkAscendedServer.exe")
            };
        }).ToArray();
        byte[] beforeRoster=File.ReadAllBytes(cluster.Store.FilePath);
        try
        {
            foreach(var extra in extras)cluster.Config.LocalMaps.Add(extra);
            var first=await Call(center.HandleAsync,"operations.snapshot",
                new{offset=0,limit=50});
            IsOk(first,"native first bounded operations page of 125 maps");
            var entry=first.GetProperty("data");
            Check(entry.GetProperty("totalMaps").GetInt32()==125 &&
                entry.GetProperty("maps").GetArrayLength()==50 &&
                entry.GetProperty("nextOffset").GetInt32()==50 &&
                entry.GetProperty("pageOffset").GetInt32()==0,
                "no global fifty-map limit: first page has a durable next cursor");
            string key=entry.GetProperty("snapshotKey").GetString()!;
            Check(key.Length==64 && key.All(Uri.IsHexDigit),
                "operations snapshot carries a stable, non-secret registry fingerprint");
            var deniedNoKey=await Call(center.HandleAsync,"operations.snapshot",
                new{offset=50,limit=50});
            Check(IsError(deniedNoKey,"UM-OPS-SNAPSHOT-KEY-REQUIRED"),
                "a later page cannot mix registry revisions without a token");
            var second=await Call(center.HandleAsync,"operations.snapshot",
                new{offset=50,limit=50,snapshotKey=key});
            var third=await Call(center.HandleAsync,"operations.snapshot",
                new{offset=100,limit=50,snapshotKey=key});
            IsOk(second,"native second 50-map page");
            IsOk(third,"native final 25-map page");
            var all=new[]{entry,second.GetProperty("data"),third.GetProperty("data")}
                .SelectMany(x=>x.GetProperty("maps").EnumerateArray()
                    .Select(m=>m.GetProperty("mapId").GetString()!)).ToArray();
            Check(all.Length==125&&all.Distinct().Count()==125&&
                third.GetProperty("data").GetProperty("maps").GetArrayLength()==25&&
                third.GetProperty("data").GetProperty("nextOffset").ValueKind==
                    JsonValueKind.Null,
                "all 125 unique host-local maps are returned without silent omission");
            var late=await Call(center.HandleAsync,"operations.map",
                new{mapId=extras[^1].Id});
            IsOk(late,"map detail beyond index 50 remains directly addressable");
            Check(late.GetProperty("data").GetProperty("maps")[0]
                    .GetProperty("mapId").GetString()==extras[^1].Id.ToString("D"),
                "native detail route has no fifty-map cap");
            var invalidLimit=await Call(center.HandleAsync,"operations.snapshot",
                new{offset=0,limit=101});
            Check(IsError(invalidLimit,"UM-OPS-PAGE-LIMIT"),
                "untrusted renderer cannot request an unbounded native response");
            cluster.Config.LocalMaps.Add(new LocalMap{
                Id=Guid.NewGuid(),Name="Post-page newly registered test map",
                InstallationPath=Path.Combine(root,"ops-roster-page-fixture","new")
            });
            var stale=await Call(center.HandleAsync,"operations.snapshot",
                new{offset=50,limit=50,snapshotKey=key});
            Check(IsError(stale,"UM-OPS-SNAPSHOT-CHANGED"),
                "registry mutation invalidates an old page token instead of silently truncating the UI");
        }
        finally
        {
            foreach(var extra in extras)cluster.Config.LocalMaps.Remove(extra);
            cluster.Config.LocalMaps.RemoveAll(m=>
                m.Name=="Post-page newly registered test map");
        }
        Check(beforeRoster.SequenceEqual(File.ReadAllBytes(cluster.Store.FilePath)),
            "large local operations roster reads never write the Manager profile or ASA files");
    }

    // Ticking the durable operational scheduler must produce REAL local,
    // read-only observations and persistent receipts, without touching any
    // ASA process, config during the tick, Store-v1, RCON or network peer.
    {
        using var supervisor=new DesktopRecurringReadOnlySupervisor(cluster,
            _=>new ProcessCheck(false,"synthetic confirmed stopped",null,true));
        var operations=new DesktopOperationsCenterBridge(cluster,supervisor);
        var deniedNoApproval=await Call(operations.HandleAsync,
            "operations.scheduleCreate",new{
                mapId=map.Id,kind="Observation",intervalMinutes=5,confirm=false
            });
        Check(IsError(deniedNoApproval,
                "UM-OPS-SCHEDULE-INVALID-OR-UNAPPROVED"),
            "auto scheduler requires explicit native operator approval");
        int oldCount=cluster.Config.RecurringSchedules.Count;
        var forbidden=await Call(operations.HandleAsync,
            "operations.scheduleCreate",new{
                mapId=map.Id,kind="ServerRestart",intervalMinutes=5,confirm=true
            });
        Check(!forbidden.GetProperty("ok").GetBoolean() &&
              cluster.Config.RecurringSchedules.Count==oldCount,
            "no UI payload can schedule unattended restart/Store/RCON");
        var unknown=await Call(operations.HandleAsync,
            "operations.scheduleCreate",new{
                mapId=Guid.NewGuid(),kind="Observation",intervalMinutes=5,confirm=true
            });
        Check(!unknown.GetProperty("ok").GetBoolean(),
            "a foreign or remote map cannot be enrolled");
        var created=await Call(operations.HandleAsync,
            "operations.scheduleCreate",new{
                mapId=map.Id,kind="Observation",intervalMinutes=5,confirm=true
            });
        IsOk(created,"operator created persisted, local read-only observation");
        string jobId=created.GetProperty("data").GetProperty("jobId").GetString()!;
        Check(created.GetProperty("data").GetProperty("autoLifecycleAllowed").GetBoolean()==false &&
              cluster.Config.RecurringSchedules.Count==oldCount+1 &&
              cluster.Config.RecurringSchedules.Single(j=>j.Id==Guid.Parse(jobId)).Enabled,
            "native scheduler creation is a real optimistic Manager configuration write");
        var duplicate=await Call(operations.HandleAsync,
            "operations.scheduleCreate",new{
                mapId=map.Id,kind="Observation",intervalMinutes=5,confirm=true
            });
        Check(!duplicate.GetProperty("ok").GetBoolean() &&
              cluster.Config.RecurringSchedules.Count==oldCount+1,
            "duplicate active local recurring task is rejected");

        byte[] beforeTick=File.ReadAllBytes(cluster.Store.FilePath);
        var start=DateTimeOffset.UtcNow;
        Check(supervisor.TickOnce(start).Length==0,
            "first recurring tick only claims next future due slot");
        var firstDue=supervisor.DueDates()[Guid.Parse(jobId)];
        Check(firstDue==start.AddMinutes(5),
            "durable interval cursor records exact next slot, not a moving GUI estimate");
        var performed=supervisor.TickOnce(start.AddMinutes(6));
        Check(performed.Any(x=>x.Status=="claimed"&&x.Job==Guid.Parse(jobId)) &&
              performed.Any(x=>x.Status=="succeeded"&&x.Job==Guid.Parse(jobId)) &&
              File.ReadAllBytes(cluster.Store.FilePath).SequenceEqual(beforeTick),
            "due schedule observes confirmed stopped process and journals success without rewriting Manager configuration");
        var snapshot=await Call(operations.HandleAsync,"operations.snapshot");
        IsOk(snapshot,"operations UI reads real persisted scheduler evidence");
        var scheduledRow=snapshot.GetProperty("data").GetProperty("schedules")
            .EnumerateArray().Single(x=>x.GetProperty("id").GetString()==jobId);
        var persistedDue=supervisor.DueDates()[Guid.Parse(jobId)];
        Check(scheduledRow.GetProperty("nextUtc").GetDateTimeOffset()==persistedDue,
            "native UI displays the persisted next due slot rather than now plus interval");
        Check(snapshot.GetProperty("data").GetProperty("scheduleReceipts")
                .EnumerateArray().Any(x=>x.GetProperty("status").GetString()=="succeeded") &&
              snapshot.GetProperty("data").GetProperty("schedulingIsReadOnly").GetBoolean(),
            "operator sees durable executed read-only schedule receipts in the native UI");
        using(var restarted=new DesktopRecurringReadOnlySupervisor(cluster))
            Check(restarted.Recent().Any(x=>x.Status=="succeeded" &&
                  x.Job==Guid.Parse(jobId)),
                "Manager supervisor reattach reads original durable receipts without replay");
        var disabled=await Call(operations.HandleAsync,
            "operations.scheduleDisable",new{jobId,confirm=true});
        IsOk(disabled,"operator disabled saved read-only inspection");
        Check(cluster.Config.RecurringSchedules.Single(j=>j.Id==Guid.Parse(jobId)).Enabled==false &&
              supervisor.TickOnce(start.AddMinutes(12)).Length==0,
            "disabled recurring task never runs even after its prior due time");
    }

    // Historical SteamCheck jobs must NEVER query SteamCMD automatically.
    // Synthetic TEMP-only test retains the exact saved legacy configuration.
    {
        string watchRoot=Path.Combine(root,"p0-scheduled-public-steam-check");
        string install=Path.Combine(watchRoot,"single-install");
        string steamApps=Path.Combine(install,"steamapps");
        Directory.CreateDirectory(steamApps);
        string acf=Path.Combine(steamApps,"appmanifest_2430930.acf");
        File.WriteAllText(acf,
            "\"AppState\"\n{\n  \"appid\" \"2430930\"\n  \"buildid\" \"123456\"\n}\n");
        byte[] originalManifest=File.ReadAllBytes(acf);
        await using var watched=new ClusterService("manual-only-public-check",
            Path.Combine(watchRoot,"manager-profile"),
            _=>new ProcessCheck(false,"synthetic confirmed stopped",null,true));
        var first=new LocalMap {
            Id=Guid.NewGuid(),Name="Retired SteamCheck A",
            InstallationPath=install,
            ExecutablePath=Path.Combine(install,"ShooterGame","Binaries",
                "Win64","ArkAscendedServer.exe")
        };
        var second=new LocalMap {
            Id=Guid.NewGuid(),Name="Retired SteamCheck B",
            InstallationPath=install,ExecutablePath=first.ExecutablePath
        };
        watched.AddMap(first);watched.AddMap(second);
        foreach(var target in new[]{first,second})
            watched.Config.RecurringSchedules.Add(new RecurringSchedule{
                Id=Guid.NewGuid(),Map=target.Id,Kind=OperationalTaskKind.SteamCheck,
                Frequency=ScheduleFrequency.IntervalMinutes,IntervalMinutes=5,
                Enabled=true
            });
        watched.Save();
        byte[] before=File.ReadAllBytes(watched.Store.FilePath);
        int queries=0;
        using var recurring=new DesktopRecurringReadOnlySupervisor(watched,
            _=>new ProcessCheck(false,"synthetic stopped",null,true),
            (_,_)=>{
                queries++;
                throw new InvalidOperationException("Steam appinfo must not run");
            });
        Check(watched.SnapshotRecurringSchedules().Count(x=>
                 x.Kind==OperationalTaskKind.SteamCheck&&!x.Enabled)==2,
            "historical SteamCheck records project as inactive without deleting user data");
        var operations=new DesktopOperationsCenterBridge(watched,recurring);
        var denied=await Call(operations.HandleAsync,"operations.scheduleCreate",
            new{mapId=first.Id,kind="SteamCheck",intervalMinutes=60,confirm=true});
        Check(IsError(denied,"UM-STEAM-CHECK-MANUAL-ONLY"),
            "native RPC cannot create a scheduled Steam version check");
        bool coreDenied=false;
        try{watched.CreateReadOnlySchedule(first.Id,OperationalTaskKind.SteamCheck,60);}
        catch(InvalidOperationException ex){
            coreDenied=ex.Message=="UM-STEAM-CHECK-MANUAL-ONLY";
        }
        Check(coreDenied,"direct Core schedule API also rejects SteamCheck");
        recurring.Start();
        Check(recurring.TickOnce(DateTimeOffset.UtcNow.AddDays(30)).Length==0 &&
              queries==0 && recurring.DueDates().Count==0,
            "expired SteamCheck cannot launch Steam appinfo from the background worker");
        var status=await Call(operations.HandleAsync,"operations.snapshot");
        IsOk(status,"operations status remains accessible without Steam polling");
        Check(File.ReadAllBytes(acf).SequenceEqual(originalManifest) &&
              File.ReadAllBytes(watched.Store.FilePath).SequenceEqual(before) &&
              watched.Config.RecurringSchedules.All(x=>x.Enabled),
            "manual-only policy preserves original Manager and ASA bytes");
    }

    // P0 manual-only SteamCMD: legacy schedules are inert even when due.
    // No real SteamCMD, ASA, Kova Store, RCON, or background updater.
    {
        string lab=Path.Combine(root,"p0-auto-stopped-steam");
        string install=Path.Combine(lab,"install");
        string executable=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable,new byte[]{1,2,3,4,5});
        await using var host=new ClusterService("p0-manual-only-steam",
            Path.Combine(lab,"manager-profile"),
            _=>new ProcessCheck(false,"synthetic confirmed stopped",null,true));
        var target=new LocalMap {
            Id=Guid.NewGuid(),Name="Isolated stopped Steam map",
            InstallationPath=install,ExecutablePath=executable,
            Arguments="TheIsland_WP?SessionName=Fixture"
        };
        host.AddMap(target);
        Check(!new ManagerConfig().AutoSteamUpdatesEnabled &&
              !new ManagerConfig().SteamRestartAfterUpdate,
            "new Manager profiles default to manual SteamCMD with no automatic ASA restart");
        // An older serialized profile may still contain both legacy true
        // preferences. Loading them must not reactivate the retired scheduler
        // or dispatch a Store/ASA process update without manual approval.
        host.Config.AutoSteamUpdatesEnabled=true;
        host.Config.SteamRestartAfterUpdate=true;
        int autoRuns=0;
        using var supervisor=new DesktopScheduledStoppedSteamSupervisor(host,
            (_,_)=>{
                autoRuns++;
                throw new InvalidOperationException("Scheduled updater must be inert");
            });
        supervisor.Start();
        var bridge=new DesktopScheduledStoppedSteamBridge(host,supervisor);
        Check(supervisor.RuntimeState=="manual-only" &&
              supervisor.Snapshot().Length==0 && autoRuns==0,
            "startup never schedules or dispatches unattended SteamCMD");
        var denied=await Call(bridge.HandleAsync,"steam.scheduleSet",
            new{mapId=target.Id,intervalMinutes=60,confirm=true,
                typedApproval="AUTOMATIKUS LEÁLLÍTOTT FRISSÍTÉS"});
        Check(IsError(denied,"UM-STEAM-SCHEDULE-MANUAL-ONLY")&&autoRuns==0,
            "old typed consent cannot enable the retired automatic Steam updater");
        bool refused=false;
        try{supervisor.Configure(target.Id,60,DateTimeOffset.UtcNow);}
        catch(InvalidOperationException ex){
            refused=ex.Message=="UM-STEAM-SCHEDULE-MANUAL-ONLY";
        }
        Check(refused&&autoRuns==0,
            "native supervisor rejects even direct Configure calls");
        var snapshot=await supervisor.TickOnceAsync(DateTimeOffset.UtcNow.AddDays(7));
        Check(snapshot.Length==0&&autoRuns==0,
            "direct scheduler TickOnce never invokes an updater");
        var status=await Call(bridge.HandleAsync,"steam.scheduleStatus");
        Check(status.GetProperty("ok").GetBoolean() &&
              status.GetProperty("data").GetProperty("runnerState").GetString()=="manual-only",
            "UI status truthfully reports manual-only SteamCMD");
        Check(File.ReadAllBytes(executable).SequenceEqual(
                  new byte[]{1,2,3,4,5}) &&
              !Directory.Exists(Path.Combine(install,"ShooterGame","Saved")),
            "retired schedule never changes ASA binary, world, profile or Store files");
    }

    // Explicit new-ASA schedule engine uses durable local claim records and
    // existing native bridges. Injected callbacks are isolated synthetic test
    // runners only; no RCON, SteamCMD, port bind or production file is used.
    {
        string isolated=Path.Combine(root,"p0-native-schedule-tests");
        Directory.CreateDirectory(isolated);
        await using var scheduleCluster=new ClusterService("p0-schedules",isolated);
        var m1=new LocalMap {
            Id=Guid.NewGuid(),Name="Fixture Island",InstallationPath=Path.Combine(isolated,"island"),
            ExecutablePath=Path.Combine(isolated,"island","ShooterGame","Binaries","Win64",
                "ArkAscendedServer.exe"),Arguments="TheIsland_WP?SessionName=Fixture",
            GamePort=47777,QueryPort=47778,RconPort=47779
        };
        var m2=new LocalMap {
            Id=Guid.NewGuid(),Name="Fixture Center",InstallationPath=Path.Combine(isolated,"center"),
            ExecutablePath=Path.Combine(isolated,"center","ShooterGame","Binaries","Win64",
                "ArkAscendedServer.exe"),Arguments="TheCenter_WP?SessionName=Fixture",
            GamePort=47877,QueryPort=47878,RconPort=47879
        };
        scheduleCluster.Config.LocalMaps.Add(m1);
        scheduleCluster.Config.LocalMaps.Add(m2);
        scheduleCluster.Save();
        var rcon=new DesktopRconReadOnlyBridge(scheduleCluster);
        using var restarter=new DesktopFreshAsaRestartBridge(scheduleCluster);
        int sent=0,simultaneous=0,maxSimultaneous=0;
        async Task<string> SyntheticRunner(Guid mapId,string kind,CancellationToken token)
        {
            Check(mapId==m1.Id||mapId==m2.Id,"only locally registered map IDs dispatch");
            int active=Interlocked.Increment(ref simultaneous);
            maxSimultaneous=Math.Max(maxSimultaneous,active);
            await Task.Delay(5,token);
            Interlocked.Decrement(ref simultaneous);
            Interlocked.Increment(ref sent);
            return "ISOLATED_FAKE_RUNNER_NO_ASA_OR_RCON";
        }
        using(var supervisor=new DesktopScheduledAsaOperationsSupervisor(
            scheduleCluster,rcon,restarter,SyntheticRunner,_=>true))
        {
            var denied=await Call(supervisor.HandleAsync,"schedule.nativeSet",
                new{mapId=m1.Id,kind="SaveWorld",frequency="Interval",
                    intervalMinutes=15,minuteOfDay=180,daysMask=127,confirm=false});
            Check(IsError(denied,"UM-ASA-SCHEDULE-APPROVAL"),
                "scheduled RCON cannot be configured without explicit native opt-in");
            var created=await Call(supervisor.HandleAsync,"schedule.nativeSet",
                new{mapId=m1.Id,kind="SaveWorld",frequency="Interval",
                    intervalMinutes=15,minuteOfDay=180,daysMask=127,confirm=true});
            IsOk(created,"isolated native schedule persisted for one local map");
            var overview=await Call(supervisor.HandleAsync,"schedule.nativeOverview");
            IsOk(overview,"native Operations overview reads one durable local ASA schedule");
            Check(overview.GetProperty("data").GetProperty("jobs").GetArrayLength()==1 &&
                  !overview.GetProperty("data").GetProperty("remoteExecutionAllowed").GetBoolean() &&
                  overview.GetProperty("data").GetProperty("jobs")[0]
                    .GetProperty("mapId").GetGuid()==m1.Id,
                "native schedule overview lists only the registered local map without peer control");
            var before=supervisor.Snapshot().Single();
            var due=before.NextDueUtc;
            await supervisor.TickOnceAsync(due.AddMinutes(1));
            Check(sent==1&&supervisor.Snapshot().Single().LastStatus==
                    "executed-no-fsync-or-client-certification",
                "native scheduler durably claims and dispatches one approved SaveWorld slot");
            var savedHistory=supervisor.History(m1.Id);
            Check(savedHistory.Length==1&&savedHistory[0].RunId!=Guid.Empty&&
                  savedHistory[0].Kind=="SaveWorld"&&
                  savedHistory[0].Status=="executed-no-fsync-or-client-certification"&&
                  savedHistory[0].FinishedUtc.HasValue,
                "native SaveWorld preserves a completed run in the durable execution history");
            await supervisor.TickOnceAsync(due.AddMinutes(1));
            Check(sent==1,"claimed native SaveWorld slot is never replayed");
            Check(supervisor.History(m1.Id).Length==1,
                "repeat tick cannot duplicate a claimed execution history entry");
            var restartedMap=await Call(supervisor.HandleAsync,"schedule.nativeStatus",
                new{mapId=m1.Id});
            IsOk(restartedMap,"native WebView bridge reads persisted local schedule");
            Check(restartedMap.GetProperty("data").GetProperty("history").GetArrayLength()==1,
                "map-specific native RPC exposes the bounded durable execution log");
            var other=await Call(supervisor.HandleAsync,"schedule.nativeStatus",
                new{mapId=m2.Id});
            Check(other.GetProperty("data").GetProperty("jobs").GetArrayLength()==0,
                "map-specific schedule UI never includes another local map");
            Check(other.GetProperty("data").GetProperty("history").GetArrayLength()==0,
                "one map cannot read another map's native execution history");
            var firstRestart=supervisor.Configure(m1.Id,"Restart","Daily",0,240,127);
            supervisor.Configure(m2.Id,"Restart","Daily",0,240,127);
            Check(supervisor.RestartConflicts(m1.Id).Contains(m2.Name),
                "same-local-host matching restart clock warns without blocking save");
            await supervisor.TickOnceAsync(firstRestart.NextDueUtc.AddMinutes(1));
            Check(maxSimultaneous==1,
                "due restarts on the same host are serialized, never concurrent");
            var disabled=supervisor.Disable(m2.Id,"Restart");
            Check(!disabled.Enabled,"operator can durably disable just one map restart");
            var history=supervisor.Snapshot();
            Check(history.Any(x=>x.MapId==m1.Id&&x.Kind=="Restart")&&
                  history.Any(x=>x.MapId==m2.Id&&x.Kind=="Restart"&&!x.Enabled),
                "native schedule journal retains both independent map identities");
        }
        using(var recovered=new DesktopScheduledAsaOperationsSupervisor(
            scheduleCluster,rcon,restarter,SyntheticRunner,_=>true))
        {
            Check(recovered.Snapshot().Length==3 && sent>=1,
                "Manager relaunch reads durable native schedule journal without automatic replay");
            Check(recovered.History(m1.Id).Any(x=>x.RunId!=Guid.Empty&&
                  x.Kind=="SaveWorld"&&x.FinishedUtc.HasValue),
                "Manager relaunch preserves prior completed native execution evidence");
            int beforeMissed=sent;
            var upcoming=recovered.Snapshot().Where(x=>x.Enabled)
                .Select(x=>x.NextDueUtc).Min();
            recovered.SkipMissedOnManagerStart(upcoming.AddMinutes(1));
            Check(sent==beforeMissed&&recovered.Snapshot().Any(x=>
                  x.LastStatus=="manager-start-skipped-no-replay"),
                "Manager startup skips overdue native slots without late RCON or restart");
            Check(recovered.History(m1.Id).Any(x=>
                  x.Status=="manager-start-skipped-no-replay"&&
                  x.FinishedUtc.HasValue),
                "missed startup slots remain visibly audited without executing RCON");
            var denyRemote=await Call(recovered.HandleAsync,"schedule.nativeSet",
                new{mapId=Guid.NewGuid(),kind="Restart",frequency="Daily",
                    intervalMinutes=0,minuteOfDay=240,daysMask=127,confirm=true});
            Check(IsError(denyRemote,"UM-ASA-SCHEDULE-NATIVE-DENIED"),
                "unowned remote map cannot receive local native schedule writes");
            m1.HistoricalStoreProtected=true;
            var protectedAttempt=await Call(recovered.HandleAsync,"schedule.nativeSet",
                new{mapId=m1.Id,kind="Restart",frequency="Daily",
                    intervalMinutes=0,minuteOfDay=240,daysMask=127,confirm=true});
            Check(IsError(protectedAttempt,"UM-ASA-SCHEDULE-NATIVE-DENIED"),
                "Store-v1 protection cannot be bypassed by an isolated runner hook");
        }
        // Force a durable receipt write failure AFTER a synthetic dispatch:
        // no live ASA binary, port, RCON or protected Store is touched.
        m1.HistoricalStoreProtected=false;
        FileStream? journalLock=null;
        int journalDispatches=0;
        using(var faulted=new DesktopScheduledAsaOperationsSupervisor(
            scheduleCluster,rcon,restarter,(id,kind,token)=>{
                journalDispatches++;
                journalLock=new FileStream(
                    Path.Combine(isolated,"native-asa-schedules-v1.json"),
                    FileMode.Open,FileAccess.Read,FileShare.None);
                return Task.FromResult("SYNTHETIC_DISPATCH_THEN_JOURNAL_LOCK");
            },_=>true))
        {
            faulted.Disable(m1.Id,"Restart");
            var next=faulted.Configure(m1.Id,"SaveWorld","Interval",
                15,180,127).NextDueUtc;
            bool interrupted=false;
            try{await faulted.TickOnceAsync(next.AddMinutes(1));}
            catch(InvalidOperationException ex) when(
                ex.Message=="UM-ASA-SCHEDULE-JOURNAL-WRITE-NEEDS-OPERATOR")
            {interrupted=true;}
            finally{journalLock?.Dispose();}
            Check(interrupted&&journalDispatches==1&&
                faulted.RuntimeState=="journal-write-needs-operator",
                "native scheduler stops on failed durable receipt and exposes operator fault");
            bool replayDenied=false;
            try{await faulted.TickOnceAsync(next.AddMinutes(20));}
            catch(InvalidOperationException ex) when(
                ex.Message=="UM-ASA-SCHEDULE-OPERATOR-REVIEW-REQUIRED")
            {replayDenied=true;}
            Check(replayDenied&&journalDispatches==1,
                "receipt failure permanently denies further automatic scheduler ticks");
        }
    }

    // An adopted running ASA map keeps the same local registry size.
    // A config save must invalidate the 900ms snapshot immediately or the UI
    // can show a stale monitoring-only map after successful native handoff.
    {
        string directory=Path.Combine(root,"p0-telemetry-invalidation");
        Directory.CreateDirectory(directory);
        await using var owner=new ClusterService("p0-telemetry",directory,
            inspect:_=>new ProcessCheck(false,"SYNTHETIC_STOPPED",null));
        var changed=new LocalMap {
            Id=Guid.NewGuid(),Name="Old observed title",
            InstallationPath=Path.Combine(directory,"server"),
            ExecutablePath=Path.Combine(directory,"server","ShooterGame",
                "Binaries","Win64","ArkAscendedServer.exe"),
            GamePort=47777,QueryPort=47778
        };
        owner.Config.LocalMaps.Add(changed);
        owner.Save();
        var oldSnapshot=owner.LocalStatus();
        Check(oldSnapshot.Maps.Single().Name=="Old observed title",
            "synthetic local host status initially exposes saved map identity");
        changed.Name="Managed title after adoption";
        owner.Save();
        var refreshed=owner.LocalStatus();
        Check(refreshed.Maps.Single().Name=="Managed title after adoption",
            "Manager config save instantly refreshes host telemetry without waiting for polling TTL");
    }

    // P0 map settings use the original ClusterService.ReplaceMap CAS and
    // the existing installation coordinator. Never touch live ASA/RCON/Store.
    {
        string isolated=Path.Combine(root,"p0-maintenance-native-fixture");
        Directory.CreateDirectory(isolated);
        await using var ownCluster=new ClusterService("p0-maintenance",isolated);
        var maintenanceMap=new LocalMap {
            Id=Guid.NewGuid(),Name="P0 Maintenance Fixture",
            InstallationPath=Path.Combine(isolated,"server"),
            ExecutablePath=Path.Combine(isolated,"server","ShooterGame",
                "Binaries","Win64","ArkAscendedServer.exe"),
            Arguments="TheIsland_WP?SessionName=MaintenanceFixture",
            GamePort=48777,QueryPort=48778,RconPort=48779
        };
        Directory.CreateDirectory(Path.GetDirectoryName(maintenanceMap.ExecutablePath)!);
        // The process inspector requires a registered binary path; the fixture
        // is synthetic bytes only and is NEVER executed by any test action.
        File.WriteAllBytes(maintenanceMap.ExecutablePath,new byte[]{0x4D,0x5A,0x00,0x00});
        ownCluster.AddMap(maintenanceMap);
        var maintenance=new DesktopMapMaintenanceBridge(ownCluster);
        var status=await Call(maintenance.HandleAsync,"maps.maintenanceStatus",
            new{mapId=maintenanceMap.Id});
        IsOk(status,"native map maintenance status is available on local fixture");
        Check(status.GetProperty("data").GetProperty("permitted").GetBoolean() &&
              !status.GetProperty("data").GetProperty("maintenance").GetBoolean(),
            "stopped fresh local fixture can configure its maintenance state");
        var noApproval=await Call(maintenance.HandleAsync,"maps.maintenanceSet",
            new{mapId=maintenanceMap.Id,maintenance=true,confirm=false});
        Check(IsError(noApproval,"UM-MAP-MAINTENANCE-CONFIRM"),
            "map maintenance mutation requires explicit approval");
        var enabled=await Call(maintenance.HandleAsync,"maps.maintenanceSet",
            new{mapId=maintenanceMap.Id,maintenance=true,confirm=true});
        IsOk(enabled,"approved map maintenance state persisted via native Core");
        Check(ownCluster.Config.LocalMaps.Single(m=>m.Id==maintenanceMap.Id).Maintenance &&
              !enabled.GetProperty("data").GetProperty("serverProcessModified").GetBoolean(),
            "maintenance switch changes only the Manager profile, no ASA process");
        var off=await Call(maintenance.HandleAsync,"maps.maintenanceSet",
            new{mapId=maintenanceMap.Id,maintenance=false,confirm=true});
        IsOk(off,"maintenance switch can be safely disabled");
        Check(!ownCluster.Config.LocalMaps.Single(m=>m.Id==maintenanceMap.Id).Maintenance,
            "reversible local maintenance setting saves with original map identity");
        var denied=await Call(maintenance.HandleAsync,"maps.maintenanceSet",
            new{mapId=Guid.NewGuid(),maintenance=true,confirm=true});
        Check(IsError(denied,"UM-MAP-NOT-OWNED"),
            "remote or unknown maps cannot be changed using a local profile endpoint");
        ownCluster.Config.LocalMaps.Single(m=>m.Id==maintenanceMap.Id).HistoricalStoreProtected=true;
        var protectedAttempt=await Call(maintenance.HandleAsync,"maps.maintenanceSet",
            new{mapId=maintenanceMap.Id,maintenance=true,confirm=true});
        Check(IsError(protectedAttempt,"UM-MAP-MAINTENANCE-STOPPED-NONSTORE-ONLY"),
            "historical Store-v1 map cannot bypass maintenance profile guard");
    }

    // Two hosts must each persist only their own LAN profile and DPAPI key.
    // No network listener or remote command runs during the isolated fixture.
    {
        var lan=new DesktopLanSettingsBridge(cluster);
        var status=await Call(lan.HandleAsync,"lan.status");
        IsOk(status,"LAN pairing reads actual local settings");
        Check(!status.GetProperty("data").GetProperty("pairingKeyConfigured").GetBoolean(),
            "new isolated Manager starts with no invented LAN pairing");
        var publicPeer=await Call(lan.HandleAsync,"lan.preview",
            new{listenPort=39290,peerAddress="8.8.8.8",peerPort=39291,
                pairingKey="0123456789abcdef0123456789abcdef"});
        Check(IsError(publicPeer,"UM-LAN-INVALID-PRIVATE-PEER"),
            "LAN pairing refuses a public IPv4 destination");
        var before=File.ReadAllBytes(cluster.Store.FilePath);
        const string pairSecret="SYNTHETIC-SHARED-PAIR-SECRET-NEVER-LOG";
        string remoteHostId=Guid.NewGuid().ToString("N");
        var ownIdRejected=await Call(lan.HandleAsync,"lan.preview",
            new{listenPort=39290,peerAddress="192.168.1.200",peerPort=39291,
                expectedPeerHostId=cluster.Config.LocalHostId,pairingKey=pairSecret});
        Check(IsError(ownIdRejected,"UM-LAN-PEER-HOST-ID-REQUIRED"),
            "LAN refuses a peer identity equal to the local Manager");
        var prepared=await Call(lan.HandleAsync,"lan.preview",
            new{listenPort=39290,peerAddress="192.168.1.200",peerPort=39291,
                expectedPeerHostId=remoteHostId,pairingKey=pairSecret});
        IsOk(prepared,"RFC1918 peer passes local-only pairing preview");
        Check(File.ReadAllBytes(cluster.Store.FilePath).SequenceEqual(before),
            "LAN preview leaves persisted configuration untouched");
        var approval=prepared.GetProperty("data").GetProperty("token").GetString()!;
        Check(!prepared.ToString().Contains(pairSecret,StringComparison.Ordinal),
            "LAN preview does not expose shared secret to React");
        var notConfirmed=await Call(lan.HandleAsync,"lan.apply",
            new{token=approval,confirm=false});
        Check(IsError(notConfirmed,"UM-LAN-EXPLICIT-APPROVAL-REQUIRED"),
            "LAN save requires final explicit approval");
        var saved=await Call(lan.HandleAsync,"lan.apply",
            new{token=approval,confirm=true});
        IsOk(saved,"LAN profile saved through atomic Manager config transaction");
        Check(cluster.Config.ListenPort==39290 &&
              cluster.Config.PeerAddress=="192.168.1.200" &&
              cluster.Config.PeerPort==39291 &&
              cluster.Config.ExpectedPeerHostId==remoteHostId &&
              !string.IsNullOrWhiteSpace(cluster.Config.PairingKeyProtected),
            "LAN settings retain local port, private peer address and DPAPI ciphertext");
        Check(SecureStore.Unprotect(cluster.Config.PairingKeyProtected)==pairSecret &&
              !File.ReadAllText(cluster.Store.FilePath).Contains(pairSecret,StringComparison.Ordinal),
            "LAN secret is DPAPI protected and absent as plaintext on disk");
        var replay=await Call(lan.HandleAsync,"lan.apply",
            new{token=approval,confirm=true});
        Check(IsError(replay,"UM-LAN-EXPLICIT-APPROVAL-REQUIRED"),
            "LAN preview token cannot be replayed");
        var current=await Call(lan.HandleAsync,"lan.status");
        IsOk(current,"LAN status reports persisted settings without secret");
        Check(current.GetProperty("data").GetProperty("pairingKeyConfigured").GetBoolean() &&
              Guid.Parse(current.GetProperty("data").GetProperty("expectedPeerHostId").GetString()!)==Guid.Parse(remoteHostId) &&
              current.GetProperty("data").GetProperty("localHostId").GetString()==cluster.Config.LocalHostId &&
              !current.ToString().Contains(pairSecret,StringComparison.Ordinal),
            "read-only LAN status never returns pairing secret");

        const string secondPairSecret="SECOND-SYNTHETIC-PAIR-SECRET-NEVER-LOG";
        string secondHostId=Guid.NewGuid().ToString("D");
        var secondPrepared=await Call(lan.HandleAsync,"lan.preview",new{
            peerAction="upsert",listenPort=39290,hostId=secondHostId,hostName="Synthetic West",
            peerAddress="10.44.1.23",peerPort=39292,pairingKey=secondPairSecret});
        IsOk(secondPrepared,"second independent LAN peer passes local-only preview");
        var secondToken=secondPrepared.GetProperty("data").GetProperty("token").GetString()!;
        IsOk(await Call(lan.HandleAsync,"lan.apply",new{token=secondToken,confirm=true}),
            "second LAN peer is saved through the local profile transaction");
        Check(cluster.Config.LanPeers.Count==2 &&
              cluster.Config.LanPeers.Select(x=>x.HostId).Distinct(StringComparer.OrdinalIgnoreCase).Count()==2 &&
              SecureStore.Unprotect(cluster.Config.LanPeers.Single(x=>Guid.Parse(x.HostId)==Guid.Parse(secondHostId)).PairingKeyProtected)==secondPairSecret &&
              !File.ReadAllText(cluster.Store.FilePath).Contains(secondPairSecret,StringComparison.Ordinal),
            "N-peer profile keeps independent DPAPI keys and no plaintext at rest");
        var twoPeers=await Call(lan.HandleAsync,"lan.status");
        Check(twoPeers.GetProperty("data").GetProperty("peers").GetArrayLength()==2 &&
              !twoPeers.ToString().Contains(secondPairSecret,StringComparison.Ordinal),
            "LAN status returns all peers without returning any key");
        var removePrepared=await Call(lan.HandleAsync,"lan.preview",new{
            peerAction="remove",listenPort=39290,hostId=remoteHostId});
        IsOk(removePrepared,"one paired peer can be removed by its pinned Host ID");
        var removeToken=removePrepared.GetProperty("data").GetProperty("token").GetString()!;
        IsOk(await Call(lan.HandleAsync,"lan.apply",new{token=removeToken,confirm=true}),
            "peer removal applies only to local Manager configuration");
        Check(cluster.Config.LanPeers.Count==1&&Guid.Parse(cluster.Config.LanPeers[0].HostId)==Guid.Parse(secondHostId)&&
              cluster.Config.PeerAddress=="10.44.1.23"&&cluster.Config.PeerPort==39292,
            "removing one peer preserves the other and refreshes legacy mirrors");
        var lastPeerBlocked=await Call(lan.HandleAsync,"lan.preview",new{
            peerAction="remove",listenPort=39290,hostId=secondHostId});
        Check(IsError(lastPeerBlocked,"UM-LAN-LAST-PEER-LISTENER-MUST_DISABLE"),
            "last peer cannot be removed while its local listener remains enabled");
        var disableAndRemove=await Call(lan.HandleAsync,"lan.preview",new{
            peerAction="remove",listenPort=0,hostId=secondHostId});
        IsOk(disableAndRemove,"last peer can be removed with the local listener disabled");
        IsOk(await Call(lan.HandleAsync,"lan.apply",new{
            token=disableAndRemove.GetProperty("data").GetProperty("token").GetString(),confirm=true}),
            "last peer removal saves only the local Manager settings");
        Check(cluster.Config.LanPeers.Count==0&&cluster.Config.ListenPort==0,
            "final peer removal leaves no unused local LAN listener enabled");
    }

    // Three-host transport proof on loopback only: each pinned HostId has an
    // independent key, authenticated status, and local-only listener. No ASA
    // ports are used and no external address is contacted.
    {
        string lanRoot=Path.Combine(root,"three-host-lan");
        Func<LocalMap,ProcessCheck> stopped=_=>new ProcessCheck(false,"synthetic host fixture",null,true);
        await using var hostA=new ClusterService("lan-host-a",Path.Combine(lanRoot,"a"),stopped);
        await using var hostB=new ClusterService("lan-host-b",Path.Combine(lanRoot,"b"),stopped);
        await using var hostC=new ClusterService("lan-host-c",Path.Combine(lanRoot,"c"),stopped);
        var mapB=new LocalMap{Id=Guid.NewGuid(),Name="Synthetic Host B Map",
            InstallationPath=Path.Combine(lanRoot,"b-map")};
        var mapC=new LocalMap{Id=Guid.NewGuid(),Name="Synthetic Host C Map",
            InstallationPath=Path.Combine(lanRoot,"c-map")};
        hostB.AddMap(mapB);hostC.AddMap(mapC);
        var probes=Enumerable.Range(0,3).Select(_=>new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,0)).ToArray();
        foreach(var probe in probes)probe.Start();
        int[] ports=probes.Select(x=>((System.Net.IPEndPoint)x.LocalEndpoint).Port).ToArray();
        foreach(var probe in probes)probe.Stop();
        int portA=ports[0],portB=ports[1],portC=ports[2];
        const string keyAB="SYNTHETIC-A-B-PAIRING-SECRET-NEVER-LOG";
        const string keyAC="SYNTHETIC-A-C-PAIRING-SECRET-NEVER-LOG";
        using var loopbackPeers=ClusterService.UseLoopbackLanForTests();
        hostA.UpdateLanConfiguration(portA,new[]{
            new LanPeerConfig{HostId=hostB.Config.LocalHostId,HostName="Synthetic B",Address="127.0.0.1",
                Port=portB,PairingKeyProtected=SecureStore.Protect(keyAB)},
            new LanPeerConfig{HostId=hostC.Config.LocalHostId,HostName="Synthetic C",Address="127.0.0.1",
                Port=portC,PairingKeyProtected=SecureStore.Protect(keyAC)}});
        hostB.UpdateLanConfiguration(portB,new[]{
            new LanPeerConfig{HostId=hostA.Config.LocalHostId,HostName="Synthetic A",Address="127.0.0.1",
                Port=portA,PairingKeyProtected=SecureStore.Protect(keyAB)}});
        hostC.UpdateLanConfiguration(portC,new[]{
            new LanPeerConfig{HostId=hostA.Config.LocalHostId,HostName="Synthetic A",Address="127.0.0.1",
                Port=portA,PairingKeyProtected=SecureStore.Protect(keyAC)}});
        await hostA.BeginLanAsync();await hostB.BeginLanAsync();await hostC.BeginLanAsync();
        var observedA=await hostA.PollPeersAsync();
        var observedB=await hostB.PollPeersAsync();
        var observedC=await hostC.PollPeersAsync();
        Check(observedA.Count==2&&observedA.All(x=>x.IsFresh)&&
              observedB.Single().IsFresh&&observedC.Single().IsFresh,
            "three synthetic Managers poll authenticated LAN status through configured peer lists");
        Check(observedA.Single(x=>x.HostId==hostB.Config.LocalHostId).Snapshot!.Maps.Any(x=>x.Id==mapB.Id.ToString("D"))&&
              observedA.Single(x=>x.HostId==hostC.Config.LocalHostId).Snapshot!.Maps.Any(x=>x.Id==mapC.Id.ToString("D")),
            "authenticated status preserves each synthetic peer's uneven local map inventory");
        Check(hostA.Config.HostRegistry.Hosts.Count(x=>!x.IsLocal&&x.IsPaired)==2&&
              hostA.Config.HostRegistry.Maps.Any(x=>x.MapId==mapB.Id&&
                  x.HostId==hostB.Config.LocalHostId&& !x.LocalConfigurationPresent)&&
              hostA.Config.HostRegistry.Maps.Any(x=>x.MapId==mapC.Id&&
                  x.HostId==hostC.Config.LocalHostId&& !x.LocalConfigurationPresent),
            "authenticated N-host poll reconciles only remote host and map ownership metadata");
        long registryRevision=hostA.Config.ClusterRegistry.Revision;
        await hostA.PollPeersAsync();
        Check(hostA.Config.ClusterRegistry.Revision==registryRevision,
            "unchanged authenticated peer heartbeat does not rewrite registry revision");
        bool wrongIdentityDenied=false;
        try{await PeerStatusClient.Query("127.0.0.1",portA,keyAB,hostB.Config.LocalHostId);}
        catch(InvalidDataException){wrongIdentityDenied=true;}
        Check(wrongIdentityDenied,"LAN client rejects a valid-key response from the wrong pinned Host ID");
        bool wrongSecretDenied=false;
        try {
            await PeerStatusClient.Query("127.0.0.1",portA,
                "INVALID-SYNTHETIC-SHARED-SECRET-12345",hostA.Config.LocalHostId);
        } catch(Exception) {wrongSecretDenied=true;}
        Check(wrongSecretDenied,
            "LAN transport rejects HMAC and encryption with incorrect shared key");
    }

    using(var drafts=new DesktopModDraftBridge(cluster))
    {
        IsOk(await Call(drafts.HandleAsync,"mods.draftStart",new{mapId=map.Id}),
            "local draft opens on registered map");
        var unauthorized=await Call(drafts.HandleAsync,"mods.draftStart",
            new{mapId=Guid.NewGuid()});
        Check(IsError(unauthorized,"UM-MAP-NOT-OWNED"),
            "foreign map cannot start a mod edit draft");
        var started=await Call(drafts.HandleAsync,"mods.draftStart",new{mapId=map.Id});
        IsOk(started,"new draft starts");
        var token=started.GetProperty("data").GetProperty("draftId").GetString()!;
        var staged=await Call(drafts.HandleAsync,"mods.draftStage",
            new{draftId=token,modId="234567",mode="passive"});
        IsOk(staged,"native ModDraftSession accepts valid passive mod");
        Check(staged.GetProperty("data").GetProperty("dirty").GetBoolean(),
            "staging reports modified draft");
        Check(map.Arguments=="-mods=123456",
            "staging never modifies launch args or on-disk Manager config");
        var undo=await Call(drafts.HandleAsync,"mods.draftUndo",new{draftId=token});
        IsOk(undo,"native undo succeeds");
        Check(!undo.GetProperty("data").GetProperty("dirty").GetBoolean(),
            "undo restores unchanged baseline");
        var redo=await Call(drafts.HandleAsync,"mods.draftRedo",new{draftId=token});
        IsOk(redo,"native redo succeeds");
        var saved=await Call(drafts.HandleAsync,"mods.draftSave",new{draftId=token});
        IsOk(saved,"native save commits local Manager config via original CAS backend");
        Check(saved.GetProperty("data").GetProperty("pending").GetBoolean(),
            "save remains pending real ASA restart and load validation");
        var set=ModStateController.Read(map);
        Check(set.Passive.SequenceEqual(new[]{"234567"}) &&
              set.EnabledOrder.SequenceEqual(new[]{"123456","234567"}),
            "passive subset and original -mods enabled order preserved");
        var persisted=new ConfigStore("obsidian-ci",profile).Read().LocalMaps.Single();
        Check(persisted.Arguments.Contains("-passivemods=234567",StringComparison.Ordinal) &&
              persisted.ModChangesPending,
            "configured mod change is persisted without touching server or Store");
        var stale=await Call(drafts.HandleAsync,"mods.draftSave",new{draftId=token});
        Check(IsError(stale,"UM-DRAFT-NOT-FOUND"),
            "consumed draft token cannot be replayed");
        var denied=await Call(drafts.HandleAsync,"mods.deleteAll",new{mapId=map.Id});
        Check(IsError(denied,"UM-METHOD-DENIED"),
            "native draft command allowlist fails closed");
    }

    var iniDirectory=Path.Combine(installation,"ShooterGame","Saved",
        "Config","WindowsServer");
    Directory.CreateDirectory(iniDirectory);
    string iniFile=Path.Combine(iniDirectory,"Game.ini");
    File.WriteAllText(iniFile,"[ServerSettings]"+Environment.NewLine+
        "Example=1"+Environment.NewLine);
    using(var ini=new DesktopIniBridge(cluster))
    {
        var wrong=await Call(ini.HandleAsync,"ini.open",
            new{mapId=map.Id,file="../Game.ini"});
        Check(IsError(wrong,"UM-INI-INVALID-FILE"),
            "INI editor forbids arbitrary path traversal");
        var opened=await Call(ini.HandleAsync,"ini.open",
            new{mapId=map.Id,file="Game.ini"});
        IsOk(opened,"stopped-map-only real INI opens read-only");
        string token=opened.GetProperty("data").GetProperty("token").GetString()!;
        var premature=await Call(ini.HandleAsync,"ini.save",new{token});
        Check(IsError(premature,"UM-INI-PREVIEW-REQUIRED"),
            "INI save fails until explicit hash preview");
        var edited="[ServerSettings]"+Environment.NewLine+
            "Example=2"+Environment.NewLine;
        var preview=await Call(ini.HandleAsync,"ini.preview",
            new{token,content=edited});
        IsOk(preview,"INI hash preview succeeds");
        Check(File.ReadAllText(iniFile).Contains("Example=1"),
            "preview is in-memory only and never edits file");
        var persisted=await Call(ini.HandleAsync,"ini.save",new{token});
        IsOk(persisted,"native INI save performs stopped-state and ETag check");
        Check(File.ReadAllText(iniFile).Contains("Example=2"),
            "local INI actually updated using existing SafeConfigEditor");
        var replay=await Call(ini.HandleAsync,"ini.save",new{token});
        Check(IsError(replay,"UM-INI-DOCUMENT-EXPIRED"),
            "successful native INI transaction cannot be replayed");

        var conflictOpen=await Call(ini.HandleAsync,"ini.open",
            new{mapId=map.Id,file="Game.ini"});
        IsOk(conflictOpen,"INI opens again for optimistic conflict test");
        string conflictToken=conflictOpen.GetProperty("data").GetProperty("token").GetString()!;
        IsOk(await Call(ini.HandleAsync,"ini.preview",
            new{token=conflictToken,content="[ServerSettings]\nExample=3\n"}),
            "second INI preview succeeds");
        File.WriteAllText(iniFile,"[ServerSettings]\nEXTERNAL=1\n");
        var conflict=await Call(ini.HandleAsync,"ini.save",new{token=conflictToken});
        Check(IsError(conflict,"UM-INI-ETAG-MISMATCH"),
            "external INI modification is a distinct ETag conflict and preserves newer bytes");
        Check(File.ReadAllText(iniFile).Contains("EXTERNAL=1"),
            "external change preserved on failed staged save");

        var failedMerge=await Call(ini.HandleAsync,"ini.rebasePreview",
            new{token=conflictToken,content="[ServerSettings]\nExample=3\n"});
        Check(IsError(failedMerge,"UM-INI-MERGE-CONFLICT"),
            "three-way merge refuses structural changes with original writer edits intact");
        Check(File.ReadAllText(iniFile).Contains("EXTERNAL=1"),
            "rejected three-way merge does not write to the disk");

        // Distinct INI keys and comments are preserved when another process
        // changes an unrelated value while this operator edits Example.
        File.WriteAllText(iniFile,"[ServerSettings]\nExample=2\nForeign=old\n; keep comment\n");
        var rebasedOpen=await Call(ini.HandleAsync,"ini.open",
            new{mapId=map.Id,file="Game.ini"});
        IsOk(rebasedOpen,"three-way INI fixture opens baseline without writes");
        string mergeToken=rebasedOpen.GetProperty("data").GetProperty("token").GetString()!;
        string operatorEdit="[ServerSettings]\nExample=3\nForeign=old\n; keep comment\n";
        IsOk(await Call(ini.HandleAsync,"ini.preview",
            new{token=mergeToken,content=operatorEdit}),
            "operator INI change receives ordinary hash preview");
        File.WriteAllText(iniFile,"[ServerSettings]\nExample=2\nForeign=new\n; keep comment\n");
        Check(IsError(await Call(ini.HandleAsync,"ini.save",new{token=mergeToken}),
                "UM-INI-ETAG-MISMATCH"),
            "other writer triggers ETag rejection before merge");
        var goodMerge=await Call(ini.HandleAsync,"ini.rebasePreview",
            new{token=mergeToken,content=operatorEdit});
        IsOk(goodMerge,"disjoint INI modifications merge without writing");
        string rebasedText=goodMerge.GetProperty("data").GetProperty("content").GetString()!;
        Check(rebasedText.Contains("Example=3")&&rebasedText.Contains("Foreign=new")&&
              rebasedText.Contains("; keep comment")&&
              File.ReadAllText(iniFile).Contains("Example=2"),
            "merge previews the operator change alongside the newer disk value and comment");
        Check(IsError(await Call(ini.HandleAsync,"ini.save",new{token=mergeToken}),
                "UM-INI-PREVIEW-REQUIRED"),
            "rebased INI cannot save before a new explicit hash preview");
        IsOk(await Call(ini.HandleAsync,"ini.preview",
            new{token=mergeToken,content=rebasedText}),
            "operator must approve fresh rebased SHA preview");
        IsOk(await Call(ini.HandleAsync,"ini.save",new{token=mergeToken}),
            "newly previewed merged INI is atomically committed");
        Check(File.ReadAllText(iniFile).Contains("Example=3")&&
              File.ReadAllText(iniFile).Contains("Foreign=new"),
            "independent disk and user values survive the approved merge");
        // External comment edits are independent of an operator changing a
        // different existing value. They must survive rebase byte-for-byte.
        var commentsOpen=await Call(ini.HandleAsync,"ini.open",
            new{mapId=map.Id,file="Game.ini"});
        IsOk(commentsOpen,"comment-only concurrent edit opens its old INI baseline");
        string commentsToken=commentsOpen.GetProperty("data").GetProperty("token").GetString()!;
        string commentsBaseline=File.ReadAllText(iniFile);
        File.WriteAllText(iniFile,commentsBaseline.Replace("; keep comment","; changed by external editor"));
        var commentsRebase=await Call(ini.HandleAsync,"ini.rebasePreview",
            new{token=commentsToken,content=commentsBaseline.Replace("Example=3","Example=7")});
        IsOk(commentsRebase,"external comment change is nonconflicting with independent INI value edit");
        string commentsMerged=commentsRebase.GetProperty("data").GetProperty("content").GetString()!;
        Check(commentsMerged.Contains("Example=7")&&
              commentsMerged.Contains("; changed by external editor")&&
              File.ReadAllText(iniFile).Contains("Example=3"),
            "three-way rebase keeps external comments and remains a no-write preview");
        IsOk(await Call(ini.HandleAsync,"ini.close",new{token=commentsToken}),
            "close comment rebase without saving or touching ASA process");
        File.WriteAllText(iniFile,commentsBaseline);
        var collisionOpen=await Call(ini.HandleAsync,"ini.open",
            new{mapId=map.Id,file="Game.ini"});
        string collisionToken=collisionOpen.GetProperty("data").GetProperty("token").GetString()!;
        File.WriteAllText(iniFile,"[ServerSettings]\nExample=4\nForeign=new\n; keep comment\n");
        var collision=await Call(ini.HandleAsync,"ini.rebasePreview",
            new{token=collisionToken,content=rebasedText.Replace("Example=3","Example=5")});
        Check(IsError(collision,"UM-INI-MERGE-CONFLICT")&&
              File.ReadAllText(iniFile).Contains("Example=4"),
            "same INI key edited by both writers conflicts and preserves newer disk bytes");
        File.WriteAllText(iniFile,"[DifferentSection]\nExample=3\nForeign=new\n; keep comment\n");
        var sectionMoved=await Call(ini.HandleAsync,"ini.rebasePreview",
            new{token=collisionToken,content=rebasedText.Replace("Example=3","Example=5")});
        Check(IsError(sectionMoved,"UM-INI-MERGE-CONFLICT")&&
              File.ReadAllText(iniFile).Contains("[DifferentSection]"),
            "external section rename is never silently accepted as an INI rebase");

        // This test's older INI read fixture intentionally had no server EXE.
        // A genuinely FRESH install must have its registered ASA binary before
        // the new create endpoint can be approved. Materialize only synthetic
        // path bytes; no executable is ever launched by this fixture.
        bool hadServerExe=File.Exists(map.ExecutablePath);
        Directory.CreateDirectory(Path.GetDirectoryName(map.ExecutablePath)!);
        if(!hadServerExe)
            File.WriteAllText(map.ExecutablePath,
                "Isolated ASA executable path fixture; NEVER executed");
        // Missing Game.ini on a new stopped ASA installation is a supported
        // explicit operation, never an implicit file write on open.
        File.Delete(iniFile);
        var historicalIniSibling=new LocalMap{
            Id=Guid.NewGuid(),Name="Synthetic historical sibling",
            InstallationPath=installation,
            HistoricalMapKey="ImportedOldMap_WP"
        };
        cluster.AddMap(historicalIniSibling);
        var sharedDenied=await Call(ini.HandleAsync,"ini.createGamePreview",
            new{mapId=map.Id});
        Check(IsError(sharedDenied,"UM-INI-CREATE-SHARED-STORE") &&
              !File.Exists(iniFile),
            "historical Store sibling returns precise denied code without creating Game.ini");
        cluster.RemoveMap(historicalIniSibling.Id);
        string originalCreateArgs=map.Arguments;
        map.Arguments=originalCreateArgs+
            " -SessionName=\"Untamed Store -usestore Weekend\"";
        var namedGameIni=await Call(ini.HandleAsync,"ini.createGamePreview",
            new{mapId=map.Id});
        IsOk(namedGameIni,
            "fresh Game.ini setup accepts quoted Store words inside SessionName");
        map.Arguments=originalCreateArgs+" -clusterid=Unregistered";
        var forbiddenGameIni=await Call(ini.HandleAsync,"ini.createGamePreview",
            new{mapId=map.Id});
        Check(IsError(forbiddenGameIni,"UM-INI-CREATE-FRESH-ONLY")&&
              !File.Exists(iniFile),
            "fresh Game.ini setup rejects actual unenrolled cluster switch");
        map.Arguments=originalCreateArgs;
        var missing=await Call(ini.HandleAsync,"ini.open",
            new{mapId=map.Id,file="Game.ini"});
        Check(IsError(missing,"UM-INI-GAME-MISSING")&&!File.Exists(iniFile),
            "opening a missing Game.ini never silently creates a file");
        var creation=await Call(ini.HandleAsync,"ini.createGamePreview",
            new{mapId=map.Id});
        IsOk(creation,"fresh local Game.ini creation is read-only preflighted");
        Check(!File.Exists(iniFile) &&
              !creation.GetProperty("data").GetProperty("createsFile").GetBoolean(),
            "creation preview leaves installation untouched");
        string creationToken=creation.GetProperty("data").GetProperty("token").GetString()!;
        // A second new ASA map has its OWN installation and Game.ini. Its
        // preview must not revoke the first map's one-shot creation approval.
        string secondIniRoot=Path.Combine(root,"second-gameini-synthetic");
        string secondIniExe=Path.Combine(secondIniRoot,"ShooterGame",
            "Binaries","Win64","ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(secondIniExe)!);
        File.WriteAllText(secondIniExe,"synthetic second ASA EXE never executed");
        var secondIniMap=new LocalMap{
            Id=Guid.NewGuid(),Name="INI second synthetic",
            InstallationPath=secondIniRoot,ExecutablePath=secondIniExe,
            Arguments="TheCenter_WP?listen"
        };
        cluster.AddMap(secondIniMap);
        var secondCreation=await Call(ini.HandleAsync,"ini.createGamePreview",
            new{mapId=secondIniMap.Id});
        IsOk(secondCreation,
            "independent second local ASA has separately scoped Game.ini preview");
        string secondCreationToken=secondCreation.GetProperty("data")
            .GetProperty("token").GetString()!;
        Check(secondCreationToken!=creationToken,
            "multi-map Game.ini approvals never share a token");
        var declinedCreate=await Call(ini.HandleAsync,"ini.createGameApply",
            new{token=creationToken,confirm=false});
        Check(IsError(declinedCreate,"UM-INI-CREATE-EXPLICIT-APPROVAL") &&
              !File.Exists(iniFile),
            "missing Game.ini cannot be created without final approval");
        var createdGame=await Call(ini.HandleAsync,"ini.createGameApply",
            new{token=creationToken,confirm=true});
        IsOk(createdGame,"approved fresh Game.ini created using atomic CreateNew");
        var secondCreated=await Call(ini.HandleAsync,"ini.createGameApply",
            new{token=secondCreationToken,confirm=true});
        IsOk(secondCreated,
            "second Game.ini create survives first map's approved filesystem write");
        string secondCreatedIni=Path.Combine(secondIniRoot,
            "ShooterGame","Saved","Config","WindowsServer","Game.ini");
        Check(File.Exists(secondCreatedIni)&&
              File.ReadAllText(secondCreatedIni)
                  .Contains("ShooterGameMode",StringComparison.Ordinal),
            "second ASA receives only its own independent Game.ini");
        var replaySecond=await Call(ini.HandleAsync,"ini.createGameApply",
            new{token=secondCreationToken,confirm=true});
        Check(IsError(replaySecond,"UM-INI-CREATE-EXPLICIT-APPROVAL"),
            "second-map Game.ini approval cannot be replayed after creation");
        Check(createdGame.GetProperty("data").GetProperty("created").GetBoolean() &&
              File.ReadAllText(iniFile).Contains("ShooterGameMode",StringComparison.Ordinal),
            "native Game.ini creation really writes a valid UTF-8 starter section");
        byte[] createdOriginal=File.ReadAllBytes(iniFile);
        var replayCreate=await Call(ini.HandleAsync,"ini.createGameApply",
            new{token=creationToken,confirm=true});
        Check(!replayCreate.GetProperty("ok").GetBoolean() &&
              createdOriginal.SequenceEqual(File.ReadAllBytes(iniFile)),
            "consumed create token cannot overwrite the newly created Game.ini");
        var alreadyExists=await Call(ini.HandleAsync,"ini.createGamePreview",
            new{mapId=map.Id});
        Check(IsError(alreadyExists,"UM-INI-GAME-ALREADY-EXISTS") &&
              createdOriginal.SequenceEqual(File.ReadAllBytes(iniFile)),
            "existing Game.ini returns precise conflict code and cannot be overwritten");
        var reopened=await Call(ini.HandleAsync,"ini.open",
            new{mapId=map.Id,file="Game.ini"});
        IsOk(reopened,"newly created Game.ini opens in the original ETag editor");
        cluster.RemoveMap(secondIniMap.Id);
        if(!hadServerExe)File.Delete(map.ExecutablePath);
    }

    // A real existing ASA install may be registered without constructing
    // fake RCON credentials, ports, Store conversion or launching a server.
    string candidate=Path.Combine(root,"asa-import");
    string binaries=Path.Combine(candidate,"ShooterGame","Binaries","Win64");
    Directory.CreateDirectory(binaries);
    string candidateExe=Path.Combine(binaries,"ArkAscendedServer.exe");
    File.WriteAllText(candidateExe,"FAKE EXE CONTENT FOR MANAGER IMPORT TEST ONLY");
    using(var importer=new DesktopLocalMapRegistryBridge(cluster))
    {
        var invalid=await Call(importer.HandleAsync,"servers.inspectLocal",
            new{name="Ragnarok",root=Path.Combine(root,"does-not-exist")});
        Check(IsError(invalid,"UM-IMPORT-DIRECTORY-MISSING"),
            "import refuses missing local installation");
        var prepared=await Call(importer.HandleAsync,"servers.inspectLocal",
            new{name="Ragnarok",root=candidate});
        IsOk(prepared,"new local ASA installation is probed without modifying it");
        var token=prepared.GetProperty("data").GetProperty("importToken").GetString()!;
        Check(!cluster.Config.LocalMaps.Any(m=>m.Name=="Ragnarok"),
            "preflight never writes the Manager profile");
        var confirmed=await Call(importer.HandleAsync,"servers.importLocal",
            new{importToken=token});
        IsOk(confirmed,"confirmed local ASA import registers a Manager profile");
        var newMap=cluster.Config.LocalMaps.Single(m=>m.Name=="Ragnarok");
        Check(newMap.ExecutablePath==candidateExe &&
              newMap.GamePort==0 && newMap.RconPort==0 &&
              !newMap.AutoStart && !newMap.StartWithAsaApi,
            "import never guesses ports, RCON, ASA loader or autostart");
        Check(File.ReadAllText(candidateExe).StartsWith("FAKE EXE CONTENT"),
            "Manager import leaves ASA binary and saved files unchanged");
        var replay=await Call(importer.HandleAsync,"servers.importLocal",
            new{importToken=token});
        Check(IsError(replay,"UM-IMPORT-EXPIRED"),
            "confirmed import cannot reuse consumed preview token");
        var duplicate=await Call(importer.HandleAsync,"servers.inspectLocal",
            new{name="Ragnarok",root=candidate});
        Check(!duplicate.GetProperty("ok").GetBoolean(),
            "duplicate local map name or overlapping install is rejected");
    }

    // Validate real native CurseForge provider response handling without
    // network access or exposing the DPAPI credential to any JavaScript reply.
    const string fakeKey="SYNTHETIC_CURSEFORGE_NATIVE_BRIDGE_TEST_KEY";
    cluster.UpdateSettings(settings =>
        settings.CurseForgeApiKeyProtected=SecureStore.Protect(fakeKey));
    var provider=new FakeCatalogProvider(fakeKey);
    using(var fakeHttp=new HttpClient(provider))
    using(var discovery=new DesktopCurseForgeDiscoveryBridge(cluster,fakeHttp))
    {
        var invalidSort=await Call(discovery.HandleAsync,"curseforge.browse",
            new{term="Creators",sort=700});
        Check(IsError(invalidSort,"UM-INVALID-SORT"),
            "invalid official CurseForge sort is rejected before API calls");
        Check(provider.Requests==0,"invalid browse request never reaches provider");

        var categories=await Call(discovery.HandleAsync,"curseforge.categories");
        IsOk(categories,"official ASA category endpoint returns sanitized metadata");
        Check(categories.GetProperty("data").GetProperty("categories")[0]
            .GetProperty("name").GetString()=="Structures",
            "ASA categories are correctly delivered through native bridge");

        var feeds=await Call(discovery.HandleAsync,"curseforge.featured");
        IsOk(feeds,"official featured/popular/recently-updated endpoint");
        Check(feeds.GetProperty("data").GetProperty("popular").GetArrayLength()==1,
            "featured categories preserve distinct provider grouping");

        var browse=await Call(discovery.HandleAsync,"curseforge.browse",
            new{term="Creator",categoryId=100,sort=2,descending=true,page=0});
        IsOk(browse,"provider-backed filtered browse succeeds");
        Check(browse.GetProperty("data").GetProperty("mods")[0]
            .GetProperty("id").GetInt32()==928548 &&
            provider.LastQuery.Contains("categoryId=100") &&
            provider.LastQuery.Contains("sortField=2"),
            "category and sort options reach exact official ASA search");
        Check(!browse.GetRawText().Contains(fakeKey,StringComparison.Ordinal),
            "browse result never serializes the protected API credential");

        var summary=await Call(discovery.HandleAsync,"curseforge.summary",
            new{modId=928548});
        IsOk(summary,"lightweight official CurseForge summary resolves configured mod title without file downloads");
        Check(summary.GetProperty("data").GetProperty("mod").GetProperty("id").GetInt32()==928548 &&
              summary.GetProperty("data").GetProperty("evidence").GetString()=="OFFICIAL_ASA_PROJECT_SUMMARY_ONLY" &&
              !summary.GetRawText().Contains(fakeKey,StringComparison.Ordinal),
            "mod summary returns provider-confirmed identity and never leaks API credentials");
        var invalidSummary=await Call(discovery.HandleAsync,"curseforge.summary",
            new{modId=0});
        Check(IsError(invalidSummary,"UM-INVALID-PROJECT"),
            "mod summary refuses untrusted or invalid project IDs before network calls");

        var project=await Call(discovery.HandleAsync,"curseforge.project",
            new{modId=928548});
        IsOk(project,"provider-backed project description and exact files");
        var meta=project.GetProperty("data");
        Check(meta.GetProperty("description").GetString()=="Synthetic safe description" &&
            meta.GetProperty("files")[0].GetProperty("dependencies")[0]
                .GetProperty("type").GetString()=="RequiredDependency",
            "plain-text official project description and file relation type preserved");

        var filePage=await Call(discovery.HandleAsync,"curseforge.files",
            new{modId=928548,page=0});
        IsOk(filePage,"version files paginated without downloads");
        Check(provider.DownloadRequests==0 &&
            !filePage.GetRawText().Contains(fakeKey,StringComparison.Ordinal),
            "native files endpoint cannot request file binaries or leak key");

        var invalidProject=await Call(discovery.HandleAsync,"curseforge.project",
            new{modId=7});
        Check(IsError(invalidProject,"UM-INVALID-PROJECT"),
            "too-small project ID is rejected before HTTP");
        var forbidden=await Call(discovery.HandleAsync,"curseforge.install",
            new{modId=928548});
        Check(IsError(forbidden,"UM-METHOD-DENIED"),
            "no mod install method is exposed to JavaScript");
    }

    // Exercise the WebView2-to-Core SteamCMD wizard with a synthetic
    // asynchronous Windows process adapter. Real SteamCMD/ASA is never started.
    string freshRoot=Path.Combine(root,"fresh-asa-empty");
    string steamDir=Path.Combine(root,"steam-toolkit");
    Directory.CreateDirectory(steamDir);
    string steamExe=Path.Combine(steamDir,"steamcmd.exe");
    File.WriteAllText(steamExe,"SYNTHETIC STEAMCMD EXE - DO NOT EXECUTE");
    int fakeRuns=0;
    IProgress<SteamInstallProgress>? lateFreshInstallProgress=null;
    async Task<SteamFreshInstallReceipt> SyntheticSteamRunner(
        SteamFreshInstallRequest request,LocalMap[] existing,
        IProgress<SteamInstallProgress>? progress,CancellationToken cancellationToken)
    {
        fakeRuns++;
        lateFreshInstallProgress=progress;
        Check(request.MapName=="Valguero" && request.SessionName=="" &&
              !existing.Any(m=>m.Name=="Valguero"),
            "new Steam installer begins without ports, session or existing map");
        progress?.Report(new("download","SYNTHETIC provider stage",32.5));
        await Task.Delay(80,cancellationToken);
        string binaries=Path.Combine(request.InstallRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binaries);
        string targetExe=Path.Combine(binaries,"ArkAscendedServer.exe");
        File.WriteAllText(targetExe,"SYNTHETIC ASA EXE - NEVER EXECUTE");
        string manifestDir=Path.Combine(request.InstallRoot,"steamapps");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir,"appmanifest_2430930.acf"),
            "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123456\"\n}\n");
        progress?.Report(new("complete","SYNTHETIC fixtures verified",100));
        return new(request.MapId,request.InstallRoot,targetExe,"123456",
            DateTimeOffset.UtcNow,request.SteamCmdExecutable,
            AsaSteamMapCatalog.LevelFor(request.MapName));
    }

    int selectedFolders=0;
    using(var steamBridge=new DesktopSteamFreshInstallBridge(cluster,
        SyntheticSteamRunner,()=>{selectedFolders++;return freshRoot;},
        // GitHub Windows runners do not guarantee 35 GiB free on D:.
        // Inject capacity in the synthetic fixture; production still probes
        // the actual fixed drive and Core rechecks during a real download.
        availableFreeBytes:_=>100L*1024*1024*1024))
    {
        var folder=await Call(steamBridge.HandleAsync,"steam.freshBrowse");
        IsOk(folder,"native Windows ASA folder picker returns an operator-selected local root");
        Check(folder.GetProperty("data").GetProperty("selected").GetBoolean() &&
              folder.GetProperty("data").GetProperty("installRoot").GetString()==
                  Path.GetFullPath(freshRoot) &&
              selectedFolders==1 && !Directory.Exists(freshRoot) &&
              fakeRuns==0,
            "native folder choice does not initiate SteamCMD or mutate installation files");
        var badPath=await Call(steamBridge.HandleAsync,"steam.freshPreflight",
            new{mapName="Valguero",installRoot=@"\\foreign\server",
                steamExe,allowSteamCmdDownload=false});
        Check(IsError(badPath,"UM-FRESH-INVALID-PATH"),
            "new Steam download shows specific invalid-drive error for foreign/UNC install root");
        Check(fakeRuns==0,"preflight never launches synthetic SteamCMD adapter");

        // The safety invariant is still enforced: an undersized disk is
        // rejected *before* SteamCMD starts or an installation root is made.
        using(var lowDiskBridge=new DesktopSteamFreshInstallBridge(cluster,
            SyntheticSteamRunner,()=>freshRoot,
            availableFreeBytes:_=>34L*1024*1024*1024))
        {
            var lowDisk=await Call(lowDiskBridge.HandleAsync,"steam.freshPreflight",
                new{mapName="Valguero",installRoot=freshRoot,
                    steamExe,allowSteamCmdDownload=false});
            Check(!lowDisk.GetProperty("ok").GetBoolean() &&
                   lowDisk.GetProperty("error").GetString()?.
                     StartsWith("UM-FRESH-LOW-DISK-SPACE|availableGiB=34|requiredGiB=35",
                         StringComparison.Ordinal)==true &&
                  fakeRuns==0 && !Directory.Exists(freshRoot),
                "insufficient free disk blocks ASA download before side effects");
        }

        // Operator need not type steamcmd.exe path: native Manager config
        // resolves it without ever exposing the path to JavaScript.
        cluster.UpdateSettings(config=>config.SteamCmdExecutable=steamExe);
        var prepared=await Call(steamBridge.HandleAsync,"steam.freshPreflight",
            new{mapName="Valguero",installRoot=freshRoot,
                steamExe="",allowSteamCmdDownload=false,explicitRetry=false});
        IsOk(prepared,"official local ASA fresh preflight resolves native SteamCMD default");
        Check(prepared.GetProperty("data")
            .GetProperty("usingManagerDefaultSteamCmd").GetBoolean(),
            "native default SteamCMD path is used with zero manual operator input");
        Check(!Directory.Exists(freshRoot),
            "new installation preflight never creates destination");
        string token=prepared.GetProperty("data").GetProperty("token").GetString()!;
        var refused=await Call(steamBridge.HandleAsync,"steam.freshStart",
            new{token,confirm=false});
        Check(IsError(refused,"UM-FRESH-EXPLICIT-CONFIRMATION-REQUIRED") &&
              fakeRuns==0,"SteamCMD cannot start without explicit confirmation");

        var started=await Call(steamBridge.HandleAsync,"steam.freshStart",
            new{token,confirm=true});
        IsOk(started,"approved SteamCMD job is scheduled separately from UI request");
        string jobId=started.GetProperty("data").GetProperty("jobId").GetString()!;
        var repeat=await Call(steamBridge.HandleAsync,"steam.freshStart",
            new{token,confirm=true});
        Check(IsError(repeat,"UM-FRESH-PREFLIGHT-EXPIRED"),
            "one-time launch token cannot replay");
        JsonElement progressState=default;
        for(int attempt=0;attempt<40;attempt++)
        {
            progressState=await Call(steamBridge.HandleAsync,"steam.freshStatus",
                new{jobId});
            if(progressState.GetProperty("data").GetProperty("verified").GetBoolean())
                break;
            await Task.Delay(40);
        }
        IsOk(progressState,"native asynchronous SteamCMD job status is readable");
        Check(progressState.GetProperty("data").GetProperty("verified").GetBoolean() &&
              fakeRuns==1 &&
              !cluster.Config.LocalMaps.Any(m=>m.Name=="Valguero"),
            "finished SteamCMD files never silently register or start a server");
        Check(lateFreshInstallProgress!=null,
            "synthetic fresh SteamCMD worker retains the source progress callback");
        lateFreshInstallProgress!.Report(new("download",
            "DELAYED_PROGRESS_AFTER_VERIFIED_STEAM_INSTALL",5));
        var stableVerified=await Call(steamBridge.HandleAsync,
            "steam.freshStatus",new{jobId});
        IsOk(stableVerified,"verified fresh install survives late worker progress");
        var verifiedState=stableVerified.GetProperty("data");
        Check(verifiedState.GetProperty("verified").GetBoolean()&&
              verifiedState.GetProperty("stage").GetString()=="verified"&&
              verifiedState.GetProperty("percent").GetDouble()==100&&
              fakeRuns==1,
            "late SteamCMD progress cannot replace a finished install receipt");
        var registered=await Call(steamBridge.HandleAsync,"steam.freshRegister",
            new{jobId,confirm=true});
        IsOk(registered,"explicit registration succeeds after verified file and manifest");
        var newMap=cluster.Config.LocalMaps.Single(x=>x.Name=="Valguero");
        Check(newMap.GamePort==0 && newMap.QueryPort==0 && newMap.RconPort==0 &&
              !newMap.AutoStart && !newMap.StartWithAsaApi &&
              newMap.Arguments=="",
            "Steam installation preserves download-before-configuration semantics");
        Check(File.ReadAllText(newMap.ExecutablePath).Contains("SYNTHETIC ASA EXE"),
            "Steam installation registration never modifies server EXE content");
        var again=await Call(steamBridge.HandleAsync,"steam.freshRegister",
            new{jobId,confirm=true});
        Check(IsError(again,"UM-FRESH-NOT-VERIFIED"),
            "completed Steam registration is not replayable");
        Check(!File.Exists(Path.Combine(freshRoot,"ShooterGame","Saved","SavedArks","Valguero_WP.ark")),
            "fresh download never fabricates world saves or Store data");
        lateFreshInstallProgress!.Report(new("download",
            "DELAYED_PROGRESS_AFTER_REGISTERED_STEAM_INSTALL",1));
        var stableRegistered=await Call(steamBridge.HandleAsync,
            "steam.freshStatus",new{jobId});
        IsOk(stableRegistered,"registered fresh install remains addressable by original job ID");
        Check(stableRegistered.GetProperty("data").GetProperty("registered").GetBoolean()&&
              stableRegistered.GetProperty("data").GetProperty("stage").GetString()=="registered"&&
              fakeRuns==1,
            "late SteamCMD progress cannot regress completed native map registration");
        var current=await Call(steamBridge.HandleAsync,"steam.freshCurrent");
        Check(current.GetProperty("data").GetProperty("current")
            .GetProperty("registered").GetBoolean(),
            "manager can restore verified native Steam job status after UI navigation");
    }

    // A failed, never-executed synthetic install must remain failed after
    // its worker publishes stale progress. No SteamCMD child or ASA is opened.
    {
        string failedRoot=Path.Combine(root,"fresh-asa-synthetic-failure");
        await using var failedCluster=new ClusterService("fresh-error-progress-fixture",
            Path.Combine(root,"fresh-error-manager"),
            _=>new ProcessCheck(false,"No ASA in synthetic failure test",null,true));
        IProgress<SteamInstallProgress>? delayedFailureProgress=null;
        int failedAttempts=0;
        using var failedBridge=new DesktopSteamFreshInstallBridge(failedCluster,
            async (request,maps,progress,token)=>{
                failedAttempts++;
                delayedFailureProgress=progress;
                progress?.Report(new("download","SYNTHETIC FAILURE BEFORE FILES",15));
                await Task.Delay(30,token);
                throw new IOException("SYNTHETIC STEAM INSTALL FAILURE");
            },
            ()=>failedRoot,availableFreeBytes:_=>100L*1024*1024*1024);
        var plan=await Call(failedBridge.HandleAsync,"steam.freshPreflight",
            new{mapName="Valguero",installRoot=failedRoot,steamExe,
                allowSteamCmdDownload=false,explicitRetry=false});
        IsOk(plan,"failed Steam progress fixture has an independent approved preflight");
        var begun=await Call(failedBridge.HandleAsync,"steam.freshStart",
            new{token=plan.GetProperty("data").GetProperty("token").GetString(),
                confirm=true});
        IsOk(begun,"synthetic failed Steam job receives a real native task ID");
        string failureJobId=begun.GetProperty("data").GetProperty("jobId").GetString()!;
        JsonElement failedState=default;
        for(int attempt=0;attempt<80;attempt++)
        {
            failedState=await Call(failedBridge.HandleAsync,"steam.freshStatus",
                new{jobId=failureJobId});
            if(failedState.GetProperty("data").GetProperty("errorCode")
                .ValueKind==JsonValueKind.String)break;
            await Task.Delay(30);
        }
        IsOk(failedState,"failed fresh SteamCMD job is observable without auto-retry");
        Check(failedState.GetProperty("data").GetProperty("stage").GetString()=="failed"&&
              failedState.GetProperty("data").GetProperty("errorCode").GetString()
                =="UM-FRESH-INSTALL-FAILED"&&failedAttempts==1,
            "failed synthetic SteamCMD installation reports terminal failure once");
        Check(delayedFailureProgress!=null,
            "failed Steam fixture captured its worker's own progress callback");
        delayedFailureProgress!.Report(new("download",
            "DELAYED_PROGRESS_AFTER_FAILED_STEAM_INSTALL",91));
        var preserved=await Call(failedBridge.HandleAsync,"steam.freshStatus",
            new{jobId=failureJobId});
        IsOk(preserved,"failed Steam install remains observable after stale progress");
        Check(preserved.GetProperty("data").GetProperty("stage").GetString()=="failed"&&
              preserved.GetProperty("data").GetProperty("errorCode").GetString()
                =="UM-FRESH-INSTALL-FAILED"&&failedAttempts==1&&
              !Directory.Exists(failedRoot),
            "late native SteamCMD progress cannot hide failure or start another download");
    }

    // Completed SteamCMD install after Manager restart: the memory-only job
    // disappeared, but the SOURCE-PINNED journal and local binaries survived.
    // This fixture creates only synthetic files under a unique TEMP root and
    // one GUID-derived journal under the isolated CI runner's ProgramData.
    {
        string recoveredRoot=Path.Combine(root,"fresh-completed-after-restart");
        string recoveredExe=Path.Combine(recoveredRoot,"ShooterGame","Binaries",
            "Win64","ArkAscendedServer.exe");
        string recoveredManifest=Path.Combine(recoveredRoot,"steamapps",
            "appmanifest_2430930.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(recoveredExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(recoveredManifest)!);
        const string recoveryMap="Synthetic Completed Recovery";
        const string recoveryLevel="SyntheticCompleted_WP";
        Guid recoveryMapId=Guid.NewGuid();
        File.WriteAllText(recoveredExe,"SYNTHETIC VERIFIED COMPLETED ASA EXE");
        File.WriteAllText(recoveredManifest,
            "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123456\"\n}\n");
        string journalKey=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(recoveredRoot.ToUpperInvariant())));
        string journalFile=Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
            "UntamedManager","FreshSteamInstall-v1",journalKey+".json");
        Directory.CreateDirectory(Path.GetDirectoryName(journalFile)!);
        File.WriteAllText(journalFile,JsonSerializer.Serialize(new {
            MapId=recoveryMapId,InstallRoot=recoveredRoot,SteamExe=steamExe,
            Phase="complete",ChangedUtc=DateTimeOffset.UtcNow,
            MapName=recoveryMap,MapLevel=recoveryLevel,
            BinarySha256=Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(recoveredExe))),
            ManifestSha256=Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(recoveredManifest)))
        }));
        try
        {
            int downloadsBeforeRecovery=fakeRuns;
            await using var recoveryCluster=new ClusterService("steam-recovery-native",
                Path.Combine(root,"steam-recovery-profile"),
                _=>new ProcessCheck(false,"Synthetic stopped only",null,true));
            using var recoveredBridge=new DesktopSteamFreshInstallBridge(recoveryCluster,
                SyntheticSteamRunner,availableFreeBytes:_=>100L*1024*1024*1024);
            string pinnedJournalBytes=File.ReadAllText(journalFile);
            File.WriteAllText(journalFile,JsonSerializer.Serialize(new{
                MapId=recoveryMapId,InstallRoot=recoveredRoot,SteamExe=steamExe,
                Phase="complete",ChangedUtc=DateTimeOffset.UtcNow,
                MapName=recoveryMap,MapLevel=recoveryLevel
            }));
            var legacyJournal=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverPreview",new{
                    mapName=recoveryMap,installRoot=recoveredRoot,
                    customLevel=recoveryLevel
                });
            Check(IsError(legacyJournal,"UM-FRESH-RECOVERY-UNPINNED-COMPLETION") &&
                  recoveryCluster.Config.LocalMaps.Count==0,
                "unhashed historical completion is not treated as verified executable ownership");
            File.WriteAllText(journalFile,pinnedJournalBytes);
            var preview=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverPreview",new{
                    mapName=recoveryMap,installRoot=recoveredRoot,
                    customLevel=recoveryLevel
                });
            IsOk(preview,"completed SteamCMD journal can be previewed after independent Manager restart");
            string approvalToken=preview.GetProperty("data")
                .GetProperty("token").GetString()!;
            Check(preview.GetProperty("data").GetProperty("mapId").GetGuid()==
                    recoveryMapId && fakeRuns==downloadsBeforeRecovery,
                "recovery preview preserves exact map GUID without another SteamCMD process");

            var unapproved=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverRegister",new{
                    token=approvalToken,confirm=true,typedApproval="NO"
                });
            Check(IsError(unapproved,"UM-FRESH-RECOVERY-APPROVAL-REQUIRED") &&
                !recoveryCluster.Config.LocalMaps.Any(m=>m.Id==recoveryMapId),
                "typed native consent is required before recovering a registered map");

            File.AppendAllText(recoveredExe,"TAMPER");
            var changed=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverRegister",new{
                    token=approvalToken,confirm=true,
                    typedApproval="ASA TELEPÍTÉS ÁTVÉTEL"
                });
            Check(IsError(changed,"UM-FRESH-RECOVERY-FILES-CHANGED") &&
                !recoveryCluster.Config.LocalMaps.Any(m=>m.Id==recoveryMapId),
                "changed EXE invalidates a one-use recovery token without writing the Manager profile");
            File.WriteAllText(recoveredExe,"SYNTHETIC VERIFIED COMPLETED ASA EXE");

            string originalManifest=File.ReadAllText(recoveredManifest);
            var manifestPreview=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverPreview",new{
                    mapName=recoveryMap,installRoot=recoveredRoot,
                    customLevel=recoveryLevel
                });
            IsOk(manifestPreview,"unchanged binary and manifest can be re-previewed");
            string manifestToken=manifestPreview.GetProperty("data")
                .GetProperty("token").GetString()!;
            File.AppendAllText(recoveredManifest,"TAMPERED_MANIFEST");
            var manifestChanged=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverRegister",new{
                    token=manifestToken,confirm=true,
                    typedApproval="ASA TELEPÍTÉS ÁTVÉTEL"
                });
            Check(IsError(manifestChanged,"UM-FRESH-RECOVERY-FILES-CHANGED") &&
                  recoveryCluster.Config.LocalMaps.Count==0,
                "modified Steam appmanifest invalidates previously approved recovery without Manager writes");
            File.WriteAllText(recoveredManifest,originalManifest);

            var freshPreview=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverPreview",new{
                    mapName=recoveryMap,installRoot=recoveredRoot,
                    customLevel=recoveryLevel
                });
            IsOk(freshPreview,"restored completed journal has a new approved recovery preview");
            approvalToken=freshPreview.GetProperty("data").GetProperty("token").GetString()!;
            var applied=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverRegister",new{
                    token=approvalToken,confirm=true,
                    typedApproval="ASA TELEPÍTÉS ÁTVÉTEL"
                });
            IsOk(applied,"previously downloaded ASA registered without a second download or start");
            var recoveredMap=recoveryCluster.Config.LocalMaps.Single(m=>m.Id==recoveryMapId);
            Check(recoveredMap.Name==recoveryMap && recoveredMap.GamePort==0 &&
                  recoveredMap.QueryPort==0 && !recoveredMap.AutoStart &&
                  !recoveredMap.StartWithAsaApi &&
                  recoveredMap.FreshInstallMapLevel==recoveryLevel &&
                  fakeRuns==downloadsBeforeRecovery &&
                  File.ReadAllText(recoveredExe)=="SYNTHETIC VERIFIED COMPLETED ASA EXE",
                "independent completed recovery preserves GUID and ASA binary while leaving initial configuration pending");
            var replay=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverRegister",new{
                    token=approvalToken,confirm=true,
                    typedApproval="ASA TELEPÍTÉS ÁTVÉTEL"
                });
            Check(IsError(replay,"UM-FRESH-RECOVERY-APPROVAL-REQUIRED") &&
                  recoveryCluster.Config.LocalMaps.Count(m=>m.Id==recoveryMapId)==1,
                "completed Steam recovery consent is never replayable");
            var duplicate=await Call(recoveredBridge.HandleAsync,
                "steam.freshRecoverPreview",new{
                    mapName=recoveryMap,installRoot=recoveredRoot,
                    customLevel=recoveryLevel
                });
            Check(IsError(duplicate,"UM-FRESH-RECOVERY-ALREADY-REGISTERED"),
                "completed Steam recovery refuses a second registration of the same installation");
        }
        finally
        {
            if(File.Exists(journalFile))File.Delete(journalFile);
        }
    }

    // Synthetic custom/unofficial map SteamCMD: the ASA 2430930 binaries
    // are shared, but the Unreal map level is an explicit validated token.
    // No custom level text may become a SteamCMD command-line argument.
    {
        string customRoot=Path.Combine(root,"fresh-custom-asa-empty");
        await using var customCluster=new ClusterService("synthetic-custom-installer",
            Path.Combine(root,"custom-installer-manager"),
            _=>new ProcessCheck(false,"Synthetic stopped only",null,true));
        int customDownloads=0;
        const string customName="Synthetic Custom Atlantis";
        const string customLevel="SyntheticAtlantis_WP";
        Check(AsaSteamMapCatalog.LevelForNewInstallation(customName,customLevel)
                ==customLevel &&
              AsaSteamMapCatalog.LevelForNewInstallation("Valguero",null)
                =="Valguero_WP",
            "custom and official Steam installations use distinct explicitly validated levels");
        bool unsafeLevel=false,officialOverride=false;
        try{AsaSteamMapCatalog.LevelForNewInstallation(customName,
            "Bad?listen -port=1");}
        catch(InvalidDataException){unsafeLevel=true;}
        try{AsaSteamMapCatalog.LevelForNewInstallation("Valguero",
            "UntrustedLevel_WP");}
        catch(InvalidDataException){officialOverride=true;}
        Check(unsafeLevel&&officialOverride,
            "native catalog rejects launch-switch injection and official level override");

        async Task<SteamFreshInstallReceipt> SyntheticCustomRunner(
            SteamFreshInstallRequest request,LocalMap[] existing,
            IProgress<SteamInstallProgress>? progress,CancellationToken token)
        {
            customDownloads++;
            Check(request.MapName==customName&&request.CustomMapLevel==customLevel &&
                  !existing.Any(m=>m.Name==customName),
                "native fresh installer forwards the custom level without converting original map data");
            await Task.Delay(40,token);
            string bin=Path.Combine(request.InstallRoot,"ShooterGame","Binaries","Win64");
            Directory.CreateDirectory(bin);
            string server=Path.Combine(bin,"ArkAscendedServer.exe");
            File.WriteAllText(server,"SYNTHETIC CUSTOM ASA EXE - NEVER EXECUTE");
            string manifests=Path.Combine(request.InstallRoot,"steamapps");
            Directory.CreateDirectory(manifests);
            File.WriteAllText(Path.Combine(manifests,"appmanifest_2430930.acf"),
                "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123456\"\n}\n");
            progress?.Report(new("complete","SYNTHETIC custom Steam success",100));
            return new(request.MapId,request.InstallRoot,server,"123456",
                DateTimeOffset.UtcNow,request.SteamCmdExecutable,customLevel);
        }
        using var customBridge=new DesktopSteamFreshInstallBridge(customCluster,
            SyntheticCustomRunner,()=>customRoot,
            availableFreeBytes:_=>100L*1024*1024*1024);
        var missing=await Call(customBridge.HandleAsync,"steam.freshPreflight",
            new{mapName=customName,installRoot=customRoot,steamExe,
                allowSteamCmdDownload=false,explicitRetry=false});
        Check(IsError(missing,"UM-FRESH-MAP-LEVEL-INVALID")&&
              !Directory.Exists(customRoot),
            "custom level cannot be guessed from the map display name");
        var overrideAttempt=await Call(customBridge.HandleAsync,"steam.freshPreflight",
            new{mapName="Valguero",customLevel="Injected_WP",
                installRoot=customRoot,steamExe,allowSteamCmdDownload=false});
        Check(IsError(overrideAttempt,"UM-FRESH-MAP-LEVEL-INVALID")&&
              !Directory.Exists(customRoot),
            "official ASA map level cannot be overridden in fresh installer");
        var injected=await Call(customBridge.HandleAsync,"steam.freshPreflight",
            new{mapName=customName,customLevel="Atlantis_WP?listen -NoBattlEye",
                installRoot=customRoot,steamExe,allowSteamCmdDownload=false});
        Check(IsError(injected,"UM-FRESH-MAP-LEVEL-INVALID")&&
              !Directory.Exists(customRoot)&&customDownloads==0,
            "custom Steam preflight refuses injected command line without disk writes");
        var customPlan=await Call(customBridge.HandleAsync,"steam.freshPreflight",
            new{mapName=customName,customLevel,installRoot=customRoot,
                steamExe,allowSteamCmdDownload=false,explicitRetry=false});
        IsOk(customPlan,"new custom level permits exact local ASA download preview");
        Check(customPlan.GetProperty("data").GetProperty("mapLevel").GetString()
                ==customLevel && customDownloads==0&&!Directory.Exists(customRoot),
            "custom level preview is read-only and never starts SteamCMD");
        var customLaunch=await Call(customBridge.HandleAsync,"steam.freshStart",
            new{token=customPlan.GetProperty("data").GetProperty("token").GetString(),
                confirm=true});
        IsOk(customLaunch,"approved custom level schedules native SteamCMD");
        string customJob=customLaunch.GetProperty("data").GetProperty("jobId").GetString()!;
        JsonElement customState=default;
        for(int attempt=0;attempt<60;attempt++)
        {
            customState=await Call(customBridge.HandleAsync,"steam.freshStatus",
                new{jobId=customJob});
            if(customState.GetProperty("data").GetProperty("verified").GetBoolean())break;
            await Task.Delay(35);
        }
        Check(customState.GetProperty("data").GetProperty("verified").GetBoolean() &&
              customDownloads==1,"custom Steam binaries verified exactly once");
        var customRegistered=await Call(customBridge.HandleAsync,"steam.freshRegister",
            new{jobId=customJob,confirm=true});
        IsOk(customRegistered,"custom map registration after verified fresh Steam files");
        var customMap=customCluster.Config.LocalMaps.Single(m=>m.Name==customName);
        Check(customMap.Arguments==""&&!customMap.AutoStart&&
              !customMap.StartWithAsaApi&&customMap.GamePort==0&&
              customMap.FreshInstallMapLevel==customLevel&&
              !Directory.Exists(Path.Combine(customRoot,"ShooterGame","Saved")),
            "custom map download pins level in Manager metadata but leaves launch, mods and Store unconfigured");
        using var customSetup=new DesktopInitialMapSetupBridge(customCluster);
        var customStatus=await Call(customSetup.HandleAsync,"servers.initialStatus",
            new{mapId=customMap.Id});
        IsOk(customStatus,"downloaded custom level is eligible for manual native first setup");
        Check(!customStatus.GetProperty("data").GetProperty("requiresCustomLevel")
                 .GetBoolean() &&
              customStatus.GetProperty("data").GetProperty("level").GetString()
                ==customLevel,
            "initial setup reuses exact operator-approved custom level without duplicate input");
        var mismatchedLevel=await Call(customSetup.HandleAsync,
            "servers.initialPreview",new{mapId=customMap.Id,
                sessionName="Synthetic Custom Server",gamePort=7951,queryPort=27951,
                customLevel="OtherCustom_WP"});
        Check(IsError(mismatchedLevel,"UM-INITIAL-INSTALLED-LEVEL-MISMATCH")&&
              customMap.Arguments==""&&customMap.GamePort==0,
            "a different Unreal map level cannot silently replace original installed level");
        var planned=await Call(customSetup.HandleAsync,"servers.initialPreview",
            new{mapId=customMap.Id,sessionName="Synthetic Custom Server",
                gamePort=7951,queryPort=27951,customLevel=""});
        IsOk(planned,"pinned custom map obtains strict native initial launch preview");
        Check(planned.GetProperty("data").GetProperty("mapLevel").GetString()
                ==customLevel,
            "initial launch preview keeps the source-pinned Unreal level");
        string staleCustomToken=planned.GetProperty("data").GetProperty("token").GetString()!;
        customMap.FreshInstallMapLevel="ExternallyChanged_WP";
        var staleCustomApply=await Call(customSetup.HandleAsync,"servers.initialApply",
            new{token=staleCustomToken,confirm=true});
        Check(IsError(staleCustomApply,"UM-INITIAL-CONFLICT")&&
              customMap.Arguments==""&&customMap.GamePort==0,
            "level changed after preview rejects first setup without writing a mismatched launch");
        customMap.FreshInstallMapLevel=customLevel;
        var replayCustom=await Call(customSetup.HandleAsync,"servers.initialApply",
            new{token=staleCustomToken,confirm=true});
        Check(IsError(replayCustom,"UM-INITIAL-PREVIEW-EXPIRED"),
            "stale first-setup approval cannot be replayed after rejected map identity change");
        var approvedCustom=await Call(customSetup.HandleAsync,"servers.initialPreview",
            new{mapId=customMap.Id,sessionName="Synthetic Custom Server",
                gamePort=7951,queryPort=27951,customLevel=""});
        IsOk(approvedCustom,"restored custom level receives a fresh native preview");
        var committed=await Call(customSetup.HandleAsync,"servers.initialApply",
            new{token=approvedCustom.GetProperty("data").GetProperty("token").GetString(),
                confirm=true});
        IsOk(committed,"approved custom first setup writes only local launch profile");
        var configuredCustom=customCluster.Config.LocalMaps.Single(m=>m.Id==customMap.Id);
        Check(configuredCustom.Arguments.StartsWith(customLevel+"?listen",
                  StringComparison.Ordinal) &&
              configuredCustom.FreshInstallMapLevel==customLevel &&
              configuredCustom.GamePort==7951 && configuredCustom.QueryPort==27951,
            "custom ASA level and selected ports persist without starting the server");
    }

    // Verify that launch settings are a separate explicit stage after ASA
    // files exist. This never starts an ASA process or enables RCON.
    var valguero=cluster.Config.LocalMaps.Single(m=>m.Name=="Valguero");
    // TEST-001/003: pristine SteamCMD installation has no Saved/INI yet.
    // Read-only editor must distinguish the missing file tree from an invalid
    // installation; neither query may create an INI or bypass stopped-state.
    string pristineSaved=Path.Combine(valguero.InstallationPath,"ShooterGame","Saved");
    Check(!Directory.Exists(pristineSaved),
        "new registered synthetic ASA has no Saved directory before first run");
    using(var firstIni=new DesktopIniBridge(cluster))
    {
        var noGame=await Call(firstIni.HandleAsync,"ini.open",
            new{mapId=valguero.Id,file="Game.ini"});
        var noGus=await Call(firstIni.HandleAsync,"ini.open",
            new{mapId=valguero.Id,file="GameUserSettings.ini"});
        Check(IsError(noGame,"UM-INI-CONFIG-DIRECTORY-MISSING") &&
              IsError(noGus,"UM-INI-CONFIG-DIRECTORY-MISSING") &&
              !Directory.Exists(pristineSaved),
            "missing first-run INI folder is reported without filesystem writes");
    }
    int testGamePort=FindFreeTestPort(true);
    int testQueryPort=FindFreeTestPort(true,testGamePort);
    using(var setup=new DesktopInitialMapSetupBridge(cluster))
    {
        var status=await Call(setup.HandleAsync,"servers.initialStatus",
            new{mapId=valguero.Id});
        IsOk(status,"freshly registered ASA map is eligible for initial setup");
        Check(status.GetProperty("data")
            .GetProperty("initialSetupAvailable").GetBoolean() &&
              status.GetProperty("data").GetProperty("blocker").GetString()=="NONE",
            "new map is confirmed stopped, unconfigured, and exposes no blocker");

        // A valid file at a DIFFERENT local installation is not the
        // executable belonging to this registered ASA map.
        string canonicalServer=valguero.ExecutablePath;
        string unrelatedExe=Path.Combine(root,"unrelated-asa-binary",
            "ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedExe)!);
        File.WriteAllText(unrelatedExe,"SYNTHETIC OTHER MAP EXE - NEVER EXECUTED");
        try
        {
            valguero.ExecutablePath=unrelatedExe;
            var wrongRootStatus=await Call(setup.HandleAsync,"servers.initialStatus",
                new{mapId=valguero.Id});
            IsOk(wrongRootStatus,"initial setup returns an explicit EXE identity blocker");
            Check(!wrongRootStatus.GetProperty("data")
                    .GetProperty("initialSetupAvailable").GetBoolean() &&
                  wrongRootStatus.GetProperty("data")
                    .GetProperty("blocker").GetString()=="SERVER_EXECUTABLE_MISSING",
                "wrong-root existing ASA EXE does not qualify for first map setup");
            var wrongRootPreview=await Call(setup.HandleAsync,"servers.initialPreview",
                new{mapId=valguero.Id,sessionName="Wrong Root",
                    gamePort=testGamePort,queryPort=testQueryPort});
            Check(IsError(wrongRootPreview,"UM-INITIAL-EXECUTABLE-INVALID") &&
                  valguero.GamePort==0 && valguero.Arguments=="",
                "initial preview rejects an unrelated existing EXE without profile mutation");
        }
        finally
        {
            valguero.ExecutablePath=canonicalServer;
        }

        var bad=await Call(setup.HandleAsync,"servers.initialPreview",
            new{mapId=valguero.Id,sessionName="UK Test",gamePort=testGamePort,queryPort=testGamePort});
        Check(!bad.GetProperty("ok").GetBoolean(),
            "initial setup rejects identical Game and Query UDP ports");

        // Real OS UDP ownership, not just the Manager's map registry.
        // The socket belongs to this isolated test process, not any ASA port.
        using(var occupied=new System.Net.Sockets.UdpClient(
            new IPEndPoint(IPAddress.Loopback,testGamePort)))
        {
            var busyPreview=await Call(setup.HandleAsync,
                "servers.initialPreview",
                new{mapId=valguero.Id,sessionName="Occupied preview",
                    gamePort=testGamePort,queryPort=testQueryPort});
            Check(IsError(busyPreview,"UM-INITIAL-PORT-IN-USE")&&
                  valguero.Arguments=="",
                "OS-owned Game UDP port rejects initial preview without profile writes");
        }

        var preview=await Call(setup.HandleAsync,"servers.initialPreview",
            new{mapId=valguero.Id,sessionName="Untamed Kingdom Test",
                gamePort=testGamePort,queryPort=testQueryPort});
        IsOk(preview,"official ASA level/session/ports preview is calculated");
        var token=preview.GetProperty("data").GetProperty("token").GetString()!;
        Check(string.IsNullOrEmpty(valguero.Arguments) &&
              valguero.GamePort==0 && valguero.QueryPort==0,
            "profile stays unmodified until approval");
        var unconfirmed=await Call(setup.HandleAsync,"servers.initialApply",
            new{token,confirm=false});
        Check(IsError(unconfirmed,"UM-INITIAL-CONFIRMATION-REQUIRED"),
            "unapproved initial setup writes nothing and preserves preview");

        using(var occupied=new System.Net.Sockets.UdpClient(
            new IPEndPoint(IPAddress.Loopback,testQueryPort)))
        {
            var blockedApply=await Call(setup.HandleAsync,"servers.initialApply",
                new{token,confirm=true});
            Check(IsError(blockedApply,"UM-INITIAL-PORT-IN-USE")&&
                  valguero.GamePort==0&&valguero.Arguments=="",
                "UDP port claimed after preview blocks apply and preserves original profile");
        }
        var committed=await Call(setup.HandleAsync,"servers.initialApply",
            new{token,confirm=true});
        IsOk(committed,"approved initial setup saves registered Manager launch profile");
        var configured=cluster.Config.LocalMaps.Single(m=>m.Id==valguero.Id);
        Check(configured.GamePort==testGamePort && configured.QueryPort==testQueryPort &&
              configured.Arguments.StartsWith("Valguero_WP?listen",StringComparison.Ordinal) &&
              configured.Arguments.Contains("SessionName=") &&
              configured.RconPort==0 && string.IsNullOrEmpty(configured.RconPasswordProtected) &&
              !configured.AutoStart,
            "official map level, session and UDP ports saved without RCON or autostart");
        var oldToken=await Call(setup.HandleAsync,"servers.initialApply",
            new{token,confirm=true});
        Check(IsError(oldToken,"UM-INITIAL-PREVIEW-EXPIRED"),
            "initial setup approval cannot be replayed");
        var alreadyConfigured=await Call(setup.HandleAsync,"servers.initialPreview",
            new{mapId=valguero.Id,sessionName="Override",gamePort=7817,queryPort=27055});
        Check(IsError(alreadyConfigured,"UM-INITIAL-ALREADY-CONFIGURED"),
            "initial-only editor never overwrites existing configured map");
        Check(File.ReadAllText(configured.ExecutablePath).Contains("SYNTHETIC ASA EXE"),
            "first profile setup preserves original ASA executable and saved data");
    }

    // A manually imported CUSTOM/modded ASA map uses the same initial
    // native bridge, but its exact Unreal level must be operator supplied.
    // Never synthesize mod downloads, Store membership or launch ASA.
    {
        string customRoot=Path.Combine(root,"custom-asa-map-install");
        string customExe=Path.Combine(customRoot,"ShooterGame","Binaries",
            "Win64","ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(customExe)!);
        File.Copy(valguero.ExecutablePath,customExe);
        byte[] originalExe=File.ReadAllBytes(customExe);
        var custom=new LocalMap{
            Id=Guid.Parse("00000000-0000-4000-9000-000000007701"),
            Name="Custom Atlantis Community",
            InstallationPath=customRoot,ExecutablePath=customExe
        };
        cluster.AddMap(custom);
        int customGame=FindFreeTestPort(true,testGamePort,testQueryPort);
        int customQuery=FindFreeTestPort(true,testGamePort,testQueryPort,customGame);
        using var customSetup=new DesktopInitialMapSetupBridge(cluster);
        var status=await Call(customSetup.HandleAsync,"servers.initialStatus",
            new{mapId=custom.Id});
        Check(status.GetProperty("ok").GetBoolean() &&
              status.GetProperty("data").GetProperty("requiresCustomLevel").GetBoolean() &&
              status.GetProperty("data").GetProperty("initialSetupAvailable").GetBoolean() &&
              status.GetProperty("data").GetProperty("blocker").GetString()=="NONE",
            "unknown manually imported map offers native explicit custom level setup");
        string officialName=new[]{"The Center","Scorched Earth","Genesis Part 1"}
            .First(name=>!cluster.Config.LocalMaps.Any(map=>
                map.Name.Equals(name,StringComparison.OrdinalIgnoreCase)));
        string officialRoot=Path.Combine(root,"official-no-override-install");
        string officialExe=Path.Combine(officialRoot,"ShooterGame","Binaries",
            "Win64","ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(officialExe)!);
        File.Copy(valguero.ExecutablePath,officialExe);
        var officialUnconfigured=new LocalMap{
            Name=officialName,InstallationPath=officialRoot,
            ExecutablePath=officialExe
        };
        cluster.AddMap(officialUnconfigured);
        var overrideRejected=await Call(customSetup.HandleAsync,"servers.initialPreview",
            new{mapId=officialUnconfigured.Id,sessionName="Official Preview",
                gamePort=customGame,queryPort=customQuery,
                customLevel="Hijacked_WP"});
        Check(IsError(overrideRejected,"UM-INITIAL-OFFICIAL-LEVEL-OVERRIDE-DENIED") &&
              officialUnconfigured.Arguments=="",
            "official registered map cannot substitute user-supplied Unreal level");
        var missing=await Call(customSetup.HandleAsync,"servers.initialPreview",
            new{mapId=custom.Id,sessionName="Custom Test",
                gamePort=customGame,queryPort=customQuery});
        Check(IsError(missing,"UM-INITIAL-CUSTOM-LEVEL-REQUIRED") &&
              custom.Arguments=="",
            "custom level is never guessed from a map display name");
        foreach(string unsafeLevel in new[]{
            "Custom_WP?listen","Custom_WP -mods=12345",
            "../Custom_WP","Custom/WP","_Custom_WP","Custom.WP",
            "Custom\nMap"
        })
        {
            var denied=await Call(customSetup.HandleAsync,"servers.initialPreview",
                new{mapId=custom.Id,sessionName="Custom Test",
                    gamePort=customGame,queryPort=customQuery,
                    customLevel=unsafeLevel});
            Check(IsError(denied,"UM-INITIAL-CUSTOM-LEVEL-INVALID"),
                "unsafe custom Unreal-level input fails closed");
        }
        using(var officialSetup=new DesktopInitialMapSetupBridge(cluster))
        {
            var overrideDenied=await Call(officialSetup.HandleAsync,
                "servers.initialPreview",new{mapId=valguero.Id,
                    sessionName="Wrong Override",gamePort=customGame,
                    queryPort=customQuery,customLevel="Foreign_WP"});
            Check(IsError(overrideDenied,"UM-INITIAL-ALREADY-CONFIGURED"),
                "already configured official map cannot be changed to a custom level");
        }
        var preview=await Call(customSetup.HandleAsync,"servers.initialPreview",
            new{mapId=custom.Id,sessionName="Untamed Custom",
                gamePort=customGame,queryPort=customQuery,
                customLevel="Atlantis_Custom_WP"});
        Check(preview.GetProperty("ok").GetBoolean() &&
              preview.GetProperty("data").GetProperty("requiresCustomLevel").GetBoolean() &&
              preview.GetProperty("data").GetProperty("mapLevel").GetString()=="Atlantis_Custom_WP",
            "native preview binds exact validated custom level and local ports");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var declined=await Call(customSetup.HandleAsync,"servers.initialApply",
            new{token,confirm=false});
        Check(IsError(declined,"UM-INITIAL-CONFIRMATION-REQUIRED") &&
              custom.Arguments=="",
            "custom level preview cannot silently change Manager profile");
        // A changed name invalidates a pinned preview before saving.
        string previousName=custom.Name;
        custom.Name="Tampered after preview";
        var changed=await Call(customSetup.HandleAsync,"servers.initialApply",
            new{token,confirm=true});
        Check(IsError(changed,"UM-INITIAL-CONFLICT") &&
              custom.Arguments=="",
            "custom level cannot be applied after map identity changes");
        custom.Name=previousName;
        // A rejected identity mismatch consumes the preview on purpose.
        // Restoring the previous name must NOT revive a stale approval.
        var replay=await Call(customSetup.HandleAsync,"servers.initialApply",
            new{token,confirm=true});
        Check(IsError(replay,"UM-INITIAL-PREVIEW-EXPIRED") &&
              custom.Arguments=="",
            "restoring a changed map name cannot replay an invalidated first-setup approval");
        var refreshed=await Call(customSetup.HandleAsync,"servers.initialPreview",
            new{mapId=custom.Id,sessionName="Untamed Custom",
                gamePort=customGame,queryPort=customQuery,
                customLevel="Atlantis_Custom_WP"});
        IsOk(refreshed,"custom level requires a new native preview after changed identity");
        var applied=await Call(customSetup.HandleAsync,"servers.initialApply",
            new{token=refreshed.GetProperty("data").GetProperty("token").GetString(),
                confirm=true});
        Check(applied.GetProperty("ok").GetBoolean(),
            "confirmed fresh custom level preview saves the atomic native Manager profile");
        var registered=cluster.Config.LocalMaps.Single(x=>x.Id==custom.Id);
        Check(registered.Arguments.StartsWith(
                "Atlantis_Custom_WP?listen?SessionName=",StringComparison.Ordinal) &&
              registered.GamePort==customGame && registered.QueryPort==customQuery &&
              registered.RconPort==0 && !registered.AutoStart &&
              File.ReadAllBytes(customExe).SequenceEqual(originalExe),
            "custom level config retains ASA EXE bytes without starting server or enabling RCON");
        var second=await Call(customSetup.HandleAsync,"servers.initialPreview",
            new{mapId=custom.Id,sessionName="Again",
                gamePort=customGame,queryPort=customQuery,
                customLevel="Malicious_WP"});
        Check(IsError(second,"UM-INITIAL-ALREADY-CONFIGURED"),
            "initial setup never overwrites a configured custom map");
    }

    // Real native fresh RCON bootstrap is separate from map first-time
    // launch settings. No Store, running process or other INI may be modified.
    {
        var fresh=cluster.Config.LocalMaps.Single(m=>m.Name=="Valguero");
        int testRconPort=FindFreeTestPort(false,testGamePort,testQueryPort);
        int otherTestRconPort=FindFreeTestPort(false,testGamePort,testQueryPort,testRconPort);
        string iniPath=AsaInitialRconSettings.NewIniPath(fresh.InstallationPath);
        Check(!File.Exists(iniPath),"fresh RCON test starts with no ASA server config");
        using var rconSetup=new DesktopRconConfigurationBridge(cluster);
        string originalBootstrapArgs=fresh.Arguments;
        fresh.Arguments=originalBootstrapArgs+
            " -SessionName=\"Untamed Store -usestore Weekend\"";
        var namedBootstrap=await Call(rconSetup.HandleAsync,"rcon.configStatus",
            new{mapId=fresh.Id});
        Check(namedBootstrap.GetProperty("data").GetProperty("permitted").GetBoolean(),
            "RCON bootstrap accepts quoted Store words inside SessionName");
        fresh.Arguments=originalBootstrapArgs+" -converttostore";
        var forbiddenBootstrap=await Call(rconSetup.HandleAsync,"rcon.configStatus",
            new{mapId=fresh.Id});
        Check(!forbiddenBootstrap.GetProperty("data").GetProperty("permitted").GetBoolean(),
            "RCON bootstrap refuses actual conversion switch");
        fresh.Arguments=originalBootstrapArgs+" -clusterid=Unregistered";
        var forbiddenBootstrapCluster=await Call(rconSetup.HandleAsync,"rcon.configStatus",
            new{mapId=fresh.Id});
        Check(!forbiddenBootstrapCluster.GetProperty("data").GetProperty("permitted").GetBoolean(),
            "RCON bootstrap refuses actual unenrolled cluster switch");
        fresh.Arguments=originalBootstrapArgs;
        var status=await Call(rconSetup.HandleAsync,"rcon.configStatus",
            new{mapId=fresh.Id});
        IsOk(status,"new stopped non-Store map can configure RCON");
        Check(status.GetProperty("data").GetProperty("rconPort").GetInt32()==fresh.RconPort,
            "native RCON status returns the exact selected local map port");
        Check(status.GetProperty("data").GetProperty("permitted").GetBoolean(),
            "native RCON configuration is eligible only for safe local map");
        const string secret="A-strong-unique-test-secret";
        var historicalSibling=new LocalMap {
            Name="Historical RCON installation sibling",
            InstallationPath=fresh.InstallationPath,
            HistoricalMapKey="LegacyMap_WP"
        };
        cluster.AddMap(historicalSibling);
        var protectedStatus=await Call(rconSetup.HandleAsync,"rcon.configStatus",
            new{mapId=fresh.Id});
        IsOk(protectedStatus,"RCON historical sibling status is observable without writes");
        Check(!protectedStatus.GetProperty("data").GetProperty("permitted").GetBoolean(),
            "RCON bootstrap is disabled when another map shares historical Store provenance");
        var protectedPreview=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=fresh.Id,rconPort=testRconPort,password=secret,createIni=true});
        Check(IsError(protectedPreview,"UM-RCON-CONFIG-STOPPED-NONSTORE-ONLY"),
            "RCON bootstrap cannot bypass historical Store map on shared installation");
        cluster.RemoveMap(historicalSibling.Id);
        // A real existing ASA INI is never rewritten or assigned a new
        // credential. Exact parity must work with short legacy values and
        // with significant whitespace and '=' around the original password.
        const string oldPassword="  =Old7!xx= ";
        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
        File.WriteAllText(iniPath,
            "[ServerSettings]\r\nRCONEnabled=True\r\nRCONPort="+
            testRconPort.ToString(System.Globalization.CultureInfo.InvariantCulture)+
            "\r\nServerAdminPassword="+oldPassword+"\r\n");
        byte[] oldBytes=File.ReadAllBytes(iniPath);
        var matchingExisting=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=fresh.Id,rconPort=testRconPort,password=oldPassword,createIni=false});
        IsOk(matchingExisting,"existing ASA INI with exact legacy password can be attached read-only");
        Check(matchingExisting.GetProperty("data").GetProperty("existingIniVerified").GetBoolean() &&
              !matchingExisting.GetProperty("data").GetProperty("createsNewIni").GetBoolean() &&
              !matchingExisting.GetRawText().Contains(oldPassword) &&
              File.ReadAllBytes(iniPath).SequenceEqual(oldBytes),
            "existing ASA credential and INI bytes remain unchanged at preview");
        var trimmedExisting=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=fresh.Id,rconPort=testRconPort,password=oldPassword.Trim(),createIni=false});
        Check(IsError(trimmedExisting,"UM-RCON-CONFIG-FAILED") &&
              File.ReadAllBytes(iniPath).SequenceEqual(oldBytes),
            "trimming an existing password is rejected without rewriting the ASA INI");
        var otherExisting=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=fresh.Id,rconPort=otherTestRconPort,password=oldPassword,createIni=false});
        Check(IsError(otherExisting,"UM-RCON-CONFIG-FAILED") &&
              File.ReadAllBytes(iniPath).SequenceEqual(oldBytes),
            "existing INI with different RCON port cannot silently bind a profile");
        File.AppendAllText(iniPath,
            "[ServerSettings]\r\nRCONEnabled=True\r\nRCONPort="+
            testRconPort+"\r\nServerAdminPassword="+oldPassword+"\r\n");
        var duplicateExisting=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=fresh.Id,rconPort=testRconPort,password=oldPassword,createIni=false});
        Check(IsError(duplicateExisting,"UM-RCON-CONFIG-FAILED"),
            "duplicate ASA ServerSettings sections cannot authorize attaching an ambiguous RCON");
        File.Delete(iniPath);
        Check(!File.Exists(iniPath),"synthetic old INI removed before new-server bootstrap");
        var preview=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=fresh.Id,rconPort=testRconPort,password=secret,createIni=true});
        IsOk(preview,"new ASA RCON config is previewed, not applied");
        Check(!File.Exists(iniPath) && !preview.GetRawText().Contains(secret),
            "RCON preview leaves disk untouched and hides credential");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        // A second independent local ASA map can finish its own first RCON
        // wizard without revoking the still pending Valguero approval.
        var customRcon=cluster.Config.LocalMaps.Single(x=>
            x.Name=="Custom Atlantis Community");
        int customRconPort=FindFreeTestPort(false,
            testGamePort,testQueryPort,testRconPort,otherTestRconPort);
        var customRconPreview=await Call(rconSetup.HandleAsync,
            "rcon.configPreview",new{mapId=customRcon.Id,
                rconPort=customRconPort,password=secret,createIni=true});
        IsOk(customRconPreview,"second map preserves independent native RCON setup");
        string customToken=customRconPreview.GetProperty("data")
            .GetProperty("token").GetString()!;
        Check(token!=customToken,"two separate ASA maps hold distinct config approvals");
        var declined=await Call(rconSetup.HandleAsync,"rcon.configApply",
            new{token,confirm=false});
        Check(IsError(declined,"UM-RCON-CONFIG-CONFIRMATION") &&
              !File.Exists(iniPath),"RCON bootstrap requires explicit confirmation");
        var configured=await Call(rconSetup.HandleAsync,"rcon.configApply",
            new{token,confirm=true});
        IsOk(configured,"native RCON bootstrap creates ASA INI and stores DPAPI credential");
        var customRconApply=await Call(rconSetup.HandleAsync,"rcon.configApply",
            new{token=customToken,confirm=true});
        IsOk(customRconApply,"second ASA RCON configuration survives Valguero commit");
        Check(cluster.Config.LocalMaps.Single(x=>x.Id==customRcon.Id).RconPort==
                customRconPort&&
              File.Exists(AsaInitialRconSettings.NewIniPath(
                  customRcon.InstallationPath)),
            "native RCON setup makes only the selected map's INI/DPAPI changes");
        var afterSetupStatus=await Call(rconSetup.HandleAsync,"rcon.configStatus",
            new{mapId=fresh.Id});
        Check(afterSetupStatus.GetProperty("data").GetProperty("rconPort").GetInt32()==testRconPort,
            "native RCON status reflects the committed map-specific port");
        Check(File.Exists(iniPath) && File.ReadAllText(iniPath).Contains("RCONEnabled=True") &&
              cluster.Config.LocalMaps.Single(m=>m.Id==fresh.Id).RconPort==testRconPort &&
              SecureStore.Unprotect(cluster.Config.LocalMaps.Single(m=>m.Id==fresh.Id)
                  .RconPasswordProtected)==secret,
            "RCON bootstrap persisted correct local ASA setting and encrypted Manager secret");
        var replay=await Call(rconSetup.HandleAsync,"rcon.configApply",
            new{token,confirm=true});
        Check(IsError(replay,"UM-RCON-CONFIG-TOKEN-EXPIRED"),
            "RCON bootstrap cannot be replayed");
        var second=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=fresh.Id,rconPort=otherTestRconPort,password=secret,createIni=true});
        Check(IsError(second,"UM-RCON-CONFIG-STOPPED-NONSTORE-ONLY"),
            "configured RCON cannot be overwritten by bootstrap");
        var denied=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=Guid.NewGuid(),rconPort=otherTestRconPort,password=secret,createIni=true});
        Check(IsError(denied,"UM-MAP-NOT-OWNED"),"foreign map cannot configure RCON");
        Check(!configured.GetRawText().Contains(secret),
            "RCON success response contains no plaintext credential");
    }

    // The first REAL ASA start has its own native two-stage approval path.
    // The Windows test injects a process adapter and never executes the
    // synthetic (non-executable) ASA fixture or touches live Store-v1.
    {
        var fresh=cluster.Config.LocalMaps.Single(x=>x.Name=="Valguero");
        Check(AsaRconConfigurationEvidence.Matches(fresh),
            "fresh server must match RCON port, WindowsServer INI and protected credential");
        string rconIni=AsaInitialRconSettings.NewIniPath(fresh.InstallationPath);
        byte[] rconOriginal=File.ReadAllBytes(rconIni);
        try
        {
            File.AppendAllText(rconIni,
                "\r\n[ServerSettings]\r\nRCONPort=49999\r\n");
            Check(!AsaRconConfigurationEvidence.Matches(fresh),
                "duplicate RCON configuration cannot bypass new-server preflight");
        }
        finally { File.WriteAllBytes(rconIni,rconOriginal); }
        Check(AsaRconConfigurationEvidence.Matches(fresh),
            "RCON settings remain byte-identical after isolated negative test");
        // Legacy Kova credentials may include '=' and meaningful whitespace.
        // A new password generator must never silently trim or replace them.
        string savedCredential=fresh.RconPasswordProtected;
        string unusual="  =legacy+secret= with trailing space ";
        try
        {
            string prior=SecureStore.Unprotect(savedCredential);
            string raw=File.ReadAllText(rconIni);
            Check(raw.Contains("ServerAdminPassword="+prior,
                    StringComparison.Ordinal),"legacy password fixture found");
            File.WriteAllText(rconIni,raw.Replace(
                "ServerAdminPassword="+prior,
                "ServerAdminPassword="+unusual,StringComparison.Ordinal));
            fresh.RconPasswordProtected=SecureStore.Protect(unusual);
            Check(AsaRconConfigurationEvidence.Matches(fresh),
                "exact preexisting RCON credential with spaces and equals is preserved");
            fresh.RconPasswordProtected=SecureStore.Protect(unusual.Trim());
            Check(!AsaRconConfigurationEvidence.Matches(fresh),
                "trimming preexisting password cannot silently pass INI parity");
        }
        finally
        {
            fresh.RconPasswordProtected=savedCredential;
            File.WriteAllBytes(rconIni,rconOriginal);
        }
        Check(AsaRconConfigurationEvidence.Matches(fresh),
            "historical password parity regression leaves INI and DPAPI state intact");
        int calls=0;
        var localGameBind=new[]{
            new System.Net.IPEndPoint(System.Net.IPAddress.Any,fresh.GamePort)};
        var localQueryBind=new[]{
            new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Any,fresh.QueryPort)};
        var localRconBind=new[]{
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,fresh.RconPort)};
        var noPorts=Array.Empty<System.Net.IPEndPoint>();
        Check(!AsaLocalPortPreflight.CanStart(fresh,localGameBind,noPorts) &&
              !AsaLocalPortPreflight.CanStart(fresh,localQueryBind,noPorts) &&
              !AsaLocalPortPreflight.CanStart(fresh,noPorts,localRconBind) &&
              AsaLocalPortPreflight.CanStart(fresh,noPorts,noPorts),
            "OS game/query UDP or RCON TCP port conflicts deny real ASA start");
        static IEnumerable<System.Net.IPEndPoint> BrokenPortQuery()
        {
            yield return new System.Net.IPEndPoint(
                System.Net.IPAddress.Loopback,12345);
            throw new InvalidOperationException("Synthetic OS port enumeration failed");
        }
        Check(AsaLocalPortPreflight.Inspect(fresh,noPorts,noPorts)==
                  AsaLocalPortAvailability.Available&&
              AsaLocalPortPreflight.Inspect(fresh,localQueryBind,noPorts)==
                  AsaLocalPortAvailability.InUse&&
              AsaLocalPortPreflight.Inspect(fresh,BrokenPortQuery(),noPorts)==
                  AsaLocalPortAvailability.Unverified,
            "busy listener and unavailable Windows port evidence have different states");
        using var starter=new DesktopFreshAsaStartBridge(cluster,(id,approved)=>{
            Check(approved && id==fresh.Id,"native launch receives exact approved local map");
            calls++;
            return new ProcessIdentity{
                Pid=45678,StartedUtc=DateTimeOffset.UtcNow,
                ExecutablePath=fresh.ExecutablePath,Arguments=fresh.Arguments
            };
        });
        var unknown=await Call(starter.HandleAsync,"servers.freshStartPreview",
            new{mapId=Guid.NewGuid()});
        Check(IsError(unknown,"UM-MAP-NOT-OWNED") && calls==0,
            "unknown map cannot start a Windows ASA process");
        var preview=await Call(starter.HandleAsync,"servers.freshStartPreview",
            new{mapId=fresh.Id});
        IsOk(preview,"stopped fresh official ASA installation passes first-start preflight");
        Check(preview.GetProperty("data").GetProperty("steamBuild").GetString()=="123456" &&
              calls==0,"preflight uses local Steam manifest without launching server");
        string firstToken=preview.GetProperty("data").GetProperty("token").GetString()!;
        var denied=await Call(starter.HandleAsync,"servers.freshStartApply",
            new{token=firstToken,confirm=false});
        Check(IsError(denied,"UM-FRESH-START-APPROVAL")&&calls==0,
            "real fresh ASA start requires explicit confirmation");
        // A concurrent, harmless-looking INI edit still invalidates a
        // previously approved start. No process may launch on old evidence.
        File.AppendAllText(rconIni,"\r\n; concurrent external ASA INI edit\r\n");
        var staleStart=await Call(starter.HandleAsync,"servers.freshStartApply",
            new{token=firstToken,confirm=true});
        Check(IsError(staleStart,"UM-FRESH-START-CHANGED")&&calls==0,
            "INI bytes changed after preview invalidate fresh ASA start approval");
        File.WriteAllBytes(rconIni,rconOriginal);
        var binaryPreview=await Call(starter.HandleAsync,
            "servers.freshStartPreview",new{mapId=fresh.Id});
        IsOk(binaryPreview,"new ASA start preflight protects actual installed server binary");
        byte[] originalFreshExe=File.ReadAllBytes(fresh.ExecutablePath);
        try
        {
            File.WriteAllBytes(fresh.ExecutablePath,
                originalFreshExe.Concat(new byte[]{0x6D}).ToArray());
            var binaryChanged=await Call(starter.HandleAsync,"servers.freshStartApply",
                new{token=binaryPreview.GetProperty("data")
                    .GetProperty("token").GetString(),confirm=true});
            Check(IsError(binaryChanged,"UM-FRESH-START-CHANGED")&&calls==0,
                "changing the ASA EXE after preview rejects a fresh Start with no process launch");
        }
        finally{File.WriteAllBytes(fresh.ExecutablePath,originalFreshExe);}
        var refreshed=await Call(starter.HandleAsync,"servers.freshStartPreview",
            new{mapId=fresh.Id});
        IsOk(refreshed,"unchanged fresh ASA INI bytes receive a new explicit start preview");
        firstToken=refreshed.GetProperty("data").GetProperty("token").GetString()!;
        var launched=await Call(starter.HandleAsync,"servers.freshStartApply",
            new{token=firstToken,confirm=true});
        IsOk(launched,"confirmed new local ASA start calls native process adapter");
        Check(calls==1 &&
              launched.GetProperty("data").GetProperty("pid").GetInt32()==45678 &&
              !launched.GetProperty("data").GetProperty("readyCertified").GetBoolean() &&
              !launched.GetProperty("data").GetProperty("storeTouched").GetBoolean(),
            "process start does not claim readiness or mutate Store");
        var replay=await Call(starter.HandleAsync,"servers.freshStartApply",
            new{token=firstToken,confirm=true});
        Check(IsError(replay,"UM-FRESH-START-TOKEN-EXPIRED")&&calls==1,
            "approved ASA start is one-shot and cannot automatically repeat");

        // Fresh Start must follow the SAME Restart-v1 terminal states as
        // fresh Stop/Restart and original Store Start. An interrupted
        // restart blocks, but a fully audited old restart may be followed
        // by a NEW, separately approved Start. The fixture is contained
        // in the isolated TEMP journal scope; there is no real ASA process.
        var localRestart=new AsaManualRestartJournal(fresh.InstallationPath);
        var restartKey=UntamedOperationCoordinator.ResourceKey(fresh.InstallationPath);
        var restartFolder=Path.Combine(UntamedOperationCoordinator.DefaultRoot,
            "Restart-v1");
        string restartFile=Path.Combine(restartFolder,restartKey+".json");
        var interrupted=localRestart.Begin(fresh.Id,56781);
        var pendingRestart=localRestart.Change(interrupted.Id,AsaRestartPhase.NeedsOperator);
        string auditFile=Path.Combine(restartFolder,restartKey+"-"+
            interrupted.Id.ToString("N")+".operator-reviewed.json");
        try
        {
            var blockedAfterCrash=await Call(starter.HandleAsync,
                "servers.freshStartPreview",new{mapId=fresh.Id});
            Check(IsError(blockedAfterCrash,"UM-FRESH-START-NOT-READY")&&calls==1,
                "pending durable Restart-v1 still blocks independent fresh ASA Start");
            // Simulate an old, crashed operation in the isolated test only:
            // native review refuses a recent journal and preserves the
            // immutable original audit record on approval.
            var oldRecord=pendingRestart with {
                ChangedUtc=DateTimeOffset.UtcNow.AddMinutes(-5)
            };
            File.WriteAllText(restartFile,JsonSerializer.Serialize(oldRecord,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var reviewed=localRestart.ReviewOriginalOldPid(
                fresh.Id,interrupted.Id,56781);
            Check(reviewed.Phase==AsaRestartPhase.OperatorReviewed &&
                  File.Exists(auditFile),
                "manual review records the interrupted restart without claiming success");
            var afterReview=await Call(starter.HandleAsync,
                "servers.freshStartPreview",new{mapId=fresh.Id});
            IsOk(afterReview,"fully reviewed old Restart-v1 no longer blocks a new explicit local Start");
            Check(calls==1,
                "post-review preview never starts ASA or replays the old Restart");
        }
        finally
        {
            // Only temporary synthetic CI fixture files are removed.
            if(File.Exists(restartFile))File.Delete(restartFile);
            if(File.Exists(auditFile))File.Delete(auditFile);
        }
        fresh.HistoricalStoreProtected=true;
        var historical=await Call(starter.HandleAsync,"servers.freshStartPreview",
            new{mapId=fresh.Id});
        Check(IsError(historical,"UM-FRESH-START-NOT-READY")&&calls==1,
            "historical Store protection always blocks fresh ASA start");
        fresh.HistoricalStoreProtected=false;
        fresh.HistoricalMapKey="Valguero_WP";
        var provenance=await Call(starter.HandleAsync,"servers.freshStartPreview",
            new{mapId=fresh.Id});
        Check(IsError(provenance,"UM-FRESH-START-NOT-READY")&&calls==1,
            "historical map key alone blocks an unsafe new-ASA launch");
        fresh.HistoricalMapKey="";
        var sibling=new LocalMap{
            Name="Historical sibling same root",
            InstallationPath=fresh.InstallationPath,
            HistoricalMapKey="ImportedOriginalMap",
            HistoricalStoreProtected=false
        };
        cluster.AddMap(sibling);
        var sharedDenied=await Call(starter.HandleAsync,"servers.freshStartPreview",
            new{mapId=fresh.Id});
        Check(IsError(sharedDenied,"UM-FRESH-START-NOT-READY")&&calls==1,
            "foreign historical Store provenance in shared install blocks fresh start");
        cluster.RemoveMap(sibling.Id);
        Check(!ReleaseCapabilityGate.Check(ManagerOperation.RealAsaStart,true,
                confirmedStopped:true).Allowed &&
              ReleaseCapabilityGate.Check(ManagerOperation.RealAsaStart,true,
                confirmedStopped:true,verifiedFreshInstall:true,
                explicitOperatorApproval:true).Allowed,
            "production start gate requires both real-install evidence and explicit approval");
        var status=await Call(starter.HandleAsync,"servers.freshStartStatus",
            new{mapId=fresh.Id});
        IsOk(status,"local ASA process can be reobserved without starting it");
        Check(!status.GetProperty("data").GetProperty("readyCertified").GetBoolean(),
            "process observation never implies game-client readiness");
        var earlyReadiness=await Call(starter.HandleAsync,
            "servers.freshReadiness",new{mapId=fresh.Id});
        Check(IsError(earlyReadiness,"UM-FRESH-READINESS-NOT-RUNNING"),
            "ASA RCON readiness requires a real OS-verified local server process");
        var foreignReadiness=await Call(starter.HandleAsync,
            "servers.freshReadiness",new{mapId=Guid.NewGuid()});
        Check(IsError(foreignReadiness,"UM-MAP-NOT-OWNED"),
            "unregistered map cannot query native ASA/RCON readiness");
        // ASA itself downloads the configured CurseForge -mods project IDs
        // when launched. Confirm that the native bridge uses the exact saved
        // launch argument list, not a disconnected mock catalog selection.
        string originalLaunch=fresh.Arguments;
        try
        {
            var withMod=ModStateController.Preview(fresh,
                new ModEdit(fresh.Id,"1163881",ModMode.Active));
            fresh.Arguments=withMod.NewArguments;
            Check(fresh.Arguments.Contains("-mods=1163881",
                      StringComparison.Ordinal) &&
                  ModStateController.Read(fresh).Active.Contains("1163881"),
                "real ASA -mods launch switch uses CurseForge project ID");
            var planned=await Call(starter.HandleAsync,
                "servers.freshStartPreview",new{mapId=fresh.Id});
            IsOk(planned,"mod-configured native fresh map can receive Start preview");
            var startedWithMod=await Call(starter.HandleAsync,
                "servers.freshStartApply",
                new{token=planned.GetProperty("data")
                    .GetProperty("token").GetString(),confirm=true});
            IsOk(startedWithMod,"approved fresh Start uses configured -mods arguments");
            Check(calls==2 &&
                  fresh.Arguments.EndsWith("-mods=1163881",
                      StringComparison.Ordinal),
                "native Start preserves ASA mod load/download order in launch args");
        }
        finally { fresh.Arguments=originalLaunch; }
        // One Core lexer is shared by the actual Process.Start boundary,
        // native preview, legacy backup guard and fresh Store enrollment.
        // Quoted session words are never misclassified as ASA flags.
        Check(!AsaStoreArgumentSwitches.ContainsProtectedSwitch(
                  "TheIsland_WP?listen?SessionName=\"Untamed Store -usestore -clusterid\" -Port=7777") &&
              !AsaStoreArgumentSwitches.ContainsConversion(
                  "TheIsland_WP?listen?SessionName=\"ConvertToStore Celebration\"") &&
              !AsaStoreArgumentSwitches.ContainsStoreWriter(
                  "TheIsland_WP?listen?SessionName=\"UK -usestore Weekend\""),
            "quoted session names and internal Store keywords never trigger real Start/backup denial");
        Check(AsaStoreArgumentSwitches.ContainsStoreWriter(
                  "TheIsland_WP?listen -usestore") &&
              AsaStoreArgumentSwitches.ContainsConversion(
                  "TheIsland_WP?listen -ConvertToStore") &&
              AsaStoreArgumentSwitches.ContainsProtectedSwitch(
                  "TheIsland_WP?listen -ClusterDirOverride=\"C:\\Transfer Share\"") &&
              AsaStoreArgumentSwitches.ContainsProtectedSwitch(
                  "TheIsland_WP?listen \"-clusterid\"=KovaCluster") &&
              AsaStoreArgumentSwitches.ContainsProtectedSwitch(
                  "TheIsland_WP?listen -ClusterDirOverride=\"unterminated"),
            "real Store/cluster switches and malformed quoted arguments cannot bypass native authorization");
        // "Store" is a legitimate custom session word; it is NOT an
        // implicit -usestore switch. Actual Store/cluster switches still
        // cannot be smuggled into a non-enrolled local launch.
        try
        {
            fresh.Arguments=AsaSteamMapCatalog.LaunchArguments(
                fresh.Name,"Untamed Store House",fresh.GamePort,fresh.QueryPort);
            var named=await Call(starter.HandleAsync,
                "servers.freshStartPreview",new{mapId=fresh.Id});
            IsOk(named,"ordinary Store word in ASA session name does not block fresh Start");
            Check(calls==2,
                "read-only preview of a Store-named fresh session does not spawn ASA");
            fresh.Arguments=originalLaunch+" -ClusterDirOverride=C:\\Unsafe";
            var forbiddenTransfer=await Call(starter.HandleAsync,
                "servers.freshStartPreview",new{mapId=fresh.Id});
            Check(IsError(forbiddenTransfer,"UM-FRESH-START-NOT-READY")&&calls==2,
                "unapproved cluster transfer override is rejected for non-Store ASA");
            fresh.Arguments=originalLaunch+" -usestore";
            var forbiddenStore=await Call(starter.HandleAsync,
                "servers.freshStartPreview",new{mapId=fresh.Id});
            Check(IsError(forbiddenStore,"UM-FRESH-START-NOT-READY")&&calls==2,
                "Store-v1 launch switch cannot bypass native enrollment");
        }
        finally {fresh.Arguments=originalLaunch;}
    }

    // Local serial SteamCMD batch: a new UI command must delegate to the
    // existing Core updater/queue rather than reimplementing file updates.
    // Synthetic runner blocks so we can prove single-use approval and
    // read-only reattachment; no actual SteamCMD or ASA process is launched.
    {
        var batchTarget=cluster.Config.LocalMaps.Single(x=>x.Name=="Valguero");
        var second=new LocalMap {
            Name="Synthetic shared Steam installation",
            InstallationPath=batchTarget.InstallationPath,
            ExecutablePath=batchTarget.ExecutablePath
        };
        cluster.AddMap(second);
        int batchRuns=0;
        var entered=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] untouched=File.ReadAllBytes(batchTarget.ExecutablePath);
        try
        {
            using var batch=new DesktopStoppedSteamBatchBridge(cluster,
                async (maps,progress)=>{
                    batchRuns++;
                    Check(maps.Count==1&&maps[0].Id==batchTarget.Id,
                        "shared ASA installations are updated only once in first-selected order");
                    progress.Report(new SteamQueueProgress(1,1,maps[0].Name,"Synthetic running"));
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(12));
                    return new [] { new SteamStoppedMapUpdater.Receipt(
                        maps[0].Id,maps[0].InstallationPath,
                        "123456","123457","synthetic-backup",
                        DateTimeOffset.UtcNow,true) };
                },
                map=>new SteamUpdatePreview(
                    Path.GetFullPath(map.InstallationPath),
                    new[]{batchTarget.Name,second.Name},"SYNTHETIC-STEAM",
                    "123456",10L*1024*1024*1024,20L*1024*1024*1024,
                    true,"Isolated preview"));

            var unknown=await Call(batch.HandleAsync,"steam.batchPreview",
                new{mapIds=new[]{Guid.NewGuid()}});
            Check(!unknown.GetProperty("ok").GetBoolean()&&batchRuns==0,
                "local Steam batch cannot accept unknown or remote map IDs");
            var duplicates=await Call(batch.HandleAsync,"steam.batchPreview",
                new{mapIds=new[]{batchTarget.Id,batchTarget.Id}});
            Check(!duplicates.GetProperty("ok").GetBoolean()&&batchRuns==0,
                "duplicated map IDs cannot execute multiple SteamCMD jobs");
            // A browser cannot declare a map "historical and stopped" and
            // thereby acquire the original Store-v1 Steam maintenance path.
            // Core requires a trusted Windows observer before creating its
            // serial queue journal or executing SteamCMD.
            second.HistoricalStoreProtected=true;
            bool originalObserverRequired=false;
            try
            {
                _=await SteamStoppedMapUpdater.UpdateAllStoppedAsync(
                    cluster,new[]{second},cluster.Config.SteamCmdExecutable,
                    10L*1024*1024*1024,TimeSpan.FromMinutes(5));
            }
            catch(UnauthorizedAccessException){originalObserverRequired=true;}
            Check(originalObserverRequired && batchRuns==0,
                "original Store-v1 batch cannot start without BOTH native stopped-family observers");
            var quarantined=await Call(batch.HandleAsync,"steam.batchPreview",
                new{mapIds=new[]{batchTarget.Id,second.Id}});
            Check(!quarantined.GetProperty("ok").GetBoolean()&&batchRuns==0,
                "a historical Store sibling blocks shared-install Steam update");
            second.HistoricalStoreProtected=false;
            // Positive isolated Core guard: a source-identified original ASA
            // installation becomes eligible ONLY with a trusted observed
            // STOPPED process family. This test never launches SteamCMD.
            string originalRoot=Path.Combine(Path.GetTempPath(),
                "Untamed-Integration-StoreSteam-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(originalRoot);
            var isolatedOriginal=new LocalMap{
                Id=Guid.NewGuid(),Name="Synthetic stopped original Store ASA",
                InstallationPath=originalRoot
            };
            cluster.AddMap(isolatedOriginal);
            try
            {
                isolatedOriginal.HistoricalStoreProtected=true;
                isolatedOriginal.HistoricalMapKey="Astraeos_WP";
                isolatedOriginal.HistoricalArchiveSha256=new string('A',64);
                Func<LocalMap,ProcessCheck> safelyStopped=_=>
                    new ProcessCheck(false,"Synthetic independent Windows STOPPED evidence",null,true);
                SteamStoppedMapUpdater.DemandSafeLocalInstallation(
                    cluster,isolatedOriginal,safelyStopped);
                bool unprovenDenied=false;
                try{SteamStoppedMapUpdater.DemandSafeLocalInstallation(cluster,isolatedOriginal);}
                catch(UnauthorizedAccessException){unprovenDenied=true;}
                Check(unprovenDenied,"original ASA serial maintenance refuses absent native observer");
                isolatedOriginal.Arguments="-UnapprovedLaunch";
                bool changedLaunchDenied=false;
                try{SteamStoppedMapUpdater.DemandSafeLocalInstallation(
                    cluster,isolatedOriginal,safelyStopped);}
                catch(UnauthorizedAccessException){changedLaunchDenied=true;}
                Check(changedLaunchDenied,
                    "source-pinned original Store ASA refuses unapproved launch-profile mutation");
                isolatedOriginal.Arguments="";
                isolatedOriginal.HistoricalLaunchModeNeedsApproval=true;
                bool pendingModeDenied=false;
                try{SteamStoppedMapUpdater.DemandSafeLocalInstallation(
                    cluster,isolatedOriginal,safelyStopped);}
                catch(UnauthorizedAccessException){pendingModeDenied=true;}
                Check(pendingModeDenied,
                    "original Store batch refuses ASA API/direct launch mode awaiting approval");
                isolatedOriginal.HistoricalLaunchModeNeedsApproval=false;
                bool runningObserverDenied=false;
                try{SteamStoppedMapUpdater.DemandSafeLocalInstallation(
                    cluster,isolatedOriginal,_=>new ProcessCheck(true,
                        "Synthetic original ASA still runs",null,false));}
                catch(InvalidOperationException){runningObserverDenied=true;}
                Check(runningObserverDenied,
                    "original Store batch cannot update a running ASA family");
            }
            finally
            {
                cluster.RemoveMap(isolatedOriginal.Id);
                try{Directory.Delete(originalRoot,true);}catch{}
            }
            var planned=await Call(batch.HandleAsync,"steam.batchPreview",
                new{mapIds=new[]{batchTarget.Id,second.Id}});
            IsOk(planned,"native serial SteamCMD preview validates both local map identities");
            var info=planned.GetProperty("data");
            Check(info.GetProperty("requestedMaps").GetInt32()==2 &&
                  info.GetProperty("installations").GetInt32()==1 &&
                  !info.GetProperty("storeWriterTouched").GetBoolean() &&
                  !info.GetProperty("serverStopRequested").GetBoolean() &&
                  batchRuns==0,
                "two local maps sharing one root have exactly one read-only SteamCMD plan");
            string approval=info.GetProperty("token").GetString()!;
            var badConsent=await Call(batch.HandleAsync,"steam.batchApply",
                new{token=approval,confirm=true,typedApproval="WRONG"});
            Check(!badConsent.GetProperty("ok").GetBoolean()&&batchRuns==0,
                "local serial Steam updates require the exact typed approval");
            var started=await Call(batch.HandleAsync,"steam.batchApply",
                new{token=approval,confirm=true,
                    typedApproval="HELYI SOROS STEAMCMD"});
            IsOk(started,"explicit serial batch approval starts existing native Core operation");
            string jobId=started.GetProperty("data").GetProperty("jobId").GetString()!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
            var active=await Call(batch.HandleAsync,"steam.batchCurrent");
            Check(active.GetProperty("data").GetProperty("activeJob")
                    .GetProperty("jobId").GetString()==jobId && batchRuns==1 &&
                  !active.GetProperty("data").GetProperty("automaticRetry").GetBoolean(),
                "React can reattach to active serial Steam job without duplicate download");
            var replay=await Call(batch.HandleAsync,"steam.batchApply",
                new{token=approval,confirm=true,
                    typedApproval="HELYI SOROS STEAMCMD"});
            Check(!replay.GetProperty("ok").GetBoolean()&&batchRuns==1,
                "consumed serial Steam approval cannot replay");
            release.TrySetResult(true);
            JsonElement progress=default;
            for(int i=0;i<60;i++)
            {
                progress=await Call(batch.HandleAsync,"steam.batchStatus",
                    new{jobId});
                if(progress.GetProperty("data").GetProperty("state").GetString()
                    =="completed")break;
                await Task.Delay(30);
            }
            IsOk(progress,"native serial Steam job reports completion");
            Check(progress.GetProperty("data").GetProperty("state")
                    .GetString()=="completed" &&
                  progress.GetProperty("data").GetProperty("completedInstallations")
                    .GetInt32()==1 &&
                  batchRuns==1 &&
                  untouched.SequenceEqual(File.ReadAllBytes(batchTarget.ExecutablePath)),
                "serial Steam batch executes exactly once and leaves real ASA binary unchanged in fixture");
            // Exercise the approved original Store-v1 path end to end through
            // the SAME native bridge/queue contract. The injected observer
            // never sees a real ASA process, and the runner never calls Steam.
            var savedOriginal=new{
                batchTarget.HistoricalStoreProtected,
                batchTarget.HistoricalMapKey,
                batchTarget.HistoricalArchiveSha256,
                batchTarget.HistoricalLaunchModeNeedsApproval,
                batchTarget.Arguments,batchTarget.ApiLoaderArguments
            };
            int historicalRuns=0,observedStopped=0;
            try
            {
                foreach(var m in new[]{batchTarget,second})
                {
                    m.HistoricalStoreProtected=true;
                    m.HistoricalMapKey=m.Name.Replace(" ","")+"WP";
                    m.HistoricalArchiveSha256=new string('A',64);
                    m.HistoricalLaunchModeNeedsApproval=false;
                    m.Arguments="";m.ApiLoaderArguments="";
                }
                using var originalBatch=new DesktopStoppedSteamBatchBridge(cluster,
                    (maps,progress)=>{
                        historicalRuns++;
                        Check(maps.Count==1&&maps[0].Id==batchTarget.Id,
                            "original Store installation deduplicates shared ASA binaries");
                        IReadOnlyList<SteamStoppedMapUpdater.Receipt> receipts=new[]{
                            new SteamStoppedMapUpdater.Receipt(maps[0].Id,
                                maps[0].InstallationPath,"123456","123457",
                                "synthetic-original-backup",DateTimeOffset.UtcNow,true)
                        };
                        return Task.FromResult(receipts);
                    },
                    map=>new SteamUpdatePreview(
                        Path.GetFullPath(map.InstallationPath),
                        new[]{batchTarget.Name,second.Name},"SYNTHETIC-STEAM",
                        "123456",10L*1024*1024*1024,20L*1024*1024*1024,
                        true,"Approved original offline fixture"),
                    (map,underOwnOperation)=>{
                        observedStopped++;
                        return new ProcessCheck(false,"Native original ASA STOPPED fixture",null,true);
                    });
                var originalPreview=await Call(originalBatch.HandleAsync,
                    "steam.batchPreview",new{mapIds=new[]{batchTarget.Id,second.Id}});
                IsOk(originalPreview,
                    "native serial batch previews approved original Store-v1 local ASA maps");
                Check(originalPreview.GetProperty("data").GetProperty("installations")
                    .GetInt32()==1&&historicalRuns==0&&observedStopped>0,
                    "original Store batch uses native stopped-family evidence before approval");
                string originalToken=originalPreview.GetProperty("data")
                    .GetProperty("token").GetString()!;
                var originalStarted=await Call(originalBatch.HandleAsync,
                    "steam.batchApply",new{token=originalToken,confirm=true,
                        typedApproval="HELYI SOROS STEAMCMD"});
                IsOk(originalStarted,
                    "operator approval can run the unchanged original Store ASA batch path");
                string originalJob=originalStarted.GetProperty("data")
                    .GetProperty("jobId").GetString()!;
                JsonElement originalFinished=default;
                for(int tries=0;tries<60;tries++)
                {
                    originalFinished=await Call(originalBatch.HandleAsync,
                        "steam.batchStatus",new{jobId=originalJob});
                    if(originalFinished.GetProperty("data").GetProperty("state")
                       .GetString()=="completed")break;
                    await Task.Delay(30);
                }
                IsOk(originalFinished,"original Store batch status is queryable");
                Check(originalFinished.GetProperty("data").GetProperty("state")
                      .GetString()=="completed"&&historicalRuns==1&&
                      originalFinished.GetProperty("data")
                        .GetProperty("completedInstallations").GetInt32()==1&&
                      untouched.SequenceEqual(File.ReadAllBytes(batchTarget.ExecutablePath)),
                    "one native original Store batch executes exactly once without touching ASA/Store bytes");
                var originalReplay=await Call(originalBatch.HandleAsync,
                    "steam.batchApply",new{token=originalToken,confirm=true,
                        typedApproval="HELYI SOROS STEAMCMD"});
                Check(!originalReplay.GetProperty("ok").GetBoolean()&&historicalRuns==1,
                    "original Store serial SteamCMD cannot replay a consumed token");
            }
            finally
            {
                batchTarget.HistoricalStoreProtected=savedOriginal.HistoricalStoreProtected;
                batchTarget.HistoricalMapKey=savedOriginal.HistoricalMapKey;
                batchTarget.HistoricalArchiveSha256=savedOriginal.HistoricalArchiveSha256;
                batchTarget.HistoricalLaunchModeNeedsApproval=savedOriginal.HistoricalLaunchModeNeedsApproval;
                batchTarget.Arguments=savedOriginal.Arguments;
                batchTarget.ApiLoaderArguments=savedOriginal.ApiLoaderArguments;
                second.HistoricalStoreProtected=false;
                second.HistoricalMapKey="";
                second.HistoricalArchiveSha256="";
                second.HistoricalLaunchModeNeedsApproval=false;
                second.Arguments="";second.ApiLoaderArguments="";
            }
        }
        finally{release.TrySetResult(true);cluster.RemoveMap(second.Id);}
    }

    // Actual SteamCMD entry point uses Core's stopped-map update and per-host
    // queue. The adapter is injected only in this isolated Windows fixture.
    {
        var target=cluster.Config.LocalMaps.Single(x=>x.Name=="Valguero");
        int executions=0;
        bool failNextSyntheticUpdate=false;
        IProgress<SteamMaintenanceStage>? delayedSteamProgress=null;
        // Hold the fake native runner at an observable in-progress boundary;
        // UI reattachment must never schedule a second update.
        var runnerEntered=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRunnerFinish=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] original=File.ReadAllBytes(target.ExecutablePath);
        using var updater=new DesktopStoppedSteamUpdateBridge(cluster,
            m=>new SteamUpdatePreview(Path.GetFullPath(m.InstallationPath)
                .TrimEnd(Path.DirectorySeparatorChar),new[]{m.Name},
                "FAKE-STEAMCMD-DIGEST","123456",
                10L*1024*1024*1024,20L*1024*1024*1024,true,"Synthetic plan"),
            async(m,progress)=>{
                executions++;
                delayedSteamProgress=progress;
                progress.Report(SteamMaintenanceStage.Backup);
                runnerEntered.TrySetResult(true);
                await allowRunnerFinish.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if(failNextSyntheticUpdate)
                    throw new InvalidOperationException("SYNTHETIC-STEAM-FAILURE");
                return new SteamStoppedMapUpdater.Receipt(m.Id,m.InstallationPath,
                    "123456","123457","fake-backup",
                    DateTimeOffset.UtcNow,true);
            },
            (map,_)=>Task.FromResult(AsaSteamBuildCatalog.Compare(
                map.InstallationPath,"public","123456","123457",DateTimeOffset.UtcNow)));
        var unknown=await Call(updater.HandleAsync,"steam.stoppedPreview",
            new{mapId=Guid.NewGuid()});
        Check(IsError(unknown,"UM-MAP-NOT-OWNED"),
            "remote or unknown map cannot schedule local SteamCMD update");
        var publishedDenied=await Call(updater.HandleAsync,"steam.publishedBuild",
            new{mapId=Guid.NewGuid()});
        Check(IsError(publishedDenied,"UM-MAP-NOT-OWNED"),
            "unknown or remote map cannot query native Steam metadata");
        var published=await Call(updater.HandleAsync,"steam.publishedBuild",
            new{mapId=target.Id});
        IsOk(published,"read-only published ASA Steam build check uses native Core comparison");
        var buildInfo=published.GetProperty("data");
        Check(buildInfo.GetProperty("installedBuild").GetString()=="123456" &&
              buildInfo.GetProperty("availableBuild").GetString()=="123457" &&
              buildInfo.GetProperty("updateAvailable").GetBoolean() &&
              !buildInfo.GetProperty("downloadStarted").GetBoolean() &&
              !buildInfo.GetProperty("serverStopped").GetBoolean() &&
              !buildInfo.GetProperty("storeWriterTouched").GetBoolean()&&
              executions==0&&
              original.SequenceEqual(File.ReadAllBytes(target.ExecutablePath)),
            "published appinfo check never executes the updater or modifies ASA files");
        // One-click stopped SteamCMD must not bypass the shared-install
        // historical Store protection already enforced by the batch UI.
        // Core and the renderer both refuse BEFORE any runner invocation.
        var storeSibling=new LocalMap {
            Name="Historical Store shared Steam root",
            InstallationPath=target.InstallationPath,
            ExecutablePath=target.ExecutablePath,
            HistoricalMapKey="OriginalKovaStoreMap"
        };
        cluster.AddMap(storeSibling);
        try
        {
            bool coreRefused=false;
            try { SteamStoppedMapUpdater.DemandSafeLocalInstallation(cluster,target); }
            catch(UnauthorizedAccessException) { coreRefused=true; }
            var denied=await Call(updater.HandleAsync,"steam.stoppedPreview",
                new{mapId=target.Id});
            Check(coreRefused && IsError(denied,"UM-STEAM-STOPPED-ONLY") &&
                executions==0 && original.SequenceEqual(File.ReadAllBytes(target.ExecutablePath)),
                "single-map and Core Steam update both refuse a stopped original Kova Store sibling");

            storeSibling.HistoricalMapKey="";
            storeSibling.FreshStoreClusterId="test-enrolled-store-cluster";
            coreRefused=false;
            try { SteamStoppedMapUpdater.DemandSafeLocalInstallation(cluster,target); }
            catch(UnauthorizedAccessException) { coreRefused=true; }
            denied=await Call(updater.HandleAsync,"steam.stoppedPreview",
                new{mapId=target.Id});
            Check(coreRefused && IsError(denied,"UM-STEAM-STOPPED-ONLY") &&
                executions==0,
                "new Store-v1 sibling enrollment also blocks shared-root Steam binary replacement");
        }
        finally {cluster.RemoveMap(storeSibling.Id);}

        var nestedOwner=new LocalMap {
            Name="Nested Steam installation owner",
            InstallationPath=Path.Combine(target.InstallationPath,"nested-server"),
            ExecutablePath=Path.Combine(target.InstallationPath,"nested-server",
                "ShooterGame","Binaries","Win64","ArkAscendedServer.exe")
        };
        cluster.AddMap(nestedOwner);
        try
        {
            bool coreRefused=false;
            try { SteamStoppedMapUpdater.DemandSafeLocalInstallation(cluster,target); }
            catch(InvalidDataException) {coreRefused=true;}
            var denied=await Call(updater.HandleAsync,"steam.stoppedPreview",
                new{mapId=target.Id});
            Check(coreRefused && IsError(denied,"UM-STEAM-STOPPED-ONLY") &&
                executions==0,
                "overlapping nested ASA installation ownership refuses unsafe one-click SteamCMD");
        }
        finally {cluster.RemoveMap(nestedOwner.Id);}
        var proposed=await Call(updater.HandleAsync,"steam.stoppedPreview",
            new{mapId=target.Id});
        IsOk(proposed,"stopped-map SteamCMD update preflight is functional");
        Check(executions==0,"SteamCMD preview has no launch side effect");
        string token=proposed.GetProperty("data").GetProperty("token").GetString()!;
        // A second locally registered map sharing the ASA installation
        // may be inspected without invalidating the first map's consent.
        // Execution remains one SteamCMD at a time per host.
        var siblingSteamMap=new LocalMap{
            Name="Second stopped SteamCMD map",
            InstallationPath=target.InstallationPath,
            ExecutablePath=target.ExecutablePath
        };
        cluster.AddMap(siblingSteamMap);
        string siblingToken="";
        try
        {
            var siblingPreview=await Call(updater.HandleAsync,"steam.stoppedPreview",
                new{mapId=siblingSteamMap.Id});
            IsOk(siblingPreview,"separate stopped map receives an independent update preview");
            siblingToken=siblingPreview.GetProperty("data").GetProperty("token").GetString()!;
            Check(siblingToken!=token &&
                  siblingPreview.GetProperty("data").GetProperty("mapId").GetGuid()==siblingSteamMap.Id &&
                  executions==0,
                "previewing a sibling never replaces the first map's SteamCMD approval");
        }
        finally{cluster.RemoveMap(siblingSteamMap.Id);}
        var refused=await Call(updater.HandleAsync,"steam.stoppedApply",
            new{token,confirm=false});
        Check(IsError(refused,"UM-STEAM-CONFIRMATION-REQUIRED") && executions==0,
            "SteamCMD update requires native approval");
        var launched=await Call(updater.HandleAsync,"steam.stoppedApply",
            new{token,confirm=true});
        IsOk(launched,"approved update dispatches native verified Core operation");
        string job=launched.GetProperty("data").GetProperty("jobId").GetString()!;
        await runnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var observedWhileRunning=await Call(updater.HandleAsync,"steam.stoppedCurrent");
        IsOk(observedWhileRunning,
            "UI reattach can read current native SteamCMD update without rerunning it");
        var resume=observedWhileRunning.GetProperty("data");
        Check(!resume.GetProperty("exists").GetBoolean() &&
              resume.GetProperty("activeJob").GetProperty("jobId").GetString()==job &&
              resume.GetProperty("activeJob").GetProperty("mapId").GetGuid()==target.Id &&
              resume.GetProperty("activeJob").GetProperty("state").GetString()=="running" &&
              !resume.GetProperty("autoResumeAllowed").GetBoolean() && executions==1,
            "current-status exposes only the existing active Steam job and never restarts it");
        var parallel=await Call(updater.HandleAsync,"steam.stoppedApply",
            new{token=siblingToken,confirm=true});
        Check(IsError(parallel,"UM-STEAM-UPDATE-IN-PROGRESS")&&executions==1,
            "another map's accepted SteamCMD preview cannot run concurrently on the same host");
        var replay=await Call(updater.HandleAsync,"steam.stoppedApply",
            new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "same SteamCMD approval token cannot replay");
        allowRunnerFinish.TrySetResult(true);
        JsonElement result=default;
        for(int n=0;n<60;n++){
            result=await Call(updater.HandleAsync,"steam.stoppedStatus",
                new{jobId=job});
            if(result.GetProperty("data").GetProperty("state").GetString()=="completed")break;
            await Task.Delay(25);
        }
        IsOk(result,"native SteamCMD update reports progress");
        Check(result.GetProperty("data").GetProperty("state").GetString()=="completed" &&
              result.GetProperty("data").GetProperty("mapId").GetGuid()==target.Id &&
              executions==1 &&
              result.GetProperty("data").GetProperty("protectedFilesIntact").GetBoolean() &&
              original.SequenceEqual(File.ReadAllBytes(target.ExecutablePath)),
            "SteamCMD update runs once and preserves fixture ASA executable");
        Check(delayedSteamProgress!=null,
            "synthetic native updater exposes progress for late callback regression");
        // .NET Progress<T> may dispatch queued callbacks after the awaited
        // native SteamCMD runner has already committed the success receipt.
        delayedSteamProgress!.Report(SteamMaintenanceStage.Backup);
        await Task.Delay(250);
        var stableAfterLateProgress=await Call(updater.HandleAsync,
            "steam.stoppedStatus",new{jobId=job});
        IsOk(stableAfterLateProgress,
            "verified SteamCMD receipt remains queryable after delayed progress");
        var stableReceipt=stableAfterLateProgress.GetProperty("data");
        Check(stableReceipt.GetProperty("state").GetString()=="completed" &&
              stableReceipt.GetProperty("stage").GetString()=="verified" &&
              stableReceipt.GetProperty("protectedFilesIntact").GetBoolean() &&
              executions==1,
            "late SteamCMD progress cannot revert completed job to running or discard verification");
        var missingSibling=await Call(updater.HandleAsync,"steam.stoppedApply",
            new{token=siblingToken,confirm=true});
        Check(IsError(missingSibling,"UM-STEAM-PREFLIGHT-CHANGED")&&executions==1,
            "removed sibling map cannot consume an old SteamCMD approval after the first update");
        var retained=await Call(updater.HandleAsync,"steam.stoppedStatus",new{jobId=job});
        IsOk(retained,"completed map-owned SteamCMD job receipt remains readable");
        Check(retained.GetProperty("data").GetProperty("mapId").GetGuid()==target.Id,
            "SteamCMD status cannot silently change to a sibling map");
        // A failure receipt must likewise be immune to delayed progress.
        // No real SteamCMD process is invoked in this negative fixture.
        failNextSyntheticUpdate=true;
        var failurePreview=await Call(updater.HandleAsync,
            "steam.stoppedPreview",new{mapId=target.Id});
        IsOk(failurePreview,"failed synthetic SteamCMD run has fresh explicit approval");
        var failureToken=failurePreview.GetProperty("data")
            .GetProperty("token").GetString()!;
        var failureStarted=await Call(updater.HandleAsync,
            "steam.stoppedApply",new{token=failureToken,confirm=true});
        IsOk(failureStarted,"synthetic failure is tracked as a native SteamCMD job");
        string failedJobId=failureStarted.GetProperty("data")
            .GetProperty("jobId").GetString()!;
        JsonElement failedResult=default;
        for(int i=0;i<60;i++)
        {
            failedResult=await Call(updater.HandleAsync,
                "steam.stoppedStatus",new{jobId=failedJobId});
            if(failedResult.GetProperty("data").GetProperty("state")
               .GetString()=="needsOperator")break;
            await Task.Delay(25);
        }
        IsOk(failedResult,"native Steam failure remains queryable");
        Check(failedResult.GetProperty("data").GetProperty("state")
              .GetString()=="needsOperator"&&executions==2,
            "failed SteamCMD runner requires operator review without automatic replay");
        delayedSteamProgress!.Report(SteamMaintenanceStage.VerifyBuild);
        await Task.Delay(250);
        var failedAfterLate=await Call(updater.HandleAsync,
            "steam.stoppedStatus",new{jobId=failedJobId});
        IsOk(failedAfterLate,"failed SteamCMD job remains readable after late progress");
        Check(failedAfterLate.GetProperty("data").GetProperty("state")
              .GetString()=="needsOperator"&&executions==2,
            "late SteamCMD progress never hides an operator-required failure");
        var emptyJournal=await Call(updater.HandleAsync,"steam.stoppedCurrent");
        IsOk(emptyJournal,"first-run Steam queue journal status is readable");
        Check(!emptyJournal.GetProperty("data").GetProperty("exists").GetBoolean(),
            "no queue journal is not misreported as an update");
        string folder=Path.Combine(cluster.Store.Root,"steam-maintenance-queue");
        Directory.CreateDirectory(folder);
        string journalPath=Path.Combine(folder,"queue-v1.json");
        var interrupted=new SteamQueueSnapshot(
            Guid.NewGuid(),SteamQueuePhase.NeedsOperator,
            cluster.Config.LocalHostId,0,new[]{target.Id},
            target.Id,DateTimeOffset.UtcNow,"Synthetic interrupted Steam update");
        File.WriteAllText(journalPath,JsonSerializer.Serialize(interrupted));
        try
        {
            var recovered=await Call(updater.HandleAsync,"steam.stoppedCurrent");
            IsOk(recovered,"durable Steam journal is readable after interruption");
            var status=recovered.GetProperty("data");
            Check(status.GetProperty("needsOperatorReview").GetBoolean() &&
                  !status.GetProperty("autoResumeAllowed").GetBoolean() &&
                  status.GetProperty("phase").GetString()=="NeedsOperator",
                "interrupted per-host Steam update refuses automatic retry");
            var blocked=await Call(updater.HandleAsync,"steam.stoppedPreview",
                new{mapId=target.Id});
            Check(IsError(blocked,"UM-STEAM-JOURNAL-NEEDS-REVIEW"),
                "native update preflight cannot bypass interrupted persistent queue");
        }
        finally{File.Delete(journalPath);}
    }

    // Graceful RCON SaveWorld/DoExit native bridge: the real Core is tested
    // with a fake local process/transport and NO network or ASA server.
    {
        string stopRoot=Path.Combine(root,"graceful-asa-stop-lab");
        string binaryDir=Path.Combine(stopRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binaryDir);
        string exe=Path.Combine(binaryDir,"ArkAscendedServer.exe");
        File.WriteAllBytes(exe,new byte[]{0x4d,0x5a,0x90,0x00});
        var stopLabMap=new LocalMap {
            Name="New lab stopLabMap",InstallationPath=stopRoot,ExecutablePath=exe,
            Arguments="TheIsland_WP?listen -port=7798 -QueryPort=27048",
            GamePort=7798,QueryPort=27048,RconPort=37556,
            RconHost="127.0.0.1",
            RconPasswordProtected=SecureStore.Protect("SYNTHETIC-LOCAL-STOP-CREDENTIAL")
        };
        var identity=new ProcessIdentity {
            Pid=44117,StartedUtc=DateTimeOffset.UtcNow,
            ExecutablePath=exe,Arguments=stopLabMap.Arguments
        };
        bool stopLabRunning=true;
        await using var lab=new ClusterService("graceful-asa-native-test",
            Path.Combine(root,"graceful-asa-native-profile"),
            _=>stopLabRunning?
                new ProcessCheck(true,"synthetic owned local ASA",identity):
                new ProcessCheck(false,"synthetic stopped",null,true));
        lab.AddMap(stopLabMap);
        var stopSibling=new LocalMap{
            Name="Historical Store sibling",
            InstallationPath=stopRoot,
            HistoricalArchiveSha256=new string('B',64)
        };
        lab.AddMap(stopSibling);
        Check(!FreshAsaInstallationSafety.IsProvenFresh(lab,stopLabMap),
            "historical metadata on shared install forbids even new-profile lifecycle");
        lab.RemoveMap(stopSibling.Id);
        Check(FreshAsaInstallationSafety.IsProvenFresh(lab,stopLabMap),
            "clean new local installation passes shared provenance safety");
        int sends=0;
        bool Owner(int port,int pid)=>port==37556&&pid==44117;
        using var stopBridge=new DesktopFreshAsaStopBridge(lab,Owner,
            async(id,expected)=>{
                var receipt=await AsaGracefulStop.StopAsync(lab,id,expected,
                    TimeSpan.FromSeconds(6),
                    sendCommands:(m,p,ct)=>{
                        sends++;
                        Check(m.Id==stopLabMap.Id&&p.Pid==identity.Pid,
                            "graceful Stop writes only to exact approved process");
                        stopLabRunning=false;
                        return Task.CompletedTask;
                    },listenerOwner:Owner);
                return receipt;
            });
        var missingStopCurrent=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopCurrent",new{mapId=Guid.NewGuid()});
        Check(IsError(missingStopCurrent,"UM-MAP-NOT-OWNED"),
            "Stop-current refuses unknown and remote map identities");
        var idleStopCurrent=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopCurrent",new{mapId=stopLabMap.Id});
        IsOk(idleStopCurrent,"idle map Stop-current is read-only and allowed");
        Check(!idleStopCurrent.GetProperty("data").GetProperty("active").GetBoolean()&&
              idleStopCurrent.GetProperty("data").GetProperty("jobId").ValueKind==
                  JsonValueKind.Null&&sends==0,
            "idle Stop-current never invents a job or sends RCON");
        var unknown=await Call(stopBridge.HandleAsync,"servers.gracefulStopPreview",
            new{mapId=Guid.NewGuid()});
        Check(IsError(unknown,"UM-MAP-NOT-OWNED")&&sends==0,
            "unknown/remote stopLabMap cannot stop local ASA");
        var preview=await Call(stopBridge.HandleAsync,"servers.gracefulStopPreview",
            new{mapId=stopLabMap.Id});
        IsOk(preview,"native graceful Stop verifies owned PID and local RCON port");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var noApproval=await Call(stopBridge.HandleAsync,"servers.gracefulStopApply",
            new{token,confirm=false});
        Check(IsError(noApproval,"UM-ASA-STOP-APPROVAL")&&sends==0,
            "graceful Stop requires explicit operator confirmation");
        var started=await Call(stopBridge.HandleAsync,"servers.gracefulStopApply",
            new{token,confirm=true});
        IsOk(started,"graceful Stop begins without blocking WebView2");
        string job=started.GetProperty("data").GetProperty("jobId").GetString()!;
        var runningStopCurrent=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopCurrent",new{mapId=stopLabMap.Id});
        IsOk(runningStopCurrent,"Stop-current reattaches to the exact map's existing job");
        Check(runningStopCurrent.GetProperty("data").GetProperty("jobId").GetString()==job,
            "Stop-current only exposes the original one-shot Stop ID");
        var replay=await Call(stopBridge.HandleAsync,"servers.gracefulStopApply",
            new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "DoExit approval cannot be replayed");
        JsonElement result=default;
        for(int i=0;i<65;i++){
            result=await Call(stopBridge.HandleAsync,"servers.gracefulStopStatus",
                new{jobId=job});
            if(result.GetProperty("data").GetProperty("state").GetString()=="completed")break;
            await Task.Delay(35);
        }
        IsOk(result,"native graceful stop job status remains readable");
        var finishedStopCurrent=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopCurrent",new{mapId=stopLabMap.Id});
        IsOk(finishedStopCurrent,"finished Stop-current remains observable without dispatch");
        Check(finishedStopCurrent.GetProperty("data").GetProperty("jobId").GetString()==job &&
              !finishedStopCurrent.GetProperty("data").GetProperty("active").GetBoolean(),
            "completed Stop is not replayed and remains bound to its map");
        Check(result.GetProperty("data").GetProperty("state").GetString()=="completed" &&
              result.GetProperty("data").GetProperty("confirmedStopped").GetBoolean() &&
              !result.GetProperty("data").GetProperty("saveCompleteProven").GetBoolean() &&
              !result.GetProperty("data").GetProperty("killPerformed").GetBoolean() &&
              sends==1,
            "native DoExit verifies process exit without inventing SaveComplete");
        stopLabRunning=true;
        // An ordinary SessionName may contain Store keywords and text
        // resembling ASA switches. Stop must agree with the common native
        // Start/backup parser instead of substring-matching the word "store".
        string originalStopArguments=stopLabMap.Arguments;
        stopLabMap.Arguments=
            "TheIsland_WP?listen?SessionName=\"Untamed Store -usestore Celebration\" -port=7798 -QueryPort=27048";
        identity=new ProcessIdentity{
            Pid=44117,StartedUtc=identity.StartedUtc,
            ExecutablePath=exe,Arguments=stopLabMap.Arguments
        };
        var namedStop=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopPreview",new{mapId=stopLabMap.Id});
        IsOk(namedStop,
            "graceful Stop accepts Store words in an ordinary quoted SessionName");
        stopLabMap.Arguments=originalStopArguments+" -usestore";
        var actualWriterStop=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopPreview",new{mapId=stopLabMap.Id});
        Check(IsError(actualWriterStop,"UM-ASA-STOP-FAILED")&&sends==1,
            "real -usestore switch cannot enter non-enrolled graceful Stop");
        stopLabMap.Arguments=originalStopArguments+" -clusterid=NotAnOwnedCluster";
        var actualClusterStop=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopPreview",new{mapId=stopLabMap.Id});
        Check(IsError(actualClusterStop,"UM-ASA-STOP-FAILED")&&sends==1,
            "real -clusterid switch cannot enter non-enrolled graceful Stop");
        stopLabMap.Arguments=originalStopArguments;
        stopLabMap.ApiLoaderArguments="-converttostore";
        var actualLoaderConversionStop=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopPreview",new{mapId=stopLabMap.Id});
        Check(IsError(actualLoaderConversionStop,"UM-ASA-STOP-FAILED")&&sends==1,
            "real Loader conversion switch cannot enter non-enrolled graceful Stop");
        stopLabMap.ApiLoaderArguments="";
        identity=new ProcessIdentity{
            Pid=44117,StartedUtc=identity.StartedUtc,
            ExecutablePath=exe,Arguments=originalStopArguments
        };
        stopLabMap.HistoricalMapKey="TheIsland_WP";
        var provenanceStop=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopPreview",new{mapId=stopLabMap.Id});
        Check(IsError(provenanceStop,"UM-ASA-STOP-FAILED")&&sends==1,
            "historical map key alone cannot authorize graceful Stop");
        stopLabMap.HistoricalMapKey="";
        stopLabMap.HistoricalStoreProtected=true;
        var protectedMap=await Call(stopBridge.HandleAsync,
            "servers.gracefulStopPreview",new{mapId=stopLabMap.Id});
        Check(IsError(protectedMap,"UM-ASA-STOP-FAILED")&&sends==1,
            "historical Store protection blocks all new graceful Stop operations");

        // The manual Restart uses a separate, one-shot approval. This fixture
        // injects a deterministic native runner; no real ASA process starts.
        stopLabMap.HistoricalStoreProtected=false;
        stopLabRunning=true;
        string manifests=Path.Combine(stopRoot,"steamapps");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests,"appmanifest_2430930.acf"),
            "\"AppState\"\n{\n\"appid\" \"2430930\"\n\"buildid\" \"123456\"\n}\n");
        AsaInitialRconSettings.CreateForNewServer(stopRoot,37556,
            "SYNTHETIC-LOCAL-STOP-CREDENTIAL");
        Check(AsaRconConfigurationEvidence.Matches(stopLabMap),
            "Restart requires a real local ASA RCON INI matching Manager credentials");
        int restartCalls=0;
        using var restartBridge=new DesktopFreshAsaRestartBridge(lab,Owner,
            async (id,old,journalId,progress)=>{
                restartCalls++;
                progress("Synthetic SaveWorld then DoExit");
                await Task.Delay(55);
                progress("Synthetic fresh ASA process");
                new AsaManualRestartJournal(stopRoot).Change(
                    journalId,AsaRestartPhase.Starting);
                var stopped=new AsaGracefulStopReceipt(id,old.Pid,
                    true,false,true,DateTimeOffset.UtcNow,
                    "SYNTHETIC_OLD_PROCESS_STOPPED");
                var replacement=new ProcessIdentity {
                    Pid=old.Pid+1,
                    StartedUtc=old.StartedUtc.AddSeconds(5),
                    ExecutablePath=old.ExecutablePath,
                    Arguments=old.Arguments
                };
                return (stopped,replacement);
            });
        // The old PID must NEVER be stopped just to discover that a
        // forbidden -usestore / -clusterid / Loader conversion switch
        // would prevent the new Process.Start. A quoted SessionName with
        // the same text is ordinary configuration and must remain legal.
        string originalRestartArgs=stopLabMap.Arguments;
        stopLabMap.Arguments=
            "TheIsland_WP?listen?SessionName=\"Untamed Store -usestore\" -port=7798 -QueryPort=27048";
        identity=new ProcessIdentity{
            Pid=44117,StartedUtc=identity.StartedUtc,
            ExecutablePath=exe,Arguments=stopLabMap.Arguments
        };
        var namedRestart=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        IsOk(namedRestart,
            "Restart accepts ordinary quoted Store session names before DoExit");
        stopLabMap.Arguments=originalRestartArgs+" -usestore";
        var writerRestart=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        Check(IsError(writerRestart,"UM-ASA-RESTART-FAILED")&&restartCalls==0,
            "Restart blocks unenrolled -usestore before old PID SaveWorld/DoExit");
        stopLabMap.Arguments=originalRestartArgs+" -clusterid=NotEnrolled";
        var clusterRestart=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        Check(IsError(clusterRestart,"UM-ASA-RESTART-FAILED")&&restartCalls==0,
            "Restart blocks unenrolled -clusterid before old PID DoExit");
        stopLabMap.Arguments=originalRestartArgs;
        stopLabMap.ApiLoaderArguments="-converttostore";
        var conversionRestart=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        Check(IsError(conversionRestart,"UM-ASA-RESTART-FAILED")&&restartCalls==0,
            "Restart blocks Loader conversion switch before old PID DoExit");
        stopLabMap.ApiLoaderArguments="";
        identity=new ProcessIdentity{
            Pid=44117,StartedUtc=identity.StartedUtc,
            ExecutablePath=exe,Arguments=originalRestartArgs
        };
        // Pin a deliberate fresh Restart approval to the same full map,
        // Game.ini, GameUserSettings.ini, ASA EXE and selected API Loader.
        // Even one changed byte must refuse BEFORE SaveWorld/DoExit.
        var profilePinned=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        IsOk(profilePinned,"fresh Restart source snapshot can be previewed");
        string pinnedToken=profilePinned.GetProperty("data")
            .GetProperty("token").GetString()!;
        string originalStopMapName=stopLabMap.Name;
        stopLabMap.Name+=" (externally changed)";
        var changedProfileRestart=await Call(restartBridge.HandleAsync,
            "servers.restartApply",new{token=pinnedToken,confirm=true});
        Check(IsError(changedProfileRestart,"UM-ASA-RESTART-SOURCE-CHANGED")&&
              restartCalls==0,
            "map profile mutation after Restart preview cannot stop original ASA");
        stopLabMap.Name=originalStopMapName;

        var iniPinned=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        IsOk(iniPinned,"fresh Restart INI snapshot can be previewed");
        string iniToken=iniPinned.GetProperty("data")
            .GetProperty("token").GetString()!;
        string gameIni=Path.Combine(stopRoot,"ShooterGame","Saved","Config",
            "WindowsServer","Game.ini");
        bool gameIniExisted=File.Exists(gameIni);
        byte[] oldGameIni=gameIniExisted?File.ReadAllBytes(gameIni):Array.Empty<byte>();
        try
        {
            File.AppendAllText(gameIni,"\n[ServerSettings]\nProbe=changed\n");
            var changedIniRestart=await Call(restartBridge.HandleAsync,
                "servers.restartApply",new{token=iniToken,confirm=true});
            Check(IsError(changedIniRestart,"UM-ASA-RESTART-SOURCE-CHANGED")&&
                  restartCalls==0,
                "Game.ini mutation after Restart approval cannot stop original ASA");
        }
        finally
        {
            if(gameIniExisted)File.WriteAllBytes(gameIni,oldGameIni);
            else File.Delete(gameIni);
        }

        var binaryPinned=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        IsOk(binaryPinned,"fresh Restart binary snapshot can be previewed");
        string binaryToken=binaryPinned.GetProperty("data")
            .GetProperty("token").GetString()!;
        byte[] originalStopExe=File.ReadAllBytes(exe);
        try
        {
            File.AppendAllText(exe,"synthetic-change-after-approval");
            var changedBinaryRestart=await Call(restartBridge.HandleAsync,
                "servers.restartApply",new{token=binaryToken,confirm=true});
            Check(IsError(changedBinaryRestart,"UM-ASA-RESTART-SOURCE-CHANGED")&&
                  restartCalls==0,
                "ASA EXE mutation after Restart approval cannot stop original ASA");
        }
        finally { File.WriteAllBytes(exe,originalStopExe); }

        var foreignRestart=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=Guid.NewGuid()});
        Check(IsError(foreignRestart,"UM-MAP-NOT-OWNED")&&restartCalls==0,
            "remote or unknown map cannot schedule local ASA restart");
        var restartPreview=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        IsOk(restartPreview,"verified local non-Store process allows Restart preview");
        string restartToken=restartPreview.GetProperty("data")
            .GetProperty("token").GetString()!;
        var declinedRestart=await Call(restartBridge.HandleAsync,
            "servers.restartApply",new{token=restartToken,confirm=false});
        Check(IsError(declinedRestart,"UM-ASA-RESTART-APPROVAL")&&restartCalls==0,
            "Restart never sends SaveWorld without explicit operator approval");
        var restartStarted=await Call(restartBridge.HandleAsync,
            "servers.restartApply",new{token=restartToken,confirm=true});
        IsOk(restartStarted,"one-shot restart dispatches native coordinator");
        string restartJob=restartStarted.GetProperty("data")
            .GetProperty("jobId").GetString()!;
        var currentRestartJob=await Call(restartBridge.HandleAsync,
            "servers.restartCurrent",new{mapId=stopLabMap.Id});
        IsOk(currentRestartJob,"Restart-current identifies accepted job after page change");
        Check(currentRestartJob.GetProperty("data")
                .GetProperty("inMemoryJobId").GetString()==restartJob,
            "Restart-current binds exactly one existing job to the local map");
        var doubleRestart=await Call(restartBridge.HandleAsync,
            "servers.restartApply",new{token=restartToken,confirm=true});
        Check(!doubleRestart.GetProperty("ok").GetBoolean(),
            "Restart token cannot be replayed");
        JsonElement finalRestart=default;
        for(int i=0;i<65;i++){
            finalRestart=await Call(restartBridge.HandleAsync,
                "servers.restartStatus",new{jobId=restartJob});
            if(finalRestart.GetProperty("data")
                .GetProperty("state").GetString()=="completed")break;
            await Task.Delay(30);
        }
        IsOk(finalRestart,"manual Restart reports real async progress");
        var restartData=finalRestart.GetProperty("data");
        Check(restartCalls==1 &&
              restartData.GetProperty("state").GetString()=="completed" &&
              restartData.GetProperty("oldPid").GetInt32()==44117 &&
              restartData.GetProperty("newPid").GetInt32()==44118 &&
              !restartData.GetProperty("saveCompleteProven").GetBoolean() &&
              !restartData.GetProperty("readinessCertified").GetBoolean() &&
              !restartData.GetProperty("autoRetry").GetBoolean(),
            "one verified old process exits before single replacement, no false readiness");
        var finishedRestartJournal=new AsaManualRestartJournal(stopRoot).Observe();
        Check(finishedRestartJournal?.Phase==AsaRestartPhase.Completed &&
              finishedRestartJournal.NewPid==44118,
            "manual Restart records its completed old/new PID durably");
        stopLabMap.HistoricalMapKey="TheIsland_WP";
        var historicalRestart=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        Check(IsError(historicalRestart,"UM-ASA-RESTART-FAILED"),
            "historical Store provenance denies manual Restart");
        stopLabMap.HistoricalMapKey="";
        var durableStatus=await Call(restartBridge.HandleAsync,
            "servers.restartCurrent",new{mapId=stopLabMap.Id});
        IsOk(durableStatus,"completed manual ASA Restart is readable after UI reload");
        Check(durableStatus.GetProperty("data").GetProperty("exists").GetBoolean() &&
              !durableStatus.GetProperty("data")
                  .GetProperty("needsOperatorReview").GetBoolean(),
            "completed restart is not falsely considered interrupted");
        var manualJournal=new AsaManualRestartJournal(stopRoot);
        // A caller supplying a stale restart ID must not bypass the journal
        // gate after a crash, removed journal, or completed prior restart.
        var absentJournal=new AsaManualRestartJournal(
            Path.Combine(root,"restart-approval-no-existing-map"),
            Path.Combine(root,"restart-approval-isolated-journals"));
        bool absentStopDenied=false,absentStartDenied=false;
        try{absentJournal.DemandAuthorizedStop(Guid.NewGuid());}
        catch(InvalidOperationException){absentStopDenied=true;}
        try{absentJournal.DemandAuthorizedStart(Guid.NewGuid());}
        catch(InvalidOperationException){absentStartDenied=true;}
        Check(absentStopDenied&&absentStartDenied,
            "explicit Restart Stop/Start IDs never authorize without a durable journal");
        bool completedStopDenied=false,completedStartDenied=false;
        try{manualJournal.DemandAuthorizedStop(finishedRestartJournal!.Id);}
        catch(InvalidOperationException){completedStopDenied=true;}
        try{manualJournal.DemandAuthorizedStart(finishedRestartJournal!.Id);}
        catch(InvalidOperationException){completedStartDenied=true;}
        Check(completedStopDenied&&completedStartDenied,
            "completed Restart cannot be replayed with its old approved ID");
        var crash=manualJournal.Begin(stopLabMap.Id,44117);
        Check(crash.Phase==AsaRestartPhase.Stopping,
            "new restart writes durable journal before sending RCON");
        manualJournal.DemandAuthorizedStop(crash.Id);
        bool refusedUnapprovedStart=false;
        try{manualJournal.DemandAuthorizedStart(null);}
        catch(InvalidOperationException){refusedUnapprovedStart=true;}
        Check(refusedUnapprovedStart,
            "interrupted Stop phase never permits an unrelated ASA Start");
        manualJournal.Change(crash.Id,AsaRestartPhase.Starting);
        manualJournal.DemandAuthorizedStart(crash.Id);
        bool refusedUnapprovedStop=false;
        try{manualJournal.DemandAuthorizedStop(null);}
        catch(InvalidOperationException){refusedUnapprovedStop=true;}
        Check(refusedUnapprovedStop,
            "Starting phase disallows unrelated local ASA Stop");
        manualJournal.Change(crash.Id,AsaRestartPhase.NeedsOperator);
        var interruptedRestart=await Call(restartBridge.HandleAsync,
            "servers.restartCurrent",new{mapId=stopLabMap.Id});
        IsOk(interruptedRestart,
            "interrupted Restart journal survives Manager/UI restart");
        Check(interruptedRestart.GetProperty("data")
                  .GetProperty("needsOperatorReview").GetBoolean() &&
              interruptedRestart.GetProperty("data").GetProperty("phase")
                  .GetString()=="NeedsOperator" &&
              !interruptedRestart.GetProperty("data")
                  .GetProperty("autoResumeAllowed").GetBoolean(),
            "durable journal refuses automatic restart replay");
        var blockedRestart=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        Check(IsError(blockedRestart,"UM-ASA-RESTART-FAILED"),
            "unresolved historical Restart prevents another approved job");

        // The OS still owns the ORIGINAL PID (44117). An interrupted
        // restart does not need a typed operator phrase or a new ASA/RCON
        // dispatch: independently verify the old process and audit the
        // exact Restart-v1 record once, leaving its prior result UNKNOWN.
        string reviewKey=UntamedOperationCoordinator.ResourceKey(stopRoot);
        string reviewFolder=Path.Combine(
            UntamedOperationCoordinator.DefaultRoot,"Restart-v1");
        string reviewFile=Path.Combine(reviewFolder,reviewKey+".json");
        var delayedRestart=manualJournal.Observe()! with {
            ChangedUtc=DateTimeOffset.UtcNow.AddMinutes(-6)
        };
        File.WriteAllText(reviewFile,JsonSerializer.Serialize(delayedRestart,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var recoveryPreview=await Call(restartBridge.HandleAsync,
            "servers.restartReviewPreview",new{mapId=stopLabMap.Id});
        IsOk(recoveryPreview,"stale local fresh Restart can verify exact old running ASA owner");
        var reviewData=recoveryPreview.GetProperty("data");
        Check(reviewData.GetProperty("oldPid").GetInt32()==44117&&
              reviewData.GetProperty("exactOldProcessVerified").GetBoolean()&&
              !reviewData.GetProperty("serverStarted").GetBoolean()&&
              restartCalls==1,
            "fresh restart recovery preview checks old PID and cannot trigger new ASA");
        string reviewToken=reviewData.GetProperty("token").GetString()!;
        var rejectedReview=await Call(restartBridge.HandleAsync,
            "servers.restartReviewApply",new{token=reviewToken,confirm=false});
        Check(IsError(rejectedReview,"UM-ASA-RESTART-REVIEW-APPROVAL")&&
              manualJournal.Observe()?.Phase==AsaRestartPhase.NeedsOperator,
            "a missing explicit repair click cannot mutate a pending Restart journal");
        var freshReviewPreview=await Call(restartBridge.HandleAsync,
            "servers.restartReviewPreview",new{mapId=stopLabMap.Id});
        IsOk(freshReviewPreview,"restart audit requires new one-shot preview after denial");
        string reviewedToken=freshReviewPreview.GetProperty("data")
            .GetProperty("token").GetString()!;
        var auditedRestart=await Call(restartBridge.HandleAsync,
            "servers.restartReviewApply",new{token=reviewedToken,confirm=true});
        IsOk(auditedRestart,"verified old running ASA allows auditable fresh Restart recovery");
        Check(manualJournal.Observe()?.Phase==AsaRestartPhase.OperatorReviewed&&
              !auditedRestart.GetProperty("data")
                .GetProperty("newRestartSucceeded").GetBoolean()&&
              !auditedRestart.GetProperty("data").GetProperty("rconSent").GetBoolean()&&
              !auditedRestart.GetProperty("data").GetProperty("serverStarted").GetBoolean()&&
              restartCalls==1,
            "audit never claims old Restart succeeded and sends no extra RCON/Start");
        string reviewedAudit=Path.Combine(reviewFolder,reviewKey+"-"+
            crash.Id.ToString("N")+".operator-reviewed.json");
        Check(File.Exists(reviewedAudit),
            "original interrupted Restart evidence is durably preserved");
        var reviewReplay=await Call(restartBridge.HandleAsync,
            "servers.restartReviewApply",new{token=reviewedToken,confirm=true});
        Check(IsError(reviewReplay,"UM-ASA-RESTART-REVIEW-APPROVAL-EXPIRED")&&
              restartCalls==1,
            "audited recovery token is one-shot and cannot retry old command");
        var freshAgain=await Call(restartBridge.HandleAsync,
            "servers.restartPreview",new{mapId=stopLabMap.Id});
        IsOk(freshAgain,"new independent Restart can be previewed after verified audit");
        // Restore a DIFFERENT isolated interruption for the legacy journal
        // read-only observation below; never delete or reuse the old audit.
        var secondCrash=manualJournal.Begin(stopLabMap.Id,44117);
        manualJournal.Change(secondCrash.Id,AsaRestartPhase.NeedsOperator);
        var foreignRestartJournal=await Call(restartBridge.HandleAsync,
            "servers.restartCurrent",new{mapId=Guid.NewGuid()});
        Check(IsError(foreignRestartJournal,"UM-MAP-NOT-OWNED"),
            "remote maps cannot inspect local Restart journal");

        // A Manager crash after real Start/Stop mutation leaves a durable
        // operator-review journal. Native/UI observation must not clear it.
        var journalBridge=new DesktopAsaLifecycleJournalBridge(lab);
        var beforeJournal=await Call(journalBridge.HandleAsync,
            "servers.lifecycleJournal",new{mapId=stopLabMap.Id});
        IsOk(beforeJournal,"native lifecycle journal can be observed read-only");
        Check(beforeJournal.GetProperty("data")
                  .GetProperty("needsOperatorReview").GetBoolean()&&
              beforeJournal.GetProperty("data")
                  .GetProperty("restartPhase").GetString()=="NeedsOperator"&&
              !beforeJournal.GetProperty("data")
                  .GetProperty("recoverySupported").GetBoolean()&&
              beforeJournal.GetProperty("data")
                  .GetProperty("blockingCode").GetString()==
                  "UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR",
            "completed graceful stop does not hide a distinct unresolved Restart-v1 journal on a non-Store map");
        using(var lease=lab.Operations.Acquire(stopRoot,UntamedOperationKind.Launch))
        {
            lease.MarkMutationStarted();
        }
        var interrupted=await Call(journalBridge.HandleAsync,
            "servers.lifecycleJournal",new{mapId=stopLabMap.Id});
        IsOk(interrupted,"interrupted native ASA lifecycle survives manager UI restart");
        Check(interrupted.GetProperty("data")
                  .GetProperty("needsOperatorReview").GetBoolean() &&
              interrupted.GetProperty("data").GetProperty("phase")
                  .GetString()=="NeedsOperator" &&
              !interrupted.GetProperty("data")
                  .GetProperty("autoResumeAllowed").GetBoolean(),
            "interrupted ASA operation is visible but never auto-resumed");
        var unknownJournal=await Call(journalBridge.HandleAsync,
            "servers.lifecycleJournal",new{mapId=Guid.NewGuid()});
        Check(IsError(unknownJournal,"UM-MAP-NOT-OWNED"),
            "foreign map cannot query native operation journal");
    }

    // Real durable coordinator + native recovery API, isolated in TEMP.
    // The original operation is kept for audit. Neither Test nor production
    // recovery can execute an ASA command, replay a dinowipe, or spawn a PID.
    {
        string path=Path.Combine(root,"store-running-launch-recovery");
        await using var originalLab=new ClusterService("store-recovery-ci",
            Path.Combine(path,"profile"),
            _=>new ProcessCheck(false,"synthetic-no-asa",null,true));
        var protectedMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Aberration",
            InstallationPath=Path.Combine(path,"Aberration"),
            HistoricalStoreProtected=true,HistoricalMapKey="Aberration_WP",
            HistoricalArchiveSha256=new string('B',64),
            RconPort=37045
        };
        originalLab.AddMap(protectedMap);
        using(var lease=originalLab.Operations.Acquire(
            protectedMap.InstallationPath,UntamedOperationKind.Launch))
            lease.MarkMutationStarted();
        var key=UntamedOperationCoordinator.ResourceKey(protectedMap.InstallationPath);
        var journalFile=Path.Combine(root,"operation-journals",key+".json");
        var initial=originalLab.Operations.Observe(protectedMap.InstallationPath)!;
        File.WriteAllText(journalFile,JsonSerializer.Serialize(initial with {
            UpdatedUtc=DateTimeOffset.UtcNow.AddMinutes(-5)
        }));
        string before=File.ReadAllText(journalFile);
        int proofCalls=0;
        var identity=new ProcessIdentity{
            Pid=23456,StartedUtc=DateTimeOffset.UtcNow.AddHours(-3),
            ExecutablePath=Path.Combine(protectedMap.InstallationPath,
                "ShooterGame","Binaries","Win64","ArkAscendedServer.exe"),
            Arguments="SYNTHETIC_OWNER_ONLY"
        };
        var restartJournal=new AsaManualRestartJournal(protectedMap.InstallationPath);
        var oldRestart=restartJournal.Begin(protectedMap.Id,23456);
        restartJournal.Change(oldRestart.Id,AsaRestartPhase.NeedsOperator);
        string restartFile=Path.Combine(root,"operation-journals","Restart-v1",key+".json");
        File.WriteAllText(restartFile,JsonSerializer.Serialize(
            restartJournal.Observe()! with{
                ChangedUtc=DateTimeOffset.UtcNow.AddMinutes(-5)
            }));
        string restartOriginalBytes=File.ReadAllText(restartFile);
        bool sameProcess=true,failAfterFirstRestartReview=false;
        var review=new DesktopAsaLifecycleJournalBridge(originalLab,id=>{
            proofCalls++;
            if(!sameProcess||failAfterFirstRestartReview&&
                restartJournal.Observe()?.Phase==AsaRestartPhase.OperatorReviewed)
                throw new InvalidDataException(
                    "UM-RECOVERY-RUNNING-SOURCE-UNVERIFIED");
            return new LegacyStoreV1GracefulStop.Owned(protectedMap,
                identity,new string('A',64));
        });
        var current=await Call(review.HandleAsync,
            "servers.lifecycleJournal",new{mapId=protectedMap.Id});
        IsOk(current,"interrupted Store-v1 Launch is classified as unknown outcome");
        Check(current.GetProperty("data").GetProperty("recoverySupported").GetBoolean()&&
            current.GetProperty("data").GetProperty("classification").GetString()=="unknownOutcome"&&
            current.GetProperty("data").GetProperty("blockingCode").GetString()==
                "UM-RECOVERY-PAIRED-LAUNCH-RESTART-REVIEW-AVAILABLE"&&
            current.GetProperty("data").GetProperty("restartOldPid").GetInt32()==23456&&
            current.GetProperty("data").GetProperty("restartPhase").GetString()=="NeedsOperator",
            "recovery status exposes the exact blocker without deleting its durable journal");
        var staged=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryPreview",new{mapId=protectedMap.Id});
        IsOk(staged,"original Store-v1 running owner recovery preview is native and read-only");
        Check(staged.GetProperty("data").GetProperty("originalStoreSessionVerified").GetBoolean()&&
              !staged.GetProperty("data").GetProperty("approvedObsidianSourcePinned").GetBoolean()&&
              staged.GetProperty("data").GetProperty("legacyPeerCheckRequiredForLocalReview").GetBoolean(),
            "synthetic unpinned original recovery must not pretend it has an approved independent local source");
        var reviewToken=staged.GetProperty("data").GetProperty("token").GetString();
        var missingApproval=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryApply",new{
                mapId=protectedMap.Id,token=reviewToken,confirm=true,
                ackUnknownOutcome=true,typedApproval="INCORRECT"
            });
        Check(IsError(missingApproval,"UM-RECOVERY-EXPLICIT-APPROVAL-REQUIRED")&&
            File.ReadAllText(journalFile)==before&&
            File.ReadAllText(restartFile)==restartOriginalBytes,
            "bad approval cannot change either durable journal or repeat a command");
        staged=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryPreview",new{mapId=protectedMap.Id});
        reviewToken=staged.GetProperty("data").GetProperty("token").GetString();
        sameProcess=false;
        var changed=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryApply",new{
                mapId=protectedMap.Id,token=reviewToken,confirm=true,
                ackUnknownOutcome=true,
                typedApproval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS"
            });
        Check(IsError(changed,"UM-RECOVERY-RUNNING-SOURCE-UNVERIFIED")&&
            File.ReadAllText(journalFile)==before&&
            File.ReadAllText(restartFile)==restartOriginalBytes,
            "a changed ASA PID/source cannot change either journal");
        sameProcess=true;
        failAfterFirstRestartReview=true;
        staged=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryPreview",new{mapId=protectedMap.Id});
        reviewToken=staged.GetProperty("data").GetProperty("token").GetString();
        var interruptedPair=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryApply",new{
                mapId=protectedMap.Id,token=reviewToken,confirm=true,
                ackUnknownOutcome=true,
                typedApproval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS"
            });
        Check(!interruptedPair.GetProperty("ok").GetBoolean()&&
            restartJournal.Observe()?.Phase==AsaRestartPhase.OperatorReviewed&&
            originalLab.Operations.Observe(protectedMap.InstallationPath)?.Phase==
                UntamedOperationPhase.NeedsOperator,
            "crash after first audited journal leaves second journal blocking every write");
        var restartAudit=Path.Combine(root,"operation-journals","Restart-v1",
            key+"-"+oldRestart.Id.ToString("N")+".operator-reviewed.json");
        Check(File.Exists(restartAudit)&&
            File.ReadAllText(restartAudit)==restartOriginalBytes,
            "original Restart-v1 NeedsOperator bytes are safely archived before transition");
        bool stillBlocked=false;
        try{using var denied=originalLab.Operations.Acquire(
            protectedMap.InstallationPath,UntamedOperationKind.Configuration);}
        catch(IOException ex) when(ex.Message=="UNTAMED_OPERATION_NEEDS_OPERATOR")
        {stillBlocked=true;}
        Check(stillBlocked,"partial paired recovery NEVER opens a second ASA mutation lease");
        failAfterFirstRestartReview=false;
        staged=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryPreview",new{mapId=protectedMap.Id});
        IsOk(staged,"fresh operator preview resumes partially audited paired journals");
        reviewToken=staged.GetProperty("data").GetProperty("token").GetString();
        var reviewed=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryApply",new{
                mapId=protectedMap.Id,token=reviewToken,confirm=true,
                ackUnknownOutcome=true,
                typedApproval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS"
            });
        IsOk(reviewed,"paired Store-v1 Launch/Restart recovery completes without restart or command replay");
        var receipt=reviewed.GetProperty("data");
        var audit=Path.Combine(root,"operation-journals",
            key+"-"+initial.Operation.ToString("N")+".operator-reviewed.json");
        Check(proofCalls>=5&&receipt.GetProperty("previousOutcomeStillUnknown").GetBoolean()&&
            !receipt.GetProperty("rconCommandSent").GetBoolean()&&
            !receipt.GetProperty("processStarted").GetBoolean()&&
            File.Exists(audit)&&File.ReadAllText(audit)==before&&
            File.Exists(restartAudit)&&File.ReadAllText(restartAudit)==restartOriginalBytes&&
            restartJournal.Observe()?.Phase==AsaRestartPhase.OperatorReviewed&&
            receipt.GetProperty("restartJournalAudited").GetBoolean()&&
            !receipt.GetProperty("restartMarkedSuccessful").GetBoolean()&&
            receipt.GetProperty("pairedJournalsReviewed").GetInt32()==2&&
            originalLab.Operations.Observe(protectedMap.InstallationPath)?.Phase==
                UntamedOperationPhase.OperatorReviewed,
            "both real-schema journals are archived byte-for-byte; no fake restart or double ASA/RCON command");
        // An audited Restart must unblock independently approved NEW
        // operations; it must never authorize replay of an old restart token.
        restartJournal.DemandNoPending();
        restartJournal.DemandAuthorizedStop(null);
        restartJournal.DemandAuthorizedStart(null);
        bool oldRestartTokenDenied=false;
        try{restartJournal.DemandAuthorizedStart(oldRestart.Id);}
        catch(InvalidOperationException ex) when(
            ex.Message=="UM-ASA-RESTART-JOURNAL-START-UNAUTHORIZED")
        {oldRestartTokenDenied=true;}
        Check(oldRestartTokenDenied,
            "reviewed Restart allows fresh Start/Stop but rejects old interrupted Restart token");
        var repeated=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryApply",new{
                mapId=protectedMap.Id,token=reviewToken,confirm=true,
                ackUnknownOutcome=true,
                typedApproval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS"
            });
        Check(IsError(repeated,"UM-RECOVERY-APPROVAL-EXPIRED"),
            "single-use recovery token cannot be applied twice");
        using(var next=originalLab.Operations.Acquire(
            protectedMap.InstallationPath,UntamedOperationKind.Configuration))
            next.Complete();
        Check(originalLab.Operations.Observe(protectedMap.InstallationPath)?.Phase==
            UntamedOperationPhase.Completed,
            "reviewed operation unlocks only fresh, separately authorized actions");

        // A historic restart may be the ONLY unresolved journal, when a
        // different operation already ended. The UI must still offer an
        // original-running-PID audit instead of an infinite dead end.
        var restartOnly=restartJournal.Begin(protectedMap.Id,23456);
        restartJournal.Change(restartOnly.Id,AsaRestartPhase.NeedsOperator);
        File.WriteAllText(restartFile,JsonSerializer.Serialize(
            restartJournal.Observe()! with{
                ChangedUtc=DateTimeOffset.UtcNow.AddMinutes(-5)
            }));
        string originalRestartOnly=File.ReadAllText(restartFile);
        var standaloneStatus=await Call(review.HandleAsync,
            "servers.lifecycleJournal",new{mapId=protectedMap.Id});
        IsOk(standaloneStatus,"standalone old Restart-v1 has readable native diagnostic");
        Check(standaloneStatus.GetProperty("data")
                .GetProperty("recoverySupported").GetBoolean()&&
              standaloneStatus.GetProperty("data")
                .GetProperty("blockingCode").GetString()==
                "UM-RECOVERY-RESTART-ONLY-REVIEW-AVAILABLE",
            "a closed general operation does not strand an unrelated old restart");
        var standalonePreview=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryPreview",new{mapId=protectedMap.Id});
        IsOk(standalonePreview,
            "old running ASA and Store proof authorize single-restart review");
        var standaloneToken=standalonePreview.GetProperty("data")
            .GetProperty("token").GetString();
        var standaloneApplied=await Call(review.HandleAsync,
            "servers.lifecycleRecoveryApply",new{
                mapId=protectedMap.Id,token=standaloneToken,confirm=true,
                ackUnknownOutcome=true,
                typedApproval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS"
            });
        IsOk(standaloneApplied,
            "restart-only NeedsOperator can be audited without replay");
        var standaloneReceipt=standaloneApplied.GetProperty("data");
        string standaloneAudit=Path.Combine(root,"operation-journals","Restart-v1",
            key+"-"+restartOnly.Id.ToString("N")+".operator-reviewed.json");
        Check(standaloneReceipt.GetProperty("restartJournalAudited").GetBoolean()&&
            !standaloneReceipt.GetProperty("originalJournalAudited").GetBoolean()&&
            standaloneReceipt.GetProperty("pairedJournalsReviewed").GetInt32()==1&&
            !standaloneReceipt.GetProperty("restartMarkedSuccessful").GetBoolean()&&
            restartJournal.Observe()?.Phase==AsaRestartPhase.OperatorReviewed&&
            File.Exists(standaloneAudit)&&
            File.ReadAllText(standaloneAudit)==originalRestartOnly&&
            originalLab.Operations.Observe(protectedMap.InstallationPath)?.Phase==
                UntamedOperationPhase.Completed,
            "standalone restart audit preserves original bytes and never falsifies a successful restart");
    }

    // Reproduce the ACTUAL native circular preflight, not only the mocked
    // recovery bridge. All binary/INI/process/port data are synthetic TEMP.
    {
        string baseDir=Path.Combine(root,"p0-original-review-circular-guard");
        string exe=Path.Combine(baseDir,"ShooterGame","Binaries",
            "Win64","ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe,new byte[]{0x4D,0x5A});
        const string password="SYNTHETIC-STORE-REVIEW-ONLY-37045";
        AsaInitialRconSettings.CreateForNewServer(baseDir,37045,password);
        var original=new LocalMap{
            Id=Guid.NewGuid(),Name="Aberration",InstallationPath=baseDir,
            ExecutablePath=exe,HistoricalStoreProtected=true,
            HistoricalMapKey="Aberration_WP",RconPort=37045,
            RconPasswordProtected=SecureStore.Protect(password)
        };
        await using var lab=new ClusterService("old-asaserver-proof",
            Path.Combine(root,"p0-circular-guard-profile"),
            _=>new ProcessCheck(false,"synthetic",null,true));
        lab.AddMap(original);
        var pid=new ProcessIdentity{
            Pid=16796,StartedUtc=DateTimeOffset.UtcNow.AddHours(-4),
            ExecutablePath=exe,Arguments="Aberration_WP?listen -usestore"
        };
        var ongoing=new ProcessCheck(true,"synthetic running original",pid);
        var restart=new AsaManualRestartJournal(baseDir);
        var old=restart.Begin(original.Id,pid.Pid);
        restart.Change(old.Id,AsaRestartPhase.NeedsOperator);
        Check(AsaRconConfigurationEvidence.Matches(original),
            "original recovery verifies DPAPI RCON secret against synthetic INI");
        bool normalDenied=false;
        try{_ =LegacyStoreV1GracefulStop.Preview(lab,original.Id,
            _=>ongoing,_=>new string('A',64),
            (port,owner)=>port==37045&&owner==16796);}
        catch(InvalidOperationException ex) when(
            ex.Message=="UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR")
        {normalDenied=true;}
        Check(normalDenied,
            "ordinary Stop/Restart cannot bypass a NeedsOperator restart journal");
        var proven=LegacyStoreV1GracefulStop.PreviewForUnfinishedRestartReview(
            lab,original.Id,_=>ongoing,_=>new string('A',64),
            (port,owner)=>port==37045&&owner==16796);
        Check(proven.Map.Id==original.Id&&proven.Process.Pid==16796&&
            proven.SourceSha==new string('A',64)&&
            restart.Observe()?.Phase==AsaRestartPhase.NeedsOperator,
            "explicit review preview breaks circular guard without changing either original journal");
        bool wrongPidDenied=false;
        try{_ =LegacyStoreV1GracefulStop.PreviewForUnfinishedRestartReview(
            lab,original.Id,
            _=>new ProcessCheck(true,"changed PID",new ProcessIdentity{
                Pid=16797,StartedUtc=pid.StartedUtc,
                ExecutablePath=pid.ExecutablePath,Arguments=pid.Arguments
            }),
            _=>new string('A',64),(port,owner)=>true);}
        catch(InvalidOperationException ex) when(
            ex.Message=="UM-RECOVERY-RESTART-OLD-PID-OR-PHASE-MISMATCH")
        {wrongPidDenied=true;}
        Check(wrongPidDenied,
            "read-only recovery still refuses an unexpected ASA PID even with a matching map and RCON listener");
    }

    // Automatic watcher: all ASA and API Loader processes are detected even
    // without registered map profiles, never silently treated as controllable.
    {
        var childExe=Path.Combine(root,"discovery-ci","ArkAscendedServer.exe");
        var parentExe=Path.Combine(root,"discovery-ci","AsaApiLoader.exe");
        var startedAt=DateTimeOffset.UtcNow.AddHours(-2);
        bool unavailable=false;
        int polls=0;
        using var watcher=new DesktopAutomaticAsaDiscoverySupervisor(()=>{
            polls++;
            if(unavailable)throw new IOException("SYNTHETIC_WMI_FAILED");
            return new LocalRunningAsaDiscovery.Sample[]{
                new(32001,32003,startedAt.AddSeconds(2),
                    "ArkAscendedServer.exe",childExe,
                    "\""+childExe+"\" Aberration_WP?listen"),
                new(32003,0,startedAt,"AsaApiLoader.exe",parentExe,
                    "\""+parentExe+"\" Aberration_WP?listen"),
                new(32005,0,startedAt,"ArkAscendedServer.exe",null,null)
            };
        },()=>Array.Empty<LocalMap>());
        var found=watcher.RefreshOnce();
        Check(found.Fresh&&found.Processes.Length==3&&polls==1&&
            found.Processes.Count(x=>x.ProcessKind=="asaServer")==2&&
            found.Processes.Count(x=>x.ProcessKind=="apiLoader")==1&&
            found.Processes.All(x=>!x.Controllable&&!x.StoreWriteAuthorized)&&
            found.Processes.Any(x=>x.Pid==32005&&x.MatchState=="unverified"),
            "automatic ASA watcher displays all known and PID-only processes without adoption or writes");
        unavailable=true;
        var failure=watcher.RefreshOnce();
        Check(!failure.Fresh&&failure.Status=="wmi-unavailable"&&
            failure.Processes.Length==0&&!watcher.Snapshot().Fresh,
            "unavailable WMI cannot produce a fake stopped/zero-ASA success state");
    }

    // Store-v1 five running maps: atomic native reconciliation retains
    // existing GUID and configuration. This is synthetic source+PID evidence;
    // production must ALSO pass exact physical Windows and ACTIVE/DONE checks.
    {
        string dir=Path.Combine(root,"original-live-five-reconcile");
        await using var lab=new ClusterService("five-map-reconcile",
            Path.Combine(dir,"profile"),
            _=>new ProcessCheck(false,"synthetic",null,true));
        var candidates=new List<LocalMap>();
        string[] names={"The Island","The Center","Aberration","Ragnarok","Scorched Earth"};
        string[] keys={"TheIsland_WP","TheCenter_WP","Aberration_WP",
            "Ragnarok_WP","ScorchedEarth_WP"};
        string sourceHash=new('B',64);
        for(int index=0;index<5;index++)
        {
            string mapRoot=Path.Combine(dir,"map-"+index);
            string binary=Path.Combine(mapRoot,"ShooterGame","Binaries",
                "Win64","ArkAscendedServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllBytes(binary,new byte[]{0x4D,0x5A});
            bool api=index==2;
            string apiBinary=Path.Combine(Path.GetDirectoryName(binary)!,
                "AsaApiLoader.exe");
            if(api)File.WriteAllBytes(apiBinary,new byte[]{0x4D,0x5A});
            var started=DateTimeOffset.UtcNow.AddHours(-2);
            var observed=new LocalMap{
                Id=Guid.NewGuid(),Name=names[index],InstallationPath=mapRoot,
                ExecutablePath=binary,GamePort=7777+index*20,
                QueryPort=27015+index*20,HistoricalStoreProtected=true,
                HistoricalArchiveSha256=sourceHash,HistoricalMapKey=keys[index],
                HistoricalEnabledMods=new(){"101","202"},
                HistoricalPassiveMods=new(){"303"},StartWithAsaApi=api,
                ApiLoaderPath=api?apiBinary:"",
                LastProcess=new ProcessIdentity{
                    Pid=42000+index,StartedUtc=started.AddSeconds(1),
                    ExecutablePath=binary,
                    Arguments=api?"HISTORICAL_STORE_V1_API_CHILD_VERIFIED":
                        "HISTORICAL_STORE_V1_READ_ONLY_OBSERVATION"
                },
                LastLoader=api?new ProcessIdentity{
                    Pid=41000+index,StartedUtc=started,
                    ExecutablePath=apiBinary,
                    Arguments="HISTORICAL_STORE_V1_API_LOADER_VERIFIED"
                }:null
            };
            candidates.Add(observed);
        }
        // Partial previous import must not be overwritten or rekeyed.
        var prior=JsonSerializer.Deserialize<LocalMap>(
            JsonSerializer.Serialize(candidates[2]))!;
        prior.StartWithAsaApi=false;
        prior.ApiLoaderPath="";
        prior.LastProcess=null;
        prior.LastLoader=null;
        prior.HistoricalLaunchModeNeedsApproval=true;
        prior.LogFilePath="existing-user-log-path";
        string retainedRconPassword="SYNTHETIC-STORE-RCON-ONLY-9572";
        prior.RconPort=37095;
        prior.RconPasswordProtected=SecureStore.Protect(retainedRconPassword);
        var ini=AsaInitialRconSettings.NewIniPath(prior.InstallationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini,
            "[ServerSettings]"+Environment.NewLine+
            "RCONEnabled=True"+Environment.NewLine+
            "RCONPort=37095"+Environment.NewLine+
            "ServerAdminPassword="+retainedRconPassword+Environment.NewLine);
        Check(AsaRconConfigurationEvidence.Matches(prior),
            "synthetic original Store RCON configuration exactly matches protected DPAPI secret");
        lab.AddMap(prior);
        int physicalChecks=0;
        var admitted=lab.ReconcileOriginalStoreLiveFive(candidates,()=>{
            physicalChecks++;return true;
        });
        var reattached=lab.Config.LocalMaps.Single(m=>m.Id==prior.Id);
        Check(admitted.Added==4&&admitted.Reattached==1&&physicalChecks==1&&
            lab.Config.LocalMaps.Count==5&&
            lab.Config.LocalMaps.Select(m=>m.Id).ToHashSet().SetEquals(
                candidates.Select(m=>m.Id))&&
            reattached.LogFilePath=="existing-user-log-path"&&
            reattached.HistoricalMapKey==prior.HistoricalMapKey&&
            reattached.RconPort==prior.RconPort&&
            reattached.RconPasswordProtected==prior.RconPasswordProtected&&
            AsaRconConfigurationEvidence.Matches(reattached)&&
            reattached.StartWithAsaApi&&reattached.LastLoader?.Pid==41002&&
            reattached.LastProcess?.Pid==42002&&
            !reattached.HistoricalLaunchModeNeedsApproval,
            "five-map live reattach preserves GUID and existing profile settings, updates only WMI-proven PID/mode");
        var idempotent=lab.ReconcileOriginalStoreLiveFive(candidates,()=>true);
        Check(idempotent.Unchanged==5&&idempotent.Added==0&&
            lab.Config.LocalMaps.Count==5,
            "repeat five-map reconciliation never creates a second map or ASA process");
        using(var lease=lab.Operations.Acquire(
            candidates[2].InstallationPath,UntamedOperationKind.Launch))
            lease.MarkMutationStarted();
        string profileBefore=JsonSerializer.Serialize(lab.Config.LocalMaps);
        bool correctlyBlocked=false;
        try{_ =lab.ReconcileOriginalStoreLiveFive(candidates,()=>true);}
        catch(IOException ex) when(ex.Message=="UNTAMED_OPERATION_NEEDS_OPERATOR")
        {correctlyBlocked=true;}
        Check(correctlyBlocked&&profileBefore==JsonSerializer.Serialize(
            lab.Config.LocalMaps),
            "unknown old Launch prevents new five-map mutation and preserves every existing profile");
    }

    // Native cross-map Mod Center inventory: real Core parser, read-only
    // historical Store-v1 metadata, never the quarantined Arguments field.
    {
        var mapA=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",
            Arguments="-mods=928548,940975 -passivemods=940975"
        };
        var mapB=new LocalMap{
            Id=Guid.NewGuid(),Name="Ragnarok",
            Arguments="-mods=928548 -port=7817"
        };
        var historical=new LocalMap{
            Id=Guid.NewGuid(),Name="Aberration",
            HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('A',64),
            HistoricalMapKey="Aberration_WP",
            HistoricalEnabledMods=new(){"928548"},
            HistoricalPassiveMods=new(){"940975"},
            Arguments=""
        };
        var before=new[]{mapA.Arguments,mapB.Arguments,historical.Arguments};
        var usage=DesktopSharedModInventory.Read(new[]{mapA,mapB,historical});
        var shared=usage.Projects.Single(p=>p.ProjectId=="928548");
        Check(usage.MapCount==3 && usage.SharedProjectCount==2 &&
              usage.UnreadableMaps.Length==0 && shared.EnabledMapCount==3 &&
              shared.Assignments.Any(a=>a.HistoricalProtected &&
                  a.MapId==historical.Id.ToString("D")),
            "shared mod inventory merges Core live configuration and historical read-only archive mod assignments");
        Check(usage.Projects.Single(p=>p.ProjectId=="940975").EnabledMapCount==2 &&
              usage.Projects.Single(p=>p.ProjectId=="940975").Assignments.Any(a=>
                  a.Mode=="passive" && a.HistoricalProtected),
            "passive mods remain distinct and historical archive assignments remain marked");
        Check(mapA.Arguments==before[0] && mapB.Arguments==before[1] &&
              historical.Arguments==before[2] && historical.HistoricalStoreProtected,
            "inventory never changes real launch arguments or unblocks historical Store");
        var bad=new LocalMap{
            Id=Guid.NewGuid(),Name="Invalid history",
            HistoricalStoreProtected=true,
            HistoricalArchiveSha256="HASH",
            HistoricalMapKey="TheIsland_WP",
            HistoricalEnabledMods=new(){"NOT_AN_ID"}
        };
        var partial=DesktopSharedModInventory.Read(new[]{mapA,bad});
        Check(partial.UnreadableMaps.Contains("Invalid history") &&
              partial.Projects.Any(p=>p.ProjectId=="928548"),
            "broken historical metadata is reported without erasing valid local mod assignments");
    }

    // Copy To / Merge / Replace is a REAL atomic local Manager-config write,
    // not a Store-v1 migration, network command or active ASA mod installation.
    {
        string modProfile=Path.Combine(root,"mod-copy-profile");
        await using var modLab=new ClusterService("mod-copy-lab",modProfile,
            _=>new ProcessCheck(false,"synthetic stopped",null,true));
        var source=new LocalMap{Id=Guid.NewGuid(),Name="The Island",
            InstallationPath=Path.Combine(root,"mod-copy-island"),
            Arguments="TheIsland_WP?listen -Port=7777 -mods=928548,940975 -passivemods=940975"};
        var target=new LocalMap{Id=Guid.NewGuid(),Name="Ragnarok",
            InstallationPath=Path.Combine(root,"mod-copy-ragnarok"),
            Arguments="Ragnarok_WP?listen -Port=7827 -mods=928548,666666"};
        var other=new LocalMap{Id=Guid.NewGuid(),Name="Valguero",
            InstallationPath=Path.Combine(root,"mod-copy-valguero"),
            Arguments="Valguero_WP?listen -Port=7837 -mods=555555 -passivemods=555555"};
        var historic=new LocalMap{Id=Guid.NewGuid(),Name="Aberration",
            InstallationPath=Path.Combine(root,"mod-copy-protected"),
            HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('A',64),
            HistoricalMapKey="Aberration_WP"};
        foreach(var item in new[]{source,target,other,historic})
            modLab.AddMap(item);
        using var copy=new DesktopModCopyBridge(modLab);
        var refused=await Call(copy.HandleAsync,"mods.copyPreview",new{
            sourceMapId=source.Id,targets=new[]{historic.Id},mode="replace"});
        Check(IsError(refused,"UM-MOD-COPY-SAFETY-CHECK-FAILED"),
            "Copy To refuses historic Store-v1 maps without touching metadata");
        string beforeTarget=target.Arguments;
        var merge=await Call(copy.HandleAsync,"mods.copyPreview",new{
            sourceMapId=source.Id,targets=new[]{target.Id,other.Id},mode="merge"});
        IsOk(merge,"native multi-map Merge preview returns two local targets");
        Check(merge.GetProperty("data").GetProperty("changed").GetBoolean() &&
              merge.GetProperty("data").GetProperty("targets").GetArrayLength()==2 &&
              target.Arguments==beforeTarget,
            "Merge preview does not write configuration");
        string firstToken=merge.GetProperty("data").GetProperty("token").GetString()!;
        var applied=await Call(copy.HandleAsync,"mods.copyApply",
            new{token=firstToken,confirm=true});
        IsOk(applied,"one approval commits both local map mod lists atomically");
        var targetMods=ModStateController.Read(target);
        var otherMods=ModStateController.Read(other);
        Check(targetMods.Active.SequenceEqual(new[]{"928548","666666"}) &&
              targetMods.Passive.Contains("940975") &&
              otherMods.Active.Contains("928548") &&
              otherMods.Passive.Contains("940975") &&
              otherMods.Passive.Contains("555555") &&
              target.Arguments.Contains("-Port=7827",StringComparison.Ordinal) &&
              historic.Arguments.Length==0 &&
              !applied.GetProperty("data").GetProperty("modsLoadedVerified").GetBoolean(),
            "Merge preserves target-specific mods/ports, retains ASA passive subset and never claims runtime load");
        var replay=await Call(copy.HandleAsync,"mods.copyApply",
            new{token=firstToken,confirm=true});
        Check(IsError(replay,"UM-MOD-COPY-APPROVAL-REQUIRED"),
            "Copy To approval cannot be replayed");
        var replace=await Call(copy.HandleAsync,"mods.copyPreview",new{
            sourceMapId=source.Id,targets=new[]{other.Id},mode="replace"});
        IsOk(replace,"native Replace previews full mod list replacement");
        var secondToken=replace.GetProperty("data").GetProperty("token").GetString()!;
        IsOk(await Call(copy.HandleAsync,"mods.copyApply",
            new{token=secondToken,confirm=true}),
            "confirmed Replace commits only selected local target");
        otherMods=ModStateController.Read(other);
        Check(otherMods.EnabledOrder.SequenceEqual(new[]{"928548","940975"}) &&
              otherMods.Passive.SequenceEqual(new[]{"940975"}) &&
              !otherMods.Disabled.Any() &&
              !other.Arguments.Contains("555555",StringComparison.Ordinal) &&
              other.Arguments.Contains("-Port=7837",StringComparison.Ordinal),
            "Replace preserves non-mod ASA launch flags and source mod order");
        var stale=await Call(copy.HandleAsync,"mods.copyPreview",new{
            sourceMapId=source.Id,targets=new[]{target.Id},mode="replace"});
        IsOk(stale,"stale-copy fixture preview prepared");
        source.Arguments+=" -log";
        var noStaleApply=await Call(copy.HandleAsync,"mods.copyApply",
            new{token=stale.GetProperty("data").GetProperty("token").GetString(),confirm=true});
        Check(IsError(noStaleApply,"UM-MOD-COPY-SAFETY-CHECK-FAILED"),
            "changed source invalidates previously approved multi-map replacement");
        Check(historic.HistoricalStoreProtected &&
              !applied.GetProperty("data").GetProperty("remoteWritesAllowed").GetBoolean(),
            "original Store protection and remote read-only ownership survive the batch");
    }

    // Export mod-only JSON on HP, transfer file manually to Ryzen, and import
    // only to its explicitly selected local map after SHA and CAS checks.
    {
        string profileRoot=Path.Combine(root,"mod-profile-lab");
        string sourceRoot=Path.Combine(root,"mod-profile-source");
        string targetRoot=Path.Combine(root,"mod-profile-target");
        Directory.CreateDirectory(sourceRoot);Directory.CreateDirectory(targetRoot);
        var hpMap=new LocalMap{Id=Guid.NewGuid(),Name="Astraeos",
            InstallationPath=sourceRoot,Arguments=
                "Astraeos_WP?listen -Port=7777 -mods=928548,940975 -passivemods=940975"};
        var ryzenMap=new LocalMap{Id=Guid.NewGuid(),Name="Ragnarok",
            InstallationPath=targetRoot,Arguments=
                "Ragnarok_WP?listen -Port=7827 -mods=888888"};
        await using var hp=new ClusterService("profile-HP",Path.Combine(profileRoot,"hp"),
            _=>new ProcessCheck(false,"synthetic stopped",null,true));
        await using var ryzen=new ClusterService("profile-Ryzen",Path.Combine(profileRoot,"ryzen"),
            _=>new ProcessCheck(false,"synthetic stopped",null,true));
        hp.AddMap(hpMap);ryzen.AddMap(ryzenMap);
        string exportPath=Path.Combine(root,"portable-mod-profile-test.json");
        using(var exporter=new DesktopModProfileBridge(hp,_=>exportPath))
        {
            var exported=await Call(exporter.HandleAsync,"mods.profileExport",new{mapId=hpMap.Id});
            IsOk(exported,"HP exports mod-only portable profile to explicit local JSON");
            Check(File.Exists(exportPath) &&
                  !File.ReadAllText(exportPath).Contains("Port=7777",StringComparison.Ordinal) &&
                  !File.ReadAllText(exportPath).Contains(sourceRoot,StringComparison.Ordinal) &&
                  !File.ReadAllText(exportPath).Contains("password",StringComparison.OrdinalIgnoreCase),
                "portable mod profile contains no ASA arguments, paths or passwords");
            var denied=await Call(exporter.HandleAsync,"mods.profileExport",new{mapId=ryzenMap.Id});
            Check(IsError(denied,"UM-MODPROFILE-SAFETY-CHECK-FAILED"),
                "HP cannot export a peer-only map that it does not own");
        }
        using(var importer=new DesktopModProfileBridge(ryzen,null,()=>exportPath))
        {
            string before=ryzenMap.Arguments;
            var prepared=await Call(importer.HandleAsync,"mods.profileImportPreview",
                new{targets=new[]{ryzenMap.Id},mode="merge"});
            IsOk(prepared,"Ryzen previews checksum-verified HP mod JSON");
            Check(ryzenMap.Arguments==before &&
                  prepared.GetProperty("data").GetProperty("changed").GetBoolean(),
                "cross-host import preview never writes local map");
            string token=prepared.GetProperty("data").GetProperty("token").GetString()!;
            var noConsent=await Call(importer.HandleAsync,"mods.profileImportApply",
                new{token,confirm=false});
            Check(IsError(noConsent,"UM-MODPROFILE-APPROVAL-REQUIRED"),
                "portable mod import requires explicit local approval");
            IsOk(await Call(importer.HandleAsync,"mods.profileImportApply",
                new{token,confirm=true}),
                "Ryzen commits approved mod-only profile via local atomic CAS");
            var applied=ModStateController.Read(ryzenMap);
            Check(applied.Active.Contains("888888")&&
                  applied.Active.Contains("928548")&&
                  applied.Passive.Contains("940975")&&
                  ryzenMap.Arguments.Contains("-Port=7827",StringComparison.Ordinal),
                "Merge preserves Ryzen-specific project, passive status and game port");
            var replay=await Call(importer.HandleAsync,"mods.profileImportApply",
                new{token,confirm=true});
            Check(IsError(replay,"UM-MODPROFILE-APPROVAL-REQUIRED"),
                "portable profile approval is one-shot");
            var replace=await Call(importer.HandleAsync,"mods.profileImportPreview",
                new{targets=new[]{ryzenMap.Id},mode="replace"});
            IsOk(replace,"Ryzen previews Replace from portable profile");
            string replaceToken=replace.GetProperty("data").GetProperty("token").GetString()!;
            IsOk(await Call(importer.HandleAsync,"mods.profileImportApply",
                new{token=replaceToken,confirm=true}),
                "Ryzen applies Replace locally after explicit preview");
            applied=ModStateController.Read(ryzenMap);
            Check(applied.EnabledOrder.SequenceEqual(new[]{"928548","940975"}) &&
                  !applied.EnabledOrder.Contains("888888") &&
                  applied.Passive.SequenceEqual(new[]{"940975"}),
                "portable Replace reproduces source load order, excludes previous project");
        }
        string valid=File.ReadAllText(exportPath);
        File.WriteAllText(exportPath,valid.Replace("940975","999999",StringComparison.Ordinal));
        using(var importer=new DesktopModProfileBridge(ryzen,null,()=>exportPath))
        {
            var corrupted=await Call(importer.HandleAsync,"mods.profileImportPreview",
                new{targets=new[]{ryzenMap.Id},mode="replace"});
            Check(IsError(corrupted,"UM-MODPROFILE-SAFETY-CHECK-FAILED"),
                "mod profile checksum blocks silently edited export from another machine");
        }
    }

    // SQL-free, COPY-ONLY cluster archive: ten verified map packages and the
    // original shared transfer directory copied to an isolated test machine.
    // It cannot authorize a consistent live Store restore or new Store writer.
    {
        string archiveLab=Path.Combine(root,"asa-offline-archive-lab");
        string hp=Path.Combine(archiveLab,"HP-copies");
        string ryzen=Path.Combine(archiveLab,"RYZEN-copies");
        string transfer=Path.Combine(archiveLab,"transfer-copy");
        Directory.CreateDirectory(hp);Directory.CreateDirectory(ryzen);
        Directory.CreateDirectory(Path.Combine(transfer,".kova-store-v1"));
        File.WriteAllText(Path.Combine(transfer,".kova-store-v1","operation.json"),
            "{\"schema\":1,\"session\":\"synthetic-only\"}");
        File.WriteAllBytes(Path.Combine(transfer,"old-zero.arkprofile"),Array.Empty<byte>());
        Guid[] ids=new Guid[10];
        for(int i=0;i<10;i++)
        {
            ids[i]=Guid.NewGuid();
            string local=Path.Combine(archiveLab,"synthetic-save-"+i);
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local,"World"+i+".ark"),
                "SYNTHETIC-TEST-WORLD-"+i);
            string container=i<5?hp:ryzen;
            TestBackupPackage.Create(local,Path.Combine(container,"map"+i),
                ids[i],"Stopped offline synthetic ASA map "+i);
        }
        // Test malformed files selected from an OFFLINE backup package. No
        // original map, Store coordination file or live process is changed.
        string manifestLab=Path.Combine(archiveLab,"untrusted-manifest-lab");
        TestBackupPackage.Create(Path.Combine(archiveLab,"synthetic-save-0"),
            manifestLab,ids[0],"synthetic manifest corruption test");
        string manifestFile=Path.Combine(manifestLab,"manifest.json");
        File.WriteAllText(manifestFile,JsonSerializer.Serialize(new {
            Version=1,MapId=ids[0],Scope="synthetic",CreatedUtc=DateTimeOffset.UtcNow,
            Files=(object?)null
        }));
        bool nullFilesRejected=false;
        try{TestBackupPackage.Verify(manifestLab,ids[0]);}
        catch(InvalidDataException){nullFilesRejected=true;}
        Check(nullFilesRejected,
            "operator-selected offline manifest with null file array fails closed");

        File.WriteAllText(manifestFile,JsonSerializer.Serialize(new {
            Version=1,MapId=ids[0],Scope="synthetic",CreatedUtc=DateTimeOffset.UtcNow,
            Files=new object?[]{null}
        }));
        bool nullEntryRejected=false;
        try{TestBackupPackage.Verify(manifestLab,ids[0]);}
        catch(InvalidDataException){nullEntryRejected=true;}
        Check(nullEntryRejected,
            "operator-selected offline manifest with null file entry fails closed");

        using(var oversized=new FileStream(manifestFile,FileMode.Open,
            FileAccess.Write,FileShare.None))
            oversized.SetLength(16L*1024*1024+1);
        bool oversizedRejected=false;
        try{TestBackupPackage.Verify(manifestLab,ids[0]);}
        catch(InvalidDataException){oversizedRejected=true;}
        Check(oversizedRejected,
            "oversized offline manifest rejected before JSON deserialization");

        // The archive builder initially inspects all ten package headers.
        // A 16 MiB+ manifest must be rejected BEFORE any new archive stage
        // is created or the full ten-map world hashes are scanned.
        string firstPackageManifest=Path.Combine(hp,"map0","manifest.json");
        byte[] goodManifest=File.ReadAllBytes(firstPackageManifest);
        using(var oversizedInput=new FileStream(firstPackageManifest,
            FileMode.Open,FileAccess.Write,FileShare.None))
            oversizedInput.SetLength(16L*1024*1024+1);
        string deniedArchive=Path.Combine(archiveLab,"preflight-oversize-rejected");
        bool firstPassDenied=false;
        try{OfflineAsaClusterArchive.BuildFromFolders(hp,ryzen,transfer,deniedArchive);}
        catch(InvalidDataException){firstPassDenied=true;}
        Check(firstPassDenied&&!Directory.Exists(deniedArchive)&&
              !Directory.Exists(deniedArchive+".incomplete"),
            "5+5 archive preflight rejects oversized map header before any copy");
        File.WriteAllBytes(firstPackageManifest,goodManifest);
        Check(TestBackupPackage.Verify(Path.Combine(hp,"map0"),ids[0]).MapId==ids[0],
            "synthetic original map package remains fully verifiable after negative preflight");

        string destination=Path.Combine(archiveLab,"verified-asa-package");
        var report=OfflineAsaClusterArchive.BuildFromFolders(hp,ryzen,transfer,destination);
        Check(report.Schema==2&&report.Maps.Length==10&&
              report.Maps.Count(m=>m.Host=="HP")==5&&
              report.Maps.Count(m=>m.Host=="RYZEN")==5&&
              report.Transfer.Length==2&&!report.LiveConsistencyCertified&&
              !report.LiveStoreWriterCertified&&!report.RestoreToProductionAllowed,
            "SQL-free ten-map Store-copy archive verified without false production consistency");
        Check(OfflineAsaClusterArchive.Verify(destination).Batch==report.Batch &&
              new FileInfo(Path.Combine(destination,"TRANSFER","old-zero.arkprofile")).Length==0 &&
              !Directory.Exists(Path.Combine(destination,"SQL")),
            "verifier preserves historical zero-byte profile and never creates SQL folder");
        // Exercise the complete ten-map index bound without copying any game
        // bytes or touching production Store-v1 data.
        string signedIndex=Path.Combine(destination,"asa-cluster-index.json");
        byte[] originalIndex=File.ReadAllBytes(signedIndex);
        using(var expanded=new FileStream(signedIndex,FileMode.Open,
            FileAccess.Write,FileShare.None))
            expanded.SetLength(32L*1024*1024+1);
        bool bigIndexDenied=false;
        try{OfflineAsaClusterArchive.Verify(destination);}
        catch(InvalidDataException){bigIndexDenied=true;}
        Check(bigIndexDenied,
            "oversized ten-map signed archive index fails before JSON parsing");
        File.WriteAllBytes(signedIndex,originalIndex);
        // The SHA-256 and completion markers must also be read with strictly
        // bounded buffers; a damaged offline copy never reaches full world
        // scans merely because its metadata is huge.
        foreach(string markerName in new[]{"asa-cluster-index.sha256","complete"})
        {
            string smallMarker=Path.Combine(destination,markerName);
            byte[] originalMarker=File.ReadAllBytes(smallMarker);
            using(var oversizedMarker=new FileStream(smallMarker,FileMode.Open,
                FileAccess.Write,FileShare.None))
                oversizedMarker.SetLength(129);
            bool refusedHugeMarker=false;
            try{OfflineAsaClusterArchive.Verify(destination);}
            catch(InvalidDataException){refusedHugeMarker=true;}
            Check(refusedHugeMarker,
                "oversized ten-map archive metadata marker rejected: "+markerName);
            File.WriteAllBytes(smallMarker,originalMarker);
        }
        Check(OfflineAsaClusterArchive.Verify(destination).Batch==report.Batch,
            "restored synthetic archive index still passes full SHA256 verification");
        // Native UI uses Windows-only directory selection, not paths from
        // untrusted React. This injected picker is isolated to this test.
        string nativeOutput=Path.Combine(archiveLab,"native-output");
        Directory.CreateDirectory(nativeOutput);
        var selectedCopies=new Queue<string>(new[]{hp,ryzen,transfer,nativeOutput});
        using(var archiveBridge=new DesktopOfflineAsaArchiveBridge(
            _=>selectedCopies.Dequeue()))
        {
            var preview=await Call(archiveBridge.HandleAsync,"cluster.offlinePreview");
            IsOk(preview,"native four-folder offline cluster preview");
            Check(preview.GetProperty("data").GetProperty("hpCopies").GetInt32()==5 &&
                  preview.GetProperty("data").GetProperty("ryzenCopies").GetInt32()==5 &&
                  !preview.ToString().Contains(archiveLab,StringComparison.Ordinal),
                "native preview redacts original absolute folder paths");
            string token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var refused=await Call(archiveBridge.HandleAsync,"cluster.offlineApply",
                new{token,confirm=false});
            Check(IsError(refused,"UM-CLUSTER-ARCHIVE-APPROVAL-REQUIRED"),
                "offline archive needs explicit one-time approval");
            var started=await Call(archiveBridge.HandleAsync,"cluster.offlineApply",
                new{token,confirm=true});
            IsOk(started,"native offline archive job starts only after approval");
            string job=started.GetProperty("data").GetProperty("jobId").GetString()!;
            var replay=await Call(archiveBridge.HandleAsync,"cluster.offlineApply",
                new{token,confirm=true});
            Check(IsError(replay,"UM-CLUSTER-ARCHIVE-APPROVAL-REQUIRED") ||
                  IsError(replay,"UM-CLUSTER-ARCHIVE-BUSY"),
                "original native archive approval cannot start a duplicate copy");
            JsonElement final=default;
            for(int i=0;i<400;i++)
            {
                final=await Call(archiveBridge.HandleAsync,"cluster.offlineStatus",new{jobId=job});
                if(final.GetProperty("data").GetProperty("state").GetString()
                    is "completed" or "needsOperator")break;
                await Task.Delay(25);
            }
            IsOk(final,"native offline archive returns a final verifiable job status");
            Check(final.GetProperty("data").GetProperty("state").GetString()=="completed" &&
                  final.GetProperty("data").GetProperty("verifiedMaps").GetInt32()==10 &&
                  !final.GetProperty("data").GetProperty("sqlUsed").GetBoolean() &&
                  !final.GetProperty("data").GetProperty("productionRestoreAllowed").GetBoolean(),
                "native archive completes 10 verified offline map copies without SQL or Store-write permissions");
        }
        using(var restoreBridge=new DesktopOfflineAsaRestoreBridge(()=>destination))
        {
            var labPre=await Call(restoreBridge.HandleAsync,"cluster.restoreLabPreview");
            IsOk(labPre,"native restore lab chooses an existing offline ASA archive");
            Check(!labPre.GetProperty("data").GetProperty("verified").GetBoolean() &&
                  !labPre.GetProperty("data").GetProperty("productionRestoreAllowed").GetBoolean() &&
                  !labPre.ToString().Contains(archiveLab,StringComparison.Ordinal),
                "native restore preview neither claims integrity nor reveals original local source");
            string token=labPre.GetProperty("data").GetProperty("token").GetString()!;
            var refused=await Call(restoreBridge.HandleAsync,"cluster.restoreLabApply",
                new{token,confirm=false});
            Check(IsError(refused,"UM-ASARESTORE-APPROVAL-REQUIRED"),
                "isolated restore lab requires explicit operator approval");
            var started=await Call(restoreBridge.HandleAsync,"cluster.restoreLabApply",
                new{token,confirm=true});
            IsOk(started,"approved isolated restore lab starts");
            var job=started.GetProperty("data").GetProperty("jobId").GetString()!;
            var replay=await Call(restoreBridge.HandleAsync,"cluster.restoreLabApply",
                new{token,confirm=true});
            Check(IsError(replay,"UM-ASARESTORE-APPROVAL-REQUIRED") ||
                  IsError(replay,"UM-ASARESTORE-JOB-BUSY"),
                "one-shot restore approval cannot be replayed");
            JsonElement final=default;
            for(int i=0;i<400;i++)
            {
                final=await Call(restoreBridge.HandleAsync,
                    "cluster.restoreLabStatus",new{jobId=job});
                if(final.GetProperty("data").GetProperty("state").GetString()
                    is "completed" or "needsOperator")break;
                await Task.Delay(25);
            }
            IsOk(final,"isolated native restore returns real async status");
            var receipt=final.GetProperty("data");
            Check(receipt.GetProperty("state").GetString()=="completed" &&
                  receipt.GetProperty("maps").GetInt32()==10 &&
                  receipt.GetProperty("files").GetInt32()==10 &&
                  !receipt.GetProperty("productionRestoreAllowed").GetBoolean() &&
                  !receipt.GetProperty("storeWriterVerified").GetBoolean() &&
                  !receipt.GetProperty("sqlUsed").GetBoolean(),
                "native restore validates ten world files with no live writer or SQL");
        }
        string labDestination=Path.Combine(Path.GetTempPath(),
            "Untamed-AsaRestore-Lab-"+Guid.NewGuid().ToString("N"));
        var labReceipt=OfflineAsaRestoreLab.MaterializeToLab(destination,labDestination);
        Check(labReceipt.SourceBatch==report.Batch &&
              labReceipt.MapFiles.Length==10 &&
              !labReceipt.ProductionRestoreAllowed &&
              OfflineAsaRestoreLab.VerifyLab(labDestination).SourceBatch==report.Batch,
            "SQL-free archive materializes to isolated TEMP restore lab with 10 hashed worlds");
        // Restore-lab index and marker are untrusted even after a successful
        // archive extraction. Prove an oversized metadata file fails BEFORE
        // SHA hashing, JSON parsing or consuming unbounded memory.
        void OversizedLabMetadataDenied(string name,long forbiddenLength,string label)
        {
            string file=Path.Combine(labDestination,name);
            byte[] original=File.ReadAllBytes(file);
            try
            {
                using(var handle=new FileStream(file,FileMode.Open,FileAccess.Write,FileShare.None))
                    handle.SetLength(forbiddenLength);
                bool earlyRejection=false;
                try{OfflineAsaRestoreLab.VerifyLab(labDestination);}
                catch(InvalidDataException ex)
                {
                    earlyRejection=ex.Message.Contains("size limit",StringComparison.Ordinal);
                }
                Check(earlyRejection,label);
            }
            finally{File.WriteAllBytes(file,original);}
        }
        OversizedLabMetadataDenied("restore-lab-index.json",32L*1024*1024+1,
            "oversized restore lab index is refused before hashing or JSON deserialization");
        OversizedLabMetadataDenied("restore-lab-index.sha256",129,
            "oversized restore lab checksum marker is refused before reading arbitrary text");
        OversizedLabMetadataDenied("complete",129,
            "oversized restore lab completion marker is refused before reading arbitrary text");
        Check(OfflineAsaRestoreLab.VerifyLab(labDestination).SourceBatch==report.Batch,
            "valid complete lab remains byte-identical and verifiable after each refused metadata attack");
        string world=Path.Combine(labDestination,"HP",ids[0].ToString("N"),"World0.ark");
        File.AppendAllText(world,"CORRUPTION");
        bool worldRejected=false;
        try{OfflineAsaRestoreLab.VerifyLab(labDestination);}
        catch(InvalidDataException){worldRejected=true;}
        Check(worldRejected,"offline restore verifier rejects modified world after completed lab restore");
        string marker=Path.Combine(destination,"TRANSFER","old-zero.arkprofile");
        File.WriteAllBytes(marker,new byte[]{7});
        bool rejected=false;
        try{OfflineAsaClusterArchive.Verify(destination);}
        catch(InvalidDataException){rejected=true;}
        Check(rejected,"modified transfer data invalidates verified ASA-only cluster package");
        File.Delete(marker);
        rejected=false;
        try{OfflineAsaClusterArchive.Verify(destination);}
        catch(InvalidDataException){rejected=true;}
        Check(rejected,"missing transfer data invalidates verified ASA-only package");
        string noStore=Path.Combine(archiveLab,"transfer-no-store");
        Directory.CreateDirectory(noStore);
        File.WriteAllText(Path.Combine(noStore,"some-transfer.bin"),"synthetic");
        rejected=false;
        try{OfflineAsaClusterArchive.BuildFromFolders(
            hp,ryzen,noStore,Path.Combine(archiveLab,"unsafe-package"));}
        catch(InvalidDataException){rejected=true;}
        Check(rejected,"ASA-only cluster archive refuses absent original Store coordination metadata");

        // Extended self-contained, locally copied config files must accompany
        // all ten worlds and the original transfer. Neither side may disappear.
        for(int i=0;i<ids.Length;i++)
        {
            string host=i<5?hp:ryzen;
            string folder=Path.Combine(host,"CONFIG",ids[i].ToString("N"),"WindowsServer");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"Game.ini"),
                "[/Script/ShooterGame.ShooterGameMode]\\nSYNTHETIC="+i);
            File.WriteAllText(Path.Combine(folder,"GameUserSettings.ini"),
                "[ServerSettings]\\nSYNTHETIC="+i);
        }
        string fullDestination=Path.Combine(archiveLab,"verified-asa-full-saved-package");
        var fullArchive=OfflineAsaClusterArchive.BuildFromFolders(
            hp,ryzen,transfer,fullDestination);
        Check(fullArchive.Schema==3&&fullArchive.Config?.Length==20 &&
              fullArchive.Maps.Length==10&&fullArchive.Transfer.Length==2&&
              !fullArchive.LiveConsistencyCertified&&!fullArchive.RestoreToProductionAllowed,
            "v3 backup bundles both hosts' ten world packages, 20 INIs and shared transfer without live certification");
        Check(OfflineAsaClusterArchive.Verify(fullDestination).Batch==fullArchive.Batch,
            "manifest verifies exact configuration path ownership and file SHA256");
        // Two independently sealed offline batches, unchanged historical
        // zero-byte profile and one NEW transfer member, never a live ASA map.
        string newTransfer=Path.Combine(transfer,"synthetic-new-transfer.dat");
        File.WriteAllText(newTransfer,"synthetic upload only");
        string afterArchivePath=Path.Combine(archiveLab,"verified-archive-after-transfer");
        var afterArchive=OfflineAsaClusterArchive.BuildFromFolders(
            hp,ryzen,transfer,afterArchivePath);
        var delta=OfflineAsaArchiveComparison.Compare(
            fullDestination,afterArchivePath);
        Check(delta.BeforeBatch==fullArchive.Batch&&
              delta.AfterBatch==afterArchive.Batch&&delta.Maps==10&&
              delta.TransferAdded==1&&delta.TransferRemoved==0&&
              delta.TransferModified==0&&delta.ZeroByteTransferFilesPreserved==1&&
              delta.Results.All(m=>!m.WorldChanged)&&
              delta.BothArchivesSha256Verified&&
              !delta.LiveConsistencyCertified&&!delta.StoreWriterCertified&&
              !delta.CharacterTravelVerified&&!delta.ProductionRestoreAllowed,
            "two signed 10-map copies identify new tribute member, retain zero-byte profile, never certify live travel");
        var pickedArchives=new Queue<string>(new[]{fullDestination,afterArchivePath});
        using(var comparisonBridge=new DesktopEmbeddedStoreBridge(
            null,()=>pickedArchives.Dequeue()))
        {
            var wire=await Call(comparisonBridge.HandleAsync,"store.compareArchives");
            IsOk(wire,"native two-folder archive comparison returns SHA-verified delta");
            Check(wire.GetProperty("data").GetProperty("transferAdded").GetInt32()==1&&
                  wire.GetProperty("data").GetProperty("results").GetArrayLength()==10&&
                  !wire.ToString().Contains(archiveLab,StringComparison.OrdinalIgnoreCase)&&
                  !wire.ToString().Contains("synthetic-new-transfer.dat",StringComparison.OrdinalIgnoreCase)&&
                  !wire.GetProperty("data").GetProperty("productionRestoreAllowed").GetBoolean(),
                "WebView receives aggregate file counts only, never transfer filenames or archive paths");
            var injected=await Call(comparisonBridge.HandleAsync,"store.compareArchives",
                new{beforeArchive=fullDestination,afterArchive=afterArchivePath});
            Check(IsError(injected,"UM-STORE-COPY-PATH-DENIED"),
                "renderer cannot inject archive paths into the native comparison");
        }
        bool reverseRejected=false;
        try{OfflineAsaArchiveComparison.Compare(afterArchivePath,fullDestination);}
        catch(InvalidDataException){reverseRejected=true;}
        Check(reverseRejected,"offline compare refuses reverse-time archive pairing");
        File.AppendAllText(Path.Combine(afterArchivePath,"TRANSFER",
            "synthetic-new-transfer.dat"),"tampered");
        bool tamperRejected=false;
        try{OfflineAsaArchiveComparison.Compare(fullDestination,afterArchivePath);}
        catch(InvalidDataException){tamperRejected=true;}
        Check(tamperRejected,"offline compare fails closed on modified tribute bytes");
        File.Delete(newTransfer);
        string fullRestore=Path.Combine(Path.GetTempPath(),
            "Untamed-AsaRestore-Lab-"+Guid.NewGuid().ToString("N"));
        var restoredFull=OfflineAsaRestoreLab.MaterializeToLab(
            fullDestination,fullRestore);
        Check(restoredFull.SourceBatch==fullArchive.Batch&&
            OfflineAsaRestoreLab.VerifyLab(fullRestore).SourceArchive.Config?.Length==20 &&
            File.ReadAllText(Path.Combine(fullRestore,"CONFIG","HP",
                ids[0].ToString("N"),"WindowsServer","Game.ini"))
                .Contains("SYNTHETIC=0",StringComparison.Ordinal),
            "full offline restore copies original map configs and transfer to TEMP with exact hashes");
        string changedConfig=Path.Combine(fullRestore,"CONFIG","RYZEN",
            ids[5].ToString("N"),"WindowsServer","GameUserSettings.ini");
        File.AppendAllText(changedConfig,"CORRUPTION");
        rejected=false;
        try{OfflineAsaRestoreLab.VerifyLab(fullRestore);}
        catch(InvalidDataException){rejected=true;}
        Check(rejected,"tampered restored configuration is never marked complete");
        string changedArchive=Path.Combine(fullDestination,"CONFIG","HP",
            ids[0].ToString("N"),"WindowsServer","Game.ini");
        File.AppendAllText(changedArchive,"CORRUPTION");
        rejected=false;
        try{OfflineAsaClusterArchive.Verify(fullDestination);}
        catch(InvalidDataException){rejected=true;}
        Check(rejected,"tampered original config invalidates sealed cluster backup");
        // A newly initialized ASA map may legitimately omit Game.ini.
        // Preserve the absence; never synthesize an empty replacement.
        string optionalGameIni=Path.Combine(ryzen,"CONFIG",
            ids[5].ToString("N"),"WindowsServer","Game.ini");
        File.Delete(optionalGameIni);
        string optionalDestination=Path.Combine(archiveLab,"valid-optional-game-ini");
        var optionalArchive=OfflineAsaClusterArchive.BuildFromFolders(
            hp,ryzen,transfer,optionalDestination);
        Check(optionalArchive.Schema==3&&optionalArchive.Config?.Length==19&&
              !optionalArchive.Config.Any(x=>x.Map==ids[5]&&
                  x.RelativePath.EndsWith("Game.ini",StringComparison.OrdinalIgnoreCase)),
            "ASA map with no Game.ini is still a valid full config backup; absence is retained");
        string optionalRestore=Path.Combine(Path.GetTempPath(),
            "Untamed-AsaRestore-Lab-"+Guid.NewGuid().ToString("N"));
        var optionalReport=OfflineAsaRestoreLab.MaterializeToLab(
            optionalDestination,optionalRestore);
        Check(optionalReport.SourceBatch==optionalArchive.Batch&&
              OfflineAsaRestoreLab.VerifyLab(optionalRestore).SourceArchive.Config?.Length==19&&
              !File.Exists(Path.Combine(optionalRestore,"CONFIG","RYZEN",
                  ids[5].ToString("N"),"WindowsServer","Game.ini"))&&
              File.Exists(Path.Combine(optionalRestore,"CONFIG","RYZEN",
                  ids[5].ToString("N"),"WindowsServer","GameUserSettings.ini")),
            "offline restore preserves absent Game.ini and hashes remaining settings");

        File.Delete(Path.Combine(ryzen,"CONFIG",ids[5].ToString("N"),
            "WindowsServer","GameUserSettings.ini"));
        rejected=false;
        try{OfflineAsaClusterArchive.BuildFromFolders(hp,ryzen,transfer,
            Path.Combine(archiveLab,"incomplete-config-source"));}
        catch(InvalidDataException){rejected=true;}
        Check(rejected,"missing required configuration never silently creates a world-only archive");

        // Real SQLite .ark files inside a SEALED ten-map archive: only the
        // locally copied data is scanned. Nine deliberately non-SQLite worlds
        // must be reported as unknown, not incorrectly marked as lost players.
        string storeMapDir=Path.Combine(archiveLab,"new-offline-sqlite-world");
        Directory.CreateDirectory(storeMapDir);
        string worldCopy=Path.Combine(storeMapDir,"World0.ark");
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{
                DataSource=worldCopy,
                Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                Pooling=false
            }.ToString()))
        {
            db.Open();
            using var cmd=db.CreateCommand();
            cmd.CommandText="CREATE TABLE custom(key TEXT PRIMARY KEY,value BLOB)";
            cmd.ExecuteNonQuery();
            using var data=db.CreateCommand();
            data.CommandText="INSERT INTO custom(key,value) VALUES($key,$value)";
            data.Parameters.AddWithValue("$key","GameModeCustomBytes");
            data.Parameters.AddWithValue("$value",new byte[]{
                1,0,0,0, 0,0,0,0, 0,0,0,0
            });
            data.ExecuteNonQuery();
        }
        string storeHp=Path.Combine(archiveLab,"index-audit-hp");
        string storeRyzen=Path.Combine(archiveLab,"index-audit-ryzen");
        Directory.CreateDirectory(storeHp);Directory.CreateDirectory(storeRyzen);
        for(int i=0;i<ids.Length;i++)
            TestBackupPackage.Create(i==0?storeMapDir:
                Path.Combine(archiveLab,"synthetic-save-"+i),
                Path.Combine(i<5?storeHp:storeRyzen,"map-"+i),ids[i],
                "Offline sealed Store index audit fixture");
        string indexArchive=Path.Combine(archiveLab,"index-audit-archive");
        var sealedIndexArchive=OfflineAsaClusterArchive.BuildFromFolders(
            storeHp,storeRyzen,transfer,indexArchive);
        var indexAudit=EmbeddedStoreArchiveInspector.Inspect(indexArchive);
        Check(indexAudit.Batch==sealedIndexArchive.Batch&&indexAudit.Maps==10&&
              indexAudit.Results.Length==10&&indexAudit.IndexScannedMaps==1&&
              indexAudit.NeedsReviewMaps==9&&indexAudit.ArchiveFilesSha256Verified&&
              !indexAudit.ProductionRestoreAllowed&&!indexAudit.CharacterTravelVerified&&
              indexAudit.Results.Count(m=>m.Status=="INDEX_SCANNED")==1&&
              indexAudit.Results.Count(m=>m.Status=="EMBEDDED_INDEX_NOT_READABLE")==9,
            "verified 10-map archive reports actual SQLite Store index vs unknown world copies without fake gameplay proof");
        using(var indexBridge=new DesktopEmbeddedStoreBridge(
            ()=>worldCopy,()=>indexArchive))
        {
            var wire=await Call(indexBridge.HandleAsync,"store.inspectArchive");
            IsOk(wire,"native archive picker enumerates actual signed ten-map worlds");
            Check(wire.GetProperty("data").GetProperty("maps").GetInt32()==10&&
                  wire.GetProperty("data").GetProperty("indexScannedMaps").GetInt32()==1&&
                  wire.GetProperty("data").GetProperty("results").GetArrayLength()==10&&
                  !wire.ToString().Contains(indexArchive,StringComparison.OrdinalIgnoreCase),
                "WebView receives only bounded map-level Store proof, no archive folder path");
            var injected=await Call(indexBridge.HandleAsync,"store.inspectArchive",
                new{archive=indexArchive});
            Check(IsError(injected,"UM-STORE-COPY-PATH-DENIED"),
                "web renderer cannot select arbitrary archive path");
        }
        File.AppendAllText(Path.Combine(indexArchive,"HP",ids[0].ToString("N"),
            "data","World0.ark"),"BROKEN");
        bool indexTamperDenied=false;
        try{EmbeddedStoreArchiveInspector.Inspect(indexArchive);}
        catch(InvalidDataException){indexTamperDenied=true;}
        Check(indexTamperDenied,
            "tampered world hash blocks all ten-map Store archive index conclusions");


    }

    // Plugin config.json is an existing local file. Native editor supports
    // strict JSON validation, concurrent modification detection and .bak copy.
    {
        string pluginProfile=Path.Combine(root,"plugin-json-profile");
        string install=Path.Combine(root,"plugin-json-test-install");
        string config=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "ArkApi","Plugins","SamplePlugin","config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config,"{\"enabled\":true,\"slot\":1}");
        await using var pluginLab=new ClusterService("plugin-json",pluginProfile,
            _=>new ProcessCheck(false,"Synthetic stopped",null,true));
        var owned=new LocalMap{
            Id=Guid.NewGuid(),Name="Plugin test map",
            InstallationPath=install,
            Arguments="TheIsland_WP?listen -Port=7777"
        };
        pluginLab.AddMap(owned);
        using var editor=new DesktopPluginJsonBridge(pluginLab);
        var traversal=await Call(editor.HandleAsync,"plugins.jsonOpen",new{
            mapId=owned.Id,plugin="../SamplePlugin"});
        Check(IsError(traversal,"UM-PLUGIN-JSON-SAFETY-CHECK-FAILED"),
            "plugin editor refuses renderer-controlled path traversal");
        var opened=await Call(editor.HandleAsync,"plugins.jsonOpen",new{
            mapId=owned.Id,plugin="SamplePlugin"});
        IsOk(opened,"native plugin JSON opens original local file");
        string token=opened.GetProperty("data").GetProperty("token").GetString()!;
        var invalid=await Call(editor.HandleAsync,"plugins.jsonPreview",new{
            token,content="{\"enabled\":true,\"enabled\":false}"});
        Check(IsError(invalid,"UM-PLUGIN-JSON-SAFETY-CHECK-FAILED"),
            "duplicate JSON plugin keys fail validation with zero writes");
        Check(File.ReadAllText(config).Contains("\"slot\":1",StringComparison.Ordinal),
            "invalid plugin preview did not alter original bytes");
        var valid=await Call(editor.HandleAsync,"plugins.jsonPreview",new{
            token,content="{\"enabled\":false,\"slot\":2}"});
        IsOk(valid,"valid stopped-map plugin JSON update prepared");
        var applied=await Call(editor.HandleAsync,"plugins.jsonSave",new{token,confirm=true});
        IsOk(applied,"native plugin JSON apply uses atomic replacement");
        Check(File.ReadAllText(config).Contains("\"slot\":2",StringComparison.Ordinal) &&
              applied.GetProperty("data").GetProperty("backupCreated").GetBoolean() &&
              !applied.GetProperty("data").GetProperty("asaPluginReloaded").GetBoolean(),
            "atomic JSON update preserved backup and never claims live plugin reload");
        var replay=await Call(editor.HandleAsync,"plugins.jsonSave",new{token,confirm=true});
        Check(IsError(replay,"UM-PLUGIN-JSON-SESSION-EXPIRED"),
            "plugin JSON approval cannot be replayed");
        var race=await Call(editor.HandleAsync,"plugins.jsonOpen",new{
            mapId=owned.Id,plugin="SamplePlugin"});
        IsOk(race,"race fixture reads current plugin JSON");
        string raceToken=race.GetProperty("data").GetProperty("token").GetString()!;
        IsOk(await Call(editor.HandleAsync,"plugins.jsonPreview",new{
            token=raceToken,content="{\"enabled\":true,\"slot\":3}"}),
            "race fixture stages valid plugin JSON");
        File.WriteAllText(config,"{\"enabled\":true,\"slot\":999}");
        var stale=await Call(editor.HandleAsync,"plugins.jsonSave",new{
            token=raceToken,confirm=true});
        Check(IsError(stale,"UM-PLUGIN-JSON-SAFETY-CHECK-FAILED") &&
              File.ReadAllText(config).Contains("\"slot\":999",StringComparison.Ordinal),
            "external plugin edit causes ETag conflict without overwriting newer bytes");
    }

    // REAL localhost Source RCON wire test: fake isolated TCP server, no ASA.
    // This catches the old bug where the admin bridge called read-only
    // RconProtocol.Execute and silently refused Broadcast/wildWipe.
    {
        using var socketDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener=new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,0);
        listener.Start();
        int rconPort=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        async Task<(int Id,int Type,string Body)> ReadWire(
            System.Net.Sockets.NetworkStream stream,CancellationToken ct)
        {
            var header=new byte[4];
            await stream.ReadExactlyAsync(header,ct);
            int count=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
            if(count is <10 or >8192)throw new InvalidDataException("Fake RCON frame length");
            var payload=new byte[count];
            await stream.ReadExactlyAsync(payload,ct);
            int id=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                payload.AsSpan(0,4));
            int type=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                payload.AsSpan(4,4));
            string message=Encoding.UTF8.GetString(payload,8,count-10);
            return(id,type,message);
        }
        async Task WriteWire(System.Net.Sockets.NetworkStream stream,
            int id,int type,string body,CancellationToken ct)
        {
            byte[] data=Encoding.UTF8.GetBytes(body);
            byte[] frame=new byte[14+data.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                frame.AsSpan(0,4),10+data.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                frame.AsSpan(4,4),id);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                frame.AsSpan(8,4),type);
            data.AsSpan().CopyTo(frame.AsSpan(12));
            await stream.WriteAsync(frame,ct);
            await stream.FlushAsync(ct);
        }
        bool authReceived=false,verifiedAfterAuth=false;
        string commandReceived="";
        Task mockServer=Task.Run(async()=>{
            using var client=await listener.AcceptTcpClientAsync(socketDeadline.Token);
            using var stream=client.GetStream();
            var auth=await ReadWire(stream,socketDeadline.Token);
            Check(auth.Type==3 && auth.Body=="LOCAL-RCON-WIRE-TEST-PASSWORD",
                "fake Source RCON server receives expected native auth request");
            authReceived=true;
            await WriteWire(stream,auth.Id,2,"",socketDeadline.Token);
            var command=await ReadWire(stream,socketDeadline.Token);
            Check(command.Type==2 && verifiedAfterAuth,
                "native permission check runs after authentication and before command bytes");
            commandReceived=command.Body;
        });
        string dispatched=await RconProtocol.DispatchApprovedAdminAsync(
            "127.0.0.1",rconPort,"LOCAL-RCON-WIRE-TEST-PASSWORD",
            "Broadcast Untamed native one-shot",
            ()=>{Check(authReceived,"admin command authorization occurs after RCON login");
                verifiedAfterAuth=true;},socketDeadline.Token);
        await mockServer.WaitAsync(socketDeadline.Token);
        listener.Stop();
        Check(commandReceived=="Broadcast Untamed native one-shot" &&
              dispatched=="ADMIN_RCON_DISPATCHED_EXECUTION_NOT_VERIFIED",
            "real localhost RCON Broadcast writes one packet and does not pretend execution succeeded");
        bool directReadOnlyDenied=false;
        try{await RconProtocol.Execute("127.0.0.1",rconPort,
            "LOCAL-RCON-WIRE-TEST-PASSWORD","Broadcast denied",socketDeadline.Token);}
        catch(InvalidOperationException){directReadOnlyDenied=true;}
        Check(directReadOnlyDenied,
            "unapproved read-only RCON endpoint still refuses admin mutations");
        bool lifecycleDenied=false;
        try{await RconProtocol.DispatchApprovedAdminAsync("127.0.0.1",rconPort,
            "LOCAL-RCON-WIRE-TEST-PASSWORD","admincheat DoExit",
            ()=>{},socketDeadline.Token);}
        catch(UnauthorizedAccessException){lifecycleDenied=true;}
        Check(lifecycleDenied,
            "admincheat-prefixed shutdown cannot bypass separately approved Stop");
        // Validate an actual native SaveWorld packet after login, never through
        // the read-only Execute() path which deliberately rejects mutation.
        using var saveDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var saveListener=new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,0);
        saveListener.Start();
        int savePort=((System.Net.IPEndPoint)saveListener.LocalEndpoint).Port;
        bool saveAuthorized=false,saveAuthenticated=false;
        string savedWireCommand="";
        var saveMock=Task.Run(async()=>{
            using var client=await saveListener.AcceptTcpClientAsync(saveDeadline.Token);
            using var stream=client.GetStream();
            var auth=await ReadWire(stream,saveDeadline.Token);
            Check(auth.Type==3 && auth.Body=="LOCAL-RCON-WIRE-TEST-PASSWORD",
                "native SaveWorld requires real local RCON authentication");
            saveAuthenticated=true;
            await WriteWire(stream,auth.Id,2,"",saveDeadline.Token);
            var command=await ReadWire(stream,saveDeadline.Token);
            Check(command.Type==2 && saveAuthorized,
                "native SaveWorld must verify exact owner after RCON login");
            savedWireCommand=command.Body;
        });
        await RconProtocol.DispatchOwnedSaveWorldAsync(savePort,
            "LOCAL-RCON-WIRE-TEST-PASSWORD",
            ()=>{Check(saveAuthenticated,"SaveWorld approved after RCON auth");
                saveAuthorized=true;},saveDeadline.Token);
        await saveMock.WaitAsync(saveDeadline.Token);
        saveListener.Stop();
        Check(savedWireCommand=="SaveWorld",
            "explicit real SaveWorld sends one RCON packet but never claims SaveComplete");

        // Empty GetChat is a normal quiet server result, provided a second
        // ordered read-only reply positively completes the Source RCON stream.
        using var chatDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var chatListener=new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,0);
        chatListener.Start();
        int chatPort=((System.Net.IPEndPoint)chatListener.LocalEndpoint).Port;
        var chatMock=Task.Run(async()=>{
            using var client=await chatListener.AcceptTcpClientAsync(chatDeadline.Token);
            using var stream=client.GetStream();
            var auth=await ReadWire(stream,chatDeadline.Token);
            await WriteWire(stream,auth.Id,2,"",chatDeadline.Token);
            var first=await ReadWire(stream,chatDeadline.Token);
            Check(first.Body=="GetChat" && first.Type==2,
                "read-only native GetChat sends only an allowlisted command");
            await WriteWire(stream,first.Id,0,"",chatDeadline.Token);
            var barrier=await ReadWire(stream,chatDeadline.Token);
            Check(barrier.Body=="ListPlayers" && barrier.Type==2,
                "empty chat requires second ordered RCON completion barrier");
            await WriteWire(stream,barrier.Id,0,"No Players Connected",chatDeadline.Token);
        });
        string emptyChat=await RconProtocol.Execute("127.0.0.1",chatPort,
            "LOCAL-RCON-WIRE-TEST-PASSWORD","GetChat",chatDeadline.Token);
        await chatMock.WaitAsync(chatDeadline.Token);
        chatListener.Stop();
        Check(emptyChat=="",
            "empty verified native GetChat is successful, not a false protocol failure");
    }

    // Independent ASA SaveWorld proof: stale log lines, mere TCP dispatch,
    // or an unchanged .ark world are NEVER success. This fixture never
    // executes a real ASA server or writes to production paths.
    {
        string witnessRoot=Path.Combine(root,"synthetic-save-proof");
        string witnessLog=Path.Combine(witnessRoot,"ShooterGame","Saved","Logs","ShooterGame.log");
        string witnessWorld=Path.Combine(witnessRoot,"ShooterGame","Saved",
            "SavedArks","TheIsland_WP","TheIsland_WP.ark");
        Directory.CreateDirectory(Path.GetDirectoryName(witnessLog)!);
        Directory.CreateDirectory(Path.GetDirectoryName(witnessWorld)!);
        File.WriteAllText(witnessLog,
            "Saving world...\\nWorld Save Complete. Took: 1.0\\n");
        File.WriteAllText(witnessWorld,"SYNTHETIC-WORLD-BEFORE");
        File.SetLastWriteTimeUtc(witnessWorld,DateTime.UtcNow.AddSeconds(-30));
        var boundary=AsaWorldSaveEvidence.BeginFromPaths(Guid.NewGuid(),
            32654,DateTimeOffset.UtcNow,witnessRoot,witnessLog,witnessWorld)!;
        var staleWitness=await AsaWorldSaveEvidence.ObserveAsync(boundary,()=>true);
        Check(!staleWitness.LogStarted&&!staleWitness.LogCompleted &&
              !staleWitness.CompletionObserved,
            "older logged World Save Complete before command boundary is not accepted");
        File.AppendAllText(witnessLog,"World Save Complete. Took: 0.8\\n");
        File.WriteAllText(witnessWorld,"SYNTHETIC-WORLD-AFTER");
        File.SetLastWriteTimeUtc(witnessWorld,DateTime.UtcNow.AddSeconds(1));
        var noStartWitness=await AsaWorldSaveEvidence.ObserveAsync(boundary,()=>true);
        Check(!noStartWitness.CompletionObserved&&!noStartWitness.LogStarted,
            "new SaveComplete without ordered post-boundary Saving world stays unverified");
        File.AppendAllText(witnessLog,
            "Saving world...\\nWorld Save Complete. Took: 0.5\\n");
        var observedWitness=await AsaWorldSaveEvidence.ObserveAsync(boundary,()=>true);
        Check(observedWitness.CompletionObserved &&
              observedWitness.LogStarted&&observedWitness.LogCompleted &&
              observedWitness.WorldUpdated&&observedWitness.WorldStable &&
              !observedWitness.RestoreOrClientJoinVerified,
            "fresh ASA log sequence plus changed stable world is separate evidence, not restored survivor proof");
        bool changedPidDenied=false;
        try{await AsaWorldSaveEvidence.ObserveAsync(boundary,()=>false);}
        catch(InvalidOperationException){changedPidDenied=true;}
        Check(changedPidDenied,
            "ASA save evidence is discarded when the owned server process identity changes");
        File.WriteAllText(witnessLog,"new log session");
        var rotatedWitness=await AsaWorldSaveEvidence.ObserveAsync(boundary,()=>true);
        Check(!rotatedWitness.CompletionObserved &&
              !rotatedWitness.LogIdentityValid,
            "log truncation or rotation cannot reuse SaveComplete evidence");
        // Some real ASA builds write the .ark but omit both save log markers.
        // This may release an operator-approved lifecycle stop, but it NEVER
        // yields the stricter paired-log SaveComplete certification.
        string silentLog=Path.Combine(witnessRoot,"absent-log","ShooterGame.log");
        string silentWorld=Path.Combine(witnessRoot,"silent-world.ark");
        File.WriteAllText(silentWorld,"SYNTHETIC SILENT WORLD BEFORE");
        File.SetLastWriteTimeUtc(silentWorld,DateTime.UtcNow.AddSeconds(-30));
        var silentBoundary=AsaWorldSaveEvidence.BeginFromPaths(Guid.NewGuid(),
            32655,DateTimeOffset.UtcNow,witnessRoot,silentLog,silentWorld)!;
        var initiallySilent=await AsaWorldSaveEvidence.ObserveAsync(silentBoundary,()=>true);
        Check(!initiallySilent.WorldMetadataObserved &&
              !initiallySilent.CompletionObserved,
            "missing ASA log alone cannot certify SaveWorld or world metadata");
        File.AppendAllText(silentWorld," + NEW SAVE");
        File.SetLastWriteTimeUtc(silentWorld,DateTime.UtcNow.AddSeconds(2));
        var stableWithoutLog=await AsaWorldSaveEvidence.ObserveAsync(
            silentBoundary,()=>true);
        Check(stableWithoutLog.WorldMetadataObserved &&
              stableWithoutLog.WorldUpdated&&stableWithoutLog.WorldStable &&
              !stableWithoutLog.CompletionObserved &&
              !stableWithoutLog.LogIdentityValid &&
              stableWithoutLog.Stage=="WORLD_UPDATED_STABLE_LOG_MISSING",
            "changed stable ASA world without log is explicitly weaker lifecycle evidence");
        var weakWait=await AsaWorldSaveEvidence.WaitAsync(silentBoundary,()=>true,
            TimeSpan.FromSeconds(5),allowWorldMetadata:true);
        Check(weakWait.WorldMetadataObserved&&!weakWait.CompletionObserved,
            "metadata-tolerant SaveWorld wait never fabricates SaveComplete log");
        var rearmed=AsaWorldSaveEvidence.BeginFromPaths(Guid.NewGuid(),
            32655,DateTimeOffset.UtcNow,witnessRoot,silentLog,silentWorld)!;
        var unchanged=await AsaWorldSaveEvidence.ObserveAsync(rearmed,()=>true);
        Check(!unchanged.WorldMetadataObserved&&!unchanged.CompletionObserved,
            "previous stable .ark cannot authorize a second SaveWorld without new change");
    }

    // Read-only RCON uses an isolated synthetic process identity and injected
    // fake ListPlayers response. This must never reach live ASA or real SQL.
    {
        string syntheticRconRoot=Path.Combine(root,"rcon-fake-install");
        string syntheticRconExe=Path.Combine(syntheticRconRoot,"ShooterGame",
            "Binaries","Win64","ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(syntheticRconExe)!);
        File.WriteAllText(syntheticRconExe,"SYNTHETIC TEST EXE NOT EXECUTED");
        string syntheticRconLog=Path.Combine(syntheticRconRoot,"ShooterGame",
            "Saved","Logs","ShooterGame.log");
        string syntheticRconWorld=Path.Combine(syntheticRconRoot,"ShooterGame",
            "Saved","SavedArks","TheIsland_WP","TheIsland_WP.ark");
        Directory.CreateDirectory(Path.GetDirectoryName(syntheticRconLog)!);
        Directory.CreateDirectory(Path.GetDirectoryName(syntheticRconWorld)!);
        File.WriteAllText(syntheticRconLog,"Saving world...\\nWorld Save Complete. Took: 1.0\\n");
        File.WriteAllText(syntheticRconWorld,"SYNTHETIC PREVIOUS WORLD");
        File.SetLastWriteTimeUtc(syntheticRconWorld,DateTime.UtcNow.AddSeconds(-25));
        var fakeMap=new LocalMap{
            Name="The Island",ExecutablePath=syntheticRconExe,
            InstallationPath=syntheticRconRoot,
            RconHost="127.0.0.1",RconPort=37555,
            RconPasswordProtected=SecureStore.Protect("test-only-passphrase")
        };
        var identity=new ProcessIdentity {
            Pid=550055,StartedUtc=DateTimeOffset.UtcNow,
            ExecutablePath=fakeMap.ExecutablePath,Arguments=""
        };
        bool rconRunning=true,owner=true;
        int dispatched=0;
        await using var observed=new ClusterService("rcon-readonly-synthetic",
            Path.Combine(root,"rcon-readonly-profile"),
            _=>new ProcessCheck(rconRunning,"synthetic process observed",
                rconRunning?identity:null,!rconRunning));
        observed.AddMap(fakeMap);
        var rcon=new DesktopRconReadOnlyBridge(observed,
            (_,pid)=>owner && pid==identity.Pid,
            (host,port,password,command,ct)=>{
                Check(host=="127.0.0.1"&&port==fakeMap.RconPort &&
                      password=="test-only-passphrase"&&command is ("ListPlayers" or "SaveWorld" or "GetGameLog" or "GetChat"),
                    "RCON command is fixed allowlist and secret never reaches WebView");
                dispatched++;return Task.FromResult(command switch {
                    "SaveWorld"=>"World save queued",
                    "GetGameLog"=>"Synthetic world log line\nServerAdminPassword=test-only-passphrase",
                    "GetChat"=>"Synthetic chat message",
                    _=>"Player 1: Synthetic Survivor"
                });
            });
        var ready=await Call(rcon.HandleAsync,"rcon.status",new{mapId=fakeMap.Id});
        IsOk(ready,"read-only local RCON status is available on synthetic owned map");
        Check(ready.GetProperty("data").GetProperty("permitted").GetBoolean() &&
              !ready.GetRawText().Contains("test-only-passphrase",StringComparison.Ordinal),
            "RCON status reports safe local ownership without exposing credentials");
        var players=await Call(rcon.HandleAsync,"rcon.listPlayers",
            new{mapId=fakeMap.Id,command="DestroyWildDinos"});
        IsOk(players,"ListPlayers can be read from a verified synthetic local process");
        Check(dispatched==1 && players.GetProperty("data").GetProperty("readOnly").GetBoolean() &&
              !players.GetProperty("data").GetProperty("saveCompleteProven").GetBoolean() &&
              players.GetProperty("data").GetProperty("response").GetString()=="Player 1: Synthetic Survivor",
            "arbitrary renderer command is ignored; only read-only ListPlayers is dispatched");
        var deniedSave=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=false});
        Check(IsError(deniedSave,"UM-RCON-SAVE-EXPLICIT-CONFIRMATION-REQUIRED") &&
              dispatched==1,"real SaveWorld cannot execute without explicit approval");
        var savedWorld=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=true,command="DestroyWildDinos"});
        IsOk(savedWorld,"real fixed SaveWorld dispatch reached verified synthetic ASA RCON");
        Check(dispatched==2 &&
              !savedWorld.GetProperty("data").GetProperty("rconAcknowledged").GetBoolean() &&
              savedWorld.GetProperty("data").GetProperty("rconCommandDispatched").GetBoolean() &&
              !savedWorld.GetProperty("data").GetProperty("diskSaveCompletionProven").GetBoolean() &&
              !savedWorld.GetProperty("data").GetProperty("worldFilesTouchedByManager").GetBoolean() &&
              !savedWorld.GetRawText().Contains("test-only-passphrase",StringComparison.Ordinal),
            "SaveWorld ignores arbitrary command injection, redacts secrets and never fakes SaveComplete");
        var rconWatch=savedWorld.GetProperty("data");
        Guid watchId=Guid.Empty;
        Check(rconWatch.GetProperty("nativeWitnessAvailable").GetBoolean() &&
              Guid.TryParse(rconWatch.GetProperty("watchId").GetString(),out watchId) &&
              watchId!=Guid.Empty,
            "owned SaveWorld dispatch establishes a private new-log witness boundary");
        var duplicateWhilePending=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=true});
        Check(IsError(duplicateWhilePending,"UM-RCON-SAVE-ALREADY-PENDING") &&
              dispatched==2,
            "second SaveWorld on same map cannot invalidate an active witness");

        var pendingLog=await Call(rcon.HandleAsync,"rcon.saveStatus",new{
            mapId=fakeMap.Id,watchId
        });
        IsOk(pendingLog,"post-dispatch SaveWorld witness poll is read-only");
        Check(!pendingLog.GetProperty("data").GetProperty("combinedSaveEvidenceObserved").GetBoolean() &&
              !pendingLog.GetProperty("data").GetProperty("logSaveCompleteObserved").GetBoolean(),
            "previous autosave marker cannot falsely confirm a new RCON SaveWorld");
        File.AppendAllText(syntheticRconLog,
            "Saving world...\\nWorld Save Complete. Took: 0.6\\n");
        File.AppendAllText(syntheticRconWorld," + VERIFIED_SYNTHETIC_SAVE");
        File.SetLastWriteTimeUtc(syntheticRconWorld,DateTime.UtcNow.AddSeconds(1));
        var verifiedLog=await Call(rcon.HandleAsync,"rcon.saveStatus",new{
            mapId=fakeMap.Id,watchId
        });
        IsOk(verifiedLog,"RCON SaveWorld proof follows source log and world metadata");
        Check(verifiedLog.GetProperty("data").GetProperty("logSaveCompleteObserved").GetBoolean() &&
              verifiedLog.GetProperty("data").GetProperty("worldFileUpdated").GetBoolean() &&
              verifiedLog.GetProperty("data").GetProperty("worldFileStable").GetBoolean() &&
              verifiedLog.GetProperty("data").GetProperty("combinedSaveEvidenceObserved").GetBoolean() &&
              !verifiedLog.GetProperty("data").GetProperty("diskFsyncCertified").GetBoolean() &&
              !verifiedLog.GetProperty("data").GetProperty("gameClientRejoinVerified").GetBoolean(),
            "SaveWorld status distinguishes observed log/world data from untested durable fsync/client rejoin");
        var fakeWatch=await Call(rcon.HandleAsync,"rcon.saveStatus",new{
            mapId=fakeMap.Id,watchId=Guid.NewGuid()
        });
        Check(IsError(fakeWatch,"UM-RCON-SAVE-WITNESS-NOT-FOUND") &&
              dispatched==2,
            "unrecognized SaveWorld proof token cannot inspect another operation");
        // A real SaveWorld cannot race the machine-wide SteamCMD, Restart
        // or Backup writer, even when its browser approval is valid.
        using(var updating=observed.Operations.Acquire(
            fakeMap.InstallationPath,UntamedOperationKind.Update))
        {
            var overlappingSave=await Call(rcon.HandleAsync,
                "rcon.saveWorld",new{mapId=fakeMap.Id,confirm=true});
            Check(IsError(overlappingSave,"UM-RCON-SAVE-INSTALLATION-BUSY")&&
                  dispatched==2,
                "native installation lock prevents overlapping SteamCMD/SaveWorld dispatch");
            updating.Complete();
        }
        var nextSave=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=true});
        IsOk(nextSave,"new explicitly approved SaveWorld works after SteamCMD lease release");
        Check(dispatched==3&&
              observed.Operations.Observe(fakeMap.InstallationPath)?.Phase==
                UntamedOperationPhase.Completed,
            "SaveWorld dispatch commits its own durable installation journal without an automatic retry");
        owner=false;
        var deniedOwnership=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=true});
        Check(IsError(deniedOwnership,"UM-RCON-READONLY-PREFLIGHT-DENIED") &&
              dispatched==3,"SaveWorld denied if native RCON listener ownership changes");
        var refused=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=fakeMap.Id});
        Check(IsError(refused,"UM-RCON-READONLY-PREFLIGHT-DENIED") &&
              dispatched==3,"RCON port ownership loss prevents another dispatch");
        owner=true;
        fakeMap.Arguments="-usestore";
        var forbiddenStoreSave=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=true});
        Check(IsError(forbiddenStoreSave,"UM-HISTORICAL-STORE-LOCKED")&&
              dispatched==3,
            "native RCON SaveWorld rejects active -usestore writes without peer writer authority");
        fakeMap.Arguments="";
        rconRunning=false;
        var stopped=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=fakeMap.Id});
        Check(IsError(stopped,"UM-RCON-READONLY-PREFLIGHT-DENIED") &&
              dispatched==3,"stopped synthetic process cannot receive RCON");
        rconRunning=true;fakeMap.HistoricalStoreProtected=true;
        var locked=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=fakeMap.Id});
        Check(IsError(locked,"UM-HISTORICAL-STORE-LOCKED") &&
              dispatched==3,"historical Store-v1 lock always prevents RCON dispatch");
        var foreign=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=Guid.NewGuid()});
        Check(IsError(foreign,"UM-MAP-NOT-OWNED"),
            "RCON cannot target an unregistered or remote peer map");

        // A slow first map must not block RCON from a different local ASA.
        // No RCON or live ASA process is started in this fixture.
        await using(var parallelCluster=new ClusterService(
            "rcon-parallel-local",
            Path.Combine(root,"rcon-parallel-local-profile"),
            _=>new ProcessCheck(true,"parallel synthetic local process",identity,false)))
        {
            var parallelA=new LocalMap{
                Name="The Island",Id=Guid.NewGuid(),
                InstallationPath=syntheticRconRoot,
                ExecutablePath=syntheticRconExe,
                RconHost="127.0.0.1",RconPort=37555,
                RconPasswordProtected=SecureStore.Protect("test-only-passphrase")
            };
            string parallelOtherRoot=Path.Combine(root,"rcon-parallel-other-install");
            Directory.CreateDirectory(parallelOtherRoot);
            var parallelB=new LocalMap{
                Name="Ragnarok",Id=Guid.NewGuid(),
                InstallationPath=parallelOtherRoot,
                ExecutablePath=Path.Combine(parallelOtherRoot,"ArkAscendedServer.exe"),
                RconHost="127.0.0.1",RconPort=37565,
                RconPasswordProtected=SecureStore.Protect("test-only-passphrase")
            };
            parallelCluster.AddMap(parallelA);
            parallelCluster.AddMap(parallelB);
            var firstEntered=new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRelease=new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int firstCalls=0,secondCalls=0;
            var parallelRcon=new DesktopRconReadOnlyBridge(parallelCluster,
                (portNumber,pid)=>pid==identity.Pid&&
                    (portNumber==parallelA.RconPort||portNumber==parallelB.RconPort),
                async(host,portNumber,password,command,ct)=>{
                    Check(host=="127.0.0.1"&&password=="test-only-passphrase"&&
                          command is "ListPlayers" or "GetGameLog",
                        "parallel fresh ASA RCON remains fixed-command and loopback-only");
                    if(portNumber==parallelA.RconPort)
                    {
                        Interlocked.Increment(ref firstCalls);
                        firstEntered.TrySetResult(true);
                        await firstRelease.Task.WaitAsync(ct);
                    }
                    else Interlocked.Increment(ref secondCalls);
                    return "SYNTHETIC_"+command;
                });
            var slowRead=Call(parallelRcon.HandleAsync,"rcon.listPlayers",
                new{mapId=parallelA.Id});
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var otherRead=await Call(parallelRcon.HandleAsync,
                    "rcon.gameLog",new{mapId=parallelB.Id})
                    .WaitAsync(TimeSpan.FromSeconds(3));
                IsOk(otherRead,"second local ASA does not wait for a stalled RCON map");
                Check(otherRead.GetProperty("data").GetProperty("mapId").GetGuid()==
                      parallelB.Id&&
                      otherRead.GetProperty("data").GetProperty("command").GetString()==
                      "GetGameLog"&&firstCalls==1&&secondCalls==1,
                    "the two fresh ASA RCON lanes dispatch independently and exactly once");
            }
            finally{firstRelease.TrySetResult(true);}
            IsOk(await slowRead.WaitAsync(TimeSpan.FromSeconds(5)),
                "original slow ASA RCON finishes without a second dispatch");

            // Both map cards can hold independent short-lived RCON approvals.
            // A global single Pending token used to revoke the first one.
            int mutatingAdminSends=0;
            var otherMapAdmin=new DesktopRconAdminBridge(parallelCluster,
                (port,pid)=>pid==identity.Pid&&
                    (port==parallelA.RconPort||port==parallelB.RconPort),
                (host,port,password,command,ct)=>{
                    Check(host=="127.0.0.1"&&password=="test-only-passphrase",
                        "parallel mutation remains credential-bound and loopback-only");
                    Interlocked.Increment(ref mutatingAdminSends);
                    return Task.FromResult("SYNTHETIC ADMIN ACK");
                });
            var aPreview=await Call(otherMapAdmin.HandleAsync,"rcon.adminPreview",
                new{mapId=parallelA.Id,action="broadcast",message="Island notice"});
            var bPreview=await Call(otherMapAdmin.HandleAsync,"rcon.adminPreview",
                new{mapId=parallelB.Id,action="broadcast",message="Ragnarok notice"});
            IsOk(aPreview,"Island native RCON preview");
            IsOk(bPreview,"Ragnarok native RCON preview");
            string aToken=aPreview.GetProperty("data").GetProperty("token").GetString()!;
            string bToken=bPreview.GetProperty("data").GetProperty("token").GetString()!;
            Check(aToken!=bToken,"ten-map RCON previews retain independent one-use tokens");
            IsOk(await Call(otherMapAdmin.HandleAsync,"rcon.adminSend",
                new{token=aToken,confirm=true,typedApproval=""}),
                "Island RCON executes under local cross-process installation lease");
            IsOk(await Call(otherMapAdmin.HandleAsync,"rcon.adminSend",
                new{token=bToken,confirm=true,typedApproval=""}),
                "Ragnarok RCON approval survives independent Island command");
            Check(mutatingAdminSends==2,"exactly one admin dispatch per owned ASA map");
            var blockedPreview=await Call(otherMapAdmin.HandleAsync,
                "rcon.adminPreview",new{mapId=parallelA.Id,
                    action="broadcast",message="blocked by maintenance"});
            IsOk(blockedPreview,"mutating admin preview is not an execution");
            var blockedToken=blockedPreview.GetProperty("data")
                .GetProperty("token").GetString()!;
            using(var held=parallelCluster.Operations.Acquire(
                parallelA.InstallationPath,UntamedOperationKind.Update))
            {
                var denied=await Call(otherMapAdmin.HandleAsync,"rcon.adminSend",
                    new{token=blockedToken,confirm=true,typedApproval=""});
                Check(IsError(denied,"UM-RCON-ADMIN-SAFETY-CHECK-FAILED_NO_AUTO_RETRY")
                      &&mutatingAdminSends==2,
                    "SteamCMD installation lease prevents concurrent admin RCON mutation");
                held.Complete();
            }
            var rejectedReplay=await Call(otherMapAdmin.HandleAsync,
                "rcon.adminSend",new{token=blockedToken,confirm=true,typedApproval=""});
            Check(IsError(rejectedReplay,"UM-RCON-ADMIN-APPROVAL-REQUIRED")&&
                  mutatingAdminSends==2,
                "releasing SteamCMD lease never replays an already rejected admin token");
        }

        // Actual RCON admin execution is a separately approved, one-shot
        // native operation; the fake adapter never touches a game server.
        fakeMap.HistoricalStoreProtected=false;rconRunning=true;owner=true;
        int adminDispatches=0;string lastAdminCommand="";
        var admin=new DesktopRconAdminBridge(observed,
            (_,pid)=>owner&&pid==identity.Pid,
            (host,port,password,command,ct)=>{
                Check(host=="127.0.0.1"&&port==fakeMap.RconPort&&
                    password=="test-only-passphrase",
                    "admin dispatch is local and obtains DPAPI secret only inside native host");
                lastAdminCommand=command;
                adminDispatches++;
                return Task.FromResult(
                    "Admin command accepted; ServerAdminPassword=test-only-passphrase");
            });
        var announcement=await Call(admin.HandleAsync,"rcon.adminPreview",
            new{mapId=fakeMap.Id,action="broadcast",message="Hello Untamed!"});
        IsOk(announcement,"actual broadcast command receives native process-bound preview");
        Check(announcement.GetProperty("data").GetProperty("command").GetString()==
              "Broadcast Hello Untamed!"&&adminDispatches==0,
              "preview compiles exact command without network transmission");
        string adminToken=announcement.GetProperty("data").GetProperty("token").GetString()!;
        var unapproved=await Call(admin.HandleAsync,"rcon.adminSend",
            new{token=adminToken,confirm=false,typedApproval=""});
        Check(IsError(unapproved,"UM-RCON-ADMIN-APPROVAL-REQUIRED")&&adminDispatches==0,
            "operator must explicitly approve RCON command");
        var sent=await Call(admin.HandleAsync,"rcon.adminSend",
            new{token=adminToken,confirm=true,typedApproval=""});
        IsOk(sent,"real broadcast admin command sent once through synthetic local RCON");
        Check(adminDispatches==1&&lastAdminCommand=="Broadcast Hello Untamed!"&&
              sent.GetProperty("data").GetProperty("oneShot").GetBoolean()&&
              !sent.GetProperty("data").GetProperty("saveCompleteProven").GetBoolean()&&
              !sent.GetRawText().Contains("test-only-passphrase",StringComparison.Ordinal)&&
              sent.GetProperty("data").GetProperty("response").GetString()!
                  .Contains("[REDACTED]",StringComparison.Ordinal),
            "broadcast dispatch is one-shot and does not disclose native credentials");
        var repeated=await Call(admin.HandleAsync,"rcon.adminSend",
            new{token=adminToken,confirm=true,typedApproval=""});
        Check(IsError(repeated,"UM-RCON-ADMIN-APPROVAL-REQUIRED")&&adminDispatches==1,
            "admin command cannot replay after acknowledgement");
        var invalid=await Call(admin.HandleAsync,"rcon.adminPreview",
            new{mapId=fakeMap.Id,action="custom",message="DoExit"});
        Check(IsError(invalid,"UM-RCON-ADMIN-SAFETY-CHECK-FAILED_NO_AUTO_RETRY") &&
              adminDispatches==1,
            "graceful shutdown is not bypassed via raw admin console");
        invalid=await Call(admin.HandleAsync,"rcon.adminPreview",
            new{mapId=fakeMap.Id,action="broadcast",message="hi;DestroyWildDinos"});
        Check(IsError(invalid,"UM-RCON-ADMIN-SAFETY-CHECK-FAILED_NO_AUTO_RETRY") &&
              adminDispatches==1,
            "semicolon RCON command injection cannot be approved");
        var wipe=await Call(admin.HandleAsync,"rcon.adminPreview",
            new{mapId=fakeMap.Id,action="wildWipe",message=""});
        IsOk(wipe,"destructive wild dino wipe is previewable but does not execute");
        adminToken=wipe.GetProperty("data").GetProperty("token").GetString()!;
        var wrongWord=await Call(admin.HandleAsync,"rcon.adminSend",
            new{token=adminToken,confirm=true,typedApproval="IGEN"});
        Check(IsError(wrongWord,"UM-RCON-ADMIN-TYPED-APPROVAL-REQUIRED") &&
              adminDispatches==1,
            "destructive command requires exact typed native confirmation");
        wipe=await Call(admin.HandleAsync,"rcon.adminPreview",
            new{mapId=fakeMap.Id,action="wildWipe",message=""});
        adminToken=wipe.GetProperty("data").GetProperty("token").GetString()!;
        var wiped=await Call(admin.HandleAsync,"rcon.adminSend",
            new{token=adminToken,confirm=true,typedApproval="DINÓTÖRLÉS"});
        IsOk(wiped,"operator approved exactly one wild dino wipe");
        Check(adminDispatches==2&&lastAdminCommand=="DestroyWildDinos",
            "destructive RCON executes only through typed two-stage approval");
        var custom=await Call(admin.HandleAsync,"rcon.adminPreview",
            new{mapId=fakeMap.Id,action="custom",message="GetGameLog"});
        IsOk(custom,"advanced admin console accepts valid non-lifecycle command");
        adminToken=custom.GetProperty("data").GetProperty("token").GetString()!;
        rconRunning=false;
        var stale=await Call(admin.HandleAsync,"rcon.adminSend",
            new{token=adminToken,confirm=true,typedApproval="ADMIN"});
        Check(IsError(stale,"UM-RCON-ADMIN-SAFETY-CHECK-FAILED_NO_AUTO_RETRY") &&
              adminDispatches==2,
            "process going offline between preview and approval cancels admin dispatch");
        rconRunning=true;
        var foreignAdmin=await Call(admin.HandleAsync,"rcon.adminPreview",
            new{mapId=Guid.NewGuid(),action="custom",message="GetGameLog"});
        Check(IsError(foreignAdmin,"UM-RCON-ADMIN-SAFETY-CHECK-FAILED_NO_AUTO_RETRY") &&
              adminDispatches==2,
            "remote or unowned maps cannot reach native admin RCON");
        var gameLog=await Call(rcon.HandleAsync,"rcon.gameLog",
            new{mapId=fakeMap.Id,command="DestroyWildDinos"});
        IsOk(gameLog,"actual native GetGameLog reads local ASA log without caller-supplied commands");
        Check(dispatched==4 &&
              gameLog.GetProperty("data").GetProperty("command").GetString()=="GetGameLog" &&
              gameLog.GetProperty("data").GetProperty("readOnly").GetBoolean() &&
              gameLog.GetProperty("data").GetProperty("response")
                  .GetString()!.Contains("Synthetic world log line") &&
              !gameLog.GetRawText().Contains("test-only-passphrase",
                  StringComparison.Ordinal),
            "GetGameLog sends only fixed read-only RCON to owned local map");
        var chatLog=await Call(rcon.HandleAsync,"rcon.chatLog",new{mapId=fakeMap.Id});
        IsOk(chatLog,"actual native GetChat reads local ASA chat");
        Check(dispatched==5 &&
              chatLog.GetProperty("data").GetProperty("command").GetString()=="GetChat" &&
              chatLog.GetProperty("data").GetProperty("response")
                  .GetString()=="Synthetic chat message",
            "GetChat reads chat without arbitrary browser command dispatch");
        var rconPendingJournal=new AsaManualRestartJournal(fakeMap.InstallationPath);
        var pendingRconRestart=rconPendingJournal.Begin(fakeMap.Id,identity.Pid);
        var noSecondSave=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=true});
        Check(IsError(noSecondSave,"UM-RCON-READONLY-UNAVAILABLE")&&
              dispatched==5,
            "pending manual Restart blocks standalone SaveWorld before any RCON bytes");
        var diagnosticDuringRestart=await Call(rcon.HandleAsync,
            "rcon.chatLog",new{mapId=fakeMap.Id});
        Check(diagnosticDuringRestart.GetProperty("ok").GetBoolean()&&
              dispatched==6,
            "read-only GetChat remains available for operator diagnostics during restart");
        rconPendingJournal.Change(pendingRconRestart.Id,AsaRestartPhase.NeedsOperator);
    }

    // Two independent 5-map hosts; never use UK-HOME-01/02 or the live
    // MariaDB. The original synthetic .arkprofile bytes are not altered by
    // capture/compare, and zero-byte/duplicate transfers must fail.
    {
        var namesA=new[]{"Astraeos","Genesis Part 1","Valguero","The Island","Extinction"};
        var namesB=new[]{"Scorched Earth","Lost Colony","Ragnarok","The Center","Aberration"};
        var tokens=new Dictionary<string,string>{
            ["Astraeos"]="Astraeos_WP",
            ["Genesis Part 1"]="Genesis_WP",
            ["Valguero"]="Valguero_WP",
            ["The Island"]="TheIsland_WP",
            ["Extinction"]="Extinction_WP",
            ["Scorched Earth"]="ScorchedEarth_WP",
            ["Lost Colony"]="LostColony_WP",
            ["Ragnarok"]="Ragnarok_WP",
            ["The Center"]="TheCenter_WP",
            ["Aberration"]="Aberration_WP"
        };
        var pathA=Path.Combine(root,"travel-lab-a");
        var pathB=Path.Combine(root,"travel-lab-b");
        bool syntheticAsaRunning=false;
        await using var firstHost=new ClusterService("travel-lab-a",
            Path.Combine(root,"travel-profile-a"),
            _=>syntheticAsaRunning
                ?new ProcessCheck(true,"synthetic ASA running",null,false)
                :new ProcessCheck(false,"confirmed stopped synthetic travel",null,true));
        await using var secondHost=new ClusterService("travel-lab-b",
            Path.Combine(root,"travel-profile-b"),
            _=>new ProcessCheck(false,"confirmed stopped synthetic travel",null,true));
        var savePaths=new Dictionary<string,string>();
        foreach(var (names,service,basePath) in new[]{
            (namesA,firstHost,pathA),(namesB,secondHost,pathB)})
        foreach(var name in names)
        {
            string install=Path.Combine(basePath,name.Replace(" ",""));
            string save=Path.Combine(install,"ShooterGame","Saved","SavedArks",tokens[name]);
            Directory.CreateDirectory(save);
            savePaths[name]=save;
            service.AddMap(new LocalMap{Name=name,InstallationPath=install});
        }
        string sourceSave=savePaths["The Island"];
        string targetSave=savePaths["Ragnarok"];
        string sourceFile=Path.Combine(sourceSave,"76561198000000001.arkprofile");
        string destinationFile=Path.Combine(targetSave,"76561198000000001.arkprofile");
        string tribePath=Path.Combine(sourceSave,"42.arktribe");
        byte[] survivor=Encoding.UTF8.GetBytes("SYNTHETIC-SURVIVOR-PROFILE-VALID-2026");
        File.WriteAllBytes(sourceFile,survivor);
        File.WriteAllText(tribePath,"synthetic tribe");
        var travelA=new DesktopStoreTravelEvidenceBridge(firstHost);
        var travelB=new DesktopStoreTravelEvidenceBridge(secondHost);
        syntheticAsaRunning=true;
        var liveSnapshot=await Call(travelA.HandleAsync,"travel.capture");
        Check(liveSnapshot.GetProperty("ok").GetBoolean()&&
              !liveSnapshot.GetProperty("data").GetProperty("complete").GetBoolean()&&
              liveSnapshot.GetProperty("data").GetProperty("maps")
                  .EnumerateArray().All(m=>m.GetProperty("profileCount").GetInt32()==0&&
                    m.GetProperty("unstable").GetBoolean()),
            "running ASA never triggers player/tribe file enumeration or success");
        syntheticAsaRunning=false;
        using(var concurrentWriter=new FileStream(sourceFile,FileMode.Open,
            FileAccess.ReadWrite,FileShare.ReadWrite))
        {
            var conflicting=await Call(travelA.HandleAsync,"travel.capture");
            Check(IsError(conflicting,"UM-TRAVEL-EVIDENCE-UNVERIFIED"),
                "open external writer prevents SHA-256 travel snapshot rather than producing torn evidence");
        }
        var beforeA=await Call(travelA.HandleAsync,"travel.capture");
        var beforeB=await Call(travelB.HandleAsync,"travel.capture");
        IsOk(beforeA,"first 5 local map inventory captured without write");
        IsOk(beforeB,"second 5 local map inventory captured without write");
        Console.WriteLine("Travel A map preflight: "+beforeA.GetProperty("data").GetProperty("maps").GetRawText());
        Console.WriteLine("Travel B map preflight: "+beforeB.GetProperty("data").GetProperty("maps").GetRawText());
        Check(beforeA.GetProperty("data").GetProperty("complete").GetBoolean() &&
              beforeB.GetProperty("data").GetProperty("complete").GetBoolean() &&
              File.ReadAllBytes(sourceFile).SequenceEqual(survivor),
            "read-only capture does not mutate survivor bytes or claim Store writer authority");
        var sourceRecord=beforeA.GetProperty("data").GetProperty("maps")
            .EnumerateArray().Single(x=>x.GetProperty("mapName").GetString()=="The Island");
        string sourceMapId=sourceRecord.GetProperty("mapId").GetString()!;
        string profileHash=sourceRecord.GetProperty("profileFiles")[0].GetProperty("id").GetString()!;
        string destinationMapId=secondHost.Config.LocalMaps.Single(m=>m.Name=="Ragnarok")
            .Id.ToString("D");
        File.Move(sourceFile,destinationFile);
        var afterA=await Call(travelA.HandleAsync,"travel.capture");
        var afterB=await Call(travelB.HandleAsync,"travel.capture");
        var request=new{
            beforeA=beforeA.GetProperty("data").GetProperty("snapshot").GetString()!,
            beforeB=beforeB.GetProperty("data").GetProperty("snapshot").GetString()!,
            afterA=afterA.GetProperty("data").GetProperty("snapshot").GetString()!,
            afterB=afterB.GetProperty("data").GetProperty("snapshot").GetString()!,
            sourceMap=sourceMapId,destinationMap=destinationMapId,profileId=profileHash
        };
        var checkedTravel=await Call(travelA.HandleAsync,"travel.compare",request);
        IsOk(checkedTravel,"four native snapshots passed to deterministic two-host verifier");
        // Corrupt user-supplied JSON must return a typed error, not crash the
        // async-void WebView2 message handler via a null record member.
        var nullMap=System.Text.Json.Nodes.JsonNode.Parse(request.beforeA)!.AsObject();
        nullMap["maps"]!.AsArray()[0]=null;
        var invalidMap=await Call(travelA.HandleAsync,"travel.compare",new{
            beforeA=nullMap.ToJsonString(),request.beforeB,request.afterA,
            request.afterB,request.sourceMap,request.destinationMap,request.profileId
        });
        Check(IsError(invalidMap,"UM-TRAVEL-EVIDENCE-UNVERIFIED"),
            "null map in externally supplied snapshot fails closed without an exception");
        var nullFile=System.Text.Json.Nodes.JsonNode.Parse(request.beforeA)!.AsObject();
        nullFile["maps"]!.AsArray()
            .First(m=>m!["name"]!.GetValue<string>()=="The Island")!["files"]!.AsArray()[0]=null;
        var invalidFile=await Call(travelA.HandleAsync,"travel.compare",new{
            beforeA=nullFile.ToJsonString(),request.beforeB,request.afterA,
            request.afterB,request.sourceMap,request.destinationMap,request.profileId
        });
        Check(IsError(invalidFile,"UM-TRAVEL-EVIDENCE-UNVERIFIED"),
            "null proof object in supplied character snapshot is rejected");
        var nullDigest=System.Text.Json.Nodes.JsonNode.Parse(request.beforeA)!.AsObject();
        nullDigest["maps"]!.AsArray()
            .First(m=>m!["name"]!.GetValue<string>()=="The Island")!["files"]![0]!["sha256"]=null;
        var invalidDigest=await Call(travelA.HandleAsync,"travel.compare",new{
            beforeA=nullDigest.ToJsonString(),request.beforeB,request.afterA,
            request.afterB,request.sourceMap,request.destinationMap,request.profileId
        });
        Check(IsError(invalidDigest,"UM-TRAVEL-EVIDENCE-UNVERIFIED"),
            "missing SHA-256 inside an external profile proof is rejected");
        Check(checkedTravel.GetProperty("data").GetProperty("passed").GetBoolean() &&
              checkedTravel.GetProperty("data").GetProperty("uniqueDestination").GetBoolean() &&
              !checkedTravel.GetProperty("data").GetProperty("productionUnlockAllowed").GetBoolean() &&
              !checkedTravel.GetProperty("data").GetProperty("liveStoreWriterCertified").GetBoolean(),
            "exactly-once synthetic travel is consistent but production Store permission stays closed");
        // Six synthetic snapshots (5 maps per host at each of three phases)
        // prove A→B→A file placement and exact original loose-profile SHA.
        // No real ASA process or protected Kova path is ever used.
        await Task.Delay(20);
        File.Move(destinationFile,sourceFile);
        var returnedA=await Call(travelA.HandleAsync,"travel.capture");
        var returnedB=await Call(travelB.HandleAsync,"travel.capture");
        var roundtripRequest=new{
            request.beforeA,request.beforeB,
            middleA=request.afterA,middleB=request.afterB,
            afterA=returnedA.GetProperty("data").GetProperty("snapshot").GetString()!,
            afterB=returnedB.GetProperty("data").GetProperty("snapshot").GetString()!,
            request.sourceMap,request.destinationMap,request.profileId
        };
        var roundtrip=await Call(travelA.HandleAsync,
            "travel.compareRoundTrip",roundtripRequest);
        IsOk(roundtrip,"six-snapshot A-B-A native verifier accepts ordered two-host inventories");
        var roundtripData=roundtrip.GetProperty("data");
        Check(roundtripData.GetProperty("passed").GetBoolean()&&
              roundtripData.GetProperty("outwardPlacementVerified").GetBoolean()&&
              roundtripData.GetProperty("returnPlacementVerified").GetBoolean()&&
              roundtripData.GetProperty("exactOriginalProfileBytes").GetBoolean()&&
              roundtripData.GetProperty("unrelatedLooseFilesUnchanged").GetBoolean()&&
              !roundtripData.GetProperty("productionUnlockAllowed").GetBoolean()&&
              File.ReadAllBytes(sourceFile).SequenceEqual(survivor),
            "synthetic A-B-A maintains profile SHA and unrelated files without granting production Store permission");
        var replayedReturn=await Call(travelA.HandleAsync,"travel.compareRoundTrip",new{
            roundtripRequest.beforeA,roundtripRequest.beforeB,
            roundtripRequest.middleA,roundtripRequest.middleB,
            afterA=roundtripRequest.middleA,afterB=roundtripRequest.middleB,
            request.sourceMap,request.destinationMap,request.profileId
        });
        Check(IsError(replayedReturn,"UM-TRAVEL-EVIDENCE-UNVERIFIED"),
            "replayed return snapshots and non-increasing epochs are rejected before any acceptance");
        // Reappearing on both maps is not a valid return trip.
        File.WriteAllBytes(destinationFile,survivor);
        await Task.Delay(20);
        var duplicateReturnA=await Call(travelA.HandleAsync,"travel.capture");
        var duplicateReturnB=await Call(travelB.HandleAsync,"travel.capture");
        var duplicateReturn=await Call(travelA.HandleAsync,"travel.compareRoundTrip",new{
            roundtripRequest.beforeA,roundtripRequest.beforeB,
            roundtripRequest.middleA,roundtripRequest.middleB,
            afterA=duplicateReturnA.GetProperty("data").GetProperty("snapshot").GetString()!,
            afterB=duplicateReturnB.GetProperty("data").GetProperty("snapshot").GetString()!,
            request.sourceMap,request.destinationMap,request.profileId
        });
        Check(duplicateReturn.GetProperty("ok").GetBoolean()&&
              !duplicateReturn.GetProperty("data").GetProperty("passed").GetBoolean()&&
              !duplicateReturn.GetProperty("data").GetProperty("returnPlacementVerified").GetBoolean(),
            "duplicated survivor on both hosts fails the A-B-A return leg");
        File.Delete(destinationFile);
        // A different returned profile body must never silently count as exact
        // source byte parity, even when its placement is otherwise correct.
        File.WriteAllText(sourceFile,"synthetic-modified-profile-return-body");
        await Task.Delay(20);
        var modifiedReturnA=await Call(travelA.HandleAsync,"travel.capture");
        var modifiedReturnB=await Call(travelB.HandleAsync,"travel.capture");
        var alteredReturn=await Call(travelA.HandleAsync,"travel.compareRoundTrip",new{
            roundtripRequest.beforeA,roundtripRequest.beforeB,
            roundtripRequest.middleA,roundtripRequest.middleB,
            afterA=modifiedReturnA.GetProperty("data").GetProperty("snapshot").GetString()!,
            afterB=modifiedReturnB.GetProperty("data").GetProperty("snapshot").GetString()!,
            request.sourceMap,request.destinationMap,request.profileId
        });
        Check(alteredReturn.GetProperty("ok").GetBoolean()&&
              !alteredReturn.GetProperty("data").GetProperty("passed").GetBoolean()&&
              !alteredReturn.GetProperty("data").GetProperty("exactOriginalProfileBytes").GetBoolean(),
            "changed return-profile SHA is disclosed rather than certified as exact character-byte preservation");
        // Unrelated map's tribe/profile proof must not silently change.
        var tamperedOtherMap=System.Text.Json.Nodes.JsonNode.Parse(roundtripRequest.afterA)!.AsObject();
        tamperedOtherMap["maps"]!.AsArray()
            .First(m=>m!["name"]!.GetValue<string>()=="Valguero")!["files"]!
            .AsArray().Add(new System.Text.Json.Nodes.JsonObject{
                ["kind"]="tribe",["id"]=new string('c',64),
                ["sha256"]=new string('d',64),["bytes"]=123
            });
        var unrelatedChanged=await Call(travelA.HandleAsync,"travel.compareRoundTrip",new{
            roundtripRequest.beforeA,roundtripRequest.beforeB,
            roundtripRequest.middleA,roundtripRequest.middleB,
            afterA=tamperedOtherMap.ToJsonString(),roundtripRequest.afterB,
            request.sourceMap,request.destinationMap,request.profileId
        });
        Check(unrelatedChanged.GetProperty("ok").GetBoolean()&&
              !unrelatedChanged.GetProperty("data").GetProperty("passed").GetBoolean()&&
              !unrelatedChanged.GetProperty("data").GetProperty("unrelatedLooseFilesUnchanged").GetBoolean(),
            "unrelated-map profile/tribe metadata modification blocks strict A-B-A parity");
        File.WriteAllBytes(sourceFile,survivor);
        File.Move(sourceFile,destinationFile);
        File.WriteAllBytes(Path.Combine(targetSave,"zero.arkprofile"),Array.Empty<byte>());
        var brokenAfter=await Call(travelB.HandleAsync,"travel.capture");
        var zeroResult=await Call(travelA.HandleAsync,"travel.compare",
            new{request.beforeA,request.beforeB,request.afterA,
                afterB=brokenAfter.GetProperty("data").GetProperty("snapshot").GetString()!,
                request.sourceMap,request.destinationMap,request.profileId});
        IsOk(zeroResult,"four-snapshot zero-byte report is returned");
        Check(!zeroResult.GetProperty("data").GetProperty("passed").GetBoolean() &&
              zeroResult.GetProperty("data").GetProperty("zeroByteFiles").GetInt32()>0,
            "zero-byte profile fails two-host acceptance and never opens production Store");
        File.Delete(Path.Combine(targetSave,"zero.arkprofile"));
        File.WriteAllBytes(sourceFile,survivor);
        var duplicateAfter=await Call(travelA.HandleAsync,"travel.capture");
        var dup=await Call(travelA.HandleAsync,"travel.compare",
            new{request.beforeA,request.beforeB,
                afterA=duplicateAfter.GetProperty("data").GetProperty("snapshot").GetString()!,
                request.afterB,request.sourceMap,request.destinationMap,request.profileId});
        Check(!dup.GetProperty("data").GetProperty("passed").GetBoolean() &&
              !dup.GetProperty("data").GetProperty("uniqueDestination").GetBoolean(),
            "same character file on both hosts is detected as duplicate survivor");
        Check(File.ReadAllBytes(sourceFile).SequenceEqual(survivor) &&
              File.ReadAllBytes(destinationFile).SequenceEqual(survivor),
            "all acceptance probes remain read-only to synthetic character bytes");
        // Official ASA -usestore embeds survivor/tribe information inside
        // the .ark world. No standalone profile is required or trustworthy.
        // Snapshot the large world by bounded filesystem metadata only.
        foreach(var service in new[]{firstHost,secondHost})
            foreach(var storeMap in service.Config.LocalMaps)
            {
                storeMap.Arguments="-usestore";
                string key=tokens[storeMap.Name];
                File.WriteAllBytes(Path.Combine(savePaths[storeMap.Name],key+".ark"),
                    Encoding.UTF8.GetBytes("SYNTHETIC-EMBEDDED-WORLD-"+key));
            }
        var firstStoreMap=firstHost.Config.LocalMaps.Single(m=>m.Name=="The Island");
        var storePolicy=firstHost.Check(firstStoreMap);
        var physicalProcess=firstHost.ObserveLocalProcessesReadOnly(firstStoreMap);
        Check(!storePolicy.ConfirmedStopped &&
              physicalProcess.ConfirmedStopped && !physicalProcess.Running,
            "read-only Store process observation is distinct from intentionally blocked Store launch policy");
        bool originalStoreStartDenied=false;
        try{firstHost.LaunchFreshAsa(firstStoreMap.Id,operatorApproved:true);}
        catch(InvalidOperationException){originalStoreStartDenied=true;}
        Check(originalStoreStartDenied,
            "Store-v1 launch remains forbidden despite physically stopped read-only inventory");
        File.Delete(sourceFile);
        File.Delete(destinationFile);
        File.WriteAllBytes(Path.Combine(targetSave,"old-leftover.arkprofile"),
            Array.Empty<byte>());
        string originalStoreWorld=Path.Combine(sourceSave,"TheIsland_WP.ark");
        using(var concurrentStoreWriter=new FileStream(originalStoreWorld,
            FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite))
        {
            var conflictingWorld=await Call(travelA.HandleAsync,"travel.capture");
            Check(IsError(conflictingWorld,"UM-TRAVEL-EVIDENCE-UNVERIFIED"),
                "open external writer prevents even metadata-only -usestore world validation");
        }
        var storeBeforeA=await Call(travelA.HandleAsync,"travel.capture");
        var storeBeforeB=await Call(travelB.HandleAsync,"travel.capture");
        Check(storeBeforeA.GetProperty("data").GetProperty("complete").GetBoolean() &&
              storeBeforeB.GetProperty("data").GetProperty("complete").GetBoolean() &&
              storeBeforeB.GetProperty("data").GetProperty("maps").EnumerateArray()
                .All(x=>x.GetProperty("storeMode").GetBoolean() &&
                   x.GetProperty("worldPresent").GetBoolean()),
            "zero-byte legacy profile does not invalidate a stable -usestore .ark world snapshot");
        File.AppendAllText(Path.Combine(targetSave,"Ragnarok_WP.ark"),
            "-SYNTHETIC-EMBEDDED-CHARACTER-CHANGE");
        var storeAfterA=await Call(travelA.HandleAsync,"travel.capture");
        var storeAfterB=await Call(travelB.HandleAsync,"travel.capture");
        var storeResult=await Call(travelA.HandleAsync,"travel.compare",new{
            beforeA=storeBeforeA.GetProperty("data").GetProperty("snapshot").GetString()!,
            beforeB=storeBeforeB.GetProperty("data").GetProperty("snapshot").GetString()!,
            afterA=storeAfterA.GetProperty("data").GetProperty("snapshot").GetString()!,
            afterB=storeAfterB.GetProperty("data").GetProperty("snapshot").GetString()!,
            sourceMap=sourceMapId,destinationMap=destinationMapId,profileId="embedded"
        });
        IsOk(storeResult,"embedded Store-v1 world snapshots accepted for read-only metadata comparison");
        await Task.Delay(20);
        var storeReturnA=await Call(travelA.HandleAsync,"travel.capture");
        var storeReturnB=await Call(travelB.HandleAsync,"travel.capture");
        var embeddedRoundTrip=await Call(travelA.HandleAsync,"travel.compareRoundTrip",new{
            beforeA=storeBeforeA.GetProperty("data").GetProperty("snapshot").GetString()!,
            beforeB=storeBeforeB.GetProperty("data").GetProperty("snapshot").GetString()!,
            middleA=storeAfterA.GetProperty("data").GetProperty("snapshot").GetString()!,
            middleB=storeAfterB.GetProperty("data").GetProperty("snapshot").GetString()!,
            afterA=storeReturnA.GetProperty("data").GetProperty("snapshot").GetString()!,
            afterB=storeReturnB.GetProperty("data").GetProperty("snapshot").GetString()!,
            sourceMap=sourceMapId,destinationMap=destinationMapId,profileId="embedded"
        });
        IsOk(embeddedRoundTrip,"three-phase Store -usestore metadata reports without reading or modifying embedded survivor records");
        Check(embeddedRoundTrip.GetProperty("data").GetProperty("storeModeDetected").GetBoolean()&&
              !embeddedRoundTrip.GetProperty("data").GetProperty("passed").GetBoolean()&&
              !embeddedRoundTrip.GetProperty("data").GetProperty("embeddedSurvivorVerified").GetBoolean()&&
              !embeddedRoundTrip.GetProperty("data").GetProperty("liveStoreWriterCertified").GetBoolean(),
            "embedded Store A-B-A snapshots never fabricate survivor, writer or real gameplay certification");

        var storeData=storeResult.GetProperty("data");
        Check(storeData.GetProperty("storeModeDetected").GetBoolean() &&
              storeData.GetProperty("worldFilesPresentForStore").GetBoolean() &&
              !storeData.GetProperty("passed").GetBoolean() &&
              !storeData.GetProperty("embeddedSurvivorVerified").GetBoolean() &&
              !storeData.GetProperty("gameClientRejoinVerified").GetBoolean() &&
              !storeData.GetProperty("productionUnlockAllowed").GetBoolean() &&
              !storeData.GetProperty("findings").EnumerateArray()
                .Any(x=>x.GetString()!.Contains("beágyaz",StringComparison.OrdinalIgnoreCase)) &&
              !storeData.GetProperty("findings").EnumerateArray()
                .Any(x=>x.GetString()!.Contains("hiányzik az érvényes karakterfájl",
                    StringComparison.OrdinalIgnoreCase)),
            "Store-v1 never invents lost survivor from absent loose .arkprofile or claims real travel");

    }

    // Kova-compatible *real file operations* against a disposable stopped
    // fake-map installation. These mutate only CI TEMP data, never the
    // production 10-map cluster, Store-v1 UNC or any MariaDB database.
    {
        string localInstall=Path.Combine(root,"world-backup-ci-install");
        string localWorld=Path.Combine(localInstall,"ShooterGame","Saved",
            "SavedArks","TheIsland_WP");
        Directory.CreateDirectory(localWorld);
        string arkFile=Path.Combine(localWorld,"TheIsland_WP.ark");
        string charFile=Path.Combine(localWorld,"76561198000000001.arkprofile");
        string tribeFile=Path.Combine(localWorld,"4411.arktribe");
        byte[] worldBeforeBackup=Encoding.UTF8.GetBytes("SYNTHETIC WORLD V1");
        byte[] survivorBeforeBackup=Encoding.UTF8.GetBytes("SYNTHETIC PROFILE V1");
        File.WriteAllBytes(arkFile,worldBeforeBackup);
        File.WriteAllBytes(charFile,survivorBeforeBackup);
        File.WriteAllText(tribeFile,"TEST TRIBE");
        string localProfile=Path.Combine(root,"world-backup-ci-profile");
        bool stopped=true;
        await using var backupHost=new ClusterService("world-backup-ci",
            localProfile,_=>new ProcessCheck(!stopped,
                stopped?"synthetic stopped":"synthetic running",
                stopped?null:new ProcessIdentity{
                    Pid=23563,ExecutablePath="synthetic.exe",
                    StartedUtc=DateTimeOffset.UtcNow
                },stopped));
        var localMap=new LocalMap {Id=Guid.NewGuid(),Name="The Island",
            InstallationPath=localInstall,
            ExecutablePath=Path.Combine(localInstall,"fake.exe")};
        backupHost.AddMap(localMap);
        var worldBridge=new DesktopWorldBackupBridge(backupHost,
            _=>new ProcessCheck(!stopped,
                stopped?"synthetic stopped":"synthetic running",
                stopped?null:new ProcessIdentity{
                    Pid=23563,ExecutablePath="synthetic.exe",
                    StartedUtc=DateTimeOffset.UtcNow
                },stopped));
        var empty=await Call(worldBridge.HandleAsync,"backup.history",
            new{mapId=localMap.Id});
        IsOk(empty,"initial genuine map backup archive is readable");
        Check(empty.GetProperty("data").GetProperty("points").GetArrayLength()==0,
            "world backup history starts empty");
        var impossible=await Call(worldBridge.HandleAsync,"backup.preview",
            new{mapId=Guid.NewGuid()});
        Check(IsError(impossible,"UM-BACKUP-NATIVE-SAFETY-CHECK-FAILED"),
            "foreign map does not receive backup capability");
        stopped=false;
        var busy=await Call(worldBridge.HandleAsync,"backup.preview",
            new{mapId=localMap.Id});
        Check(IsError(busy,"UM-BACKUP-NATIVE-SAFETY-CHECK-FAILED"),
            "running map cannot be snapshotted as stopped world");
        stopped=true;
        var preview=await Call(worldBridge.HandleAsync,"backup.preview",
            new{mapId=localMap.Id});
        IsOk(preview,"real stopped-world backup preview prepared");
        string ticket=preview.GetProperty("data").GetProperty("token").GetString()!;
        var noConfirm=await Call(worldBridge.HandleAsync,"backup.create",
            new{token=ticket,confirm=false});
        Check(IsError(noConfirm,"UM-BACKUP-EXPLICIT-APPROVAL-REQUIRED"),
            "native creation demands explicit operator confirmation");
        var saved=await Call(worldBridge.HandleAsync,"backup.create",
            new{token=ticket,confirm=true});
        IsOk(saved,"Kova-style local world backup created and verified");
        string point=saved.GetProperty("data").GetProperty("point").GetString()!;
        Check(saved.GetProperty("data").GetProperty("backupVerified").GetBoolean() &&
              saved.GetProperty("data").GetProperty("characters").GetInt32()==1 &&
              saved.GetProperty("data").GetProperty("tribes").GetInt32()==1 &&
              File.ReadAllBytes(arkFile).SequenceEqual(worldBeforeBackup) &&
              File.ReadAllBytes(charFile).SequenceEqual(survivorBeforeBackup),
            "actual save package includes world/profile/tribe and leaves source bytes unchanged");
        var replay=await Call(worldBridge.HandleAsync,"backup.create",
            new{token=ticket,confirm=true});
        Check(IsError(replay,"UM-BACKUP-EXPLICIT-APPROVAL-REQUIRED"),
            "backup creation token cannot replay");
        var pointProof=UntamedWorldBackup.Verify(localMap,localProfile,point);
        Check(pointProof.Files==3 &&
              pointProof.Bytes==worldBeforeBackup.Length+survivorBeforeBackup.Length+"TEST TRIBE".Length,
            "saved manifest contents are cryptographically re-verified from disk");
        File.WriteAllBytes(arkFile,Encoding.UTF8.GetBytes("SYNTHETIC WORLD V2"));
        File.WriteAllBytes(charFile,Encoding.UTF8.GetBytes("SYNTHETIC PROFILE V2"));
        var restorePreview=await Call(worldBridge.HandleAsync,"backup.restorePreview",
            new{mapId=localMap.Id,point});
        IsOk(restorePreview,"selected verified world backup can be restored under stopped local profile");
        string restoreToken=restorePreview.GetProperty("data").GetProperty("token").GetString()!;
        var wrongName=await Call(worldBridge.HandleAsync,"backup.restore",
            new{token=restoreToken,mapName="Aberration",confirm=true});
        Check(IsError(wrongName,"UM-BACKUP-MAP-NAME-CONFIRMATION-REQUIRED"),
            "map-name confirmation required for world-changing restore");
        var restored=await Call(worldBridge.HandleAsync,"backup.restore",
            new{token=restoreToken,mapName="The Island",confirm=true});
        // Explicit preview is one-use even if operator first typed a wrong
        // map name: this must be repeated before real restore.
        Check(IsError(restored,"UM-BACKUP-EXPLICIT-APPROVAL-REQUIRED"),
            "invalid confirmation consumes a restore ticket");
        restorePreview=await Call(worldBridge.HandleAsync,"backup.restorePreview",
            new{mapId=localMap.Id,point});
        restoreToken=restorePreview.GetProperty("data").GetProperty("token").GetString()!;
        restored=await Call(worldBridge.HandleAsync,"backup.restore",
            new{token=restoreToken,mapName="The Island",confirm=true});
        IsOk(restored,"real map world restore finishes with protective backup");
        var restoreJournal=new UntamedOperationCoordinator().Observe(localInstall);
        Check(restoreJournal?.Kind==UntamedOperationKind.Restore&&
              restoreJournal.Phase==UntamedOperationPhase.Completed,
            "completed world directory swap is durably journaled as Restore, not a misleading Backup");
        Check(File.ReadAllBytes(arkFile).SequenceEqual(worldBeforeBackup) &&
              File.ReadAllBytes(charFile).SequenceEqual(survivorBeforeBackup) &&
              restored.GetProperty("data").GetProperty("oldWorldRetained").GetBoolean() &&
              restored.GetProperty("data").GetProperty("bytesRestoredAndHashesVerified").GetBoolean() &&
              !restored.GetProperty("data").GetProperty("worldRestoreVerified").GetBoolean() &&
              !restored.GetProperty("data").GetProperty("gameClientLoadVerified").GetBoolean() &&
              !restored.GetProperty("data").GetProperty("tribeOwnershipVerified").GetBoolean() &&
              !restored.GetProperty("data").GetProperty("characterRejoinVerified").GetBoolean(),
            "restored world/profile bytes match and prior world is preserved; no fictitious ASA game/tribe/client proof");
        string protective=restored.GetProperty("data").GetProperty("protectiveBackup").GetString()!;
        var protectiveProof=UntamedWorldBackup.Verify(localMap,localProfile,protective);
        Check(protectiveProof.Kind=="protective",
            "old live world is preserved in a separately verified protected snapshot");
        var retainedWorlds=Directory.EnumerateDirectories(
            Path.Combine(localInstall,"ShooterGame","Saved","SavedArks"),
            ".untamed-previous-TheIsland_WP-*").ToArray();
        Check(retainedWorlds.Length==1 &&
              File.ReadAllText(Path.Combine(retainedWorlds[0],"TheIsland_WP.ark"))==
                  "SYNTHETIC WORLD V2" &&
              File.ReadAllText(Path.Combine(retainedWorlds[0],"76561198000000001.arkprofile"))==
                  "SYNTHETIC PROFILE V2",
            "restored older world keeps a directly accessible byte-for-byte previous world and profile");
        var recentWorldPoints=UntamedWorldBackup.List(localMap,localProfile,2);
        Check(recentWorldPoints.Length==2 &&
              recentWorldPoints[0].Id==protective &&
              recentWorldPoints[1].Id==point,
            "backup history orders verified packages by real creation time, never random GUID order");
        // A world restore package is operator-selectable untrusted local
        // metadata. Corrupt packages must never be selectable, restorable or
        // cause an uncaught NullReferenceException. All changes below occur
        // only in NEW synthetic backup folders, not a current ASA world.
        string packageRoot=Path.Combine(localProfile,"world-backups",
            localMap.Id.ToString("N"));
        string originalManifest=File.ReadAllText(
            Path.Combine(packageRoot,point,"manifest.json"));
        void RejectManifest(string label,Action<System.Text.Json.Nodes.JsonObject> mutate)
        {
            var candidate=Guid.NewGuid().ToString("D");
            string folder=Path.Combine(packageRoot,candidate);
            Directory.CreateDirectory(folder);
            var payload=System.Text.Json.Nodes.JsonNode.Parse(originalManifest)!.AsObject();
            mutate(payload);
            File.WriteAllText(Path.Combine(folder,"manifest.json"),payload.ToJsonString());
            File.WriteAllText(Path.Combine(folder,"complete"),"1");
            bool denied=false;
            try{UntamedWorldBackup.Verify(localMap,localProfile,candidate);}
            catch(InvalidDataException){denied=true;}
            Check(denied,label+" rejected before full world restore/hash scan");
            Check(!UntamedWorldBackup.List(localMap,localProfile,50)
                .Any(x=>x.Id==candidate),
                label+" cannot appear in selectable backup history");
            Directory.Delete(folder,true);
        }
        RejectManifest("null world backup files collection",x=>x["files"]=null);
        RejectManifest("null world backup file entry",
            x=>x["files"]!.AsArray()[0]=null);
        RejectManifest("null SHA-256 backup digest",
            x=>x["files"]![0]!["sha256"]=null);
        RejectManifest("null relative file path",
            x=>x["files"]![0]!["relative"]=null);
        {
            string forgedId=Guid.NewGuid().ToString("D");
            string folder=Path.Combine(packageRoot,forgedId);
            Directory.CreateDirectory(folder);
            string m=Path.Combine(folder,"manifest.json");
            string marker=Path.Combine(folder,"complete");
            File.WriteAllText(m,originalManifest);
            File.WriteAllText(marker,"INVALID");
            bool notCommitted=false;
            try{UntamedWorldBackup.Verify(localMap,localProfile,forgedId);}
            catch(InvalidDataException){notCommitted=true;}
            Check(notCommitted,
                "incorrect backup completion marker never authenticates a world package");
            File.WriteAllText(marker,"1");
            using(var oversized=new FileStream(m,FileMode.Open,
                FileAccess.Write,FileShare.None))
                oversized.SetLength(12_000_001);
            bool hugeDenied=false;
            try{UntamedWorldBackup.Verify(localMap,localProfile,forgedId);}
            catch(InvalidDataException){hugeDenied=true;}
            Check(hugeDenied,
                "oversized world backup manifest denied before JSON allocation");
            File.WriteAllText(m,originalManifest);
            using(var oversized=new FileStream(marker,FileMode.Open,
                FileAccess.Write,FileShare.None))
                oversized.SetLength(17);
            bool markerDenied=false;
            try{UntamedWorldBackup.Verify(localMap,localProfile,forgedId);}
            catch(InvalidDataException){markerDenied=true;}
            Check(markerDenied,
                "oversized world backup complete marker denied");
            Directory.Delete(folder,true);
        }
        Check(UntamedWorldBackup.Verify(localMap,localProfile,point).Files==3 &&
              File.ReadAllBytes(arkFile).SequenceEqual(worldBeforeBackup) &&
              File.ReadAllBytes(charFile).SequenceEqual(survivorBeforeBackup),
            "original verified backup and restored world/player bytes unchanged after negative metadata tests");
        // Real scheduled stopped-world backups are disabled by default.
        // Every claim is durable and cannot replay after process restart.
        // Synthetic local installation/profile ONLY; never a live ASA map.
        {
            DateTimeOffset scheduleNow=DateTimeOffset.UtcNow;
            using var scheduled=new DesktopScheduledStoppedBackupSupervisor(backupHost,
                _=>new ProcessCheck(!stopped,
                    stopped?"synthetic stopped":"synthetic running",
                    stopped?null:new ProcessIdentity{
                        Pid=23563,ExecutablePath="synthetic.exe",
                        StartedUtc=scheduleNow
                    },stopped));
            Check(scheduled.Snapshot().Length==0,
                "automatic world backups are opt-in, never default enabled");
            var scheduleBridge=new DesktopWorldBackupBridge(backupHost,
                _=>new ProcessCheck(!stopped,
                    stopped?"synthetic stopped":"synthetic running",
                    stopped?null:new ProcessIdentity{
                        Pid=23563,ExecutablePath="synthetic.exe",
                        StartedUtc=scheduleNow
                    },stopped),scheduled);
            var scheduleUnauthorized=await Call(scheduleBridge.HandleAsync,
                "backup.scheduleCreate",new{mapId=localMap.Id,intervalMinutes=60,
                    confirm=false});
            Check(IsError(scheduleUnauthorized,"UM-BACKUP-EXPLICIT-APPROVAL-REQUIRED"),
                "scheduling requires explicit native confirmation");
            var scheduleConfigured=scheduled.Configure(localMap.Id,60,scheduleNow);
            Check(scheduleConfigured.Enabled&&
                  scheduleConfigured.NextDueUtc==scheduleNow.AddMinutes(60),
                "enabled stopped-world schedule has a persisted future due time");
            stopped=false;
            var runningResult=scheduled.TickOnce(scheduleNow.AddMinutes(61));
            Check(runningResult.Single().LastStatus=="not-confirmed-stopped" &&
                  runningResult.Single().LastPoint==null,
                "running ASA cannot be silently stopped or auto-backed-up");
            stopped=true;
            scheduleConfigured=scheduled.Configure(localMap.Id,60,scheduleNow);
            var completed=scheduled.TickOnce(scheduleNow.AddMinutes(61)).Single();
            Check(completed.LastStatus=="verified-backup-created" &&
                  completed.LastPoint!=null &&
                  UntamedWorldBackup.Verify(localMap,localProfile,
                      completed.LastPoint).Files==3 &&
                  File.ReadAllBytes(arkFile).SequenceEqual(worldBeforeBackup) &&
                  File.ReadAllBytes(charFile).SequenceEqual(survivorBeforeBackup),
                "due stopped-world job copies real world/profile/tribe without changing originals");
            var sameSlot=scheduled.TickOnce(scheduleNow.AddMinutes(61)).Single();
            Check(sameSlot.LastPoint==completed.LastPoint,
                "claimed scheduled backup slot cannot execute twice");
            using(var restarted=new DesktopScheduledStoppedBackupSupervisor(backupHost,
                _=>new ProcessCheck(false,"synthetic stopped",null,true)))
            {
                Check(restarted.Snapshot().Single().LastPoint==completed.LastPoint,
                    "job receipt and due cursor survive Manager process recreation");
                Check(restarted.Disable(localMap.Id).Enabled==false,
                    "operator can durably disable scheduled backups");
            }
            var disabled=scheduled.TickOnce(scheduleNow.AddHours(3)).Single();
            Check(!disabled.Enabled && disabled.LastPoint==completed.LastPoint,
                "disabled schedule never replays or deletes verified backup");
            localMap.Arguments="-usestore";
            bool autoStoreDenied=false;
            try{scheduled.Configure(localMap.Id,60,scheduleNow);}
            catch(UnauthorizedAccessException){autoStoreDenied=true;}
            Check(autoStoreDenied,
                "historical/active Store-v1 character world cannot be scheduled for autonomous copying");
            localMap.Arguments="";
            var dueBeforeMissed=scheduled.Configure(localMap.Id,60,scheduleNow);
            var missed=scheduled.TickOnce(scheduleNow.AddHours(4)).Single();
            Check(missed.LastStatus=="missed-no-catchup"&&
                  missed.LastPoint==null &&
                  missed.NextDueUtc>scheduleNow.AddHours(4),
                "missed backup windows are consumed durably without replay");
            // Revoking an active schedule in a native stop-state callback
            // must abort the copy, not publish a late snapshot.
            int archiveCountBefore=UntamedWorldBackup.List(localMap,localProfile,50).Length;
            DesktopScheduledStoppedBackupSupervisor? revoker=null;
            int stopProbes=0;
            using(revoker=new DesktopScheduledStoppedBackupSupervisor(backupHost,
                _=>{
                    if(++stopProbes==2)revoker!.Disable(localMap.Id);
                    return new ProcessCheck(false,"synthetic stopped",null,true);
                }))
            {
                revoker.Configure(localMap.Id,60,scheduleNow);
                var revoked=revoker.TickOnce(scheduleNow.AddMinutes(61)).Single();
                Check(!revoked.Enabled&&revoked.LastStatus=="disabled" &&
                      revoked.LastPoint==null&&stopProbes>=2 &&
                      UntamedWorldBackup.List(localMap,localProfile,50).Length==archiveCountBefore,
                    "operator disable mid-copy blocks publication and preserves disabled state");
            }
        }
        File.WriteAllBytes(Path.Combine(localWorld,"broken.arkprofile"),Array.Empty<byte>());
        preview=await Call(worldBridge.HandleAsync,"backup.preview",
            new{mapId=localMap.Id});
        ticket=preview.GetProperty("data").GetProperty("token").GetString()!;
        var blocked=await Call(worldBridge.HandleAsync,"backup.create",
            new{token=ticket,confirm=true});
        Check(IsError(blocked,"UM-BACKUP-NATIVE-SAFETY-CHECK-FAILED"),
            "zero-byte local arkprofile prevents corrupt backup publication");
        File.Delete(Path.Combine(localWorld,"broken.arkprofile"));
        localMap.Arguments="-usestore";
        // Unlike non-Store saves, .arkprofile and .arktribe may be stale or
        // zero-byte loose leftovers after official ASA -usestore conversion.
        // Their bytes must be preserved, never automatically repaired/deleted.
        string zeroLegacy=Path.Combine(localWorld,"converted-empty.arkprofile");
        File.WriteAllBytes(zeroLegacy,Array.Empty<byte>());
        var storePoint=UntamedWorldBackup.Create(localMap,localProfile,
            ()=>new ProcessCheck(false,"synthetic stopped Store",null,true));
        var storeVerified=UntamedWorldBackup.Verify(localMap,localProfile,storePoint.Id);
        Check(storeVerified.Files==4 && storeVerified.Characters==2 &&
              File.Exists(zeroLegacy)&&new FileInfo(zeroLegacy).Length==0,
            "stopped Store-v1 backup preserves zero-byte historical loose profiles byte-for-byte");
        File.Delete(zeroLegacy);
        byte[] validWorld=File.ReadAllBytes(arkFile);
        File.WriteAllBytes(arkFile,Array.Empty<byte>());
        bool emptyWorldDenied=false;
        try{UntamedWorldBackup.Create(localMap,localProfile,
            ()=>new ProcessCheck(false,"synthetic stopped Store",null,true));}
        catch(InvalidDataException){emptyWorldDenied=true;}
        Check(emptyWorldDenied,"zero-byte .ark world hard-blocks even Store-v1 backup publication");
        File.WriteAllBytes(arkFile,validWorld);
        var unsafeRestore=await Call(worldBridge.HandleAsync,"backup.restorePreview",
            new{mapId=localMap.Id,point});
        Check(IsError(unsafeRestore,"UM-BACKUP-STORE-V1-RESTORE-LOCKED"),
            "live Store-v1 character map cannot be rolled back unilaterally");
        localMap.Arguments="";
        localMap.HistoricalMapKey="TheIsland_WP";
        unsafeRestore=await Call(worldBridge.HandleAsync,"backup.restorePreview",
            new{mapId=localMap.Id,point});
        Check(IsError(unsafeRestore,"UM-BACKUP-STORE-V1-RESTORE-LOCKED"),
            "historically imported map cannot evade Store-v1 guard with blank launch arguments");
        localMap.HistoricalMapKey="";
        localMap.StoreTestRoot=Path.Combine(root,"historical-store-sandbox");
        unsafeRestore=await Call(worldBridge.HandleAsync,"backup.restorePreview",
            new{mapId=localMap.Id,point});
        Check(IsError(unsafeRestore,"UM-BACKUP-STORE-V1-RESTORE-LOCKED"),
            "configured Store root prevents unilateral world restore");
        localMap.StoreTestRoot="";
        localMap.LastProcess=new ProcessIdentity{
            Pid=999999,StartedUtc=DateTimeOffset.UtcNow.AddHours(-1),
            ExecutablePath=localMap.ExecutablePath,Arguments="-usestore"
        };
        unsafeRestore=await Call(worldBridge.HandleAsync,"backup.restorePreview",
            new{mapId=localMap.Id,point});
        Check(IsError(unsafeRestore,"UM-BACKUP-STORE-V1-RESTORE-LOCKED"),
            "last observed Store-v1 launch remains protected even if current config was stripped");
        localMap.LastProcess=null;
        File.WriteAllText(Path.Combine(localWorld,"TheIsland_WP.ark"),"TAMPERED");
        // The archived original is unaffected by source changes.
        Check(UntamedWorldBackup.Verify(localMap,localProfile,point).Files==3,
            "source tampering cannot modify verified archive bytes");
        var forged=await Call(worldBridge.HandleAsync,"backup.restorePreview",
            new{mapId=localMap.Id,point=Guid.NewGuid().ToString("D")});
        Check(IsError(forged,"UM-BACKUP-NATIVE-SAFETY-CHECK-FAILED"),
            "unknown archive ID is rejected without writing world or Store");

        // A failure immediately before the directory swap must not alter
        // current world/character bytes or leave a misleading recovery marker.
        bool rejectedBeforeSwap=false;
        try
        {
            UntamedWorldBackup.Restore(localMap,localProfile,point,
                ()=>new ProcessCheck(false,"synthetic stopped",null,true),
                ()=>{throw new IOException("Injected before-rename failure");});
        }
        catch(IOException){rejectedBeforeSwap=true;}
        var worldParent=Path.Combine(localInstall,"ShooterGame","Saved","SavedArks");
        Check(rejectedBeforeSwap &&
              File.ReadAllText(arkFile)=="TAMPERED" &&
              !File.Exists(Path.Combine(worldParent,
                  ".untamed-restore-TheIsland_WP.json")) &&
              !Directory.EnumerateDirectories(worldParent,".untamed-stage-*").Any(),
            "pre-swap failure leaves live world intact, removes stage and does not fake a crash recovery marker");
        var deniedRestoreJournal=new UntamedOperationCoordinator().Observe(localInstall);
        Check(deniedRestoreJournal?.Kind==UntamedOperationKind.Restore&&
              deniedRestoreJournal.Phase==UntamedOperationPhase.CancelledBeforeMutation,
            "denied pre-swap restore remains a non-mutated Restore operation in durable journal");

        string crashMarker=Path.Combine(worldParent,
            ".untamed-restore-TheIsland_WP.json");
        File.WriteAllText(crashMarker,"synthetic crash recovery marker");
        var recoverable=await Call(worldBridge.HandleAsync,"backup.history",
            new{mapId=localMap.Id});
        IsOk(recoverable,"restore recovery warning is discoverable without modifying the marked world");
        Check(recoverable.GetProperty("data").GetProperty("recoveryRequired").GetBoolean(),
            "backup history exposes interrupted restore marker to operator");
        var preflightDuringRecovery=await Call(worldBridge.HandleAsync,"backup.preview",
            new{mapId=localMap.Id});
        Check(IsError(preflightDuringRecovery,"UM-BACKUP-RESTORE-RECOVERY-REQUIRED"),
            "native world backup refuses approval while crash marker is present");
        File.Delete(crashMarker);
        Check(!UntamedWorldBackup.RecoveryRequired(localMap),
            "synthetic recovery marker removal returns map to no-recovery state");

        // Two local ASA map profiles can legitimately share the same
        // SteamCMD installation if their SavedArks/<MapKey> folders differ.
        // Both must remain independently selectable, never overwrite each
        // other's world data or create ambiguous shared folder backups.
        string centerWorld=Path.Combine(localInstall,"ShooterGame","Saved",
            "SavedArks","TheCenter_WP");
        Directory.CreateDirectory(centerWorld);
        byte[] centerBytes=Encoding.UTF8.GetBytes("SYNTHETIC CENTER WORLD");
        File.WriteAllBytes(Path.Combine(centerWorld,"TheCenter_WP.ark"),centerBytes);
        var centerMap=new LocalMap{Id=Guid.NewGuid(),Name="The Center",
            InstallationPath=localInstall,
            ExecutablePath=Path.Combine(localInstall,"fake.exe")};
        backupHost.AddMap(centerMap);
        var sharedFirst=await Call(worldBridge.HandleAsync,"backup.preview",
            new{mapId=localMap.Id});
        IsOk(sharedFirst,"The Island backup can be prepared in shared ASA SteamCMD install");
        var sharedSecond=await Call(worldBridge.HandleAsync,"backup.preview",
            new{mapId=centerMap.Id});
        IsOk(sharedSecond,"The Center has its own independent world backup in shared install");
        string islandBackupToken=sharedFirst.GetProperty("data").GetProperty("token").GetString()!;
        string centerBackupToken=sharedSecond.GetProperty("data").GetProperty("token").GetString()!;
        Check(islandBackupToken!=centerBackupToken,
            "two stopped map cards retain distinct backup approval tokens");
        var firstSharedBackup=await Call(worldBridge.HandleAsync,
            "backup.create",new{token=islandBackupToken,confirm=true});
        IsOk(firstSharedBackup,
            "first map's backup token survives an independent second-map preview");
        var secondSharedBackup=await Call(worldBridge.HandleAsync,
            "backup.create",new{token=centerBackupToken,confirm=true});
        IsOk(secondSharedBackup,
            "second map's backup token survives the first map's committed archive");
        Check(firstSharedBackup.GetProperty("data").GetProperty("mapId").GetGuid()==localMap.Id &&
              secondSharedBackup.GetProperty("data").GetProperty("mapId").GetGuid()==centerMap.Id &&
              firstSharedBackup.GetProperty("data").GetProperty("point").GetString()!=
                secondSharedBackup.GetProperty("data").GetProperty("point").GetString(),
            "shared-install ASA maps receive separate verified world backup package IDs");
        var reusedIsland=await Call(worldBridge.HandleAsync,"backup.create",
            new{token=islandBackupToken,confirm=true});
        Check(IsError(reusedIsland,"UM-BACKUP-EXPLICIT-APPROVAL-REQUIRED"),
            "completed backup tokens cannot replay after another map's backup");
        Check(File.ReadAllBytes(Path.Combine(centerWorld,"TheCenter_WP.ark"))
                .SequenceEqual(centerBytes),
            "first map backup cannot alter neighboring map world bytes");
        string originalKovaMarker=Path.Combine(worldParent,
            ".kova-restore-TheIsland_WP.json");
        File.WriteAllText(originalKovaMarker,"synthetic interrupted Kova backup marker");
        Check(UntamedWorldBackup.InstallationRecoveryRequired(centerMap),
            "another map's original Kova restore marker blocks shared installation");
        var centerBlocked=await Call(worldBridge.HandleAsync,"backup.preview",
            new{mapId=centerMap.Id});
        Check(IsError(centerBlocked,"UM-BACKUP-RESTORE-RECOVERY-REQUIRED"),
            "native manager refuses Center snapshot while Island Kova restore needs recovery");
        File.Delete(originalKovaMarker);
        Check(!UntamedWorldBackup.InstallationRecoveryRequired(centerMap),
            "cleared synthetic Kova marker restores normal shared-map preflight");
    }

    // Update Center: deterministic provider response; no remote download,
    // installer launch or server mutation in this test.
    {
        var release=new UpdateRelease(new Version(9,0,0),"v9.0.0-obsidian-preview.2",
            "Synthetic Obsidian test release; do not install.",
            "https://github.com/Gunter1122cloud/untamed-manager-releases/releases/download/v9.0.0-obsidian-preview.2/Untamed_Obsidian_Desktop_Preview_Setup.exe",
            new string('a',64),"Untamed_Obsidian_Desktop_Preview_Setup.exe");
        var update=new DesktopUpdateStatusBridge((_,_)=>Task.FromResult<UpdateRelease?>(release));
        var status=await Call(update.HandleAsync,"updates.status");
        IsOk(status,"local Update Center exposes installed release identity without network");
        Check(!status.GetProperty("data").GetProperty("installationPermitted").GetBoolean() &&
              !status.GetProperty("data").GetProperty("updaterVerified").GetBoolean(),
            "local update status does not invent verified updater privileges");
        var found=await Call(update.HandleAsync,"updates.check",new{channel="preview"});
        IsOk(found,"official release metadata checker resolves trusted static repo");
        Check(found.GetProperty("data").GetProperty("found").GetBoolean() &&
              found.GetProperty("data").GetProperty("tag").GetString()=="v9.0.0-obsidian-preview.2" &&
              !found.GetProperty("data").GetProperty("installationPermitted").GetBoolean(),
            "update check cannot install even when newer release metadata is present");
        var invalid=await Call(update.HandleAsync,"updates.check",new{channel="invalid"});
        Check(IsError(invalid,"UM-UPDATE-CHECK-FAILED"),
            "malformed release channel is denied, not redirected to untrusted API");
        var untrusted=new DesktopUpdateStatusBridge((_,_)=>Task.FromResult<UpdateRelease?>(
            release with{AssetUrl="https://example.org/Untamed_Obsidian_Desktop_Preview_Setup.exe"}));
        var blocked=await Call(untrusted.HandleAsync,"updates.check",new{channel="stable"});
        Check(IsError(blocked,"UM-UPDATE-UNTRUSTED-RELEASE"),
            "untrusted release asset domain fails closed without staging an installer");
    }

    // A large installer download must not freeze unrelated local map status
    // and RCON RPCs. Final update handoff still waits for serialized writes.
    {
        const string requestId="4d7892e0-2d8f-4ca1-b49d-240352481a61";
        string envelope=ObsidianWindow.NativeErrorResponse(
            "{\"id\":\""+requestId+"\",\"method\":\"travel.compare\"}");
        using var nativeError=JsonDocument.Parse(envelope);
        Check(nativeError.RootElement.GetProperty("id").GetString()==requestId &&
              nativeError.RootElement.GetProperty("ok").GetBoolean()==false &&
              nativeError.RootElement.GetProperty("error").GetString()==
                "UM-NATIVE-UNEXPECTED-FAILURE" &&
              !envelope.Contains("travel.compare",StringComparison.Ordinal),
            "unhandled native RPC exception retains only the request ID and redacted error");
        string malformed=ObsidianWindow.NativeErrorResponse("{not-json");
        using var malformedError=JsonDocument.Parse(malformed);
        Check(malformedError.RootElement.GetProperty("id").ValueKind==
                JsonValueKind.Null &&
              !malformed.Contains("not-json",StringComparison.Ordinal),
            "malformed native WebView payload cannot leak details or kill the UI");
    }
    // WebView2 never loads a stale prior-release index.html from the
    // persistent host profile; URL scoping cannot escape the trusted origin.
    {
        string oldUrl=ObsidianWindow.ReleaseScopedUiUrl(
            "v2.0.0-obsidian-preview.2031");
        string newUrl=ObsidianWindow.ReleaseScopedUiUrl(
            "v2.0.0-obsidian-preview.2035");
        string malformed=ObsidianWindow.ReleaseScopedUiUrl(
            "https://attacker.example/x?evil=true");
        Check(oldUrl!=newUrl&&
              oldUrl.StartsWith("https://untamed-app.example/index.html?release=",
                  StringComparison.Ordinal)&&
              newUrl.StartsWith("https://untamed-app.example/index.html?release=",
                  StringComparison.Ordinal)&&
              malformed=="https://untamed-app.example/index.html?release=unversioned",
            "packaged React startup always stays local and changes URL when native release changes");
    }
    Check(!ObsidianWindow.RequiresGlobalBridgeGate("updates.prepare") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("store.inspectCopy") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("store.inspectArchive") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("store.compareArchives") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("dashboard.snapshot") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("servers.runningLocal") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("rcon.listPlayers") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("rcon.gameLog") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("servers.restartStatus") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("historical.restartStatus") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("historical.gracefulStopStatus") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("schedule.nativeStatus") &&
          ObsidianWindow.RequiresGlobalBridgeGate("updates.install") &&
          ObsidianWindow.RequiresGlobalBridgeGate("servers.restartApply") &&
          ObsidianWindow.RequiresGlobalBridgeGate("servers.gracefulStopApply") &&
          ObsidianWindow.RequiresGlobalBridgeGate("rcon.saveWorld") &&
          ObsidianWindow.RequiresGlobalBridgeGate("historical.restartApply") &&
          ObsidianWindow.RequiresGlobalBridgeGate("schedule.nativeSet") &&
          ObsidianWindow.RequiresGlobalBridgeGate(null),
        "read-only dashboard, RCON and lifecycle status bypass slow UI mutation gate without unlocking any writes");

    // Obsidian catalog must never accept a WPF binary even if its release
    // digest, version and GitHub repository are otherwise correct.
    {
        string asset=ObsidianReleaseCatalog.Installer;
        string tag="v8.2.0-obsidian-preview.3";
        string trustedUrl="https://github.com/"+ObsidianReleaseCatalog.Repository+
            "/releases/download/"+tag+"/"+asset;
        string releaseJson=JsonSerializer.Serialize(new[]{
            new{tag_name="v8.2.0-wpf-preview.1",draft=false,prerelease=true,
                assets=new[]{new{name="UntamedManager-8.2.0-SETUP.exe",
                    browser_download_url="https://github.com/"+ObsidianReleaseCatalog.Repository+
                        "/releases/download/v8.2.0-wpf-preview.1/UntamedManager-8.2.0-SETUP.exe",
                    digest="sha256:"+new string('1',64)}},body="WRONG WPF INSTALLER"},
            new{tag_name=tag,draft=false,prerelease=true,
                assets=new[]{new{name=asset,browser_download_url=trustedUrl,
                    digest="sha256:"+new string('f',64)}},
                body="Synthetic preview release"}
        });
        // GitHub metadata can contain malformed assets from unrelated tags.
        // Ignore them independently; one wrong JSON field must never hide a
        // later properly validated Obsidian release.
        string mixedJson=JsonSerializer.Serialize(new object[]{
            new{tag_name="v8.2.0-obsidian-preview.9",draft=false,prerelease=true,
                assets=new object[]{
                    new{name=17,browser_download_url=trustedUrl,digest=17},
                    new{name=asset,browser_download_url=123,
                        digest="sha256:"+new string('a',64)},
                    new{name=asset,browser_download_url=trustedUrl,digest=42}
                }},
            new{tag_name=tag,draft=false,prerelease=true,
                assets=new object[]{new{name=asset,browser_download_url=trustedUrl,
                    digest="sha256:"+new string('f',64)}}}
        });
        var afterMalformed=ObsidianReleaseCatalog.Select(mixedJson,UpdateChannel.Preview);
        Check(afterMalformed!=null&&afterMalformed.Tag==tag&&
              ObsidianReleaseCatalog.IsTrusted(afterMalformed),
            "malformed asset field types cannot block a later trusted Obsidian release");
        var selected=ObsidianReleaseCatalog.Select(releaseJson,UpdateChannel.Preview);
        Check(selected!=null&&selected.Tag==tag&&
              selected.AssetName==asset&&ObsidianReleaseCatalog.IsTrusted(selected),
            "only exact Obsidian AppId installer is eligible for native update discovery");
        Check(ObsidianReleaseCatalog.Select(releaseJson,UpdateChannel.Stable)==null,
            "stable update channel cannot automatically select preview releases");
        Check(!ObsidianReleaseCatalog.IsTrusted(selected with{
            AssetName="UntamedManager-8.2.0-SETUP.exe"}),
            "compatible-looking WPF installer is never an Obsidian update");
        Check(ObsidianReleaseCatalog.IsNewer(selected,new Version(8,2,0),""),
            "first install of a legacy untagged Obsidian can find its preview upgrade");
        Check(ObsidianReleaseCatalog.IsNewer(selected,new Version(8,2,0),
                "v8.2.0-obsidian-preview.2") &&
              !ObsidianReleaseCatalog.IsNewer(selected,new Version(8,2,0),
                "v8.2.0-obsidian-preview.3") &&
              !ObsidianReleaseCatalog.IsNewer(selected,new Version(8,2,0),
                "v8.2.0-obsidian-preview.4"),
            "stamped Preview version is strictly monotonic and never loops on same installer");
        Check(!ObsidianReleaseCatalog.IsNewer(selected,new Version(8,2,0),
                "v8.2.0-wpf-asa-only.967") &&
              !ObsidianReleaseCatalog.IsNewer(selected,new Version(8,2,0),
                "INVALID") &&
              !ObsidianReleaseCatalog.IsNewer(selected,new Version(8,2,0),
                "v8.2.0-obsidian-preview.999999999999999999999999999999"),
            "WPF, corrupted and malformed installed tags fail closed");
        string oversizedPreview=releaseJson.Replace(
            tag,"v8.2.0-obsidian-preview."+new string('9',60),StringComparison.Ordinal);
        Check(ObsidianReleaseCatalog.Select(oversizedPreview,UpdateChannel.Preview)==null,
            "malformed oversized GitHub preview run ID cannot crash release discovery");
        // The release metadata the human reviewed is not necessarily
        // the latest by the time the installer is staged. Pin both exact
        // tag and immutable expected bytes; any newer/mutated GitHub release
        // must require another explicit Check rather than installing unseen.
        Check(DesktopObsidianUpdateInstallBridge.MatchesCheckedRelease(
                    selected,tag,new string('F',64))&&
              !DesktopObsidianUpdateInstallBridge.MatchesCheckedRelease(
                    selected,"v8.2.0-obsidian-preview.4",new string('f',64))&&
              !DesktopObsidianUpdateInstallBridge.MatchesCheckedRelease(
                    selected,tag,new string('a',64))&&
              !DesktopObsidianUpdateInstallBridge.MatchesCheckedRelease(
                    selected with{AssetUrl="https://attacker.invalid/setup.exe"},
                    tag,new string('f',64))&&
              !DesktopObsidianUpdateInstallBridge.MatchesCheckedRelease(
                    null,tag,new string('f',64)),
            "updater refuses changed tag, changed digest, untrusted asset or absent release");
        var staging=new DesktopObsidianUpdateInstallBridge();
        var missingConsent=await Call(staging.HandleAsync,"updates.prepare",
            new{channel="preview",confirm=false});
        Check(IsError(missingConsent,"UM-UPDATE-EXPLICIT-CONFIRMATION-REQUIRED"),
            "updater cannot stage an installation without explicit local approval");
        var missingCheck=await Call(staging.HandleAsync,"updates.prepare",
            new{channel="preview",confirm=true});
        Check(IsError(missingCheck,"UM-UPDATE-CHECK-REQUIRED"),
            "native installer staging requires previously checked release tag and SHA");
        var malformedCheck=await Call(staging.HandleAsync,"updates.prepare",
            new{channel="preview",confirm=true,expectedTag=tag,expectedSha256="wrong"});
        Check(IsError(malformedCheck,"UM-UPDATE-CHECK-REQUIRED"),
            "native updater rejects malformed digest before any download or application inspection");
        var replay=await Call(staging.HandleAsync,"updates.install",
            new{token=Guid.NewGuid(),confirm=true});
        Check(IsError(replay,"UM-UPDATE-PREPARE-EXPIRED"),
            "unapproved or unknown installer staging token is never replayable");
    }

    // Canonical Obsidian 2.0.0 Preview can upgrade an earlier 1.4.0
    // Obsidian installation. Legacy WPF releases cannot cross this boundary.
    {
        string newTag="v2.0.0-obsidian-preview.1499";
        var newRelease=new UpdateRelease(new Version(2,0,0),newTag,
            "Single-product preview fixture",
            "https://github.com/"+ObsidianReleaseCatalog.Repository+
                "/releases/download/"+newTag+"/"+ObsidianReleaseCatalog.Installer,
            new string('c',64),ObsidianReleaseCatalog.Installer);
        Check(ObsidianReleaseCatalog.IsTrusted(newRelease)&&
              ObsidianReleaseCatalog.IsNewer(newRelease,new Version(1,4,0,0),
                  "v1.4.0-obsidian-preview.1478")&&
              !ObsidianReleaseCatalog.IsNewer(newRelease,new Version(1,6,1,0),
                  "v1.6.1-wpf-asa-only.967")&&
              !ObsidianReleaseCatalog.IsNewer(newRelease,new Version(2,0,0,0),
                  newTag),
            "Obsidian 2.0 upgrades prior Obsidian only, never WPF or the same build");
        Check(ObsidianReleaseCatalog.IsNewer(newRelease,new Version(2,0,0,0),"")&&
              !ObsidianReleaseCatalog.IsNewer(newRelease,new Version(2,0,1,0),""),
            "untagged four-part .NET assembly version uses three-part SemVer correctly");
        Check(!ObsidianReleaseCatalog.IsTrusted(newRelease with{
                AssetName="UntamedManager-1.6.1-SETUP.exe"}),
            "obsolete WPF installer stays outside canonical Obsidian release identity");
    }

    // Real native Plugin Center mutations only in a disposable synthetic
    // installation. Both local maps sharing the plugin folder must be stopped.
    {
        string install=Path.Combine(root,"plugin-state-bridge-install");
        string active=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "ArkApi","Plugins","ExamplePlugin");
        string disabled=Path.Combine(install,"ShooterGame","Binaries","Win64",
            "UntamedDisabledPlugins","ExamplePlugin");
        Directory.CreateDirectory(active);
        File.WriteAllBytes(Path.Combine(active,"ExamplePlugin.dll"),
            new byte[]{0x4d,0x5a,0x90,0,0x13,0x37});
        File.WriteAllText(Path.Combine(active,"config.json"),"{\"secret\":123}");
        File.WriteAllText(Path.Combine(active,"license.key"),"KEEP-LICENSE-PRIVATE");
        bool peerRunning=false;
        var a=new LocalMap{Name="Plugin test A",InstallationPath=install};
        var b=new LocalMap{Name="Plugin test B",InstallationPath=install+Path.DirectorySeparatorChar};
        await using var pluginCluster=new ClusterService("plugin-state-native-test",
            Path.Combine(root,"plugin-native-profile"),
            m=>peerRunning && m.Id==b.Id
                ?new ProcessCheck(true,"synthetic running peer",null,false)
                :new ProcessCheck(false,"synthetic confirmed stopped",null,true));
        pluginCluster.Config.LocalMaps.Add(a);
        pluginCluster.Config.LocalMaps.Add(b);
        pluginCluster.Save();
        using var pluginBridge=new DesktopPluginStateBridge(pluginCluster);
        var state=await Call(pluginBridge.HandleAsync,"plugins.state",new{mapId=a.Id});
        IsOk(state,"plugin-state native inventory reads existing DLL folder");
        Check(state.GetProperty("data").GetProperty("plugins")[0]
                .GetProperty("enabled").GetBoolean(),
            "native plugin inventory reports installed disk state without runtime claims");
        a.HistoricalStoreProtected=true;
        var locked=await Call(pluginBridge.HandleAsync,"plugins.togglePreview",
            new{mapId=a.Id,plugin="ExamplePlugin",enabled=false});
        Check(!locked.GetProperty("ok").GetBoolean() && Directory.Exists(active),
            "unregistered original map has no right to toggle plugins; source retained");
        a.HistoricalStoreProtected=false;
        peerRunning=true;
        var denied=await Call(pluginBridge.HandleAsync,"plugins.togglePreview",
            new{mapId=a.Id,plugin="ExamplePlugin",enabled=false});
        IsOk(denied,"running-peer preview is an informative but blocked result");
        Check(!denied.GetProperty("data").GetProperty("allowed").GetBoolean() &&
              Directory.Exists(active),"running shared-install map blocks plugin toggle");
        peerRunning=false;
        var preview=await Call(pluginBridge.HandleAsync,"plugins.togglePreview",
            new{mapId=a.Id,plugin="ExamplePlugin",enabled=false});
        IsOk(preview,"confirmed stopped local maps allow one-shot plugin preview");
        Check(preview.GetProperty("data").GetProperty("allowed").GetBoolean() &&
              preview.GetProperty("data").GetProperty("checkedLocalMaps").GetInt32()==2,
            "preview verified both maps sharing the installation");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var unconfirmed=await Call(pluginBridge.HandleAsync,"plugins.toggleApply",
            new{token,confirm=false});
        Check(IsError(unconfirmed,"UM-PLUGIN-EXPLICIT-CONFIRMATION-REQUIRED") &&
              Directory.Exists(active),"plugin move requires explicit confirmation");
        peerRunning=true;
        var stale=await Call(pluginBridge.HandleAsync,"plugins.toggleApply",
            new{token,confirm=true});
        Check(IsError(stale,"UM-PLUGIN-STATE-CHANGED") && Directory.Exists(active),
            "running shared-install peer after preview causes fail-closed recheck");
        var staleReplay=await Call(pluginBridge.HandleAsync,"plugins.toggleApply",
            new{token,confirm=true});
        Check(IsError(staleReplay,"UM-PLUGIN-PREVIEW-EXPIRED"),
            "failed plugin confirmation cannot reuse stale approval");
        peerRunning=false;
        var membershipPreview=await Call(pluginBridge.HandleAsync,"plugins.togglePreview",
            new{mapId=a.Id,plugin="ExamplePlugin",enabled=false});
        string memberToken=membershipPreview.GetProperty("data").GetProperty("token").GetString()!;
        var third=new LocalMap{Name="Late shared map",InstallationPath=install};
        pluginCluster.Config.LocalMaps.Add(third);
        var changedGroup=await Call(pluginBridge.HandleAsync,"plugins.toggleApply",
            new{token=memberToken,confirm=true});
        Check(IsError(changedGroup,"UM-PLUGIN-STATE-CHANGED") && Directory.Exists(active),
            "adding a shared-install map after approval forces fresh plugin preflight");
        pluginCluster.Config.LocalMaps.Remove(third);
        var rootPreview=await Call(pluginBridge.HandleAsync,"plugins.togglePreview",
            new{mapId=a.Id,plugin="ExamplePlugin",enabled=false});
        string rootToken=rootPreview.GetProperty("data").GetProperty("token").GetString()!;
        a.InstallationPath=Path.Combine(root,"DIFFERENT-PLUGIN-INSTALL");
        var rootDenied=await Call(pluginBridge.HandleAsync,"plugins.toggleApply",
            new{token=rootToken,confirm=true});
        Check(IsError(rootDenied,"UM-PLUGIN-STATE-CHANGED") && Directory.Exists(active),
            "plugin approval is bound to the original local installation root");
        a.InstallationPath=install;
        var secondPreview=await Call(pluginBridge.HandleAsync,"plugins.togglePreview",
            new{mapId=a.Id,plugin="ExamplePlugin",enabled=false});
        string approvedToken=secondPreview.GetProperty("data").GetProperty("token").GetString()!;
        var saved=await Call(pluginBridge.HandleAsync,"plugins.toggleApply",
            new{token=approvedToken,confirm=true});
        IsOk(saved,"guarded native plugin state changes with full backup");
        Check(!Directory.Exists(active) && Directory.Exists(disabled) &&
              saved.GetProperty("data").GetProperty("backupCreated").GetBoolean() &&
              File.ReadAllText(Path.Combine(disabled,"config.json"))=="{\"secret\":123}" &&
              File.ReadAllText(Path.Combine(disabled,"license.key"))=="KEEP-LICENSE-PRIVATE",
            "plugin DLL move preserves original settings and license data");
        Check(!saved.GetRawText().Contains("KEEP-LICENSE-PRIVATE",StringComparison.Ordinal) &&
              !saved.GetRawText().Contains(install,StringComparison.OrdinalIgnoreCase),
            "native plugin receipt never exposes source path or license contents");
        var newState=await Call(pluginBridge.HandleAsync,"plugins.state",new{mapId=a.Id});
        Check(!newState.GetProperty("data").GetProperty("plugins")[0]
                .GetProperty("enabled").GetBoolean(),
            "native plugin inventory can see explicitly disabled plugin folder");

        // Independent stop proof must run INSIDE the actual Core directory
        // transaction, not merely at the React preview/approval boundary.
        bool blockedUnderLease=false,underLeaseInvoked=false;
        try
        {
            PluginStateManager.ToggleLocalChecked(pluginCluster,a.Id,
                "ExamplePlugin",true,()=>{
                    underLeaseInvoked=true;
                    throw new InvalidOperationException("SYNTHETIC-HISTORICAL-LOADER-START");
                });
        }
        catch(InvalidOperationException ex)
        {
            blockedUnderLease=ex.Message=="SYNTHETIC-HISTORICAL-LOADER-START";
        }
        Check(underLeaseInvoked&&blockedUnderLease&&
              !Directory.Exists(active)&&Directory.Exists(disabled)&&
              File.ReadAllText(Path.Combine(disabled,"license.key"))=="KEEP-LICENSE-PRIVATE",
            "late historical ASA/Loader start refuses plugin move before writing or losing files");
    }

    // Native plugin ZIP picker and guarded installation. The WinForms file
    // dialog is replaced by a deterministic picker in this isolated test only.
    {
        string zipInstall=Path.Combine(root,"plugin-zip-bridge-install");
        string pluginRoot=Path.Combine(zipInstall,"ShooterGame","Binaries","Win64","ArkApi","Plugins");
        Directory.CreateDirectory(pluginRoot);
        string zipPath=Path.Combine(root,"SampleZip-package.zip");
        using(var stream=File.Create(zipPath))
        using(var zip=new System.IO.Compression.ZipArchive(stream,
            System.IO.Compression.ZipArchiveMode.Create))
        {
            using(var binary=zip.CreateEntry("SampleZip/SampleZip.dll").Open())
                binary.Write(new byte[]{0x4d,0x5a,0x01,0x02,0x03});
            using(var readme=new StreamWriter(zip.CreateEntry("SampleZip/README.txt").Open()))
                readme.Write("Ignored non-binary asset");
        }
        bool zipPeerRunning=false;
        var zipMap=new LocalMap{Name="Plugin ZIP test",InstallationPath=zipInstall};
        var zipPeer=new LocalMap{Name="Plugin ZIP peer",InstallationPath=zipInstall};
        await using var zipCluster=new ClusterService("native-zip-fixture",
            Path.Combine(root,"plugin-zip-profile"),
            m=>zipPeerRunning && m.Id==zipPeer.Id
                ?new ProcessCheck(true,"synthetic running peer",null,false)
                :new ProcessCheck(false,"synthetic stopped",null,true));
        zipCluster.AddMap(zipMap);
        zipCluster.AddMap(zipPeer);
        int pickerCalls=0;
        using var zipBridge=new DesktopPluginZipBridge(zipCluster,
            ()=>{pickerCalls++;return zipPath;});
        var outsider=await Call(zipBridge.HandleAsync,"plugins.zipSelect",
            new{mapId=Guid.NewGuid()});
        Check(IsError(outsider,"UM-MAP-NOT-OWNED")&&pickerCalls==0,
            "foreign map cannot open plugin ZIP picker");
        zipPeerRunning=true;
        var runningDenied=await Call(zipBridge.HandleAsync,"plugins.zipSelect",
            new{mapId=zipMap.Id});
        Check(IsError(runningDenied,"UM-PLUGIN-ZIP-FAILED")&&pickerCalls==0,
            "running sibling blocks ZIP selection before picker opens");
        zipPeerRunning=false;
        var selected=await Call(zipBridge.HandleAsync,"plugins.zipSelect",
            new{mapId=zipMap.Id});
        IsOk(selected,"native ZIP picker prepares a valid plugin archive");
        string zipToken=selected.GetProperty("data").GetProperty("token").GetString()!;
        Check(pickerCalls==1&&!Directory.Exists(Path.Combine(pluginRoot,"SampleZip")) &&
              selected.GetProperty("data").GetProperty("skippedFiles").GetInt32()==1,
            "plugin ZIP preview does not write files and reports ignored data");
        var noApproval=await Call(zipBridge.HandleAsync,"plugins.zipApply",
            new{token=zipToken,confirm=false});
        Check(IsError(noApproval,"UM-PLUGIN-ZIP-APPROVAL"),
            "plugin install requires explicit operator approval");
        var applied=await Call(zipBridge.HandleAsync,"plugins.zipApply",
            new{token=zipToken,confirm=true});
        IsOk(applied,"guarded Core installs selected ZIP binary");
        Check(File.Exists(Path.Combine(pluginRoot,"SampleZip","SampleZip.dll")) &&
              !File.Exists(Path.Combine(pluginRoot,"SampleZip","README.txt")) &&
              applied.GetProperty("data").GetProperty("installedBinaries").GetInt32()==1,
            "DLL is installed but arbitrary nonbinary payload is not");
        var replay=await Call(zipBridge.HandleAsync,"plugins.zipApply",
            new{token=zipToken,confirm=true});
        Check(IsError(replay,"UM-PLUGIN-ZIP-TOKEN-EXPIRED"),
            "same plugin ZIP approval token cannot be replayed");
        // An updated DLL must never replace the user's existing settings,
        // licensing data or other nonbinary files. The Core makes an
        // independently verified snapshot before swapping the binary.
        string installedPlugin=Path.Combine(pluginRoot,"SampleZip");
        File.WriteAllText(Path.Combine(installedPlugin,"config.json"),
            "{\"preserve\":\"user-value\"}");
        File.WriteAllText(Path.Combine(installedPlugin,"license.key"),
            "PRIVATE-LICENSE-UNCHANGED");
        File.Delete(zipPath);
        using(var stream=File.Create(zipPath))
        using(var zip=new System.IO.Compression.ZipArchive(stream,
            System.IO.Compression.ZipArchiveMode.Create))
        {
            using(var dll=zip.CreateEntry("SampleZip/SampleZip.dll").Open())
                dll.Write(new byte[]{0x4d,0x5a,0x09,0x09,0x09});
            using(var bogus= new StreamWriter(
                zip.CreateEntry("SampleZip/config.json").Open()))
                bogus.Write("MUST_NOT_OVERWRITE");
        }
        var updatePreview=await Call(zipBridge.HandleAsync,"plugins.zipSelect",
            new{mapId=zipMap.Id});
        IsOk(updatePreview,"native ZIP update detects existing plugin");
        Check(updatePreview.GetProperty("data")
                  .GetProperty("previousExists").GetBoolean(),
            "existing plugin triggers complete SHA-verified backup");
        var updated=await Call(zipBridge.HandleAsync,"plugins.zipApply",
            new{token=updatePreview.GetProperty("data").GetProperty("token").GetString(),
                confirm=true});
        IsOk(updated,"native ZIP update replaces a plugin DLL with full protection");
        Check(updated.GetProperty("data").GetProperty("backupCreated").GetBoolean() &&
              File.ReadAllBytes(Path.Combine(installedPlugin,"SampleZip.dll"))
                  .SequenceEqual(new byte[]{0x4d,0x5a,0x09,0x09,0x09}) &&
              File.ReadAllText(Path.Combine(installedPlugin,"config.json"))==
                  "{\"preserve\":\"user-value\"}" &&
              File.ReadAllText(Path.Combine(installedPlugin,"license.key"))==
                  "PRIVATE-LICENSE-UNCHANGED" &&
              !updated.GetRawText().Contains("PRIVATE-LICENSE-UNCHANGED"),
            "existing plugin settings/license remain byte-identical and secret");
        var interruptedPluginRestart=new AsaManualRestartJournal(zipInstall);
        var pendingRestart=interruptedPluginRestart.Begin(zipMap.Id,44117);
        int pickerCountBeforeCrash=pickerCalls;
        var stoppedButRestartPending=await Call(zipBridge.HandleAsync,
            "plugins.zipSelect",new{mapId=zipMap.Id});
        Check(IsError(stoppedButRestartPending,"UM-PLUGIN-ZIP-FAILED") &&
              pickerCalls==pickerCountBeforeCrash,
            "pending interrupted ASA Restart refuses plugin ZIP writes before picker opens");
        interruptedPluginRestart.Change(pendingRestart.Id,AsaRestartPhase.NeedsOperator);
        zipMap.HistoricalStoreProtected=true;
        var historicalDenied=await Call(zipBridge.HandleAsync,"plugins.zipSelect",
            new{mapId=zipMap.Id});
        Check(IsError(historicalDenied,"UM-PLUGIN-ZIP-FAILED"),
            "historical Store-v1 map never authorizes plugin ZIP changes");
    }

    // A historical Store-v1 ZIP is an identity/provenance source, NOT a
    // complete command line. Test imported map remains blocked even with
    // valid SHA and an actual world file.
    string oldRoot=Path.Combine(root,"historical-synthetic-map");
    string oldExeFolder=Path.Combine(oldRoot,"ShooterGame","Binaries","Win64");
    Directory.CreateDirectory(oldExeFolder);
    File.WriteAllText(Path.Combine(oldExeFolder,"ArkAscendedServer.exe"),
        "SYNTHETIC HISTORICAL ASA EXE - NEVER LAUNCH");
    string oldSaveFolder=Path.Combine(oldRoot,"ShooterGame","Saved","SavedArks");
    Directory.CreateDirectory(oldSaveFolder);
    string oldWorld=Path.Combine(oldSaveFolder,"ScorchedEarth_WP.ark");
    File.WriteAllBytes(oldWorld,new byte[]{3,1,4,1,5,9,2,6,5});
    // A zero-byte temporary profile is not automatically corruption. It must
    // never be deleted or replaced solely because of its byte length.
    string zeroProfile=Path.Combine(oldSaveFolder,"temporary-transfer.arkprofile");
    File.WriteAllBytes(zeroProfile,Array.Empty<byte>());
    byte[] originalWorld=File.ReadAllBytes(oldWorld);
    ManagerMigrationArchiveTests.Run(root,oldRoot);
    string oldArchive=Path.Combine(root,"untamed-manager-package-fixtures",
        "Untamed-Manager-migration-fixture.zip");
    string originalZipSha=Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(oldArchive)));
    // The verified 39-file historical ZIP must correspond byte-for-byte to
    // the previous Manager's actual four local Store metadata files. All
    // writes below affect a disposable synthetic TEST directory only.
    string parityLocal=Path.Combine(root,"original-local-source-fixture");
    Directory.CreateDirectory(parityLocal);
    using(var historicalZip=System.IO.Compression.ZipFile.OpenRead(oldArchive))
        foreach(string name in new[]{
            "cluster-v1.json","cluster-store-v1.json",
            "shared-store-device-v1.json","shared-store-lock-v1.json"})
        {
            var entry=historicalZip.Entries.Single(e=>
                e.FullName.Replace('\\','/').Equals("Kova/"+name,
                    StringComparison.OrdinalIgnoreCase));
            using var source=entry.Open();
            using var target=File.Create(Path.Combine(parityLocal,name));
            source.CopyTo(target);
        }
    var matchingParity=LegacyStoreV1ArchiveParity.Inspect(oldArchive,parityLocal);
    Check(matchingParity.ArchiveVerified&&matchingParity.AllFourExactlyMatch &&
          matchingParity.Files.Length==4 && matchingParity.LocalStoreActive &&
          !matchingParity.LiveTwoHostVerified &&
          !matchingParity.CharacterTravelVerified,
        "original four local Store-v1 metadata files match verified 39-file archive without production authority");
    using(var originalParity=new DesktopHistoricalStoreImportBridge(cluster,
        _=>new ProcessCheck(false,"Synthetic stopped",null,true),parityLocal))
    {
        var ok=await Call(originalParity.HandleAsync,"historical.localParity",
            new{archive=oldArchive});
        IsOk(ok,"native historical Store archive parity works through WebView2 boundary");
        Check(ok.GetProperty("data").GetProperty("allFourExactlyMatch").GetBoolean() &&
              !ok.GetProperty("data").GetProperty("productionUseAllowed").GetBoolean() &&
              !ok.ToString().Contains(parityLocal,StringComparison.OrdinalIgnoreCase),
            "original-vs-local parity does not disclose source roots or grant Store write");
        var rejected=await Call(originalParity.HandleAsync,"historical.localParity",
            new{archive=@"\\192.0.2.1\invalid-store.zip"});
        Check(IsError(rejected,"UM-STORE-PARITY-FAILED"),
            "remote UNC archive cannot be used for original Store parity");
    }
    File.AppendAllText(Path.Combine(parityLocal,"cluster-v1.json")," ");
    var parityChangedCopy=LegacyStoreV1ArchiveParity.Inspect(oldArchive,parityLocal);
    Check(!parityChangedCopy.AllFourExactlyMatch&&parityChangedCopy.Files.Count(f=>!f.ExactlySame)==1 &&
          parityChangedCopy.Files.Single(f=>!f.ExactlySame).Kind=="cluster",
        "changed local metadata invalidates archive byte parity without altering historic source");
    Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(oldArchive)))==originalZipSha,
        "parity checks never modify original historical ZIP");

    using(var generic=new DesktopLocalMapRegistryBridge(cluster))
    {
        var denied=await Call(generic.HandleAsync,"servers.inspectLocal",
            new{name="Scorched Earth",root=oldRoot});
        Check(IsError(denied,"UM-EXISTING-WORLD-REQUIRES-PRESERVED-IMPORT"),
            "generic blank-profile import refuses an existing historical world");
    }
    using(var archiveImport=new DesktopHistoricalStoreImportBridge(cluster,
        _=>new ProcessCheck(false,"Confirmed stopped synthetic test only",null,true)))
    {
        using(var malformed=JsonDocument.Parse(await archiveImport.HandleAsync(
            "{not valid json")))
        {
            Check(malformed.RootElement.GetProperty("error").GetString()=="UM-HISTORICAL-VERIFICATION-FAILED" &&
                  !malformed.RootElement.GetProperty("ok").GetBoolean(),
                "malformed WebView JSON fails closed without crashing the bridge");
        }
        var listing=await Call(archiveImport.HandleAsync,"historical.list",
            new{archive=oldArchive});
        IsOk(listing,"historical 39-file Manager ZIP passes verification and lists five maps");
        var choices=listing.GetProperty("data").GetProperty("maps").EnumerateArray().ToArray();
        Check(choices.Length==5,"original Manager backup has exactly five local map identities");
        var chosen=choices.Single(x=>x.GetProperty("mapKey").GetString()=="ScorchedEarth_WP");
        string historicalId=chosen.GetProperty("mapId").GetString()!;
        Check(!listing.GetProperty("data").GetProperty("allowServerStart").GetBoolean(),
            "read-only historical metadata never certifies production launch");

        var preview=await Call(archiveImport.HandleAsync,"historical.preview",
            new{archive=oldArchive,root=oldRoot,mapId=historicalId});
        IsOk(preview,"original MapID/root/mods/UDP provenance validated");
        Check(preview.GetProperty("data").GetProperty("mapId").GetString()==historicalId &&
            !preview.GetProperty("data").GetProperty("fullLaunchArgumentsVerified").GetBoolean(),
            "original GUID preserved while missing complete launch arguments remain explicitly unknown");
        const string privateLaunch="ArkAscendedServer.exe ScorchedEarth_WP?SessionName=\"PrivateServer\"?ServerAdminPassword=DO_NOT_ECHO -port=7777 -QueryPort=27015 -mods=928548 -clusterid=KovaCluster -ClusterDirOverride=C:\\private\\cluster -usestore";
        var launchEvidence=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,command=privateLaunch});
        IsOk(launchEvidence,"operator-supplied launch text is compared read-only with archived ports and mod order");
        var assessed=launchEvidence.GetProperty("data");
        Check(assessed.GetProperty("gamePortStatus").GetString()=="MATCH" &&
              assessed.GetProperty("modOrderStatus").GetString()=="MATCH" &&
              assessed.GetProperty("useStorePresent").GetBoolean() &&
              !assessed.GetProperty("productionUseAllowed").GetBoolean() &&
              !assessed.GetProperty("serverStartAllowed").GetBoolean(),
            "matching text is never certified as launch permission");
        Check(!launchEvidence.GetRawText().Contains("DO_NOT_ECHO",StringComparison.Ordinal) &&
              !launchEvidence.GetRawText().Contains("KovaCluster",StringComparison.Ordinal) &&
              !launchEvidence.GetRawText().Contains("private",StringComparison.OrdinalIgnoreCase),
            "raw command, secrets and cluster path never return to WebView");
        Check(assessed.GetProperty("clusterIdStatus").GetString()=="MATCH",
            "original KovaCluster internal identity matches without renaming it");
        var wrongCluster=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,
                command=privateLaunch.Replace("-clusterid=KovaCluster",
                    "-clusterid=WrongCluster",StringComparison.Ordinal)});
        Check(wrongCluster.GetProperty("data").GetProperty("clusterIdStatus").GetString()=="MISMATCH" &&
              !wrongCluster.GetProperty("data").GetProperty("serverStartAllowed").GetBoolean(),
            "a merely present but renamed cluster id must not count as a verified match");
        var queryStyle=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,
                command=privateLaunch.Replace(" -port=7777 -QueryPort=27015",
                    "?port=7777?QueryPort=27015",StringComparison.Ordinal)});
        Check(queryStyle.GetProperty("data").GetProperty("gamePortStatus").GetString()=="MATCH" &&
              queryStyle.GetProperty("data").GetProperty("queryPortStatus").GetString()=="MATCH",
            "query-string ASA port and QueryPort parameters are recognized");
        var duplicatePort=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,
                command=privateLaunch+"?port=9999"});
        Check(duplicatePort.GetProperty("data").GetProperty("gamePortStatus").GetString()=="CONFLICT" &&
              !duplicatePort.GetProperty("data").GetProperty("serverStartAllowed").GetBoolean(),
            "duplicate mixed-style ASA port definitions fail closed");
        Check(assessed.GetProperty("passiveModOrderStatus").GetString()=="MATCH",
            "empty original passive mod list matches absent passive launch flag");
        var injectedPassive=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,
                command=privateLaunch+" -passivemods=928548"});
        Check(injectedPassive.GetProperty("data").GetProperty("passiveModOrderStatus").GetString()=="MISMATCH" &&
              !injectedPassive.GetProperty("data").GetProperty("serverStartAllowed").GetBoolean(),
            "new passive mods never silently become historical launch evidence");
        var conversion=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,command=privateLaunch+" -converttostore"});
        Check(conversion.GetProperty("data").GetProperty("conversionFlagPresent").GetBoolean() &&
              !conversion.GetProperty("data").GetProperty("serverStartAllowed").GetBoolean(),
            "conversion flag is recognized but never executed");
        var exactSourceStyle=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,
                command="ArkAscendedServer.exe ScorchedEarth_WP?listen -Port=7777 -QueryPort=27015 -mods=928548 -clusterid=KovaCluster -ClusterDirOverride=C:\\private\\cluster -usestore -log"});
        IsOk(exactSourceStyle,"read-only launch preview matches original supplied JavaScript source switch grammar");
        Check(exactSourceStyle.GetProperty("data").GetProperty("canonicalSourceFlagsObserved").GetBoolean() &&
              exactSourceStyle.GetProperty("data").GetProperty("storeFlagCount").GetInt32()==1 &&
              !exactSourceStyle.GetProperty("data").GetProperty("productionUseAllowed").GetBoolean(),
            "exact original source syntax is recognized but cannot authorize historic ASA launch");
        var duplicateStore=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,
                command=privateLaunch+" -usestore"});
        Check(duplicateStore.GetProperty("data").GetProperty("storeFlagCount").GetInt32()==2 &&
              !duplicateStore.GetProperty("data").GetProperty("canonicalSourceFlagsObserved").GetBoolean() &&
              duplicateStore.GetProperty("data").GetProperty("findings").EnumerateArray()
                .Any(x=>x.GetString()=="DUPLICATE_USESTORE_FLAG"),
            "repeated Store flag is explicit evidence of an altered historical launch");
        var reverseConvert=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,
                command=privateLaunch+" -convertfromstore"});
        Check(reverseConvert.GetProperty("data").GetProperty("conversionFlagPresent").GetBoolean() &&
              !reverseConvert.GetProperty("data").GetProperty("serverStartAllowed").GetBoolean(),
            "reverse conversion is denied as unsafe for existing Store-v1 characters");
        var missing=await Call(archiveImport.HandleAsync,"historical.launchEvidence",
            new{archive=oldArchive,mapId=historicalId,
                command="ArkAscendedServer.exe ScorchedEarth_WP -port=9999"});
        Check(missing.GetProperty("data").GetProperty("gamePortStatus").GetString()=="MISMATCH" &&
              !missing.GetProperty("data").GetProperty("clusterIdPresent").GetBoolean(),
            "incorrect/incomplete source never guesses a historical launch profile");
        Check(!cluster.Config.LocalMaps.Any(x=>x.Id==Guid.Parse(historicalId)),
            "preview does not mutate local Manager config");
        string approval=preview.GetProperty("data").GetProperty("token").GetString()!;
        var noApproval=await Call(archiveImport.HandleAsync,"historical.register",
            new{token=approval,confirm=false});
        Check(IsError(noApproval,"UM-HISTORICAL-EXPLICIT-CONFIRMATION-REQUIRED"),
            "missing confirmation never consumes historical source token");
        var receipt=await Call(archiveImport.HandleAsync,"historical.register",
            new{token=approval,confirm=true});
        IsOk(receipt,"Manager-only quarantined historical map registration");
        var imported=cluster.Config.LocalMaps.Single(x=>x.Id==Guid.Parse(historicalId));
        Check(imported.HistoricalStoreProtected && imported.Arguments=="" &&
              !imported.AutoStart && !imported.StartWithAsaApi &&
              imported.GamePort==7777 && imported.QueryPort==27015 &&
              imported.HistoricalMapKey=="ScorchedEarth_WP" &&
              imported.HistoricalEnabledMods.SequenceEqual(new[]{"928548"}),
            "quarantined profile keeps historical GUID/ports/mod IDs, no invented arguments or auto-start");
        Check(imported.HistoricalArchiveSha256==originalZipSha &&
            File.ReadAllBytes(oldWorld).SequenceEqual(originalWorld) &&
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(oldArchive)))==originalZipSha,
            "actual copied world and historical ZIP bytes remain exactly unchanged");
        Check(File.Exists(zeroProfile) && new FileInfo(zeroProfile).Length==0,
            "zero-byte temporary arkprofile is neither deleted nor rewritten on import");
        Check(!cluster.Check(imported).ConfirmedStopped,
            "Store-v1 map readiness remains locked even when no process is running");
        bool launchDenied=false;
        try{cluster.LaunchLocal(imported.Id);}
        catch(InvalidOperationException){launchDenied=true;}
        Check(launchDenied,"no direct lifecycle code path can launch a quarantined Store-v1 map");

        using(var blockedSetup=new DesktopInitialMapSetupBridge(cluster))
        {
            var initial=await Call(blockedSetup.HandleAsync,"servers.initialStatus",
                new{mapId=imported.Id});
            Check(!initial.GetProperty("data").GetProperty("initialSetupAvailable").GetBoolean(),
                "new-map wizard cannot overwrite historical Store launch configuration");
        }
        using(var blockedMods=new DesktopModDraftBridge(cluster))
        {
            var blocked=await Call(blockedMods.HandleAsync,"mods.draftStart",
                new{mapId=imported.Id});
            Check(!blocked.GetProperty("ok").GetBoolean(),
            "quarantined original Kova map without completed takeover cannot edit mods");
        }
        using(var blockedIni=new DesktopIniBridge(cluster))
        {
            var blocked=await Call(blockedIni.HandleAsync,"ini.open",
                new{mapId=imported.Id,file="Game.ini"});
            Check(!blocked.GetProperty("ok").GetBoolean(),
            "quarantined original Kova map cannot edit INI without authenticated stopped owner");
        }
        var replay=await Call(archiveImport.HandleAsync,"historical.register",
            new{token=approval,confirm=true});
        Check(IsError(replay,"UM-HISTORICAL-PREVIEW-EXPIRED"),
            "historical import token is one-time only");
        var duplicate=await Call(archiveImport.HandleAsync,"historical.preview",
            new{archive=oldArchive,root=oldRoot,mapId=historicalId});
        Check(!duplicate.GetProperty("ok").GetBoolean(),
            "second registration of same original map is refused");
    }
    await using(var runningStoreCluster=new ClusterService("store-running-synthetic",
        Path.Combine(root,"store-running-profile"),
        _=>new ProcessCheck(true,"Synthetic running ASA process",null,false)))
    using(var uncertain=new DesktopHistoricalStoreImportBridge(runningStoreCluster,
        _=>new ProcessCheck(true,"Synthetic running ASA process",null,false)))
    {
        var listed=await Call(uncertain.HandleAsync,"historical.list",
            new{archive=oldArchive});
        IsOk(listed,"historical list is read-only even when synthetic ASA is running");
        var sample=listed.GetProperty("data").GetProperty("maps").EnumerateArray()
            .First(x=>x.GetProperty("mapKey").GetString()=="ScorchedEarth_WP");
        var stoppedOnly=await Call(uncertain.HandleAsync,"historical.preview",
            new{archive=oldArchive,root=oldRoot,
                mapId=sample.GetProperty("mapId").GetString()});
        Check(IsError(stoppedOnly,"UM-HISTORICAL-STOP-REQUIRED"),
            "running ASA prevents historical Store import before a map profile is created");
    }

    // Synthetic two-host metadata: common eight-file coordination ZIP plus
    // complementary four-file HP local JSON copy. No live host/SMB or Store writes.
    ClusterVaultTests.Run(root);
    // The real browser route may only read a known ORIGINAL local metadata
    // folder. CI injects a self-created copy; it never opens AppData/Kova.
    string syntheticLegacyLocal=Path.Combine(root,"v16-vault-zip","Synthetic-HP-Local-Copy");
    using(var localSource=new DesktopHistoricalStoreImportBridge(cluster,
        null,syntheticLegacyLocal))
    {
        var localProof=await Call(localSource.HandleAsync,"historical.localStatus");
        IsOk(localProof,"native original-profile route reads only four existing copied files");
        var data=localProof.GetProperty("data");
        Check(data.GetProperty("localSnapshotReady").GetBoolean() &&
              data.GetProperty("maps").GetArrayLength()==5 &&
              !data.GetProperty("liveTwoHostVerified").GetBoolean() &&
              !data.GetProperty("characterTravelVerified").GetBoolean() &&
              !data.GetProperty("productionUseAllowed").GetBoolean(),
            "local active Store-v1 metadata never certifies live writer or character travel");
        Check(!localProof.ToString().Contains(@"C:\\synthetic",StringComparison.OrdinalIgnoreCase),
            "legacy local status does not reveal original filesystem roots");
    }
    using(var absentSource=new DesktopHistoricalStoreImportBridge(cluster,
        null,Path.Combine(root,"missing-legacy-copy")))
    {
        var absent=await Call(absentSource.HandleAsync,"historical.localStatus");
        Check(IsError(absent,"UM-LOCAL-STORE-NOT-FOUND"),
            "missing local Store-v1 metadata produces a bounded error and no regeneration");
    }
    string peerTestRoot=Path.Combine(root,"v16-vault-zip");
    string coordinationCopy=Path.Combine(peerTestRoot,"Untamed-Cluster-Vault.zip");
    string hpLocalCopy=Path.Combine(peerTestRoot,"Synthetic-HP-Local-v1.zip");
    string originalCoordHash=Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(coordinationCopy)));
    string originalHpHash=Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hpLocalCopy)));
    using(var pairBridge=new DesktopHistoricalStoreImportBridge(cluster))
    {
        var paired=await Call(pairBridge.HandleAsync,"historical.pairedEvidence",
            new{coordinationArchive=coordinationCopy,localMetadataArchive=hpLocalCopy});
        IsOk(paired,"paired historical 8+4-file Store metadata can be inspected without SMB");
        var data=paired.GetProperty("data");
        Check(data.GetProperty("completeTwoHostEvidence").GetBoolean() &&
              data.GetProperty("sessionConsistent").GetBoolean() &&
              data.GetProperty("configHashesVerified").GetBoolean() &&
              !data.GetProperty("productionUseAllowed").GetBoolean(),
            "consistent historical two-host archive still never authorizes live Store writer");
        var badCopy=await Call(pairBridge.HandleAsync,"historical.pairedEvidence",
            new{coordinationArchive=coordinationCopy,
                localMetadataArchive=Path.Combine(peerTestRoot,"Synthetic-HP-Incomplete.zip")});
        Check(IsError(badCopy,"UM-HISTORICAL-VERIFICATION-FAILED"),
            "incomplete second host metadata is denied without inventing ownership");
        var denied=await Call(pairBridge.HandleAsync,"historical.pairedEvidence",
            new{coordinationArchive=coordinationCopy,localMetadataArchive=coordinationCopy});
        Check(IsError(denied,"UM-HISTORICAL-PAIR-INVALID-REQUEST"),
            "same ZIP cannot masquerade as two host sources");
    }
    Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(coordinationCopy)))==originalCoordHash &&
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(hpLocalCopy)))==originalHpHash,
        "paired historical Store-v1 verification leaves BOTH original ZIPs byte-identical");

    // A new, deliberately untrusted process observer must not write INI.
    await using var running=new ClusterService("obsidian-running",Path.Combine(root,"running-profile"),
        _=>new ProcessCheck(true,"Synthetic running map",null,false));
    running.AddMap(new LocalMap{
        Id=Guid.NewGuid(),Name="TestMap",InstallationPath=installation,
        ExecutablePath=map.ExecutablePath
    });
    using(var ini=new DesktopIniBridge(running))
    {
        var denied=await Call(ini.HandleAsync,"ini.open",
            new{mapId=running.Config.LocalMaps[0].Id,file="Game.ini"});
        Check(IsError(denied,"UM-INI-STOP-REQUIRED"),
            "running ASA blocks INI editing before even reading the file");
    }

    // Store-v1 imported map RCON attachment: the original ASA INI is
    // immutable; only the NEW Manager's DPAPI profile may receive the
    // original credential after two separate native approvals.
    {
        string histRoot=Path.Combine(root,"historical-rcon-owned");
        string bin=Path.Combine(histRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(bin);
        string exe=Path.Combine(bin,"ArkAscendedServer.exe");
        File.WriteAllBytes(exe,new byte[]{77,90,1,2});
        string cfg=Path.Combine(histRoot,"ShooterGame","Saved","Config",
            "WindowsServer");
        Directory.CreateDirectory(cfg);
        string ini=Path.Combine(cfg,"GameUserSettings.ini");
        // Real historical credentials need not obey new-server creation
        // policy: this existing ASA secret is short and has INI symbols.
        const string originalPass="K0va=;[!]";
        const int rconPort=37145;
        File.WriteAllText(ini,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+rconPort+"\r\nServerAdminPassword="+originalPass+"\r\n");
        var bytesBefore=File.ReadAllBytes(ini);
        await using var histCluster=new ClusterService("obsidian-historical-rcon",
            Path.Combine(root,"historical-rcon-profile"),m=>
            new ProcessCheck(false,"Synthetic only; no ASA launch",null,true));
        var protectedMap=new LocalMap{
            Id=Guid.NewGuid(),Name="The Center",InstallationPath=histRoot,
            ExecutablePath=exe,GamePort=7941,QueryPort=27441,
            HistoricalStoreProtected=true,HistoricalMapKey="TheCenter_WP",
            HistoricalArchiveSha256=new string('A',64),
            HistoricalEnabledMods=new List<string>(),HistoricalPassiveMods=new List<string>(),
            Arguments="",ApiLoaderArguments=""
        };
        histCluster.AddMap(protectedMap);
        using(var deniedSource=new DesktopHistoricalRconAttachBridge(
            histCluster,sourceGuard:_=>false))
        {
            var blocked=await Call(deniedSource.HandleAsync,"historical.rconPreview",
                new{mapId=protectedMap.Id,rconPort,password=originalPass});
            Check(!blocked.GetProperty("ok").GetBoolean(),
                "original Store-v1 ACTIVE/DONE failed guard prevents even RCON profile attach");
        }
        using(var bridge=new DesktopHistoricalRconAttachBridge(
            histCluster,sourceGuard:_=>true))
        {
            var status=await Call(bridge.HandleAsync,"historical.rconStatus",
                new{mapId=protectedMap.Id});
            IsOk(status,"historical native RCON initial status");
            Check(!status.GetProperty("data").GetProperty("configured").GetBoolean(),
                "historical RCON initially unconfigured without modifying original ASA files");
            var wrong=await Call(bridge.HandleAsync,"historical.rconPreview",
                new{mapId=protectedMap.Id,rconPort,password="WrongRconSecret!2026"});
            Check(!wrong.GetProperty("ok").GetBoolean() &&
                  wrong.GetProperty("error").GetString()=="UM-HISTORICAL-RCON-INI-CHANGED",
                "historical RCON reports existing INI/credential mismatch without claiming malformed legacy password");
            var invalidLine=await Call(bridge.HandleAsync,"historical.rconPreview",
                new{mapId=protectedMap.Id,rconPort,password="bad\ncredential"});
            Check(!invalidLine.GetProperty("ok").GetBoolean() &&
                  invalidLine.GetProperty("error").GetString()=="UM-HISTORICAL-RCON-EXISTING-PASSWORD-INVALID" &&
                  !invalidLine.ToString().Contains("bad",StringComparison.Ordinal),
                "only true legacy INI-line incompatibility gets a dedicated sanitized error");
            var preview=await Call(bridge.HandleAsync,"historical.rconPreview",
                new{mapId=protectedMap.Id,rconPort,password=originalPass});
            IsOk(preview,"original existing INI verifies historical RCON credentials");
            Check(preview.GetProperty("data").GetProperty("originalActiveDoneVerified").GetBoolean()&&
                  !preview.GetProperty("data").GetProperty("approvedObsidianSourcePinned").GetBoolean()&&
                  preview.GetProperty("data").GetProperty("legacyPeerCheckRequiredForLocalAttach").GetBoolean(),
                "un-pinned synthetic legacy Store RCON reports historical peer proof without claiming a local pin");
            Check(!preview.ToString().Contains(originalPass,StringComparison.Ordinal)&&
                  !preview.ToString().Contains(histRoot,StringComparison.OrdinalIgnoreCase),
                "historical RCON preview does not expose password or private root to React");
            string token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var noConsent=await Call(bridge.HandleAsync,"historical.rconApply",
                new{token,confirm=false});
            Check(!noConsent.GetProperty("ok").GetBoolean(),
                "historical Store-v1 RCON attach requires explicit second approval");
            var applied=await Call(bridge.HandleAsync,"historical.rconApply",
                new{token,confirm=true});
            IsOk(applied,"native historical RCON DPAPI profile attachment committed");
            Check(applied.GetProperty("data").GetProperty("rconPort").GetInt32()==rconPort&&
                  applied.GetProperty("data").GetProperty("originalStoreTouched").GetBoolean()==false&&
                  applied.GetProperty("data").GetProperty("iniModified").GetBoolean()==false,
                "historical RCON attaches to Manager profile without modifying Store or ASA INI");
            Check(histCluster.Config.LocalMaps.Single().RconPort==rconPort&&
                  SecureStore.Unprotect(histCluster.Config.LocalMaps.Single()
                      .RconPasswordProtected)==originalPass,
                "original historical RCON secret is stored as reversible current-user DPAPI only");
            var replay=await Call(bridge.HandleAsync,"historical.rconApply",
                new{token,confirm=true});
            Check(!replay.GetProperty("ok").GetBoolean(),
                "historical RCON approval cannot be replayed");
            var after=await Call(bridge.HandleAsync,"historical.rconStatus",
                new{mapId=protectedMap.Id});
            Check(after.GetProperty("data").GetProperty("configured").GetBoolean()&&
                  after.GetProperty("data").GetProperty("verifiedIni").GetBoolean(),
                "historical RCON status verifies existing password and port after Manager profile save");
        }
        Check(File.ReadAllBytes(ini).SequenceEqual(bytesBefore),
            "historical original GameUserSettings.ini bytes remain IDENTICAL after every attach test");
    }

    // Native original ACTIVE/DONE Store Start bridge: simulate the execution
    // boundary but NEVER call Process.Start or connect to the original UNC.
    // Actual production path uses the real source guard and OS process proof.
    {
        string historicalRoot=Path.Combine(root,"historical-active-launch");
        Directory.CreateDirectory(historicalRoot);
        string exe=Path.Combine(historicalRoot,"ArkAscendedServer.exe");
        File.WriteAllBytes(exe,new byte[]{77,90});
        Check(LegacyStoreV1ProcessObserver.PotentialOriginalWriter(exe,
                "ArkAscendedServer.exe",exe),
            "original Store Start blocks an existing ASA process with the same executable");
        Check(LegacyStoreV1ProcessObserver.PotentialOriginalWriter(exe,
                "AsaApiLoader.exe",Path.Combine(historicalRoot,"AsaApiLoader.exe")),
            "original Store Start blocks an existing ASA API loader from the same installation");
        Check(LegacyStoreV1ProcessObserver.PotentialOriginalWriter(exe,
                "ArkAscendedServer.exe",null),
            "unreadable original ASA process path never proves the installation stopped");
        Check(!LegacyStoreV1ProcessObserver.PotentialOriginalWriter(exe,
                "ArkAscendedServer.exe",Path.Combine(root,"other-install",
                    "ArkAscendedServer.exe")),
            "an unrelated ASA binary on another installation does not falsely claim the local map");
        Check(!LegacyStoreV1ProcessObserver.PotentialOriginalWriter(exe,
                "Unrelated.exe",exe),
            "unrelated processes never block original Store local process identity");

        await using var historic=new ClusterService("obsidian-native-original-start",
            Path.Combine(root,"historic-start-profile"),
            _=>new ProcessCheck(false,"Synthetic stopped source",null,true));
        var storeMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Ragnarok",InstallationPath=historicalRoot,
            ExecutablePath=exe,HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('A',64),
            HistoricalMapKey="Ragnarok_WP",
            GamePort=7787,QueryPort=27017,RconPort=37025,
            RconPasswordProtected=SecureStore.Protect("SyntheticOriginalRcon!"),
            Arguments=""
        };
        historic.AddMap(storeMap);

        // The original ten-map cluster may share ONE ASA EXE between maps.
        // Ignore another process only when it is an EXACT, different, locally
        // registered original Store-v1 map with source-accurate flags.
        var otherOriginalMap=new LocalMap{
            Id=Guid.NewGuid(),Name="The Island",
            InstallationPath=historicalRoot,ExecutablePath=exe,
            HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('A',64),
            HistoricalMapKey="TheIsland_WP",
            GamePort=7847,QueryPort=27087,RconPort=37087,
            HistoricalEnabledMods=new List<string>(),
            HistoricalPassiveMods=new List<string>()
        };
        string otherArgs=string.Join(" ",LegacyStoreV1ActiveLaunch.Build(new(
            otherOriginalMap.HistoricalMapKey,
            otherOriginalMap.GamePort,otherOriginalMap.QueryPort,
            otherOriginalMap.HistoricalEnabledMods,
            otherOriginalMap.HistoricalPassiveMods,
            "KovaCluster",@"\\192.0.2.1\shared","active","")).Arguments);
        string otherCommand="\"" + exe + "\" " + otherArgs;
        Check(LegacyStoreV1ProcessObserver.ProvenDistinctOwnedMap(
            otherCommand,exe,storeMap,new[]{storeMap,otherOriginalMap}),
            "legitimate original same-EXE different owned map is not a false stop/start blocker");
        Check(!LegacyStoreV1ProcessObserver.ProvenDistinctOwnedMap(
            otherCommand,exe,storeMap,new[]{storeMap}),
            "unregistered same-EXE process always blocks original Store-v1 start");
        Check(LegacyStoreV1ProcessObserver.DefinitelyDifferentMap(
                otherCommand,exe,storeMap.HistoricalMapKey) &&
              !LegacyStoreV1ProcessObserver.ProvenDistinctOwnedMap(
                otherCommand,exe,storeMap,new[]{storeMap}),
            "physical Store status must not use the weaker token-only peer heuristic");
        Check(!LegacyStoreV1ProcessObserver.ProvenDistinctOwnedMap(
            otherCommand+" -converttostore",exe,storeMap,
            new[]{storeMap,otherOriginalMap}),
            "same-EXE source conversion switch is not accepted as a safe different map");
        Check(!LegacyStoreV1ProcessObserver.ProvenDistinctOwnedMap(
            otherCommand.Replace("-Port=7847","-Port=7787"),exe,storeMap,
            new[]{storeMap,otherOriginalMap}),
            "another process with different map key but wrong source port stays blocked");

        // Loader can exit while its independently verified ASA child survives.
        // Only the exact, previously persisted PID + creation-time pair can
        // authorize read-only reattach; syntactic Store launch flags alone
        // cannot reconstruct the lost parent/child relationship.
        {
            var loaderPath=Path.Combine(Path.GetDirectoryName(exe)!,"AsaApiLoader.exe");
            var loaderCreated=new DateTimeOffset(2026,11,8,10,0,0,TimeSpan.Zero);
            var childCreated=loaderCreated.AddSeconds(6);
            var sourceChild=new ObservedProcess(57104,57103,childCreated,exe,
                "HISTORICAL_STORE_V1_SOURCE_VERIFIED");
            var pinnedLoader=new ProcessIdentity{Pid=57103,StartedUtc=loaderCreated,
                ExecutablePath=loaderPath,Arguments="HISTORICAL_STORE_V1_API_LOADER_VERIFIED"};
            var pinnedChild=new ProcessIdentity{Pid=57104,StartedUtc=childCreated,
                ExecutablePath=exe,Arguments="HISTORICAL_STORE_V1_API_CHILD_VERIFIED"};
            Check(LegacyStoreV1ProcessObserver.PreviouslyVerifiedOriginalApiChild(
                sourceChild,exe,loaderPath,pinnedChild,pinnedLoader),
                "original API orphan remains reattachable with exact old verified process family");
            Check(!LegacyStoreV1ProcessObserver.PreviouslyVerifiedOriginalApiChild(
                sourceChild with {StartedUtc=childCreated.AddSeconds(1)},
                exe,loaderPath,pinnedChild,pinnedLoader),
                "reused ASA PID with different creation time cannot reattach to old Loader");
            Check(LegacyStoreV1ProcessObserver.PreviouslyVerifiedOriginalApiChild(
                sourceChild with {ParentPid=57102},
                exe,loaderPath,pinnedChild,pinnedLoader),
                "previously verified exact PID/start pair survives Windows parent PID reuse");
            Check(!LegacyStoreV1ProcessObserver.PreviouslyVerifiedOriginalApiChild(
                sourceChild,exe,loaderPath,pinnedChild,
                new ProcessIdentity{Pid=pinnedLoader.Pid,StartedUtc=pinnedLoader.StartedUtc,
                    ExecutablePath=pinnedLoader.ExecutablePath,Arguments="UNVERIFIED_LOADER"}),
                "a prior unverified Loader observation cannot authorize orphan reattach");
        }

        // Kova can launch another ALREADY-ACTIVE original Store map through
        // AsaApiLoader.exe with the same source -usestore arguments. This
        // loader does not block an unrelated original map's Start or Stop
        // when its exact registered owner and original arguments are proven.
        string otherLoader=Path.Combine(Path.GetDirectoryName(exe)!,"AsaApiLoader.exe");
        otherOriginalMap.StartWithAsaApi=true;
        otherOriginalMap.ApiLoaderPath=otherLoader;
        string originalLoaderCommand="\"" + otherLoader + "\" " + otherArgs;
        Check(LegacyStoreV1ProcessObserver.ProvenDistinctOwnedApiLoader(
                originalLoaderCommand,exe,storeMap,new[]{storeMap,otherOriginalMap}),
            "separate original registered Store map's exact ASA API Loader launch does not block another map");
        Check(!LegacyStoreV1ProcessObserver.ProvenDistinctOwnedApiLoader(
                originalLoaderCommand,exe,storeMap,new[]{storeMap}),
            "unregistered original ASA API Loader remains a hard process-family blocker");
        Check(!LegacyStoreV1ProcessObserver.ProvenDistinctOwnedApiLoader(
                originalLoaderCommand+" -converttostore",exe,storeMap,
                new[]{storeMap,otherOriginalMap}),
            "ASA API Loader requesting a second Store conversion is never considered a safe other map");
        Check(!LegacyStoreV1ProcessObserver.ProvenDistinctOwnedApiLoader(
                originalLoaderCommand.Replace("-Port=7847","-Port=7787"),
                exe,storeMap,new[]{storeMap,otherOriginalMap}),
            "source-mismatched ASA API Loader port never proves legitimate other-map ownership");
        otherOriginalMap.StartWithAsaApi=false;
        Check(!LegacyStoreV1ProcessObserver.ProvenDistinctOwnedApiLoader(
                originalLoaderCommand,exe,storeMap,new[]{storeMap,otherOriginalMap}),
            "unconfigured loader mode cannot be guessed from an EXE path or process name");
        otherOriginalMap.StartWithAsaApi=true;

        // A direct original Store launch must not reuse approval if the
        // physical ASA EXE changed at the same registered path. This
        // explicit preflight hashes only the server binary, never world
        // saves or shared Store transfer files.
        string hashRoot=Path.Combine(root,"original-direct-exe-fingerprint");
        string hashBin=Path.Combine(hashRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(hashBin);
        string hashExe=Path.Combine(hashBin,"ArkAscendedServer.exe");
        byte[] beforeExeBytes={77,90,10,23};
        File.WriteAllBytes(hashExe,beforeExeBytes);
        var hashMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Ragnarok",InstallationPath=hashRoot,
            ExecutablePath=hashExe,StartWithAsaApi=false
        };
        string directSha=AsaReadyProbe.ApprovedLaunchBinariesFingerprint(hashMap);
        File.WriteAllBytes(hashExe,beforeExeBytes.Concat(new byte[]{0x42}).ToArray());
        string changedExeSha=AsaReadyProbe.ApprovedLaunchBinariesFingerprint(hashMap);
        Check(directSha.Length==64&&changedExeSha.Length==64&&
              directSha!=changedExeSha,
            "original direct ASA Start approval can bind physical executable content, not only its path");
        File.WriteAllBytes(hashExe,beforeExeBytes);
        Check(AsaReadyProbe.ApprovedLaunchBinariesFingerprint(hashMap)==directSha,
            "original ASA EXE fixture restored without touching world, Store or transfer data");
        string hashLoader=Path.Combine(hashBin,"AsaApiLoader.exe");
        File.WriteAllBytes(hashLoader,new byte[]{77,90,11,24});
        hashMap.ApiLoaderPath=hashLoader;
        hashMap.StartWithAsaApi=true;
        string loaderSha=AsaReadyProbe.ApprovedLaunchBinariesFingerprint(hashMap);
        File.AppendAllText(hashLoader,"changed");
        Check(AsaReadyProbe.ApprovedLaunchBinariesFingerprint(hashMap)!=loaderSha,
            "API Loader replacement invalidates exact original ASA/Loader start fingerprint");

        int launches=0,prepared=0;
        bool changedPlan=false;
        LegacyStoreV1NativeStartPlan Prepare(LocalMap original)
        {
            prepared++;
            Check(original.Id==storeMap.Id&&original.HistoricalStoreProtected,
                "native original Store Start checks locally registered historical map");
            return new("Ragnarok_WP?listen -Port=7787 -usestore -log",
                new string(changedPlan?'C':'B',64),7787,27017,37025,
                "SYNTHETIC_TWO_HOST_SOURCE_TEST_ONLY",
                OriginalTwoPeersVerified:true);
        }
        ProcessIdentity Start(Guid mapId,string fingerprint,bool confirmed)
        {
            launches++;
            Check(mapId==storeMap.Id&&fingerprint==new string('B',64)&&confirmed,
                "native real Start executor receives exactly its approved fingerprint and map");
            return new ProcessIdentity{
                Pid=42101,StartedUtc=DateTimeOffset.UtcNow,
                ExecutablePath=exe,Arguments="SYNTHETIC_NO_ACTUAL_PROCESS"
            };
        }
        // Native failures retain an exact bounded safety code without
        // leaking a filesystem path or starting/replaying an ASA process.
        using(var blockedBridge=new DesktopHistoricalActiveStartBridge(historic,
            _=>throw new InvalidOperationException("UM-STORE-NATIVE-PORT-IN-USE"),
            (_,_,_)=>throw new InvalidOperationException("Must not launch")))
        {
            var blocked=await Call(blockedBridge.HandleAsync,
                "historical.activeStartPreview",new{mapId=storeMap.Id});
            Check(IsError(blocked,"UM-STORE-NATIVE-PORT-IN-USE")&&launches==0,
                "Store-v1 native preview reports exact safe local port conflict without launch");
        }
        // Exact original source/Restart failure codes must be actionable;
        // never expose raw paths or arbitrary exception text to WebView.
        foreach(string safeCode in new[]{
            "UM-STORE-PIN-FILE-CHANGED",
            "UM-STORE-PIN-MAP-ROOT-CHANGED",
            "UM-STORE-PIN-MAP-IDENTITY",
            "UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR"})
        {
            using var denied=new DesktopHistoricalActiveStartBridge(historic,
                _=>throw new InvalidDataException(safeCode),
                (_,_,_)=>throw new InvalidOperationException("Must not launch"));
            var response=await Call(denied.HandleAsync,
                "historical.activeStartPreview",new{mapId=storeMap.Id});
            Check(IsError(response,safeCode)&&launches==0,
                "native Store Start preserves a concrete source/restart blocker without launching ASA");
        }
        using(var redacted=new DesktopHistoricalActiveStartBridge(historic,
            _=>throw new IOException("path C:\\SECRET\\my-key.txt"),
            (_,_,_)=>throw new InvalidOperationException("Must not launch")))
        {
            var response=await Call(redacted.HandleAsync,
                "historical.activeStartPreview",new{mapId=storeMap.Id});
            Check(IsError(response,"UM-STORE-ACTIVE-START-SAFETY-FAILED")&&
                  !response.ToString().Contains("SECRET",StringComparison.Ordinal),
                "native Store Start redacts unexpected filesystem and credential errors");
        }
        using(var bridge=new DesktopHistoricalActiveStartBridge(historic,
            Prepare,Start))
        {
            var unknown=await Call(bridge.HandleAsync,"historical.activeStartStatus",
                new{mapId=Guid.NewGuid()});
            Check(!unknown.GetProperty("ok").GetBoolean(),
                "original Store Start status refuses maps not owned by this host");
            var status=await Call(bridge.HandleAsync,"historical.activeStartStatus",
                new{mapId=storeMap.Id});
            IsOk(status,"native original Store Start read-only process status");
            Check(!status.GetProperty("data").GetProperty("running").GetBoolean()&&
                  status.GetProperty("data").GetProperty("rconConfigured").GetBoolean(),
                "registered Store-v1 map reports stopped process and attached original RCON");

            var preview=await Call(bridge.HandleAsync,"historical.activeStartPreview",
                new{mapId=storeMap.Id});
            IsOk(preview,"real native original Store-v1 Start is available after source preflight");
            Check(preview.GetProperty("data").GetProperty("originalTwoPeersVerified").GetBoolean()&&
                  preview.GetProperty("data").GetProperty("originalPeerCheckRequiredForLocalStart").GetBoolean()&&
                  !preview.GetProperty("data").GetProperty("conversionAllowed").GetBoolean()&&
                  !preview.ToString().Contains(historicalRoot,StringComparison.OrdinalIgnoreCase)&&
                  !preview.ToString().Contains("Ragnarok_WP?listen",StringComparison.Ordinal),
                "original Store Start preview proves source shape without exposing command line or source paths");
            string token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var withoutPhrase=await Call(bridge.HandleAsync,"historical.activeStartApply",
                new{token,confirm=true,typedApproval="START"});
            Check(!withoutPhrase.GetProperty("ok").GetBoolean()&&launches==0,
                "historical two-host Store Start cannot run without exact typed operator phrase");
            changedPlan=true;
            var moved=await Call(bridge.HandleAsync,"historical.activeStartApply",
                new{token,confirm=true,typedApproval="STORE-V1 ACTIVE START"});
            Check(!moved.GetProperty("ok").GetBoolean()&&launches==0,
                "changed original two-host source fingerprint blocks Start before spawning");
            changedPlan=false;
            // Preview remains intact on source mismatch but still requires
            // explicit second attempt and fresh source verification.
            var newPreview=await Call(bridge.HandleAsync,"historical.activeStartPreview",
                new{mapId=storeMap.Id});
            IsOk(newPreview,"operator can start a fresh source-verification attempt");
            token=newPreview.GetProperty("data").GetProperty("token").GetString()!;
            var started=await Call(bridge.HandleAsync,"historical.activeStartApply",
                new{token,confirm=true,typedApproval="STORE-V1 ACTIVE START"});
            IsOk(started,"native original Store-v1 Start calls owned executor only with valid typed consent");
            var receipt=started.GetProperty("data");
            Check(receipt.GetProperty("pid").GetInt32()==42101&&
                  !receipt.GetProperty("readyCertified").GetBoolean()&&
                  !receipt.GetProperty("conversionPerformed").GetBoolean()&&
                  !receipt.GetProperty("originalStoreMetadataChanged").GetBoolean()&&
                  !receipt.GetProperty("gameClientRejoinVerified").GetBoolean(),
                "native historical Start reports real PID only and never invents gameplay, Store conversion or travel proof");
            var replay=await Call(bridge.HandleAsync,"historical.activeStartApply",
                new{token,confirm=true,typedApproval="STORE-V1 ACTIVE START"});
            Check(!replay.GetProperty("ok").GetBoolean()&&launches==1,
                "original Store-v1 Start token is one-shot and never silently retries a process");
            Check(prepared>=4,
                "original Store Start checks source fingerprint before both preflight and apply");
            Check(historic.Config.LocalMaps.Single().Arguments=="" &&
                  historic.Config.LocalMaps.Single().LastProcess==null,
                "synthetic Start never changes original launch profile or Store source files");
        }
        // The API option must still use the exact original Store source
        // argument grammar. Its Process.Start receipt is ONLY the Loader
        // parent, never a fabricated ASA child PID or runtime-ready status.
        storeMap.StartWithAsaApi=true;
        storeMap.ApiLoaderPath=otherLoader;
        int apiLaunches=0;
        using(var loaderBridge=new DesktopHistoricalActiveStartBridge(historic,
            preparation:original=>{
                Check(original.StartWithAsaApi&&original.Arguments==""&&
                      original.ApiLoaderArguments=="",
                    "historical API opt-in never persists Store/UNC launch arguments");
                return new LegacyStoreV1NativeStartPlan(
                    "Ragnarok_WP?listen -Port=7787 -usestore -log",
                    new string('D',64),7787,27017,37025,
                    "SYNTHETIC_API_PARENT_ONLY",true);
            },
            launcher:(mapId,fingerprint,approved)=>{
                apiLaunches++;
                Check(mapId==storeMap.Id&&approved&&
                      fingerprint==new string('D',64),
                    "only the locally owned explicitly approved API map reaches its launcher");
                return new ProcessIdentity{
                    Pid=52703,StartedUtc=DateTimeOffset.UtcNow,
                    ExecutablePath=otherLoader,
                    Arguments="SYNTHETIC_LOADER_PARENT_ONLY"
                };
            }))
        {
            var apiPreview=await Call(loaderBridge.HandleAsync,
                "historical.activeStartPreview",new{mapId=storeMap.Id});
            IsOk(apiPreview,"original Store API Loader opt-in native preview");
            Check(apiPreview.GetProperty("data").GetProperty("launchMode").GetString()
                    =="asaApiLoader"&&apiLaunches==0&&
                  !apiPreview.GetProperty("data").GetProperty("originalTwoPeersVerified").GetBoolean()&&
                  !apiPreview.GetProperty("data").GetProperty("originalPeerCheckRequiredForLocalStart").GetBoolean(),
                "API Loader preview is explicit and does not start an ASA process");
            string approval=apiPreview.GetProperty("data")
                .GetProperty("token").GetString()!;
            var apiStarted=await Call(loaderBridge.HandleAsync,
                "historical.activeStartApply",
                new{token=approval,confirm=true,typedApproval="STORE-V1 ACTIVE START"});
            IsOk(apiStarted,"original Store API Loader executes only after native approval");
            var apiReceipt=apiStarted.GetProperty("data");
            Check(apiLaunches==1&&
                  apiReceipt.GetProperty("processRole").GetString()=="loaderParent"&&
                  apiReceipt.GetProperty("loaderPid").GetInt32()==52703&&
                  apiReceipt.GetProperty("serverPid").ValueKind==JsonValueKind.Null&&
                  !apiReceipt.GetProperty("readyCertified").GetBoolean()&&
                  !apiReceipt.GetProperty("gameClientRejoinVerified").GetBoolean(),
                "Loader parent PID is never mistaken for a running ASA child or readiness");
            var duplicate=await Call(loaderBridge.HandleAsync,
                "historical.activeStartApply",
                new{token=approval,confirm=true,typedApproval="STORE-V1 ACTIVE START"});
            Check(!duplicate.GetProperty("ok").GetBoolean()&&apiLaunches==1,
                "API loader original Store spawn remains one-shot across a repeated request");
        }
        // The optional API choice is saved only after a new, stopped-map,
        // owner-only native approval; it never invokes Process.Start.
        int modeChecks=0;
        using(var modeBridge=new DesktopHistoricalActiveStartBridge(historic,
            preparation:null,launcher:null,
            loaderModePreflight:(map,useApi)=>{
                modeChecks++;
                return map.Id==storeMap.Id&&map.HistoricalStoreProtected;
            }))
        {
            var modePreview=await Call(modeBridge.HandleAsync,
                "historical.loaderModePreview",
                new{mapId=storeMap.Id,useApi=false});
            IsOk(modePreview,"Store API Loader opt-out native preference preview");
            string modeToken=modePreview.GetProperty("data")
                .GetProperty("token").GetString()!;
            var unauthorized=await Call(modeBridge.HandleAsync,
                "historical.loaderModeApply",new{token=modeToken,confirm=false});
            Check(!unauthorized.GetProperty("ok").GetBoolean()&&
                  storeMap.StartWithAsaApi,
                "native Loader preference cannot be changed without explicit approval");
            var applied=await Call(modeBridge.HandleAsync,
                "historical.loaderModeApply",new{token=modeToken,confirm=true});
            IsOk(applied,"stopped original Store map supports saved direct ASA opt-out");
            Check(!storeMap.StartWithAsaApi&&apiLaunches==1,
                "native Store launch switch writes only local preference, not ASA process");
            var replay=await Call(modeBridge.HandleAsync,
                "historical.loaderModeApply",new{token=modeToken,confirm=true});
            Check(!replay.GetProperty("ok").GetBoolean(),
                "Store API mode preview token is one-shot");
            var optIn=await Call(modeBridge.HandleAsync,
                "historical.loaderModePreview",new{mapId=storeMap.Id,useApi=true});
            IsOk(optIn,"original Store API Loader opt-in requires stopped native preflight");
            modeToken=optIn.GetProperty("data").GetProperty("token").GetString()!;
            var enabled=await Call(modeBridge.HandleAsync,
                "historical.loaderModeApply",new{token=modeToken,confirm=true});
            IsOk(enabled,"original Store API Loader opt-in saved only after approval");
            Check(storeMap.StartWithAsaApi&&modeChecks>=4&&
                  storeMap.Arguments==""&&storeMap.ApiLoaderArguments=="",
                "native Loader mode cannot persist original UNC or Store switches");
        }
        storeMap.StartWithAsaApi=false;
        storeMap.ApiLoaderPath="";
    }

    // Read-only upstream ASA API cache identity is bound to the actual
    // ArkAscendedServer.exe byte hash, never an unrelated build or UNC file.
    {
        string apiRoot=Path.Combine(root,"synthetic-api-cache-identity");
        string binary=Path.Combine(apiRoot,"ShooterGame","Binaries","Win64");
        string exe=Path.Combine(binary,"ArkAscendedServer.exe");
        string cache=Path.Combine(binary,"ArkApi","Cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(exe,new byte[]{0x4D,0x5A,0,1,2,3,4,5,6,7,8,9});
        string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe)))
            .ToLowerInvariant();
        var missing=AsaApiCacheProbe.Inspect(apiRoot,exe);
        Check(missing.State=="UM-API-CACHE-NO-LOCAL-KEY"&&
              missing.ExecutableSha256==hash&&!missing.RuntimeCertified,
            "official API cache probe does not fabricate absent local cache proof");
        string key=Path.Combine(cache,"cached_key.cache");
        File.WriteAllText(key,new string('f',64));
        var wrong=AsaApiCacheProbe.Inspect(apiRoot,exe);
        Check(wrong.State=="UM-API-CACHE-EXE-HASH-MISMATCH"&&
              !wrong.KeyMatchesExecutable,
            "a different ASA exe SHA-256 never authorizes another build's cache");
        File.WriteAllText(key,JsonSerializer.Serialize(new{
            version=1,executable_hash=hash,
            cache_directory="../unknown",last_modified="synthetic"
        }));
        Check(AsaApiCacheProbe.Inspect(apiRoot,exe).State==
              "UM-API-CACHE-KEY-INVALID",
            "malformed or traversing official generation-directory metadata is rejected");
        string generation="generations/"+hash+"-1-2-3";
        string generationPath=Path.Combine(cache,"generations",hash+"-1-2-3");
        Directory.CreateDirectory(generationPath);
        File.WriteAllBytes(Path.Combine(generationPath,"cached_offsets.cache"),
            new byte[]{1,2,3});
        File.WriteAllBytes(Path.Combine(generationPath,"cached_bitfields.cache"),
            new byte[]{4,5,6});
        File.WriteAllText(key,JsonSerializer.Serialize(new{
            version=1,executable_hash=hash,cache_directory=generation,
            last_modified="synthetic"
        }));
        var cacheCandidate=AsaApiCacheProbe.Inspect(apiRoot,exe);
        Check(cacheCandidate.State=="UM-API-CACHE-HASH-BOUND-CANDIDATE"&&
              cacheCandidate.KeyMatchesExecutable&&cacheCandidate.CandidateFilesPresent&&
              !cacheCandidate.RuntimeCertified,
            "hash-bound API cache generation is observed but never certified runtime-loaded");
        File.AppendAllText(exe,"modified");
        Check(AsaApiCacheProbe.Inspect(apiRoot,exe).State==
              "UM-API-CACHE-EXE-HASH-MISMATCH",
            "mutated ASA executable invalidates previously matching cache metadata");
    }

    // Actual original Store-v1 RCON channel is distinct from fresh ASA
    // endpoints and permits exactly the three READ-ONLY commands. No WMI,
    // real socket, ASA Process.Start, or original live Store access in tests.
    {
        string historicalRoot=Path.Combine(root,"historical-rcon-readonly-owned");
        string bin=Path.Combine(historicalRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(bin);
        string exe=Path.Combine(bin,"ArkAscendedServer.exe");
        File.WriteAllBytes(exe,new byte[]{77,90,2,0});
        string iniDir=Path.Combine(historicalRoot,"ShooterGame","Saved",
            "Config","WindowsServer");
        Directory.CreateDirectory(iniDir);
        string ini=Path.Combine(iniDir,"GameUserSettings.ini");
        const string secret="HistoricalReadOnlyPass!2026";
        const int port=37165;
        File.WriteAllText(ini,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+port+"\r\nServerAdminPassword="+secret+"\r\n");
        byte[] originalIni=File.ReadAllBytes(ini);
        await using var historical=new ClusterService("original-readonly-rcon",
            Path.Combine(root,"historical-readonly-rcon-profile"),m=>
                new ProcessCheck(false,"Synthetic external process observation",null,true));
        var originalRconMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Aberration",
            InstallationPath=historicalRoot,ExecutablePath=exe,
            HistoricalStoreProtected=true,HistoricalMapKey="Aberration_WP",
            HistoricalArchiveSha256=new string('A',64),
            GamePort=7877,QueryPort=27177,RconPort=port,
            RconPasswordProtected=SecureStore.Protect(secret)
        };
        historical.AddMap(originalRconMap);
        bool sourceReady=true,listenerOwned=true;
        string sourceEpoch=new string('A',64);
        bool changeEpochBeforeDispatch=false,changeEpochDuringDispatch=false;
        int epochReads=0;
        int sent=0;
        int currentPid=43210;
        using(var bridge=new DesktopHistoricalRconReadOnlyBridge(historical,
            processInspector:_=>new ProcessCheck(true,"Synthetic owned",new ProcessIdentity{
                Pid=currentPid,StartedUtc=new DateTimeOffset(2026,11,7,10,0,0,TimeSpan.Zero),
                ExecutablePath=exe,Arguments="SYNTHETIC_ORIGINAL_ACTIVE"},false),
            sourceEpochProof:_=>{
                epochReads++;
                if(changeEpochBeforeDispatch&&epochReads>=2)
                    sourceEpoch=new string('B',64);
                return sourceReady?sourceEpoch:"invalid";
            },
            portOwner:(p,pid)=>p==port&&pid==43210&&listenerOwned,
            dispatch:(host,actualPort,password,command,ct)=>{
                Check(host=="127.0.0.1"&&actualPort==port&&password==secret,
                    "original Store RCON dispatch uses only owned local loopback port and DPAPI secret");
                Check(command is "ListPlayers" or "GetGameLog" or "GetChat",
                    "original Store RCON never transmits SaveWorld, DoExit, or arbitrary mutations");
                sent++;
                if(changeEpochDuringDispatch)
                    sourceEpoch=new string('C',64);
                return Task.FromResult("SYNTHETIC_"+command);
            }))
        {
            var unknown=await Call(bridge.HandleAsync,"historical.rconLiveStatus",
                new{mapId=Guid.NewGuid()});
            Check(!unknown.GetProperty("ok").GetBoolean(),
                "original Store RCON rejects remote and unregistered map identities");
            var status=await Call(bridge.HandleAsync,"historical.rconLiveStatus",
                new{mapId=originalRconMap.Id});
            IsOk(status,"original Store owned PID and RCON status");
            Check(status.GetProperty("data").GetProperty("permitted").GetBoolean()&&
                  status.GetProperty("data").GetProperty("pid").GetInt32()==43210,
                "original ACTIVE/DONE source and TCP owner allow read-only commands");
            foreach(var (method,command) in new[]{
                ("historical.rconPlayers","ListPlayers"),
                ("historical.rconGameLog","GetGameLog"),
                ("historical.rconChatLog","GetChat")})
            {
                var reply=await Call(bridge.HandleAsync,method,new{mapId=originalRconMap.Id});
                IsOk(reply,"original Store read-only "+command);
                Check(reply.GetProperty("data").GetProperty("command").GetString()==command&&
                      reply.GetProperty("data").GetProperty("readOnly").GetBoolean()&&
                      !reply.GetProperty("data").GetProperty("storeTouched").GetBoolean()&&
                      reply.GetProperty("data").GetProperty("response").GetString()=="SYNTHETIC_"+command,
                    "original Store native RCON command response verifies source and does not write");
            }
            Check(sent==3,"three original RCON queries each dispatch exactly once");
            var blockedWrite=await Call(bridge.HandleAsync,"historical.rconSaveWorld",
                new{mapId=originalRconMap.Id});
            Check(!blockedWrite.GetProperty("ok").GetBoolean()&&sent==3,
                "original Store RCON rejects mutating and unknown bridge methods");
            sourceReady=false;
            var stale=await Call(bridge.HandleAsync,"historical.rconPlayers",
                new{mapId=originalRconMap.Id});
            Check(!stale.GetProperty("ok").GetBoolean()&&sent==3,
                "changed original two-peer ACTIVE/DONE source blocks RCON before dispatch");
            sourceReady=true;listenerOwned=false;
            var hijacked=await Call(bridge.HandleAsync,"historical.rconGameLog",
                new{mapId=originalRconMap.Id});
            Check(!hijacked.GetProperty("ok").GetBoolean()&&sent==3,
                "RCON listener belonging to another PID never receives a Store RCON command");
            listenerOwned=true;currentPid=43211;
            var replaced=await Call(bridge.HandleAsync,"historical.rconPlayers",
                new{mapId=originalRconMap.Id});
            Check(!replaced.GetProperty("ok").GetBoolean()&&sent==3,
                "replaced original Store ASA PID is not authorized by previous TCP owner");
            currentPid=43210;listenerOwned=true;sourceReady=true;
            sourceEpoch=new string('A',64);
            changeEpochBeforeDispatch=true;
            epochReads=0;
            var preSwap=await Call(bridge.HandleAsync,"historical.rconPlayers",
                new{mapId=originalRconMap.Id});
            Check(IsError(preSwap,"UM-ORIGINAL-RCON-SOURCE-EPOCH-CHANGED")&&sent==3,
                "new valid Store epoch between preflight and send blocks RCON before bytes");
            changeEpochBeforeDispatch=false;
            sourceEpoch=new string('A',64);
            changeEpochDuringDispatch=true;
            var duringSwap=await Call(bridge.HandleAsync,"historical.rconChatLog",
                new{mapId=originalRconMap.Id});
            Check(IsError(duringSwap,"UM-ORIGINAL-RCON-SOURCE-EPOCH-CHANGED")&&sent==4,
                "source epoch change while read-only RCON is in flight never verifies old reply");
            changeEpochDuringDispatch=false;
            sourceEpoch=new string('A',64);
            var afterRecovery=await Call(bridge.HandleAsync,"historical.rconPlayers",
                new{mapId=originalRconMap.Id});
            Check(afterRecovery.GetProperty("ok").GetBoolean()&&sent==5,
                "fresh operator read-only RCON may proceed after a new stable source epoch is verified");
        }
        Check(File.ReadAllBytes(ini).SequenceEqual(originalIni),
            "every native original Store RCON query preserves exact ASA INI file bytes");

        // Independent Store maps have independent RCON request lanes. If one
        // ASA does not reply, a different locally owned map must remain usable.
        string secondRoot=Path.Combine(root,"historical-rcon-readonly-second-map");
        string secondBin=Path.Combine(secondRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(secondBin);
        string secondExe=Path.Combine(secondBin,"ArkAscendedServer.exe");
        File.WriteAllBytes(secondExe,new byte[]{77,90,2,0});
        string secondIniDir=Path.Combine(secondRoot,"ShooterGame","Saved",
            "Config","WindowsServer");
        Directory.CreateDirectory(secondIniDir);
        string secondIni=Path.Combine(secondIniDir,"GameUserSettings.ini");
        const int secondPort=37175;
        File.WriteAllText(secondIni,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+secondPort+"\r\nServerAdminPassword="+secret+"\r\n");
        var secondMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Valguero",
            InstallationPath=secondRoot,ExecutablePath=secondExe,
            HistoricalStoreProtected=true,HistoricalMapKey="Valguero_WP",
            HistoricalArchiveSha256=new string('A',64),
            GamePort=7887,QueryPort=27187,RconPort=secondPort,
            RconPasswordProtected=SecureStore.Protect(secret)
        };
        historical.AddMap(secondMap);
        byte[] firstBeforeParallel=File.ReadAllBytes(ini);
        byte[] secondBeforeParallel=File.ReadAllBytes(secondIni);
        var firstDispatched=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstReply=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int firstDispatches=0,secondDispatches=0;
        using(var parallel=new DesktopHistoricalRconReadOnlyBridge(historical,
            processInspector:map=>new ProcessCheck(true,"synthetic per-map process",
                new ProcessIdentity{
                    Pid=map.Id==secondMap.Id?43220:43210,
                    StartedUtc=new DateTimeOffset(2026,11,7,10,0,0,TimeSpan.Zero),
                    ExecutablePath=map.ExecutablePath,
                    Arguments="SYNTHETIC_ORIGINAL_ACTIVE"},false),
            sourceEpochProof:_=>new string('A',64),
            portOwner:(requestedPort,pid)=>
                (requestedPort==port&&pid==43210)||
                (requestedPort==secondPort&&pid==43220),
            dispatch:async(host,requestedPort,password,command,ct)=>{
                Check(host=="127.0.0.1"&&password==secret&&
                      command is "ListPlayers" or "GetGameLog" or "GetChat",
                    "each original Store RCON lane stays on its own local read-only channel");
                if(requestedPort==port)
                {
                    Interlocked.Increment(ref firstDispatches);
                    firstDispatched.TrySetResult(true);
                    await allowFirstReply.Task.WaitAsync(ct);
                }
                else Interlocked.Increment(ref secondDispatches);
                return "SYNTHETIC_"+command;
            }))
        {
            var stalledFirst=Call(parallel.HandleAsync,"historical.rconPlayers",
                new{mapId=originalRconMap.Id});
            await firstDispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var independent=await Call(parallel.HandleAsync,
                    "historical.rconGameLog",new{mapId=secondMap.Id})
                    .WaitAsync(TimeSpan.FromSeconds(3));
                IsOk(independent,"another ASA RCON map is usable while one map's reply is held");
                Check(independent.GetProperty("data").GetProperty("mapId").GetGuid()==
                      secondMap.Id&&
                      independent.GetProperty("data").GetProperty("command").GetString()==
                      "GetGameLog"&&
                      secondDispatches==1&&firstDispatches==1,
                    "simultaneous original Store RCON commands stay bound to distinct map IDs");
            }
            finally{allowFirstReply.TrySetResult(true);}
            var released=await stalledFirst.WaitAsync(TimeSpan.FromSeconds(5));
            IsOk(released,"the stalled first RCON map resumes without duplicate dispatch");
            Check(firstDispatches==1&&
                  File.ReadAllBytes(ini).SequenceEqual(firstBeforeParallel)&&
                  File.ReadAllBytes(secondIni).SequenceEqual(secondBeforeParallel),
                "parallel original Store RCON reads dispatch once and preserve both INI files");
        }
    }

    // The gallery consumes the native snapshot, never static Store flags:
    // an uncertain journal takes precedence over a running process.
    {
        var storeStateFixture=new LocalMap{
            HistoricalStoreProtected=true,RconPort=37015,
            RconPasswordProtected="SYNTHETIC_PROTECTED_SECRET"
        };
        Check(DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                storeStateFixture,false,false,false)=="preflightRequired",
            "stopped original Store requires actual native start preflight");
        Check(DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                storeStateFixture,true,false,false)=="running",
            "observed Store process reports running without claiming gameplay certification");
        Check(DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                storeStateFixture,true,true,false)=="needsOperator"&&
              DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                storeStateFixture,true,false,true)=="needsOperator",
            "uncertain or unreadable journal overrides running Store process");
        Check(DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                storeStateFixture,true,false,false,AsaRestartPhase.Starting)=="restartPending"&&
              DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                storeStateFixture,true,false,false,AsaRestartPhase.Stopping)=="restartPending"&&
              DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                storeStateFixture,true,false,false,AsaRestartPhase.NeedsOperator)=="needsOperator",
            "Restart in Stopping/Starting shows a blocked native journal instead of operable Store");
        storeStateFixture.RconPasswordProtected="";
        Check(DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                storeStateFixture,false,false,false)=="needsConfiguration",
            "Store missing original RCON handoff cannot appear ready");
        storeStateFixture.Maintenance=true;
        Check(DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                storeStateFixture,false,false,false)=="maintenance"&&
              DesktopReadOnlyBridge.ClassifyOriginalStoreLifecycle(
                new LocalMap(),true,false,false)=="protected",
            "maintenance and non-imported protected maps cannot appear operable");
    }

    // REAL Store-v1 SaveWorld-only executes the same Core source/PID/RCON
    // ownership checks as graceful Stop, but never calls DoExit.
    // All RCON effects and process observations below are synthetic.
    {
        string saveRoot=Path.Combine(root,"historical-original-save-only");
        string saveBin=Path.Combine(saveRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(saveBin);
        string exe=Path.Combine(saveBin,"ArkAscendedServer.exe");
        File.WriteAllBytes(exe,new byte[]{77,90,0,2});
        string config=Path.Combine(saveRoot,"ShooterGame","Saved","Config","WindowsServer");
        Directory.CreateDirectory(config);
        const string password="OriginalStoreSaveOnlyTest2026";
        const int rconPort=37205,expectedPid=43631;
        string ini=Path.Combine(config,"GameUserSettings.ini");
        File.WriteAllText(ini,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+rconPort+"\r\nServerAdminPassword="+password+"\r\n");
        byte[] beforeIni=File.ReadAllBytes(ini);
        await using var saveCluster=new ClusterService("store-save-only-synthetic",
            Path.Combine(root,"store-save-only-profile"),
            _=>new ProcessCheck(false,"Synthetic process",null,true));
        var saveMap=new LocalMap{
            Id=Guid.NewGuid(),Name="The Center",HistoricalStoreProtected=true,
            HistoricalMapKey="TheCenter_WP",HistoricalArchiveSha256=new string('A',64),
            InstallationPath=saveRoot,ExecutablePath=exe,
            GamePort=7889,QueryPort=27189,RconPort=rconPort,
            RconPasswordProtected=SecureStore.Protect(password)
        };
        saveCluster.AddMap(saveMap);
        var processStart=new DateTimeOffset(2026,11,7,11,0,0,TimeSpan.Zero);
        const string sourceSha="EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";
        int saveDispatches=0,exitDispatches=0;
        bool shouldFail=false;
        bool pauseFirst=true;
        var releaseFirstSave=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessCheck Running(LocalMap current)=>new(true,"Synthetic ACTIVE owner",
            new ProcessIdentity{Pid=expectedPid,StartedUtc=processStart,
                ExecutablePath=exe,Arguments="ORIGINAL_STORE_SYNTHETIC"},false);
        string Source(LocalMap current)=>sourceSha;
        bool RconOwned(int port,int pid)=>port==rconPort&&pid==expectedPid;
        LegacyStoreV1GracefulStop.Owned Probe(Guid id)=>
            LegacyStoreV1GracefulStop.Preview(saveCluster,id,Running,Source,RconOwned);
        Task<AsaGracefulStopReceipt> ForbiddenStop(Guid id,int pid,DateTimeOffset at,
            string source,Action<string> report)
        {
            exitDispatches++;
            return Task.FromException<AsaGracefulStopReceipt>(
                new InvalidOperationException("SaveWorld-only may never call Stop"));
        }
        Task<LegacyStoreV1GracefulStop.SaveOnlyReceipt> Save(Guid id,int pid,
            DateTimeOffset at,string source,Action<string> report)=>
            LegacyStoreV1GracefulStop.SaveWorldOnlyAsync(saveCluster,id,pid,at,
                source,TimeSpan.FromSeconds(6),
                inspect:Running,source:Source,portOwner:RconOwned,
                execute:async (selected,identity,verify,token)=>{
                    verify();saveDispatches++;
                    if(pauseFirst)await releaseFirstSave.Task.WaitAsync(token);
                    if(shouldFail)throw new IOException("Synthetic mid-SaveWorld interruption");
                    report("Synthetic SaveWorld dispatched without DoExit");
                    verify();
                    await Task.CompletedTask;
                    return true;
                },stage:report);
        using(var bridge=new DesktopHistoricalGracefulStopBridge(saveCluster,
            Probe,ForbiddenStop,Save))
        {
            var unauthorized=await Call(bridge.HandleAsync,
                "historical.saveWorldPreview",new{mapId=Guid.NewGuid()});
            Check(!unauthorized.GetProperty("ok").GetBoolean()&&saveDispatches==0,
                "SaveWorld-only denies foreign maps before any RCON dispatch");
            var preview=await Call(bridge.HandleAsync,"historical.saveWorldPreview",
                new{mapId=saveMap.Id});
            IsOk(preview,"original ACTIVE Store SaveWorld-only preflight");
            Check(!preview.GetProperty("data").GetProperty("usesDoExit").GetBoolean()&&
                preview.GetProperty("data").GetProperty("usesSaveWorld").GetBoolean()&&
                !preview.GetProperty("data").GetProperty("originalTwoPeersVerified").GetBoolean()&&
                preview.GetProperty("data").GetProperty("originalPeerCheckRequiredForLocalOperation").GetBoolean()&&
                !preview.GetRawText().Contains(password,StringComparison.Ordinal),
                "SaveWorld-only preview is explicit and never reveals Store credentials");
            string token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var wrongKind=await Call(bridge.HandleAsync,"historical.gracefulStopApply",
                new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD DOEXIT"});
            Check(!wrongKind.GetProperty("ok").GetBoolean()&&saveDispatches==0,
                "SaveWorld-only approval cannot be replayed as graceful Stop");
            var wrongPhrase=await Call(bridge.HandleAsync,"historical.saveWorldApply",
                new{token,confirm=true,typedApproval="SAVEWORLD"});
            Check(!wrongPhrase.GetProperty("ok").GetBoolean()&&saveDispatches==0,
                "Store SaveWorld requires the exact typed approval");
            var queued=await Call(bridge.HandleAsync,"historical.saveWorldApply",
                new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD"});
            IsOk(queued,"Store SaveWorld-only native task queued");
            string jobId=queued.GetProperty("data").GetProperty("jobId").GetString()!;
            var inFlight=await Call(bridge.HandleAsync,
                "historical.gracefulStopRecovery",new{mapId=saveMap.Id});
            IsOk(inFlight,"original SaveWorld native operation reattach read");
            Check(inFlight.GetProperty("data").GetProperty("inMemoryRunning").GetBoolean()&&
                  inFlight.GetProperty("data").GetProperty("activeJobKind").GetString()=="SaveWorldOnly"&&
                  inFlight.GetProperty("data").GetProperty("jobId").GetString()==jobId,
                "SaveWorld UI reattaches to exact native live job identity, not Stop");
            var wrongStatus=await Call(bridge.HandleAsync,
                "historical.gracefulStopStatus",new{jobId});
            Check(!wrongStatus.GetProperty("ok").GetBoolean(),
                "Stop status cannot be used to replay or impersonate SaveWorld job");
            pauseFirst=false;releaseFirstSave.TrySetResult(true);
            var replay=await Call(bridge.HandleAsync,"historical.saveWorldApply",
                new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD"});
            Check(!replay.GetProperty("ok").GetBoolean(),
                "SaveWorld-only approval token cannot dispatch twice");
            JsonElement status=default;
            for(int i=0;i<80;i++)
            {
                status=await Call(bridge.HandleAsync,"historical.saveWorldStatus",
                    new{jobId});
                if(status.GetProperty("data").GetProperty("state").GetString()
                    is "completed" or "needsOperator")break;
                await Task.Delay(30);
            }
            Check(status.GetProperty("data").GetProperty("state").GetString()=="completed"&&
                  status.GetProperty("data").GetProperty("kind").GetString()=="SaveWorldOnly"&&
                  !status.GetProperty("data").GetProperty("confirmedStopped").GetBoolean()&&
                  saveDispatches==1&&exitDispatches==0,
                "Store SaveWorld executes exactly once without DoExit or process shutdown");
            var receiptView=await Call(bridge.HandleAsync,
                "historical.saveWorldEvidence",new{mapId=saveMap.Id});
            IsOk(receiptView,"native SaveComplete receipt read through real WinForms bridge");
            var receiptData=receiptView.GetProperty("data");
            Check(receiptData.GetProperty("phase").GetString()=="completed"&&
                  receiptData.GetProperty("localSaveCompleteObserved").GetBoolean()&&
                  !receiptData.GetProperty("bothHostsSaveCompleteCertified").GetBoolean()&&
                  !receiptData.GetProperty("sharedStoreWriterCertified").GetBoolean()&&
                  !receiptData.GetProperty("productionRestoreAllowed").GetBoolean()&&
                  receiptData.GetProperty("pid").GetInt32()==expectedPid,
                "WinForms SaveComplete status is durable and explicitly LOCAL ONLY");
            Check(LegacyStoreV1SaveEvidenceJournal.Inspect(
                    saveCluster.Store.Root,saveMap.Id)?.Phase=="completed",
                "native SaveWorld success is recorded in independent Manager-local SHA-256 journal");
            Check(saveCluster.Operations.Observe(saveRoot)?.Phase==
                    UntamedOperationPhase.Completed,
                "successfully observed original Store SaveWorld closes its durable journal");
            // Failure after claim permanently requires operator review, not retry.
            shouldFail=true;
            var second=await Call(bridge.HandleAsync,"historical.saveWorldPreview",
                new{mapId=saveMap.Id});
            IsOk(second,"new independently approved original Store SaveWorld preview");
            token=second.GetProperty("data").GetProperty("token").GetString()!;
            queued=await Call(bridge.HandleAsync,"historical.saveWorldApply",
                new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD"});
            IsOk(queued,"synthetic interrupted Store SaveWorld job started");
            jobId=queued.GetProperty("data").GetProperty("jobId").GetString()!;
            for(int i=0;i<80;i++)
            {
                status=await Call(bridge.HandleAsync,"historical.saveWorldStatus",
                    new{jobId});
                if(status.GetProperty("data").GetProperty("state").GetString()
                    is "completed" or "needsOperator")break;
                await Task.Delay(30);
            }
            Check(status.GetProperty("data").GetProperty("state").GetString()=="needsOperator"&&
                  saveCluster.Operations.Observe(saveRoot)?.Phase==
                    UntamedOperationPhase.NeedsOperator&&
                  saveDispatches==2&&exitDispatches==0,
                "failed SaveWorld leaves persistent NeedsOperator and never dispatches DoExit");
            var interruptedSave=await Call(bridge.HandleAsync,
                "historical.saveWorldEvidence",new{mapId=saveMap.Id});
            Check(interruptedSave.GetProperty("ok").GetBoolean()&&
                  interruptedSave.GetProperty("data").GetProperty("phase").GetString()=="needsOperator"&&
                  !interruptedSave.GetProperty("data").GetProperty("localSaveCompleteObserved").GetBoolean(),
                "failed operation never inherits previously completed SaveComplete claim");
            var recoverySave=await Call(bridge.HandleAsync,
                "historical.gracefulStopRecovery",new{mapId=saveMap.Id});
            Check(recoverySave.GetProperty("data").GetProperty("blocked").GetBoolean()&&
                  recoverySave.GetProperty("data").GetProperty("requiresOperator").GetBoolean(),
                "after interrupted SaveWorld, native recovery blocks future mutating Store operations");
            var blocked=await Call(bridge.HandleAsync,"historical.saveWorldPreview",
                new{mapId=saveMap.Id});
            Check(!blocked.GetProperty("ok").GetBoolean()&&saveDispatches==2,
                "unresolved original Store operation blocks another SaveWorld");
        }
        using(var again=new DesktopHistoricalGracefulStopBridge(saveCluster,
            Probe,ForbiddenStop,Save))
        {
            var recovery=await Call(again.HandleAsync,
                "historical.gracefulStopRecovery",new{mapId=saveMap.Id});
            Check(recovery.GetProperty("data").GetProperty("requiresOperator").GetBoolean()&&
                  recovery.GetProperty("data").GetProperty("blocked").GetBoolean()&&
                  !recovery.GetProperty("data").GetProperty("inMemoryRunning").GetBoolean()&&
                  recovery.GetProperty("data").GetProperty("activeJobKind").ValueKind==
                    JsonValueKind.Null,
                "fresh Manager observes prior Store SaveWorld interruption without auto-replay");
            var persisted=await Call(again.HandleAsync,
                "historical.saveWorldEvidence",new{mapId=saveMap.Id});
            Check(persisted.GetProperty("ok").GetBoolean()&&
                  persisted.GetProperty("data").GetProperty("phase").GetString()=="needsOperator"&&
                  !persisted.GetProperty("data").GetProperty("productionRestoreAllowed").GetBoolean(),
                "a fresh native bridge independently recovers the uncertain SHA-256 SaveComplete receipt");
        }
        Check(File.ReadAllBytes(ini).SequenceEqual(beforeIni)&&exitDispatches==0,
            "SaveWorld-only never edits original ASA INI or runs DoExit");
    }


    // Original ACTIVE Store-v1 manual restart: the production bridge
    // authorizes a durable one-shot Stop -> verified PID exit -> Start.
    // All process identities and RCON effects are synthetic here.
    {
        string restartRoot=Path.Combine(root,"historical-original-restart-owned");
        string binary=Path.Combine(restartRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binary);
        string exe=Path.Combine(binary,"ArkAscendedServer.exe");
        File.WriteAllBytes(exe,new byte[]{77,90,3,3});
        string cfg=Path.Combine(restartRoot,"ShooterGame","Saved","Config","WindowsServer");
        Directory.CreateDirectory(cfg);
        string ini=Path.Combine(cfg,"GameUserSettings.ini");
        File.WriteAllText(ini,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort=37305\r\nServerAdminPassword=SyntheticRestartOnly\r\n");
        byte[] beforeRestartIni=File.ReadAllBytes(ini);
        await using var restartCluster=new ClusterService(
            "store-restart-synthetic",
            Path.Combine(root,"store-restart-profile"),
            _=>new ProcessCheck(false,"Synthetic original process",null,true));
        var restartMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Valguero",
            HistoricalStoreProtected=true,HistoricalMapKey="Valguero_WP",
            HistoricalArchiveSha256=new string('A',64),
            InstallationPath=restartRoot,ExecutablePath=exe,
            GamePort=7891,QueryPort=27191,RconPort=37305,
            RconPasswordProtected=SecureStore.Protect("SyntheticRestartOnly")
        };
        restartCluster.AddMap(restartMap);
        const string restartSource="CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        int oldRestartPid=44311,saveExitCalls=0,newStarts=0;
        bool failNextStart=false,oldRestartExited=false;
        Action<string>? lateStoreRestartProgress=null;
        var oldStartTime=new DateTimeOffset(2026,11,8,11,0,0,TimeSpan.Zero);
        var releaseOldExit=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        LegacyStoreV1GracefulStop.Owned RestartProbe(Guid mapId)=>
            mapId==restartMap.Id?
                new(restartMap,new ProcessIdentity{
                    Pid=oldRestartPid,StartedUtc=oldStartTime,
                    ExecutablePath=exe,Arguments="SYNTHETIC_ORIGINAL_ACTIVE"},
                    restartSource):
                throw new UnauthorizedAccessException("Foreign original Store map");
        async Task<AsaGracefulStopReceipt> RestartStop(Guid mapId,int pid,
            DateTimeOffset started,string source,Guid journalId,
            Action<string> report)
        {
            saveExitCalls++;
            lateStoreRestartProgress=report;
            var journal=new AsaManualRestartJournal(restartRoot).Observe();
            Check(journal!=null&&journal.Phase==AsaRestartPhase.Stopping&&
                  journal.Id==journalId&&journal.MapId==restartMap.Id&&
                  pid==oldRestartPid&&started==oldStartTime&&
                  source==restartSource,
                "native Store Restart Stop receives exact durable writer/owner approval");
            ProcessCheck Inspect(LocalMap selected)=>oldRestartExited?
                new(false,"Synthetic original PID exited",null,true):
                new(true,"Synthetic original ACTIVE owner",new ProcessIdentity{
                    Pid=oldRestartPid,StartedUtc=oldStartTime,
                    ExecutablePath=exe,Arguments="SYNTHETIC_ORIGINAL_ACTIVE"},false);
            ProcessCheck Stopped(LocalMap selected)=>oldRestartExited?
                new(false,"Synthetic original PID exited",null,true):
                Inspect(selected);
            string Source(LocalMap selected)=>restartSource;
            bool Owner(int port,int ownerPid)=>port==37305&&ownerPid==oldRestartPid;
            Task<AsaGracefulStopReceipt> StopCore(Guid approvedId)=>
                LegacyStoreV1GracefulStop.StopAsync(restartCluster,mapId,
                    pid,started,source,TimeSpan.FromSeconds(7),
                    inspect:Inspect,stopped:Stopped,source:Source,portOwner:Owner,
                    execute:async (selected,identity,verify,cancellation)=>{
                        verify();
                        report("Synthetic SaveWorld -> DoExit");
                        await releaseOldExit.Task.WaitAsync(cancellation);
                        verify();
                        oldRestartExited=true;
                        return true;
                    },stage:report,approvedRestartId:approvedId);
            bool unauthorizedRejected=false;
            try{await StopCore(Guid.NewGuid());}
            catch(InvalidOperationException){unauthorizedRejected=true;}
            Check(unauthorizedRejected&&!oldRestartExited,
                "real original Store Stop Core rejects wrong durable restart ID");
            return await StopCore(journalId);
        }
        ProcessIdentity RestartStart(Guid mapId,Guid journalId,
            Action<string> report)
        {
            newStarts++;
            var journal=new AsaManualRestartJournal(restartRoot).Observe();
            Check(journal!=null&&journal.Id==journalId&&
                  journal.Phase==AsaRestartPhase.Starting&&
                  journal.MapId==mapId&&mapId==restartMap.Id,
                "original Store Start requires the same journal in Starting phase");
            if(failNextStart)
                throw new IOException("Synthetic original restart spawn failure");
            report("Synthetic new original ACTIVE process spawned");
            var started=new ProcessIdentity{
                Pid=oldRestartPid+1,StartedUtc=oldStartTime.AddSeconds(10),
                ExecutablePath=exe,Arguments="SYNTHETIC_ORIGINAL_ACTIVE"
            };
            oldRestartPid=started.Pid;
            oldStartTime=started.StartedUtc;
            oldRestartExited=false;
            return started;
        }
        using(var restartBridge=new DesktopHistoricalRestartBridge(
            restartCluster,RestartProbe,RestartStop,RestartStart))
        {
            var foreign=await Call(restartBridge.HandleAsync,
                "historical.restartPreview",new{mapId=Guid.NewGuid()});
            Check(!foreign.GetProperty("ok").GetBoolean()&&
                  saveExitCalls==0&&newStarts==0,
                "Store Restart never accepts foreign or remote map IDs");
            var approved=await Call(restartBridge.HandleAsync,
                "historical.restartPreview",new{mapId=restartMap.Id});
            IsOk(approved,"original native Store Restart preflight");
            var token=approved.GetProperty("data").GetProperty("token").GetString()!;
            var wrong=await Call(restartBridge.HandleAsync,
                "historical.restartApply",new{token,confirm=true,
                    typedApproval="STORE-V1 SAVEWORLD DOEXIT"});
            Check(!wrong.GetProperty("ok").GetBoolean()&&
                  saveExitCalls==0&&newStarts==0,
                "Store Restart requires exact typed Start approval before any mutation");
            var queued=await Call(restartBridge.HandleAsync,
                "historical.restartApply",new{token,confirm=true,
                    typedApproval="STORE-V1 SAVEWORLD DOEXIT START"});
            IsOk(queued,"native original Store Restart job queued");
            string jobId=queued.GetProperty("data").GetProperty("jobId").GetString()!;
            var replay=await Call(restartBridge.HandleAsync,
                "historical.restartApply",new{token,confirm=true,
                    typedApproval="STORE-V1 SAVEWORLD DOEXIT START"});
            Check(!replay.GetProperty("ok").GetBoolean(),
                "original Store Restart token is consumed once, never replayed");
            var inFlight=await Call(restartBridge.HandleAsync,
                "historical.restartRecovery",new{mapId=restartMap.Id});
            Check(inFlight.GetProperty("data").GetProperty("blocked").GetBoolean()&&
                  inFlight.GetProperty("data").GetProperty("inMemoryRunning").GetBoolean()&&
                  inFlight.GetProperty("data").GetProperty("jobId").GetString()==jobId&&
                  newStarts==0,
                "pending Stop phase blocks new Start while UI can reattach by jobId");
            var duplicate=await Call(restartBridge.HandleAsync,
                "historical.restartPreview",new{mapId=restartMap.Id});
            Check(!duplicate.GetProperty("ok").GetBoolean(),
                "second Store Restart denied while first one is running");
            releaseOldExit.TrySetResult(true);
            JsonElement status=default;
            for(int k=0;k<100;k++)
            {
                status=await Call(restartBridge.HandleAsync,
                    "historical.restartStatus",new{jobId});
                if(status.GetProperty("data").GetProperty("state").GetString()
                    is "completed" or "needsOperator")break;
                await Task.Delay(30);
            }
            Check(status.GetProperty("data").GetProperty("state").GetString()=="completed"&&
                  status.GetProperty("data").GetProperty("oldProcessStopped").GetBoolean()&&
                  status.GetProperty("data").GetProperty("preExitSaveLogAndWorldObserved").GetBoolean()&&
                  status.GetProperty("data").GetProperty("newPid").GetInt32()==44312&&
                  !status.GetProperty("data").GetProperty("readyCertified").GetBoolean()&&
                  saveExitCalls==1&&newStarts==1,
                "original Store one-shot Restart completes after old PID and save proof");
            var completed=new AsaManualRestartJournal(restartRoot).Observe();
            Check(completed?.Phase==AsaRestartPhase.Completed&&
                  completed.NewPid==44312,
                "Store Restart closes durable journal only on verified new PID");
            Check(lateStoreRestartProgress!=null,
                "Store Restart captures the approved native progress callback");
            lateStoreRestartProgress!("DELAYED ORIGINAL RESTART PROGRESS");
            var stableRestart=await Call(restartBridge.HandleAsync,
                "historical.restartStatus",new{jobId});
            Check(stableRestart.GetProperty("data").GetProperty("state")
                    .GetString()=="completed"&&
                  !stableRestart.GetProperty("data").GetProperty("step")
                    .GetString()!.Contains("DELAYED",StringComparison.Ordinal),
                "late Store Restart stage cannot undo a completed replacement PID");
            // A separately approved attempt may fail after the old ASA stopped;
            // a crashed Manager must never launch another process automatically.
            failNextStart=true;
            approved=await Call(restartBridge.HandleAsync,
                "historical.restartPreview",new{mapId=restartMap.Id});
            IsOk(approved,"independent second synthetic original Restart preflight");
            token=approved.GetProperty("data").GetProperty("token").GetString()!;
            queued=await Call(restartBridge.HandleAsync,
                "historical.restartApply",new{token,confirm=true,
                    typedApproval="STORE-V1 SAVEWORLD DOEXIT START"});
            IsOk(queued,"synthetic second restart queued");
            jobId=queued.GetProperty("data").GetProperty("jobId").GetString()!;
            for(int k=0;k<100;k++)
            {
                status=await Call(restartBridge.HandleAsync,
                    "historical.restartStatus",new{jobId});
                if(status.GetProperty("data").GetProperty("state").GetString()
                    is "completed" or "needsOperator")break;
                await Task.Delay(30);
            }
            Check(status.GetProperty("data").GetProperty("state").GetString()=="needsOperator"&&
                  saveExitCalls==2&&newStarts==2&&
                  new AsaManualRestartJournal(restartRoot).Observe()?.Phase==
                    AsaRestartPhase.NeedsOperator,
                "failed Store restart persists NeedsOperator and never fakes a new PID");
            Check(status.GetProperty("data").GetProperty("step").GetString()!
                      .Contains("[UM-",StringComparison.Ordinal),
                "failed Store restart exposes a bounded failure code while retaining NeedsOperator");
            lateStoreRestartProgress!("DELAYED ORIGINAL FAILED RESTART");
            var retainedFailure=await Call(restartBridge.HandleAsync,
                "historical.restartStatus",new{jobId});
            Check(retainedFailure.GetProperty("data").GetProperty("state")
                    .GetString()=="needsOperator"&&
                  retainedFailure.GetProperty("data").GetProperty("step")
                    .GetString()!.Contains("[UM-",StringComparison.Ordinal),
                "late Store Restart evidence cannot hide a failed durable recovery");
        }
        using(var reattached=new DesktopHistoricalRestartBridge(
            restartCluster,RestartProbe,RestartStop,RestartStart))
        {
            var recovery=await Call(reattached.HandleAsync,
                "historical.restartRecovery",new{mapId=restartMap.Id});
            Check(recovery.GetProperty("data").GetProperty("blocked").GetBoolean()&&
                  recovery.GetProperty("data").GetProperty("requiresOperator").GetBoolean()&&
                  !recovery.GetProperty("data").GetProperty("inMemoryRunning").GetBoolean(),
                "fresh Manager sees interrupted Store Restart, never auto-replays");
            var denied=await Call(reattached.HandleAsync,
                "historical.restartPreview",new{mapId=restartMap.Id});
            Check(!denied.GetProperty("ok").GetBoolean()&&
                  saveExitCalls==2&&newStarts==2,
                "failed Store Restart blocks a third dispatch until manual recovery");
        }
        Check(File.ReadAllBytes(ini).SequenceEqual(beforeRestartIni),
            "native Store Restart touches no original GameUserSettings INI bytes");
    }

    // Original Store-v1 API Loader restart remains strictly one-shot:
    // SaveWorld/DoExit must complete before a NEW Loader parent spawn, and
    // the completed Restart journal MUST contain the observed ASA child PID,
    // never the Loader parent. No real process or RCON is used.
    {
        string apiRestartRoot=Path.Combine(root,"historical-api-restart-synthetic");
        string binary=Path.Combine(apiRestartRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binary);
        string exe=Path.Combine(binary,"ArkAscendedServer.exe");
        string loader=Path.Combine(binary,"AsaApiLoader.exe");
        File.WriteAllBytes(exe,new byte[]{77,90,7,5});
        File.WriteAllBytes(loader,new byte[]{77,90,7,6});
        await using var apiCluster=new ClusterService("api-restart-synthetic",
            Path.Combine(root,"api-restart-profile"),
            _=>new ProcessCheck(false,"Synthetic Windows only",null,true));
        var apiMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Genesis",
            HistoricalStoreProtected=true,
            HistoricalMapKey="Genesis_WP",
            HistoricalArchiveSha256=new string('D',64),
            InstallationPath=apiRestartRoot,ExecutablePath=exe,
            ApiLoaderPath=loader,StartWithAsaApi=true,
            GamePort=7901,QueryPort=27201,RconPort=37401,
            RconPasswordProtected=SecureStore.Protect("SyntheticApiRestartOnly")
        };
        apiCluster.AddMap(apiMap);
        var oldTime=new DateTimeOffset(2026,11,8,13,0,0,TimeSpan.Zero);
        var newParentTime=oldTime.AddSeconds(15);
        int apiStopCount=0,apiSpawnCount=0;
        LegacyStoreV1GracefulStop.Owned ApiProbe(Guid mapId)=>
            mapId==apiMap.Id?
                new(apiMap,new ProcessIdentity{Pid=64001,
                    StartedUtc=oldTime,ExecutablePath=exe,
                    Arguments="SYNTHETIC_OLD_API_CHILD"},new string('E',64)):
                throw new UnauthorizedAccessException("synthetic foreign map");
        Task<AsaGracefulStopReceipt> ApiStop(Guid mapId,int pid,
            DateTimeOffset started,string source,Guid restartId,
            Action<string> stage)
        {
            apiStopCount++;
            var journal=new AsaManualRestartJournal(apiRestartRoot).Observe();
            Check(mapId==apiMap.Id&&pid==64001&&started==oldTime&&
                  source==new string('E',64)&&journal?.Id==restartId&&
                  journal.Phase==AsaRestartPhase.Stopping,
                "Loader Restart begins with exactly approved old ASA child and Stop journal");
            stage("Synthetic SaveWorld and DoExit confirmed");
            return Task.FromResult(new AsaGracefulStopReceipt(mapId,pid,
                true,false,true,DateTimeOffset.UtcNow,"SYNTHETIC_API_STOP"){
                    PreExitSaveLogAndWorldObserved=true
                });
        }
        ProcessIdentity ApiStart(Guid mapId,Guid restartId,Action<string> stage)
        {
            apiSpawnCount++;
            var journal=new AsaManualRestartJournal(apiRestartRoot).Observe();
            Check(apiStopCount==1&&mapId==apiMap.Id&&
                  journal?.Id==restartId&&journal.Phase==AsaRestartPhase.Starting,
                "Loader Process.Start occurs only after the old ASA child was stopped");
            stage("Synthetic Loader parent spawned");
            return new ProcessIdentity{Pid=64002,
                StartedUtc=newParentTime,ExecutablePath=loader,
                Arguments="SYNTHETIC_API_LOADER_PARENT"};
        }
        ProcessCheck ApiObserve(LocalMap map)=>
            new(true,"Synthetic parent and child from same owner",
                new ProcessIdentity{Pid=64003,
                    StartedUtc=newParentTime.AddSeconds(1),ExecutablePath=exe,
                    Arguments="SYNTHETIC_API_CHILD"},false,
                new ProcessIdentity{Pid=64002,
                    StartedUtc=newParentTime,ExecutablePath=loader,
                    Arguments="SYNTHETIC_API_PARENT"});
        using(var apiRestart=new DesktopHistoricalRestartBridge(apiCluster,
            ApiProbe,ApiStop,ApiStart,ApiObserve,
            (port,pid)=>port==37401&&pid==64003))
        {
            var preview=await Call(apiRestart.HandleAsync,
                "historical.restartPreview",new{mapId=apiMap.Id});
            IsOk(preview,"native original API Loader Restart source preview");
            Check(preview.GetProperty("data").GetProperty("launchMode").GetString()
                    =="asaApiLoader",
                "Loader Restart preview cannot silently downgrade to direct ASA");
            string token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var launched=await Call(apiRestart.HandleAsync,
                "historical.restartApply",new{
                    token,confirm=true,typedApproval="STORE-V1 SAVEWORLD DOEXIT START"
                });
            IsOk(launched,"synthetic Loader manual restart queued");
            string jobId=launched.GetProperty("data").GetProperty("jobId").GetString()!;
            JsonElement result=default;
            for(int k=0;k<100;k++)
            {
                result=await Call(apiRestart.HandleAsync,
                    "historical.restartStatus",new{jobId});
                if(result.GetProperty("data").GetProperty("state").GetString()
                    is "completed" or "needsOperator")break;
                await Task.Delay(25);
            }
            Check(result.GetProperty("data").GetProperty("state").GetString()=="completed"&&
                  result.GetProperty("data").GetProperty("launchMode").GetString()
                    =="asaApiLoader"&&
                  result.GetProperty("data").GetProperty("newPid").GetInt32()==64003&&
                  apiStopCount==1&&apiSpawnCount==1,
                "Loader Restart completion requires a verified ASA CHILD and owned RCON");
            var journal=new AsaManualRestartJournal(apiRestartRoot).Observe();
            Check(journal?.Phase==AsaRestartPhase.Completed&&
                  journal.NewPid==64003,
                "durable Store Restart journal records child PID, never Loader parent");
        }
    }

    // ORIGINAL ACTIVE Store-v1 graceful stop uses the real operation lease
    // and native two-stage approved queue, but synthetic process and RCON
    // callbacks only. Never start real ASA or touch a live Store directory.
    {
        string stopRoot=Path.Combine(root,"historical-original-graceful-stop");
        string binary=Path.Combine(stopRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(binary);
        string exe=Path.Combine(binary,"ArkAscendedServer.exe");
        File.WriteAllBytes(exe,new byte[]{77,90,0,2});
        string cfg=Path.Combine(stopRoot,"ShooterGame","Saved","Config","WindowsServer");
        Directory.CreateDirectory(cfg);
        const string originalPassword="OriginalStoreStopPass!2026";
        const int port=37175,oldPid=43571;
        string ini=Path.Combine(cfg,"GameUserSettings.ini");
        File.WriteAllText(ini,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+port+"\r\nServerAdminPassword="+originalPassword+"\r\n");
        byte[] originalIni=File.ReadAllBytes(ini);
        await using var clusterForStop=new ClusterService("historical-graceful-stop",
            Path.Combine(root,"historical-graceful-stop-profile"),
            _=>new ProcessCheck(false,"Synthetic external state",null,true));
        var originalStopMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Scorched Earth",InstallationPath=stopRoot,
            ExecutablePath=exe,HistoricalStoreProtected=true,
            HistoricalMapKey="ScorchedEarth_WP",
            HistoricalArchiveSha256=new string('A',64),
            GamePort=7887,QueryPort=27187,RconPort=port,
            RconPasswordProtected=SecureStore.Protect(originalPassword)
        };
        clusterForStop.AddMap(originalStopMap);
        var startedAt=new DateTimeOffset(2026,11,7,10,5,0,TimeSpan.Zero);
        const string expectedDigest="BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        bool exited=false,failDuringDispatch=false;
        int dispatched=0;
        ProcessCheck Running(LocalMap m)=>exited
            ?new ProcessCheck(false,"Synthetic stopped",null,true)
            :new ProcessCheck(true,"Synthetic owned",new ProcessIdentity{
                Pid=oldPid,StartedUtc=startedAt,
                ExecutablePath=exe,Arguments="ORIGINAL_SOURCE_SYNTHETIC"},false);
        ProcessCheck ConfirmStopped(LocalMap m)=>new ProcessCheck(
            false,exited?"Synthetic exit complete":"Still alive",null,exited);
        string OldSource(LocalMap m)=>expectedDigest;
        bool Owns(int rcon,int pid)=>rcon==port&&pid==oldPid;
        LegacyStoreV1GracefulStop.Owned Observe(Guid id)=>
            LegacyStoreV1GracefulStop.Preview(clusterForStop,id,Running,OldSource,Owns);
        Task<AsaGracefulStopReceipt> Execute(Guid id,int pid,DateTimeOffset at,
            string source,Action<string> stage)=>
            LegacyStoreV1GracefulStop.StopAsync(clusterForStop,id,pid,at,source,
                TimeSpan.FromSeconds(6),inspect:Running,stopped:ConfirmStopped,
                source:OldSource,portOwner:Owns,
                execute:async(map,identity,verify,ct)=>{
                    verify();dispatched++;
                    if(failDuringDispatch)
                        throw new IOException("Synthetic interruption after durable mutation marker");
                    stage("Synthetic SaveWorld and DoExit dispatched");
                    verify();
                    exited=true;
                    await Task.CompletedTask;
                    return true;
                },stage:stage);
        using(var stopBridge=new DesktopHistoricalGracefulStopBridge(clusterForStop,
            Observe,Execute))
        {
            var freshRecovery=await Call(stopBridge.HandleAsync,
                "historical.gracefulStopRecovery",
                new{mapId=originalStopMap.Id});
            IsOk(freshRecovery,"original Store Stop durable recovery for clean map");
            Check(freshRecovery.GetProperty("data").GetProperty("phase").GetString()=="None"&&
                  !freshRecovery.GetProperty("data").GetProperty("blocked").GetBoolean(),
                "original Store clean map has no durable pending recovery work");
            var missing=await Call(stopBridge.HandleAsync,"historical.gracefulStopPreview",
                new{mapId=Guid.NewGuid()});
            Check(!missing.GetProperty("ok").GetBoolean(),
                "original Store stop refuses remote and unregistered maps before any RCON");
            var preview=await Call(stopBridge.HandleAsync,"historical.gracefulStopPreview",
                new{mapId=originalStopMap.Id});
            IsOk(preview,"native two-stage original Store SaveWorld+DoExit preflight");
            Check(preview.GetProperty("data").GetProperty("usesSaveWorld").GetBoolean()&&
                  preview.GetProperty("data").GetProperty("usesDoExit").GetBoolean()&&
                  !preview.GetProperty("data").GetProperty("originalTwoPeersVerified").GetBoolean()&&
                  preview.GetProperty("data").GetProperty("originalPeerCheckRequiredForLocalOperation").GetBoolean()&&
                  !preview.GetProperty("data").GetProperty("forceKillAllowed").GetBoolean()&&
                  !preview.ToString().Contains(originalPassword,StringComparison.Ordinal),
                "original Store stop binds real source/PID but never leaks credentials or offers Kill");
            string token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var wrong=await Call(stopBridge.HandleAsync,"historical.gracefulStopApply",
                new{token,confirm=true,typedApproval="DOEXIT"});
            Check(!wrong.GetProperty("ok").GetBoolean()&&dispatched==0,
                "no original Store SaveWorld/DoExit before exact typed confirmation");
            var queued=await Call(stopBridge.HandleAsync,"historical.gracefulStopApply",
                new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD DOEXIT"});
            IsOk(queued,"original Store SaveWorld+DoExit real native operation queued");
            string jobId=queued.GetProperty("data").GetProperty("jobId").GetString()!;
            var replay=await Call(stopBridge.HandleAsync,"historical.gracefulStopApply",
                new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD DOEXIT"});
            Check(!replay.GetProperty("ok").GetBoolean(),
                "original historical Stop permit cannot dispatch twice");
            JsonElement status=default;
            for(int i=0;i<40;i++)
            {
                status=await Call(stopBridge.HandleAsync,"historical.gracefulStopStatus",
                    new{jobId});
                if(status.GetProperty("data").GetProperty("state").GetString()
                    is "completed" or "needsOperator")break;
                await Task.Delay(30);
            }
            IsOk(status,"original historical Store stop final native status");
            Check(status.GetProperty("data").GetProperty("state").GetString()=="completed"&&
                  status.GetProperty("data").GetProperty("confirmedStopped").GetBoolean()&&
                  status.GetProperty("data").GetProperty("preExitSaveLogAndWorldObserved").GetBoolean()&&
                  !status.GetProperty("data").GetProperty("finalDoExitSaveCertified").GetBoolean()&&
                  dispatched==1,
                "real Stop state requires verified PID exit and keeps final DoExit/fsync unverified");
            Check(clusterForStop.Operations.Observe(stopRoot)?.Phase==
                  UntamedOperationPhase.Completed,
                "original Store stop commits OS journal after verified PID exit");
            var completedRecovery=await Call(stopBridge.HandleAsync,
                "historical.gracefulStopRecovery",new{mapId=originalStopMap.Id});
            Check(completedRecovery.GetProperty("data").GetProperty("phase")
                    .GetString()=="Completed"&&
                  !completedRecovery.GetProperty("data").GetProperty("blocked")
                    .GetBoolean(),
                "completed historical Stop journal does not need an automatic unlock");


            // A second, interrupted mutation leaves NeedsOperator forever.
            exited=false;failDuringDispatch=true;
            var again=await Call(stopBridge.HandleAsync,"historical.gracefulStopPreview",
                new{mapId=originalStopMap.Id});
            IsOk(again,"second independent original Store Stop preview");
            token=again.GetProperty("data").GetProperty("token").GetString()!;
            var interrupted=await Call(stopBridge.HandleAsync,"historical.gracefulStopApply",
                new{token,confirm=true,typedApproval="STORE-V1 SAVEWORLD DOEXIT"});
            IsOk(interrupted,"synthetic interrupted Store stop starts distinct native job");
            jobId=interrupted.GetProperty("data").GetProperty("jobId").GetString()!;
            for(int i=0;i<40;i++)
            {
                status=await Call(stopBridge.HandleAsync,"historical.gracefulStopStatus",
                    new{jobId});
                if(status.GetProperty("data").GetProperty("state").GetString()
                    is "completed" or "needsOperator")break;
                await Task.Delay(30);
            }
            Check(status.GetProperty("data").GetProperty("state").GetString()=="needsOperator"&&
                  clusterForStop.Operations.Observe(stopRoot)?.Phase==
                    UntamedOperationPhase.NeedsOperator&&dispatched==2,
                "interrupted Store-v1 SaveWorld/DoExit remains durably blocked without automatic retries");
            var blockedRecovery=await Call(stopBridge.HandleAsync,
                "historical.gracefulStopRecovery",new{mapId=originalStopMap.Id});
            Check(blockedRecovery.GetProperty("data").GetProperty("phase")
                    .GetString()=="NeedsOperator"&&
                  blockedRecovery.GetProperty("data").GetProperty("blocked")
                    .GetBoolean()&&
                  blockedRecovery.GetProperty("data").GetProperty("requiresOperator")
                    .GetBoolean()&&
                  !blockedRecovery.GetProperty("data").GetProperty("lockCleared")
                    .GetBoolean(),
                "an interrupted historical Stop reports durable operator-needed block without lock deletion");
            var noPreview=await Call(stopBridge.HandleAsync,
                "historical.gracefulStopPreview",new{mapId=originalStopMap.Id});
            Check(!noPreview.GetProperty("ok").GetBoolean()&&dispatched==2,
                "historical RCON Stop cannot issue a new preview after interrupted journal");
        }
        // Simulate a completely NEW Manager process/bridge after exit. No
        // in-memory job or original source token survives, but the durable
        // journal must remain readable and prohibit implicit retry.
        using(var restarted=new DesktopHistoricalGracefulStopBridge(clusterForStop,
            Observe,Execute))
        {
            var resumed=await Call(restarted.HandleAsync,
                "historical.gracefulStopRecovery",new{mapId=originalStopMap.Id});
            IsOk(resumed,"new Manager observes old Stop journal without in-memory job");
            Check(resumed.GetProperty("data").GetProperty("phase")
                    .GetString()=="NeedsOperator"&&
                  resumed.GetProperty("data").GetProperty("requiresOperator")
                    .GetBoolean()&&
                  !resumed.GetProperty("data").GetProperty("inMemoryRunning")
                    .GetBoolean()&&
                  resumed.GetProperty("data").GetProperty("jobId").ValueKind==
                    JsonValueKind.Null,
                "new Manager correctly flags historical pending Stop without pretending to resume old worker");
            var rejected=await Call(restarted.HandleAsync,
                "historical.gracefulStopPreview",new{mapId=originalStopMap.Id});
            Check(!rejected.GetProperty("ok").GetBoolean()&&dispatched==2,
                "restarted Manager refuses automatic Store SaveWorld or DoExit retry");
        }
        Check(File.ReadAllBytes(ini).SequenceEqual(originalIni),
            "original ASA INI remains byte-identical after successful and interrupted Stop tests");
    }

    // The original ACTIVE Store-v1 admin queue sends ACTUAL native RCON
    // only under two-host source, exact PID, typed approval and a durable
    // installation lease. All OS/RCON execution is synthetic here.
    {
        string adminRoot=Path.Combine(root,"historical-original-admin-owned");
        string adminBin=Path.Combine(adminRoot,"ShooterGame","Binaries","Win64");
        Directory.CreateDirectory(adminBin);
        string adminExe=Path.Combine(adminBin,"ArkAscendedServer.exe");
        File.WriteAllBytes(adminExe,new byte[]{77,90,2,6});
        string adminIniDir=Path.Combine(adminRoot,"ShooterGame","Saved",
            "Config","WindowsServer");
        Directory.CreateDirectory(adminIniDir);
        const string adminPassword="OriginalAdminTestSecret!2026";
        const int adminPort=37195,adminPid=43814;
        string adminIni=Path.Combine(adminIniDir,"GameUserSettings.ini");
        File.WriteAllText(adminIni,"[ServerSettings]\r\nRCONEnabled=True\r\n"+
            "RCONPort="+adminPort+"\r\nServerAdminPassword="+adminPassword+"\r\n");
        byte[] originalAdminIni=File.ReadAllBytes(adminIni);
        await using var adminCluster=new ClusterService("original-admin-rcon",
            Path.Combine(root,"historical-admin-profile"),
            _=>new ProcessCheck(false,"Synthetic unused",null,true));
        var originalAdminMap=new LocalMap{
            Id=Guid.NewGuid(),Name="Valguero",InstallationPath=adminRoot,
            ExecutablePath=adminExe,HistoricalStoreProtected=true,
            HistoricalMapKey="Valguero_WP",
            HistoricalArchiveSha256=new string('A',64),
            GamePort=7897,QueryPort=27197,RconPort=adminPort,
            RconPasswordProtected=SecureStore.Protect(adminPassword)
        };
        adminCluster.AddMap(originalAdminMap);
        int adminSent=0,verifiedAfterAuthentication=0;
        bool adminSourceReady=true;
        var adminStarted=new DateTimeOffset(2026,11,7,10,10,0,TimeSpan.Zero);
        string adminSource=new string('B',64);
        LegacyStoreV1GracefulStop.Owned ProofAdmin(Guid target)
        {
            if(target!=originalAdminMap.Id||!adminSourceReady)
                throw new InvalidDataException("Synthetic original Store source invalid");
            return new(originalAdminMap,new ProcessIdentity{
                Pid=adminPid,StartedUtc=adminStarted,
                ExecutablePath=adminExe,Arguments="SYNTHETIC_ORIGINAL_ACTIVE"
            },adminSource);
        }
        using(var adminBridge=new DesktopHistoricalRconAdminBridge(
            adminCluster,ProofAdmin,(host,port,password,command,recheck,ct)=>{
                recheck();verifiedAfterAuthentication++;
                Check(host=="127.0.0.1"&&port==adminPort&&password==adminPassword,
                    "original Store admin RCON uses only local owned listener and current-user DPAPI password");
                Check(command is "Broadcast Hello Store" or "DestroyWildDinos",
                    "original Store admin RCON never sends lifecycle commands from arbitrary input");
                adminSent++;
                return Task.FromResult("ServerAdminPassword="+adminPassword);
            }))
        {
            var forbidden=await Call(adminBridge.HandleAsync,
                "historical.adminPreview",
                new{mapId=originalAdminMap.Id,action="custom",message="SaveWorld"});
            Check(!forbidden.GetProperty("ok").GetBoolean()&&adminSent==0,
                "original Store admin RCON cannot bypass the separate SaveWorld/DoExit lifecycle");
            var unknown=await Call(adminBridge.HandleAsync,
                "historical.adminPreview",
                new{mapId=Guid.NewGuid(),action="broadcast",message="test"});
            Check(!unknown.GetProperty("ok").GetBoolean(),
                "original Store admin RCON refuses unregistered or remote maps");
            var preview=await Call(adminBridge.HandleAsync,
                "historical.adminPreview",
                new{mapId=originalAdminMap.Id,action="broadcast",message="Hello Store"});
            IsOk(preview,"original Store real admin native Broadcast preview");
            Check(preview.GetProperty("data").GetProperty("approvalWord").GetString()
                    =="STORE-V1 ADMIN"&&
                  !preview.ToString().Contains(adminPassword,StringComparison.Ordinal),
                "original Store admin preview supplies strong typed consent without leaking password");
            string token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var wrong=await Call(adminBridge.HandleAsync,"historical.adminSend",
                new{token,confirm=true,typedApproval="ADMIN"});
            Check(!wrong.GetProperty("ok").GetBoolean()&&adminSent==0,
                "original Store admin RCON cannot send with wrong operator phrase");
            var retry=await Call(adminBridge.HandleAsync,"historical.adminSend",
                new{token,confirm=true,typedApproval="STORE-V1 ADMIN"});
            Check(!retry.GetProperty("ok").GetBoolean()&&adminSent==0,
                "original Store admin RCON confirmation token is consumed on failure");
            preview=await Call(adminBridge.HandleAsync,"historical.adminPreview",
                new{mapId=originalAdminMap.Id,action="broadcast",message="Hello Store"});
            IsOk(preview,"fresh original Store admin preflight after denied attempt");
            token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var success=await Call(adminBridge.HandleAsync,"historical.adminSend",
                new{token,confirm=true,typedApproval="STORE-V1 ADMIN"});
            IsOk(success,"real original Store admin Broadcast dispatched under durable local lease");
            Check(success.GetProperty("data").GetProperty("commandSent").GetBoolean()&&
                  success.GetProperty("data").GetProperty("oneShot").GetBoolean()&&
                  !success.GetProperty("data").GetProperty("response")
                    .GetString()!.Contains(adminPassword,StringComparison.Ordinal)&&
                  adminCluster.Operations.Observe(adminRoot)?.Phase==
                    UntamedOperationPhase.Completed&&
                  adminSent==1&&verifiedAfterAuthentication==1,
                "original Store admin RCON redacts secrets and commits journal only after one authenticated dispatch");
            var replay=await Call(adminBridge.HandleAsync,"historical.adminSend",
                new{token,confirm=true,typedApproval="STORE-V1 ADMIN"});
            Check(!replay.GetProperty("ok").GetBoolean()&&adminSent==1,
                "original Store admin command cannot be sent again with the same token");
            adminSourceReady=false;
            var changed=await Call(adminBridge.HandleAsync,
                "historical.adminPreview",
                new{mapId=originalAdminMap.Id,action="broadcast",message="no source"});
            Check(!changed.GetProperty("ok").GetBoolean()&&adminSent==1,
                "invalidated original two-host Store session blocks fresh admin RCON");
            adminSourceReady=true;
            preview=await Call(adminBridge.HandleAsync,
                "historical.adminPreview",
                new{mapId=originalAdminMap.Id,action="wildWipe",message=""});
            IsOk(preview,"original Store dangerous wild wipe requires a separate preview");
            Check(preview.GetProperty("data").GetProperty("approvalWord").GetString()
                    =="STORE-V1 DINÓTÖRLÉS",
                "dangerous original Store admin action needs exact stronger typed approval");
            token=preview.GetProperty("data").GetProperty("token").GetString()!;
            var wipe=await Call(adminBridge.HandleAsync,"historical.adminSend",
                new{token,confirm=true,typedApproval="STORE-V1 DINÓTÖRLÉS"});
            IsOk(wipe,"original Store admin wild wipe can be dispatched exactly once with explicit consent");
            Check(adminSent==2,
                "two separate original Store admin approvals dispatched exactly twice in total");
            // Restart-v1 is durable independently from the generic operation
            // journal, which is already Completed after the last RCON. An
            // interrupted restart must suppress both a fresh admin preview
            // and a previously issued admin approval before sending any byte.
            var staleAdmin=await Call(adminBridge.HandleAsync,
                "historical.adminPreview",
                new{mapId=originalAdminMap.Id,action="broadcast",message="Hello Store"});
            IsOk(staleAdmin,"fresh admin preview before restart overlap");
            var restartGuard=new AsaManualRestartJournal(adminRoot);
            var restarting=restartGuard.Begin(originalAdminMap.Id,adminPid);
            var blockedSend=await Call(adminBridge.HandleAsync,
                "historical.adminSend",
                new{token=staleAdmin.GetProperty("data").GetProperty("token").GetString(),
                    confirm=true,typedApproval="STORE-V1 ADMIN"});
            Check(IsError(blockedSend,"UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR")&&
                  adminSent==2,
                "admin RCON preview made before restart cannot dispatch during pending Restart-v1");
            var blockedPreview=await Call(adminBridge.HandleAsync,
                "historical.adminPreview",
                new{mapId=originalAdminMap.Id,action="broadcast",message="Hello Store"});
            Check(IsError(blockedPreview,"UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR")&&
                  adminSent==2,
                "pending Restart-v1 blocks fresh RCON even when generic operation journal is Completed");
            restartGuard.Change(restarting.Id,AsaRestartPhase.NeedsOperator);
            var blockedRecovery=await Call(adminBridge.HandleAsync,
                "historical.adminPreview",
                new{mapId=originalAdminMap.Id,action="wildWipe",message=""});
            Check(IsError(blockedRecovery,"UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR")&&
                  adminSent==2,
                "interrupted Restart-v1 NeedsOperator blocks dangerous wild wipe without command replay");
        }
        Check(File.ReadAllBytes(adminIni).SequenceEqual(originalAdminIni),
            "original ASA GameUserSettings.ini stays byte-identical after approved Store RCON admin tests");
    }

    await MultiHostClusterBackupBridgeTests.Run(root);
    Console.WriteLine("PASS all native bridge checks (synthetic local fixtures, no ASA launch)");
}
finally
{
    try{Directory.Delete(root,true);}
    catch(IOException){/* test cleanup not part of production path */}
}


sealed class FakeCatalogProvider : HttpMessageHandler
{
    private readonly string expectedKey;
    public int Requests {get;private set;}
    public int DownloadRequests {get;private set;}
    public string LastQuery {get;private set;}="";
    public FakeCatalogProvider(string key)=>expectedKey=key;
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,CancellationToken cancellationToken)
    {
        Requests++;
        if(request.RequestUri?.Host!="api.curseforge.com" ||
           !request.Headers.TryGetValues("x-api-key",out var values) ||
           values.Single()!=expectedKey)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        string path=request.RequestUri.AbsolutePath;
        if(path.Contains("/download-url",StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
            DownloadRequests++;
        if(path=="/v1/mods/search")LastQuery=request.RequestUri.Query;
        string json=path switch {
            "/v1/games" => """{"data":[{"id":4418,"slug":"ark-survival-ascended"}]}""",
            "/v1/categories" => """{"data":[{"id":100,"name":"Structures","isClass":false,"gameId":4418,"parentCategoryId":0}]}""",
            "/v1/mods/featured" => """{"data":{"featured":[{"id":928548,"gameId":4418,"name":"Featured"}],"popular":[{"id":928548,"gameId":4418,"name":"Popular"}],"recentlyUpdated":[{"id":928548,"gameId":4418,"name":"Recent"}]}}""",
            "/v1/mods/search" => """{"data":[{"id":928548,"gameId":4418,"name":"Synthetic mod","authors":[{"name":"Creator"}],"categories":[{"name":"Structures"}],"downloadCount":23}],"pagination":{"totalCount":1}}""",
            "/v1/mods/928548" => """{"data":{"id":928548,"gameId":4418,"name":"Synthetic mod","summary":"Safe description"}}""",
            "/v1/mods/928548/description" => """{"data":"Synthetic safe description"}""",
            "/v1/mods/928548/files" => """{"data":[{"id":45001,"modId":928548,"displayName":"v1.0","isAvailable":true,"fileLength":1024,"gameVersions":["ASA"],"dependencies":[{"modId":912345,"relationType":3}]}],"pagination":{"totalCount":1}}""",
            _ => """{"data":[]}"""
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
            Content=new StringContent(json,Encoding.UTF8,"application/json")
        });
    }
}
