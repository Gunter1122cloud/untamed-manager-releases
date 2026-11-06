import {useCallback,useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,RefreshCw,Shield,Terminal,Users,Save} from "lucide-react";
import {managerBridge,type RconStatus,type RconPlayers,type RconWorldSave,type RconAdminPreview,type RconAdminResult} from "./bridge";

export function RconReadOnlyPanel({mapId}:{mapId:string}){
  const [status,setStatus]=useState<RconStatus|null>(null);
  const [result,setResult]=useState<RconPlayers|null>(null);
  const [saveReceipt,setSaveReceipt]=useState<RconWorldSave|null>(null);
  const [saveApproved,setSaveApproved]=useState(false);
  const [adminAction,setAdminAction]=useState("broadcast");
  const [adminMessage,setAdminMessage]=useState("");
  const [adminDraft,setAdminDraft]=useState<RconAdminPreview|null>(null);
  const [adminApproved,setAdminApproved]=useState(false);
  const [adminTyped,setAdminTyped]=useState("");
  const [adminReceipt,setAdminReceipt]=useState<RconAdminResult|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const load=useCallback(async()=>{
    if(!mapId)return;
    setBusy(true);setError("");setStatus(null);setResult(null);
    setSaveReceipt(null);setSaveApproved(false);
    setAdminDraft(null);setAdminApproved(false);setAdminTyped("");setAdminReceipt(null);
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
  const prepareAdmin=async()=>{
    if(!status?.permitted||busy||(!adminMessage.trim()&&adminAction!=="wildWipe"))return;
    setBusy(true);setError("");setAdminDraft(null);setAdminReceipt(null);
    setAdminApproved(false);setAdminTyped("");
    try{setAdminDraft(await managerBridge.rconAdminPreview(mapId,adminAction,adminMessage));}
    catch{setError("A helyi adminparancs ellenőrzése sikertelen: a művelet nincs engedélyezve vagy a RCON-kapcsolat nem igazolt.");}
    finally{setBusy(false);}
  };
  const executeAdmin=async()=>{
    if(!adminDraft||!adminApproved||busy||
       (adminDraft.approvalWord!==null&&adminTyped!==adminDraft.approvalWord))return;
    const token=adminDraft.token;
    setAdminDraft(null);setAdminApproved(false);setAdminTyped("");
    setBusy(true);setError("");setAdminReceipt(null);
    try{setAdminReceipt(await managerBridge.rconAdminSend(token,adminTyped));}
    catch{setError("A parancs nem kapott ellenőrzött választ. Ismétlés előtt ellenőrizd a szerver állapotát; automatikus újraküldés nincs.");}
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
        <section className="uk-rcon-admin-panel" aria-label="Helyi admin RCON parancsok">
          <div className="uk-rcon-admin-title"><Shield size={18}/><b>Admin RCON – éles parancsküldés</b>
            <span>Kétlépcsős jóváhagyás · nincs automatikus ismétlés</span></div>
          <div className="uk-rcon-admin-grid">
            <label>Parancstípus
              <select aria-label="Admin RCON parancstípus" disabled={busy||!status?.permitted}
                value={adminAction} onChange={e=>{
                  setAdminAction(e.target.value);setAdminDraft(null);setAdminReceipt(null);
                }}>
                <option value="broadcast">Közlemény minden játékosnak</option>
                <option value="serverChat">Szerver-chat üzenet</option>
                <option value="wildWipe">Vad dinók újragenerálása</option>
                <option value="custom">Egyedi adminparancs (haladó)</option>
              </select>
            </label>
            {adminAction!=="wildWipe"&&<label>{adminAction==="custom"?"Adminparancs":"Üzenet"}
              <input aria-label="Admin RCON üzenet vagy parancs" maxLength={240}
                value={adminMessage} disabled={busy||!status?.permitted}
                placeholder={adminAction==="custom"?"Pl. GetAllState ...":"Írd be az üzenetet..."}
                onChange={e=>{setAdminMessage(e.target.value);setAdminDraft(null);
                  setAdminReceipt(null);}}/>
            </label>}
          </div>
          <button type="button" className="secondary uk-rcon-admin-preview"
            disabled={!status?.permitted||busy||(!adminMessage.trim()&&adminAction!=="wildWipe")}
            onClick={()=>{void prepareAdmin();}}>
            <Shield size={16}/> Éles RCON-parancs ellenőrzése
          </button>
          {adminDraft&&<div className={adminDraft.dangerous?"uk-rcon-admin-danger":"uk-rcon-admin-review"}
            role="region" aria-label="Adminparancs végső jóváhagyása">
            <b>{adminDraft.dangerous?"Veszélyes adminművelet – végső jóváhagyás":
              "Ellenőrzött helyi művelet – végső jóváhagyás"}</b>
            <code>{adminDraft.command}</code>
            <p>{adminDraft.dangerous?
              "Ez a parancs tartós módosítást végezhet. Ellenőrizd a mapot és a paramétereket; visszavonás nincs.":
              "Az üzenet az aktív helyi szerverre kerül, a RCON hitelesítési adatokat nem küldjük a felületre."}</p>
            <label className="uk-rcon-approve-check">
              <input type="checkbox" checked={adminApproved}
                onChange={e=>setAdminApproved(e.target.checked)}/>
              Jóváhagyom a parancs egyszeri elküldését: {adminDraft.mapName}
            </label>
            {adminDraft.approvalWord!==null&&<label>Gépeld be a megerősítő szót: {adminDraft.approvalWord}
              <input aria-label="Adminparancs megerősítő szó" value={adminTyped}
                onChange={e=>setAdminTyped(e.target.value)} placeholder={adminDraft.approvalWord}/>
            </label>}
            <div className="uk-rcon-admin-actions">
              <button type="button" className={adminDraft.dangerous?"danger":"primary"}
                disabled={!adminApproved||busy||
                  (adminDraft.approvalWord!==null&&adminTyped!==adminDraft.approvalWord)}
                onClick={()=>{void executeAdmin();}}>Parancs elküldése</button>
              <button type="button" className="secondary" disabled={busy}
                onClick={()=>{setAdminDraft(null);setAdminApproved(false);}}>Mégsem</button>
            </div>
          </div>}
          {adminReceipt&&<div className="uk-rcon-admin-success" role="status">
            <CheckCircle2 size={17}/><div><b>Adminparancs egyszer elküldve</b>
              <small>{adminReceipt.action} · {new Date(adminReceipt.observedUtc).toLocaleString("hu-HU")}</small>
              {adminReceipt.response&&<pre>{adminReceipt.response}</pre>}
            </div>
          </div>}
        </section>
        <div className="uk-rcon-terminal" role="log" aria-label="RCON ListPlayers válasz">
          <div className="uk-rcon-terminal-bar"><span aria-hidden="true">●</span>
            UNTAMED / VERIFIED LOCAL RCON
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
