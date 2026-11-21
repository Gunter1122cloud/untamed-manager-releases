import {useEffect,useRef,useState} from "react";
import {managerBridge,type WatchdogRecoveryMap} from "./bridge";
const reasons:Record<string,string>={INTENTIONAL_STOP_WAITING_TRUSTED_START:"Szándékos Stop: indítsd el a mapot a natív Start gombbal; készültség után visszaélesedik.",OPERATOR_EXTERNAL_MAINTENANCE_NO_AUTOSTART:"A külső karbantartás után, futó és kész mapnál élesítsd újra.",DIFFERENT_PROCESS_FAMILY_REARM_REQUIRED:"Ismeretlen folyamatcsere. Ellenőrizd a folyamatot, majd élesítsd újra.",NO_RETAINED_EXIT_HANDLE_NO_AUTOSTART:"A Manager nem látta a régi kilépést. Ellenőrizd a szervert; automatikus Start tiltva.",RECOVERY_COOLDOWN_NO_CRASH_LOOP:"Ismételt crash 30 percen belül. Vizsgáld meg a szerverlogot.",RECOVERY_READINESS_TIMEOUT_NO_RETRY:"A készültség 12 percen belül nem igazolható. Ellenőrizd az ASA/API konzolt és a portokat.",RECOVERY_START_UNCERTAIN_NO_RETRY:"Az indítás eredménye bizonytalan. Ellenőrizd a natív naplót és a futó folyamatokat.",MAINTENANCE_OR_PROFILE_CHANGE:"A beállítások vagy a karbantartási állapot megváltozott. Ellenőrizd és élesítsd újra.",RECOVERY_NATIVE_LANE_BUSY:"Másik natív művelet vagy ütemező foglalja a gépet. Nincs új Start.",RECOVERY_STEAM_HOST_BUSY:"SteamCMD telepít vagy frissít. A helyreállítás vár.",RECOVERY_OLD_FAMILY_NOT_STOPPED:"A régi ASA/Loader még nem igazoltan állt le.",RECOVERY_OLD_FAMILY_NOT_STOPPED_NO_START:"A régi folyamatcsalád nem állt le teljesen. Ellenőrizd a folyamatokat és portokat.",RECOVERY_FATAL_EXIT_WAITING_FULL_STOP:"Igazolt végzetes kilépés; a teljes leállás ellenőrzése folyamatban."};
const actions:Record<string,string>={
 READY_MAP_REQUIRED_TO_ENABLE:"Először igazold a helyi map futását és készültségét, majd engedélyezd a felügyeletet.",
 WAIT_FOR_THREE_FRESH_STOP_SAMPLES:"Várj a három különálló, friss folyamatvizsgálatra; nincs szükség külön indításra.",
 NO_ACTION_PROTECTION_ACTIVE:"A crash-védelem aktív, nincs teendő.",
 DO_NOT_RETRY_NATIVE_START:"Tartós indítási foglalás aktív. Ne indíts kézzel még egy példányt.",
 WAIT_FOR_TRUSTED_NATIVE_START_AND_READINESS:"A szándékos Stop után csak a következő támogatott natív Start és igazolt készültség élesíthet vissza.",
 FINISH_EXTERNAL_MAINTENANCE_THEN_ARM_ON_READY_MAP:"Karbantartás után futó, kész mapon külön élesítsd újra a felügyeletet.",
 REVIEW_LISTENERS_RCON_AND_CURRENT_STARTUP_LOG:"Nézd meg az új PID-et, a saját TCP/UDP portokat, az RCON-t és az aktuális startup logot; nincs automatikus újrapróbálkozás.",
 RECONCILE_NATIVE_START_JOURNAL_AND_RUNNING_PROCESSES:"Egyeztesd a natív Start-naplót és az ASA/Loader PID-eket; újabb Start nem küldhető vakon.",
 REVIEW_REPEATED_CRASH_AND_COOLDOWN:"Vizsgáld ki az ismétlődő crash okát és a 30 perces korlátot.",
 MANUAL_NATIVE_JOURNAL_AND_PROCESS_REVIEW:"Kézi ellenőrzés: natív naplók, forráspin, konfiguráció és folyamatok.",
 REVIEW_WATCHDOG_STATUS:"Ellenőrizd a legutóbbi felügyeleti bizonylatot."
};
const names:Record<WatchdogRecoveryMap["status"],string>={armed:"Élesítve",disarmed:"Kikapcsolva",claimed:"Tartós helyreállítási foglalás",awaitingReady:"Új ASA készül",needsReview:"Kézi ellenőrzés szükséges",suspended:"Szándékosan leállítva / új natív Start várása",externalSuspended:"Külső karbantartásra felfüggesztve"};
export function WatchdogRecoveryPanel({fixedMapId}:{fixedMapId?:string}={}){
  const [rows,setRows]=useState<WatchdogRecoveryMap[]>([]),[running,setRunning]=useState(false),[error,setError]=useState("");
  const [busy,setBusy]=useState(false),[selected,setSelected]=useState(fixedMapId??""),[ack,setAck]=useState(false);
  const guard=useRef(false);
  useEffect(()=>{let alive=true,polling=false;
    const poll=async()=>{if(polling||guard.current)return;polling=true;
      try{const data=await managerBridge.recoveryStatus();if(alive){setRows(fixedMapId?data.maps.filter(row=>row.mapId===fixedMapId):data.maps);setRunning(data.running);}}
      catch{if(alive)setRunning(false);}finally{polling=false;}};
    void poll();const timer=window.setInterval(()=>{void poll();},5000);
    return()=>{alive=false;window.clearInterval(timer);};},[fixedMapId]);
  const target=rows.find(x=>x.mapId===selected);
  const operate=async(arm:boolean|"suspend")=>{
    if(guard.current||!target||!ack)return;guard.current=true;setBusy(true);setError("");
    try{
      const data=arm==="suspend"?await managerBridge.recoverySuspend(selected):arm?await managerBridge.recoveryArm(selected):await managerBridge.recoveryDisarm(selected);
      setRows(old=>old.map(r=>r.mapId===data.mapId?data:r));setAck(false);
    }catch(e){setError(e instanceof Error?e.message:"UM-WATCHDOG-UNAVAILABLE");}
    finally{setBusy(false);guard.current=false;}
  };
  return <section className="premium-panel" aria-label="Automatikus Watchdog Recovery">
    <header className="premium-panel-heading"><h2>Automatikus Watchdog Recovery</h2></header>
    <p className="muted small">Helyi maponkénti engedély. Igazolt, súlyos Windows folyamatösszeomlás után, három teljes leállási megfigyelést követően egy natív Start. A Managernek futnia kell (tálcára minimalizálható). RCON-hiba önmagában nem indít újra. A kézi Stop nem indít vissza. A támogatott Restart és későbbi natív Start igazolt készültség után visszaélesíti a védelmet. Külső karbantartás előtt függeszd fel; ismeretlen folyamatcsere nem jogosít automatikus indításra. Újraindítás után új PID, ASA API család és RCON készenlét ellenőrzése; 30 perces újraindítási korlát. Ismeretlen kilépésnél kézi ellenőrzés.</p>
    <p role="status">{running?"Automatikus motor aktív":"Automatikus motor nem igazolt"}</p>
    <table className="ops-schedule-table"><thead><tr><th>Map</th><th>Mód</th><th>Recovery</th><th>Utolsó kísérlet / bizonyíték</th></tr></thead>
      <tbody>{rows.map(row=><tr key={row.mapId}><td>{row.mapName}</td><td>{row.storeV1?"Store-v1 · ":""}{row.launchMode==="asaApiLoader"?"ASA API":"ASA"}</td><td>{(row.enabled??row.status!=="disarmed")?"Engedélyezve · ":"Kikapcsolva · "}{names[row.status]}{row.stoppedSamples>0?" · "+row.stoppedSamples+"/3":""}</td><td>{row.lastAttemptUtc?new Date(row.lastAttemptUtc).toLocaleString("hu-HU")+" · ":""}{reasons[row.evidence]??row.evidence}{row.nextAction&&<div><strong>Következő teendő:</strong> {actions[row.nextAction]??row.nextAction}</div>}{row.terminalPublicationPending&&<div>Lezárási értesítés publikálásra vár.</div>}{row.lastResultUtc&&<div>Utolsó lezárás: {new Date(row.lastResultUtc).toLocaleString("hu-HU")} · {reasons[row.lastResultEvidence??""]??row.lastResultEvidence}</div>}<div>Utolsó ellenőrzés: {row.lastCheckedUtc?new Date(row.lastCheckedUtc).toLocaleString("hu-HU"):"–"}</div><div>Következő kísérlet legkorábban: {row.nextAttemptUtc?new Date(row.nextAttemptUtc).toLocaleString("hu-HU"):"–"}</div></td></tr>)}</tbody></table>
    <label>Helyi map <select aria-label="Watchdog recovery map" value={selected} disabled={busy||!!fixedMapId} onChange={e=>{setSelected(e.target.value);setAck(false);}}>
      <option value="">Válassz mapot</option>{rows.map(row=><option key={row.mapId} value={row.mapId}>{row.mapName}</option>)}</select></label>
    <label><input type="checkbox" checked={ack} disabled={busy||!target} onChange={e=>setAck(e.target.checked)}/>Jóváhagyom a kijelölt map recovery beállítását. Élesítéskor engedélyezem a következő igazolt összeomlás automatikus újraindítását.</label>
    <div className="ini-actions"><button type="button" className="primary" disabled={busy||!running||!target||!ack||target.status==="claimed"||target.status==="awaitingReady"} onClick={()=>{void operate(true);}}>Automatikus helyreállítás igazolt szervercrash után</button>
      <button type="button" className="secondary" disabled={busy||!target||!ack||target.status==="claimed"||target.status==="awaitingReady"} onClick={()=>{void operate("suspend");}}>Felfüggesztés külső karbantartáshoz</button>
      <button type="button" className="secondary" disabled={busy||!target||!ack||target.status==="claimed"||target.status==="awaitingReady"} onClick={()=>{void operate(false);}}>Recovery kikapcsolása (ASA marad)</button></div>
    {error&&<p className="error-inline" role="alert">{error}</p>}
  </section>;
}
