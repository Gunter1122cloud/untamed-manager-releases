import { useCallback, useEffect, useState } from "react";
import { Activity, AlertTriangle, RefreshCw, Shield } from "lucide-react";
import { managerBridge, type RunningLocalAsaSnapshot } from "./bridge";

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
        </div>
      </div>)}
      <p className="muted small"><Shield size={14}/> Nincs automatikus regisztráció, RCON,
        leállítás, újraindítás vagy közös Store-módosítás. Azonosítás ≠ lifecycle-jogosultság.</p>
    </div>
  </section>;
}
