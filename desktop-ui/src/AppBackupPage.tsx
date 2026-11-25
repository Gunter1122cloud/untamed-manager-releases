import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, Archive, CheckCircle2, HardDrive, RefreshCw, Search, Shield } from "lucide-react";
import { managerBridge, type BackupOverview, type DashboardSnapshot } from "./bridge";
import { MapCover } from "./MapArtwork";
import { OfflineAsaArchivePanel } from "./OfflineAsaArchivePanel";
import { OfflineAsaRestorePanel } from "./OfflineAsaRestorePanel";
import { WorldBackupControls } from "./WorldBackupControls";

export function BackupCenter({mapId,snapshot}:{mapId?:string;snapshot:DashboardSnapshot}) {
  const [info,setInfo]=useState<BackupOverview|null>(null);
  const [error,setError]=useState("");
  const [busy,setBusy]=useState(false);
  const [filter,setFilter]=useState("");
  const reload=useCallback(async()=>{
    setBusy(true);
    try{setInfo(await managerBridge.backupOverview());setError("");}
    catch(e){setError(e instanceof Error?e.message:"UM-READ-UNAVAILABLE");}
    finally{setBusy(false);}
  },[]);
  useEffect(()=>{void reload();},[reload]);
  const scope=useMemo(()=>(info?.maps??[]).filter(m=>!mapId||m.mapId===mapId),
    [info,mapId]);
  const visible=useMemo(()=>{
    const term=filter.trim().toLocaleLowerCase("hu-HU");
    return scope.filter(m=>m.mapName.toLocaleLowerCase("hu-HU").includes(term));
  },[scope,filter]);
  const totals=useMemo(()=>scope.reduce((all,m)=>({
    valid:all.valid+m.validPackages,
    invalid:all.invalid+m.invalidPackages,
    bytes:all.bytes+m.verifiedBytes
  }),{valid:0,invalid:0,bytes:0}),[scope]);
  const recent=useMemo(()=>scope.filter(m=>m.latestVerifiedUtc&&
    !Number.isNaN(new Date(m.latestVerifiedUtc).getTime()))
    .sort((a,b)=>new Date(b.latestVerifiedUtc!).getTime()-
      new Date(a.latestVerifiedUtc!).getTime())[0], [scope]);
  return <div className="premium-backup-page">
    <div className="page-intro"><span className="eyebrow">BACKUP / FILE INTEGRITY</span>
      <h1>Mentésközpont</h1>
      <p>Valódi, ellenőrzött világ-, karakter- és törzsmentés az adott map leállítása
        után; külön védőmentéssel és az eredeti világ megőrzésével történő
        visszaállítás. Az éles kétgépes Store-v1 konzisztencia ettől külön igazolandó.</p>
    </div>
    <WorldBackupControls key={mapId??"all-local-maps"} maps={snapshot.local.maps} selectedMap={mapId}/>
    <OfflineAsaArchivePanel/>
    <OfflineAsaRestorePanel/>
    <div className="premium-backup-metrics">
      <div><span className="premium-backup-icon"><Archive size={24}/></span>
        <b>{scope.length}</b><span>Map</span><small>Mentésleltárban</small></div>
      <div><span className="premium-backup-icon"><CheckCircle2 size={24}/></span>
        <b>{totals.valid}</b><span>Rendben</span><small>Igazolt integritás</small></div>
      <div className={totals.invalid?"has-errors":""}>
        <span className="premium-backup-icon"><AlertTriangle size={24}/></span>
        <b>{totals.invalid}</b><span>Hibás</span><small>Fájlcsomag</small></div>
      <div><span className="premium-backup-icon"><HardDrive size={24}/></span>
        <b>{(totals.bytes/1073741824).toFixed(2)}</b><span>GiB</span>
        <small>Ellenőrzött bájtok</small></div>
    </div>
    <div className="premium-backup-grid">
      <section className="premium-panel premium-backup-list">
        <header className="premium-panel-heading">
          <h2><Archive size={19}/> Mapok mentései</h2>
          <div className="premium-backup-controls">
            <label className="premium-search"><Search size={15}/>
              <input aria-label="Mentés map keresése" placeholder="Map keresése..."
                value={filter} onChange={e=>setFilter(e.target.value)}/></label>
            <button type="button" className="secondary" onClick={()=>{void reload();}}
              disabled={busy}><RefreshCw size={15}/> Frissítés</button>
          </div>
        </header>
        {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
        {!info&&!error&&<div className="premium-map-empty">Mentésadatok ellenőrzése...</div>}
        {info&&visible.length===0&&<div className="premium-map-empty">
          <Archive size={28}/>
          <b>{filter?"Nincs találat":"Nincs regisztrált ellenőrzött mentés"}</b>
          <span>{filter?"Próbálj másik mapnevet.":"A mentéslistát a helyi Managerből olvassuk. Nem jelenítünk meg szintetikus csomagokat."}</span>
        </div>}
        {visible.map(m=><div className="premium-backup-row" key={m.mapId}>
          <div className="premium-backup-map"><MapCover name={m.mapName}/>
            <div><b>{m.mapName}</b><small>Legutóbbi integritás-ellenőrzés:
              {" "}{m.latestVerifiedUtc?new Date(m.latestVerifiedUtc).toLocaleString("hu-HU"):
              "Nem ismert"}</small></div>
          </div>
          <div className="premium-backup-row-status">
            <strong className="premium-backup-ok">{m.validPackages} rendben</strong>
            <span className={m.invalidPackages?"premium-backup-bad":"muted"}>
              {m.invalidPackages} hibás</span>
            <small>{(m.verifiedBytes/1073741824).toFixed(2)} GiB</small>
          </div>
        </div>)}
      </section>
      <aside className="premium-panel premium-backup-integrity">
        <header className="premium-panel-heading">
          <h2><Shield size={19}/> Mentésbiztonság</h2>
        </header>
        <div className="premium-backup-integrity-body">
          <div className="premium-backup-evidence">
            <CheckCircle2 size={20}/>
            <div><b>Fájlintegritás</b>
              <span>Helyileg ellenőrzött csomagok, kizárólag olvasás</span></div>
          </div>
          <div className="premium-backup-fact"><span>Utolsó ismert ellenőrzés</span>
            <strong>{recent?.latestVerifiedUtc?
              new Date(recent.latestVerifiedUtc).toLocaleString("hu-HU"):"Nincs adat"}</strong></div>
          <div className="premium-backup-fact"><span>Mentésállományok</span>
            <strong>{totals.valid+totals.invalid} csomag</strong></div>
          <div className="premium-backup-fact"><span>Visszaállítás</span>
            <strong>Leállított, nem Store-v1 mapon · védőmentéssel</strong></div>
          <div className="premium-backup-caution"><AlertTriangle size={18}/>
            <span>Az ellenőrzött fájlméret és hash <b>nem bizonyítja</b> az ASA
              SaveComplete vagy a kétgépes Store-v1 karakterutazás biztonságát.</span>
          </div>
        </div>
      </aside>
    </div>
  </div>;
}
