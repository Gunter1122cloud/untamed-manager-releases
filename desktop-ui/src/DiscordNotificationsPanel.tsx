import {useEffect,useRef,useState} from "react";
import {managerBridge,type DiscordNotificationStatus} from "./bridge";
const labels:Record<string,string>={watchdog:"Watchdog / folyamat",lifecycle:"Tömeges életciklus",schedule:"Ütemezett ASA művelet",backup:"Ütemezett mentés"};
export function DiscordNotificationsPanel(){
  const [status,setStatus]=useState<DiscordNotificationStatus|null>(null);
  const [url,setUrl]=useState(""),[categories,setCategories]=useState(Object.keys(labels));
  const [warnings,setWarnings]=useState(true),[busy,setBusy]=useState(false),[error,setError]=useState("");
  const lock=useRef(false);
  const load=(s:DiscordNotificationStatus)=>{setStatus(s);setCategories(s.categories);setWarnings(s.warningsOnly);};
  useEffect(()=>{let alive=true;managerBridge.notificationsStatus().then(s=>{if(alive)load(s);}).catch(()=>{if(alive)setError("A helyi webhook állapota nem olvasható.");});return()=>{alive=false;};},[]);
  const act=async(kind:"save"|"test"|"clear"|"status")=>{
    if(lock.current)return;lock.current=true;setBusy(true);setError("");
    try{
      const s=kind==="save"?await managerBridge.notificationsSave(url,categories,warnings):
        kind==="test"?await managerBridge.notificationsTest():
        kind==="clear"?await managerBridge.notificationsClear():await managerBridge.notificationsStatus();
      load(s);setUrl("");
    }catch{setError("A művelet nem igazolható. Ellenőrizd a helyi állapotot; sikertelen küldést nem ismétlünk automatikusan.");}
    finally{setBusy(false);lock.current=false;}
  };
  return <section className="premium-panel" aria-label="Discord üzemeltetési értesítések">
    <header className="premium-panel-heading"><h2>Discord webhook értesítések</h2></header>
    <p className="muted small">Helyi, opcionális üzemeltetési jelzések. A titkosított URL nem olvasható vissza. Nincs játékoschat, nyers log vagy RCON parancsszöveg. A Manager futása alatt működik; legfeljebb 10 percnyi friss esemény, bizonytalan küldés ismétlése nélkül.</p>
    <p role="status">{status?.configured?"Konfigurálva":"Nincs konfigurálva"} · {status?.lastStatus??"Nem ellenőrizhető"}{status?.lastAttemptUtc?" · "+new Date(status.lastAttemptUtc).toLocaleString("hu-HU"):""}</p>
    <label>Discord webhook URL <input type="password" autoComplete="off" spellCheck={false} aria-label="Discord webhook URL" value={url} maxLength={512} placeholder={status?.configured?"Üresen hagyva a meglévő URL marad":"https://discord.com/api/webhooks/..."} disabled={busy} onChange={e=>setUrl(e.target.value)}/></label>
    <div>{Object.entries(labels).map(([key,label])=><label key={key} style={{display:"inline-flex",gap:6,margin:8}}>
      <input type="checkbox" checked={categories.includes(key)} disabled={busy} onChange={e=>setCategories(old=>e.target.checked?[...old,key]:old.filter(x=>x!==key))}/>{label}</label>)}</div>
    <label><input type="checkbox" checked={warnings} disabled={busy} onChange={e=>setWarnings(e.target.checked)}/>Csak figyelmeztetések és hibák</label>
    <div className="ini-actions">
      <button type="button" className="primary" disabled={busy||categories.length===0||(!status?.configured&&!url)} onClick={()=>{void act("save");}}>Webhook mentése</button>
      <button type="button" className="secondary" disabled={busy||!status?.configured} onClick={()=>{void act("test");}}>Próbaértesítés küldése</button>
      <button type="button" className="secondary" disabled={busy||!status?.configured} onClick={()=>{void act("clear");}}>Webhook eltávolítása</button>
      <button type="button" className="secondary" disabled={busy} onClick={()=>{void act("status");}}>Küldési állapot frissítése</button>
    </div>
    {error&&<p role="alert" className="error-inline">{error}</p>}
  </section>;
}
