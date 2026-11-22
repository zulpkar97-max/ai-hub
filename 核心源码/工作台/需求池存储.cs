using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexLauncher
{
    public class BacklogItem
    {
        public string id { get; set; }
        public string title { get; set; }
        public string project { get; set; }
        public string status { get; set; }
        public string priority { get; set; }
        public string notes { get; set; }
        public string created_at { get; set; }
        public string updated_at { get; set; }
    }

    public class BacklogStore
    {
        public int version { get; set; }
        public List<BacklogItem> items { get; set; }

        public BacklogStore()
        {
            version = 1;
            items = new List<BacklogItem>();
        }
    }

    internal static class BacklogStorage
    {
        private static readonly object SyncLock = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 20 * 1024 * 1024 };
        private static readonly string BacklogFile = Path.Combine(CoreService.DataDir, "backlog.json");

        private static readonly Regex ChecklistLine = new Regex(@"^(\s*-\s+)(?:\[([ xX])\]\s+)?(.*)$");
        private static readonly List<DeletedItem> RecentlyDeleted = new List<DeletedItem>();

        private sealed class DeletedItem
        {
            public string Receipt;
            public BacklogItem Item;
            public int Index;
            public DateTimeOffset Expires;
        }

        private static readonly HashSet<string> ValidProjects = new HashSet<string>(StringComparer.Ordinal)
        {
            "AI Hub", "Life Hub", "通用"
        };

        private static readonly HashSet<string> ValidStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            "idea", "planned", "doing", "done", "paused"
        };

        private static readonly HashSet<string> ValidPriorities = new HashSet<string>(StringComparer.Ordinal)
        {
            "high", "medium", "low"
        };

        internal static List<BacklogItem> LoadItems()
        {
            lock (SyncLock)
            {
                return ReadStore().items.Select(Clone).ToList();
            }
        }

        internal static List<BacklogItem> SaveItem(string id, string title, string project, string status, string priority, string notes)
        {
            string cleanTitle = (title ?? "").Trim();
            if (cleanTitle.Length == 0)
                throw new InvalidOperationException("需求标题不能为空。");

            string cleanProject = NormalizeValue(project, ValidProjects, "Life Hub");
            string cleanStatus = NormalizeValue(status, ValidStatuses, "idea");
            string cleanPriority = NormalizeValue(priority, ValidPriorities, "medium");
            string cleanNotes = (notes ?? "").Replace("\r\n", "\n").Trim();
            string now = DateTimeOffset.Now.ToString("o");

            lock (SyncLock)
            {
                var store = ReadStore();
                BacklogItem target = string.IsNullOrEmpty(id)
                    ? null
                    : store.items.FirstOrDefault(item => string.Equals(item.id, id, StringComparison.Ordinal));

                if (target == null)
                {
                    if (!string.IsNullOrEmpty(id))
                        throw new InvalidOperationException("未找到对应的需求条目。");

                    target = new BacklogItem
                    {
                        id = "req-" + Guid.NewGuid().ToString("N").Substring(0, 10),
                        created_at = now
                    };
                    store.items.Insert(0, target);
                }

                target.title = cleanTitle;
                target.project = cleanProject;
                target.status = cleanStatus;
                target.priority = cleanPriority;
                target.notes = cleanNotes;
                target.updated_at = now;

                WriteStore(store);
                return store.items.Select(Clone).ToList();
            }
        }

        internal static List<BacklogItem> SetStatus(string id, string status)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("缺少需求标识。");
            if (!ValidStatuses.Contains(status ?? ""))
                throw new InvalidOperationException("无效的需求状态。");

            lock (SyncLock)
            {
                var store = ReadStore();
                var target = store.items.FirstOrDefault(item => string.Equals(item.id, id, StringComparison.Ordinal));
                if (target == null)
                    throw new InvalidOperationException("未找到对应的需求条目。");

                target.status = status;
                target.updated_at = DateTimeOffset.Now.ToString("o");
                WriteStore(store);
                return store.items.Select(Clone).ToList();
            }
        }

        internal static List<BacklogItem> SetChecklistLine(string id, int index, string expectedText, bool isChecked)
        {
            lock (SyncLock)
            {
                var store = ReadStore();
                var target = store.items.FirstOrDefault(item => item.id == id);
                if (target == null) throw new InvalidOperationException("未找到对应的需求条目。");

                var lines = (target.notes ?? "").Replace("\r\n", "\n").Split('\n');
                var match = index >= 0 && index < lines.Length ? ChecklistLine.Match(lines[index]) : null;
                if (match == null || !match.Success || match.Groups[3].Value != expectedText)
                    throw new InvalidOperationException("清单内容已变更，请重新检查后再勾选。");

                lines[index] = match.Groups[1].Value + (isChecked ? "[x] " : "[ ] ") + expectedText;
                target.notes = string.Join("\n", lines);
                target.updated_at = DateTimeOffset.Now.ToString("o");
                WriteStore(store);
                return store.items.Select(Clone).ToList();
            }
        }

        internal static List<BacklogItem> SetMetadata(string id, string field, string value)
        {
            lock (SyncLock)
            {
                var store = ReadStore();
                var target = store.items.FirstOrDefault(item => item.id == id);
                if (target == null) throw new InvalidOperationException("未找到对应的需求条目。");

                if (field == "priority" && ValidPriorities.Contains(value)) target.priority = value;
                else if (field == "project" && ValidProjects.Contains(value)) target.project = value;
                else throw new InvalidOperationException("无效的需求属性。");

                target.updated_at = DateTimeOffset.Now.ToString("o");
                WriteStore(store);
                return store.items.Select(Clone).ToList();
            }
        }

        internal static List<BacklogItem> DeleteItem(string id, out string receipt)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("缺少需求标识。");

            lock (SyncLock)
            {
                var store = ReadStore();
                int index = store.items.FindIndex(item => item.id == id);
                if (index < 0) throw new InvalidOperationException("未找到要删除的需求条目。");

                var removed = store.items[index];
                store.items.RemoveAt(index);
                WriteStore(store);

                var now = DateTimeOffset.UtcNow;
                RecentlyDeleted.RemoveAll(entry => entry.Expires <= now);
                if (RecentlyDeleted.Count >= 20) RecentlyDeleted.RemoveAt(0);
                receipt = Guid.NewGuid().ToString("N");
                RecentlyDeleted.Add(new DeletedItem { Receipt = receipt, Item = removed, Index = index, Expires = now.AddMinutes(2) });
                return store.items.Select(Clone).ToList();
            }
        }

        internal static List<BacklogItem> RestoreItem(string receipt)
        {
            lock (SyncLock)
            {
                var now = DateTimeOffset.UtcNow;
                RecentlyDeleted.RemoveAll(entry => entry.Expires <= now);
                var entryToRestore = RecentlyDeleted.FirstOrDefault(entry => entry.Receipt == receipt);
                if (entryToRestore == null) throw new InvalidOperationException("撤销已过期。");

                var store = ReadStore();
                if (store.items.Any(item => item.id == entryToRestore.Item.id))
                    throw new InvalidOperationException("该需求已存在。");

                store.items.Insert(Math.Min(entryToRestore.Index, store.items.Count), entryToRestore.Item);
                WriteStore(store);
                RecentlyDeleted.Remove(entryToRestore);
                return store.items.Select(Clone).ToList();
            }
        }

        internal static List<BacklogItem> ReplaceItems(List<BacklogItem> items)
        {
            lock (SyncLock)
            {
                var store = new BacklogStore();
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        if (it == null || string.IsNullOrWhiteSpace(it.title)) continue;
                        var clean = Clone(it);
                        if (string.IsNullOrWhiteSpace(clean.id)) clean.id = "bl-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        clean.project = NormalizeValue(clean.project, ValidProjects, "Life Hub");
                        clean.status = NormalizeValue(clean.status, ValidStatuses, "idea");
                        clean.priority = NormalizeValue(clean.priority, ValidPriorities, "medium");
                        store.items.Add(clean);
                    }
                }
                WriteStore(store);
                RecentlyDeleted.Clear();
                return store.items.Select(Clone).ToList();
            }
        }

        private static BacklogStore ReadStore()
        {
            Directory.CreateDirectory(CoreService.DataDir);
            if (!File.Exists(BacklogFile)) return new BacklogStore();

            try
            {
                string raw = File.ReadAllText(BacklogFile, Encoding.UTF8);
                var store = Json.Deserialize<BacklogStore>(raw);
                if (store == null || store.items == null)
                    throw new InvalidDataException("需求池数据结构无效。");

                store.items = store.items.Where(item => item != null && !string.IsNullOrWhiteSpace(item.title)).ToList();
                return store;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("无法读取需求池数据文件，已保留原文件。", ex);
            }
        }

        private static void WriteStore(BacklogStore store)
        {
            Directory.CreateDirectory(CoreService.DataDir);
            CoreService.WriteTextAtomically(BacklogFile, Json.Serialize(store), true);
        }

        private static string NormalizeValue(string value, HashSet<string> allowed, string fallback)
        {
            string trimmed = (value ?? "").Trim();
            return allowed.Contains(trimmed) ? trimmed : fallback;
        }

        private static BacklogItem Clone(BacklogItem item)
        {
            return new BacklogItem
            {
                id = item.id,
                title = item.title,
                project = item.project,
                status = item.status,
                priority = item.priority,
                notes = item.notes ?? "",
                created_at = item.created_at,
                updated_at = item.updated_at
            };
        }
    }
}
