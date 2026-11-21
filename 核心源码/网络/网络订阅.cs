using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Text.RegularExpressions;

namespace CodexLauncher
{
    internal sealed class SubscriptionUsage
    {
        public long used_bytes { get; set; }
        public long total_bytes { get; set; }
        public string expires_at { get; set; }
    }

    internal sealed class NivaSubscription
    {
        private SubscriptionUsage _usage;
        private DateTimeOffset _updatedAt;
        private DateTimeOffset _nextRead;

        public SubscriptionUsage ReadCached(Func<string, Dictionary<string, object>> get = null)
        {
            if (DateTimeOffset.UtcNow < _nextRead) return _usage;
            _nextRead = DateTimeOffset.UtcNow.AddSeconds(30);
            try
            {
                var profile = FindProfile();
                if (profile != null) Store(profile.Usage, profile.UpdatedAt);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (get != null)
            {
                try
                {
                    var provider = FindProvider(get);
                    if (provider != null)
                        Store(ParseProviderUsage(provider.Value.Value), ReadTimestamp(AuthParser.GetStr(provider.Value.Value, "updatedAt")));
                }
                catch (Exception) { }
            }
            return _usage;
        }

        public void Refresh(Func<string, Dictionary<string, object>> get, Action<string> update, int mixedPort, string clashExe)
        {
            KeyValuePair<string, Dictionary<string, object>>? provider = null;
            if (get != null)
            {
                try { provider = FindProvider(get); }
                catch (IOException) { }
                catch (TimeoutException) { }
                catch (WebException) { }
            }
            if (provider != null)
            {
                var path = "/providers/proxies/" + Uri.EscapeDataString(provider.Value.Key);
                update(path);
                var usage = ParseProviderUsage(get(path));
                if (usage != null)
                {
                    Store(usage, DateTimeOffset.UtcNow);
                    return;
                }
            }

            var profile = FindProfile();
            if (profile == null || profile.Url == null)
                throw new InvalidOperationException("未找到 NIVA Network 订阅");
            Store(DownloadUsage(profile, mixedPort, clashExe), DateTimeOffset.UtcNow);
        }

        private void Store(SubscriptionUsage usage, DateTimeOffset updatedAt)
        {
            if (usage == null || (_usage != null && updatedAt < _updatedAt)) return;
            _usage = usage;
            _updatedAt = updatedAt;
        }

        private static KeyValuePair<string, Dictionary<string, object>>? FindProvider(Func<string, Dictionary<string, object>> get)
        {
            var providers = AuthParser.GetDict(get("/providers/proxies"), "providers");
            if (providers == null) return null;
            foreach (var entry in providers)
            {
                var value = entry.Value as Dictionary<string, object>;
                if (IsNiva(entry.Key) && string.Equals(AuthParser.GetStr(value, "vehicleType"), "HTTP", StringComparison.OrdinalIgnoreCase))
                    return new KeyValuePair<string, Dictionary<string, object>>(entry.Key, value);
            }
            return null;
        }

        private static SubscriptionUsage ParseProviderUsage(Dictionary<string, object> provider)
        {
            var values = AuthParser.GetDict(provider, "subscriptionInfo");
            if (values == null) return null;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in values) fields[field.Key] = Convert.ToString(field.Value, CultureInfo.InvariantCulture);
            return ParseUsage(fields);
        }

