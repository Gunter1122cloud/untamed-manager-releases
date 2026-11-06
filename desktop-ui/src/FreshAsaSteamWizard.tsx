import { useEffect, useRef, useState, type FormEvent } from "react";
import { AlertTriangle, CheckCircle2, FolderOpen, HardDrive, Shield } from "lucide-react";
import { managerBridge, type FreshInstallJob } from "./bridge";

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
export function FreshAsaSteamWizard({onRegistered}:{onRegistered:()=>Promise<void>}) {
  const [mapName,setMapName]=useState("The Island");
  const [installRoot,setInstallRoot]=useState("");
  const [steamExe,setSteamExe]=useState("");
  const [explicitRetry,setExplicitRetry]=useState(false);
  const [job,setJob]=useState<FreshInstallJob|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [message,setMessage]=useState("");
  const autoRegisterId=useRef<string|null>(null);

  useEffect(()=>{
    let active=true;
    managerBridge.freshCurrent().then(data=>{
      if(active && data.current)setJob(data.current);
    }).catch(()=>{});
    return ()=>{active=false;};
  },[]);

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
            if(!active)return;
            setJob({...state,registered:true,stage:"registered"});
            setMessage(receipt.mapName+" letöltve és hozzáadva a Managerhez. A portok, RCON és modok később állíthatók be.");
            await onRegistered();
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
        setMessage("Célmappa kiválasztva. Az ASA letöltését az alábbi zöld gomb indítja.");
      }
    }catch{
      setError("A Windows-mappaválasztó nem érhető el. A teljes elérési utat kézzel is megadhatod.");
    }finally{setBusy(false);}
  };
  const startDownload=async(e:FormEvent)=>{
    e.preventDefault();
    if(busy||running)return;
    if(!installRoot.trim()){
      setError("Válassz egy üres, helyi telepítési mappát.");
      return;
    }
    setBusy(true);invalidate();
    try{
      // Missing Valve SteamCMD is downloaded automatically; the clicked
      // Install button itself is explicit consent for the new disk write.
      const checked=await managerBridge.freshPreflight({
        mapName,installRoot:installRoot.trim(),steamExe:steamExe.trim(),
        allowSteamCmdDownload:true,explicitRetry
      });
      const started=await managerBridge.freshStart(checked.token);
      autoRegisterId.current=started.jobId;
      setJob({jobId:started.jobId,mapName,stage:"prepared",
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
        "UM-FRESH-INSTALL-IN-PROGRESS":"Egy korábbi letöltés még folyamatban van vagy regisztrációra vár.",
        "UM-FRESH-LOW-DISK-SPACE":"A telepítéshez legalább 35 GiB szabad hely szükséges a célmeghajtón.",
        "UM-FRESH-INVALID-PATH":"A célmappa útvonala hibás. Ellenőrizd, hogy létező helyi meghajtóra mutat-e."
      };
      const matched=Object.keys(messages).find(k=>code.includes(k));
      setError(matched?messages[matched]:"A telepítés nem indult el. Natív hiba: "+code);
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
      setJob(current=>current?{...current,registered:true,stage:"registered"}:null);
      setMessage(receipt.mapName+" regisztrálva. Az indítás, portok, RCON és modok külön konfigurálhatók.");
      await onRegistered();
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
      setJob(null);
      setMessage("A feladatkövetés lezárult. A fájlok a lemezen maradtak.");
    }catch{setError("A feladatot nem lehet lezárni, amíg SteamCMD írhat a lemezre.");}
    finally{setBusy(false);}
  };

  const running=!!job&&!job.registered&&!job.verified&&!job.errorCode;
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
            onChange={e=>{setMapName(e.target.value);invalidate();}}>
            {mapNames.map(map=><option key={map} value={map}>{map}</option>)}
          </select>
        </label>
        <label>Új, üres helyi telepítési könyvtár
          <span className="fresh-folder-row">
            <input aria-label="Új ASA célmappa" value={installRoot}
              onChange={e=>{setInstallRoot(e.target.value);invalidate();}}
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
        <p className="muted tiny">A „Letöltés indítása” gomb jóváhagyja az új mappába történő valódi lemezírást.
          A beépített ellenőrzés megállítja a telepítést ütköző, nem üres vagy Store-v1-védett hely esetén.</p>
      </form>
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
      {error&&<div role="alert" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      {message&&<div role="status" className="success-inline"><CheckCircle2 size={17}/>{message}</div>}
      <div className="warning-inline"><Shield size={17}/>
        Éles Store-v1 szervert nem módosít; új ASA letöltése kizárólag külön Windows tesztgépen ajánlott.</div>
    </div>
  </section>;
}
