import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,RotateCcw,Shield} from "lucide-react";
import {managerBridge,type FreshAsaRestartPreview,type FreshAsaRestartStatus} from "./bridge";

const latestRestart=new Map<string,string>();

export function FreshAsaRestartPanel({mapId,running,journalBlocked,onRestarted}:{
  mapId:string;running:boolean;journalBlocked:boolean;
  onRestarted:()=>Promise<void>
}){
  const [preview,setPreview]=useState<FreshAsaRestartPreview|null>(null);
  const [busy,setBusy]=useState(false);
  const [approved,setApproved]=useState(false);
  const [jobId,setJobId]=useState<string|null>(null);
  const [status,setStatus]=useState<FreshAsaRestartStatus|null>(null);
  const [message,setMessage]=useState(()=>latestRestart.get(mapId)??"");
  const [error,setError]=useState("");
  const [needsLocalReview,setNeedsLocalReview]=useState(false);
  useEffect(()=>{
    setPreview(null);setApproved(false);setError("");
    setNeedsLocalReview(false);
    setMessage(latestRestart.get(mapId)??"");
  },[mapId]);
  useEffect(()=>{
    if(!jobId)return;
    let active=true;
    const poll=async()=>{
      try{
        const state=await managerBridge.freshAsaRestartStatus(jobId);
        if(!active)return;
        setStatus(state);
        if(state.state==="completed"){
          setJobId(null);
          const summary=state.newProcessRole==="loaderParent"?
            "ASA API Loaderrel végzett újraindítás: régi ASA PID "+state.oldPid+
              " → új Loader szülő PID "+state.newPid+
              ". Az ASA gyermekfolyamat, a játék/RCON készenlét és a lemezmentés külön igazolandó.":
            "Normál ASA újraindítás befejeződött: "+
              state.oldPid+" → "+state.newPid+
              ". A játék/RCON készenlét és a lemezre mentés független bizonyítása külön ellenőrzést igényel.";
          latestRestart.set(mapId,summary);
          setMessage(summary);
          onRestarted().catch(()=>{});
        }else if(state.state==="needsOperator"){
          setJobId(null);setNeedsLocalReview(true);
          setError("A szerver újraindítása félbeszakadt. Automatikus ismétlés nem történt. Ellenőrizd a Windows-folyamatot és a natív műveleti naplót.");
        }
      }catch{
        if(active){
          setJobId(null);setNeedsLocalReview(true);
          setError("Nem érhető el a natív újraindítási feladat. Ellenőrizd a helyi naplót új művelet előtt.");
        }
      }
    };
    void poll();
    const timer=window.setInterval(()=>{void poll();},2500);
    return ()=>{active=false;window.clearInterval(timer);};
  },[jobId,mapId,onRestarted]);

  const check=async()=>{
    if(!mapId||!running||busy||jobId||journalBlocked||needsLocalReview)return;
    setBusy(true);setError("");setMessage("");setPreview(null);setApproved(false);
    try{setPreview(await managerBridge.freshAsaRestartPreview(mapId));}
    catch{setError("Az újraindítás előellenőrzése sikertelen. Csak saját, RCON-nal azonosított, nem Store-v1 ASA folyamat engedélyezett, a Managerrel egyező GameUserSettings.ini konfigurációval.");}
    finally{setBusy(false);}
  };
  const run=async()=>{
    if(!preview||!approved||busy||jobId||journalBlocked||needsLocalReview)return;
    const token=preview.token;
    setBusy(true);setPreview(null);setApproved(false);setError("");
    try{
      const job=await managerBridge.freshAsaRestartApply(token);
      setJobId(job.jobId);
      setStatus({
        jobId:job.jobId,state:"queued",stage:"preflight",
        oldPid:preview.currentPid,newPid:0,changedUtc:new Date().toISOString(),
        saveCompleteProven:false,readinessCertified:false,
        preExitSaveEvidenceObserved:false,finalDoExitSaveCertified:false,
        gameClientRejoinVerified:false,
        killPerformed:false,storeTouched:false,autoRetry:false
      });
    }catch{setError("A helyi újraindítás nem indult el. A szerverállapot megváltozhatott.");}
    finally{setBusy(false);}
  };

  return <section className="premium-panel" aria-label="Valódi ASA normál újraindítása" style={{padding:20,marginBlock:18}}>
    <header className="premium-panel-heading">
      <h2><RotateCcw size={19}/> ASA normál újraindítása</h2>
    </header>
    <p>A C# Manager a régi szerver SaveWorld naplójelzését és stabil .ark mentését ellenőrzi, majd DoExit után egyszer indítja el az új folyamatot. Hiányzó mentési bizonyítéknál nincs DoExit vagy automatikus ismétlés. Csak saját, helyi normál vagy újonnan regisztrált Store-mapokhoz; a történeti Kova Store-mapok külön védett műveleteket használnak.</p>
    <button type="button" className="secondary"
      disabled={!running||busy||!!jobId||journalBlocked||needsLocalReview}
      onClick={()=>{void check();}}><RefreshCw size={16}/> Újraindítás előellenőrzése</button>
    {preview&&<div className="historical-preview" role="region" aria-label="ASA újraindítás jóváhagyása">
      <h3>{preview.mapName} – jelenlegi PID: {preview.currentPid}</h3>
      <p>A rendszer mentési parancsot küld és szabályosan kilépteti a szervert. A folyamatkilépés nem független SaveComplete-bizonyíték.</p>
      <label className="historical-approval">
        <input type="checkbox" checked={approved}
          onChange={e=>setApproved(e.target.checked)} disabled={busy}/>
        Jóváhagyom a helyi ASA szerver tényleges leállítását és újraindítását.
      </label>
      <button type="button" className="primary" disabled={!approved||busy||journalBlocked||needsLocalReview}
        onClick={()=>{void run();}}>Szerver újraindítása</button>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>setPreview(null)}>Mégsem</button>
    </div>}
    {status&&<div className="warning-inline" role="status"><Shield size={17}/>
      <span>Újraindítás: {status.state} · {status.stage}</span>
    </div>}
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{message}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
    <p className="muted tiny">A Manager nem végez kényszerített Kill-t, nem módosít karakter- vagy Store-v1-fájlokat, és hiba után nem indít másodszor automatikusan.</p>
  </section>;
}
