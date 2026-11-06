import {useMemo,useState} from "react";
import {Activity,AlertTriangle,CheckCircle2,ClipboardCopy,FileCheck2,
  FileWarning,HardDrive,LockKeyhole,RefreshCw,Server,ShieldCheck,Users} from "lucide-react";
import {managerBridge,type TravelCapture,type TravelComparison} from "./bridge";

type Slot="beforeA"|"beforeB"|"afterA"|"afterB";
const slots:Array<{key:Slot;label:string;subtitle:string}>= [
  {key:"beforeA",label:"Előtte · UK-HOME-01",subtitle:"HP · indulás előtt"},
  {key:"beforeB",label:"Előtte · UK-HOME-02",subtitle:"Ryzen · indulás előtt"},
  {key:"afterA",label:"Utána · UK-HOME-01",subtitle:"HP · utazás után"},
  {key:"afterB",label:"Utána · UK-HOME-02",subtitle:"Ryzen · utazás után"}
];
type Snapshot={hostId:string;utc:string;maps:Array<{
  mapId:string;name:string;confirmedStopped:boolean;
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
  const [comparing,setComparing]=useState(false);
  const beforeA=useMemo(()=>parse(evidence.beforeA),[evidence.beforeA]);
  const beforeB=useMemo(()=>parse(evidence.beforeB),[evidence.beforeB]);
  const candidates=useMemo(()=>[...(beforeA?.maps??[]),...(beforeB?.maps??[])],[beforeA,beforeB]);
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
      ...evidence,sourceMap:source,destinationMap:target,profileId:profile
    }));}
    catch{setError("A négy pillanatkép, a gazdagépek vagy a karakterazonosító nem egyeztethető. Nincs éles változtatás.");}
    finally{setComparing(false);}
  };
  const ready=slots.every(slot=>parse(evidence[slot.key])!==null)&&
    Boolean(source&&target&&source!==target&&profile);
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
              {local.complete?"Az öt helyi map leltára teljes és nyugalomban készült.":
                "Hiányzó mentés, futó map vagy sérült/instabil fájl: az elfogadás nem teljes."}
            </div>
            <div className="uk-travel-map-results">
              {local.maps.map(item=><div key={item.mapId}>
                <span><Server size={14}/><b>{item.mapName}</b></span>
                <span>{item.profileCount} karakter · {item.tribeCount} tribe</span>
                {item.confirmedStopped&&item.saveDirectoryFound&&!item.zeroByteDetected&&
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
                  setResult(null);
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
            <label>Vizsgált karakterfájl
              <select aria-label="Vizsgált karakterfájl" value={profile}
                onChange={e=>{setProfile(e.target.value);setResult(null);}}>
                <option value="">Válassz egy profil-lenyomatot</option>
                {survivorIds.map(x=><option key={x.id} value={x.id}>
                  {x.id.slice(0,12)}… · {x.bytes} bájt</option>)}
              </select></label>
          </div>
          <button type="button" className="primary uk-travel-primary"
            disabled={!ready||comparing} onClick={()=>{void compare();}}>
            <FileCheck2 size={17}/>{comparing?"Összevetés...":"Négy mentéslenyomat ellenőrzése"}
          </button>
          {result&&<div role="status" className={result.passed?"uk-travel-pass":"uk-travel-warning"}>
            {result.passed?<CheckCircle2 size={19}/>:<AlertTriangle size={19}/>}
            <div><b>{result.passed?"A fájlszintű karakterelhelyezkedés egyezik.":
              "A karakterfolytonosság nem igazolt."}</b>
              <p>10 map · 2 gazdagép · {result.zeroByteFiles} nulla bájtos fájl.
                Ez nem bizonyítja az ASA játékmeneti utazását vagy az egyetlen Store-írót.</p>
              {result.findings.map((x,i)=><p key={i}>{x}</p>)}
            </div>
          </div>}
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
