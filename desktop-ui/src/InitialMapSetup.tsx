import { useEffect, useState, type FormEvent } from "react";
import { AlertTriangle, CheckCircle2, RefreshCw, Shield, Server, Settings2 } from "lucide-react";
import {
  managerBridge, type InitialMapPreview, type InitialMapStatus
} from "./bridge";

export function InitialMapSetup({mapId,onConfigured}: {
  mapId:string; onConfigured:()=>Promise<void>
}) {
  const [status,setStatus]=useState<InitialMapStatus | null>(null);
  const [session,setSession]=useState("Untamed Kingdom");
  const [customLevel,setCustomLevel]=useState("");
  const [game,setGame]=useState("");
  const [query,setQuery]=useState("");
  const [preview,setPreview]=useState<InitialMapPreview | null>(null);
  const [approved,setApproved]=useState(false);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const [checkingStatus,setCheckingStatus]=useState(false);
  useEffect(()=>{
    let active=true;
    setStatus(null);setPreview(null);setApproved(false);setError("");setNotice("");
    setCustomLevel("");
    managerBridge.initialStatus(mapId).then(value=>{
      if(!active)return;
      setStatus(value);
      setGame(String(value.suggestion.game));
      setQuery(String(value.suggestion.query));
    }).catch(()=>{
      if(active)setError("A map első konfigurálhatósága nem ellenőrizhető a natív Managerben.");
    });
    return ()=>{active=false;};
  },[mapId]);

  const refreshStatus=async()=>{
    if(busy||checkingStatus)return;
    setCheckingStatus(true);setError("");
    try{
      const next=await managerBridge.initialStatus(mapId);
      setStatus(next);
      setPreview(null);setApproved(false);
    }catch{
      setStatus(null);
      setError("Nem sikerült újból ellenőrizni a szerver folyamatállapotát; konfigurációt nem módosítottunk.");
    }finally{setCheckingStatus(false);}
  };
  const clear=()=>{setPreview(null);setApproved(false);setError("");setNotice("");};
  const inspect=async(e:FormEvent)=>{
    e.preventDefault();clear();
    const g=Number(game),q=Number(query);
    if(!Number.isInteger(g)||!Number.isInteger(q) ||
        g<1 || g>65535 || q<1 || q>65535 || g===q){
      setError("Különböző Game és Query UDP-portok szükségesek (1–65535).");
      return;
    }
    if(!session.trim() || session.length>75){
      setError("A szerver neve 1–75 karakter lehet.");
      return;
    }
    if(status?.requiresCustomLevel && !/^[A-Za-z][A-Za-z0-9_]{0,79}$/.test(customLevel.trim())){
      setError("Adj meg egy érvényes Unreal ASA pályaszintet: 1–80 karakter, angol betűvel kezdve, csak betű, szám és aláhúzás.");
      return;
    }
    setBusy(true);
    try{
      const result=await managerBridge.initialPreview(
        mapId,session.trim(),g,q,status?.requiresCustomLevel?customLevel.trim():"");
      setPreview(result);
    }catch(e){
      const code=e instanceof Error?e.message:"UM-INITIAL-OPERATION-FAILED";
      setError(code==="UM-INITIAL-EXECUTABLE-INVALID"
        ? "A regisztrált ASA EXE nem igazolható a map saját telepítési gyökerében. Az első konfiguráció nem lett mentve."
        : code==="UM-INITIAL-PORT-IN-USE"
          ? "A kiválasztott Game vagy Query UDP-portot már egy Windows-folyamat használja. Válassz szabad portokat, majd ellenőrizd újra; a Manager-profil nem módosult."
          : code==="UM-INITIAL-PORT-UNVERIFIED"
            ? "A Windows UDP-portfoglalásai most nem ellenőrizhetők megbízhatóan. Nem állítjuk, hogy a port foglalt vagy szabad; kérj új előnézetet, ha a rendszerállapot rendeződött."
            : "A Manager nem fogadta el az indítási paramétereket ("+code+
            "). Lehetséges helyi portütközés, futó map vagy módosult konfiguráció.");
    }finally{setBusy(false);}
  };
  const apply=async()=>{
    if(!preview || !approved)return;
    setBusy(true);setError("");
    try{
      const result=await managerBridge.initialApply(preview.token);
      setPreview(null);setApproved(false);
      setNotice(result.mapName+" indítási profilja mentve. A szerver még nem indult el. Következő lépés: RCON előkészítés, majd az ASA Start előnézete.");
      // The native apply receipt is authoritative. A subsequent refresh
      // failure must never be reported as a rejected configuration commit.
      try {
        await onConfigured();
        const next=await managerBridge.initialStatus(mapId);
        setStatus(next);
      } catch {
        setError("A map indítási profilja mentve, de az állapotfrissítés nem sikerült. Ne ismételd meg a mentést; ellenőrizd újra a map állapotát.");
      }
    }catch(e){
      setPreview(null);setApproved(false);
      const code=e instanceof Error?e.message:"UM-INITIAL-OPERATION-FAILED";
      setError(code==="UM-INITIAL-PORT-IN-USE"
        ? "A Game vagy Query UDP-port az előnézet után foglalttá vált. A jóváhagyott profil nem lett mentve; válassz új portokat és kérj új előnézetet."
        : code==="UM-INITIAL-PORT-UNVERIFIED"
          ? "A jóváhagyás pillanatában a Windows UDP-portállapota nem ellenőrizhető. Nem írtunk profilt; kérj új előnézetet, és ne indíts mapot ismeretlen portállapottal."
          : "Az első konfigurálás elutasítva ("+code+
          "). A megfigyelt mapot és a fájlokat nem indítottuk el.");
    }finally{setBusy(false);}
  };

  if(status && !status.initialSetupAvailable && status.gamePort>0)return null;
  const blockerMessage=status?.blocker==="PROCESS_STOP_NOT_VERIFIED" ?
    "A Windows nem igazolta, hogy a szerver leállt. Ellenőrizd a folyamatokat és a Manager naplóját; a védelmet nem kerüljük meg." :
    status?.blocker==="PROCESS_RUNNING" ? "A Windows szerint a szerverfolyamat fut. Első konfiguráció nem engedélyezett." :
    status?.blocker==="SERVER_EXECUTABLE_MISSING" ? "Az ASA EXE hiányzik, másik telepítéshez tartozik, vagy nem biztonságos helyi útvonalon van. Ellenőrizd a map regisztrált telepítési gyökerét." :
    "A profil már konfigurált, vagy történeti Store-v1 védelem alatt áll.";
  return <section className="glass panel initial-map-setup">
    <div className="panel-head"><h2><Settings2 size={18}/> Első mapbeállítások</h2>
      <span className="mod-tag amber">Indítás előtt</span></div>
    <div className="panel-inner">
      <p className="muted small">Egy már regisztrált ASA-telepítéshez külön állítsd be a szerver nevét és a Game/Query UDP-portokat. A varázsló csak a Manager indítási profilját módosítja.</p>
      {status && !status.initialSetupAvailable && <div className="warning-inline">
        <Shield size={17}/> {blockerMessage}</div>}
      {status?.initialSetupAvailable && status.requiresCustomLevel && <div className="warning-inline">
        <Shield size={17}/> Egyedi/modolt ASA-map: az első indításhoz a mod készítője
        által megadott pontos Unreal pályaszintet kell beírni. Ez önmagában nem telepít
        vagy ellenőriz CurseForge modot, nem engedélyez Store-v1 transfert és nem indít szervert.</div>}
      <div className="draft-actions">
        <button type="button" className="secondary" disabled={busy||checkingStatus}
          onClick={()=>{void refreshStatus();}}><RefreshCw size={16}/>
          {checkingStatus?"Újraellenőrzés...":"Első beállítások állapotának újraellenőrzése"}</button>
        {status&&<span className="muted small">Natív állapot: {status.blocker==="NONE"?"Előkészíthető":status.blocker} · Igazolt leállítás: {status.confirmedStopped?"igen":"nem"}</span>}
      </div>
      <form className="initial-setup-form" onSubmit={e=>{void inspect(e);}}>
        {status?.requiresCustomLevel && <label><span>Egyedi ASA pályaszint (Unreal level)</span>
          <input aria-label="Egyedi ASA pályaszint" value={customLevel} maxLength={80}
            onChange={e=>{setCustomLevel(e.target.value);clear();}}
            placeholder="Példa: EgyediMap_WP"
            autoComplete="off" spellCheck={false}
            disabled={busy||!status?.initialSetupAvailable} required/>
        </label>}
        <label><span>Szerver nyilvános neve</span>
          <input aria-label="ASA szerver neve" value={session} maxLength={75}
            onChange={e=>{setSession(e.target.value);clear();}}
            disabled={busy||!status?.initialSetupAvailable} required/>
        </label>
        <label><span>Game UDP port</span>
          <input aria-label="Első Game UDP" value={game} inputMode="numeric"
            onChange={e=>{setGame(e.target.value);clear();}}
            disabled={busy||!status?.initialSetupAvailable} required/>
        </label>
        <label><span>Query UDP port</span>
          <input aria-label="Első Query UDP" value={query} inputMode="numeric"
            onChange={e=>{setQuery(e.target.value);clear();}}
            disabled={busy||!status?.initialSetupAvailable} required/>
        </label>
        <button className="secondary" type="submit"
          disabled={busy||!status?.initialSetupAvailable ||
            Boolean(status?.requiresCustomLevel&&!/^[A-Za-z][A-Za-z0-9_]{0,79}$/.test(customLevel.trim()))}>
          <Server size={16}/> Beállítások ellenőrzése
        </button>
      </form>
      {preview && <div className="initial-preview">
        <h3><CheckCircle2 size={18}/> Konfigurációs előnézet</h3>
        <div className="initial-preview-grid">
          <span>ASA pálya</span><b>{preview.mapLevel}</b>
          <span>Szerver</span><b>{preview.sessionName}</b>
          <span>Game / Query UDP</span><b>{preview.gamePort} / {preview.queryPort}</b>
          <span>RCON</span><b>Nem engedélyezett</b>
          <span>ASA indítás</span><b>Nem történik</b>
        </div>
        <label className="initial-confirm">
          <input type="checkbox" checked={approved} onChange={e=>setApproved(e.target.checked)}/>
          Jóváhagyom a regisztrált helyi map Manager-profiljának mentését.
        </label>
        <button className="primary" disabled={busy||!approved} onClick={()=>{void apply();}}>
          Jóváhagyás és első konfiguráció mentése
        </button>
      </div>}
      {notice && <div role="status" className="success-inline"><CheckCircle2 size={17}/>{notice}</div>}
      {error && <div role="alert" className="error-inline"><AlertTriangle size={17}/>{error}</div>}
      <div className="warning-inline"><Shield size={17}/>
        A javasolt portok csak a Manager helyi profiljai alapján készülnek.
        A Windows UDP-foglalásokat az előnézetnél és a végső mentés előtt újra ellenőrizzük.
        A router/NAT és a külső elérhetőség ettől még nincs igazolva.</div>
    </div>
  </section>;
}
