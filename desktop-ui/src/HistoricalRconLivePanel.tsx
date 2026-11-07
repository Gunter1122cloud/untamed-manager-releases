import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,FileText,RefreshCw,Shield,Users} from "lucide-react";
import {managerBridge,type HistoricalRconLiveStatus,
  type HistoricalRconReadResponse} from "./bridge";

/**
 * Read-only original ACTIVE/DONE Store-v1 RCON on the original local ASA
 * process, not the fresh-map console and never a remote-host writer.
 */
export function HistoricalRconLivePanel({mapId,running}:{
  mapId:string;running:boolean
}){
  const [status,setStatus]=useState<HistoricalRconLiveStatus|null>(null);
  const [result,setResult]=useState<HistoricalRconReadResponse|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const refresh=async()=>{
    setBusy(true);setError("");setResult(null);
    try{setStatus(await managerBridge.historicalRconLiveStatus(mapId));}
    catch{setStatus(null);setError("A történeti Store-v1 RCON tulajdonosa nem ellenőrizhető.");}
    finally{setBusy(false);}
  };
  useEffect(()=>{
    let active=true;
    setStatus(null);setResult(null);setError("");
    managerBridge.historicalRconLiveStatus(mapId).then(v=>{
      if(active)setStatus(v);
    }).catch(()=>{
      if(active)setError("A történeti RCON port tulajdonosa vagy a session nem igazolható.");
    });
    return ()=>{active=false;};
  },[mapId,running]);
  const read=async(kind:"players"|"game"|"chat")=>{
    if(busy||!status?.permitted)return;
    setBusy(true);setResult(null);setError("");
    try{
      const response=kind==="players"
        ?await managerBridge.historicalRconPlayers(mapId)
        :kind==="game"
        ?await managerBridge.historicalRconGameLog(mapId)
        :await managerBridge.historicalRconChatLog(mapId);
      setResult(response);
    }catch{
      setStatus(null);
      setError("A Store-v1 peer/session, a valódi Windows PID vagy a "+
        "hozzá tartozó TCP RCON-port időközben megváltozott. "+
        "Nem küldtünk további parancsot.");
    }finally{setBusy(false);}
  };
  return <section className="glass panel historical-rcon-live"
    aria-label="Eredeti Store-v1 RCON konzol">
    <div className="panel-head"><h2><Users size={18}/> Eredeti Store-v1 RCON</h2>
      <span className="mod-tag grey"><Shield size={13}/> Kizárólag olvasás</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">Csak a helyi, eredeti ACTIVE/DONE sessionhez
        kapcsolt ASA-folyamat és a Windows szerint hozzá tartozó
        RCON TCP-port kérdezhető le. A két gép közötti távoli parancsküldés
        és az önálló Store-írás tiltott.</p>
      <div className="details-line"><span>Natív RCON-tulajdonosi ellenőrzés</span>
        <b>{status?.permitted?"Igazolt · PID "+status.pid:
          status?"Nem igazolt":"Ellenőrzés alatt"}</b></div>
      <div className="settings-actions">
        <button className="secondary" type="button" disabled={busy}
          onClick={()=>{void refresh();}}>
          <RefreshCw size={16}/> Állapot frissítése
        </button>
        <button className="secondary" type="button"
          disabled={busy||!status?.permitted} onClick={()=>{void read("players");}}>
          <Users size={16}/> Játékoslista lekérdezése
        </button>
        <button className="secondary" type="button"
          disabled={busy||!status?.permitted} onClick={()=>{void read("game");}}>
          <FileText size={16}/> ASA játéknapló
        </button>
        <button className="secondary" type="button"
          disabled={busy||!status?.permitted} onClick={()=>{void read("chat");}}>
          <FileText size={16}/> ASA chatnapló
        </button>
      </div>
      {status?.permitted&&<div className="success-inline" role="status">
        <CheckCircle2 size={16}/> A helyi PID, TCP port, eredeti INI és a két peer
        Store-v1 állapota ellenőrizve.
      </div>}
      {result&&<div className="premium-log-view" role="region"
        aria-label="Eredeti Store-v1 RCON válasz">
        <div className="details-line"><span>RCON parancs</span><b>{result.command}</b></div>
        <pre className="premium-rcon-response">{result.response||"Nincs válaszadat."}</pre>
      </div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={17}/>{error}</div>}
      <div className="warning-inline"><Shield size={17}/>
        A játékoslista vagy a napló sikeres olvasása nem tanúsítja a mentést,
        a karakterutazást és a közös Store-restore biztonságát.
      </div>
    </div>
  </section>;
}
