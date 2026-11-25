import { useState } from "react";
import { describeNativeError } from "./state/nativeErrors";
import { Panel } from "./Panel";
import { asaMapNames } from "./state/asaMapSuggestions";
import { AlertTriangle, CheckCircle2, Grid2X2, List, Search, Shield } from "lucide-react";
import { managerBridge, dashboardRemotes, type LocalImportPreview, type DashboardSnapshot } from "./bridge";
import { FreshAsaSteamWizard } from "./FreshAsaSteamWizard";
import { HistoricalStoreImport } from "./HistoricalStoreImport";
import { LocalRunningAsaPanel } from "./LocalRunningAsaPanel";
import { ClusterMapGallery } from "./ClusterMapGallery";
import { FutureMapOnboarding } from "./FutureMapOnboarding";
import { observedAt, MapTable } from "./AppSharedMapViews";

function LocalAsaImport({onImported}: {onImported:()=>Promise<void>}) {
  const [name,setName]=useState("The Island");
  const [root,setRoot]=useState("");
  const [preview,setPreview]=useState<LocalImportPreview | null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const validate = async (e:React.FormEvent) => {
    e.preventDefault(); setError(""); setNotice("");setPreview(null);
    setBusy(true);
    try { setPreview(await managerBridge.inspectLocalAsa(name,root)); }
    catch(e) {
      const code=e instanceof Error?e.message:"UM-IMPORT-VALIDATION-FAILED";
      setError(code==="UM-ASA-EXE-MISSING"
        ? "A megadott helyen nincs meg az ASA ShooterGame/Binaries/Win64/ArkAscendedServer.exe."
        : code==="UM-EXISTING-WORLD-REQUIRES-PRESERVED-IMPORT"
          ? "Ez a telepítés már világmentést tartalmaz. Ne importáld üres szerverként; használd a külön Store-v1 átvételt."
          : describeNativeError(code,"A kiválasztott helyi telepítés nem importálható. Ellenőrizd a mappát és a már regisztrált mapokat."));
    } finally {setBusy(false);}
  };
  const confirm=async()=>{
    if(!preview)return;
    setBusy(true);setError("");setNotice("");
    try {
      const result=await managerBridge.importLocalAsa(preview.importToken);
      setPreview(null);
      setNotice(result.mapName+" bekerült a helyi Manager profiljába. A portok, RCON, modok és indítás később konfigurálhatók.");
      await onImported();
    } catch(e) {
      const code=e instanceof Error?e.message:"UM-IMPORT-VALIDATION-FAILED";
      setPreview(null);
      setError("A natív Manager megtagadta a regisztrációt ("+code+"). Lehetséges versengő konfigurációmódosítás; ellenőrizd a maplistát.");
    } finally{setBusy(false);}
  };
  const updateName=(v:string)=>{setName(v);setPreview(null);setNotice("");};
  const updateRoot=(v:string)=>{setRoot(v);setPreview(null);setNotice("");};
  return <Panel title="Helyi ASA-telepítés felvétele" action={<span className="mod-tag green">Telepítés változatlan</span>}>
    <p className="muted small">Meglévő ASA-szerver EXE importálása a saját Manager profilba. Nem másol, nem tölt le és nem indít fájlt. A friss SteamCMD telepítés külön művelet.</p>
    <div className="map-name-suggestions" aria-label="ASA mapválasztó">
      {asaMapNames.map(item=><button key={item} type="button" className={name===item?"map-choice selected":"map-choice"}
        onClick={()=>updateName(item)}>{item}</button>)}
    </div>
    <form onSubmit={e=>{void validate(e);}} className="import-form">
      <label>Map neve
        <input aria-label="Map neve" value={name} maxLength={100} onChange={e=>updateName(e.target.value)} required/>
      </label>
      <label>Helyi ASA mappa (gyökér / ShooterGame / Binaries / Win64)
        <input aria-label="Helyi ASA mappa" value={root} maxLength={520} onChange={e=>updateRoot(e.target.value)}
          placeholder="C:\\ASA\\TheIsland" autoComplete="off" required/>
      </label>
      <div className="import-actions">
        <button type="submit" disabled={busy || !root.trim() || !name.trim()} className="secondary">
          {busy?"Ellenőrzés...":"Helyi telepítés ellenőrzése"}
        </button>
        <button type="button" disabled={busy || !preview} className="primary" onClick={()=>{void confirm();}}>
          Ellenőrzött map regisztrálása
        </button>
      </div>
    </form>
    {preview && <div className="import-preview">
      <CheckCircle2 size={22}/><div><b>{preview.name} · ASA EXE megtalálva</b>
        <p>Mappa: {preview.folderName || "Kiválasztott helyi gyökér"} · ASA API loader: {preview.apiLoaderDetected?"észlelve, de nincs engedélyezve":"nem észlelhető"}</p>
        {preview.rootResolvedFromNestedFolder&&<p>A Manager felismerte a fölérendelt ASA telepítési gyökeret. A regisztráció ehhez a gyökérhez tartozik.</p>}
        <p>Portok, RCON és modok: később beállíthatók. Nem indul ASA.</p>
      </div>
    </div>}
    {notice && <div className="success-inline" role="status"><CheckCircle2 size={16}/>{notice}</div>}
    {error && <div className="error-inline" role="alert"><AlertTriangle size={16}/>{error}</div>}
    <div className="warning-inline"><Shield size={17}/> Csak helyi, létező ASA EXE regisztrálható. Az új Managerben egy map hozzáadásával semmilyen világ- vagy karakterfájl nem módosul.</div>
  </Panel>;
}

