import {useEffect,useRef,useState} from "react";
import {managerBridge,type WatchdogRecoveryMap} from "./bridge";
const names:Record<WatchdogRecoveryMap["status"],string>={armed:"Élesítve",disarmed:"Kikapcsolva",claimed:"Tartós helyreállítási foglalás",awaitingReady:"Új ASA készül",needsReview:"Kézi ellenőrzés szükséges",suspended:"Szándékosan leállítva / új natív Start várása",externalSuspended:"Külső karbantartásra felfüggesztve"};
export function WatchdogRecoveryPanel(){
  const [rows,setRows]=useState<WatchdogRecoveryMap[]>([]),[running,setRunning]=useState(false),[error,setError]=useState("");
  const [busy,setBusy]=useState(false),[selected,setSelected]=useState(""),[ack,setAck]=useState(false);
  const guard=useRef(false);
  useEffect(()=>{let alive=true,polling=false;
    const poll=async()=>{if(polling||guard.current)return;polling=true;
      try{const data=await managerBridge.recoveryStatus();if(alive){setRows(data.maps);setRunning(data.running);}}
      catch{if(alive)setRunning(false);}finally{polling=false;}};
    void poll();const timer=window.setInterval(()=>{void poll();},5000);
    return()=>{alive=false;window.clearInterval(timer);};},[]);
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
      <tbody>{rows.map(row=><tr key={row.mapId}><td>{row.mapName}</td><td>{row.storeV1?"Store-v1 · ":""}{row.launchMode==="asaApiLoader"?"ASA API":"ASA"}</td><td>{(row.enabled??row.status!=="disarmed")?"Engedélyezve · ":"Kikapcsolva · "}{names[row.status]}{row.stoppedSamples>0?" · "+row.stoppedSamples+"/3":""}</td><td>{row.lastAttemptUtc?new Date(row.lastAttemptUtc).toLocaleString("hu-HU")+" · ":""}{row.evidence}<div>Utolsó ellenőrzés: {row.lastCheckedUtc?new Date(row.lastCheckedUtc).toLocaleString("hu-HU"):"–"}</div><div>Következő kísérlet legkorábban: {row.nextAttemptUtc?new Date(row.nextAttemptUtc).toLocaleString("hu-HU"):"–"}</div></td></tr>)}</tbody></table>
    <label>Helyi map <select aria-label="Watchdog recovery map" value={selected} disabled={busy} onChange={e=>{setSelected(e.target.value);setAck(false);}}>
      <option value="">Válassz mapot</option>{rows.map(row=><option key={row.mapId} value={row.mapId}>{row.mapName}</option>)}</select></label>
    <label><input type="checkbox" checked={ack} disabled={busy||!target} onChange={e=>setAck(e.target.checked)}/>Jóváhagyom a kijelölt map recovery beállítását. Élesítéskor engedélyezem a következő igazolt összeomlás automatikus újraindítását.</label>
    <div className="ini-actions"><button type="button" className="primary" disabled={busy||!running||!target||!ack||target.status==="claimed"||target.status==="awaitingReady"} onClick={()=>{void operate(true);}}>Automatikus helyreállítás igazolt szervercrash után</button>
      <button type="button" className="secondary" disabled={busy||!target||!ack||target.status==="claimed"||target.status==="awaitingReady"} onClick={()=>{void operate("suspend");}}>Felfüggesztés külső karbantartáshoz</button>
      <button type="button" className="secondary" disabled={busy||!target||!ack} onClick={()=>{void operate(false);}}>Recovery kikapcsolása (ASA marad)</button></div>
    {error&&<p className="error-inline" role="alert">{error}</p>}
  </section>;
}
