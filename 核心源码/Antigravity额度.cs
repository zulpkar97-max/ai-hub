using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexLauncher
{
    internal sealed class AntigravityUsage
    {
        public string email { get; set; }
        public string plan { get; set; }
        public string fetched_at { get; set; }
        public Dictionary<string, AntigravityBucket> quota { get; set; }
    }

    internal sealed class AntigravityBucket
    {
        public double? remaining_fraction { get; set; }
        public double? remaining_amount { get; set; }
        public string reset_time { get; set; }
        public bool disabled { get; set; }
    }

    internal static class AntigravityQuota
    {
        private static string WorkDir { get { return Path.Combine(CoreService.DataDir, "agy-quota"); } }
        private static string CachePath { get { return Path.Combine(CoreService.DataDir, "antigravity-usage.json"); } }
        private static string Binary
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"agy\bin\agy.exe"); }
        }

        internal static bool IsInstalled { get { return File.Exists(Binary); } }

        internal static AntigravityUsage ReadCache()
        {
            try { return ReadUsage(CachePath); }
            catch { return null; }
        }

        internal static AntigravityUsage Refresh(CancellationToken cancellation)
        {
            if (!IsInstalled) throw new InvalidOperationException("请先安装 Antigravity CLI。");
            Directory.CreateDirectory(WorkDir);
            string id = Guid.NewGuid().ToString("N");
            string capture = Path.Combine(WorkDir, id + ".json");
            string settings = Path.Combine(WorkDir, id + "-settings.json");
            string script = Path.Combine(WorkDir, id + "-capture.cmd");
            try
            {
                var serializer = new JavaScriptSerializer();
                var exe = typeof(AntigravityQuota).Assembly.Location;
                File.WriteAllText(script, "@echo off\r\n\"%AIHUB_AGY_QUOTA_EXE%\" --capture-antigravity-quota \"%AIHUB_AGY_QUOTA_PATH%\"\r\n", Encoding.ASCII);
                File.WriteAllText(settings, serializer.Serialize(new
                {
                    statusLine = new { type = "command", command = "call " + Path.GetFileName(script) }
                }), new UTF8Encoding(false));

                var environment = new Dictionary<string, string>
                {
                    { "AIHUB_AGY_QUOTA_EXE", exe },
                    { "AIHUB_AGY_QUOTA_PATH", capture },
                    { "AGY_CLI_DISABLE_AUTO_UPDATE", "true" }
                };
                using (var terminal = new QuotaTerminal(Binary, "--mode plan --settings " + Quote(settings), WorkDir, environment))
                {
                    var clock = Stopwatch.StartNew();
                    long firstQuotaAt = -1;
                    while (clock.Elapsed < TimeSpan.FromSeconds(22))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var usage = ReadUsage(capture) ?? ReadUsage(capture + ".partial");
                        if (usage != null)
                        {
                            if (firstQuotaAt < 0) firstQuotaAt = clock.ElapsedMilliseconds;
                            if ((!string.IsNullOrWhiteSpace(usage.plan) && !string.IsNullOrWhiteSpace(usage.email)) ||
                                clock.ElapsedMilliseconds - firstQuotaAt >= 5000 || terminal.Exited)
                            {
                                try { WriteCache(usage, serializer); }
                                catch (IOException) { }
                                catch (UnauthorizedAccessException) { }
                                return usage;
                            }
                        }
                        if (terminal.NeedsSetup)
                            throw new InvalidOperationException("请在 Antigravity CLI 中完成首次设置后重试。");
                        if (terminal.Exited) break;
                        cancellation.WaitHandle.WaitOne(150);
                    }
                }
                throw new InvalidOperationException("未从 Antigravity CLI 取得额度。请确认终端中的 agy 已登录。");
            }
            finally
            {
                DeleteFile(settings);
                DeleteFile(script);
                DeleteFile(capture);
                DeleteFile(capture + ".tmp");
                DeleteFile(capture + ".partial");
                DeleteFile(capture + ".partial.tmp");
            }
        }

        internal static int Capture(string path)
        {
            try
            {
                Guid id;
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), WorkDir, StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path) != ".json" || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out id))
                    return 1;
                var json = new JavaScriptSerializer();
                using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
                {
                    var data = json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    var quota = NormalizeBuckets(data == null ? null : AuthParser.GetDict(data, "quota"), json);
                    if (quota.Count == 0) return 0;
                    var usage = new AntigravityUsage
                    {
                        email = AuthParser.GetStr(data, "email"),
                        plan = AuthParser.GetStr(data, "plan_tier"),
                        quota = quota,
                        fetched_at = DateTimeOffset.UtcNow.ToString("o")
                    };
                    var target = string.IsNullOrWhiteSpace(usage.email) || string.IsNullOrWhiteSpace(usage.plan)
                        ? path + ".partial" : path;
                    if (File.Exists(target)) return 0;
                    File.WriteAllText(target + ".tmp", json.Serialize(usage), new UTF8Encoding(false));
                    File.Move(target + ".tmp", target);
                }
                return 0;
            }
            catch { return 1; }
        }

        private static Dictionary<string, AntigravityBucket> NormalizeBuckets(Dictionary<string, object> input, JavaScriptSerializer json)
        {
            var buckets = new Dictionary<string, AntigravityBucket>();
            if (input == null) return buckets;
            foreach (var entry in input)
            {
                if (!(entry.Value is Dictionary<string, object>)) continue;
                var bucket = json.ConvertToType<AntigravityBucket>(entry.Value);
                if (bucket.remaining_fraction.HasValue || bucket.remaining_amount.HasValue || bucket.disabled)
                    buckets[entry.Key] = bucket;
            }
            return buckets;
        }

        private static AntigravityUsage ReadUsage(string path)
        {
            if (!File.Exists(path)) return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var usage = new JavaScriptSerializer().Deserialize<AntigravityUsage>(reader.ReadToEnd());
                return usage != null && usage.quota != null && usage.quota.Count > 0 ? usage : null;
            }
        }

        private static void WriteCache(AntigravityUsage usage, JavaScriptSerializer serializer)
        {
            var temp = CachePath + ".tmp";
            try
            {
                File.WriteAllText(temp, serializer.Serialize(usage), new UTF8Encoding(false));
                if (File.Exists(CachePath)) File.Replace(temp, CachePath, null);
                else File.Move(temp, CachePath);
            }
            finally { DeleteFile(temp); }
        }

        private static string Quote(string value) { return "\"" + value + "\""; }
        private static void DeleteFile(string path) { try { File.Delete(path); } catch { } }
    }

    // The official status hook runs in the interactive CLI. ConPTY keeps that session off-screen.
    internal sealed class QuotaTerminal : IDisposable
    {
        private IntPtr _console;
        private IntPtr _process;
        private FileStream _input;
        private FileStream _output;
        private Task _drain;
        private volatile bool _needsSetup;

        public bool NeedsSetup { get { return _needsSetup; } }
        public bool Exited { get { return _process == IntPtr.Zero || WaitForSingleObject(_process, 0) == 0; } }

        public QuotaTerminal(string exe, string args, string cwd, Dictionary<string, string> environment)
        {
            SafeFileHandle inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
            try
            {
                Require(CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
                Require(CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));
                int result = CreatePseudoConsole(new Coord { X = 110, Y = 36 }, inputRead, outputWrite, 0, out _console);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                _input = new FileStream(inputWrite, FileAccess.Write);
                inputWrite = null;
                _output = new FileStream(outputRead, FileAccess.Read);
                outputRead = null;
                _drain = Task.Factory.StartNew(Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Start(exe, args, cwd, environment);
            }
            catch
            {
                if (inputRead != null) inputRead.Dispose();
                if (outputWrite != null) outputWrite.Dispose();
                Dispose();
                throw;
            }
            finally
            {
                if (inputRead != null) inputRead.Dispose();
                if (inputWrite != null) inputWrite.Dispose();
                if (outputRead != null) outputRead.Dispose();
                if (outputWrite != null) outputWrite.Dispose();
            }
        }

        private void Start(string exe, string args, string cwd, Dictionary<string, string> environment)
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception();
            var attributes = Marshal.AllocHGlobal(size);
            IntPtr environmentBlock = IntPtr.Zero;
            bool initialized = false;
            try
            {
                Require(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
                initialized = true;
                Require(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020016), _console,
                    new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
                var info = new StartupInfoEx { Size = Marshal.SizeOf(typeof(StartupInfoEx)), AttributeList = attributes };
                var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
                    vars[(string)item.Key] = (string)item.Value;
                foreach (var pair in environment) vars[pair.Key] = pair.Value;
                var block = new StringBuilder();
                foreach (var pair in vars) block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
                environmentBlock = Marshal.StringToHGlobalUni(block.ToString());
                ProcessInfo process;
                Require(CreateProcess(exe, new StringBuilder("\"" + exe + "\" " + args), IntPtr.Zero,
                    IntPtr.Zero, false, 0x00080400, environmentBlock, cwd, ref info, out process));
                _process = process.Process;
                CloseHandle(process.Thread);
            }
            finally
            {
                if (initialized) DeleteProcThreadAttributeList(attributes);
                if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
                Marshal.FreeHGlobal(attributes);
            }
        }

        private void Drain()
        {
            try
            {
                var buffer = new byte[4096];
                string tail = "";
                bool trusted = false;
                int read;
                while ((read = _output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    tail += Encoding.UTF8.GetString(buffer, 0, read);
                    if (!trusted && tail.Contains("Do you trust the contents of this project?"))
                    {
                        trusted = true;
                        _input.WriteByte(13);
                        _input.Flush();
                    }
                    if (tail.Contains("Choose your color scheme:") || tail.Contains("Terms of Service & Data Use"))
                        _needsSetup = true;
                    if (tail.Length > 512) tail = tail.Substring(tail.Length - 512);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (_console != IntPtr.Zero)
            {
                ClosePseudoConsole(_console);
                _console = IntPtr.Zero;
            }
            if (_input != null) _input.Dispose();
            if (_drain != null) _drain.Wait(2000);
            if (_output != null) _output.Dispose();
            if (_process != IntPtr.Zero)
            {
                WaitForSingleObject(_process, 1000);
                CloseHandle(_process);
                _process = IntPtr.Zero;
            }
        }

        private static void Require(bool ok) { if (!ok) throw new Win32Exception(); }

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord { public short X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public int Size;
            public IntPtr Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
            public short Show, ReservedSize;
            public IntPtr ReservedBytes, StdIn, StdOut, StdErr, AttributeList;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
        [DllImport("kernel32.dll")]
        private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr handle);
        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value,
            IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