export function Servers({ snapshot, search, setSearch, onSelect, onImported, onCluster }: {
  snapshot: DashboardSnapshot; search: string; setSearch: (v: string) => void; onSelect: (v: string) => void;
  onImported:()=>Promise<void>;onCluster:()=>void
}) {
  const [importMode,setImportMode]=useState<"none"|"wizard"|"new"|"existing"|"store">("none");
  // A first-run Windows test machine should show real map choices instead of
  // a half-page empty table. Existing cluster installs retain compact list.
  const [serverView,setServerView]=useState<"table"|"gallery">(
    snapshot.local.maps.length===0?"gallery":"table");
  const [installMap,setInstallMap]=useState("The Island");
  const openInstall=(mapName?:string)=>{if(mapName)setInstallMap(mapName);setImportMode("new");};
  return <><div className="page-intro"><span className="eyebrow">LOCAL HOST / LAN</span><h1>Szerverek</h1><p>A helyi mapok megfigyelése; távoli mapok kizárólag olvashatók.</p></div>
    <div className="server-toolbar">
      <div className="toolbar"><Search size={19}/><input aria-label="Map keresése" placeholder="Map keresése..." value={search} onChange={e=>setSearch(e.target.value)}/></div>
      <div className="server-import-buttons">
        <button className="primary" type="button"
          onClick={()=>setImportMode(v=>v==="wizard"?"none":"wizard")}>
          {importMode==="wizard"?"Maphozzáadás bezárása":"Új map hozzáadása"}
        </button>
        <div className="premium-view-switcher" role="group" aria-label="Szervernézet">
          <button type="button" className={serverView==="table"?"is-active":""}
            aria-pressed={serverView==="table"} onClick={()=>setServerView("table")}><List size={15}/> Lista</button>
          <button type="button" className={serverView==="gallery"?"is-active":""}
            aria-pressed={serverView==="gallery"} onClick={()=>setServerView("gallery")}><Grid2X2 size={15}/> Kártyák</button>
        </div>
        <button className="secondary" onClick={()=>setImportMode(v=>v==="store"?"none":"store")}>
          {importMode==="store"?"Store-v1 import bezárása":"Store-v1 átvétel"}
        </button>
        <button className="secondary" onClick={()=>setImportMode(v=>v==="existing"?"none":"existing")}>
          {importMode==="existing"?"Import bezárása":"Meglévő ASA importálása"}
        </button>
        <button className="primary" onClick={()=>setImportMode(v=>v==="new"?"none":"new")}>
          {importMode==="new"?"Letöltő bezárása":"Új ASA letöltése"}
        </button>
      </div>
    </div>
    {/* Keep the selected onboarding action above the potentially long WMI process list. */}
    {importMode==="wizard"&&<FutureMapOnboarding maps={snapshot.local.maps}
      onInstall={()=>setImportMode("new")} onExisting={()=>setImportMode("existing")}
      onHistorical={()=>setImportMode("store")} onCluster={onCluster}
      onOpenMap={onSelect} onImported={onImported}/>}
    {importMode==="new" && <FreshAsaSteamWizard onRegistered={onImported} initialMapName={installMap}
      onImport={()=>setImportMode("existing")} onOpenMap={onSelect}/>}
    {importMode==="existing" && <LocalAsaImport onImported={onImported}/>}
    {importMode==="store" && <HistoricalStoreImport onImported={onImported}/>}
    <LocalRunningAsaPanel onRegistered={onImported}/>
    {serverView==="gallery"?<Panel title="Mapkártyák · helyi kezelés / távoli megfigyelés">
      <ClusterMapGallery snapshot={snapshot} query={search} onSelect={onSelect}
        onInstall={openInstall} onImport={()=>setImportMode("existing")}/>
    </Panel>:<>
      <Panel title={`Helyi mapok · ${snapshot.local.hostName}`}><MapTable maps={snapshot.local.maps} host={snapshot.local.hostName} filter={search} onPick={onSelect}/></Panel>
      {dashboardRemotes(snapshot).map(remote=><Panel key={remote.hostId}
        title={`Távoli mapok · ${remote.hostName}`}>
        <p className="muted small">{remote.fresh===false?"Elavult / offline megfigyelés":"Hitelesített LAN-adat"}
          {remote.lastSuccessUtc?` · utolsó sikeres mérés: ${observedAt(remote.lastSuccessUtc)}`:""}</p>
        <MapTable maps={remote.maps} host={remote.hostName} filter={search}/>
      </Panel>)}
    </>}
  </>;
}
