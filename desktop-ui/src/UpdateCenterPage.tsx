import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,CloudDownload,GitBranch,HardDrive,Info,
  RefreshCw,ShieldCheck,ServerCog} from "lucide-react";
import {managerBridge,type UpdateStatus,type UpdateCheck} from "./bridge";

/** Real GitHub Releases metadata: read-only. The release signature/digest and
 * external updater must be verified before installation can be enabled. */
export function UpdateCenterPage(){
  const [current,setCurrent]=useState<UpdateStatus|null>(null);
  const [latest,setLatest]=useState<UpdateCheck|null>(null);
  const [channel,setChannel]=useState<"stable"|"preview">("stable");
  const [loading,setLoading]=useState(false);
  const [error,setError]=useState("");
  useEffect(()=>{
    let active=true;
    managerBridge.updateStatus().then(r=>{if(active)setCurrent(r);})
      .catch(()=>{if(active)setError("A helyi verzióazonosító nem olvasható.");});
    return ()=>{active=false;};
  },[]);
  const check=async()=>{
    if(loading)return;
    setLoading(true);setError("");setLatest(null);
    try{const result=await managerBridge.updateCheck(channel);setLatest(result);}
    catch{setError("A GitHub kiadványok ellenőrzése sikertelen. A telepített Manager változatlan maradt.");}
    finally{setLoading(false);}
  };
  return <div className="uk-updates-page">
    <div className="page-intro"><span className="eyebrow">UNTAMED KINGDOM / RELEASE CHANNEL</span>
      <h1>Frissítési központ</h1>
      <p>Hivatalos GitHub Releases verzióellenőrzés a jelenlegi Managerhez.
      A keresés nem állít le ASA-mapot, nem ír adatot és nem telepít automatikusan.</p>
    </div>
    <section className="uk-updates-hero">
      <span className="uk-updates-symbol"><CloudDownload size={38}/></span>
      <div><span className="uk-updates-kicker">UNTAMED COMMAND • UPDATE CHANNEL</span>
        <h2>Maradj naprakész. Őrizd meg a clustert.</h2>
        <p>Külön helyi Manager, külön MariaDB és karaktermentés. A kiadvány ellenőrzése nem jelent
          engedélyt a Store-v1 vagy az éles szerverek módosítására.</p>
      </div>
    </section>
    <div className="uk-update-grid">
      <section className="premium-panel uk-updates-version">
        <header className="premium-panel-heading"><h2><ServerCog size={19}/> Jelenlegi telepítés</h2></header>
        <div className="uk-updates-inner">
          <div className="uk-updates-info-row"><span><HardDrive size={16}/> Programverzió</span>
            <strong>{current?.runningVersion??"Beolvasás..."}</strong></div>
          <div className="uk-updates-info-row"><span><GitBranch size={16}/> Telepített kiadvány</span>
            <strong>{current?.installedTag||"Fejlesztői / nem címkézett build"}</strong></div>
          <div className="uk-updates-info-row"><span><ShieldCheck size={16}/> Forrás</span>
            <strong>Gunter1122cloud / GitHub Releases</strong></div>
          <div className="uk-updates-info-row"><span><Info size={16}/> Automatikus telepítés</span>
            <strong>Letiltva · külső updater még nincs igazolva</strong></div>
          <div className="uk-updates-checkbox"><CheckCircle2 size={17}/>
            <span>Ez a nézet csak a helyi programverziót és a kiadványok metadatait olvassa.</span></div>
        </div>
      </section>
      <section className="premium-panel uk-updates-discovery">
        <header className="premium-panel-heading"><h2><CloudDownload size={19}/> Új verzió ellenőrzése</h2></header>
        <div className="uk-updates-inner">
          <label className="uk-updates-channel">Kiadási csatorna
            <select aria-label="Frissítési csatorna" value={channel}
              disabled={loading} onChange={e=>{
                setChannel(e.target.value as "stable"|"preview");setLatest(null);setError("");
              }}>
              <option value="stable">Stabil kiadások</option>
              <option value="preview">Előzetes kiadások is</option>
            </select>
          </label>
          <button type="button" className="primary uk-updates-check" onClick={()=>{void check();}}
            disabled={loading}>
            <RefreshCw size={18} className={loading?"spin":""}/>
            {loading?"GitHub kiadványok ellenőrzése...":"Frissítések keresése"}
          </button>
          {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
          {latest&&<div className="uk-updates-release" role="status">
            <strong>{latest.found?
              latest.newer?"Új kiadás érhető el":"Kiadás ellenőrizve":
              "Nincs ellenőrizhető kiadás ebben a csatornában"}</strong>
            {latest.found&&<>
              <div><span>Verzió</span><b>{latest.tag}</b></div>
              <div><span>Telepítő</span><b>{latest.assetName}</b></div>
              <div className="uk-updates-hash"><span>SHA-256 (kiadási adat)</span>
                <code>{latest.sha256}</code></div>
              {latest.releaseNotes&&<p className="uk-updates-release-notes">
                {latest.releaseNotes}</p>}
            </>}
            <div className="uk-updates-install-lock"><ShieldCheck size={17}/>
              A telepítés nincs engedélyezve: a letöltött telepítő és a külső updater
              külön ellenőrzést igényel. Nem írunk a futó alkalmazás könyvtárába.
            </div>
          </div>}
          <small className="uk-updates-detail">
            A GitHub kiadványadatainak olvasása nem hitelesíti önmagában a letöltött fájlt.
            Az Untamed Store-v1, az ASA mentések és a MariaDB változatlanok.
          </small>
        </div>
      </section>
    </div>
  </div>;
}
