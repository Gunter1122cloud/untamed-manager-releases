import {useCallback,useEffect,useRef,useState} from "react";
import {AlertTriangle,CalendarClock,CheckCircle2,RefreshCw,Save,Shield,RotateCcw} from "lucide-react";
import {managerBridge,type NativeAsaScheduleStatus,
  type NativeScheduleFrequency,type NativeScheduleKind,
  type NativeScheduleReviewPreview,type NativeAsaExecutionEntry} from "./bridge";
import "./native-schedule-fidelity.css";

const days=[
  {name:"V",key:0},{name:"H",key:1},{name:"K",key:2},{name:"Sze",key:3},
  {name:"Cs",key:4},{name:"P",key:5},{name:"Szo",key:6}
];
const displayEventStatus:Record<string,string>={
  "executed-no-fsync-or-client-certification":"A natív művelet lefutott (játékbeli igazolás nélkül)",
  "failed-needs-operator-no-retry":"Sikertelen – kézi ellenőrzés szükséges",
  "interrupted-needs-operator":"Megszakadt – nincs automatikus újrapróbálás",
  "config-or-store-blocked":"Konfiguráció vagy Store-védelem miatt kihagyva",
  "claimed-no-auto-retry":"Végrehajtás lefoglalva – még nincs eredmény",
  "missed-no-catchup":"Kimaradt időpont – nincs utólagos végrehajtás",
  "manager-start-skipped-no-replay":"Manager újraindításakor kihagyva",
  "skipped-blocked-prior-unknown-slot":"Kihagyva: régi bizonytalan slot még felülvizsgálandó",
  "blocked-prior-unknown-slot":"Régi bizonytalan slot miatt blokkolva",
  "operator-reviewed-unknown-no-replay":"Operátor felülvizsgálta: kimenetel továbbra is ismeretlen",
  "skipped-overlapping-restart-wipe":"Kihagyva: ugyanebben a ciklusban restart utáni wipe esedékes"
};
function eventStatus(raw:string){return displayEventStatus[raw]??raw;}

