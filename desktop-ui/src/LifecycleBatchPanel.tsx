import {useEffect,useRef,useState} from "react";
import {managerBridge,type LifecycleBatchAction,type LifecycleBatchPreview,type LifecycleBatchReceipt,type OperationsMap} from "./bridge";

const labels:Record<LifecycleBatchAction,string>={start:"Indítás",stop:"Leállítás",restart:"Újraindítás"};
export function LifecycleBatchPanel({maps,visible,onRefresh}:{maps:OperationsMap[];visible:OperationsMap[];onRefresh:()=>Promise<void>}){
  const [selected,setSelected]=useState<string[]>([]);
  const [action,setAction]=useState<LifecycleBatchAction>("start");
  const [delay,setDelay]=useState(15);
  const [preview,setPreview]=useState<LifecycleBatchPreview|null>(null);
  const [receipt,setReceipt]=useState<LifecycleBatchReceipt|null>(null);
  const [busy,setBusy]=useState(false),[error,setError]=useState("");
  const [ack,setAck]=useState(false),[readable,setReadable]=useState(false);
  const inFlight=useRef(false),lastTerminal=useRef("");
  const refreshRef=useRef(onRefresh);refreshRef.current=onRefresh;
  useEffect(()=>{
    let alive=true,polling=false;
    const poll=async()=>{
      if(polling)return;polling=true;
      try{
        const status=await managerBridge.lifecycleBatchCurrent();
        if(!alive)return;
        setReceipt(status.receipt);setReadable(true);
        if(status.receipt&&status.receipt.state!=="running"){
          const key=status.receipt.jobId+status.receipt.state;
          if(lastTerminal.current!==key){lastTerminal.current=key;void refreshRef.current().catch(()=>{});}
        }
      }catch{if(alive)setReadable(false);}
      finally{polling=false;}
    };
    void poll();const timer=window.setInterval(()=>{void poll();},2500);
    return()=>{alive=false;window.clearInterval(timer);};
  },[]);
  const blocked=!readable||busy||receipt?.state==="running"||receipt?.state==="needsReview";
  const eligible=(m:OperationsMap)=>!m.maintenance&&(action==="start"?m.confirmedStopped&&!m.running:m.running&&!m.confirmedStopped);
  const targets=maps.filter(m=>selected.includes(m.mapId));
  const valid=targets.length===selected.length&&targets.length>0&&targets.length<=25&&targets.every(eligible)&&Number.isInteger(delay)&&delay>=0&&delay<=300;
  const change=(ids:string[])=>{setSelected(ids);setPreview(null);setAck(false);};
  const operate=async(kind:"preview"|"apply"|"review")=>{
    if(inFlight.current)return;inFlight.current=true;setBusy(true);setError("");
    try{
      if(kind==="preview"){
        if(!valid||blocked)return;
        const plan=await managerBridge.lifecycleBatchPreview(selected,action,delay);
        if(plan.remoteExecutionAllowed!==false||plan.action!==action||
          plan.maps.length!==selected.length||plan.maps.some(m=>!selected.includes(m.mapId)))
          throw new Error("UM-BATCH-SCOPE-MISMATCH");
        setPreview(plan);setAck(false);
      }else if(kind==="apply"&&preview&&ack){
        const plan=preview;setPreview(null);setAck(false);
        const started=await managerBridge.lifecycleBatchApply(plan.token);
        setReceipt(started);
      }else if(kind==="review"&&receipt&&ack){
        setAck(false);
        setReceipt(await managerBridge.lifecycleBatchReview(receipt.jobId));
      }
    }catch(e){setError((e instanceof Error?e.message:"UM-BATCH-UNAVAILABLE")+
      " · Ellenőrizd a helyi naplót; bizonytalan műveletet ne ismételj.");}
    finally{setBusy(false);inFlight.current=false;}
  };
  return <section className="premium-panel" aria-label="Helyi tömeges ASA műveletek">
    <header className="premium-panel-heading"><h2>Tömeges helyi Start / Stop / Restart</h2></header>
    <p className="muted small">Kizárólag a kiválasztott saját mapok. Soros indítás, új PID és RCON készenlét után következő map. Bizonytalan műveletnél megáll; nincs automatikus ismétlés.</p>
    <div className="ini-actions">
      <label>Művelet <select aria-label="Tömeges művelet" value={action} disabled={blocked}
        onChange={e=>{setAction(e.target.value as LifecycleBatchAction);change([]);}}>
        {Object.entries(labels).map(([value,label])=><option key={value} value={value}>{label}</option>)}
      </select></label>
      <label>Várakozás mapok között (mp) <input type="number" min={0} max={300} value={delay} disabled={blocked}
         onChange={e=>{setDelay(Number(e.target.value));setPreview(null);setAck(false);}}/></label>
      <button className="secondary" type="button" disabled={blocked} onClick={()=>change(visible.filter(eligible).map(m=>m.mapId))}>Látható alkalmas mapok</button>
      <button className="secondary" type="button" disabled={blocked} onClick={()=>change(maps.filter(eligible).map(m=>m.mapId))}>Összes alkalmas helyi map</button>
      <button className="secondary" type="button" disabled={blocked} onClick={()=>change([])}>Kijelölés törlése</button>
    </div>
    <div className="ops-schedule-scroll">{maps.map(m=><label key={m.mapId} style={{display:"inline-flex",gap:6,margin:8}}>
      <input type="checkbox" checked={selected.includes(m.mapId)} disabled={blocked||!eligible(m)}
        onChange={e=>change(e.target.checked?[...selected,m.mapId]:selected.filter(id=>id!==m.mapId))}/>{m.mapName}</label>)}</div>
    <p>{targets.length} kijelölt map (maximum 25 kötegenként).</p>
    <button type="button" className="primary" disabled={blocked||!valid} onClick={()=>{void operate("preview");}}>Tömeges művelet előnézete</button>
    {preview&&<div className="warning-inline" role="region" aria-label="Tömeges művelet jóváhagyása"><div>
      <b>{labels[preview.action]} · {preview.maps.length} map</b><p>{preview.maps.map(m=>m.mapName).join(", ")}</p>
      <label><input type="checkbox" checked={ack} onChange={e=>setAck(e.target.checked)}/> Jóváhagyom a felsorolt helyi mapok műveletét.</label>
      <button type="button" className="primary" disabled={!ack||blocked} onClick={()=>{void operate("apply");}}>Köteg indítása</button>
    </div></div>}
    {receipt&&<div role="region" aria-label="Tartós tömeges műveleti napló">
      <p><b>{labels[receipt.action]}</b> · {receipt.state} · {receipt.jobId.slice(0,8)}</p>
      <table className="ops-schedule-table"><thead><tr><th>Map</th><th>Állapot</th><th>Natív bizonyíték</th></tr></thead>
        <tbody>{receipt.targets.map(row=><tr key={row.mapId}><td>{row.mapName}</td><td>{row.state}</td><td>{row.evidence}</td></tr>)}</tbody></table>
      {receipt.state==="needsReview"&&<div><label><input type="checkbox" checked={ack} onChange={e=>setAck(e.target.checked)}/>
        Ellenőriztem a natív műveleti naplót; a régi köteg nem indulhat újra.</label>
        <button className="secondary" type="button" disabled={!ack||busy||!readable} onClick={()=>{void operate("review");}}>Bizonytalan köteg lezárása ismétlés nélkül</button></div>}
    </div>}
    {!readable&&<p className="muted small">A natív kötegnapló nem ellenőrizhető.</p>}
    {error&&<p className="error-inline" role="alert">{error}</p>}
  </section>;
}
