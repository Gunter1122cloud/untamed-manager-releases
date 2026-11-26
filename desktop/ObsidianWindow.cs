using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace UntamedDesktop;

/// <summary>Only Windows shell and browser hosting live here; server operations stay in Core.</summary>
public sealed class ObsidianWindow : Form
{
    private const string AppOrigin = "https://untamed-app.example";

    // The WebView2 profile is intentionally retained across upgrades.
    // Pin the index.html navigation URL to the installed native release
    // marker so an earlier WebView cached HTML shell cannot keep serving
    // the old React bundle after a verified Setup/Portable upgrade.
    // Never use remote content or arbitrary URL parts from a profile file.
    internal static string ReleaseScopedUiUrl(string? installedTag)
    {
        string tag=installedTag??"";
        if(tag.Length is <1 or >100 ||
           !System.Text.RegularExpressions.Regex.IsMatch(tag,
               @"\Av\d{1,4}\.\d{1,4}\.\d{1,4}-obsidian(?:-preview\.\d{1,12})?\z",
               System.Text.RegularExpressions.RegexOptions.CultureInvariant,
               TimeSpan.FromMilliseconds(100)))
            tag="unversioned";
        return AppOrigin+"/index.html?release="+Uri.EscapeDataString(tag);
    }

    // Production WebView2 dispatcher AND isolated native integration tests
    // share this exact allowlist. Never route by prefix: an unapproved
    // historical.* method must continue to fail closed in the fallback.
    internal static bool IsHistoricalStoreMethod(string? method)=>method is
        "historical.localStatus" or "historical.localParity" or
        "historical.list" or "historical.preview" or "historical.register" or
        "historical.supplementalPreview" or "historical.supplementalRegister" or "historical.supplementalStart" or
        "historical.originalList" or "historical.originalPreview" or
        "historical.originalRegister" or
        "historical.originalBatchPreview" or "historical.originalBatchRegister" or
        "historical.originalLiveBatchPreview" or
        "historical.originalLiveOperationBlockers" or "historical.operationJournalInventory" or
        "historical.originalLiveBatchApply" or
        "historical.originalClusterPreview" or "historical.originalClusterApply" or
        "historical.sourcePinPreview" or "historical.sourcePinApply" or
        "historical.runtimeSettingsStatus" or
        "historical.runtimeSettingsPreview" or "historical.runtimeSettingsApply" or
        "historical.pairedEvidence" or "historical.launchEvidence" or
        "historical.liveGuard";

    // This is the ACTUAL final host dispatch for the historical Store bridge,
    // not a test-only imitation. The host's WebMessageReceived event uses
    // this method; synthetic tests call it with the real native bridge.
    internal static Task<string> DispatchHistoricalOrReadOnlyAsync(
        string? method,string raw,
        Func<string,Task<string>> historicalHandler,
        Func<string,Task<string>> readOnlyFallback)
    {
        ArgumentNullException.ThrowIfNull(historicalHandler);
        ArgumentNullException.ThrowIfNull(readOnlyFallback);
        return IsHistoricalStoreMethod(method)?
            historicalHandler(raw):readOnlyFallback(raw);
    }

    // Staging an installer is isolated from ASA maps and local configuration.
    // It can take minutes on a slow connection, so the WebView host must not
    // block Dashboard/RCON/status RPCs behind the global command gate.
    // Recovery is invoked under the host gate. Freeze independent native
    // scheduling/backup lanes only for the actual automatic launch dispatch.
    // Only the typed Discord adapter can invoke this closed route table.
    private Task<string> DispatchDiscordNative(string method,string raw)=>method switch
    {
        "operations.snapshot" or "operations.map"=>_operations.HandleAsync(raw),
        "operations.lifecycleBatchPreview" or "operations.lifecycleBatchApply" or "operations.lifecycleBatchCurrent"=>_lifecycleBatch.HandleAsync(raw),
        "historical.activeStartPreview" or "historical.activeStartApply" or "historical.loaderModePreview" or "historical.loaderModeApply"=>_historicalStart.HandleAsync(raw),
        "servers.apiModePreview" or "servers.apiModeApply"=>_localAsaApiMode.HandleAsync(raw),
        "servers.freshStartPreview" or "servers.freshStartApply"=>_freshStart.HandleAsync(raw),
        "servers.gracefulStopPreview" or "servers.gracefulStopApply" or "servers.gracefulStopStatus"=>_freshStop.HandleAsync(raw),
        "historical.gracefulStopPreview" or "historical.gracefulStopApply" or "historical.gracefulStopStatus" or
        "historical.saveWorldPreview" or "historical.saveWorldApply" or "historical.saveWorldStatus"=>_historicalStop.HandleAsync(raw),
        "historical.restartPreview" or "historical.restartApply" or "historical.restartStatus"=>_historicalRestart.HandleAsync(raw),
        "servers.restartPreview" or "servers.restartApply" or "servers.restartStatus"=>_freshRestart.HandleAsync(raw),
        "historical.rconPlayers"=>_historicalReadOnlyRcon.HandleAsync(raw),
        "rcon.listPlayers" or "rcon.status" or "rcon.saveWorld"=>_rcon.HandleAsync(raw),
        "rcon.adminPreview" or "rcon.adminSend"=>_rconAdmin.HandleAsync(raw),
        "historical.adminPreview" or "historical.adminSend"=>_historicalAdminRcon.HandleAsync(raw),
        "backup.preview" or "backup.create" or "backup.restorePreview" or "backup.restore" or "backup.history"=>_worldBackup.HandleAsync(raw),
        "steam.stoppedPreview" or "steam.stoppedApply" or "steam.stoppedStatus"=>_steamUpdate.HandleAsync(raw),
        _=>Task.FromResult(NativeErrorResponse(raw,"UM-DC-ROUTE-DENIED"))
    };
    private bool TryEnterRecoveryLane()
    {
        if(!_lifecycleBatch.CanManagerUpdate()||!_nativeSchedules.TryFreezeForManagerUpdate())return false;
        if(_scheduledBackups.TryFreezeForManagerUpdate())return true;
        _nativeSchedules.ReleaseManagerUpdateFreeze();return false;
    }
    // Discord is not a Manager update: reserve only the selected maps
    // against active/queued native scheduled workers and batch targets.
    // The outer host gate still serializes approval/config publication.
    private bool TryEnterDiscordLane(Guid[] maps)
    {
        if(!_lifecycleBatch.CanDispatchDiscord(maps)||!_nativeSchedules.TryReserveDiscord(maps))return false;
        if(_scheduledBackups.TryReserveDiscord(maps))return true;
        _nativeSchedules.ReleaseDiscord();return false;
    }
    private void ReleaseDiscordLane()
    {
        _scheduledBackups.ReleaseDiscord();
        _nativeSchedules.ReleaseDiscord();
    }
    private bool NativeLifecycleWorkerBusy(Guid? mapId=null)=>
        _freshStop.RecoveryLaneBusy(mapId)||_historicalStop.RecoveryLaneBusy(mapId)||
        _freshRestart.RecoveryLaneBusy(mapId)||_historicalRestart.RecoveryLaneBusy(mapId);
    private bool NativeStorageWorkerBusy()=>
        _steamUpdate.RecoveryLaneBusy()||_steamBatch.RecoveryLaneBusy()||
        _freshInstall.RecoveryLaneBusy()||_worldBackup.RecoveryLaneBusy()||
        _multiHostBackup.RecoveryLaneBusy();
    private bool NativeWorkerBusy()=>NativeLifecycleWorkerBusy()||NativeStorageWorkerBusy();

    // Manual reconciliation and automatic recovery share the same exact
    // map reservation. Global storage/handoff exclusion remains until
    // those workers can expose their own verified resource scopes.
    private bool TryEnterMapRuntimeLane(Guid mapId)
    {
        if(_updateHandoff||_suppressScheduledMutations||
           NativeLifecycleWorkerBusy(mapId)||NativeStorageWorkerBusy())return false;
        var maps=_bridge.LocalCluster.Config.LocalMaps;
        var map=System.Linq.Enumerable.SingleOrDefault(maps,m=>m.Id==mapId);
        if(map==null||map.Maintenance||map.ObservedReadOnlyImported)return false;
        string key=UntamedManager.Core.UntamedOperationCoordinator.ResourceKey(map.InstallationPath);
        if(System.Linq.Enumerable.Count(maps,m=>!string.IsNullOrWhiteSpace(m.InstallationPath)&&
            UntamedManager.Core.UntamedOperationCoordinator.ResourceKey(m.InstallationPath)==key)!=1)return false;
        return TryEnterDiscordLane(new[]{mapId});
    }

