import { useCallback, useEffect, useRef, useState } from "react";
import { Activity, AlertTriangle, RefreshCw, Shield } from "lucide-react";
import { managerBridge, type RunningLocalAsaSnapshot, type RunningLocalAsaProcess,
  type RunningAsaImportPreview, type RunningAsaAdoptPreview,
  type RunningAsaBatchPreview } from "./bridge";


/** Two-stage registration changes ONLY the local Manager profile, never ASA. */
function LiveImportControls({process,afterImport}:{process:RunningLocalAsaProcess;afterImport:()=>void}) {
  const [name,setName]=useState(process.mapKey.replace(/_WP$/i,"").replace(/_/g," "));
  const [game,setGame]=useState(String(process.launchFacts?.gamePort??""));
  const [query,setQuery]=useState(String(process.launchFacts?.queryPort??""));
  const [preview,setPreview]=useState<RunningAsaImportPreview|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const validPort=(v:string)=>v===""?0:/^\d{1,5}$/.test(v)?Number(v):NaN;
  const prepare=async()=>{
    if(!process.startedUtc)return;
    setBusy(true);setError("");setPreview(null);
    try {
      setPreview(await managerBridge.runningAsaPreview(process.pid,process.startedUtc,
        name,validPort(game),validPort(query)));
    } catch(e) {setError(e instanceof Error?e.message:"UM-LIVE-PREVIEW-FAILED");}
    finally {setBusy(false);}
  };
  const confirm=async()=>{
    if(!preview)return;
    setBusy(true);setError("");
    try {
      const receipt=await managerBridge.runningAsaRegister(preview.token);
      if(!receipt.readOnly||receipt.lifecycleAuthorized||receipt.storeWriterAuthorized)
        throw new Error("UM-LIVE-UNEXPECTED-AUTHORIZATION");
      setPreview(null);afterImport();
    } catch(e) {setPreview(null);setError(e instanceof Error?e.message:"UM-LIVE-REGISTER-FAILED");}
    finally {setBusy(false);}
  };
  return <div className="import-preview" aria-label={`Futó ASA regisztrálása PID ${process.pid}`}>
    <div>
      <p className="muted small">Futó szerver hozzáadása a Managerhez leállítás nélkül.
        Csak megfigyelés; RCON, Start/Stop és Store-writer jog nélkül.</p>
      <label>Map megjelenített neve
        <input aria-label={`Map neve PID ${process.pid}`} value={name} maxLength={96}
          disabled={busy} onChange={e=>{setName(e.target.value);setPreview(null);}}/>
      </label>
      <label>Game port
        <input aria-label={`Game port PID ${process.pid}`} inputMode="numeric" value={game}
          disabled={busy} onChange={e=>{setGame(e.target.value);setPreview(null);}}/>
      </label>
      <label>Query port
        <input aria-label={`Query port PID ${process.pid}`} inputMode="numeric" value={query}
          disabled={busy} onChange={e=>{setQuery(e.target.value);setPreview(null);}}/>
      </label>
      {!preview?<button type="button" className="secondary" disabled={busy||!name.trim()}
        onClick={()=>{void prepare();}}>Regisztráció ellenőrzése</button>:
        <div aria-label="Futó ASA import előnézet">
          <p>{preview.mapName} · {preview.mapKey} · {preview.folderName} ·
            Game {preview.gamePort||"ismeretlen"} · Query {preview.queryPort||"ismeretlen"}</p>
          <p className="muted small">Az aktuális PID és indítási adatok a mentés előtt
            újra ellenőrizve lesznek. Ez nem lifecycle-átvétel.</p>
          <button type="button" disabled={busy} onClick={()=>{void confirm();}}>
            Regisztrálás · csak Manager
          </button>
        </div>}
      {error&&<p className="error-inline" role="alert">{error}</p>}
    </div>
  </div>;
}

