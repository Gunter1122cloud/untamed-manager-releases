import { useCallback, useEffect, useState } from "react";
import { Activity, AlertTriangle, RefreshCw, Shield } from "lucide-react";
import { managerBridge, type RunningLocalAsaSnapshot, type RunningLocalAsaProcess,
  type RunningAsaImportPreview } from "./bridge";


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

/** Live, on-demand WMI process observation. No process adoption or mutation. */
export function LocalRunningAsaPanel() {
  const [snapshot,setSnapshot]=useState<RunningLocalAsaSnapshot|null>(null);
  const [loading,setLoading]=useState(true);
  const [error,setError]=useState("");
  const inspect=useCallback(async (isCurrent:()=>boolean=()=>true)=>{
    if(isCurrent()){setLoading(true);setError("");}
    try {
      const next=await managerBridge.runningLocalAsa();
      if(isCurrent())setSnapshot(next);
    } catch(e) {
      if(isCurrent()){
        setSnapshot(null);
        setError(e instanceof Error?e.message:"UM-READ-UNAVAILABLE");
      }
    } finally {
      if(isCurrent())setLoading(false);
    }
  },[]);
  useEffect(()=>{
    let current=true;
    void inspect(()=>current);
    return ()=>{current=false;};
  },[inspect]);

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
      <p className="muted small">A Windows folyamatlistáját olvassa. A találat nem jelenti, hogy a
        Manager átvette a szerver vezérlését vagy a Store-v1 írási jogát.</p>
      {error && <div className="error-inline" aria-live="polite"><AlertTriangle size={16}/>
        A helyi folyamatfelderítés nem elérhető ({error}); az állapot ismeretlen.</div>}
      {!error && loading && !snapshot && <p className="muted small">Helyi folyamatok ellenőrzése…</p>}
      {!loading && snapshot?.processes.length===0 && <p className="muted small">
        A lekérdezés nem talált ASA-szervert vagy ASA API Loadert ezen a Windows-gépen.
      </p>}
      {snapshot?.processes.map(p=><div key={p.pid} className="import-preview">
        <Activity size={18}/>
        <div>
          <b>{p.processKind==="apiLoader"?"ASA API Loader":"ARK Ascended Server"} · PID {p.pid}</b>
          <p>{p.mapKey==="unknown"?"Ismeretlen map":p.mapKey} · {
            p.matchState==="profileCandidate"?"Egyező helyi profiljelölt":
            p.matchState==="unregistered"?"Nincs egyező helyi profil":
            p.matchState==="ambiguous"?"Több lehetséges helyi profil":
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
          {p.processKind==="asaServer" && p.matchState==="unregistered" &&
            p.launchFacts?.argumentsValidated && !p.launchFacts.storeFlagPresent &&
            !p.launchFacts.clusterSwitchPresent && !p.launchFacts.transferOverridePresent &&
            !p.launchFacts.conversionFlagPresent && p.startedUtc &&
            <LiveImportControls process={p} afterImport={()=>{void inspect();}}/>}
          {p.processKind==="asaServer" && p.launchFacts?.argumentsValidated &&
            (p.launchFacts.storeFlagPresent||p.launchFacts.clusterSwitchPresent||
             p.launchFacts.transferOverridePresent||p.launchFacts.conversionFlagPresent) &&
            <p className="muted small">Store/cluster folyamat: az eredeti Kova-forrásból
              ellenőrzött történeti regisztráció szükséges. Ez az importútvonal tiltott.</p>}
        </div>
      </div>)}
      <p className="muted small"><Shield size={14}/> Csak explicit, kétlépcsős, read-only profilregisztráció érhető el. Nincs RCON,
        leállítás, újraindítás vagy közös Store-módosítás. Azonosítás ≠ lifecycle-jogosultság.</p>
    </div>
  </section>;
}
