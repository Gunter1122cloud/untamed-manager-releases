import { useEffect, useState } from "react";
import { Panel } from "./Panel";
import { IniCenter } from "./IniCenter";
import { ArrowRight, CheckCircle2, FileText, Layers3, Activity, RefreshCw, Shield } from "lucide-react";
import { managerBridge, type DashboardSnapshot, type AsaLifecycleJournal, type AsaRestartJournalStatus } from "./bridge";
import { InitialMapSetup } from "./InitialMapSetup";
import { FreshStoreEnrollmentPanel } from "./FreshStoreEnrollmentPanel";
import { FreshStoreLoaderModePanel } from "./FreshStoreLoaderModePanel";
import { HistoricalStoreImport } from "./HistoricalStoreImport";
import { HistoricalRconAttach } from "./HistoricalRconAttach";
import { HistoricalActiveStartPanel } from "./HistoricalActiveStartPanel";
import { HistoricalRconLivePanel } from "./HistoricalRconLivePanel";
import { HistoricalAdminRconPanel } from "./HistoricalAdminRconPanel";
import { HistoricalGracefulStopPanel } from "./HistoricalGracefulStopPanel";
import { HistoricalStoreRestartPanel } from "./HistoricalStoreRestartPanel";
import { HistoricalStoreSaveWorldPanel } from "./HistoricalStoreSaveWorldPanel";
import { LocalRunningAsaPanel } from "./LocalRunningAsaPanel";
import { MapCover } from "./MapArtwork";
import { InstallationRelocationPanel } from "./InstallationRelocationPanel";
import { StoppedSteamUpdate } from "./StoppedSteamUpdate";
import { FreshAsaStartPanel } from "./FreshAsaStartPanel";
import { FreshAsaStopPanel } from "./FreshAsaStopPanel";
import { FreshAsaRestartPanel } from "./FreshAsaRestartPanel";
import { LocalMapLifecycleBar } from "./LocalMapLifecycleBar";
import { OriginalStoreJournalRecoveryPanel } from "./OriginalStoreJournalRecoveryPanel";
import { RconReadOnlyPanel } from "./RconReadOnlyPanel";
import { OperationsCenter } from "./OperationsCenter";
import { NativeMapScheduler } from "./NativeMapScheduler";
import { RollingSteamSettings } from "./RollingSteamSettings";
import { MapMaintenancePanel } from "./MapMaintenancePanel";
import { State } from "./AppSharedMapViews";
import { Mods } from "./AppModsPage";
import { PluginCenter } from "./AppPluginPage";
import { BackupCenter } from "./AppBackupPage";
import { LogsCenter } from "./AppLogsPage";