/** Explicit two-stage ownership handoff for registered, running NON-STORE ASA. */
function LiveAdoptControls({process,afterAdopt}:{
  process:RunningLocalAsaProcess;afterAdopt:()=>void
}) {
  const [preview,setPreview]=useState<RunningAsaAdoptPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  if(!process.profileMapId)return null;
  const prepare=async()=>{
    setBusy(true);setError("");setPreview(null);setApproved(false);
    try {setPreview(await managerBridge.runningAsaAdoptPreview(process.profileMapId!));}
    catch(e){setError(e instanceof Error?e.message:"UM-ADOPT-PREVIEW-FAILED");}
    finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!preview||!approved)return;
    setBusy(true);setError("");
    try {
      const done=await managerBridge.runningAsaAdoptApply(preview.token,approved);
      if(!done.lifecycleAuthorized||done.storeWriterAuthorized||
         done.processStarted||done.processStopped)
        throw new Error("UM-ADOPT-INVALID-NATIVE-RECEIPT");
      setPreview(null);setApproved(false);afterAdopt();
    } catch(e) {
      setPreview(null);setApproved(false);
      setError(e instanceof Error?e.message:"UM-ADOPT-APPLY-FAILED");
    } finally{setBusy(false);}
  };
  return <div className="import-preview" aria-label={`Futó ASA vezérlés átvétele PID ${process.pid}`}>
    <div>
      <p><b>Vezérlés átvétele · meglévő nem-Store szerver</b></p>
      <p className="muted small">Kétlépcsős, helyi Windows-azonosítás.
        Az átvétel nem állítja le és nem indítja újra az ASA-t; a meglévő
        adatokat és INI-fájlokat nem módosítja. Store-v1 maphoz nem használható.
        RCON/SaveWorld/Stop csak külön konfiguráció és sikeres előfeltétel-ellenőrzés után működik.</p>
      {!preview?<button type="button" className="secondary" disabled={busy}
        onClick={()=>{void prepare();}}>Vezérlés átvételének ellenőrzése</button>:
        <div aria-label="Natív vezérlésátvétel előnézet">
          <p>{preview.mapName} · {preview.mapKey} · PID {preview.pid} ·
            Game {preview.gamePort} / Query {preview.queryPort}</p>
          <label className="muted small">
            <input type="checkbox" checked={approved} disabled={busy}
              onChange={e=>setApproved(e.target.checked)}/>
            Megerősítem, hogy a korábbi Manager már nem felügyeli ezt
            a nem-Store ASA-folyamatot. A vezérlés átvételét kérem.
          </label>
          <button type="button" disabled={busy||!approved}
            onClick={()=>{void apply();}}>Vezérlés átvétele – natív ellenőrzéssel</button>
        </div>}
      {error&&<p className="error-inline" role="alert">{error}</p>}
    </div>
  </div>;
}

