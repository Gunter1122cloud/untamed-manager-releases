import {useState} from "react";
import type {PublicMap} from "./bridge";
import type {PlayerHistory} from "./state/playerActivity";
export function PlayerActivityPanel({enabled,onEnabled,data,maps,error,onClear}:{
  enabled:boolean;onEnabled:(v:boolean)=>void;data:PlayerHistory;maps:PublicMap[];error:string;onClear:()=>void
}){
  const [query,setQuery]=useState("");
  const [mapId,setMapId]=useState("");
  const [limit,setLimit]=useState(50);
  const rows=data.records.filter(r=>(!mapId||r.mapId===mapId)&&r.name.toLowerCase().includes(query.toLowerCase()));
  const duration=(seconds:number)=>Math.floor(seconds/3600)+" ó "+Math.floor(seconds%3600/60)+" p";
  return <details className="premium-panel" style={{padding:16,marginTop:12}}>
    <summary>Játékoselőzmények · megfigyelt aktivitás</summary>
    <p className="muted small">A Manager natív, helyi ListPlayers mintáiból, külön engedéllyel; tálcára kicsinyítve és más menüpontban is fut.
      Két egymást követő mintában látott játékos köztes ideje becslés; nem pontos belépési/kilépési napló.
      RCON-kimaradás, szerverfolyamat-váltás és leállított Manager idejét nem számoljuk. A helyi előzmény titkosítva tárolódik. Legfeljebb 1000 rekord, 30 napos utolsó megfigyelés szerinti megőrzéssel.</p>
    <label><input type="checkbox" checked={enabled} onChange={e=>onEnabled(e.target.checked)}/> Helyi játékosnév, azonosító és megfigyelt idő folyamatos rögzítése</label>
    <div><label>Map <select value={mapId} onChange={e=>{setMapId(e.target.value);setLimit(50);}}>
      <option value="">Összes</option>{maps.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}</select></label>
      <input aria-label="Játékoselőzmény keresése" placeholder="Játékosnév" value={query} onChange={e=>{setQuery(e.target.value);setLimit(50);}}/>
    </div>
    <div style={{overflowX:"auto"}}><table><thead><tr><th>Játékos</th><th>Map</th><th>Megfigyelt idő (becslés)</th><th>Utolsó megfigyelés</th></tr></thead>
      <tbody>{rows.slice(0,limit).map(r=><tr key={r.mapId+":"+r.id}><td>{r.name}</td><td>{maps.find(m=>m.id===r.mapId)?.name??"Korábbi map"}</td>
        <td>{duration(r.seconds)}</td><td>{new Date(r.lastSeen).toLocaleString("hu-HU")}</td></tr>)}</tbody></table></div>
    {rows.length>limit&&<button type="button" className="secondary" onClick={()=>setLimit(x=>x+50)}>További játékosok</button>}
    {!rows.length&&<p className="muted">Nincs megfigyelt játékosrekord.</p>}
    <button type="button" className="secondary" onClick={onClear}>Helyi játékoselőzmények törlése</button>
    {error&&<p role="alert">{error}</p>}
  </details>;
}
