using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WinForms = System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;
using Colors = System.Windows.Media.Colors;

namespace CodexLauncher
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private static readonly IntPtr HTCAPTION = new IntPtr(2);

        private Grid _rootGrid;
        private WebView2 _webView;
        private WebViewBridge _bridge;
        private bool _webReady = false;

        private bool _forceExit = false;
        private bool _balloonShownThisSession = false;

        private WinForms.NotifyIcon _trayIcon;
        private string _lastTrayState = "";
        private TrayPopupWindow _activeTrayPopup;
        private EventWaitHandle _wakeEvent;
        private volatile bool _wakeStop = false;

        private FileSystemWatcher _uiWatcher;
        private DispatcherTimer _reloadDebounce;
        private bool _uiReloadPending;

        private IntPtr _hwnd = IntPtr.Zero;
        private bool _hotkeyRegistered = false;
        private string _hotkeyError = "";
        private DispatcherTimer _screenshotDelayTimer;
        private const string UiProtocol = "5";
        private static readonly Dictionary<string, string> UiContentTypes = new Dictionary<string, string>
        {
            { "/index.html", "text/html; charset=utf-8" },
            { "/设计系统.html", "text/html; charset=utf-8" },
            { "/界面样式.css", "text/css; charset=utf-8" },
            { "/工作台交互.js", "text/javascript; charset=utf-8" },
            { "/gemini.png", "image/png" },
            { "/ai-studio.png", "image/png" }
        };
        private Dictionary<string, byte[]> _uiAssets;
        private readonly string _updateToken;
        private readonly bool _justUpdated;
        private readonly bool _startMinimized;
        private bool _startingUpdate;
        private FileSystemWatcher _updateWatcher;
        private DispatcherTimer _updateDebounce;
        private bool _waitingPreviewStopForUpdate;

        public MainWindow(string updateToken, bool startMinimized = false)
        {
            _updateToken = updateToken;
            _justUpdated = !string.IsNullOrEmpty(updateToken);
            _startMinimized = startMinimized;
            _store = CoreService.LoadStore();
            _quotaAutoRefreshEnabled = _store.settings.quota_auto_refresh;

            Title = "AI BASE";
            Width = 940;
            Height = 630;
            MinWidth = 820;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Brushes.Transparent;
            FontFamily = UiHelpers.MainFont;
            Icon = UiHelpers.LoadWindowIcon();

            // Full-bleed native Windows 11 borderless window with real resize, Aero Snap & rounded corners
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(-1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            _rootGrid = new Grid { Background = Brushes.Transparent };
            _webView = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.Transparent
            };
            _rootGrid.Children.Add(_webView);
            Content = _rootGrid;

            SourceInitialized += OnSourceInitialized;
            StateChanged += OnStateChanged;
            Activated += (s, e) => { if (_webReady) RefreshAsync(false); };
            Loaded += OnLoaded;
            Closing += OnWindowClosing;
            Closed += OnWindowClosed;

            SetupTrayIcon();
            StartWakeListener();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                PositionStartupWindow();
                if (_startMinimized)
                {
                    Hide();
                }
                else
                {
                    WindowState = WindowState.Maximized;
                }
                var src = HwndSource.FromHwnd(_hwnd);
                if (src != null)
                {
                    if (src.CompositionTarget != null)
                        src.CompositionTarget.BackgroundColor = Colors.Transparent;
                    src.AddHook(WndProc);
                }

                // Light mode + Sharp 0px corners + Ink outer DWM border + Windows 11 Mica backdrop
                int dark = 0;
                DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int));
                int corner = 1; // DWMWCP_DONOTROUND (strict 0px sharp corners)
                DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int));
                int borderColor = 0x0017191A; // COLORREF 0x00BBGGRR for #1A1917
                DwmSetWindowAttribute(_hwnd, 34, ref borderColor, sizeof(int));
                int mica = 2; // DWMSBT_MAINWINDOW (True Windows 11 Mica)
                int hr = DwmSetWindowAttribute(_hwnd, 38, ref mica, sizeof(int));
                if (hr != 0)
                {
                    // Fallback on older Windows 10 builds: clean solid light backing
                    Background = UiHelpers.SidebarBg;
                }

                SyncScreenshotHotkey();
            }
            catch
            {
                Background = UiHelpers.SidebarBg;
            }
        }

        private void PositionStartupWindow()
        {
            var monitor = NativeScreen.GetAllMonitors()
                .Where(display => display.WorkingArea.Width > 0 && display.WorkingArea.Height > 0)
                .OrderBy(display => display.Bounds.Left)
                .ThenBy(display => display.Bounds.Top)
                .FirstOrDefault();
            var work = monitor.WorkingArea;
            if (work.Width <= 0 || work.Height <= 0) return;

            double scale = monitor.ScalePercent > 0 ? monitor.ScalePercent / 100.0 : 1;
            int width = Math.Min(work.Width, (int)Math.Round(Width * scale));
            int height = Math.Min(work.Height, (int)Math.Round(Height * scale));
            const uint moveFlags = NativeScreen.SWP_NOZORDER | NativeScreen.SWP_NOACTIVATE;
            if (!NativeScreen.SetWindowPos(_hwnd, IntPtr.Zero,
                work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2,
                width, height, moveFlags)) return;

            // WPF can adjust the window size when it receives the destination monitor's DPI.
            NativeScreen.RECT actual;
            if (NativeScreen.GetWindowRect(_hwnd, out actual) && (actual.Width != width || actual.Height != height))
                NativeScreen.SetWindowPos(_hwnd, IntPtr.Zero,
                    work.Left + Math.Max(0, (work.Width - actual.Width) / 2),
                    work.Top + Math.Max(0, (work.Height - actual.Height) / 2),
                    0, 0, moveFlags | NativeScreen.SWP_NOSIZE);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == HotkeyHelper.WM_HOTKEY && wParam.ToInt32() == HotkeyHelper.SCREENSHOT_HOTKEY_ID)
            {
                handled = true;
                Dispatcher.BeginInvoke(new Action(() => StartScreenshotNow(0)));
            }
            return IntPtr.Zero;
        }

        private void SyncScreenshotHotkey()
        {
            if (_hwnd == IntPtr.Zero) return;
            bool enabled = _store != null && _store.settings != null && _store.settings.screenshot_enabled;
            string spec = (_store != null && _store.settings != null && !string.IsNullOrWhiteSpace(_store.settings.screenshot_hotkey))
                ? _store.settings.screenshot_hotkey
                : "Ctrl+Shift+A";

            if (!enabled)
            {
                HotkeyHelper.Unregister(_hwnd, HotkeyHelper.SCREENSHOT_HOTKEY_ID);
                _hotkeyRegistered = false;
                _hotkeyError = "";
                return;
            }

            string err;
            _hotkeyRegistered = HotkeyHelper.Register(_hwnd, HotkeyHelper.SCREENSHOT_HOTKEY_ID, spec, out err);
            _hotkeyError = err ?? "";
        }

        private void StartScreenshotNow(int delayMs)
        {
            if (ScreenshotSession.IsActive) return;
            if (_screenshotDelayTimer != null)
            {
                _screenshotDelayTimer.Stop();
                _screenshotDelayTimer = null;
            }
            Action run = () =>
            {
                if (ScreenshotSession.IsActive) return;
                ScreenshotSession.Start((msg, isErr) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (IsVisible && WindowState != WindowState.Minimized && _webReady) SendToastToJs(msg, isErr);
                        else ShowDesktopNotification("AI BASE", msg, isErr ? "danger" : "ok", isErr ? 4500 : 1800);
                    }));
                });
            };

            if (delayMs > 0)
            {
                var dt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
                _screenshotDelayTimer = dt;
                dt.Tick += (s, e) =>
                {
                    dt.Stop();
                    if (_screenshotDelayTimer == dt) _screenshotDelayTimer = null;
                    run();
                };
                dt.Start();
            }
            else
            {
                run();
            }
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            _rootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            PushWindowState();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                CoreService.EnsureDirs();
                Directory.CreateDirectory(CoreService.WebViewCacheDir);

                var env = await CoreWebView2Environment.CreateAsync(null, CoreService.WebViewCacheDir);
                await _webView.EnsureCoreWebView2Async(env);

                var core = _webView.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;

                string uiDir = Path.Combine(CoreService.BaseDir, "页面代码");
                _uiAssets = LoadUiAssets(uiDir);
                core.AddWebResourceRequestedFilter("https://aihub.local/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (s2, e2) => ServeUiAsset(core, e2);
                core.SetVirtualHostNameToFolderMapping("icons.aihub.local", CoreService.AppIconsCacheDir, CoreWebView2HostResourceAccessKind.Allow);

                _bridge = new WebViewBridge(core, HandleJsAction, SendToastToJs);
                core.NavigationCompleted += (s2, e2) =>
                {
                    _webReady = e2.IsSuccess;
                    if (!_webReady) return;
                    PushWindowState();
                    RefreshAsync(true);
                };

                core.Navigate(WebViewBridge.DocumentUri);
                SetupUiHotReload(uiDir);
                SetupPendingUpdateWatcher();

                _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _pollTimer.Tick += (s3, e3) => RefreshAsync(false);
                _pollTimer.Start();

                _quotaTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _quotaTimer.Tick += (s3, e3) => CheckQuotaRefresh();
                _quotaTimer.Start();
                RefreshInstalledApps();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "初始化界面引擎失败：\n" + ex.Message, "AI BASE", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== TRAY & WAKEUP ====================

        private void SetupTrayIcon()
        {
            try
            {
                _trayIcon = new WinForms.NotifyIcon();
                _trayIcon.Text = "AI BASE · 点击打开";
                UpdateTrayStatusIcon("normal", "AI BASE · 点击打开");

                _trayIcon.MouseUp += (s, e) =>
                {
                    if (e.Button == WinForms.MouseButtons.Left)
                        Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                    else if (e.Button == WinForms.MouseButtons.Right)
                        Dispatcher.BeginInvoke(new Action(ShowModernTrayMenu));
                };
                _trayIcon.Visible = true;
            }
            catch { }
        }

        private void UpdateTrayStatusIcon(string state, string tooltip)
        {
            if (_trayIcon == null) return;
            try
            {
                string next = string.IsNullOrEmpty(state) ? "normal" : state;
                if (next != _lastTrayState)
                {
                    _lastTrayState = next;
                    Icon icon = null;
                    try { icon = TrayIconFactory.GetStatusIcon(next); } catch { }
                    if (icon == null)
                    {
                        string icoPath = File.Exists(CoreService.PermanentIcon) ? CoreService.PermanentIcon : CoreService.BundledIcon;
                        icon = File.Exists(icoPath) ? new Icon(icoPath) : SystemIcons.Application;
                    }
                    _trayIcon.Icon = icon;
                }
                if (!string.IsNullOrEmpty(tooltip))
                {
                    _trayIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
                }
            }
            catch { }
        }

        private void ShowDesktopNotification(string title, string message, string tone = "ok", int durationMs = 5000)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                DesktopToastWindow.ShowToast(title, message, tone, durationMs, RestoreFromTray);
            }));
        }

        private void ShowModernTrayMenu()
        {
            try
            {
                if (_activeTrayPopup != null)
                {
                    try { _activeTrayPopup.Close(); } catch { }
                    _activeTrayPopup = null;
                }
                _activeTrayPopup = new TrayPopupWindow(
                    () => { _openBatchExitPending = true; RestoreFromTray(); },
                    ForceQuit);
                _activeTrayPopup.Show();
            }
            catch { }
        }

        public void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
            Activate();
            Topmost = true;
            Topmost = false;
            _quotaRefreshOnResume = true;
            try { if (_webView.CoreWebView2 != null) _webView.CoreWebView2.Resume(); }
            catch (InvalidOperationException) { }
            if (_uiReloadPending && _webView.CoreWebView2 != null)
            {
                _uiReloadPending = false;
                _webView.CoreWebView2.Reload();
            }
            if (_pollTimer != null && !_pollTimer.IsEnabled) _pollTimer.Start();
            RefreshAsync(false);
        }

        private void ForceQuit()
        {
            _forceExit = true;
            Close();
        }

        private async void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_forceExit && _store != null && _store.settings != null && _store.settings.minimize_to_tray)
            {
                e.Cancel = true;
                Hide();
                if (_pollTimer != null) _pollTimer.Stop();
                try
                {
                    if (_webReady && _webView != null && _webView.CoreWebView2 != null)
                    {
                        var core = _webView.CoreWebView2;
                        await core.TrySuspendAsync();
                        if (!_wakeStop && IsVisible) core.Resume();
                    }
                }
                catch { }
            }
            else if (_antigravityRefresh != null) _antigravityRefresh.Cancel();
        }

        private static Dictionary<string, byte[]> LoadUiAssets(string uiDir)
        {
            string marker = Path.Combine(uiDir, "协议版本.txt");
            try
            {
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == UiProtocol)
                {
                    var source = UiContentTypes.Keys.ToDictionary(path => path, path =>
                        File.ReadAllBytes(Path.Combine(uiDir, path.Substring(1))));
                    if (File.ReadAllText(marker).Trim() == UiProtocol) return source;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var bundled = new Dictionary<string, byte[]>();
            foreach (string path in UiContentTypes.Keys)
            {
                using (var input = typeof(MainWindow).Assembly.GetManifestResourceStream("AIHub.Ui." + path.Substring(1)))
                using (var output = new MemoryStream())
                {
                    if (input == null) throw new InvalidOperationException("AI Hub 缺少内置界面文件 " + path);
                    input.CopyTo(output);
                    bundled[path] = output.ToArray();
                }
            }
            return bundled;
        }

        private void ServeUiAsset(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs args)
        {
            Uri uri;
            byte[] body;
            string reqPath = null;
            if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out uri) && uri.Host == "aihub.local")
            {
                reqPath = Uri.UnescapeDataString(uri.AbsolutePath);
            }
            if (reqPath == null || _uiAssets == null || !_uiAssets.TryGetValue(reqPath, out body))
            {
                args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 404, "Not Found", "");
                return;
            }
            string headers = "Content-Type: " + UiContentTypes[reqPath] + "\r\nCache-Control: no-store";
            args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(body, false), 200, "OK", headers);
        }

        private void SetupUiHotReload(string uiDir)
        {
            try
            {
                if (!Directory.Exists(uiDir)) return;
                _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                _reloadDebounce.Tick += (s, e) =>
                {
                    _reloadDebounce.Stop();
                    try
                    {
                        if (_webReady && _webView != null && _webView.CoreWebView2 != null)
                        {
                            _uiAssets = LoadUiAssets(uiDir);
                            if (IsVisible) _webView.CoreWebView2.Reload();
                            else _uiReloadPending = true;
                        }
                    }
                    catch { }
                };

                _uiWatcher = new FileSystemWatcher(uiDir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler onChange = (s, e) =>
                {
                    if (e.Name != null && (e.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                        || e.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                        || e.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                        || e.Name.Equals("协议版本.txt", StringComparison.OrdinalIgnoreCase)))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _reloadDebounce.Stop();
                            _reloadDebounce.Start();
                        }));
                    }
                };
                _uiWatcher.Changed += onChange;
                _uiWatcher.Created += onChange;
                _uiWatcher.Deleted += onChange;
                _uiWatcher.Renamed += (s, e) => onChange(s, e);
            }
            catch { }
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            _wakeStop = true;
            DesktopToastWindow.CloseAll();
            _projectPreviews.Dispose();
            if (_pollTimer != null) _pollTimer.Stop();
            if (_quotaTimer != null) _quotaTimer.Stop();
            if (_quotaBalloonTimer != null) _quotaBalloonTimer.Stop();
            try { if (_bridge != null) _bridge.Dispose(); } catch { }
            _bridge = null;
            if (_hwnd != IntPtr.Zero) HotkeyHelper.Unregister(_hwnd, HotkeyHelper.SCREENSHOT_HOTKEY_ID);
            try { if (_uiWatcher != null) _uiWatcher.Dispose(); } catch { }
            try { if (_updateWatcher != null) _updateWatcher.Dispose(); } catch { }
            try { if (_wakeEvent != null) _wakeEvent.Set(); } catch { }
            try { if (_wakeEvent != null) _wakeEvent.Dispose(); } catch { }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        private void StartWakeListener()
        {
            try
            {
                _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.WakeEventName);
                var t = new Thread(() =>
                {
                    while (!_wakeStop)
                    {
                        try
                        {
                            if (_wakeEvent.WaitOne(1000) && !_wakeStop)
                                Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                        }
                        catch { break; }
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch { }
        }

        private void SendEventToJs(string type, object payload)
        {
            if (_webReady && _bridge != null) _bridge.Post(type, payload);
        }

        private void SendToastToJs(string text, bool isError)
        {
            if (!IsVisible) return;
            SendEventToJs("toast", new Dictionary<string, object> { { "text", text }, { "isError", isError } });
        }

        private void SetupPendingUpdateWatcher()
        {
            try
            {
                _updateDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                _updateDebounce.Tick += (s, e) =>
                {
                    _updateDebounce.Stop();
                    TryAutoApplyPendingUpdate();
                };

                _updateWatcher = new FileSystemWatcher(CoreService.BaseDir, "AI BASE.next.exe")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler onNextExe = (s, e) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _waitingPreviewStopForUpdate = false;
                        _updateDebounce.Stop();
                        _updateDebounce.Start();
                    }));
                };
                _updateWatcher.Created += onNextExe;
                _updateWatcher.Changed += onNextExe;
                _updateWatcher.Renamed += (s, e) => onNextExe(s, e);

                if (LocalUpdate.IsPending)
                {
                    _updateDebounce.Stop();
                    _updateDebounce.Start();
                }
            }
            catch { }
        }

        private void TryAutoApplyPendingUpdate()
        {
            if (_wakeStop || _startingUpdate) return;
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!LocalUpdate.IsPendingReadyForAutoApply(exePath)) return;

            if (ScreenshotSession.IsActive || _projectPreviews.HasActiveSessions)
            {
                if (!_waitingPreviewStopForUpdate)
                {
                    _waitingPreviewStopForUpdate = true;
                    PushFullState();
                    if (_projectPreviews.HasActiveSessions)
                        SendToastToJs("检测到新版本，停止项目预览后将自动重启更新", false);
                }
                return;
            }

            try
            {
                LocalUpdate.Start();
                _startingUpdate = true;
                ForceQuit();
            }
            catch
            {
                PushFullState();
            }
        }

        private void PushWindowState()
        {
            SendEventToJs("windowState", new Dictionary<string, object> { { "maximized", WindowState == WindowState.Maximized } });
        }

    }
}