        private static bool IsNiva(string name)
        {
            return Regex.IsMatch(name ?? "", @"^\s*[◈◇◆]?\s*NIVA\s+Network(?:\.ya?ml)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private sealed class Profile
        {
            public Uri Url;
            public bool SystemProxy;
            public bool ClashProxy;
            public string UserAgent;
            public SubscriptionUsage Usage;
            public DateTimeOffset UpdatedAt;
        }

        private static Profile FindProfile()
        {
            var path = Path.Combine(MihomoController.ConfigurationDirectory, "profiles.yaml");
            if (!File.Exists(path)) return null;
            var contents = File.ReadAllText(path);
            string current = Scalar(contents, @"^current:");
            var profiles = Regex.Split(contents, @"(?m)^[ \t]*-[ \t]+uid:[ \t]*").Skip(1);
            string first = null;
            foreach (var block in profiles)
            {
                if (Scalar(block, @"^[ ]{2}type:") != "remote" || !IsNiva(Scalar(block, @"^[ ]{2}name:"))) continue;
                if (first == null) first = block;
                if (Unquote(block.Split('\n')[0].Trim()) == current)
                {
                    first = block;
                    break;
                }
            }
            if (first == null) return null;
            Uri url;
            if (!Uri.TryCreate(Scalar(first, @"^[ ]{2}url:"), UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps)
                url = null;
            var extra = Regex.Match(first, @"(?m)^[ ]{2}extra:[ \t]*\r?\n((?:[ ]{4}[^\r\n]*\r?\n?)*)").Groups[1].Value;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in new[] { "upload", "download", "total", "expire" })
                values[field] = Scalar(extra, @"^[ ]{4}" + field + ":");
            return new Profile
            {
                Url = url,
                SystemProxy = Scalar(first, @"^[ ]{4}with_proxy:") == "true",
                ClashProxy = Scalar(first, @"^[ ]{4}self_proxy:") == "true",
                UserAgent = Scalar(first, @"^[ ]{4}user_agent:"),
                Usage = ParseUsage(values),
                UpdatedAt = ReadTimestamp(Scalar(first, @"^[ ]{2}updated:"))
            };
        }

        private static string Scalar(string contents, string prefix)
        {
            return Unquote(Regex.Match(contents, "(?m)" + prefix + @"[ \t]*([^\r\n]*)").Groups[1].Value.Trim());
        }

        private static string Unquote(string value)
        {
            if (value.Length > 1 && value[0] == '\'' && value[value.Length - 1] == '\'')
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            if (value.Length > 1 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2);
            return value;
        }

        private static DateTimeOffset ReadTimestamp(string value)
        {
            long seconds;
            DateTimeOffset timestamp;
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) && seconds > 0 && seconds <= 253402300799L)
                return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp)
                ? timestamp : DateTimeOffset.MinValue;
        }

        private static SubscriptionUsage ParseUsage(Dictionary<string, string> values)
        {
            long upload, download, total;
            if (!ReadBytes(values, "upload", out upload) || !ReadBytes(values, "download", out download) ||
                !ReadBytes(values, "total", out total) || total <= 0 || upload > long.MaxValue - download) return null;
            string expires;
            values.TryGetValue("expire", out expires);
            var expiry = ReadTimestamp(expires);
            return new SubscriptionUsage
            {
                used_bytes = upload + download,
                total_bytes = total,
                expires_at = expiry == DateTimeOffset.MinValue ? null : expiry.ToString("o")
            };
        }

        private static bool ReadBytes(Dictionary<string, string> values, string key, out long bytes)
        {
            string value;
            bytes = 0;
            return values.TryGetValue(key, out value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out bytes);
        }

        private static SubscriptionUsage DownloadUsage(Profile profile, int mixedPort, string clashExe)
        {
            IWebProxy clashProxy = mixedPort > 0 && mixedPort <= 65535 ? new WebProxy("http://127.0.0.1:" + mixedPort) : null;
            string userAgent = profile.UserAgent;
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                string version = !string.IsNullOrEmpty(clashExe) && File.Exists(clashExe) ? FileVersionInfo.GetVersionInfo(clashExe).ProductVersion : null;
                userAgent = "clash-verge" + (string.IsNullOrWhiteSpace(version) ? "" : "/v" + version);
            }
            var configured = profile.ClashProxy ? clashProxy : (profile.SystemProxy ? WebRequest.GetSystemWebProxy() : null);
            var attempts = !profile.SystemProxy && !profile.ClashProxy && clashProxy != null
                ? new[] { configured, clashProxy } : new[] { configured };
            foreach (var proxy in attempts)
            {
                try
                {
                    var usage = FetchUsageHeader(profile.Url, userAgent, proxy);
                    if (usage != null) return usage;
                }
                catch (WebException ex)
                {
                    if (ex.Response != null) ex.Response.Dispose();
                }
            }
            throw new IOException("订阅源未返回可用的流量数据。");
        }

        private static SubscriptionUsage FetchUsageHeader(Uri url, string userAgent, IWebProxy proxy)
        {
            for (int redirects = 0; redirects <= 3; redirects++)
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = userAgent;
                request.Proxy = proxy;
                request.Timeout = request.ReadWriteTimeout = 6500;
                request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
                request.AllowAutoRedirect = false;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (code >= 300 && code < 400)
                    {
                        Uri next;
                        if (!Uri.TryCreate(url, response.Headers["Location"], out next) || next.Scheme != Uri.UriSchemeHttps)
                            return null;
                        url = next;
                        continue;
                    }
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in (response.Headers["subscription-userinfo"] ?? "").Split(';'))
                    {
                        var part = item.Split(new[] { '=' }, 2);
                        if (part.Length == 2) fields[part[0].Trim()] = part[1].Trim();
                    }
                    return ParseUsage(fields);
                }
            }
            return null;
        }
    }
}
