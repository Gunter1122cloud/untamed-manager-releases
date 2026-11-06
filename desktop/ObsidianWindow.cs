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
    private readonly DesktopReadOnlyBridge _bridge = new();
    private readonly DesktopCredentialBridge _credentials;
    private readonly DesktopModDraftBridge _modDrafts;
    private readonly DesktopPluginStateBridge _pluginState;
    private readonly DesktopPluginZipBridge _pluginZip;
    private readonly DesktopIniBridge _iniEditor;
    private readonly DesktopLocalMapRegistryBridge _mapRegistry;
    private readonly DesktopCurseForgeDiscoveryBridge _modCatalog;
    private readonly DesktopSteamFreshInstallBridge _freshInstall;
    private readonly DesktopInitialMapSetupBridge _initialMapSetup;
    private readonly DesktopHistoricalStoreImportBridge _historicalStore;
    private readonly DesktopUpdateStatusBridge _updates = new();
    private readonly DesktopObsidianUpdateInstallBridge _updateInstall;
    private readonly DesktopRconReadOnlyBridge _rcon;
    private readonly DesktopRconAdminBridge _rconAdmin;
    private readonly DesktopRconConfigurationBridge _rconConfig;
    private readonly DesktopStoppedSteamUpdateBridge _steamUpdate;
    private readonly DesktopFreshAsaStartBridge _freshStart;
    private readonly DesktopFreshAsaStopBridge _freshStop;
    private readonly DesktopFreshAsaRestartBridge _freshRestart;
    private readonly DesktopAsaLifecycleJournalBridge _lifecycleJournal;
    private readonly DesktopOperationsCenterBridge _operations;
    private readonly DesktopLanSettingsBridge _lan;
    private readonly DesktopStoreTravelEvidenceBridge _travel;
    private readonly DesktopWorldBackupBridge _worldBackup;
    private readonly SemaphoreSlim _bridgeGate = new(1, 1);
    private bool _reallyExit;
    private readonly bool _smokeTest;
    private bool _smokeCompleted;

    public ObsidianWindow(bool smokeTest = false)
    {
        _smokeTest = smokeTest;
        _credentials = new DesktopCredentialBridge(_bridge.LocalCluster);
        _updateInstall = new DesktopObsidianUpdateInstallBridge(_bridge.LocalCluster);
        _modDrafts = new DesktopModDraftBridge(_bridge.LocalCluster);
        _pluginState = new DesktopPluginStateBridge(_bridge.LocalCluster);
        _pluginZip = new DesktopPluginZipBridge(_bridge.LocalCluster);
        _iniEditor = new DesktopIniBridge(_bridge.LocalCluster);
        _mapRegistry = new DesktopLocalMapRegistryBridge(_bridge.LocalCluster);
        _modCatalog = new DesktopCurseForgeDiscoveryBridge(_bridge.LocalCluster);
        _freshInstall = new DesktopSteamFreshInstallBridge(_bridge.LocalCluster);
        _initialMapSetup = new DesktopInitialMapSetupBridge(_bridge.LocalCluster);
        _historicalStore = new DesktopHistoricalStoreImportBridge(_bridge.LocalCluster);
        _rcon = new DesktopRconReadOnlyBridge(_bridge.LocalCluster);
        _rconAdmin = new DesktopRconAdminBridge(_bridge.LocalCluster);
        _rconConfig = new DesktopRconConfigurationBridge(_bridge.LocalCluster);
        _steamUpdate = new DesktopStoppedSteamUpdateBridge(_bridge.LocalCluster);
        _freshStart = new DesktopFreshAsaStartBridge(_bridge.LocalCluster);
        _freshStop = new DesktopFreshAsaStopBridge(_bridge.LocalCluster);
        _freshRestart = new DesktopFreshAsaRestartBridge(_bridge.LocalCluster);
        _lifecycleJournal = new DesktopAsaLifecycleJournalBridge(_bridge.LocalCluster);
        _operations = new DesktopOperationsCenterBridge(_bridge.LocalCluster);
        _lan = new DesktopLanSettingsBridge(_bridge.LocalCluster);
        _travel = new DesktopStoreTravelEvidenceBridge(_bridge.LocalCluster);
        _worldBackup = new DesktopWorldBackupBridge(_bridge.LocalCluster);
        Text = "Untamed Kingdom — Obsidian Command";
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
        _tray.Text = "Untamed Kingdom — Obsidian Command";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Megnyitás", null, (_, _) => RestoreWindow());
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
            if (!_smokeTest) try { await _bridge.LocalCluster.BeginLanAsync(); }
                catch (Exception) { /* UI remains usable; peer status stays unknown */ }
            await InitializeBrowserAsync();
        };
        FormClosing += (_, e) =>
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
            }
        };
        FormClosed += (_, _) =>
        {
            _tray.Visible = false;
            _tray.Dispose();
            _browser.Dispose();
            _historicalStore.Dispose();
            _rconConfig.Dispose();
            _steamUpdate.Dispose();
            _freshStart.Dispose();
            _freshStop.Dispose();
            _freshRestart.Dispose();
            _initialMapSetup.Dispose();
            _freshInstall.Dispose();
            _modCatalog.Dispose();
            _mapRegistry.Dispose();
            _iniEditor.Dispose();
            _modDrafts.Dispose();
            _pluginState.Dispose();
            _pluginZip.Dispose();
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

            string data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UntamedManager", "ObsidianWebView2");
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
                string incoming = e.WebMessageAsJson;
                bool credentialRoute = false;
                bool draftRoute = false;
                bool iniRoute = false;
                bool registryRoute = false;
                bool discoveryRoute = false;
                bool freshRoute = false;
                bool initialRoute = false;
                bool historicalRoute = false;
                bool pluginStateRoute = false;
                bool pluginZipRoute = false;
                bool updateRoute = false;
                bool updateInstallRoute = false;
                bool rconRoute = false;
                bool rconAdminRoute = false;
                bool rconConfigRoute = false;
                bool travelRoute = false;
                bool worldBackupRoute = false;
                bool stoppedSteamRoute = false;
                bool freshStartRoute = false;
                bool freshStopRoute = false;
                bool freshRestartRoute = false;
                bool lifecycleJournalRoute = false;
                bool operationsRoute = false;
                bool lanRoute = false;
                try
                {
                    using var parsed = JsonDocument.Parse(incoming);
                    if (parsed.RootElement.TryGetProperty("method", out var method) &&
                        method.ValueKind == JsonValueKind.String)
                    {
                        string? name = method.GetString();
                        credentialRoute = name is
                            "curseforge.credentialStatus" or
                            "curseforge.credentialSave" or
                            "curseforge.credentialClear";
                        updateRoute = name is "updates.status" or "updates.check";
                        updateInstallRoute = name is "updates.prepare" or "updates.install";
                        rconRoute = name is "rcon.status" or "rcon.listPlayers" or
                            "rcon.gameLog" or "rcon.chatLog" or "rcon.saveWorld" or
                            "rcon.saveStatus";
                        rconAdminRoute = name is "rcon.adminPreview" or "rcon.adminSend";
                        rconConfigRoute = name is "rcon.configStatus" or "rcon.configPreview" or "rcon.configApply";
                        freshStartRoute = name is "servers.freshStartPreview" or "servers.freshStartApply" or
                            "servers.freshStartStatus" or "servers.freshReadiness";
                        freshStopRoute = name is "servers.gracefulStopPreview" or "servers.gracefulStopApply" or "servers.gracefulStopStatus";
                        freshRestartRoute = name is "servers.restartPreview" or "servers.restartApply" or
                            "servers.restartStatus" or "servers.restartCurrent";
                        lifecycleJournalRoute = name is "servers.lifecycleJournal";
                        operationsRoute = name is "operations.snapshot" or "operations.map";
                        lanRoute = name is "lan.status" or "lan.preview" or "lan.apply";
                        stoppedSteamRoute = name is "steam.stoppedPreview" or "steam.stoppedApply" or
                            "steam.stoppedStatus" or "steam.stoppedCurrent";
                        travelRoute = name is "travel.capture" or "travel.compare";
                        worldBackupRoute = name is "backup.history" or "backup.preview" or
                            "backup.create" or "backup.restorePreview" or "backup.restore";
                        pluginStateRoute = name is
                            "plugins.state" or "plugins.togglePreview" or "plugins.toggleApply";
                        pluginZipRoute = name is "plugins.zipSelect" or "plugins.zipApply";
                        draftRoute = name is
                            "mods.draftStart" or "mods.draftStage" or
                            "mods.draftUndo" or "mods.draftRedo" or
                            "mods.draftDiscard" or "mods.draftSave";
                        iniRoute = name is "ini.open" or "ini.preview" or
                            "ini.save" or "ini.close";
                        registryRoute = name is "servers.inspectLocal" or
                            "servers.importLocal";
                        discoveryRoute = name is "curseforge.categories" or
                            "curseforge.featured" or "curseforge.browse" or
                            "curseforge.project" or "curseforge.files" or "curseforge.summary";
                        freshRoute = name is "steam.freshPreflight" or "steam.freshStart" or
                            "steam.freshStatus" or "steam.freshCurrent" or
                            "steam.freshCancel" or "steam.freshDiscard" or
                            "steam.freshRegister" or "steam.freshBrowse";
                        initialRoute = name is "servers.initialStatus" or
                            "servers.initialPreview" or "servers.initialApply";
                        historicalRoute = name is "historical.localStatus" or
                            "historical.localParity" or
                            "historical.list" or
                            "historical.preview" or "historical.register" or
                            "historical.pairedEvidence" or "historical.launchEvidence";
                    }
                }
                catch (JsonException) { /* read-only bridge will safely reject invalid messages */ }
                await _bridgeGate.WaitAsync();
                try
                {
                    string response = lanRoute ? await _lan.HandleAsync(incoming)
                        : operationsRoute ? await _operations.HandleAsync(incoming)
                        : freshRestartRoute ? await _freshRestart.HandleAsync(incoming)
                        : lifecycleJournalRoute ? await _lifecycleJournal.HandleAsync(incoming)
                        : freshStopRoute ? await _freshStop.HandleAsync(incoming)
                        : freshStartRoute ? await _freshStart.HandleAsync(incoming)
                        : pluginZipRoute ? await _pluginZip.HandleAsync(incoming)
                        : stoppedSteamRoute ? await _steamUpdate.HandleAsync(incoming)
                        : rconConfigRoute ? await _rconConfig.HandleAsync(incoming)
                        : rconAdminRoute ? await _rconAdmin.HandleAsync(incoming)
                        : worldBackupRoute ? await _worldBackup.HandleAsync(incoming)
                        : updateInstallRoute ? await _updateInstall.HandleAsync(incoming)
                        : travelRoute ? await _travel.HandleAsync(incoming)
                        : rconRoute ? await _rcon.HandleAsync(incoming)
                        : updateRoute ? await _updates.HandleAsync(incoming)
                        : credentialRoute ? await _credentials.HandleAsync(incoming)
                        : pluginStateRoute ? await _pluginState.HandleAsync(incoming)
                        : draftRoute ? await _modDrafts.HandleAsync(incoming)
                            : iniRoute ? await _iniEditor.HandleAsync(incoming)
                                : registryRoute ? await _mapRegistry.HandleAsync(incoming)
                                    : discoveryRoute ? await _modCatalog.HandleAsync(incoming)
                                        : freshRoute ? await _freshInstall.HandleAsync(incoming)
                                            : initialRoute ? await _initialMapSetup.HandleAsync(incoming)
                                                : historicalRoute ? await _historicalStore.HandleAsync(incoming)
                                                    : await _bridge.HandleAsync(incoming);
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
                finally { _bridgeGate.Release(); }
            };
            if (_smokeTest)
                core.NavigationCompleted += async (_, args) =>
                    await CompleteNativeSmokeAsync(args.IsSuccess);
            core.Navigate(AppOrigin + "/index.html");
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

    private static bool IsAppUrl(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("untamed-app.example", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo);
    }
}
