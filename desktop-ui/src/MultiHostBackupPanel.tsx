import {useEffect,useState} from "react";
import {AlertTriangle,Archive,CheckCircle2,FolderOpen,HardDriveDownload,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type MultiHostClusterBackupPreview,
  type MultiHostClusterRestorePreview,type MultiHostClusterBackupStatus} from "./bridge";

export function MultiHostBackupPanel(){
  const [backupPreview,setBackupPreview]=useState<MultiHostClusterBackupPreview|null>(null);
  const [restorePreview,setRestorePreview]=useState<MultiHostClusterRestorePreview|null>(null);
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
    setBackupPreview(null);setRestorePreview(null);setStatus(null);setJobId("");setJobKind("");
    try{setBackupPreview(await managerBridge.multiHostBackupPreview());}
    catch{setError("A kiválasztott offline katalógus, HostRegistry vagy ClusterRegistry nem egyezik, illetve a forrás érinthet védett ASA/Store útvonalat.");}
    finally{setBusy(false);}
  }
  async function previewRestore(){
    if(busy||jobId&&status?.state!=="completed"&&status?.state!=="needsOperator")return;
    setBusy(true);setError("");setNotice("");setApproved(false);
    setBackupPreview(null);setRestorePreview(null);setStatus(null);setJobId("");setJobKind("");
    try{setRestorePreview(await managerBridge.multiHostRestorePreview());}
    catch{setError("Az offline multi-host archívum nem választható ki vagy nem ment át az integritásellenőrzésen.");}
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
      const started=await managerBridge.multiHostRestoreApply(restorePreview.token);
      setJobId(started.jobId);setJobKind("restore");setRestorePreview(null);setApproved(false);
      setNotice("A fájlok ellenőrzött kibontása új TEMP tesztsandboxba indult. Éles map visszaállítása nem történik.");
    }catch{setError("A TEMP sandbox restore indítását a natív Manager elutasította.");}
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
      Az archívum byte-integritást igazol, nem élő ASA SaveComplete-et, Store-írózár-konzisztenciát vagy valódi
      karakterutazást. A Restore művelet kizárólag új TEMP sandboxba másol.
    </div>
    <div className="draft-actions">
      <button type="button" className="secondary" disabled={busy||Boolean(jobId&&!finished)}
        onClick={()=>{void previewBackup();}}><FolderOpen size={16}/> Offline mentés előnézete</button>
      <button type="button" className="secondary" disabled={busy||Boolean(jobId&&!finished)}
        onClick={()=>{void previewRestore();}}><HardDriveDownload size={16}/> Archívum ellenőrzése / TEMP restore</button>
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
      <p><b>Cél: új TEMP sandbox.</b> Regisztrált map, Store-v1, cluster-transfer és éles szerver fájl nem íródik.</p>
      <label><input type="checkbox" checked={approved} disabled={busy}
        onChange={e=>setApproved(e.target.checked)}/>
        Jóváhagyom az ellenőrzött fájlok elkülönített TEMP sandboxba másolását.</label>
      <div className="draft-actions">
        <button type="button" className="primary" disabled={!approved||busy}
          onClick={()=>{void applyRestore();}}>TEMP restore indítása</button>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>{setRestorePreview(null);setApproved(false);}}>Mégsem</button>
      </div>
    </div>}
    {jobId&&<div className="uk-rcon-receipt" role="status">
      <strong>{jobKind==="restore"?"TEMP restore":"Offline backup"} állapot: {status?.state||"ellenőrzés folyamatban"}</strong>
      <p>{status?.stage||"NATÍV MŰVELET INDÍTÁSA"}</p>
      {status?.state==="completed"&&<p><CheckCircle2 size={16}/>
        {status.hosts} host, {status.maps} map, {status.clusters} cluster, {status.components} fájl SHA-256 ellenőrizve.
        {status.artifactName?` Cél: ${status.artifactName}.`:""}</p>}
      {status?.state==="needsOperator"&&<p><AlertTriangle size={16}/>
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
