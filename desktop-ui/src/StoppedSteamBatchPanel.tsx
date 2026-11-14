import {useEffect,useMemo,useState} from "react";
import {AlertTriangle,CheckCircle2,RefreshCw,Shield} from "lucide-react";
import {managerBridge, type PublicMap, type StoppedSteamBatchPreview,
  type StoppedSteamBatchJob, type StoppedSteamBatchCurrent} from "./bridge";

const approvalPhrase="HELYI SOROS STEAMCMD";
const pageSize=25;

/**
 * Local host only. A selection may span arbitrarily many registered maps
 * across pages, but each explicit native batch admits at most 25 map IDs.
 * No running or uncertain ASA server may be stopped from this UI.
 * Original Store-v1 maps require a separate native stopped-family check.
 */
export function StoppedSteamBatchPanel({maps}:{maps:PublicMap[]}) {
  const [selected,setSelected]=useState<string[]>([]);
  const [query,setQuery]=useState("");
  const [page,setPage]=useState(0);
  const [preview,setPreview]=useState<StoppedSteamBatchPreview|null>(null);
  const [approval,setApproval]=useState("");
  const [job,setJob]=useState<StoppedSteamBatchJob|null>(null);
  const [jobId,setJobId]=useState("");
  const [current,setCurrent]=useState<StoppedSteamBatchCurrent|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const candidates=useMemo(()=>maps.filter(m=>
    !m.freshStoreEnrolled&&!m.observedReadOnlyImported&&
    !m.maintenance&&!m.running),
    [maps]);
  const filtered=useMemo(()=>candidates.filter(m=>
    !query.trim()||m.name.toLocaleLowerCase("hu-HU").includes(
      query.trim().toLocaleLowerCase("hu-HU"))||
    m.id.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase())
  ),[candidates,query]);
  const pages=Math.max(1,Math.ceil(filtered.length/pageSize));
  const safePage=Math.min(page,pages-1);
  const visible=filtered.slice(safePage*pageSize,(safePage+1)*pageSize);
  const running=job?.state==="running"||job?.state==="queued"||Boolean(jobId);

  useEffect(()=>{
    let active=true;
    managerBridge.steamBatchCurrent().then(value=>{
      if(!active)return;
      setCurrent(value);
      if(value.activeJob&&
         (value.activeJob.state==="queued"||value.activeJob.state==="running")){
        setJob(value.activeJob);setJobId(value.activeJob.jobId);
      }
    }).catch(()=>{/* Pre-batch isolated UI hosts may not expose this route. */});
    return ()=>{active=false;};
  },[]);

  useEffect(()=>{
    if(!jobId)return;
    let active=true;
    const poll=async()=>{
      try{
        const status=await managerBridge.steamBatchStatus(jobId);
        if(!active)return;
        setJob(status);
        if(status.state==="completed"||status.state==="needsOperator"){
          setJobId("");
          managerBridge.steamBatchCurrent().then(value=>{
            if(active)setCurrent(value);
          }).catch(()=>{});
        }
      }catch{
        if(active){
          setError("A natív soros SteamCMD feladat állapota nem ellenőrizhető. Új futtatás előtt nézd meg a helyi műveleti naplót.");
          setJobId("");
        }
      }
    };
    void poll();
    const timer=window.setInterval(()=>{void poll();},3000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[jobId]);

  const invalidate=()=>{setPreview(null);setApproval("");setNotice("");setError("");};
  const toggle=(mapId:string,checked:boolean)=>{
    invalidate();
    if(checked&&selected.length>=25){
      setError("Egy jóváhagyással legfeljebb 25 helyi map frissíthető. A további mapokat egy következő, külön műveletbe válaszd.");
      return;
    }
    setSelected(old=>checked?[...old.filter(x=>x!==mapId),mapId]:
      old.filter(x=>x!==mapId));
  };
  const check=async()=>{
    if(busy||running||selected.length===0)return;
    setBusy(true);invalidate();
    try{
      setPreview(await managerBridge.steamBatchPreview(selected));
    }catch(e){
      setError("A natív előellenőrzés elutasította a műveletet ("+
        (e instanceof Error?e.message:"UM-STEAM-BATCH-PREFLIGHT")+"). Egyik map sem módosult.");
    }finally{setBusy(false);}
  };
  const start=async()=>{
    if(busy||running||!preview||approval!==approvalPhrase)return;
    const token=preview.token;
    setBusy(true);setPreview(null);setApproval("");setError("");setNotice("");
    try{
      const started=await managerBridge.steamBatchApply(token,approvalPhrase);
      setJob({jobId:started.jobId,state:"queued",stage:"preflight",
        position:0,total:preview.installations,activeMapName:"",
        completedInstallations:0,changedUtc:new Date().toISOString(),
        serverStopRequested:false,remoteHostTouched:false,
        storeWriterTouched:false,automaticRetry:false});
      setJobId(started.jobId);
      setNotice("A helyi, soros SteamCMD-feladat elindult. A következő telepítés kizárólag az előző igazolt lezárása után indulhat.");
    }catch(e){
      setError("A jóváhagyott előnézet lejárt vagy a natív körülmények megváltoztak ("+
        (e instanceof Error?e.message:"UM-STEAM-BATCH-FAILED")+
        "). Új jóváhagyás szükséges.");
    }finally{setBusy(false);}
  };
  const blocked=Boolean(current?.needsOperatorReview);
  return <section className="glass panel cluster-registry-panel"
    aria-label="Helyi mapok soros SteamCMD frissítése">
    <div className="panel-head"><h2>Helyi mapok soros SteamCMD frissítése</h2></div>
    <div className="panel-inner">
      <p className="muted small">Csak az ezen a Windows gépen regisztrált, bizonyítottan
        leállított ASA mapok, köztük az átvett eredeti Store-v1 mapok.
        Azonos ASA telepítés csak egyszer frissül,
        natív mentéssel és műveleti naplóval. Futó szervert sosem állít le;
        sikertelen vagy félbeszakadt műveletet nem ismétel automatikusan.</p>
      <label>Helyi célmapok keresése
        <input aria-label="Soros SteamCMD célmap keresése" type="search"
          disabled={busy||running} value={query} placeholder="Mapnév vagy azonosító"
          onChange={e=>{setQuery(e.target.value);setPage(0);invalidate();}}/>
      </label>
      <div className="draft-actions">
        <button type="button" className="secondary" disabled={busy||running||visible.length===0}
          onClick={()=>{
            setSelected(old=>[...new Set([...old,...visible.map(m=>m.id)])].slice(0,25));
            invalidate();
          }}>Látható mapok kijelölése (max. 25)</button>
        <button type="button" className="quiet"
          disabled={busy||running||safePage===0}
          onClick={()=>setPage(p=>Math.max(0,p-1))}>Előző SteamCMD-oldal</button>
        <button type="button" className="quiet"
          disabled={busy||running||safePage>=pages-1}
          onClick={()=>setPage(p=>Math.min(pages-1,p+1))}>Következő SteamCMD-oldal</button>
        <span className="muted small">{safePage+1}. / {pages} oldal ·
          {filtered.length} helyi jelölt · {selected.length} kijelölt</span>
      </div>
      <div className="premium-mod-copy-targets" role="group"
        aria-label="Soros SteamCMD célmapok">
        {visible.length===0?<p className="muted small">Nincs a keresésnek megfelelő, leállított helyi map.</p>:
        visible.map(map=><label className="plugin-row" key={map.id}>
          <input type="checkbox" checked={selected.includes(map.id)}
            disabled={busy||running}
            onChange={e=>toggle(map.id,e.target.checked)}/>
          <span>{map.name}</span><small>{map.steamBuildId??"ismeretlen build"}</small>
        </label>)}
      </div>
      <button type="button" className="secondary"
        disabled={busy||running||blocked||selected.length===0}
        onClick={()=>{void check();}}>
        <RefreshCw size={16}/> {selected.length} map natív frissítési előellenőrzése
      </button>
      {preview&&<div className="historical-preview" role="region"
        aria-label="Soros SteamCMD jóváhagyása">
        <h3>{preview.requestedMaps} map, {preview.installations} külön ASA telepítés</h3>
        {preview.details.map(entry=><p className="muted small" key={entry.mapId}>
          {entry.mapName} · Steam build {entry.installedBuild} ·
          {entry.sharedMapCount} közös map
        </p>)}
        <label>Jóváhagyó szöveg
          <input aria-label="Soros SteamCMD jóváhagyó szöveg"
            disabled={busy} value={approval} autoComplete="off"
            placeholder={approvalPhrase}
            onChange={e=>setApproval(e.target.value)}/>
        </label>
        <button type="button" className="primary"
          disabled={busy||running||approval!==approvalPhrase}
          onClick={()=>{void start();}}>Helyi soros SteamCMD frissítés indítása</button>
        <p className="muted small">A jóváhagyás a meglévő natív Core-frissítőt
          indítja, nem a szerverleállítást vagy az automatikus újraindítást.</p>
      </div>}
      {job&&<div className="historical-preview" role="status">
        <b>SteamCMD: {job.state}</b>
        <p>{job.position}. / {job.total} · {job.activeMapName||"Előkészítés"} ·
          {job.stage}</p>
        {job.state==="completed"&&<p><CheckCircle2 size={16}/> Igazoltan frissített
          telepítések: {job.completedInstallations}</p>}
        {job.state==="needsOperator"&&<p className="error-inline">
          <AlertTriangle size={16}/> A soros művelet megállt. Ellenőrizd a natív
          naplót; automatikus ismétlés nincs.</p>}
      </div>}
      {blocked&&<div className="warning-inline"><Shield size={16}/>
        Félbeszakadt SteamCMD műveleti napló: csak külön kézi helyreállítás után
        indítható új feladat.</div>}
      {notice&&<div className="success-inline" role="status">{notice}</div>}
      {error&&<div className="error-inline" role="alert">
        <AlertTriangle size={16}/>{error}</div>}
      <div className="warning-inline"><Shield size={16}/>
        A távoli gazdagépek függetlenek: ezen a gépen csak helyi ASA telepítések
        módosíthatók. A Store-v1 mapokhoz ugyanaz a leállított állapot,
        natív Windows folyamatazonosítás és kézi jóváhagyás szükséges.</div>
    </div>
  </section>;
}
