using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Nexus.Desktop;

public sealed class MainForm : Form
{
    private readonly DesktopUserConfig _config;
    private readonly WebView2 _webView;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private readonly bool _startMinimized;
    private bool _exiting;
    private Uri? _currentServerOrigin;
    private Uri? _lastNavigationUri;
    private DateTimeOffset _lastDownloadStartedAt = DateTimeOffset.MinValue;

    public MainForm(DesktopUserConfig config, bool startMinimized)
    {
        _config = config;
        _startMinimized = startMinimized;

        Text = "Nexus";
        MinimumSize = new Size(900, 620);
        Size = new Size(Math.Clamp(config.WindowWidth, 900, 2560), Math.Clamp(config.WindowHeight, 620, 1600));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Nexus.ico");
        if (File.Exists(iconPath))
        {
            Icon = new Icon(iconPath);
        }
        else if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        }

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_webView);

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = DesktopConfigStore.IsStartWithWindowsEnabled()
        };
        _startWithWindowsItem.Click += (_, _) =>
        {
            try
            {
                DesktopConfigStore.SetStartWithWindows(_startWithWindowsItem.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not update startup setting: {ex.Message}", "Nexus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _startWithWindowsItem.Checked = DesktopConfigStore.IsStartWithWindowsEnabled();
            }
        };

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Nexus", null, (_, _) => ShowAndActivate());
        trayMenu.Items.Add("Reload", null, (_, _) => _webView.CoreWebView2?.Reload());
        trayMenu.Items.Add("Change Server URL...", null, (_, _) => PromptAndSwitchServer());
        trayMenu.Items.Add("Reset camera and microphone permissions", null, async (_, _) => await ResetMediaPermissionsAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(_startWithWindowsItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Quit Nexus", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "Nexus",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowAndActivate();

        KeyDown += OnKeyDownShortcut;
        Load += OnFormLoadAsync;
        FormClosing += OnFormClosing;
    }

    public void ShowAndActivate()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ShowAndActivate);
            return;
        }

        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = _config.IsMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }

        Activate();
    }

    private void OnKeyDownShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.S)
        {
            e.Handled = true;
            PromptAndSwitchServer();
        }
    }

    private async void OnFormLoadAsync(object? sender, EventArgs e)
    {
        if (_config.IsMaximized && !_startMinimized)
        {
            WindowState = FormWindowState.Maximized;
        }

        if (_startMinimized)
        {
            Hide();
        }

        if (string.IsNullOrWhiteSpace(_config.ServerUrl))
        {
            if (!PromptForServerUrl())
            {
                ExitApplication();
                return;
            }
        }

        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(DesktopConfigStore.WebView2DataDir);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: DesktopConfigStore.WebView2DataDir);

            await _webView.EnsureCoreWebView2Async(env);

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = true;
            settings.IsZoomControlEnabled = true;

            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.PermissionRequested += OnPermissionRequested;
            _webView.CoreWebView2.ScreenCaptureStarting += OnScreenCaptureStarting;
            _webView.CoreWebView2.DownloadStarting += (_, _) =>
            {
                _lastDownloadStartedAt = DateTimeOffset.UtcNow;
            };
            _webView.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                var docTitle = _webView.CoreWebView2.DocumentTitle;
                Text = string.IsNullOrWhiteSpace(docTitle) ? "Nexus" : docTitle;
            };

            NavigateToConfiguredServer();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var choice = MessageBox.Show(
                this,
                "Microsoft Edge WebView2 Runtime is required to run Nexus Desktop.\n\nOpen the official Microsoft download page now?",
                "WebView2 Runtime Required",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (choice == DialogResult.Yes)
            {
                OpenExternalUrl("https://developer.microsoft.com/en-us/microsoft-edge/webview2/");
            }

            ExitApplication();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Failed to initialize Nexus Desktop: {ex.Message}",
                "Nexus",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            ExitApplication();
        }
    }

    private void NavigateToConfiguredServer()
    {
        if (!Uri.TryCreate(_config.ServerUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        _currentServerOrigin = uri;
        _webView.CoreWebView2?.Navigate(uri.ToString());
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
        {
            if (IsSameServerOrigin(target))
            {
                _webView.CoreWebView2.Navigate(target.ToString());
            }
            else if (target.Scheme is "https" or "http")
            {
                OpenExternalUrl(target.ToString());
            }
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone &&
            (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var requester) || !IsSameServerOrigin(requester)))
        {
            e.SavesInProfile = false;
            e.State = CoreWebView2PermissionState.Deny;
        }
    }

    private void OnScreenCaptureStarting(object? sender, CoreWebView2ScreenCaptureStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.OriginalSourceFrameInfo.Source, UriKind.Absolute, out var requester) || !IsSameServerOrigin(requester))
        {
            e.Cancel = true;
        }
    }

    private async Task ResetMediaPermissionsAsync()
    {
        if (_webView.CoreWebView2?.Profile is not { } profile || _currentServerOrigin is not { } server) return;

        try
        {
            var origin = server.GetLeftPart(UriPartial.Authority);
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Camera, origin, CoreWebView2PermissionState.Default);
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Default);
            _trayIcon.ShowBalloonTip(3000, "Nexus", "Camera and microphone permissions will be requested again when you use them.", ToolTipIcon.Info);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            _trayIcon.ShowBalloonTip(3000, "Nexus", "Could not reset camera and microphone permissions. Try again after reloading Nexus.", ToolTipIcon.Warning);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
        {
            return;
        }

        _lastNavigationUri = target;

        if (target.Scheme == Uri.UriSchemeFile)
        {
            // Prevent accidental local file drag-and-drop outside the chat dropzone from navigating away.
            e.Cancel = true;
            return;
        }

        if (target.Scheme == "about" || target.Scheme == "data")
        {
            return;
        }

        if (!IsSameServerOrigin(target) && target.Scheme is "https" or "http")
        {
            e.Cancel = true;
            OpenExternalUrl(target.ToString());
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            return;
        }

        // WebView2 aborts a top-level navigation with ConnectionAborted when a server response
        // triggers a file download (Content-Disposition: attachment). Never replace the live app
        // with an offline error screen in that case.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted && IsDownloadAbort())
        {
            return;
        }

        var escapedUrl = System.Net.WebUtility.HtmlEncode(_config.ServerUrl);
        var escapedError = System.Net.WebUtility.HtmlEncode(e.WebErrorStatus.ToString());
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <style>
                body { font-family: "Segoe UI", system-ui, sans-serif; background: #f4f6fb; color: #182238; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
                .card { background: #fff; border: 1px solid #d9e0ef; border-radius: 12px; padding: 28px 32px; max-width: 460px; box-shadow: 0 8px 24px rgba(24,34,56,.08); }
                h2 { margin: 0 0 8px; font-size: 20px; }
                p { color: #52607a; line-height: 1.5; margin: 8px 0; font-size: 14px; }
                code { background: #eef2fa; padding: 2px 6px; border-radius: 4px; }
                button { margin-top: 14px; background: #3743aa; color: #fff; border: 0; border-radius: 8px; padding: 9px 16px; font-size: 14px; cursor: pointer; }
              </style>
            </head>
            <body>
              <div class="card">
                <h2>Unable to reach Nexus</h2>
                <p>Server: <code>{{escapedUrl}}</code></p>
                <p>Status: <code>{{escapedError}}</code></p>
                <p>If your demo server is waking up from sleep, wait a few seconds and click Retry. Press <code>Ctrl+Shift+S</code> (or use the tray menu) to change the server address.</p>
                <button onclick="window.location.href='{{escapedUrl}}'">Retry Connection</button>
              </div>
            </body>
            </html>
            """;

        _webView.CoreWebView2.NavigateToString(html);
    }

    private bool IsDownloadAbort()
    {
        if (DateTimeOffset.UtcNow - _lastDownloadStartedAt <= TimeSpan.FromSeconds(5))
        {
            return true;
        }

        if (_lastNavigationUri is not null)
        {
            var path = _lastNavigationUri.AbsolutePath;
            var query = _lastNavigationUri.Query;
            if (path.Contains("/api/files/", StringComparison.OrdinalIgnoreCase) ||
                query.Contains("format=csv", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSameServerOrigin(Uri target)
    {
        if (_currentServerOrigin is null)
        {
            return false;
        }

        return string.Equals(
            _currentServerOrigin.GetLeftPart(UriPartial.Authority),
            target.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);
    }

    private void PromptAndSwitchServer()
    {
        if (PromptForServerUrl())
        {
            NavigateToConfiguredServer();
            ShowAndActivate();
        }
    }

    private bool PromptForServerUrl()
    {
        using var dialog = new ServerUrlDialog(_config.ServerUrl, Icon);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        _config.ServerUrl = dialog.SelectedServerUrl;
        DesktopConfigStore.Save(_config);
        return true;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        PersistWindowBounds();

        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void PersistWindowBounds()
    {
        _config.IsMaximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal)
        {
            _config.WindowWidth = Width;
            _config.WindowHeight = Height;
        }

        try
        {
            DesktopConfigStore.Save(_config);
        }
        catch
        {
            // Ignore non-critical settings write failures on shutdown.
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
        Application.Exit();
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch errors if no default browser is registered.
        }
    }
}
