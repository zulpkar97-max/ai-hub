using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexLauncher
{
    public class AccountRecord
    {
        public string id { get; set; }
        public string name { get; set; }
        public string account_type { get; set; } // "recycled" | "permanent"
        public string notes { get; set; }
        public string created_at { get; set; }
        public string started_at { get; set; }
        public string recycle_at { get; set; }
        public double? recycle_days { get; set; }
        public string config_mode { get; set; } // "clone" | "junction_skills" | "fresh" | "native"
        public bool disable_memories { get; set; }
    }

    public class ToolItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string kind { get; set; } // "exe" | "msix" | "url"
        public string target { get; set; }
        public string arguments { get; set; }
        public string process_name { get; set; }
        public string uninstall_hint { get; set; }
        public string subtitle { get; set; }
    }

    public class ToolSection
    {
        public string id { get; set; }
        public string title { get; set; }
        public List<ToolItem> items { get; set; }

        public ToolSection()
        {
            items = new List<ToolItem>();
        }
    }

    public class LauncherSettings
    {
        public bool minimize_to_tray { get; set; }
        public bool expiry_balloon_alert { get; set; }
        public bool quota_auto_refresh { get; set; }
        public bool screenshot_enabled { get; set; }
        public string screenshot_hotkey { get; set; }

        public LauncherSettings()
        {
            minimize_to_tray = true;
            expiry_balloon_alert = true;
            quota_auto_refresh = true;
            screenshot_enabled = true;
            screenshot_hotkey = "Ctrl+Shift+A";
        }
    }

    public class AccountsStore
    {
        public int version { get; set; }
        public List<AccountRecord> accounts { get; set; }
        public List<ToolSection> tool_sections { get; set; }
        public LauncherSettings settings { get; set; }

        public AccountsStore()
        {
            version = 5;
            accounts = new List<AccountRecord>();
            tool_sections = null;
            settings = new LauncherSettings();
        }
    }

    public class AuthInfo
    {
        public bool LoggedIn { get; set; }
        public string Email { get; set; }
        public string DisplayName { get; set; }
        public string PlanType { get; set; }
        public string AccountId { get; set; }
        public string AccessToken { get; set; }
        public string UserId { get; set; }
        public bool CanFetchUsage { get { return LoggedIn && !string.IsNullOrEmpty(AccessToken); } }

        internal static bool SameUsageIdentity(AuthInfo previous, AuthInfo current)
        {
            bool hadUsage = previous != null && previous.CanFetchUsage;
            bool hasUsage = current != null && current.CanFetchUsage;
            if (!hadUsage || !hasUsage) return hadUsage == hasUsage;
            if (!string.Equals(previous.AccountId, current.AccountId, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(previous.UserId) && !string.IsNullOrEmpty(current.UserId))
                return string.Equals(previous.UserId, current.UserId, StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(previous.Email) && !string.IsNullOrEmpty(current.Email))
                return string.Equals(previous.Email, current.Email, StringComparison.OrdinalIgnoreCase);
            return string.Equals(previous.AccessToken, current.AccessToken, StringComparison.Ordinal);
        }

        public DateTimeOffset? SubscriptionStart { get; set; }
        public DateTimeOffset? SubscriptionUntil { get; set; }
    }

    public class UsageWindowInfo
    {
        public string Label { get; set; }
        public double UsedPercent { get; set; }
        public double LeftPercent { get; set; }
        public DateTimeOffset? ResetAt { get; set; }
        public int ResetAfterSeconds { get; set; }
        public int LimitWindowSeconds { get; set; }

        public string FormatResetCountdown()
        {
            TimeSpan ts;
            if (ResetAt.HasValue)
            {
                ts = ResetAt.Value - DateTimeOffset.Now;
                if (ts.TotalSeconds < 0) ts = TimeSpan.Zero;
            }
            else if (ResetAfterSeconds > 0)
            {
                ts = TimeSpan.FromSeconds(ResetAfterSeconds);
            }
            else
            {
                return "";
            }

            if (ts.TotalSeconds <= 60) return "即将重置";
            if (ts.TotalDays >= 1.0)
                return string.Format("{0}d {1}h 后重置", (int)Math.Floor(ts.TotalDays), ts.Hours);
            if (ts.TotalHours >= 1.0)
                return string.Format("{0}h {1}m 后重置", (int)Math.Floor(ts.TotalHours), ts.Minutes);
            return string.Format("{0}m 后重置", Math.Max(1, (int)Math.Ceiling(ts.TotalMinutes)));
        }
    }

    public class UsageSnapshot
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string PlanType { get; set; }
        public UsageWindowInfo Primary { get; set; }
        public UsageWindowInfo Secondary { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
    }

    internal sealed class QuotaBucketSnapshot
    {
        internal string Label;
        internal double? Percent;
        internal DateTimeOffset? ResetAt;
    }

    internal sealed class QuotaRefreshState
    {
        private DateTimeOffset _lastAttempt;
        private DateTimeOffset _retryAfter;
        private int _failureCount;
        internal bool LastSuccessWasAutomatic;

        internal static bool TryGetAutomaticProxy(bool appRunning, bool enabled, string networkState,
            Func<ProxyStatusInfo> readProxy, out ProxyStatusInfo proxy)
        {
            proxy = null;
            if (!appRunning || !enabled) return false;
            try { proxy = readProxy(); }
            catch (Exception) { return false; }
            return proxy != null && proxy.Running && proxy.ClashProcessRunning && proxy.PortListening
                && proxy.MixedPort > 0 && networkState != "stopped";
        }

        internal bool IsDue(DateTimeOffset now, DateTimeOffset? fetchedAt, IEnumerable<QuotaBucketSnapshot> buckets,
            TimeSpan maximumAge, bool onlyNonFullResets)
        {
            if (now < _retryAfter || now - _lastAttempt < TimeSpan.FromMinutes(1)) return false;
            if (!fetchedAt.HasValue || now - fetchedAt.Value >= maximumAge) return true;
            foreach (var bucket in buckets)
            {
                if (bucket.ResetAt.HasValue && fetchedAt.Value < bucket.ResetAt.Value
                    && now - bucket.ResetAt.Value >= TimeSpan.FromMinutes(1)
                    && (!onlyNonFullResets || (bucket.Percent.HasValue && bucket.Percent.Value < 99.5))) return true;
            }
            return false;
        }

        internal void Begin(DateTimeOffset now) { _lastAttempt = now; }

        internal void Finish(DateTimeOffset now, bool success, bool automatic)
        {
            if (success)
            {
                _failureCount = 0;
                _retryAfter = DateTimeOffset.MinValue;
                LastSuccessWasAutomatic = automatic;
            }
            else if (automatic)
            {
                _failureCount = Math.Min(_failureCount + 1, 3);
                _retryAfter = now.AddMinutes(_failureCount == 1 ? 10 : _failureCount == 2 ? 30 : 120);
            }
        }

        internal static List<string> CollectRefilled(string owner, Dictionary<string, long> notified,
            Dictionary<string, QuotaBucketSnapshot> before, Dictionary<string, QuotaBucketSnapshot> after)
        {
            var labels = new List<string>();
            foreach (var entry in after)
            {
                var current = entry.Value;
                QuotaBucketSnapshot previous;
                if (!current.ResetAt.HasValue || !current.Percent.HasValue || current.Percent.Value < 99.5
                    || !before.TryGetValue(entry.Key, out previous) || !previous.Percent.HasValue || previous.Percent.Value >= 80) continue;
                string key = owner + "/" + entry.Key;
                long resetTicks = current.ResetAt.Value.UtcDateTime.Ticks;
                long last;
                if (notified.TryGetValue(key, out last) && last >= resetTicks) continue;
                notified[key] = resetTicks;
                labels.Add(current.Label);
            }
            return labels;
        }
    }

    public class ProxyStatusInfo
    {
        public bool Installed { get; set; }
        public string ExePath { get; set; }
        public bool Running { get; set; }
        internal bool ClashProcessRunning { get; set; }
        public bool SystemProxyOn { get; set; }
        public bool TunOn { get; set; }
        public int MixedPort { get; set; }
        public bool PortListening { get; set; }
        public string ActiveProfileName { get; set; }
    }

    public class ChromeProfileInfo
    {
        public string DirectoryName { get; set; }
        public string DisplayName { get; set; }
        public string Email { get; set; }
    }

    public class ProcInfo
    {
        public int ProcessId { get; set; }
        public int ParentProcessId { get; set; }
        public string Name { get; set; }
        public string CommandLine { get; set; }
    }

    public class AccountViewModel
    {
        public AccountRecord Record { get; set; }
        public bool IsDefault { get; set; }
        public AuthInfo Auth { get; set; }
        public bool Running { get; set; }
        public int PidCount { get; set; }
        public bool WindowVisible { get; set; }
        public string PermissionMode { get; set; }
        public long? DiskSizeBytes { get; set; }
        public string CodexHome { get; set; }
        public string UserDataDir { get; set; }
    }

    public class CountdownSnapshot
    {
        public bool Expired { get; set; }
        public TimeSpan Remaining { get; set; }
        public int Days { get; set; }
        public int Hours { get; set; }
        public int Minutes { get; set; }
        public int Seconds { get; set; }
        public double RemainingPercent { get; set; }
        public string Level { get; set; }
        public DateTimeOffset TargetLocal { get; set; }
        public string CompactText { get; set; }
    }

    public static class AuthParser
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };

        public static AuthInfo ReadAuthInfo(string codexHome)
        {
            var info = new AuthInfo();
            try
            {
                string authPath = Path.Combine(codexHome, "auth.json");
                if (!File.Exists(authPath)) return info;

                string raw = File.ReadAllText(authPath, Encoding.UTF8);
                var root = Json.DeserializeObject(raw) as Dictionary<string, object>;
                if (root == null) return info;

                var tokens = GetDict(root, "tokens");
                string idToken = GetStr(tokens, "id_token");
                string accessToken = GetStr(tokens, "access_token");
                string apiKey = GetStr(root, "OPENAI_API_KEY");
                string accountId = GetStr(tokens, "account_id");

                info.LoggedIn = !string.IsNullOrEmpty(accessToken) || !string.IsNullOrEmpty(idToken) || !string.IsNullOrEmpty(apiKey);
                info.AccessToken = accessToken;
                info.AccountId = accountId;

                var idClaims = DecodeJwt(idToken);
                var accClaims = DecodeJwt(accessToken);

                var idAuth = GetDict(idClaims, "https://api.openai.com/auth");
                var accAuth = GetDict(accClaims, "https://api.openai.com/auth");
                var profileBlock = GetDict(accClaims, "https://api.openai.com/profile");

                info.UserId = GetStr(accAuth, "chatgpt_user_id") ?? GetStr(idAuth, "chatgpt_user_id")
                    ?? GetStr(idClaims, "sub");
                info.Email = GetStr(idClaims, "email") ?? GetStr(profileBlock, "email") ?? GetStr(accClaims, "email");
                info.DisplayName = GetStr(idClaims, "name") ?? GetStr(profileBlock, "name") ?? GetStr( info.Email != null ? idClaims : null, "email");
                info.PlanType = GetStr(idAuth, "chatgpt_plan_type") ?? GetStr(accAuth, "chatgpt_plan_type");
                if (string.IsNullOrEmpty(info.AccountId))
                    info.AccountId = GetStr(idAuth, "chatgpt_account_id") ?? GetStr(accAuth, "chatgpt_account_id");

                DateTimeOffset dto;
                string startStr = GetStr(idAuth, "chatgpt_subscription_active_start") ?? GetStr(accAuth, "chatgpt_subscription_active_start");
                if (!string.IsNullOrEmpty(startStr) && DateTimeOffset.TryParse(startStr, out dto))
                    info.SubscriptionStart = dto.ToLocalTime();

                string untilStr = GetStr(idAuth, "chatgpt_subscription_active_until") ?? GetStr(accAuth, "chatgpt_subscription_active_until");
                if (!string.IsNullOrEmpty(untilStr) && DateTimeOffset.TryParse(untilStr, out dto))
                    info.SubscriptionUntil = dto.ToLocalTime();
            }
            catch { }
            return info;
        }

        private static Dictionary<string, object> DecodeJwt(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            string[] parts = token.Split('.');
            if (parts.Length != 3) return null;
            try
            {
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                int mod = payload.Length % 4;
                if (mod == 2) payload += "==";
                else if (mod == 3) payload += "=";
                byte[] bytes = Convert.FromBase64String(payload);
                return Json.DeserializeObject(Encoding.UTF8.GetString(bytes)) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object val;
            if (d.TryGetValue(key, out val)) return val as Dictionary<string, object>;
            return null;
        }

        public static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object val;
            if (d.TryGetValue(key, out val) && val != null) return val.ToString();
            return null;
        }

        public static double? GetDouble(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                double res;
                if (double.TryParse(val.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out res))
                    return res;
            }
            return null;
        }
    }

    public static class CountdownHelper
    {
        public static CountdownSnapshot Compute(AccountRecord rec)
        {
            if (rec == null || rec.account_type != "recycled" || string.IsNullOrEmpty(rec.recycle_at))
                return null;

            DateTimeOffset endDto;
            if (!DateTimeOffset.TryParse(rec.recycle_at, out endDto))
                return null;

            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset startDto;
            if (!string.IsNullOrEmpty(rec.started_at) && DateTimeOffset.TryParse(rec.started_at, out startDto))
            {
            }
            else
            {
                double days = rec.recycle_days.HasValue && rec.recycle_days.Value > 0 ? rec.recycle_days.Value : 25.0;
                startDto = endDto.AddDays(-days);
            }

            double totalSec = Math.Max(1.0, (endDto - startDto).TotalSeconds);
            TimeSpan remain = endDto - now;
            bool expired = remain.TotalSeconds <= 0;
            TimeSpan abs = expired ? remain.Negate() : remain;

            int d = (int)Math.Floor(abs.TotalDays);
            int h = abs.Hours;
            int m = abs.Minutes;
            int s = abs.Seconds;

            double pct = expired ? 0.0 : Math.Max(0.0, Math.Min(100.0, (remain.TotalSeconds / totalSec) * 100.0));
            string level = "ok";
            if (expired || remain.TotalHours <= 48.0) level = "danger";
            else if (remain.TotalDays <= 5.0) level = "warn";

            string compact = expired
                ? string.Format("已超期 {0}天 {1:D2}:{2:D2}:{3:D2}", d, h, m, s)
                : string.Format("{0}天 {1:D2}时 {2:D2}分 {3:D2}秒", d, h, m, s);

            return new CountdownSnapshot
            {
                Expired = expired,
                Remaining = remain,
                Days = d,
                Hours = h,
                Minutes = m,
                Seconds = s,
                RemainingPercent = pct,
                Level = level,
                TargetLocal = endDto.ToLocalTime(),
                CompactText = compact
            };
        }

        public static string FormatBytes(long? bytes)
        {
            if (!bytes.HasValue) return "";
            double b = bytes.Value;
            if (b < 1024) return string.Format("{0:F0} B", b);
            if (b < 1024 * 1024) return string.Format("{0:F1} KB", b / 1024.0);
            if (b < 1024 * 1024 * 1024) return string.Format("{0:F1} MB", b / (1024.0 * 1024.0));
            return string.Format("{0:F2} GB", b / (1024.0 * 1024.0 * 1024.0));
        }
    }
}
