import {useEffect,useRef,useState,type FormEvent} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,Shield,RadioTower} from "lucide-react";
import {managerBridge,type HistoricalRconPreview,type HistoricalRconStatus} from "./bridge";

/**
 * Native-only original ASA RCON attachment. The original GUS.ini, shared
 * Store lock/session, world, characters and tribes are NEVER modified.
 * Secrets exist only in the password input until preview; native DPAPI
 * keeps the persisted copy out of the renderer.
 */
export function HistoricalRconAttach({mapId,onConfigured}:{
  mapId:string;onConfigured:()=>Promise<void>
}){
  const mapGeneration=useRef(0);
  const [status,setStatus]=useState<HistoricalRconStatus|null>(null);
  const [port,setPort]=useState("");
  const [password,setPassword]=useState("");
  const [preview,setPreview]=useState<HistoricalRconPreview|null>(null);
  const [confirm,setConfirm]=useState(false);
  const [busy,setBusy]=useState(false);
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    let active=true;
    ++mapGeneration.current;
    setStatus(null);setPort("");setPreview(null);setPassword("");setConfirm(false);setBusy(false);setError("");setNotice("");
    managerBridge.historicalRconStatus(mapId).then(v=>{
      if(active){setStatus(v);setPort(v.suggestedPort>0?String(v.suggestedPort):"");}
    }).catch(()=>{if(active)setError("Az eredeti Store-v1 RCON-beállítás állapota nem olvasható.");});
    return ()=>{active=false;++mapGeneration.current;};
  },[mapId]);
  const edit=()=>{setPreview(null);setConfirm(false);setNotice("");setError("");};
  const inspect=async(event:FormEvent)=>{
    event.preventDefault();
    const parsed=Number(port);
    if(!Number.isInteger(parsed)||parsed<1024||parsed>65535||!password){
      setError("Adj meg érvényes RCON-portot és az eredeti GameUserSettings.ini jelszavát.");
      return;
    }
    const generation=mapGeneration.current;
    setBusy(true);setError("");setNotice("");setPreview(null);setConfirm(false);
    try{
      const result=await managerBridge.historicalRconPreview(mapId,parsed,password);
      if(mapGeneration.current!==generation)return;
      setPassword("");setPreview(result);
    }catch(cause){
      if(mapGeneration.current!==generation)return;
      setPassword("");
      const code=cause instanceof Error?cause.message:"";
      setError(code==="UM-HISTORICAL-RCON-EXISTING-PASSWORD-INVALID"
        ?"A meglévő ASA-jelszó nem adható át egyetlen INI-sorként (üres, 256 karakternél hosszabb vagy vezérlőkaraktert tartalmaz). A régi, rövid vagy speciális karakteres jelszót nem kell megváltoztatni."
        :code==="UM-HISTORICAL-RCON-INI-CHANGED"
        ?"A megadott RCON-port vagy jelszó nem egyezik a meglévő ASA GameUserSettings.ini beállításával, vagy az RCON nincs engedélyezve. A jelszót nem módosítottuk."
        :code==="UM-HISTORICAL-RCON-ORIGINAL-STORE-INVALID"
        ?"A jóváhagyott helyi Store-v1 forrásazonosság vagy a még át nem vett korábbi Manager ACTIVE/DONE állapota nem egyezik. A szerverhez és a jelszóhoz nem nyúltunk."
        :code==="UM-HISTORICAL-RCON-API-CHILD-UNVERIFIED"
        ?"Az ASA API Loader mögötti eredeti szerverfolyamat vagy a TCP-port tulajdonosa nem igazolható. Nem történt RCON-csatolás."
        :"A natív RCON-ellenőrzés nem sikerült. Az eredeti ASA/korábbi Manager-fájlok érintetlenek; ellenőrizd a maphoz tartozó futó ASA-folyamatot és RCON-beállítást.");
    }finally{if(mapGeneration.current===generation)setBusy(false);}
  };
  const attach=async()=>{
    if(!preview||!confirm||busy)return;
    const generation=mapGeneration.current;
    setBusy(true);setError("");setNotice("");
    const token=preview.token;setPreview(null);setConfirm(false);
    let committed=false;
    try{
      const receipt=await managerBridge.historicalRconApply(token);
      committed=true;
      if(mapGeneration.current!==generation)return;
      // The native DPAPI profile is already committed. A failed subsequent
      // status/refresh MUST NOT disguise that commit as a failed attach and
      // tempt the operator to repeat a single-use mutation.
      setNotice("Az eredeti RCON-profil a helyi Managerbe elmentve (TCP "+
        receipt.rconPort+"). Az ASA és a Store-fájlok nem módosultak.");
      try{
        const current=await managerBridge.historicalRconStatus(mapId);
        if(mapGeneration.current!==generation)return;
        setStatus(current);
        await onConfigured();
      }catch{
        if(mapGeneration.current!==generation)return;
        setNotice("Az eredeti RCON-profil elmentve (TCP "+receipt.rconPort+
          "), de a felület frissítése sikertelen. Újraküldés helyett "+
          "frissítsd a maplistát. Az ASA és a Store-fájlok nem módosultak.");
      }
    }catch{
      if(mapGeneration.current===generation && !committed)setError("Az eredeti RCON-kapcsolat átvétele nem fejezhető be, vagy a profil közben megváltozott. Ellenőrizd az aktuális állapotot.");
    }finally{if(mapGeneration.current===generation)setBusy(false);}
  };
  return <section className="glass panel historical-rcon-attach"
      aria-label="Történeti Store-v1 RCON csatlakoztatás">
    <div className="panel-head"><h2><RadioTower size={18}/> Eredeti Store-v1 RCON átvétele</h2>
      <span className="mod-tag amber"><LockKeyhole size={13}/> Eredeti fájlok érintetlenek</span></div>
    <div className="panel-inner">
      <p className="muted small">Az eredeti Store-v1 map meglévő RCON-portját kapcsolja
        az új Managerhez, <b>új INI, új session, Store-konverzió és szerverindítás nélkül</b>.
        Az Obsidianba jóváhagyottan átvett forrás helyi használatához nem szükséges
        a másik gép régi korábbi Manager ACTIVE/DONE-jelölője. A jelszót a natív Windows DPAPI védi.</p>
      {status&&!status.configured&&status.suggestedPort>0&&
        <p className="muted small" role="status">
          Az eredeti ASA INI alapján felismert TCP RCON-port: {status.suggestedPort}.
          A jelszót biztonsági okból külön kell megadni; az ASA INI-hez nem nyúlunk.
        </p>}
      {status?.configured?<div className="success-inline" role="status">
        <CheckCircle2 size={17}/> RCON-port: {status.port} ·
        {status.verifiedIni?" Eredeti INI egyezik":" INI egyezése jelenleg nem igazolt"}
      </div>:<form className="settings-form" onSubmit={e=>{void inspect(e);}}>
        <label htmlFor="historical-rcon-port">Meglévő ASA RCON-port</label>
        <input id="historical-rcon-port" inputMode="numeric" type="number" min={1024}
          max={65535} value={port} onChange={e=>{setPort(e.target.value);edit();}}
          disabled={busy||!!preview} placeholder="37015"/>
        <label htmlFor="historical-rcon-pass">Eredeti ASA ServerAdminPassword</label>
        <input id="historical-rcon-pass" type="password" value={password}
          autoComplete="off" spellCheck={false} maxLength={256}
          onChange={e=>{setPassword(e.target.value);edit();}} disabled={busy||!!preview}
          placeholder="A meglévő GameUserSettings.ini jelszava"/>
        <button className="secondary" type="submit" disabled={busy||!!preview||
          !password||!port}><Shield size={16}/> Eredeti RCON előellenőrzése</button>
      </form>}
      {preview&&<div className="warning-inline" role="region"
          aria-label="Store-v1 RCON jóváhagyása">
        <Shield size={17}/>
        <div>
          <b>{preview.mapName} · TCP {preview.rconPort}</b>
          <p>{preview.approvedObsidianSourcePinned?
            "A jóváhagyott Obsidian-forrás és a meglévő ASA RCON INI ellenőrzött. A régi kétpeer-es korábbi Manager ACTIVE/DONE-jelölő nem szükséges ehhez a helyi csatoláshoz.":
            "A még át nem vett eredeti eredeti forrás és a kétpeer-es ACTIVE/DONE állapot, valamint az ASA RCON INI ellenőrzött."}
            {" "}Jóváhagyáskor kizárólag a saját Manager-profil titkosított beállítása változik.</p>
          <label><input type="checkbox" checked={confirm}
            onChange={e=>setConfirm(e.target.checked)}/>
            Jóváhagyom az eredeti RCON-kapcsolat helyi átvételét.</label>
          <div className="settings-actions">
            <button type="button" className="primary" disabled={!confirm||busy}
              onClick={()=>{void attach();}}>RCON átvétel véglegesítése</button>
            <button type="button" className="secondary" disabled={busy}
              onClick={()=>{setPreview(null);setConfirm(false);}}>Mégse</button>
          </div>
        </div>
      </div>}
      {notice&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{notice}</div>}
      {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
      <div className="warning-inline"><Shield size={16}/> A RCON-kapcsolat profilbeli
        átvétele nem igazolja a karakter-/tribe-utazást vagy az ASA SaveComplete-t.</div>
    </div>
  </section>;
}
