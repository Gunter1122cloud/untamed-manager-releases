import {useEffect,useRef,useState} from "react";
import {managerBridge,type OperationsMap,type ModerationPreview,type ModerationReceipt} from "./bridge";
export function BanManagerPanel({maps}:{maps:OperationsMap[]}){
  const [ids,setIds]=useState<string[]>([]),[action,setAction]=useState("ban"),[eos,setEos]=useState(""),[reason,setReason]=useState(""),[hours,setHours]=useState(0);
  const [plan,setPlan]=useState<ModerationPreview|null>(null),[typed,setTyped]=useState(""),[rows,setRows]=useState<ModerationReceipt[]>([]),[busy,setBusy]=useState(false),[error,setError]=useState("");
  const guard=useRef(false);
  const load=async()=>setRows((await managerBridge.moderationHistory()).rows);
  useEffect(()=>{void load().catch(()=>setError("A moderációs napló nem olvasható."));},[]);
  const reset=()=>{setPlan(null);setTyped("");};
  const valid=ids.length>0&&ids.length<=5&&/^[a-zA-Z0-9]{32}$/.test(eos)&&reason.trim().length>0&&reason.length<=200&&Number.isInteger(hours)&&hours>=0&&hours<=8760;
  const run=async(apply:boolean)=>{
    if(guard.current)return;guard.current=true;setBusy(true);setError("");
    try{
      if(apply&&plan&&typed==="MODERÁCIÓ"){const token=plan.token;reset();await managerBridge.moderationApply(token,typed);await load();}
      else if(!apply&&valid){const p=await managerBridge.moderationPreview(ids,action,eos,reason,action==="ban"?hours:0);setPlan(p);}
    }catch(e){setError(e instanceof Error?e.message:"UM-BAN-UNVERIFIED");if(apply)await load().catch(()=>{});}
    finally{guard.current=false;setBusy(false);}
  };
  return <section className="premium-panel" aria-label="Ban Manager">
    <header className="premium-panel-heading"><h2>Ban Manager · játékosmoderáció</h2></header>
    <p className="muted small">Csak saját futó mapok, EOS azonosítóval. Az elküldött parancs nem bizonyítja a játékbeli tiltást. Az opcionális lejárati időpont felülvizsgálati határidő, nem automatikus unban.</p>
    <label>Művelet <select value={action} disabled={busy} onChange={e=>{setAction(e.target.value);reset();}}><option value="ban">Ban</option><option value="unban">Unban</option><option value="kick">Kick</option></select></label>
    <label>EOS ID <input aria-label="Moderációs EOS ID" value={eos} maxLength={32} disabled={busy} onChange={e=>{setEos(e.target.value);reset();}}/></label>
    <label>Indok (helyi napló) <input value={reason} maxLength={200} disabled={busy} onChange={e=>{setReason(e.target.value);reset();}}/></label>
    {action==="ban"&&<label>Felülvizsgálat órában (0 = nincs) <input type="number" min={0} max={8760} value={hours} disabled={busy} onChange={e=>{setHours(Number(e.target.value));reset();}}/></label>}
    <div>{maps.map(m=><label key={m.mapId} style={{display:"inline-flex",gap:6,margin:8}}><input type="checkbox" checked={ids.includes(m.mapId)} disabled={busy||!m.running||m.maintenance} onChange={e=>{setIds(old=>e.target.checked?[...old,m.mapId]:old.filter(x=>x!==m.mapId));reset();}}/>{m.mapName}</label>)}</div>
    <button type="button" className="primary" disabled={busy||!valid} onClick={()=>void run(false)}>Moderáció előnézete</button>
    {plan&&<div className="warning-inline"><div><p>{plan.command} · {plan.maps.map(x=>x.mapName).join(", ")}</p><label>Írd be: MODERÁCIÓ <input value={typed} disabled={busy} onChange={e=>setTyped(e.target.value)}/></label><button type="button" className="primary" disabled={busy||typed!=="MODERÁCIÓ"} onClick={()=>void run(true)}>Parancs küldése</button></div></div>}
    <button type="button" className="secondary" disabled={busy} onClick={()=>void load().catch(()=>setError("Naplóhiba"))}>Moderációs napló frissítése</button>
    <div className="ops-schedule-scroll"><table className="ops-schedule-table"><thead><tr><th>Időpont</th><th>Művelet / EOS</th><th>Indok</th><th>Map eredmény</th><th>Felülvizsgálat</th></tr></thead><tbody>{rows.map(row=><tr key={row.id}><td>{new Date(row.createdUtc).toLocaleString("hu-HU")}</td><td>{row.action} · {row.eosId}</td><td>{row.reason}</td><td>{row.targets.map(t=><div key={t.mapId}>{t.mapName}: {t.status}</div>)}</td><td>{row.reviewAtUtc?new Date(row.reviewAtUtc).toLocaleString("hu-HU")+(Date.parse(row.reviewAtUtc)<=Date.now()?" · esedékes":""):"—"}</td></tr>)}</tbody></table></div>
    {error&&<p role="alert" className="error-inline">{error}</p>}
  </section>;
}
