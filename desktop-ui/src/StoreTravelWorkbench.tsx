import {useMemo,useState} from "react";
import {AlertTriangle,CheckCircle2,ClipboardCopy,FileCheck2,
  FileWarning,HardDrive,LockKeyhole,RefreshCw,Server,ShieldCheck,Users} from "lucide-react";
import {managerBridge,type EmbeddedStoreArchiveReport,type EmbeddedStoreCopyReport,type OfflineAsaArchiveDiff,type TravelCapture,type TravelComparison,type TravelRoundTripComparison} from "./bridge";

type Slot="beforeA"|"beforeB"|"afterA"|"afterB";
type ReturnSlot="returnA"|"returnB";
const returnSlots:Array<{key:ReturnSlot;label:string;subtitle:string}>=[
  {key:"returnA",label:"Visszaút után · UK-HOME-01",subtitle:"HP · B → A befejezése"},
  {key:"returnB",label:"Visszaút után · UK-HOME-02",subtitle:"Ryzen · B → A befejezése"}
];
const slots:Array<{key:Slot;label:string;subtitle:string}>= [
  {key:"beforeA",label:"Előtte · UK-HOME-01",subtitle:"HP · indulás előtt"},
  {key:"beforeB",label:"Előtte · UK-HOME-02",subtitle:"Ryzen · indulás előtt"},
  {key:"afterA",label:"Utána · UK-HOME-01",subtitle:"HP · utazás után"},
  {key:"afterB",label:"Utána · UK-HOME-02",subtitle:"Ryzen · utazás után"}
];
type Snapshot={hostId:string;utc:string;maps:Array<{
  mapId:string;name:string;confirmedStopped:boolean;
  storeMode:boolean;worldPresent:boolean;worldBytes:number;
  files:Array<{id:string;kind:string;bytes:number}>
}>};
function parse(raw:string):Snapshot|null{
  if(raw.length>350000||raw.length<15)return null;
  try{
    const value=JSON.parse(raw) as Partial<Snapshot>;
    if(typeof value.hostId!=="string"||typeof value.utc!=="string"||
      !Array.isArray(value.maps)||value.maps.length!==5)return null;
    if(!value.maps.every(m=>typeof m.mapId==="string"&&typeof m.name==="string"&&
      Array.isArray(m.files)))return null;
    return value as Snapshot;
  }catch{return null;}
}
const seed={beforeA:"",beforeB:"",afterA:"",afterB:""};
function EmbeddedStoreCopyInspector(){
  const [data,setData]=useState<EmbeddedStoreCopyReport|null>(null);
  const [archive,setArchive]=useState<EmbeddedStoreArchiveReport|null>(null);
  const [comparison,setComparison]=useState<OfflineAsaArchiveDiff|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  async function inspect(){
    if(busy)return;
    setBusy(true);setData(null);setError("");
    try{setData(await managerBridge.embeddedStoreInspectCopy());}
    catch{setError("A helyi .ark MÁSOLAT nem elemezhető. A forrás nem változott. Ellenőrizd, hogy leállított világ teljes offline másolatát választottad-e.");}
    finally{setBusy(false);}
  }
  async function inspectArchive(){
    if(busy)return;
    setBusy(true);setArchive(null);setError("");
    try{setArchive(await managerBridge.embeddedStoreInspectArchive());}
    catch{setError("Az offline 10-map archívum fájlleltára vagy beágyazott indexe nem ellenőrizhető. Az eredeti fájlok érintetlenek.");}
    finally{setBusy(false);}
  }
  async function compareArchives(){
    if(busy)return;
    setBusy(true);setComparison(null);setError("");
    try{setComparison(await managerBridge.offlineArchiveCompare());}
    catch{setError("A két OFFLINE archívum nem egyeztethető vagy SHA-256 ellenőrzésük sikertelen. Válaszd ki előbb a korábbi, majd az újabb tízmapos mentést.");}
    finally{setBusy(false);}
  }
  return <section className="premium-panel uk-store-copy-panel" aria-label="Beágyazott Store-v1 ellenőrzése">
    <header className="premium-panel-heading"><h2><HardDrive size={19}/> Beágyazott karakter- és tribe-leltár</h2></header>
    <div className="uk-travel-inner">
      <p className="muted small">Válassz ki egy <b>külön elkészített, helyi, offline .ark másolatot</b>.
        A Windows natív fájlválasztója megnyitja az ASA világmentést csak olvasásra;
        a beágyazott tároló indexéből kizárólag rekordjelölteket számol. Nem
        hoz létre új profilt, nem ír SQL-adatbázist, és nem nyúl a Store sessionhöz.</p>
      <button type="button" className="secondary uk-travel-primary" disabled={busy}
        onClick={()=>{void inspect();}}>
        <FileCheck2 size={16}/>{busy?"Offline Store index elemzése...":"Offline .ark másolat megvizsgálása"}
      </button>
      <button type="button" className="secondary uk-travel-primary" disabled={busy}
        onClick={()=>{void inspectArchive();}}>
        <HardDrive size={16}/>{busy?"Offline ellenőrzés folyamatban...":
          "Teljes 10-map archívum Store-indexeinek vizsgálata"}
      </button>
      <button type="button" className="secondary uk-travel-primary" disabled={busy}
        onClick={()=>{void compareArchives();}}>
        <FileCheck2 size={16}/>{busy?"Offline ellenőrzés folyamatban...":
          "Két teljes 10-map archívum változásainak összevetése"}
      </button>
      <p className="muted small">Először az utazás ELŐTTI, majd az UTÁNI lezárt archívumot
        válaszd ki. A fájlnevek, játékosazonosítók és Store-metaadatok nem
        kerülnek át a felületre.</p>
      {comparison&&<section className="uk-store-copy-result"
          aria-label="Két offline clusterarchívum változásai">
        <strong>Mindkét archívum SHA-256-tal ellenőrizve · {comparison.maps} map</strong>
        <div className="uk-store-copy-metrics">
          <span><strong>{comparison.transferAdded}</strong> hozzáadott transferfájl</span>
          <span><strong>{comparison.transferRemoved}</strong> hiányzó transferfájl</span>
          <span><strong>{comparison.transferModified}</strong> módosult transferfájl</span>
          <span><strong>{comparison.zeroByteTransferFilesPreserved}</strong> változatlan nulla bájtos fájl</span>
        </div>
        <p>Konfigurációk: +{comparison.configurationAdded} / −{comparison.configurationRemoved}
          / módosult {comparison.configurationModified}.
          {comparison.configurationCoverageChanged?" A két mentés konfigurációs lefedése eltér.":""}
        </p>
        <div className="uk-store-archive-scroller">
          <table className="uk-store-archive-table">
            <thead><tr><th>Gép</th><th>Map</th><th>Világmentés változott?</th>
              <th>Új fájl</th><th>Hiányzó fájl</th><th>Módosult</th></tr></thead>
            <tbody>{comparison.results.map(m=><tr key={m.mapId}>
              <td>{m.host}</td><td>{m.name}</td>
              <td>{m.worldChanged?"Igen":"Nem"}</td>
              <td>{m.filesAdded}</td><td>{m.filesRemoved}</td><td>{m.filesModified}</td>
            </tr>)}</tbody>
          </table>
        </div>
        <p><strong>Ez fájlkülönbség-leltár, nem sikeres karakterutazás.</strong>
          Nem igazol játékbeli belépést, inventoryt, tribe-tagságot, eredeti
          Store-v1 írójogot vagy konzisztens élő cluster-pillanatképet.
          Az éles visszaállítás továbbra is tiltott.</p>
      </section>}
      {archive&&<section className="uk-store-copy-result"
        aria-label="Tízmapos beágyazott Store-leltár">
        <strong>{archive.maps} map ellenőrizve: {archive.indexScannedMaps} olvasható index,
          {archive.needsReviewMaps} további vizsgálatot igényel</strong>
        <p>A világ-, konfigurációs és transfer-állományok SHA-256 fájlintegritása:
          {archive.archiveFilesSha256Verified?" ellenőrizve":" nem igazolt"}.
          Nem igazol karakterbelépést, tribe-tulajdont vagy tényleges clusterutazást.</p>
        <div className="uk-store-archive-scroller">
          <table className="uk-store-archive-table">
            <thead><tr><th>Gép</th><th>Mapcsomag</th><th>Index státusza</th>
              <th>Karakterjelölt</th><th>Tribe-jelölt</th></tr></thead>
            <tbody>{archive.results.map(m=><tr key={m.mapId}>
              <td>{m.host}</td><td>{m.package}</td><td>{m.status}</td>
              <td>{m.profileCandidates}</td><td>{m.tribeCandidates}</td>
            </tr>)}</tbody>
          </table>
        </div>
      </section>}
      {data&&<div role="status" className="uk-store-copy-result">
        <b>{data.fileName} · {data.state}</b>
        <div className="uk-store-copy-metrics">
          <span><strong>{data.profileCandidates}</strong> karakterindex-bejegyzés</span>
          <span><strong>{data.tribeCandidates}</strong> tribe-index-bejegyzés</span>
          <span><strong>{data.unclassifiedCandidates}</strong> nem besorolt bejegyzés</span>
          <span><strong>{data.supersededEntries}</strong> felülírt rekord</span>
        </div>
        <p>Tárolóformátum: {data.format} · indexhatárok: {data.indexBoundsVerified?"ellenőrizve":"nem ellenőrizhető"}
          · eredeti másolat változatlan: {data.sourceUnchanged?"igen":"nem igazolt"}.</p>
        <p>Az indexben talált rekordok nem igazolják a karakter beléphetőségét,
          a tribe tulajdonjogát, az inventoryt vagy az A→B→A utazást.
          A játékbeli igazolás és a Store-v1 írásvédelem változatlan követelmény.</p>
      </div>}
      {error&&<div role="alert" className="uk-travel-warning"><AlertTriangle size={17}/>{error}</div>}
    </div>
  </section>;
}

