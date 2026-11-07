import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,Play,Shield} from "lucide-react";
import {managerBridge,type HistoricalActiveStartPreview,
  type HistoricalActiveStartStatus} from "./bridge";

/**
 * Starts the ORIGINAL ACTIVE/DONE local ASA map only after a native
 * old-session/two-peer/INI/port/process preflight and an additional typed
 * confirmation. No WebView-supplied launch arguments or original paths.
 */
export function HistoricalActiveStartPanel({mapId,rconAttached,onStarted}:{
  mapId:string;rconAttached:boolean;onStarted:()=>Promise<void>
}){
  const [state,setState]=useState<HistoricalActiveStartStatus|null>(null);
  const [preview,setPreview]=useState<HistoricalActiveStartPreview|null>(null);
  const [phrase,setPhrase]=useState("");
  const [busy,setBusy]=useState(false);
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    let active=true;
    setState(null);setPreview(null);setPhrase("");setError("");setNotice("");
    managerBridge.historicalActiveStartStatus(mapId).then(v=>{
      if(active)setState(v);
    }).catch(()=>{if(active)setError("A történeti Store-v1 folyamatállapot nem ellenőrizhető.");});
    return ()=>{active=false;};
  },[mapId,rconAttached]);
  const inspect=async()=>{
    if(busy||state?.running||!state?.rconConfigured)return;
    setBusy(true);setError("");setNotice("");setPreview(null);setPhrase("");
    try{
      const result=await managerBridge.historicalActiveStartPreview(mapId);
      setPreview(result);
    }catch{
      setError("Az eredeti kétgépes ACTIVE/DONE session/lock, a valódi leállítás, "+
        "az eredeti RCON/INI, a portok vagy a megőrzött indítási profil nem igazolható. "+
        "Nem indítottunk ASA-folyamatot.");
    }finally{setBusy(false);}
  };
  const start=async()=>{
    if(!preview||phrase!=="STORE-V1 ACTIVE START"||busy)return;
    const token=preview.token;setPreview(null);setPhrase("");setBusy(true);
    setError("");setNotice("");
    try{
      const receipt=await managerBridge.historicalActiveStartApply(
        token,"STORE-V1 ACTIVE START");
      setNotice(receipt.mapName+" · valódi Windows ASA PID: "+receipt.pid+
        ". Az indítás megtörtént, a játékbeli csatlakozás és SaveComplete külön ellenőrzendő.");
      try{setState(await managerBridge.historicalActiveStartStatus(mapId));}
      catch{setState(null);}
      await onStarted();
    }catch{
      setError("Az eredeti állapot megváltozott, az indítás nem igazolható, "+
        "vagy megszakadt. Nem lesz automatikus újrapróbálás. "+
        "Ellenőrizd a Windows PID-et és az eredeti Store-műveleti naplót.");
    }finally{setBusy(false);}
  };
  return <section className="glass panel historical-active-start"
    aria-label="Történeti Store-v1 ASA indítás">
    <div className="panel-head">
      <h2><Play size={18}/> Eredeti Store-v1 szerver indítása</h2>
      <span className="mod-tag amber"><LockKeyhole size={13}/> Meglévő ACTIVE session</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">Az eredeti tízmapos, kétgépes Store-v1 klaszter
        már aktív adatformátumával indít. Az eredeti két peer, mapazonosító,
        lock, meglévő ASA/RCON INI, portok és leállított folyamat minden
        indítás előtt a natív Windows rétegben újraellenőrzésre kerül.</p>
      <div className="details-line"><span>Megfigyelt ASA</span>
        <b>{state?.running?"Fut · PID "+(state.pid??"nem igazolt"):
          state?.confirmedStopped?"Leállítás igazolva":"Folyamatállapot nem igazolt"}</b></div>
      <div className="details-line"><span>Meglévő RCON</span>
        <b>{state?.rconConfigured?"Managerhez kapcsolva":"Előbb add meg az eredeti RCON-t"}</b></div>
      <div className="settings-actions">
        <button className="secondary" type="button"
          onClick={()=>{void inspect();}} disabled={busy||!!preview||
            !state||state.running||!state.rconConfigured}>
          <Shield size={16}/> Eredeti Store-v1 indítás előellenőrzése
        </button>
      </div>
      {preview&&<div className="warning-inline" role="region"
        aria-label="Eredeti Store-v1 ASA indítás jóváhagyása">
        <Shield size={17}/>
        <div>
          <b>{preview.mapName} · Game {preview.gamePort} · Query {preview.queryPort}</b>
          <p>Mindkét eredeti Store peer ACTIVE/DONE állapota igazolt a
            művelet előnézetében. A Manager valóban elindítja a helyi ASA EXE-t.
            Nem konvertál, nem hoz létre új sessiont vagy shared lockot.</p>
          <label htmlFor="historical-start-phrase">
            Biztonsági megerősítés: írd be pontosan: STORE-V1 ACTIVE START
          </label>
          <input id="historical-start-phrase" spellCheck={false} autoComplete="off"
            value={phrase} onChange={e=>setPhrase(e.target.value)} maxLength={32}/>
          <div className="settings-actions">
            <button type="button" className="primary" disabled={busy||
              phrase!=="STORE-V1 ACTIVE START"} onClick={()=>{void start();}}>
              <Play size={16}/> Eredeti Store-v1 ASA indítása
            </button>
            <button type="button" className="secondary" disabled={busy}
              onClick={()=>{setPreview(null);setPhrase("");}}>Mégse</button>
          </div>
        </div>
      </div>}
      {notice&&<div className="success-inline" role="status">
        <CheckCircle2 size={17}/>{notice}</div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={17}/>{error}</div>}
      <div className="warning-inline"><Shield size={16}/>
        Nincs automatikus Start/Restart/Store-konverzió; a Windows PID önmagában
        nem tanúsít karakterutazást, kész mentést vagy sikeres játékcsatlakozást.
      </div>
    </div>
  </section>;
}
