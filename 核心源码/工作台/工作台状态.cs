using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace CodexLauncher
{
    public partial class MainWindow
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 20 * 1024 * 1024 };

        private AccountsStore _store;
        private int _settingsRevision;
        private int _backlogRevision;
        private List<AccountViewModel> _accounts = new List<AccountViewModel>();
        private ProxyStatusInfo _proxy = new ProxyStatusInfo();
        private readonly ClashNetwork _network = new ClashNetwork();
        private readonly SemaphoreSlim _networkGate = new SemaphoreSlim(1);
        private volatile NetworkStatus _networkStatus = new NetworkStatus { state = "unknown" };
        private bool _networkPolling;
        private bool _networkWorking;
        private CancellationTokenSource _networkBatch;
        private List<ChromeProfileInfo> _chromeProfiles = new List<ChromeProfileInfo>();
        private bool _openBatchExitPending;
        private bool _batchExitInFlight;
        private bool _discoveringApps;
        private List<ToolItem> _installedApps = new List<ToolItem>();
        private string _lastRefreshError;

        private bool _refreshInFlight = false;
        private bool _refreshQueued = false;
        private bool _refreshQueuedChrome = false;
        private DispatcherTimer _pollTimer;
        private List<DesktopShortcutInfo> _desktopShortcuts = new List<DesktopShortcutInfo>();
        private List<BacklogItem> _backlogItems = new List<BacklogItem>();
        private List<ProjectViewModel> _projects = new List<ProjectViewModel>();
        private readonly ProjectPreviewManager _projectPreviews = new ProjectPreviewManager();
        private List<string> _projectRoots = new List<string>();
        private bool _projectsLoaded;
        private bool _projectsRefreshing;

        private void RefreshAsync(bool includeChrome)
        {
            if (_refreshInFlight)
            {
                _refreshQueued = true;
                if (includeChrome) _refreshQueuedChrome = true;
                return;
            }
            _refreshInFlight = true;
            _refreshQueued = false;
            _refreshQueuedChrome = false;
            int settingsRevision = _settingsRevision;
            int backlogRevision = _backlogRevision;
            bool refreshChrome = includeChrome || _chromeProfiles.Count == 0;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                AccountsStore freshStore = null;
                List<AccountViewModel> vms = null;
                ProxyStatusInfo proxy = null;
                List<ChromeProfileInfo> chromes = null;
                List<DesktopShortcutInfo> shortcuts = null;
                List<BacklogItem> backlogItems = null;
                List<ProjectViewModel> projects = null;
                List<string> projectRoots = null;
                bool loadProjects = !_projectsLoaded;
                string refreshError = null;
                try
                {
                    freshStore = CoreService.LoadStore();
                    vms = CoreService.BuildAccountViewModels(freshStore);
                    proxy = CoreService.GetProxyStatus();
                    if (refreshChrome)
                        chromes = CoreService.DiscoverChromeProfiles();
                    shortcuts = DesktopStashService.ScanShortcuts();
                    backlogItems = BacklogStorage.LoadItems();
                    if (loadProjects)
                    {
                        projectRoots = ProjectStorage.LoadScanRoots();
                        projects = ProjectStorage.BuildViewModels();
                    }
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
                        _quotaAutoRefreshEnabled = _store.settings.quota_auto_refresh;
                    }
                    if (vms != null) UpdateUsageAccounts(vms);
                    if (proxy != null) _proxy = proxy;
                    if (chromes != null) _chromeProfiles = chromes;
                    if (shortcuts != null) _desktopShortcuts = shortcuts;
                    if (backlogItems != null && backlogRevision == _backlogRevision) _backlogItems = backlogItems;
                    if (projectRoots != null) _projectRoots = projectRoots;
                    if (projects != null)
                    {
                        try
                        {
                            _projects = ProjectStorage.ApplySavedOrder(projects);
                            _projectsLoaded = true;
                        }
                        catch (InvalidDataException ex) { refreshError = ex.Message; }
                    }

                    PushFullState();
                    SyncTrayStatus();
                    TryAutoApplyPendingUpdate();
                    if (IsVisible) PollNetwork();
                    CheckExpiryBalloon();
                    if (_quotaRefreshOnResume) CheckQuotaRefresh();

                    if (refreshError != null && refreshError != _lastRefreshError)
                        SendToastToJs(refreshError, true);
                    _lastRefreshError = refreshError;

                    if (_refreshQueued && !_wakeStop)
                    {
                        bool nextChrome = _refreshQueuedChrome;
                        _refreshQueued = false;
                        _refreshQueuedChrome = false;
                        RefreshAsync(nextChrome);
                    }
                }));
            });
        }

        private async void RefreshProjectsAsync(string reqId = null, bool toast = false)
        {
            if (_projectsRefreshing)
            {
                if (!string.IsNullOrEmpty(reqId)) ReplyJs(reqId, true, null, null);
                return;
            }
            _projectsRefreshing = true;
            try
            {
                var roots = await Task.Run(() => ProjectStorage.LoadScanRoots());
                var list = await Task.Run(() => ProjectStorage.BuildViewModels());
                if (!_wakeStop)
                {
                    _projectRoots = roots;
                    _projects = ProjectStorage.ApplySavedOrder(list);
                    _projectsLoaded = true;
                    PushFullState();
                    if (toast) SendToastToJs("项目状态已刷新", false);
                }
                if (!string.IsNullOrEmpty(reqId)) ReplyJs(reqId, true, null, null);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(reqId)) ReplyJs(reqId, false, null, ex.Message);
                else SendToastToJs(ex.Message, true);
            }
            finally
            {
                _projectsRefreshing = false;
            }
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
            SyncTrayStatus();
            if (_quotaRefreshOnResume) CheckQuotaRefresh();
        }

        private void SyncTrayStatus()
        {
            try
            {
                bool hasDanger = false;
                if (_proxy != null && _proxy.Running && _networkStatus != null &&
                    (_networkStatus.state == "timeout" || _networkStatus.state == "unavailable"))
                {
                    hasDanger = true;
                }
                if (!hasDanger && _antigravityUsage != null && _antigravityUsage.quota != null)
                {
                    foreach (var kv in _antigravityUsage.quota)
                    {
                        var b = kv.Value;
                        if (b != null && !b.disabled && b.remaining_fraction.HasValue && b.remaining_fraction.Value * 100.0 <= 10.0)
                        {
                            hasDanger = true;
                            break;
                        }
                    }
                }
                if (!hasDanger && _usageCache != null)
                {
                    foreach (var kv in _usageCache)
                    {
                        var snap = kv.Value;
                        if (snap == null || !snap.Success) continue;
                        if ((snap.Primary != null && snap.Primary.LeftPercent <= 10.0) ||
                            (snap.Secondary != null && snap.Secondary.LeftPercent <= 10.0))
                        {
                            hasDanger = true;
                            break;
                        }
                    }
                }

                bool anyRunning = (_proxy != null && _proxy.Running)
                    || _accounts.Any(a => a.Running)
                    || _installedApps.Any(t => t != null && t.kind != "url" && CoreService.IsToolRunning(t));

                string state = hasDanger ? "danger" : (!anyRunning ? "idle" : "normal");
                string tip = hasDanger ? "AI BASE · 额度告急或节点超时"
                    : (!anyRunning ? "AI BASE · 全部待机" : "AI BASE · 运行正常");
                UpdateTrayStatusIcon(state, tip);
            }
            catch { }
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
                        ShowDesktopNotification("账号回收提醒", tip, "warn", 5500);
                    }
                    catch { }
                    break;
                }
            }
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
                { "just_updated", _justUpdated },
                { "brand_icon", CoreService.BrandIconUrl },
                { "voice_recorder_hotkey", VoiceRecorderShortcut.Display },
                { "quick_ledger_forms", CoreService.ReadQuickLedgerForms().Keys.ToArray() },
                { "settings", new Dictionary<string, object>
                    {
                        { "minimize_to_tray", _store.settings.minimize_to_tray },
                        { "expiry_balloon_alert", _store.settings.expiry_balloon_alert },
                        { "quota_auto_refresh", _store.settings.quota_auto_refresh },
                        { "screenshot_enabled", _store.settings.screenshot_enabled },
                        { "screenshot_hotkey", _store.settings.screenshot_hotkey ?? "Ctrl+Shift+A" },
                        { "auto_start_silent", CoreService.IsAutoStartSilentEnabled() }
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
                { "backlog_items", _backlogItems },
                { "projects", _projects },
                { "project_previews", _projects.Where(item => item.preview_port > 0)
                    .ToDictionary(item => item.id, item => _projectPreviews.State(item.preview_port)) },
                { "project_scan_roots", _projectRoots },
                { "projects_refreshing", _projectsRefreshing },
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
    }
}
