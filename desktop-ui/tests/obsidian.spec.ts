import { test, expect } from "@playwright/test";

const first = "00000000-0000-4000-8000-000000000001";
const second = "00000000-0000-4000-8000-000000000002";

test.beforeEach(async ({ page }) => {
  await page.addInitScript(({ one, two }) => {
    type Handler = (e: { data: { id: string; ok: boolean; data?: unknown; error?: string } }) => void;
    const handlers: Handler[] = [];
    const state = {
      mode: "LIVE_READ_ONLY",
      observedUtc: "2026-11-05T12:00:00Z",
      local: {
        hostId: "laboratory-1",
        hostName: "LAB-WIN-01",
        cpuPercent: 22,
        memoryUsedKiB: 20_971_520,
        memoryTotalKiB: 67_108_864,
        maps: [
          { id: one, name: "The Island", running: true, status: "Fut", gamePort: 7777, steamBuildId: null, steamUpdateState: "Nem ellenőrizhető" },
          { id: two, name: "Astraeos", running: false, status: "Leállítva", gamePort: 7847, steamBuildId: null, steamUpdateState: "Nem ellenőrizhető" }
        ]
      },
      remote: {
        hostId: "laboratory-2",
        hostName: "LAB-WIN-02",
        observedUtc: "2026-11-05T12:00:00Z",
        maps: [{ id: "peer-1", name: "Ragnarok", running: true, status: "Fut", gamePort: 7827, steamBuildId: null, steamUpdateState: "Nem ellenőrizhető" }]
      },
      peerMessage: ""
    };
    let freshId = "";
    let keySaved = false;
    let stagedId = "";
    let redoId = "";
    const draftToken = "00000000-0000-4000-8000-000000001234";
    const draftState = () => ({
      draftId:draftToken,mapId:one,active:stagedId?["123456",stagedId]:["123456"],
      passive:["789012"],disabled:[],pending:!!stagedId,dirty:!!stagedId,
      canUndo:!!stagedId,canRedo:!!redoId,evidence:"IN_MEMORY_MOD_DRAFT",
      differences:stagedId?[{modId:stagedId,beforeMode:null,beforePosition:null,afterMode:"active",afterPosition:2}]:[]
    });
    const iniToken="00000000-0000-4000-8000-000000003333";
    const answers = (m: { method: string; mapId?: string; term?: string; page?: number; credential?: string; draftId?: string; modId?: string | number; mode?: string; file?: string; token?: string; content?: string; name?: string; root?: string; importToken?: string; categoryId?: number; sort?: number; descending?: boolean; mapName?: string; sessionName?: string; gamePort?: number; queryPort?: number; archive?: string; coordinationArchive?: string; localMetadataArchive?: string }) => {
      switch (m.method) {
        case "historical.pairedEvidence": return {
          files:12,maps:10,peers:2,sessionConsistent:true,
          configHashesVerified:true,activeStoreRecords:true,
          completeTwoHostEvidence:false,historicalConsistent:false,
          findings:["LocalLock"],
          hosts:[{number:1,maps:5,localEvidence:true,sessionMatches:true,
            configHashMatches:false,lockMatches:false,activeMaps:true},
            {number:2,maps:5,localEvidence:true,sessionMatches:true,
            configHashMatches:true,lockMatches:true,activeMaps:true}],
          productionUseAllowed:false,
          evidence:"PAIRED_COPIES_OFFLINE_READ_ONLY_NO_WRITER_CERTIFICATION"
        };
        case "historical.list": return {
          sourceVerified:true,completeLaunchArguments:false,liveStoreVerified:false,
          allowServerStart:false,evidence:"HISTORICAL_LOCAL_ARCHIVE_READ_ONLY",
          maps:[{mapId:"00000000-0000-4000-8000-000000003888",
            mapKey:"ScorchedEarth_WP",gamePort:7777,queryPort:27015,
            enabledMods:22,passiveMods:3}]
        };
        case "historical.preview": return {
          token:"00000000-0000-4000-8000-000000003889",
          mapId:m.mapId,mapKey:"ScorchedEarth_WP",name:"Scorched Earth",
          gamePort:7777,queryPort:27015,enabledMods:22,passiveMods:3,
          folderName:"TestExistingMap",historicalActive:true,
          originalMapIdPreserved:true,fullLaunchArgumentsVerified:false,
          peerWriterVerified:false,serverStartAllowed:false,
          evidence:"HISTORICAL_STORE_V1_QUARANTINE_PREVIEW"
        };
        case "historical.register":
          state.local.maps.push({
            id:"00000000-0000-4000-8000-000000003888",
            name:"Scorched Earth",running:false,status:"Store-v1 zárolt",
            gamePort:7777,steamBuildId:null,steamUpdateState:"Nem ellenőrizhető"});
          return {mapId:"00000000-0000-4000-8000-000000003888",
            mapName:"Scorched Earth",registered:true,storeProtected:true,
            requiresOriginalLaunchVerification:true,serverStarted:false,
            worldFilesChanged:false,evidence:"HISTORICAL_STORE_V1_REGISTERED_QUARANTINED"};
        case "steam.freshCurrent": return {current:null};
        case "steam.freshPreflight": return {
          token:"00000000-0000-4000-8000-000000008888",
          mapName:m.mapName||"Valguero",mapLevel:"Valguero_WP",folderName:"ASA-NEW",
          steamCmdPresent:true,usingManagerDefaultSteamCmd:true,needsOfficialSteamCmdDownload:false,
          existingInterruptedInstall:false,minimumFreeGiB:35,
          evidence:"FRESH_INSTALL_PREFLIGHT_NO_FILE_MUTATION"
        };
        case "steam.freshStart":
          freshId="00000000-0000-4000-8000-000000008889";
          return {jobId:freshId,started:true,evidence:"EXPLICIT_FRESH_INSTALL_STARTED"};
        case "steam.freshStatus":
          return {jobId:freshId,mapName:"Valguero",stage:"verified",
            detail:"ASA EXE és Steam appmanifest ellenőrizve",percent:100,
            verified:true,registered:false,errorCode:null,evidence:"NATIVE_STEAMCMD_JOB_STATE"};
        case "steam.freshRegister":
          state.local.maps.push({id:"00000000-0000-4000-8000-000000008881",
            name:"Valguero",running:false,status:"Nincs beállítva",gamePort:0,
            steamBuildId:"123456",steamUpdateState:"Nem ellenőrizhető"});
          return {mapId:"00000000-0000-4000-8000-000000008881",
            mapName:"Valguero",needsConfiguration:true,serverStarted:false,
            evidence:"VERIFIED_STEAM_FILES_REGISTERED_MANAGER_PROFILE_ONLY"};
        case "steam.freshCancel": return {cancelRequested:true};
        case "steam.freshDiscard": return {discarded:true};
        case "servers.initialStatus": {
          const map = state.local.maps.find(x=>x.id===m.mapId);
          if(!map)throw new Error("UM-MAP-NOT-OWNED");
          return {mapId:map.id,mapName:map.name,level:"Genesis_WP",
            initialSetupAvailable:map.gamePort===0,confirmedStopped:!map.running,
            hasServerExecutable:true,gamePort:map.gamePort,queryPort:0,
            suggestion:{game:7797,query:27035},
            evidence:"LOCAL_MANAGER_PROFILE_FIRST_SETUP_STATUS"};
        }
        case "servers.initialPreview": return {
          token:"00000000-0000-4000-8000-000000008877",mapId:m.mapId,
          mapName:"Genesis Part 1",sessionName:m.sessionName||"Untamed Kingdom",
          mapLevel:"Genesis_WP",gamePort:m.gamePort||7797,queryPort:m.queryPort||27035,
          rconEnabled:false,serverStartRequested:false,configuredArgumentsLength:93,
          evidence:"INITIAL_MAP_PROFILE_PREVIEW_NO_WRITES"
        };
        case "servers.initialApply": {
          const map=state.local.maps.find(x=>x.name==="Genesis Part 1");
          if(map)map.gamePort=7797;
          return {mapId:map?.id||"",mapName:"Genesis Part 1",
            configured:true,gamePort:7797,queryPort:27035,
            rconEnabled:false,serverStarted:false,
            evidence:"ATOMIC_MANAGER_PROFILE_INITIAL_SETUP_ONLY"};
        }
        case "servers.inspectLocal": return {importToken:"00000000-0000-4000-8000-000000009999",name:m.name||"The Island",folderName:"ASA-LAB",exeFound:true,apiLoaderDetected:false,portsConfigured:false,evidence:"LOCAL_BINARY_LAYOUT_PROBED_NO_MUTATION"};
        case "servers.importLocal":
          state.local.maps.push({id:"00000000-0000-4000-8000-000000009998",name:"Genesis Part 1",running:false,status:"Nincs beállítva",gamePort:0,steamBuildId:null,steamUpdateState:"Nem ellenőrizhető"});
          return {mapId:"00000000-0000-4000-8000-000000009998",mapName:"Genesis Part 1",exeRegistered:true,needsConfiguration:true,evidence:"MANAGER_PROFILE_ONLY_NO_ASA_CHANGE"};
        case "dashboard.snapshot": return state;
        case "ini.open": return {token:iniToken,mapId:one,file:m.file||"Game.ini",content:"[ServerSettings]\\nExample=1\\n",encoding:"utf-8",sha256:"AABBCC",bytes:28,evidence:"STOPPED_LOCAL_INI_SNAPSHOT"};
        case "ini.preview": return {previewToken:iniToken,changed:true,oldLines:3,newLines:3,bytes:28,originalSha256:"AABBCC",nextSha256:"FFEE11",evidence:"HASHED_IN_MEMORY_PREVIEW"};
        case "ini.save": return {saved:true,backupCreated:true,sha256:"FFEE11",evidence:"ATOMIC_LOCAL_INI_SAVE_ETAG_VERIFIED"};
        case "ini.close": return {closed:true};
        case "mods.draftStart": stagedId="";return draftState();
        case "mods.draftStage": stagedId=String(m.modId||"");redoId="";return draftState();
        case "mods.draftUndo": redoId=stagedId;stagedId="";return draftState();
        case "mods.draftRedo": stagedId=redoId;redoId="";return draftState();
        case "mods.draftDiscard": stagedId="";return {discarded:true};
        case "mods.draftSave": return {saved:true,pending:true,active:["123456",stagedId],passive:["789012"],disabled:[],evidence:"CONFIGURED_ONLY_PENDING_REAL_RESTART"};
        case "curseforge.credentialStatus": return {configured:keySaved};
        case "curseforge.credentialSave":
          if(!m.credential || m.credential.length<3) throw new Error("invalid");
          keySaved = true; return {configured:true,verifiedNow:true};
        case "curseforge.credentialClear": keySaved=false;return {configured:false,verifiedNow:false};
        case "mods.configured": return {mapId: m.mapId, mapName: "The Island", pending: false, active: ["123456"], passive: ["789012"], disabled: [], evidence:"CONFIGURED_ONLY"};
        case "plugins.inventory": return {mapId:m.mapId,mapName:"The Island",evidence:"READ_ONLY_LOCAL_FILE_INVENTORY",plugins:[{name:"ExamplePlugin",protectedData:false,binaries:[{name:"ExamplePlugin.dll",version:"1.0",bytes:1234}]}]};
        case "backups.summary": return {evidence:"FILE_INTEGRITY_ONLY_NOT_ASA_SAVE_COMPLETE",maps:[{mapId:one,mapName:"The Island",validPackages:1,invalidPackages:0,verifiedBytes:1250000,latestVerifiedUtc:"2026-11-05T12:00:00Z"}]};
        case "steam.installations": return {evidence:"READ_ONLY_STEAM_INSTALLATION_PREFLIGHT",installations:[{id:0,maps:["The Island","Astraeos"],executablePresent:true,installedSteamBuild:"123456",updateBlocked:true,reason:"Read-only test fixture: upgrade blocked"}]};
        case "servers.suggestPorts": return {game:7797,query:27035,rcon:37035,evidence:"PROFILE_ONLY_NO_NETWORK_PROBE"};
        case "logs.read": return {mapId:m.mapId,mapName:"The Island",lastModifiedUtc:"2026-11-05T12:00:00Z",totalBytes:1900,truncated:false,evidence:"SANITIZED_LOCAL_LOG_TAIL_NOT_SERVER_HEALTH",lines:[{number:1,level:"info",content:"Synthetic ASA startup line"},{number:2,level:"error",content:"Synthetic failure detected"}]};
        case "curseforge.categories": return {
          evidence:"OFFICIAL_ASA_CATEGORY_METADATA",
          categories:[{id:101,name:"Structures",isClass:false,parentCategoryId:0},{id:102,name:"Creatures",isClass:false,parentCategoryId:0}]
        };
        case "curseforge.featured": return {
          evidence:"OFFICIAL_ASA_FEATURED_METADATA",gameId:1234,
          featured:[{id:123456,name:"Featured Mod",summary:"Synthetic featured mod",website:null,
            thumbnailUrl:null,authors:["Lab"],categories:["Structures"],screenshots:[],isAvailable:true,
            latestFile:"v1.0",modifiedUtc:null,downloadCount:650}],
          popular:[],recentlyUpdated:[]
        };
        case "curseforge.project": return {
          evidence:"OFFICIAL_ASA_PROJECT_METADATA_ONLY",
          mod:{id:m.modId||123456,name:(m.modId===123456?"Example Search Result":"Featured Mod"),
            summary:"Synthetic detailed description",website:null,thumbnailUrl:null,authors:["Lab"],
            categories:["Structures"],screenshots:[],isAvailable:true,latestFile:"v2.0",
            modifiedUtc:null,downloadCount:1000},
          description:"Synthetic provider description, inert plain text",
          files:[{fileId:11111,name:"Example provider file",published:"2026-11-01T10:00:00Z",
            available:true,sizeBytes:125000,gameVersions:["ASA"],
            dependencies:[{modId:456789,type:"RequiredDependency"}],
            distributionState:"File available"}],
          filesTotal:30,filesHasMore:true
        };
        case "curseforge.files": return {
          evidence:"OFFICIAL_ASA_FILE_METADATA_ONLY",modId:m.modId||123456,
          page:m.page||0,hasMore:false,totalCount:30,
          files:[{fileId:22222,name:"Example older version",published:null,
            available:true,sizeBytes:200000,gameVersions:["ASA"],dependencies:[],distributionState:"File available"}]
        };
        case "curseforge.browse": return {
          gameId: 1234,totalCount: 25,page:m.page||0,hasMore:false,evidence:"OFFICIAL_CATALOG_METADATA_ONLY",
          mods:[{id:123456,name:"Example Search Result",summary:"Synthetic catalog response for UI test",website:null,thumbnailUrl:null,authors:["Lab"],categories:["Structures"],screenshots:[],isAvailable:true,latestFile:null,modifiedUtc:null,downloadCount:15}]
        };
      }
      throw new Error("Denied");
    };
    Object.assign(window, {
      __UNTAMED_TEST_FIXTURE__: true,
      chrome: { webview: {
        addEventListener: (_name: string, fn: Handler) => { handlers.push(fn); },
        postMessage: (msg: {id:string;method:string}) => {
          queueMicrotask(() => {
            try {
              const data = answers(msg);
              handlers.forEach(fn=>fn({ data: {id:msg.id,ok:true,data} }));
            } catch {
              handlers.forEach(fn=>fn({ data: {id:msg.id,ok:false,error:"UM-METHOD-DENIED"} }));
            }
          });
        }
      }}
    });
  }, { one: first, two: second });
});

