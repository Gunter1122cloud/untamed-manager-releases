import {useEffect,useState} from "react";
import {clearRconHistory,loadHistory,readCommands,toggleFavorite,type ReadCommand} from "./state/rconHistory";
export function RconHistoryPanel({mapId,disabled,onRead}:{mapId:string;disabled:boolean;onRead:(command:ReadCommand)=>void}){
  const [state,setState]=useState(()=>({mapId,data:loadHistory(mapId)}));
  const [error,setError]=useState("");
  useEffect(()=>{
    const refresh=()=>setState({mapId,data:loadHistory(mapId)});
    refresh();window.addEventListener("obsidian-rcon-history",refresh);
    return ()=>window.removeEventListener("obsidian-rcon-history",refresh);
  },[mapId]);
  const data=state.mapId===mapId?state.data:{entries:[],favorites:[]};
  return <details className="premium-panel" style={{padding:12,marginBottom:10}}>
    <summary>RCON előzmények és kedvencek</summary>
    <p className="muted small">Az utolsó 100 igazolt konzolválasz vagy küldési bizonylat ezen a mapon, helyi WebView-profilban.
      Egyedi parancsok és üzenetek szövege, RCON-jelszó és válaszadat nem kerül mentésre.
      A küldés nem bizonyít játékon belüli végrehajtást.</p>
    {readCommands.map(command=><div key={command}>
      <label><input type="checkbox" checked={data.favorites.includes(command)}
        onChange={()=>setError(toggleFavorite(mapId,command)?"":"A helyi kedvenc nem menthető.")}/> {command}</label>
      {data.favorites.includes(command)&&<button type="button" className="secondary" disabled={disabled} onClick={()=>onRead(command)}>Lekérdezés · {command}</button>}
    </div>)}
    <button type="button" className="secondary" onClick={()=>setError(clearRconHistory(mapId)?"":"Az előzmény nem törölhető.")}>Helyi parancselőzmények törlése</button>
    <div style={{maxHeight:210,overflowY:"auto"}}>
      {data.entries.map((entry,i)=><div key={entry.utc+"-"+i}>{new Date(entry.utc).toLocaleString("hu-HU")} · {entry.action} · {entry.kind==="reply"?"Olvasási válasz":"Elküldve, végrehajtás nem igazolt"}</div>)}
      {!data.entries.length&&<p className="muted">Nincs rögzített konzolelőzmény.</p>}
    </div>
    {error&&<p role="alert">{error}</p>}
  </details>;
}
