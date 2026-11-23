import {useCallback,useEffect,useMemo,useState} from "react";
import {AlertTriangle,CheckCircle2,Plus,RefreshCw,Shield} from "lucide-react";
import {managerBridge,type ClusterMembershipPreviewResult,type ClusterRegistrySnapshot,
  type OriginalKovaLocalMapList,type PublicMap,type RunningLocalAsaSnapshot,
  type SupplementalStorePreview} from "./bridge";
import {FreshStoreEnrollmentPanel} from "./FreshStoreEnrollmentPanel";

type Props={
  maps:PublicMap[];
  onInstall:()=>void;
  onExisting:()=>void;
  onHistorical:()=>void;
  onCluster:()=>void;
  onOpenMap:(mapId:string)=>void;
  onImported:()=>Promise<void>;
};

const same=(a:string,b:string)=>a.toLowerCase()===b.toLowerCase();

export function FutureMapOnboarding({maps,onInstall,onExisting,onHistorical,
  onCluster,onOpenMap,onImported}:Props){
  const [running,setRunning]=useState<RunningLocalAsaSnapshot|null>(null);
  const [historical,setHistorical]=useState<OriginalKovaLocalMapList|null>(null);
  const [registry,setRegistry]=useState<ClusterRegistrySnapshot|null>(null);
  const [loading,setLoading]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const [extraId,setExtraId]=useState("");
  const [extraPreview,setExtraPreview]=useState<SupplementalStorePreview|null>(null);
  const [selectedMap,setSelectedMap]=useState("");
  const [clusterId,setClusterId]=useState("");
  const [membership,setMembership]=useState<ClusterMembershipPreviewResult|null>(null);
  const [busy,setBusy]=useState(false);

  const refresh=useCallback(async()=>{
    setLoading(true);setError("");
    const [r,h,c]=await Promise.allSettled([
      managerBridge.runningLocalAsa(),
      managerBridge.historicalOriginalList(),
      managerBridge.clusterRegistrySnapshot()
    ]);
    if(r.status==="fulfilled")setRunning(r.value);else setRunning(null);
    if(h.status==="fulfilled")setHistorical(h.value);else setHistorical(null);
    if(c.status==="fulfilled")setRegistry(c.value);else setRegistry(null);
    if(r.status==="rejected"&&h.status==="rejected"&&c.status==="rejected")
      setError("A helyi natív mapfelderítés jelenleg nem érhető el.");
    setLoading(false);
  },[]);
  useEffect(()=>{void refresh();},[refresh]);

  const extras=historical?.additionalMaps?.filter(e=>!e.registered)??[];
  const selectedExtra=extras.find(e=>e.mapId===extraId);
  const candidates=running?.processes.filter(p=>
    p.processKind==="asaServer"&&p.matchState==="unregistered")??[];
  const local=maps.find(m=>m.id===selectedMap);
  const selectedCluster=registry?.clusterRegistry.clusters.find(c=>c.clusterId===clusterId);
  const assignments=registry?.clusterRegistry.mapAssignments??[];
  const existing=assignments.find(a=>same(a.mapId,selectedMap));
  const assignedHere=!!existing&&same(existing.clusterId,clusterId);
  // A historical source-pinned Store cluster is not a generic fresh Store.
  // Do not offer a registry-only "success" that implies new Store writer rights.
  const protectedCluster=useMemo(()=>!!selectedCluster&&
    (same(selectedCluster.clusterId,"KovaCluster")||
      assignments.some(a=>same(a.clusterId,selectedCluster.clusterId)&&
        maps.some(m=>m.storeProtected&&same(m.id,a.mapId)))),
    [selectedCluster,assignments,maps]);
  const canAdd=!!local&&!!selectedCluster&&!assignedHere&&!existing&&
    !local.storeProtected&&!local.observedReadOnlyImported&&!protectedCluster;
  const safelyEnrolled=!!local&&!local.storeProtected&&!local.observedReadOnlyImported&&
    !local.freshStoreEnrolled&&!local.running&&assignedHere&&!protectedCluster;

  const prepareExtra=async()=>{
    if(!selectedExtra||busy||!selectedExtra.sessionMatches)return;
    setBusy(true);setError("");setNotice("");setExtraPreview(null);
    try{
      const p=await managerBridge.historicalSupplementalPreview(selectedExtra.mapId,true);
      if(!same(p.mapId,selectedExtra.mapId)||p.alreadyRegistered)
        throw Error("UM-ONBOARD-EXTRA-PREVIEW-IDENTITY");
      setExtraPreview(p);
    }catch(e){setError("A Store-forrás hiteles átvétele még nem igazolható ("+
      (e instanceof Error?e.message:"UM-ONBOARD-EXTRA-PREVIEW")+").");}
    finally{setBusy(false);}
  };
  const applyExtra=async()=>{
    if(!extraPreview||extraPreview.alreadyRegistered||busy)return;
    const approved=extraPreview;setExtraPreview(null);setBusy(true);setError("");
    try{
      const receipt=await managerBridge.historicalSupplementalRegister(approved.token);
      if(!same(receipt.mapId,approved.mapId)||!receipt.registered||
         receipt.processCommandIssued||receipt.scheduleChanged)
        throw Error("UM-ONBOARD-EXTRA-POSTCHECK-REQUIRED");
      // A successful atomic registration with a changing unrelated WMI
      // inventory is still a committed profile; do not misreport failure
      // or silently replay the registration.
      setNotice(receipt.processInventoryUnchanged?
        "A kiegészítő Store-map regisztrálva; a futó folyamatleltár változatlan.":
        "A kiegészítő Store-map regisztrálva, de a folyamatleltár megváltozott. "+
        "Natív PID-ellenőrzés szükséges a vezérlés előtt.");
      await onImported();await refresh();
    }catch(e){setError("A Store-regisztráció eredménye kézi ellenőrzést igényel; "+
      "ne ismételd meg automatikusan ("+
      (e instanceof Error?e.message:"UM-ONBOARD-EXTRA-APPLY")+").");}
    finally{setBusy(false);}
  };
  const prepareMembership=async()=>{
    if(!canAdd||!local||!selectedCluster||busy)return;
    setBusy(true);setError("");setNotice("");setMembership(null);
    try{
      // Re-read the authoritative registry immediately before preview.
      // Always retain ALL existing members; NEVER submit only the new map,
      // which would remove every other member from this cluster.
      const fresh=await managerBridge.clusterRegistrySnapshot();
      const entries=fresh.clusterRegistry.mapAssignments;
      if(entries.some(a=>same(a.mapId,local.id))||
         (same(selectedCluster.clusterId,"KovaCluster")||
          entries.some(a=>same(a.clusterId,selectedCluster.clusterId)&&
            maps.some(m=>m.storeProtected&&same(m.id,a.mapId)))))
        throw Error("UM-ONBOARD-CLUSTER-OWNERSHIP-CHANGED");
      const current=fresh.clusterRegistry.clusters.find(c=>
        same(c.clusterId,selectedCluster.clusterId));
      if(!current)throw Error("UM-ONBOARD-CLUSTER-MISSING");
      const ids=entries.filter(a=>same(a.clusterId,current.clusterId)).map(a=>a.mapId);
      const preview=await managerBridge.clusterMembershipPreview(current.clusterId,[...ids,local.id]);
      const changes=preview.preview;
      if(!preview.token||!changes.isValid||changes.errors.length||
         changes.additions.length!==1||changes.moves.length||changes.removals.length||
         !same(changes.additions[0].mapId,local.id))
        throw Error("UM-ONBOARD-OTHER-MEMBER-CHANGE");
      setRegistry(fresh);setMembership(preview);
    }catch(e){setError("A clusterhozzáadás biztonságos előnézete nem készülhetett el ("+
      (e instanceof Error?e.message:"UM-ONBOARD-CLUSTER-PREVIEW")+").");}
    finally{setBusy(false);}
  };
  const applyMembership=async()=>{
    if(!membership?.token||!local||busy)return;
    const token=membership.token;setMembership(null);setBusy(true);setError("");
    try{
      const done=await managerBridge.clusterMembershipApply(token);
      if(done.addedCount!==1||done.movedCount!==0||done.removedCount!==0||
         !same(done.clusterId,clusterId))
        throw Error("UM-ONBOARD-CLUSTER-RECEIPT-REVIEW");
      setNotice(local.name+" hozzáadva a cluster regiszteréhez. "+
        "Ez még nem bizonyít Store-tagságot vagy karakterutazást.");
      await refresh();await onImported();
    }catch(e){setError("A cluster módosítás eredménye nem biztos. "+
      "Ellenőrizd a regisztert, ne nyomd meg újra vakon ("+
      (e instanceof Error?e.message:"UM-ONBOARD-CLUSTER-APPLY")+").");}
    finally{setBusy(false);}
  };

  return <section className="glass panel" aria-label="Jövőbeli mapok hozzáadási varázslója">
    <div className="panel-head">
      <h2><Plus size={19}/> Új map – telepítés, cluster, Store</h2>
      <button className="secondary" type="button" onClick={()=>{void refresh();}}
        disabled={loading||busy}><RefreshCw size={15}/> Felismerés frissítése</button>
    </div>
    <div className="panel-inner">
      <p className="muted small">A később megjelenő ASA mapokhoz is használható. A pálya neve és
        a hivatalos level kulcsa megadható, nem vagyunk tíz maphoz kötve.
        Minden módosítást a meglévő natív Manager ellenőriz; egy
        map hozzáadása önmagában nem indít, nem állít le és nem konvertál szervert.</p>
      <div className="settings-actions">
        <button type="button" className="primary" onClick={onInstall}>
          Új ASA telepítése (egyedi map is)</button>
        <button type="button" className="secondary" onClick={onExisting}>
          Meglévő ASA-könyvtár</button>
        <button type="button" className="secondary" onClick={onHistorical}>
          Történeti Store-map átvétele</button>
        <button type="button" className="secondary" onClick={onCluster}>
          Cluster regiszter megnyitása</button>
      </div>
      {candidates.length>0&&<div className="warning-inline">
        <Shield size={16}/> {candidates.length} nem regisztrált, futó ASA megfigyelhető.
        A folyamatfelismerés önmagában nem ad Start/Stop vagy Store-jogot.
        A lentebb lévő „Futó ASA-folyamatok” panelben ellenőrizhetők.
      </div>}
      {extras.length>0&&<fieldset className="import-preview">
        <legend>Külön hozzáadott történeti Store-mapok ({extras.length})</legend>
        <label>Felderített map
          <select aria-label="Felderített Store-map" disabled={loading||busy}
            value={extraId} onChange={e=>{setExtraId(e.target.value);
              setExtraPreview(null);setError("");}}>
            <option value="">Válassz egy mapot</option>
            {extras.map(e=><option key={e.mapId} value={e.mapId}>
              {e.mapKey} · {e.active?"aktív":"nem aktív"}</option>)}
          </select>
        </label>
        {selectedExtra&&<>
          <p className="muted small">Forrás: {selectedExtra.mapKey} ·
            {selectedExtra.backupIdentityPresent?" saját backup-ID":" backup-ID nincs"} ·
            {selectedExtra.launchState} · Game {selectedExtra.gamePort??"?"} /
            Query {selectedExtra.queryPort??"?"}</p>
          {!selectedExtra.sessionMatches?<div className="warning-inline" role="status">
            {selectedExtra.sessionBinding==="missing-shared-session"?
              "A külön hozzáadott map Store-rekordjában nincs sharedSession. Ez nem bizonyítja önmagában a konverziót: külön hiteles forrás, saját backup és a jelenlegi kétgépes Store-állapot igazolása szükséges.":
              "A map Store-session értéke eltér vagy érvénytelen. Másik clusterhez tartozó rekord nem vehető át az eredeti clusterbe."}
            Az átvétel blokkolva marad, amíg a natív Store-bizonyítás hiányzik.
            A futó ASA és az eredeti öt map érintetlen.
          </div>:<>
            <button type="button" className="secondary" disabled={busy||!selectedExtra.active}
              onClick={()=>{void prepareExtra();}}>Store-forrás natív ellenőrzése</button>
            {extraPreview&&!extraPreview.alreadyRegistered&&<div role="region" aria-label="Kiegészítő Store-map jóváhagyása">
              <p>{extraPreview.mapName} · {extraPreview.mapKey} ·
                {extraPreview.running?"fut":"leállított"}</p>
              <button type="button" className="primary" disabled={busy}
                onClick={()=>{void applyExtra();}}>
                Ellenőrzött Store-map hozzáadása</button>
              <p className="muted small">Csak Manager-profil és cluster-metaadat.
                Nincs ASA újraindítás vagy Store-írás.</p>
            </div>}
          </>}
        </>}
      </fieldset>}
      {maps.length>0&&<fieldset className="import-preview">
        <legend>Regisztrált map csatlakoztatása clusterhez</legend>
        <label>Helyi map
          <select aria-label="Clusterbe felveendő map" value={selectedMap}
            disabled={busy} onChange={e=>{setSelectedMap(e.target.value);
              setMembership(null);setNotice("");}}>
            <option value="">Válassz helyi mapot</option>
            {maps.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}
          </select>
        </label>
        <label>Meglévő cluster
          <select aria-label="Célcluster" value={clusterId} disabled={busy}
            onChange={e=>{setClusterId(e.target.value);setMembership(null);setNotice("");}}>
            <option value="">Válassz clustert</option>
            {(registry?.clusterRegistry.clusters??[]).map(c=>
              <option key={c.clusterId} value={c.clusterId}>{c.name}</option>)}
          </select>
        </label>
        {local&&selectedCluster&&<>
          {assignedHere?<p className="success-inline">A map már szerepel a kiválasztott
            cluster regiszterében. Ez önmagában nem igazolja a Store-tagságot.</p>:
          existing?<p className="warning-inline">Ez a map már másik clusterhez tartozik.
            Áthelyezést csak a teljes cluster-regiszter natív felületén lehet jóváhagyni.</p>:
          protectedCluster?<p className="warning-inline">A célcluster történeti Store-v1
            tulajdonú. Itt nem elegendő a regiszter tagsága: az új Store-writer/session
            bizonyítás külön szükséges. Az eredeti öt map védelme változatlan.</p>:
          local.storeProtected||local.observedReadOnlyImported?
          <p className="warning-inline">A védett vagy csak megfigyelt map nem
            adható hozzá ezen a friss-ASA útvonalon.</p>:
          <button type="button" className="secondary" disabled={!canAdd||busy}
            onClick={()=>{void prepareMembership();}}>Cluster-tagság natív előnézete</button>}
          {membership&&<div role="region" aria-label="Egyetlen új map cluster-jóváhagyása">
            <p>Csak {local.name} hozzáadása · nincs másik map eltávolítva
              vagy áthelyezve.</p>
            <button type="button" className="primary" disabled={busy}
              onClick={()=>{void applyMembership();}}>
              Egyetlen map hozzáadásának jóváhagyása</button>
          </div>}
          {safelyEnrolled&&<FreshStoreEnrollmentPanel mapId={local.id}
            onConfigured={async()=>{await refresh();await onImported();}}/>}
          {assignedHere&&<button type="button" className="secondary"
            onClick={()=>onOpenMap(local.id)}>Mapadatlap és natív beállítások</button>}
        </>}
      </fieldset>}
      {notice&&<p role="status" className="success-inline"><CheckCircle2 size={16}/>{notice}</p>}
      {error&&<p role="alert" className="error-inline"><AlertTriangle size={16}/>{error}</p>}
      <p className="muted small"><Shield size={14}/> A „cluster-regiszterhez adva”,
        a „Store indítási profil előkészítve” és a „közös Store/karakterutazás
        igazolt” három külön állapot. A varázsló nem állít be meg nem vizsgált
        Store-writer vagy kétgépes transferjogot.</p>
    </div>
  </section>;
}
