using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexLauncher
{
    internal sealed class NetworkStatus
    {
        public string state { get; set; }
        public string mode { get; set; }
        public string group { get; set; }
        public string node { get; set; }
        public int? delay { get; set; }
        public string checked_at { get; set; }
        public bool can_select { get; set; }
    }

    internal sealed class ClashNetwork
    {
        private const string TestUrl = "https://www.gstatic.com/generate_204";
        private static readonly string[] PolicyNames = { "NIVA Network", "AI 服务", "谷歌" };
        private static readonly Regex JapanName = new Regex(
            @"日本|🇯🇵|(?<![A-Za-z])(?:Japan|JP(?![A-Za-z])|JP(?=Tokyo|Osaka))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex InformationalName = new Regex(
            @"流量|到期|过期|套餐|官网|客服|重置|订阅|公告|通知|防失联|traffic|expires?|reset|website|support",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NonProxyType = new Regex(
            @"^(?:Direct|Reject.*|Pass|Compatible|Selector|URLTest|Fallback|LoadBalance|Relay)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly Func<string, string, string, string> _request;
        private readonly Dictionary<string, NetworkStatus> _measurements = new Dictionary<string, NetworkStatus>(StringComparer.Ordinal);
        private string _selectedGroup;

        public ClashNetwork(Func<string, string, string, string> request = null)
        {
            _request = request;
        }

        public NetworkStatus Read(bool running, bool probe)
        {
            if (!running)
            {
                _measurements.Clear();
                return new NetworkStatus { state = "stopped" };
            }
            try
            {
                return ReadStatus(Connect(), false, probe);
            }
            catch (Exception)
            {
                _measurements.Clear();
                return new NetworkStatus { state = "controller" };
            }
        }

        public NetworkStatus Measure()
        {
            return ReadStatus(Connect(), true, true);
        }

        public object ListNodes(string group)
        {
            var routes = Connect();
            var groups = routes.Selectors();
            if (groups.Length == 0) throw new InvalidOperationException("当前模式没有可手动切换的策略组。");
            if (string.IsNullOrEmpty(group))
            {
                group = routes.Target(_selectedGroup);
                if (!groups.Contains(group)) group = groups[0];
            }
            return new
            {
                group,
                groups = groups.Select(name => new { name, label = PolicyLabel(name) }).ToArray(),
                nodes = BuildNodeList(routes, group, routes.Choices(group))
            };
        }

        public NetworkStatus MeasureGroup(string group, IProgress<object> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var routes = Connect();
            var choices = routes.Choices(group);
            progress.Report(new { nodes = BuildNodeList(routes, group, choices) });
            var results = new ConcurrentBag<NetworkStatus>();
            Parallel.ForEach(choices, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (name, loop) =>
            {
                if (cancellation.IsCancellationRequested)
                {
                    loop.Stop();
                    return;
                }
                progress.Report(new { name, state = "testing" });
                var outcome = MeasureNode(routes, name);
                results.Add(outcome);
                progress.Report(new { name, outcome.delay, outcome.state });
            });
            foreach (var outcome in results) _measurements[outcome.node] = outcome;
            cancellation.ThrowIfCancellationRequested();
            return Read(true, false);
        }

        private object[] BuildNodeList(Routes routes, string group, string[] choices)
        {
            var selected = AuthParser.GetStr(routes.Find(group), "now");
            var active = AuthParser.GetStr(routes.Leaf(group), "name");
            return choices.Select(name =>
            {
                var status = new NetworkStatus { node = name };
                ReadDelay(status, routes.Find(name));
                return (object)new { name, selected = name == selected, active = name == active, status.delay, status.state };
            }).ToArray();
        }

        public NetworkStatus SwitchNode(string group, string node)
        {
            var routes = Connect();
            if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(node) || !routes.Choices(group).Contains(node))
                throw new InvalidOperationException("该节点已不在策略组中，请重新打开列表。");

            routes.Send("PUT", ProxyPath(group), routes.Json.Serialize(new { name = node }));
            routes.ReloadProxies();
            if (AuthParser.GetStr(routes.Find(group), "now") != node)
                throw new InvalidOperationException("Clash 未确认节点切换，请重新查看当前节点。");
            _selectedGroup = group;
            return ReadStatus(routes, true, true);
        }

        private Routes Connect()
        {
            return new Routes(_request ?? MihomoController.Open());
        }

        private NetworkStatus ReadStatus(Routes routes, bool force, bool probe)
        {
            var target = routes.Target(_selectedGroup);
            var leaf = routes.Leaf(target);
            var status = new NetworkStatus
            {
                state = "unknown",
                mode = routes.Mode,
                group = PolicyLabel(target),
                node = AuthParser.GetStr(leaf, "name"),
                can_select = routes.Selectors().Length > 0
            };
            if (string.IsNullOrEmpty(status.node)) return status;
            if ((AuthParser.GetStr(leaf, "type") ?? "").StartsWith("Reject", StringComparison.OrdinalIgnoreCase))
            {
                status.delay = null;
                status.state = "unavailable";
                return status;
            }

            NetworkStatus cached;
            DateTimeOffset measuredAt;
            if (force || (probe && (!_measurements.TryGetValue(status.node, out cached) ||
                !DateTimeOffset.TryParse(cached.checked_at, out measuredAt) ||
                DateTimeOffset.UtcNow - measuredAt >= TimeSpan.FromMinutes(2))))
            {
                cached = MeasureNode(routes, status.node);
                _measurements[status.node] = cached;
                CopyDelay(status, cached);
            }
            else ReadDelay(status, leaf);
            return status;
        }

        private static NetworkStatus MeasureNode(Routes routes, string name)
        {
            var status = new NetworkStatus { node = name };
            try
            {
                var body = routes.Send("GET", ProxyPath(name) + "/delay?timeout=3500&expected=204&url=" + Uri.EscapeDataString(TestUrl), null);
                var reply = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                int value;
                if (int.TryParse(AuthParser.GetStr(reply, "delay"), out value) && value > 0)
                    status.delay = value;
                status.state = status.delay.HasValue ? "reachable" : "unavailable";
            }
            catch (MihomoController.ResponseException ex)
            {
                status.state = ex.Status == 504 ? "timeout" : (ex.Status == 503 ? "unavailable" : "controller");
            }
            catch (Exception)
            {
                status.state = "controller";
            }
            status.checked_at = DateTimeOffset.UtcNow.ToString("o");
            return status;
        }

        private void ReadDelay(NetworkStatus status, Dictionary<string, object> proxy)
        {
            ReadHistory(status, proxy);
            NetworkStatus cached;
            DateTimeOffset historyAt;
            DateTimeOffset measuredAt;
            if (_measurements.TryGetValue(status.node, out cached) &&
                DateTimeOffset.TryParse(cached.checked_at, out measuredAt) &&
                (!DateTimeOffset.TryParse(status.checked_at, out historyAt) || historyAt <= measuredAt))
                CopyDelay(status, cached);
        }

        private static void CopyDelay(NetworkStatus status, NetworkStatus measured)
        {
            status.state = measured.state;
            status.delay = measured.delay;
            status.checked_at = measured.checked_at;
        }

        private static string PolicyLabel(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var plain = name.Trim().TrimStart('◈', '◇', '◆').TrimStart();
            return PolicyNames.FirstOrDefault(label => string.Equals(label, plain, StringComparison.OrdinalIgnoreCase));
        }

        private static void ReadHistory(NetworkStatus status, Dictionary<string, object> proxy)
        {
            var test = AuthParser.GetDict(AuthParser.GetDict(proxy, "extra"), TestUrl);
            var source = test ?? proxy;
            var latest = Items(source, "history").OfType<Dictionary<string, object>>().LastOrDefault();
            status.checked_at = AuthParser.GetStr(latest, "time");
            status.state = "unknown";
            int delay;
            if (latest == null || !int.TryParse(AuthParser.GetStr(latest, "delay"), out delay)) return;
            if (delay > 0 && !IsFalse(source, "alive"))
            {
                status.delay = delay;
                status.state = "reachable";
            }
            else status.state = "unavailable";
        }

        private static bool IsFalse(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) && Equals(value, false);
        }

        private static IEnumerable Items(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) && !(value is string)
                ? value as IEnumerable ?? Array.Empty<object>() : Array.Empty<object>();
        }

        private static string[] Members(Dictionary<string, object> proxy)
        {
            return Items(proxy, "all").OfType<string>().Distinct().ToArray();
        }

        private static string ProxyPath(string name)
        {
            return "/proxies/" + Uri.EscapeDataString(name);
        }

        private sealed class Routes
        {
            public readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            public readonly Func<string, string, string, string> Send;
            public readonly string Mode;
            private Dictionary<string, object> _proxies;

            public Routes(Func<string, string, string, string> send)
            {
                Send = send;
                Mode = (AuthParser.GetStr(Get("/configs"), "mode") ?? "").ToLowerInvariant();
                ReloadProxies();
            }

            public Dictionary<string, object> Get(string path)
            {
                return Json.Deserialize<Dictionary<string, object>>(Send("GET", path, null));
            }

            public void ReloadProxies()
            {
                _proxies = AuthParser.GetDict(Get("/proxies"), "proxies");
                if (_proxies == null) throw new InvalidOperationException("Clash 未返回代理节点。");
            }

            public Dictionary<string, object> Find(string name)
            {
                return string.IsNullOrEmpty(name) ? null : AuthParser.GetDict(_proxies, name);
            }

            public string[] Selectors()
            {
                if (Mode != "rule") return Array.Empty<string>();
                var selectors = _proxies.Keys.Where(name => AuthParser.GetStr(Find(name), "type") == "Selector").ToArray();
                return PolicyNames.Select(label => selectors.FirstOrDefault(name => name == label) ??
                    selectors.FirstOrDefault(name => PolicyLabel(name) == label)).Where(name => name != null).ToArray();
            }

            public string[] Choices(string group)
            {
                if (!Selectors().Contains(group)) throw new InvalidOperationException("当前模式下无法切换该策略组。");
                return Members(Find(group)).Where(name =>
                {
                    if (!JapanName.IsMatch(name) || InformationalName.IsMatch(name)) return false;
                    var proxy = Find(name);
                    var type = AuthParser.GetStr(proxy, "type");
                    return !string.IsNullOrEmpty(type) && !NonProxyType.IsMatch(type) && Members(proxy).Length == 0;
                }).ToArray();
            }

            public string Target(string preferred)
            {
                if (Mode == "direct") return "DIRECT";
                if (Mode == "global") return "GLOBAL";
                if (Mode != "rule") return null;
                if (Selectors().Contains(preferred)) return preferred;

                const string host = "chatgpt.com";
                foreach (var rule in Items(Get("/rules"), "rules").OfType<Dictionary<string, object>>())
                {
                    var target = AuthParser.GetStr(rule, "proxy");
                    if (Find(target) == null || AuthParser.GetStr(AuthParser.GetDict(rule, "extra"), "disabled") == "True") continue;
                    var type = (AuthParser.GetStr(rule, "type") ?? "").Replace("-", "").ToUpperInvariant();
                    var payload = (AuthParser.GetStr(rule, "payload") ?? "").ToLowerInvariant();
                    if (type == "MATCH" ||
                        (type == "DOMAIN" && host == payload) ||
                        (type == "DOMAINSUFFIX" && (host == payload || host.EndsWith("." + payload, StringComparison.Ordinal))) ||
                        (type == "DOMAINKEYWORD" && payload.Length > 0 && host.Contains(payload)) ||
                        ((type == "GEOSITE" || type == "RULESET") &&
                            (payload == "openai" || payload == "chatgpt" || payload == "ai")))
                        return target;
                }
                return null;
            }

            public Dictionary<string, object> Leaf(string name)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (!string.IsNullOrEmpty(name) && seen.Add(name))
                {
                    var node = Find(name);
                    var next = AuthParser.GetStr(node, "now");
                    if (string.IsNullOrEmpty(next)) return node;
                    name = next;
                }
                return null;
            }
        }
    }

    internal static class MihomoController
    {
        private const string PipePrefix = @"\\.\pipe\";
        private const int TimeoutMs = 5500;

        public static Func<string, string, string, string> Open()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var directory = Path.Combine(roaming, "io.github.clash-verge-rev.clash-verge-rev");
            var runtime = Path.Combine(directory, "clash-verge.yaml");
            var yaml = File.Exists(runtime) ? File.ReadAllText(runtime) : "";
            var configured = ReadValue(yaml, "external-controller-pipe");
            var pipe = configured.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)
                ? configured.Substring(PipePrefix.Length) : "";
            var pipes = Directory.GetFiles(PipePrefix);
            var sidecar = Regex.Match(pipe, @"^verge-mihomo-sidecar-(?:release|debug)-([a-f0-9]+)$", RegexOptions.IgnoreCase);
            var production = sidecar.Success ? "verge-mihomo-production-" + sidecar.Groups[1].Value : "";
            foreach (var candidate in new[] { production, pipe, "verge-mihomo" })
            {
                if (candidate.Length != 0 && Array.Exists(pipes, path => string.Equals(path, PipePrefix + candidate, StringComparison.OrdinalIgnoreCase)))
                    return (method, path, body) => SendPipe(candidate, method, path, body);
            }

            Uri endpoint;
            if (Uri.TryCreate("http://" + ReadValue(yaml, "external-controller"), UriKind.Absolute, out endpoint) &&
                endpoint.IsLoopback && endpoint.UserInfo.Length == 0 && endpoint.PathAndQuery == "/")
            {
                var secret = ReadValue(yaml, "secret");
                return (method, path, body) => SendHttp(endpoint, secret, method, path, body);
            }
            throw new IOException("未找到本机 Clash Verge 控制接口。");
        }

        private static string ReadValue(string yaml, string key)
        {
            var value = Regex.Match(yaml, @"(?m)^" + Regex.Escape(key) + @":[ \t]*([^\r\n]*)").Groups[1].Value.Trim();
            if (value.StartsWith("\"") && value.EndsWith("\""))
                return new JavaScriptSerializer().Deserialize<string>(value);
            if (value.Length > 1 && value.StartsWith("'") && value.EndsWith("'"))
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            return value;
        }

        private static string SendPipe(string pipe, string method, string path, string body)
        {
            using (var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                stream.Connect(1000);
                byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                string headers = method + " " + path + " HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n";
                if (body != null)
                    headers += "Content-Type: application/json\r\nContent-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n";
                byte[] message = Encoding.ASCII.GetBytes(headers + "\r\n").Concat(payload).ToArray();
                if (!stream.WriteAsync(message, 0, message.Length).Wait(TimeoutMs)) throw new TimeoutException();
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var read = reader.ReadToEndAsync();
                    if (!read.Wait(TimeoutMs)) throw new TimeoutException();
                    var response = read.Result;
                    int split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    var status = Regex.Match(response, @"\AHTTP/1\.[01] (\d{3})\b");
                    int code;
                    if (split < 0 || !int.TryParse(status.Groups[1].Value, out code)) throw new IOException("Clash 返回了无效的响应。");
                    if (code < 200 || code >= 300) throw new ResponseException(code);
                    return response.Substring(split + 4);
                }
            }
        }

        private static string SendHttp(Uri endpoint, string secret, string method, string path, string body)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(endpoint, path));
            request.Method = method;
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = TimeoutMs;
            if (secret.Length > 0) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + secret;
            try
            {
                if (body != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentType = "application/json";
                    request.ContentLength = bytes.Length;
                    using (var output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var code = (int)response.StatusCode;
                    if (code < 200 || code >= 300) throw new ResponseException(code);
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                using (var response = ex.Response as HttpWebResponse)
                {
                    if (response != null) throw new ResponseException((int)response.StatusCode);
                }
                throw;
            }
        }

        internal sealed class ResponseException : IOException
        {
            public readonly int Status;

            public ResponseException(int status) : base("Clash 控制接口返回 HTTP " + status + "。")
            {
                Status = status;
            }
        }
    }
}
