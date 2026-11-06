import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,Download,Shield,RefreshCcw} from "lucide-react";
import {managerBridge,type AsaApiCoreRelease,type AsaApiCorePreview} from "./bridge";

/**
 * Official upstream ASA API CORE updater. Trusted native bridge owns HTTPS,
 * ZIP bytes, on-disk backups, process checks and transaction journal.
 * The React UI never sees a filesystem path, token or secret from Kova Store.
 */
export function AsaApiCoreInstaller({mapId,onInstalled}:{
  mapId:string;onInstalled:()=>void
}){
  const [release,setRelease]=useState<AsaApiCoreRelease|null>(null);
  const [asset,setAsset]=useState("");
  const [preview,setPreview]=useState<AsaApiCorePreview|null>(null);
  const [busy,setBusy]=useState(false);
  const [approved,setApproved]=useState(false);
  const [message,setMessage]=useState("");
  const [error,setError]=useState("");
  useEffect(()=>{
    setRelease(null);setAsset("");setPreview(null);setApproved(false);
    setMessage("");setError("");
  },[mapId]);
  const refresh=async()=>{
    if(busy)return;
    setBusy(true);setError("");setPreview(null);setApproved(false);setMessage("");
    try{
      const upstream=await managerBridge.apiCoreRelease();
      setRelease(upstream);
      setAsset(upstream.assets.find(x=>x.publisherDigestAvailable)?.name??"");
    }catch{
      setError("Az ArkServerApi hivatalos GitHub-kiadása nem kérdezhető le. Az ASA fájljai változatlanok.");
    }finally{setBusy(false);}
  };
  const prepare=async()=>{
    if(!mapId||!asset||busy)return;
    setBusy(true);setPreview(null);setApproved(false);setError("");setMessage("");
    try{setPreview(await managerBridge.apiCorePreview(mapId,asset));}
    catch{
      setError("A hivatalos ZIP SHA-256 azonosítója, vagy a leállított, saját non-Store szerver nem igazolható.");
    }finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!preview||!approved||busy)return;
    const id=preview.token;
    setBusy(true);setPreview(null);setApproved(false);setError("");setMessage("");
    try{
      const result=await managerBridge.apiCoreApply(id);
      setMessage(result.releaseTag+" · "+result.binaries+
        " ellenőrzött ASA API CORE bináris telepítve; a korábbi fájlok célzott védőmentésben maradtak. Szerverindítás és automatikus újrapróbálás nem történt.");
      onInstalled();
    }catch{
      setError("A binárisok frissítése nem igazolható. Ne indítsd újra a műveletet vakon; ellenőrizd a Manager tartós API CORE műveleti naplóját.");
    }finally{setBusy(false);}
  };
  return <section className="premium-panel" aria-label="Hivatalos ASA API CORE telepítés" style={{marginTop:18,padding:20}}>
    <header className="premium-panel-heading"><h2><Download size={19}/> ASA API CORE · hivatalos GitHub frissítő</h2></header>
    <p>Csak saját, leállított, nem Store-v1 ASA telepítésen végez célzott binárisfrissítést. A hivatalos kiadás GitHub SHA-256 ellenőrzése és a régi DLL/EXE bájtpontos megőrzése kötelező. Pluginbeállítások, mentések és a világfájlok nem módosulnak.</p>
    <button className="secondary" type="button" disabled={busy}
      onClick={()=>{void refresh();}}>
      <RefreshCcw size={15}/>{busy?"Hivatalos kiadás ellenőrzése...":"Hivatalos ASA API kiadás lekérdezése"}
    </button>
    {release&&<div className="historical-preview">
      <h3>ArkServerApi · {release.tag}</h3>
      <label>Hivatalos SHA-256-tal ellátott ZIP
        <select aria-label="ASA API CORE ZIP" value={asset}
          onChange={e=>{setAsset(e.target.value);setPreview(null);setApproved(false);}}
          disabled={busy}>
          <option value="">Válassz ellenőrizhető ZIP-et</option>
          {release.assets.map(x=><option key={x.name}
            value={x.publisherDigestAvailable?x.name:""}
            disabled={!x.publisherDigestAvailable}>
            {x.name} · {(x.bytes/1024/1024).toFixed(1)} MiB
            {x.publisherDigestAvailable?"":" · nincs publikált SHA-256"}
          </option>)}
        </select>
      </label>
      <button className="secondary" type="button" disabled={!asset||!mapId||busy}
        onClick={()=>{void prepare();}}>
        <Shield size={16}/> Leállítás, helyi tulajdonjog és ZIP előellenőrzése
      </button>
    </div>}
    {preview&&<div className="historical-preview" role="region" aria-label="ASA API CORE telepítés jóváhagyása">
      <h3>{preview.mapName} · {preview.releaseTag}</h3>
      <p>Hivatalos SHA-256, leállított helyi ASA és célzott védőmentés előellenőrizve.
        Az API modul tényleges betöltését a következő szerverindításkor lehet külön megfigyelni.</p>
      <label className="historical-approval">
        <input type="checkbox" checked={approved} disabled={busy}
          onChange={e=>setApproved(e.target.checked)}/>
        Jóváhagyom a helyi ASA API CORE binárisok telepítését, az eredeti pluginok és Store változtatása nélkül.
      </label>
      <button className="primary" type="button" disabled={!approved||busy}
        onClick={()=>{void apply();}}>
        <Download size={16}/> {busy?"Hivatalos csomag ellenőrzése és telepítése...":"ASA API CORE telepítése"}
      </button>
      <button className="secondary" type="button" disabled={busy}
        onClick={()=>setPreview(null)}>Mégsem</button>
    </div>}
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={18}/>{message}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={18}/>{error}</div>}
    <p className="muted tiny"><Shield size={14}/> A művelet nem indít szervert, nem végez Store-v1 konverziót, és nem tanúsít automatikusan plugin-futásidőt.</p>
  </section>;
}
