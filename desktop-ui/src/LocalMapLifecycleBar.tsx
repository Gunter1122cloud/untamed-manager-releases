import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,Play,RotateCcw,Octagon,Skull,Shield} from "lucide-react";
import {managerBridge} from "./bridge";
import "./local-map-lifecycle.css";

type Action="start"|"restart"|"stop"|"kill";
type Approval={action:Action;token:string;mapName:string;pid?:number};
type RunningJob={action:"restart"|"stop";id:string};
const copy:Record<Action,string>={start:"Indítás",restart:"Újraindítás",stop:"Leállítás",kill:"Kill"};
const phrase:Record<Action,string>={start:"STORE-V1 ACTIVE START",restart:"STORE-V1 SAVEWORLD DOEXIT START",stop:"STORE-V1 SAVEWORLD DOEXIT",kill:"KILL LOCAL ASA"};

/** Compact command surface using the EXISTING native lifecycle bridges.
 * RCON, PID, journal and stored-character permissions stay in native C#.
 * Historical Store-v1 maps always use their own ACTIVE/SaveWorld/DoExit flow. */
export function LocalMapLifecycleBar({mapId,mapName,running,historical,
  journalBlocked,onRefresh,onConfigure}:{
  mapId:string;mapName:string;running:boolean;historical:boolean;
  journalBlocked?:boolean;onRefresh:()=>Promise<void>;onConfigure:()=>void;
}){
  const [pending,setPending]=useState<Approval|null>(null);
  const [checked,setChecked]=useState(false);
  const [typed,setTyped]=useState("");
  const [busy,setBusy]=useState(false);
  const [ownJournalBlocked,setOwnJournalBlocked]=useState(true);
  const [job,setJob]=useState<RunningJob|null>(null);
  const [message,setMessage]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    if(journalBlocked!==undefined)return;
    let active=true;
    setOwnJournalBlocked(true);
    const check=async()=>{
      try{
        const lifecycle=await managerBridge.asaLifecycleJournal(mapId);
        const restart=historical?null:await managerBridge.freshAsaRestartCurrent(mapId);
        if(active)setOwnJournalBlocked(Boolean(lifecycle.needsOperatorReview||
          restart?.needsOperatorReview));
      }catch{if(active)setOwnJournalBlocked(true);}
    };
    void check();
    return ()=>{active=false;};
  },[mapId,historical,journalBlocked]);
  const blocked=journalBlocked??ownJournalBlocked;
  useEffect(()=>{
    setPending(null);setChecked(false);setTyped("");setError("");setMessage("");
    setJob(null);
  },[mapId]);
  useEffect(()=>{
    if(!job)return;
    let alive=true;
    const poll=async()=>{
      try{
        const data=job.action==="stop"
          ?historical?await managerBridge.historicalGracefulStopStatus(job.id)
            :await managerBridge.freshAsaStopStatus(job.id)
          :historical?await managerBridge.historicalRestartStatus(job.id)
            :await managerBridge.freshAsaRestartStatus(job.id);
        if(!alive)return;
        if(data.state==="completed"){
          setJob(null);
          setMessage(copy[job.action]+" – a natív művelet befejeződött. A játékbeli készenlét és Store-utazás külön ellenőrzendő.");
          void onRefresh();
        }else if(data.state==="needsOperator"){
          setJob(null);
          setError(copy[job.action]+" megszakadt: a natív műveleti napló kézi ellenőrzést igényel. Automatikus ismétlés nincs.");
        }
      }catch(e){
        if(alive){
          setJob(null);
          setError("A natív "+copy[job.action]+" státusza nem igazolható ("+
            (e instanceof Error?e.message:"ismeretlen")+"). Ellenőrizd a naplót, ne ismételd meg automatikusan.");
        }
      }
    };
    void poll();
    const timer=window.setInterval(()=>{void poll();},2500);
    return ()=>{alive=false;window.clearInterval(timer);};
  },[job,historical,onRefresh]);

  const preview=async(action:Action)=>{
    if(busy||job||blocked)return;
    setBusy(true);setPending(null);setError("");setMessage("");
    setChecked(false);setTyped("");
    try{
      if(action==="kill"&&historical){
        setError("Az eredeti eredeti Store-v1 map kényszerített Kill művelete itt tiltott: a közös writer és a karakteradatok megsérülhetnek. Használd a natív SaveWorld/DoExit leállítást.");
        return;
      }
      const data=action==="start"
        ?historical?await managerBridge.historicalActiveStartPreview(mapId)
          :await managerBridge.freshAsaStartPreview(mapId)
        :action==="restart"
        ?historical?await managerBridge.historicalRestartPreview(mapId)
          :await managerBridge.freshAsaRestartPreview(mapId)
        :action==="stop"
        ?historical?await managerBridge.historicalGracefulStopPreview(mapId)
          :await managerBridge.freshAsaStopPreview(mapId)
        :await managerBridge.ownedAsaKillPreview(mapId);
      setPending({action,token:data.token,mapName:data.mapName,
        pid:"pid" in data&&typeof data.pid==="number"?data.pid:undefined});
    }catch(e){
      setError(copy[action]+" előellenőrzés elutasítva: "+
        (e instanceof Error?e.message:"nem ellenőrizhető")+
        ". Ellenőrizd a helyi konfigurációt, a naplót, a PID-et, az RCON-t és a portokat.");
    }finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!pending||!checked||busy||job||(historical||pending.action==="kill")&&typed!==phrase[pending.action])
      return;
    const selected=pending;
    setPending(null);setBusy(true);setError("");setMessage("");
    try{
      const started=selected.action==="start"
        ?historical?await managerBridge.historicalActiveStartApply(selected.token,phrase.start)
          :await managerBridge.freshAsaStartApply(selected.token)
        :selected.action==="stop"
        ?historical?await managerBridge.historicalGracefulStopApply(selected.token,phrase.stop)
          :await managerBridge.freshAsaStopApply(selected.token)
        :selected.action==="restart"
        ?historical?await managerBridge.historicalRestartApply(selected.token,phrase.restart)
          :await managerBridge.freshAsaRestartApply(selected.token)
        :await managerBridge.ownedAsaKillApply(selected.token,phrase.kill);
      if(selected.action==="start"){
        setMessage("Indítási PID létrejött. Az ASA/Loader gyermekfolyamat és az RCON készenléte külön vizsgálandó.");
        await onRefresh();
      }else if(selected.action==="kill"){
        if(!("confirmedStopped" in started)||!started.confirmedStopped)
          throw new Error("UM-KILL-STOP-UNVERIFIED");
        setMessage("Kizárólag a bizonyított helyi ASA PID megszűnt. Kill nem mentett világot – ellenőrizd a mentéseket.");
        await onRefresh();
      }else {
        if(!("jobId" in started))throw new Error("UM-LIFECYCLE-JOB-ID-MISSING");
        setJob({action:selected.action,id:started.jobId});
        setMessage(copy[selected.action]+" natív feladat elindítva; folyamatban lévő állapot lekérdezése...");
      }
    }catch(e){
      setError(copy[selected.action]+" nem sikerült: "+(e instanceof Error?e.message:"nem igazolt")+
        ". Nincs automatikus ismétlés; ellenőrizd a natív naplót.");
    }finally{setBusy(false);}
  };
  const unavailable=busy||Boolean(job)||blocked;
  return <section className="uk-lifecycle-command" aria-label="Helyi map életciklus vezérlése">
    <div className="uk-lifecycle-row" role="group" aria-label={mapName+" alapvezérlők"}>
      {(["start","restart","stop","kill"] as Action[]).map(action=>{
        const impossible=action==="start"?running:
          action==="kill"?historical||!running:!running;
        return <button type="button" key={action}
          className={"uk-lifecycle-btn uk-lifecycle-"+action}
          disabled={unavailable||impossible} onClick={()=>{void preview(action);}}
          title={action==="kill"&&historical?"Eredeti Store-v1 mapokon tiltott: használj SaveWorld/DoExit-et":
            blocked?"Műveleti napló felülvizsgálata szükséges":
            impossible?"A jelenlegi folyamatállapotban nem engedélyezett":copy[action]+" natív előellenőrzés"}>
          {action==="start"?<Play size={15}/>:action==="restart"?<RotateCcw size={15}/>:
            action==="stop"?<Octagon size={15}/>:<Skull size={15}/>}
          {copy[action]}
        </button>;
      })}
    </div>
    {blocked&&<p role="alert" className="warning-inline"><Shield size={15}/>
      A műveleti napló nincs igazolva vagy kézi ellenőrzést igényel. Nem indul új ASA-művelet.</p>}
    {pending&&<div role="region" className="uk-lifecycle-approval"
      aria-label={copy[pending.action]+" jóváhagyása"}>
      <b>{pending.mapName} · {copy[pending.action]} előellenőrzés sikeres</b>
      <p>{pending.action==="kill"
        ?"FIGYELEM: a Kill nem küld SaveWorld parancsot, és elveszhet a legutóbbi nem mentett világállapot."
        :pending.action==="restart"||pending.action==="stop"
        ?"A valódi natív művelet mentésbizonyítékot és pontos ASA PID-t ellenőriz a DoExit előtt."
        :"A natív indítás a saját helyi ASA/Store-v1 profilból történik; nem bizonyítja a beléphetőséget."}</p>
      {pending.pid&&<p>Igazolt PID: {pending.pid}</p>}
      {(historical||pending.action==="kill")&&<label>Gépeld be: <b>{phrase[pending.action]}</b>
        <input aria-label="Életciklus művelet jóváhagyó szövege"
          value={typed} onChange={e=>setTyped(e.target.value)} disabled={busy}/>
      </label>}
      <label><input type="checkbox" checked={checked} disabled={busy}
        onChange={e=>setChecked(e.target.checked)}/>
        Jóváhagyom a kiválasztott helyi szerveren a tényleges művelet végrehajtását.</label>
      <div className="uk-lifecycle-row">
        <button type="button" className="primary" disabled={busy||!checked||
          (historical||pending.action==="kill")&&typed!==phrase[pending.action]}
          onClick={()=>{void apply();}}>Művelet végrehajtása</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>{setPending(null);setTyped("");}}>Mégsem</button>
      </div>
    </div>}
    {job&&<p role="status" className="warning-inline">{copy[job.action]} folyamatban – natív állapot lekérdezése...</p>}
    {message&&<p role="status" className="success-inline"><CheckCircle2 size={16}/>{message}</p>}
    {error&&<div role="alert" className="warning-inline"><AlertTriangle size={16}/>{error}
      <button type="button" className="secondary" onClick={onConfigure}>
        Beállítások megnyitása
      </button></div>}
  </section>;
}
