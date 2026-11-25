import {useEffect,useState} from "react";
import {managerBridge} from "./bridge";

type Status=Awaited<ReturnType<typeof managerBridge.steamRollingStatus>>;
export function RollingSteamSettings({mapId}:{mapId:string}){
  const [status,setStatus]=useState<Status|null>(null);
  const [error,setError]=useState("");
  const [busy,setBusy]=useState(false);
  const [risk,setRisk]=useState(false);
  const load=async()=>{try{setStatus(await managerBridge.steamRollingStatus());}catch(e){setError(String(e));}};
  useEffect(()=>{
    let live=true;
    const poll=()=>managerBridge.steamRollingStatus().then(s=>{if(live)setStatus(s);})
      .catch(e=>{if(live)setError(String(e));});
    void poll();const timer=setInterval(poll,5000);
    return ()=>{live=false;clearInterval(timer);};
  },[mapId]);
  const enabled=status?.settings.find(s=>s.mapId===mapId)?.enabled===true;
  const unresolved=status?.current&&(status.current.state==="needs-review"||
    status.current.state==="running"&&!status.busy);
  const change=async(value:boolean)=>{
    setBusy(true);setError("");
    try{await managerBridge.steamRollingSet(mapId,value,risk);await load();}
    catch(e){setError(String(e));}finally{setBusy(false);}
  };
  return <section className="glass panel" aria-label="Automatikus szerverfrissítés">
    <div className="panel-head"><h2>Automatikus SteamCMD-frissítés (sorban)</h2></div>
    <div className="panel-inner">
      <p>Alapból kikapcsolva. Az engedélyezett helyi mapok 15 percenként ellenőrzött új publikus build esetén egymás után frissülnek. A futó map 5 perces játékbeli figyelmeztetést, szabályos leállítást, ellenőrzött mentést és frissítést, majd ellenőrzött indítást kap. A már leállított map leállítva marad.</p>
      <p className="muted small">Csak önálló helyi telepítések. A másik host külön sor. A közös Store és pluginfájlok nem frissülnek automatikusan. A következő ASA kiadás megtörheti az API/plugin kompatibilitást.</p>
      <p role="status">Automatikus frissítés: <b>{enabled?"Bekapcsolva":"Kikapcsolva"}</b></p>
      {!enabled&&<label><input type="checkbox" checked={risk} disabled={busy||status?.busy===true}
        onChange={e=>setRisk(e.target.checked)}/> Elfogadom az automatikus szerverfrissítést ASA API használatakor is, a pluginok kompatibilitási kockázatával.</label>}
      <div className="button-row">
        <button type="button" className={enabled?"secondary":"primary"}
          disabled={busy||!status||!enabled&&(!status.runnerStarted||status.busy||!!unresolved)}
          onClick={()=>{void change(!enabled);}}>{enabled?"Automatikus frissítés kikapcsolása":"Automatikus frissítés bekapcsolása"}</button>
      </div>
      {status?.current&&<p role="status">Helyi sor: {status.current.state} · {status.current.stage} · {status.current.oldBuild} → {status.current.targetBuild}{status.current.error?" · "+status.current.error:""}</p>}
      {unresolved&&status?.current&&<><p className="warning-inline">Ellenőrizd a natív Stop, SteamCMD és Start naplókat, valamint az érintett map állapotát. A félbeszakadt frissítést nem ismételjük meg.</p>
        <button type="button" disabled={busy||status.busy} onClick={async()=>{
          setBusy(true);setError("");
          try{await managerBridge.steamRollingReview(status.current!.id);await load();}
          catch(e){setError(String(e));}finally{setBusy(false);}
        }}>Korábbi sor naplózása és minden automatikus frissítés kikapcsolása</button></>}
      {error&&<p role="alert" className="error-inline">{error}</p>}
    </div>
  </section>;
}
