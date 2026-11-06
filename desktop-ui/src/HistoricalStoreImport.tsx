import { useRef, useState, type FormEvent } from "react";
import { AlertTriangle, Archive, CheckCircle2, Shield, LockKeyhole } from "lucide-react";
import { managerBridge, type HistoricalStoreList, type HistoricalStorePreview, type HistoricalPairEvidence, type HistoricalLocalStoreStatus, type HistoricalLocalStoreParity, type HistoricalLaunchEvidence } from "./bridge";

export function HistoricalStoreImport({onImported}: {onImported:()=>Promise<void>}) {
  const [archive,setArchive]=useState("");
  const [known,setKnown]=useState<HistoricalStoreList | null>(null);
  const [mapId,setMapId]=useState("");
  const [root,setRoot]=useState("");
  const [preview,setPreview]=useState<HistoricalStorePreview | null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [registering,setRegistering]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const [localProbe,setLocalProbe]=useState<HistoricalLocalStoreStatus|null>(null);
  const [localBusy,setLocalBusy]=useState(false);
  const [localError,setLocalError]=useState("");
  const [parity,setParity]=useState<HistoricalLocalStoreParity|null>(null);
  const [parityError,setParityError]=useState("");
  const [parityBusy,setParityBusy]=useState(false);
  const parityEpoch=useRef(0);
  const invalidateParity=()=>{parityEpoch.current++;setParity(null);setParityError("");setParityBusy(false);};
  const [coordZip,setCoordZip]=useState("");
  const [localZip,setLocalZip]=useState("");
  const [pairResult,setPairResult]=useState<HistoricalPairEvidence | null>(null);
  const [pairError,setPairError]=useState("");
  const [pairBusy,setPairBusy]=useState(false);
  const [launchCommand,setLaunchCommand]=useState("");
  const [launchEvidence,setLaunchEvidence]=useState<HistoricalLaunchEvidence|null>(null);
  const [launchBusy,setLaunchBusy]=useState(false);
  const [launchError,setLaunchError]=useState("");
  const launchEpoch=useRef(0);
  const invalidateLaunch=()=>{launchEpoch.current++;setLaunchEvidence(null);setLaunchError("");setLaunchBusy(false);};
  // A late response must never revive approval for a different ZIP/map/root.
  const importEpoch=useRef(0);
  const pairEpoch=useRef(0);
  const invalidateImport=()=>{importEpoch.current++;setBusy(false);reset();invalidateLaunch();setLaunchCommand("");};
  const invalidatePair=()=>{pairEpoch.current++;setPairBusy(false);setPairResult(null);setPairError("");};

  const reset=()=>{setPreview(null);setApproved(false);setError("");setNotice("");};
  const inspectArchive=async(e:FormEvent)=>{
    e.preventDefault();const epoch=++importEpoch.current;reset();setKnown(null);setBusy(true);
    try{
      const result=await managerBridge.historicalList(archive);
      if(epoch!==importEpoch.current)return;
      setKnown(result);setMapId(result.maps[0]?.mapId??"");
    }catch{
      if(epoch===importEpoch.current)setError("A privát Manager ZIP nem ellenőrizhető, hiányos vagy megváltozott. A fájlokat nem módosítottuk.");
    }finally{if(epoch===importEpoch.current)setBusy(false);}
  };
  const previewImport=async(e:FormEvent)=>{
    e.preventDefault();const epoch=++importEpoch.current;reset();setBusy(true);
    try{
      const result=await managerBridge.historicalPreview(archive,mapId,root);
      if(epoch===importEpoch.current)setPreview(result);
    }catch(e){
      if(epoch!==importEpoch.current)return;
      const code=e instanceof Error?e.message:"UM-HISTORICAL-VERIFICATION-FAILED";
      const hints:Record<string,string>={
        "UM-HISTORICAL-ROOT-MISMATCH":"A megadott helyi ASA telepítés nem egyezik a korábbi archivált eredeti mapgyökérrel.",
        "UM-HISTORICAL-STOP-REQUIRED":"A map/loader még fut, vagy nem igazolható a szabályos leállítás.",
        "UM-ASA-EXE-MISSING":"A map könyvtárában nem található az eredeti ASA-szerver EXE."
      };
      setError(hints[code]??"A natív Store-v1 előellenőrzés elutasította az importot ("+code+").");
    }finally{if(epoch===importEpoch.current)setBusy(false);}
  };
  const register=async()=>{
    if(!preview || !approved || busy || registering)return;
    const epoch=++importEpoch.current;
    setRegistering(true);setBusy(true);setError("");setNotice("");
    try{
      const receipt=await managerBridge.historicalRegister(preview.token);
      if(epoch!==importEpoch.current)return;
      setPreview(null);setApproved(false);
      setNotice(receipt.mapName+" felvéve az eredeti mapazonosítóval. A Store-v1 zárolt: a szerver nem indulhat, amíg a teljes régi indítási profil és a két host állapota nincs igazolva.");
      try { await onImported(); }
      catch { setError("A zárolt regisztráció megtörtént, de a szerverlista frissítése sikertelen. A nézet újranyitásával ellenőrizhető."); }
    }catch(e){
      if(epoch!==importEpoch.current)return;
      setError("A Store-v1 regisztráció meghiúsult ("+(e instanceof Error?e.message:"UM-HISTORICAL-VERIFICATION-FAILED")+"). Az eredeti világ- és karakterfájlokhoz nem nyúltunk.");
    }finally{setRegistering(false);if(epoch===importEpoch.current)setBusy(false);}
  };

  const checkLaunch=async(e:FormEvent)=>{
    e.preventDefault();
    if(!known || !mapId || !launchCommand.trim() || registering)return;
    const epoch=++launchEpoch.current;
    setLaunchBusy(true);setLaunchError("");setLaunchEvidence(null);
    try{
      const result=await managerBridge.historicalLaunchEvidence(archive,mapId,launchCommand);
      if(epoch===launchEpoch.current){setLaunchCommand("");setLaunchEvidence(result);}
    }catch{
      if(epoch===launchEpoch.current){setLaunchCommand("");setLaunchError("A parancssor csak olvasható összevetése sikertelen.");}
    }finally{if(epoch===launchEpoch.current)setLaunchBusy(false);}
  };
  const checkOriginalLocal=async()=>{
    setLocalBusy(true);setLocalError("");setLocalProbe(null);
    try{setLocalProbe(await managerBridge.historicalLocalStatus());}
    catch(e){setLocalError((e instanceof Error&&e.message==="UM-LOCAL-STORE-NOT-FOUND")?
      "Ezen a Windows-fiókon nem található a korábbi kezelő helyi Store-v1 profilja. A ZIP-másolatos ellenőrzés továbbra is használható.":
      "A négy eredeti Store-v1 fájl nem igazolható. Semmi nem változott az eredeti adatokban.");}
    finally{setLocalBusy(false);}
  };
  const checkOriginalParity=async()=>{
    if(!archive.trim()||registering)return;
    const epoch=++parityEpoch.current;
    setParity(null);setParityError("");setParityBusy(true);
    try{
      const matched=await managerBridge.historicalLocalParity(archive);
      if(epoch===parityEpoch.current)setParity(matched);
    }catch(e){
      if(epoch===parityEpoch.current)setParityError(
        e instanceof Error&&e.message==="UM-STORE-PARITY-LOCAL-MISSING"?
          "A korábbi Manager helyi Store-mappája nem található ezen a gépen.":
          "A négy fájl egyezősége nem ellenőrizhető. Az eredeti fájlok változatlanok maradtak.");
    }finally{if(epoch===parityEpoch.current)setParityBusy(false);}
  };
  const comparePeers=async(e:FormEvent)=>{
    e.preventDefault();const epoch=++pairEpoch.current;setPairResult(null);setPairError("");setPairBusy(true);
    try {
      const result=await managerBridge.historicalPairedEvidence(coordZip,localZip);
      if(epoch===pairEpoch.current)setPairResult(result);
    }
    catch { if(epoch===pairEpoch.current)setPairError("A két történeti Store-másolat összevetése sikertelen. Semmilyen adatot nem módosítottunk."); }
    finally {if(epoch===pairEpoch.current)setPairBusy(false);}
  };

  return <section className="glass panel historical-import">
    <div className="panel-head">
      <h2><Archive size={19}/> Untamed Store-v1 · meglévő map átvétele</h2>
      <span className="mod-tag amber"><LockKeyhole size={13}/> Indítás zárolva</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">
        A már működő Store-v1 rendszert nem konvertáljuk újra. A korábbi Manager hitelesített ZIP-másolatából
        ellenőrizzük az eredeti mapazonosítót, mapgyökeret, portokat és a modlistát.
        A ZIP nem tartalmaz teljes indítási parancssort: emiatt a regisztrált map
        <strong> védett és nem indítható</strong> a további kétgépes ellenőrzésig.
      </p>
      <section className="historical-peer-check" aria-label="Korábbi helyi Store-v1 állapot">
        <h3>Eredeti helyi Store-v1 állapot · közvetlen, csak olvasási ellenőrzés</h3>
        <p className="muted small">A jelenlegi Windows-fiók korábbi kezelőjének négy helyi Store-metaadatfájlját ellenőrzi. Nem olvas SMB-megosztást, nem futtat ASA-parancsot és nem módosít mentéseket.</p>
        <button type="button" className="secondary" disabled={localBusy} onClick={()=>{void checkOriginalLocal();}}>
          <Shield size={16}/> {localBusy?"Helyi ellenőrzés...":"Eredeti helyi Store állapotának ellenőrzése"}
        </button>
        {localProbe&&<div className="historical-peer-result" role="status">
          <div className="historical-proof-grid">
            <span>Helyi öt Store-map</span><strong>{localProbe.maps.length} db</strong>
            <span>Eredeti konfigurációhash</span><strong>{localProbe.configHashVerified?"Igazolt":"Eltérés"}</strong>
            <span>Tartós helyi zár</span><strong>{localProbe.localLockVerified?"Egyezik":"Hibás"}</strong>
            <span>Mind az öt map ACTIVE</span><strong>{localProbe.allLocalMapsActive?"Igen":"Nem igazolt"}</strong>
            <span>Kétgépes élő állapot / karakterutazás</span><strong>NEM IGAZOLT</strong>
          </div>
          <div className="historical-peer-hosts">
            {localProbe.maps.map(m=><div className="historical-peer-host" key={m.mapKey}>
              <b>{m.mapKey}</b><span>ACTIVE: {m.active?"igen":"nem"} · session: {m.sessionMatches?"egyező":"eltérő"}</span>
              <span>Mapgyökér / lock: {m.rootMatches?"OK":"?"} / {m.selected?"OK":"?"}</span>
            </div>)}
          </div>
        </div>}
        {localError&&<div role="alert" className="error-inline"><AlertTriangle size={16}/>{localError}</div>}
        <div className="historical-parity-check">
          <p className="muted small">Ha a régi Manager ZIP-másolata is rendelkezésre áll, az eredeti négy Store-fájl változatlan bájtegyezőségét külön ellenőrizheted.</p>
          <button type="button" className="secondary" disabled={!archive.trim()||parityBusy||registering}
            onClick={()=>{void checkOriginalParity();}}><Shield size={16}/>
            {parityBusy?"Store-fájlok összevetése...":"Helyi Store és archivált ZIP egyeztetése"}
          </button>
          {parity&&<div role="status" className="historical-peer-result">
            <div className="historical-proof-grid">
              <span>ZIP ellenőrzése</span><strong>{parity.archiveVerified?"39 fájl ellenőrizve":"Nem igazolt"}</strong>
              <span>Mind a négy eredeti metaadatfájl</span><strong>{parity.allFourExactlyMatch?"BÁJT-PONTOSAN EGYEZIK":"Eltérés / más időpontú másolat"}</strong>
              <span>Helyi Store ACTIVE</span><strong>{parity.localStoreActive?"Igen":"Nem teljesül"}</strong>
              <span>Éles karakterutazás</span><strong>NEM IGAZOLT</strong>
            </div>
            <div className="historical-peer-hosts">
              {parity.files.map(f=><div className="historical-peer-host" key={f.kind}>
                <b>{f.kind}</b><span>{f.exactlySame?"SHA-256 egyezés":"Eltérő bájtok"}</span>
              </div>)}
            </div>
          </div>}
          {parityError&&<div role="alert" className="error-inline"><AlertTriangle size={16}/>{parityError}</div>}
        </div>
      </section>
      <form className="historical-import-form" onSubmit={e=>{void inspectArchive(e);}}>
        <label>Korábban mentett Manager ZIP (helyi, olvasási célú másolat)
          <input aria-label="Korábbi Manager archívum" placeholder="D:\Mentések\Korábbi-Manager.zip"
            value={archive} maxLength={520} disabled={registering} onChange={e=>{setArchive(e.target.value);setKnown(null);setMapId("");invalidateImport();invalidateParity();setLaunchCommand("");}} required/>
        </label>
        <button type="submit" className="secondary" disabled={busy||registering||!archive.trim()}>
          <Shield size={16}/> Archívum ellenőrzése
        </button>
      </form>
      {known && <form className="historical-import-form" onSubmit={e=>{void previewImport(e);}}>
        <label>Archivált Store-v1 map
          <select aria-label="Archivált Store-v1 map" value={mapId} disabled={registering} onChange={e=>{setMapId(e.target.value);invalidateImport();}}>
            {known.maps.map(m=><option key={m.mapId} value={m.mapId}>
              {m.mapKey} · Game {m.gamePort} / Query {m.queryPort} · {m.enabledMods} mod
            </option>)}
          </select>
        </label>
        <label>Eredeti, változatlan helyi ASA telepítési mappa
          <input aria-label="Korábbi ASA map mappája" placeholder="C:\ASA\ExistingMap"
            value={root} maxLength={520} disabled={registering} onChange={e=>{setRoot(e.target.value);invalidateImport();}} required/>
        </label>
        <button className="secondary" type="submit" disabled={busy||registering||!mapId||!root.trim()}>
          <Shield size={16}/> Map és Store-adatok előellenőrzése
        </button>
      </form>}
      {preview && <div className="historical-preview">
        <div className="historical-preview-heading"><CheckCircle2 size={20}/>
          <strong>{preview.name} · {preview.mapKey}</strong></div>
        <div className="historical-proof-grid">
          <span>Eredeti mapazonosító</span><strong>Megőrzendő</strong>
          <span>Store-v1 állapot a másolatban</span><strong>Active</strong>
          <span>Game / Query UDP</span><strong>{preview.gamePort} / {preview.queryPort}</strong>
          <span>Aktív / passzív mod</span><strong>{preview.enabledMods} / {preview.passiveMods}</strong>
          <span>Telepítési mappa</span><strong>{preview.folderName}</strong>
          <span>Teljes launch argumentumok</span><strong>Nem igazolt</strong>
          <span>Kétgépes élő Store-írójog</span><strong>Nem igazolt</strong>
          <span>Szerver indítása</span><strong>TILTOTT</strong>
        </div>
        <label className="historical-approval">
          <input type="checkbox" checked={approved} onChange={e=>setApproved(e.target.checked)}/>
          Jóváhagyom a kizárólag Manager-profilba történő, zárolt regisztrációt.
          Az eredeti Store- és ASA-állományok nem változhatnak.
        </label>
        <button className="primary" disabled={busy||registering||!approved}
          onClick={()=>{void register();}}><LockKeyhole size={16}/> Zárolt Store-v1 map regisztrálása</button>
      </div>}
      {known && <section className="historical-peer-check" aria-label="Eredeti indítási sor ellenőrzése">
        <h3>Eredeti ASA indítási sor · csak olvasási összevetés</h3>
        <p className="muted small">Kizárólag a korábbi működő Managerből hitelesen kinyert teljes indítási sort add meg. Nem tároljuk el, nem jelenítjük meg az eredményben, és az ellenőrzés nem engedélyezi a szerverindítást. Jelszó is előfordulhat benne, ezért ne osszd meg nyilvánosan.</p>
        <form className="historical-import-form" onSubmit={e=>{void checkLaunch(e);}}>
          <label>Helyben ellenőrizendő eredeti indítási sor
            <textarea aria-label="Eredeti ASA indítási sor" rows={3} maxLength={3072}
              value={launchCommand} disabled={registering} autoComplete="off" spellCheck={false}
              onChange={e=>{setLaunchCommand(e.target.value);invalidateLaunch();}}
              placeholder="ArkAscendedServer.exe MAP?SessionName=... -port=... -mods=... -usestore"/>
          </label>
          <button type="submit" className="secondary"
            disabled={registering||launchBusy||!launchCommand.trim()}>
            <Shield size={16}/> Indítási paraméterek összevetése
          </button>
        </form>
        {launchEvidence && <div className="historical-proof-grid" role="status">
          <span>Map neve</span><strong>{launchEvidence.mapStatus}</strong>
          <span>Game UDP</span><strong>{launchEvidence.gamePortStatus}</strong>
          <span>Query UDP</span><strong>{launchEvidence.queryPortStatus}</strong>
          <span>Aktív modok sorrendje</span><strong>{launchEvidence.modOrderStatus}</strong>
          <span>Passzív modok sorrendje</span><strong>{launchEvidence.passiveModOrderStatus}</strong>
          <span>Eredeti belső clusterazonosító / könyvtár</span><strong>{launchEvidence.clusterIdStatus} / {launchEvidence.clusterPathPresent?"Megadva, nem igazolt":"Nem igazolt"}</strong>
          <span>-usestore kapcsoló</span><strong>{launchEvidence.useStorePresent?
            (launchEvidence.storeFlagCount===1?"Pontosan egyszer":"VESZÉLY: többször szerepel"):"Nem igazolt"}</strong>
          <span>Eredeti forrás szerinti indítási kapcsolók</span>
          <strong>{launchEvidence.canonicalSourceFlagsObserved?"Szintaxis egyezik – eredet nem igazolt":
            "Indítási kapcsolók nem teljesen egyeznek a forrásmintával"}</strong>
          <span>-converttostore</span><strong>{launchEvidence.conversionFlagPresent?"VESZÉLY: szerepel":"Nem található"}</strong>
          <span>Éles indítás</span><strong>TILTOTT</strong>
          <span>Hiányosságok</span><strong>{launchEvidence.findings.join(", ")||"Nem észlelt szöveges eltérés – eredet továbbra sem igazolt"}</strong>
        </div>}
        {launchError && <div role="alert" className="error-inline"><AlertTriangle size={16}/>{launchError}</div>}
      </section>}
      <section className="historical-peer-check" aria-label="Store-v1 peer bizonyítékok">
        <h3>Kétgépes Store-v1 · történeti másolatok összevetése</h3>
        <p className="muted small">A közös koordinációs ZIP és a másik host négy helyi Store-metaadatfájljának másolata. Csak olvasás; a másolat egyezősége nem igazol élő írótulajdont vagy karakterutazást.</p>
        <form className="historical-import-form" onSubmit={e=>{void comparePeers(e);}}>
          <label>Közös Store-koordinációs archívum
            <input aria-label="Store koordináció ZIP" value={coordZip} maxLength={520}
              onChange={e=>{setCoordZip(e.target.value);invalidatePair();}}
              placeholder="D:\\Backups\\Store-coordination.zip" required/>
          </label>
          <label>Másik host négy helyi metaadatának ZIP-másolata
            <input aria-label="Másik Store host ZIP" value={localZip} maxLength={520}
              onChange={e=>{setLocalZip(e.target.value);invalidatePair();}}
              placeholder="D:\\Backups\\Store-peer-local.zip" required/>
          </label>
          <button className="secondary" type="submit" disabled={pairBusy || !coordZip.trim() || !localZip.trim()}>
            <Shield size={16}/> {pairBusy?"Ellenőrzés...":"Két host Store-adatainak összevetése"}
          </button>
        </form>
        {pairResult && <div className="historical-peer-result" role="status">
          <div className="historical-preview-heading"><Shield size={18}/>
            <strong>Csak offline bizonyíték · éles Store-írás TILOS</strong></div>
          <div className="historical-proof-grid">
            <span>Map / peer</span><strong>{pairResult.maps} / {pairResult.peers}</strong>
            <span>Két teljes helyi hostmásolat</span><strong>{pairResult.completeTwoHostEvidence?"Igen":"NEM IGAZOLT"}</strong>
            <span>Azonos Store-session</span><strong>{pairResult.sessionConsistent?"Egyező":"Eltérés vagy hiány"}</strong>
            <span>Konfigurációhash-ek</span><strong>{pairResult.configHashesVerified?"Egyező":"Eltérés vagy hiány"}</strong>
            <span>Aktív Store rekordok</span><strong>{pairResult.activeStoreRecords?"Másolatban igazolt":"NEM IGAZOLT"}</strong>
            <span>Eredeti kétgépes állapotgép</span><strong>{pairResult.historicalActiveProtocolShape?
              "Történeti másolat szerint DONE/ACTIVE – élő állapot ismeretlen":
              "Nem teljesült a DONE/ACTIVE feltétel"}</strong>
            <span>Éles írási jogosultság</span><strong>TILTOTT</strong>
          </div>
          {pairResult.findings.length>0 && <p className="muted small">Ellenőrzési kódok: {pairResult.findings.join(", ")}</p>}
          <div className="historical-peer-hosts">
            {pairResult.hosts.map(h=><div key={h.number} className="historical-peer-host">
              <b>Peer {h.number}</b>
              <span>{h.maps} map · helyi másolat: {h.localEvidence?"igen":"hiányzik"}</span>
              <span>Lock / hash / aktív: {h.lockMatches?"OK":"?"} / {h.configHashMatches?"OK":"?"} / {h.activeMaps?"OK":"?"}</span>
            </div>)}
          </div>
        </div>}
        {pairError && <div className="error-inline" role="alert"><AlertTriangle size={16}/>{pairError}</div>}
      </section>
      {error && <div className="error-inline" role="alert"><AlertTriangle size={16}/>{error}</div>}
      {notice && <div className="success-inline" role="status"><CheckCircle2 size={16}/>{notice}</div>}
      <div className="warning-inline"><Shield size={17}/>
        A történeti ZIP nem igazolja az éles karakterutazást vagy a teljes, eredeti indítási konfigurációt.
        Az import nem helyettesíti a kétgépes Store-v1 és ASA végtesztet.
        Nem futtatunk -converttostore műveletet.</div>
    </div>
  </section>;
}
