import {useCallback,useEffect,useMemo,useState} from "react";
import {AlertTriangle,CalendarClock,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type ScheduledStoppedSteamOverview} from "./bridge";

const approvalPhrase="AUTOMATIKUS LEÁLLÍTOTT FRISSÍTÉS";

/** An explicit opt-in for STOPPED-only SteamCMD updates, not running lifecycle. */
export function ScheduledStoppedSteamSettings({mapId}:{mapId:string}) {
  const [overview,setOverview]=useState<ScheduledStoppedSteamOverview|null>(null);
  const [interval,setIntervalMinutes]=useState(180);
  const [typed,setTyped]=useState("");
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const reload=useCallback(async()=>{
    const current=await managerBridge.steamScheduledStatus();
    setOverview(current);
    return current;
  },[]);
  useEffect(()=>{
    let active=true;
    setOverview(null);setError("");setNotice("");setTyped("");
    managerBridge.steamScheduledStatus().then(value=>{
      if(active)setOverview(value);
    }).catch(()=>{if(active)setError("A helyi SteamCMD-ütemező nem érhető el.");});
    return ()=>{active=false;};
  },[mapId]);
  const scheduled=useMemo(()=>overview?.schedules.find(s=>s.mapId===mapId),
    [overview,mapId]);
  const change=async(action:"enable"|"disable")=>{
    if(busy||!mapId||typed!==approvalPhrase)return;
    setBusy(true);setError("");setNotice("");
    try{
      if(action==="enable") {
        if(!Number.isInteger(interval)||interval<60||interval>10080)
          throw new Error("UM-STEAM-SCHEDULE-INTERVAL");
        await managerBridge.steamScheduledSet(mapId,interval,typed);
        setNotice("Engedélyezve: kizárólag ellenőrzött új build és igazoltan leállított, nem Store map esetén fut.");
      } else {
        await managerBridge.steamScheduledDisable(mapId,typed);
        setNotice("A következő automatikus SteamCMD-frissítések letiltva.");
      }
      setTyped("");
      await reload();
    }catch(e){
      setError("A natív ütemező elutasította a módosítást ("+
        (e instanceof Error?e.message:"UM-STEAM-SCHEDULE-ERROR")+
        "). Ellenőrizd a helyi process-, Store- és műveleti naplóállapotot.");
    }finally{setBusy(false);}
  };
  return <section className="historical-preview" aria-label="Leállított ASA Steam automatikus frissítése">
    <h3><CalendarClock size={17}/> Automatikus SteamCMD – csak leállított mapon</h3>
    <p className="muted small">Alapból kikapcsolva. A natív, tartós ütemezés kizárólag friss, hiteles
      Steam-build összehasonlítás és igazolt leállítás után indítja a meglévő
      mentés → soros SteamCMD → fájlellenőrzés folyamatot. Futó ASA-t nem állít le
      és nem indít újra. Kova/Store-v1 vagy bizonytalan folyamat esetén blokkol.</p>
    <p role="status"><Shield size={15}/> {scheduled?.enabled?
      "Aktív – következő ellenőrzés: "+new Date(scheduled.nextDueUtc).toLocaleString("hu-HU"):
      scheduled?"Letiltva":"Nincs beállítva"}
      {scheduled&&" · "+scheduled.lastStatus}</p>
    {scheduled?.lastStatus==="needs-operator"&&
      <p className="warning-inline"><AlertTriangle size={16}/>
        Félbeszakadt frissítés: kézi ellenőrzés szükséges. Nincs automatikus újrapróbálkozás.
      </p>}
    <div className="uk-rcon-admin-grid">
      <label>Ellenőrzés gyakorisága (perc)
        <input type="number" min={60} max={10080} step={1}
          aria-label="Automatikus SteamCMD ütemezés perc"
          disabled={busy} value={interval}
          onChange={e=>setIntervalMinutes(Number(e.target.value))}/>
      </label>
      <label>Jóváhagyás (pontosan)
        <input type="text" autoComplete="off"
          aria-label="Automatikus SteamCMD jóváhagyó szöveg"
          placeholder={approvalPhrase} value={typed} disabled={busy}
          onChange={e=>setTyped(e.target.value)}/>
      </label>
    </div>
    <div className="uk-rcon-admin-actions">
      <button type="button" className="secondary"
        disabled={busy||!mapId||typed!==approvalPhrase||
          !Number.isInteger(interval)||interval<60||interval>10080}
        onClick={()=>{void change("enable");}}>Leállított map automatikus frissítése: BE</button>
      <button type="button" className="secondary"
        disabled={busy||!scheduled?.enabled||typed!==approvalPhrase}
        onClick={()=>{void change("disable");}}>Kikapcsolás</button>
      <button type="button" className="quiet" disabled={busy}
        onClick={()=>{void reload().catch(()=>setError("Ütemező nem olvasható."));}}>
        <RefreshCw size={15}/> Állapot frissítése
      </button>
    </div>
    {notice&&<p role="status">{notice}</p>}
    {error&&<p role="alert" className="error-inline">
      <AlertTriangle size={16}/>{error}</p>}
  </section>;
}