type MapSection = "overview" | "monitor" | "scheduler" | "steam" | "mods" | "plugins" | "ini" | "backups" | "logs" | "rcon";
export function MapDetails({snapshot,mapId,onBack,setSelected,onConfigured,onOpenCluster,refreshRevision}: {
  snapshot:DashboardSnapshot;mapId:string;onBack:()=>void;
  setSelected:(value:string)=>void;onConfigured:()=>Promise<void>;
  onOpenCluster:()=>void;refreshRevision:number
}) {
  const [section,setSection]=useState<MapSection>("overview");
  const [originalRconAction,setOriginalRconAction]=useState<"broadcast"|"wildWipe">("broadcast");
  const [openOriginalStoreEnrollment,setOpenOriginalStoreEnrollment]=useState(false);
  const [initialSetupSaved,setInitialSetupSaved]=useState(false);
  const [lifecycle,setLifecycle]=useState<AsaLifecycleJournal|null>(null);
  const [restartJournal,setRestartJournal]=useState<AsaRestartJournalStatus|null>(null);
  const [journalsVerifiedFor,setJournalsVerifiedFor]=useState("");
  const [journalReadError,setJournalReadError]=useState(false);
  const [journalRevision,setJournalRevision]=useState(0);
  const map=snapshot.local.maps.find(m=>m.id===mapId);
  const historical=!!map?.storeProtected&&!map?.freshStoreEnrolled;
  const fresh=!!map&&(!map.storeProtected||!!map.freshStoreEnrolled);
  const observedOnly=!!map?.observedReadOnlyImported;
  const journalScope=mapId+":"+refreshRevision+":"+journalRevision;
  useEffect(()=>{
    setSection("overview");setInitialSetupSaved(false);
    setOriginalRconAction("broadcast");setOpenOriginalStoreEnrollment(false);
  },[mapId]);
  useEffect(()=>{
    let active=true;
    setLifecycle(null);setRestartJournal(null);
    setJournalsVerifiedFor("");setJournalReadError(false);
    if(observedOnly){
      // Observation-only map never needs native lifecycle probes.
      setJournalsVerifiedFor(journalScope);
      return ()=>{active=false;};
    }
    const originalStore=historical;
    Promise.all([
      managerBridge.asaLifecycleJournal(mapId),
      originalStore?Promise.resolve(null):managerBridge.freshAsaRestartCurrent(mapId)
    ]).then(([lifecycleState,restartState])=>{
      if(!active)return;
      if(lifecycleState.mapId.toLowerCase()!==mapId.toLowerCase()){
        setJournalReadError(true);return;
      }
      setLifecycle(lifecycleState);
      setRestartJournal(restartState);
      setJournalsVerifiedFor(journalScope);
    }).catch(()=>{
      if(active)setJournalReadError(true);
    });
    return ()=>{active=false;};
  },[mapId,observedOnly,historical,journalScope]);
  // An observed map may become actively managed without navigating away.
  // Re-probe durable journals on that transition, fail closed until they load.
  const lifecycleBlocked=journalsVerifiedFor!==journalScope||journalReadError||
    Boolean(lifecycle?.needsOperatorReview||restartJournal?.needsOperatorReview);
  // Native reconciliation may finish without a navigation or command receipt.
  // Retry only READ evidence, and only after the previous check has settled.
  // Keep ownership and mutation guards in the native bridge.
  const retryJournalRead=!!map&&!observedOnly&&(journalReadError||
    (journalsVerifiedFor===journalScope&&lifecycleBlocked));
  useEffect(()=>{
    if(!retryJournalRead)return;
    const timer=window.setTimeout(()=>setJournalRevision(n=>n+1),5000);
    return ()=>window.clearTimeout(timer);
  },[retryJournalRead,journalScope]);
  if(!map)return <div className="empty">A kiválasztott map már nem található a helyi Managerben.</div>;
  // WMI registration is a read-only observation, never process ownership.
  if(map.observedReadOnlyImported)return <div className="map-detail-page" role="region"
    aria-label="Megfigyelési map adatlapja">
    <button className="quiet back-to-servers" onClick={onBack}>
      <ArrowRight size={16}/> Vissza a szerverlistához
    </button>
    <div className="map-detail-hero">
      <MapCover name={map.name}/>
      <div className="premium-map-detail-copy">
        <span className="eyebrow">CSAK MEGFIGYELÉS / NINCS ÁTVETT VEZÉRLÉS</span>
        <h1>{map.name}</h1>
        <p>{snapshot.local.hostName} · Game port: {map.gamePort||"ismeretlen"}</p>
      </div>
    </div>
    <div className="warning-inline" role="status"><Shield size={17}/>
      Ez a külső ASA-folyamat kizárólag megfigyelésre került a Managerbe.
      WMI-jelzés: {map.observedRuntimeState==="observedRunning"?
        "megfigyelt futó szerver":"nincs egyértelmű aktuális folyamatbizonyíték"}.
      Start, Stop, Restart, SaveWorld, RCON, INI, mod, plugin, SteamCMD és
      Store-v1 írási jog nem lett átvéve. A futás állapotát az alábbi friss WMI-felderítés mutatja.
    </div>
    <div className="map-overview-actions">
      <button className="primary" type="button"
        aria-expanded={openOriginalStoreEnrollment}
        onClick={()=>setOpenOriginalStoreEnrollment(v=>!v)}>
        {openOriginalStoreEnrollment?
          "eredeti Store-v1 átvételi varázsló bezárása":
          "eredeti Store-v1 mapok átvételi varázslója"}
      </button>
    </div>
    {openOriginalStoreEnrollment&&
      <HistoricalStoreImport onImported={onConfigured}/>}
    <LocalRunningAsaPanel onRegistered={onConfigured}/>
  </div>;
  const sections: Array<{id:MapSection;name:string}>=[
    {id:"overview",name:"Áttekintés"},{id:"monitor",name:"Üzemeltetés"},
    {id:"scheduler",name:"Ütemező"},{id:"steam",name:"Frissítés"},{id:"mods",name:"Modok"},
    {id:"plugins",name:"Pluginok / ASA API"},{id:"ini",name:"INI"},
    {id:"backups",name:"Mentések"},{id:"logs",name:"Naplók"},
    {id:"rcon",name:"RCON / Konzol"}
  ];
  return <div className="map-detail-page">
    <button className="quiet back-to-servers" onClick={onBack}>
      <ArrowRight size={16}/> Vissza a szerverlistához
    </button>
    <div className="map-detail-hero">
      <MapCover name={map.name}/>
      <div className="premium-map-detail-copy">
        <span className="eyebrow">UNTAMED KINGDOM / HELYI MAP</span>
        <h1>{map.name}</h1>
        <p>{snapshot.local.hostName} · ARK: Survival Ascended</p>
      </div>
      <State running={map.running} status={map.status}/>
    </div>
    <LocalMapLifecycleBar key={mapId} mapId={mapId} mapName={map.name}
      running={map.running} historical={historical} journalBlocked={lifecycleBlocked} refreshRevision={refreshRevision}
      onRefresh={onConfigured} onConfigure={()=>setSection(historical?"rcon":"overview")}/>
    {journalReadError&&<div className="warning-inline" role="alert">
      <Shield size={17}/> A helyi ASA-műveleti naplók nem olvashatók.
      A Start, Stop és Restart gombok tiltva maradnak, amíg a naplók ellenőrzése nem sikerül.
    </div>}
    {restartJournal?.needsOperatorReview&&<div className="warning-inline" role="alert">
      <Shield size={17}/> A manuális ASA újraindítás megszakadt ({restartJournal.phase??"ismeretlen"}).
      Régi PID: {restartJournal.oldPid??"?"}; új PID: {restartJournal.newPid||"nem igazolt"}.
      A Manager automatikusan újraellenőrzi a natív bizonyítékot. Igazolatlan műveletet nem ismétel meg.
    </div>}
    {lifecycle?.needsOperatorReview&&<div className="warning-inline" role="alert">
      <Shield size={17}/> Félbeszakadt helyi ASA-művelet ({lifecycle.kind??"ismeretlen"} / {lifecycle.phase??"ismeretlen"}).
      A Manager automatikusan újraellenőrzi a map műveleti állapotát. A napló megmarad, és igazolatlan parancsot nem ismétel meg.
    </div>}
    {historical&&lifecycle?.recoverySupported&&
      <OriginalStoreJournalRecoveryPanel mapId={mapId} journal={lifecycle}
        onRecovered={async()=>{
          setJournalsVerifiedFor("");
          setJournalRevision(n=>n+1);
          await onConfigured();
        }}/>}
    {historical&&lifecycle?.needsOperatorReview&&!lifecycle.recoverySupported&&
      <div className="warning-inline" role="alert">
        <Shield size={17}/> Zárolás: {lifecycle.blockingCode??"UM-RECOVERY-UNKNOWN"}.
        Ez a naplótípus nem rendezhető a futó Launch helyreállítóval;
        az érintett natív művelet külön helyreállítási eljárást igényel.
      </div>}
    <div className="map-detail-tabs" role="tablist" aria-label="Map funkciói">
      {sections.map(item=><button type="button" key={item.id} role="tab"
        aria-selected={section===item.id}
        className={section===item.id?"map-detail-tab selected":"map-detail-tab"}
        onClick={()=>setSection(item.id)}>{item.name}</button>)}

    </div>
    {section==="overview" && <>
      <div className="map-overview-grid">
        <Panel title="Helyi folyamatállapot">
          <div className="map-info-line"><span>Megfigyelt állapot</span><State running={map.running} status={map.status}/></div>
          <div className="map-info-line"><span>Gazdagép</span><strong>{snapshot.local.hostName}</strong></div>
          <div className="map-info-line"><span>Game UDP</span><strong>{map.gamePort || "Nem ismert"}</strong></div>
          <div className="map-info-line"><span>Steam build</span><strong>{map.steamBuildId || "Nem ellenőrizhető"}</strong></div>
          <div className="map-info-line"><span>Frissítési állapot</span><strong>{map.steamUpdateState}</strong></div>
        </Panel>
        <Panel title="Biztonságos karbantartási útvonal">
          <p className="muted small">A regisztrált helyi maphoz már elérhető a modkonfiguráció, az INI, a pluginleltár, a mentéstörténet és a titokszűrt napló.</p>
          <div className="map-overview-actions">
            <button className="primary" onClick={()=>setSection("mods")}><Layers3 size={17}/> Modok kezelése</button>
            <button className="secondary" onClick={()=>setSection("ini")}><FileText size={17}/> INI szerkesztése</button>
            <button className="secondary" onClick={()=>setSection("monitor")}><Activity size={17}/> PID / RAM / állapot</button>
            <button className="secondary" onClick={()=>setSection("steam")}><RefreshCw size={17}/> Frissítés ellenőrzése</button>
          </div>
          <div className="warning-inline"><Shield size={17}/> Frissen telepített helyi map indítása jóváhagyással elérhető. A megőrzött Store-v1 map helyi indítása egy kattintással, natív forrás-, folyamat- és konfiguráció-ellenőrzéssel történik. A karakterutazás, SaveComplete és közös Store-restore igazolása továbbra is külön követelmény.</div>
        </Panel>
      </div>
      <MapMaintenancePanel key={mapId+":MapMaintenancePanel"} mapId={mapId} onConfigured={onConfigured}/>
      {!historical&&!map.freshStoreEnrolled&&!map.observedReadOnlyImported&&<InstallationRelocationPanel key={mapId+":move"} mapId={mapId} running={map.running} onConfigured={onConfigured}/>}
      {fresh&&map.gamePort>0&&<Panel title="Új map teljes indítási beállítása">
        <div role="region" aria-label="Új map beállításának lépései">
          <p className="muted small">
            A beállítások a meglévő natív kezelőkhöz vezetnek. Minden mentés
            az adott map profiljában történik, jóváhagyással; a navigáció
            önmagában nem indít ASA-t, nem küld RCON-t és nem ír Store-adatot.
          </p>
          <div className="map-info-line">
            <span>1. Game port és indítási profil</span>
            <strong>{map.gamePort}</strong>
          </div>
          <div className="map-info-line">
            <span>2. RCON</span>
            <strong>{map.rconConfigured?"Konfigurálva":"Beállítás szükséges"}</strong>
          </div>
          <div className="map-overview-actions">
            <button className="secondary" type="button"
              onClick={()=>setSection("rcon")}>RCON konfigurálása</button>
            <button className="secondary" type="button"
              onClick={()=>setSection("ini")}>Game.ini / GameUserSettings.ini</button>
            <button className="secondary" type="button"
              onClick={()=>setSection("mods")}>Modlista és sorrend</button>
            <button className="secondary" type="button"
              onClick={()=>setSection("plugins")}>ASA API indítási mód</button>
            <button className="secondary" type="button"
              onClick={onOpenCluster}>Cluster hozzárendelés</button>
            <button className="primary" type="button"
              onClick={()=>document.getElementById("fresh-asa-start-control")
                ?.scrollIntoView({behavior:"smooth",block:"start"})}>
              Start és jóváhagyás
            </button>
          </div>
        </div>
      </Panel>}
      {historical&&!map.rconConfigured&&<div className="warning-inline" role="status">
        <Shield size={17}/> Az eredeti Store-v1 map RCON-kapcsolata még nincs
        az Obsidianhoz rendelve. A natív adminparancsokhoz és a külön jóváhagyott
        szervervezérléshez a meglévő RCON-t csatlakoztatni kell.
        <button type="button" className="primary" onClick={()=>setSection("rcon")}>
          Meglévő RCON csatlakoztatása
        </button>
      </div>}
      {historical&&<div className="map-overview-actions">
        <button type="button" className="secondary" onClick={()=>{
          setOriginalRconAction("wildWipe");setSection("rcon");
        }}>
          Vad dinók törlése (RCON)
        </button>
      </div>}
      {historical&&<HistoricalActiveStartPanel key={mapId+":HistoricalActiveStartPanel"} mapId={mapId} rconAttached={!!map.rconConfigured} refreshRevision={refreshRevision} onStarted={onConfigured}/>}
      {historical&&<HistoricalStoreSaveWorldPanel key={mapId+":HistoricalStoreSaveWorldPanel"} mapId={mapId} running={map.running} refreshRevision={refreshRevision} onSaved={onConfigured}/>}
      {historical&&<HistoricalGracefulStopPanel key={mapId+":HistoricalGracefulStopPanel"} mapId={mapId} running={map.running} refreshRevision={refreshRevision} onStopped={onConfigured}/>}
      {historical&&<HistoricalStoreRestartPanel key={mapId+":HistoricalStoreRestartPanel"} mapId={mapId} running={map.running} refreshRevision={refreshRevision} onRestarted={onConfigured}/>}
      {map.gamePort>0 && fresh && <div id="fresh-asa-start-control">
        <FreshAsaStartPanel mapId={mapId} onStarted={onConfigured}
          onOpenRcon={()=>setSection("rcon")} journalBlocked={lifecycleBlocked}/>
      </div>}
      {map.gamePort>0 && fresh && <FreshAsaStopPanel mapId={mapId} running={map.running} onStopped={onConfigured} journalBlocked={lifecycleBlocked}/>}
      {map.gamePort>0 && fresh && <FreshAsaRestartPanel mapId={mapId} running={map.running}
        onRestarted={async()=>{
          // A successful native Restart-v1 review changes the durable
          // journal, not the dashboard snapshot. Re-read BOTH journals
          // before unlocking any lifecycle button; a stale parent
          // needsOperatorReview flag must not strand an audited server.
          // The same recheck after a normal Restart ensures that
          // independently pending operations still fail closed.
          setJournalsVerifiedFor("");
          setJournalRevision(n=>n+1);
          await onConfigured();
        }} journalBlocked={lifecycleBlocked}/>}
      {map.gamePort>0 && (!map.storeProtected||map.freshStoreEnrolled) &&
        <FreshStoreEnrollmentPanel key={mapId+":FreshStoreEnrollmentPanel"} mapId={mapId} onConfigured={onConfigured}/>}
      {map.freshStoreEnrolled &&
        <FreshStoreLoaderModePanel key={mapId+":FreshStoreLoaderModePanel"} mapId={mapId} onConfigured={onConfigured}/>}
      {map.gamePort===0 && <InitialMapSetup mapId={mapId} onConfigured={async()=>{
        await onConfigured();
        setInitialSetupSaved(true);
      }}/>}
      {map.gamePort>0 && fresh && !map.rconConfigured && <div className="warning-inline" role="status">
        <Shield size={17}/> Az első ASA-indítás előtt készítsd elő a RCON-t a meglévő natív, egyszeri GameUserSettings.ini mechanizmussal.
        <button type="button" className="secondary" onClick={()=>setSection("rcon")}>Első RCON beállítása</button>
      </div>}
      {map.gamePort>0 && initialSetupSaved && <div role="status" className="success-inline">
        <CheckCircle2 size={17}/>{map.name} indítási profilja mentve. A szerver nem indult el; RCON és modok később konfigurálhatók.
      </div>}
    </>}
    {section==="monitor" && <OperationsCenter selectedMapId={mapId} onOpenMap={()=>setSection("overview")}/>}
    {section==="scheduler" && <>
      <NativeMapScheduler mapId={mapId}/>
      <OperationsCenter selectedMapId={mapId} showScheduler onOpenMap={()=>setSection("overview")}/>
    </>}
    {section==="steam" && <div>
      {map.storeProtected&&!map.freshStoreEnrolled&&
        <div className="warning-inline"><Shield size={17}/> Eredeti Store-v1: a natív SteamCMD-frissítés csak teljesen leállított helyi ASA/Loader folyamatokkal, előzetes védőmentéssel és jóváhagyással hajtható végre. A közös Store és a másik gép nem módosul.</div>}
      {map.freshStoreEnrolled&&
        <div className="warning-inline"><Shield size={17}/> Új Store-map: csak az egyedül birtokolt, igazoltan leállított helyi ASA-telepítés frissíthető, a ClusterRegistry- és transzfer-fájlazonosság újraellenőrzésével. A közös transzferadatokat nem módosítjuk, és nincs automatikus újraindítás.</div>}
      <RollingSteamSettings key={mapId} mapId={mapId}/>
      <StoppedSteamUpdate maps={[map]} fixedMapId={mapId}/>
    </div>}
    {section==="mods" && <Mods key={mapId} snapshot={snapshot} selected={mapId} setSelected={setSelected} mapLocked/>} 
    {section==="plugins" && <PluginCenter key={mapId} snapshot={snapshot} selected={mapId} setSelected={setSelected} mapLocked onLaunchModeSaved={onConfigured}/>} 
    {section==="ini" && <IniCenter key={mapId} snapshot={snapshot} selected={mapId} setSelected={setSelected} mapLocked/>}
    {section==="backups" && <BackupCenter mapId={mapId} snapshot={snapshot}/>}
    {section==="logs" && <LogsCenter key={mapId} snapshot={snapshot} selected={mapId} setSelected={setSelected} mapLocked/>}
    {section==="rcon" && (historical
      ?<>
        <HistoricalRconAttach key={mapId+":HistoricalRconAttach"} mapId={mapId} onConfigured={onConfigured}/>
        {map.rconConfigured
          ?<><HistoricalRconLivePanel key={mapId+":HistoricalRconLivePanel"} mapId={mapId} running={map.running}/>
            <HistoricalAdminRconPanel key={mapId+":HistoricalAdminRconPanel"} mapId={mapId}
              initialAction={originalRconAction} running={map.running}/></>
          :<div className="warning-inline" role="status">
            <Shield size={17}/> A meglévő RCON-csatlakoztatás befejezéséig
            az adminparancsok és a konzol zároltak. A portot és jelszót
            a fenti űrlapon, natív jóváhagyással állíthatod be.
          </div>}
      </>
      :<RconReadOnlyPanel key={mapId} mapId={mapId} onConfigured={onConfigured} onOpenStart={()=>setSection("overview")}/>)}
  </div>;
}
