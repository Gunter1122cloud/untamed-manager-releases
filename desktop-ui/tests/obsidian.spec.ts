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
    let keySaved = false;
    let stagedId = "";
    const draftToken = "00000000-0000-4000-8000-000000001234";
    const draftState = () => ({
      draftId:draftToken,mapId:one,active:stagedId?["123456",stagedId]:["123456"],
      passive:["789012"],disabled:[],pending:!!stagedId,dirty:!!stagedId,
      canUndo:!!stagedId,canRedo:false,evidence:"IN_MEMORY_MOD_DRAFT",
      differences:stagedId?[{modId:stagedId,beforeMode:null,beforePosition:null,afterMode:"active",afterPosition:2}]:[]
    });
    const answers = (m: { method: string; mapId?: string; term?: string; page?: number; credential?: string; draftId?: string; modId?: string; mode?: string }) => {
      switch (m.method) {
        case "dashboard.snapshot": return state;
        case "mods.draftStart": stagedId="";return draftState();
        case "mods.draftStage": stagedId=m.modId||"";return draftState();
        case "mods.draftUndo": stagedId="";return draftState();
        case "mods.draftRedo": stagedId="567890";return draftState();
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
        case "curseforge.search": return {
          gameId: 1234,totalCount: 25,page:m.page||0,hasMore:false,evidence:"OFFICIAL_CATALOG_METADATA_ONLY",
          mods:[{id:123456,name:"Example Search Result",summary:"Synthetic catalog response for UI test",website:null,thumbnailUrl:null,authors:["Lab"],latestFile:null,modifiedUtc:null,downloadCount:15}]
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
