import {useCallback,useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,RefreshCw,Shield,Terminal,Users,Save} from "lucide-react";
import {managerBridge,type RconStatus,type RconPlayers,type RconReadLog,type RconWorldSave,type RconSaveStatus,type RconAdminPreview,type RconAdminResult,type RconConfigStatus,type RconConfigPreview} from "./bridge";

export function RconReadOnlyPanel({mapId,onConfigured,onOpenStart}:{mapId:string;onConfigured?:()=>Promise<void>;onOpenStart?:()=>void}){
  const [status,setStatus]=useState<RconStatus|null>(null);
  const [result,setResult]=useState<RconPlayers|null>(null);
  const [readLog,setReadLog]=useState<RconReadLog|null>(null);
  const [saveReceipt,setSaveReceipt]=useState<RconWorldSave|null>(null);
  const [saveEvidence,setSaveEvidence]=useState<RconSaveStatus|null>(null);
  const [checkingSave,setCheckingSave]=useState(false);
  const [saveApproved,setSaveApproved]=useState(false);
  const [adminAction,setAdminAction]=useState("broadcast");
  const [adminMessage,setAdminMessage]=useState("");
  const [adminDraft,setAdminDraft]=useState<RconAdminPreview|null>(null);
  const [adminApproved,setAdminApproved]=useState(false);
  const [adminTyped,setAdminTyped]=useState("");
  const [adminReceipt,setAdminReceipt]=useState<RconAdminResult|null>(null);
  const [config,setConfig]=useState<RconConfigStatus|null>(null);
  const [rconPort,setRconPort]=useState("37015");
  const [rconPassword,setRconPassword]=useState("");
  const [createNewIni,setCreateNewIni]=useState(true);
  const [configPreview,setConfigPreview]=useState<RconConfigPreview|null>(null);
  const [configDone,setConfigDone]=useState(false);
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const requestEpoch=useRef(0);
  const belongs=(requested:string,epoch:number)=>
    currentMap.current===requested&&requestEpoch.current===epoch;
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const load=useCallback(async()=>{
    if(!mapId)return;
    const requested=mapId,epoch=requestEpoch.current;
    setBusy(true);setError("");setStatus(null);setResult(null);setReadLog(null);
    setSaveReceipt(null);setSaveEvidence(null);setSaveApproved(false);
    setAdminDraft(null);setAdminApproved(false);setAdminTyped("");setAdminReceipt(null);
    try{
      // The optional configuration endpoint must never block existing RCON
      // commands or the admin panel when old/native test bridges omit it.
      const [rcon,configuration]=await Promise.allSettled([
        managerBridge.rconStatus(requested),managerBridge.rconConfigStatus(requested)
      ]);
      if(!belongs(requested,epoch))return;
      if(rcon.status!=="fulfilled"||
         rcon.value.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-RCON-STATUS-UNAVAILABLE");
      setStatus(rcon.value);
      const nextConfig=configuration.status==="fulfilled"&&
        configuration.value.mapId.toLowerCase()===requested.toLowerCase()
          ?configuration.value:null;
      setConfig(nextConfig);
      // Port selection is read from the opened map's native configuration.
      // The map-bound component key discards unsaved drafts on navigation.
      if(nextConfig?.rconPort && nextConfig.rconPort>=1024&&nextConfig.rconPort<=65535)
        setRconPort(String(nextConfig.rconPort));
    }
    catch{if(belongs(requested,epoch))
      setError("A helyi RCON-állapot nem ellenőrizhető. Egyetlen parancs sem került elküldésre.");}
    finally{if(belongs(requested,epoch))setBusy(false);}
  },[mapId]);
  useEffect(()=>{
    ++requestEpoch.current;
    setStatus(null);setResult(null);setReadLog(null);
    setSaveReceipt(null);setSaveEvidence(null);setAdminDraft(null);
    setAdminReceipt(null);setConfigPreview(null);setConfig(null);
    setSaveApproved(false);setAdminApproved(false);setAdminTyped("");
    setConfigDone(false);setRconPassword("");setBusy(false);setError("");
    void load();
    return ()=>{++requestEpoch.current;};
  },[load]);
  const previewConfiguration=async()=>{
    if(busy||!config?.permitted||
       rconPassword.length<(createNewIni?12:1))return;
    const requested=mapId,epoch=requestEpoch.current;
    setBusy(true);setError("");setConfigPreview(null);setConfigDone(false);
    try{
      const result=await managerBridge.rconConfigPreview(
        requested,Number(rconPort),rconPassword,createNewIni);
      if(!belongs(requested,epoch))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-RCON-CONFIG-PREVIEW-MAP-MISMATCH");
      setConfigPreview(result);
    }catch{
      if(belongs(requested,epoch))setError("Az RCON-konfiguráció előellenőrzése sikertelen. Az INI, a port vagy a mapazonosító nem egyezik.");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const commitConfiguration=async()=>{
    if(!configPreview||configPreview.mapId.toLowerCase()!==mapId.toLowerCase()||busy)return;
    const token=configPreview.token,requested=mapId,epoch=requestEpoch.current;
    setConfigPreview(null);setRconPassword("");
    setBusy(true);setError("");
    try{
      const receipt=await managerBridge.rconConfigApply(token);
      if(!belongs(requested,epoch))return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         !receipt.configured||receipt.serverStarted)
        throw new Error("UM-RCON-CONFIG-RECEIPT-MISMATCH");
      setConfigDone(true);
      try {
        const [nextStatus,nextConfig]=await Promise.all([
          managerBridge.rconStatus(requested),managerBridge.rconConfigStatus(requested)
        ]);
        if(!belongs(requested,epoch))return;
        if(nextStatus.mapId.toLowerCase()!==requested.toLowerCase()||
           nextConfig.mapId.toLowerCase()!==requested.toLowerCase())
          throw new Error("UM-RCON-CONFIG-STATUS-MAP-MISMATCH");
        setStatus(nextStatus);setConfig(nextConfig);
        if(onConfigured)await onConfigured();
      } catch {
        if(belongs(requested,epoch))
          setError("A natív RCON-konfiguráció mentve, de az állapotfrissítés nem sikerült. Ne ismételd meg az INI-előkészítést; frissítsd a Manager állapotát.");
      }
    }catch{
      if(belongs(requested,epoch))
        setError("Az RCON-beállítás eredménye nem igazolható. Ellenőrizd a helyi profilt, mielőtt megismételnéd.");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const list=async()=>{
    if(!status?.permitted||busy)return;
    const requested=mapId,epoch=requestEpoch.current;
    setBusy(true);setError("");setResult(null);
    try{
      const reply=await managerBridge.rconListPlayers(requested);
      if(!belongs(requested,epoch))return;
      if(reply.mapId.toLowerCase()!==requested.toLowerCase()||!reply.readOnly)
        throw new Error("UM-RCON-PLAYERS-MAP-MISMATCH");
      setResult(reply);
    }catch{if(belongs(requested,epoch))
      setError("A helyi ASA játékoslista válasza vagy mapazonosítója nem igazolható.");}
    finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const queryReadLog=async(type:"game"|"chat")=>{
    if(!status?.permitted||busy)return;
    const requested=mapId,epoch=requestEpoch.current;
    setBusy(true);setError("");setReadLog(null);setResult(null);
    try{
      const response=type==="game"
        ?await managerBridge.rconGameLog(requested)
        :await managerBridge.rconChatLog(requested);
      if(!belongs(requested,epoch))return;
      if(response.mapId.toLowerCase()!==requested.toLowerCase()||
         response.command!==(type==="game"?"GetGameLog":"GetChat")||!response.readOnly)
        throw new Error("UM-RCON-LOG-MAP-MISMATCH");
      setReadLog(response);
    }catch{
      if(belongs(requested,epoch))
        setError("Az ASA napló/chat RCON-válasz mapazonossága nem igazolható.");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const saveWorld=async()=>{
    if(!status?.permitted||!saveApproved||busy)return;
    const requested=mapId,epoch=requestEpoch.current;
    setBusy(true);setError("");setSaveReceipt(null);setSaveEvidence(null);setSaveApproved(false);
    try{
      const receipt=await managerBridge.rconSaveWorld(requested);
      if(!belongs(requested,epoch))return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         !receipt.rconCommandDispatched)
        throw new Error("UM-RCON-SAVE-RECEIPT-MAP-MISMATCH");
      setSaveReceipt(receipt);
    }catch{
      if(belongs(requested,epoch))
        setError("A SaveWorld kiküldésének eredménye nem igazolható. A parancs már célba érhetett; ne ismételd meg ellenőrzés nélkül.");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const verifySave=async()=>{
    if(!saveReceipt?.watchId||checkingSave||busy)return;
    if(saveReceipt.mapId.toLowerCase()!==mapId.toLowerCase())return;
    const requested=mapId,epoch=requestEpoch.current;
    const watch=saveReceipt.watchId;
    setCheckingSave(true);setError("");
    try{
      const proof=await managerBridge.rconSaveStatus(requested,watch);
      if(!belongs(requested,epoch))return;
      if(proof.mapId.toLowerCase()!==requested.toLowerCase()||
         proof.watchId!==watch)
        throw new Error("UM-RCON-SAVE-STATUS-MAP-MISMATCH");
      setSaveEvidence(proof);
    }catch{
      if(belongs(requested,epoch))
        setError("A mentésnapló vagy világfájl nem igazolható. Új SaveWorld nem indult.");
    }finally{if(belongs(requested,epoch))setCheckingSave(false);}
  };
  const prepareAdmin=async()=>{
    if(!status?.permitted||busy||(!adminMessage.trim()&&adminAction!=="wildWipe"))return;
    const requested=mapId,epoch=requestEpoch.current;
    setBusy(true);setError("");setAdminDraft(null);setAdminReceipt(null);
    setAdminApproved(false);setAdminTyped("");
    try{
      const result=await managerBridge.rconAdminPreview(requested,adminAction,
        adminAction==="wildWipe"?"":adminMessage);
      if(!belongs(requested,epoch))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         result.action!==adminAction||result.dispatched)
        throw new Error("UM-RCON-ADMIN-PREVIEW-MAP-MISMATCH");
      setAdminDraft(result);
    }catch{
      if(belongs(requested,epoch))
        setError("A helyi adminparancs előellenőrzése vagy mapazonossága nem igazolható.");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const executeAdmin=async()=>{
    if(!adminDraft||adminDraft.mapId.toLowerCase()!==mapId.toLowerCase()||
       !adminApproved||busy||
       (adminDraft.approvalWord!==null&&adminTyped!==adminDraft.approvalWord))return;
    const token=adminDraft.token,requested=mapId,epoch=requestEpoch.current,
      approvedCommand=adminDraft.command,typed=adminTyped;
    setAdminDraft(null);setAdminApproved(false);setAdminTyped("");
    setBusy(true);setError("");setAdminReceipt(null);
    try{
      const receipt=await managerBridge.rconAdminSend(token,typed);
      if(!belongs(requested,epoch))return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         receipt.command!==approvedCommand||!receipt.oneShot||
         !receipt.commandSent||receipt.storeWriterTouched)
        throw new Error("UM-RCON-ADMIN-RECEIPT-MISMATCH");
      setAdminReceipt(receipt);
    }catch{
      if(belongs(requested,epoch))
        setError("Az adminparancs eredménye nem igazolható. A TCP-küldés megtörténhetett; ismétlés előtt ellenőrizd a mapot. Automatikus újraküldés nincs.");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  return <div className="uk-rcon-page">
    <div className="page-intro"><span className="eyebrow">UNTAMED ASA / LOCAL RCON</span>
      <h1>RCON konzol</h1>
      <p>Valódi ListPlayers, SaveWorld, Broadcast, szerver-chat, vad dinó törlés
        és külön jóváhagyott egyedi adminparancsok, kizárólag a helyi, igazolt
        ASA-folyamathoz kötve. A leállítás külön életciklus-funkció; távoli
        peer-vezérlés és Store-v1 átírás nincs.</p></div>
    {!config?.configured&&<section className="premium-panel" aria-label="RCON első beállítása" style={{marginBottom:18,padding:20}}>
      <header className="premium-panel-heading"><h2><LockKeyhole size={18}/> RCON beállítása a helyi mapon</h2></header>
      <p>Csak leállított, nem Store-v1 mapon. A jelszó kizárólag a natív Windows részhez kerül; a Manager DPAPI-val titkosítja.</p>
      <div className="uk-rcon-admin-grid">
        <label>RCON TCP-port
          <input aria-label="RCON beállítás port" type="number" min={1024} max={65535} value={rconPort}
            disabled={!config?.permitted||busy} onChange={e=>{setRconPort(e.target.value);setConfigPreview(null);}}/>
        </label>
        <label>{createNewIni?"Új adminjelszó (12–128 karakter)":
          "Meglévő ASA ServerAdminPassword (1–256 karakter, pontos egyezés)"}
          <input aria-label="RCON beállítás jelszó" type="password" autoComplete="new-password"
            minLength={createNewIni?12:1} maxLength={createNewIni?128:256} value={rconPassword} disabled={!config?.permitted||busy}
            onChange={e=>{setRconPassword(e.target.value);setConfigPreview(null);}}/>
        </label>
      </div>
      <label><input type="checkbox" checked={createNewIni} disabled={!config?.permitted||busy}
        onChange={e=>{setCreateNewIni(e.target.checked);setConfigPreview(null);}}/>
        Új, még nem létező GameUserSettings.ini létrehozása. Ha már van működő INI, vedd ki a jelölést: a Manager kizárólag ellenőrzi, nem írja át.
      </label>
      <div className="uk-rcon-admin-actions">
        <button className="secondary" type="button" disabled={!config?.permitted||busy||
              rconPassword.length<(createNewIni?12:1)}
          onClick={()=>{void previewConfiguration();}}>Beállítás ellenőrzése</button>
      </div>
      {configPreview&&<div className="uk-rcon-admin-review" role="region" aria-label="RCON beállítás jóváhagyás">
        <b>{configPreview.mapName} · TCP {configPreview.rconPort}</b>
        <p>{configPreview.createsNewIni?"Új helyi ASA INI készül; a szerver nem indul el.":"A meglévő INI változatlan marad; csak a Manager tárolja el a védett kapcsolatot."}</p>
        <button className="primary" type="button" disabled={busy} onClick={()=>{void commitConfiguration();}}>Beállítás véglegesítése</button>
        <button className="secondary" type="button" disabled={busy} onClick={()=>setConfigPreview(null)}>Mégsem</button>
      </div>}
      {!config?.permitted&&<p className="muted">A jelenlegi maphoz a konfigurálás nincs engedélyezve. A történeti Store-v1 mapok írásvédelme megmarad.</p>}
    </section>}
    {configDone&&<div role="status" className="uk-rcon-admin-success">
        RCON-konfiguráció mentve, a helyi Manager állapota frissült. A szerver még nem indult el.
        {onOpenStart&&<button type="button" className="secondary" onClick={onOpenStart}>Vissza az ASA Start előnézethez</button>}
      </div>}
    <div className="uk-rcon-grid">
      <section className="premium-panel uk-rcon-console">
        <header className="premium-panel-heading">
          <h2><Terminal size={19}/> Helyi RCON konzol</h2>
          <span className="premium-small-muted">RCON / ADMIN / SAVEWORLD</span>
        </header>
        <div className="uk-rcon-commands">
          <button type="button" className="secondary" disabled={busy} onClick={()=>{void load();}}>
            <RefreshCw size={16}/> Állapot frissítése</button>
          <button type="button" className="primary"
            disabled={!status?.permitted||busy} onClick={()=>{void list();}}>
            <Users size={16}/> Játékoslista lekérdezése</button>
          <button type="button" className="secondary"
            disabled={!status?.permitted||busy} onClick={()=>{void queryReadLog("game");}}>
            <Terminal size={16}/> ASA játéknapló (GetGameLog)</button>
          <button type="button" className="secondary"
            disabled={!status?.permitted||busy} onClick={()=>{void queryReadLog("chat");}}>
            <Terminal size={16}/> ASA chat (GetChat)</button>
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
                  setAdminAction(e.target.value);setAdminMessage("");
                  setAdminDraft(null);setAdminReceipt(null);
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
            <CheckCircle2 size={17}/><div><b>Adminparancs egyszer elküldve – a végrehajtás még nem igazolt</b>
              <small>{adminReceipt.action} · {new Date(adminReceipt.observedUtc).toLocaleString("hu-HU")}</small>
              {adminReceipt.response&&<pre>{adminReceipt.response}</pre>}
            </div>
          </div>}
        </section>
        <div className="uk-rcon-terminal" role="log" aria-label="RCON ListPlayers válasz">
          <div className="uk-rcon-terminal-bar"><span aria-hidden="true">●</span>
            UNTAMED / VERIFIED LOCAL RCON
          </div>
          {readLog?<pre>{readLog.response||"Nincs új naplóbejegyzés az ASA RCON válaszában."}</pre>:
           result?<pre>{result.response||"A helyi RCON nem adott megjeleníthető választ."}</pre>:
            <div className="uk-rcon-placeholder">
              <Terminal size={29}/>
              {status?.permitted?
                "A helyi RCON-azonosság ellenőrizve. Indítsd a kizárólag olvasási lekérdezést.":
                "Nem küldhető RCON parancs. A hitelesített helyi folyamat és a TCP-port tulajdonosa még nem igazolt."}
            </div>}
        </div>
        {saveReceipt&&<div className="uk-rcon-save-receipt" role="status">
          <CheckCircle2 size={18}/><div><b>SaveWorld RCON-küldés megtörtént; végrehajtás nincs igazolva.</b>
            <p>{new Date(saveReceipt.observedUtc).toLocaleString("hu-HU")} ·
              A lemezre írás befejezését ez önmagában nem bizonyítja.
              {saveReceipt.nativeWitnessAvailable?
                " A szervernapló és a világfájl külön ellenőrizhető.":
                " A megadott maphoz nem található hiteles naplóhatár; a mentés nem igazolt."}
              A teljes ellenőrzött fájlmentéshez használd a Mentésközpontot
              a map szabályos leállítása után.</p>
              {saveReceipt.watchId&&<button className="secondary" type="button"
                disabled={checkingSave||busy} onClick={()=>{void verifySave();}}>
                <RefreshCw size={15}/>{checkingSave?"Lemez és napló ellenőrzése...":
                  "SaveWorld napló- és világfájl-állapot ellenőrzése"}
              </button>}
              {saveEvidence&&<div className="uk-rcon-save-evidence" role="status">
                <p>ASA mentésindítás: <b>{saveEvidence.logSavingStarted?"Megfigyelve":"Nem igazolt"}</b></p>
                <p>Új World Save Complete: <b>{saveEvidence.logSaveCompleteObserved?"Megfigyelve":"Nem igazolt"}</b></p>
                <p>.ark fájl módosult: <b>{saveEvidence.worldFileUpdated?"Igen":"Nem igazolt"}</b></p>
                <p>.ark fájl stabil: <b>{saveEvidence.worldFileStable?"Igen":"Nem igazolt"}</b></p>
                <p><b>{saveEvidence.combinedSaveEvidenceObserved?
                  "Napló és lemezmetaadat egyezik – valódi visszatöltés még nem tesztelt":
                  "A mentés befejezése továbbra sem igazolt"}</b></p>
              </div>}</div>
        </div>}
        {readLog&&<div className="uk-rcon-receipt" role="status"><CheckCircle2 size={17}/>
          {readLog.command} – olvasási válasz · {new Date(readLog.observedUtc).toLocaleString("hu-HU")}
          · a helyi folyamat és a TCP-port ellenőrizve
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
            A DoExit és a szerverfolyamat indítása/leállítása a külön életciklus-felületen
            engedélyezett. Az egyedi adminparancs egyszeri küldést és írásos jóváhagyást igényel;
            Store-v1 írását a RCON konzol nem végzi.</div>
        </div>
      </aside>
    </div>
  </div>;
}
