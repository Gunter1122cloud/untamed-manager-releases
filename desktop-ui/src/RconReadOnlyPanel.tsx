import {useCallback,useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,RefreshCw,Shield,Terminal,Users,Save} from "lucide-react";
import {managerBridge,type RconStatus,type RconPlayers,type RconWorldSave} from "./bridge";

export function RconReadOnlyPanel({mapId}:{mapId:string}){
  const [status,setStatus]=useState<RconStatus|null>(null);
  const [result,setResult]=useState<RconPlayers|null>(null);
  const [saveReceipt,setSaveReceipt]=useState<RconWorldSave|null>(null);
  const [saveApproved,setSaveApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const load=useCallback(async()=>{
    if(!mapId)return;
    setBusy(true);setError("");setStatus(null);setResult(null);
    setSaveReceipt(null);setSaveApproved(false);
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
  const saveWorld=async()=>{
    if(!status?.permitted||!saveApproved||busy)return;
    setBusy(true);setError("");setSaveReceipt(null);setSaveApproved(false);
    try{setSaveReceipt(await managerBridge.rconSaveWorld(mapId));}
    catch{setError("A SaveWorld RCON-parancs nem lett ellenőrzötten visszaigazolva. A folyamatot és a mentés fájljait ellenőrizd.");}
    finally{setBusy(false);}
  };
  return <div className="uk-rcon-page">
    <div className="page-intro"><span className="eyebrow">UNTAMED ASA / LOCAL RCON</span>
      <h1>RCON konzol</h1>
      <p>Az igazolt helyi ASA-folyamathoz kötött RCON-kapcsolat valódi
        ListPlayers és külön jóváhagyott SaveWorld műveletet kezel.
        Tetszőleges parancs, távoli peer-vezérlés és Store-v1 átírás nincs.</p></div>
    <div className="uk-rcon-grid">
      <section className="premium-panel uk-rcon-console">
        <header className="premium-panel-heading">
          <h2><Terminal size={19}/> Helyi RCON konzol</h2>
          <span className="premium-small-muted">RCON / LISTPLAYERS / SAVEWORLD</span>
        </header>
        <div className="uk-rcon-commands">
          <button type="button" className="secondary" disabled={busy} onClick={()=>{void load();}}>
            <RefreshCw size={16}/> Állapot frissítése</button>
          <button type="button" className="primary"
            disabled={!status?.permitted||busy} onClick={()=>{void list();}}>
            <Users size={16}/> Játékoslista lekérdezése</button>
        </div>
        <div className="uk-rcon-save-panel">
          <label><input type="checkbox" checked={saveApproved}
            onChange={e=>setSaveApproved(e.target.checked)}
            disabled={!status?.permitted||busy}/>
            Kifejezetten kérem a kiválasztott helyi map SaveWorld mentési parancsát.
          </label>
          <button type="button" className="primary" disabled={!status?.permitted||!saveApproved||busy}
            onClick={()=>{void saveWorld();}}><Save size={16}/> Valódi SaveWorld elküldése</button>
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
        {saveReceipt&&<div className="uk-rcon-save-receipt" role="status">
          <CheckCircle2 size={18}/><div><b>SaveWorld RCON-válasz érkezett.</b>
            <p>{new Date(saveReceipt.observedUtc).toLocaleString("hu-HU")} ·
              A lemezre írás befejezését ez önmagában nem bizonyítja.
              A teljes ellenőrzött fájlmentéshez használd a Mentésközpontot
              a map szabályos leállítása után.</p></div>
        </div>}
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
          <div><span>SaveWorld művelet</span><b>{status?.permitted?"Külön jóváhagyással":"Tiltva"}</b></div>
          <div><span>Karbantartási mód</span><b>{status?.maintenance?"Aktív":"Nincs igazolt karbantartás"}</b></div>
          <div className="uk-rcon-locked"><LockKeyhole size={17}/>
            A DoExit, Kill, tetszőleges adminparancs és a Store-v1 írása
            továbbra is külön, jóváhagyott és ellenőrzött műveletet igényel.</div>
        </div>
      </aside>
    </div>
  </div>;
}
