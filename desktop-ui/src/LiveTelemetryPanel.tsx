import {useEffect,useState} from "react";
import {Activity} from "lucide-react";
import type {DashboardSnapshot} from "./bridge";
import {appendPoint,percent,playerTotal,plotSegments,type Point,type PlayerProof} from "./state/liveTelemetry";

function Trend({points,metric,label,max,unit}:{points:Point[];metric:"cpu"|"ram"|"players";label:string;max:number;unit:string}){
  const latest=points[points.length-1]?.[metric]??null;
  const paths=plotSegments(points,metric,max);
  return <div style={{minWidth:0,flex:"1 1 200px"}}>
    <b>{label}: {latest===null?"Nem mérhető":latest.toFixed(metric==="players"?0:1)+unit}</b>
    <svg viewBox="0 0 300 85" width="100%" height="85" role="img" aria-label={label+" mérési előzményei"}>
      <path d="M10 10 H290 M10 42.5 H290 M10 75 H290" stroke="currentColor" opacity=".15" fill="none"/>
      {paths.map((d,i)=><path key={i} d={d} stroke="#55b9d4" strokeWidth="2" strokeLinecap="round" fill="none"/>)}
      {paths.length===0&&<text x="150" y="45" textAnchor="middle" fill="currentColor" fontSize="10">Nincs igazolt adat</text>}
    </svg>
    <small className="muted">0–{max}{unit} · a hiányzó minták megszakítják a görbét</small>
  </div>;
}
export function LiveTelemetryPanel({snapshot,runningMapIds,playerProofs}:{
  snapshot:DashboardSnapshot;runningMapIds:string[];playerProofs:Record<string,PlayerProof>
}){
  const [points,setPoints]=useState<Point[]>([]);
  const {cpuPercent,memoryTotalKiB,memoryUsedKiB}=snapshot.local;
  const time=Date.parse(snapshot.observedUtc);
  const cpu=percent(cpuPercent);
  const ram=percent(memoryTotalKiB!==null&&memoryTotalKiB>0&&memoryUsedKiB!==null?memoryUsedKiB/memoryTotalKiB*100:null);
  const players=playerTotal(runningMapIds,playerProofs,time);
  useEffect(()=>{
    const now=Date.now();
    if(!Number.isFinite(time)||time>now+5000||now-time>60000)return;
    setPoints(old=>appendPoint(old,{time,cpu,ram,players}));
  },[time,cpu,ram,players]);
  const playerMax=Math.max(1,...points.map(p=>p.players??0));
  return <details className="premium-panel" style={{padding:16,marginTop:12}}>
    <summary aria-label="Helyi teljesítménygrafikonok"><Activity size={16}/> Helyi teljesítménygrafikonok</summary>
    <p className="muted small">Az aktuális Dashboard munkamenet legfeljebb egyórás, tényleges helyi mintái.
      Nem tartós előzmény, a távoli hostok és részleges RCON adatok nem kerülnek a játékosgörbébe.</p>
    <div style={{display:"flex",flexWrap:"wrap",gap:16}}>
      <Trend points={points} metric="cpu" label="Host CPU" max={100} unit="%"/>
      <Trend points={points} metric="ram" label="Host RAM" max={100} unit="%"/>
      <Trend points={points} metric="players" label="Helyi játékosok" max={playerMax} unit=" fő"/>
    </div>
    <small className="muted">{points.length} minta · {points.length?new Date(points[0].time).toLocaleTimeString("hu-HU")+" – "+new Date(points[points.length-1].time).toLocaleTimeString("hu-HU"):"Mérésre vár"}</small>
  </details>;
}
