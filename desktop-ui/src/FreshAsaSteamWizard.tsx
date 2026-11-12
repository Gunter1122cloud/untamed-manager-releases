import { useEffect, useRef, useState, type FormEvent } from "react";
import { AlertTriangle, CheckCircle2, FolderOpen, HardDrive, Shield } from "lucide-react";
import { managerBridge, type FreshInstallJob } from "./bridge";
import { InitialMapSetup } from "./InitialMapSetup";

const mapNames = [
  "The Island","The Center","Scorched Earth","Aberration","Extinction",
  "Genesis Part 1","Valguero","Ragnarok","Astraeos","Lost Colony"
];

/**
 * Operator workflow: choose map, choose an empty folder, click Download.
 * The SINGLE click runs the native preflight and starts the actual SteamCMD
 * job. No permission gate is removed; Core still checks root, Store,
 * journals and disk, then uses official Valve SteamCMD.
 */
export function FreshAsaSteamWizard({
  onRegistered,initialMapName="The Island",onImport,onOpenMap
}:{
  onRegistered:()=>Promise<void>;initialMapName?:string;
  onImport?:()=>void;onOpenMap?:(mapId:string)=>void
}) {
  const [mapName,setMapName]=useState(
    mapNames.includes(initialMapName)?initialMapName:"__custom__");
  const [customMapName,setCustomMapName]=useState(
    mapNames.includes(initialMapName)?"":initialMapName);
  const [customLevel,setCustomLevel]=useState("");
  const resolvedMapName=mapName==="__custom__"?customMapName.trim():mapName;
  const [installRoot,setInstallRoot]=useState("");
  const [steamExe,setSteamExe]=useState("");
  const [explicitRetry,setExplicitRetry]=useState(false);
  const [recoveryPreview,setRecoveryPreview]=useState<{
    token:string;mapId:string;mapName:string;mapLevel:string;buildId:string
  }|null>(null);
  const [recoveryPhrase,setRecoveryPhrase]=useState("");
  const [job,setJob]=useState<FreshInstallJob|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [message,setMessage]=useState("");
  const [registeredMapId,setRegisteredMapId]=useState<string|null>(null);
  const [firstConfigurationSaved,setFirstConfigurationSaved]=useState(false);
  const autoRegisterId=useRef<string|null>(null);
  // A late initial current-job response must not replace a job started
  // or registered during this WebView session.
  const jobMutationGeneration=useRef(0);

  useEffect(()=>{
    let active=true;
    const generation=jobMutationGeneration.current;
    managerBridge.freshCurrent().then(data=>{
      if(active && generation===jobMutationGeneration.current && data.current){
        setJob(data.current);
        // Native source ID survives reopening the wizard during this
        // Manager session. Never guess the map ID from its display name.
        if(data.current.registered && data.current.mapId)
          setRegisteredMapId(data.current.mapId);
      }
    }).catch(()=>{});
    return ()=>{active=false;};
  },[]);

  // The post-install wizard may be closed and reopened at any time.
  // Reconstruct the completed step from the NATIVE persisted map profile,
  // never a local boolean or a guessed display name. This is read-only and
  // does not issue a second registration, INI write, Start or RCON.
  const resumedMapId=registeredMapId||(job?.registered?job.mapId:null);
  useEffect(()=>{
    if(!job?.registered||!resumedMapId)return;
    let active=true;
    managerBridge.initialStatus(resumedMapId).then(status=>{
      if(active && status.mapId===resumedMapId &&
         status.blocker==="PROFILE_ALREADY_CONFIGURED_OR_PROTECTED" &&
         status.gamePort>0 && status.queryPort>0 && status.hasServerExecutable)
        setFirstConfigurationSaved(true);
    }).catch(()=>{
      // Read-only status unavailable: keep the native initial setup panel
      // available instead of fabricating a completed configuration.
    });
    return ()=>{active=false;};
  },[job?.registered,resumedMapId]);

  useEffect(()=>{
    if(!job||job.registered||job.verified||!!job.errorCode)return;
    let active=true;
    let registrationPending=false;
    const poll=async()=>{
      if(registrationPending)return;
      try{
        const state=await managerBridge.freshStatus(job.jobId);
        if(!active)return;
        if(state.verified && !state.registered && !state.errorCode &&
           autoRegisterId.current===state.jobId) {
          // Do not change job.verified before registration completes: a
          // React effect cleanup would otherwise abandon the receipt.
          registrationPending=true;
          autoRegisterId.current=null; // no automatic retry of config writes
          setBusy(true);
          try{
            const receipt=await managerBridge.freshRegister(state.jobId);
            jobMutationGeneration.current++;
            if(!active)return;
            setJob({...state,registered:true,mapId:receipt.mapId,stage:"registered"});
            setRegisteredMapId(receipt.mapId);
            setFirstConfigurationSaved(false);
            setMessage(receipt.mapName+" letöltve és hozzáadva a Managerhez. Következő lépés: első konfiguráció, majd RCON/mod/API/cluster és külön jóváhagyott Start.");
            try {
              await onRegistered();
            } catch {
              if(active)setError("A map regisztrációja sikerült, de a szerverlista frissítése nem. Ne regisztráld újra; nyisd meg a Szerverek oldalt, vagy frissítsd az állapotot.");
            }
          }catch{
            if(active){
              setJob(state);
              setError("A fájlok letöltődtek, de a Manager-regisztráció nem sikerült. A lenti gombbal próbálhatod meg újra; a letöltést NEM kell megismételni.");
            }
          }finally{
            registrationPending=false;
            if(active)setBusy(false);
          }
          return;
        }
        setJob(state);
      }catch{
        if(active)setError("A natív SteamCMD állapota nem olvasható. Ellenőrizd a telepítést a következő művelet előtt.");
      }
    };
    void poll();
    const interval=window.setInterval(()=>{void poll();},2000);
    return ()=>{active=false;window.clearInterval(interval);};
  },[job?.jobId,job?.registered,job?.verified,job?.errorCode,onRegistered]);

  const invalidate=()=>{setError("");setMessage("");};
  const chooseFolder=async()=>{
    if(busy||running)return;
    setBusy(true);setError("");
    try{
      const result=await managerBridge.freshBrowse();
      if(result.selected && result.installRoot) {
        setInstallRoot(result.installRoot);
        setRecoveryPreview(null);setRecoveryPhrase("");
        setMessage("Célmappa kiválasztva. Az ASA letöltését az alábbi zöld gomb indítja.");
      }
    }catch{
      setError("A Windows-mappaválasztó nem érhető el. A teljes elérési utat kézzel is megadhatod.");
    }finally{setBusy(false);}
  };
  const previewCompletedRecovery=async()=>{
    if(busy||running)return;
    if(!installRoot.trim()||!resolvedMapName){
      setError("Add meg a befejezett telepítés mapnevét és teljes helyi mappáját.");
      return;
    }
    setBusy(true);invalidate();setRecoveryPreview(null);setRecoveryPhrase("");
    try{
      const proof=await managerBridge.freshRecoverPreview(resolvedMapName,
        installRoot.trim(),mapName==="__custom__"?customLevel.trim():undefined);
      setRecoveryPreview(proof);
      setMessage("A befejezett SteamCMD-napló, az ASA EXE és az appmanifest ellenőrizve. "+
        "Nem történt új letöltés vagy regisztráció. A mapazonosító megőrzéséhez külön jóváhagyás szükséges.");
    }catch(e){
      const code=e instanceof Error?e.message:"UM-FRESH-RECOVERY-NOT-VERIFIED";
      const detail:Record<string,string>={
        "UM-FRESH-RECOVERY-UNPINNED-COMPLETION":
          "A régi befejezett telepítési napló nem tartalmaz SHA-256 bizonyítékot. "+
          "A szervert a meglévő ASA-könyvtár kézi importálásával veheted át.",
        "UM-FRESH-RECOVERY-COMPLETED-FILES-CHANGED":
          "Az ASA EXE vagy a Steam appmanifest eltér a befejezéskor ellenőrzött "+
          "fájltól. Automatikus átvétel tilos; ellenőrizd a telepítést.",
        "UM-FRESH-RECOVERY-NO-COMPLETE-JOURNAL":
          "Nincs befejezett SteamCMD-napló ehhez a mappához. A megszakadt "+
          "letöltést a külön tudatos folytatással kezeld.",
        "UM-FRESH-RECOVERY-ALREADY-REGISTERED":
          "Ez a map vagy telepítési mappa már szerepel a Manager profiljában."
      };
      setError(detail[code]??("Befejezett ASA-telepítés nem vehető át ("+
        code+"). A meglévő fájlokat nem módosítottuk."));
    }finally{setBusy(false);}
  };
  const registerCompletedRecovery=async()=>{
    if(busy||!recoveryPreview||recoveryPhrase!=="ASA TELEPÍTÉS ÁTVÉTEL")return;
    const proof=recoveryPreview;
    setBusy(true);invalidate();setRecoveryPreview(null);setRecoveryPhrase("");
    try{
      const restored=await managerBridge.freshRecoverRegister(
        proof.token,"ASA TELEPÍTÉS ÁTVÉTEL");
      jobMutationGeneration.current++;
      autoRegisterId.current=null;
      setJob({
        jobId:restored.jobId,mapId:restored.mapId,mapName:restored.mapName,
        stage:"registered",detail:"Korábban ellenőrzött ASA-fájlok regisztrálva",
        percent:100,verified:true,registered:true,errorCode:null,
        evidence:"NATIVE_STEAMCMD_JOB_STATE"
      });
      setRegisteredMapId(restored.mapId);setFirstConfigurationSaved(false);
      setMessage(restored.mapName+
        " eredeti mapazonosítóval regisztrálva, letöltés és szerverindítás nélkül. "+
        "Folytasd az első konfigurációval.");
      try{await onRegistered();}catch{
        setError("A map átvétele sikerült, de a szerverlista frissítése nem. "+
          "Ne ismételd a regisztrációt; nyisd meg újra a Szerverek oldalt.");
      }
    }catch(e){
      setError("A befejezett telepítés regisztrációja nem sikerült ("+
        (e instanceof Error?e.message:"UM-FRESH-RECOVERY-FAILED")+
        "). A már telepített ASA-fájlokat nem módosítottuk.");
    }finally{setBusy(false);}
  };

  const startDownload=async(e:FormEvent)=>{
    e.preventDefault();
    if(busy||running)return;
    if(!installRoot.trim()){
      setError("Válassz egy üres, helyi telepítési mappát.");
      return;
    }
    if(mapName==="__custom__" &&
       (!customMapName.trim()||customMapName.trim().length>100||
        !/^[A-Za-z][A-Za-z0-9_]{0,79}$/.test(customLevel.trim()))){
      setError("Egyedi maphoz add meg a megjelenített nevet és a pontos, legfeljebb 80 karakteres Unreal level azonosítót. Nem fogadunk el indítási kapcsolót vagy szóközt.");
      return;
    }
    jobMutationGeneration.current++;
    setRecoveryPreview(null);setRecoveryPhrase("");
    setBusy(true);invalidate();
    try{
      // Missing Valve SteamCMD is downloaded automatically; the clicked
      // Install button itself is explicit consent for the new disk write.
      const checked=await managerBridge.freshPreflight({
        mapName:resolvedMapName,installRoot:installRoot.trim(),
        steamExe:steamExe.trim(),allowSteamCmdDownload:true,explicitRetry,
        customLevel:mapName==="__custom__"?customLevel.trim():undefined
      });
      const started=await managerBridge.freshStart(checked.token);
      autoRegisterId.current=started.jobId;
      setRegisteredMapId(null);setFirstConfigurationSaved(false);
      setJob({jobId:started.jobId,mapName:resolvedMapName,stage:"prepared",
        detail:"Hivatalos Valve SteamCMD és ASA 2430930 előkészítése",
        percent:null,verified:false,registered:false,errorCode:null,
        evidence:"NATIVE_STEAMCMD_JOB_STATE"});
      setMessage("A valódi ASA-letöltés elindult. A szerver nem indul el automatikusan.");
    }catch(e){
      const code=e instanceof Error?e.message:"UM-FRESH-OPERATION-FAILED";
      const messages:Record<string,string>={
        "UM-FRESH-ROOT-NOT-EMPTY":"Ez a mappa nem üres. Válassz egy új, üres mappát; meglévő ASA-szervert a Szerverek oldalon importálj.",
        "UM-FRESH-STEAMCMD-MISSING":"A SteamCMD hiányzik. A hivatalos Valve-letöltés nem volt elérhető.",
        "UM-FRESH-ROOT-OVERLAP":"A mappa ütközik egy már regisztrált ASA- vagy SteamCMD-könyvtárral.",
        "UM-FRESH-RETRY-REVIEW-REQUIRED":"Megszakadt telepítés található. A Speciális beállításokban engedélyezd a tudatos folytatást ugyanazzal a mappával.",
        "UM-FRESH-DUPLICATE-MAP":"Ez a map már szerepel a helyi Managerben.",
        "UM-FRESH-MAP-LEVEL-INVALID":"Az egyedi level azonosító hiányzik, érvénytelen, vagy egy hivatalos map szintjét próbálná felülírni.",
        "UM-FRESH-CUSTOM-LEVEL-INVALID":"Az egyedi level kizárólag 1–80 ASCII betűt, számot és aláhúzást tartalmazhat; betűvel kell kezdődnie.",
        "UM-FRESH-INSTALL-IN-PROGRESS":"Egy korábbi letöltés még folyamatban van vagy regisztrációra vár.",
        "UM-FRESH-LOW-DISK-SPACE":"A telepítéshez legalább 35 GiB szabad hely szükséges a célmeghajtón.",
        "UM-FRESH-INVALID-PATH":"A célmappa útvonala hibás. Ellenőrizd, hogy létező helyi meghajtóra mutat-e."
      };
      const matched=Object.keys(messages).find(k=>code.includes(k));
      const fields=Object.fromEntries(code.split("|").slice(1).map(part=>{
        const at=part.indexOf("=");return at<0?[part,""]:[part.slice(0,at),part.slice(at+1)];
      }));
      const collision=matched==="UM-FRESH-ROOT-OVERLAP"?
        " Ütköző összetevő: "+(fields.map??"ismeretlen")+
        ". Foglalt könyvtár: "+(fields.path??"nincs adat")+
        ". Új telepítéshez válassz ezen kívül külön üres mappát.":"";
      const space=matched==="UM-FRESH-LOW-DISK-SPACE"?
        " Elérhető: "+(fields.availableGiB??"?")+" GiB; szükséges: "+
        (fields.requiredGiB??"35")+" GiB.":"";
      setError((matched?messages[matched]:"A telepítés nem indult el. Natív hiba: "+code)+collision+space);
    }finally{setBusy(false);}
  };

  const cancel=async()=>{
    if(!job)return;
    setBusy(true);setError("");
    try{
      await managerBridge.freshCancel(job.jobId);
      setMessage("Megszakítás kérve. A natív SteamCMD-folyamat leállítását külön ellenőrizzük.");
    }catch{setError("A megszakítást nem sikerült elküldeni. Ellenőrizd az állapotot.");}
    finally{setBusy(false);}
  };
  const register=async()=>{
    if(!job?.verified||job.registered)return;
    setBusy(true);setError("");
    try{
      const receipt=await managerBridge.freshRegister(job.jobId);
      jobMutationGeneration.current++;
      setJob(current=>current?{...current,registered:true,mapId:receipt.mapId,stage:"registered"}:null);
      setRegisteredMapId(receipt.mapId);
      setFirstConfigurationSaved(false);
      setMessage(receipt.mapName+" regisztrálva. Folytasd az alábbi első mapbeállítással, utána a mapadatlap RCON/mod/API/cluster lépéseivel.");
      try {
        await onRegistered();
      } catch {
        setError("A map regisztrációja sikerült, de a szerverlista frissítése nem. Ne próbáld újra regisztrálni; frissítsd a nézetet.");
      }
    }catch(e){
      setError("A Manager nem regisztrálta az ellenőrzött fájlokat ("+
        (e instanceof Error?e.message:"UM-FRESH-OPERATION-FAILED")+").");
    }finally{setBusy(false);}
  };
  const discard=async()=>{
    if(!job||(!job.verified&&!job.errorCode))return;
    setBusy(true);setError("");
    try{
      await managerBridge.freshDiscard(job.jobId);
      setJob(null);setRegisteredMapId(null);setFirstConfigurationSaved(false);
      setMessage("A feladatkövetés lezárult. A fájlok a lemezen maradtak.");
    }catch{setError("A feladatot nem lehet lezárni, amíg SteamCMD írhat a lemezre.");}
    finally{setBusy(false);}
  };

  const running=!!job&&!job.registered&&!job.verified&&!job.errorCode;
  // The map gallery can open the same installer preselected to the clicked map.
  // Do not change the active job or an in-progress download.
  useEffect(()=>{
    if(running||busy)return;
    setMapName(mapNames.includes(initialMapName)?initialMapName:"__custom__");
    setCustomMapName(mapNames.includes(initialMapName)?"":initialMapName);
    setCustomLevel("");
  },[initialMapName]);
  return <section className="glass panel fresh-wizard">
    <div className="panel-head"><h2>Új ASA szerver · SteamCMD telepítés</h2>
      <span className="mod-tag amber">Map → mappa → letöltés</span></div>
    <div className="panel-inner">
      <p className="muted small">Válassz mapot és új, üres helyi mappát, majd indítsd a letöltést.
        A Manager automatikusan beszerzi a hiányzó hivatalos SteamCMD-t, ellenőrzi az ASA-fájlokat, és hozzáadja a mapot.
        Portot, RCON-t és modokat csak később kell megadni.</p>
      <form className="fresh-wizard-form" onSubmit={e=>{void startDownload(e);}}>
        <label>ASA map
          <select aria-label="Új ASA map" value={mapName} disabled={running||busy}
            onChange={e=>{setMapName(e.target.value);setRecoveryPreview(null);setRecoveryPhrase("");invalidate();}}>
            {mapNames.map(map=><option key={map} value={map}>{map}</option>)}
            <option value="__custom__">Egyedi / modolt ASA map</option>
          </select>
        </label>
        {mapName==="__custom__"&&<div className="fresh-advanced">
          <label>Egyedi map neve
            <input aria-label="Egyedi ASA map neve" value={customMapName}
              maxLength={100} disabled={running||busy}
              placeholder="Például: Atlantis"
              onChange={e=>{setCustomMapName(e.target.value);setRecoveryPreview(null);setRecoveryPhrase("");invalidate();}}/>
          </label>
          <label>Pontos Unreal map level
            <input aria-label="Egyedi ASA level azonosító" value={customLevel}
              maxLength={80} disabled={running||busy} spellCheck={false}
              placeholder="A mod készítőjétől kapott tényleges level"
              onChange={e=>{setCustomLevel(e.target.value);setRecoveryPreview(null);setRecoveryPhrase("");invalidate();}}/>
          </label>
          <p className="muted small">A SteamCMD a közös ASA 2430930 szerverfájlokat telepíti.
            Az egyedi pálya modjait később a Mod Centerben kell megadni.
            A level nevét nem találjuk ki automatikusan, és a szervert nem indítjuk el.</p>
        </div>}
        <label>Új, üres helyi telepítési könyvtár
          <span className="fresh-folder-row">
            <input aria-label="Új ASA célmappa" value={installRoot}
              onChange={e=>{setInstallRoot(e.target.value);setRecoveryPreview(null);setRecoveryPhrase("");invalidate();}}
              placeholder="D:\ASA-Servers\TheIsland" maxLength={520} disabled={running||busy}/>
            <button type="button" className="secondary" disabled={running||busy}
              onClick={()=>{void chooseFolder();}}><FolderOpen size={16}/> Mappa kiválasztása</button>
          </span>
        </label>
        <details className="fresh-advanced">
          <summary>Speciális beállítások (általában nem szükségesek)</summary>
          <label>SteamCMD.exe helyi elérési út (opcionális)
            <input aria-label="SteamCMD EXE" value={steamExe}
              onChange={e=>{setSteamExe(e.target.value);invalidate();}}
              placeholder="Üresen: a Manager automatikusan beszerzi" maxLength={520}
              disabled={running||busy}/>
          </label>
          <label><input type="checkbox" checked={explicitRetry} disabled={running||busy}
            onChange={e=>{setExplicitRetry(e.target.checked);invalidate();}}/>
            Korábban megszakadt telepítés tudatos folytatása (ugyanaz a map, mappa, SteamCMD)</label>
        </details>
        <button type="submit" className="primary" disabled={running||busy}>
          <HardDrive size={17}/>{busy?"Előkészítés...":running?"Letöltés folyamatban":"ASA letöltés indítása"}
        </button>
        <button type="button" className="secondary"
          disabled={busy||running||!installRoot.trim()}
          onClick={()=>{void previewCompletedRecovery();}}>
          <Shield size={16}/> Befejezett SteamCMD-telepítés visszavétele (újraindítás után)
        </button>
        {onImport&&<button type="button" className="secondary"
          disabled={busy||running} onClick={onImport}>
          Már telepített ASA-könyvtár importálása
        </button>}
        <p className="muted tiny">A „Letöltés indítása” gomb jóváhagyja az új mappába történő valódi lemezírást.
          A beépített ellenőrzés megállítja a telepítést ütköző, nem üres vagy Store-v1-védett hely esetén.</p>
      </form>
      {recoveryPreview&&<div className="fresh-job" role="region"
        aria-label="Befejezett SteamCMD telepítés jóváhagyása">
        <h3>Ellenőrzött telepítés visszavétele</h3>
        <p>Map: {recoveryPreview.mapName} · Level: {recoveryPreview.mapLevel} ·
          Build: {recoveryPreview.buildId}</p>
        <p className="muted small">A Manager korábbi befejezett telepítési
          naplójából az eredeti map GUID kerül regisztrálásra, új SteamCMD
          futtatás, ASA-indítás, INI- vagy Store-v1-módosítás nélkül.
          A szervernév, portok és RCON külön első konfigurációt igényelnek.</p>
        <label htmlFor="fresh-complete-recovery-approval">
          Jóváhagyás – írd be: ASA TELEPÍTÉS ÁTVÉTEL
        </label>
        <input id="fresh-complete-recovery-approval" type="text"
          aria-label="Befejezett telepítés jóváhagyó szöveg"
          value={recoveryPhrase} autoComplete="off" spellCheck={false}
          onChange={e=>setRecoveryPhrase(e.target.value)}/>
        <button type="button" className="primary"
          disabled={busy||recoveryPhrase!=="ASA TELEPÍTÉS ÁTVÉTEL"}
          onClick={()=>{void registerCompletedRecovery();}}>
          Ellenőrzött ASA-fájlok regisztrálása az eredeti GUID-dal
        </button>
      </div>}
      {job&&<div className="fresh-job">
        <div className="fresh-job-head"><h3>{job.mapName}</h3><span className="mod-tag amber">
          {job.registered?"Regisztrálva":job.verified?"Ellenőrzött ASA-fájlok":job.errorCode?"Megszakadt":"SteamCMD folyamatban"}</span></div>
        <p>{job.detail}</p>
        <div className="fresh-progress">
          {job.percent===null?<span>A SteamCMD nem közölt ellenőrzött százalékot</span>:
          <><progress aria-label="SteamCMD előrehaladás" max={100} value={job.percent}/>
            <span>{job.percent.toFixed(1)}%</span></>}
        </div>
        {job.errorCode&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>
          A telepítés megszakadt ({job.errorCode}). A folytatáshoz ellenőrizd a mappát.</div>}
        <div className="fresh-actions">
          {running&&<button className="secondary" disabled={busy} onClick={()=>{void cancel();}}>
            SteamCMD megszakításának kérése</button>}
          {job.verified&&!job.registered&&<button className="primary" disabled={busy}
            onClick={()=>{void register();}}>Letöltött ASA hozzáadása a Managerhez</button>}
          {!running&&!job.registered&&<button className="secondary" disabled={busy}
            onClick={()=>{void discard();}}>Feladatkövetés lezárása (fájlok megmaradnak)</button>}
        </div>
      </div>}
      {job?.registered && (registeredMapId||job.mapId) && <div
        className="fresh-registered-next" role="region" aria-label="Telepítés utáni ASA konfiguráció">
        <h3>2. Első konfiguráció – natív jóváhagyással</h3>
        <p className="muted small">A szervernév, Game/Query UDP-portok és az egyedi map
          azonosítója itt állítható be. Ez nem indít ASA-folyamatot, és nem
          nyúl a Kova Store-v1 karakteradatokhoz.</p>
        {!firstConfigurationSaved&&<InitialMapSetup
          mapId={registeredMapId||job.mapId!}
          onConfigured={async()=>{setFirstConfigurationSaved(true);
            await onRegistered();}}/>}
        {firstConfigurationSaved&&<div role="status" className="success-inline">
          <CheckCircle2 size={16}/> Az első indítási profil mentve.
          A további lépések a mapadatlap meglévő, natív funkciói.
        </div>}
        <h3>3. RCON, modok, opcionális ASA API, cluster és indítás</h3>
        <p className="muted small">A mapadatlapról folytathatod az INI,
          mod- és pluginbeállítást, az RCON-t és a cluster-tagságot.
          Az ASA Start külön előnézetet és jóváhagyást igényel.</p>
        {onOpenMap&&<button type="button" className="primary"
          onClick={()=>onOpenMap(registeredMapId||job.mapId!)}>
          Mapadatlap megnyitása – további konfiguráció
        </button>}
      </div>}
      {error&&<div role="alert" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      {message&&<div role="status" className="success-inline"><CheckCircle2 size={17}/>{message}</div>}
      <div className="warning-inline"><Shield size={17}/>
        Éles Store-v1 szervert nem módosít; új ASA letöltése kizárólag külön Windows tesztgépen ajánlott.</div>
    </div>
  </section>;
}
