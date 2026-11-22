using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexLauncher
{
    public class ProjectRecord
    {
        public string id { get; set; }
        public string name { get; set; }
        public string path { get; set; }
        public bool hidden { get; set; }
        public bool pinned { get; set; }
        public string notes { get; set; }
        public string created_at { get; set; }
        public string updated_at { get; set; }
    }

    public class ProjectConfigStore
    {
        public int version { get; set; }
        public List<string> scan_roots { get; set; }
        public List<ProjectRecord> items { get; set; }
        public List<string> order { get; set; }

        public ProjectConfigStore()
        {
            version = 1;
            scan_roots = new List<string>();
            items = new List<ProjectRecord>();
            order = new List<string>();
        }
    }

    public class ProjectViewModel
    {
        public string id { get; set; }
        public string name { get; set; }
        public string folder_name { get; set; }
        public string path { get; set; }
        public string source { get; set; } // "manual" | "scanned"
        public bool exists { get; set; }
        public bool hidden { get; set; }
        public bool pinned { get; set; }
        public string notes { get; set; }
        public bool is_git { get; set; }
        public string branch { get; set; }
        public int dirty_count { get; set; }
        public string last_commit_time { get; set; }
        public string last_commit_msg { get; set; }
        public string remote_url { get; set; }
        public bool has_preview { get; set; }
        public int preview_port { get; set; }
        public bool is_self { get; set; }
        public long sort_ts { get; set; }
    }

    internal static class ProjectStorage
    {
        private static readonly object SyncLock = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
        private static readonly string ProjectsFile = Path.Combine(CoreService.DataDir, "projects.json");

        private static readonly string[] IgnoredDirNames =
        {
            ".git", ".vs", "node_modules", "__pycache__", "bin", "obj", ".playwright-cli"
        };

        private static readonly string[] ProjectMarkers =
        {
            ".git", "启动预览.ps1", "AIHub.csproj", "package.json", "index.html", "README.md"
        };

        internal static List<string> LoadScanRoots()
        {
            lock (SyncLock)
            {
                return ReadStore().scan_roots.ToList();
            }
        }

        internal static List<ProjectViewModel> BuildViewModels()
        {
            ProjectConfigStore store;
            lock (SyncLock)
            {
                store = ReadStore();
            }

            var manualByPath = new Dictionary<string, ProjectRecord>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<Tuple<ProjectRecord, string>>();

            foreach (var item in store.items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.path)) continue;
                string norm = NormPath(item.path);
                if (manualByPath.ContainsKey(norm)) continue;
                manualByPath[norm] = item;
                candidates.Add(Tuple.Create(item, "manual"));
            }

            foreach (string root in store.scan_roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string dirName = Path.GetFileName(dir);
                        if (string.IsNullOrEmpty(dirName) || dirName.StartsWith(".", StringComparison.Ordinal)) continue;
                        if (IgnoredDirNames.Any(ig => string.Equals(ig, dirName, StringComparison.OrdinalIgnoreCase))) continue;
                        if (!LooksLikeProject(dir)) continue;

                        string norm = NormPath(dir);
                        if (manualByPath.ContainsKey(norm)) continue;
                        manualByPath[norm] = null;

                        var synth = new ProjectRecord
                        {
                            id = StablePathId(norm),
                            name = dirName,
                            path = dir,
                            hidden = false,
                            pinned = false,
                            notes = ""
                        };
                        candidates.Add(Tuple.Create(synth, "scanned"));
                    }
                }
                catch { }
            }

            var results = new ProjectViewModel[candidates.Count];
            Parallel.For(0, candidates.Count, idx =>
            {
                results[idx] = InspectProject(candidates[idx].Item1, candidates[idx].Item2);
            });

            return SortProjects(results, store.order);
        }

        internal static List<ProjectViewModel> ApplySavedOrder(IEnumerable<ProjectViewModel> projects)
        {
            lock (SyncLock) return SortProjects(projects, ReadStore().order);
        }

        private static List<ProjectViewModel> SortProjects(IEnumerable<ProjectViewModel> projects, List<string> order)
        {
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in order)
                if (!string.IsNullOrEmpty(id) && !rank.ContainsKey(id)) rank.Add(id, rank.Count);
            return projects
                .OrderBy(p => !string.IsNullOrEmpty(p.id) && rank.ContainsKey(p.id) ? rank[p.id] : int.MaxValue)
                .ThenBy(p => p.hidden)
                .ThenByDescending(p => p.sort_ts)
                .ThenBy(p => p.name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        internal static void SaveOrder(List<string> visibleOrder)
        {
            lock (SyncLock)
            {
                var store = ReadStore();
                store.order = visibleOrder.Concat(store.order.Except(visibleOrder, StringComparer.Ordinal)).ToList();
                WriteStore(store);
            }
        }

        internal static void SaveItem(string id, string path, string name, string notes, bool pinned, bool hidden)
        {
            string cleanPath = (path ?? "").Trim().Trim('"');
            if (cleanPath.Length == 0)
                throw new InvalidOperationException("项目路径不能为空。");

            string norm = NormPath(cleanPath);
            string folderName = Path.GetFileName(norm.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string cleanName = string.IsNullOrWhiteSpace(name) ? (string.IsNullOrEmpty(folderName) ? norm : folderName) : name.Trim();
            string cleanNotes = (notes ?? "").Replace("\r\n", "\n").Trim();
            string now = DateTimeOffset.Now.ToString("o");

            lock (SyncLock)
            {
                var store = ReadStore();
                ProjectRecord target = FindRecord(store, id, norm);
                if (target == null)
                {
                    target = new ProjectRecord
                    {
                        id = string.IsNullOrEmpty(id) ? StablePathId(norm) : id,
                        created_at = now
                    };
                    store.items.Add(target);
                }

                target.name = cleanName;
                target.path = cleanPath;
                target.notes = cleanNotes;
                target.pinned = pinned;
                target.hidden = hidden;
                target.updated_at = now;
                WriteStore(store);
            }
        }

        internal static void SetFlag(string id, string path, string flagName, bool value)
        {
            string norm = string.IsNullOrWhiteSpace(path) ? "" : NormPath(path);
            string folderName = string.IsNullOrEmpty(norm) ? "" : Path.GetFileName(norm.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string now = DateTimeOffset.Now.ToString("o");

            lock (SyncLock)
            {
                var store = ReadStore();
                ProjectRecord target = FindRecord(store, id, norm);
                if (target == null)
                {
                    if (string.IsNullOrEmpty(norm))
                        throw new InvalidOperationException("未找到对应项目。");
                    target = new ProjectRecord
                    {
                        id = string.IsNullOrEmpty(id) ? StablePathId(norm) : id,
                        name = string.IsNullOrEmpty(folderName) ? norm : folderName,
                        path = path.Trim(),
                        notes = "",
                        created_at = now
                    };
                    store.items.Add(target);
                }

                if (flagName == "hidden") target.hidden = value;
                else if (flagName == "pinned")
                {
                    target.pinned = value;
                    if (value && store.order.Count > 0)
                    {
                        store.order.Remove(target.id);
                        store.order.Insert(0, target.id);
                    }
                }
                target.updated_at = now;
                WriteStore(store);
            }
        }

        internal static void RemoveManualItem(string id, string path)
        {
            string norm = string.IsNullOrWhiteSpace(path) ? "" : NormPath(path);
            lock (SyncLock)
            {
                var store = ReadStore();
                int removed = store.items.RemoveAll(it =>
                    (!string.IsNullOrEmpty(id) && string.Equals(it.id, id, StringComparison.Ordinal))
                    || (!string.IsNullOrEmpty(norm) && string.Equals(NormPath(it.path), norm, StringComparison.OrdinalIgnoreCase)));
                if (removed == 0)
                    throw new InvalidOperationException("扫描目录自动发现的项目无法直接移除，请点击「隐藏」将其收纳。");
                WriteStore(store);
            }
        }

        internal static void SaveScanRoots(IEnumerable<string> roots)
        {
            var cleaned = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots != null)
            {
                foreach (string raw in roots)
                {
                    string trimmed = (raw ?? "").Trim().Trim('"');
                    if (trimmed.Length == 0) continue;
                    string norm = NormPath(trimmed);
                    if (seen.Add(norm)) cleaned.Add(trimmed);
                }
            }

            lock (SyncLock)
            {
                var store = ReadStore();
                store.scan_roots = cleaned;
                WriteStore(store);
            }
        }

        private static ProjectRecord FindRecord(ProjectConfigStore store, string id, string normPath)
        {
            return store.items.FirstOrDefault(it =>
                (!string.IsNullOrEmpty(id) && string.Equals(it.id, id, StringComparison.Ordinal))
                || (!string.IsNullOrEmpty(normPath) && string.Equals(NormPath(it.path), normPath, StringComparison.OrdinalIgnoreCase)));
        }

        private static ProjectViewModel InspectProject(ProjectRecord rec, string source)
        {
            string path = rec.path ?? "";
            bool exists = Directory.Exists(path);
            var preview = exists ? ProjectPreview.For(path) : null;
            string folderName = Path.GetFileName(NormPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(folderName)) folderName = path;

            bool isSelf = exists && File.Exists(Path.Combine(path, "AIHub.csproj"));
            string displayName = string.IsNullOrWhiteSpace(rec.name) ? folderName : rec.name.Trim();
            if (isSelf || string.Equals(displayName, "AI Hub", StringComparison.OrdinalIgnoreCase) || string.Equals(displayName, "ai-hub", StringComparison.OrdinalIgnoreCase))
                displayName = "AI BASE";
            else if (string.Equals(displayName, "留学总盘", StringComparison.OrdinalIgnoreCase) || string.Equals(displayName, "Life Hub", StringComparison.OrdinalIgnoreCase))
                displayName = "LIFE BASE";

            var vm = new ProjectViewModel
            {
                id = string.IsNullOrEmpty(rec.id) ? StablePathId(NormPath(path)) : rec.id,
                name = displayName,
                folder_name = folderName,
                path = path,
                source = source,
                exists = exists,
                hidden = rec.hidden,
                pinned = rec.pinned,
                notes = rec.notes ?? "",
                is_git = false,
                branch = "",
                dirty_count = 0,
                last_commit_time = "",
                last_commit_msg = "",
                remote_url = "",
                has_preview = exists && (preview != null || File.Exists(Path.Combine(path, "启动预览.ps1"))),
                preview_port = preview == null ? 0 : preview.Port,
                is_self = isSelf,
                sort_ts = 0
            };

            if (!exists) return vm;

            try { vm.sort_ts = new DirectoryInfo(path).LastWriteTimeUtc.Ticks; }
            catch { }

            if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git")))
                return vm;

            vm.is_git = true;
            string branch = RunGit(path, "rev-parse --abbrev-ref HEAD");
            if (!string.IsNullOrEmpty(branch)) vm.branch = branch.Trim();

            string statusRaw = RunGit(path, "status --porcelain");
            if (!string.IsNullOrEmpty(statusRaw))
            {
                vm.dirty_count = statusRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            }

            string logRaw = RunGit(path, "log -1 --format=%ct|%s");
            if (!string.IsNullOrEmpty(logRaw))
            {
                int sep = logRaw.IndexOf('|');
                if (sep > 0)
                {
                    long unixSec;
                    if (long.TryParse(logRaw.Substring(0, sep).Trim(), out unixSec))
                    {
                        DateTimeOffset dto = DateTimeOffset.FromUnixTimeSeconds(unixSec).ToLocalTime();
                        vm.last_commit_time = dto.ToString("yyyy-MM-dd HH:mm");
                        vm.sort_ts = dto.UtcTicks;
                    }
                    vm.last_commit_msg = logRaw.Substring(sep + 1).Trim();
                }
            }

            string remoteRaw = RunGit(path, "remote get-url origin");
            if (!string.IsNullOrEmpty(remoteRaw)) vm.remote_url = NormalizeRemoteUrl(remoteRaw.Trim());

            return vm;
        }

        private static string RunGit(string workDir, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return "";
                    string output = proc.StandardOutput.ReadToEnd();
                    if (!proc.WaitForExit(2500))
                    {
                        try { proc.Kill(); } catch { }
                        return "";
                    }
                    return proc.ExitCode == 0 ? output : "";
                }
            }
            catch { return ""; }
        }

        private static string NormalizeRemoteUrl(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string url = raw;
            if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
                url = "https://github.com/" + url.Substring("git@github.com:".Length);
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - 4);
            Uri parsed;
            if (Uri.TryCreate(url, UriKind.Absolute, out parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
                return parsed.AbsoluteUri;
            return "";
        }

        private static bool LooksLikeProject(string dir)
        {
            foreach (string marker in ProjectMarkers)
            {
                string full = Path.Combine(dir, marker);
                if (File.Exists(full) || Directory.Exists(full)) return true;
            }
            return false;
        }

        private static string StablePathId(string normPath)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normPath.ToLowerInvariant()));
                var sb = new StringBuilder("prj-", 16);
                for (int i = 0; i < 5; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string NormPath(string path)
        {
            try { return Path.GetFullPath((path ?? "").Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return (path ?? "").Trim(); }
        }

        private static ProjectConfigStore ReadStore()
        {
            Directory.CreateDirectory(CoreService.DataDir);
            if (!File.Exists(ProjectsFile)) return new ProjectConfigStore();

            try
            {
                string raw = File.ReadAllText(ProjectsFile, Encoding.UTF8);
                var store = Json.Deserialize<ProjectConfigStore>(raw);
                if (store == null) throw new InvalidDataException("项目配置格式无效。");
                if (store.scan_roots == null) store.scan_roots = new List<string>();
                if (store.items == null) store.items = new List<ProjectRecord>();
                if (store.order == null) store.order = new List<string>();
                return store;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("无法读取项目管理配置文件，已保留原文件。", ex);
            }
        }

        private static void WriteStore(ProjectConfigStore store)
        {
            Directory.CreateDirectory(CoreService.DataDir);
            CoreService.WriteTextAtomically(ProjectsFile, Json.Serialize(store), true);
        }
    }
}