export function StoreTravelWorkbench(){
  const [local,setLocal]=useState<TravelCapture|null>(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [copyMessage,setCopyMessage]=useState("");
  const [evidence,setEvidence]=useState<Record<Slot,string>>(seed);
  const [source,setSource]=useState("");
  const [target,setTarget]=useState("");
  const [profile,setProfile]=useState("");
  const [result,setResult]=useState<TravelComparison|null>(null);
  const [returnEvidence,setReturnEvidence]=useState<Record<ReturnSlot,string>>({
    returnA:"",returnB:""
  });
  const [roundTripResult,setRoundTripResult]=useState<TravelRoundTripComparison|null>(null);
  const [comparing,setComparing]=useState(false);
  const beforeA=useMemo(()=>parse(evidence.beforeA),[evidence.beforeA]);
  const beforeB=useMemo(()=>parse(evidence.beforeB),[evidence.beforeB]);
  const candidates=useMemo(()=>[...(beforeA?.maps??[]),...(beforeB?.maps??[])],[beforeA,beforeB]);
  const sourceEmbedded=candidates.some(x=>x.mapId===source&&x.storeMode);
  const survivorIds=useMemo(()=>{
    const original=candidates.find(x=>x.mapId===source);
    return (original?.files??[]).filter(f=>f.kind==="profile"&&f.bytes>0);
  },[source,candidates]);
  const capture=async()=>{
    if(busy)return;
    setBusy(true);setError("");setLocal(null);
    try{setLocal(await managerBridge.travelCapture());}
    catch{setError("A helyi ötmapos, csak olvasási mentésleltár nem készíthető el. A szerverfájlok változatlanok.");}
    finally{setBusy(false);}
  };
  const copy=async()=>{
    if(!local)return;
    try{
      await navigator.clipboard.writeText(local.snapshot);
      setCopyMessage("A helyi pillanatkép a vágólapra került. A másik géphez csak saját, biztonságos csatornán vidd át.");
    }catch{
      setCopyMessage("Automatikus másolás nem engedélyezett. Jelöld ki és másold ki kézzel az alábbi szöveget.");
    }
  };
  const compare=async()=>{
    if(comparing)return;
    setComparing(true);setResult(null);setError("");
    try{setResult(await managerBridge.travelCompare({
      ...evidence,sourceMap:source,destinationMap:target,
      profileId:sourceEmbedded?"embedded":profile
    }));}
    catch{setError("A négy pillanatkép, a gazdagépek vagy a karakterazonosító nem egyeztethető. Nincs éles változtatás.");}
    finally{setComparing(false);}
  };
  const ready=slots.every(slot=>parse(evidence[slot.key])!==null)&&
    Boolean(source&&target&&source!==target&&
      (sourceEmbedded?profile==="embedded":profile));
  const roundTripReady=ready&&returnSlots.every(
    slot=>parse(returnEvidence[slot.key])!==null);
  const compareRoundTrip=async()=>{
    if(comparing||!roundTripReady)return;
    setComparing(true);setRoundTripResult(null);setError("");
    try{
      setRoundTripResult(await managerBridge.travelCompareRoundTrip({
        beforeA:evidence.beforeA,beforeB:evidence.beforeB,
        middleA:evidence.afterA,middleB:evidence.afterB,
        afterA:returnEvidence.returnA,afterB:returnEvidence.returnB,
        sourceMap:source,destinationMap:target,
        profileId:sourceEmbedded?"embedded":profile
      }));
    }catch{
      setError("A hat pillanatkép időrendje, hostjai vagy SHA-256 leltára nem igazolható. Nincs éles művelet.");
    }finally{setComparing(false);}
  };
  return <div className="uk-travel-page">
    <header className="page-intro">
      <span className="eyebrow">UNTAMED KINGDOM / 5 + 5 MAP / STORE-V1</span>
      <h1>Karakterutazás tesztközpont</h1>
      <p>HP és Ryzen mentésfájlok ellenőrzése az utazás előtt és után.
        A modul nem ad írási jogosultságot az eredeti Store-v1-hez, nem
        szerkeszt .arkprofile vagy .arktribe fájlt és nem módosít MariaDB-t.</p>
    </header>
    <section className="uk-travel-hero">
      <div className="uk-travel-hero-icon"><ShieldCheck size={33}/></div>
      <div><span>VÉDETT · CSAK OLVASÁS</span>
        <h2>Az eredeti karakterfájlok megőrzése az első.</h2>
        <p>A legszigorúbb összehasonlítás nyugalomban lévő (igazoltan leállított)
          mapoknál érvényes. Készíts 5+5 mentéslenyomatot indulás előtt,
          teszteld az utazást, majd 5+5-öt utána. Ez nem kapcsolja ki a Store-zárat.</p>
      </div>
    </section>
    <section className="uk-travel-reference" aria-label="Referenciával igazolt, kötelező működés">
      <div className="uk-travel-reference-header">
        <ShieldCheck size={19}/>
        <strong>REFERENCIÁVAL IGAZOLT – NÁLUNK IS KÖTELEZŐEN MŰKÖDNIE KELL</strong>
      </div>
      <p>Nyilvánosan dokumentált ASA Manager-tesztekben játékbeli próbával igazolták a két map közötti
        karakter-, tárgy- és dinóátvitelt, újraindítással és oda-vissza utazással.
        Ez nálunk is kötelező képesség, kiegészítve a két külön gépes, 5+5 mapos
        Store-v1 kompatibilitással, a tribe-adatok és az eredeti fájlok sértetlenségével.</p>
      <div className="uk-travel-reference-status">
        <span><CheckCircle2 size={14}/> Kötelező megvalósítási cél</span>
        <span><LockKeyhole size={14}/> Saját éles Store-v1 igazolás: még nincs</span>
      </div>
      <small>Referenciabizonyíték: külső ASA Manager teszt, két map egy Windows gépen.
        Nem a mi kétgépes clusterünk tanúsítása. A Writer/Store védelem továbbra is aktív.</small>
    </section>
    <EmbeddedStoreCopyInspector/>
    <div className="uk-travel-grid">
      <section className="premium-panel uk-travel-capture">
        <header className="premium-panel-heading"><h2><HardDrive size={19}/> 1. Helyi mentéslenyomat</h2></header>
        <div className="uk-travel-inner">
          <p className="muted small">A Manager csak az öt helyileg regisztrált map
            SavedArks fájljait olvassa. Fájlnév helyett nem visszafejthető lenyomat,
            bájtméret és SHA-256 szerepel az exportban.</p>
          <button type="button" className="primary uk-travel-primary"
            onClick={()=>{void capture();}} disabled={busy}>
            <RefreshCw size={17}/>{busy?"Fájlleltár készül...":"Helyi 5 map ellenőrzése"}
          </button>
          {local&&<>
            <div className={local.complete?"uk-travel-pass":"uk-travel-warning"}>
              {local.complete?<CheckCircle2 size={18}/>:<AlertTriangle size={18}/>}
              {local.complete?"Az öt helyi map fájlleltára nyugalomban készült; ez nem karakterigazolás.":
                "Hiányzó világmentés, futó map vagy instabil fájl: az elfogadás nem teljes."}
            </div>
            <div className="uk-travel-map-results">
              {local.maps.map(item=><div key={item.mapId}>
                <span><Server size={14}/><b>{item.mapName}</b></span>
                <span>{item.storeMode?
                  ("Store-v1 · .ark: "+(item.worldPresent?item.worldBytes+" bájt":"hiányzik")+
                    " · beágyazott karakterek nem olvashatók"):
                  (item.profileCount+" külön profil · "+item.tribeCount+" tribe")}</span>
                {item.confirmedStopped&&item.saveDirectoryFound&&
                (item.storeMode?item.worldPresent&&!item.worldUnstable:!item.zeroByteDetected)&&
                !item.unstable&&!item.ambiguousLayout?
                  <CheckCircle2 size={16} className="uk-good"/>:
                  <FileWarning size={16} className="uk-bad"/>}
              </div>)}
            </div>
            <button type="button" className="secondary uk-travel-primary"
              onClick={()=>{void copy();}}><ClipboardCopy size={17}/> Lenyomat másolása</button>
            {copyMessage&&<p role="status" className="muted small">{copyMessage}</p>}
            <textarea aria-label="Helyi mentéslenyomat JSON" readOnly rows={3}
              spellCheck={false} value={local.snapshot} onFocus={e=>e.target.select()}/>
          </>}
        </div>
      </section>
      <section className="premium-panel uk-travel-evidence">
        <header className="premium-panel-heading"><h2><Users size={19}/> 2. Négy pillanatkép összevetése</h2></header>
        <div className="uk-travel-inner">
          <p className="muted small">Illeszd be a két gépről az utazás előtt és után
            gyűjtött négy teljes JSON-lenyomatot. A fájlok helyben maradnak.</p>
          <div className="uk-travel-snapshot-grid">
            {slots.map(slot=><label key={slot.key}>
              <strong>{slot.label}</strong><small>{slot.subtitle}</small>
              <textarea rows={2} spellCheck={false} value={evidence[slot.key]}
                aria-label={slot.label+" JSON"}
                onChange={e=>{
                  setEvidence(prev=>({...prev,[slot.key]:e.target.value.slice(0,350000)}));
                  setResult(null);setRoundTripResult(null);
                }}
                placeholder="Illeszd be a JSON-lenyomatot..."/>
              <span className={parse(evidence[slot.key])?"is-validated":""}>
                {parse(evidence[slot.key])?"5 map felismert":"Még nincs érvényes lenyomat"}
              </span>
            </label>)}
          </div>
          <div className="uk-travel-targets">
            <label>Induló map
              <select aria-label="Induló map" value={source}
                onChange={e=>{setSource(e.target.value);setProfile("");setResult(null);}}>
                <option value="">Válassz mapot</option>
                {candidates.map(m=><option key={m.mapId} value={m.mapId}>{m.name}</option>)}
              </select></label>
            <label>Célmap
              <select aria-label="Célmap" value={target} onChange={e=>{setTarget(e.target.value);setResult(null);}}>
                <option value="">Válassz mapot</option>
                {candidates.filter(m=>m.mapId!==source).map(m=><option key={m.mapId}
                  value={m.mapId}>{m.name}</option>)}
              </select></label>
            <label>{sourceEmbedded?"Store-v1 vizsgálati mód":"Vizsgált karakterfájl"}
              <select aria-label="Vizsgált karakterfájl" value={sourceEmbedded?"embedded":profile}
                onChange={e=>{setProfile(e.target.value);setResult(null);}}
                disabled={sourceEmbedded}>
                {sourceEmbedded?<option value="embedded">
                  Beágyazott világmentés · játékbeli igazolás szükséges
                </option>:<>
                  <option value="">Válassz egy profil-lenyomatot</option>
                  {survivorIds.map(x=><option key={x.id} value={x.id}>
                    {x.id.slice(0,12)}… · {x.bytes} bájt</option>)}
                </>}
              </select></label>
          </div>
          <button type="button" className="primary uk-travel-primary"
            disabled={!ready||comparing} onClick={()=>{void compare();}}>
            <FileCheck2 size={17}/>{comparing?"Összevetés...":"Négy mentéslenyomat ellenőrzése"}
          </button>
          {result&&<div role="status" className={result.passed?"uk-travel-pass":"uk-travel-warning"}>
            {result.passed?<CheckCircle2 size={19}/>:<AlertTriangle size={19}/>}
            <div><b>{result.storeModeDetected?
              "Store-v1 világmentések ellenőrizve – a karakterutazás NEM IGAZOLT.":
              result.passed?"A külön profilfájlok elhelyezkedése egyezik.":
              "A karakterfolytonosság nem igazolt."}</b>
              <p>10 map · 2 gazdagép · {result.zeroByteFiles} nulla bájtos fájl.
                Ez nem bizonyítja az ASA játékmeneti utazását vagy az egyetlen Store-írót.</p>
              {result.findings.map((x,i)=><p key={i}>{x}</p>)}
            </div>
          </div>}
          <section className="uk-travel-return-evidence"
            aria-label="Hat pillanatképes oda-vissza utazás">
            <h3>3. A → B → A visszaút ellenőrzése</h3>
            <p className="muted small">A fenti „Utána” pillanatképek az első A → B
              utazás utáni köztes állapotot jelölik. Illeszd be most a B → A
              visszaút UTÁNI két új lenyomatot is. A hat lenyomatot a natív
              ellenőrző időrend, map- és hostazonosság alapján vizsgálja.</p>
            <div className="uk-travel-snapshot-grid">
              {returnSlots.map(slot=><label key={slot.key}>
                <strong>{slot.label}</strong><small>{slot.subtitle}</small>
                <textarea rows={2} spellCheck={false}
                  aria-label={slot.label+" JSON"}
                  value={returnEvidence[slot.key]} onChange={e=>{
                    setReturnEvidence(prev=>({...prev,[slot.key]:
                      e.target.value.slice(0,350000)}));
                    setRoundTripResult(null);
                  }} placeholder="Visszaút utáni JSON-lenyomat..."/>
                <span className={parse(returnEvidence[slot.key])?"is-validated":""}>
                  {parse(returnEvidence[slot.key])?"5 map felismert":
                    "Még nincs érvényes lenyomat"}
                </span>
              </label>)}
            </div>
            <button type="button" className="primary uk-travel-primary"
              disabled={!roundTripReady||comparing}
              onClick={()=>{void compareRoundTrip();}}>
              <FileCheck2 size={17}/>
              {comparing?"Oda-vissza ellenőrzés...":
                "Hat mentéslenyomat (A → B → A) ellenőrzése"}
            </button>
            {roundTripResult&&<div role="status"
              className={roundTripResult.passed?"uk-travel-pass":"uk-travel-warning"}>
              {roundTripResult.passed?<CheckCircle2 size={19}/>:<AlertTriangle size={19}/>}
              <div>
                <b>{roundTripResult.storeModeDetected?
                  "Store-v1 beágyazott karakterutazás NEM IGAZOLT.":
                  roundTripResult.passed?
                    "A külön karakterfájl A → B → A elhelyezkedése és eredeti SHA-256-a egyezik.":
                    "Az oda-vissza fájlfolytonosság további ellenőrzést igényel."}</b>
                <p>Odaút: {roundTripResult.outwardPlacementVerified?"fájlelrendezés egyezik":"nem igazolt"}
                  · Visszaút: {roundTripResult.returnPlacementVerified?"fájlelrendezés egyezik":"nem igazolt"}
                  · Eredeti profil SHA: {roundTripResult.exactOriginalProfileBytes?"egyezik":"nem igazolt"}.</p>
                <p>Érintetlen mapok profil-/tribe-lenyomatai:
                  {roundTripResult.unrelatedLooseFilesUnchanged?" változatlanok":" nem igazoltak"}.
                  Ez nem bizonyítja az inventory, hotbar, implant, tribe-tulajdon,
                  tribute vagy játékbeli belépés helyességét, és nem oldja fel
                  a Store-writer vagy production restore védelmet.</p>
              </div>
            </div>}
          </section>
        </div>
      </section>
    </div>
    {error&&<div role="alert" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
    <div className="uk-travel-lock"><LockKeyhole size={19}/>
      <span>Éles karakterutazás, a Store-v1 kétgépes írójoga, az implant és
        a MariaDB-tranzakciók csak valódi ASA teszttel fogadhatók el.
        A mentéslenyomatok összevetése nem oldja fel a védett mapok indítási zárolását.</span>
    </div>
  </div>;
}
