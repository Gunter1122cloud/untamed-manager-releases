import {useEffect,useRef,useState} from "react";
import {managerBridge,type LifecycleBatchAction,type LifecycleBatchReceipt,type OperationsMap} from "./bridge";

const labels:Record<LifecycleBatchAction,string>={start:"Indítás",stop:"Leállítás",restart:"Újraindítás"};
export function LifecycleBatchPanel({maps,visible,onRefresh}:{maps:OperationsMap[];visible:OperationsMap[];onRefresh:()=>Promise<void>}){
  const [selected,setSelected]=useState<string[]>([]);
  const [action,setAction]=useState<LifecycleBatchAction>("start");
  const [delay,setDelay]=useState(15);
  const [receipt,setReceipt]=useState<LifecycleBatchReceipt|null>(null);
  const [busy,setBusy]=useState(false),[error,setError]=useState("");
  const [readable,setReadable]=useState(false);
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
  const blocked=!readable||busy||receipt?.state==="running";
  const eligible=(m:OperationsMap)=>!m.maintenance&&(action==="start"?m.confirmedStopped&&!m.running:m.running&&!m.confirmedStopped);
  const targets=maps.filter(m=>selected.includes(m.mapId));
  const valid=targets.length===selected.length&&targets.length>0&&targets.length<=25&&targets.every(eligible)&&Number.isInteger(delay)&&delay>=0&&delay<=300;
  const change=(ids:string[])=>setSelected(ids);
  const operate=async()=>{
    if(inFlight.current||!valid||blocked)return;
    inFlight.current=true;setBusy(true);setError("");
    try{
      const started=await managerBridge.lifecycleBatchRun(selected,action,delay);
      if(started.action!==action||started.targets.length!==selected.length||
         started.targets.some(m=>!selected.includes(m.mapId)))
        throw new Error("UM-BATCH-SCOPE-MISMATCH");
      setReceipt(started);
    }catch(e){setError((e instanceof Error?e.message:"UM-BATCH-UNAVAILABLE")+
      " · A natív művelet állapota a naplóban látható.");}
    finally{setBusy(false);inFlight.current=false;}
  };
  return <section className="premium-panel" aria-label="Helyi tömeges ASA műveletek">
    <header className="premium-panel-heading"><h2>Tömeges helyi Start / Stop / Restart</h2></header>
    <p className="muted small">Kizárólag a kiválasztott saját mapok. Soros indítás, új PID és RCON készenlét után következő map. A független mapok külön eredményt kapnak; régi bizonytalan parancs nem ismétlődik meg.</p>
    <div className="ini-actions">
      <label>Művelet <select aria-label="Tömeges művelet" value={action} disabled={blocked}
        onChange={e=>{setAction(e.target.value as LifecycleBatchAction);change([]);}}>
        {Object.entries(labels).map(([value,label])=><option key={value} value={value}>{label}</option>)}
      </select></label>
      <label>Várakozás mapok között (mp) <input type="number" min={0} max={300} value={delay} disabled={blocked}
         onChange={e=>setDelay(Number(e.target.value))}/></label>
      <button className="secondary" type="button" disabled={blocked} onClick={()=>change(visible.filter(eligible).map(m=>m.mapId))}>Látható alkalmas mapok</button>
      <button className="secondary" type="button" disabled={blocked} onClick={()=>change(maps.filter(eligible).map(m=>m.mapId))}>Összes alkalmas helyi map</button>
      <button className="secondary" type="button" disabled={blocked} onClick={()=>change([])}>Kijelölés törlése</button>
    </div>
    <div className="ops-schedule-scroll">{maps.map(m=><label key={m.mapId} style={{display:"inline-flex",gap:6,margin:8}}>
      <input type="checkbox" checked={selected.includes(m.mapId)} disabled={blocked||!eligible(m)}
        onChange={e=>change(e.target.checked?[...selected,m.mapId]:selected.filter(id=>id!==m.mapId))}/>{m.mapName}</label>)}</div>
    <p>{targets.length} kijelölt map (maximum 25 kötegenként).</p>
    <button type="button" className="primary" disabled={blocked||!valid} onClick={()=>{void operate();}}>{labels[action]} – {targets.length} map</button>
    {receipt&&<div role="region" aria-label="Tartós tömeges műveleti napló">
      <p><b>{labels[receipt.action]}</b> · {receipt.state} · {receipt.jobId.slice(0,8)}</p>
      <table className="ops-schedule-table"><thead><tr><th>Map</th><th>Állapot</th><th>Natív bizonyíték</th></tr></thead>
        <tbody>{receipt.targets.map(row=><tr key={row.mapId}><td>{row.mapName}</td><td>{row.state}</td><td>{row.evidence}</td></tr>)}</tbody></table>
      {receipt.state==="needsReview"&&<p className="muted small">A régi köteg eredménye bizonytalan; az új művelet natív ellenőrzése automatikusan rendezi az inaktív köteget, ha már nincs aktív munkafolyamat.</p>}
    </div>}
    {!readable&&<p className="muted small">A natív kötegnapló nem ellenőrizhető.</p>}
    {error&&<p className="error-inline" role="alert">{error}</p>}
  </section>;
}
