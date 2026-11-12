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
    const expectedPair=prepared.pairedJournals??1;
    const oldPid=prepared.pid;
    setBusy(true);setPrepared(null);setError("");
    try{
      const result=await managerBridge.originalStoreLifecycleRecoveryApply(
        mapId,token,typed);
      if(result.state!=="operatorReviewed"||!result.originalJournalAudited||
         !result.previousOutcomeStillUnknown||result.processStarted||
         result.processStopped||result.rconCommandSent||
         (expectedPair===2&&(!result.restartJournalAudited||
           result.pairedJournalsReviewed!==2||result.oldRunningPid!==oldPid||
           result.restartMarkedSuccessful!==false)))
        throw new Error("UM-RECOVERY-INVALID-NATIVE-RECEIPT");
      setReceipt(true);
      try{await onRecovered();}
      catch{setError("UM-RECOVERY-FINISHED-UI-REFRESH-FAILED: az audit elkészült, csak a nézet frissítése nem sikerült; NE ismételd a műveletet.");}
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
      <p className="muted small">Műveleti napló: {journal.operationId??"nem olvasható"}
        {" · "}{journal.changedUtc??"időpont nem olvasható"}
        {" · "}{journal.blockingCode??"ismeretlen blokkoló"}.
        Az eredeti ASA PID, indításiidő, RCON-porttulajdonos, INI,
        a Kova ACTIVE/DONE session és a jelenlegi Manager-profil
        friss igazolása kötelező.</p>
      {journal.restartJournalExists&&<div className="warning-inline">
        <ShieldCheck size={16}/>
        <span><b>Összetartozó Restart-v1 napló:</b> {journal.restartId??"ismeretlen"}
          {" · "}{journal.restartPhase??"ismeretlen fázis"}
          {" · régi PID "}{journal.restartOldPid??"?"}
          {" · új PID "}{journal.restartNewPid??"?"}.
          {" "}{journal.restartClassification==="operatorReviewed"?
            "Az előző rendezésben már auditálva: nem sikeres restart.":
            "A régi ASA tovább fut; a korábbi restart kimenetele nem lesz sikeresnek minősítve."}
        </span>
      </div>}
      {!prepared&&!receipt&&<button type="button" className="secondary"
        disabled={busy} onClick={()=>{void inspect();}}>
        {busy?"Ellenőrzés…":"Helyreállítás előellenőrzése"}
      </button>}
      {prepared&&<div className="import-preview" aria-label="Store-v1 napló-helyreállítás előnézete">
        <p><b>{prepared.mapName}</b> · PID {prepared.pid} · RCON {prepared.rconPort}
          {" · "}{prepared.pairedJournals??1} napló felülvizsgálata</p>
        {prepared.restartId&&<p className="muted small">
          Restart-v1 {prepared.restartId} · {prepared.restartPhase} ·
          régi PID {prepared.restartOldPid}, új PID {prepared.restartNewPid}.
          Az eredeti ASA-folyamatot megtartjuk; nem lesz új Start, DoExit vagy SaveWorld.
        </p>}
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
        Az összetartozó műveleti naplók auditálva, a korábbi restart
        eredménye továbbra is bizonytalan. Új parancsot nem küldtem.
      </div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={16}/>{error}. Nem történt automatikus ismétlés.
      </div>}
    </div>
  </section>;
}
