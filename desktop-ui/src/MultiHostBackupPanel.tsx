import {useEffect,useState} from "react";
import {AlertTriangle,Archive,CheckCircle2,FolderOpen,HardDriveDownload,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type MultiHostClusterBackupPreview,
  type MultiHostClusterRestorePreview,type MultiHostClusterBackupStatus,
  type MultiHostRestoreRecoveryListing,type MultiHostRestoreRecoveryPreview} from "./bridge";

export function MultiHostBackupPanel(){
  const [backupPreview,setBackupPreview]=useState<MultiHostClusterBackupPreview|null>(null);
  const [restorePreview,setRestorePreview]=useState<MultiHostClusterRestorePreview|null>(null);
  const [recoveryListing,setRecoveryListing]=useState<MultiHostRestoreRecoveryListing|null>(null);
  const [recoveryCandidateName,setRecoveryCandidateName]=useState("");
  const [recoveryPreview,setRecoveryPreview]=useState<MultiHostRestoreRecoveryPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [jobId,setJobId]=useState("");
  const [jobKind,setJobKind]=useState<"backup"|"restore"|"">("");
  const [status,setStatus]=useState<MultiHostClusterBackupStatus|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");

  useEffect(()=>{
    if(!jobId||status?.state==="completed"||status?.state==="needsOperator")return;
    const poll=()=>{
      const read=jobKind==="restore"?
        managerBridge.multiHostRestoreStatus(jobId):managerBridge.multiHostBackupStatus(jobId);
      void read.then(setStatus).catch(()=>setError("A művelet állapota nem olvasható; az archívumot ne használd ellenőrzés nélkül."));
    };
    poll();
    const timer=window.setInterval(poll,1500);
    return ()=>window.clearInterval(timer);
  },[jobId,jobKind,status?.state]);

  async function previewBackup(){
    if(busy||jobId&&status?.state!=="completed"&&status?.state!=="needsOperator")return;
    setBusy(true);setError("");setNotice("");setApproved(false);
    setBackupPreview(null);setRestorePreview(null);setRecoveryListing(null);setRecoveryCandidateName("");setRecoveryPreview(null);setStatus(null);setJobId("");setJobKind("");
    try{setBackupPreview(await managerBridge.multiHostBackupPreview());}
    catch{setError("A kiválasztott offline katalógus, HostRegistry vagy ClusterRegistry nem egyezik, illetve a forrás érinthet védett ASA/Store útvonalat.");}
    finally{setBusy(false);}
  }
  async function previewRestore(){
    if(busy||jobId&&status?.state!=="completed"&&status?.state!=="needsOperator")return;
    setBusy(true);setError("");setNotice("");setApproved(false);
    setBackupPreview(null);setRestorePreview(null);setRecoveryListing(null);setRecoveryCandidateName("");setRecoveryPreview(null);setStatus(null);setJobId("");setJobKind("");
    try{setRestorePreview(await managerBridge.multiHostRestorePreview());}
    catch{setError("Az offline multi-host archívum nem választható ki vagy nem ment át az integritásellenőrzésen.");}
    finally{setBusy(false);}
  }
  async function previewRestoreReplacement(){
    if(busy||jobId&&status?.state!=="completed"&&status?.state!=="needsOperator")return;
    setBusy(true);setError("");setNotice("");setApproved(false);
    setBackupPreview(null);setRestorePreview(null);setRecoveryListing(null);setRecoveryCandidateName("");setRecoveryPreview(null);setStatus(null);setJobId("");setJobKind("");
    try{setRestorePreview(await managerBridge.multiHostRestoreReplacePreview());}
    catch{setError("A kijelölt archívum vagy TEMP sandbox nem ment át az integritás- és tulajdonjog-ellenőrzésen.");}
    finally{setBusy(false);}
  }
  async function listRestoreRecovery(){
    if(busy||jobId&&status?.state!=="completed"&&status?.state!=="needsOperator")return;
    setBusy(true);setError("");setNotice("");setApproved(false);
    setBackupPreview(null);setRestorePreview(null);setRecoveryListing(null);setRecoveryCandidateName("");setRecoveryPreview(null);setStatus(null);setJobId("");setJobKind("");
    try{setRecoveryListing(await managerBridge.multiHostRestoreRecoveryList());}
    catch{setError("A TEMP restore tranzakciók listája nem olvasható.");}
    finally{setBusy(false);}
  }
  async function previewRestoreRecovery(){
    if(busy||!recoveryCandidateName)return;
    setBusy(true);setError("");setNotice("");setApproved(false);setRecoveryPreview(null);
    try{setRecoveryPreview(await managerBridge.multiHostRestoreRecoveryPreview(recoveryCandidateName));}
    catch{setError("A kiválasztott TEMP tranzakció naplója nem olvasható vagy a sandbox nem biztonságos.");}
    finally{setBusy(false);}
  }
  async function applyBackup(){
    if(busy||!backupPreview||!approved)return;
    setBusy(true);setError("");setNotice("");
    try{
      const started=await managerBridge.multiHostBackupApply(backupPreview.token);
      setJobId(started.jobId);setJobKind("backup");setBackupPreview(null);setApproved(false);
      setNotice("Az elkülönített offline másolatok archiválása elindult. A futó ASA/Kova és a Store-v1 források nem módosulnak.");
    }catch{setError("A natív Manager elutasította a mentés-jóváhagyást vagy a registry időközben megváltozott.");}
    finally{setBusy(false);}
  }
  async function applyRestore(){
    if(busy||!restorePreview||!approved)return;
    setBusy(true);setError("");setNotice("");
    try{
      const replaces=restorePreview.restoreTarget==="EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK";
      const started=replaces
        ?await managerBridge.multiHostRestoreReplaceApply(restorePreview.token)
        :await managerBridge.multiHostRestoreApply(restorePreview.token);
      setJobId(started.jobId);setJobKind("restore");setRestorePreview(null);setApproved(false);
      setNotice(replaces
        ?"A TEMP sandbox cseréje elindult; a korábbi ellenőrzött sandbox rollback-másolata megmarad. Éles adat nem íródik."
        :"Az ellenőrzött archívum kibontása új TEMP tesztsandboxba indult. Éles map nem áll vissza.");
    }catch{setError("A TEMP sandbox restore indítását a natív Manager elutasította.");}
    finally{setBusy(false);}
  }
  async function applyRestoreRecovery(){
    if(busy||!recoveryPreview?.token||!approved)return;
    setBusy(true);setError("");setNotice("");
    try{
      const started=await managerBridge.multiHostRestoreRecoveryApply(recoveryPreview.token);
      setJobId(started.jobId);setJobKind("restore");setRecoveryListing(null);setRecoveryCandidateName("");setRecoveryPreview(null);setApproved(false);
      setNotice("A kiválasztott TEMP sandbox rollbackje elindult; az újabb példány megőrzött quarantine mappába kerül.");
    }catch{setError("A TEMP rollback jóváhagyását vagy a tranzakció aktuális állapotát a natív Manager elutasította.");}
    finally{setBusy(false);}
  }

  const finished=status?.state==="completed"||status?.state==="needsOperator";
  const MiB=(bytes:number)=>`${(bytes/1024/1024).toFixed(1)} MiB`;
  return <section className="premium-panel uk-multihost-backup" aria-label="N-host mentés és izolált restore">
    <div className="panel-head"><h3><Archive size={18}/> N-host / többclusteres mentés</h3>
      <span className="mod-tag amber">Verziózott · SHA-256</span></div>
    <p className="muted small">Csak előre elkészített, helyi offline másolatokat és a hozzájuk tartozó
      <code>multi-host-backup-catalog.json</code> fájlt dolgozza fel. A katalógus host-, map- és clusterazonosságát
      ellenőrzi a mostani regiszterekkel. Nem olvas éles map-/Store-útvonalról, nem küld RCON-t, és nem indít,
      állít le vagy módosít szerverfolyamatot.</p>
    <div className="warning-inline"><Shield size={16}/>
      Az archívum csak a byte-integritást igazolja. A Restore új TEMP sandboxot hozhat létre, vagy ellenőrzött TEMP sandboxot cserélhet úgy, hogy a korábbi állapot rollback-másolata megmarad. Az élő SaveComplete, Store-zár, karakterutazás és éles visszaállítás nincs igazolva.
    </div>
    <div className="draft-actions">
      <button type="button" className="secondary" disabled={busy||Boolean(jobId&&!finished)}
        onClick={()=>{void previewBackup();}}><FolderOpen size={16}/> Offline mentés előnézete</button>
      <button type="button" className="secondary" disabled={busy||Boolean(jobId&&!finished)}
        onClick={()=>{void previewRestore();}}><HardDriveDownload size={16}/> Archívum ellenőrzése / TEMP restore</button>
      <button type="button" className="secondary" disabled={busy||Boolean(jobId&&!finished)}
        onClick={()=>{void previewRestoreReplacement();}}><Shield size={16}/> TEMP sandbox cseréje / rollback előnézet</button>
      <button type="button" className="secondary" disabled={busy||Boolean(jobId&&!finished)}
        onClick={()=>{void listRestoreRecovery();}}>TEMP restore tranzakciók keresése / rollback</button>
    </div>
    {backupPreview&&<div className="historical-preview" role="region" aria-label="Multi-host mentés jóváhagyása">
      <h4>Mentési előnézet · {backupPreview.batchId.slice(0,8)}…</h4>
      <p>{backupPreview.hosts} host · {backupPreview.maps} map · {backupPreview.clusters} cluster ·
        {" "}{backupPreview.components} fájl · {MiB(backupPreview.bytes)}</p>
      <p className="muted small">A hostonkénti mentéskülönbség megengedett. A mentés nem írja felül az eredeti fájlokat.</p>
      <label><input type="checkbox" checked={approved} disabled={busy}
        onChange={e=>setApproved(e.target.checked)}/>
        Jóváhagyom az offline másolatok verziózott, ellenőrzött archívumba másolását.</label>
      <div className="draft-actions">
        <button type="button" className="primary" disabled={!approved||busy}
          onClick={()=>{void applyBackup();}}>Mentés létrehozása</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>{setBackupPreview(null);setApproved(false);}}>Mégsem</button>
      </div>
    </div>}
    {restorePreview&&<div className="historical-preview" role="region" aria-label="TEMP restore jóváhagyása">
      <h4>Restore előnézet · {restorePreview.batchId.slice(0,8)}…</h4>
      <p>{restorePreview.hosts} host · {restorePreview.maps} map · {restorePreview.clusters} cluster ·
        {" "}{restorePreview.components} fájl</p>
      {restorePreview.restoreTarget==="EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK"
        ?<p><b>Cél: {restorePreview.targetDirectoryName} TEMP sandbox.</b> Az előző ellenőrzött állapot rollback-másolata megmarad. Éles map, Store-v1, transfer-adat és szerverfolyamat nem változik.</p>
        :<p><b>Cél: új TEMP sandbox.</b> Regisztrált map, Store-v1, transfer-adat és éles szerverfájl nem íródik.</p>}
      <label><input type="checkbox" checked={approved} disabled={busy}
        onChange={e=>setApproved(e.target.checked)}/>
        {restorePreview.restoreTarget==="EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK"
          ?"Jóváhagyom ennek az ellenőrzött TEMP sandboxnak a cseréjét a korábbi állapot rollback-másolatának megtartásával."
          :"Jóváhagyom az ellenőrzött fájlok elkülönített TEMP sandboxba másolását."}</label>
      <div className="draft-actions">
        <button type="button" className="primary" disabled={!approved||busy}
          onClick={()=>{void applyRestore();}}>{restorePreview.restoreTarget==="EXISTING VERIFIED TEMP SANDBOX WITH PROTECTIVE ROLLBACK"
          ?"TEMP sandbox cseréje (rollback-másolat megtartásával)":"TEMP restore indítása"}</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>{setRestorePreview(null);setApproved(false);}}>Mégsem</button>
      </div>
    </div>}
    {recoveryListing&&<div className="historical-preview" role="region" aria-label="TEMP restore tranzakciók">
      <h4>Helyreállítható TEMP tranzakciók</h4>
      {recoveryListing.transactions.length===0
        ?<p>Nincs helyreállítási napló a TEMP mappában.</p>
        :<>
          <label>Tranzakció kiválasztása
            <select aria-label="Helyreállítandó TEMP tranzakció" value={recoveryCandidateName}
              disabled={busy} onChange={e=>{setRecoveryCandidateName(e.target.value);setRecoveryPreview(null);setApproved(false);}}>
              <option value="">Válassz egy tranzakciót</option>
              {recoveryListing.transactions.map(candidate=><option key={candidate.targetDirectoryName}
                value={candidate.targetDirectoryName}>{candidate.targetDirectoryName} · {candidate.phase}
                {candidate.canRollback?" · rollback ellenőrizhető":" · kézi felülvizsgálat"}</option>)}
            </select>
          </label>
          <p className="muted small">A lista csak a TEMP naplóneveket olvassa. A teljes ellenőrzés a kiválasztott napló SHA-256 és sandbox-validációjával történik.</p>
          <div className="draft-actions"><button type="button" className="secondary"
            disabled={!recoveryCandidateName||busy} onClick={()=>{void previewRestoreRecovery();}}>
            Kiválasztott napló teljes ellenőrzése</button></div>
        </>}
      {recoveryListing.truncated&&<p role="status">A lista 128 tranzakciónál csonkolt; csak kiválasztás után történik tartalmi ellenőrzés.</p>}
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>{setRecoveryListing(null);setRecoveryCandidateName("");setRecoveryPreview(null);setApproved(false);}}>Mégsem</button>
    </div>}
    {recoveryPreview&&<div className="historical-preview" role="region" aria-label="TEMP restore recovery jóváhagyása">
      <h4>Restore tranzakció · {recoveryPreview.phase}</h4>
      <p><b>Cél:</b> {recoveryPreview.targetDirectoryName}</p>
      <p>{recoveryPreview.reason}</p>
      {recoveryPreview.protectiveCopyName&&<p>Védőmásolat: {recoveryPreview.protectiveCopyName}</p>}
      {recoveryPreview.quarantinedReplacementName&&<p>Quarantine: {recoveryPreview.quarantinedReplacementName}</p>}
      {recoveryPreview.canRollback&&recoveryPreview.token
        ?<><label><input type="checkbox" checked={approved} disabled={busy}
            onChange={e=>setApproved(e.target.checked)}/>
            Jóváhagyom kizárólag a kiválasztott TEMP sandbox rollbackjét.</label>
          <div className="draft-actions"><button type="button" className="primary" disabled={!approved||busy}
            onClick={()=>{void applyRestoreRecovery();}}>TEMP sandbox rollback indítása</button></div></>
        :<p>A napló nem kínál biztonságos rollbacket; automatikus módosítás nem történt.</p>}
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>{setRecoveryPreview(null);setApproved(false);}}>Mégsem</button>
    </div>}
    {jobId&&<div className="uk-rcon-receipt" role="status">
      <strong>{jobKind==="restore"?"TEMP restore":"Offline backup"} állapot: {status?.state||"ellenőrzés folyamatban"}</strong>
      <p>{status?.stage||"NATÍV MŰVELET INDÍTÁSA"}</p>
      {status?.state==="completed"&&<p><CheckCircle2 size={16}/>
        {status.hosts} host, {status.maps} map, {status.clusters} cluster, {status.components} fájl SHA-256 ellenőrizve.
        {status.artifactName?` Cél: ${status.artifactName}.`:""}</p>}
      {status?.state==="completed"&&status.rollbackArtifactName&&<p>Rollback-másolat: {status.rollbackArtifactName}</p>}
      {status?.state==="needsOperator"&&
        status.stage==="TEMP_SANDBOX_CONCURRENT_MANAGER_NO_WRITES"&&
        <p><AlertTriangle size={16}/>
          Másik Manager-folyamat már birtokolja ezt a TEMP restore-tranzakciót.
          Az ütköző művelet nem írt fájlt és nem indított rollbacket.
          Ellenőrizd a másik folyamat állapotát; automatikus újrapróbálás nincs.</p>}
      {status?.state==="needsOperator"&&
        status.stage!=="TEMP_SANDBOX_CONCURRENT_MANAGER_NO_WRITES"&&
        <p><AlertTriangle size={16}/>
          A művelet nem fejeződött be ellenőrzötten. A részleges archívum vagy TEMP stage kézi felülvizsgálatot igényel; nincs automatikus újrapróbálás.</p>}
      {!finished&&<button className="secondary" type="button" disabled={busy}
        onClick={()=>{const read=jobKind==="restore"?managerBridge.multiHostRestoreStatus(jobId):managerBridge.multiHostBackupStatus(jobId);
          void read.then(setStatus).catch(()=>setError("A státusz nem elérhető."));}}>
        <RefreshCw size={15}/> Státusz frissítése</button>}
      {finished&&<button type="button" className="secondary" disabled={busy}
        onClick={()=>{setJobId("");setJobKind("");setStatus(null);setNotice("");setError("");}}>Új művelet</button>}
    </div>}
    {notice&&<p className="muted small" role="status">{notice}</p>}
    {error&&<p className="error-inline" role="alert"><AlertTriangle size={16}/>{error}</p>}
  </section>;
}
