import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,Play,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type FreshAsaStartPreview,type FreshAsaStartStatus,type FreshAsaReadiness} from "./bridge";

// Nonsecret process receipt survives a dashboard refresh/remount. Never persist
// launch arguments, credentials or Store data in WebView2 storage.
const recentStartReceipts=new Map<string,string>();

export function FreshAsaStartPanel({mapId,onStarted,onOpenRcon,journalBlocked}:{
  mapId:string;onStarted:()=>Promise<void>;onOpenRcon?:()=>void;journalBlocked:boolean
}){
  const [observed,setObserved]=useState<FreshAsaStartStatus|null>(null);
  const [preview,setPreview]=useState<FreshAsaStartPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [readiness,setReadiness]=useState<FreshAsaReadiness|null>(null);
  const [checkingReady,setCheckingReady]=useState(false);
  const [awaitingStartup,setAwaitingStartup]=useState(false);
  const readinessGeneration=useRef(0);
  const [busy,setBusy]=useState(false);
  const [message,setMessage]=useState(()=>recentStartReceipts.get(mapId)??"");
  const [error,setError]=useState("");
  useEffect(()=>{
    let active=true;
    readinessGeneration.current++;
    setAwaitingStartup(false);
    setPreview(null);setApproved(false);setObserved(null);setError("");
    setReadiness(null);
    setMessage(recentStartReceipts.get(mapId)??"");
    managerBridge.freshAsaStartStatus(mapId)
      .then(s=>{if(active)setObserved(s);})
      .catch(()=>{});
    return ()=>{active=false;readinessGeneration.current++;};
  },[mapId]);

  const observe=async()=>{
    setError("");
    try{setObserved(await managerBridge.freshAsaStartStatus(mapId));}
    catch{setError("A helyi ASA-folyamat állapotának beolvasása sikertelen.");}
  };
  const checkReadiness=async()=>{
    if(!observed?.running||checkingReady||busy||journalBlocked)return;
    setCheckingReady(true);setReadiness(null);setError("");
    try{setReadiness(await managerBridge.freshAsaReadiness(mapId));}
    catch{setError("Nem sikerült az ASA/RCON-készenlét ellenőrzése. Ez nem jelent karaktervesztést; a szerverfolyamatot nem állítottuk le.");}
    finally{setCheckingReady(false);}
  };
  // Source-compatible post-spawn readiness handoff: observe the exact
  // existing ASA process and authenticated RCON every two seconds for at
  // most ten minutes. This never retries Start, sends mutating RCON, or
  // stops an unresponsive server. UI teardown/map switch cancels the loop.
  const observeStartedServer=async(targetMapId:string)=>{
    const generation=++readinessGeneration.current;
    setAwaitingStartup(true);
    let finished=false;
    try{
      for(let attempt=0;attempt<300;attempt++){
        await new Promise<void>(resolve=>setTimeout(resolve,2000));
        if(readinessGeneration.current!==generation)return;
        let status:FreshAsaStartStatus;
        try{status=await managerBridge.freshAsaStartStatus(targetMapId);}
        catch{continue;}
        if(readinessGeneration.current!==generation)return;
        setObserved(status);
        if(status.confirmedStopped){
          setError("Az indított ASA-folyamat leállt, mielőtt RCON-készenlétet igazolhattunk volna. Nem indítottunk második szervert.");
          finished=true;
          break;
        }
        if(!status.running)continue;
        try{
          const result=await managerBridge.freshAsaReadiness(targetMapId);
          if(readinessGeneration.current!==generation)return;
          setReadiness(result);
          if(result.productionReady){
            const readyMessage="ASA PID + RCON + indítási napló alapján futásra kész. A tényleges játékoscsatlakozás és karakterutazás külön ellenőrzendő.";
            setMessage(previous=>previous+" "+readyMessage);
            recentStartReceipts.set(targetMapId,
              (recentStartReceipts.get(targetMapId)??"")+" "+readyMessage);
            finished=true;
            break;
          }
          // The original desktop lifecycle completes the control handoff
          // on the first authenticated RCON response. Runtime advertising
          // and optional API hook loading are separate facts and must not
          // delay or falsely negate a working RCON console.
          if(result.rconAuthenticated){
            const controlMessage="ASA RCON-vezérlés hitelesítve és elérhető. A játék szerverhirdetése vagy az opcionális API inicializálása még nincs teljesen igazolva.";
            setMessage(previous=>previous+" "+controlMessage);
            recentStartReceipts.set(targetMapId,
              (recentStartReceipts.get(targetMapId)??"")+" "+controlMessage);
            finished=true;
            break;
          }
        }catch{/* During ASA startup an unavailable RCON is expected; keep the original process. */}
      }
      if(!finished && readinessGeneration.current===generation)
        setError("Az ASA/RCON-készenlét 10 percen belül nem igazolható. A szerverfolyamatot nem állítottuk le, és nem indítottunk másik példányt.");
    }finally{
      if(readinessGeneration.current===generation)setAwaitingStartup(false);
    }
  };

  const check=async()=>{
    if(busy || !mapId || journalBlocked)return;
    setBusy(true);setError("");setMessage("");setPreview(null);setApproved(false);
    try{setPreview(await managerBridge.freshAsaStartPreview(mapId));}
    catch{setError("Indítás nem engedélyezett: új, érvényesen konfigurált ASA-telepítés, a Manager RCON-portjával és jelszavával egyező GameUserSettings.ini, szabad Game/Query/RCON portok, igazoltan leállított folyamat és sértetlen mentésállapot szükséges. Store-v1 történeti mapok nem indíthatók ezzel a művelettel.");}
    finally{setBusy(false);}
  };
  const start=async()=>{
    if(!preview || !approved || busy || journalBlocked)return;
    const token=preview.token;
    setBusy(true);setError("");setMessage("");setPreview(null);setApproved(false);
    try{
      const receipt=await managerBridge.freshAsaStartApply(token);
      const summary=receipt.processRole==="loaderParent"?
        receipt.mapName+" · ASA API Loader szülőfolyamat indítva, PID: "+
          receipt.loaderPid+". Az ASA gyermekfolyamat és RCON még nem igazolt.":
        receipt.mapName+" · ASA szerverfolyamat indítva, PID: "+(receipt.serverPid??receipt.pid)+
          ". A folyamatindítás még nem bizonyítja a játék és az RCON készenlétét.";
      recentStartReceipts.set(mapId,summary);
      setMessage(summary);
      try{await onStarted();}catch{/* Process receipt remains visible even if dashboard refresh fails. */}
      await observe();
      void observeStartedServer(mapId);
    }catch(cause){
      const code=cause instanceof Error?cause.message:"";
      const safe:Record<string,string>={
        "UM-FRESH-START-CHANGED":"A szerverprofil, az ASA EXE/Loader bináris vagy az INI megváltozott a jóváhagyás után. Új indítási előellenőrzés szükséges; ASA nem lett automatikusan újraindítva.",
        "UM-FRESH-START-TOKEN-EXPIRED":"A Start jóváhagyása lejárt vagy már felhasználtad. Friss előellenőrzést kell indítani.",
        "UM-FRESH-START-UNVERIFIED-PROCESS":"Az indított Windows folyamat nem igazolható. Nézd meg a PID-et és a tartós naplót; NE próbáld vakon újra.",
        "UM-FRESH-START-APPROVAL":"Az indításhoz külön kezelői jóváhagyás szükséges.",
        "UM-FRESH-START-NOT-READY":"A helyi telepítés, RCON INI, portok vagy leállított folyamat előfeltételei már nem teljesülnek."
      };
      setError(safe[code]??"Az ASA elindítása nem igazolható. Ellenőrizd a helyi naplókat és a folyamatállapotot; automatikus ismétlés nem történt.");
    }finally{setBusy(false);}
  };

  return <section className="premium-panel" aria-label="Valódi ASA első indítása" style={{marginBlock:18,padding:20}}>
    <header className="premium-panel-heading">
      <h2><Play size={19}/> Új ASA-szerver indítása</h2>
      <button type="button" className="quiet" onClick={()=>{void observe();}} disabled={busy}>
        <RefreshCw size={16}/> Állapotfrissítés
      </button>
    </header>
    <p>Valódi Windows ASA-indítás a meglévő C# Managerből. Kizárólag az újonnan telepített, helyi, konfigurált szerverhez, külön megerősítéssel. A történeti Store-v1 mapok külön átvételi folyamatot igényelnek.</p>
    {observed&&<div role="status" className="details-line">
      <span>Windows folyamat</span>
      <b>{observed.running?"ASA fut · PID "+(observed.serverPid??observed.pid??"?"):
        observed.loaderPid?"Loader PID "+observed.loaderPid+" · ASA gyermek nem igazolt":
        observed.confirmedStopped?"Igazoltan leállítva":"Nem igazolt állapot"}</b>
    </div>}
    <button type="button" className="secondary" disabled={busy||journalBlocked||Boolean(observed?.running)}
      onClick={()=>{void check();}}>Valódi ASA-indítás előellenőrzése</button>
    <button type="button" className="secondary"
      disabled={!observed?.running||busy||journalBlocked||checkingReady}
      onClick={()=>{void checkReadiness();}}><RefreshCw size={16}/> ASA / RCON készenlét ellenőrzése</button>
    {awaitingStartup&&<p className="muted small" role="status">Indítás utáni ASA/RCON-készenlét ellenőrzése (legfeljebb 10 perc, csak olvasás). A szerver nincs automatikusan újraindítva vagy leállítva.</p>}
    {readiness&&<div className="historical-preview" role="status">
      <h3>ASA készenléti szint: {readiness.stage}</h3>
      <div className="details-line"><span>Windows folyamat / RCON</span>
        <b>{readiness.rconAuthenticated?"RCON hitelesítve":"RCON nem igazolt"}</b></div>
      <div className="details-line"><span>ASA API Loader</span>
        <b>{!readiness.apiRequired?"Közvetlen ASA mód – Loader nem szükséges":
          !readiness.loaderObserved?"ASA API mód – Loader PID nem igazolt":
          readiness.apiInitialized?"Inicializálás igazolva":
            "Loader látható; API inicializálás nem igazolt"}</b></div>
      <div className="details-line"><span>ASA szerverhirdetés</span>
        <b>{readiness.joinAdvertised?"Aktuális indítás naplójában igazolt":"Jelenleg nem igazolt"}</b></div>
      <div className="details-line"><span>ASA / RCON futásra kész</span>
        <b>{readiness.productionReady?"PID + RCON + szükséges runtime igazolt":
          "Várakozás vagy hiba – a natív bizonyíték nem teljes"}</b></div>
      <p className="muted small">A szerverhirdetés és RCON még nem bizonyítja, hogy valódi játékos csatlakozott vagy karakterutazás sikerült.</p>
      <p className="muted small">{readiness.description}</p>
    </div>}
    {preview&&<div className="historical-preview" role="region" aria-label="Valódi ASA indítás jóváhagyása">
      <h3>{preview.mapName} – Steam build: {preview.steamBuild}</h3>
      <div className="details-line"><span>Game / Query UDP</span>
        <b>{preview.gamePort} / {preview.queryPort}</b></div>
      <div className="details-line"><span>RCON konfigurálva</span>
        <b>{preview.rconConfigured?"Igen":"Nem"}</b></div>
      <div className="details-line"><span>Választott folyamat</span>
        <b>{preview.launchMode==="asaApiLoader"?
          "ASA API Loader szülőfolyamat – gyermek PID külön ellenőrzendő":
          "Közvetlen ArkAscendedServer"}</b></div>
      <label className="historical-approval"><input type="checkbox"
        checked={approved} disabled={busy}
        onChange={e=>setApproved(e.target.checked)}/>
        Jóváhagyom az új, saját helyi ASA szerver tényleges Windows-folyamatának elindítását.
      </label>
      <button type="button" className="primary" disabled={busy||!approved||journalBlocked}
        onClick={()=>{void start();}}><Play size={16}/> ASA szerver indítása</button>
      <button type="button" className="secondary" disabled={busy}
        onClick={()=>setPreview(null)}>Mégsem</button>
    </div>}
    {message&&<div className="success-inline" role="status"><CheckCircle2 size={18}/>{message}</div>}
    {error&&<div className="warning-inline" role="alert"><AlertTriangle size={18}/>{error}
      {onOpenRcon&&<button type="button" className="secondary" onClick={onOpenRcon}>RCON előkészítés megnyitása</button>}
    </div>}
    <p className="muted tiny"><Shield size={14}/> Nem használ karakterkonverziót, nem ír a Store-v1-be, nem indít másik hostot, és nincs automatikus második Start-kísérlet.</p>
  </section>;
}