    private bool TryEnterWatchdogLane()
    {
        // The caller holds the UI/Discord host gate. Do not race an already
        // accepted native worker which has not yet acquired its Core lease.
        if(NativeWorkerBusy())return false;
        return TryEnterRecoveryLane();
    }
    // Called under the host mutation gate by the periodic supervisor and
    // normal manual lifecycle requests. Existing map/installation proofs
    // remain authoritative; historical receipts are never replayed.
    private bool ReconcileLifecycleMap(UntamedManager.Core.LocalMap map)
    {
        if(map.Maintenance||map.ObservedReadOnlyImported)return false;
        bool changed=false;
        if(map.HistoricalStoreProtected)
        {
            changed|=_historicalStop.TryReconcileSaveReceipt(map);
            changed|=_lifecycleJournal.TryReconcileCurrentOwner(map);
            changed|=_lifecycleJournal.TryReconcileOldOwner(map);
            changed|=_lifecycleJournal.TryReconcileStopped(map);
        }
        else
        {
            changed|=_freshRestart.TryReconcileCurrentOwner(map);
            changed|=_freshRestart.TryReconcileOldOwner(map);
            changed|=_freshRestart.TryReconcileStopped(map);
            changed|=_freshStop.TryReconcileStoppedReceipt(map);
        }
        return changed;
    }

    private async Task ReconcileBeforeLifecyclePreviewAsync(string? method,string raw)
    {
        Guid? id=DesktopRestartReconciler.RequestedPreviewMap(method,raw);
        if(id==null||_updateHandoff||_suppressScheduledMutations)return;
        var map=System.Linq.Enumerable.SingleOrDefault(_bridge.LocalCluster.Config.LocalMaps,
            m=>m.Id==id.Value);
        if(map==null||map.Maintenance||map.ObservedReadOnlyImported)return;
        // The host bridge gate excludes new interactive admissions, but
        // already accepted lifecycle workers and scheduled work on other
        // maps must not deny journal-only recovery here. Reuse the exact
        // map reservation used by Discord; keep unscoped storage workers
        // excluded until their resource scopes are independently proven.
        try
        {
            if(!TryEnterMapRuntimeLane(map.Id))return;
            try
            {
                await Task.Run(()=>{
                    _bridge.LocalCluster.Operations.TryCancelAbandonedPrepared(map.InstallationPath);
                    ReconcileLifecycleMap(map);
                });
            }
            finally{ReleaseDiscordLane();}
        }
        catch(Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Lifecycle reconciliation {0}: {1}",map.Id,ex.GetType().Name);
        }
    }

    private void ReleaseRecoveryLane()
    {
        _scheduledBackups.ReleaseManagerUpdateFreeze();
        _nativeSchedules.ReleaseManagerUpdateFreeze();
    }
    // Final update.install still takes the global gate before handing off
    // to the external updater and closing the Manager.
    private async Task<string> InstallManagerWithFrozenSchedulesAsync(string raw)
    {
        // update.install is dispatched under the host command gate. Hold the
        // independent scheduler lanes too; if busy, leave the running task alone.
        if(NativeWorkerBusy()||_rollingSteam.Busy)
            return NativeErrorResponse(raw,"UM-UPDATE-NATIVE-WORKER-IN-PROGRESS");
        if(!_recovery.CanManagerUpdate())
            return NativeErrorResponse(raw,"UM-UPDATE-WATCHDOG-RECOVERY-IN-PROGRESS");
        if(!_lifecycleBatch.CanManagerUpdate())
            return NativeErrorResponse(raw,"UM-UPDATE-LIFECYCLE-BATCH-IN-PROGRESS");
        if(!_nativeSchedules.TryFreezeForManagerUpdate())
            return NativeErrorResponse(raw,"UM-UPDATE-SCHEDULE-IN-PROGRESS");
        bool backupFrozen=false;
        try
        {
            backupFrozen=_scheduledBackups.TryFreezeForManagerUpdate();
            if(!backupFrozen)return NativeErrorResponse(raw,"UM-UPDATE-BACKUP-IN-PROGRESS");
            string response=await _updateInstall.HandleAsync(raw);
            using var result=JsonDocument.Parse(response);
            _updateHandoff=result.RootElement.TryGetProperty("ok",out var ok)&&
                ok.ValueKind==JsonValueKind.True&&
                result.RootElement.TryGetProperty("data",out var data)&&
                data.TryGetProperty("externalUpdaterStarted",out var started)&&
                started.ValueKind==JsonValueKind.True;
            // Successful handoff holds the gates until this Manager exits;
            // the replacement Manager reads existing journals/ASA identity.
            return response;
        }
        finally
        {
            if(!_updateHandoff)
            {
                if(backupFrozen)_scheduledBackups.ReleaseManagerUpdateFreeze();
                _nativeSchedules.ReleaseManagerUpdateFreeze();
            }
        }
    }

    internal static bool RequiresGlobalBridgeGate(string? method) =>
        method is not (
            // Exact read-only methods must stay responsive while an owner
            // is running an approved SteamCMD/backup/other lengthy mutation.
            // Each native bridge retains its own per-operation coordination.
            "dashboard.snapshot" or "servers.runningLocal" or "activity.status" or "activity.setEnabled" or "activity.clear" or
            "servers.lifecycleJournal" or
            "servers.lifecycleRecoveryPreview" or
            "operations.snapshot" or "operations.map" or "operations.lifecycleBatchCurrent" or "notifications.status" or
            "schedule.nativeStatus" or "schedule.nativeOverview" or "steam.rollingStatus" or "steam.rollingSet" or "steam.rollingReview" or
            "servers.gracefulStopStatus" or "servers.gracefulStopCurrent" or "servers.restartStatus" or
            "servers.restartCurrent" or "servers.freshStartStatus" or
            "historical.restartStatus" or "historical.restartRecovery" or
            "historical.gracefulStopStatus" or "historical.gracefulStopRecovery" or
            "historical.saveWorldStatus" or "historical.saveWorldEvidence" or
            "historical.saveWorldLateEvidence" or "historical.activeStartStatus" or
            "historical.operationJournalInventory" or
            "rcon.status" or "rcon.listPlayers" or "rcon.gameLog" or "rcon.chatLog" or
            "historical.rconPlayers" or "historical.rconGameLog" or
            "historical.rconChatLog" or "historical.rconLiveStatus" or
            "updates.status" or "updates.prepare" or
            "store.inspectCopy" or "store.inspectArchive" or
            "store.compareArchives");
    // A malformed bridge request must never escape an async-void WebView2
    // event handler and terminate the Windows Manager. Keep native exception
    // details and private machine paths out of renderer responses.
    // Plugin management is intentionally MANUAL: only map-owned folder
    // opening and on-disk inventory are available in the shipping Manager.
    // Retired editor/ZIP/toggle handler classes are not an executable route
    // from WebView2, even if an old client directly posts their method names.
    internal static bool IsManualOnlyDeniedPluginMethod(string? method) =>
        method is "plugins.togglePreview" or "plugins.toggleApply" or
            "plugins.zipSelect" or "plugins.zipApply" or
            "plugins.zipRollbackSelect" or "plugins.zipRollbackApply" or
            "plugins.jsonOpen" or "plugins.jsonPreview" or
            "plugins.jsonSave" or "plugins.jsonClose";

    internal static string NativeErrorResponse(string raw,
        string error="UM-NATIVE-UNEXPECTED-FAILURE")
    {
        string? id=null;
        try
        {
            if(raw.Length <= 1_500_000)
            {
                using var doc=JsonDocument.Parse(raw);
                if(doc.RootElement.ValueKind==JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("id",out var field) &&
                   field.ValueKind==JsonValueKind.String &&
                   Guid.TryParse(field.GetString(),out var guid) && guid!=Guid.Empty)
                    id=guid.ToString("D");
            }
        }
        catch(JsonException) { /* Untrusted syntax: return an uncorrelated error */ }
        return JsonSerializer.Serialize(new{id,ok=false,error});
    }