test("real UI contract renders the synthetic dashboard and responsive navigation", async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Cluster Command Center" })).toBeVisible();
  await expect(page.getByText("KIZÁRÓLAG SZINTETIKUS UI-TESZTADAT")).toBeVisible();
  await expect(page.getByText("LAB-WIN-01").first()).toBeVisible();
  await expect(page.getByText("The Island").first()).toBeVisible();
  await page.screenshot({ path: "test-results/dashboard-1600.png", fullPage: true });

  await page.getByRole("button", { name: "Modok", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Mod Center" })).toBeVisible();
  await expect(page.getByText("Modprojekt · 123456")).toBeVisible();

  await page.getByRole("textbox", { name: "CurseForge mod keresése" }).fill("Shiny");
  await page.getByRole("button", { name: "Keresés", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Example Search Result" })).toBeVisible();
  await page.screenshot({ path: "test-results/mod-center-1600.png", fullPage: true });

  await page.getByRole("button", { name: "Pluginok" }).click();
  await expect(page.getByText("ExamplePlugin").first()).toBeVisible();
  await page.getByRole("button", { name: "Mentések" }).click();
  await expect(page.getByText("1 rendben")).toBeVisible();
});

test("1366px viewport preserves keyboard focus and page scrolling", async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Cluster Command Center" })).toBeVisible();
  const sidebar = page.locator(".sidebar");
  await expect(sidebar).toBeVisible();
  await page.getByRole("button", { name: "Szerverek", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Szerverek" })).toBeVisible();
  const search = page.getByRole("textbox", { name: "Map keresése" });
  await search.focus();
  await expect(search).toBeFocused();
  await search.fill("Astraeos");
  await expect(page.getByText("Astraeos").first()).toBeVisible();
  await page.screenshot({ path: "test-results/server-list-1366.png", fullPage: true });
});

test("Steam installation preflight and redacted log center are read-only and filterable", async ({page}) => {
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.getByRole("button", { name: "SteamCMD" }).click();
  await expect(page.getByRole("heading", {name:"SteamCMD Center"})).toBeVisible();
  await expect(page.getByText("Build: 123456")).toBeVisible();
  await expect(page.getByText("2797", {exact:true})).toHaveCount(0);
  await expect(page.getByText("7797", {exact:true})).toBeVisible();
  await expect(page.getByText("Frissítés tiltva")).toBeVisible();
  await page.screenshot({path:"test-results/steam-preflight-1366.png",fullPage:true});

  await page.getByRole("button", { name: "Naplók" }).click();
  await expect(page.getByRole("heading", {name:"Naplóközpont"})).toBeVisible();
  await expect(page.getByText("Synthetic ASA startup line")).toBeVisible();
  await page.getByRole("combobox", {name:"Naplószint"}).selectOption("error");
  await expect(page.getByText("Synthetic failure detected")).toBeVisible();
  await expect(page.getByText("Synthetic ASA startup line")).toHaveCount(0);
  await page.getByRole("textbox", {name:"Keresés a naplóban"}).fill("no match");
  await expect(page.getByText("Nincs a szűrőnek megfelelő naplósor.")).toBeVisible();
  await page.screenshot({path:"test-results/log-center-filter-1366.png",fullPage:true});
});

test("credential settings keeps API secret hidden and only saves on explicit submission", async ({page}) => {
  await page.goto("/");
  await page.getByRole("button",{name:"Beállítások"}).click();
  await expect(page.getByRole("heading",{name:"Beállítások"})).toBeVisible();
  await expect(page.getByText("Nincs beállított API-kulcs")).toBeVisible();
  const secret = page.getByLabel("CurseForge API-kulcs (nem kerül megjelenítésre)");
  await expect(secret).toHaveAttribute("type","password");
  await secret.fill("synthetic-credential-for-ui-test");
  await page.getByRole("button",{name:"Kulcs ellenőrzése és mentése"}).click();
  await expect(page.getByText("API-kulcs helyileg beállítva")).toBeVisible();
  await expect(secret).toHaveValue("");
  await expect(page.getByText("synthetic-credential-for-ui-test")).toHaveCount(0);
  await page.getByRole("button",{name:"Mentett kulcs törlése"}).click();
  await expect(page.getByRole("button",{name:"Megerősítés: kulcs törlése"})).toBeVisible();
  await page.getByRole("button",{name:"Megerősítés: kulcs törlése"}).click();
  await expect(page.getByText("Nincs beállított API-kulcs")).toBeVisible();
});

test("native mod draft contract stages in memory and saves only on explicit click",async ({page})=>{
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await expect(page.getByRole("heading",{name:"Mod Center"})).toBeVisible();
  await page.getByRole("button",{name:"Piszkozat indítása"}).click();
  await page.getByRole("textbox",{name:"Modprojekt ID"}).fill("567890");
  await page.getByRole("button",{name:"Lépés hozzáadása"}).click();
  await expect(page.locator(".draft-difference").getByText("567890")).toBeVisible();
  await page.screenshot({path:"test-results/mod-draft-1600.png",fullPage:true});
  await page.getByRole("button",{name:"Visszavonás"}).click();
  await expect(page.locator(".draft-difference")).toHaveCount(0);
  await page.getByRole("button",{name:"Újra",exact:true}).click();
  await expect(page.locator(".draft-difference").getByText("567890")).toBeVisible();
  await page.getByRole("button",{name:"Jóváhagyás és mentés"}).click();
  await expect(page.getByText("Modprojekt · 567890")).toBeVisible();
  await expect(page.getByText(/Mentve a helyi indítási konfigurációba/)).toBeVisible();
});

test("INI editor requires explicit open, preview and save in test contract",async ({page})=>{
  await page.setViewportSize({width:1366,height:768});
  await page.goto("/");
  await page.getByRole("button",{name:"INI szerkesztő"}).click();
  await expect(page.getByRole("heading",{name:"INI szerkesztő"})).toBeVisible();
  await page.getByRole("button",{name:"Fájl biztonságos megnyitása"}).click();
  const editor=page.getByRole("textbox",{name:"Az eredeti fájl szerkeszthető tartalma"});
  await expect(editor).toBeVisible();
  await expect(page.getByRole("button",{name:"Jóváhagyás és atomikus mentés"})).toBeDisabled();
  await editor.fill("[ServerSettings]\\nExample=2\\n");
  await page.getByRole("button",{name:"Hash és változások előnézete"}).click();
  await expect(page.getByText(/FFEE11/).first()).toBeVisible();
  await expect(page.getByRole("button",{name:"Jóváhagyás és atomikus mentés"})).toBeEnabled();
  await page.screenshot({path:"test-results/ini-preview-1366.png",fullPage:true});
  await page.getByRole("button",{name:"Jóváhagyás és atomikus mentés"}).click();
  await expect(page.getByText(/visszaállítási \.bak másolattal/)).toBeVisible();
});

test("720px desktop sidebar scroll reaches Settings and INI without clipping", async ({page})=>{
  await page.setViewportSize({width:1280,height:720});
  await page.goto("/");
  const nav=page.getByRole("navigation",{name:"Fő navigáció"});
  await expect(nav).toBeVisible();
  await expect.poll(async()=>nav.evaluate(el=>el.scrollHeight>el.clientHeight)).toBeTruthy();
  await page.getByRole("button",{name:"Beállítások"}).click();
  await expect(page.getByRole("heading",{name:"Beállítások"})).toBeVisible();
  await page.getByRole("button",{name:"INI szerkesztő"}).click();
  await expect(page.getByRole("heading",{name:"INI szerkesztő"})).toBeVisible();
});

test("local map card opens map-specific details, functional tabs, back navigation and blocks RCON", async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("row",{name:/The Island/}).first().click();
  await expect(page.getByRole("heading",{name:"The Island",exact:true})).toBeVisible();
  await expect(page.getByRole("tablist",{name:"Map funkciói"})).toBeVisible();
  await expect(page.getByRole("tab",{name:/RCON/})).toBeDisabled();
  await page.getByRole("tab",{name:"Modok"}).click();
  await expect(page.getByRole("heading",{name:"Mod Center"})).toBeVisible();
  await page.getByRole("tab",{name:"INI"}).click();
  await expect(page.getByRole("heading",{name:"INI szerkesztő"})).toBeVisible();
  await page.getByRole("tab",{name:"Naplók"}).click();
  await expect(page.getByRole("heading",{name:"Naplóközpont"})).toBeVisible();
  await page.getByRole("button",{name:"Vissza a szerverlistához"}).click();
  await expect(page.getByRole("heading",{name:"Szerverek"})).toBeVisible();
  await page.screenshot({path:"test-results/map-detail-navigation-1600.png",fullPage:true});
});

test("local ASA import wizard requires preview and confirmation, then refreshes registered maps",async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Új helyi map felvétele"}).click();
  await expect(page.getByText("Helyi ASA-telepítés felvétele")).toBeVisible();
  await expect(page.getByRole("button",{name:"Ellenőrzött map regisztrálása"})).toBeDisabled();
  await page.getByRole("button",{name:"Genesis Part 1",exact:true}).click();
  await page.getByRole("textbox",{name:"Helyi ASA mappa"}).fill("C:\\ASA-LAB");
  await page.getByRole("button",{name:"Helyi telepítés ellenőrzése"}).click();
  await expect(page.getByText(/Genesis Part 1 · ASA EXE megtalálva/)).toBeVisible();
  await page.getByRole("button",{name:"Ellenőrzött map regisztrálása"}).click();
  await expect(page.getByText(/bekerült a helyi Manager profiljába/)).toBeVisible();
  await expect(page.getByRole("row",{name:/Genesis Part 1/})).toBeVisible();
  await page.screenshot({path:"test-results/map-import-1600.png",fullPage:true});
});

test("CurseForge official Galaxy browses categories, projects, versions, and pre-fills guarded mod editor", async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Modok",exact:true}).click();
  await expect(page.getByText("CurseForge ASA Mod Galaxy")).toBeVisible();
  await expect(page.getByRole("tab",{name:"Kiemelt modok"})).toBeVisible();
  await expect(page.getByText("Featured Mod").first()).toBeVisible();
  await page.getByRole("combobox",{name:"CurseForge kategória"}).selectOption("101");
  await page.getByRole("combobox",{name:"CurseForge rendezés"}).selectOption("2");
  await page.getByRole("textbox",{name:"CurseForge mod keresése"}).fill("Shiny");
  await page.getByRole("button",{name:"Keresés",exact:true}).click();
  await expect(page.getByRole("heading",{name:"Example Search Result"})).toBeVisible();
  await expect(page.getByText("Synthetic provider description, inert plain text")).toBeVisible();
  await expect(page.getByText("Example provider file")).toBeVisible();
  await expect(page.getByText(/456789 \(RequiredDependency\)/)).toBeVisible();
  await page.getByRole("button",{name:"További fájlverziók"}).click();
  await expect(page.getByText("Example older version")).toBeVisible();
  await page.getByRole("button",{name:/Projekt ID előkészítése/}).click();
  await expect(page.getByRole("textbox",{name:"Modprojekt ID"})).toHaveValue("123456");
  await page.screenshot({path:"test-results/curseforge-galaxy-1600.png",fullPage:true});
});

test("SteamCMD new ASA workflow requires approval, follows provider progress, and registers only after verification",async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"SteamCMD"}).click();
  await expect(page.getByText("Új ASA szerver · SteamCMD telepítés")).toBeVisible();
  await page.getByRole("combobox",{name:"Új ASA map"}).selectOption("Valguero");
  await page.getByRole("textbox",{name:"Új ASA célmappa"}).fill("D:\\ASA-NEW");
  // The Manager-managed SteamCMD executable path is optional.
  await expect(page.getByRole("textbox",{name:"SteamCMD EXE"})).toHaveValue("");
  await page.getByRole("button",{name:"Letöltés előtti ellenőrzés"}).click();
  await expect(page.getByText(/Valguero · Valguero_WP/)).toBeVisible();
  await expect(page.getByText(/A Manager saját SteamCMD elérési útját használja/)).toBeVisible();
  await expect(page.getByRole("button",{name:/ASA 2430930 letöltése/})).toBeDisabled();
  await page.getByText(/Jóváhagyom a valódi SteamCMD-lemezműveletet/).click();
  await page.getByRole("button",{name:/ASA 2430930 letöltése/}).click();
  await expect(page.getByText(/ASA EXE és Steam appmanifest ellenőrizve/)).toBeVisible({timeout:10000});
  await page.getByRole("button",{name:"Ellenőrzött ASA regisztrálása a Managerbe"}).click();
  await expect(page.getByText(/Valguero regisztrálva/)).toBeVisible();
  await expect(page.getByText("Valguero").first()).toBeVisible();
  await page.screenshot({path:"test-results/steam-fresh-install-1600.png",fullPage:true});
});

