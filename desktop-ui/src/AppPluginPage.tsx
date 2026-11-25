import { useEffect, useRef, useState } from "react";
import { Panel } from "./Panel";
import { AlertTriangle, CheckCircle2, Plug, RefreshCw, Shield } from "lucide-react";
import { managerBridge, type PluginInventory, type DashboardSnapshot } from "./bridge";
import { AsaApiLaunchModeSwitch } from "./AsaApiLaunchModeSwitch";
import { AsaApiCoreInstaller } from "./AsaApiCoreInstaller";

export function PluginCenter({ snapshot, selected, setSelected, mapLocked=false,
  onLaunchModeSaved }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (v: string) => void;
  mapLocked?:boolean;onLaunchModeSaved?:()=>Promise<void>
}) {
  const [info,setInfo]=useState<PluginInventory|null>(null);
  const currentPluginMap=useRef(selected);
  currentPluginMap.current=selected;
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const [opening,setOpening]=useState(false);
  const [revision,setRevision]=useState(0);
  useEffect(()=>{
    let active=true;
    setInfo(null);setError("");setNotice("");
    if(selected)managerBridge.pluginInventory(selected)
      .then(result=>{
        if(!active)return;
        if(result.mapId.toLowerCase()!==selected.toLowerCase()||
           result.evidence!=="READ_ONLY_LOCAL_FILE_INVENTORY")
          throw new Error("UM-PLUGIN-INVENTORY-MAP-MISMATCH");
        setInfo(result);
      })
      .catch(e=>{
        if(active)setError(e instanceof Error?e.message:"UM-READ-UNAVAILABLE");
      });
    return ()=>{active=false;};
  },[selected,revision]);
  const openFolder=async()=>{
    if(!selected||opening)return;
    const requested=selected;
    setOpening(true);setNotice("");setError("");
    try{
      const result=await managerBridge.pluginOpenFolder(requested);
      if(currentPluginMap.current!==requested)return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         !result.opened||result.filesChanged||result.serverOperation||
         result.rconSent||result.runtimeLoadedVerified)
        throw new Error("UM-PLUGIN-FOLDER-RECEIPT-MAP-MISMATCH");
      setNotice("A map saját ASA API Plugins mappája megnyílt a Windows Intézőben. A fájlok jelenléte nem igazolja az ASA-beli betöltést.");
    }catch(e){
      if(currentPluginMap.current!==requested)return;
      const code=e instanceof Error?e.message:"UM-PLUGIN-FOLDER-EXPLORER-FAILED";
      setError(code==="UM-PLUGIN-FOLDER-MISSING"
        ?"Az ASA API Plugins mappája még nem található. Ellenőrizd a map saját ASA API telepítését; a Manager nem hoz létre üres helyettesítő mappát."
        :code==="UM-PLUGIN-FOLDER-ASA-MISSING"
        ?"A map tényleges ASA szerverfájljai hiányoznak a regisztrált helyről."
        :"A helyi pluginmappa nem nyitható meg biztonságosan ("+code+").");
    }finally{setOpening(false);}
  };
  const owned=snapshot.local.maps.find(m=>m.id===selected);
  return <div className="premium-plugin-page">
    <div className="page-intro"><span className="eyebrow">ASA API / LOCAL FILES</span>
      <h1>Plugin Center</h1>
      <p>A pluginok másolása és konfigurálása kézzel történik a Windows Intézőben.
        Az Obsidian csak a tényleges helyi mappát nyitja meg, és leltározza
        az ott található fájlokat. Nem telepít, nem tölt újra plugint,
        és nem indít vagy vezérel ASA-szervert.</p>
    </div>
    <div className="premium-mod-map-picker"><Plug size={19}/>
      {mapLocked
        ?<strong aria-label="Pluginok forrásmapja">{owned?.name??"Nem található"}</strong>
        :<select aria-label="Pluginok mapja" value={selected}
          onChange={e=>setSelected(e.target.value)}>
          {snapshot.local.maps.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}
        </select>}
    </div>
    {owned&&<AsaApiLaunchModeSwitch key={selected} map={owned}
        onSaved={onLaunchModeSaved}/>}
    {owned&&(!owned.storeProtected||owned.freshStoreEnrolled)&&
      !owned.observedReadOnlyImported&&<AsaApiCoreInstaller key={selected}
        mapId={selected} mapName={owned.name} onInstalled={()=>{
          // The CORE binaries are separate from manually copied plugins.
          // Refresh only read-only native inventory; never trigger a restart.
          setRevision(n=>n+1);
          if(onLaunchModeSaved)void onLaunchModeSaved().catch(()=>{});
        }}/>}
    {owned&&((owned.storeProtected&&!owned.freshStoreEnrolled)||
      owned.observedReadOnlyImported)&&<div className="warning-inline" role="status">
      <Shield size={17}/> ASA API CORE binárisfrissítés ezen a mapon nem
      támogatott a jelenlegi védett telepítési útvonalon. A pluginmappa
      kézi megnyitása és a meglévő Loader-indítási mód külön működik.
    </div>}
    <Panel className="premium-plugin-inventory"
      title={info?"Helyi pluginfájlok · "+info.mapName:"Helyi pluginfájlok"}>
      <div className="ini-actions">
        <button type="button" className="primary" disabled={!owned||opening}
          onClick={()=>{void openFolder();}}>
          <Plug size={17}/> Pluginmappa megnyitása
        </button>
        <button type="button" className="secondary" disabled={opening}
          onClick={()=>setRevision(n=>n+1)}>
          <RefreshCw size={16}/> Fájlleltár frissítése
        </button>
      </div>
      <p className="muted small">Az adott map tényleges
        ShooterGame/Binaries/Win64/ArkApi/Plugins mappája nyílik meg.
        Működő szervernél sem indít, állít le, indít újra vagy küld RCON-parancsot.</p>
      {info&&<p className="muted small" role="status">ASA API inicializálási napló:
        {" "+(info.apiLogInitialized?"Aktuális folyamatnál igazolt pozitív jel":"Nem igazolt")}.
        Modulmegfigyelés: {info.moduleObservedUtc?new Date(info.moduleObservedUtc).toLocaleString("hu-HU"):"nem ellenőrizhető"}.
      </p>}
      {info?.plugins.map(plugin=><div className="plugin-row" key={plugin.name}>
        <span className="plugin-symbol"><Plug size={19}/></span>
        <div className="plugin-copy">
          <b>{plugin.name}</b>
          <small>{plugin.binaries.map(b=>b.name+
            (b.version?" · "+b.version:"")).join(", ")||"Nincs DLL-adat"}</small>
          <small>config.json: {plugin.configStatus==="valid-json"?"Érvényes JSON-szintaxis":
            plugin.configStatus==="invalid-json"?"Hibás JSON-szintaxis":
            plugin.configStatus==="missing"?"Nem található":
            plugin.configStatus==="protected"?"Védett, nem olvasott":"Nem ellenőrizhető"}</small>
          {plugin.binaries.some(b=>b.sha256)&&<details><summary>DLL ellenőrzőösszegek</summary>
            {plugin.binaries.filter(b=>b.sha256).map(b=><small key={b.name} title={b.sha256}>{b.name} · SHA-256: {b.sha256?.slice(0,16)}…</small>)}
          </details>}
        </div>
        <span className={"mod-tag "+(info.moduleObservation==="observed-current-asa-process"&&plugin.binaries.some(b=>b.loadedModuleObserved)?"green":"amber")}>
          {info.moduleObservation==="observed-current-asa-process"&&plugin.binaries.some(b=>b.loadedModuleObserved)
            ?`${plugin.binaries.filter(b=>b.loadedModuleObserved).length}/${plugin.binaries.length} DLL megfigyelve az aktuális ASA-folyamatban`
            :"Pluginmappa megtalálva · betöltés nem igazolt"}</span>
      </div>)}
      {info&&info.plugins.length===0&&<div className="empty">
        Nem található plugin a map helyi ASA API mappájában.
      </div>}
      {!info&&!error&&selected&&<div className="empty">A helyi fájlleltár olvasása...</div>}
      {notice&&<div className="success-inline" role="status">
        <CheckCircle2 size={16}/>{notice}
      </div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={16}/>{error}
      </div>}
      <div className="warning-inline"><Shield size={17}/>
        A fájlleltár nem igazol betöltést. Az aktuális, azonosított ASA-folyamat moduljai külön megfigyelhetők; ez nem igazolja a hookok vagy a plugin játékbeli működését.
        A Manager nem másol, nem konfigurál és nem reloadol pluginokat.
      </div>
    </Panel>
  </div>;
}
