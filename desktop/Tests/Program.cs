using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UntamedDesktop;
using UntamedManager.Core;

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
static void IsOk(JsonElement value,string context)=>
    Check(value.GetProperty("ok").GetBoolean(),context);
static bool IsError(JsonElement value,string code)=>
    !value.GetProperty("ok").GetBoolean() &&
    value.GetProperty("error").GetString()==code;

string root=Path.Combine(Path.GetTempPath(),
    "Untamed-Obsidian-Native-Bridge-Tests-"+Guid.NewGuid().ToString("N"));
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
// A live historical Store-v1 server must NOT appear stopped merely
// because its START command is locked. Monitoring is READ-ONLY and cannot
// confer Start/Stop, Store write or remote lifecycle authority.
{
    string historicRoot=Path.Combine(root,"historical-status-only");
    int observations=0;
    await using var history=new ClusterService("historical-observer",
        Path.Combine(historicRoot,"profile"),
        _=>{
            Interlocked.Increment(ref observations);
            return new ProcessCheck(true,"Existing ASA process observed (synthetic)",null);
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
            new[]{"123456","654321","123456"},new[]{"654321"},
            "KovaCluster", @"\\192.0.2.1\SyntheticCluster","active",safeIni);
        var active=LegacyStoreV1ActiveLaunch.Build(input);
        Check(active.Arguments.SequenceEqual(new[]{
            "ScorchedEarth_WP?listen","-Port=7777","-QueryPort=27015",
            "-mods=654321,123456","-passivemods=654321",
            "-clusterid=KovaCluster",@"-ClusterDirOverride=\\192.0.2.1\SyntheticCluster",
            "-usestore","-log"})&&!active.ConversionRequested,
            "original active Store-v1 source grammar preserves passive-first mods and forbids conversion");
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
              oneMap.GetProperty("data").GetProperty("schedules").GetArrayLength()==0,
            "map inspection is bounded and never creates recurring jobs");
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
              current.GetProperty("data").GetProperty("expectedPeerHostId").GetString()==remoteHostId &&
              current.GetProperty("data").GetProperty("localHostId").GetString()==cluster.Config.LocalHostId &&
              !current.ToString().Contains(pairSecret,StringComparison.Ordinal),
            "read-only LAN status never returns pairing secret");
    }

    // Two-host transport proof on loopback only: the real AES/HMAC LAN
    // protocol must reply to an authenticated peer and reject wrong secrets.
    // This never opens or contacts an actual cluster machine.
    {
        var probe=new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,0);
        probe.Start();
        int freePort=((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var peerListener=new PeerStatusListener(freePort,
            "SYNTHETIC-SHARED-PAIR-SECRET-NEVER-LOG",cluster.LocalStatus);
        peerListener.Start();
        var response=await PeerStatusClient.Query("127.0.0.1",freePort,
            "SYNTHETIC-SHARED-PAIR-SECRET-NEVER-LOG");
        Check(response.HostId==cluster.Config.LocalHostId &&
              response.Maps.Any(x=>x.Name=="The Island"),
            "authenticated real local TCP peer returns native map observation");
        bool wrongSecretDenied=false;
        try {
            await PeerStatusClient.Query("127.0.0.1",freePort,
                "INVALID-SYNTHETIC-SHARED-SECRET-12345");
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
        Check(IsError(conflict,"UM-INI-CONFLICT-OR-IO-ERROR"),
            "external INI modification defeats stale hash/ETag save");
        Check(File.ReadAllText(iniFile).Contains("EXTERNAL=1"),
            "external change preserved on failed staged save");
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
    async Task<SteamFreshInstallReceipt> SyntheticSteamRunner(
        SteamFreshInstallRequest request,LocalMap[] existing,
        IProgress<SteamInstallProgress>? progress,CancellationToken cancellationToken)
    {
        fakeRuns++;
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
            Check(IsError(lowDisk,"UM-FRESH-LOW-DISK-SPACE") &&
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
        var current=await Call(steamBridge.HandleAsync,"steam.freshCurrent");
        Check(current.GetProperty("data").GetProperty("current")
            .GetProperty("registered").GetBoolean(),
            "manager can restore verified native Steam job status after UI navigation");
    }

    // Verify that launch settings are a separate explicit stage after ASA
    // files exist. This never starts an ASA process or enables RCON.
    var valguero=cluster.Config.LocalMaps.Single(m=>m.Name=="Valguero");
    using(var setup=new DesktopInitialMapSetupBridge(cluster))
    {
        var status=await Call(setup.HandleAsync,"servers.initialStatus",
            new{mapId=valguero.Id});
        IsOk(status,"freshly registered ASA map is eligible for initial setup");
        Check(status.GetProperty("data")
            .GetProperty("initialSetupAvailable").GetBoolean(),
            "new map is confirmed stopped and unconfigured");

        var bad=await Call(setup.HandleAsync,"servers.initialPreview",
            new{mapId=valguero.Id,sessionName="UK Test",gamePort=7797,queryPort=7797});
        Check(!bad.GetProperty("ok").GetBoolean(),
            "initial setup rejects identical Game and Query UDP ports");

        var preview=await Call(setup.HandleAsync,"servers.initialPreview",
            new{mapId=valguero.Id,sessionName="Untamed Kingdom Test",
                gamePort=7797,queryPort=27035});
        IsOk(preview,"official ASA level/session/ports preview is calculated");
        var token=preview.GetProperty("data").GetProperty("token").GetString()!;
        Check(string.IsNullOrEmpty(valguero.Arguments) &&
              valguero.GamePort==0 && valguero.QueryPort==0,
            "profile stays unmodified until approval");
        var unconfirmed=await Call(setup.HandleAsync,"servers.initialApply",
            new{token,confirm=false});
        Check(IsError(unconfirmed,"UM-INITIAL-CONFIRMATION-REQUIRED"),
            "unapproved initial setup writes nothing and preserves preview");

        var committed=await Call(setup.HandleAsync,"servers.initialApply",
            new{token,confirm=true});
        IsOk(committed,"approved initial setup saves registered Manager launch profile");
        var configured=cluster.Config.LocalMaps.Single(m=>m.Id==valguero.Id);
        Check(configured.GamePort==7797 && configured.QueryPort==27035 &&
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

    // Real native fresh RCON bootstrap is separate from map first-time
    // launch settings. No Store, running process or other INI may be modified.
    {
        var fresh=cluster.Config.LocalMaps.Single(m=>m.Name=="Valguero");
        string iniPath=AsaInitialRconSettings.NewIniPath(fresh.InstallationPath);
        Check(!File.Exists(iniPath),"fresh RCON test starts with no ASA server config");
        using var rconSetup=new DesktopRconConfigurationBridge(cluster);
        var status=await Call(rconSetup.HandleAsync,"rcon.configStatus",
            new{mapId=fresh.Id});
        IsOk(status,"new stopped non-Store map can configure RCON");
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
            new{mapId=fresh.Id,rconPort=37555,password=secret,createIni=true});
        Check(IsError(protectedPreview,"UM-RCON-CONFIG-STOPPED-NONSTORE-ONLY"),
            "RCON bootstrap cannot bypass historical Store map on shared installation");
        cluster.RemoveMap(historicalSibling.Id);
        var preview=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=fresh.Id,rconPort=37555,password=secret,createIni=true});
        IsOk(preview,"new ASA RCON config is previewed, not applied");
        Check(!File.Exists(iniPath) && !preview.GetRawText().Contains(secret),
            "RCON preview leaves disk untouched and hides credential");
        string token=preview.GetProperty("data").GetProperty("token").GetString()!;
        var declined=await Call(rconSetup.HandleAsync,"rcon.configApply",
            new{token,confirm=false});
        Check(IsError(declined,"UM-RCON-CONFIG-CONFIRMATION") &&
              !File.Exists(iniPath),"RCON bootstrap requires explicit confirmation");
        var configured=await Call(rconSetup.HandleAsync,"rcon.configApply",
            new{token,confirm=true});
        IsOk(configured,"native RCON bootstrap creates ASA INI and stores DPAPI credential");
        Check(File.Exists(iniPath) && File.ReadAllText(iniPath).Contains("RCONEnabled=True") &&
              cluster.Config.LocalMaps.Single(m=>m.Id==fresh.Id).RconPort==37555 &&
              SecureStore.Unprotect(cluster.Config.LocalMaps.Single(m=>m.Id==fresh.Id)
                  .RconPasswordProtected)==secret,
            "RCON bootstrap persisted correct local ASA setting and encrypted Manager secret");
        var replay=await Call(rconSetup.HandleAsync,"rcon.configApply",
            new{token,confirm=true});
        Check(IsError(replay,"UM-RCON-CONFIG-TOKEN-EXPIRED"),
            "RCON bootstrap cannot be replayed");
        var second=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=fresh.Id,rconPort=37556,password=secret,createIni=true});
        Check(IsError(second,"UM-RCON-CONFIG-STOPPED-NONSTORE-ONLY"),
            "configured RCON cannot be overwritten by bootstrap");
        var denied=await Call(rconSetup.HandleAsync,"rcon.configPreview",
            new{mapId=Guid.NewGuid(),rconPort=37556,password=secret,createIni=true});
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
    }

    // Actual SteamCMD entry point uses Core's stopped-map update and per-host
    // queue. The adapter is injected only in this isolated Windows fixture.
    {
        var target=cluster.Config.LocalMaps.Single(x=>x.Name=="Valguero");
        int executions=0;
        byte[] original=File.ReadAllBytes(target.ExecutablePath);
        using var updater=new DesktopStoppedSteamUpdateBridge(cluster,
            m=>new SteamUpdatePreview(Path.GetFullPath(m.InstallationPath)
                .TrimEnd(Path.DirectorySeparatorChar),new[]{m.Name},
                "FAKE-STEAMCMD-DIGEST","123456",
                10L*1024*1024*1024,20L*1024*1024*1024,true,"Synthetic plan"),
            async(m,progress)=>{
                executions++;
                progress.Report(SteamMaintenanceStage.Backup);
                await Task.Delay(65);
                return new SteamStoppedMapUpdater.Receipt(m.Id,m.InstallationPath,
                    "123456","123457","fake-backup",
                    DateTimeOffset.UtcNow,true);
            });
        var unknown=await Call(updater.HandleAsync,"steam.stoppedPreview",
            new{mapId=Guid.NewGuid()});
        Check(IsError(unknown,"UM-MAP-NOT-OWNED"),
            "remote or unknown map cannot schedule local SteamCMD update");
        var proposed=await Call(updater.HandleAsync,"steam.stoppedPreview",
            new{mapId=target.Id});
        IsOk(proposed,"stopped-map SteamCMD update preflight is functional");
        Check(executions==0,"SteamCMD preview has no launch side effect");
        string token=proposed.GetProperty("data").GetProperty("token").GetString()!;
        var refused=await Call(updater.HandleAsync,"steam.stoppedApply",
            new{token,confirm=false});
        Check(IsError(refused,"UM-STEAM-CONFIRMATION-REQUIRED") && executions==0,
            "SteamCMD update requires native approval");
        var launched=await Call(updater.HandleAsync,"steam.stoppedApply",
            new{token,confirm=true});
        IsOk(launched,"approved update dispatches native verified Core operation");
        string job=launched.GetProperty("data").GetProperty("jobId").GetString()!;
        var replay=await Call(updater.HandleAsync,"steam.stoppedApply",
            new{token,confirm=true});
        Check(!replay.GetProperty("ok").GetBoolean(),
            "same SteamCMD approval token cannot replay");
        JsonElement result=default;
        for(int n=0;n<60;n++){
            result=await Call(updater.HandleAsync,"steam.stoppedStatus",
                new{jobId=job});
            if(result.GetProperty("data").GetProperty("state").GetString()=="completed")break;
            await Task.Delay(25);
        }
        IsOk(result,"native SteamCMD update reports progress");
        Check(result.GetProperty("data").GetProperty("state").GetString()=="completed" &&
              executions==1 &&
              result.GetProperty("data").GetProperty("protectedFilesIntact").GetBoolean() &&
              original.SequenceEqual(File.ReadAllBytes(target.ExecutablePath)),
            "SteamCMD update runs once and preserves fixture ASA executable");
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
        Check(result.GetProperty("data").GetProperty("state").GetString()=="completed" &&
              result.GetProperty("data").GetProperty("confirmedStopped").GetBoolean() &&
              !result.GetProperty("data").GetProperty("saveCompleteProven").GetBoolean() &&
              !result.GetProperty("data").GetProperty("killPerformed").GetBoolean() &&
              sends==1,
            "native DoExit verifies process exit without inventing SaveComplete");
        stopLabRunning=true;
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
        Check(!beforeJournal.GetProperty("data")
                  .GetProperty("needsOperatorReview").GetBoolean(),
            "completed graceful stop has no pending operator review");
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
                    "GetGameLog"=>"Synthetic world log line",
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
        owner=false;
        var deniedOwnership=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=true});
        Check(IsError(deniedOwnership,"UM-RCON-READONLY-PREFLIGHT-DENIED") &&
              dispatched==2,"SaveWorld denied if native RCON listener ownership changes");
        var refused=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=fakeMap.Id});
        Check(IsError(refused,"UM-RCON-READONLY-PREFLIGHT-DENIED") &&
              dispatched==2,"RCON port ownership loss prevents another dispatch");
        owner=true;
        fakeMap.Arguments="-usestore";
        var forbiddenStoreSave=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=true});
        Check(IsError(forbiddenStoreSave,"UM-HISTORICAL-STORE-LOCKED")&&
              dispatched==2,
            "native RCON SaveWorld rejects active -usestore writes without peer writer authority");
        fakeMap.Arguments="";
        rconRunning=false;
        var stopped=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=fakeMap.Id});
        Check(IsError(stopped,"UM-RCON-READONLY-PREFLIGHT-DENIED") &&
              dispatched==2,"stopped synthetic process cannot receive RCON");
        rconRunning=true;fakeMap.HistoricalStoreProtected=true;
        var locked=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=fakeMap.Id});
        Check(IsError(locked,"UM-HISTORICAL-STORE-LOCKED") &&
              dispatched==2,"historical Store-v1 lock always prevents RCON dispatch");
        var foreign=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=Guid.NewGuid()});
        Check(IsError(foreign,"UM-MAP-NOT-OWNED"),
            "RCON cannot target an unregistered or remote peer map");

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
                return Task.FromResult("Admin command accepted");
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
              !sent.GetRawText().Contains("test-only-passphrase",StringComparison.Ordinal),
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
        Check(dispatched==3 &&
              gameLog.GetProperty("data").GetProperty("command").GetString()=="GetGameLog" &&
              gameLog.GetProperty("data").GetProperty("readOnly").GetBoolean() &&
              gameLog.GetProperty("data").GetProperty("response")
                  .GetString()=="Synthetic world log line",
            "GetGameLog sends only fixed read-only RCON to owned local map");
        var chatLog=await Call(rcon.HandleAsync,"rcon.chatLog",new{mapId=fakeMap.Id});
        IsOk(chatLog,"actual native GetChat reads local ASA chat");
        Check(dispatched==4 &&
              chatLog.GetProperty("data").GetProperty("command").GetString()=="GetChat" &&
              chatLog.GetProperty("data").GetProperty("response")
                  .GetString()=="Synthetic chat message",
            "GetChat reads chat without arbitrary browser command dispatch");
        var rconPendingJournal=new AsaManualRestartJournal(fakeMap.InstallationPath);
        var pendingRconRestart=rconPendingJournal.Begin(fakeMap.Id,identity.Pid);
        var noSecondSave=await Call(rcon.HandleAsync,"rcon.saveWorld",
            new{mapId=fakeMap.Id,confirm=true});
        Check(IsError(noSecondSave,"UM-RCON-READONLY-UNAVAILABLE")&&
              dispatched==4,
            "pending manual Restart blocks standalone SaveWorld before any RCON bytes");
        var diagnosticDuringRestart=await Call(rcon.HandleAsync,
            "rcon.chatLog",new{mapId=fakeMap.Id});
        Check(diagnosticDuringRestart.GetProperty("ok").GetBoolean()&&
              dispatched==5,
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
        await using var firstHost=new ClusterService("travel-lab-a",
            Path.Combine(root,"travel-profile-a"),
            _=>new ProcessCheck(false,"confirmed stopped synthetic travel",null,true));
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
    Check(!ObsidianWindow.RequiresGlobalBridgeGate("updates.prepare") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("store.inspectCopy") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("store.inspectArchive") &&
          !ObsidianWindow.RequiresGlobalBridgeGate("store.compareArchives") &&
          ObsidianWindow.RequiresGlobalBridgeGate("updates.install") &&
          ObsidianWindow.RequiresGlobalBridgeGate("servers.restartApply") &&
          ObsidianWindow.RequiresGlobalBridgeGate(null),
        "Obsidian updater staging cannot monopolize global server command gate");

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
        var staging=new DesktopObsidianUpdateInstallBridge();
        var missingConsent=await Call(staging.HandleAsync,"updates.prepare",
            new{channel="preview",confirm=false});
        Check(IsError(missingConsent,"UM-UPDATE-EXPLICIT-CONFIRMATION-REQUIRED"),
            "updater cannot stage an installation without explicit local approval");
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
        Check(IsError(locked,"UM-HISTORICAL-STORE-LOCKED") && Directory.Exists(active),
            "historical Store-v1 map cannot authorize plugin folder mutation");
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
            Check(IsError(blocked,"UM-HISTORICAL-STORE-LOCKED"),
                "mod draft cannot change historical protected profile");
        }
        using(var blockedIni=new DesktopIniBridge(cluster))
        {
            var blocked=await Call(blockedIni.HandleAsync,"ini.open",
                new{mapId=imported.Id,file="Game.ini"});
            Check(IsError(blocked,"UM-HISTORICAL-STORE-LOCKED"),
                "INI writes forbidden before full historical map acceptance");
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