test("Newly imported local map first setup saves session and UDP ports only after preview and confirmation",async ({page})=>{
  await page.setViewportSize({width:1500,height:850});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Új helyi map felvétele"}).click();
  await page.getByRole("button",{name:"Genesis Part 1",exact:true}).click();
  await page.getByRole("textbox",{name:"Helyi ASA mappa"}).fill("C:\\ASA-LAB");
  await page.getByRole("button",{name:"Helyi telepítés ellenőrzése"}).click();
  await page.getByRole("button",{name:"Ellenőrzött map regisztrálása"}).click();
  await page.getByRole("row",{name:/Genesis Part 1/}).click();
  await expect(page.getByText("Első mapbeállítások")).toBeVisible();
  await page.getByRole("textbox",{name:"ASA szerver neve"}).fill("Untamed Kingdom Valued Genesis");
  await page.getByRole("textbox",{name:"Első Game UDP"}).fill("7797");
  await page.getByRole("textbox",{name:"Első Query UDP"}).fill("27035");
  await page.getByRole("button",{name:"Beállítások ellenőrzése"}).click();
  await expect(page.getByText("Konfigurációs előnézet")).toBeVisible();
  await expect(page.getByText("Genesis_WP")).toBeVisible();
  await expect(page.getByRole("button",{name:"Jóváhagyás és első konfiguráció mentése"})).toBeDisabled();
  await page.getByText(/Jóváhagyom a regisztrált helyi map Manager-profiljának mentését/).click();
  await page.getByRole("button",{name:"Jóváhagyás és első konfiguráció mentése"}).click();
  await expect(page.getByText(/indítási profilja mentve/)).toBeVisible();
  await expect(page.getByText("7797").first()).toBeVisible();
  await page.screenshot({path:"test-results/asa-first-map-setup-1500.png",fullPage:true});
});

