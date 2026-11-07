import {useEffect,useState,type FormEvent} from "react";
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
export function HistoricalAdminRconPanel({mapId,running}:{
  mapId:string;running:boolean
}){
  const [action,setAction]=useState<Action>("broadcast");
  const [message,setMessage]=useState("");
  const [preview,setPreview]=useState<HistoricalAdminRconPreview|null>(null);
  const [approved,setApproved]=useState("");
  const [confirmed,setConfirmed]=useState(false);
  const [reply,setReply]=useState<HistoricalAdminRconReceipt|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  useEffect(()=>{
    setAction("broadcast");setMessage("");setPreview(null);
    setApproved("");setConfirmed(false);setReply(null);setError("");
  },[mapId]);
  const change=()=>{setPreview(null);setApproved("");setConfirmed(false);setReply(null);setError("");};
  const inspect=async(e:FormEvent)=>{
    e.preventDefault();
    if(!running||busy)return;
    setBusy(true);setError("");setReply(null);
    try{
      const result=await managerBridge.historicalAdminPreview(mapId,action,message);
      setPreview(result);setApproved("");setConfirmed(false);
    }catch{
      setPreview(null);
      setError("Az admin RCON előellenőrzés sikertelen: az eredeti "+
        "Store-v1 session, helyi PID, RCON-port vagy a korábbi "+
        "műveleti napló nem igazolható. Nem küldtünk parancsot.");
    }finally{setBusy(false);}
  };
  const send=async()=>{
    if(!preview||!confirmed||approved!==preview.approvalWord||busy)return;
    const token=preview.token;
    setPreview(null);setApproved("");setConfirmed(false);
    setBusy(true);setError("");setReply(null);
    try{
      const result=await managerBridge.historicalAdminSend(token,preview.approvalWord);
      setReply(result);
    }catch{
      setError("Az eredeti Store-v1 adminparancs nem igazolható. "+
        "Az egyszer használatos jóváhagyás lejárt. "+
        "Ha a parancs elküldése közben szakadt meg, "+
        "ne ismételd meg ellenőrzés nélkül!");
    }finally{setBusy(false);}
  };
  return <section className="glass panel historical-admin-rcon"
      aria-label="Eredeti Store-v1 admin RCON">
    <div className="panel-head">
      <h2><LockKeyhole size={18}/> Eredeti Store-v1 admin RCON</h2>
      <span className="mod-tag amber"><Shield size={13}/> Helyi · kézi jóváhagyás</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">Csak a két peer alapján igazolt, helyi ASA PID-hez
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
              disabled={busy||!confirmed||approved!==preview.approvalWord}
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
