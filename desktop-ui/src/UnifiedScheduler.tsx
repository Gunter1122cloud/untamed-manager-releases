import {useMemo,useState} from "react";
import {CalendarClock} from "lucide-react";
import type {OperationsSnapshot,NativeAsaScheduleOverview} from "./bridge";
import {NativeMapScheduler} from "./NativeMapScheduler";
import {RollingSteamSettings} from "./RollingSteamSettings";
import {scheduleRows} from "./state/schedulerOverview";
export function UnifiedScheduler({snapshot,nativeOverview,onRefresh}:{
  snapshot:OperationsSnapshot;nativeOverview:NativeAsaScheduleOverview|null;onRefresh:()=>Promise<void>
}){
  const [expanded,setExpanded]=useState(false);
  const [mapId,setMapId]=useState("");
  const [activeOnly,setActiveOnly]=useState(true);
  const [page,setPage]=useState(0);
  const [busy,setBusy]=useState(false);
  const rows=useMemo(()=>scheduleRows(snapshot,nativeOverview),[snapshot,nativeOverview]);
  const target=snapshot.maps.some(m=>m.mapId===mapId)?mapId:"";
  const visible=rows.filter(row=>(!target||row.mapId===target)&&(!activeOnly||row.enabled));
  const safePage=Math.min(page,Math.max(0,Math.ceil(visible.length/50)-1));
  const date=(utc:string|null)=>{const t=Date.parse(utc??"");return Number.isFinite(t)?new Date(t).toLocaleString("hu-HU"):"Nem ismert";};
  return <section className="premium-panel" aria-label="Egységes ütemezőközpont" style={{padding:16}}>
    <header className="premium-panel-heading"><h2><CalendarClock size={19}/> Egységes ütemezőközpont</h2>
      <button type="button" className="secondary" aria-expanded={expanded} onClick={()=>setExpanded(v=>!v)}>{expanded?"Összecsukás":"Ütemezések megjelenítése"}</button></header>
    {expanded&&<>
      <p className="muted small">A meglévő helyi natív szerverütemező és az ellenőrző feladatok közös nézete.
        Nem indít új ütemezőmotort. A kiválasztott maphoz az eredeti natív szerkesztők nyílnak meg.</p>
      {!nativeOverview&&<p role="status" className="warning-inline">A natív ASA feladatlista nem ellenőrizhető; ez nem jelenti, hogy nincsenek ütemezések.</p>}
      <label>Map szűrése / szerkesztése <select aria-label="Központi ütemező mapja" value={target} onChange={e=>{setMapId(e.target.value);setPage(0);}}>
        <option value="">Összes helyi map</option>{snapshot.maps.map(m=><option key={m.mapId} value={m.mapId}>{m.mapName}</option>)}
      </select></label>
      <label><input type="checkbox" checked={activeOnly} onChange={e=>{setActiveOnly(e.target.checked);setPage(0);}}/> Csak aktív feladatok</label>
      <button type="button" className="secondary" disabled={busy} onClick={async()=>{setBusy(true);try{await onRefresh();}finally{setBusy(false);}}}>Ütemező összesítés frissítése</button>
      <div className="ops-schedule-scroll"><table className="ops-schedule-table"><thead><tr><th>Map</th><th>Feladat</th><th>Forrás</th><th>Következő időpont</th><th>Állapot</th></tr></thead>
        <tbody>{visible.slice(safePage*50,(safePage+1)*50).map(row=><tr key={row.id}>
          <td>{row.mapName}</td><td>{row.kind}</td><td>{row.source==="native"?"Natív ASA":"Ellenőrzés"}</td>
          <td>{row.enabled?date(row.nextUtc):"Kikapcsolva"}</td><td>{row.status||"Nem ismert"}</td>
        </tr>)}</tbody></table></div>
      {visible.length===0&&<p className="muted">A rendelkezésre álló adatokban nincs a szűrőnek megfelelő feladat.</p>}
      {visible.length>50&&<div><button type="button" disabled={safePage===0} onClick={()=>setPage(safePage-1)}>Előző</button>
        <span> {safePage+1}/{Math.ceil(visible.length/50)} </span><button type="button" disabled={(safePage+1)*50>=visible.length} onClick={()=>setPage(safePage+1)}>Következő</button></div>}
      {target&&<div key={target}>
        <NativeMapScheduler mapId={target}/>
        <RollingSteamSettings mapId={target}/>
      </div>}
      <p className="muted small">Az automatikus SteamCMD állapota a kiválasztott map eredeti paneljén jelenik meg.
        A feladatlista időpontjai az aktuális natív válaszból származnak; nem becsült futási időpontok.</p>
    </>}
  </section>;
}
