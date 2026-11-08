import {useCallback,useEffect,useState} from "react";
import {AlertTriangle,CalendarClock,CheckCircle2,RefreshCw,Save,Shield,RotateCcw} from "lucide-react";
import {managerBridge,type NativeAsaScheduleStatus,
  type NativeScheduleFrequency,type NativeScheduleKind} from "./bridge";
import "./native-schedule-fidelity.css";

const days=[
  {name:"V",key:0},{name:"H",key:1},{name:"K",key:2},{name:"Sze",key:3},
  {name:"Cs",key:4},{name:"P",key:5},{name:"Szo",key:6}
];
function localDate(value:string|null|undefined){
  if(!value)return "Nem ellenőrizhető";
  const date=new Date(value);
  return Number.isNaN(date.getTime())?"Nem ellenőrizhető":
    date.toLocaleString("hu-HU",{timeZone:"Europe/Budapest"});
}
type Draft={
  kind:NativeScheduleKind;frequency:NativeScheduleFrequency;
  minutes:number;clock:string;daysMask:number;
};
function parseClock(raw:string){
  const match=/^([01]\d|2[0-3]):([0-5]\d)$/.exec(raw);
  return match?Number(match[1])*60+Number(match[2]):-1;
}
function clockOf(minute:number){
  return String(Math.floor(minute/60)).padStart(2,"0")+":"+
    String(minute%60).padStart(2,"0");
}
function defaultDraft(kind:NativeScheduleKind):Draft{
  return {kind,frequency:kind==="Restart"?"Daily":"Interval",
    minutes:120,clock:kind==="Restart"?"04:00":"03:00",daysMask:127};
}
function ScheduleEditor({mapId,kind,status,onChanged}:{
  mapId:string;kind:NativeScheduleKind;status:NativeAsaScheduleStatus;
  onChanged:()=>Promise<void>;
}){
  const existing=status.jobs.find(j=>j.kind===kind);
  const [draft,setDraft]=useState<Draft>(()=>defaultDraft(kind));
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [message,setMessage]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    setDraft(existing?{
      kind,frequency:existing.frequency,minutes:existing.intervalMinutes,
      clock:clockOf(existing.minuteOfDay),daysMask:existing.daysMask
    }:defaultDraft(kind));
    setApproved(false);setMessage("");setError("");
  },[mapId,kind,existing?.nextDueUtc,existing?.enabled]);
  const valid=parseClock(draft.clock)>=0 && draft.daysMask>0 &&
    (draft.frequency!=="Interval"||
      Number.isInteger(draft.minutes)&&draft.minutes>=15&&draft.minutes<=10080);
  const change=async(enable:boolean)=>{
    if(!approved||busy||!valid||status.protectedStore)return;
    setBusy(true);setMessage("");setError("");
    try{
      if(enable){
        await managerBridge.nativeScheduleSet(mapId,kind,draft.frequency,
          draft.minutes,parseClock(draft.clock),draft.daysMask);
      }else await managerBridge.nativeScheduleDisable(mapId,kind);
      setApproved(false);
      await onChanged();
      setMessage(enable?"A map ütemezése tartósan mentve.":"A map automatikus feladata kikapcsolva.");
    }catch(e){
      setError("A natív Manager nem fogadta el az ütemezést. "+
        (e instanceof Error?e.message:"Ellenőrizd a map állapotát."));
    }finally{setBusy(false);}
  };
  return <section className="native-schedule-editor" aria-label={kind==="SaveWorld"?"SaveWorld ütemezése":"Újraindítás ütemezése"}>
    <header>
      <h3>{kind==="SaveWorld"?<Save size={18}/>:<RotateCcw size={18}/>}
        {kind==="SaveWorld"?"Automatikus SaveWorld":"Automatikus újraindítás"}</h3>
      <span className={existing?.enabled?"native-schedule-on":"native-schedule-off"}>
        {existing?.enabled?"Bekapcsolva":"Kikapcsolva"}</span>
    </header>
    <div className="native-schedule-summary">
      <div><span>Következő végrehajtás</span><strong>{existing?.enabled?localDate(existing.nextDueUtc):"Nincs beállítva"}</strong></div>
      <div><span>Utolsó végrehajtás</span><strong>{existing?.lastClaimedUtc?localDate(existing.lastClaimedUtc):"Még nem futott"}</strong></div>
      {existing?.lastClaimedUtc&&<p role="status">{existing.lastStatus} · {existing.lastEvidence||"Nincs független játékon belüli igazolás"}</p>}
    </div>
    <div className="native-schedule-controls">
      {kind==="SaveWorld"&&<label>Gyakoriság
        <select aria-label="SaveWorld gyakoriság" value={draft.frequency}
          disabled={busy||status.protectedStore}
          onChange={e=>setDraft(old=>({...old,frequency:e.target.value as NativeScheduleFrequency}))}>
          <option value="Interval">Adott percenként</option><option value="Daily">Megadott napokon</option>
        </select>
      </label>}
      {draft.frequency==="Interval"&&kind==="SaveWorld"&&<label>Ismétlés (perc)
        <input aria-label="SaveWorld gyakorisága percben" type="number" min={15} max={10080}
          step={5} value={draft.minutes} disabled={busy||status.protectedStore}
          onChange={e=>setDraft(old=>({...old,minutes:Number(e.target.value)}))}/>
      </label>}
      {draft.frequency==="Daily"&&<label>Időpont (Budapest)
        <input aria-label={kind==="SaveWorld"?"SaveWorld időpont":"Újraindítás időpont"}
          type="time" value={draft.clock} disabled={busy||status.protectedStore}
          onChange={e=>setDraft(old=>({...old,clock:e.target.value}))}/>
      </label>}
      {draft.frequency==="Daily"&&<fieldset className="native-schedule-days">
        <legend>Napok</legend>
        {days.map(d=><label key={d.key} className={(draft.daysMask&(1<<d.key))?"selected":""}>
          <input type="checkbox" aria-label={d.name+" nap"} disabled={busy||status.protectedStore}
            checked={Boolean(draft.daysMask&(1<<d.key))}
            onChange={e=>setDraft(old=>({...old,
              daysMask:e.target.checked?old.daysMask|(1<<d.key):old.daysMask&~(1<<d.key)
            }))}/>
          {d.name}
        </label>)}
      </fieldset>}
    </div>
    {kind==="Restart"&&status.restartConflicts.length>0&&<p className="native-schedule-warning">
      <AlertTriangle size={16}/> Azonos helyi időpont más mapokkal: {status.restartConflicts.join(", ")}.
      A natív végrehajtás sorban történik. Ez csak figyelmeztetés.</p>}
    <label className="native-schedule-consent">
      <input type="checkbox" checked={approved} disabled={busy||status.protectedStore}
        onChange={e=>setApproved(e.target.checked)}/>
      Kifejezetten jóváhagyom ennek a mapnak az automatikus {kind==="SaveWorld"?"RCON SaveWorld küldését":"SaveWorld → DoExit → indítás műveletét"}.
    </label>
    <div className="native-schedule-buttons">
      <button type="button" className="primary" disabled={!approved||!valid||busy||status.protectedStore}
        onClick={()=>{void change(true);}}>{busy?"Mentés...":"Ütemezés mentése"}</button>
      <button type="button" className="secondary" disabled={!approved||!existing?.enabled||busy||status.protectedStore}
        onClick={()=>{void change(false);}}>Kikapcsolás</button>
    </div>
    {error&&<p className="error-inline" role="alert">{error}</p>}
    {message&&<p className="success-inline" role="status"><CheckCircle2 size={15}/>{message}</p>}
  </section>;
}
export function NativeMapScheduler({mapId}:{mapId:string}){
  const [status,setStatus]=useState<NativeAsaScheduleStatus|null>(null);
  const [error,setError]=useState("");
  const [busy,setBusy]=useState(false);
  const reload=useCallback(async()=>{
    setBusy(true);
    try {setStatus(await managerBridge.nativeScheduleStatus(mapId));setError("");}
    catch(e){setError("A map natív ütemezése nem olvasható: "+
      (e instanceof Error?e.message:"ismeretlen hiba"));setStatus(null);}
    finally{setBusy(false);}
  },[mapId]);
  useEffect(()=>{void reload();},[reload]);
  return <section className="native-schedule-panel">
    <div className="native-schedule-heading">
      <h2><CalendarClock size={19}/> Maponkénti szerverütemező</h2>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>{void reload();}}><RefreshCw size={15}/> Frissítés</button>
    </div>
    <p className="muted small">A feladatok a helyi Windows Managerben futnak. Csak előzetesen jóváhagyott, új, nem Store-v1 ASA telepítésen aktiválhatók. Kimaradt vagy megszakadt műveletet nem indítanak újra automatikusan.</p>
    {error&&<p role="alert" className="error-inline">{error}</p>}
    {!status&&!error&&<p>Helyi ütemezések ellenőrzése...</p>}
    {status?.protectedStore&&<p className="native-schedule-warning"><Shield size={17}/>
      Store-v1 vagy történeti karakteradat-védelem: automatikus RCON és restart tiltva.</p>}
    {status&&<div className="native-schedule-grid">
      <ScheduleEditor key={mapId+"-save"} mapId={mapId} kind="SaveWorld" status={status} onChanged={reload}/>
      <ScheduleEditor key={mapId+"-restart"} mapId={mapId} kind="Restart" status={status} onChanged={reload}/>
    </div>}
    {status&&<p className="native-schedule-foot">
      Ütemezőmotor: {status.runnerState}. Az újraindítás Windows PID-je igazolható,
      de az ASA játékbeli visszacsatlakozás és az operációs rendszer szintű fájlflush külön teszt.
      Automatikus SteamCMD-frissítés még nem engedélyezett ezen a natív útvonalon.
    </p>}
  </section>;
}
