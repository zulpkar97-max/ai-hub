using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexLauncher
{
    public class StashManifestEntry
    {
        public string id { get; set; }
        public string name { get; set; }
        public string file_name { get; set; }
        public string scope { get; set; } // "user" | "public"
        public string original_path { get; set; }
        public string stashed_path { get; set; }
        public string category { get; set; } // "ai" | "companion"
        public string stashed_at { get; set; }
    }

    public class DesktopShortcutInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string FileName { get; set; }
        public string Scope { get; set; } // "user" | "public"
        public string OriginalPath { get; set; }
        public string StashedPath { get; set; }
        public string Category { get; set; } // "ai" | "companion"
        public bool Hidden { get; set; }
        public bool RequiresAdmin { get; set; }
    }

    public static class DesktopStashService
    {
        private static readonly object StashLock = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        public static readonly string UserDesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static readonly string PublicDesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        public static readonly string StashDir = Path.Combine(CoreService.DataDir, "desktop-stash");
        public static readonly string ManifestFile = Path.Combine(StashDir, "manifest.json");

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private class ManagedRule
        {
            public string Id;
            public string DisplayName;
            public string Category; // "ai" | "companion"
            public string[] MatchKeywords;
        }

        private static readonly ManagedRule[] Rules = new[]
        {
            new ManagedRule { Id = "shortcut-clash", DisplayName = "Clash Verge", Category = "ai", MatchKeywords = new[] { "clash verge", "clash-verge" } },
            new ManagedRule { Id = "shortcut-gemini", DisplayName = "Gemini", Category = "ai", MatchKeywords = new[] { "gemini" } },
            new ManagedRule { Id = "shortcut-antigravity", DisplayName = "Antigravity", Category = "ai", MatchKeywords = new[] { "antigravity" } },
            new ManagedRule { Id = "shortcut-opendesign", DisplayName = "Open Design", Category = "ai", MatchKeywords = new[] { "open design" } },
            new ManagedRule { Id = "shortcut-codex", DisplayName = "ChatGPT / Codex", Category = "ai", MatchKeywords = new[] { "chatgpt", "codex" } },
            new ManagedRule { Id = "shortcut-chrome", DisplayName = "Chrome", Category = "companion", MatchKeywords = new[] { "chrome", "google chrome" } },
            new ManagedRule { Id = "shortcut-feishu", DisplayName = "飞书", Category = "companion", MatchKeywords = new[] { "飞书", "feishu", "lark" } }
        };

        private static List<StashManifestEntry> LoadManifest()
        {
            Directory.CreateDirectory(StashDir);
            if (!File.Exists(ManifestFile)) return new List<StashManifestEntry>();
            try
            {
                string raw = File.ReadAllText(ManifestFile, Encoding.UTF8);
                var list = Json.Deserialize<List<StashManifestEntry>>(raw);
                if (list == null || list.Any(entry => entry == null))
                    throw new InvalidDataException("快捷方式清单格式无效。");
                return list;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("无法读取快捷方式收纳清单，原文件已保留。", ex);
            }
        }

        private static void SaveManifest(List<StashManifestEntry> entries)
        {
            Directory.CreateDirectory(StashDir);
            CoreService.WriteTextAtomically(ManifestFile, Json.Serialize(entries), true);
        }

        private static ManagedRule MatchRule(string fileNameNoExt)
        {
            if (string.IsNullOrEmpty(fileNameNoExt)) return null;
            string low = fileNameNoExt.Trim().ToLowerInvariant();
            if (low == "ai hub" || low.Contains("ai hub")) return null;

            foreach (var r in Rules)
            {
                foreach (var kw in r.MatchKeywords)
                {
                    if (low == kw || low.StartsWith(kw + " ") || low.Contains(kw))
                        return r;
                }
            }
            return null;
        }

        public static List<DesktopShortcutInfo> ScanShortcuts()
        {
            lock (StashLock)
            {
                Directory.CreateDirectory(StashDir);
                var manifest = LoadManifest();
                var result = new List<DesktopShortcutInfo>();
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. Add currently stashed (hidden) shortcuts that still exist in StashDir
                bool manifestDirty = false;
                var validManifest = new List<StashManifestEntry>();
                foreach (var entry in manifest)
                {
                    if (string.IsNullOrEmpty(entry.stashed_path) || !File.Exists(entry.stashed_path))
                    {
                        manifestDirty = true;
                        continue;
                    }
                    validManifest.Add(entry);
                    if (!seenIds.Add(entry.id)) continue;

                    result.Add(new DesktopShortcutInfo
                    {
                        Id = entry.id,
                        Name = entry.name,
                        FileName = entry.file_name,
                        Scope = entry.scope,
                        OriginalPath = entry.original_path,
                        StashedPath = entry.stashed_path,
                        Category = string.IsNullOrEmpty(entry.category) ? "ai" : entry.category,
                        Hidden = true,
                        RequiresAdmin = string.Equals(entry.scope, "public", StringComparison.OrdinalIgnoreCase)
                    });
                }
                if (manifestDirty) SaveManifest(validManifest);

                // 2. Scan User Desktop & Public Desktop for visible matching .lnk shortcuts
                ScanDirectory(UserDesktopDir, "user", result, seenIds);
                ScanDirectory(PublicDesktopDir, "public", result, seenIds);

                // Sort: AI category first, then rule order
                var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < Rules.Length; i++) orderMap[Rules[i].Id] = i;

                result.Sort((a, b) =>
                {
                    int ca = a.Category == "ai" ? 0 : 1;
                    int cb = b.Category == "ai" ? 0 : 1;
                    if (ca != cb) return ca.CompareTo(cb);
                    int oa = orderMap.ContainsKey(a.Id) ? orderMap[a.Id] : 99;
                    int ob = orderMap.ContainsKey(b.Id) ? orderMap[b.Id] : 99;
                    return oa.CompareTo(ob);
                });

                return result;
            }
        }

        private static void ScanDirectory(string dir, string scope, List<DesktopShortcutInfo> result, HashSet<string> seenIds)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (string fullPath in Directory.GetFiles(dir, "*.lnk"))
                {
                    string fileName = Path.GetFileName(fullPath);
                    string baseName = Path.GetFileNameWithoutExtension(fullPath);
                    var rule = MatchRule(baseName);
                    if (rule == null) continue;
                    if (!seenIds.Add(rule.Id)) continue;

                    string stashName = scope + "__" + fileName;
                    result.Add(new DesktopShortcutInfo
                    {
                        Id = rule.Id,
                        Name = rule.DisplayName,
                        FileName = fileName,
                        Scope = scope,
                        OriginalPath = fullPath,
                        StashedPath = Path.Combine(StashDir, stashName),
                        Category = rule.Category,
                        Hidden = false,
                        RequiresAdmin = string.Equals(scope, "public", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch { }
        }

        public static string SetShortcutHidden(string id, bool hide)
        {
            var list = ScanShortcuts();
            var target = list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (target == null) throw new InvalidOperationException("未找到对应的桌面快捷方式");
            if (target.Hidden == hide) return target.Name + (hide ? " 已处于隐藏状态" : " 已在桌面显示");

            List<string> skipped;
            int changed = ApplyBatch(new List<DesktopShortcutInfo> { target }, hide, out skipped);
            if (changed == 0) throw new InvalidOperationException(skipped.FirstOrDefault() ?? "未能移动快捷方式。");
            return hide
                ? string.Format("已从桌面隐藏「{0}」（随时可一键恢复）", target.Name)
                : string.Format("已恢复「{0}」到桌面", target.Name);
        }

        public static string BatchHideCategory(string categoryOnly)
        {
            var list = ScanShortcuts();
            var targets = list.Where(x => !x.Hidden && (string.IsNullOrEmpty(categoryOnly) || x.Category == categoryOnly)).ToList();
            if (targets.Count == 0) return "没有需要隐藏的快捷方式";
            List<string> skipped;
            int count = ApplyBatch(targets, true, out skipped);
            return DescribeBatch("隐藏", count, skipped);
        }

        public static string BatchRestoreAll()
        {
            var list = ScanShortcuts();
            var targets = list.Where(x => x.Hidden).ToList();
            if (targets.Count == 0) return "当前没有已隐藏的快捷方式";
            List<string> skipped;
            int count = ApplyBatch(targets, false, out skipped);
            return DescribeBatch("恢复", count, skipped);
        }

        private static string DescribeBatch(string action, int count, List<string> skipped)
        {
            string summary = string.Format("已{0} {1} 个快捷方式", action, count);
            return skipped.Count == 0 ? summary : summary + "。跳过：" + string.Join("；", skipped);
        }

        private static int ApplyBatch(List<DesktopShortcutInfo> targets, bool hide, out List<string> skipped)
        {
            lock (StashLock)
            {
                Directory.CreateDirectory(StashDir);
                var manifest = LoadManifest();
                var succeeded = new List<DesktopShortcutInfo>();
                var publicMoves = new List<Tuple<DesktopShortcutInfo, string, string>>();
                skipped = new List<string>();
                foreach (var item in targets)
                {
                    string src = hide ? item.OriginalPath : item.StashedPath;
                    string dst = hide ? item.StashedPath : item.OriginalPath;
                    if (!File.Exists(src))
                    {
                        skipped.Add(item.Name + "（原文件已移动）");
                        continue;
                    }
                    if (File.Exists(dst) || Directory.Exists(dst))
                    {
                        skipped.Add(item.Name + "（目标位置有同名项，已保留两份）");
                        continue;
                    }
                    if (item.RequiresAdmin)
                    {
                        publicMoves.Add(Tuple.Create(item, src, dst));
                        continue;
                    }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Move(src, dst);
                        succeeded.Add(item);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(item.Name + "（" + ex.Message + "）");
                    }
                }

                if (publicMoves.Count > 0)
                {
                    string error = null;
                    try { RunElevatedMoves(publicMoves); }
                    catch (Exception ex) { error = ex.Message; }

                    foreach (var move in publicMoves)
                    {
                        if (File.Exists(move.Item3) && !File.Exists(move.Item2)) succeeded.Add(move.Item1);
                        else skipped.Add(move.Item1.Name + "（" + (error ?? "未移动，可能遇到同名项或文件正在使用") + "）");
                    }
                }

                // 3. Update manifest for all succeeded moves
                foreach (var item in succeeded)
                {
                    manifest.RemoveAll(m => string.Equals(m.id, item.Id, StringComparison.OrdinalIgnoreCase));
                    if (hide)
                    {
                        manifest.Add(new StashManifestEntry
                        {
                            id = item.Id,
                            name = item.Name,
                            file_name = item.FileName,
                            scope = item.Scope,
                            original_path = item.OriginalPath,
                            stashed_path = item.StashedPath,
                            category = item.Category,
                            stashed_at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")
                        });
                    }
                }
                if (succeeded.Count > 0)
                {
                    SaveManifest(manifest);
                    try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }
                }

                return succeeded.Count;
            }
        }

        private static void RunElevatedMoves(List<Tuple<DesktopShortcutInfo, string, string>> pairs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            foreach (var p in pairs)
            {
                string src = p.Item2.Replace("'", "''");
                string dst = p.Item3.Replace("'", "''");
                sb.AppendLine(string.Format("try {{ [IO.File]::Move('{0}', '{1}') }} catch {{ }}", src, dst));
            }

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) throw new InvalidOperationException("无法启动公共桌面操作。");
                    proc.WaitForExit();
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // ERROR_CANCELLED (1223) when user clicks No on UAC dialog
                if (ex.NativeErrorCode == 1223)
                    throw new InvalidOperationException("已取消管理员授权，公共桌面图标未变动");
                throw;
            }
        }
    }
}
