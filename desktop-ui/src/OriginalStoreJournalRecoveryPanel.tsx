import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,ShieldCheck} from "lucide-react";
import {managerBridge,type AsaLifecycleJournal,
  type OriginalStoreLifecycleRecoveryPreview} from "./bridge";

const approval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS";

/**
 * Manual, one-time recovery of a historical ACTIVE Store-v1 Launch journal.
 * NO process, RCON command, original Kova Store file, or game data is changed.
 * A prior command outcome stays UNKNOWN even after the audit review.
 */
export function OriginalStoreJournalRecoveryPanel({mapId,journal,onRecovered}:{
  mapId:string;journal:AsaLifecycleJournal;onRecovered:()=>Promise<void>|void;
}){
  const [prepared,setPrepared]=useState<OriginalStoreLifecycleRecoveryPreview|null>(null);
  const [typed,setTyped]=useState("");
  const [confirmed,setConfirmed]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [receipt,setReceipt]=useState(false);
  useEffect(()=>{
    setPrepared(null);setTyped("");setConfirmed(false);setError("");setReceipt(false);
  },[mapId,journal.operationId]);
  const inspect=async()=>{
    setBusy(true);setError("");setPrepared(null);setTyped("");setConfirmed(false);
    try{setPrepared(await managerBridge.originalStoreLifecycleRecoveryPreview(mapId));}
    catch(e){setError(e instanceof Error?e.message:"UM-RECOVERY-PREVIEW-FAILED");}
    finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!prepared||typed!==approval||!confirmed||busy)return;
    const token=prepared.token;
    setBusy(true);setPrepared(null);setError("");
    try{
      const result=await managerBridge.originalStoreLifecycleRecoveryApply(
        mapId,token,typed);
      if(result.state!=="operatorReviewed"||!result.originalJournalAudited||
         !result.previousOutcomeStillUnknown||result.processStarted||
         result.processStopped||result.rconCommandSent)
        throw new Error("UM-RECOVERY-INVALID-NATIVE-RECEIPT");
      setReceipt(true);
      await onRecovered();
    }catch(e){setError(e instanceof Error?e.message:"UM-RECOVERY-APPLY-FAILED");}
    finally{setBusy(false);}
  };
  return <section className="glass panel" role="region"
    aria-label="Store-v1 beragadt indítás helyreállítása">
    <div className="panel-head"><h2><ShieldCheck size={18}/>
      Beragadt Store-v1 indítás · ellenőrzött helyreállítás</h2></div>
    <div className="panel-inner">
      <p className="warning-inline"><AlertTriangle size={16}/>
        A korábbi <b>{journal.kind??"Launch"} / {journal.phase??"NeedsOperator"}</b>
        művelet eredménye nem ismert. Az előző parancs NEM lesz újraküldve,
        és az auditnapló NEM lesz törölve.</p>
      <p className="muted small">Napóazonosító: {journal.operationId??"nem olvasható"}.
        Az eredeti ASA PID, indításiidő, RCON-porttulajdonos, a Kova ACTIVE/DONE
        session és a jelenlegi Manager-profil friss igazolása kötelező.</p>
      {!prepared&&!receipt&&<button type="button" className="secondary"
        disabled={busy} onClick={()=>{void inspect();}}>
        {busy?"Ellenőrzés…":"Helyreállítás előellenőrzése"}
      </button>}
      {prepared&&<div className="import-preview" aria-label="Store-v1 napló-helyreállítás előnézete">
        <p><b>{prepared.mapName}</b> · PID {prepared.pid} · RCON {prepared.rconPort}</p>
        <p className="muted small">Az előző művelet kimenetele továbbra is bizonytalan.
          Az audit után a későbbi műveletek új, önálló jóváhagyással indíthatók.</p>
        <label><input type="checkbox" checked={confirmed} disabled={busy}
          onChange={e=>setConfirmed(e.target.checked)}/>
          Tudomásul veszem a korábbi parancs ismeretlen kimenetelét.
          Nem kérek automatikus ismétlést.</label>
        <label>Gépeld be: <b>{approval}</b>
          <input aria-label="Store-v1 napló helyreállítási jóváhagyás"
            disabled={busy} value={typed}
            onChange={e=>setTyped(e.target.value)}/>
        </label>
        <button className="primary" type="button"
          disabled={!confirmed||typed!==approval||busy}
          onClick={()=>{void apply();}}>Auditált helyreállítás végrehajtása</button>
        <button className="secondary" type="button" disabled={busy}
          onClick={()=>{setPrepared(null);setTyped("");setConfirmed(false);}}>
          Mégsem
        </button>
      </div>}
      {receipt&&<div className="success-inline" role="status"><CheckCircle2 size={16}/>
        A korábbi napló auditmásolata megmaradt, kezelői felülvizsgálata kész.
        Új parancsot nem küldtem.
      </div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={16}/>{error}. Nem történt automatikus ismétlés.
      </div>}
    </div>
  </section>;
}
