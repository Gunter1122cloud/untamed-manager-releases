import {useEffect,useRef,useState} from "react";
import {AlertTriangle,CheckCircle2,Shield} from "lucide-react";
import {managerBridge,type DashboardSnapshot,type IniFile,type IniDocument,type IniPreview,type IniGameCreatePreview} from "./bridge";
import {EngramPointsEditor} from "./EngramPointsEditor";
import {NamedEngramEditor} from "./NamedEngramEditor";
import {LootEditor} from "./LootEditor";
import {GameplayPresetPanel} from "./GameplayPresetPanel";
import {Panel} from "./Panel";
import {describeNativeError} from "./state/nativeErrors";

const iniCreateErrorMessages:Record<string,string>={
  "UM-INI-CREATE-FRESH-ONLY":"A kiválasztott map nem igazoltan új, helyi és Store-v1-től független ASA-telepítés. A történeti adatokhoz nem nyúltunk.",
  "UM-INI-CREATE-SHARED-STORE":"A telepítést egy történeti korábbi kezelőalkalmazás/Store-v1 map is használja. Új Game.ini nem hozható létre itt.",
  "UM-INI-CREATE-ALL-MAPS-STOPPED":"A közös telepítés valamelyik mapja fut vagy leállítása nem igazolt. Előbb ellenőrizd az összes helyi folyamatot.",
  "UM-INI-GAME-ALREADY-EXISTS":"A Game.ini már létezik. Nyisd meg a meglévő fájlt; a létrehozás nem írta felül.",
  "UM-INI-CREATE-LINKED-PATH":"A célmappa junction/link útvonalon található. A művelet tiltott.",
  "UM-INI-CREATE-LINK-CHANGED":"A célmappa linkállapota az előellenőrzés óta megváltozott. A létrehozás leállt.",
  "UM-INI-CREATE-CONCURRENT-TARGET":"Egy másik művelet létrehozta a Game.ini-t. Nyisd meg újra; nem történt felülírás.",
  "UM-INI-CREATE-PROFILE-CHANGED":"A map beállításai megváltoztak az előnézet óta. Ellenőrizd újra.",
  "UM-INI-CREATE-LEASE-CHANGED":"A telepítés vagy a map módosult a zárolás alatt. Indíts új előellenőrzést.",
  "UM-INI-CREATE-EXPIRED":"Az előnézet jóváhagyása lejárt. Indíts új előellenőrzést.",
  "UM-INI-CREATE-EXPLICIT-APPROVAL":"A létrehozáshoz új, kifejezett jóváhagyás kell.",
  "UM-INI-CREATE-SHA-MISMATCH":"A frissen létrehozott fájl tartalma nem egyezik a visszaellenőrzéskor. Kézi fájlellenőrzés szükséges.",
  "UM-INI-CREATE-FIXED-DISK":"A telepítésnek helyi Windows-lemezen kell lennie.",
  "UM-INI-MAP-NOT-OWNED":"Ezt a mapot nem a helyi Manager kezeli."
};
function describeIniCreateFailure(code:string){
  return Object.hasOwn(iniCreateErrorMessages,code)?
    iniCreateErrorMessages[code]:
    "A natív ellenőrzés nem engedélyezte a létrehozást. Ellenőrizd a map állapotát; nem történt automatikus újrapróbálkozás.";
}

