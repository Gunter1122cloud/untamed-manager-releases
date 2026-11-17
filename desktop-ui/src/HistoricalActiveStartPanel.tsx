import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,Play,Shield} from "lucide-react";
import {managerBridge,type HistoricalActiveStartPreview,
  type HistoricalActiveStartStatus} from "./bridge";

/**
 * Starts a preserved historical Store-v1 ASA map with native source,
 * INI/port/process checks and native one-use approval. A pinned Obsidian
 * source does not depend on another host's obsolete korábbi kezelőalkalmazás ACTIVE/DONE marker.
 * No WebView-supplied launch arguments or original paths.
 */
// Native replies may be lost after Process.Start. An uncertain launch must
// never become an automatic second Start after a React view remount.
const acceptedHistoricalStarts=new Set<string>();
const uncertainHistoricalStarts=new Set<string>();
const uncertainHistoricalModes=new Set<string>();

export function HistoricalActiveStartPanel({mapId,rconAttached,onStarted}:{
  mapId:string;rconAttached:boolean;onStarted:()=>Promise<void>
}){
  const [state,setState]=useState<HistoricalActiveStartStatus|null>(null);
  const startInFlight=useRef(false);
  const [startAccepted,setStartAccepted]=useState(()=>acceptedHistoricalStarts.has(mapId));
  const [startUncertain,setStartUncertain]=useState(()=>uncertainHistoricalStarts.has(mapId));
  const modeInFlight=useRef(false);
  const [modeUncertain,setModeUncertain]=useState(()=>uncertainHistoricalModes.has(mapId));
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
    setState(null);setModeUncertain(uncertainHistoricalModes.has(mapId));
    setStartAccepted(acceptedHistoricalStarts.has(mapId));
    setStartUncertain(uncertainHistoricalStarts.has(mapId));
    setError("");setNotice("");
    managerBridge.historicalActiveStartStatus(mapId).then(v=>{
      if(active&&belongs(mapId,generation)&&v.mapId.toLowerCase()===mapId.toLowerCase())
        setState(v);
    }).catch(()=>{if(active&&belongs(mapId,generation))
      setError("A Store-v1 folyamatállapot nem ellenőrizhető.");});
    return ()=>{active=false;controlGeneration.current++;};
  },[mapId,rconAttached]);
  // Read-only refresh: never starts/stops the ASA or sends RCON bytes.
  const refreshRuntimeEvidence=async()=>{
    if(busy||startInFlight.current)return;
    const requested=mapId,generation=actionGeneration.current;
    setBusy(true);
    try{
      const fresh=await managerBridge.historicalActiveStartStatus(requested);
      if(!fresh||fresh.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-STORE-START-STATUS-OTHER-MAP");
      if(belongs(requested,generation)){
        setState(fresh);
        if(fresh.confirmedStopped&&!fresh.running&&!fresh.loaderPid){
          acceptedHistoricalStarts.delete(requested);
          uncertainHistoricalStarts.delete(requested);
          uncertainHistoricalModes.delete(requested);
          setStartAccepted(false);setStartUncertain(false);
          setModeUncertain(false);
        }
      }
    }catch{
      if(belongs(requested,generation))
        setError("Az ASA API / Loader állapot csak olvasó vizsgálata nem sikerült; szervervezérlés nem történt.");
    }finally{if(belongs(requested,generation))setBusy(false);}
  };
  const changeMode=async(useApi=state?.requestedMode!=="asaApiLoader")=>{
    if(!state?.confirmedStopped||busy||startInFlight.current||
       modeInFlight.current||startAccepted||startUncertain||modeUncertain)return;
    modeInFlight.current=true;
    const requested=mapId,generation=actionGeneration.current;
    let applied=false;
    setBusy(true);setError("");setNotice("");
    try{
      const proof=await managerBridge.historicalLoaderModePreview(requested,useApi);
      if(!belongs(requested,generation))return;
      if(!proof||!proof.token||proof.mapId.toLowerCase()!==requested.toLowerCase()||
         proof.useApi!==useApi||proof.startsServer!==false||
         proof.changesStore!==false||proof.requiresStoppedProcess!==true)
        throw new Error("UM-STORE-MODE-PREVIEW-INVALID");
      applied=true;
      const receipt=await managerBridge.historicalLoaderModeApply(proof.token);
      if(!receipt||receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         receipt.useApi!==useApi||receipt.mode!==(useApi?"asaApiLoader":"directAsa")||
         receipt.serverStarted!==false||receipt.originalStoreTouched!==false)
        throw new Error("UM-STORE-MODE-RECEIPT-INVALID");
      if(!belongs(requested,generation))return;
      setNotice(useApi?"Az ASA API Loader indítási mód mentve; ASA-szerver nem indult.":
        "A közvetlen ASA indítási mód mentve; ASA-szerver nem indult.");
      try{
        const status=await managerBridge.historicalActiveStartStatus(requested);
        if(!status||status.mapId.toLowerCase()!==requested.toLowerCase())
          throw new Error("UM-STORE-MODE-STATUS-OTHER-MAP");
        if(belongs(requested,generation))setState(status);
      }catch{
        if(belongs(requested,generation))
          setError("A módváltás igazolt, de az állapotfrissítés sikertelen. ASA nem indult.");
      }
    }catch{
      if(applied){
        uncertainHistoricalModes.add(requested);
        if(belongs(requested,generation)){
          setModeUncertain(true);
          setError("A módváltás kimenetele nem igazolható. Nincs ismétlés vagy Start a helyi folyamat kézi ellenőrzéséig.");
        }
      }else if(belongs(requested,generation))
        setError("A helyi ASA/Loader indítási mód előellenőrzése elutasítva. ASA nem indult.");
    }finally{
      modeInFlight.current=false;
      if(belongs(requested,generation))setBusy(false);
    }
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
    if(startInFlight.current||busy||state?.running||
       !state?.confirmedStopped||!state?.rconConfigured||
       state?.launchModeApprovalRequired||startAccepted||startUncertain||modeUncertain)return;
    startInFlight.current=true;
    const requested=mapId,generation=actionGeneration.current;
    setBusy(true);setError("");setNotice("");
    try{
      const result=await managerBridge.historicalActiveStartPreview(requested);
      if(!belongs(requested,generation))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-STORE-START-PREVIEW-MAP-MISMATCH");
      if(!result.token||result.conversionAllowed!==false||
         result.automaticRetryAllowed!==false||result.serverStarted!==false||
         result.readyCertified!==false||
         result.evidence!=="ORIGINAL_ACTIVE_STORE_NATIVE_START_PREFLIGHT")
        throw new Error("UM-STORE-START-PREVIEW-INVALID");
      await start(result);
    }catch(e){
      if(!belongs(requested,generation))return;
      const code=e instanceof Error?e.message:"";
      const errors:Record<string,string>={
        "UM-STORE-NATIVE-PORT-IN-USE":"A map Game/Query vagy RCON-portja jelenleg foglalt. Ellenőrizd a helyi Windows-folyamatokat; ASA nem indult.",
        "UM-STORE-NATIVE-PROCESS-NOT-PROVEN-STOPPED":"A korábbi ASA/Loader folyamat leállítása nem bizonyítható. Új Start nem indítható.",
        "UM-STORE-NATIVE-ORIGINAL-TWO-HOST-NOT-READY":"A még át nem vett eredeti profil régi kétgépes ACTIVE/DONE állapota nem igazolt. A jóváhagyott Obsidian-profil ettől függetlenül működhet.",
        "UM-STORE-LIVE-PEER-DONE-INVALID":"UM-STORE-LIVE-PEER-DONE-INVALID: Az egyik történeti Store-v1 peer DONE rekordja érvénytelen (fázis, session, konfigurációlenyomat vagy 5 eredeti kiválasztás). Az eredeti koordinációs fájlokat ne módosítsd.",
        "UM-STORE-LIVE-PEER-SELECTED-MISMATCH":"UM-STORE-LIVE-PEER-SELECTED-MISMATCH: A peer történeti öt kiválasztott mapja nem felel meg a regisztrált tulajdonosi listának. Ne indítsd kézzel és ne töröld a peer-rekordot.",
        "UM-STORE-LIVE-LOCAL-LOCK-MISMATCH":"UM-STORE-LIVE-LOCAL-LOCK-MISMATCH: A helyi Store-v1 lock/session/konfiguráció eltér az érvényes forrástól. Nincs automatikus jóváhagyás.",
        "UM-STORE-LIVE-MAP-NOT-OWNED-BY-LOCAL-PEER":"UM-STORE-LIVE-MAP-NOT-OWNED-BY-LOCAL-PEER: Ez a map nem szerepel a helyi DONE peer öt eredeti kiválasztott mapja között.",
        "UM-STORE-LAUNCH-EXPLICIT-SOURCE-PIN-REQUIRED":"UM-STORE-LAUNCH-EXPLICIT-SOURCE-PIN-REQUIRED: A naplórendezést már jóváhagytad, de az indításhoz az aktuális bővített Store-v1 forrás még nincs külön jóváhagyottan átvéve. A Store-v1 átvételi panelen ellenőrizd a „Meglévő mapok forrásátvételének ellenőrzése” funkciót; a jóváhagyás több eredeti helyi map közös forrásmásolatát is érintheti. Indítás és Store-írás nem történt.",
        "UM-STORE-TOPOLOGY-PEER-INVALID":"UM-STORE-TOPOLOGY-PEER-INVALID: A két peer egyikének maplistája vagy eredeti kiválasztása érvénytelen.",
        "UM-STORE-TOPOLOGY-OVERLAPPING-MAP-OWNERS":"UM-STORE-TOPOLOGY-OVERLAPPING-MAP-OWNERS: Azonos map GUID vagy mapnév több peerben szerepel. Indítás letiltva.",
        "UM-STORE-TOPOLOGY-SELECTION-MISMATCH":"UM-STORE-TOPOLOGY-SELECTION-MISMATCH: A történeti kiválasztott map nincs a peer tulajdonosi listájában.",
        "UM-STORE-TOPOLOGY-LOCAL-OWNER-INVALID":"UM-STORE-TOPOLOGY-LOCAL-OWNER-INVALID: A két peer vagy az eredeti tíz kiválasztott map tulajdonosi azonossága nem igazolt.",
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
        "UM-STORE-PIN-NOT-YET-APPROVED":"Az eredeti forrás még nem került jóváhagyottan az Obsidian saját profiljába; előbb az átvételt kell véglegesíteni."
      };
      setError(errors[code]??(code.startsWith("UM-STORE-")?
        code+" · A natív előellenőrzés elutasította az indítást; ASA nem indult.":
        "A Store-v1 map indítási előellenőrzése nem sikerült. Ellenőrizd a helyi naplót és folyamatállapotot; ASA nem indult."));
    }finally{
      startInFlight.current=false;
      if(belongs(requested,generation))setBusy(false);
    }
  };
  const start=async(proof:HistoricalActiveStartPreview)=>{
    if(!proof||proof.mapId.toLowerCase()!==mapId.toLowerCase())return;
    const token=proof.token,requested=mapId,generation=actionGeneration.current;
    setBusy(true);
    setError("");setNotice("");
    try{
      const receipt=await managerBridge.historicalActiveStartApply(token);
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         receipt.startRequested!==true||receipt.originalStoreMetadataChanged!==false||
         receipt.conversionPerformed!==false||receipt.readyCertified!==false||
         receipt.automaticRetryAllowed!==false||
         !Number.isSafeInteger(receipt.pid)||receipt.pid<=0||
         receipt.launchMode!==proof.launchMode||
         (receipt.processRole==="loaderParent"?
           receipt.loaderPid!==receipt.pid||receipt.serverPid!==null:
           receipt.processRole!=="asaServer"||receipt.serverPid!==receipt.pid||receipt.loaderPid!==null))
        throw new Error("UM-STORE-START-RECEIPT-INVALID");
      acceptedHistoricalStarts.add(requested);
      uncertainHistoricalStarts.delete(requested);
      if(!belongs(requested,generation))return;
      setStartAccepted(true);setStartUncertain(false);
      setNotice(receipt.processRole==="loaderParent"?
        receipt.mapName+" · ASA API Loader PID: "+receipt.loaderPid+
        ". Az ASA gyermek PID és RCON készenlét még ellenőrzendő.":
        receipt.mapName+" · valódi Windows ASA PID: "+(receipt.serverPid??receipt.pid)+
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
      uncertainHistoricalStarts.add(requested);
      if(!belongs(requested,generation))return;
      setStartUncertain(true);
      const code=e instanceof Error?e.message:"";
      const details:Record<string,string>={
        "UM-STORE-ACTIVE-MAP-CHANGED":"A map jóváhagyott profilja változott. Indíts új előellenőrzést.",
        "UM-STORE-ACTIVE-ORIGINAL-SOURCE-CHANGED":"Az eredeti Store-forrás vagy a helyi indítási terv változott. Új előellenőrzés szükséges.",
        "UM-STORE-ACTIVE-PROCESS-UNVERIFIED":"A Start utáni Windows folyamat nem igazolható. Ellenőrizd a tényleges PID-et és a tartós műveleti naplót; ne próbáld vakon újraindítani.",
        "UM-STORE-ACTIVE-APPROVAL-EXPIRED":"A jóváhagyás lejárt vagy már felhasználtad. Friss előellenőrzés kell."
      };
      setError((details[code]??"Az indítás eredménye nem igazolható, "+
        "vagy az eredeti állapot megváltozott.")+
        " A szerver már elindulhatott. Nincs automatikus újrapróbálás; "+
        "az állapotfrissítés és a helyi Windows PID/Store-napló ellenőrzése szükséges.");
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
        host korábbi kezelőalkalmazás ACTIVE/DONE-jelölőjét; a tényleges clusterutazás külön bizonyítandó.</p>
      <div className="details-line"><span>Kiválasztott indítás</span>
        <b>{state?.launchModeApprovalRequired?
          "Még nem jóváhagyott · indítás tiltva":
          state?.requestedMode==="asaApiLoader"?"ASA API Loader":"Közvetlen ASA"}</b></div>
      {state?.launchModeApprovalRequired&&<div className="warning-inline" role="alert">
        <Shield size={17}/> A mapot leállított eredeti profilból importáltad. Az
        eredeti indítási mód nem bizonyítható a futó folyamat nélkül.
        Az első Start előtt jóvá kell hagyni az ASA API Loader vagy
        közvetlen ASA módot; ez a meglévő RCON csatlakoztatása előtt is elvégezhető.
        Addig az Obsidian natív rétege semmilyen indítást nem enged.
      </div>}
      <div className="details-line"><span>Megfigyelt ASA</span>
        <b>{state?.running?"Fut · ASA PID "+(state.serverPid??"nem igazolt"):
          state?.loaderPid?"Loader PID "+state.loaderPid+" · ASA még nem igazolt":
          state?.confirmedStopped?"Leállítás igazolva":"Folyamatállapot nem igazolt"}</b></div>
      {state?.requestedMode==="asaApiLoader"&&
        <div className="historical-preview" role="region"
          aria-label="ASA API Loader és plugin bizonyítékok">
          <div className="details-line"><span>API Loader</span>
            <b>{state.loaderProcessObserved&&state.loaderPid?
              "Szülő PID "+state.loaderPid:"Loader-folyamat nem igazolt"}</b></div>
          <div className="details-line"><span>ASA indulás</span>
            <b>{state.asaJoinAdvertised?
              "Játékosfogadás naplója megfigyelve":
              "Teljes indulás nem igazolt"}</b></div>
          <div className="details-line"><span>API DLL a gyermekben</span>
            <b>{state.asaApiDllInChildObserved?
              "Megfigyelve az ASA PID-ben":"Nem igazolt"}</b></div>
          <div className="details-line"><span>API hook-napló</span>
            <b>{state.apiHooksLogObserved?
              "Aktuális naplóban megfigyelve":"Nem igazolt"}</b></div>
          <p className="muted small">
            A következő API-s indításkor a Loader külön konzolja megjelenik.
            Loader PID, ASA DLL és API hook-napló három külön bizonyíték;
            a pluginok játékbeli működését önmagukban nem igazolják.
            {state.apiRuntimeState==="diagnosticUnavailable"&&
              " A modul- vagy naplóvizsgálat nem elérhető; ez nem bizonyít DLL-hibát."}
          </p>
          <button type="button" className="secondary" disabled={busy}
            onClick={()=>{void refreshRuntimeEvidence();}}>
            <Shield size={16}/> API/Loader állapot frissítése (csak olvasás)
          </button>
        </div>}
      {state?.requestedMode!=="asaApiLoader"&&<button type="button"
        className="secondary" disabled={busy} onClick={()=>{void refreshRuntimeEvidence();}}>
        <Shield size={16}/> API/Loader állapot frissítése (csak olvasás)
      </button>}
      <div className="settings-actions">
        <button type="button" className="secondary"
          disabled={busy||!state?.confirmedStopped||startAccepted||startUncertain||modeUncertain} onClick={()=>{void changeMode();}}>
          <Shield size={16}/> {state?.requestedMode==="asaApiLoader"?
            "Közvetlen ASA mód választása":"ASA API Loader mód választása"}
        </button>
        {state?.launchModeApprovalRequired&&
          state.requestedMode==="directAsa"&&
          <button type="button" className="secondary"
            disabled={busy||!state.confirmedStopped||startAccepted||startUncertain||modeUncertain} onClick={()=>{void changeMode(false);}}>
            <Shield size={16}/> Közvetlen ASA mód kifejezett jóváhagyása
          </button>}
      </div>
      <div className="details-line"><span>Meglévő RCON</span>
        <b>{state?.rconConfigured?"Managerhez kapcsolva":"Előbb add meg az eredeti RCON-t"}</b></div>
      <div className="settings-actions">
        <button className="primary" type="button"
          onClick={()=>{void inspect();}}
          disabled={busy||modeUncertain||!state||state.running||
            !state.confirmedStopped||!state.rconConfigured||
            state.launchModeApprovalRequired||startAccepted||startUncertain||modeUncertain}>
          <Play size={16}/> {state?.requestedMode==="asaApiLoader"?
            "Eredeti Store-v1 API Loader indítása":"Eredeti Store-v1 ASA indítása"}
        </button>
      </div>
      {(startUncertain||modeUncertain)&&<p role="status" className="muted small">
        A korábbi Start kimenetele bizonytalan. A csak olvasó API/Loader állapot
        frissítése után, igazoltan leállított folyamat esetén engedélyezhető
        egy új, külön felhasználói Start. Nincs automatikus ismétlés.
      </p>}
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
