import { useCallback, useEffect, useState } from "react";
import { Panel } from "./Panel";
import { AlertTriangle, CheckCircle2, Shield } from "lucide-react";
import { managerBridge } from "./bridge";
import { LanSettingsPanel } from "./LanSettingsPanel";
import { HostedResourcesPanel } from "./HostedResourcesPanel";

export function SettingsCenter() {
  const [status, setStatus] = useState<boolean | null>(null);
  const [credential, setCredential] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const statusReload = useCallback(async () => {
    try { const result = await managerBridge.curseForgeKeyStatus(); setStatus(result.configured); setError(""); }
    catch { setError("A natív hitelesítőadat-tároló nem olvasható."); }
  }, []);
  useEffect(() => { void statusReload(); }, [statusReload]);
  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    if (credential.length === 0 || credential.length > 512) {
      setError("Az API-kulcs hossza 1–512 karakter lehet."); return;
    }
    setBusy(true); setError(""); setMessage("");
    try {
      const response = await managerBridge.saveCurseForgeKey(credential);
      setStatus(response.configured);
      setCredential("");
      setMessage(response.verifiedNow
        ? "A CurseForge hivatalos API-ja elfogadta a kulcsot; a natív Manager Windows DPAPI-val tárolta."
        : "A kulcs tárolása megváltozott.");
    } catch (e) {
      const code = e instanceof Error ? e.message : "UM-CREDENTIAL-OPERATION-FAILED";
      setError(code === "UM-CURSEFORGE-VERIFY-FAILED" ? "A CurseForge nem fogadta el vagy nem tudta ellenőrizni a kulcsot. A régi beállítás megmaradt."
        : code === "UM-CURSEFORGE-TIMEOUT" ? "Időtúllépés a hivatalos CurseForge szerver ellenőrzése közben. A régi kulcs megmaradt."
        : "Nem sikerült biztonságosan menteni a kulcsot (" + code + ").");
    } finally { setBusy(false); }
  };
  const clear = async () => {
    if (!deleteConfirm) { setDeleteConfirm(true); return; }
    setBusy(true); setDeleteConfirm(false); setError(""); setMessage("");
    try {
      const response = await managerBridge.clearCurseForgeKey();
      setStatus(response.configured);
      setMessage("A helyi CurseForge API-kulcs beállítása törölve lett.");
    } catch { setError("Nem sikerült törölni a kulcsot; a korábbi konfiguráció megmaradhatott."); }
    finally { setBusy(false); }
  };
  return <>
    <div className="page-intro"><span className="eyebrow">SECURITY / NATIVE WINDOWS SETTINGS</span><h1>Beállítások</h1>
      <p>A CurseForge API-kulcs kizárólag a Windows-profilodban, DPAPI-védelemmel tárolódik. Nincs GitHub-feltöltés vagy frontend-tárolás.</p></div>
    <Panel title="CurseForge API integráció" action={<span className="mod-tag grey">Natív Windows tároló</span>}>
      <div className="settings-state"><Shield size={20}/><div>
        <strong>{status === null ? "Állapot lekérdezése..." : status ? "API-kulcs helyileg beállítva" : "Nincs beállított API-kulcs"}</strong>
        <p className="muted small">A tárolt kulcsot a felület soha nem jeleníti meg. Régi kulcs sikeres ellenőrzés nélkül nem tekinthető hitelesítettnek.</p>
      </div></div>
      <form className="settings-form" onSubmit={e => { void save(e); }}>
        <label htmlFor="cf-key">CurseForge API-kulcs (nem kerül megjelenítésre)</label>
        <input id="cf-key" type="password" autoComplete="off" spellCheck={false} maxLength={512}
          value={credential} onChange={e=>setCredential(e.target.value)} placeholder="Helyi API-kulcs megadása"/>
        <div className="settings-actions">
          <button className="primary" type="submit" disabled={busy || credential.length===0}>
            <Shield size={16}/>{busy ? "Hivatalos ellenőrzés..." : "Kulcs ellenőrzése és mentése"}
          </button>
          <button className="secondary" type="button" onClick={() => { void clear(); }} disabled={busy || status===false}>
            {deleteConfirm ? "Megerősítés: kulcs törlése" : "Mentett kulcs törlése"}
          </button>
          {deleteConfirm && <button className="quiet" type="button" onClick={()=>setDeleteConfirm(false)}>Mégse</button>}
        </div>
      </form>
      {message && <div className="success-inline" role="status"><CheckCircle2 size={18}/>{message}</div>}
      {error && <div className="error-inline" role="alert"><AlertTriangle size={18}/>{error}</div>}
      <div className="warning-inline"><Shield size={18}/> Kizárólag az API-kulcs kerül mentésre. Semmilyen ASA/Store/SteamCMD művelet nem indul.</div>
    </Panel>
    <LanSettingsPanel/>
    <HostedResourcesPanel/>
    <Panel title="Manager integrációk">
      <div className="details-line"><span>Frontend</span><b>React / TypeScript · offline</b></div>
      <div className="details-line"><span>Natív Windows-felület</span><b>WebView2 / WinForms</b></div>
      <div className="details-line"><span>ASA műveletek</span><b>Friss helyi mapokon külön jóváhagyás; történeti Store-v1 zárolva</b></div>
    </Panel>
  </>;
}
