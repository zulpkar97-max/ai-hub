using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CodexLauncher
{
    public static class CoreService
    {
        public const string AppVersion = "v5.9.1";
        private const string BrandIconFile = "aihub-v5.png";
        public const string BrandIconUrl = "https://icons.aihub.local/" + BrandIconFile;

        private static readonly object StoreLock = new object();
        private static readonly object ProfileLifecycleLock = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };

        public static readonly string BaseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        public static readonly string DefaultCodexHome = Path.Combine(UserHome, ".codex");
        public static readonly string DefaultUserData = Path.Combine(RoamingAppData, "Codex");

        public static readonly string DataDir = Path.Combine(UserHome, ".codex-launcher");
        public static readonly string ProfilesDir = Path.Combine(DataDir, "profiles");
        public static readonly string AppIconsCacheDir = Path.Combine(DataDir, "app-icons");
        public static readonly string WebViewCacheDir = Path.Combine(DataDir, "webview2-cache");
        public static readonly string AccountsFile = Path.Combine(DataDir, "accounts.json");

        public static readonly string PermanentIcon = Path.Combine(DataDir, "aihub-v5.ico");
        public static readonly string PermanentPng256 = Path.Combine(DataDir, BrandIconFile);
        public static readonly string BundledIcon = Path.Combine(BaseDir, "资源", "aihub.ico");
        public static readonly string BundledPng256 = Path.Combine(BaseDir, "资源", "aihub-256.png");
        public static readonly string BundledChatGptPng = Path.Combine(BaseDir, "资源", "chatgpt-256.png");

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hwnd, int command);

        static CoreService()
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
            }
            catch
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            }
        }

        public static void EnsureDirs()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(ProfilesDir);
            Directory.CreateDirectory(AppIconsCacheDir);
            CopyAssetIfNeeded(BundledIcon, PermanentIcon);
            CopyAssetIfNeeded(BundledPng256, PermanentPng256);
            CopyAssetIfNeeded(BundledPng256, Path.Combine(AppIconsCacheDir, BrandIconFile));
            CopyAssetIfNeeded(BundledChatGptPng, Path.Combine(AppIconsCacheDir, "chatgpt.png"));
        }

        public static string GetCachedIconUrl(string exePath, string kind)
        {
            try
            {
                EnsureDirs();
                if (kind == "url" || string.IsNullOrEmpty(exePath))
                    return BrandIconUrl;
                if (exePath.IndexOf("chatgpt", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "https://icons.aihub.local/chatgpt.png";

                string key = "exe_" + Math.Abs(exePath.ToLowerInvariant().GetHashCode()).ToString("x8") + ".png";
                string dst = Path.Combine(AppIconsCacheDir, key);
                if (!File.Exists(dst) && File.Exists(exePath))
                {
                    using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (ico != null)
                        {
                            using (var bmp = ico.ToBitmap())
                                bmp.Save(dst, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
                if (File.Exists(dst)) return "https://icons.aihub.local/" + key;
            }
            catch { }
            return BrandIconUrl;
        }

        private static void CopyAssetIfNeeded(string src, string dst)
        {
            try
            {
                if (!File.Exists(src)) return;
                bool need = !File.Exists(dst) || new FileInfo(src).Length != new FileInfo(dst).Length;
                if (need) File.Copy(src, dst, true);
            }
            catch { }
        }

        public static AccountsStore LoadStore()
        {
            lock (StoreLock)
            {
                EnsureDirs();
                bool migrated = false;
                AccountsStore store = File.Exists(AccountsFile) ? ReadAccountsStore() : new AccountsStore();
                if (store.settings == null) store.settings = new LauncherSettings();

                if (store.version < 5)
                {
                    store.settings.screenshot_enabled = true;
                    if (string.IsNullOrWhiteSpace(store.settings.screenshot_hotkey))
                        store.settings.screenshot_hotkey = "Ctrl+Shift+A";
                    store.version = 5;
                    migrated = true;
                }

                if (store.tool_sections == null)
                {
                    store.tool_sections = SeedDefaultToolSections();
                    migrated = true;
                }

                if (migrated) SaveStore(store);
                return store;
            }
        }

        private static AccountsStore ReadAccountsStore()
        {
            try
            {
                string raw = File.ReadAllText(AccountsFile, Encoding.UTF8);
                var root = Json.DeserializeObject(raw) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("accounts") || root["accounts"] == null)
                    throw new InvalidDataException("缺少账号列表。");
                var store = Json.Deserialize<AccountsStore>(raw);
                if (store == null || store.accounts == null || store.accounts.Any(a => a == null))
                    throw new InvalidDataException("账号列表无效。");
                return store;
            }
            catch (Exception ex)
            {
                string recovery = File.Exists(AccountsFile + ".bak") ? " 可从 accounts.json.bak 恢复。" : "";
                throw new InvalidDataException("无法读取账号配置，原文件已保留。" + recovery, ex);
            }
        }

        public static void SaveStore(AccountsStore store)
        {
            lock (StoreLock)
            {
                EnsureDirs();
                if (File.Exists(AccountsFile)) ReadAccountsStore();
                WriteTextAtomically(AccountsFile, Json.Serialize(store), true);
            }
        }

        internal static LauncherSettings UpdateLauncherSettings(Action<LauncherSettings> update)
        {
            lock (StoreLock)
            {
                var store = LoadStore();
                update(store.settings);
                SaveStore(store);
                return store.settings;
            }
        }

        internal static void WriteTextAtomically(string path, string content, bool keepBackup)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(tmp, path, keepBackup ? path + ".bak" : null);
                else
                    File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        public static List<ToolItem> DiscoverInstalledAiApps()
        {
            var found = new List<ToolItem>();

            string codexExe = FindCodexExecutable();
            if (!string.IsNullOrEmpty(codexExe))
            {
                found.Add(new ToolItem
                {
                    id = "tool-codex-native",
                    name = "ChatGPT / Codex",
                    kind = "msix",
                    target = codexExe,
                    process_name = "ChatGPT",
                    uninstall_hint = "OpenAI.Codex",
                    subtitle = "OpenAI Codex 桌面客户端（MSIX）"
                });
            }

            string geminiExe = Path.Combine(LocalAppData, "google", "Gemini", "Gemini.exe");
            if (File.Exists(geminiExe))
            {
                found.Add(new ToolItem
                {
                    id = "tool-gemini",
                    name = "Gemini",
                    kind = "exe",
                    target = geminiExe,
                    process_name = "Gemini",
                    uninstall_hint = "Gemini",
                    subtitle = "Google Gemini 桌面端"
                });
            }

            string agyExe = Path.Combine(LocalAppData, "Programs", "antigravity", "Antigravity.exe");
            if (File.Exists(agyExe))
            {
                found.Add(new ToolItem
                {
                    id = "tool-antigravity",
                    name = "Antigravity",
                    kind = "exe",
                    target = agyExe,
                    process_name = "Antigravity",
                    uninstall_hint = "Antigravity",
                    subtitle = "Google Antigravity IDE / Agent"
                });
            }

            string openDesignExe = Path.Combine(LocalAppData, "Programs", "Open Design", "Open Design.exe");
            if (File.Exists(openDesignExe))
            {
                found.Add(new ToolItem
                {
                    id = "tool-opendesign",
                    name = "Open Design",
                    kind = "exe",
                    target = openDesignExe,
                    process_name = "Open Design",
                    uninstall_hint = "Open Design",
                    subtitle = "AI 设计桌面端"
                });
            }

            return found;
        }

        private static List<ToolSection> SeedDefaultToolSections()
        {
            var sec = new ToolSection
            {
                id = "sec-desktop-ai",
                title = "桌面 AI 应用",
                items = DiscoverInstalledAiApps()
            };
            return new List<ToolSection> { sec };
        }

        public static string FindCodexExecutable()
        {
            string localAlias = Path.Combine(LocalAppData, "Microsoft", "WindowsApps", "chatgpt-desktop.exe");
            if (File.Exists(localAlias)) return localAlias;

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string cand = Path.Combine(dir.Trim(), "chatgpt-desktop.exe");
                    if (File.Exists(cand)) return cand;
                }
                catch { }
            }

            try
            {
                string winApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                if (Directory.Exists(winApps))
                {
                    var dirs = Directory.GetDirectories(winApps, "OpenAI.Codex_*");
                    Array.Sort(dirs);
                    Array.Reverse(dirs);
                    foreach (string d in dirs)
                    {
                        string exe = Path.Combine(d, "app", "ChatGPT.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            catch { }
            return null;
        }

        // ==================== CODEX USAGE FETCHER ====================

        public static UsageSnapshot FetchCodexUsage(AuthInfo auth, ProxyStatusInfo automaticProxy = null)
        {
            if (auth == null || !auth.LoggedIn || string.IsNullOrEmpty(auth.AccessToken))
                return new UsageSnapshot { Success = false, ErrorMessage = "尚未登录或无访问令牌" };

            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://chatgpt.com/backend-api/wham/usage");
                req.Method = "GET";
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.UserAgent = "codex_cli_rs";
                req.Accept = "application/json";
                req.Headers["Authorization"] = "Bearer " + auth.AccessToken;
                if (!string.IsNullOrEmpty(auth.AccountId))
                    req.Headers["ChatGPT-Account-Id"] = auth.AccountId;

                var proxyInfo = automaticProxy ?? GetProxyStatus();
                if (automaticProxy != null || (!proxyInfo.SystemProxyOn && proxyInfo.PortListening && proxyInfo.MixedPort > 0))
                    req.Proxy = new WebProxy("127.0.0.1", proxyInfo.MixedPort);

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return ParseUsageResponse(reader.ReadToEnd());
                }
            }
            catch (WebException wex)
            {
                string msg = wex.Message;
                try
                {
                    var httpResp = wex.Response as HttpWebResponse;
                    if (httpResp != null)
                    {
                        int code = (int)httpResp.StatusCode;
                        msg = (code == 401 || code == 403)
                            ? "额度访问被拒绝，请在 Codex 中检查登录状态"
                            : string.Format("接口返回 HTTP {0}", code);
                    }
                    else
                    {
                        msg = "网络不可达，请确认代理已开启";
                    }
                }
                catch { }
                return new UsageSnapshot { Success = false, ErrorMessage = msg, FetchedAt = DateTimeOffset.Now };
            }
            catch (Exception ex)
            {
                return new UsageSnapshot { Success = false, ErrorMessage = ex.Message, FetchedAt = DateTimeOffset.Now };
            }
        }

        private static UsageSnapshot ParseUsageResponse(string jsonBody)
        {
            var root = Json.DeserializeObject(jsonBody) as Dictionary<string, object>;
            if (root == null)
                return new UsageSnapshot { Success = false, ErrorMessage = "无法解析用量数据" };

            var snap = new UsageSnapshot
            {
                Success = true,
                PlanType = AuthParser.GetStr(root, "plan_type"),
                FetchedAt = DateTimeOffset.Now
            };

            var rateLimit = AuthParser.GetDict(root, "rate_limit");
            if (rateLimit != null)
            {
                var prim = AuthParser.GetDict(rateLimit, "primary_window");
                var sec = AuthParser.GetDict(rateLimit, "secondary_window");
                if (prim != null) snap.Primary = ParseWindow(prim, "周期限额");
                if (sec != null) snap.Secondary = ParseWindow(sec, "长周期限额");
                if ((prim != null || sec != null) && snap.Primary == null && snap.Secondary == null)
                    return new UsageSnapshot { Success = false, ErrorMessage = "额度返回缺少有效的剩余比例" };
            }
            else if (string.IsNullOrEmpty(snap.PlanType))
                return new UsageSnapshot { Success = false, ErrorMessage = "无法解析用量数据" };

            if (snap.Primary == null && snap.Secondary == null)
                snap.ErrorMessage = "当前账号暂无用量窗口数据";
            return snap;
        }

        private static UsageWindowInfo ParseWindow(Dictionary<string, object> w, string fallbackLabel)
        {
            double? usage = AuthParser.GetDouble(w, "used_percent");
            if (!usage.HasValue || double.IsNaN(usage.Value) || double.IsInfinity(usage.Value)) return null;
            double used = usage.Value;
            double left = Math.Max(0.0, Math.Min(100.0, 100.0 - used));
            int winSec = (int)(AuthParser.GetDouble(w, "limit_window_seconds") ?? 0);
            int resetAfter = (int)(AuthParser.GetDouble(w, "reset_after_seconds") ?? 0);

            DateTimeOffset? resetAt = null;
            double? resetEpoch = AuthParser.GetDouble(w, "reset_at");
            if (resetEpoch.HasValue && resetEpoch.Value > 1000000000)
            {
                resetAt = DateTimeOffset.FromUnixTimeSeconds((long)resetEpoch.Value).ToLocalTime();
                if (resetAfter <= 0)
                    resetAfter = (int)Math.Max(0, (resetAt.Value - DateTimeOffset.Now).TotalSeconds);
            }
            else if (resetAfter > 0)
            {
                resetAt = DateTimeOffset.Now.AddSeconds(resetAfter);
            }

            // Accurate label classification: Monthly (>= 25d or reset > 8d), Weekly (>= 5d), Hourly (< 5d)
            string label = fallbackLabel;
            if (winSec >= 86400 * 25 || (winSec <= 0 && resetAfter >= 86400 * 8))
                label = "每月限额";
            else if (winSec >= 86400 * 5 || (winSec <= 0 && resetAfter >= 86400 * 2))
                label = "每周限额";
            else if (winSec >= 3600)
                label = string.Format("{0}h 限额", Math.Round(winSec / 3600.0));
            else if (winSec > 0)
                label = string.Format("{0}m 限额", Math.Max(1, Math.Round(winSec / 60.0)));

            return new UsageWindowInfo
            {
                Label = label,
                UsedPercent = used,
                LeftPercent = left,
                LimitWindowSeconds = winSec,
                ResetAfterSeconds = resetAfter,
                ResetAt = resetAt
            };
        }

        // ==================== CLASH VERGE PROXY ====================

        public static string FindClashVergeExe()
        {
            string[] candidates = new[]
            {
                @"D:\VPNTools\ClashVergeRev\clash-verge.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Clash Verge", "clash-verge.exe"),
                Path.Combine(LocalAppData, "Programs", "Clash Verge", "clash-verge.exe")
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;

            try
            {
                string pubLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Clash Verge.lnk");
                if (File.Exists(pubLnk))
                {
                    string target = ReadShortcutTarget(pubLnk);
                    if (!string.IsNullOrEmpty(target) && File.Exists(target)) return target;
                }
            }
            catch { }
            return null;
        }

        private static string ReadShortcutTarget(string lnkPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                object tp = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                return tp != null ? tp.ToString() : null;
            }
            catch { return null; }
        }

        public static ProxyStatusInfo GetProxyStatus()
        {
            var info = new ProxyStatusInfo { MixedPort = 7897 };
            info.ExePath = FindClashVergeExe();
            info.Installed = !string.IsNullOrEmpty(info.ExePath);

            try
            {
                info.ClashProcessRunning = Process.GetProcessesByName("clash-verge").Length > 0
                    || Process.GetProcessesByName("verge-mihomo").Length > 0;
                info.Running = info.ClashProcessRunning;
            }
            catch { }

            string cvDir = Path.Combine(RoamingAppData, "io.github.clash-verge-rev.clash-verge-rev");
            try
            {
                string vergeYaml = Path.Combine(cvDir, "verge.yaml");
                if (File.Exists(vergeYaml))
                {
                    string content = File.ReadAllText(vergeYaml, Encoding.UTF8);
                    info.TunOn = Regex.IsMatch(content, @"(?m)^\s*enable_tun_mode\s*:\s*true\b");
                    info.SystemProxyOn = Regex.IsMatch(content, @"(?m)^\s*enable_system_proxy\s*:\s*true\b");
                    var pm = Regex.Match(content, @"(?m)^\s*verge_mixed_port\s*:\s*(\d+)");
                    int port;
                    if (pm.Success && int.TryParse(pm.Groups[1].Value, out port)) info.MixedPort = port;
                }
            }
            catch { }

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key != null)
                    {
                        object pe = key.GetValue("ProxyEnable");
                        if (pe != null && Convert.ToInt32(pe) == 1) info.SystemProxyOn = true;
                        else if (!info.Running) info.SystemProxyOn = false;
                    }
                }
            }
            catch { }

            try
            {
                string profYaml = Path.Combine(cvDir, "profiles.yaml");
                if (File.Exists(profYaml))
                {
                    string content = File.ReadAllText(profYaml, Encoding.UTF8);
                    var curMatch = Regex.Match(content, @"(?m)^\s*current\s*:\s*['""]?([^\r\n'""]+)['""]?");
                    if (curMatch.Success)
                    {
                        string curUid = curMatch.Groups[1].Value.Trim();
                        foreach (string blk in Regex.Split(content, @"(?m)^-\s+uid\s*:"))
                        {
                            if (blk.TrimStart().StartsWith(curUid, StringComparison.OrdinalIgnoreCase))
                            {
                                var nm = Regex.Match(blk, @"(?m)^\s*name\s*:\s*['""]?([^\r\n'""]+)['""]?");
                                if (nm.Success) { info.ActiveProfileName = nm.Groups[1].Value.Trim(); break; }
                            }
                        }
                    }
                }
            }
            catch { }

            if (info.MixedPort > 0)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        var ar = client.BeginConnect("127.0.0.1", info.MixedPort, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(150, false) && client.Connected)
                        {
                            info.PortListening = true;
                            client.EndConnect(ar);
                        }
                    }
                }
                catch { }
            }

            if (info.PortListening) info.Running = true;
            return info;
        }

        public static void OpenOrStartClashVerge()
        {
            string exe = FindClashVergeExe();
            if (string.IsNullOrEmpty(exe))
                throw new InvalidOperationException("未找到 Clash Verge 可执行文件。");
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }

        public static void StopClashVerge()
        {
            foreach (string name in new[] { "clash-verge", "verge-mihomo" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                        try { p.Kill(); } catch { }
                }
                catch { }
            }
        }

        public static void RestartClashVerge()
        {
            StopClashVerge();
            Thread.Sleep(700);
            OpenOrStartClashVerge();
        }

        // ==================== CHROME PROFILES ====================

        public static string FindChromeExe()
        {
            string[] candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(LocalAppData, "Google", "Chrome", "Application", "chrome.exe")
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        public static List<ChromeProfileInfo> DiscoverChromeProfiles()
        {
            var list = new List<ChromeProfileInfo>();
            try
            {
                string localState = Path.Combine(LocalAppData, "Google", "Chrome", "User Data", "Local State");
                if (!File.Exists(localState)) return list;
                var root = Json.DeserializeObject(File.ReadAllText(localState, Encoding.UTF8)) as Dictionary<string, object>;
                var cache = AuthParser.GetDict(AuthParser.GetDict(root, "profile"), "info_cache");
                if (cache == null) return list;

                foreach (var kv in cache)
                {
                    var item = kv.Value as Dictionary<string, object>;
                    if (item == null) continue;
                    list.Add(new ChromeProfileInfo
                    {
                        DirectoryName = kv.Key,
                        DisplayName = AuthParser.GetStr(item, "name") ?? kv.Key,
                        Email = AuthParser.GetStr(item, "user_name") ?? ""
                    });
                }
            }
            catch { }
            return list;
        }

        public static void LaunchChromeProfileUrl(string profileDir, string url)
        {
            string chrome = FindChromeExe();
            if (string.IsNullOrEmpty(chrome))
                throw new InvalidOperationException("未找到 Google Chrome。");
            Process.Start(new ProcessStartInfo
            {
                FileName = chrome,
                Arguments = string.Format("--profile-directory=\"{0}\" \"{1}\"", profileDir, url),
                UseShellExecute = false
            });
        }

        // ==================== AI TOOLS & SOFTWARE UNINSTALL ====================

        public static bool IsToolRunning(ToolItem item)
        {
            if (item == null || item.kind == "url") return false;
            string pname = item.process_name;
            if (string.IsNullOrEmpty(pname) && !string.IsNullOrEmpty(item.target))
                pname = Path.GetFileNameWithoutExtension(item.target);
            if (string.IsNullOrEmpty(pname)) return false;
            try { return Process.GetProcessesByName(pname).Length > 0; }
            catch { return false; }
        }

        public static void LaunchToolItem(ToolItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.target))
                throw new InvalidOperationException("该工具未配置有效路径或网址。");

            if (item.kind == "url")
            {
                Process.Start(new ProcessStartInfo { FileName = item.target, UseShellExecute = true });
                return;
            }
            if (item.kind == "msix" && item.uninstall_hint == "OpenAI.Codex")
            {
                LaunchProfile("default");
                return;
            }
            string processName = string.IsNullOrEmpty(item.process_name) ? Path.GetFileNameWithoutExtension(item.target) : item.process_name;
            bool focused = false;
            if (!string.IsNullOrEmpty(processName))
            {
                foreach (var process in Process.GetProcessesByName(processName))
                    using (process) if (!focused) focused = TryFocusWindow(process);
            }
            if (focused) return;
            var psi = new ProcessStartInfo { FileName = item.target, Arguments = item.arguments ?? "", UseShellExecute = true };
            if (File.Exists(item.target)) psi.WorkingDirectory = Path.GetDirectoryName(item.target);
            Process.Start(psi);
        }

        private static bool TryFocusWindow(Process process)
        {
            try
            {
                IntPtr window = process.MainWindowHandle;
                if (window == IntPtr.Zero) return false;
                if (NativeScreen.IsIconic(window)) ShowWindowAsync(window, 9); // SW_RESTORE
                NativeScreen.SetForegroundWindow(window);
                return true;
            }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        public static Dictionary<string, string> ReadQuickLedgerForms()
        {
            var forms = new Dictionary<string, string>();
            try
            {
                string path = Path.Combine(DataDir, "quick-ledger.json");
                if (!File.Exists(path)) return forms;
                var entries = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8));
                if (entries == null) return forms;
                foreach (var entry in entries)
                {
                    string name = entry.Key.Trim();
                    Uri url;
                    if (name.Length == 0 || !Uri.TryCreate(entry.Value, UriKind.Absolute, out url)
                        || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0
                        || !url.Host.EndsWith(".feishu.cn", StringComparison.OrdinalIgnoreCase)
                        || !IsFeishuLedgerPath(url.AbsolutePath)) continue;
                    forms[name] = url.AbsoluteUri;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return forms;
        }

        private static bool IsFeishuLedgerPath(string path)
        {
            int separator = path.IndexOf('/', 1);
            if (separator < 0 || string.IsNullOrWhiteSpace(path.Substring(separator + 1).Trim('/'))) return false;
            switch (path.Substring(0, separator))
            {
                case "/wiki":
                case "/base":
                case "/sheets":
                case "/docx": return true;
                default: return false;
            }
        }

        private static readonly string QuotaRefillsFile = Path.Combine(DataDir, "quota-refills.json");

        internal static Dictionary<string, long> ReadQuotaRefillHistory()
        {
            try
            {
                if (File.Exists(QuotaRefillsFile))
                {
                    var stored = new JavaScriptSerializer().Deserialize<Dictionary<string, long>>(File.ReadAllText(QuotaRefillsFile, Encoding.UTF8));
                    long cutoff = DateTime.UtcNow.AddDays(-7).Ticks;
                    if (stored != null) return stored.Where(item => item.Value >= cutoff).ToDictionary(item => item.Key, item => item.Value);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return new Dictionary<string, long>();
        }

        internal static void SaveQuotaRefillHistory(Dictionary<string, long> history)
        {
            try { WriteTextAtomically(QuotaRefillsFile, new JavaScriptSerializer().Serialize(history), false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidOperationException) { }
        }

        public static int StopToolItem(ToolItem item)
        {
            if (item == null || item.kind == "url") return 0;
            string pname = item.process_name;
            if (string.IsNullOrEmpty(pname) && !string.IsNullOrEmpty(item.target))
                pname = Path.GetFileNameWithoutExtension(item.target);
            if (string.IsNullOrEmpty(pname)) return 0;

            int killed = 0;
            try
            {
                foreach (var p in Process.GetProcessesByName(pname))
                    using (p)
                    {
                        try { p.Kill(); if (p.WaitForExit(1500)) killed++; }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
            }
            catch { }
            return killed;
        }

        public static string UninstallSoftware(ToolItem item)
        {
            if (item == null) throw new ArgumentNullException("item");
            if (item.kind == "url")
                throw new InvalidOperationException("网页链接无需系统卸载。");

            if (item.kind == "msix" && !string.IsNullOrEmpty(item.uninstall_hint))
            {
                string pkg = item.uninstall_hint.Replace("'", "''");
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = string.Format("-NoProfile -Command \"Get-AppxPackage -Name '{0}' | Remove-AppxPackage\"", pkg),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(30000);
                    if (p.ExitCode != 0)
                        throw new InvalidOperationException("MSIX 卸载未完成，请在 Windows「已安装的应用」中确认。");
                }
                return "已通过系统包管理卸载：" + item.name;
            }

            string uninstallCmd = FindRegistryUninstallCommand(item.uninstall_hint ?? item.name, item.target);
            if (!string.IsNullOrEmpty(uninstallCmd))
            {
                LaunchCommandString(uninstallCmd);
                return "已拉起「" + item.name + "」官方卸载程序";
            }

            Process.Start(new ProcessStartInfo { FileName = "ms-settings:appsfeatures", UseShellExecute = true });
            return "未查到直接卸载项，已打开 Windows「已安装的应用」";
        }

        private static string FindRegistryUninstallCommand(string hintName, string targetExe)
        {
            string targetDir = (!string.IsNullOrEmpty(targetExe) && File.Exists(targetExe))
                ? Path.GetDirectoryName(targetExe).ToLowerInvariant()
                : "";
            string hintLow = (hintName ?? "").ToLowerInvariant();

            Tuple<RegistryKey, string>[] roots = new[]
            {
                Tuple.Create(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
            };

            foreach (var r in roots)
            {
                try
                {
                    using (var baseKey = r.Item1.OpenSubKey(r.Item2))
                    {
                        if (baseKey == null) continue;
                        foreach (string subName in baseKey.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = baseKey.OpenSubKey(subName))
                                {
                                    if (sk == null) continue;
                                    string dn = (sk.GetValue("DisplayName") ?? "").ToString();
                                    string loc = (sk.GetValue("InstallLocation") ?? "").ToString().ToLowerInvariant();
                                    string icon = (sk.GetValue("DisplayIcon") ?? "").ToString().ToLowerInvariant();
                                    string ustr = (sk.GetValue("UninstallString") ?? "").ToString();
                                    if (string.IsNullOrEmpty(ustr)) continue;

                                    if ((!string.IsNullOrEmpty(targetDir) && (loc.Contains(targetDir) || icon.Contains(targetDir)))
                                        || (!string.IsNullOrEmpty(hintLow) && dn.ToLowerInvariant().Contains(hintLow)))
                                        return ustr;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static void LaunchCommandString(string cmdLine)
        {
            cmdLine = cmdLine.Trim();
            string file, args = "";
            if (cmdLine.StartsWith("\""))
            {
                int end = cmdLine.IndexOf('"', 1);
                file = end > 1 ? cmdLine.Substring(1, end - 1) : cmdLine.Trim('"');
                args = end > 1 ? cmdLine.Substring(end + 1).Trim() : "";
            }
            else
            {
                int sp = cmdLine.IndexOf(' ');
                file = sp > 0 ? cmdLine.Substring(0, sp) : cmdLine;
                args = sp > 0 ? cmdLine.Substring(sp + 1).Trim() : "";
            }
            Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = true });
        }

        // ==================== CODEX PROCESS & ACCOUNTS ====================

        public static List<ProcInfo> ScanCodexProcesses(bool requireComplete = false)
        {
            var list = new List<ProcInfo>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process WHERE Name LIKE 'ChatGPT%' OR Name LIKE 'codex%'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementBaseObject obj in results)
                    {
                        try
                        {
                            list.Add(new ProcInfo
                            {
                                ProcessId = Convert.ToInt32(obj["ProcessId"]),
                                ParentProcessId = Convert.ToInt32(obj["ParentProcessId"]),
                                Name = obj["Name"] != null ? obj["Name"].ToString() : "",
                                CommandLine = obj["CommandLine"] != null ? obj["CommandLine"].ToString() : ""
                            });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (requireComplete) throw new InvalidOperationException("无法确认 Codex 进程状态，请稍后再试。", ex);
            }
            if (requireComplete && list.Any(p => string.Equals(p.Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(p.CommandLine)))
                throw new InvalidOperationException("有 Codex 进程无法识别，请先手动退出相关实例。");
            return list;
        }

        private static string NormPath(string p)
        {
            return (p ?? "").Replace('/', '\\').ToLowerInvariant().TrimEnd('\\');
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static string GetUserDataArgument(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return null;
            int count;
            IntPtr argv = CommandLineToArgvW(commandLine, out count);
            if (argv == IntPtr.Zero) return null;
            try
            {
                const string flag = "--user-data-dir";
                for (int i = 1; i < count; i++)
                {
                    string arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                    if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                        return NormPath(arg.Substring(flag.Length + 1));
                    if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < count)
                        return NormPath(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, (i + 1) * IntPtr.Size)));
                }
                return null;
            }
            finally { LocalFree(argv); }
        }

        public static List<int> GetProfilePids(string profileId, string userDataDir, List<ProcInfo> processes)
        {
            if (processes == null || processes.Count == 0) return new List<int>();

            var childrenMap = new Dictionary<int, List<int>>();
            foreach (var p in processes)
            {
                List<int> ch;
                if (!childrenMap.TryGetValue(p.ParentProcessId, out ch))
                {
                    ch = new List<int>();
                    childrenMap[p.ParentProcessId] = ch;
                }
                ch.Add(p.ProcessId);
            }

            var browserRoots = processes.Where(p => string.Equals(p.Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(p.CommandLine)
                && !Regex.IsMatch(p.CommandLine, @"--type(?:=|\s)", RegexOptions.IgnoreCase)).ToList();
            var rootIds = new HashSet<int>(browserRoots.Select(p => p.ProcessId));

            Func<HashSet<int>, HashSet<int>> collectTree = (roots) =>
            {
                var seen = new HashSet<int>(roots);
                var stack = new Stack<int>(roots);
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    List<int> ch;
                    if (childrenMap.TryGetValue(cur, out ch))
                    {
                        foreach (int child in ch)
                            if (!rootIds.Contains(child) && seen.Add(child)) stack.Push(child);
                    }
                }
                return seen;
            };

            var dataArguments = processes.ToDictionary(p => p.ProcessId, p => GetUserDataArgument(p.CommandLine));
            string requested = NormPath(userDataDir);
            string nativeBrowserData = NormPath(Path.Combine(userDataDir, "web", "Codex"));
            var result = new HashSet<int>();
            foreach (var browser in browserRoots)
            {
                var tree = collectTree(new HashSet<int> { browser.ProcessId });
                string explicitData = dataArguments[browser.ProcessId];
                bool matches = explicitData != null
                    ? explicitData == requested || (profileId == "default" && explicitData == nativeBrowserData)
                    : tree.Any(id => dataArguments[id] == requested
                        || (profileId == "default" && dataArguments[id] == nativeBrowserData));
                if (matches) result.UnionWith(tree);
            }
            return result.OrderBy(id => id).ToList();
        }

        private static bool HasVisibleProfileWindow(List<int> pids, List<ProcInfo> processes)
        {
            var candidates = new HashSet<int>(pids);
            foreach (var process in processes)
            {
                if (!candidates.Contains(process.ProcessId) || !string.Equals(process.Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using (var windowProcess = Process.GetProcessById(process.ProcessId))
                        if (windowProcess.MainWindowHandle != IntPtr.Zero) return true;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return false;
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                if (!Directory.Exists(path) && !File.Exists(path)) return false;
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }

        public static long GetDirSizeSafe(string root)
        {
            if (!Directory.Exists(root)) return 0;
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                if (IsReparsePoint(cur)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(cur))
                        try { if (!IsReparsePoint(f)) total += new FileInfo(f).Length; } catch { }
                    foreach (string d in Directory.GetDirectories(cur))
                        if (!IsReparsePoint(d)) stack.Push(d);
                }
                catch { }
            }
            return total;
        }

        public static List<AccountViewModel> BuildAccountViewModels(AccountsStore store = null)
        {
            if (store == null) store = LoadStore();
            var procs = ScanCodexProcesses();
            var list = new List<AccountViewModel>();

            var mainPids = GetProfilePids("default", DefaultUserData, procs);
            list.Add(new AccountViewModel
            {
                Record = new AccountRecord
                {
                    id = "default",
                    name = "主账号（原生默认）",
                    account_type = "permanent",
                    notes = "",
                    config_mode = "native",
                    disable_memories = false
                },
                IsDefault = true,
                Auth = AuthParser.ReadAuthInfo(DefaultCodexHome),
                Running = mainPids.Count > 0,
                WindowVisible = HasVisibleProfileWindow(mainPids, procs),
                PermissionMode = ReadProfilePermissionMode(DefaultCodexHome),
                PidCount = mainPids.Count,
                DiskSizeBytes = null,
                CodexHome = DefaultCodexHome,
                UserDataDir = DefaultUserData
            });

            foreach (var rec in store.accounts)
            {
                string root = Path.Combine(ProfilesDir, rec.id);
                string codexHome = Path.Combine(root, "codex-home");
                string userData = Path.Combine(root, "user-data");
                var pids = GetProfilePids(rec.id, userData, procs);
                list.Add(new AccountViewModel
                {
                    Record = rec,
                    IsDefault = false,
                    Auth = AuthParser.ReadAuthInfo(codexHome),
                    Running = pids.Count > 0,
                    WindowVisible = HasVisibleProfileWindow(pids, procs),
                    PermissionMode = ReadProfilePermissionMode(codexHome),
                    PidCount = pids.Count,
                    DiskSizeBytes = GetDirSizeSafe(root),
                    CodexHome = codexHome,
                    UserDataDir = userData
                });
            }
            return list;
        }

        private static int CopyMissingFiles(string srcDir, string dstDir)
        {
            if (IsReparsePoint(srcDir) || IsReparsePoint(dstDir)) return 0;
            Directory.CreateDirectory(dstDir);
            int copied = 0;
            foreach (string file in Directory.GetFiles(srcDir))
            {
                string target = Path.Combine(dstDir, Path.GetFileName(file));
                if (IsReparsePoint(file) || File.Exists(target) || IsReparsePoint(target)) continue;
                File.Copy(file, target, false);
                copied++;
            }
            foreach (string sub in Directory.GetDirectories(srcDir))
                copied += CopyMissingFiles(sub, Path.Combine(dstDir, Path.GetFileName(sub)));
            return copied;
        }

        public static void UnlinkJunctionsUnder(string root)
        {
            if (!Directory.Exists(root)) return;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                string[] subDirs;
                try { subDirs = Directory.GetDirectories(cur); } catch { continue; }
                foreach (string d in subDirs)
                {
                    if (IsReparsePoint(d)) Directory.Delete(d, false);
                    else stack.Push(d);
                }
            }
        }

        public static List<string> PopulateProfileConfig(string codexHome, string configMode, bool disableMemories)
        {
            Directory.CreateDirectory(codexHome);
            var synced = new List<string>();
            string configPath = Path.Combine(codexHome, "config.toml");

            // Codex owns the settings in each profile, including its sandbox and permissions.
            if (disableMemories && !File.Exists(configPath))
            {
                WriteTextAtomically(configPath, "[memories]\r\ngenerate_memories = false\r\nuse_memories = false\r\n", false);
                synced.Add("初始记忆设置");
            }
            if (configMode == "fresh") return synced;

            string srcAgents = Path.Combine(DefaultCodexHome, "AGENTS.md");
            string dstAgents = Path.Combine(codexHome, "AGENTS.md");
            if (File.Exists(srcAgents) && !IsReparsePoint(srcAgents) && !File.Exists(dstAgents) && !IsReparsePoint(dstAgents))
            {
                File.Copy(srcAgents, dstAgents, false);
                synced.Add("AGENTS.md");
            }

            string srcSkills = Path.Combine(DefaultCodexHome, "skills");
            string dstSkills = Path.Combine(codexHome, "skills");
            if (!Directory.Exists(srcSkills) || IsReparsePoint(dstSkills)) return synced;

            if (configMode == "junction_skills" && !Directory.Exists(dstSkills))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = string.Format("/c mklink /J \"{0}\" \"{1}\"", dstSkills, srcSkills),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    if (process != null && !process.WaitForExit(5000))
                        throw new IOException("创建 Skills 共享目录超时。");
                    if (process != null && process.ExitCode == 0)
                    {
                        synced.Add("Skills（共享）");
                        return synced;
                    }
                }
            }

            int copied = CopyMissingFiles(srcSkills, dstSkills);
            if (copied > 0) synced.Add("Skills（补齐 " + copied + " 个文件）");
            return synced;
        }

        private static string GetConfigRoot(string contents)
        {
            var firstTable = Regex.Match(contents, @"(?m)^[ \t]*\[{1,2}[^\r\n]+\]{1,2}[ \t]*(?:#[^\r\n]*)?\r?$");
            return firstTable.Success ? contents.Substring(0, firstTable.Index) : contents;
        }

        private static Match FindConfigValue(string root, string key)
        {
            return Regex.Match(root, @"(?m)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[ \t]*(?<value>[^\r\n]+)");
        }

        private static bool HasConfigValue(string root, string key, string value)
        {
            var assignment = FindConfigValue(root, key);
            return assignment.Success && Regex.IsMatch(assignment.Groups["value"].Value,
                "^([\"'])" + Regex.Escape(value) + "\\1[ \\t]*(?:#.*)?$");
        }

        private static string ReadProfilePermissionMode(string codexHome)
        {
            string path = Path.Combine(codexHome, "config.toml");
            try
            {
                if (!File.Exists(path)) return "custom";
                string root = GetConfigRoot(File.ReadAllText(path, Encoding.UTF8));
                if (FindConfigValue(root, "default_permissions").Success) return "custom";
                if (HasConfigValue(root, "approval_policy", "never"))
                {
                    if (HasConfigValue(root, "sandbox_mode", "danger-full-access")) return "full";
                    if (HasConfigValue(root, "sandbox_mode", "workspace-write")) return "workspace";
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return "custom";
        }

        private static string SetRootConfigValue(string root, string key, string value, string lineBreak)
        {
            var existing = FindConfigValue(root, key);
            if (!existing.Success) return key + " = \"" + value + "\"" + lineBreak + root;

            string oldValue = existing.Groups["value"].Value;
            if (!Regex.IsMatch(oldValue, @"^(['""])[A-Za-z0-9_-]+\1[ \t]*(?:#.*)?$"))
                throw new InvalidOperationException("该账号使用了复杂的 Codex 权限配置，请在 config.toml 中调整。");

            var match = Regex.Match(oldValue, @"^(['""])[A-Za-z0-9_-]+\1");
            return root.Substring(0, existing.Groups["value"].Index) + "\"" + value + "\""
                + root.Substring(existing.Groups["value"].Index + match.Length);
        }

        private static void ApplyProfilePermissionMode(string codexHome, string mode)
        {
            if (mode != "full" && mode != "workspace") throw new InvalidOperationException("未知权限模式。");
            Directory.CreateDirectory(codexHome);
            string path = Path.Combine(codexHome, "config.toml");
            string contents = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            string root = GetConfigRoot(contents);
            if (FindConfigValue(root, "default_permissions").Success)
                throw new InvalidOperationException("该账号使用了具名权限配置，请在其 config.toml 中管理权限。");

            string lineBreak = contents.Contains("\r\n") ? "\r\n" : "\n";
            string updated = SetRootConfigValue(root, "sandbox_mode", mode == "full" ? "danger-full-access" : "workspace-write", lineBreak);
            updated = SetRootConfigValue(updated, "approval_policy", "never", lineBreak);
            if (updated != root) WriteTextAtomically(path, updated + contents.Substring(root.Length), false);
        }

        public static void SaveProfilePermissionMode(string profileId, string mode)
        {
            lock (ProfileLifecycleLock)
            {
                if (profileId == "default") ApplyProfilePermissionMode(DefaultCodexHome, mode);
                else
                {
                    if (string.IsNullOrEmpty(profileId) || !Regex.IsMatch(profileId, @"\Aacc-[0-9a-f]{8}\z", RegexOptions.IgnoreCase)
                        || !LoadStore().accounts.Any(account => account.id == profileId))
                        throw new InvalidOperationException("未找到该账号。");
                    ApplyProfilePermissionMode(Path.Combine(ProfilesDir, profileId, "codex-home"), mode);
                }
            }
        }

        public static AccountRecord CreateAccount(
            string name, string accountType, DateTimeOffset startedAt, DateTimeOffset? recycleAt,
            double? recycleDays, string configMode, bool disableMemories, string notes, bool launchNow, string permissionMode)
        {
            EnsureDirs();
            string id = "acc-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string root = Path.Combine(ProfilesDir, id);
            string codexHome = Path.Combine(root, "codex-home");
            string userData = Path.Combine(root, "user-data");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(userData);

            PopulateProfileConfig(codexHome, configMode, disableMemories);
            if (!string.IsNullOrEmpty(permissionMode) && permissionMode != "custom")
                ApplyProfilePermissionMode(codexHome, permissionMode);

            var rec = new AccountRecord
            {
                id = id,
                name = name.Trim(),
                account_type = accountType,
                notes = (notes ?? "").Trim(),
                created_at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                started_at = startedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                recycle_at = accountType == "recycled" && recycleAt.HasValue ? recycleAt.Value.ToString("yyyy-MM-ddTHH:mm:sszzz") : null,
                recycle_days = accountType == "recycled" ? recycleDays : null,
                config_mode = configMode,
                disable_memories = disableMemories
            };

            lock (StoreLock)
            {
                var store = LoadStore();
                store.accounts.Add(rec);
                SaveStore(store);
            }

            if (launchNow) LaunchProfile(id);
            return rec;
        }

        public static void UpdateAccount(AccountRecord updated, string permissionMode = null)
        {
            if (updated == null || updated.id == "default") return;
            lock (ProfileLifecycleLock)
            lock (StoreLock)
            {
                var store = LoadStore();
                int idx = store.accounts.FindIndex(a => a.id == updated.id);
                if (idx < 0) throw new InvalidOperationException("未找到该账号记录。");
                string configPath = null;
                string priorConfig = null;
                if (!string.IsNullOrEmpty(permissionMode))
                {
                    if (!Regex.IsMatch(updated.id, @"\Aacc-[0-9a-f]{8}\z", RegexOptions.IgnoreCase))
                        throw new InvalidOperationException("未找到该账号。");
                    string codexHome = Path.Combine(ProfilesDir, updated.id, "codex-home");
                    configPath = Path.Combine(codexHome, "config.toml");
                    priorConfig = File.Exists(configPath) ? File.ReadAllText(configPath, Encoding.UTF8) : null;
                    ApplyProfilePermissionMode(codexHome, permissionMode);
                }
                store.accounts[idx] = updated;
                try { SaveStore(store); }
                catch
                {
                    if (configPath != null)
                    {
                        if (priorConfig == null) File.Delete(configPath);
                        else WriteTextAtomically(configPath, priorConfig, false);
                    }
                    throw;
                }
            }
        }

        public static void LaunchProfile(string profileId)
        {
            lock (ProfileLifecycleLock) LaunchProfileCore(profileId);
        }

        internal static void OpenRunningProfile(string profileId)
        {
            lock (ProfileLifecycleLock)
            {
                string userData = profileId == "default" ? DefaultUserData : Path.Combine(ProfilesDir, profileId, "user-data");
                var processes = ScanCodexProcesses(true);
                var pids = new HashSet<int>(GetProfilePids(profileId, userData, processes));
                foreach (var candidate in processes.Where(p => pids.Contains(p.ProcessId)
                    && string.Equals(p.Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using (var process = Process.GetProcessById(candidate.ProcessId))
                            if (TryFocusWindow(process)) return;
                    }
                    catch (ArgumentException) { }
                }
                LaunchProfileCore(profileId);
            }
        }

        private static void LaunchProfileCore(string profileId)
        {
            string exe = FindCodexExecutable();
            if (string.IsNullOrEmpty(exe))
                throw new InvalidOperationException("未找到 chatgpt-desktop.exe 或已安装的 OpenAI Codex 桌面端。");

            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            if (profileId == "default")
            {
                if (psi.EnvironmentVariables.ContainsKey("CODEX_HOME")) psi.EnvironmentVariables.Remove("CODEX_HOME");
                if (psi.EnvironmentVariables.ContainsKey("CODEX_ELECTRON_USER_DATA_PATH")) psi.EnvironmentVariables.Remove("CODEX_ELECTRON_USER_DATA_PATH");
                Process.Start(psi);
                return;
            }

            var store = LoadStore();
            if (!store.accounts.Any(a => a.id == profileId))
                throw new InvalidOperationException("未找到指定账号：" + profileId);

            string root = Path.Combine(ProfilesDir, profileId);
            string codexHome = Path.Combine(root, "codex-home");
            string userData = Path.Combine(root, "user-data");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(userData);

            psi.Arguments = string.Format("--user-data-dir=\"{0}\"", userData);
            psi.EnvironmentVariables["CODEX_HOME"] = codexHome;
            psi.EnvironmentVariables["CODEX_ELECTRON_USER_DATA_PATH"] = userData;
            Process.Start(psi);
        }

        public static void StopProfileProcesses(string profileId)
        {
            lock (ProfileLifecycleLock)
            {
                string userData = profileId == "default" ? DefaultUserData : Path.Combine(ProfilesDir, profileId, "user-data");
                var processes = ScanCodexProcesses(true);
                var pids = GetProfilePids(profileId, userData, processes);
                if (pids.Count == 0) return;

                foreach (var candidate in processes.Where(p => pids.Contains(p.ProcessId)
                    && string.Equals(p.Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using (var process = Process.GetProcessById(candidate.ProcessId))
                        {
                            if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow();
                        }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }

                for (int i = 0; i < 12; i++)
                {
                    if (GetProfilePids(profileId, userData, ScanCodexProcesses(true)).Count == 0) return;
                    Thread.Sleep(200);
                }
                throw new InvalidOperationException("Codex 仍在运行。请在 Codex 或系统托盘选择退出，然后重试。");
            }
        }

        public static void WipeProfile(string profileId)
        {
            if (string.IsNullOrEmpty(profileId) || !Regex.IsMatch(profileId, @"\Aacc-[0-9a-f]{8}\z", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("只有独立账号可以删除本地数据。");

            string root = Path.Combine(ProfilesDir, profileId);
            lock (ProfileLifecycleLock)
            {
                if (IsReparsePoint(ProfilesDir) || IsReparsePoint(root))
                    throw new InvalidOperationException("账号目录指向其他位置，已取消删除。");
                if (GetProfilePids(profileId, Path.Combine(root, "user-data"), ScanCodexProcesses(true)).Count > 0)
                    throw new InvalidOperationException("请先完全退出此 Codex 实例，再删除本地数据。");

                string pending = root + ".pending-delete-" + Guid.NewGuid().ToString("N");
                lock (StoreLock)
                {
                    var store = LoadStore();
                    if (store.accounts.RemoveAll(a => a.id == profileId) == 0)
                        throw new InvalidOperationException("未找到该账号记录。");

                    if (Directory.Exists(root)) Directory.Move(root, pending);
                    try { SaveStore(store); }
                    catch
                    {
                        if (Directory.Exists(pending)) Directory.Move(pending, root);
                        throw;
                    }
                }

                if (!Directory.Exists(pending)) return;
                try
                {
                    UnlinkJunctionsUnder(pending);
                    ClearReadOnlyRecursive(pending);
                    Directory.Delete(pending, true);
                }
                catch (Exception ex)
                {
                    throw new IOException("账号卡片已移除，剩余本地文件未删完：" + pending, ex);
                }
            }
        }

        private static void ClearReadOnlyRecursive(string root)
        {
            try
            {
                foreach (string f in Directory.GetFiles(root))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                foreach (string d in Directory.GetDirectories(root))
                    if (!IsReparsePoint(d)) ClearReadOnlyRecursive(d);
            }
            catch { }
        }

        public static string ReadChangelogText()
        {
            string p = Path.Combine(BaseDir, "更新日志.md");
            if (!File.Exists(p)) p = Path.Combine(BaseDir, "CHANGELOG.md");
            if (File.Exists(p))
            {
                try { return File.ReadAllText(p, Encoding.UTF8); }
                catch { }
            }
            return "未找到 更新日志.md 文件。";
        }
    }
}
