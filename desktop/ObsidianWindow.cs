using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace UntamedDesktop;

/// <summary>Only Windows shell and browser hosting live here; server operations stay in Core.</summary>
public sealed class ObsidianWindow : Form
{
    private const string AppOrigin = "https://untamed-app.example";
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _tray = new();
    private readonly DesktopReadOnlyBridge _bridge = new();
    private bool _reallyExit;

    public ObsidianWindow()
    {
        Text = "Untamed Kingdom — Obsidian Command";
        Width = 1510;
        Height = 880;
        MinimumSize = new Size(940, 620);
        BackColor = Color.FromArgb(12, 20, 24);
        StartPosition = FormStartPosition.CenterScreen;
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

        Shown += async (_, _) => await InitializeBrowserAsync();
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
            _ = _bridge.DisposeAsync();
        };
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
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
                string response = await _bridge.HandleAsync(e.WebMessageAsJson);
                if (!_browser.IsDisposed && _browser.CoreWebView2 is not null)
                    _browser.CoreWebView2.PostWebMessageAsJson(response);
            };
            core.Navigate(AppOrigin + "/index.html");
        }
        catch (Exception)
        {
            MessageBox.Show(this,
                "A helyi felület nem indítható. Ellenőrizd a WebView2 Runtime-ot és a csomagolt ui könyvtárat. A szerverekhez nem nyúltunk.",
                "Untamed Desktop — UM-WEBVIEW-START", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