test("Store-v1 historic import preserves map identity and never permits launch before real validation",async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Kova Store-v1 átvétel"}).click();
  await expect(page.getByText(/Kova Store-v1 · meglévő map átvétele/)).toBeVisible();
  await page.getByRole("textbox",{name:"Kova Manager archívum"}).fill("C:\\Backups\\KovaManager-Fontos.zip");
  await page.getByRole("button",{name:"Archívum ellenőrzése"}).click();
  await expect(page.getByRole("combobox",{name:"Archivált Kova map"})).toBeVisible();
  await page.getByRole("textbox",{name:"Kova ASA map mappája"}).fill("C:\\ASA\\TestExistingMap");
  await page.getByRole("button",{name:"Map és Store-adatok előellenőrzése"}).click();
  await expect(page.getByText(/Teljes launch argumentumok/)).toBeVisible();
  await expect(page.getByText("TILTOTT")).toBeVisible();
  await expect(page.getByRole("button",{name:"Zárolt Store-v1 map regisztrálása"})).toBeDisabled();
  await page.getByText(/Jóváhagyom a kizárólag Manager-profilba történő, zárolt regisztrációt/).click();
  await page.getByRole("button",{name:"Zárolt Store-v1 map regisztrálása"}).click();
  await expect(page.getByText(/Store-v1 zárolt: a szerver nem indulhat/)).toBeVisible();
  await expect(page.getByRole("row",{name:/Scorched Earth/})).toBeVisible();
  await page.screenshot({path:"test-results/store-v1-locked-import-1600.png",fullPage:true});
});

test("Store-v1 cross-host evidence distinguishes historical files from live writer authorization", async ({page})=>{
  await page.setViewportSize({width:1600,height:900});
  await page.goto("/");
  await page.getByRole("button",{name:"Szerverek",exact:true}).click();
  await page.getByRole("button",{name:"Kova Store-v1 átvétel"}).click();
  await page.getByRole("textbox",{name:"Store koordináció ZIP"}).fill("D:\\Store-Coordination.zip");
  await page.getByRole("textbox",{name:"Másik Store host ZIP"}).fill("D:\\Other-Host-Local.zip");
  await page.getByRole("button",{name:"Két host Store-adatainak összevetése"}).click();
  const report=page.getByRole("status").filter({hasText:"Csak offline bizonyíték"});
  await expect(report).toBeVisible();
  await expect(report.getByText("NEM IGAZOLT").first()).toBeVisible();
  await expect(report.getByText("LocalLock")).toBeVisible();
  await expect(report.getByText("TILTOTT")).toBeVisible();
  await page.screenshot({path:"test-results/store-v1-peer-evidence-1600.png",fullPage:true});
});
