import {useEffect,useState} from "react";
import {AlertTriangle,Shield} from "lucide-react";
import {managerBridge,type ScheduledStoppedSteamOverview} from "./bridge";

/**
 * Legacy schedule records remain readable for audit, but are never actionable.
 * Actual SteamCMD check/update controls live in the explicit manual update UI.
 */
export function ScheduledStoppedSteamSettings({mapId}:{mapId:string}) {
  const [overview,setOverview]=useState<ScheduledStoppedSteamOverview|null>(null);
  const [error,setError]=useState("");
  useEffect(()=>{
    let active=true;
    setOverview(null);
    setError("");
    managerBridge.steamScheduledStatus().then(value=>{
      if(active)setOverview(value);
    }).catch(()=>{
      if(active)setError("A korábbi ütemezési adatok nem olvashatók.");
    });
    return ()=>{active=false;};
  },[mapId]);
  const legacy=overview?.schedules.find(s=>s.mapId===mapId);
  return <section className="historical-preview" aria-label="Korábbi SteamCMD ütemezések">
    <h3><Shield size={17}/> SteamCMD – korábbi ütemezések</h3>
    <p className="muted small">A korábbi automatikus SteamCMD-ütemezések inaktívak maradnak.
      Az új gördülő frissítés külön engedélyezhető; a régi időzítőket nem lehet visszakapcsolni.
      A kézi frissítés továbbra is külön jóváhagyással működik.</p>
    <p role="status"><Shield size={15}/> Régi automatikus frissítés: kikapcsolva.
      {legacy?" · Korábbi ütemezés megtartva, de inaktív.":""}</p>
    {legacy?.lastStatus==="needs-operator"&&
      <p className="warning-inline"><AlertTriangle size={16}/>
        Egy korábbi művelet beavatkozást igényelhet; a naplót kézzel ellenőrizd.
      </p>}
    {error&&<p role="alert" className="error-inline">
      <AlertTriangle size={16}/>{error}</p>}
  </section>;
}
