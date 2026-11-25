import {useEffect,useState} from "react";
import {managerBridge,type HostedResourcesStatus,type HostedResourceKind} from "./bridge";
const kinds=["AdminListURL","BanListURL","BadWordListURL","BadWordWhiteListURL","CustomDynamicConfigUrl","CustomNotificationURL","CustomLiveTuningUrl"] as const;
export function HostedResourcesPanel(){
  const [status,setStatus]=useState<HostedResourcesStatus|null>(null);
  const [kind,setKind]=useState<HostedResourceKind>(kinds[0]);
  const [port,setPort]=useState("18765");
  const [content,setContent]=useState("");
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  useEffect(()=>{let active=true;managerBridge.hostedResourcesStatus().then(s=>{if(active){setStatus(s);if(s.running)setPort(String(s.port));}})
    .catch(()=>{if(active)setError("A helyi erőforrásszolgáltatás nem ellenőrizhető.");});return ()=>{active=false;};},[]);
  const change=async(stop:boolean)=>{
    setBusy(true);setError("");
    try{
      const result=stop?await managerBridge.hostedResourcesStop():await managerBridge.hostedResourcesPublish(kind,content,Number(port));
      if(!result.loopbackOnly||result.persistent||result.running===stop)throw Error("UM-RESOURCE-RECEIPT");
      setStatus(result);if(!stop)setContent("");
    }catch{setError("A natív erőforrás-művelet nem igazolható. Ellenőrizd a portot és a tartalmat.");}
    finally{setBusy(false);}
  };
  return <details className="premium-panel" style={{padding:16}}>
    <summary>Hosted Resources · helyi ASA-erőforrások</summary>
    <p className="muted small">Kizárólag 127.0.0.1-en kiszolgált, kézzel megadott szövegek. Csak a Manager futásáig élnek;
      leállításkor törlődnek. Ne adj meg jelszót vagy titkos adatot: más helyi folyamat is olvashatja.
      Az ASA INI és a CousinCustomRates változatlan marad. A pontos erőforrásformátumot az adott ASA funkció határozza meg.</p>
    <p>Állapot: {status?status.running?"Fut · localhost:"+status.port:"Leállítva":"Nem igazolt"}</p>
    <label>Helyi TCP-port <input aria-label="Hosted Resources port" type="number" min={1024} max={65535} value={port} disabled={busy||status?.running} onChange={e=>setPort(e.target.value)}/></label>
    <label>Erőforrás <select aria-label="Hosted Resources típus" value={kind} onChange={e=>{setKind(e.target.value as HostedResourceKind);setContent("");}} disabled={busy}>{kinds.map(x=><option key={x}>{x}</option>)}</select></label>
    <label>Kiszolgálandó szöveg <textarea aria-label="Hosted Resources tartalom" rows={5} maxLength={65536} value={content} disabled={busy} onChange={e=>setContent(e.target.value)}/></label>
    <button type="button" className="primary" disabled={busy||!status||!Number.isInteger(Number(port))||Number(port)<1024||Number(port)>65535} onClick={()=>{void change(false);}}>Közzététel / frissítés localhoston</button>
    <button type="button" className="secondary" disabled={busy||!status?.running} onClick={()=>{void change(true);}}>Szolgáltatás leállítása és tartalom törlése</button>
    {status?.resources.map(resource=><p key={resource.kind}><b>{resource.kind}</b> · {resource.bytes} bájt · <code>{resource.url}</code></p>)}
    {error&&<p role="alert" className="warning-inline">{error}</p>}
  </details>;
}
