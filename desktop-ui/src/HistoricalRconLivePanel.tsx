import {useEffect,useRef,useState} from "react";
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
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const generation=useRef(0);
  const belongs=(requested:string,epoch:number)=>
    currentMap.current===requested&&generation.current===epoch;
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const refresh=async()=>{
    if(busy)return;
    const requested=mapId,epoch=generation.current;
    setBusy(true);setError("");setResult(null);
    try{
      const next=await managerBridge.historicalRconLiveStatus(requested);
      if(!belongs(requested,epoch))return;
      if(next.mapId.toLowerCase()!==requested.toLowerCase())
        throw new Error("UM-STORE-RCON-STATUS-MAP-MISMATCH");
      setStatus(next);
    }catch{
      if(belongs(requested,epoch)){
        setStatus(null);
        setError("A Store-v1 RCON folyamata vagy helyi TCP-portja nem ellenőrizhető.");
      }
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  useEffect(()=>{
    let active=true;
    const epoch=++generation.current;
    setStatus(null);setResult(null);setError("");setBusy(false);
    managerBridge.historicalRconLiveStatus(mapId).then(v=>{
      if(!active||!belongs(mapId,epoch))return;
      if(v.mapId.toLowerCase()!==mapId.toLowerCase())
        throw new Error("UM-STORE-RCON-STATUS-MAP-MISMATCH");
      setStatus(v);
    }).catch(()=>{
      if(active&&belongs(mapId,epoch))
        setError("A helyi ASA PID/RCON-port vagy a kiválasztott map azonossága nem igazolható.");
    });
    return ()=>{active=false;++generation.current;};
  },[mapId,running]);
  const read=async(kind:"players"|"game"|"chat")=>{
    if(busy||!status?.permitted||status.mapId.toLowerCase()!==mapId.toLowerCase())
      return;
    const requested=mapId,epoch=generation.current;
    const command=kind==="players"?"ListPlayers":kind==="game"?"GetGameLog":"GetChat";
    setBusy(true);setResult(null);setError("");
    try{
      const response=kind==="players"
        ?await managerBridge.historicalRconPlayers(requested)
        :kind==="game"
        ?await managerBridge.historicalRconGameLog(requested)
        :await managerBridge.historicalRconChatLog(requested);
      if(!belongs(requested,epoch))return;
      if(response.mapId.toLowerCase()!==requested.toLowerCase()||
         response.command!==command||!response.readOnly||response.storeTouched)
        throw new Error("UM-STORE-RCON-REPLY-IDENTITY-MISMATCH");
      setResult(response);
    }catch{
      if(belongs(requested,epoch)){
        setStatus(null);
        setError("A helyi ASA PID, RCON-port vagy a válasz mapazonossága "+
          "nem igazolható. Új parancs nem indult.");
      }
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  return <section className="glass panel historical-rcon-live"
    aria-label="Eredeti Store-v1 RCON konzol">
    <div className="panel-head"><h2><Users size={18}/> Eredeti Store-v1 RCON</h2>
      <span className="mod-tag grey"><Shield size={13}/> Kizárólag olvasás</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">A Managerhez átvett map saját helyi ASA-folyamata
        és a hozzá tartozó ellenőrzött RCON TCP-port kérdezhető le.
        A másik gép elérhetősége nem szükséges a helyi olvasáshoz;
        távoli parancsküldés vagy Store-írás nem történik.</p>
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
        <CheckCircle2 size={16}/> A kiválasztott map helyi ASA PID-je,
        TCP RCON-portja és az INI azonosítva. A kétgépes karakterutazás külön ellenőrzendő.
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
