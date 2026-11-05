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
