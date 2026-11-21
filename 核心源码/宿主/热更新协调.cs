using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace CodexLauncher
{
    internal static class LocalUpdate
    {
        private const string ReadyEventPrefix = "Local\\AIHubUpdateReady-";
        private static string PendingPath { get { return Path.Combine(CoreService.BaseDir, "CodexLauncher.next.exe"); } }
        private static string UpdaterPath { get { return Path.Combine(CoreService.BaseDir, "应用更新.ps1"); } }

        internal static bool IsPending { get { return File.Exists(PendingPath) && File.Exists(UpdaterPath); } }

        internal static void Start()
        {
            if (!IsPending) throw new InvalidOperationException("目前没有待安装的本地更新。");
            string hash;
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(PendingPath))
                hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");

            using (var current = Process.GetCurrentProcess())
            {
                string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                var start = new ProcessStartInfo
                {
                    FileName = powershell,
                    Arguments = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{0}\" -RunningProcessId {1} -RunningStartTicks {2} -ExpectedHash {3} -ReadyToken {4}",
                        UpdaterPath, current.Id, current.StartTime.ToUniversalTime().Ticks, hash, Guid.NewGuid().ToString("N")),
                    WorkingDirectory = CoreService.BaseDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var helper = Process.Start(start))
                    if (helper == null) throw new InvalidOperationException("无法启动 AI Hub 更新程序。");
            }
        }

        internal static void SignalReady(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            try
            {
                using (var ready = EventWaitHandle.OpenExisting(ReadyEventPrefix + token))
                    ready.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }
    }
}

