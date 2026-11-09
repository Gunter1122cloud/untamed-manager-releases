import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,ShieldCheck} from "lucide-react";
import {managerBridge,type PublicMap,type LiveSteamPreview,
  type LiveSteamStatus,type LiveSteamCurrent} from "./bridge";

/** Production-only native orchestration of existing confirmed SaveWorld/Stop,
 * SteamCMD protected backup/update and independently checked ASA Start.
 * No fake timer, success animation, or raw remote commands. */
export function LiveSteamMaintenancePanel({maps,mapId}:{
  maps:PublicMap[];mapId:string
}){
  const [preview,setPreview]=useState<LiveSteamPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [current,setCurrent]=useState<LiveSteamCurrent|null>(null);
  const [jobId,setJobId]=useState("");
  const [status,setStatus]=useState<LiveSteamStatus|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  useEffect(()=>{
    setPreview(null);setApproved(false);setError("");
  },[mapId]);
  useEffect(()=>{
    let active=true;
    managerBridge.steamLiveCurrent().then(result=>{
      if(!active)return;
      setCurrent(result);
      if(result.exists&&result.jobId)setJobId(result.jobId);
    }).catch(()=>{
      if(active)setNotice("A futó map frissítési naplója nem olvasható.");
    });
    return()=>{active=false;};
  },[]);
  useEffect(()=>{
    if(!jobId)return;
    let active=true;
    const poll=async()=>{
      try{
        const next=await managerBridge.steamLiveStatus(jobId);
        if(!active)return;
        setStatus(next);
        if(next.state==="completed"||next.state==="needsOperator"){
          setJobId("");
          setCurrent({exists:true,state:next.state,
            stage:next.stage,mapId:next.mapId,jobId:next.jobId});
        }
      }catch{
        if(active){
          setError("A natív munkafolyamat állapotát nem sikerült lekérni. "+
            "Ne indítsd újra automatikusan a Stop vagy a SteamCMD lépést.");
          setJobId("");
        }
      }
    };
    void poll();
    const timer=window.setInterval(()=>{void poll();},2500);
    return()=>{active=false;window.clearInterval(timer);};
  },[jobId]);

  const inspect=async()=>{
    if(!mapId||busy||jobId)return;
    setBusy(true);setApproved(false);setPreview(null);setError("");setNotice("");
    try{setPreview(await managerBridge.steamLivePreview(mapId));}
    catch(e){
      setError("Nem teljesül a natív előellenőrzés. Csak helyi, futó, "+
        "azonosított RCON/PID-ű, nem Store-v1 ASA map választható, "+
        "igazolt SteamCMD és elegendő tárhely mellett. "+
        (e instanceof Error?e.message:""));
    }finally{setBusy(false);}
  };
  const execute=async()=>{
    if(!approved||!preview||busy||jobId)return;
    setBusy(true);setError("");setNotice("");
    const token=preview.token;
    setPreview(null);setApproved(false);
    try{
      const started=await managerBridge.steamLiveApply(token);
      if(!started.started||started.originalStoreTouched||
         !started.localOnly||!started.jobId)
        throw new Error("Érvénytelen natív indulási visszaigazolás");
      setJobId(started.jobId);setStatus(null);
      setNotice("Az ellenőrzött helyi munkafolyamat elindult. "+
        "A siker csak a végső ASA-készenlét után jelenhet meg.");
    }catch(e){
      setError("A natív működtetés nem indult el: "+
        (e instanceof Error?e.message:"ellenőrzés sikertelen"));
    }finally{setBusy(false);}
  };
  const mapped=maps.find(m=>m.id===mapId);
  const running=!!jobId||
    current?.state==="queued"||current?.state==="running";
  return <section aria-label="Futó ASA automatikus SteamCMD karbantartása"
    className="premium-panel" style={{padding:20,marginBlock:18}}>
    <header className="premium-panel-heading">
      <h2><ShieldCheck size={19}/> Futó ASA – SaveWorld, Stop, Update, Start</h2>
    </header>
    <p>A natív Manager sorban végrehajtja az új világmentés ellenőrzését,
      a kíméletes leállítást, a védőmentéses SteamCMD-frissítést és az indítást.
      Csak a valós ASA-készenlét után jelöl sikeresnek. Egyetlen lépést sem
      ismétel automatikusan hiba vagy Manager-újraindítás után.</p>
    <p className="muted small">Kiválasztott helyi map: <b>{mapped?.name??"Nincs kiválasztva"}</b>.
      Az eredeti Kova Store-v1 és a megosztott új Store-mapok nem jogosultak erre
      a műveletre, amíg a közös writer és transfer koherenciája nem igazolt.
      A másik Windows host frissítését ez nem zárolja.</p>
    <div className="draft-actions">
      <button type="button" className="secondary"
        disabled={!mapId||busy||running}
        onClick={()=>{void inspect();}}>
        <RefreshCw size={16}/> Futó map teljes frissítési előellenőrzése
      </button>
    </div>
    {preview&&<div role="region" aria-label="Futó ASA SteamCMD-frissítés jóváhagyása"
      className="uk-rcon-admin-review">
      <b>{preview.mapName} · PID {preview.pid} · Build {preview.installedBuild}</b>
      <p>Egyszeri jóváhagyás, sorrend: {preview.stages.join(" → ")}.</p>
      <p>A SaveWorld napló- és világfájl-bizonyítéka, az igazolt DoExit,
        a SteamCMD utáni fájlintegritás és az új ASA-készenlét kötelező.
        Ha egy lépés hibás, a szerver akár leállítva maradhat; nincs
        kényszerkilövés vagy automatikus újrapróbálás.</p>
      <label><input type="checkbox" checked={approved}
        disabled={busy} onChange={e=>setApproved(e.target.checked)}/>
        Jóváhagyom ennek a helyi, futó mapnak a teljes SaveWorld–Stop–Update–Start
        műveletsorát és az esetleges leállítva maradást hiba esetén.</label>
      <div className="draft-actions">
        <button type="button" className="primary"
          disabled={!approved||busy||running}
          onClick={()=>{void execute();}}>Teljes frissítés indítása</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>{setPreview(null);setApproved(false);}}>Mégsem</button>
      </div>
    </div>}
    {status&&<div className={status.state==="completed"&&status.productionReady?
        "uk-rcon-admin-success":"warning-inline"} role="status">
      {status.state==="completed"&&status.productionReady?
        <CheckCircle2 size={18}/>:<ShieldCheck size={18}/>}
      <span>{status.state} · {status.stage} · Stop: {status.stopVerified?"OK":"nem igazolt"}
        {" "}· SteamCMD: {status.steamUpdateVerified?"OK":"nem igazolt"}
        {" "}· Start: {status.startRequested?"elindítva":"nem igazolt"}
        {" "}· ASA készenlét: {status.productionReady?"igazolt":"nem igazolt"}</span>
      {status.state==="needsOperator"&&<p role="alert">Operátori ellenőrzés szükséges.
        A napló megmarad; a Manager nem ismétli meg a Stop, SteamCMD vagy Start
        műveleteket.</p>}
    </div>}
    {!status&&current?.exists&&<p role="status">
      Korábbi Manager-napló: {current.state} · {current.stage}.
      A korábban rögzített lépés nem bizonyítja a szerver jelenlegi állapotát.
    </p>}
    {notice&&<p role="status" className="muted small">{notice}</p>}
    {error&&<p role="alert" className="error-inline">
      <AlertTriangle size={16}/>{error}
    </p>}
  </section>;
}
