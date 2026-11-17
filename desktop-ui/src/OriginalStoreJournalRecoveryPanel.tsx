import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,ShieldCheck} from "lucide-react";
import {managerBridge,type AsaLifecycleJournal,
  type OriginalStoreLifecycleRecoveryPreview} from "./bridge";

// The native journal review remains a two-stage, one-time operation with a
// fresh exact-PID/Store/RCON proof and durable original audit. The operator
// explicitly requests the WHOLE review with ONE button, rather than being
// forced through an unneeded text-entry workflow to clear local metadata.
// This never replays an uncertain ASA command or certifies a past restart.
const approval="STORE-V1 NAPLÓ HELYREÁLLÍTÁS";
const sourceApproval="FORRÁS ELTÉRÉS JÓVÁHAGYÁSA";

export function OriginalStoreJournalRecoveryPanel({mapId,journal,onRecovered}:{
  mapId:string;journal:AsaLifecycleJournal;onRecovered:()=>Promise<void>|void;
}){
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [receipt,setReceipt]=useState(false);
  const [verifiedPid,setVerifiedPid]=useState<number|null>(null);
  const [changedSource,setChangedSource]=
    useState<OriginalStoreLifecycleRecoveryPreview|null>(null);
  const [sourceConfirmation,setSourceConfirmation]=useState("");
  const inFlight=useRef(false);
  const currentMap=useRef(mapId);
  useEffect(()=>{
    currentMap.current=mapId;
    setError("");setReceipt(false);setVerifiedPid(null);
    setChangedSource(null);setSourceConfirmation("");
  },[mapId,journal.operationId,journal.restartId]);

  const recover=async(approveChangedSource=false)=>{
    if(inFlight.current||receipt)return;
    inFlight.current=true;
    setBusy(true);setError("");setVerifiedPid(null);
    try{
      // Preview performs the same strict native preflight as the old
      // separate step; it sends no commands or writes to original korábbi Manager.
      const proof=approveChangedSource&&changedSource?
        changedSource:
        await managerBridge.originalStoreLifecycleRecoveryPreview(mapId);
      if(currentMap.current!==mapId)return;
      const stopped=proof.reviewMode==="stopped";
      if(!proof.token||proof.pid<=0||
         proof.mapId.toLowerCase()!==mapId.toLowerCase()||
         (!stopped&&(!proof.exactWindowsProcessVerified||
           !proof.rconListenerOwnerVerified||
           !(proof.originalStoreSessionVerified||proof.approvedObsidianSourcePinned)))||
         (stopped&&(!proof.stoppedWindowsProcessVerified||
           !proof.rconListenerAbsent||!proof.historicalSelectedOwnerVerified||
           proof.pairedJournals!==2||!proof.restartId||
           proof.exactWindowsProcessVerified||proof.rconListenerOwnerVerified))||
         (proof.historicalSourceReapprovalRequired&&
           (!stopped||!proof.historicalApprovedFourFilesSha256||
            !proof.historicalCurrentFourFilesSha256||
            proof.historicalApprovedFourFilesSha256===
              proof.historicalCurrentFourFilesSha256))||
         !proof.priorCommandOutcomeUnknown||
         !proof.previousOperationWillNotBeRetried||
         (proof.pairedJournals===2&&!proof.restartId)||
         (proof.restartId&&(proof.restartOldPid!==proof.pid||
           proof.restartNewPid!==0)))
        throw new Error("UM-RECOVERY-PREVIEW-OWNER-NOT-VERIFIED");
      setVerifiedPid(proof.pid);
      if(proof.historicalSourceReapprovalRequired&&!approveChangedSource){
        setChangedSource(proof);
        setSourceConfirmation("");
        return; // Different old/current hashes require a second explicit click.
      }
      if(proof.historicalSourceReapprovalRequired&&
         (!changedSource||sourceConfirmation!==sourceApproval))
        throw new Error("UM-RECOVERY-SOURCE-REAPPROVAL-REQUIRED");
      setChangedSource(null); // Never keep a consumed or failed approval token.
      // Explicit click is consent for an audited LOCAL journal review, not
      // for an ASA/RCON command. Native applies a one-shot token and checks
      // source, exact old PID/time, and both journal identities AGAIN.
      const result=await managerBridge.originalStoreLifecycleRecoveryApply(
        mapId,proof.token,approval,
        proof.historicalSourceReapprovalRequired?sourceApproval:undefined);
      if(currentMap.current!==mapId)return;
      if(result.state!=="operatorReviewed"||
         (!result.originalJournalAudited&&!result.restartJournalAudited)||
         !result.previousOutcomeStillUnknown||
         result.storeBytesModified||result.processStarted||
         result.processStopped||result.rconCommandSent||
         result.noAutomaticRetry!==true||
         (proof.restartId&&(!result.restartJournalAudited||
           (stopped?result.oldStoppedPid!==proof.pid:
             result.oldRunningPid!==proof.pid)||
           result.restartMarkedSuccessful!==false))||
         (stopped&&result.reviewMode!=="stopped")||
         (proof.historicalSourceReapprovalRequired&&
           (!result.historicalSourceReapprovedForJournalOnly||
            !result.oldHistoricalHashPreserved))||
         (proof.pairedJournals===2&&result.pairedJournalsReviewed!==2))
        throw new Error("UM-RECOVERY-INVALID-NATIVE-RECEIPT");
      setReceipt(true);
      try{await onRecovered();}
      catch{
        setError("UM-RECOVERY-FINISHED-UI-REFRESH-FAILED: a helyi naplórendezés befejeződött, csak a nézet frissítése nem sikerült. Ne indítsd újra a műveletet.");
      }
    }catch(e){
      if(currentMap.current===mapId)
        setError(e instanceof Error?e.message:"UM-RECOVERY-FAILED");
    }finally{
      inFlight.current=false;
      if(currentMap.current===mapId)setBusy(false);
    }
  };

  return <section className="glass panel" role="region"
    aria-label="Store-v1 beragadt indítás helyreállítása">
    <div className="panel-head"><h2><ShieldCheck size={18}/>
      Beragadt Store-v1 indítás · helyi napló rendezése</h2></div>
    <div className="panel-inner">
      <p className="warning-inline"><AlertTriangle size={16}/>
        A korábbi <b>{journal.restartClassification==="unknownOutcome"&&
          journal.classification==="closed"?
          "Restart-v1 / "+(journal.restartPhase??"NeedsOperator"):
          (journal.kind??"Launch")+" / "+(journal.phase??"NeedsOperator")}</b>
        művelet kimenetele nem ismert. Nem lesz parancsismétlés, szerverindítás,
        leállítás, és az eredeti auditnapló megmarad.</p>
      <p className="muted small">Helyi műveleti napló: {journal.operationId??"nem olvasható"}
        {" · "}{journal.changedUtc??"időpont nem olvasható"}
        {" · "}{journal.blockingCode??"ismeretlen blokkoló"}.
        A gomb frissen ellenőrzi a Store-forrást és a naplók azonosítóját.
        Futó régi ASA esetén a pontos PID-et és RCON-tulajdonost,
        leállított régi ASA esetén a teljes folyamatcsalád és RCON-listener
        hiányát ellenőrzi. Ezután kizárólag a Manager naplóit rendezi.</p>
      {journal.restartJournalExists&&<div className="warning-inline">
        <ShieldCheck size={16}/>
        <span><b>Restart-v1 napló:</b> {journal.restartId??"ismeretlen"}
          {" · "}{journal.restartPhase??"ismeretlen"}
          {" · régi PID "}{journal.restartOldPid??"?"}
          {" · új PID "}{journal.restartNewPid??"?"}.
          A korábbi restart nem kap hamis sikeres állapotot.
        </span>
      </div>}
      {!receipt&&!changedSource&&<button type="button" className="primary" disabled={busy}
        onClick={()=>{void recover();}}>
        {busy?"Forrás, folyamat és naplók ellenőrzése…":
          "Ellenőrzés és helyi naplórendezés"}
      </button>}
      {!receipt&&changedSource&&<div className="warning-inline" role="group"
        aria-label="Történeti Store forráseltérés külön jóváhagyása">
        <AlertTriangle size={16}/>
        <div>
          <p><b>Eltér a korábbi jóváhagyott Store-v1 forráslenyomat.</b>
            Az aktuális hatmapos lock és az öt eredeti, helyi map azonosítója
            ellenőrzött, de ez nem bizonyítja a régi forrásbájtok egyezését.
            Csak a két helyi műveleti napló auditált rendezését hagyhatod jóvá,
            nem szerverindítást vagy Store-átalakítást.</p>
          <p className="muted small">Régi SHA-256:
            <code>{changedSource.historicalApprovedFourFilesSha256}</code>
          </p>
          <p className="muted small">Aktuális SHA-256:
            <code>{changedSource.historicalCurrentFourFilesSha256}</code>
          </p>
          <label htmlFor="source-reapproval-phrase">
            A külön jóváhagyáshoz írd be pontosan: <b>{sourceApproval}</b>
          </label>
          <input id="source-reapproval-phrase" value={sourceConfirmation}
            autoComplete="off" spellCheck={false}
            onChange={e=>setSourceConfirmation(e.target.value)}/>
          <div className="button-row">
            <button type="button" disabled={busy||sourceConfirmation!==sourceApproval}
              onClick={()=>{void recover(true);}}>
              {busy?"Újraellenőrzés…":"Forráseltérés és naplórendezés jóváhagyása"}
            </button>
            <button type="button" disabled={busy} onClick={()=>{
              setChangedSource(null);setSourceConfirmation("");
            }}>Mégse</button>
          </div>
        </div>
      </div>}
      {verifiedPid!==null&&!receipt&&<p className="muted small" role="status">
        Naplóban rögzített korábbi ASA PID: {verifiedPid}. A végső rendezés
        külön, friss natív ellenőrzést végez; nem küld RCON-parancsot.
      </p>}
      {receipt&&<div className="success-inline" role="status"><CheckCircle2 size={16}/>
        A Manager helyi naplói auditálva. A korábbi parancs kimenetele
        továbbra is bizonytalan; új parancsot nem küldtem. Az érintett
        vezérlés az új, aktuális előellenőrzés alapján használható.
      </div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={16}/>{error}. A korábbi parancsot nem ismételtük meg.
      </div>}
    </div>
  </section>;
}
