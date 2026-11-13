import {useEffect,useState} from "react";
import {Shield,CheckCircle2,RefreshCw,AlertTriangle} from "lucide-react";
import {managerBridge,type FreshStoreEnrollmentStatus,type FreshStoreEnrollmentPreview,
  type RegisteredCluster} from "./bridge";

export function FreshStoreEnrollmentPanel({mapId,onConfigured}:{
  mapId:string;onConfigured:()=>Promise<void>
}){
  const [status,setStatus]=useState<FreshStoreEnrollmentStatus|null>(null);
  const [clusters,setClusters]=useState<RegisteredCluster[]>([]);
  const [selected,setSelected]=useState("");
  const [preview,setPreview]=useState<FreshStoreEnrollmentPreview|null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  useEffect(()=>{
    let active=true;
    setStatus(null);setClusters([]);setSelected("");setPreview(null);
    setApproved(false);setError("");setNotice("");
    Promise.all([managerBridge.freshStoreEnrollmentStatus(mapId),
      managerBridge.clusterRegistrySnapshot()]).then(([info,registry])=>{
      if(!active)return;
      setStatus(info);
      const memberships=registry.clusterRegistry.mapAssignments.filter(x=>
        x.mapId.toLowerCase()===mapId.toLowerCase());
      const options=memberships.length===1?registry.clusterRegistry.clusters.filter(x=>
        x.clusterId===memberships[0].clusterId&&x.clusterId!=="kovacluster"):[];
      setClusters(options);setSelected(options[0]?.clusterId??"");
    }).catch(()=>{
      if(active)setError("A helyi map és a clusterhozzárendelés nem igazolható. Nem történt módosítás.");
    });
    return ()=>{active=false;};
  },[mapId]);
  const reset=()=>{setPreview(null);setApproved(false);setError("");setNotice("");};
  const inspect=async()=>{
    if(!selected||busy)return;
    reset();setBusy(true);
    try{setPreview(await managerBridge.freshStoreEnrollmentPreview(mapId,selected));}
    catch(e){
      const code=e instanceof Error?e.message:"UM-FRESH-STORE-PREFLIGHT";
      const hints:Record<string,string>={
        "UM-FRESH-STORE-KOVA-CLUSTER-RESERVED":
          "Az eredeti KovaCluster nem érhető el az új, üres Store-map általános regisztrációjával. Az eredeti kétgazdás transferazonosságot külön kell igazolni; az eredeti világot nem módosítottuk.",
        "UM-FRESH-STORE-RCON-SETUP-REQUIRED-BEFORE-ENROLLMENT":
          "Először a map RCON fülén konfiguráld a helyi RCON-portot és jelszót, utána kérj új Store-előellenőrzést.",
        "UM-FRESH-STORE-MAP-MEMBERSHIP-REQUIRED":
          "A map nincs pontosan egyszer hozzárendelve a kiválasztott clusterhez. Nyisd meg a Cluster fület és mentsd a hozzárendelést.",
        "UM-FRESH-STORE-REMOTE-TRANSFER-NOT-CERTIFIED":
          "Többgépes Store átviteli könyvtár azonossága még nem igazolt. Az UNC-útvonal önmagában nem bizonyít közös Store-t.",
        "UM-FRESH-STORE-NEW-STEAM-INSTALL-REQUIRED":
          "A map nem igazoltan új, üres SteamCMD-telepítés. Meglévő mapot a külön importvarázslóval vegyél át.",
        "UM-FRESH-STORE-EXISTING-SAVE-OR-REPARSE":
          "A map mappájában már vannak világ-, játékos- vagy tribe-adatok, vagy nem biztonságos hivatkozás. A Manager ezeket nem konvertálja.",
        "UM-FRESH-STORE-ASA-NOT-PROVEN-STOPPED":
          "A map teljes ASA/Loader folyamata nem igazoltan állt le. Nem történt Store-regisztráció."
      };
      setError(hints[code]??("Új, üres Store-map nem készíthető elő ("+
        code+"). A meglévő ASA és Store fájlokat nem módosítottuk."));
    }
    finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!preview||!approved||busy)return;
    const token=preview.token;setPreview(null);setApproved(false);
    setBusy(true);setError("");setNotice("");
    try{
      const saved=await managerBridge.freshStoreEnrollmentApply(token);
      setStatus({mapId,mapName:saved.mapName,enrolled:true,historicalProtected:false,
        clusterId:saved.clusterId,startsServer:false,originalStoreTouched:false,
        evidence:"LOCAL_MAP_NEW_STORE_ENROLLMENT_STATUS"});
      setNotice("Új, üres Store-map profil mentve, konverzió és ASA-indítás nélkül.");
      try{await onConfigured();}
      catch{setError("A mentés sikeres, de az állapotfrissítés meghiúsult. Ne ismételd meg a műveletet.");}
    }catch{
      setError("Az eredmény nem igazolható. Nincs automatikus ismétlés; ellenőrizd a natív naplót.");
    }finally{setBusy(false);}
  };
  return <section className="glass panel" aria-label="Új üres Store-map">
    <div className="panel-head"><h2><Shield size={18}/> Új, üres Store-map</h2>
      <span className="mod-tag amber">Külön jóváhagyás</span></div>
    <div className="panel-inner">
      {status?.historicalProtected?<div className="warning-inline" role="alert">
        Eredeti Kova Store-map: az új mapos regisztráció itt tiltva.</div>:
      status?.enrolled?<div className="success-inline" role="status">
        <CheckCircle2 size={16}/> Store-profil regisztrálva: {status.clusterId}.
        A tényleges játékbeli utazás még nem igazolt.</div>:<>
        <p className="muted small">Csak soha nem indított, mentés nélküli SteamCMD-maphoz.
          A Store-regisztráció ELŐTT készítsd el a helyi RCON/INI-beállítást a
          RCON fülön, majd a Cluster fülön rendeld hozzá egy létrehozott clusterhez.
          A KovaCluster és az igazolatlan többgépes megosztás továbbra is védett.</p>
        <label><span>Jóváhagyott helyi cluster</span>
          <select aria-label="Új Store-map cluster" value={selected}
            disabled={busy||clusters.length===0}
            onChange={e=>{setSelected(e.target.value);reset();}}>
            {clusters.length===0?<option value="">Nincs helyi clusterhozzárendelés</option>:
              clusters.map(c=><option key={c.clusterId} value={c.clusterId}>
                {c.name} ({c.clusterId})</option>)}
          </select>
        </label>
        <div className="draft-actions">
          <button type="button" className="secondary" disabled={!selected||busy}
            onClick={()=>{void inspect();}}><RefreshCw size={16}/>
            Üres Store-map előellenőrzése</button>
        </div>
        {preview&&<div className="initial-preview" role="region"
          aria-label="Üres Store-map előnézete">
          <h3>Új Store-map előnézet</h3>
          <div className="initial-preview-grid">
            <span>Map</span><b>{preview.mapName}</b>
            <span>Cluster</span><b>{preview.clusterId}</b>
            <span>Steam build</span><b>{preview.steamBuild}</b>
            <span>Game / Query</span><b>{preview.gamePort} / {preview.queryPort}</b>
            <span>Konverzió</span><b>Nem</b>
            <span>ASA Start</span><b>Nem</b>
            <span>Másik host átviteli azonossága</span><b>Nem igazolt</b>
          </div>
          <label className="initial-confirm">
            <input type="checkbox" checked={approved}
              onChange={e=>setApproved(e.target.checked)} disabled={busy}/>
            Jóváhagyom az üres map helyi Store-indítási profiljának mentését.
          </label>
          <div className="draft-actions">
            <button type="button" className="primary" disabled={!approved||busy}
              onClick={()=>{void apply();}}>Új Store-profil mentése</button>
            <button type="button" className="secondary" disabled={busy}
              onClick={reset}>Mégse</button>
          </div>
        </div>}
      </>}
      {notice&&<div role="status" className="success-inline"><CheckCircle2 size={16}/>{notice}</div>}
      {error&&<div role="alert" className="error-inline"><AlertTriangle size={16}/>{error}</div>}
      <p className="muted tiny">Nem fut -converttostore, nem módosul a Kova-session vagy a cluster-átviteli fájl, és nincs automatikus indítás. Többgazdás adatvédelem külön elfogadást igényel.</p>
    </div>
  </section>;
}
