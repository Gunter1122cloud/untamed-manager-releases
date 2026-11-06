import {useCallback,useEffect,useState,type FormEvent} from "react";
import {AlertTriangle,CheckCircle2,KeyRound,Network,RefreshCw,Shield,Wifi,WifiOff} from "lucide-react";
import {managerBridge,type LanPreview,type LanStatus} from "./bridge";

export function LanSettingsPanel(){
  const [status,setStatus]=useState<LanStatus|null>(null);
  const [listen,setListen]=useState("0");
  const [peer,setPeer]=useState("");
  const [port,setPort]=useState("0");
  const [secret,setSecret]=useState("");
  const [reveal,setReveal]=useState(false);
  const [preview,setPreview]=useState<LanPreview|null>(null);
  const [working,setWorking]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const reload=useCallback(async()=>{
    try{
      const s=await managerBridge.lanStatus();
      setStatus(s);
      setListen(String(s.listenPort));setPeer(s.peerAddress);
      setPort(String(s.peerPort));
    }catch{setError("A natív LAN-beállítás nem olvasható.");}
  },[]);
  useEffect(()=>{void reload();},[reload]);
  const invalidate=()=>{setPreview(null);setNotice("");setError("");};
  const generate=()=>{
    const random=crypto.getRandomValues(new Uint8Array(32));
    setSecret(Array.from(random,b=>b.toString(16).padStart(2,"0")).join(""));
    setReveal(true);invalidate();
  };
  const preflight=async(event:FormEvent)=>{
    event.preventDefault();invalidate();
    const local=Number(listen),other=Number(port);
    if(!Number.isInteger(local)||!Number.isInteger(other)||
       !([0].includes(local)||local>=1024&&local<=65535)||
       !([0].includes(other)||other>=1024&&other<=65535)){
      setError("A LAN-port 0 (kikapcsolva) vagy 1024–65535 lehet.");return;
    }
    if(peer.trim().length===0 && other!==0){
      setError("Távoli cím nélkül a peer-portnak 0-nak kell lennie.");return;
    }
    if(secret && secret.length<24){
      setError("A két Manager közös kulcsa legalább 24 karakter legyen.");return;
    }
    setWorking(true);
    try{
      const p=await managerBridge.lanPreview(local,peer.trim(),other,secret);
      setPreview(p);
      // The native bridge retains the one-time approval token; the sensitive
      // plaintext must not remain in component state during final approval.
      setSecret("");setReveal(false);
    }catch(e){
      setError("A LAN-párosítás ellenőrzése sikertelen ("+
        (e instanceof Error?e.message:"UM-LAN-INVALID-PRIVATE-PEER")+
        "). Csak saját 10.x / 172.16–31.x / 192.168.x IPv4-cím engedélyezett.");
    }finally{setWorking(false);}
  };
  const save=async()=>{
    if(!preview||working)return;
    const token=preview.token;setPreview(null);setWorking(true);setError("");
    try{
      await managerBridge.lanApply(token);
      setNotice("A helyi párosítás mentve. A LAN-listener újraindításához csak a Managert kell újraindítani, az ASA mapokat nem.");
      await reload();
    }catch(e){
      setError("A helyi LAN-profil mentése elutasítva ("+
        (e instanceof Error?e.message:"UM-LAN-SAFE-UPDATE-FAILED")+").");
    }finally{setWorking(false);}
  };
  return <section className="glass panel lan-pairing-panel">
    <div className="panel-head"><h2><Network size={19}/> Kétgépes LAN-párosítás</h2>
      <span className="mod-tag green">Helyi Manager / peer csak olvasás</span></div>
    <div className="panel-inner">
      <p className="muted small">A HP és a Ryzen Manager ugyanazzal a közös kulccsal, egymás privát IPv4-címére csatlakozik.
        Mindkét gépen külön kell megadni a fogadóportot és a másik gép címét.
        A távoli szervervezérlés és Store-v1 írás nem engedélyezett.</p>
      <div className="lan-status" role="status">
        {status?.remoteFresh?<Wifi size={17}/>:<WifiOff size={17}/>}
        <div><strong>{status?.hostName||"Helyi gazdagép"}</strong>
          <span>{status?.remoteState||"LAN-konfiguráció lekérése..."}</span>
          <small>Közös kulcs: {status?.pairingKeyConfigured?"helyileg, védetten beállítva":"nincs beállítva"}</small>
        </div>
        <button type="button" className="quiet" disabled={working} aria-label="LAN állapot újraolvasása"
          onClick={()=>{void reload();}}><RefreshCw size={16}/></button>
      </div>
      <form className="lan-settings-form" onSubmit={e=>{void preflight(e);}}>
        <label>Helyi fogadóport (0 = kikapcsolva)
          <input aria-label="Helyi LAN port" inputMode="numeric" value={listen}
            onChange={e=>{setListen(e.target.value);invalidate();}} disabled={working}/>
        </label>
        <label>Másik Manager privát IPv4-címe
          <input aria-label="Másik LAN cím" value={peer} placeholder="192.168.1.200"
            onChange={e=>{setPeer(e.target.value);invalidate();}} disabled={working}/>
        </label>
        <label>Másik Manager fogadóportja
          <input aria-label="Távoli LAN port" inputMode="numeric" value={port}
            onChange={e=>{setPort(e.target.value);invalidate();}} disabled={working}/>
        </label>
        <label className="lan-secret">Közös párosítási kulcs – mindkét gépen ugyanaz
          <input aria-label="LAN közös kulcs" type={reveal?"text":"password"}
            autoComplete="off" spellCheck={false} maxLength={256} value={secret}
            placeholder={status?.pairingKeyConfigured?"Már van beállított kulcs – üresen hagyva megtartjuk":"Legalább 24 karakter"}
            onChange={e=>{setSecret(e.target.value);invalidate();}} disabled={working}/>
          <span className="lan-secret-actions">
            <button type="button" className="secondary" disabled={working} onClick={generate}>
              <KeyRound size={15}/> Biztonságos kulcs generálása</button>
            <button type="button" className="quiet" disabled={working||!secret}
              onClick={()=>setReveal(v=>!v)}>{reveal?"Elrejtés":"Megjelenítés"}</button>
          </span>
        </label>
        <button className="secondary" type="submit" disabled={working}>
          <Shield size={16}/> LAN beállítások ellenőrzése
        </button>
      </form>
      {preview&&<div className="lan-preview" role="region" aria-label="LAN párosítás jóváhagyása">
        <b><CheckCircle2 size={17}/> Biztonságos helyi előnézet</b>
        <p>Fogadóport: {preview.listenPort||"ki"} · Peer:
          {" "}{preview.peerAddress||"nincs"}:{preview.peerPort||"—"}.
          A párosítás csak Manager-újraindítás után lép életbe.</p>
        <p>Új közös kulcs: {preview.pairingKeyWillChange?"igen, DPAPI-védetten mentjük":"nem, a régit megtartjuk"}.
          Másik gazdagépen nem végzünk műveletet.</p>
        <div className="lan-preview-actions">
          <button className="primary" type="button" disabled={working}
            onClick={()=>{void save();}}>Párosítás helyi mentése</button>
          <button className="secondary" type="button" disabled={working}
            onClick={()=>setPreview(null)}>Mégse</button>
        </div>
      </div>}
      {notice&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{notice}</div>}
      {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
      <div className="warning-inline"><Shield size={16}/>
        A közös kulcs nem kerül visszaolvasásra a Windows DPAPI-tárolóból.
        Ha most generáltál új kulcsot, már az ellenőrzés előtt jegyezd fel a másik Manager számára.
        Tűzfalat, routert, adatbázist és ASA-fájlokat nem módosítunk.</div>
    </div>
  </section>;
}
