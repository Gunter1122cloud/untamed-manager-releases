import { useEffect, useState, type FormEvent } from "react";
import { AlertTriangle, CheckCircle2, HardDrive, Shield } from "lucide-react";
import { managerBridge, type FreshInstallPreflight, type FreshInstallJob } from "./bridge";

const mapNames = [
  "The Island","The Center","Scorched Earth","Aberration","Extinction",
  "Genesis Part 1","Valguero","Ragnarok","Astraeos","Lost Colony"
];

export function FreshAsaSteamWizard({onRegistered}:{onRegistered:()=>Promise<void>}) {
  const [mapName,setMapName]=useState("The Island");
  const [installRoot,setInstallRoot]=useState("");
  const [steamExe,setSteamExe]=useState("");
  const [downloadSteam,setDownloadSteam]=useState(false);
  const [explicitRetry,setExplicitRetry]=useState(false);
  const [approval,setApproval]=useState(false);
  const [preview,setPreview]=useState<FreshInstallPreflight | null>(null);
  const [job,setJob]=useState<FreshInstallJob | null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [message,setMessage]=useState("");

  useEffect(()=>{
    let mounted=true;
    managerBridge.freshCurrent().then(data=>{
      if(mounted && data.current)setJob(data.current);
    }).catch(()=>{});
    return ()=>{mounted=false;};
  },[]);

  useEffect(()=>{
    if(!job || job.registered || job.verified || !!job.errorCode)return;
    let mounted=true;
    const interval=window.setInterval(()=>{
      managerBridge.freshStatus(job.jobId).then(current=>{
        if(mounted)setJob(current);
      }).catch(()=>{
        if(mounted)setError("A natív SteamCMD állapota nem olvasható. Nem állítjuk, hogy a telepítés befejeződött.");
      });
    },2000);
    return ()=>{mounted=false;window.clearInterval(interval);};
  },[job?.jobId,job?.registered,job?.verified,job?.errorCode]);

  const invalidate=()=>{setPreview(null);setApproval(false);setError("");setMessage("");};
  const check=async(e:FormEvent)=>{
    e.preventDefault();invalidate();setBusy(true);
    try{
      const result=await managerBridge.freshPreflight({
        mapName,installRoot,steamExe,
        allowSteamCmdDownload:downloadSteam,explicitRetry
      });
      setPreview(result);
    }catch(e){
      const code=e instanceof Error?e.message:"UM-FRESH-OPERATION-FAILED";
      const messages:Record<string,string>={
        "UM-FRESH-ROOT-NOT-EMPTY":"A mappa nem üres. Meglévő ASA-szervert a Szerverek oldalon importálj.",
        "UM-FRESH-STEAMCMD-MISSING":"Hiányzik a steamcmd.exe. Választhatod a hivatalos Valve letöltést.",
        "UM-FRESH-ROOT-OVERLAP":"A mappa ütközik egy meglévő ASA vagy SteamCMD könyvtárral.",
        "UM-FRESH-RETRY-REVIEW-REQUIRED":"Megszakadt napló található. Azonos mappa és SteamCMD mellett engedélyezd a tudatos folytatást.",
        "UM-FRESH-DUPLICATE-MAP":"A map már regisztrálva van a helyi Managerben.",
        "UM-FRESH-INSTALL-IN-PROGRESS":"Az előző telepítés még fut vagy regisztrációra vár."
      };
      setError(messages[code]??"A natív telepítés-előellenőrzés elutasította a műveletet ("+code+").");
    }finally{setBusy(false);}
  };
  const start=async()=>{
    if(!preview || !approval)return;
    setBusy(true);setError("");setMessage("");
    try {
      const started=await managerBridge.freshStart(preview.token);
      setPreview(null);setApproval(false);
      setJob({jobId:started.jobId,mapName,stage:"prepared",
        detail:"ASA SteamCMD előkészítése",percent:null,
        verified:false,registered:false,errorCode:null,
        evidence:"NATIVE_STEAMCMD_JOB_STATE"});
    }catch(e){
      setError("A natív SteamCMD nem indulhatott el ("+
        (e instanceof Error?e.message:"UM-FRESH-OPERATION-FAILED")+").");
    }finally{setBusy(false);}
  };
  const cancel=async()=>{
    if(!job)return;
    setBusy(true);setError("");
    try{
      await managerBridge.freshCancel(job.jobId);
      setMessage("Megszakítás kérve. A natív SteamCMD állapotellenőrzése továbbra is aktív.");
    }catch{setError("A megszakítási kérelem elutasítva. Ellenőrizd a natív állapotot.");}
    finally{setBusy(false);}
  };
  const register=async()=>{
    if(!job?.verified || job.registered)return;
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
    if(!job || (!job.verified && !job.errorCode))return;
    setBusy(true);setError("");
    try{
      await managerBridge.freshDiscard(job.jobId);
      setJob(null);setMessage("A feladat nyomon követése lezárult. A fájlok változatlanul megmaradtak.");
    }catch{setError("A feladatot nem lehet lezárni, amíg SteamCMD írhat a lemezre.");}
    finally{setBusy(false);}
  };
  const running=!!job && !job.registered && !job.verified && !job.errorCode;
  return <section className="glass panel fresh-wizard">
    <div className="panel-head"><h2>Új ASA szerver · SteamCMD telepítés</h2>
      <span className="mod-tag amber">Letöltés → ellenőrzés → beállítás</span></div>
    <div className="panel-inner">
      <p className="muted small">Valódi, hivatalos ASA 2430930 telepítés új, üres helyi könyvtárba. A telepítés NEM indítja el a szervert, és még nem kér portot, RCON-t vagy modokat.</p>
      <form className="fresh-wizard-form" onSubmit={e=>{void check(e);}}>
        <label>ASA map
          <select aria-label="Új ASA map" value={mapName} disabled={running}
            onChange={e=>{setMapName(e.target.value);invalidate();}}>
            {mapNames.map(map=><option key={map} value={map}>{map}</option>)}
          </select>
        </label>
        <label>Új, üres helyi telepítési könyvtár
          <input aria-label="Új ASA célmappa" value={installRoot}
            onChange={e=>{setInstallRoot(e.target.value);invalidate();}}
            placeholder="D:\ASA-Servers\TheIsland" maxLength={520} disabled={running} required/>
        </label>
        <label>SteamCMD.exe helyi elérési út
          <input aria-label="SteamCMD EXE" value={steamExe}
            onChange={e=>{setSteamExe(e.target.value);invalidate();}}
            placeholder="D:\UntamedTools\SteamCMD\steamcmd.exe" maxLength={520} disabled={running} required/>
        </label>
        <div className="fresh-options">
          <label><input type="checkbox" checked={downloadSteam} disabled={running}
            onChange={e=>{setDownloadSteam(e.target.checked);invalidate();}}/>
            Hiányzó SteamCMD letöltése a Valve hivatalos szerveréről</label>
          <label><input type="checkbox" checked={explicitRetry} disabled={running}
            onChange={e=>{setExplicitRetry(e.target.checked);invalidate();}}/>
            Megszakadt telepítés tudatos folytatása (ugyanaz a map, mappa, SteamCMD)</label>
        </div>
        <button type="submit" className="secondary" disabled={running||busy||!installRoot.trim()||!steamExe.trim()}>
          <Shield size={16}/> {busy?"Ellenőrzés...":"Letöltés előtti ellenőrzés"}
        </button>
      </form>
      {preview && !running && <div className="fresh-preview">
        <b><CheckCircle2 size={20}/>{preview.mapName} · {preview.mapLevel}</b>
        <p>Mappa: {preview.folderName}. SteamCMD: {preview.steamCmdPresent?"megtalálva":"hivatalos letöltés szükséges"}.</p>
        <p>Legalább {preview.minimumFreeGiB} GiB szabad helyet ellenőriz a natív telepítő. Az ASA később külön konfigurálható.</p>
        <label><input type="checkbox" checked={approval} onChange={e=>setApproval(e.target.checked)}/>
          Jóváhagyom a valódi SteamCMD-lemezműveletet a megadott új könyvtárban.</label>
        <button className="primary" disabled={!approval||busy} onClick={()=>{void start();}}>
          <HardDrive size={16}/> ASA 2430930 letöltése és ellenőrzése
        </button>
      </div>}
      {job && <div className="fresh-job">
        <div className="fresh-job-head"><h3>{job.mapName}</h3><span className="mod-tag amber">
          {job.registered?"Regisztrálva":job.verified?"Igazolt fájlok":job.errorCode?"Megszakadt":"SteamCMD folyamatban"}</span></div>
        <p>{job.detail}</p>
        <div className="fresh-progress">
          {job.percent===null?<span>Nem érkezett hiteles százalékos előrehaladás</span>:
          <><progress aria-label="SteamCMD előrehaladás" max={100} value={job.percent}/>
            <span>{job.percent.toFixed(1)}%</span></>}
        </div>
        {job.errorCode && <div className="error-inline" role="alert"><AlertTriangle size={17}/>
          A telepítés nem igazolt ({job.errorCode}). Az újrapróbáláshoz ellenőrizd a megszakadt telepítést.</div>}
        <div className="fresh-actions">
          {running && <button className="secondary" disabled={busy} onClick={()=>{void cancel();}}>
            SteamCMD megszakításának kérése</button>}
          {job.verified&&!job.registered && <button className="primary" disabled={busy} onClick={()=>{void register();}}>
            Ellenőrzött ASA regisztrálása a Managerbe</button>}
          {!running&&!job.registered && <button className="secondary" disabled={busy} onClick={()=>{void discard();}}>
            Feladatkövetés lezárása (fájlok maradnak)</button>}
        </div>
      </div>}
      {error && <div role="alert" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      {message && <div role="status" className="success-inline"><CheckCircle2 size={17}/>{message}</div>}
      <div className="warning-inline"><Shield size={17}/>
        Csak külön Windows-tesztgépen használd a migráció alatt. A telepítő nem módosít meglévő mapot, világmentést vagy Store-t.</div>
    </div>
  </section>;
}
