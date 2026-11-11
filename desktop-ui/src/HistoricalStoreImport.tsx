import { useEffect, useRef, useState, type FormEvent } from "react";
import { AlertTriangle, Archive, CheckCircle2, Shield, LockKeyhole } from "lucide-react";
import { managerBridge, type HistoricalStoreList, type HistoricalStorePreview, type HistoricalPairEvidence, type HistoricalLocalStoreStatus, type HistoricalLocalStoreParity, type HistoricalLaunchEvidence, type HistoricalLiveGuard,
  type OriginalKovaLocalMapList, type OriginalKovaImportPreview,
  type OriginalKovaBatchPreview, type OriginalKovaLiveBatchPreview,
  type OriginalKovaClusterPreview } from "./bridge";

export function HistoricalStoreImport({onImported}: {onImported:()=>Promise<void>}) {
  const [archive,setArchive]=useState("");
  const [directList,setDirectList]=useState<OriginalKovaLocalMapList|null>(null);
  const [directMapId,setDirectMapId]=useState("");
  const [directGamePort,setDirectGamePort]=useState("");
  const [directQueryPort,setDirectQueryPort]=useState("");
  const [directPreview,setDirectPreview]=useState<OriginalKovaImportPreview|null>(null);
  const [batchPreview,setBatchPreview]=useState<OriginalKovaBatchPreview|null>(null);
  const [batchApproved,setBatchApproved]=useState(false);
  const [liveBatchPreview,setLiveBatchPreview]=useState<OriginalKovaLiveBatchPreview|null>(null);
  const [liveControllerStopped,setLiveControllerStopped]=useState(false);
  const [liveTypedApproval,setLiveTypedApproval]=useState("");
  const [liveClockMs,setLiveClockMs]=useState(()=>Date.now());
  const liveApprovalPhrase="STORE-V1 FUTÓ ÁTVÉTEL";
  // The approval is native, single use and expires after three minutes.
  // A stale preview must never remain actionable just because the phrase matches.
  useEffect(()=>{
    if(!liveBatchPreview)return;
    const ticker=window.setInterval(()=>setLiveClockMs(Date.now()),1000);
    return ()=>window.clearInterval(ticker);
  },[liveBatchPreview]);
  const [clusterPreview,setClusterPreview]=useState<OriginalKovaClusterPreview|null>(null);
  const [clusterApproval,setClusterApproval]=useState("");
  const [clusterBusy,setClusterBusy]=useState(false);
  const [clusterError,setClusterError]=useState("");
  const [clusterNotice,setClusterNotice]=useState("");
  const [directApproved,setDirectApproved]=useState(false);
  const [directBusy,setDirectBusy]=useState(false);
  const [directError,setDirectError]=useState("");
  const [directNotice,setDirectNotice]=useState("");
  const directEpoch=useRef(0);
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
  const [liveGuard,setLiveGuard]=useState<HistoricalLiveGuard|null>(null);
  const [liveGuardBusy,setLiveGuardBusy]=useState(false);
  const [liveGuardError,setLiveGuardError]=useState("");
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
      setNotice(receipt.mapName+" felvéve az eredeti mapazonosítóval. A map saját vezérlőiben RCON-átvétel és natív Start-előellenőrzés után indítható.");
      try { await onImported(); }
      catch { setError("A zárolt regisztráció megtörtént, de a szerverlista frissítése sikertelen. A nézet újranyitásával ellenőrizhető."); }
    }catch(e){
      if(epoch!==importEpoch.current)return;
      setError("A Store-v1 regisztráció meghiúsult ("+(e instanceof Error?e.message:"UM-HISTORICAL-VERIFICATION-FAILED")+"). Az eredeti világ- és karakterfájlokhoz nem nyúltunk.");
    }finally{setRegistering(false);if(epoch===importEpoch.current)setBusy(false);}
  };

  const loadOriginalMaps=async()=>{
    if(directBusy||registering)return;
    const epoch=++directEpoch.current;
    setDirectBusy(true);setDirectError("");setDirectNotice("");
    setDirectList(null);setDirectPreview(null);setDirectApproved(false);
    setBatchPreview(null);setBatchApproved(false);
    setLiveBatchPreview(null);setLiveControllerStopped(false);setLiveTypedApproval("");
    try{
      const result=await managerBridge.historicalOriginalList();
      if(epoch!==directEpoch.current)return;
      setDirectList(result);
      const first=result.maps.find(m=>!m.registered);
      setDirectMapId(first?.mapId??"");
      setDirectGamePort(first?String(first.gamePort):"");
      setDirectQueryPort(first?String(first.queryPort):"");
    }catch(e){
      if(epoch===directEpoch.current)setDirectError(
        "Az eredeti Kova helyi ACTIVE/DONE profil nem ellenőrizhető ("+
        (e instanceof Error?e.message:"hibás vagy hiányzó helyi Store-metaadat")+").");
    }finally{if(epoch===directEpoch.current)setDirectBusy(false);}
  };
  const previewOriginalMap=async(e:FormEvent)=>{
    e.preventDefault();
    const game=Number(directGamePort),query=Number(directQueryPort);
    if(directBusy||registering||!directMapId||
       !Number.isInteger(game)||game<1024||game>65535||
       !Number.isInteger(query)||query<1024||query>65535||game===query){
      setDirectError("Adj meg két különböző, eredeti UDP Game/Query portot (1024–65535).");
      return;
    }
    const epoch=++directEpoch.current;
    setDirectBusy(true);setDirectError("");setDirectNotice("");
    setDirectPreview(null);setDirectApproved(false);
    setBatchPreview(null);setBatchApproved(false);
    try{
      const result=await managerBridge.historicalOriginalPreview(directMapId,game,query);
      if(epoch===directEpoch.current)setDirectPreview(result);
    }catch(e){
      if(epoch===directEpoch.current)setDirectError(
        "Az eredeti map, ASA EXE, telepítési mappa vagy a leállított folyamat nem igazolható ("+
        (e instanceof Error?e.message:"natív előellenőrzés")+").");
    }finally{if(epoch===directEpoch.current)setDirectBusy(false);}
  };
  const importOriginalMap=async()=>{
    if(directBusy||registering||!directApproved||!directPreview)return;
    const epoch=++directEpoch.current;
    const token=directPreview.token;
    setDirectBusy(true);setDirectError("");setDirectNotice("");
    setDirectPreview(null);setDirectApproved(false);
    try{
      const result=await managerBridge.historicalOriginalRegister(token);
      if(epoch!==directEpoch.current)return;
      setDirectNotice(result.mapName+" felvéve az eredeti GUID-dal, Game/Query portokkal "+
        "és mod-sorrenddel. A leállított import kezdetben közvetlen ASA módot jelöl; "+
        "csatlakoztasd a meglévő RCON-t, majd ASA API-s map esetén válaszd ki az "+
        "'ASA API Loader mód választása' opciót a map indítási paneljén, "+
        "mielőtt bármilyen Startot jóváhagynál.");
      setDirectList(prev=>prev?{...prev,maps:prev.maps.map(m=>
        m.mapId===result.mapId?{...m,registered:true}:m)}:prev);
      setDirectMapId("");
      try{await onImported();}
      catch{setDirectError("A map regisztrálva van, de a szerverlista frissítése sikertelen.");}
    }catch(e){
      if(epoch===directEpoch.current)setDirectError(
        "A közvetlen Kova-import nem fejeződött be ("+
        (e instanceof Error?e.message:"natív ellenőrzés")+"). Az eredeti Kova fájlok változatlanok.");
    }finally{if(epoch===directEpoch.current)setDirectBusy(false);}
  };

  const previewBatch=async()=>{
    if(directBusy||registering||!directList||
       !directList.maps.some(m=>!m.registered))return;
    const epoch=++directEpoch.current;
    setDirectBusy(true);setDirectError("");setDirectNotice("");
    setDirectPreview(null);setDirectApproved(false);
    setBatchPreview(null);setBatchApproved(false);
    try{
      const result=await managerBridge.historicalOriginalBatchPreview();
      if(epoch===directEpoch.current)setBatchPreview(result);
    }catch(e){
      if(epoch===directEpoch.current)setDirectError(
        "A teljes helyi Store-import előellenőrzése nem sikerült ("+
        (e instanceof Error?e.message:"forrás vagy folyamatállapot")+").");
    }finally{if(epoch===directEpoch.current)setDirectBusy(false);}
  };
  const confirmBatch=async()=>{
    if(directBusy||registering||!batchApproved||!batchPreview)return;
    const token=batchPreview.token,epoch=++directEpoch.current;
    setDirectBusy(true);setDirectError("");setDirectNotice("");
    setBatchPreview(null);setBatchApproved(false);
    try{
      const receipt=await managerBridge.historicalOriginalBatchRegister(token);
      if(epoch!==directEpoch.current)return;
      setDirectNotice(receipt.importedMaps+
        " eredeti map atomikusan felvéve. A korábbi Kova-adatok érintetlenek. "+
        "A leállított import után még nincs ASA API mód igazolva: "+
        "csatlakoztasd a meglévő RCON-t, és ASA API-s maponként válaszd ki "+
        "az 'ASA API Loader mód választása' opciót az első Start előtt. "+
        "Az import önmagában nem indított szervert.");
      setDirectList(prev=>prev?{...prev,
        maps:prev.maps.map(m=>receipt.mapIds.includes(m.mapId)?
          {...m,registered:true}:m)}:prev);
      setDirectMapId("");
      try{await onImported();}
      catch{setDirectError("A mapok regisztrálva vannak, de a nézet frissítése sikertelen.");}
    }catch(e){
      if(epoch===directEpoch.current)setDirectError(
        "A kötegelt import nem került teljesen rögzítésre ("+
        (e instanceof Error?e.message:"natív hiba")+"). Ellenőrizd a mapregisztert.");
    }finally{if(epoch===directEpoch.current)setDirectBusy(false);}
  };

  const previewLiveFive=async()=>{
    if(directBusy||registering||!directList||
       directList.maps.length!==5)return;
    const epoch=++directEpoch.current;
    setDirectBusy(true);setDirectError("");setDirectNotice("");
    setBatchPreview(null);setBatchApproved(false);setLiveBatchPreview(null);
    setLiveControllerStopped(false);setLiveTypedApproval("");
    try{
      const proof=await managerBridge.historicalOriginalLiveBatchPreview();
      if(epoch===directEpoch.current){
        setLiveClockMs(Date.now());setLiveBatchPreview(proof);
      }
    }catch(e){
      if(epoch===directEpoch.current){
        const code=e instanceof Error?e.message:"UM-ORIGINAL-LIVE-UNVERIFIED";
        setDirectError(code==="UM-METHOD-DENIED"?
          "A telepített Obsidian natív WebView2-dispatchere elutasította a "+
          "historical.originalLiveBatchPreview hívást (UM-METHOD-DENIED). "+
          "Ez alkalmazásverzió-/útvonalhiba, NEM Kova ACTIVE/DONE-, PID-, "+
          "mod- vagy porthiba. A futó szervereket ne állítsd le; "+
          "telepíts azonos kiadásból származó React + Windows host frissítést.":
          "A futó eredeti Store-v1 szerverek natív ACTIVE/DONE, WMI/PID, "+
          "kétpeer session vagy mod/port ellenőrzése sikertelen ("+
          code+"). Nem állítottunk le és nem indítottunk ASA-folyamatot.");
      }
    }finally{if(epoch===directEpoch.current)setDirectBusy(false);}
  };
  const applyLiveFive=async()=>{
    if(directBusy||registering||!liveBatchPreview||
       liveApprovalDisabledReason!==null)return;
    // Recheck the native deadline at the actual click: background WebView2
    // timers may be throttled, so a displayed countdown is not an authority.
    const expiry=Date.parse(liveBatchPreview.expiresUtc);
    if(!Number.isFinite(expiry)||Date.now()>=expiry){
      setLiveClockMs(Date.now());return;
    }
    const token=liveBatchPreview.token,epoch=++directEpoch.current;
    setDirectBusy(true);setDirectError("");setDirectNotice("");
    setLiveBatchPreview(null);setLiveTypedApproval("");setLiveControllerStopped(false);
    try{
      const receipt=await managerBridge.historicalOriginalLiveBatchApply(
        token,true,"STORE-V1 FUTÓ ÁTVÉTEL");
      if(epoch!==directEpoch.current)return;
      setDirectNotice(receipt.addedMaps+" hiányzó Kova-map hozzáadva, "+
        receipt.reconnectedMaps+" már ismert futó szerver újracsatlakoztatva "+
        "az Obsidian helyi regiszterébe a régi GUID-dal és folyamat-PID-vel. "+
        "Nem indult, nem állt le ASA, és semmilyen Store-writer, session "+
        "vagy karakterfájl nem módosult. A meglévő RCON csatlakoztatása külön "+
        "natív ellenőrzés után végezhető el.");
      setDirectList(prev=>prev?{...prev,maps:prev.maps.map(m=>
        receipt.mapIds.includes(m.mapId)?{...m,registered:true}:m)}:prev);
      setDirectMapId("");
      try{await onImported();}
      catch{setDirectError("A Store-profil átvétele megtörtént, "+
        "de a frissített maplista nem tölthető be. Ne ismételd automatikusan.");}
    }catch(e){
      if(epoch===directEpoch.current){
        const code=e instanceof Error?e.message:"UM-ORIGINAL-LIVE-FAILED";
        setDirectError(code==="UM-METHOD-DENIED"?
          "A telepített natív dispatcher elutasította a "+
          "historical.originalLiveBatchApply hívást (UM-METHOD-DENIED). "+
          "Ez alkalmazáskomponens-/kiadáseltérés, nem Store-migrációs "+
          "bizonyíték. Ellenőrizd a helyi mapregisztert, majd frissíts "+
          "azonos React + Windows host buildre. Ne ismételd vakon az átvételt.":
          ["UM-ORIGINAL-LIVE-APPROVAL-EXPIRED",
           "UM-ORIGINAL-LIVE-APPROVAL-STALE",
           "UM-ORIGINAL-LIVE-APPROVAL-INVALID"].includes(code)?
          "A futó Store-v1 átvétel háromperces jóváhagyása lejárt, "+
          "vagy az előellenőrzés időközben megváltozott ("+code+"). "+
          "Ellenőrizd előbb a helyi mapregisztert: lehet, hogy egy "+
          "korábbi kérés már sikerült. Ha nincs átvéve az öt map, "+
          "kérj új teljes natív felismerést, és csak az új "+
          "bizonyítékok alapján hagyd jóvá. Nem indítunk újra "+
          "automatikusan átvételt.":
          "Az eredeti futó Store-v1 mapok átvétele nem igazolható ("+
          code+"). Ellenőrizd a Manager helyi mapregiszterét, ne futtasd újra vakon.");
      }
    }finally{if(epoch===directEpoch.current)setDirectBusy(false);}
  };

  const previewOriginalCluster=async()=>{
    if(clusterBusy||directBusy||registering)return;
    setClusterPreview(null);setClusterApproval("");setClusterError("");setClusterNotice("");
    setClusterBusy(true);
    try{setClusterPreview(await managerBridge.historicalOriginalClusterPreview());}
    catch(e){setClusterError("Az eredeti öt map forrásazonos, írásmentes Cluster-regiszter előellenőrzése sikertelen ("+
      (e instanceof Error?e.message:"UM-ORIGINAL-CLUSTER-INVALID")+").");}
    finally{setClusterBusy(false);}
  };
  const applyOriginalCluster=async()=>{
    if(clusterBusy||!clusterPreview||clusterApproval!=="EREDETI KOVA REGISZTER")return;
    const token=clusterPreview.token;
    setClusterBusy(true);setClusterError("");setClusterNotice("");
    setClusterPreview(null);setClusterApproval("");
    try{
      const result=await managerBridge.historicalOriginalClusterApply(
        token,"EREDETI KOVA REGISZTER");
      setClusterNotice(result.addedCount+" eredeti map-tagság átvezetve az Obsidian Cluster-regiszterbe. A korábbi Kova-session, writer lock és karakteradatok változatlanok.");
      try{await onImported();}
      catch{setClusterError("A regiszter mentése sikeres, de a nézet frissítése meghiúsult; ne ismételd meg automatikusan.");}
    }catch(e){
      setClusterError("A jóváhagyás elutasítva, lejárt vagy a forrás megváltozott ("+
        (e instanceof Error?e.message:"UM-ORIGINAL-CLUSTER-FAILED")+
        "). Ellenőrizd a natív regiszter állapotát, mielőtt újra próbálnád.");
    }finally{setClusterBusy(false);}
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
  const inspectOriginalLive=async()=>{
    if(!mapId||liveGuardBusy)return;
    setLiveGuardBusy(true);setLiveGuard(null);setLiveGuardError("");
    try{setLiveGuard(await managerBridge.historicalLiveGuard(mapId));}
    catch{setLiveGuardError("A helyi eredeti Store-profil, a két gép közös UNC állapota vagy az ACTIVE/DONE session nem egyezik. A közös zár érintetlen.");}
    finally{setLiveGuardBusy(false);}
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
  // The exact typed phrase is intentionally required for native Store-v1
  // ownership handoff. The UI must explain EVERY disabled action gate.
  const liveExpiryMs=liveBatchPreview?Date.parse(liveBatchPreview.expiresUtc):NaN;
  const liveApprovalExpired=liveBatchPreview!==null&&
    (!Number.isFinite(liveExpiryMs)||liveClockMs>=liveExpiryMs);
  const liveSecondsRemaining=Number.isFinite(liveExpiryMs)?
    Math.max(0,Math.ceil((liveExpiryMs-liveClockMs)/1000)):0;
  const liveApprovalDisabledReason=directBusy||registering?
    "A natív ellenőrzés vagy egy korábbi művelet még folyamatban van.":
    liveApprovalExpired?
      "A háromperces natív előellenőrzés lejárt vagy nem ellenőrizhető. Kérj újat.":
    !liveControllerStopped?
      "Hiányzik a Kova-felügyelet leállításának jelölése.":
      liveTypedApproval!==liveApprovalPhrase?
        "Írd be a pontos jóváhagyó szöveget a látható mezőbe.":
        null;

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
      <span className="mod-tag amber"><LockKeyhole size={13}/> Eredeti Store-adatok megőrzése</span>
    </div>
    <div className="panel-inner">
      <p className="muted small">
        A már működő Store-v1 rendszert nem konvertáljuk újra.
        Közvetlenül felismerheted a régi Kova-profil mapjait, vagy használhatod a korábbi
        Manager hitelesített ZIP-másolatát a portok és modlista ellenőrzésével.
        A regisztráció nem indít szervert; a map saját Start, Stop és mentés funkciói
        külön előellenőrzés és jóváhagyás után használhatók.
      </p>
      <section className="historical-peer-check" aria-label="Közvetlen Kova Store-v1 mapimport">
        <h3>Közvetlen átvétel a korábbi Kova-profilból</h3>
        <p className="muted small">
          Az eredeti Windows-fiók Kova Store-v1 profiljából felismeri az öt helyi mapot,
          a mapazonosítókat és a telepítési mappákat. Nem kell hozzá ZIP vagy kézi mappaútvonal.
          Az eredeti launch-profiles-v1.json fájlból veszi át a Game/Query portokat,
          az aktív és passzív modok pontos sorrendjét. Ha ez a fájl hiányzik,
          a korábbi Manager ZIP alapú importálása használható.
          A leállított mapokra a szokásos kötegelt import használható.
          A leállított map indítási módja a Kova metaadatából önmagában nem
          bizonyítható, ezért import után az eredeti RCON csatlakoztatásával,
          majd maponként az „ASA API Loader mód választása” jóváhagyással
          kell beállítani a Loaderes indítást a legelső Start előtt.
          Már futó mind az öt eredeti Store-v1 maphoz a külön natív,
          ACTIVE/DONE és PID/ASA API-szülő–gyermek ellenőrzéses átvétel
          elérhető, szerverleállítás és újrakonvertálás nélkül.
        </p>
        <button type="button" className="secondary"
          disabled={directBusy||registering} onClick={()=>{void loadOriginalMaps();}}>
          <Archive size={16}/>{directBusy?"Eredeti mapok ellenőrzése...":
            "Öt eredeti Kova-map felismerése"}
        </button>
        {directList&&directList.maps.some(m=>!m.registered)&&
          <div className="settings-actions">
            <button type="button" className="primary"
              disabled={directBusy||registering}
              onClick={()=>{void previewBatch();}}>
              <Archive size={16}/> Összes hiányzó helyi map átvétele egy lépésben
            </button>
          </div>}
        {directList&&directList.maps.length===5&&<div className="settings-actions">
            <button type="button" className="secondary" disabled={directBusy||registering}
              onClick={()=>{void previewLiveFive();}}>
              <Shield size={16}/> Öt már futó eredeti Store-v1 map teljes natív felismerése
            </button>
          </div>}
        {liveBatchPreview&&<div className="historical-preview" role="region"
          aria-label="Öt futó Store-v1 szerver átvételének jóváhagyása">
          <h4>{liveBatchPreview.localMaps} eredeti, futó Kova-map átvétele</h4>
          <p>{liveBatchPreview.mapNames.join(", ")}</p>
          <p className="muted small">
            {liveBatchPreview.directProcesses} közvetlen ASA,
            {liveBatchPreview.apiLoaderPairs} ellenőrzött ASA API Loader-pár.
            {" "}{liveBatchPreview.alreadyRegistered} már regisztrált map újracsatlakoztatása,
            {" "}{liveBatchPreview.newlyDiscovered} még hiányzó map felvétele.
            A natív Windows azonosító, folyamatindítási idő, teljes eredeti
            argumentumok, Kova ACTIVE/DONE session és mindkét peer forrása egyezik.
            Az átvétel nem hoz létre új ASA-folyamatot, nem végez Store-írást.
          </p>
          <p className="muted small">
            A korábbi Kova Manager vezérlését előzőleg meg kell szüntetni,
            de a futó ASA-folyamatokat nem kell leállítani. Két Manager
            egyszerre nem vezérelheti ugyanazokat a mapokat.
          </p>
          <p className="muted small" role="status"
            data-testid="store-live-approval-expiry">
            {liveApprovalExpired?
              "A háromperces natív előellenőrzés lejárt vagy hiányos. Az átvétel tiltva; kérj új felismerést.":
              "A natív jóváhagyás még "+liveSecondsRemaining+
              " másodpercig érvényes. Lejárat után új előellenőrzés szükséges."}
          </p>
          {liveApprovalExpired&&<button type="button" className="secondary"
            disabled={directBusy||registering}
            onClick={()=>{void previewLiveFive();}}>
            Új natív felismerés kérése
          </button>}
          <label className="historical-approval">
            <input type="checkbox" checked={liveControllerStopped}
              disabled={directBusy}
              onChange={e=>setLiveControllerStopped(e.target.checked)}/>
            Megerősítem, hogy a régi Kova Manager már nem felügyeli ezeket
            a folyamatokat. Nem állítottam le az ASA-szervereket.
          </label>
          <label className="historical-typed-confirmation" htmlFor="store-live-approval-text">
            <span>Végső jóváhagyás – írd be pontosan: <strong>{liveApprovalPhrase}</strong></span>
            <input id="store-live-approval-text" type="text"
              aria-label="Store-v1 futó átvétel megerősítése"
              aria-describedby="store-live-approval-reason"
              data-testid="store-live-approval-input"
              value={liveTypedApproval} disabled={directBusy||registering}
              maxLength={64} autoComplete="off" spellCheck={false}
              placeholder={liveApprovalPhrase}
              onChange={e=>setLiveTypedApproval(e.target.value)}/>
          </label>
          <div className="settings-actions historical-live-action-row">
            <button type="button" className="primary"
              disabled={liveApprovalDisabledReason!==null}
              aria-describedby="store-live-approval-reason"
              onClick={()=>{void applyLiveFive();}}>
              <Shield size={16}/> Eredeti öt futó Store-v1 map átvétele
            </button>
            <span id="store-live-approval-reason" role="status"
              className="historical-approval-reason">
              {liveApprovalDisabledReason??
                "Minden jóváhagyási feltétel teljesült. Az átvétel külön kattintással indul."}
            </span>
            <button type="button" className="secondary" disabled={directBusy}
              onClick={()=>{setLiveBatchPreview(null);setLiveTypedApproval("");
                setLiveControllerStopped(false);}}>Mégsem</button>
          </div>
        </div>}
        {batchPreview&&<div className="historical-preview" role="region"
          aria-label="Összes eredeti Kova-map importálásának jóváhagyása">
          <h4>{batchPreview.localMaps} eredeti map egyetlen regisztrációval</h4>
          <p>{batchPreview.mapNames.join(", ")}</p>
          <p className="muted small">A natív Manager valamennyi mapot,
            telepítést, eredeti mod-sorrendet és leállított folyamatot ellenőrizte.
            Egyetlen ConfigStore-mentést végez. Az eredeti ASA, Kova és
            Store-session adatok nem módosulnak, szerver nem indul.
            Az import még nem tudhatja bizonyítottan a leállított ASA eredeti
            indítómódját: API-s mapoknál előbb az eredeti RCON, utána a
            „ASA API Loader mód választása” szükséges, még Start előtt.</p>
          <label className="historical-approval">
            <input type="checkbox" checked={batchApproved}
              disabled={directBusy} onChange={e=>setBatchApproved(e.target.checked)}/>
            Jóváhagyom az összes felsorolt eredeti map együttes,
            GUID-megőrző regisztrációját.
          </label>
          <div className="settings-actions">
            <button type="button" className="primary"
              disabled={!batchApproved||directBusy} onClick={()=>{void confirmBatch();}}>
              <Archive size={16}/> {batchPreview.localMaps} map hozzáadása
            </button>
            <button type="button" className="secondary" disabled={directBusy}
              onClick={()=>{setBatchPreview(null);setBatchApproved(false);}}>Mégsem</button>
          </div>
        </div>}
        {directList&&<form className="historical-import-form"
          onSubmit={e=>{void previewOriginalMap(e);}}>
          <label>Átveendő helyi map
            <select aria-label="Közvetlen Kova-map" value={directMapId}
              disabled={directBusy||registering}
              onChange={e=>{directEpoch.current++;setDirectMapId(e.target.value);
                const selected=directList.maps.find(m=>m.mapId===e.target.value);
                setDirectGamePort(selected?String(selected.gamePort):"");
                setDirectQueryPort(selected?String(selected.queryPort):"");
                setDirectPreview(null);setDirectApproved(false);}}>
              <option value="">Válassz még nem regisztrált mapot</option>
              {directList.maps.map(m=><option key={m.mapId} value={m.mapId}
                disabled={m.registered}>
                {m.name} · {m.folderName} · {m.enabledMods} mod{m.registered?" · már felvéve":""}
              </option>)}
            </select>
          </label>
          <label>Eredeti Game UDP port
            <input aria-label="Közvetlen Game port" type="number" min={1024}
              max={65535} placeholder="Korábbi Game port"
              disabled={directBusy||registering} value={directGamePort}
              onChange={e=>{setDirectGamePort(e.target.value);
                setDirectPreview(null);setDirectApproved(false);}} required/>
          </label>
          <label>Eredeti Query UDP port
            <input aria-label="Közvetlen Query port" type="number" min={1024}
              max={65535} placeholder="Korábbi Query port"
              disabled={directBusy||registering} value={directQueryPort}
              onChange={e=>{setDirectQueryPort(e.target.value);
                setDirectPreview(null);setDirectApproved(false);}} required/>
          </label>
          <button className="secondary" type="submit"
            disabled={!directMapId||directBusy||registering}>
            <Shield size={16}/> Eredeti map regisztrációjának előellenőrzése
          </button>
        </form>}
        {directPreview&&<div className="historical-preview" role="region"
          aria-label="Közvetlen Kova-import jóváhagyása">
          <div className="historical-preview-heading">
            <CheckCircle2 size={18}/><strong>{directPreview.mapName}</strong>
          </div>
          <div className="historical-proof-grid">
            <span>Eredeti map GUID</span><strong>Megmarad</strong>
            <span>ASA mappa</span><strong>{directPreview.folderName}</strong>
            <span>Game / Query UDP</span>
            <strong>{directPreview.gamePort} / {directPreview.queryPort}</strong>
            <span>Eredeti aktív / passzív mod</span>
            <strong>{directPreview.originalEnabledMods} / {directPreview.originalPassiveMods} · sorrend megőrizve</strong>
            <span>RCON</span><strong>Import után csatlakoztatandó</strong>
            <span>Kova világ-, session- és transferadatok</span><strong>Változatlanok</strong>
          </div>
          <label className="historical-approval">
            <input type="checkbox" checked={directApproved}
              onChange={e=>setDirectApproved(e.target.checked)}/>
            Jóváhagyom az eredeti GUID megőrzésével történő helyi Manager-regisztrációt.
            Az eredeti Store-fájlokhoz és a szerverfolyamathoz nem nyúlhat.
          </label>
          <button type="button" className="primary"
            disabled={!directApproved||directBusy||registering}
            onClick={()=>{void importOriginalMap();}}>
            <Archive size={16}/> Eredeti map hozzáadása
          </button>
        </div>}
        {directError&&<div role="alert" className="error-inline">
          <AlertTriangle size={16}/>{directError}</div>}
        {directNotice&&<div role="status" className="success-inline">
          <CheckCircle2 size={16}/>{directNotice}</div>}
        <section className="historical-parity-check" aria-label="Eredeti Kova Cluster-regiszter átvétele">
          <h3>Eredeti 5 map hozzárendelése a Cluster-regiszterhez</h3>
          <p className="muted small">Ha az eredeti mapokat már felvetted, ezzel
            a régi négy Kova-metaadatfájlból átvehető a pontos clusterazonosító
            és az öt eredeti helyi map-tagság az Obsidian saját regiszterébe.
            Az eredeti Store-fájlok, közös transfermappa és a szerverek nem módosulnak.</p>
          <button type="button" className="secondary" disabled={clusterBusy||directBusy}
            onClick={()=>{void previewOriginalCluster();}}>
            <Shield size={16}/> Eredeti Cluster-regiszter előellenőrzése
          </button>
          {clusterPreview&&<div className="historical-preview" role="region"
            aria-label="Eredeti Cluster-regiszter jóváhagyása">
            <div className="historical-proof-grid">
              <span>Forrás cluster</span><strong>{clusterPreview.clusterId}</strong>
              <span>Eredeti map</span><strong>{clusterPreview.fiveOriginalMaps} db</strong>
              <span>Már hozzárendelt</span><strong>{clusterPreview.alreadyAssigned}</strong>
              <span>Hozzáadandó</span><strong>{clusterPreview.missingAssignments}</strong>
              <span>Korábbi Kova és writer lock</span><strong>Változatlan</strong>
            </div>
            <label className="historical-typed-confirmation">Pontos jóváhagyó szöveg
              <input type="text" aria-label="Eredeti Cluster-regiszter jóváhagyó szöveg"
                value={clusterApproval} disabled={clusterBusy} autoComplete="off"
                spellCheck={false}
                onChange={e=>setClusterApproval(e.target.value)}
                placeholder="EREDETI KOVA REGISZTER"/>
            </label>
            <button type="button" className="primary"
              disabled={clusterBusy||clusterApproval!=="EREDETI KOVA REGISZTER"}
              onClick={()=>{void applyOriginalCluster();}}>
              Eredeti map-tagság rögzítése
            </button>
          </div>}
          {clusterError&&<div role="alert" className="error-inline">
            <AlertTriangle size={16}/>{clusterError}</div>}
          {clusterNotice&&<div role="status" className="success-inline">
            <CheckCircle2 size={16}/>{clusterNotice}</div>}
        </section>
      </section>
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
        <div className="historical-parity-check" aria-label="Eredeti élő Store ACTIVE ellenőrzés">
          <h3>Eredeti élő kétgépes ACTIVE / DONE ellenőrzés</h3>
          <p className="muted small">A regisztrált Store-map és az eredeti helyi profil alapján, közvetlenül és kizárólag olvasva ellenőrzi a közös SMB koordinációs mappát. A régi zárolásokat nem hozza létre és nem törli.</p>
          <button type="button" className="secondary" disabled={!mapId||liveGuardBusy||registering}
            onClick={()=>{void inspectOriginalLive();}}>
            <Shield size={16}/>{liveGuardBusy?"Eredeti UNC állapot olvasása...":"Kétgépes eredeti Store-session ellenőrzése"}
          </button>
          {liveGuard&&<div role="status" className="historical-peer-result">
            <div className="historical-proof-grid">
              <span>Eredeti ACTIVE map + session</span><strong>{liveGuard.originalActiveReady?"Egyező":"Nem igazolt"}</strong>
              <span>Két eredeti DONE résztvevő</span><strong>{liveGuard.donePeers} / 2</strong>
              <span>Mindkét gép mapja</span><strong>{liveGuard.activeMaps} db</strong>
              <span>Félbeszakadt átállítási zárak</span><strong>{liveGuard.noMutationMarker?"Nincsenek":"Tiltott"}</strong>
              <span>Eredeti adatfájl módosítása</span><strong>Nem történt</strong>
              <span>Karaktervisszalépés / tényleges szerverindítás</span><strong>Még külön ellenőrizendő</strong>
            </div>
          </div>}
          {liveGuardError&&<p role="alert" className="error-inline"><AlertTriangle size={16}/>{liveGuardError}</p>}
        </div>
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
          <span>Szerver indítása</span><strong>Külön ellenőrzés és jóváhagyás</strong>
        </div>
        <label className="historical-approval">
          <input type="checkbox" checked={approved} onChange={e=>setApproved(e.target.checked)}/>
          Jóváhagyom a kizárólag Manager-profilba történő, zárolt regisztrációt.
          Az eredeti Store- és ASA-állományok nem változhatnak.
        </label>
        <button className="primary" disabled={busy||registering||!approved}
          onClick={()=>{void register();}}><LockKeyhole size={16}/> Védett Store-v1 map regisztrálása</button>
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
