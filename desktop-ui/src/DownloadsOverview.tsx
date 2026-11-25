import {useEffect,useState} from "react";
import {Download,RefreshCw} from "lucide-react";
import {managerBridge,type PublicMap} from "./bridge";
import {describeDownloads,type DownloadRow} from "./state/downloadsOverview";

export function DownloadsOverview({maps}:{maps:PublicMap[]}){
  const [expanded,setExpanded]=useState(false);
  const [revision,setRevision]=useState(0);
  const [data,setData]=useState<{rows:DownloadRow[];unavailable:string[];at:string}|null>(null);
  const [busy,setBusy]=useState(false);
  // Only read status while expanded; no separate queue or mutation route.
  // Each request settles before scheduling the next poll.
  const mapNamesKey=JSON.stringify(maps.map(m=>[m.id,m.name]));
  useEffect(()=>{
    if(!expanded){setData(null);setBusy(false);return;}
    let active=true;
    let timer:ReturnType<typeof setTimeout>|undefined;
    const names=Object.fromEntries(JSON.parse(mapNamesKey) as [string,string][]);
    const read=async()=>{
      if(!active)return;
      setBusy(true);
      const [fresh,single,batch,rolling]=await Promise.allSettled([
        managerBridge.freshCurrent(),managerBridge.steamStoppedCurrent(),
        managerBridge.steamBatchCurrent(),managerBridge.steamRollingStatus()
      ]);
      if(!active)return;
      const unavailable=[["Új telepítés",fresh.status],["Egyedi frissítés",single.status],
        ["Kötegelt frissítés",batch.status],["Ütemezett frissítés",rolling.status]]
        .filter(x=>x[1]==="rejected").map(x=>x[0]);
      setData({rows:describeDownloads({
        fresh:fresh.status==="fulfilled"?fresh.value.current:null,
        single:single.status==="fulfilled"?single.value:null,
        batch:batch.status==="fulfilled"?batch.value:null,
        rolling:rolling.status==="fulfilled"?rolling.value:null,mapNames:names
      }),unavailable,at:new Date().toLocaleTimeString("hu-HU")});
      setBusy(false);
      timer=setTimeout(()=>{void read();},5000);
    };
    void read();
    return ()=>{active=false;if(timer!==undefined)clearTimeout(timer);};
  },[expanded,revision,mapNamesKey]);
  return <section className="glass panel" aria-label="Letöltésközpont">
    <div className="panel-head"><h2><Download size={19}/> Downloads Center · Letöltések</h2>
      <button type="button" className="secondary" aria-expanded={expanded} onClick={()=>setExpanded(v=>!v)}>
        {expanded?"Összecsukás":"Feladatok megjelenítése"}</button></div>
    {expanded&&<div className="panel-inner">
      <p className="muted small">Helyi natív műveletek közös megfigyelése. Egyazon frissítés több kapcsolódó naplóban is szerepelhet.
        Ez a nézet nem indít vagy ismétel meg műveletet.</p>
      <button type="button" className="secondary" disabled={busy} onClick={()=>setRevision(v=>v+1)}><RefreshCw size={15}/> Újraolvasás</button>
      {data&&<small className="muted"> Utolsó lekérdezés: {data.at}</small>}
      {data?.unavailable.length? <div className="warning-inline" role="status">Nem ellenőrizhető: {data.unavailable.join(", ")}. Ez nem jelent üres vagy befejezett letöltési sort.</div>:null}
      {!data&&<p className="muted">Natív állapot lekérése...</p>}
      {data&&data.rows.length===0&&data.unavailable.length===0&&<div className="empty">A négy ellenőrzött forrás nem adott vissza aktuális feladatot vagy naplót.</div>}
      {data?.rows.map(row=><div key={row.id} className="steam-row">
        <div className="plugin-copy"><b>{row.label} · {row.subject}</b><small>{row.stage} · {row.detail}</small></div>
        <div className="backup-stats"><strong>{row.state}</strong>
          {row.progress===null?<span>Letöltési százalék nem ismert</span>:<><progress max={100} value={row.progress} aria-label={row.label+" előrehaladás"}/><span>{row.progress.toFixed(1)}%</span></>}
        </div>
      </div>)}
      <p className="muted small">Sebesség, letöltendő bájtok és ETA: a jelenlegi natív állapotválaszokból nem igazoltak.</p>
    </div>}
  </section>;
}
