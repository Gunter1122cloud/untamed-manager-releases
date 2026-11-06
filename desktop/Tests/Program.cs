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
try
{
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

    using(var steamBridge=new DesktopSteamFreshInstallBridge(cluster,
        SyntheticSteamRunner))
    {
        var badPath=await Call(steamBridge.HandleAsync,"steam.freshPreflight",
            new{mapName="Valguero",installRoot=@"\\foreign\server",
                steamExe,allowSteamCmdDownload=false});
        Check(!badPath.GetProperty("ok").GetBoolean(),
            "new Steam download refuses foreign/UNC install root");
        Check(fakeRuns==0,"preflight never launches synthetic SteamCMD adapter");

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

    // Read-only RCON uses an isolated synthetic process identity and injected
    // fake ListPlayers response. This must never reach live ASA or real SQL.
    {
        var fakeMap=new LocalMap{
            Name="RCON synthetic map",ExecutablePath=Path.Combine(root,"rcon-fake.exe"),
            InstallationPath=Path.Combine(root,"rcon-fake-install"),
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
                      password=="test-only-passphrase"&&command=="ListPlayers",
                    "RCON command is fixed and secret never reaches WebView");
                dispatched++;return Task.FromResult("Player 1: Synthetic Survivor");
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
        owner=false;
        var refused=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=fakeMap.Id});
        Check(IsError(refused,"UM-RCON-READONLY-PREFLIGHT-DENIED") &&
              dispatched==1,"RCON port ownership loss prevents another dispatch");
        owner=true;rconRunning=false;
        var stopped=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=fakeMap.Id});
        Check(IsError(stopped,"UM-RCON-READONLY-PREFLIGHT-DENIED") &&
              dispatched==1,"stopped synthetic process cannot receive RCON");
        rconRunning=true;fakeMap.HistoricalStoreProtected=true;
        var locked=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=fakeMap.Id});
        Check(IsError(locked,"UM-HISTORICAL-STORE-LOCKED") &&
              dispatched==1,"historical Store-v1 lock always prevents RCON dispatch");
        var foreign=await Call(rcon.HandleAsync,"rcon.listPlayers",new{mapId=Guid.NewGuid()});
        Check(IsError(foreign,"UM-MAP-NOT-OWNED"),
            "RCON cannot target an unregistered or remote peer map");
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
        var selected=ObsidianReleaseCatalog.Select(releaseJson,UpdateChannel.Preview);
        Check(selected!=null&&selected.Tag==tag&&
              selected.AssetName==asset&&ObsidianReleaseCatalog.IsTrusted(selected),
            "only exact Obsidian AppId installer is eligible for native update discovery");
        Check(ObsidianReleaseCatalog.Select(releaseJson,UpdateChannel.Stable)==null,
            "stable update channel cannot automatically select preview releases");
        Check(!ObsidianReleaseCatalog.IsTrusted(selected with{
            AssetName="UntamedManager-8.2.0-SETUP.exe"}),
            "compatible-looking WPF installer is never an Obsidian update");
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
