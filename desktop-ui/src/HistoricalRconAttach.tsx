import {useEffect,useState,type FormEvent} from "react";
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
    setStatus(null);setPreview(null);setPassword("");setError("");setNotice("");
    managerBridge.historicalRconStatus(mapId).then(v=>{
      if(active){setStatus(v);setPort(v.suggestedPort>0?String(v.suggestedPort):"");}
    }).catch(()=>{if(active)setError("Az eredeti Store-v1 RCON-beállítás állapota nem olvasható.");});
    return ()=>{active=false;};
  },[mapId]);
  const edit=()=>{setPreview(null);setConfirm(false);setNotice("");setError("");};
  const inspect=async(event:FormEvent)=>{
    event.preventDefault();
    const parsed=Number(port);
    if(!Number.isInteger(parsed)||parsed<1024||parsed>65535||!password){
      setError("Adj meg érvényes RCON-portot és az eredeti GameUserSettings.ini jelszavát.");
      return;
    }
    setBusy(true);setError("");setNotice("");setPreview(null);setConfirm(false);
    try{
      const result=await managerBridge.historicalRconPreview(mapId,parsed,password);
      setPassword("");setPreview(result);
    }catch{
      setPassword("");
      setError("Az eredeti kétgépes ACTIVE/DONE állapot, a helyi EXE, az INI vagy a RCON-jelszó nem egyezik. Az eredeti fájlok érintetlenek.");
    }finally{setBusy(false);}
  };
  const attach=async()=>{
    if(!preview||!confirm||busy)return;
    setBusy(true);setError("");setNotice("");
    const token=preview.token;setPreview(null);setConfirm(false);
    let committed=false;
    try{
      const receipt=await managerBridge.historicalRconApply(token);
      committed=true;
      // The native DPAPI profile is already committed. A failed subsequent
      // status/refresh MUST NOT disguise that commit as a failed attach and
      // tempt the operator to repeat a single-use mutation.
      setNotice("Az eredeti RCON-profil a helyi Managerbe elmentve (TCP "+
        receipt.rconPort+"). Az ASA és a Store-fájlok nem módosultak.");
      try{
        setStatus(await managerBridge.historicalRconStatus(mapId));
        await onConfigured();
      }catch{
        setNotice("Az eredeti RCON-profil elmentve (TCP "+receipt.rconPort+
          "), de a felület frissítése sikertelen. Újraküldés helyett "+
          "frissítsd a maplistát. Az ASA és a Store-fájlok nem módosultak.");
      }
    }catch{
      if(!committed)setError("Az eredeti RCON-kapcsolat átvétele nem fejezhető be, vagy a profil közben megváltozott. Ellenőrizd az aktuális állapotot.");
    }finally{setBusy(false);}
  };
  return <section className="glass panel historical-rcon-attach"
      aria-label="Történeti Store-v1 RCON csatlakoztatás">
    <div className="panel-head"><h2><RadioTower size={18}/> Eredeti Store-v1 RCON átvétele</h2>
      <span className="mod-tag amber"><LockKeyhole size={13}/> Eredeti fájlok érintetlenek</span></div>
    <div className="panel-inner">
      <p className="muted small">A kétgépes, már aktív Store-v1 szerver meglévő RCON-portját kapcsolja
        az új Managerhez, <b>új INI, új session, Store-konverzió és szerverindítás nélkül</b>.
        A jelszót a natív Windows DPAPI védi.</p>
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
          <p>Az eredeti kétpeer-es Store-v1 állapotot és a meglévő RCON INI-t ellenőriztük.
            Jóváhagyáskor kizárólag a saját Manager-profil titkosított beállítása változik.</p>
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