export function IniCenter({ snapshot, selected, setSelected, mapLocked=false }: {
  snapshot: DashboardSnapshot; selected: string; setSelected: (v: string) => void;
  mapLocked?:boolean
}) {
  const [file, setFile] = useState<IniFile>("Game.ini");
  // Bind async INI snapshots, previews and approvals to the exact map/file.
  // A late response after map navigation cannot populate another map's editor.
  const currentMap=useRef(selected);
  const currentFile=useRef(file);
  currentMap.current=selected;
  currentFile.current=file;
  const operationEpoch=useRef(0);
  const mounted=useRef(true);
  useEffect(()=>{
    mounted.current=true;
    return ()=>{mounted.current=false;++operationEpoch.current;};
  },[]);
  const belongs=(mapId:string,name:IniFile,epoch:number)=>
    mounted.current&&operationEpoch.current===epoch&&
    currentMap.current===mapId&&currentFile.current===name;
  const [opened, setOpened] = useState<IniDocument | null>(null);
  const [content, setContent] = useState("");
  const [preview, setPreview] = useState<IniPreview | null>(null);
  const [mergeAvailable,setMergeAvailable]=useState(false);
  const [createPreview,setCreatePreview]=useState<IniGameCreatePreview|null>(null);
  const [createApproved,setCreateApproved]=useState(false);
  const [missingGame,setMissingGame]=useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");
  useEffect(() => {
    ++operationEpoch.current;
    setOpened(null);setContent("");setPreview(null);setMergeAvailable(false);
    setCreatePreview(null);setCreateApproved(false);setMissingGame(false);
    setNotice("");setError("");setBusy(false);
    return ()=>{++operationEpoch.current;};
  }, [selected, file]);
  const load = async (draftOnly=false) => {
    if(!selected||busy)return;
    const requested=selected,name=file,epoch=operationEpoch.current;
    setBusy(true);setError("");setNotice("");setPreview(null);setMergeAvailable(false);
    setCreatePreview(null);setCreateApproved(false);setMissingGame(false);
    try {
      const fresh=draftOnly?await managerBridge.iniOpenDraft(requested):await managerBridge.iniOpen(requested,name);
      if(!belongs(requested,name,epoch))return;
      if(fresh.mapId.toLowerCase()!==requested.toLowerCase()||fresh.file!==name)
        throw new Error("UM-INI-OPEN-OTHER-MAP");
      setOpened(fresh);setContent(fresh.content);
    }catch(e){
      if(!belongs(requested,name,epoch))return;
      const code=e instanceof Error?e.message:"UM-INI-OPERATION-FAILED";
      setMissingGame(file==="Game.ini"&&
        ["UM-INI-GAME-MISSING","UM-INI-CONFIG-DIRECTORY-MISSING"].includes(code));
      const message:Record<string,string>={
        "UM-INI-STOP-REQUIRED":"A map nincs igazoltan leállítva, vagy karbantartás folyik. Az INI-szerkesztés tiltva.",
        "UM-INI-INSTALLATION-MISSING":"A regisztrált ASA telepítési gyökér hiányzik vagy nem helyi útvonal. Ellenőrizd a map regisztrációját.",
        "UM-INI-SERVER-EXE-MISSING":"A regisztrált telepítésben nem található az elvárt ArkAscendedServer.exe. Ellenőrizd a SteamCMD telepítés útvonalát.",
        "UM-INI-CONFIG-DIRECTORY-MISSING":"Az ASA még nem hozta létre a Saved/Config/WindowsServer mappát. Első indítás előtt ez normális lehet. Állítsd be a mapot, majd készítsd elő az első RCON-konfigurációt.",
        "UM-INI-GAME-MISSING":"A Game.ini még nem létezik. Ez önmagában nem telepítési hiba; az ASA első indítása után próbáld újra.",
        "UM-INI-GUS-MISSING":"A GameUserSettings.ini még nem létezik. Új, leállított mapnál az első RCON-előkészítő jóváhagyással hozhatja létre.",
        "UM-INI-ACCESS-DENIED":"A Windows megtagadta az INI olvasását. Ellenőrizd a jogosultságokat.",
        "UM-INI-PATH-OR-CONTENT-UNSAFE":"Az INI útvonala, linkje vagy tartalma nem felelt meg a biztonsági ellenőrzésnek.",
        "UM-INI-ETAG-MISMATCH":"Az INI fájl az előnézet óta megváltozott. A frissebb fájlt nem írtuk felül. Nyisd meg újra, és ellenőrizd az eltéréseket.",
        "UM-INI-CONFLICT-OR-IO-ERROR":"Az INI fájl zárolt, megváltozott vagy nem olvasható biztonságosan.",
        "UM-HISTORICAL-STORE-LOCKED":"A történeti Store-v1 map konfigurációja védett.",
        "UM-INI-OPEN-OTHER-MAP":"Az INI natív válasza másik maphoz vagy fájlhoz tartozik; a tartalom és a mentési jóváhagyás elutasítva."
      };
      setError(message[code]??"A natív INI-ellenőrzés megtagadta a megnyitást ("+code+").");
    }finally{if(belongs(requested,name,epoch))setBusy(false);}
  };
  const prepareMissingGame=async()=>{
    if(!selected||file!=="Game.ini"||busy)return;
    const requested=selected,name=file,epoch=operationEpoch.current;
    setBusy(true);setError("");setCreatePreview(null);setCreateApproved(false);
    try{
      const result=await managerBridge.iniCreateGamePreview(requested);
      if(!belongs(requested,name,epoch))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||result.file!==name||
         result.originalStoreTouched||result.createsFile)
        throw new Error("UM-INI-CREATE-PREVIEW-OTHER-MAP");
      setCreatePreview(result);
    }catch(e){
      if(belongs(requested,name,epoch)){
        const code=e instanceof Error?e.message:"UM-INI-CREATE-DENIED";
        setError(describeIniCreateFailure(code));
      }
    }finally{if(belongs(requested,name,epoch))setBusy(false);}
  };
  const applyMissingGame=async()=>{
    if(!createPreview||!createApproved||createPreview.mapId!==selected||
       file!=="Game.ini"||busy)return;
    const token=createPreview.token,requested=selected,name=file,
      epoch=operationEpoch.current;
    setBusy(true);setError("");setCreatePreview(null);setCreateApproved(false);
    let committed=false;
    try{
      const receipt=await managerBridge.iniCreateGameApply(token);
      if(!belongs(requested,name,epoch))return;
      if(receipt.mapId.toLowerCase()!==requested.toLowerCase()||
         receipt.file!==name||!receipt.created||receipt.originalStoreTouched)
        throw new Error("UM-INI-CREATE-RECEIPT-INVALID");
      committed=true;
      setMissingGame(false);
      setNotice("A hiányzó Game.ini ezen az igazoltan leállított mapon létrejött.");
      try{
        const fresh=await managerBridge.iniOpen(requested,name);
        if(!belongs(requested,name,epoch))return;
        if(fresh.mapId.toLowerCase()!==requested.toLowerCase()||fresh.file!==name)
          throw new Error("UM-INI-OPEN-OTHER-MAP");
        setOpened(fresh);setContent(fresh.content);
        setNotice("A hiányzó Game.ini kizárólag ezen az igazoltan leállított új mapon jött létre. A fájlt az ETag-védett szerkesztő már megnyitotta.");
      }catch{
        if(belongs(requested,name,epoch))
          setError("A Game.ini létrejött, de az újraolvasása sikertelen. Ne ismételd meg a létrehozást; csak nyisd meg a fájlt.");
      }
    }catch(e){
      if(belongs(requested,name,epoch)&&!committed){
        const code=e instanceof Error?e.message:"UM-INI-CREATE-FAILED";
        setError(describeIniCreateFailure(code));
      }
    }finally{if(belongs(requested,name,epoch))setBusy(false);}
  };
  const showPreview = async () => {
    if(!opened||busy||opened.mapId.toLowerCase()!==selected.toLowerCase()||
       opened.file!==file)return;
    const requested=selected,name=file,epoch=operationEpoch.current;
    setBusy(true);setError("");setNotice("");
    try{
      const result=await managerBridge.iniPreview(opened.token,content);
      if(!belongs(requested,name,epoch))return;
      if(result.originalSha256.toLowerCase()!==opened.sha256.toLowerCase())
        throw new Error("UM-INI-PREVIEW-BASE-HASH-MISMATCH");
      setPreview(result);
    }catch{
      if(belongs(requested,name,epoch)){
        setPreview(null);
        setError("Az INI előnézet érvénytelen, másik alapverzióhoz tartozik vagy lejárt. Nem írtunk fájlba.");
      }
    }finally{if(belongs(requested,name,epoch))setBusy(false);}
  };
  const rebaseIni = async () => {
    if(!opened||busy||opened.mapId.toLowerCase()!==selected.toLowerCase()||
       opened.file!==file)return;
    const requested=selected,name=file,epoch=operationEpoch.current;
    setBusy(true);setError("");setNotice("");setPreview(null);
    try{
      const merge=await managerBridge.iniRebasePreview(opened.token,content);
      if(!belongs(requested,name,epoch))return;
      setOpened({...opened,sha256:merge.freshSha256,content:merge.diskContent});
      setContent(merge.content);
      setMergeAvailable(false);
      setNotice("A nem ütköző INI-kulcsváltozások egyeztetve, lemezírás nélkül. Kérj új hash-előnézetet, majd külön hagyd jóvá a mentést.");
    }catch(e){
      if(!belongs(requested,name,epoch))return;
      const code=e instanceof Error?e.message:"UM-INI-MERGE-FAILED";
      setError(code==="UM-INI-MERGE-CONFLICT"
        ?"Az INI ugyanazon kulcsán vagy a sorstruktúrában versengő módosítás történt. Automatikus egyeztetés tiltva; a meglévő fájl változatlan. Ellenőrizd kézzel a különbségeket."
        :"Nem sikerült az INI újraegyeztetése ("+code+"). A lemezen nem írtunk fájlt.");
    }finally{if(belongs(requested,name,epoch))setBusy(false);}
  };
  const save = async () => {
    if(!opened||!preview?.changed||busy||
       opened.mapId.toLowerCase()!==selected.toLowerCase()||opened.file!==file||
       preview.originalSha256.toLowerCase()!==opened.sha256.toLowerCase())return;
    const requested=selected,name=file,epoch=operationEpoch.current;
    setBusy(true);setError("");setNotice("");
    try{
      const result=await managerBridge.iniSave(opened.token);
      if(!belongs(requested,name,epoch))return;
      if(!result.saved||result.sha256.toLowerCase()!==preview.nextSha256.toLowerCase())
        throw new Error("UM-INI-SAVE-RECEIPT-MISMATCH");
      setOpened(null);setPreview(null);setContent("");setMergeAvailable(false);
      setNotice(result.backupCreated
        ?"Az INI mentése atomikusan befejeződött, visszaállítási .bak másolattal."
        :"Az INI mentése befejeződött.");
    }catch(e){
      if(!belongs(requested,name,epoch))return;
      const code=e instanceof Error?e.message:"UM-INI-OPERATION-FAILED";
      setError(code==="UM-INI-ETAG-MISMATCH"
        ?"Az INI időközben megváltozott. Az eredeti fájlt nem írtuk felül. A háromutas egyeztetéssel ellenőrizheted a független kulcsmódosításokat."
        :code==="UM-INI-SAVE-RECEIPT-MISMATCH"
        ?"Az INI-mentésre visszakapott SHA-256 nem egyezik az elfogadott előnézettel. A mentés megtörténhetett; ne ismételd meg ellenőrzés nélkül."
        :"Az INI-mentést a natív biztonsági ellenőrzés elutasította ("+code+"). Ellenőrizd, hogy a map leállt-e, és nem változott-e közben a fájl.");
      setMergeAvailable(code==="UM-INI-ETAG-MISMATCH");
      setPreview(null);
    }finally{if(belongs(requested,name,epoch))setBusy(false);}
  };
  const stageGame=async()=>{
    if(!opened||!preview?.changed||busy||file!=="Game.ini"||opened.mapId!==selected)return;
    const requested=selected,epoch=operationEpoch.current;
    setBusy(true);setError("");setNotice("");
    try{
      const result=await managerBridge.iniStageDraft(opened.token);
      if(!belongs(requested,"Game.ini",epoch))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase()||
         (result.state!=="queued"&&result.state!=="applied")||result.nextSha256!==preview.nextSha256)
        throw Error("UM-INI-DRAFT-RECEIPT");
      setOpened(null);setPreview(null);setContent("");
      setNotice("Titkosított Game.ini piszkozat mentve. A következő igazolt natív indítás előtt alkalmazható; külső fájlváltozás esetén megmarad, de nem ír felül semmit.");
    }catch(e){if(belongs(requested,"Game.ini",epoch))setError(describeNativeError(e instanceof Error?e.message:"UM-INI-DRAFT-RECEIPT","A következő indításra előkészített piszkozat nem igazolható."));}
    finally{if(belongs(requested,"Game.ini",epoch))setBusy(false);}
  };
  const pendingGame=async(discard=false)=>{
    if(!selected||busy)return;
    const requested=selected,name=file,epoch=operationEpoch.current;
    setBusy(true);setNotice("");setError("");
    try{
      const result=discard?await managerBridge.iniPendingDiscard(requested):await managerBridge.iniPendingStatus(requested);
      if(!belongs(requested,name,epoch))return;
      if(result.mapId.toLowerCase()!==requested.toLowerCase())throw Error("UM-INI-DRAFT-MAP");
      const labels={none:"Nincs függő Game.ini piszkozat.",queued:"Előkészítve a következő natív indításra.",conflict:"A lemezen lévő Game.ini megváltozott; a piszkozat nem lesz automatikusan alkalmazva.",applied:"Az előkészített SHA-256 egyezik a lemezen lévő Game.ini fájllal.",unavailable:"A helyi piszkozat állapota nem ellenőrizhető."};
      setNotice(labels[result.state]);
    }catch{if(belongs(requested,name,epoch))setError("A Game.ini piszkozat állapota nem ellenőrizhető.");}
    finally{if(belongs(requested,name,epoch))setBusy(false);}
  };
  const close = async () => {
    if(!opened||busy||opened.mapId.toLowerCase()!==selected.toLowerCase()||
       opened.file!==file)return;
    const requested=selected,name=file,epoch=operationEpoch.current;
    setBusy(true);setError("");
    try{await managerBridge.iniClose(opened.token);}
    catch{/* Local draft expires; discarding UI text never writes a file. */}
    finally{
      if(belongs(requested,name,epoch)){
        setOpened(null);setContent("");setPreview(null);
        setMergeAvailable(false);setBusy(false);
      }
    }
  };
  return <>
    <div className="page-intro"><span className="eyebrow">LOCAL CONFIGURATION / ETAG PROTECTION</span><h1>INI szerkesztő</h1>
      <p>A valódi Game.ini és GameUserSettings.ini szerkesztése, eredeti kódolás és sortörés megőrzésével. Az azonnali lemezírás csak igazoltan leállított helyi mapnál lehetséges; Game.ini piszkozat futás közben is előkészíthető.</p></div>
    <div className="ini-toolbar">
      <label>Helyi map
        {mapLocked
          ?<strong aria-label="INI forrásmapja">{snapshot.local.maps.find(m=>m.id===selected)?.name??"Nem található"}</strong>
          :<select aria-label="INI mapja" value={selected} onChange={e=>setSelected(e.target.value)}>
            {snapshot.local.maps.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}
          </select>}
      </label>
      <label>INI-fájl
        <select aria-label="INI fájl" value={file} onChange={e=>setFile(e.target.value as IniFile)}>
          <option value="Game.ini">Game.ini</option><option value="GameUserSettings.ini">GameUserSettings.ini</option>
        </select>
      </label>
      <button className="primary" onClick={() => { void load(); }} disabled={busy || !selected}>Fájl biztonságos megnyitása</button>
      {file==="Game.ini"&&<>
        <button className="secondary" disabled={busy||!selected} onClick={()=>void load(true)}>Piszkozat megnyitása futó mapnál is</button>
        <button className="secondary" disabled={busy||!selected} onClick={()=>void pendingGame()}>Függő Game.ini állapota</button>
        <button className="secondary" disabled={busy||!selected} onClick={()=>void pendingGame(true)}>Függő piszkozat elvetése</button>
      </>}
    </div>
    <Panel title={opened ? `Szerkesztés · ${opened.file}` : "Helyi konfiguráció"}
      action={opened && <span className="muted tiny">Kódolás: {opened.encoding} · {opened.bytes.toLocaleString("hu-HU")} byte</span>}>
      {!opened && <div className="empty">Válaszd ki a regisztrált helyi mapot és az INI-fájlt. Azonnali szerkesztéshez igazolt leállítás szükséges. Game.ini esetén használható a külön futás közbeni piszkozatnyitás is.</div>}
      {missingGame && file==="Game.ini"&&!opened&&<section className="historical-preview"
        role="region" aria-label="Hiányzó Game.ini létrehozása">
        <h3>Új, még nem létező Game.ini biztonságos létrehozása</h3>
        <p>Csak új, nem Store-v1-es, helyi és igazoltan leállított szervernél.
          A már létező Game.ini soha nem írható felül ezzel a művelettel.</p>
        <button type="button" className="secondary" disabled={busy}
          onClick={()=>{void prepareMissingGame();}}>Game.ini létrehozásának előnézete</button>
        {createPreview&&<>
          <label className="historical-approval">
            <input type="checkbox" checked={createApproved} disabled={busy}
              onChange={e=>setCreateApproved(e.target.checked)}/>
            Jóváhagyom a hiányzó Game.ini létrehozását ezen az új, leállított mapon.
          </label>
          <button type="button" className="primary" disabled={!createApproved||busy}
            onClick={()=>{void applyMissingGame();}}>Game.ini létrehozása és megnyitása</button>
        </>}
      </section>}
      {opened && <>
        {file==="Game.ini"&&opened.file===file&&opened.mapId.toLowerCase()===selected.toLowerCase()&&
          <><EngramPointsEditor key={opened.token} content={content} disabled={busy}
            onDraft={next=>{setContent(next);setPreview(null);setNotice("");}}/>
          <NamedEngramEditor key={"named:"+opened.token} content={content} disabled={busy}
            onDraft={next=>{setContent(next);setPreview(null);setNotice("");}}/>
          <LootEditor key={"loot:"+opened.token} content={content} disabled={busy}
            onDraft={next=>{setContent(next);setPreview(null);setNotice("");}}/>
          <GameplayPresetPanel key={"preset:"+opened.token} content={content} disabled={busy}
            onDraft={next=>{setContent(next);setPreview(null);setNotice("");}}/></>}
        <label className="ini-editor-label" htmlFor="ini-contents">Az eredeti fájl szerkeszthető tartalma</label>
        <textarea id="ini-contents" className="ini-editor" spellCheck={false} value={content}
          onChange={e=>{setContent(e.target.value);setPreview(null);}} disabled={busy}/>
        <div className="ini-actions">
          <button className="secondary" disabled={busy} onClick={() => { void close(); }}>Mégse · piszkozat elvetése</button>
          <button className="primary" disabled={busy || content===opened.content} onClick={() => { void showPreview(); }}>Hash és változások előnézete</button>
          {opened.evidence!=="LOCAL_GAME_INI_DRAFT_SNAPSHOT"&&<button className="primary" disabled={busy || !preview?.changed} onClick={() => { void save(); }}>Jóváhagyás és atomikus mentés</button>}
          {file==="Game.ini"&&<button className="primary" disabled={busy||!preview?.changed} onClick={()=>void stageGame()}>Mentés a következő natív indításra</button>}
          {mergeAvailable&&<button className="secondary" disabled={busy}
            onClick={()=>{void rebaseIni();}}>
            Háromutas INI-egyeztetés előnézete (nem ment)
          </button>}
        </div>
        {preview && <div className="ini-preview">
          <span>Változás: <b>{preview.changed ? "Igen" : "Nincs"}</b></span>
          <span>Sorok: <b>{preview.oldLines} → {preview.newLines}</b></span>
          <span>Eredeti SHA-256: <code>{preview.originalSha256.slice(0,16)}…</code></span>
          <span>Új SHA-256: <code>{preview.nextSha256.slice(0,16)}…</code></span>
        </div>}
      </>}
      {notice && <div role="status" className="success-inline"><CheckCircle2 size={18}/>{notice}</div>}
      {error && <div role="alert" className="error-inline"><AlertTriangle size={18}/>{error}</div>}
      <div className="warning-inline"><Shield size={17}/> Az INI jelszavakat tartalmazhat: kizárólag a helyi, védett Windows WebView2 felületen szerkeszd. Mentéskor a natív C# újraellenőrzi a leállítást és az eredeti fájl SHA-256 lenyomatát.</div>
    </Panel>
  </>;
}

