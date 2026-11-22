using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexLauncher
{
    internal sealed class ProjectPreview
    {
        internal readonly int Port;
        internal readonly string Entry;
        internal readonly string Path;
        internal readonly bool IsAstra;
        private readonly bool _runtime;
        private readonly bool _python;

        private ProjectPreview(string path, int port, string entry, bool python, bool astra = false, bool runtime = false)
        {
            Path = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
            Port = port;
            Entry = entry;
            IsAstra = astra;
            _runtime = runtime;
            _python = python;
        }

        internal static ProjectPreview For(string path)
        {
            if (!Directory.Exists(path)) return null;
            string name = new DirectoryInfo(path).Name;
            const string pythonEntry = "预览服务.py";
            switch (name.ToLowerInvariant())
            {
                case "af-unknown-astra":
                    if (File.Exists(System.IO.Path.Combine(path, pythonEntry)))
                        return new ProjectPreview(path, 1000, "/index.html#AF研究图", true, astra: true);
                    break;
                case "留学总盘":
                    if (File.Exists(System.IO.Path.Combine(path, pythonEntry)))
                        return new ProjectPreview(path, 3000, "/index.html", true);
                    break;
                case "af-runtime":
                    if (File.Exists(System.IO.Path.Combine(path, @"runtime\vite.runtime.config.mjs")))
                        return new ProjectPreview(path, 4000, "/runtime/index.html", false, runtime: true);
                    break;
                case "portfolio-v2":
                    if (File.Exists(System.IO.Path.Combine(path, "vite.config.js")))
                        return new ProjectPreview(path, 5000, "/", false);
                    break;
            }
            return null;
        }

        internal ProcessStartInfo StartInfo()
        {
            string arguments;
            if (_python)
                arguments = "\"预览服务.py\" --port " + Port;
            else
            {
                const string vite = @"node_modules\vite\bin\vite.js";
                if (!File.Exists(System.IO.Path.Combine(Path, vite)))
                    throw new InvalidOperationException("找不到项目的 Vite，请先安装该项目的依赖。");
                arguments = "\"" + vite + "\" --host 127.0.0.1 --port " + Port + " --strictPort";
                if (_runtime) arguments += @" --config runtime\vite.runtime.config.mjs";
            }
            var start = new ProcessStartInfo
            {
                FileName = _python ? "python.exe" : "node.exe",
                Arguments = arguments,
                WorkingDirectory = Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };
            if (_python) start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            return start;
        }

        internal string Url { get { return "http://127.0.0.1:" + Port + Entry; } }

        internal bool IsReady()
        {
            if (!_runtime) return MatchesPreview(Port, true);
            string html = ReadLocal(Port, Entry);
            return html != null && html.Contains("<title>AF 运行空间");
        }

        internal bool MatchesPreview(int port, bool requireReady)
        {
            if (_runtime) return false;
            string response = ReadLocal(port, "/__preview_status");
            if (response == null) return false;
            try
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(response);
                string path = AuthParser.GetStr(data, "project");
                return path != null &&
                    string.Equals(System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'), Path, StringComparison.OrdinalIgnoreCase) &&
                    (!requireReady || data.ContainsKey("ready") && object.Equals(data["ready"], true));
            }
            catch { return false; }
        }

        internal bool SyncIsIdle()
        {
            if (!IsAstra) return true;
            string response = ReadLocal(Port, "/__research/sync");
            if (response == null) return false;
            try
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(response);
                var sync = AuthParser.GetDict(data, "sync");
                return sync != null && AuthParser.GetStr(sync, "state") != "syncing";
            }
            catch { return false; }
        }

        private static string ReadLocal(int port, string path)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                request.Timeout = 700;
                request.ReadWriteTimeout = 700;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    if (response.StatusCode != HttpStatusCode.OK) return null;
                    var content = new char[8192];
                    int length = reader.ReadBlock(content, 0, content.Length);
                    return new string(content, 0, length);
                }
            }
            catch (WebException) { return null; }
            catch (IOException) { return null; }
        }
    }

    // The window owns these jobs. An independently started listener is never added to one.
    internal sealed class ProjectPreviewManager : IDisposable
    {
        private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        private readonly HashSet<int> _busy = new HashSet<int>();
        private bool _disposed;

        internal bool HasActiveSessions
        {
            get { return _sessions.Count > 0 || _busy.Count > 0; }
        }

        internal string State(int port)
        {
            if (_busy.Contains(port)) return "busy";
            Session session;
            if (!_sessions.TryGetValue(port, out session)) return "idle";
            if (!session.Process.HasExited) return "running";
            _sessions.Remove(port);
            session.Dispose();
            return "idle";
        }

        internal async Task OpenAsync(ProjectPreview preview, Action update)
        {
            if (_disposed || !_busy.Add(preview.Port)) return;
            Session started = null;
            update();
            try
            {
                Session session;
                if (_sessions.TryGetValue(preview.Port, out session) && session.Process.HasExited)
                {
                    _sessions.Remove(preview.Port);
                    session.Dispose();
                    session = null;
                }
                if (session != null && !string.Equals(session.Path, preview.Path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("端口 " + preview.Port + " 已用于另一个项目目录。");

                if (session == null)
                {
                    bool reuse = await Task.Run(() => preview.MatchesPreview(preview.Port, true));
                    if (_disposed) return;
                    if (reuse)
                    {
                        OpenBrowser(preview);
                        return;
                    }
                    if (preview.IsAstra && await Task.Run(() => preview.MatchesPreview(8000, false)))
                        throw new InvalidOperationException("Astra 的旧 8000 预览还在运行，请先结束旧窗口，再用固定端口 1000。");
                    if (_disposed) return;
                    if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == preview.Port))
                        throw new InvalidOperationException("端口 " + preview.Port + " 已被其他进程占用。");
                    started = new Session(preview);
                    session = started;
                    _sessions.Add(preview.Port, session);
                }

                int pid = session.Process.Id;
                var deadline = Stopwatch.StartNew();
                while (!_disposed && !session.Process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(20))
                {
                    bool ready = await Task.Run(() => PreviewJob.OwnsListener(preview.Port, pid) && preview.IsReady());
                    if (_disposed) return;
                    if (ready && !session.Process.HasExited)
                    {
                        OpenBrowser(preview);
                        return;
                    }
                    await Task.Delay(160);
                }
                if (_disposed) return;
                string details = session.LastError;
                throw new InvalidOperationException("无法打开端口 " + preview.Port + " 的项目预览。" +
                    (string.IsNullOrEmpty(details) ? "请检查项目依赖或端口占用。" : " " + details));
            }
            catch
            {
                if (!_disposed && started != null)
                {
                    _sessions.Remove(preview.Port);
                    started.Dispose();
                }
                throw;
            }
            finally
            {
                _busy.Remove(preview.Port);
                if (!_disposed) update();
            }
        }

        internal async Task StopAsync(ProjectPreview preview, Action update)
        {
            if (_disposed || !_busy.Add(preview.Port)) return;
            update();
            try
            {
                Session session;
                if (!_sessions.TryGetValue(preview.Port, out session) || !string.Equals(session.Path, preview.Path, StringComparison.OrdinalIgnoreCase)) return;
                if (!session.Process.HasExited && preview.IsAstra)
                {
                    bool idle = false;
                    for (int wait = 0; wait < 20 && !_disposed && !session.Process.HasExited; wait++)
                    {
                        idle = await Task.Run(() => preview.SyncIsIdle());
                        if (idle || _disposed) break;
                        await Task.Delay(300);
                    }
                    if (_disposed) return;
                    if (!idle && !session.Process.HasExited)
                        throw new InvalidOperationException("Astra 正在同步或无法确认保存状态，请稍等后再停止预览。");
                }
                int pid = session.Process.Id;
                _sessions.Remove(preview.Port);
                session.Dispose();
                for (int i = 0; i < 10 && PreviewJob.OwnsListener(preview.Port, pid); i++)
                    await Task.Delay(100);
            }
            finally
            {
                _busy.Remove(preview.Port);
                if (!_disposed) update();
            }
        }

        private static void OpenBrowser(ProjectPreview preview)
        {
            Process.Start(new ProcessStartInfo { FileName = preview.Url, UseShellExecute = true });
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var session in _sessions.Values) session.Dispose();
            _sessions.Clear();
        }

        private sealed class Session : IDisposable
        {
            internal readonly string Path;
            internal readonly Process Process;
            private readonly PreviewJob _job;
            private volatile string _lastError;
            internal string LastError { get { return _lastError; } }

            internal Session(ProjectPreview preview)
            {
                Path = preview.Path;
                Process = new Process { StartInfo = preview.StartInfo() };
                _job = new PreviewJob();
                bool started = false;
                try
                {
                    Process.ErrorDataReceived += (sender, args) =>
                    {
                        if (string.IsNullOrWhiteSpace(args.Data)) return;
                        string line = args.Data.Trim();
                        _lastError = line.Substring(0, Math.Min(180, line.Length));
                    };
                    started = Process.Start();
                    if (!started) throw new InvalidOperationException("无法启动项目预览。");
                    _job.Attach(Process);
                    Process.BeginErrorReadLine();
                    Process.BeginOutputReadLine();
                }
                catch
                {
                    if (started)
                    {
                        try { if (!Process.HasExited) Process.Kill(); }
                        catch (InvalidOperationException) { }
                        catch (Win32Exception) { }
                    }
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                _job.Dispose();
                Process.Dispose();
            }
        }
    }
}
