using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexLauncher
{
    public partial class MainWindow
    {
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
                            _quotaAutoRefreshEnabled = _store.settings.quota_auto_refresh;
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

                    case "saveBacklogItem":
                        _backlogItems = BacklogStorage.SaveItem(
                            AuthParser.GetStr(data, "id"),
                            AuthParser.GetStr(data, "title"),
                            AuthParser.GetStr(data, "project"),
                            AuthParser.GetStr(data, "status"),
                            AuthParser.GetStr(data, "priority"),
                            AuthParser.GetStr(data, "notes"));
                        PushFullState();
                        ReplyJs(reqId, true, null, null);
                        SendToastToJs(string.IsNullOrEmpty(AuthParser.GetStr(data, "id")) ? "已添加到需求池" : "需求已更新", false);
                        break;

                    case "setBacklogStatus":
                        _backlogItems = BacklogStorage.SetStatus(
                            AuthParser.GetStr(data, "id"),
                            AuthParser.GetStr(data, "status"));
                        PushFullState();
                        ReplyJs(reqId, true, null, null);
                        break;

                    case "deleteBacklogItem":
                        _backlogItems = BacklogStorage.DeleteItem(AuthParser.GetStr(data, "id"));
                        PushFullState();
                        ReplyJs(reqId, true, null, null);
                        SendToastToJs("已删除需求", false);
                        break;

                    case "copyText":
                        {
                            string text = AuthParser.GetStr(data, "text") ?? "";
                            if (text.Length > 0) Clipboard.SetText(text);
                            ReplyJs(reqId, true, null, null);
                            break;
                        }

                    default:
                        ReplyJs(reqId, false, null, "不支持的操作。");
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
