using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
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
    public class MainWindow : Window
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 20 * 1024 * 1024 };

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
        private bool _webReady = false;

        private AccountsStore _store;
        private int _settingsRevision;
        private List<AccountViewModel> _accounts = new List<AccountViewModel>();
        private ProxyStatusInfo _proxy = new ProxyStatusInfo();
        private readonly ClashNetwork _network = new ClashNetwork();
        private readonly SemaphoreSlim _networkGate = new SemaphoreSlim(1);
        private NetworkStatus _networkStatus = new NetworkStatus { state = "unknown" };
        private bool _networkPolling;
        private bool _networkWorking;
        private CancellationTokenSource _networkBatch;
        private AntigravityUsage _antigravityUsage = AntigravityQuota.ReadCache();
        private CancellationTokenSource _antigravityRefresh;
        private string _antigravityError;
        private List<ChromeProfileInfo> _chromeProfiles = new List<ChromeProfileInfo>();
        private readonly Dictionary<string, UsageSnapshot> _usageCache = new Dictionary<string, UsageSnapshot>();
        private readonly Dictionary<string, AuthInfo> _usageLoading = new Dictionary<string, AuthInfo>();
        private readonly Dictionary<string, string> _usageErrors = new Dictionary<string, string>();
        private readonly Dictionary<string, QuotaRefreshState> _codexQuota = new Dictionary<string, QuotaRefreshState>();
        private readonly QuotaRefreshState _antigravityQuota = new QuotaRefreshState();
        private readonly Dictionary<string, long> _quotaRefillHistory = CoreService.ReadQuotaRefillHistory();
        private readonly List<string> _quotaBalloonLabels = new List<string>();
        private DispatcherTimer _quotaTimer;
        private DispatcherTimer _quotaBalloonTimer;
        private bool _quotaRefreshOnResume = true;
        private bool _openBatchExitPending;
        private bool _batchExitInFlight;
        private bool _discoveringApps;
        private List<ToolItem> _installedApps = new List<ToolItem>();
        private string _lastRefreshError;

        private bool _refreshInFlight = false;
        private bool _forceExit = false;
        private bool _balloonShownThisSession = false;

        private WinForms.NotifyIcon _trayIcon;
        private TrayPopupWindow _activeTrayPopup;
        private EventWaitHandle _wakeEvent;
        private volatile bool _wakeStop = false;

        private DispatcherTimer _pollTimer;
        private FileSystemWatcher _uiWatcher;
        private DispatcherTimer _reloadDebounce;
        private bool _uiReloadPending;

        private IntPtr _hwnd = IntPtr.Zero;
        private bool _hotkeyRegistered = false;
        private string _hotkeyError = "";
        private List<DesktopShortcutInfo> _desktopShortcuts = new List<DesktopShortcutInfo>();

        private const string UiProtocol = "3";
        private static readonly Dictionary<string, string> UiContentTypes = new Dictionary<string, string>
        {
            { "/index.html", "text/html; charset=utf-8" },
            { "/界面样式.css", "text/css; charset=utf-8" },
            { "/工作台交互.js", "text/javascript; charset=utf-8" },
            { "/gemini.png", "image/png" },
            { "/ai-studio.png", "image/png" }
        };
        private Dictionary<string, byte[]> _uiAssets;
        private readonly string _updateToken;
        private bool _startingUpdate;

        public MainWindow(string updateToken)
        {
            _updateToken = updateToken;
            _store = CoreService.LoadStore();

            Title = "AI Hub";
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
                var src = HwndSource.FromHwnd(_hwnd);
                if (src != null)
                {
                    if (src.CompositionTarget != null)
                        src.CompositionTarget.BackgroundColor = Colors.Transparent;
                    src.AddHook(WndProc);
                }

                // Light mode + Windows 11 Mica backdrop + Native rounded corners
                int dark = 0;
                DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int));
                int corner = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int));
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
            Action run = () =>
            {
                ScreenshotSession.Start((msg, isErr) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (IsVisible && _webReady) SendToastToJs(msg, isErr);
                        else if (isErr && _trayIcon != null && _trayIcon.Visible)
                            _trayIcon.ShowBalloonTip(4000, "AI Hub 截图", msg, WinForms.ToolTipIcon.Warning);
                    }));
                });
            };

            if (delayMs > 0)
            {
                var dt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
                dt.Tick += (s, e) => { dt.Stop(); run(); };
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

                core.WebMessageReceived += OnWebMessageReceived;
                core.NavigationCompleted += (s2, e2) =>
                {
                    _webReady = e2.IsSuccess;
                    if (!_webReady) return;
                    PushWindowState();
                    RefreshAsync(true);
                };

                core.Navigate("https://aihub.local/index.html");
                SetupUiHotReload(uiDir);

                _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _pollTimer.Tick += (s3, e3) => RefreshAsync(false);
                _pollTimer.Start();

                _quotaTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _quotaTimer.Tick += (s3, e3) => CheckQuotaRefresh();
                _quotaTimer.Start();
                RefreshInstalledApps();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "初始化界面引擎失败：\n" + ex.Message, "AI Hub", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== TRAY & WAKEUP ====================

        private void SetupTrayIcon()
        {
            try
            {
                _trayIcon = new WinForms.NotifyIcon();
                _trayIcon.Text = "AI Hub · 点击打开";
                string icoPath = File.Exists(CoreService.PermanentIcon) ? CoreService.PermanentIcon : CoreService.BundledIcon;
                _trayIcon.Icon = File.Exists(icoPath) ? new Icon(icoPath) : SystemIcons.Application;

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
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
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
            if (_pollTimer != null) _pollTimer.Stop();
            if (_quotaTimer != null) _quotaTimer.Stop();
            if (_quotaBalloonTimer != null) _quotaBalloonTimer.Stop();
            if (_hwnd != IntPtr.Zero) HotkeyHelper.Unregister(_hwnd, HotkeyHelper.SCREENSHOT_HOTKEY_ID);
            try { if (_uiWatcher != null) _uiWatcher.Dispose(); } catch { }
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

        // ==================== STATE SYNC & BACKGROUND POLL ====================

        private void RefreshAsync(bool includeChrome)
        {
            if (_refreshInFlight) return;
            _refreshInFlight = true;
            int settingsRevision = _settingsRevision;
            bool refreshChrome = includeChrome || _chromeProfiles.Count == 0;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                AccountsStore freshStore = null;
                List<AccountViewModel> vms = null;
                ProxyStatusInfo proxy = null;
                List<ChromeProfileInfo> chromes = null;
                List<DesktopShortcutInfo> shortcuts = null;
                string refreshError = null;
                try
                {
                    freshStore = CoreService.LoadStore();
                    vms = CoreService.BuildAccountViewModels();
                    proxy = CoreService.GetProxyStatus();
                    if (refreshChrome)
                        chromes = CoreService.DiscoverChromeProfiles();
                    shortcuts = DesktopStashService.ScanShortcuts();
                }
                catch (Exception ex) { refreshError = ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _refreshInFlight = false;
                    if (_wakeStop) return;
                    if (freshStore != null)
                    {
                        if (settingsRevision != _settingsRevision) freshStore.settings = _store.settings;
                        _store = freshStore;
                    }
                    if (vms != null) UpdateUsageAccounts(vms);
                    if (proxy != null) _proxy = proxy;
                    if (chromes != null) _chromeProfiles = chromes;
                    if (shortcuts != null) _desktopShortcuts = shortcuts;

                    PushFullState();
                    if (IsVisible) PollNetwork();
                    CheckExpiryBalloon();
                    if (_quotaRefreshOnResume) CheckQuotaRefresh();

                    if (refreshError != null && refreshError != _lastRefreshError)
                        SendToastToJs(refreshError, true);
                    _lastRefreshError = refreshError;
                }));
            });
        }

        private async void RefreshInstalledApps()
        {
            if (_discoveringApps) return;
            _discoveringApps = true;
            try { _installedApps = await Task.Run(() => CoreService.DiscoverInstalledAiApps()); }
            catch { }
            finally
            {
                _discoveringApps = false;
                if (!_wakeStop) PushFullState();
            }
        }

        private async void PollNetwork()
        {
            if (_networkPolling || _networkWorking) return;
            _networkPolling = true;
            await _networkGate.WaitAsync();
            try
            {
                bool running = _proxy.Running;
                bool visible = IsVisible;
                var status = await Task.Run(() => _network.Read(running, visible));
                if (running == _proxy.Running) SetNetworkStatus(status);
            }
            finally
            {
                _networkGate.Release();
                _networkPolling = false;
            }
        }

        private void SetNetworkStatus(NetworkStatus status)
        {
            if (Json.Serialize(_networkStatus) == Json.Serialize(status)) return;
            _networkStatus = status;
            PushFullState();
            if (_quotaRefreshOnResume) CheckQuotaRefresh();
        }

        private async void RefreshAntigravity(bool automatic)
        {
            if (_antigravityRefresh != null) return;
            var previous = _antigravityUsage;
            _antigravityQuota.Begin(DateTimeOffset.UtcNow);
            using (var cancellation = new CancellationTokenSource())
            {
                _antigravityRefresh = cancellation;
                if (!automatic) _antigravityError = null;
                PushFullState();
                try
                {
                    var usage = await Task.Run(() => AntigravityQuota.Refresh(cancellation.Token));
                    if (_wakeStop) return;
                    _antigravityUsage = usage;
                    _antigravityError = null;
                    _antigravityQuota.Finish(DateTimeOffset.UtcNow, true, automatic);
                    if (previous != null && (string.IsNullOrWhiteSpace(previous.email) || string.IsNullOrWhiteSpace(usage.email)
                        || string.Equals(previous.email, usage.email, StringComparison.OrdinalIgnoreCase)))
                        QueueQuotaBalloon("antigravity", "Antigravity", AntigravityBuckets(previous), AntigravityBuckets(usage));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _antigravityQuota.Finish(DateTimeOffset.UtcNow, false, automatic);
                    if (!automatic) _antigravityError = ex is InvalidOperationException ? ex.Message : "暂时无法读取 Antigravity CLI 的额度。";
                }
                finally
                {
                    _antigravityRefresh = null;
                    PushFullState();
                }
            }
        }

        private void CheckQuotaRefresh()
        {
            if (_wakeStop || !_store.settings.quota_auto_refresh) return;
            if (!_proxy.Running && _networkStatus.state != "reachable")
            {
                try { if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return; }
                catch (System.Net.NetworkInformation.NetworkInformationException) { return; }
            }
            bool resume = _quotaRefreshOnResume;
            _quotaRefreshOnResume = false;
            bool inBackground = !IsVisible || WindowState == WindowState.Minimized;
            TimeSpan age = TimeSpan.FromMinutes(resume ? 30 : inBackground ? 240 : 60);
            bool onlyNonFullResets = inBackground && !resume;
            var now = DateTimeOffset.UtcNow;

            if (AntigravityQuota.IsInstalled && _antigravityRefresh == null && _antigravityQuota.IsDue(now,
                ParseQuotaTimestamp(_antigravityUsage == null ? null : _antigravityUsage.fetched_at), AntigravityBuckets(_antigravityUsage).Values, age, onlyNonFullResets))
                RefreshAntigravity(true);

            foreach (var account in _accounts)
            {
                string id = account.Record.id;
                if (account.Auth == null || !account.Auth.CanFetchUsage || _usageLoading.ContainsKey(id)) continue;
                UsageSnapshot snapshot;
                _usageCache.TryGetValue(id, out snapshot);
                if (CodexQuotaState(id).IsDue(now, snapshot == null ? (DateTimeOffset?)null : snapshot.FetchedAt,
                    CodexBuckets(snapshot).Values, age, onlyNonFullResets)) RefreshUsageForAccountAsync(account, true, true);
            }
        }

        private static DateTimeOffset? ParseQuotaTimestamp(string value)
        {
            DateTimeOffset timestamp;
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp)
                ? timestamp : (DateTimeOffset?)null;
        }

        private static Dictionary<string, QuotaBucketSnapshot> CodexBuckets(UsageSnapshot snapshot)
        {
            var buckets = new Dictionary<string, QuotaBucketSnapshot>();
            if (snapshot == null || !snapshot.Success) return buckets;
            foreach (var bucket in new[] { snapshot.Primary, snapshot.Secondary })
            {
                if (bucket == null) continue;
                string label = bucket.Label ?? "额度";
                buckets[label + ":" + bucket.LimitWindowSeconds] = new QuotaBucketSnapshot
                {
                    Label = label.Replace("限额", "额度"), Percent = bucket.LeftPercent, ResetAt = bucket.ResetAt
                };
            }
            return buckets;
        }

        private static Dictionary<string, QuotaBucketSnapshot> AntigravityBuckets(AntigravityUsage usage)
        {
            var buckets = new Dictionary<string, QuotaBucketSnapshot>();
            if (usage == null || usage.quota == null) return buckets;
            foreach (var entry in usage.quota)
            {
                var bucket = entry.Value;
                if (bucket == null || bucket.disabled) continue;
                string name = entry.Key.StartsWith("3p-", StringComparison.Ordinal) ? "Claude and GPT Models"
                    : entry.Key.StartsWith("gemini-argon-", StringComparison.Ordinal) ? "Gemini 4 Argon"
                    : entry.Key.StartsWith("gemini-", StringComparison.Ordinal) ? "Gemini Models" : entry.Key;
                string period = entry.Key.EndsWith("-5h", StringComparison.Ordinal) ? "5h 额度"
                    : entry.Key.EndsWith("-weekly", StringComparison.Ordinal) ? "每周额度" : "额度";
                buckets[entry.Key] = new QuotaBucketSnapshot
                {
                    Label = name + "（" + period + "）", Percent = bucket.remaining_fraction * 100.0, ResetAt = ParseQuotaTimestamp(bucket.reset_time)
                };
            }
            return buckets;
        }

        private QuotaRefreshState CodexQuotaState(string id)
        {
            QuotaRefreshState state;
            if (!_codexQuota.TryGetValue(id, out state)) _codexQuota[id] = state = new QuotaRefreshState();
            return state;
        }

        private void QueueQuotaBalloon(string owner, string name, Dictionary<string, QuotaBucketSnapshot> before, Dictionary<string, QuotaBucketSnapshot> after)
        {
            if (_trayIcon == null || !_store.settings.quota_auto_refresh) return;
            var buckets = QuotaRefreshState.CollectRefilled(owner, _quotaRefillHistory, before, after);
            if (buckets.Count == 0) return;
            CoreService.SaveQuotaRefillHistory(_quotaRefillHistory);
            _quotaBalloonLabels.Add(name + " · " + string.Join("、", buckets) + " 已恢复至 100%");
            if (_quotaBalloonTimer == null)
            {
                _quotaBalloonTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _quotaBalloonTimer.Tick += (sender, args) =>
                {
                    _quotaBalloonTimer.Stop();
                    string message = string.Join("\n", _quotaBalloonLabels);
                    _quotaBalloonLabels.Clear();
                    if (_trayIcon == null || !_store.settings.quota_auto_refresh) return;
                    try { _trayIcon.ShowBalloonTip(5000, "额度已刷新", message, WinForms.ToolTipIcon.Info); }
                    catch { }
                };
            }
            _quotaBalloonTimer.Start();
        }

        private async Task RunNetworkAction(string reqId, Func<object> work)
        {
            if (_networkWorking)
            {
                ReplyJs(reqId, false, null, "正在处理网络操作，请稍后再试。");
                return;
            }

            _networkWorking = true;
            await _networkGate.WaitAsync();
            try
            {
                var result = await Task.Run(work);
                var status = result as NetworkStatus;
                if (status != null) SetNetworkStatus(status);
                ReplyJs(reqId, true, result, null);
            }
            catch (InvalidOperationException ex)
            {
                ReplyJs(reqId, false, null, ex.Message);
            }
            catch (OperationCanceledException)
            {
                ReplyJs(reqId, false, null, "已取消测速。");
            }
            catch (Exception)
            {
                ReplyJs(reqId, false, null, "无法从本机 Clash 控制接口完成操作，请稍后重试。");
            }
            finally
            {
                _networkGate.Release();
                _networkWorking = false;
            }
        }

        private async void RunNetworkBatch(string reqId, string group)
        {
            if (_networkWorking || _networkBatch != null)
            {
                ReplyJs(reqId, false, null, "正在处理网络操作，请稍后再试。");
                return;
            }

            using (var cancellation = new CancellationTokenSource())
            {
                _networkBatch = cancellation;
                var token = cancellation.Token;
                var progress = new Progress<object>(update =>
                {
                    if (!token.IsCancellationRequested)
                        SendEventToJs("networkProbeProgress", new { reqId, update });
                });
                try
                {
                    await RunNetworkAction(reqId, () => _network.MeasureGroup(group, progress, token));
                }
                finally
                {
                    _networkBatch = null;
                }
            }
        }

        private void CheckExpiryBalloon()
        {
            if (_balloonShownThisSession || _trayIcon == null) return;
            if (_store == null || _store.settings == null || !_store.settings.expiry_balloon_alert) return;

            foreach (var vm in _accounts)
            {
                var cd = CountdownHelper.Compute(vm.Record);
                if (cd != null && cd.Level == "danger")
                {
                    _balloonShownThisSession = true;
                    string tip = cd.Expired
                        ? string.Format("「{0}」已到达回收截止时间，请及时处理。", vm.Record.name)
                        : string.Format("「{0}」距离回收不足 48 小时（剩余 {1}）", vm.Record.name, cd.CompactText);
                    try
                    {
                        if (!_trayIcon.Visible) _trayIcon.Visible = true;
                        _trayIcon.ShowBalloonTip(5000, "账号回收提醒", tip, WinForms.ToolTipIcon.Warning);
                    }
                    catch { }
                    break;
                }
            }
        }

        private void UpdateUsageAccounts(List<AccountViewModel> accounts)
        {
            var previous = _accounts.ToDictionary(a => a.Record.id, a => a.Auth);
            var current = accounts.ToDictionary(a => a.Record.id, a => a.Auth);
            foreach (string id in previous.Keys)
            {
                AuthInfo auth;
                if (!current.TryGetValue(id, out auth) || !AuthInfo.SameUsageIdentity(previous[id], auth))
                {
                    _usageCache.Remove(id);
                    _usageErrors.Remove(id);
                    _codexQuota.Remove(id);
                }
            }

            _accounts = accounts;
        }

        private void RefreshUsageForAccountAsync(AccountViewModel vm, bool silent, bool automatic = false)
        {
            if (vm == null || vm.Auth == null || !vm.Auth.CanFetchUsage) return;
            string id = vm.Record.id;
            if (_usageLoading.ContainsKey(id)) return;
            AuthInfo authCopy = vm.Auth;
            _usageLoading[id] = authCopy;
            var state = CodexQuotaState(id);
            state.Begin(DateTimeOffset.UtcNow);
            if (!automatic) _usageErrors.Remove(id);
            PushFullState();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                UsageSnapshot snap = CoreService.FetchCodexUsage(authCopy);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_wakeStop) return;
                    AuthInfo pending;
                    if (!_usageLoading.TryGetValue(id, out pending) || !object.ReferenceEquals(pending, authCopy)) return;
                    _usageLoading.Remove(id);
                    var current = _accounts.FirstOrDefault(a => a.Record.id == id);
                    if (current == null || !AuthInfo.SameUsageIdentity(current.Auth, authCopy)) return;
                    state.Finish(DateTimeOffset.UtcNow, snap.Success, automatic);
                    if (snap.Success)
                    {
                        UsageSnapshot previous;
                        _usageCache.TryGetValue(id, out previous);
                        _usageCache[id] = snap;
                        _usageErrors.Remove(id);
                        QueueQuotaBalloon("codex/" + id, "Codex · " + current.Record.name, CodexBuckets(previous), CodexBuckets(snap));
                    }
                    else if (!automatic) _usageErrors[id] = snap.ErrorMessage ?? "额度查询失败";
                    PushFullState();
                    if (!silent)
                        SendToastToJs(snap.Success ? ("已刷新「" + current.Record.name + "」额度") : ("额度查询失败：" + snap.ErrorMessage), !snap.Success);
                }));
            });
        }

        private void SendEventToJs(string type, object payload)
        {
            if (!_webReady || _webView == null || _webView.CoreWebView2 == null) return;
            try
            {
                var msg = new Dictionary<string, object> { { "type", type }, { "payload", payload } };
                _webView.CoreWebView2.PostWebMessageAsJson(Json.Serialize(msg));
            }
            catch { }
        }

        private void SendToastToJs(string text, bool isError)
        {
            if (!IsVisible) return;
            SendEventToJs("toast", new Dictionary<string, object> { { "text", text }, { "isError", isError } });
        }

        private void PushWindowState()
        {
            SendEventToJs("windowState", new Dictionary<string, object> { { "maximized", WindowState == WindowState.Maximized } });
        }

        private void PushFullState()
        {
            if (!IsVisible || !_webReady) return;

            var accList = new List<object>();
            foreach (var vm in _accounts)
            {
                string id = vm.Record.id;
                UsageSnapshot uSnap;
                _usageCache.TryGetValue(id, out uSnap);
                string usageError;
                _usageErrors.TryGetValue(id, out usageError);

                object usageObj = null;
                if (uSnap != null)
                {
                    usageObj = new Dictionary<string, object>
                    {
                        { "success", uSnap.Success },
                        { "automatic", CodexQuotaState(id).LastSuccessWasAutomatic },
                        { "fetched_at", uSnap.FetchedAt == default(DateTimeOffset) ? null : uSnap.FetchedAt.ToString("o") },
                        { "primary", SerializeUsageWindow(uSnap.Primary) },
                        { "secondary", SerializeUsageWindow(uSnap.Secondary) }
                    };
                }

                accList.Add(new Dictionary<string, object>
                {
                    { "id", vm.Record.id },
                    { "name", vm.Record.name },
                    { "is_default", vm.IsDefault },
                    { "account_type", vm.Record.account_type },
                    { "notes", vm.Record.notes ?? "" },
                    { "started_at", vm.Record.started_at },
                    { "recycle_at", vm.Record.recycle_at },
                    { "recycle_days", vm.Record.recycle_days },
                    { "config_mode", vm.Record.config_mode ?? "clone" },
                    { "disable_memories", vm.Record.disable_memories },
                    { "running", vm.Running },
                    { "window_visible", vm.WindowVisible },
                    { "permission_mode", vm.PermissionMode },
                    { "disk_size", vm.DiskSizeBytes.HasValue ? CountdownHelper.FormatBytes(vm.DiskSizeBytes) : "" },
                    { "codex_home", vm.CodexHome },
                    { "auth", new Dictionary<string, object>
                        {
                            { "logged_in", vm.Auth != null && vm.Auth.LoggedIn },
                            { "can_fetch_usage", vm.Auth != null && vm.Auth.CanFetchUsage },
                            { "email", vm.Auth != null ? vm.Auth.Email : null },
                            { "display_name", vm.Auth != null ? vm.Auth.DisplayName : null },
                            { "plan_type", vm.Auth != null ? vm.Auth.PlanType : null },
                            { "subscription_start", vm.Auth != null && vm.Auth.SubscriptionStart.HasValue ? vm.Auth.SubscriptionStart.Value.ToString("yyyy-MM-ddTHH:mm:sszzz") : null },
                            { "subscription_until", vm.Auth != null && vm.Auth.SubscriptionUntil.HasValue ? vm.Auth.SubscriptionUntil.Value.ToString("yyyy-MM-ddTHH:mm:sszzz") : null }
                        }
                    },
                    { "usage", usageObj },
                    { "usage_error", usageError },
                    { "usage_loading", _usageLoading.ContainsKey(id) }
                });
            }

            var secList = new List<object>();
            ToolItem antigravityRef = null;
            var discovered = _installedApps.ToDictionary(item => item.id);
            var remaining = new Dictionary<string, ToolItem>(discovered);
            if (_store != null && _store.tool_sections != null)
            {
                foreach (var sec in _store.tool_sections)
                {
                    if (sec == null || sec.items == null) continue;
                    var items = new List<object>();
                    foreach (var storedTool in sec.items)
                    {
                        if (storedTool == null || string.IsNullOrEmpty(storedTool.id)) continue;
                        ToolItem it;
                        if (!discovered.TryGetValue(storedTool.id, out it)) it = storedTool;
                        remaining.Remove(it.id);
                        if (it.kind == "exe" && !File.Exists(it.target) && !CoreService.IsToolRunning(it)) continue;
                        if (it.id == "tool-antigravity" || (it.name ?? "").IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) >= 0)
                            antigravityRef = it;
                        items.Add(SerializeTool(it));
                    }
                    secList.Add(new Dictionary<string, object>
                    {
                        { "id", sec.id },
                        { "title", sec.title },
                        { "items", items }
                    });
                }
            }
            if (remaining.Count > 0)
            {
                ToolItem antigravity;
                if (remaining.TryGetValue("tool-antigravity", out antigravity)) antigravityRef = antigravity;
                secList.Add(new { id = "discovered-desktop-ai", title = "桌面 AI 应用", items = remaining.Values.Select(SerializeTool).ToArray() });
            }

            var chromeList = new List<object>();
            foreach (var cp in _chromeProfiles)
            {
                chromeList.Add(new Dictionary<string, object>
                {
                    { "dir", cp.DirectoryName },
                    { "name", cp.DisplayName },
                    { "email", cp.Email }
                });
            }

            var shortcutList = new List<object>();
            foreach (var sc in _desktopShortcuts)
            {
                shortcutList.Add(new Dictionary<string, object>
                {
                    { "id", sc.Id },
                    { "name", sc.Name },
                    { "file_name", sc.FileName },
                    { "scope", sc.Scope },
                    { "category", sc.Category },
                    { "hidden", sc.Hidden },
                    { "requires_admin", sc.RequiresAdmin }
                });
            }

            var statePayload = new Dictionary<string, object>
            {
                { "version", CoreService.AppVersion },
                { "safe_profile_actions", true },
                { "pending_update", LocalUpdate.IsPending },
                { "brand_icon", CoreService.BrandIconUrl },
                { "voice_recorder_hotkey", VoiceRecorderShortcut.Display },
                { "quick_ledger_forms", CoreService.ReadQuickLedgerForms().Keys.ToArray() },
                { "settings", new Dictionary<string, object>
                    {
                        { "minimize_to_tray", _store.settings.minimize_to_tray },
                        { "expiry_balloon_alert", _store.settings.expiry_balloon_alert },
                        { "quota_auto_refresh", _store.settings.quota_auto_refresh },
                        { "screenshot_enabled", _store.settings.screenshot_enabled },
                        { "screenshot_hotkey", _store.settings.screenshot_hotkey ?? "Ctrl+Shift+A" }
                    }
                },
                { "screenshot", new Dictionary<string, object>
                    {
                        { "enabled", _store.settings.screenshot_enabled },
                        { "hotkey", _store.settings.screenshot_hotkey ?? "Ctrl+Shift+A" },
                        { "registered", _hotkeyRegistered },
                        { "error", _hotkeyError ?? "" }
                    }
                },
                { "desktop_shortcuts", shortcutList },
                { "installed_apps", _installedApps.Select(item => new
                    {
                        item.id,
                        item.name,
                        icon_url = CoreService.GetCachedIconUrl(item.target, item.kind),
                        uninstall_method = item.kind == "msix" ? "系统 MSIX 包卸载" : "调用软件官方卸载程序"
                    }).ToArray() },
                { "network", _networkStatus },
                { "network_status_refresh", true },
                { "network_japan_picker", true },
                { "proxy", new Dictionary<string, object>
                    {
                        { "installed", _proxy.Installed },
                        { "running", _proxy.Running },
                        { "system_proxy", _proxy.SystemProxyOn },
                        { "tun", _proxy.TunOn },
                        { "port", _proxy.MixedPort },
                        { "port_listening", _proxy.PortListening },
                        { "profile", _proxy.ActiveProfileName ?? "" },
                        { "icon_url", _proxy.Installed ? CoreService.GetCachedIconUrl(_proxy.ExePath, "exe") : "" }
                    }
                },
                { "accounts", accList },
                { "tool_sections", secList },
                { "chrome_profiles", chromeList },
                { "chrome_available", CoreService.FindChromeExe() != null },
                { "antigravity", new Dictionary<string, object>
                    {
                        { "id", antigravityRef == null ? null : antigravityRef.id },
                        { "running", antigravityRef != null && CoreService.IsToolRunning(antigravityRef) },
                        { "icon_url", antigravityRef == null ? null : CoreService.GetCachedIconUrl(antigravityRef.target, antigravityRef.kind) },
                        { "cli_installed", AntigravityQuota.IsInstalled },
                        { "usage", _antigravityUsage },
                        { "loading", _antigravityRefresh != null },
                        { "automatic", _antigravityQuota.LastSuccessWasAutomatic },
                        { "error", _antigravityError }
                    }
                }
            };

            SendEventToJs("state", statePayload);
            if (_openBatchExitPending)
            {
                _openBatchExitPending = false;
                SendEventToJs("openBatchExit", null);
            }
        }

        private static object SerializeTool(ToolItem tool)
        {
            return new
            {
                tool.id, tool.name, tool.kind, tool.subtitle, tool.uninstall_hint,
                running = CoreService.IsToolRunning(tool), icon_url = CoreService.GetCachedIconUrl(tool.target, tool.kind)
            };
        }

        private object SerializeUsageWindow(UsageWindowInfo w)
        {
            if (w == null) return null;
            return new Dictionary<string, object>
            {
                { "label", w.Label },
                { "left_percent", Math.Round(w.LeftPercent) },
                { "used_percent", Math.Round(w.UsedPercent) },
                { "reset_text", w.FormatResetCountdown() }
            };
        }

        // ==================== JS -> C# BRIDGE HANDLER ====================

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!string.Equals(e.Source, "https://aihub.local/index.html", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var msg = Json.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>;
                if (msg == null) return;
                string action = AuthParser.GetStr(msg, "action") ?? "";
                string reqId = AuthParser.GetStr(msg, "reqId") ?? "";
                var data = AuthParser.GetDict(msg, "data") ?? new Dictionary<string, object>();

                HandleJsAction(action, reqId, data);
            }
            catch (Exception ex)
            {
                SendToastToJs("操作异常：" + ex.Message, true);
            }
        }

        private void ReplyJs(string reqId, bool ok, object result, string error)
        {
            if (string.IsNullOrEmpty(reqId)) return;
            SendEventToJs("reply", new Dictionary<string, object>
            {
                { "reqId", reqId },
                { "ok", ok },
                { "result", result },
                { "error", error }
            });
        }

        private async void HandleJsAction(string action, string reqId, Dictionary<string, object> data)
        {
            try
            {
                switch (action)
                {
                    case "dragWindow":
                        ReleaseCapture();
                        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
                        break;

                    case "minimize":
                        WindowState = WindowState.Minimized;
                        break;

                    case "toggleMaximize":
                        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                        break;

                    case "close":
                        Close();
                        break;

                    case "uiReady":
                        LocalUpdate.SignalReady(_updateToken);
                        break;

                    case "applyLocalUpdate":
                        if (ScreenshotSession.IsActive) throw new InvalidOperationException("请先结束当前截图，再重启更新。");
                        if (_startingUpdate) break;
                        LocalUpdate.Start();
                        _startingUpdate = true;
                        ForceQuit();
                        break;

                    case "forceQuit":
                        ForceQuit();
                        break;

                    case "refresh":
                        RefreshAsync(false);
                        if (AuthParser.GetStr(data, "toast") == "1") SendToastToJs("状态已刷新", false);
                        break;

                    case "refreshAllUsage":
                        int triggered = 0;
                        foreach (var vm in _accounts)
                        {
                            if (vm.Auth != null && vm.Auth.CanFetchUsage && !_usageLoading.ContainsKey(vm.Record.id)) { RefreshUsageForAccountAsync(vm, true); triggered++; }
                        }
                        SendToastToJs(triggered > 0 ? "正在刷新各账号剩余额度..." : (_usageLoading.Count > 0 ? "额度刷新正在进行" : "暂无可查询额度的账号"), triggered == 0);
                        break;

                    case "refreshAntigravity":
                        RefreshAntigravity(false);
                        break;

                    case "refreshUsage":
                        {
                            string id = AuthParser.GetStr(data, "id");
                            var vm = _accounts.FirstOrDefault(a => a.Record.id == id);
                            if (vm != null) RefreshUsageForAccountAsync(vm, false);
                            break;
                        }

                    case "refreshNetwork":
                        {
                            int mixedPort = _proxy.PortListening ? _proxy.MixedPort : 0;
                            var clashExe = _proxy.ExePath;
                            await RunNetworkAction(reqId, () => _network.Refresh(mixedPort, clashExe));
                            break;
                        }

                    case "listNetworkNodes":
                        await RunNetworkAction(reqId, () => _network.ListNodes(AuthParser.GetStr(data, "group")));
                        break;

                    case "switchNetworkNode":
                        await RunNetworkAction(reqId, () => _network.SwitchNode(AuthParser.GetStr(data, "group"), AuthParser.GetStr(data, "node")));
                        break;

                    case "measureNetworkGroup":
                        RunNetworkBatch(reqId, AuthParser.GetStr(data, "group"));
                        break;

                    case "cancelNetworkBatch":
                        if (_networkBatch != null) _networkBatch.Cancel();
                        break;

                    case "proxyOpen":
                        CoreService.OpenOrStartClashVerge();
                        SendToastToJs(_proxy.Running ? "已唤出 Clash Verge" : "正在启动 Clash Verge...", false);
                        ScheduleDelayedRefresh(1800);
                        break;

                    case "proxyStop":
                        CoreService.StopClashVerge();
                        SendToastToJs("已关闭 Clash Verge", false);
                        RefreshAsync(false);
                        break;

                    case "proxyRestart":
                        SendToastToJs("正在重启 Clash Verge...", false);
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { CoreService.RestartClashVerge(); } catch { }
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                SendToastToJs("已重启 Clash Verge", false);
                                RefreshAsync(false);
                            }));
                        });
                        break;

                    case "launchAccount":
                        {
                            string id = AuthParser.GetStr(data, "id");
                            var vm = _accounts.FirstOrDefault(a => a.Record.id == id);
                            if (vm == null) throw new InvalidOperationException("未找到该账号。");
                            if (vm.Running) CoreService.OpenRunningProfile(id);
                            else CoreService.LaunchProfile(id);
                            SendToastToJs("正在打开「" + vm.Record.name + "」...", false);
                            ScheduleDelayedRefresh(1800);
                            break;
                        }

                    case "stopAccount":
                        {
                            string id = AuthParser.GetStr(data, "id");
                            var vm = _accounts.FirstOrDefault(a => a.Record.id == id);
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                string error = null;
                                try { CoreService.StopProfileProcesses(id); }
                                catch (Exception ex) { error = ex.Message; }
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    SendToastToJs(error ?? ("已关闭「" + (vm != null ? vm.Record.name : id) + "」"), error != null);
                                    RefreshAsync(false);
                                }));
                            });
                            break;
                        }

                    case "createAccount":
                        {
                            string name = (AuthParser.GetStr(data, "name") ?? "").Trim();
                            string accType = AuthParser.GetStr(data, "account_type") ?? "recycled";
                            string mode = AuthParser.GetStr(data, "config_mode") ?? "clone";
                            bool disableMem = Convert.ToBoolean(data.ContainsKey("disable_memories") ? data["disable_memories"] : true);
                            bool launchNow = Convert.ToBoolean(data.ContainsKey("launch_now") ? data["launch_now"] : true);
                            string notes = AuthParser.GetStr(data, "notes") ?? "";

                            DateTimeOffset startedAt = DateTimeOffset.Now;
                            DateTimeOffset? recycleAt = null;
                            double? recycleDays = null;

                            if (accType == "recycled")
                            {
                                DateTimeOffset dto;
                                if (DateTimeOffset.TryParse(AuthParser.GetStr(data, "started_at"), out dto)) startedAt = dto;
                                if (DateTimeOffset.TryParse(AuthParser.GetStr(data, "recycle_at"), out dto)) recycleAt = dto;
                                recycleDays = AuthParser.GetDouble(data, "recycle_days") ?? 25.0;
                                if (!recycleAt.HasValue) recycleAt = startedAt.AddDays(recycleDays.Value);
                            }

                            string permissionMode = AuthParser.GetStr(data, "permission_mode");
                            CoreService.CreateAccount(name, accType, startedAt, recycleAt, recycleDays, mode, disableMem, notes, launchNow, permissionMode);
                            ReplyJs(reqId, true, null, null);
                            SendToastToJs(launchNow ? ("已创建「" + name + "」，请在 Codex 窗口登录") : ("已创建「" + name + "」"), false);
                            RefreshAsync(true);
                            break;
                        }

                    case "updateAccount":
                        {
                            string id = AuthParser.GetStr(data, "id");
                            var vm = _accounts.FirstOrDefault(a => a.Record.id == id);
                            if (vm == null || vm.IsDefault) throw new InvalidOperationException("未找到可编辑账号");

                            var rec = new AccountRecord
                            {
                                id = vm.Record.id, created_at = vm.Record.created_at, config_mode = vm.Record.config_mode,
                                disable_memories = vm.Record.disable_memories, started_at = vm.Record.started_at,
                                name = (AuthParser.GetStr(data, "name") ?? vm.Record.name ?? "").Trim(),
                                account_type = AuthParser.GetStr(data, "account_type") ?? vm.Record.account_type,
                                notes = (AuthParser.GetStr(data, "notes") ?? "").Trim()
                            };
                            if (string.IsNullOrEmpty(rec.name) || (rec.account_type != "recycled" && rec.account_type != "permanent"))
                                throw new InvalidOperationException("请填写账号名称并选择账号类型。");
                            if (rec.account_type == "recycled")
                            {
                                rec.started_at = AuthParser.GetStr(data, "started_at") ?? vm.Record.started_at;
                                rec.recycle_at = AuthParser.GetStr(data, "recycle_at") ?? vm.Record.recycle_at;
                                rec.recycle_days = AuthParser.GetDouble(data, "recycle_days") ?? vm.Record.recycle_days ?? 25.0;
                                DateTimeOffset start, end;
                                if (!DateTimeOffset.TryParse(rec.started_at, out start) || !DateTimeOffset.TryParse(rec.recycle_at, out end)
                                    || end <= start || rec.recycle_days <= 0 || double.IsNaN(rec.recycle_days.Value) || double.IsInfinity(rec.recycle_days.Value))
                                    throw new InvalidOperationException("请填写有效的回收期限，截止时间须晚于起算时间。");
                            }
                            string mode = AuthParser.GetStr(data, "permission_mode");
                            CoreService.UpdateAccount(rec, mode == vm.PermissionMode ? null : mode);
                            ReplyJs(reqId, true, null, null);
                            SendToastToJs("已保存「" + rec.name + "」", false);
                            RefreshAsync(false);
                            break;
                        }

                    case "saveProfilePermissionMode":
                        {
                            string id = AuthParser.GetStr(data, "id");
                            string mode = AuthParser.GetStr(data, "mode");
                            CoreService.SaveProfilePermissionMode(id, mode);
                            ReplyJs(reqId, true, null, null);
                            SendToastToJs("已保存此账号的本地权限。Codex 使用「自定义」时读取这份配置。", false);
                            RefreshAsync(false);
                            break;
                        }

                    case "syncConfig":
                        {
                            string id = AuthParser.GetStr(data, "id");
                            var vm = _accounts.FirstOrDefault(a => a.Record.id == id);
                            if (vm == null || vm.IsDefault) break;
                            string mode = vm.Record.config_mode == "junction_skills" ? "junction_skills" : "clone";
                            var synced = CoreService.PopulateProfileConfig(vm.CodexHome, mode, false);
                            SendToastToJs("已检查主号指引与 Skills：" + (synced.Count > 0 ? string.Join("、", synced) : "无变化"), false);
                            break;
                        }

                    case "wipeAccount":
                        {
                            string id = AuthParser.GetStr(data, "id");
                            var vm = _accounts.FirstOrDefault(a => a.Record.id == id);
                            string accName = vm != null ? vm.Record.name : id;
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    CoreService.WipeProfile(id);
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        ReplyJs(reqId, true, null, null);
                                        SendToastToJs("已删除「" + accName + "」的本地数据", false);
                                        RefreshAsync(true);
                                    }));
                                }
                                catch (Exception ex)
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        ReplyJs(reqId, false, null, ex.Message);
                                        SendToastToJs("删除本地数据失败：" + ex.Message, true);
                                        RefreshAsync(false);
                                    }));
                                }
                            });
                            break;
                        }

                    case "launchTool":
                        {
                            var tool = FindToolById(AuthParser.GetStr(data, "id"));
                            if (tool == null) throw new InvalidOperationException("未找到该应用");
                            CoreService.LaunchToolItem(tool);
                            SendToastToJs("正在启动「" + tool.name + "」...", false);
                            ScheduleDelayedRefresh(1800);
                            break;
                        }

                    case "stopTool":
                        {
                            var tool = FindToolById(AuthParser.GetStr(data, "id"));
                            if (tool == null) break;
                            await Task.Run(() => CoreService.StopToolItem(tool));
                            if (CoreService.IsToolRunning(tool)) throw new InvalidOperationException(tool.name + " 仍在运行。");
                            SendToastToJs("已关闭「" + tool.name + "」", false);
                            RefreshAsync(false);
                            break;
                        }

                    case "uninstallTool":
                        {
                            var tool = _installedApps.FirstOrDefault(item => item.id == AuthParser.GetStr(data, "id"));
                            if (tool == null) throw new InvalidOperationException("未找到该应用");
                            string msg = CoreService.UninstallSoftware(tool);
                            ReplyJs(reqId, true, msg, null);
                            SendToastToJs(msg, false);
                            RefreshInstalledApps();
                            break;
                        }

                    case "launchChromeUrl":
                        {
                            string tool = AuthParser.GetStr(data, "tool");
                            string url = tool == "gemini" ? "https://gemini.google.com/app" :
                                tool == "ai-studio" ? "https://aistudio.google.com/" : null;
                            if (url == null) throw new InvalidOperationException("未知的网页工具。");
                            string dir = AuthParser.GetStr(data, "dir");
                            if (!_chromeProfiles.Any(profile => profile.DirectoryName == dir))
                                throw new InvalidOperationException("请先选择可用的 Google 身份。");
                            CoreService.LaunchChromeProfileUrl(dir, url);
                            SendToastToJs("已在对应 Chrome 账号中打开", false);
                            break;
                        }

                    case "batchExit":
                        {
                            var codexIds = ToStringList(data, "codexIds").Where(id => _accounts.Any(account => account.Record.id == id)).Distinct().ToArray();
                            var tools = ToStringList(data, "toolIds").Distinct().Select(FindToolById).Where(t => t != null).ToArray();
                            bool stopProxy = data.ContainsKey("stopProxy") && Convert.ToBoolean(data["stopProxy"]);
                            bool quitAiHub = data.ContainsKey("quitAiHub") && Convert.ToBoolean(data["quitAiHub"]);
                            if (_batchExitInFlight) throw new InvalidOperationException("正在退出所选程序，请稍候。");
                            _batchExitInFlight = true;
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                int count = 0;
                                var failures = new List<string>();
                                foreach (string cid in codexIds)
                                {
                                    try { CoreService.StopProfileProcesses(cid); count++; }
                                    catch (Exception ex) { failures.Add(ex.Message); }
                                }
                                foreach (var tool in tools)
                                {
                                    try
                                    {
                                        CoreService.StopToolItem(tool);
                                        if (CoreService.IsToolRunning(tool)) throw new InvalidOperationException(tool.name + " 仍在运行。");
                                        count++;
                                    }
                                    catch (Exception ex) { failures.Add(ex.Message); }
                                }
                                if (stopProxy) { try { CoreService.StopClashVerge(); count++; } catch (Exception ex) { failures.Add(ex.Message); } }

                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    _batchExitInFlight = false;
                                    ReplyJs(reqId, true, count, null);
                                    if (quitAiHub) { ForceQuit(); return; }
                                    string summary = string.Format("已退出 {0} 项程序", count);
                                    if (failures.Count > 0) summary += "，有 " + failures.Count + " 项需手动退出。" + failures[0];
                                    SendToastToJs(summary, failures.Count > 0);
                                    RefreshAsync(false);
                                }));
                            });
                            break;
                        }

                    case "updateSettings":
                        {
                            string hotkey = (AuthParser.GetStr(data, "screenshot_hotkey") ?? "").Trim();
                            bool hotkeyChanged = data.ContainsKey("screenshot_enabled") || hotkey.Length > 0;
                            _store.settings = CoreService.UpdateLauncherSettings(settings =>
                            {
                                if (data.ContainsKey("minimize_to_tray")) settings.minimize_to_tray = Convert.ToBoolean(data["minimize_to_tray"]);
                                if (data.ContainsKey("expiry_balloon_alert")) settings.expiry_balloon_alert = Convert.ToBoolean(data["expiry_balloon_alert"]);
                                if (data.ContainsKey("quota_auto_refresh")) settings.quota_auto_refresh = Convert.ToBoolean(data["quota_auto_refresh"]);
                                if (data.ContainsKey("screenshot_enabled")) settings.screenshot_enabled = Convert.ToBoolean(data["screenshot_enabled"]);
                                if (hotkey.Length > 0) settings.screenshot_hotkey = hotkey;
                            });
                            _settingsRevision++;
                            if (data.ContainsKey("quota_auto_refresh") && _store.settings.quota_auto_refresh)
                            {
                                _quotaRefreshOnResume = true;
                                CheckQuotaRefresh();
                            }
                            if (hotkeyChanged) SyncScreenshotHotkey();
                            PushFullState();
                            if (hotkeyChanged && _store.settings.screenshot_enabled && !_hotkeyRegistered)
                                SendToastToJs(_hotkeyError, true);
                            else
                                SendToastToJs("设置已保存", false);
                            break;
                        }

                    case "startScreenshot":
                        {
                            StartScreenshotNow(140);
                            break;
                        }

                    case "startVoiceRecorder":
                        {
                            uint screenshotModifiers, screenshotKey, recorderModifiers, recorderKey;
                            if (_hotkeyRegistered
                                && HotkeyHelper.TryParse(_store.settings.screenshot_hotkey, out screenshotModifiers, out screenshotKey)
                                && HotkeyHelper.TryParse(VoiceRecorderShortcut.Display, out recorderModifiers, out recorderKey)
                                && screenshotModifiers == recorderModifiers && screenshotKey == recorderKey)
                                throw new InvalidOperationException("AI Hub 截图正在使用录音笔的快捷键，请先更换截图快捷键。");
                            VoiceRecorderShortcut.Send();
                            break;
                        }

                    case "reregisterHotkey":
                        {
                            SyncScreenshotHotkey();
                            PushFullState();
                            if (_hotkeyRegistered)
                                SendToastToJs("截图快捷键 " + _store.settings.screenshot_hotkey + " 注册成功", false);
                            else if (!_store.settings.screenshot_enabled)
                                SendToastToJs("截图快捷键当前处于关闭状态", false);
                            else
                                SendToastToJs(_hotkeyError, true);
                            break;
                        }

                    case "toggleDesktopShortcut":
                        {
                            string id = AuthParser.GetStr(data, "id");
                            bool hide = data.ContainsKey("hide") && Convert.ToBoolean(data["hide"]);
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    string msg = DesktopStashService.SetShortcutHidden(id, hide);
                                    var refreshed = DesktopStashService.ScanShortcuts();
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        _desktopShortcuts = refreshed;
                                        PushFullState();
                                        SendToastToJs(msg, false);
                                    }));
                                }
                                catch (Exception ex)
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        SendToastToJs(ex.Message, true);
                                        RefreshAsync(false);
                                    }));
                                }
                            });
                            break;
                        }

                    case "hideAllDesktopShortcuts":
                        {
                            string category = AuthParser.GetStr(data, "category") ?? "";
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    string msg = DesktopStashService.BatchHideCategory(category);
                                    var refreshed = DesktopStashService.ScanShortcuts();
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        _desktopShortcuts = refreshed;
                                        PushFullState();
                                        SendToastToJs(msg, false);
                                    }));
                                }
                                catch (Exception ex)
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        SendToastToJs(ex.Message, true);
                                        RefreshAsync(false);
                                    }));
                                }
                            });
                            break;
                        }

                    case "restoreAllDesktopShortcuts":
                        {
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    string msg = DesktopStashService.BatchRestoreAll();
                                    var refreshed = DesktopStashService.ScanShortcuts();
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        _desktopShortcuts = refreshed;
                                        PushFullState();
                                        SendToastToJs(msg, false);
                                    }));
                                }
                                catch (Exception ex)
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        SendToastToJs(ex.Message, true);
                                        RefreshAsync(false);
                                    }));
                                }
                            });
                            break;
                        }

                    case "refreshInstalledApps":
                        RefreshInstalledApps();
                        break;

                    case "openLedgerForm":
                        string formUrl;
                        if (!CoreService.ReadQuickLedgerForms().TryGetValue(AuthParser.GetStr(data, "id") ?? "", out formUrl))
                        {
                            PushFullState();
                            throw new InvalidOperationException("记账入口已变更，请检查本地 quick-ledger.json。");
                        }
                        CoreService.LaunchToolItem(new ToolItem { kind = "url", target = formUrl });
                        break;

                    case "getChangelog":
                        ReplyJs(reqId, true, CoreService.ReadChangelogText(), null);
                        break;
                }
            }
            catch (Exception ex)
            {
                ReplyJs(reqId, false, null, ex.Message);
                SendToastToJs(ex.Message, true);
            }
        }

        private ToolItem FindToolById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var installed = _installedApps.FirstOrDefault(item => item.id == id);
            if (installed != null) return installed;
            if (_store == null || _store.tool_sections == null) return null;
            foreach (var sec in _store.tool_sections)
                if (sec != null && sec.items != null)
                    foreach (var it in sec.items)
                        if (it != null && it.id == id) return it;
            return null;
        }

        private List<string> ToStringList(Dictionary<string, object> data, string key)
        {
            var res = new List<string>();
            object val;
            if (!data.TryGetValue(key, out val) || val == null) return res;
            var arr = val as object[];
            if (arr != null)
            {
                foreach (var x in arr) if (x != null) res.Add(x.ToString());
                return res;
            }
            var list = val as System.Collections.IEnumerable;
            if (list != null)
            {
                foreach (var x in list) if (x != null) res.Add(x.ToString());
            }
            return res;
        }

        private void ScheduleDelayedRefresh(int ms)
        {
            var d = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            d.Tick += (s, e) => { d.Stop(); RefreshAsync(false); };
            d.Start();
        }
    }
}