    // Windows-native chrome is styled through DWM. Keep the regular system
    // caption/buttons so taskbar, resize, snap, accessibility and DPI work.
    // DWM may reject an attribute on older builds; these are best-effort only.
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int valueSize);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyObsidianTitleBar();
    }

    private void ApplyObsidianTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        try
        {
            int dark = 1;
            _ = DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode,
                ref dark, sizeof(int));
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
            int caption = ColorTranslator.ToWin32(Color.FromArgb(16, 27, 33));
            int foreground = ColorTranslator.ToWin32(Color.FromArgb(235, 246, 242));
            _ = DwmSetWindowAttribute(Handle, DwmCaptionColor,
                ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(Handle, DwmTextColor,
                ref foreground, sizeof(int));
        }
        catch (DllNotFoundException) { /* Older Windows: system chrome */ }
        catch (EntryPointNotFoundException) { /* Older Windows: system chrome */ }
    }

    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _tray = new();
    private readonly DesktopReadOnlyBridge _bridge;
    private readonly DesktopCredentialBridge _credentials;
    private readonly DesktopModDraftBridge _modDrafts;
    private readonly DesktopModRepairBridge _modRepair;
    private readonly DesktopInstallationRelocationBridge _relocation;
    private readonly DesktopModCopyBridge _modCopy;
    private readonly DesktopModProfileBridge _modProfiles;
    private readonly DesktopOfflineAsaArchiveBridge _offlineClusterArchive;
    private readonly DesktopOfflineAsaRestoreBridge _offlineRestoreLab;
    private readonly DesktopMultiHostClusterBackupBridge _multiHostBackup;
    private readonly DesktopPluginStateBridge _pluginState;
    private readonly DesktopPluginJsonBridge _pluginJson;
    private readonly DesktopPluginZipBridge _pluginZip;
    private readonly DesktopAsaApiCoreBridge _apiCore;
    private readonly DesktopIniBridge _iniEditor;
    private readonly DesktopHostedResourcesBridge _hostedResources;
    private readonly DesktopPlayerActivityBridge _playerActivity;
    private readonly System.Windows.Forms.Timer _playerActivityTimer=new(){Interval=30000};
    private readonly DesktopLocalMapRegistryBridge _mapRegistry;
    private readonly DesktopRunningAsaRegistrationBridge _runningAsaRegistration;
    private readonly DesktopRunningAsaAdoptionBridge _runningAsaAdoption;
    private readonly DesktopClusterRegistryBridge _clusterRegistry;
    private readonly DesktopCurseForgeDiscoveryBridge _modCatalog;
    private readonly DesktopSteamFreshInstallBridge _freshInstall;
    private readonly DesktopInitialMapSetupBridge _initialMapSetup;
    private readonly DesktopFreshStoreEnrollmentBridge _freshStoreEnrollment;
    private readonly DesktopFreshStoreLoaderModeBridge _freshStoreLoaderMode;
    private readonly DesktopLocalAsaApiModeBridge _localAsaApiMode;
    private readonly DesktopHistoricalStoreImportBridge _historicalStore;
    private readonly DesktopHistoricalRconAttachBridge _historicalRcon;
    private readonly DesktopHistoricalActiveStartBridge _historicalStart;
    private readonly DesktopHistoricalRconReadOnlyBridge _historicalReadOnlyRcon;
    private readonly DesktopHistoricalRconAdminBridge _historicalAdminRcon;
    private readonly DesktopHistoricalGracefulStopBridge _historicalStop;
    private readonly DesktopHistoricalRestartBridge _historicalRestart;
    private readonly DesktopUpdateStatusBridge _updates = new();
    private readonly DesktopObsidianUpdateInstallBridge _updateInstall;
    private readonly DesktopRconReadOnlyBridge _rcon;
    private readonly DesktopRconAdminBridge _rconAdmin;
    private readonly DesktopRconConfigurationBridge _rconConfig;
    private readonly DesktopStoppedSteamUpdateBridge _steamUpdate;
    private readonly DesktopStoppedSteamBatchBridge _steamBatch;
    private readonly DesktopRollingSteamSupervisor _rollingSteam;
    private readonly DesktopScheduledStoppedSteamSupervisor _scheduledSteam;
    private readonly DesktopScheduledStoppedSteamBridge _scheduledSteamBridge;
    private readonly DesktopFreshAsaStartBridge _freshStart;
    private readonly DesktopFreshAsaStopBridge _freshStop;
    private readonly DesktopOwnedAsaKillBridge _ownedKill;
    private readonly DesktopFreshAsaRestartBridge _freshRestart;
    private readonly DesktopAsaLifecycleJournalBridge _lifecycleJournal;
    private readonly DesktopRestartReconciliationSupervisor _restartReconciliation;
    private readonly DesktopOperationsCenterBridge _operations;
    private readonly DesktopDiscordNotifications _notifications;
    private readonly DesktopDiscordControlApi _discordControl;
    private readonly DesktopWatchdogRecovery _recovery;
    private readonly DesktopBanManager _moderation;
    private readonly DesktopLifecycleBatchBridge _lifecycleBatch;
    private readonly DesktopRecurringReadOnlySupervisor _readOnlySupervisor;
    private readonly DesktopAutomaticAsaDiscoverySupervisor _asaDiscovery;
    private readonly DesktopHistoricalLiveModSync _liveMods;
    private readonly DesktopLanSettingsBridge _lan;
    private readonly DesktopStoreTravelEvidenceBridge _travel;
    private readonly DesktopEmbeddedStoreBridge _embeddedStore;
    private readonly DesktopWorldBackupBridge _worldBackup;
    private readonly DesktopScheduledStoppedBackupSupervisor _scheduledBackups;
    private readonly DesktopScheduledAsaOperationsSupervisor _nativeSchedules;
    private readonly DesktopMapMaintenanceBridge _mapMaintenance;
    private readonly SemaphoreSlim _bridgeGate = new(1, 1);
    private bool _updateHandoff;
    private bool _reallyExit;
    private readonly bool _smokeTest;
    private readonly bool _suppressScheduledMutations;
    private readonly string? _smokeRoot;
    private bool _smokeCompleted;

    public ObsidianWindow(bool smokeTest = false,string? isolatedProfileRoot = null,
        bool suppressScheduledMutations = false)
    {
        _smokeTest = smokeTest;
        _suppressScheduledMutations = suppressScheduledMutations;
        if(smokeTest)
        {
            _smokeRoot=string.IsNullOrWhiteSpace(isolatedProfileRoot)
                ?Path.Combine(Path.GetTempPath(),"Untamed-Obsidian-Smoke-"+
                    Guid.NewGuid().ToString("N"))
                :Path.GetFullPath(isolatedProfileRoot);
            Directory.CreateDirectory(_smokeRoot);
            _bridge=new DesktopReadOnlyBridge(Path.Combine(_smokeRoot,"profile"));
        }
        else _bridge=new DesktopReadOnlyBridge();
        _credentials = new DesktopCredentialBridge(_bridge.LocalCluster);
        _updateInstall = new DesktopObsidianUpdateInstallBridge(_bridge.LocalCluster);
        _modDrafts = new DesktopModDraftBridge(_bridge.LocalCluster);
        _modRepair = new DesktopModRepairBridge(_bridge.LocalCluster);
        _relocation = new DesktopInstallationRelocationBridge(_bridge.LocalCluster);
        _modCopy = new DesktopModCopyBridge(_bridge.LocalCluster);
        _modProfiles = new DesktopModProfileBridge(_bridge.LocalCluster);
        _offlineClusterArchive = new DesktopOfflineAsaArchiveBridge();
        _offlineRestoreLab = new DesktopOfflineAsaRestoreBridge();
        _multiHostBackup = new DesktopMultiHostClusterBackupBridge(_bridge.LocalCluster);
        _pluginState = new DesktopPluginStateBridge(_bridge.LocalCluster);
        _pluginJson = new DesktopPluginJsonBridge(_bridge.LocalCluster);
        _pluginZip = new DesktopPluginZipBridge(_bridge.LocalCluster);
        _apiCore = new DesktopAsaApiCoreBridge(_bridge.LocalCluster);
        _iniEditor = new DesktopIniBridge(_bridge.LocalCluster);
        _hostedResources = new DesktopHostedResourcesBridge(profileRoot:smokeTest||suppressScheduledMutations?null:_bridge.LocalCluster.Store.Root);
        _mapRegistry = new DesktopLocalMapRegistryBridge(_bridge.LocalCluster);
        _runningAsaRegistration = new DesktopRunningAsaRegistrationBridge(_bridge.LocalCluster);
        _runningAsaAdoption = new DesktopRunningAsaAdoptionBridge(_bridge.LocalCluster);
        _clusterRegistry = new DesktopClusterRegistryBridge(_bridge.LocalCluster);
        _modCatalog = new DesktopCurseForgeDiscoveryBridge(_bridge.LocalCluster);
        _freshInstall = new DesktopSteamFreshInstallBridge(_bridge.LocalCluster);
        _initialMapSetup = new DesktopInitialMapSetupBridge(_bridge.LocalCluster);
        _freshStoreEnrollment = new DesktopFreshStoreEnrollmentBridge(_bridge.LocalCluster);
        _freshStoreLoaderMode = new DesktopFreshStoreLoaderModeBridge(_bridge.LocalCluster);
        _localAsaApiMode = new DesktopLocalAsaApiModeBridge(_bridge.LocalCluster);
        _historicalStore = new DesktopHistoricalStoreImportBridge(_bridge.LocalCluster);
        _historicalRcon = new DesktopHistoricalRconAttachBridge(_bridge.LocalCluster);
        _historicalStart = new DesktopHistoricalActiveStartBridge(_bridge.LocalCluster);
        _historicalReadOnlyRcon = new DesktopHistoricalRconReadOnlyBridge(_bridge.LocalCluster);
        _historicalAdminRcon = new DesktopHistoricalRconAdminBridge(_bridge.LocalCluster);
        _historicalStop = new DesktopHistoricalGracefulStopBridge(_bridge.LocalCluster);
        _historicalRestart = new DesktopHistoricalRestartBridge(_bridge.LocalCluster);
        _rcon = new DesktopRconReadOnlyBridge(_bridge.LocalCluster);
        _playerActivity=new DesktopPlayerActivityBridge(smokeTest||suppressScheduledMutations?null:_bridge.LocalCluster.Store.Root,_rcon.HandleAsync,_historicalReadOnlyRcon.HandleAsync);
        _playerActivityTimer.Tick+=async (_,_)=>{
            if(_updateHandoff||_suppressScheduledMutations){_playerActivity.BreakContinuity();return;}
            try{
                var maps=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(_bridge.LocalCluster.Config.LocalMaps,m=>!m.ObservedReadOnlyImported&&!m.Maintenance&&m.RconPort>0&&!string.IsNullOrWhiteSpace(m.RconPasswordProtected)),m=>(m.Id,m.HistoricalStoreProtected)));
                await _playerActivity.PollAsync(maps);
            }catch{_playerActivity.BreakContinuity();}
        };
        _rconAdmin = new DesktopRconAdminBridge(_bridge.LocalCluster);
        _rconConfig = new DesktopRconConfigurationBridge(_bridge.LocalCluster);
        _steamUpdate = new DesktopStoppedSteamUpdateBridge(_bridge.LocalCluster);
        _steamBatch = new DesktopStoppedSteamBatchBridge(_bridge.LocalCluster);
        _scheduledSteam = new DesktopScheduledStoppedSteamSupervisor(_bridge.LocalCluster);
        _scheduledSteamBridge = new DesktopScheduledStoppedSteamBridge(_bridge.LocalCluster,_scheduledSteam);
        _freshStart = new DesktopFreshAsaStartBridge(_bridge.LocalCluster);
        _freshStop = new DesktopFreshAsaStopBridge(_bridge.LocalCluster);
        _ownedKill = new DesktopOwnedAsaKillBridge(_bridge.LocalCluster);
        _freshRestart = new DesktopFreshAsaRestartBridge(_bridge.LocalCluster);
        _nativeSchedules = new DesktopScheduledAsaOperationsSupervisor(
            _bridge.LocalCluster,_rcon,_freshRestart,
            historicalStop:_historicalStop,historicalRestart:_historicalRestart,historicalAdmin:_historicalAdminRcon);
        _mapMaintenance = new DesktopMapMaintenanceBridge(_bridge.LocalCluster);
        _lifecycleJournal = new DesktopAsaLifecycleJournalBridge(_bridge.LocalCluster);
        _restartReconciliation=new DesktopRestartReconciliationSupervisor(_bridge.LocalCluster,_bridgeGate,
            TryEnterWatchdogLane,ReleaseRecoveryLane,()=>_updateHandoff||_suppressScheduledMutations,
            ReconcileLifecycleMap);
        _asaDiscovery = new DesktopAutomaticAsaDiscoverySupervisor(_bridge.LocalCluster);
        _bridge.AttachAutomaticAsaDiscovery(_asaDiscovery);
        _readOnlySupervisor = new DesktopRecurringReadOnlySupervisor(_bridge.LocalCluster);
        _operations = new DesktopOperationsCenterBridge(_bridge.LocalCluster,_readOnlySupervisor);
        _notifications = new DesktopDiscordNotifications(_bridge.LocalCluster);
        _moderation = new DesktopBanManager(_bridge.LocalCluster,_rconAdmin.HandleAsync,_historicalAdminRcon.HandleAsync);
        _lifecycleBatch = new DesktopLifecycleBatchBridge(_bridge.LocalCluster,
            _freshStart.HandleAsync,_freshStop.HandleAsync,_freshRestart.HandleAsync,
            _historicalStart.HandleAsync,_historicalStop.HandleAsync,_historicalRestart.HandleAsync,
            _historicalReadOnlyRcon.HandleAsync);
        _lan = new DesktopLanSettingsBridge(_bridge.LocalCluster);
        _travel = new DesktopStoreTravelEvidenceBridge(_bridge.LocalCluster);
        _embeddedStore = new DesktopEmbeddedStoreBridge();
        _scheduledBackups = new DesktopScheduledStoppedBackupSupervisor(_bridge.LocalCluster);
        _worldBackup = new DesktopWorldBackupBridge(_bridge.LocalCluster,
            scheduledBackups:_scheduledBackups);
        _recovery = new DesktopWatchdogRecovery(_bridge.LocalCluster,_bridgeGate,
            ()=>false,ReleaseDiscordLane,()=>_updateHandoff||_suppressScheduledMutations,
            _freshStart.HandleAsync,_historicalStart.HandleAsync,_historicalReadOnlyRcon.HandleAsync,
            mapMaintenanceLane:TryEnterMapRuntimeLane);
        _nativeSchedules.AutomaticRecoveryPending=_recovery.HasPendingRecovery;
        _rollingSteam=new DesktopRollingSteamSupervisor(_bridge.LocalCluster,_lifecycleBatch,_steamUpdate,_bridgeGate,
            ()=>_recovery.CanManagerUpdate()&&TryEnterWatchdogLane(),ReleaseRecoveryLane,()=>_updateHandoff||_suppressScheduledMutations);
        _discordControl = new DesktopDiscordControlApi(_bridge.LocalCluster,_bridgeGate,()=>_updateHandoff,
            TryEnterRecoveryLane,ReleaseRecoveryLane,DispatchDiscordNative,_asaDiscovery.Snapshot,_nativeSchedules.LegacyOperationHistory,
            TryEnterDiscordLane,ReleaseDiscordLane);
        _liveMods=new DesktopHistoricalLiveModSync(_bridge.LocalCluster,_bridgeGate,
            ()=>_updateHandoff,TryEnterRecoveryLane,ReleaseRecoveryLane);
        Text = suppressScheduledMutations&&!smokeTest
            ? "Untamed Kingdom — Obsidian Command [MANUÁLIS TESZT]"
            : "Untamed Kingdom — Obsidian Command";
        Width = 1510;
        Height = 880;
        MinimumSize = new Size(940, 620);
        BackColor = Color.FromArgb(12, 20, 24);
        StartPosition = FormStartPosition.CenterScreen;
        // Match the approved full-screen dashboard on 1366p, 1600p and
        // 1920p monitors. Keep the isolated CI capture at fixed dimensions.
        WindowState = smokeTest ? FormWindowState.Normal : FormWindowState.Maximized;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        Controls.Add(_browser);

        _tray.Icon = Icon;
        _tray.Text = suppressScheduledMutations&&!smokeTest
            ? "Obsidian Command — MANUÁLIS TESZT"
            : "Untamed Kingdom — Obsidian Command";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Megnyitás", null, (_, _) => RestoreWindow());
        menu.Items.Add("Elrejtés a tálcára",null,(_,_)=>{
            Hide();ShowInTaskbar=false;
        });
        var trayStatus=new ToolStripMenuItem("ASA-folyamatok: még nincs mérés"){Enabled=false};
        menu.Items.Add(trayStatus);
        // Reuse the existing WMI discovery snapshot; never trigger a new
        // probe, or count a Loader as a separate running game server.
        menu.Opening+=(_,_)=>{
            var seen=_asaDiscovery.Snapshot();
            trayStatus.Text=DesktopTrayStatus.Describe(seen);
        };
        if(!smokeTest&&!suppressScheduledMutations)
        {
            menu.Items.Add(new ToolStripSeparator());
            var login=new ToolStripMenuItem("Windowszal együtt indul (normál mód)")
                {CheckOnClick=false};
            void UpdateLogin()
            {
                try
                {
                    var state=DesktopLoginStartup.Read();
                    login.Checked=state==DesktopLoginStartup.State.Enabled;
                    login.Enabled=state!=DesktopLoginStartup.State.OtherInstallation;
                    login.Text=state==DesktopLoginStartup.State.OtherInstallation?
                        "Automatikus indulás: másik telepítéshez tartozik":
                        "Windowszal együtt indul (normál mód)";
                }
                catch
                {
                    login.Enabled=false;login.Checked=false;
                    login.Text="Automatikus indulás: nem ellenőrizhető";
                }
            }
            menu.Opening+=(_,_)=>UpdateLogin();
            login.Click+=(_,_)=>{
                try
                {
                    // Explicit user toggle. No game-process command or
                    // scheduler settings write is performed here.
                    DesktopLoginStartup.Set(!login.Checked);
                }
                catch
                {
                    MessageBox.Show(this,"A Windows indítási beállítás nem módosítható. "+
                        "Ellenőrizd az Indítópult beállításait és a telepítés helyét.",
                        "Obsidian — UM-LOGIN-STARTUP",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                }
                UpdateLogin();
            };
            menu.Items.Add(login);
        }
        if(!smokeTest)
            menu.Items.Add("Discord bot",null,(_,_)=>{
                RestoreWindow();
                DesktopDiscordBotRuntime.Show(this,()=>!_suppressScheduledMutations&&!_updateHandoff);
            });
        menu.Items.Add("Kilépés a Managerből", null, (_, _) =>
        {
            _reallyExit = true;
            Close();
        });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow();

        Shown += async (_, _) => {
            // Once per Manager start, enable only the explicitly configured
            // authenticated read-only LAN listener. Config changes require
            // Manager restart, never an ASA server restart.
            if (!_smokeTest)
            {
                // Restarts only the Manager-side read-only schedule loop;
                // never starts/stops any ARK server or touches shared Store.
                _asaDiscovery.Start();
                _readOnlySupervisor.Start();
                // Local profile metadata only, never a scheduled ASA
                // command. Leave smoke fixtures and update handoff untouched.
                if(!_smokeTest)_liveMods.Start();
                // Installation acceptance never silently executes a
                // previously configured ASA Restart/SaveWorld or backup.
                // Explicit native manual commands remain fully available.
                if(!_suppressScheduledMutations)
                {
                    _playerActivityTimer.Start();
                    _restartReconciliation.Start();
                    _scheduledBackups.Start();
                    _nativeSchedules.Start();
                    _notifications.Start();
                    _recovery.Start();
                    _discordControl.Start();
                    _rollingSteam.Start();
                }
                _scheduledSteam.Start();
                try { await _bridge.LocalCluster.BeginLanAsync(); }
                catch (Exception) { /* UI remains usable; peer status stays unknown */ }
            }
            await InitializeBrowserAsync();
        };
        FormClosing += (_, e) =>
        {
            if(_rollingSteam.Busy&&e.CloseReason==CloseReason.UserClosing&&_reallyExit)
            {
                _reallyExit=false;
                e.Cancel=true;
                MessageBox.Show(this,"A SteamCMD művelet még fut. A Manager a háttérben tovább működik.",
                    "Obsidian Command",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return;
            }
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
            }
        };
        FormClosed += (_, _) =>
        {
            _playerActivityTimer.Stop();_playerActivityTimer.Dispose();_playerActivity.Dispose();
            _restartReconciliation.Dispose();
            _liveMods.Dispose();
            _discordControl.Dispose();
            _asaDiscovery.Dispose();
            _readOnlySupervisor.Dispose();
            _scheduledBackups.Dispose();
            _rollingSteam.Dispose();
            _scheduledSteam.Dispose();
            _nativeSchedules.Dispose();
            _lifecycleBatch.Dispose();
            _recovery.Dispose();
            _notifications.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            menu.Dispose();
            _browser.Dispose();
            _localAsaApiMode.Dispose();
            _historicalStore.Dispose();
            _historicalRcon.Dispose();
            _historicalStart.Dispose();
            _historicalReadOnlyRcon.Dispose();
            _historicalAdminRcon.Dispose();
            _historicalStop.Dispose();
            _historicalRestart.Dispose();
            _embeddedStore.Dispose();
            _rconConfig.Dispose();
            _steamUpdate.Dispose();
            _steamBatch.Dispose();
            _freshStart.Dispose();
            _freshStop.Dispose();
            _freshRestart.Dispose();
            _initialMapSetup.Dispose();
            _freshStoreEnrollment.Dispose();
            _freshStoreLoaderMode.Dispose();
            _freshInstall.Dispose();
            _modCatalog.Dispose();
            _mapRegistry.Dispose();
            _runningAsaRegistration.Dispose();
            _runningAsaAdoption.Dispose();
            _clusterRegistry.Dispose();
            _hostedResources.Dispose();
            _iniEditor.Dispose();
            _modDrafts.Dispose();
            _modCopy.Dispose();
            _modProfiles.Dispose();
            _offlineClusterArchive.Dispose();
            _offlineRestoreLab.Dispose();
            _multiHostBackup.Dispose();
            _pluginState.Dispose();
            _pluginJson.Dispose();
            _pluginZip.Dispose();
            _apiCore.Dispose();
            _credentials.Dispose();
            _ = _bridge.DisposeAsync();
        };
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
    }

    private async System.Threading.Tasks.Task InitializeBrowserAsync()
    {
        try
        {
            // Published JS/CSS is local and can load without LAN or public internet.
            string assets = Path.Combine(AppContext.BaseDirectory, "ui");
            if (!File.Exists(Path.Combine(assets, "index.html")))
                throw new InvalidOperationException("Missing packaged React UI (ui/index.html). Run npm build first.");

            string data = _smokeTest
                ?Path.Combine(_smokeRoot!,"webview")
                :Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "UntamedManager","ObsidianWebView2");
            Directory.CreateDirectory(data);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: data);
            await _browser.EnsureCoreWebView2Async(environment);

            var core = _browser.CoreWebView2;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;

            core.SetVirtualHostNameToFolderMapping("untamed-app.example", assets,
                CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += (_, e) =>
            {
                // No remote pages are allowed in the privileged application WebView.
                if (!IsAppUrl(e.Uri)) e.Cancel = true;
            };
            core.FrameNavigationStarting += (_, e) =>
            {
                // Third-party content cannot enter the trusted origin through frames.
                if (!IsAppUrl(e.Uri)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += async (_, e) =>
            {
                if (!IsAppUrl(e.Source)) return;
                string incoming;
                try { incoming = e.WebMessageAsJson; }
                catch (Exception) { return; }
                if (_smokeTest)
                {
                    // Packaged UI smoke tests use a deterministic renderer
                    // fixture. Do not let smoke mode inspect host resources,
                    // profile files, LAN peers, ASA processes or any mutation
                    // bridge, even if the page requests one.
                    string smokeResponse = SyntheticSmokeBridgeResponse(incoming);
                    if (!_browser.IsDisposed && _browser.CoreWebView2 is not null)
                        _browser.CoreWebView2.PostWebMessageAsJson(smokeResponse);
                    return;
                }
                try
                {
                string? methodName = null;
                bool credentialRoute = false;
                bool resourcesRoute = false;
                bool activityRoute = false;
                bool draftRoute = false;
                bool modRepairRoute = false;
                bool relocationRoute = false;
                bool modCopyRoute = false;
                bool modProfileRoute = false;
                bool offlineClusterArchiveRoute = false;
                bool offlineRestoreLabRoute = false;
                bool multiHostBackupRoute = false;
                bool iniRoute = false;
                bool registryRoute = false;
                bool runningAsaRegistrationRoute = false;
                bool runningAsaAdoptionRoute = false;
                bool clusterRegistryRoute = false;
                bool discoveryRoute = false;
                bool freshRoute = false;
                bool initialRoute = false;
                bool freshStoreEnrollmentRoute = false;
                bool freshStoreLoaderModeRoute = false;
                bool localAsaApiModeRoute = false;
                bool historicalRconRoute = false;
                bool historicalStartRoute = false;
                bool historicalReadOnlyRconRoute = false;
                bool historicalAdminRconRoute = false;
                bool historicalStopRoute = false;
                bool historicalRestartRoute = false;
                bool pluginStateRoute = false;
                bool pluginZipRoute = false;
                bool pluginJsonRoute = false;
                bool apiCoreRoute = false;
                bool updateRoute = false;
                bool updateInstallRoute = false;
                bool rconRoute = false;
                bool rconAdminRoute = false;
                bool rconConfigRoute = false;
                bool travelRoute = false;
                bool embeddedStoreRoute = false;
                bool worldBackupRoute = false;
                bool stoppedSteamRoute = false;
                bool stoppedSteamBatchRoute = false;
                bool scheduledSteamRoute = false;
                bool rollingSteamRoute = false;
                bool freshStartRoute = false;
                bool freshStopRoute = false;
                bool ownedKillRoute = false;
                bool freshRestartRoute = false;
                bool lifecycleJournalRoute = false;
                bool operationsRoute = false;
                bool notificationsRoute = false;
                bool recoveryRoute = false;
                bool moderationRoute = false;
                bool lifecycleBatchRoute = false;
                bool nativeScheduleRoute = false;
                bool mapMaintenanceRoute = false;
                bool lanRoute = false;
                try
                {
                    using var parsed = JsonDocument.Parse(incoming);
                    if (parsed.RootElement.TryGetProperty("method", out var method) &&
                        method.ValueKind == JsonValueKind.String)
                    {
                        string? name = method.GetString();
                        methodName = name;
                        credentialRoute = name is
                            "curseforge.credentialStatus" or
                            "curseforge.credentialSave" or
                            "curseforge.credentialClear";
                        resourcesRoute = name is "resources.status" or "resources.publish" or "resources.stop";
                        activityRoute = name is "activity.status" or "activity.setEnabled" or "activity.clear";
                        updateRoute = name is "updates.status" or "updates.check";
                        updateInstallRoute = name is "updates.prepare" or "updates.install";
                        rconRoute = name is "rcon.status" or "rcon.listPlayers" or
                            "rcon.gameLog" or "rcon.chatLog" or "rcon.saveWorld" or
                            "rcon.saveStatus";
                        rconAdminRoute = name is "rcon.adminPreview" or "rcon.adminSend";
                        rconConfigRoute = name is "rcon.configStatus" or "rcon.configPreview" or "rcon.configApply";
                        freshStartRoute = name is "servers.freshStartPreview" or "servers.freshStartApply" or
                            "servers.freshStartStatus" or "servers.freshReadiness";
                        freshStopRoute = name is "servers.gracefulStopPreview" or "servers.gracefulStopApply" or
                            "servers.gracefulStopStatus" or "servers.gracefulStopCurrent" or
                            "servers.gracefulStopReview";
                        ownedKillRoute = name is "servers.ownedKillPreview" or "servers.ownedKillApply";
                        freshRestartRoute = name is "servers.restartPreview" or "servers.restartApply" or
                            "servers.restartStatus" or "servers.restartCurrent" or
                            "servers.restartReviewPreview" or "servers.restartReviewApply";
                        lifecycleJournalRoute = name is "servers.lifecycleJournal" or
                            "servers.lifecycleRecoveryPreview" or
                            "servers.lifecycleRecoveryApply";
                        moderationRoute = name is "moderation.history" or "moderation.preview" or "moderation.apply";
                        recoveryRoute = name is "recovery.status" or "recovery.arm" or "recovery.disarm" or "recovery.suspend";
                        notificationsRoute = name is "notifications.status" or "notifications.save" or "notifications.clear" or "notifications.test";
                        operationsRoute = name is "operations.snapshot" or "operations.map" or
                            "operations.scheduleCreate" or "operations.scheduleDisable" or "operations.watchdogStart";
                        lifecycleBatchRoute = name is "operations.lifecycleBatchPreview" or
                            "operations.lifecycleBatchApply" or "operations.lifecycleBatchCurrent" or "operations.lifecycleBatchReview";
                        nativeScheduleRoute = name is "schedule.nativeStatus" or
                            "schedule.nativeSet" or "schedule.nativeDisable" or
                            "schedule.nativeOverview" or "schedule.nativeReviewPreview" or
                            "schedule.nativeReviewApply" or "schedule.nativeResume";
                        mapMaintenanceRoute = name is "maps.maintenanceStatus" or "maps.maintenanceSet";
                        lanRoute = name is "lan.status" or "lan.preview" or "lan.apply" or "lan.physicalStoreProbe" or "lan.historicalSaveProbe";
                        stoppedSteamRoute = name is "steam.stoppedDiagnostics" or "steam.stoppedPreview" or "steam.stoppedApply" or "steam.stoppedStart" or
                            "steam.stoppedStatus" or "steam.stoppedCurrent" or "steam.publishedBuild" or "steam.selectExecutable" or "steam.prepareExecutable" or
                            "steam.discoverExecutables" or "steam.acceptDiscoveredExecutable";
                        stoppedSteamBatchRoute = name is "steam.batchPreview" or "steam.batchApply" or "steam.batchStart" or
                            "steam.batchStatus" or "steam.batchCurrent";
                        rollingSteamRoute = name is "steam.rollingStatus" or "steam.rollingSet" or "steam.rollingReview";
                        scheduledSteamRoute = name is "steam.scheduleStatus" or "steam.scheduleSet" or
                            "steam.scheduleDisable";
                        travelRoute = name is "travel.capture" or "travel.compare" or "travel.compareRoundTrip";
                        embeddedStoreRoute = name is "store.inspectCopy" or "store.inspectArchive" or "store.compareArchives";
                        worldBackupRoute = name is "backup.history" or "backup.preview" or
                            "backup.create" or "backup.restorePreview" or "backup.restore" or "backup.worldStage" or
                            "backup.fullLocalCreate" or "backup.fullLocalHistory" or "backup.fullLocalStage" or
                "backup.retentionPreview" or "backup.retentionProtect" or "backup.retentionQuarantine" or "backup.retentionReturn" or
                            "backup.scheduleStatus" or "backup.scheduleCreate" or
                            "backup.scheduleDisable" or "backup.originalBatchPreview" or
                            "backup.originalBatchCreate" or "backup.originalBatchStatus" or
                            "backup.originalBatchJournalRepairPreview" or
                            "backup.originalBatchJournalRepairApply";
                        pluginStateRoute = name is
                            "plugins.state" or "plugins.openFolder" or
                            "plugins.togglePreview" or "plugins.toggleApply";
                        pluginZipRoute = name is "plugins.zipSelect" or "plugins.zipApply" or
                            "plugins.zipRollbackSelect" or "plugins.zipRollbackApply";
                        pluginJsonRoute = name is "plugins.jsonOpen" or "plugins.jsonPreview" or
                            "plugins.jsonSave" or "plugins.jsonClose";
                        apiCoreRoute = name is "api.coreRelease" or "api.corePreview" or "api.coreApply" or
                            "api.coreRollbackCandidates" or "api.coreRollbackPreview" or "api.coreRollbackApply";
                        modCopyRoute = name is "mods.copyPreview" or "mods.copyApply";
                        modProfileRoute = name is "mods.profileExport" or
                            "mods.profileImportPreview" or "mods.profileImportApply";
                        offlineClusterArchiveRoute = name is "cluster.offlinePreview" or
                            "cluster.offlineApply" or "cluster.offlineStatus";
                        offlineRestoreLabRoute = name is "cluster.restoreLabPreview" or
                            "cluster.restoreLabApply" or "cluster.restoreLabStatus";
                        multiHostBackupRoute = name is "cluster.multiHostCoordinatedPreview" or "cluster.multiHostCoordinatedApply" or
                            "cluster.multiHostHostStagePreview" or
                            "cluster.multiHostHostStageApply" or
                            "cluster.multiHostHostStageStatus" or
                            "cluster.multiHostBackupRecoveryPreview" or
                            "cluster.multiHostBackupRecoveryApply" or
                            "cluster.multiHostCatalogPreview" or
                            "cluster.multiHostCatalogApply" or
                            "cluster.multiHostBackupPreview" or
                            "cluster.multiHostBackupApply" or "cluster.multiHostBackupStatus" or
                            "cluster.multiHostRestorePreview" or "cluster.multiHostRestoreApply" or
                            "cluster.multiHostRestoreReplacePreview" or "cluster.multiHostRestoreReplaceApply" or
                            "cluster.multiHostRestoreRecoveryList" or
                            "cluster.multiHostRestoreRecoveryPreview" or "cluster.multiHostRestoreRecoveryApply" or
                            "cluster.multiHostRestoreStatus" or "cluster.multiHostCancel";
                        relocationRoute = name is "servers.relocationPreview" or "servers.relocationApply";
                        modRepairRoute = name is "mods.localRepairPreview" or "mods.localRepairApply" or "mods.localRepairStatus";
                        draftRoute = name is
                            "mods.draftStart" or "mods.draftStage" or
                            "mods.draftUndo" or "mods.draftRedo" or
                            "mods.draftDiscard" or "mods.draftSave";
                        iniRoute = name is "ini.openDraft" or "ini.stageDraft" or "ini.pendingStatus" or "ini.pendingDiscard" or
                            "ini.open" or "ini.preview" or
                            "ini.rebasePreview" or "ini.save" or "ini.close" or
                            "ini.createGamePreview" or "ini.createGameApply";
                        registryRoute = name is "servers.inspectLocal" or
                            "servers.importLocal";
                        runningAsaRegistrationRoute = name is "servers.runningPreview" or
                            "servers.runningRegister" or
                            "servers.runningBatchPreview" or "servers.runningBatchRegister";
                        runningAsaAdoptionRoute = name is "servers.runningAdoptPreview" or
                            "servers.runningAdoptApply";
                        clusterRegistryRoute = name is "cluster.registry.snapshot" or
                            "cluster.host.register" or "cluster.map.register" or
                            "cluster.create" or "cluster.update" or
                            "cluster.membership.preview" or "cluster.membership.apply" or
                            "cluster.historicalExpansionInspect";
                        discoveryRoute = name is "curseforge.categories" or
                            "curseforge.featured" or "curseforge.browse" or
                            "curseforge.project" or "curseforge.files" or "curseforge.summary";
                        freshRoute = name is "steam.freshPreflight" or "steam.freshStart" or
                            "steam.freshStatus" or "steam.freshCurrent" or
                            "steam.freshCancel" or "steam.freshDiscard" or
                            "steam.freshRegister" or "steam.freshBrowse" or
                            "steam.freshRecoverPreview" or "steam.freshRecoverRegister";
                        initialRoute = name is "servers.initialStatus" or
                            "servers.initialPreview" or "servers.initialApply";
                        freshStoreEnrollmentRoute = name is "freshStore.enrollmentStatus" or
                            "freshStore.enrollmentPreview" or "freshStore.enrollmentApply";
                        freshStoreLoaderModeRoute = name is "freshStore.loaderModeStatus" or
                            "freshStore.loaderModePreview" or "freshStore.loaderModeApply";
                        localAsaApiModeRoute = name is "servers.apiModeStatus" or
                            "servers.apiModePreview" or "servers.apiModeApply";
                        historicalRestartRoute = name is "historical.restartPreview" or
                            "historical.restartApply" or "historical.restartStatus" or
                            "historical.restartRecovery";
                        historicalStopRoute = name is "historical.gracefulStopPreview" or
                            "historical.gracefulStopApply" or "historical.gracefulStopStatus" or
                            "historical.gracefulStopRecovery" or "historical.saveWorldPreview" or
                            "historical.saveWorldApply" or "historical.saveWorldStatus" or
                            "historical.saveWorldEvidence" or "historical.saveWorldReviewPreview" or
                            "historical.saveWorldReviewApply" or
                            "historical.saveWorldForensicExport" or
                            "historical.saveWorldLateEvidence";
                        historicalAdminRconRoute = name is "historical.adminPreview" or
                            "historical.adminSend";
                        historicalReadOnlyRconRoute = name is "historical.rconLiveStatus" or
                            "historical.rconPlayers" or "historical.rconGameLog" or
                            "historical.rconChatLog";
                        historicalStartRoute = name is "historical.activeStartStatus" or
                            "historical.activeStartPreview" or "historical.activeStartApply" or
                            "historical.loaderModeStatus" or "historical.loaderModePreview" or
                            "historical.loaderModeApply";
                        historicalRconRoute = name is "historical.rconStatus" or
                            "historical.rconPreview" or
                            "historical.rconExistingPreview" or "historical.rconApply";
                    }
                }
                catch (JsonException) { /* read-only bridge will safely reject invalid messages */ }
                bool requireGlobalGate = RequiresGlobalBridgeGate(methodName);
                if(requireGlobalGate)
                {
                    // A long maintenance cycle must not leave a UI mutation
                    // queued invisibly for hours, then dispatch it afterwards.
                    if(!await _bridgeGate.WaitAsync(TimeSpan.FromSeconds(2)))
                    {
                        if(!_browser.IsDisposed&&_browser.CoreWebView2!=null)
                            _browser.CoreWebView2.PostWebMessageAsJson(NativeErrorResponse(incoming,
                                "UM-HOST-MUTATION-BUSY"));
                        return;
                    }
                }
                try
                {
                    using var operationOrigin=DiscordOperationEvents.EnterOrigin("manager");
                    if(requireGlobalGate)
                        await ReconcileBeforeLifecyclePreviewAsync(methodName,incoming);
                    string response = _updateHandoff&&requireGlobalGate
                        ? NativeErrorResponse(incoming,"UM-UPDATE-MANAGER-HANDOFF")
                        : IsManualOnlyDeniedPluginMethod(methodName)
                        ? NativeErrorResponse(incoming,"UM-PLUGIN-MANUAL-FOLDER-ONLY")
                        : rollingSteamRoute ? await _rollingSteam.HandleAsync(incoming)
                        : apiCoreRoute ? await _apiCore.HandleAsync(incoming)
                        : lanRoute ? await _lan.HandleAsync(incoming)
                        : nativeScheduleRoute ? await _nativeSchedules.HandleAsync(incoming)
                        : mapMaintenanceRoute ? await _mapMaintenance.HandleAsync(incoming)
                        : lifecycleBatchRoute ? await _lifecycleBatch.HandleAsync(incoming)
                        : moderationRoute ? await _moderation.HandleAsync(incoming)
                        : recoveryRoute ? await _recovery.HandleAsync(incoming)
                        : notificationsRoute ? await _notifications.HandleAsync(incoming)
                        : operationsRoute ? await _operations.HandleAsync(incoming)
                        : freshRestartRoute ? await _freshRestart.HandleAsync(incoming)
                        : lifecycleJournalRoute ? await _lifecycleJournal.HandleAsync(incoming)
                        : ownedKillRoute ? await _ownedKill.HandleAsync(incoming)
                        : freshStopRoute ? await _freshStop.HandleAsync(incoming)
                        : freshStartRoute ? await _freshStart.HandleAsync(incoming)
                        : pluginJsonRoute ? await _pluginJson.HandleAsync(incoming)
                        : pluginZipRoute ? await _pluginZip.HandleAsync(incoming)
                        : scheduledSteamRoute ? await _scheduledSteamBridge.HandleAsync(incoming)
                        : stoppedSteamBatchRoute ? await _steamBatch.HandleAsync(incoming)
                        : stoppedSteamRoute ? await _steamUpdate.HandleAsync(incoming)
                        : rconConfigRoute ? await _rconConfig.HandleAsync(incoming)
                        : rconAdminRoute ? await _rconAdmin.HandleAsync(incoming)
                        : worldBackupRoute ? await _worldBackup.HandleAsync(incoming)
                        : updateInstallRoute ? (methodName=="updates.install"?await InstallManagerWithFrozenSchedulesAsync(incoming):await _updateInstall.HandleAsync(incoming))
                        : embeddedStoreRoute ? await _embeddedStore.HandleAsync(incoming)
                        : travelRoute ? await _travel.HandleAsync(incoming)
                        : rconRoute ? await _rcon.HandleAsync(incoming)
                        : updateRoute ? await _updates.HandleAsync(incoming)
                        : activityRoute ? await _playerActivity.HandleAsync(incoming)
                        : resourcesRoute ? await _hostedResources.HandleAsync(incoming)
                        : credentialRoute ? await _credentials.HandleAsync(incoming)
                        : pluginStateRoute ? await _pluginState.HandleAsync(incoming)
                        : multiHostBackupRoute ? await _multiHostBackup.HandleAsync(incoming)
                        : offlineRestoreLabRoute ? await _offlineRestoreLab.HandleAsync(incoming)
                        : offlineClusterArchiveRoute ? await _offlineClusterArchive.HandleAsync(incoming)
                        : modProfileRoute ? await _modProfiles.HandleAsync(incoming)
                        : modCopyRoute ? await _modCopy.HandleAsync(incoming)
                        : relocationRoute ? await _relocation.HandleAsync(incoming)
                        : modRepairRoute ? await _modRepair.HandleAsync(incoming)
                        : draftRoute ? await _modDrafts.HandleAsync(incoming)
                            : iniRoute ? await _iniEditor.HandleAsync(incoming)
                                : clusterRegistryRoute ? await _clusterRegistry.HandleAsync(incoming)
                                    : runningAsaAdoptionRoute ? await _runningAsaAdoption.HandleAsync(incoming)
                                    : runningAsaRegistrationRoute ? await _runningAsaRegistration.HandleAsync(incoming)
                                    : registryRoute ? await _mapRegistry.HandleAsync(incoming)
                                    : discoveryRoute ? await _modCatalog.HandleAsync(incoming)
                                        : freshRoute ? await _freshInstall.HandleAsync(incoming)
                                            : freshStoreEnrollmentRoute ? await _freshStoreEnrollment.HandleAsync(incoming)
                                            : localAsaApiModeRoute ? await _localAsaApiMode.HandleAsync(incoming)
                        : freshStoreLoaderModeRoute ? await _freshStoreLoaderMode.HandleAsync(incoming)
                                            : initialRoute ? await _initialMapSetup.HandleAsync(incoming)
                                                : historicalRestartRoute ? await _historicalRestart.HandleAsync(incoming)
                                                : historicalStopRoute ? await _historicalStop.HandleAsync(incoming)
                                                 : historicalAdminRconRoute ? await _historicalAdminRcon.HandleAsync(incoming)
                                                 : historicalReadOnlyRconRoute ? await _historicalReadOnlyRcon.HandleAsync(incoming)
                                                 : historicalStartRoute ? await _historicalStart.HandleAsync(incoming)
                                                 : historicalRconRoute ? await _historicalRcon.HandleAsync(incoming)
                                                 : await DispatchHistoricalOrReadOnlyAsync(
                                                       methodName,incoming,
                                                       _historicalStore.HandleAsync,
                                                       _bridge.HandleAsync);
                    if (!_browser.IsDisposed && _browser.CoreWebView2 is not null)
                        _browser.CoreWebView2.PostWebMessageAsJson(response);
                    if (updateInstallRoute && response.Contains("\"externalUpdaterStarted\":true",
                        StringComparison.Ordinal) && response.Contains("\"ok\":true",
                        StringComparison.Ordinal))
                    {
                        // Only a native, verified updater handoff can close the
                        // Manager; the WebView cannot directly request Exit().
                        // ASA processes remain independent; the external helper
                        // waits for the current UI PID before touching binaries.
                        _reallyExit = true;
                        BeginInvoke(new Action(Close));
                    }
                }
                finally { if (requireGlobalGate) _bridgeGate.Release(); }
                }
                catch (Exception)
                {
                    // File races, unexpected JSON members and a disposed
                    // WebView must not tear down the native Manager. Release
                    // any global mutation gate via the inner finally above.
                    try
                    {
                        if (!_browser.IsDisposed && _browser.CoreWebView2 is not null)
                            _browser.CoreWebView2.PostWebMessageAsJson(
                                NativeErrorResponse(incoming));
                    }
                    catch (Exception) { /* Window is closing; no renderer remains */ }
                }
            };
            if (_smokeTest)
                core.NavigationCompleted += async (_, args) =>
                    await CompleteNativeSmokeAsync(args.IsSuccess);
            core.Navigate(ReleaseScopedUiUrl(ObsidianReleaseCatalog.InstalledTag));
        }
        catch (Exception)
        {
            if (_smokeTest) { FailNativeSmoke(); return; }
            MessageBox.Show(this,
                "A helyi felület nem indítható. Ellenőrizd a WebView2 Runtime-ot és a csomagolt ui könyvtárat. A szerverekhez nem nyúltunk.",
                "Untamed Desktop — UM-WEBVIEW-START", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }


    private async Task CompleteNativeSmokeAsync(bool navigationSucceeded)
    {
        if (_smokeCompleted) return;
        _smokeCompleted = true;
        try
        {
            if (!navigationSucceeded) throw new InvalidOperationException("Offline WebView2 navigation failed");
            // A mounted React root alone is not proof that the native host
            // answered its first dashboard request. Wait for real, displayed
            // local data; do not accept a spinner/empty shell screenshot.
            string loaded = "\"loading\"";
            for (int attempt = 0; attempt < 70; attempt++)
            {
                loaded = await _browser.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('.hero') ? 'ready' : " +
                    "(document.querySelector('.error-banner') ? 'error' : 'loading')");
                if (loaded is "\"ready\"" or "\"error\"") break;
                await Task.Delay(350);
            }
            string? shot = Environment.GetEnvironmentVariable("UNTAMED_OBSIDIAN_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(shot))
            {
                var path = Path.GetFullPath(shot);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    await _browser.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, output);
                }
                if (new FileInfo(path).Length < 8000)
                    throw new InvalidOperationException("WebView2 screenshot was empty");
            }
            // Native screenshot may exist even when the bridge failed. Mark
            // the CI red unless the user-visible, data-driven hero appeared.
            if (loaded != "\"ready\"")
                throw new InvalidOperationException(
                    loaded == "\"error\"" ? "Native dashboard reports bridge error"
                                            : "Native dashboard never completed loading");
            Environment.ExitCode = 0;
            _reallyExit = true;
            Close();
        }
        catch (Exception) { FailNativeSmoke(); }
    }

    private void FailNativeSmoke()
    {
        Environment.ExitCode = 1;
        _reallyExit = true;
        if (!IsDisposed) Close();
    }

    private static string SyntheticSmokeBridgeResponse(string raw)
    {
        try
        {
            using var request = JsonDocument.Parse(raw);
            if (request.RootElement.ValueKind != JsonValueKind.Object ||
                !request.RootElement.TryGetProperty("id", out var idField) ||
                idField.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(idField.GetString(), out var parsedId) ||
                parsedId == Guid.Empty ||
                !request.RootElement.TryGetProperty("method", out var methodField) ||
                methodField.ValueKind != JsonValueKind.String)
                return NativeErrorResponse(raw);

            string id = parsedId.ToString("D");
            if (methodField.GetString() != "dashboard.snapshot")
                return JsonSerializer.Serialize(new
                {
                    id,
                    ok = false,
                    error = "UM-SMOKE-ROUTE-DENIED"
                });

            return JsonSerializer.Serialize(new
            {
                id,
                ok = true,
                data = new
                {
                    mode = "LIVE_READ_ONLY",
                    observedUtc = DateTimeOffset.UtcNow,
                    local = new
                    {
                        hostName = "SYNTHETIC-SMOKE-HOST",
                        hostId = "11111111-1111-4111-8111-111111111111",
                        maps = new[]
                        {
                            new
                            {
                                id = "22222222-2222-4222-8222-222222222222",
                                name = "Synthetic Test Map",
                                running = false,
                                status = "STOPPED",
                                gamePort = 0,
                                steamBuildId = (string?)null,
                                steamUpdateState = "unknown",
                                storeProtected = false,
                                maintenance = false,
                                rconConfigured = false
                            }
                        },
                        cpuPercent = 0,
                        memoryUsedKiB = 128,
                        memoryTotalKiB = 1024
                    },
                    remote = (object?)null,
                    peerMessage = "Szintetikus WebView2 smoke fixture; nincs natív művelet."
                }
            });
        }
        catch (JsonException)
        {
            return NativeErrorResponse(raw);
        }
    }

    private static bool IsAppUrl(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("untamed-app.example", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo);
    }
}
