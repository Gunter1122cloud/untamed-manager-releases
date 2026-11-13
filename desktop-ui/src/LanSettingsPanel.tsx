import {useCallback,useEffect,useState,type FormEvent} from "react";
import {AlertTriangle,CheckCircle2,KeyRound,Network,RefreshCw,Shield,Wifi,WifiOff} from "lucide-react";
import {managerBridge,type LanPreview,type LanStatus} from "./bridge";
import "./lan-pairing.css";

export function LanSettingsPanel(){
  const [status,setStatus]=useState<LanStatus|null>(null);
  const [listen,setListen]=useState("0");
  const [hostId,setHostId]=useState("");
  const [hostName,setHostName]=useState("");
  const [address,setAddress]=useState("");
  const [port,setPort]=useState("39290");
  const [secret,setSecret]=useState("");
  const [reveal,setReveal]=useState(false);
  const [action,setAction]=useState<"upsert"|"remove">("upsert");
  const [preview,setPreview]=useState<LanPreview|null>(null);
  const [working,setWorking]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");

  const reload=useCallback(async()=>{
    try{
      const s=await managerBridge.lanStatus();
      setStatus({...s,peers:s.peers??[]});
      setListen(String(s.listenPort));
    }catch{setError("A natív LAN-beállítás nem olvasható.");}
  },[]);
  useEffect(()=>{void reload();},[reload]);
  const invalidate=()=>{setPreview(null);setNotice("");setError("");};
  const choosePeer=(id:string)=>{
    const selected=status?.peers.find(x=>x.hostId===id);
    setHostId(id);
    if(selected){setHostName(selected.hostName);setAddress(selected.address);setPort(String(selected.port));}
    if(!id){setHostName("");setAddress("");setPort("39290");}
    invalidate();
  };
  const generate=()=>{
    const random=crypto.getRandomValues(new Uint8Array(32));
    setSecret(Array.from(random,b=>b.toString(16).padStart(2,"0")).join(""));
    setReveal(true);invalidate();
  };
  const preflight=async(event:FormEvent)=>{
    event.preventDefault();invalidate();
    const local=Number(listen),other=Number(port);
    if(!Number.isInteger(local)||local!==0&&(local<1024||local>65535)){
      setError("A helyi LAN-port 0 (kikapcsolva) vagy 1024–65535 lehet.");return;
    }
    if(!/^[0-9a-fA-F]{32}$|^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(hostId.trim())||
       hostId.replaceAll("-","").toLowerCase()===status?.localHostId?.replaceAll("-","").toLowerCase()){
      setError("Adj meg egy másik Managerhez tartozó, tartós Host ID-t.");return;
    }
    if(action==="upsert"){
      if(!hostName.trim()||!address.trim()||!Number.isInteger(other)||other<1024||other>65535){
        setError("A peer neve, privát IPv4-címe és fogadóportja kötelező.");return;
      }
      if(secret&&secret.length<24){setError("A közös kulcs legalább 24 karakter legyen.");return;}
    }else if(!peerList.some(x=>x.hostId===hostId.trim()||x.hostId.toLowerCase()===hostId.trim().toLowerCase())){
      setError("Eltávolításhoz válassz egy már párosított gazdagépet.");return;
    }else if(peerList.length===1&&local!==0){
      setError("Az utolsó peer eltávolításához előbb állítsd a helyi fogadóportot 0-ra.");return;
    }
    setWorking(true);
    try{
      const p=await managerBridge.lanPreview(local,hostId.trim(),hostName.trim(),address.trim(),other,secret,action);
      setPreview(p);
      // The native bridge retains the one-time approval token; plaintext does
      // not remain in React state while the operator reviews the change.
      setSecret("");setReveal(false);
    }catch(e){
      setError("A LAN-változtatás előnézete elutasítva ("+
        (e instanceof Error?e.message:"UM-LAN-INVALID-PRIVATE-PEER")+
        "). Csak privát IPv4-cím engedélyezett.");
    }finally{setWorking(false);}
  };
  const save=async()=>{
    if(!preview||working)return;
    const token=preview.token;setPreview(null);setWorking(true);setError("");
    try{
      await managerBridge.lanApply(token);
      setNotice(preview.action==="remove"?"A peer eltávolítva a helyi Manager-profilból.":
        "A peer mentve a helyi Manager-profilba.");
      await reload();
    }catch(e){
      setError("A helyi LAN-profil mentése elutasítva ("+
        (e instanceof Error?e.message:"UM-LAN-SAFE-UPDATE-FAILED")+").");
    }finally{setWorking(false);}
  };
  const probeOriginalPhysical=async()=>{
    if(working)return;
    setWorking(true);setError("");setNotice("");
    try{
      const response=await managerBridge.lanPhysicalStoreProbe();
      if(!response.localPhysicalDirectoryIdVerified||
         !response.originalMetadataUnchanged||response.productionRestoreAllowed)
        throw new Error("UM-STORE-PHYSICAL-NOT-VERIFIED");
      setNotice("Helyi fizikai UNC fájlazonosság ellenőrizve. A másik párosított gépen is indítsd el ugyanezt az olvasási ellenőrzést. A tanúsítvány 5 percig él; éles Store-írójogot nem ad.");
      await reload();
    }catch(e){
      setError("A fizikai Store-v1 transferkönyvtár nem ellenőrizhető. Az eredeti helyi metaadatoknak és a megosztás Windows fájlazonosítójának egyeznie kell ("+
        (e instanceof Error?e.message:"UM-STORE-PHYSICAL-UNC-UNAVAILABLE")+").");
    }finally{setWorking(false);}
  };
  const probeHistoricalSaves=async()=>{
    if(working)return;
    setWorking(true);setError("");setNotice("");
    try{
      const proof=await managerBridge.lanHistoricalSaveProbe();
      if(!proof.localHistoricalReceiptsReadOnly||
         proof.crossHostSaveCompleteCertified||proof.sharedStoreWriterCertified||
         proof.productionRestoreAllowed||proof.rconUsed||proof.originalStoreFilesWritten)
        throw new Error("UM-STORE-SAVE-PAIR-UNSAFE-WITNESS");
      setNotice("Helyi történeti SaveComplete naplók: "+
        proof.recentCompletedMapReceipts+"/5 friss, tartósan ellenőrzött bejegyzés. "+
        "A másik gépen külön indítsd el. Ez nem bizonyít jelenlegi Store-írójogot vagy éles restore-készséget.");
      await reload();
    }catch(e){
      setError("A helyi Store-v1 SaveComplete naplók nem ellenőrizhetők. "+
        "Függő, sérült vagy eltérő mapforrás esetén a tanúsítás tiltott ("+
        (e instanceof Error?e.message:"UM-STORE-SAVE-PAIR-UNAVAILABLE")+").");
    }finally{setWorking(false);}
  };
  const peerList=status?.peers??[];

  return <section className="glass panel lan-pairing-panel">
    <div className="panel-head"><h2><Network size={19}/> Többgépes LAN-párosítás</h2>
      <span className="mod-tag green">Hitelesített állapot / távoli művelet nélkül</span></div>
    <div className="panel-inner">
      <p className="muted small">Tetszőleges számú Manager párosítható külön Host ID-val, privát IPv4-címmel és saját DPAPI-védett kulccsal.
        A kapcsolat kizárólag hitelesített állapotot olvas; távoli Start/Stop, RCON és Store-v1 írás nincs engedélyezve.</p>
      <div className="lan-status" role="status">
        {status?.remoteFresh?<Wifi size={17}/>:<WifiOff size={17}/>}
        <div><strong>{status?.hostName||"Helyi gazdagép"}</strong>
          <span>{status?.remoteState||"LAN-konfiguráció lekérése…"}</span>
          <small>{peerList.filter(x=>x.online).length} / {peerList.length} peer elérhető</small>
          <small>Saját Host ID (a társított Manageren ezt rögzítsd): <code>{status?.localHostId||"—"}</code></small>
        </div>
        <button type="button" className="quiet" disabled={working} aria-label="LAN állapot újraolvasása"
          onClick={()=>{void reload();}}><RefreshCw size={16}/></button>
      </div>
      {peerList.length>0&&<section className="lan-peer-list" data-testid="lan-peer-list" aria-label="Párosított gazdagépek">
        <h3>Párosított gazdagépek ({peerList.length})</h3>
        {peerList.map(peer=><button type="button" className="lan-peer-row" key={peer.hostId}
          onClick={()=>choosePeer(peer.hostId)} aria-label={`Szerkesztés: ${peer.hostName}`}>
          <span className={peer.online?"peer-dot online":"peer-dot"}/>
          <strong>{peer.hostName}</strong><code>{peer.hostId}</code>
          <span>{peer.address}:{peer.port}</span>
          <small>{peer.online?`${peer.mapCount} map · friss`:peer.stateCode}</small>
        </button>)}
      </section>}
      {status?.originalStorePair&&<section className="lan-store-proof" data-testid="lan-store-proof" aria-label="Eredeti többhostos Store-v1 egyezőség">
        <div className="lan-store-heading"><Shield size={18}/><strong>Eredeti Store-v1 · helyi összevetés</strong>
          <span>{status.originalStorePair.copiedMetadataPairMatches?"Történeti metaadatok egyeznek":"További ellenőrzés szükséges"}</span></div>
        <p className="muted small">{
          status.originalStorePair.originalPeerHostId
            ? "Eredeti korábbi kezelőalkalmazás partner: "+(status.peers.find(p=>
                p.hostId.replace(/-/g,"").toLowerCase()===
                status.originalStorePair.originalPeerHostId.toLowerCase())?.hostName??"ismeretlen Manager")
            : "Eredeti korábbi kezelőalkalmazás partner: még nem azonosítható ("
              +status.originalStorePair.statusCode+")"
        }. Az egyezés kizárólag történeti metaadat, nem élő Store-writer vagy karakterutazási bizonyíték.</p>
        <div className="lan-store-grid">
          <div><span>Azonos Store-session</span><b>{status.originalStorePair.sameSharedSession?"Egyező":"Nem igazolt"}</b></div>
          <div><span>Azonos UNC-megosztás</span><b>{status.originalStorePair.sameSharedPath?"Egyező":"Nem igazolt"}</b></div>
          <div><span>Azonos fizikai transferkönyvtár (Windows fájl-ID)</span>
            <b>{status.originalStorePair.samePhysicalTransferDirectory?
              "Igazolt, friss kétoldali fájlazonosítóval":
              status.originalStorePair.bothPhysicalWitnessesFresh?
                "Különböző fizikai könyvtár – blokkolva":"Kétoldali ellenőrzés szükséges"}</b></div>
          <div><span>Mindkét gépen öt friss, történeti helyi SaveComplete napló</span>
            <b>{status.originalStorePair.bothHaveFiveRecentHistoricalLocalSaveReceipts?
              "Mindkét oldali korábbi naplóbizonylat egyező előfeltételekkel" :
              "Nincs kétoldali friss történeti bizonyíték – tiltva"}</b></div>
          <div><span>Külön gazdagépazonosítók</span><b>{status.originalStorePair.distinctDeviceIds?"Igazolt":"Nem igazolt"}</b></div>
          <div><span>Öt aktív Store-map az eredeti forrásfájlokban</span><b>{status.originalStorePair.bothHaveFiveActiveMaps?"Igazolt":"Nem igazolt"}</b></div>
          <div><span>Az eredeti öt-öt GUID és mapnév tényleges Manager-regisztrációja</span>
            <b>{status.originalStorePair.bothOriginalFiveRegisteredInManagers?
              "Mindkét Managerben igazolt":"Hiányzik vagy eltér – ellenőrzés szükséges"}</b></div>
        </div>
        <div className="draft-actions">
          <button type="button" className="secondary" disabled={working}
            onClick={()=>{void probeOriginalPhysical();}}>
            <Shield size={16}/> Helyi Store transferkönyvtár fizikai ellenőrzése
          </button>
          <button type="button" className="secondary" disabled={working}
            onClick={()=>{void probeHistoricalSaves();}}>
            <Shield size={16}/> Helyi 5-map SaveComplete-naplók ellenőrzése
          </button>
        </div>
        <p className="muted small">Az UNC név egyezése nem bizonyít azonos fizikai könyvtárat.
          A gomb az eredeti négy korábbi kezelőalkalmazás metaadatfájlt változtatás nélkül olvassa,
          és az UNC könyvtár Windows kötet-/fájlazonosítóját ellenőrzi.
          Mindkét gépen külön kell elindítani. A rövid életű lenyomat csak
          a hitelesített LAN státuszban utazik; írás, új session, locktörlés,
          SaveWorld vagy éles restore nincs. Ez továbbra sem Store-írójog- vagy
          játékbeli karaktermegőrzési tanúsítvány.
          Az ötmapos SaveComplete-ellenőrzés kizárólag a Manager saját naplóit olvassa;
          a valódi kétgépes Store-writer quiescence és közös védőmentés még nincs igazolva.</p>
      </section>}
      <form className="lan-settings-form" onSubmit={e=>{void preflight(e);}}>
        <label>Művelet
          <select data-testid="lan-action" aria-label="Peer mĹ±velet" value={action} onChange={e=>{setAction(e.target.value as "upsert"|"remove");invalidate();}} disabled={working}>
            <option value="upsert">Peer hozzáadása vagy frissítése</option>
            <option value="remove">Peer eltávolítása</option>
          </select>
        </label>
        <label>Helyi fogadóport (0 = kikapcsolva)
          <input data-testid="lan-listen-port" aria-label="Helyi LAN port" inputMode="numeric" value={listen}
            onChange={e=>{setListen(e.target.value);invalidate();}} disabled={working}/>
        </label>
        <label className="lan-peer-picker">Meglévő peer betöltése
          <select data-testid="lan-peer-select" aria-label="Select paired peer" value="" disabled={working||!peerList.length}
            onChange={e=>choosePeer(e.target.value)}>
            <option value="">Új peer / kézi megadás</option>
            {peerList.map(item=><option value={item.hostId} key={item.hostId}>{item.hostName} · {item.address}</option>)}
          </select>
        </label>
        <label>Távoli tartós Host ID
          <input data-testid="lan-host-id" aria-label="Remote Host ID" spellCheck={false} value={hostId}
            placeholder="A távoli Manager saját Host ID-ja" maxLength={40}
            onChange={e=>{setHostId(e.target.value);invalidate();}} disabled={working}/>
        </label>
        {action==="upsert"&&<>
          <label>Gazdagép megjelenített neve
            <input data-testid="lan-host-name" aria-label="Remote host name" value={hostName} maxLength={100}
              onChange={e=>{setHostName(e.target.value);invalidate();}} disabled={working}/>
          </label>
          <label>Távoli Manager privát IPv4-címe
            <input data-testid="lan-address" aria-label="Remote Manager private IPv4 address" value={address} placeholder="192.168.1.200"
              onChange={e=>{setAddress(e.target.value);invalidate();}} disabled={working}/>
          </label>
          <label>Távoli Manager fogadóportja
            <input data-testid="lan-peer-port" aria-label="Remote LAN port" inputMode="numeric" value={port}
              onChange={e=>{setPort(e.target.value);invalidate();}} disabled={working}/>
          </label>
          <label className="lan-secret">Párosítási kulcs · üresen hagyva a meglévő kulcs marad
            <input data-testid="lan-key" aria-label="LAN pairing key" type={reveal?"text":"password"} autoComplete="off"
              spellCheck={false} maxLength={256} value={secret}
              placeholder="Új peerhez legalább 24 karakter" onChange={e=>{setSecret(e.target.value);invalidate();}} disabled={working}/>
            <span className="lan-secret-actions">
              <button type="button" className="secondary" disabled={working} onClick={generate}><KeyRound size={15}/> Véletlen kulcs készítése</button>
              <button type="button" className="quiet" disabled={working||!secret} onClick={()=>setReveal(v=>!v)}>{reveal?"Elrejtés":"Megjelenítés"}</button>
            </span>
          </label>
        </>}
        <button data-testid="lan-preview-button" className="secondary" type="submit" disabled={working||action==="remove"&&!hostId}>
          <Shield size={16}/> Változtatás ellenőrzése
        </button>
      </form>
      {preview&&<div className="lan-preview" data-testid="lan-preview" role="region" aria-label="LAN-változtatás jóváhagyása">
        <b><CheckCircle2 size={17}/> Helyi változtatás előnézete</b>
        <p>{preview.action==="remove"?"Peer eltávolítása":"Peer mentése"}: {preview.hostName||preview.hostId} · helyi fogadóport: {preview.listenPort||"kikapcsolva"}.</p>
        {preview.action==="upsert"&&<p>Távoli cím: {preview.peerAddress}:{preview.peerPort}. A kulcs {preview.pairingKeyWillChange?"új DPAPI-védett értékre cserélődik":"változatlan marad"}.</p>}
        <p>A beállítás csak ezen a Manageren lép életbe újraindítás után. A távoli géphez nem küldünk módosító parancsot.</p>
        <div className="lan-preview-actions">
          <button data-testid="lan-save-button" className="primary" type="button" disabled={working} onClick={()=>{void save();}}>Helyi beállítás mentése</button>
          <button className="secondary" type="button" disabled={working} onClick={()=>setPreview(null)}>Mégse</button>
        </div>
      </div>}
      {notice&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{notice} A LAN-listener újraindításához a Managert kell újraindítani, az ASA mapokat nem.</div>}
      {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
      <div className="warning-inline"><Shield size={16}/> A kulcs titkosított formában marad helyben, és nem olvasható vissza a React felületre.
        A párosítás mindkét Manageren külön, a másik fél Host ID-jával történjen. Tűzfalat, ASA-fájlt vagy eredeti profilt nem módosítunk.</div>
    </div>
  </section>;
}