/** Live, on-demand WMI process observation. No process adoption or mutation. */
export function LocalRunningAsaPanel({onRegistered}:{
  onRegistered?:()=>Promise<void>;
}={}) {
  const [snapshot,setSnapshot]=useState<RunningLocalAsaSnapshot|null>(null);
  const [loading,setLoading]=useState(true);
  const [error,setError]=useState("");
  const [bulkPreview,setBulkPreview]=useState<RunningAsaBatchPreview|null>(null);
  const [bulkApproved,setBulkApproved]=useState(false);
  const [bulkBusy,setBulkBusy]=useState(false);
  const [bulkError,setBulkError]=useState("");
  const [bulkNotice,setBulkNotice]=useState("");
  // React StrictMode mounts/cleans/remounts effects. A single in-flight
  // boolean may strand the second mount behind an abandoned first request:
  // the process list then stays on "Ellenőrzés..." indefinitely.
  // Each request gets a monotonically increasing identity instead. The
  // newest live response wins, and old responses can never re-authorize PID
  // adoption or overwrite a newer Windows process observation.
  const requestVersion=useRef(0);
  const inspect=useCallback(async (isCurrent:()=>boolean=()=>true)=>{
    const version=++requestVersion.current;
    if(isCurrent()){setLoading(true);setError("");}
    const active=()=>isCurrent()&&version===requestVersion.current;
    try {
      const next=await managerBridge.runningLocalAsa();
      if(active())setSnapshot(next);
    } catch(e) {
      if(active()){
        setSnapshot(null);
        setError(e instanceof Error?e.message:"UM-READ-UNAVAILABLE");
      }
    } finally {
      if(active())setLoading(false);
    }
  },[]);
  useEffect(()=>{
    let current=true;
    void inspect(()=>current);
    const timer=window.setInterval(()=>{void inspect(()=>current);},15000);
    return ()=>{
      current=false;
      ++requestVersion.current;
      window.clearInterval(timer);
    };
  },[inspect]);

  const prepareAll=async()=>{
    if(bulkBusy||!snapshot||snapshot.fresh===false)return;
    setBulkBusy(true);setBulkApproved(false);setBulkPreview(null);
    setBulkError("");setBulkNotice("");
    try{
      const result=await managerBridge.runningAsaBatchPreview();
      if(!result.readOnly||result.lifecycleAuthorized||
         result.storeWriterAuthorized||result.serverProcessTouched||
         !result.count||result.maps.length!==result.count)
        throw new Error("UM-LIVE-BATCH-NATIVE-PREVIEW-MISMATCH");
      setBulkPreview(result);
    }catch(e){
      setBulkError(e instanceof Error?e.message:"UM-LIVE-BATCH-NO-ELIGIBLE");
    }finally{setBulkBusy(false);}
  };
  const applyAll=async()=>{
    if(bulkBusy||!bulkPreview||!bulkApproved)return;
    const selected=bulkPreview;
    setBulkBusy(true);setBulkApproved(false);setBulkPreview(null);
    setBulkError("");setBulkNotice("");
    try{
      const result=await managerBridge.runningAsaBatchRegister(selected.token);
      if(result.imported!==selected.count||
         result.mapIds.length!==selected.count||!result.readOnly||
         result.lifecycleAuthorized||result.storeWriterAuthorized||
         result.serverProcessTouched||result.originalStoreTouched)
        throw new Error("UM-LIVE-BATCH-NATIVE-RECEIPT-MISMATCH");
      setBulkNotice(result.imported+" futó, nem-Store ASA-map"+
        " egyetlen Manager-profil mentéssel importálva. "+
        "Nincs RCON, Start/Stop vagy Store-írási jogosultság; "+
        "a vezérlés átvétele külön ellenőrzés és jóváhagyás.");
      void inspect();
      try{await onRegistered?.();}
      catch{
        setBulkError("A natív import sikeres, de a dashboard nem frissült. "+
          "Ne ismételd meg a batch műveletet, frissítsd a listát.");
      }
    }catch(e){
      setBulkError("A teljes batch import eredménye nem igazolt. "+
        "Nincs automatikus újrapróbálkozás: "+
        (e instanceof Error?e.message:"UM-LIVE-BATCH-APPLY-FAILED"));
    }finally{setBulkBusy(false);}
  };
  return <section className="glass panel" aria-label="Futó helyi ASA-folyamatok">
    <div className="panel-head">
      <h2><Activity size={18}/> Futó ASA-folyamatok · helyi felismerés</h2>
      <button type="button" className="secondary" disabled={loading}
        onClick={()=>{void inspect();}}>
        <RefreshCw size={15} className={loading?"spin":""}/>
        {loading?"Ellenőrzés...":"Új ellenőrzés"}
      </button>
    </div>
    <div className="panel-inner">
      <p className="muted small">A Windows összes ASA és ASA API Loader folyamatát
        automatikusan ellenőrzi 15 másodpercenként, a háttérben és tálcára csukva is.
        Az ismeretlen PID megfigyelés, nem tulajdonjog vagy Store-v1 írási engedély.</p>
      <div className="settings-actions">
        <button type="button" className="secondary"
          disabled={loading||bulkBusy||!snapshot||snapshot.fresh===false||!!bulkPreview}
          onClick={()=>{void prepareAll();}}>
          <Shield size={16}/> Összes azonosított nem-Store ASA importálása
        </button>
      </div>
      {bulkPreview&&<div role="region" className="historical-preview"
        aria-label="Futó nem-Store ASA batch import jóváhagyása">
        <strong>{bulkPreview.count} map egyetlen helyi Manager-profil mentéssel</strong>
        <p>{bulkPreview.storeOrCluster} Store/cluster folyamat külön eredeti Store-v1 átvételt
          igényel; {bulkPreview.otherSkipped} hiányos vagy már regisztrált folyamat kihagyva.</p>
        {bulkPreview.maps.map(p=><p key={p.pid}>
          {p.mapName} · PID {p.pid} · Game {p.gamePort} / Query {p.queryPort}
        </p>)}
        <label><input type="checkbox" checked={bulkApproved}
          disabled={bulkBusy} onChange={e=>setBulkApproved(e.target.checked)}/>
          Jóváhagyom a felsorolt, pontosan azonosított futó nem-Store mapok
          együttes regisztrációját. A szerverek nem állnak le és nem indulnak újra.
        </label>
        <button type="button" className="primary" disabled={!bulkApproved||bulkBusy}
          onClick={()=>{void applyAll();}}>Futó ASA mapok importálása</button>
        <button type="button" className="secondary" disabled={bulkBusy}
          onClick={()=>{setBulkPreview(null);setBulkApproved(false);}}>Mégsem</button>
      </div>}
      {bulkNotice&&<p className="success-inline" role="status">{bulkNotice}</p>}
      {bulkError&&<p className="error-inline" role="alert">{bulkError}</p>}
      {snapshot?.automaticDiscovery&&<p className="muted small">
        Folyamatfigyelő: {snapshot.watcherState??"ismeretlen"} ·
        {snapshot.fresh?" friss Windows-folyamatkép":" újraellenőrzés szükséges"}
      </p>}
      {snapshot?.fresh===false&&<div role="alert" className="warning-inline">
        <AlertTriangle size={16}/> A folyamatlista pillanatnyilag nem igazolt.
        Ez nem jelenti azt, hogy az ASA-szerverek leálltak.
      </div>}
      {error && <div className="error-inline" aria-live="polite"><AlertTriangle size={16}/>
        A helyi folyamatfelderítés nem elérhető ({error}); az állapot ismeretlen.</div>}
      {!error && loading && !snapshot && <p className="muted small">Helyi folyamatok ellenőrzése…</p>}
      {!loading && snapshot?.fresh!==false && snapshot?.processes.length===0 && <p className="muted small">
        A lekérdezés nem talált ASA-szervert vagy ASA API Loadert ezen a Windows-gépen.
      </p>}
      {snapshot?.processes.map(p=><div key={p.pid} className="import-preview">
        <Activity size={18}/>
        <div>
          <b>{p.processKind==="apiLoader"?"ASA API Loader":"ARK Ascended Server"} · PID {p.pid}</b>
          <p>{p.mapKey==="unknown"?"Ismeretlen map":p.mapKey} · {
            p.observedReadOnlyImported?"Managerhez regisztrálva · kizárólag megfigyelés":
            p.matchState==="profileCandidate"?"Egyező helyi profiljelölt":
            p.matchState==="unregistered"?"Nincs egyező helyi profil":
            p.matchState==="ambiguous"?"Több lehetséges helyi profil":
            p.loaderParentObserved&&p.mapKey!=="unknown"?
              "ASA API Loader alapján azonosított mapjelölt · gyermekfolyamat nincs hitelesítve":
            "Nem igazolható folyamatidentitás"
          }</p>
          {p.profileMapName && <p>Manager-profil: {p.profileMapName}</p>}
          {p.launchFacts?.argumentsValidated && <>
            <p>Game: {p.launchFacts.gamePort??"nem igazolt"} · Query: {p.launchFacts.queryPort??"nem igazolt"} · RCON: csak külön INI-ellenőrzéssel</p>
            <p>Aktív modok: {p.launchFacts.activeModsState==="parsed"?
              p.launchFacts.activeModIds.join(", "):
              p.launchFacts.activeModsState==="notDeclared"?"nem szerepel a parancssorban":"nem igazolható"}
            </p>
            <p>Passzív modok: {p.launchFacts.passiveModsState==="parsed"?
              p.launchFacts.passiveModIds.join(", "):
              p.launchFacts.passiveModsState==="notDeclared"?"nem szerepel a parancssorban":"nem igazolható"}
            </p>
            <p>Store-v1: {p.launchFacts.storeFlagPresent?"kapcsoló észlelve":"nem igazolt"} ·
              Cluster-ID kapcsoló: {p.launchFacts.clusterSwitchPresent?"észlelve":"nem igazolt"} ·
              Transfer-útvonal kapcsoló: {p.launchFacts.transferOverridePresent?"észlelve":"nem igazolt"}</p>
            {p.launchFacts.conversionFlagPresent && <p className="muted">
              Konverziós kapcsoló észlelve – csak megfigyelés, újraindítás és átvétel tiltva.</p>}
          </>}
          {p.loaderParentObserved && <p>ASA API Loader szülőfolyamat észlelve (csak megfigyelés).</p>}
          {p.processKind==="asaServer" && p.matchState==="profileCandidate" &&
            p.observedReadOnlyImported && p.profileMapId && !p.loaderParentObserved &&
            !p.launchFacts?.storeFlagPresent && !p.launchFacts?.clusterSwitchPresent &&
            !p.launchFacts?.transferOverridePresent && !p.launchFacts?.conversionFlagPresent &&
            <LiveAdoptControls process={p} afterAdopt={()=>{
              void inspect();void onRegistered?.();
            }}/>}
          {p.processKind==="asaServer" && p.matchState==="unregistered" &&
            p.launchFacts?.argumentsValidated && !p.launchFacts.storeFlagPresent &&
            !p.launchFacts.clusterSwitchPresent && !p.launchFacts.transferOverridePresent &&
            !p.launchFacts.conversionFlagPresent && p.startedUtc &&
            <LiveImportControls process={p} afterImport={()=>{
              void inspect();void onRegistered?.();
            }}/>}
          {p.processKind==="asaServer" && p.launchFacts?.argumentsValidated &&
            (p.launchFacts.storeFlagPresent||p.launchFacts.clusterSwitchPresent||
             p.launchFacts.transferOverridePresent||p.launchFacts.conversionFlagPresent) &&
            <p className="muted small">Store/cluster folyamat: az eredeti eredeti forrásból
              ellenőrzött történeti regisztráció szükséges. Ez az importútvonal tiltott.</p>}
        </div>
      </div>)}
      <p className="muted small"><Shield size={14}/> Az import első lépése csak megfigyelést ad. A nem-Store profilok natív
        vezérlésátvétele külön jóváhagyást és friss Windows-folyamatellenőrzést igényel;
        a történeti Store-v1 adatok továbbra is védettek.</p>
    </div>
  </section>;
}
