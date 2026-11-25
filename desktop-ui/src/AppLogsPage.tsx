import { useEffect, useMemo, useState } from "react";
import { Panel } from "./Panel";
import { AlertTriangle, FileText, RefreshCw, Search, Shield } from "lucide-react";
import { managerBridge, type LogTail, type DashboardSnapshot } from "./bridge";
import { observedAt } from "./AppSharedMapViews";

export function LogsCenter({ snapshot, selected, setSelected, mapLocked=false }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (value: string) => void;
  mapLocked?:boolean
}) {
  const [result, setResult] = useState<LogTail | null>(null);
  const [error, setError] = useState("");
  const [query, setQuery] = useState("");
  const [level, setLevel] = useState<"all" | "info" | "warning" | "error">("all");
  const [generation, setGeneration] = useState(0);
  const [follow,setFollow]=useState(false);
  useEffect(() => {
    let active = true;
    let timer:ReturnType<typeof setTimeout>|undefined;
    // Each effect owns its exact map and generation. Read-only follow waits
    // for the previous response before the next tick, including slow disks.
    setResult(null); setError("");
    if (!selected) return;
    const read=async()=>{
      try{
        const data=await managerBridge.logTail(selected);
        if(!active)return;
        if(data.mapId.toLowerCase()!==selected.toLowerCase()||
           data.evidence!=="SANITIZED_LOCAL_LOG_TAIL_NOT_SERVER_HEALTH")
          throw new Error("UM-LOG-OTHER-MAP");
        setResult(data);setError("");
      }catch{
        if(active){
          setResult(null);
          setError("A helyi ASA napló nem olvasható, vagy a válasz másik maphoz tartozik. Más szerver naplóját nem jelenítjük meg.");
        }
      }finally{
        if(active&&follow)timer=setTimeout(()=>{void read();},5000);
      }
    };
    void read();
    return () => { active=false;if(timer!==undefined)clearTimeout(timer); };
  }, [selected, generation, follow]);
  // Render-time scope check also covers the render before effect cleanup.
  const visibleResult=result?.mapId.toLowerCase()===selected.toLowerCase()?result:null;
  const filtered = useMemo(() => (visibleResult?.lines ?? []).filter(row =>
    (level === "all" || row.level === level) &&
    row.content.toLocaleLowerCase("hu-HU").includes(query.toLocaleLowerCase("hu-HU"))
  ), [visibleResult, query, level]);
  const logStats=useMemo(()=>{
    const rows=visibleResult?.lines??[];
    return {
      loaded:rows.length,
      warnings:rows.filter(line=>line.level==="warning").length,
      errors:rows.filter(line=>line.level==="error").length,
      filtered:filtered.length
    };
  },[visibleResult,filtered]);
  return <div className="premium-log-page">
    <div className="page-intro"><span className="eyebrow">LOCAL ASA / SAFE LOG TAIL</span><h1>Naplóközpont</h1>
      <p>Legfeljebb 300, a meglévő C# által titokszűrt naplósor — csak regisztrált helyi mapról. Nem jelent ASA állapot- vagy mentési bizonyítékot.</p></div>
    <div className="premium-log-metrics">
      <div><span><FileText size={19}/></span><b>{logStats.loaded}</b><small>Olvasott naplósor</small></div>
      <div><span><Search size={19}/></span><b>{logStats.filtered}</b><small>Szűrésben látható</small></div>
      <div><span><AlertTriangle size={19}/></span><b>{logStats.warnings}</b><small>Figyelmeztetés</small></div>
      <div><span><Shield size={19}/></span><b>{logStats.errors}</b><small>Hiba</small></div>
    </div>
    <div className="log-toolbar premium-log-toolbar">
      <label className="log-select-label">Helyi map
        {mapLocked
          ?<strong aria-label="Napló forrásmapja">{snapshot.local.maps.find(m=>m.id===selected)?.name??"Nem található"}</strong>
          :<select aria-label="Napló mapja" value={selected} onChange={e => setSelected(e.target.value)}>
            {snapshot.local.maps.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
          </select>}
      </label>
      <label className="log-select-label">Szint
        <select aria-label="Naplószint" value={level} onChange={e => setLevel(e.target.value as typeof level)}>
          <option value="all">Összes</option><option value="info">Információ</option>
          <option value="warning">Figyelmeztetés</option><option value="error">Hiba</option>
        </select>
      </label>
      <label className="log-select-label log-search-label">Keresés
        <input aria-label="Keresés a naplóban" value={query} maxLength={128}
          onChange={e => setQuery(e.target.value)} placeholder="Szöveg szűrése..."/>
      </label>
      <label className="log-select-label"><span>Élő követés (5 mp)</span>
        <input type="checkbox" aria-label="Napló élő követése" checked={follow}
          onChange={e=>setFollow(e.target.checked)}/>
      </label>
      <button className="icon-button" aria-label="Napló újraolvasása" onClick={() => setGeneration(x => x+1)}><RefreshCw size={18}/></button>
    </div>
    <div className="premium-log-view">
    <Panel title={visibleResult ? `Naplórészlet · ${visibleResult.mapName}` : "Naplórészlet"}
      action={visibleResult && <span className="muted tiny">Módosítva: {observedAt(visibleResult.lastModifiedUtc)} · {visibleResult.truncated ? "Részlet (levágva)" : "Teljes olvasott rész"}</span>}>
      {error && <div role="status" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      {!selected && <div className="empty">Nincs kiválasztott helyi map.</div>}
      {selected && !visibleResult && !error && <div className="empty">Naplósorok betöltése...</div>}
      {visibleResult && <div className="log-list premium-log-list" role="log" aria-label="Titokszűrt helyi napló">
        {filtered.length===0 && <div className="empty">Nincs a szűrőnek megfelelő naplósor.</div>}
        {filtered.map(row => <div key={row.number} className={`log-row log-${row.level}`}>
          <span className="log-number">{row.number}</span><span className="log-level">{row.level}</span>
          <span className="log-message">{row.content}</span>
        </div>)}
      </div>}
      <div className="warning-inline"><Shield size={17}/> Titokszűrt, korlátozott méretű naplórészlet. A bejegyzés önmagában nem igazolja a SaveComplete vagy RCON művelet sikerét.</div>
    </Panel>
    </div>
  </div>;
}
