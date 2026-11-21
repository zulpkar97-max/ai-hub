using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace CodexLauncher
{
    public static class Program
    {
        public const string WakeEventName = "Local\\CodexAiHubWakeEvent";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length == 2 && args[0] == "--capture-antigravity-quota")
                return AntigravityQuota.Capture(args[1]);

            // The embedded manifest opts in to PerMonitorV2 before any HWND is created.
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            ConfigureNativeAndManagedLibs();
            CoreService.EnsureDirs();

            if (args != null && args.Length >= 2 && string.Equals(args[0], "--launch", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    CoreService.LaunchProfile(args[1]);
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("启动账号失败：\n" + ex.Message, "AI Hub", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
            }

            if (args != null && args.Length >= 1 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                var vms = CoreService.BuildAccountViewModels();
                var proxy = CoreService.GetProxyStatus();
                var chromes = CoreService.DiscoverChromeProfiles();
                Console.WriteLine("SELFTEST OK: accounts=" + vms.Count + ", proxyInstalled=" + proxy.Installed + ", chromes=" + chromes.Count);
                return 0;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\CodexMultiAccountLauncherSingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(WakeEventName))
                            ev.Set();
                    }
                    catch { }
                    return 0;
                }

                Guid updateToken;
                bool confirmingUpdate = args != null && args.Length == 2 && args[0] == "--update-ready"
                    && Guid.TryParseExact(args[1], "N", out updateToken);
                return RunUi(confirmingUpdate ? args[1] : null);
            }
        }

        private static void ConfigureNativeAndManagedLibs()
        {
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
            string libDir = Path.Combine(baseDir, "资源", "第三方");
            if (!Directory.Exists(libDir)) libDir = Path.Combine(baseDir, "lib");
            if (Directory.Exists(libDir))
            {
                try { SetDllDirectory(libDir); } catch { }
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                {
                    try
                    {
                        string shortName = new AssemblyName(e.Name).Name + ".dll";
                        string candidate = Path.Combine(libDir, shortName);
                        if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                    }
                    catch { }
                    return null;
                };
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunUi(string updateToken)
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            try
            {
                var win = new MainWindow(updateToken);
                app.Run(win);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动 AI Hub：\n" + ex.Message, "AI Hub", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
