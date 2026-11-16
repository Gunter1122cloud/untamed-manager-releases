import {useEffect,useState} from "react";
import {AlertTriangle,CheckCircle2,CloudDownload,GitBranch,HardDrive,Info,
  RefreshCw,ShieldCheck,ServerCog} from "lucide-react";
import {managerBridge,type UpdateStatus,type UpdateCheck,type UpdatePrepared} from "./bridge";


function previewVersion(raw:string|undefined):string {
  if(!raw)return "Beolvasás...";
  const match=/^(\d+[.]\d+[.]\d+)(?:[.]\d+)?$/.exec(raw);
  return match?`${match[1]} Preview`:"Nem azonosított verzió";
}
function buildLabel(tag:string|undefined):string {
  const match=/^v\d+[.]\d+[.]\d+-obsidian-preview[.](\d+)$/.exec(tag??"");
  return match?`Build #${match[1]}`:(tag?"Korábbi telepítési jelölő":"Fejlesztői build");
}

/** Real GitHub Releases metadata: read-only. The release signature/digest and
 * external updater must be verified before installation can be enabled. */
export function UpdateCenterPage(){
  const [current,setCurrent]=useState<UpdateStatus|null>(null);
  const [latest,setLatest]=useState<UpdateCheck|null>(null);
  // One active Obsidian release channel; historical WPF is never selectable.
  const channel:"preview"="preview";
  const [loading,setLoading]=useState(false);
  const [prepared,setPrepared]=useState<UpdatePrepared|null>(null);
  const [approved,setApproved]=useState(false);
  const [installing,setInstalling]=useState(false);
  const [handedOff,setHandedOff]=useState(false);
  const [error,setError]=useState("");
  useEffect(()=>{
    let active=true;
    managerBridge.updateStatus().then(r=>{if(active)setCurrent(r);})
      .catch(e=>{
        if(!active)return;
        setError(e instanceof Error && e.message==="UM-NATIVE-TIMEOUT-OPERATION-STATUS-UNKNOWN"
          ? "A natív verziólekérdezés időtúllépést ért el. Az állapot ismeretlen; nincs automatikus újrapróbálás vagy telepítés."
          : "A helyi verzióazonosító nem olvasható.");
      });
    return ()=>{active=false;};
  },[]);
  const check=async()=>{
    if(loading||installing||handedOff)return;
    setLoading(true);setError("");setLatest(null);
    setPrepared(null);setApproved(false);
    try{
      const result=await managerBridge.updateCheck(channel);
      if(result.found&&result.newer&&
         (!result.tag||
          !/^v\d{1,4}\.\d{1,4}\.\d{1,4}-obsidian(?:-preview\.\d{1,12})?$/.test(result.tag)||
          !result.sha256||!/^[a-fA-F0-9]{64}$/.test(result.sha256))){
        setError("A GitHub frissítésből hiányzik a pontos Obsidian kiadásazonosító vagy a SHA-256. A letöltés tiltva marad; ellenőrizd újra a kiadást.");
        setLatest(null);
        return;
      }
      setLatest(result);
    }
    catch{setError("A GitHub kiadványok ellenőrzése sikertelen. A telepített Manager változatlan maradt.");}
    finally{setLoading(false);}
  };
  const prepare=async()=>{
    if(!latest?.newer||!latest.found||!latest.tag||!latest.sha256||
       loading||installing||handedOff)return;
    const checkedTag=latest.tag,checkedDigest=latest.sha256;
    setLoading(true);setError("");setPrepared(null);setApproved(false);
    try{
      const staged=await managerBridge.updatePrepare(channel,checkedTag,checkedDigest);
      // Defense in depth: the native bridge already pins tag + SHA before
      // download. An unexpectedly mixed frontend/native build must fail closed.
      if(staged.tag!==checkedTag ||
         staged.sha256.toLowerCase()!==checkedDigest.toLowerCase())
        throw new Error("UM-UPDATE-CHECK-STALE");
      setPrepared(staged);
    }
    catch(e){
      const code=e instanceof Error?e.message:"";
      if(code==="UM-UPDATE-CHECK-STALE" || code==="UM-UPDATE-CHECK-REQUIRED"){
        setLatest(null);
        setError("A GitHub kiadás az ellenőrzés óta megváltozott, vagy a telepített UI és natív host verziója eltér. Keresd újra a frissítést; az eltérő telepítő nem lett elfogadva.");
      }else{
        setError("Az Obsidian telepítő ellenőrzése nem sikerült ("+
          (code||"UM-UPDATE-VERIFY-FAILED")+
          "). A jelenlegi program és minden ASA-fájl változatlan maradt.");
      }
    }
    finally{setLoading(false);}
  };
  const install=async()=>{
    if(!prepared||!approved||installing||handedOff||loading)return;
    setInstalling(true);setError("");
    try{
      const response=await managerBridge.updateInstall(prepared.token);
      if(response.externalUpdaterStarted)setHandedOff(true);
    }catch{
      setPrepared(null);setApproved(false);
      setError("A külső frissítő átadása nem sikerült. A Manager nem módosult.");
    }finally{setInstalling(false);}
  };
  return <div className="uk-updates-page">
    <div className="page-intro"><span className="eyebrow">UNTAMED KINGDOM / RELEASE CHANNEL</span>
      <h1>Frissítési központ</h1>
      <p>Hivatalos GitHub Releases verzióellenőrzés a jelenlegi Managerhez.
      A keresés nem állít le ASA-mapot, nem ír adatot és nem telepít automatikusan.</p>
    </div>
    <section className="uk-updates-hero">
      <span className="uk-updates-symbol"><CloudDownload size={38}/></span>
      <div><span className="uk-updates-kicker">UNTAMED COMMAND • UPDATE CHANNEL</span>
        <h2>Maradj naprakész. Őrizd meg a clustert.</h2>
        <p>A Manager frissítése külön történik a szerverek és karaktermentések kezelésétől. A kiadvány ellenőrzése nem jelent
          engedélyt a Store-v1 vagy az éles szerverek módosítására.</p>
      </div>
    </section>
    <div className="uk-update-grid">
      <section className="premium-panel uk-updates-version">
        <header className="premium-panel-heading"><h2><ServerCog size={19}/> Jelenlegi telepítés</h2></header>
        <div className="uk-updates-inner">
          <div className="uk-updates-info-row"><span><HardDrive size={16}/> Programverzió</span>
            <strong>{previewVersion(current?.runningVersion)}</strong></div>
          <div className="uk-updates-info-row"><span><GitBranch size={16}/> Technikai build</span>
            <strong>{buildLabel(current?.installedTag)}</strong></div>
          <div className="uk-updates-info-row"><span><ShieldCheck size={16}/> Forrás</span>
            <strong>Gunter1122cloud / GitHub Releases</strong></div>
          <div className="uk-updates-info-row"><span><Info size={16}/> Frissítési mód</span>
            <strong>Kizárólag kézi indítás – ellenőrzés, letöltés és telepítés külön jóváhagyással</strong></div>
          <div className="uk-updates-checkbox"><CheckCircle2 size={17}/>
            <span>A verzióellenőrzés önmagában semmilyen szerver- vagy alkalmazásfájlt nem módosít.</span></div>
        </div>
      </section>
      <section className="premium-panel uk-updates-discovery">
        <header className="premium-panel-heading"><h2><CloudDownload size={19}/> Új verzió ellenőrzése</h2></header>
        <div className="uk-updates-inner">
          <p className="uk-updates-channel"><strong>Obsidian Command Preview</strong> — az egyetlen támogatott kiadási ág.
            A régi WPF nem választható frissítési forrásként.</p>
          <button type="button" className="primary uk-updates-check" onClick={()=>{void check();}}
            disabled={loading||installing||handedOff}>
            <RefreshCw size={18} className={loading?"spin":""}/>
            {loading?"GitHub kiadványok ellenőrzése...":"Frissítések keresése"}
          </button>
          {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
          {latest&&<div className="uk-updates-release" role="status">
            <strong>{latest.found?
              latest.newer?"Új kiadás érhető el":"Kiadás ellenőrizve":
              "Nincs ellenőrizhető kiadás ebben a csatornában"}</strong>
            {latest.found&&<>
              <div><span>Programverzió</span><b>{latest.version} Preview</b></div>
              <div><span>Technikai kiadás</span><small>{latest.tag}</small></div>
              <div><span>Telepítő</span><b>{latest.assetName}</b></div>
              <div className="uk-updates-hash"><span>SHA-256 (kiadási adat)</span>
                <code>{latest.sha256}</code></div>
              {latest.releaseNotes&&<p className="uk-updates-release-notes">
                {latest.releaseNotes}</p>}
            </>}

            {latest.newer&&!prepared&&<button className="secondary uk-updates-check"
              type="button" disabled={loading||installing||handedOff} onClick={()=>{void prepare();}}>
              <CloudDownload size={18}/>
              {loading?"Obsidian telepítő ellenőrzése...":"Telepítő letöltése és SHA-256 ellenőrzése"}
            </button>}
            {prepared&&<div className="uk-updates-ready">
              <div><CheckCircle2 size={18}/> Telepítő SHA-256 ellenőrizve</div>
              <span>{prepared.tag} · {(prepared.fileBytes/1048576).toFixed(1)} MiB</span>
              <code>{prepared.sha256}</code>
              <label><input type="checkbox" checked={approved}
                onChange={e=>setApproved(e.target.checked)}/>
                Engedélyezem a Manager bezárását és a külön külső updater
                indítását, mentéssel és automatikus visszaállítással hiba esetén.
              </label>
              <button className="primary uk-updates-check" disabled={!approved||installing||handedOff||loading}
                type="button" onClick={()=>{void install();}}>
                <ShieldCheck size={17}/>{installing?"Külső frissítő indítása...":
                  "Ellenőrzött frissítés telepítése"}
              </button>
            </div>}
            {handedOff&&<p role="status">A külön külső updater átvette a telepítést;
              a Manager felülete bezárul. Az ASA-szerverfolyamatok függetlenek.</p>}
            <div className="uk-updates-install-lock"><ShieldCheck size={17}/>
              Csak az Obsidian Command saját, SHA-256-tal ellenőrzött Windows
              telepítője fogadható el. Más alkalmazás kiadása nem kompatibilis;
              a karaktermentéseket nem módosítjuk.
            </div>
          </div>}
          <small className="uk-updates-detail">
            A GitHub kiadványadatainak olvasása nem hitelesíti önmagában a letöltött fájlt.
            Az Untamed Store-v1 és az ASA mentések változatlanok.
          </small>
        </div>
      </section>
    </div>
  </div>;
}
