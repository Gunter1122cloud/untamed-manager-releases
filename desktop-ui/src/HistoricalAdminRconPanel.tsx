import {useEffect,useRef,useState,type FormEvent} from "react";
import {AlertTriangle,CheckCircle2,LockKeyhole,Send,Shield} from "lucide-react";
import {managerBridge,type HistoricalAdminRconPreview,
  type HistoricalAdminRconReceipt} from "./bridge";

type Action="broadcast"|"serverChat"|"wildWipe"|"custom";

/**
 * Operator-approved original Store-v1 admin actions. The normal read-only
 * player/log view remains separate. Commands may affect the game world,
 * but never acquire, release or rewrite original Store writer/session locks.
 * SaveWorld/DoExit must be handled by the independent protected lifecycle.
 */
export function HistoricalAdminRconPanel({mapId,running,initialAction="broadcast"}:{
  mapId:string;running:boolean;initialAction?:Action
}){
  const [action,setAction]=useState<Action>(initialAction);
  const [message,setMessage]=useState("");
  const [preview,setPreview]=useState<HistoricalAdminRconPreview|null>(null);
  const [approved,setApproved]=useState("");
  const [confirmed,setConfirmed]=useState(false);
  const [reply,setReply]=useState<HistoricalAdminRconReceipt|null>(null);
  // No approval, response, or in-flight request may cross the selected map.
  // The native bridge is authoritative for one-shot command delivery.
  const currentMap=useRef(mapId);
  currentMap.current=mapId;
  const generation=useRef(0);
  const belongs=(requested:string,epoch:number)=>
    currentMap.current===requested&&generation.current===epoch;
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  useEffect(()=>{
    ++generation.current;
    setAction(initialAction);setMessage("");setPreview(null);
    setApproved("");setConfirmed(false);setReply(null);setError("");setBusy(false);
    return ()=>{++generation.current;};
  },[mapId,initialAction]);
  const change=()=>{setPreview(null);setApproved("");setConfirmed(false);setReply(null);setError("");};
  const inspect=async(e:FormEvent)=>{
    e.preventDefault();
    if(!running||busy)return;
    const requested=mapId,epoch=generation.current;
    setBusy(true);setError("");setReply(null);setPreview(null);
    try{
      const result=await managerBridge.historicalAdminPreview(requested,action,message);
      if(!belongs(requested,epoch))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         result.action!==action||result.dispatched)
        throw new Error("UM-STORE-ADMIN-PREVIEW-IDENTITY-MISMATCH");
      setPreview(result);setApproved("");setConfirmed(false);
    }catch{
      if(!belongs(requested,epoch))return;
      setPreview(null);
      setError("A kiválasztott map admin RCON előellenőrzése vagy "+
        "Windows PID/RCON azonosítója nem igazolható. Nem küldtünk parancsot.");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  const send=async()=>{
    if(!preview||preview.mapId.toLowerCase()!==mapId.toLowerCase()||
       !confirmed||approved!==preview.approvalWord||busy)return;
    const token=preview.token,requested=mapId,epoch=generation.current,
      approvedCommand=preview.command;
    setPreview(null);setApproved("");setConfirmed(false);
    setBusy(true);setError("");setReply(null);
    try{
      const result=await managerBridge.historicalAdminSend(token,preview.approvalWord);
      if(!belongs(requested,epoch))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         result.command!==approvedCommand||!result.commandSent||!result.oneShot||
         result.originalStoreMetadataWritten)
        throw new Error("UM-STORE-ADMIN-SEND-RECEIPT-MISMATCH");
      setReply(result);
    }catch{
      if(belongs(requested,epoch))
        setError("Az egyszeri admin RCON parancs eredménye nem igazolható. "+
          "A TCP-küldés már megtörténhetett; ne ismételd meg "+
          "a map és a parancs tényleges állapotának ellenőrzése nélkül.");
    }finally{if(belongs(requested,epoch))setBusy(false);}
  };
  return <section className="glass panel historical-admin-rcon"
      aria-label="Eredeti Store-v1 admin RCON">
    <div className="panel-head">
      <h2><LockKeyhole size={18}/> Eredeti Store-v1 admin RCON</h2>
      <span className="mod-tag amber"><Shield size={13}/> Helyi · kézi jóváhagyás</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">Csak az adott map ellenőrzött helyi ASA PID-jéhez
        tartozó RCON TCP-porthoz küldhető adminparancs. Minden parancs külön
        natív jóváhagyást kap, automatikus ismétlés nélkül.
        A SaveWorld és DoExit kizárólag az elkülönített mentés/leállítás részen érhető el.</p>
      <form className="settings-form" onSubmit={e=>{void inspect(e);}}>
        <label htmlFor="original-admin-action">Admin RCON parancstípus</label>
        <select id="original-admin-action" value={action}
          disabled={busy||!!preview} onChange={e=>{
            setAction(e.target.value as Action);setMessage("");change();
          }}>
          <option value="broadcast">Üzenet minden játékosnak</option>
          <option value="serverChat">Szerver chat</option>
          <option value="wildWipe">Vad dinók törlése</option>
          <option value="custom">Egyedi adminparancs</option>
        </select>
        {action!=="wildWipe"&&<>
          <label htmlFor="original-admin-message">
            {action==="custom"?"Adminparancs":"Küldendő üzenet"}
          </label>
          <textarea id="original-admin-message" value={message}
            placeholder={action==="custom"?"Például: ListTribes":"Szerverüzenet"}
            disabled={busy||!!preview} maxLength={240} rows={3}
            onChange={e=>{setMessage(e.target.value);change();}}/>
        </>}
        <button type="submit" className="secondary"
          disabled={!running||busy||!!preview||
            (action!=="wildWipe"&&!message.trim())}>
          <Shield size={16}/> Eredeti adminparancs ellenőrzése
        </button>
      </form>
      {preview&&<div className="warning-inline" role="region"
        aria-label="Store-v1 adminparancs végső jóváhagyása">
        <AlertTriangle size={17}/>
        <div>
          <p><b>{preview.mapName}</b> · egyszeri natív RCON parancs:</p>
          <pre className="premium-rcon-response">{preview.command}</pre>
          <p>{preview.dangerous?
            "Figyelem: ez a parancs módosíthatja a játék állapotát!":
            "A parancs egyetlen alkalommal lesz elküldve."}</p>
          <label><input type="checkbox" checked={confirmed}
              onChange={e=>setConfirmed(e.target.checked)}/>
            Jóváhagyom a helyi Store-v1 RCON műveletet.</label>
          <label htmlFor="original-admin-confirm">Írd be pontosan: {preview.approvalWord}</label>
          <input id="original-admin-confirm" value={approved}
            autoComplete="off" spellCheck={false} maxLength={32}
            onChange={e=>setApproved(e.target.value)}/>
          <div className="settings-actions">
            <button className="primary" type="button"
              disabled={busy||preview.mapId.toLowerCase()!==mapId.toLowerCase()||
                !confirmed||approved!==preview.approvalWord}
              onClick={()=>{void send();}}>
              <Send size={16}/> Adminparancs egyszeri elküldése
            </button>
            <button className="secondary" type="button" disabled={busy}
              onClick={()=>change()}>Mégse</button>
          </div>
        </div>
      </div>}
      {reply&&<div role="status" className="success-inline">
        <CheckCircle2 size={17}/> Egyszeri RCON-parancs elküldve: {reply.command}
      </div>}
      {reply?.response&&<div className="premium-log-view" role="region"
          aria-label="Store-v1 adminparancs válasza">
        <pre className="premium-rcon-response">{reply.response}</pre>
      </div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={17}/>{error}
      </div>}
      <div className="warning-inline">
        <Shield size={16}/> A RCON dispatch visszajelzése nem igazolja a
        mentés lezárását, a Store-v1 karakterutazást vagy a parancs játékbeli
        eredményét.
      </div>
    </div>
  </section>;
}
