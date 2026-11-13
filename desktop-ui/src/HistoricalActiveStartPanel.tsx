import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,Play,Shield} from "lucide-react";
import {managerBridge,type HistoricalActiveStartPreview,
  type HistoricalActiveStartStatus,type HistoricalLoaderModePreview} from "./bridge";

/**
 * Starts a preserved historical Store-v1 ASA map with native source,
 * INI/port/process checks and explicit typed approval. A pinned Obsidian
 * source does not depend on another host's obsolete korábbi Manager ACTIVE/DONE marker.
 * No WebView-supplied launch arguments or original paths.
 */
export function HistoricalActiveStartPanel({mapId,rconAttached,onStarted}:{
  mapId:string;rconAttached:boolean;onStarted:()=>Promise<void>
}){
  const [state,setState]=useState<HistoricalActiveStartStatus|null>(null);
  const [preview,setPreview]=useState<HistoricalActiveStartPreview|null>(null);
  const [modePreview,setModePreview]=useState<HistoricalLoaderModePreview|null>(null);
  const [phrase,setPhrase]=useState("");
  const [busy,setBusy]=useState(false);
  const [checkingControl,setCheckingControl]=useState(false);
  const controlGeneration=useRef(0);
  const actionGeneration=useRef(0);
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const belongs=(requested:string,generation:number)=>
    currentMap.current===requested&&actionGeneration.current===generation;
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    let active=true;
    controlGeneration.current++;
    const generation=++actionGeneration.current;
    setCheckingControl(false);setBusy(false);
    setState(null);setPreview(null);setModePreview(null);
    setPhrase("");setError("");setNotice("");
    managerBridge.historicalActiveStartStatus(mapId).then(v=>{
      if(active&&belongs(mapId,generation)&&v.mapId.toLowerCase()===mapId.toLowerCase())
        setState(v);
    }).catch(()=>{if(active&&belongs(mapId,generation))
      setError("A Store-v1 folyamatállapot nem ellenőrizhető.");});
    return ()=>{active=false;controlGeneration.current++;};
  },[mapId,rconAttached]);
  const changeMode=async(useApi=state?.requestedMode!=="asaApiLoader")=>{
    if(!state?.confirmedStopped||busy||preview||modePreview)return;
    const requested=mapId,generation=actionGeneration.current;
    setBusy(true);setError("");setNotice("");
    try{
      const result=await managerBridge.historicalLoaderModePreview(requested,useApi);
      if(!belongs(requested,generation))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-STORE-MODE-PREVIEW-OTHER-MAP");
      setModePreview(result);
    }catch{
      if(belongs(requested,generation))setError("A map helyi folyamata, RCON-ja, naplója vagy ASA API indítási módja nem igazolt.");
    }finally{if(belongs(requested,generation))setBusy(false);}
  };
  const applyMode=async()=>{
    if(!modePreview||busy)return;
    if(modePreview.mapId.toLowerCase()!==mapId.toLowerCase())return;
    const token=modePreview.token,requested=mapId,generation=actionGeneration.current;
    setModePreview(null);setBusy(true);setError("");setNotice("");
    try{
      const receipt=await managerBridge.historicalLoaderModeApply(token);
      if(!belongs(requested,generation))return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||receipt.serverStarted||
         receipt.originalStoreTouched)throw new Error("UM-STORE-MODE-RECEIPT");
      setPreview(null);
      setNotice(receipt.mode==="asaApiLoader"?
        "Az ASA API Loader indítási mód mentve; ASA-szerver nem indult.":
        "A közvetlen ASA indítási mód mentve; ASA-szerver nem indult.");
      try{
        const status=await managerBridge.historicalActiveStartStatus(requested);
        if(belongs(requested,generation))setState(status);
      }catch{
        if(belongs(requested,generation))setError("A módválasztás mentve, de a képernyő frissítése nem sikerült. Nem indítottunk ASA-szervert.");
      }
    }catch{
      if(belongs(requested,generation))setError("A módváltás eredménye nem igazolt. Nem indítunk automatikusan ASA-t.");
    }finally{if(belongs(requested,generation))setBusy(false);}
  };
  // The original Store map's RCON handoff is separate from Process.Start.
  // Its native bridge verifies original source, current ASA PID and the
  // Windows-owned TCP listener before every read-only ListPlayers query.
  const verifyExistingControl=async(targetMapId:string)=>{
    const generation=++controlGeneration.current;
    setCheckingControl(true);
    let completed=false;
    try{
      for(let attempt=0;attempt<300;attempt++){
        await new Promise<void>(resolve=>setTimeout(resolve,2000));
        if(controlGeneration.current!==generation)return;
        try{
          const status=await managerBridge.historicalRconLiveStatus(targetMapId);
          if(controlGeneration.current!==generation)return;
          if(!status.permitted)continue;
          const response=await managerBridge.historicalRconPlayers(targetMapId);
          if(controlGeneration.current!==generation)return;
          if(response.readOnly && response.command==="ListPlayers" &&
             response.response.trim().length>0){
            setNotice(previous=>previous+
              " A helyi Store-v1 ASA RCON-vezérlése elérhető és hitelesítve (PID + TCP tulajdonos ellenőrizve). A karakterutazás külön ellenőrzendő.");
            completed=true;
            break;
          }
        }catch{/* ASA may still be starting; no Start, Stop or mutating command retry. */}
      }
      if(!completed && controlGeneration.current===generation)
        setError("A Store-v1 ASA RCON-kapcsolata 10 percen belül nem igazolható. Nem állítottuk le és nem indítottuk újra a szervert.");
    }finally{
      if(controlGeneration.current===generation)setCheckingControl(false);
    }
  };

  const inspect=async()=>{
    if(busy||state?.running||!state?.rconConfigured||
       state?.launchModeApprovalRequired)return;
    const requested=mapId,generation=actionGeneration.current;
    setBusy(true);setError("");setNotice("");setPreview(null);setPhrase("");
    try{
      const result=await managerBridge.historicalActiveStartPreview(requested);
      if(!belongs(requested,generation))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-STORE-START-PREVIEW-MAP-MISMATCH");
      setPreview(result);
    }catch(e){
      if(!belongs(requested,generation))return;
      const code=e instanceof Error?e.message:"";
      const errors:Record<string,string>={
        "UM-STORE-NATIVE-PORT-IN-USE":"A map Game/Query vagy RCON-portja jelenleg foglalt. Ellenőrizd a helyi Windows-folyamatokat; ASA nem indult.",
        "UM-STORE-NATIVE-PROCESS-NOT-PROVEN-STOPPED":"A korábbi ASA/Loader folyamat leállítása nem bizonyítható. Új Start nem indítható.",
        "UM-STORE-NATIVE-ORIGINAL-TWO-HOST-NOT-READY":"A még át nem vett eredeti profil régi kétgépes ACTIVE/DONE állapota nem igazolt. A jóváhagyott Obsidian-profil ettől függetlenül működhet.",
        "UM-STORE-RUNTIME-CLUSTER-INCOMPLETE":"A jóváhagyott Obsidian map hiányos runtime clusterazonosítóval vagy átviteli gyökérrel rendelkezik.",
        "UM-STORE-RUNTIME-RCON-INI-MISMATCH":"Az ASA jelenlegi RCON INI és a helyi Obsidian RCON-profil nem egyezik. Ellenőrizd a jóváhagyott beállításokat.",
        "UM-STORE-NATIVE-EXE-RCON-PARITY":"A helyi ASA EXE vagy RCON INI azonossága nem igazolható.",
        "UM-STORE-NATIVE-BACKUP-RECOVERY":"A telepítésnél korábbi mentési/visszaállítási helyreállítás vár kezelői rendezésre.",
        "UM-STORE-LAUNCH-MODE-APPROVAL-REQUIRED":"Válaszd ki és hagyd jóvá az ASA API Loader vagy a közvetlen indítási módot.",
        "UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR":"Az előző Restart megszakadt, a helyi Restart-v1 napló kézi rendezést igényel. A map nem indulhat el újra automatikusan.",
        "UM-STORE-PIN-MANIFEST-MISSING":"Hiányzik a jóváhagyott eredeti forrás Obsidianban tárolt ellenőrzőjegyzéke. Az eredeti fájlokat ne írd felül; ellenőrizd az átvételt.",
        "UM-STORE-PIN-MANIFEST-INVALID":"Az átvett Store-v1 forrás ellenőrzőjegyzéke hibás. Az eredeti fájlok megőrzendők; újraellenőrzés szükséges.",
        "UM-STORE-PIN-FILE-CHANGED":"Az Obsidian saját, jóváhagyott Store-v1 forrásmásolata eltér az átvételkori fájltól. Az indítás letiltva.",
        "UM-STORE-PIN-SOURCE-PARITY":"Az átvett Store-v1 forrásfájlok közös ellenőrzőösszege nem egyezik. Új forrásazonosság-ellenőrzés szükséges.",
        "UM-STORE-PIN-NOT-A-DIRECTORY":"A helyi Store-v1 forrásmappa helyét fájl foglalja el. Kézi profiljavítás szükséges.",
        "UM-STORE-PIN-MAP-IDENTITY":"A jóváhagyott Store-v1 forrás nem ehhez a maphoz tartozik. A mapazonosító megőrzése mellett ellenőrizd az importot.",
        "UM-STORE-PIN-OWNER-CHANGED":"A map tulajdonosa vagy forrásazonossága eltér az átvett Store-v1 profiltól. Indítás letiltva.",
        "UM-STORE-PIN-MAP-ROOT-CHANGED":"Az eredeti map telepítési gyökere eltér a megőrzött forrástól. Ellenőrizd a jóváhagyott helyi profilt.",
        "UM-STORE-PIN-CLUSTER-METADATA":"Az eredeti runtime clusterazonosító vagy átviteli útvonal nem olvasható a megőrzött forrásból.",
        "UM-STORE-PIN-NOT-YET-APPROVED":"Az eredeti eredeti forrás még nem került jóváhagyottan az Obsidian saját profiljába; előbb az átvételt kell véglegesíteni."
      };
      setError(errors[code]??"A Store-v1 map indítási előellenőrzése nem sikerült. Ellenőrizd a helyi naplót és folyamatállapotot; ASA nem indult.");
    }finally{if(belongs(requested,generation))setBusy(false);}
  };
  const start=async()=>{
    if(!preview||preview.mapId.toLowerCase()!==mapId.toLowerCase()||
       phrase!=="STORE-V1 ACTIVE START"||busy)return;
    const token=preview.token,requested=mapId,generation=actionGeneration.current;
    setPreview(null);setPhrase("");setBusy(true);
    setError("");setNotice("");
    try{
      const receipt=await managerBridge.historicalActiveStartApply(
        token,"STORE-V1 ACTIVE START");
      if(!belongs(requested,generation))return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         !receipt.startRequested||receipt.originalStoreMetadataChanged||
         receipt.conversionPerformed)
        throw new Error("UM-STORE-START-RECEIPT-INVALID");
      setNotice(receipt.processRole==="loaderParent"?
        receipt.mapName+" · ASA API Loader PID: "+receipt.loaderPid+
        ". Az ASA gyermek PID és RCON készenlét még ellenőrzendő.":
        receipt.mapName+" · Windows ASA PID: "+(receipt.serverPid??receipt.pid)+
        ". Az indítás megtörtént; játékbeli betöltés külön ellenőrzendő.");
      try{
        const status=await managerBridge.historicalActiveStartStatus(requested);
        if(belongs(requested,generation))setState(status);
      }catch{
        if(belongs(requested,generation))
          setError("Az ASA indítási visszaigazolása megvan, de a folyamatállapot frissítése sikertelen. Ne ismételd meg az indítást.");
      }
      try{await onStarted();}catch{
        if(belongs(requested,generation))
          setError("Az ASA indítási visszaigazolása megvan, de a dashboard nem frissült. Új Start parancs nem történt.");
      }
      if(belongs(requested,generation))void verifyExistingControl(requested);
    }catch(e){
      if(!belongs(requested,generation))return;
      const code=e instanceof Error?e.message:"";
      const details:Record<string,string>={
        "UM-STORE-ACTIVE-MAP-CHANGED":"A map jóváhagyott profilja változott. Indíts új előellenőrzést.",
        "UM-STORE-ACTIVE-ORIGINAL-SOURCE-CHANGED":"Az eredeti Store-forrás vagy a helyi indítási terv változott. Új előellenőrzés szükséges.",
        "UM-STORE-ACTIVE-PROCESS-UNVERIFIED":"A Start utáni Windows folyamat nem igazolható. Ellenőrizd a tényleges PID-et és a tartós műveleti naplót; ne próbáld vakon újraindítani.",
        "UM-STORE-ACTIVE-APPROVAL-EXPIRED":"A jóváhagyás lejárt vagy már felhasználtad. Friss előellenőrzés kell."
      };
      setError(details[code]??"Az indítás eredménye nem igazolható, "+
        "vagy az eredeti állapot megváltozott. Nincs automatikus újrapróbálás. "+
        "Ellenőrizd a Windows PID-et és a Store-műveleti naplót.");
    }finally{if(belongs(requested,generation))setBusy(false);}
  };
  return <section className="glass panel historical-active-start"
    aria-label="Történeti Store-v1 ASA indítás">
    <div className="panel-head">
      <h2><Play size={18}/> Eredeti Store-v1 szerver indítása</h2>
      <span className="mod-tag amber"><LockKeyhole size={13}/> Meglévő ACTIVE session</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">Az eredeti Store-v1 mapot megőrzött adatformátummal
        indítja. A natív Windows réteg minden indítás előtt ellenőrzi a map
        azonosságát, az ASA/RCON INI-t, a portokat és a leállított folyamatot.
        Az Obsidianba jóváhagyottan átvett map helyi indítása nem igényli a másik
        host régi korábbi Manager ACTIVE/DONE-jelölőjét; a tényleges clusterutazás külön bizonyítandó.</p>
      <div className="details-line"><span>Kiválasztott indítás</span>
        <b>{state?.launchModeApprovalRequired?
          "Még nem jóváhagyott · indítás tiltva":
          state?.requestedMode==="asaApiLoader"?"ASA API Loader":"Közvetlen ASA"}</b></div>
      {state?.launchModeApprovalRequired&&<div className="warning-inline" role="alert">
        <Shield size={17}/> A mapot leállított eredeti profilból importáltad. Az
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
          <p>{preview.originalPeerCheckRequiredForLocalStart?
              "A régi, még át nem vett eredeti profil két peerének ACTIVE/DONE állapota igazolt.":
              "Az Obsidianban jóváhagyott helyi Store-profil indítható a másik host régi ACTIVE/DONE jelölője nélkül; a távoli elérés és a karakterutazás ettől még nem igazolt."}
            {" "}A Manager a {preview.launchMode==="asaApiLoader"?
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
      {checkingControl&&<p role="status" className="muted small">Indítás utáni Store-v1 ASA/RCON ellenőrzés (csak olvasás, legfeljebb 10 perc). Nincs új Start vagy Store-írás.</p>}
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