function localDate(value:string|null|undefined){
  if(!value)return "Nem ellenőrizhető";
  const date=new Date(value);
  return Number.isNaN(date.getTime())?"Nem ellenőrizhető":
    date.toLocaleString("hu-HU",{timeZone:"Europe/Budapest"});
}
function kindLabel(kind:NativeScheduleKind){return kind==="SaveWorld"?"SaveWorld":kind==="WildWipe"?"Dinowipe":"Újraindítás";}
type Draft={
  kind:NativeScheduleKind;frequency:NativeScheduleFrequency;
  minutes:number;clock:string;daysMask:number;postRestartWipe:boolean;wipeDelaySeconds:number;
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
    minutes:120,clock:kind==="Restart"?"04:00":"03:00",daysMask:127,postRestartWipe:false,wipeDelaySeconds:0};
}
function ScheduleEditor({mapId,kind,status,onChanged}:{
  mapId:string;kind:NativeScheduleKind;status:NativeAsaScheduleStatus;
  onChanged:()=>Promise<void>;
}){
  const existing=status.jobs.find(j=>j.kind===kind);
  const [draft,setDraft]=useState<Draft>(()=>defaultDraft(kind));
  const canConfigure=status.canConfigure??!status.protectedStore;
  const [wipeConsent,setWipeConsent]=useState(false);
  const actionInFlight=useRef(false);
  const [busy,setBusy]=useState(false);
  const [message,setMessage]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    setDraft(existing?{
      kind,frequency:existing.frequency,minutes:existing.intervalMinutes,
      clock:clockOf(existing.minuteOfDay),daysMask:existing.daysMask,postRestartWipe:!!existing.postRestartWipe,wipeDelaySeconds:existing.wipeDelaySeconds??0
    }:defaultDraft(kind));
    setMessage("");setError("");setWipeConsent(false);
  },[mapId,kind,existing?.nextDueUtc,existing?.enabled]);
  const valid=parseClock(draft.clock)>=0 && draft.daysMask>0 &&
    (!draft.postRestartWipe||Number.isInteger(draft.wipeDelaySeconds)&&draft.wipeDelaySeconds>=0&&draft.wipeDelaySeconds<=600) &&
    (draft.frequency==="Once"?[1,2].includes(draft.minutes):draft.frequency!=="Interval"||
      Number.isInteger(draft.minutes)&&draft.minutes>=15&&draft.minutes<=10080);
  const change=async(enable:boolean)=>{
    if(actionInFlight.current||busy||(enable&&(!valid||!canConfigure||(kind==="WildWipe"&&!wipeConsent))))return;
    actionInFlight.current=true;
    setBusy(true);setMessage("");setError("");
    try{
      if(enable){
        await managerBridge.nativeScheduleSet(mapId,kind,draft.frequency,
          draft.minutes,parseClock(draft.clock),draft.daysMask,draft.postRestartWipe,draft.postRestartWipe?draft.wipeDelaySeconds:0);
      }else await managerBridge.nativeScheduleDisable(mapId,kind);
      await onChanged();
      setMessage(enable?"A map ütemezése tartósan mentve.":"A map automatikus feladata kikapcsolva.");
    }catch(e){
      setError("A natív Manager nem fogadta el az ütemezést. "+
        (e instanceof Error?e.message:"Ellenőrizd a map állapotát."));
    }finally{actionInFlight.current=false;setBusy(false);}
  };
  return <section className="native-schedule-editor" aria-label={kindLabel(kind)+" ütemezése"}>
    <header>
      <h3>{kind==="SaveWorld"?<Save size={18}/>:<RotateCcw size={18}/>}
        {kind==="SaveWorld"?"Automatikus SaveWorld":kind==="WildWipe"?"Önálló automatikus dinowipe":"Automatikus újraindítás"}</h3>
      <span className={existing?.enabled?"native-schedule-on":"native-schedule-off"}>
        {existing?.enabled?"Bekapcsolva":"Kikapcsolva"}</span>
    </header>
    <div className="native-schedule-summary">
      <div><span>Következő végrehajtás</span><strong>{existing?.lastStatus==="claimed-no-auto-retry"?"Folyamatban":existing?.enabled?localDate(existing.nextDueUtc):"Nincs beállítva"}</strong></div>
      <div><span>Utolsó végrehajtás</span><strong>{existing?.lastClaimedUtc?localDate(existing.lastClaimedUtc):"Még nem futott"}</strong></div>
      {existing?.lastClaimedUtc&&<p role="status">{existing.lastStatus} · {existing.lastEvidence||"Nincs független játékon belüli igazolás"}</p>}
    </div>
    <div className="native-schedule-controls">
      <label>Gyakoriság
        <select aria-label={kindLabel(kind)+" gyakoriság"} value={draft.frequency}
          disabled={busy||!canConfigure}
          onChange={e=>setDraft(old=>({...old,frequency:e.target.value as NativeScheduleFrequency,
            minutes:e.target.value==="Once"?2:e.target.value==="Interval"?Math.max(15,old.minutes):old.minutes}))}>
          {kind!=="Restart"&&<option value="Interval">Adott percenként</option>}<option value="Daily">Megadott napokon</option>
          <option value="Once">Egyszeri próba</option>
        </select>
      </label>
      {draft.frequency==="Once"&&<label>Első és egyetlen esedékesség
        <select aria-label="Egyszeri próba késleltetése" value={draft.minutes} disabled={busy||!canConfigure}
          onChange={e=>setDraft(old=>({...old,minutes:Number(e.target.value)}))}>
          <option value={1}>1 perc múlva</option><option value={2}>2 perc múlva</option>
        </select>
      </label>}
      {draft.frequency==="Interval"&&kind!=="Restart"&&<label>Ismétlés (perc)
        <input aria-label={kindLabel(kind)+" gyakorisága percben"} type="number" min={15} max={10080}
          step={5} value={draft.minutes} disabled={busy||!canConfigure}
          onChange={e=>setDraft(old=>({...old,minutes:Number(e.target.value)}))}/>
      </label>}
      {draft.frequency==="Daily"&&<label>Időpont (Budapest)
        <input aria-label={kindLabel(kind)+" időpont"}
          type="time" value={draft.clock} disabled={busy||!canConfigure}
          onChange={e=>setDraft(old=>({...old,clock:e.target.value}))}/>
      </label>}
      {draft.frequency==="Daily"&&<fieldset className="native-schedule-days">
        <legend>Napok</legend>
        {days.map(d=><label key={d.key} className={(draft.daysMask&(1<<d.key))?"selected":""}>
          <input type="checkbox" aria-label={d.name+" nap"} disabled={busy||!canConfigure}
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
    {kind==="Restart"&&status.historicalSchedulingSupported&&<label className="native-schedule-consent">
      <input type="checkbox" checked={draft.postRestartWipe} disabled={busy||!canConfigure}
        onChange={e=>setDraft(old=>({...old,postRestartWipe:e.target.checked}))}/>
      Újraindítás után vad dinók törlése (DestroyWildDinos) – csak az új saját ASA PID és játékosfogadási napló igazolása után
    </label>}
    {kind==="Restart"&&status.historicalSchedulingSupported&&draft.postRestartWipe&&<label>Dinowipe késleltetése a készenlét után (másodperc)
      <input type="number" min={0} max={600} step={15} aria-label="Dinowipe késleltetése másodpercben" disabled={busy||!canConfigure}
        value={draft.wipeDelaySeconds} onChange={e=>setDraft(old=>({...old,wipeDelaySeconds:Number(e.target.value)}))}/>
    </label>}
    {kind==="WildWipe"&&<label className="native-schedule-consent"><input type="checkbox" checked={wipeConsent} disabled={busy||!canConfigure} onChange={e=>setWipeConsent(e.target.checked)}/> Jóváhagyom a vad dinók automatikus törlését a kiválasztott helyi mapon.</label>}
    <p className="muted small">A mentés a kijelölt map automatikus {kind==="SaveWorld"?"SaveWorld":kind==="WildWipe"?"DestroyWildDinos":"SaveWorld → DoExit → Start"} feladatát aktiválja. {draft.frequency==="Once"?"Az egyszeri próba nem ismétlődik.":""}</p>
    <div className="native-schedule-buttons">
      <button type="button" className="primary" disabled={!valid||busy||!canConfigure||(kind==="WildWipe"&&!wipeConsent)}
        onClick={()=>{void change(true);}}>{busy?"Mentés...":"Ütemezés mentése"}</button>
      <button type="button" className="secondary" disabled={!existing?.enabled||busy}
        onClick={()=>{void change(false);}}>Kikapcsolás</button>
    </div>
    {error&&<p className="error-inline" role="alert">{error}</p>}
    {message&&<p className="success-inline" role="status"><CheckCircle2 size={15}/>{message}</p>}
  </section>;
}
export function NativeMapScheduler({mapId}:{mapId:string}){
  const [status,setStatus]=useState<NativeAsaScheduleStatus|null>(null);
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const [error,setError]=useState("");
  const [busy,setBusy]=useState(false);
  const [reviewing,setReviewing]=useState(false);
  const [reviewPreview,setReviewPreview]=useState<NativeScheduleReviewPreview|null>(null);
  const [reviewNotice,setReviewNotice]=useState("");
  const reviewSlot=async(entry:NativeAsaExecutionEntry)=>{
    if(reviewing||busy)return;
    const target=mapId;
    setReviewing(true);setReviewNotice("");setReviewPreview(null);
    try{
      const proof=await managerBridge.nativeScheduleReviewPreview(
        target,entry.kind,entry.runId);
      if(target!==currentMap.current)return;
      if(proof.mapId.toLowerCase()!==target.toLowerCase()||
         proof.runId!==entry.runId||!proof.outcomeStillUnknown||
         !proof.scheduledTaskWillBeDisabled||!proof.originalRunWillNotExecute)
        throw new Error("UM-ASA-SCHEDULE-REVIEW-INVALID-PREVIEW");
      setReviewPreview(proof);
    }catch(e){
      if(target===currentMap.current)setError("A bizonytalan ütemezési slot még nem rendezhető: "+
        (e instanceof Error?e.message:"Ismeretlen, csak olvasható ellenőrzési hiba"));
    }finally{if(target===currentMap.current)setReviewing(false);}
  };
  const approveSlot=async()=>{
    if(reviewing||busy||!reviewPreview)return;
    const target=mapId,proof=reviewPreview;
    setReviewing(true);setReviewPreview(null);setReviewNotice("");
    try{
      const receipt=await managerBridge.nativeScheduleReviewApply(target,proof.token);
      if(target!==currentMap.current)return;
      if(receipt.mapId.toLowerCase()!==target.toLowerCase()||
         receipt.runId!==proof.runId||!receipt.outcomeStillUnknown||
         !receipt.noRconSent||!receipt.noAutomaticReplay||
         !receipt.oldJournalArchived||!receipt.jobDisabled)
        throw new Error("UM-ASA-SCHEDULE-REVIEW-INVALID-RECEIPT");
      await reload();
      setReviewNotice("A régi slot kimenetele ISMERETLEN marad. "+
        "A napló auditálva, az adott feladat kikapcsolva. "+
        "Nincs parancsismétlés; későbbi aktiváláshoz új jóváhagyás kell.");
    }catch(e){
      if(target===currentMap.current)setError("A naplófelülvizsgálat eredménye nem igazolható. "+
        "Frissítsd a helyi státuszt; ne indítsd újra a régi parancsot. "+
        (e instanceof Error?e.message:""));
    }finally{if(target===currentMap.current)setReviewing(false);}
  };
  const resumeSupervisor=async()=>{
    if(reviewing||busy||status?.supportedNextAction!=="schedule.nativeResume")return;
    const target=mapId;
    setReviewing(true);setReviewNotice("");
    try{
      const receipt=await managerBridge.nativeScheduleResume(target);
      if(currentMap.current!==target)return;
      if(!receipt.supervisorResumed||receipt.oldSlotReplayed||
         receipt.oldTaskReenabled||!receipt.nextTaskRequiresExplicitOptIn)
        throw new Error("UM-ASA-SCHEDULE-RESUME-RECEIPT-NOT-VERIFIED");
      await reload();
      setReviewNotice("A helyi ütemező újra felügyel, de a régi slotot nem pótolta. "+
        "A feladatok új aktiválásához külön hozzájárulás szükséges.");
    }catch(e){
      if(currentMap.current===target)setError("Az ütemező nem indítható újra biztonságosan: "+
        (e instanceof Error?e.message:"A tartós napló még nincs rendezve."));
    }finally{if(currentMap.current===target)setReviewing(false);}
  };
  const reload=useCallback(async()=>{
    setBusy(true);
    try {
      const fresh=await managerBridge.nativeScheduleStatus(mapId);
      if(currentMap.current!==mapId)return;
      setStatus(fresh);setError("");
    }catch(e){
      if(currentMap.current===mapId){
        setError("A map natív ütemezése nem olvasható: "+
          (e instanceof Error?e.message:"ismeretlen hiba"));setStatus(null);
      }
    }finally{if(currentMap.current===mapId)setBusy(false);}
  },[mapId]);
  useEffect(()=>{
    setReviewPreview(null);setReviewNotice("");
    void reload();
  },[reload]);
  return <section className="native-schedule-panel">
    <div className="native-schedule-heading">
      <h2><CalendarClock size={19}/> Maponkénti szerverütemező</h2>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>{void reload();}}><RefreshCw size={15}/> Frissítés</button>
    </div>
    <p className="muted small">A feladatok a helyi Windows Managerben futnak. Bizonyított saját ASA telepítésen és ellenőrzött, átvett Store-v1 mapon aktiválhatók. Kimaradt vagy megszakadt műveletet nem indítanak újra automatikusan.</p>
    {error&&<p role="alert" className="error-inline">{error}</p>}
    {!status&&!error&&<p>Helyi ütemezések ellenőrzése...</p>}
    {status?.protectedStore&&!status.historicalSchedulingSupported&&<p className="native-schedule-warning"><Shield size={17}/>
      Az átvett Store-v1 map helyi forrása vagy RCON-beállítása nem igazolt az ütemezőhöz.</p>}
    {status?.historicalSchedulingSupported&&<p className="muted small">Megőrzött Store-v1: támogatott helyi ütemezés, a közös cluster változatlan marad.</p>}
    {status?.blockedScope!=="none"&&status?.blockedScope&&
      <p className="native-schedule-warning" role="status">
        <AlertTriangle size={16}/> Blokkolás hatóköre: {status.blockedScope==="task"?
          "csak a korábbi bizonytalan feladat-slot":
          "teljes helyi ütemező felügyelet"}.
        Következő ellenőrzött lépés: {status.supportedNextAction}.
        Az eredeti slotot nem ismételjük meg automatikusan.
      </p>}
    {status?.supportedNextAction==="schedule.nativeResume"&&
      <button type="button" className="secondary" disabled={busy||reviewing}
        onClick={()=>{void resumeSupervisor();}}>
        <Shield size={16}/> Helyi ütemező felügyeletének újraindítása – régi slot nélkül
      </button>}
    {reviewNotice&&<p className="success-inline" role="status">
      <CheckCircle2 size={16}/>{reviewNotice}</p>}
    {reviewPreview&&<div className="warning-inline" role="region"
      aria-label="Ütemezett bizonytalan művelet operátori jóváhagyása">
      <AlertTriangle size={16}/>
      <div><b>{reviewPreview.kind} · {reviewPreview.runId.slice(0,8)}</b>
        <p>Az eredeti parancs kimenetele ISMERETLEN marad.
          Csak a napló változatlan előzménye kerül archiválásra;
          az adott feladat kikapcsolódik, nem lesz RCON, Stop, Restart
          vagy automatikus ismétlés.</p>
        <button type="button" className="primary" disabled={reviewing||busy}
          onClick={()=>{void approveSlot();}}>Régi slot naplórendezésének jóváhagyása</button>
        <button type="button" className="secondary" disabled={reviewing||busy}
          onClick={()=>setReviewPreview(null)}>Mégsem</button>
      </div>
    </div>}
    {status&&<div className="native-schedule-grid">
      <ScheduleEditor key={mapId+"-save"} mapId={mapId} kind="SaveWorld" status={status} onChanged={reload}/>
      <ScheduleEditor key={mapId+"-restart"} mapId={mapId} kind="Restart" status={status} onChanged={reload}/>
      <ScheduleEditor key={mapId+"-wipe"} mapId={mapId} kind="WildWipe" status={status} onChanged={reload}/>
    </div>}
    {status&&<section className="native-schedule-history" aria-label="Végrehajtási napló">
      <details>
        <summary>Végrehajtási napló ({status.history.length} bejegyzés)</summary>
        <p className="muted small">A legutóbbi 100 maponkénti esemény. A natív napló a Manager újraindítása után is megmarad. A kimaradt feladatokat nem pótolja automatikusan.</p>
        {status.history.length===0?<p className="muted">Még nincs végrehajtási esemény ezen a mapon.</p>:
          <div className="native-schedule-history-list">
            {status.history.map(entry=><article key={entry.runId} className="native-schedule-history-event">
              <strong>{kindLabel(entry.kind)} · {eventStatus(entry.status)}</strong>
              <span>Tervezett: {localDate(entry.scheduledUtc)} · Észlelt: {localDate(entry.claimedUtc)}</span>
              <span>Lezárva: {localDate(entry.finishedUtc)} · Azonosító: {entry.runId.slice(0,8)}</span>
              {entry.evidence&&<small>Natív igazolás: {entry.evidence}</small>}
              {status.unknownRunIds.includes(entry.runId)&&
                <button type="button" className="secondary"
                  disabled={busy||reviewing||!!reviewPreview}
                  onClick={()=>{void reviewSlot(entry);}}>
                  <Shield size={14}/> Ismeretlen kimenetelű slot felülvizsgálata
                </button>}
            </article>)}
          </div>}
      </details>
    </section>}
    {status&&<p className="native-schedule-foot">
      Ütemezőmotor: {status.runnerState}. Az újraindítás Windows PID-je igazolható,
      de az ASA játékbeli visszacsatlakozás és az operációs rendszer szintű fájlflush külön teszt.
      Automatikus SteamCMD-frissítés nem engedélyezett. Csak külön jóváhagyott kézi frissítés futtatható.
    </p>}
  </section>;
}
