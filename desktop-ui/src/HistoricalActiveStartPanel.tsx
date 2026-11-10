import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,Play,Shield} from "lucide-react";
import {managerBridge,type HistoricalActiveStartPreview,
  type HistoricalActiveStartStatus,type HistoricalLoaderModePreview} from "./bridge";

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
  const [modePreview,setModePreview]=useState<HistoricalLoaderModePreview|null>(null);
  const [phrase,setPhrase]=useState("");
  const [busy,setBusy]=useState(false);
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    let active=true;
    setState(null);setPreview(null);setModePreview(null);
    setPhrase("");setError("");setNotice("");
    managerBridge.historicalActiveStartStatus(mapId).then(v=>{
      if(active)setState(v);
    }).catch(()=>{if(active)setError("A történeti Store-v1 folyamatállapot nem ellenőrizhető.");});
    return ()=>{active=false;};
  },[mapId,rconAttached]);
  const changeMode=async(useApi=state?.requestedMode!=="asaApiLoader")=>{
    if(!state?.confirmedStopped||busy||preview||modePreview)return;
    setBusy(true);setError("");setNotice("");
    try{
      setModePreview(await managerBridge.historicalLoaderModePreview(
        mapId,useApi));
    }catch{
      setError("Az indítási mód nem módosítható: az eredeti map folyamatállapota, "+
        "RCON-ja, naplója vagy Loader/API előfeltétele nem igazolt.");
    }finally{setBusy(false);}
  };
  const applyMode=async()=>{
    if(!modePreview||busy)return;
    const token=modePreview.token;
    setModePreview(null);setBusy(true);setError("");setNotice("");
    try{
      const receipt=await managerBridge.historicalLoaderModeApply(token);
      setPreview(null);
      setNotice(receipt.mode==="asaApiLoader"?
        "Az ASA API Loader indítási mód mentve; ASA-szerver nem indult.":
        "A közvetlen ASA indítási mód mentve; ASA-szerver nem indult.");
      setState(await managerBridge.historicalActiveStartStatus(mapId));
    }catch{
      setError("A módváltás nem igazolható. Nincs automatikus indítás. "+
        "Ellenőrizd a helyi műveleti naplót és a map folyamatát.");
    }finally{setBusy(false);}
  };
  const inspect=async()=>{
    if(busy||state?.running||!state?.rconConfigured||
       state?.launchModeApprovalRequired)return;
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
      setNotice(receipt.processRole==="loaderParent"?
        receipt.mapName+" · ASA API Loader szülőfolyamat PID: "+receipt.loaderPid+
        ". Az ASA gyermek PID, RCON és játékbeli készenlét még NEM igazolt.":
        receipt.mapName+" · valódi Windows ASA PID: "+(receipt.serverPid??receipt.pid)+
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
      <div className="details-line"><span>Kiválasztott indítás</span>
        <b>{state?.launchModeApprovalRequired?
          "Még nem jóváhagyott · indítás tiltva":
          state?.requestedMode==="asaApiLoader"?"ASA API Loader":"Közvetlen ASA"}</b></div>
      {state?.launchModeApprovalRequired&&<div className="warning-inline" role="alert">
        <Shield size={17}/> A mapot leállított Kova-profilból importáltad. Az
        eredeti indítási mód nem bizonyítható a futó folyamat nélkül.
        A meglévő RCON csatlakoztatása után még az első Start előtt
        jóvá kell hagyni az ASA API Loader vagy a közvetlen ASA módot.
        Addig az Obsidian natív rétege semmilyen indítást nem enged.
      </div>}
      <div className="details-line"><span>Megfigyelt ASA</span>
        <b>{state?.running?"Fut · ASA PID "+(state.serverPid??"nem igazolt"):
          state?.loaderPid?"Loader PID "+state.loaderPid+" · ASA még nem igazolt":
          state?.confirmedStopped?"Leállítás igazolva":"Folyamatállapot nem igazolt"}</b></div>
      <div className="settings-actions">
        <button type="button" className="secondary"
          disabled={busy||!state?.confirmedStopped||!!preview||!!modePreview||
            !state?.rconConfigured} onClick={()=>{void changeMode();}}>
          <Shield size={16}/> {state?.requestedMode==="asaApiLoader"?
            "Közvetlen ASA mód választása":"ASA API Loader mód választása"}
        </button>
        {state?.launchModeApprovalRequired&&
          state.requestedMode==="directAsa"&&
          <button type="button" className="secondary"
            disabled={busy||!state.confirmedStopped||!!preview||!!modePreview||
              !state.rconConfigured} onClick={()=>{void changeMode(false);}}>
            <Shield size={16}/> Közvetlen ASA mód kifejezett jóváhagyása
          </button>}
      </div>
      {modePreview&&<div className="warning-inline" role="region"
        aria-label="Eredeti Store-v1 indítási mód módosítása">
        <Shield size={17}/>
        <div>
          <b>{modePreview.useApi?"ASA API Loader":"Közvetlen ASA"} indítás</b>
          <p>Csak a helyi Manager maponkénti indítási kapcsolója módosul.
            Nem indul szerver, nem változik Store-session, cluster-lock vagy
            karakteradat. A következő Start külön előellenőrzést igényel.</p>
          <div className="settings-actions">
            <button type="button" className="primary" disabled={busy}
              onClick={()=>{void applyMode();}}>Indítási mód jóváhagyása</button>
            <button type="button" className="secondary" disabled={busy}
              onClick={()=>setModePreview(null)}>Mégse</button>
          </div>
        </div>
      </div>}
      <div className="details-line"><span>Meglévő RCON</span>
        <b>{state?.rconConfigured?"Managerhez kapcsolva":"Előbb add meg az eredeti RCON-t"}</b></div>
      <div className="settings-actions">
        <button className="secondary" type="button"
          onClick={()=>{void inspect();}} disabled={busy||!!preview||
            !state||state.running||!state.rconConfigured||
            state.launchModeApprovalRequired}>
          <Shield size={16}/> Eredeti Store-v1 indítás előellenőrzése
        </button>
      </div>
      {preview&&<div className="warning-inline" role="region"
        aria-label="Eredeti Store-v1 ASA indítás jóváhagyása">
        <Shield size={17}/>
        <div>
          <b>{preview.mapName} · Game {preview.gamePort} · Query {preview.queryPort}</b>
          <p>Mindkét eredeti Store peer ACTIVE/DONE állapota igazolt a
            művelet előnézetében. A Manager a {preview.launchMode==="asaApiLoader"?
              "helyi ASA API Loadert indítja; a gyermek ASA PID-et külön kell igazolni":
              "helyi ASA EXE-t indítja"}.
            Nem konvertál, nem hoz létre új sessiont vagy shared lockot.</p>
          {preview.launchMode==="asaApiLoader"&&<p className="muted small">
            Hivatalos API cache: {preview.apiCacheState}. A cache-kulcs és a
            jelenlegi ASA EXE hashének egyezése még nem bizonyít API-modulbetöltést;
            cache-hiánynál a hivatalos API saját cache-letöltése és
            ellenőrzése szükséges.
          </p>}
          <label htmlFor="historical-start-phrase">
            Biztonsági megerősítés: írd be pontosan: STORE-V1 ACTIVE START
          </label>
          <input id="historical-start-phrase" spellCheck={false} autoComplete="off"
            value={phrase} onChange={e=>setPhrase(e.target.value)} maxLength={32}/>
          <div className="settings-actions">
            <button type="button" className="primary" disabled={busy||
              phrase!=="STORE-V1 ACTIVE START"} onClick={()=>{void start();}}>
              <Play size={16}/> {preview.launchMode==="asaApiLoader"?
                "Eredeti Store-v1 API Loader indítása":"Eredeti Store-v1 ASA indítása"}
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
