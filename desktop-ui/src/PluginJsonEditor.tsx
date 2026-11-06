import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,FileJson2,Shield} from "lucide-react";
import {managerBridge,type PluginJsonDocument,type PluginJsonPreview} from "./bridge";

export function PluginJsonEditor({mapId,plugin,onClose,onSaved}:{
  mapId:string;plugin:string;onClose:()=>void;onSaved:()=>void
}){
  const [snapshot,setSnapshot]=useState<PluginJsonDocument|null>(null);
  const [content,setContent]=useState("");
  const [preview,setPreview]=useState<PluginJsonPreview|null>(null);
  const [confirm,setConfirm]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  useEffect(()=>{
    let active=true;
    setBusy(true);setError("");setSnapshot(null);setPreview(null);
    managerBridge.pluginJsonOpen(mapId,plugin).then(doc=>{
      if(active){setSnapshot(doc);setContent(doc.content);}
      else void managerBridge.pluginJsonClose(doc.token).catch(()=>{});
    }).catch(()=>{
      if(active)setError("A plugin config.json nem olvasható, vagy a map nincs igazoltan leállítva.");
    }).finally(()=>{if(active)setBusy(false);});
    return()=>{active=false;};
  },[mapId,plugin]);
  const close=()=>{
    if(snapshot)void managerBridge.pluginJsonClose(snapshot.token).catch(()=>{});
    onClose();
  };
  const check=async()=>{
    if(!snapshot||busy)return;
    setBusy(true);setPreview(null);setConfirm(false);setError("");setNotice("");
    try{setPreview(await managerBridge.pluginJsonPreview(snapshot.token,content));}
    catch{setError("Érvénytelen JSON vagy ismétlődő kulcs. Nem történt fájlmódosítás.");}
    finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!snapshot||!preview||!confirm||busy)return;
    const token=snapshot.token;
    setBusy(true);setError("");setPreview(null);setConfirm(false);
    try{
      const done=await managerBridge.pluginJsonSave(token);
      setNotice(done.saved?
        "JSON-konfiguráció mentve, védőmásolattal. A plugin újratöltése még szükséges.":
        "A konfiguráció nem változott.");
      setSnapshot(null);onSaved();
    }catch{setError("Mentés visszautasítva: a konfiguráció vagy a processzállapot változhatott. Ellenőrizd a fájlokat.");}
    finally{setBusy(false);}
  };
  return <section className="premium-panel" aria-label="Plugin JSON szerkesztő">
    <div className="panel-head"><h3><FileJson2 size={18}/> {plugin} – config.json</h3>
      <span className="mod-tag amber">Helyi · leállított map</span></div>
    <div className="warning-inline"><Shield size={16}/>
      Kizárólag a map saját meglévő pluginbeállítása szerkeszthető. A Manager
      megőrzi az eredeti karakterkódolást, ellenőrzi a fájl lenyomatát,
      és előző verziót tart meg. A plugin futás közbeni betöltése nincs igazolva.
    </div>
    {snapshot&&<>
      <p className="muted small">SHA-256: {snapshot.sha256.slice(0,16)}… · {snapshot.encoding} · {snapshot.bytes} bájt</p>
      <label htmlFor="plugin-json-content">Plugin JSON tartalma</label>
      <textarea id="plugin-json-content" className="ini-editor" spellCheck={false}
        aria-label="Plugin JSON tartalma" rows={15} value={content}
        disabled={busy} onChange={e=>{
          setContent(e.target.value);setPreview(null);setConfirm(false);setNotice("");
        }}/>
      <div className="draft-actions">
        <button className="secondary" type="button" disabled={busy}
          onClick={()=>{void check();}}>JSON és változások ellenőrzése</button>
      </div>
    </>}
    {preview&&<div className="historical-preview" role="region"
      aria-label="Plugin JSON módosítás jóváhagyása">
      <p>Változott: {preview.changed?"igen":"nem"} · sorok: {preview.oldLines} → {preview.newLines}</p>
      <p>Új SHA-256: {preview.newSha256.slice(0,16)}…</p>
      {preview.changed&&<label><input type="checkbox" disabled={busy}
        checked={confirm} onChange={e=>setConfirm(e.target.checked)}/>
        Jóváhagyom a helyi JSON-fájl mentését védőmásolattal.</label>}
      <button className="primary" disabled={!preview.changed||!confirm||busy}
        onClick={()=>{void apply();}}>Mentés véglegesítése</button>
    </div>}
    {busy&&<p className="muted small">Fájl és folyamatellenőrzés…</p>}
    {notice&&<p className="success-inline"><CheckCircle2 size={16}/>{notice}</p>}
    {error&&<p role="alert" className="error-inline"><AlertTriangle size={16}/>{error}</p>}
    <button className="quiet" type="button" disabled={busy} onClick={close}>Szerkesztő bezárása</button>
  </section>;
}
