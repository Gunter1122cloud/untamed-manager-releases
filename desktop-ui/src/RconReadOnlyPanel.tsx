import {useCallback,useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,RefreshCw,Shield,Terminal,Users} from "lucide-react";
import {managerBridge,type RconStatus,type RconPlayers} from "./bridge";

export function RconReadOnlyPanel({mapId}:{mapId:string}){
  const [status,setStatus]=useState<RconStatus|null>(null);
  const [result,setResult]=useState<RconPlayers|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const load=useCallback(async()=>{
    if(!mapId)return;
    setBusy(true);setError("");setStatus(null);setResult(null);
    try{setStatus(await managerBridge.rconStatus(mapId));}
    catch{setError("A helyi RCON-állapot nem ellenőrizhető. Egyetlen parancs sem került elküldésre.");}
    finally{setBusy(false);}
  },[mapId]);
  useEffect(()=>{void load();},[load]);
  const list=async()=>{
    if(!status?.permitted||busy)return;
    setBusy(true);setError("");setResult(null);
    try{setResult(await managerBridge.rconListPlayers(mapId));}
    catch{setError("A biztonságos ListPlayers lekérdezés nem igazolható. A kapcsolódás vagy a folyamat állapota megváltozhatott.");}
    finally{setBusy(false);}
  };
  return <div className="uk-rcon-page">
    <div className="page-intro"><span className="eyebrow">UNTAMED ASA / LOCAL RCON</span>
      <h1>RCON konzol</h1>
      <p>Csak a kiválasztott helyi ASA-map igazolt saját folyamatából
        kérdezhető le a játékoslista. Nincs tetszőleges parancsbeküldés,
        karaktermentés, adminírás vagy távoli peer-vezérlés.</p></div>
    <div className="uk-rcon-grid">
      <section className="premium-panel uk-rcon-console">
        <header className="premium-panel-heading">
          <h2><Terminal size={19}/> Helyi konzol · csak olvasás</h2>
          <span className="premium-small-muted">RCON / LISTPLAYERS</span>
        </header>
        <div className="uk-rcon-commands">
          <button type="button" className="secondary" disabled={busy} onClick={()=>{void load();}}>
            <RefreshCw size={16}/> Állapot frissítése</button>
          <button type="button" className="primary"
            disabled={!status?.permitted||busy} onClick={()=>{void list();}}>
            <Users size={16}/> Játékoslista lekérdezése</button>
        </div>
        <div className="uk-rcon-terminal" role="log" aria-label="RCON ListPlayers válasz">
          <div className="uk-rcon-terminal-bar"><span aria-hidden="true">●</span>
            UNTAMED / VERIFIED READ-ONLY
          </div>
          {result?<pre>{result.response||"A helyi RCON nem adott megjeleníthető választ."}</pre>:
            <div className="uk-rcon-placeholder">
              <Terminal size={29}/>
              {status?.permitted?
                "A helyi RCON-azonosság ellenőrizve. Indítsd a kizárólag olvasási lekérdezést.":
                "Nem küldhető RCON parancs. A hitelesített helyi folyamat és a TCP-port tulajdonosa még nem igazolt."}
            </div>}
        </div>
        {result&&<div className="uk-rcon-receipt"><CheckCircle2 size={17}/>
          Olvasási válasz · {new Date(result.observedUtc).toLocaleString("hu-HU")}
          · SaveComplete nem igazolt</div>}
        {error&&<div role="alert" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      </section>
      <aside className="premium-panel uk-rcon-security">
        <header className="premium-panel-heading"><h2><Shield size={19}/> Hozzáférésvédelem</h2></header>
        <div className="uk-rcon-security-inner">
          <div><span>RCON konfiguráció</span><b>{status?.configured?"Beállítva":"Nem igazolt"}</b></div>
          <div><span>Helyi ASA-folyamat</span><b>{status?.processVerified?"Azonosítva":"Nem igazolt"}</b></div>
          <div><span>ListPlayers jogosultság</span><b>{status?.permitted?"Engedélyezett":"Tiltva"}</b></div>
          <div><span>Karbantartási mód</span><b>{status?.maintenance?"Aktív":"Nincs igazolt karbantartás"}</b></div>
          <div className="uk-rcon-locked"><LockKeyhole size={17}/>
            Az adminparancsok, SaveWorld, DoExit és a teljes szervervezérlés
            nem ezen a csak olvasó RCON-kapcsolaton futnak.</div>
        </div>
      </aside>
    </div>
  </div>;
}
