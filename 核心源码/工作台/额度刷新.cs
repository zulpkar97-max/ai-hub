using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace CodexLauncher
{
    public partial class MainWindow
    {
        private AntigravityUsage _antigravityUsage = AntigravityQuota.ReadCache();
        private CancellationTokenSource _antigravityRefresh;
        private string _antigravityError;
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
        private volatile bool _quotaAutoRefreshEnabled;

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
                    Func<ProxyStatusInfo> readProxy = automatic ? (Func<ProxyStatusInfo>)ReadProxyBeforeAutomaticRequest : null;
                    var usage = await Task.Run(() => AntigravityQuota.Refresh(cancellation.Token, readProxy));
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
            ProxyStatusInfo proxy;
            bool canRefresh = QuotaRefreshState.TryGetAutomaticProxy(!_wakeStop, _quotaAutoRefreshEnabled,
                _networkStatus.state, CoreService.GetProxyStatus, out proxy);
            if (proxy != null) _proxy = proxy;
            if (!canRefresh) return;
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

        private ProxyStatusInfo ReadProxyBeforeAutomaticRequest()
        {
            ProxyStatusInfo proxy;
            return QuotaRefreshState.TryGetAutomaticProxy(!_wakeStop, _quotaAutoRefreshEnabled,
                _networkStatus.state, CoreService.GetProxyStatus, out proxy)
                && !_wakeStop && _quotaAutoRefreshEnabled && _networkStatus.state != "stopped" ? proxy : null;
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
                var proxy = automatic ? ReadProxyBeforeAutomaticRequest() : null;
                var snap = automatic && proxy == null ? null : CoreService.FetchCodexUsage(authCopy, proxy);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_wakeStop) return;
                    AuthInfo pending;
                    if (!_usageLoading.TryGetValue(id, out pending) || !object.ReferenceEquals(pending, authCopy)) return;
                    _usageLoading.Remove(id);
                    var current = _accounts.FirstOrDefault(a => a.Record.id == id);
                    if (current == null || !AuthInfo.SameUsageIdentity(current.Auth, authCopy)) return;
                    if (snap == null)
                    {
                        PushFullState();
                        return;
                    }
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
    }
}
