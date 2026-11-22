using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using GdiColor = System.Drawing.Color;
using GdiRect = System.Drawing.Rectangle;

namespace CodexLauncher
{
    public static class HotkeyHelper
    {
        public const int WM_HOTKEY = 0x0312;
        public const int SCREENSHOT_HOTKEY_ID = 0x51A1;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static bool TryParse(string spec, out uint modifiers, out uint vk)
        {
            modifiers = MOD_NOREPEAT;
            vk = 0;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            uint pureMod = 0;
            bool allowNoModifier = false;
            string[] parts = spec.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string p = raw.Trim().ToUpperInvariant();
                if (p == "CTRL" || p == "CONTROL") { modifiers |= MOD_CONTROL; pureMod |= MOD_CONTROL; }
                else if (p == "SHIFT") { modifiers |= MOD_SHIFT; pureMod |= MOD_SHIFT; }
                else if (p == "ALT") { modifiers |= MOD_ALT; pureMod |= MOD_ALT; }
                else if (p == "WIN") { modifiers |= MOD_WIN; pureMod |= MOD_WIN; }
                else if (p == "PRINTSCREEN" || p == "PRTSC" || p == "SNAPSHOT")
                {
                    vk = 0x2C;
                    allowNoModifier = true;
                }
                else if (p == "SPACE") vk = 0x20;
                else if (p == "~" || p == "`" || p == "OEM3") vk = 0xC0;
                else if (p.Length == 1 && p[0] >= 'A' && p[0] <= 'Z') vk = (uint)p[0];
                else if (p.Length == 1 && p[0] >= '0' && p[0] <= '9') vk = (uint)p[0];
                else if (p.StartsWith("F") && p.Length >= 2)
                {
                    int fn;
                    if (int.TryParse(p.Substring(1), out fn) && fn >= 1 && fn <= 12)
                    {
                        vk = (uint)(0x70 + fn - 1);
                        allowNoModifier = true;
                    }
                }
            }
            if (vk == 0) return false;
            if (!allowNoModifier && pureMod == 0) return false;
            return true;
        }

        public static bool Register(IntPtr hwnd, int id, string spec, out string errorMsg)
        {
            errorMsg = "";
            try { UnregisterHotKey(hwnd, id); } catch { }
            uint mod, vk;
            if (!TryParse(spec, out mod, out vk))
            {
                errorMsg = "快捷键格式无效：" + spec;
                return false;
            }
            bool ok = RegisterHotKey(hwnd, id, mod, vk);
            if (!ok)
            {
                errorMsg = string.Format("快捷键 {0} 已被其他程序占用（如飞书），请关闭冲突软件后重试或更换快捷键", spec);
            }
            return ok;
        }

        public static void Unregister(IntPtr hwnd, int id)
        {
            try { UnregisterHotKey(hwnd, id); } catch { }
        }
    }

    internal static class NativeScreen
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        internal struct DisplayMonitor
        {
            public RECT Bounds;
            public RECT WorkingArea;
            public string Device;
            public int ScalePercent;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string device;
        }

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool GetMonitorDevice(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr CreateDisplayDC(string driver, string device, IntPtr port, IntPtr devmode);

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("shcore.dll")]
        private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scalePercent);

        [DllImport("user32.dll")]
        public static extern bool GetPhysicalCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int index);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal static readonly IntPtr PerMonitorV2 = new IntPtr(-4);
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        private const uint GW_HWNDNEXT = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;

        public static List<DisplayMonitor> GetAllMonitors()
        {
            var list = new List<DisplayMonitor>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                bool hasInfo = GetMonitorDevice(hMon, ref info);
                int scalePercent;
                GetScaleFactorForMonitor(hMon, out scalePercent);
                list.Add(new DisplayMonitor
                {
                    Bounds = hasInfo ? info.rcMonitor : rc,
                    WorkingArea = hasInfo ? info.rcWork : rc,
                    Device = hasInfo ? info.device : null,
                    ScalePercent = scalePercent
                });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static List<RECT> GetVisibleTopWindowRects()
        {
            var rects = new List<RECT>();
            var visited = new HashSet<IntPtr>();
            var className = new StringBuilder(256);
            IntPtr shell = GetShellWindow();
            for (IntPtr hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero && visited.Add(hwnd); hwnd = GetWindow(hwnd, GW_HWNDNEXT))
            {
                if (hwnd == shell || !IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
                if ((GetWindowLong(hwnd, GWL_EXSTYLE) & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT)) != 0) continue;

                className.Clear();
                GetClassName(hwnd, className, className.Capacity);
                string name = className.ToString();
                if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") continue;

                int cloaked;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
                RECT r;
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) != 0
                    && !GetWindowRect(hwnd, out r)) continue;
                if (r.Width >= 40 && r.Height >= 40) rects.Add(r);
            }
            return rects;
        }

        public static Bitmap CaptureMonitor(DisplayMonitor monitor)
        {
            var r = monitor.Bounds;
            if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("屏幕范围无效。");

            Bitmap frame = null;
            Exception captureError = null;
            double blank = 1;
            try
            {
                frame = CaptureFromMonitor(monitor);
                if (frame != null) blank = BlankBorderFraction(frame);
                if (frame != null && blank < 0.25) return frame;
            }
            catch (Exception ex) { captureError = ex; }

            Bitmap alternate = null;
            IntPtr desktop = GetDC(IntPtr.Zero);
            if (desktop != IntPtr.Zero)
            {
                try
                {
                    alternate = CaptureFromDC(desktop, r.Left, r.Top, r.Width, r.Height);
                    if (frame == null || BlankBorderFraction(alternate) < blank)
                    {
                        if (frame != null) frame.Dispose();
                        var result = alternate;
                        alternate = null;
                        return result;
                    }
                }
                catch (Exception ex) { captureError = ex; }
                finally
                {
                    if (alternate != null) alternate.Dispose();
                    ReleaseDC(IntPtr.Zero, desktop);
                }
            }

            if (frame != null) return frame;
            throw new InvalidOperationException("无法抓取该屏幕。", captureError);
        }

        private static Bitmap CaptureFromMonitor(DisplayMonitor monitor)
        {
            if (string.IsNullOrEmpty(monitor.Device)) return null;
            IntPtr display = CreateDisplayDC(monitor.Device, null, IntPtr.Zero, IntPtr.Zero);
            if (display == IntPtr.Zero) return null;
            try
            {
                var bounds = monitor.Bounds;
                const int horzRes = 8, vertRes = 10;
                if (GetDeviceCaps(display, horzRes) != bounds.Width || GetDeviceCaps(display, vertRes) != bounds.Height)
                    return null;
                return CaptureFromDC(display, 0, 0, bounds.Width, bounds.Height);
            }
            finally { DeleteDC(display); }
        }

        private static double BlankBorderFraction(Bitmap bitmap)
        {
            var rows = new bool[12];
            var columns = new bool[16];
            var lockRect = new GdiRect(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(lockRect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                int w = bitmap.Width;
                int h = bitmap.Height;
                byte[] rowBuf = new byte[w * 4];
                for (int y = 0; y < rows.Length; y++)
                {
                    int py = (2 * y + 1) * h / (2 * rows.Length);
                    Marshal.Copy(IntPtr.Add(data.Scan0, py * stride), rowBuf, 0, rowBuf.Length);
                    for (int x = 0; x < columns.Length; x++)
                    {
                        int px = (2 * x + 1) * w / (2 * columns.Length);
                        int off = px * 4;
                        if (rowBuf[off] > 3 || rowBuf[off + 1] > 3 || rowBuf[off + 2] > 3)
                            rows[y] = columns[x] = true;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return Math.Max(BlankEdge(rows), BlankEdge(columns));
        }

        private static double BlankEdge(bool[] occupied)
        {
            int first = Array.IndexOf(occupied, true);
            if (first < 0) return 1;
            int last = Array.LastIndexOf(occupied, true);
            return (double)Math.Max(first, occupied.Length - 1 - last) / occupied.Length;
        }

        private static Bitmap CaptureFromDC(IntPtr source, int x, int y, int width, int height)
        {
            IntPtr memory = IntPtr.Zero, hBitmap = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                memory = CreateCompatibleDC(source);
                hBitmap = CreateCompatibleBitmap(source, width, height);
                if (memory == IntPtr.Zero || hBitmap == IntPtr.Zero)
                    throw new InvalidOperationException("无法为屏幕分配截图缓冲区。");

                previous = SelectObject(memory, hBitmap);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                    throw new InvalidOperationException("无法初始化截图缓冲区。");

                if (!BitBlt(memory, 0, 0, width, height, source, x, y, SRCCOPY | CAPTUREBLT)
                    && !BitBlt(memory, 0, 0, width, height, source, x, y, SRCCOPY))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能抓取该屏幕。");

                using (var raw = System.Drawing.Image.FromHbitmap(hBitmap))
                    return new Bitmap(raw);
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memory != IntPtr.Zero) DeleteDC(memory);
            }
        }

        public static BitmapSource ToBitmapSource(Bitmap bmp)
        {
            IntPtr hBmp = bmp.GetHbitmap();
            try
            {
                var bs = Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            finally
            {
                DeleteObject(hBmp);
            }
        }
    }

    internal static class ClipboardHelper
    {
        public static bool TrySetImage(Bitmap bmp, out string error)
        {
            error = "";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using (var pngStream = new MemoryStream())
                    {
                        bmp.Save(pngStream, ImageFormat.Png);
                        pngStream.Position = 0;
                        var data = new WinForms.DataObject();
                        data.SetData(WinForms.DataFormats.Bitmap, true, bmp);
                        data.SetData("PNG", false, pngStream);
                        WinForms.Clipboard.SetDataObject(data, true);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    if (attempt < 4) Thread.Sleep(30);
                }
            }
            return false;
        }

        public static bool TrySetText(string text, out string error)
        {
            error = "";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    WinForms.Clipboard.SetText(text ?? "");
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    if (attempt < 4) Thread.Sleep(30);
                }
            }
            return false;
        }
    }

    public static class ScreenshotSession
    {
        private static bool _active = false;
        private static ScreenshotOverlayWindow _overlay;
        private static Action<string, bool> _notifyCallback;

        public static bool IsActive { get { return _active; } }

        public static void Start(Action<string, bool> onNotify)
        {
            if (_active) return;
            _active = true;
            _notifyCallback = onNotify;

            IntPtr previousContext = IntPtr.Zero;
            try
            {
                previousContext = NativeScreen.SetThreadDpiAwarenessContext(NativeScreen.PerMonitorV2);
                if (previousContext == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用截图所需的每屏 DPI 坐标。");
                var monitors = NativeScreen.GetAllMonitors();
                var winRects = NativeScreen.GetVisibleTopWindowRects();
                if (monitors.Count == 0) throw new InvalidOperationException("未找到可用的显示器。");

                var desktopBounds = new NativeScreen.RECT
                {
                    Left = monitors.Min(m => m.Bounds.Left), Top = monitors.Min(m => m.Bounds.Top),
                    Right = monitors.Max(m => m.Bounds.Right), Bottom = monitors.Max(m => m.Bounds.Bottom)
                };

                // One desktop snapshot and one input window share the same pixel origin.
                Bitmap desktop = CaptureDesktop(desktopBounds, monitors);
                try
                {
                    _overlay = new ScreenshotOverlayWindow(desktopBounds, desktop, monitors, winRects, CloseAll, Notify);
                    desktop = null;
                    _overlay.ShowOverlay();
                }
                finally { if (desktop != null) desktop.Dispose(); }
            }
            catch (Exception ex)
            {
                CloseAll();
                Notify("启动截图失败：" + ex.Message, true);
            }
            finally
            {
                if (previousContext != IntPtr.Zero) NativeScreen.SetThreadDpiAwarenessContext(previousContext);
            }
        }

        private static Bitmap CaptureDesktop(NativeScreen.RECT bounds, List<NativeScreen.DisplayMonitor> monitors)
        {
            if (monitors.Count == 1) return NativeScreen.CaptureMonitor(monitors[0]);

            var desktop = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                using (var g = Graphics.FromImage(desktop))
                {
                    g.Clear(GdiColor.Black);
                    foreach (var monitor in monitors)
                    using (var frame = NativeScreen.CaptureMonitor(monitor))
                    {
                        g.DrawImageUnscaled(frame, monitor.Bounds.Left - bounds.Left, monitor.Bounds.Top - bounds.Top);
                    }
                }
                return desktop;
            }
            catch { desktop.Dispose(); throw; }
        }

        private static void Notify(string msg, bool isError)
        {
            if (_notifyCallback != null)
            {
                try { _notifyCallback(msg, isError); } catch { }
            }
        }

        public static void CloseAll()
        {
            var overlay = _overlay;
            _overlay = null;
            _active = false;
            if (overlay != null) overlay.ForceClose();
        }
    }

    internal enum DrawTool
    {
        None,
        Rect,
        Arrow,
        Brush,
        Mosaic,
        Text,
        Stamp
    }

    internal class AnnotationItem
    {
        public DrawTool Tool;
        public WpfColor Color;
        public double Thickness;
        public WpfPoint Start;
        public WpfPoint End;
        public List<WpfPoint> Points;
        public string Text;
        public double FontSize;
        public int StampNumber;
        public int MosaicBlockSize = 10;
        public UIElement CachedVisual;
    }

    internal sealed class SelectionShade : FrameworkElement
    {
        private readonly DrawingVisual _visual = new DrawingVisual();
        private readonly SolidColorBrush _shade = new SolidColorBrush(WpfColor.FromArgb(0x73, 0x1A, 0x19, 0x17));
        private readonly SolidColorBrush _badgeBg = new SolidColorBrush(WpfColor.FromRgb(0x1A, 0x19, 0x17));
        private readonly System.Windows.Media.Pen _badgeRim = new System.Windows.Media.Pen(WpfBrushes.White, 1.0);
        private static readonly Typeface MonoTypeface = new Typeface(
            new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            FontStyles.Normal,
            FontWeights.Bold,
            FontStretches.Normal);

        internal SelectionShade()
        {
            _shade.Freeze();
            _badgeBg.Freeze();
            _badgeRim.Freeze();
            IsHitTestVisible = false;
            AddVisualChild(_visual);
        }

        protected override int VisualChildrenCount { get { return 1; } }
        protected override Visual GetVisualChild(int index)
        {
            if (index != 0) throw new ArgumentOutOfRangeException("index");
            return _visual;
        }

        internal void Paint(WpfRect monitor, WpfRect opening, System.Windows.Media.Pen outline = null, string sizeLabel = null, double uiScale = 1.0)
        {
            using (var drawing = _visual.RenderOpen())
            {
                if (monitor.IsEmpty) return;

                var cutout = WpfRect.Intersect(monitor, opening);
                if (cutout.IsEmpty || cutout.Width == 0 || cutout.Height == 0)
                {
                    drawing.DrawRectangle(_shade, null, monitor);
                    return;
                }

                drawing.DrawRectangle(_shade, null, new WpfRect(monitor.Left, monitor.Top, monitor.Width, cutout.Top - monitor.Top));
                drawing.DrawRectangle(_shade, null, new WpfRect(monitor.Left, cutout.Bottom, monitor.Width, monitor.Bottom - cutout.Bottom));
                drawing.DrawRectangle(_shade, null, new WpfRect(monitor.Left, cutout.Top, cutout.Left - monitor.Left, cutout.Height));
                drawing.DrawRectangle(_shade, null, new WpfRect(cutout.Right, cutout.Top, monitor.Right - cutout.Right, cutout.Height));
                if (outline != null)
                {
                    double inset = outline.Thickness / 2;
                    if (cutout.Width > outline.Thickness && cutout.Height > outline.Thickness)
                    {
                        var frameRect = cutout;
                        frameRect.Inflate(-inset, -inset);
                        drawing.DrawRectangle(null, outline, frameRect);
                    }
                }

                if (!string.IsNullOrEmpty(sizeLabel))
                {
                    double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                    var ft = new FormattedText(sizeLabel, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        MonoTypeface, 11.0 * uiScale, WpfBrushes.White, dpi);
                    double padX = 7.0 * uiScale;
                    double padY = 3.0 * uiScale;
                    double bw = ft.Width + padX * 2;
                    double bh = ft.Height + padY * 2;
                    double margin = 6.0 * uiScale;
                    double bx = Math.Max(monitor.Left + margin, Math.Min(monitor.Right - bw - margin, cutout.Left));
                    double by = cutout.Top - bh - margin >= monitor.Top + margin ? cutout.Top - bh - margin : cutout.Top + margin;
                    var badgeRect = new WpfRect(bx, by, bw, bh);
                    drawing.DrawRectangle(_badgeBg, _badgeRim, badgeRect);
                    drawing.DrawText(ft, new WpfPoint(bx + padX, by + padY));
                }
            }
        }
    }

    internal sealed class ScreenshotOverlayWindow
    {
        private readonly NativeScreen.RECT _physRect;
        private Bitmap _rawBmp;
        private readonly List<NativeScreen.DisplayMonitor> _monitors;
        private readonly List<NativeScreen.RECT> _windowRects;
        private readonly Action _closeSession;
        private readonly Action<string, bool> _notify;

        // Cached BGRA32 raw pixels for lock-free loupe magnifier
        private byte[] _rawPixels;
        private int _rawStride;
        private int _rawWidth;
        private int _rawHeight;

        private Grid _root;
        private HwndSource _source;
        private Canvas _canvas;
        private SelectionShade _shade;
        private Border _selFrame;
        private Border _hoverFrame;
        private Grid _sizePill;
        private TextBlock _sizeText;
        private Canvas _annotCanvas;
        private Grid _topHintBar;

        // Magnifier loupe
        private Grid _loupeCard;
        private System.Windows.Controls.Image _loupeImg;
        private WriteableBitmap _loupeBitmap;
        private byte[] _loupeBuf;
        private Border _loupeColorDot;
        private TextBlock _loupeInfoText;
        private bool _loupeHexMode = true;
        private string _lastColorText = "#000000";

        // Floating toolbar & color sub-bar
        private Grid _toolbar;
        private Grid _subBar;
        private readonly Dictionary<DrawTool, Button> _toolButtons = new Dictionary<DrawTool, Button>();
        private readonly List<Border> _sizeDots = new List<Border>();
        private readonly List<Border> _colorFrames = new List<Border>();
        private readonly List<Border> _colorDots = new List<Border>();
        private TextBlock _strokeHud;
        private Grid _strokeHudWrap;
        private DispatcherTimer _strokeHudTimer;
        private DrawingBrush _screentoneBrush;

        // 8 resize handles
        private readonly Border[] _handles = new Border[8];

        // State
        private bool _hasSelection = false;
        private WpfRect _selRect;
        private WpfRect _selectionMonitor;
        private double _selectionUiScale = 1;
        private WpfRect _hoverMonitor = WpfRect.Empty;
        private WpfRect? _hoverWinRect = null;
        private bool _isDraggingSelection = false;
        private bool _isMovingSelection = false;
        private int _activeHandle = -1; // 0..7
        private WpfPoint _dragAnchor;
        private WpfRect _dragOrigRect;

        private DrawTool _activeTool = DrawTool.None;
        private WpfColor _activeColor = WpfColor.FromRgb(0xC2, 0x48, 0x40);
        private double _activeThickness = 1.5;
        private int _mosaicBlockSize = 10;

        private readonly List<AnnotationItem> _annotations = new List<AnnotationItem>();
        private readonly List<AnnotationItem> _redoStack = new List<AnnotationItem>();
        private AnnotationItem _currentAnnot = null;
        private AnnotationItem _selectedAnnot = null;
        private AnnotationItem _editingAnnot = null;
        private WpfRectangle _annotSelBox = null;
        private bool _isMovingAnnot = false;
        private WpfPoint _annotMoveAnchor;
        private WpfPoint _annotOrigStart;
        private WpfPoint _annotOrigEnd;
        private List<WpfPoint> _annotOrigPoints;
        private TextBox _activeTextBox = null;
        private WpfPoint _activeTextPos;

        private bool _closing = false;
        private bool _displayChanging;
        private bool _pointerFrameQueued;
        private WpfPoint _latestPointer;
        private System.Windows.Media.Pen _dragPen;
        private bool _outlineActive;

        private static readonly WpfColor[] PaletteColors = new[]
        {
            WpfColor.FromRgb(0xC2, 0x48, 0x40), // 1. 砖红 (Default)
            WpfColor.FromRgb(0xE3, 0x8D, 0x17), // 2. 琥珀黄
            WpfColor.FromRgb(0x25, 0x63, 0xEB)  // 3. 建筑蓝
        };

        private static readonly double[] StrokePresets = new[] { 1.5, 2.5, 4.5 };

        public ScreenshotOverlayWindow(
            NativeScreen.RECT physRect,
            Bitmap rawBmp,
            List<NativeScreen.DisplayMonitor> monitors,
            List<NativeScreen.RECT> windowRects,
            Action closeSession,
            Action<string, bool> notify)
        {
            _physRect = physRect;
            _rawBmp = rawBmp;
            _monitors = monitors;
            _windowRects = windowRects;
            _closeSession = closeSession;
            _notify = notify;

            CacheRawPixels();
            _screentoneBrush = BuildScreentoneBrush();
            BuildVisualTree();

            _root.SizeChanged += (s, e) =>
            {
                _shade.Width = e.NewSize.Width;
                _shade.Height = e.NewSize.Height;
                if (!_hasSelection && !_isDraggingSelection) PreviewCursorWindow();
                UpdateDimLayout();
            };
            _root.PreviewKeyDown += OnPreviewKeyDown;
            _root.PreviewKeyUp += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                    _loupeCard.Visibility = Visibility.Collapsed;
            };
            _root.PreviewMouseWheel += OnPreviewMouseWheel;
        }

        private void CacheRawPixels()
        {
            if (_rawBmp == null) return;
            _rawWidth = _rawBmp.Width;
            _rawHeight = _rawBmp.Height;
            _rawStride = _rawWidth * 4;
            _rawPixels = new byte[_rawStride * _rawHeight];
            var rect = new GdiRect(0, 0, _rawWidth, _rawHeight);
            BitmapData data = _rawBmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                if (data.Stride == _rawStride)
                {
                    Marshal.Copy(data.Scan0, _rawPixels, 0, _rawPixels.Length);
                }
                else
                {
                    for (int y = 0; y < _rawHeight; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _rawPixels, y * _rawStride, _rawStride);
                }
            }
            finally
            {
                _rawBmp.UnlockBits(data);
            }
        }

        private static DrawingBrush BuildScreentoneBrush()
        {
            var group = new DrawingGroup();
            var bg = new GeometryDrawing(
                new SolidColorBrush(WpfColor.FromRgb(0xFA, 0xF8, 0xF5)),
                null,
                new RectangleGeometry(new WpfRect(0, 0, 4, 4)));
            var dotsGeom = new GeometryGroup();
            dotsGeom.Children.Add(new RectangleGeometry(new WpfRect(0, 0, 1, 1)));
            dotsGeom.Children.Add(new RectangleGeometry(new WpfRect(2, 2, 1, 1)));
            var dots = new GeometryDrawing(
                new SolidColorBrush(WpfColor.FromRgb(0xD2, 0xCF, 0xC7)),
                null,
                dotsGeom);
            group.Children.Add(bg);
            group.Children.Add(dots);
            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new WpfRect(0, 0, 4, 4),
                ViewportUnits = BrushMappingMode.Absolute
            };
            brush.Freeze();
            return brush;
        }

        private static Grid WrapWithHardShadow(Border card, double offset = 3.0)
        {
            var wrap = new Grid { Visibility = Visibility.Collapsed };
            var shadow = new WpfRectangle
            {
                Fill = UiHelpers.BorderStrong,
                Margin = new Thickness(offset, offset, 0, 0),
                IsHitTestVisible = false
            };
            card.Margin = new Thickness(0, 0, offset, offset);
            wrap.Children.Add(shadow);
            wrap.Children.Add(card);
            return wrap;
        }

        internal void ShowOverlay()
        {
            const int wsPopup = unchecked((int)0x80000000);
            const int wsExTopmost = 0x00000008, wsExToolWindow = 0x00000080;
            var placement = new HwndSourceParameters("AI BASE 截图", _physRect.Width, _physRect.Height)
            {
                PositionX = _physRect.Left,
                PositionY = _physRect.Top,
                WindowStyle = wsPopup,
                ExtendedWindowStyle = wsExTopmost | wsExToolWindow,
                TreatAsInputRoot = true,
                HwndSourceHook = OverlayMessage
            };
            _source = new HwndSource(placement);
            _source.Disposed += (s, e) => { if (!_closing) _closeSession(); };
            _source.RootVisual = _root;
            var hwnd = _source.Handle;
            if (!NativeScreen.SetWindowPos(hwnd, NativeScreen.HWND_TOPMOST, _physRect.Left, _physRect.Top,
                _physRect.Width, _physRect.Height, NativeScreen.SWP_NOACTIVATE | NativeScreen.SWP_SHOWWINDOW))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位截图遮罩。");

            _root.UpdateLayout();
            NativeScreen.RECT client;
            var origin = new NativeScreen.POINT();
            if (!NativeScreen.GetClientRect(hwnd, out client) || !NativeScreen.ClientToScreen(hwnd, ref origin)
                || origin.X != _physRect.Left || origin.Y != _physRect.Top
                || client.Width != _physRect.Width || client.Height != _physRect.Height
                || Math.Abs(_root.ActualWidth * ScaleX - client.Width) > 1
                || Math.Abs(_root.ActualHeight * ScaleY - client.Height) > 1)
                throw new InvalidOperationException("截图遮罩的像素位置与桌面不一致。");

            _source.DpiChanged += (s, e) => EndForDisplayChange();
            PreviewCursorWindow();
            NativeScreen.SetForegroundWindow(hwnd);
            Keyboard.Focus(_root);
        }

        private IntPtr OverlayMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int wmDisplayChange = 0x007E;
            if (message == wmDisplayChange) EndForDisplayChange();
            return IntPtr.Zero;
        }

        private void EndForDisplayChange()
        {
            if (_closing || _displayChanging) return;
            _displayChanging = true;
            _root.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing) return;
                _closeSession();
                _notify("显示设置已变化，请重新开始截图。", true);
            }));
        }

        private void PreviewCursorWindow()
        {
            NativeScreen.POINT cursor;
            if (_source == null || !NativeScreen.GetPhysicalCursorPos(out cursor)
                || cursor.X < _physRect.Left || cursor.X >= _physRect.Right
                || cursor.Y < _physRect.Top || cursor.Y >= _physRect.Bottom || _root.ActualWidth <= 0 || _root.ActualHeight <= 0)
                return;

            _latestPointer = ToCanvasPoint(cursor);
            UpdateHoverWindow(_latestPointer);
        }

        public void ForceClose()
        {
            if (_closing) return;
            _closing = true;
            CancelPointerFrame();
            if (_strokeHudTimer != null) { _strokeHudTimer.Stop(); _strokeHudTimer = null; }
            var source = _source;
            _source = null;
            try
            {
                if (source != null && !source.IsDisposed) source.Dispose();
            }
            finally
            {
                _rawPixels = null;
                if (_rawBmp != null) { _rawBmp.Dispose(); _rawBmp = null; }
            }
        }

        private double ScaleX { get { return _source.CompositionTarget.TransformToDevice.M11; } }
        private double ScaleY { get { return _source.CompositionTarget.TransformToDevice.M22; } }

        private WpfPoint ToCanvasPoint(NativeScreen.POINT cursor)
        {
            return new WpfPoint((cursor.X - _physRect.Left) / ScaleX, (cursor.Y - _physRect.Top) / ScaleY);
        }

        private WpfRect ToCanvasRect(NativeScreen.RECT rect)
        {
            return new WpfRect((rect.Left - _physRect.Left) / ScaleX, (rect.Top - _physRect.Top) / ScaleY,
                rect.Width / ScaleX, rect.Height / ScaleY);
        }

        private WpfRect MonitorAt(WpfPoint point, out double uiScale)
        {
            double deviceScale = ScaleX;
            double x = _physRect.Left + Math.Round(point.X * deviceScale);
            double y = _physRect.Top + Math.Round(point.Y * ScaleY);
            uiScale = 1;
            foreach (var monitor in _monitors)
            {
                var bounds = monitor.Bounds;
                if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom) continue;
                // Selection geometry stays in the desktop host's coordinate space.
                if (monitor.ScalePercent > 0) uiScale = monitor.ScalePercent / (100.0 * deviceScale);
                return ToCanvasRect(bounds);
            }
            return WpfRect.Empty;
        }

        private void ScaleSelectionControls(double scale)
        {
            if (_selectionUiScale == scale) return;
            _selectionUiScale = scale;
            var transform = new ScaleTransform(scale, scale);
            transform.Freeze();
            _toolbar.LayoutTransform = _subBar.LayoutTransform = _sizePill.LayoutTransform = _strokeHudWrap.LayoutTransform = transform;
            foreach (var handle in _handles) handle.LayoutTransform = transform;
            _selFrame.BorderThickness = new Thickness(1.5 * scale);
        }

        private WpfPoint PointerPosition(MouseEventArgs e)
        {
            NativeScreen.POINT cursor;
            if (NativeScreen.GetPhysicalCursorPos(out cursor)) return ToCanvasPoint(cursor);
            return e == null ? Mouse.GetPosition(_canvas) : e.GetPosition(_canvas);
        }

        private GdiRect ToBitmapRect(WpfRect rect)
        {
            double scaleX = ScaleX, scaleY = ScaleY;
            int left = Math.Max(0, Math.Min(_physRect.Width, (int)Math.Round(rect.Left * scaleX)));
            int top = Math.Max(0, Math.Min(_physRect.Height, (int)Math.Round(rect.Top * scaleY)));
            int right = Math.Max(left, Math.Min(_physRect.Width, (int)Math.Round(rect.Right * scaleX)));
            int bottom = Math.Max(top, Math.Min(_physRect.Height, (int)Math.Round(rect.Bottom * scaleY)));
            return GdiRect.FromLTRB(left, top, right, bottom);
        }

        private void BuildVisualTree()
        {
            var root = new Grid
            {
                Background = WpfBrushes.Black,
                Cursor = Cursors.Cross,
                Focusable = true,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            TextElement.SetFontFamily(root, UiHelpers.MainFont);
            _root = root;

            var desktop = new System.Windows.Controls.Image
            {
                Source = NativeScreen.ToBitmapSource(_rawBmp),
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(desktop, BitmapScalingMode.NearestNeighbor);
            root.Children.Add(desktop);

            // 2. Interactive canvas
            _canvas = new Canvas { Background = WpfBrushes.Transparent, ClipToBounds = true };
            _canvas.MouseLeftButtonDown += OnCanvasMouseDown;
            _canvas.MouseMove += OnCanvasMouseMove;
            _canvas.MouseLeave += (s, e) =>
            {
                if (_hasSelection || _isDraggingSelection) return;
                CancelPointerFrame();
                _hoverWinRect = null;
                _hoverMonitor = WpfRect.Empty;
                _hoverFrame.Visibility = Visibility.Collapsed;
                _loupeCard.Visibility = Visibility.Collapsed;
                UpdateDimLayout();
            };
            _canvas.MouseLeftButtonUp += OnCanvasMouseUp;
            _canvas.LostMouseCapture += OnCanvasLostMouseCapture;
            _canvas.MouseRightButtonUp += OnCanvasRightUp;
            root.Children.Add(_canvas);

            // 3. Annotation preview layer (clipped to selection during render)
            _annotCanvas = new Canvas { IsHitTestVisible = false };
            _canvas.Children.Add(_annotCanvas);

            // 4. The shade and moving outline share one visual.
            _shade = new SelectionShade();
            _canvas.Children.Add(_shade);

            // 5. Smart window hover highlight frame (0px sharp warm-ink)
            _hoverFrame = new Border
            {
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(2.0),
                Background = new SolidColorBrush(WpfColor.FromArgb(0x10, 0x1A, 0x19, 0x17)),
                CornerRadius = new CornerRadius(0),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_hoverFrame);

            // 6. Active selection border (0px sharp warm-ink + 1px white inner contrast rim)
            _selFrame = new Border
            {
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(0),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Child = new Border
                {
                    BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1.0),
                    CornerRadius = new CornerRadius(0)
                }
            };
            _canvas.Children.Add(_selFrame);

            // 6b. Selected annotation highlight outline (outside _annotCanvas so it never bakes into image)
            _annotSelBox = new WpfRectangle
            {
                Stroke = UiHelpers.BorderStrong,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = new SolidColorBrush(WpfColor.FromArgb(0x0E, 0x1A, 0x19, 0x17)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_annotSelBox);

            // 7. 8 resize handles (0px 8x8 white squares + 1.5px warm-ink border)
            var handleCursors = new[]
            {
                Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
                Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE
            };
            for (int i = 0; i < 8; i++)
            {
                int idx = i;
                var h = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(0),
                    Background = WpfBrushes.White,
                    BorderBrush = UiHelpers.BorderStrong,
                    BorderThickness = new Thickness(1.5),
                    Cursor = handleCursors[i],
                    Visibility = Visibility.Collapsed
                };
                h.MouseLeftButtonDown += (s, e) =>
                {
                    if (_activeTool != DrawTool.None) return;
                    CommitActiveTextBox();
                    if (!_canvas.CaptureMouse()) return;
                    _activeHandle = idx;
                    _dragAnchor = PointerPosition(e);
                    _dragOrigRect = _selRect;
                    BeginOutline();
                    e.Handled = true;
                };
                _handles[i] = h;
                _canvas.Children.Add(h);
            }

            // 8. Selection size pill (0px #1A1917 + 1px white rim + 11px mono)
            _sizeText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = WpfBrushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI")
            };
            var sizeBorder = new Border
            {
                Background = UiHelpers.PrimaryBg,
                BorderBrush = WpfBrushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(7, 3, 7, 3),
                Child = _sizeText
            };
            _sizePill = WrapWithHardShadow(sizeBorder, 2);
            _sizePill.IsHitTestVisible = false;
            _canvas.Children.Add(_sizePill);

            // 8b. Top-center compact hotkey hint strip (0px, fixed physical readability)
            var hintBorder = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(0xEE, 0x1A, 0x19, 0x17)),
                BorderBrush = UiHelpers.TextSecondary,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock
                {
                    Text = "拖动取景 · 单击识窗 · 单击标注可选中/按 Delete 删除 · 双击序号可写字 · 右键返回",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0xFA, 0xF8, 0xF5))
                }
            };
            _topHintBar = WrapWithHardShadow(hintBorder, 2);
            _topHintBar.IsHitTestVisible = false;
            _canvas.Children.Add(_topHintBar);

            // 9. Pixel Magnifier Loupe
            BuildLoupe();
            _canvas.Children.Add(_loupeCard);

            // 10. Stroke / Mosaic / FontSize wheel feedback HUD (0px)
            _strokeHud = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = WpfBrushes.White };
            var hudBorder = new Border
            {
                Background = UiHelpers.PrimaryBg,
                BorderBrush = WpfBrushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(8, 3, 8, 3),
                Child = _strokeHud
            };
            _strokeHudWrap = WrapWithHardShadow(hudBorder, 2);
            _strokeHudWrap.IsHitTestVisible = false;
            _canvas.Children.Add(_strokeHudWrap);

            // 11. Contextual Floating Toolbar + Color/Weight Sub-bar
            BuildToolbar();
            _canvas.Children.Add(_toolbar);
            _canvas.Children.Add(_subBar);
        }

        private void BuildLoupe()
        {
            _loupeBitmap = new WriteableBitmap(11, 11, 96, 96, PixelFormats.Bgra32, null);
            _loupeBuf = new byte[11 * 11 * 4];
            _loupeImg = new System.Windows.Controls.Image { Source = _loupeBitmap, Width = 121, Height = 121, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_loupeImg, BitmapScalingMode.NearestNeighbor);

            var imgGrid = new Grid { Width = 121, Height = 121, ClipToBounds = true };
            imgGrid.Children.Add(_loupeImg);

            // Crosshair lines (Warm Ink #1A1917)
            var crossBrush = new SolidColorBrush(WpfColor.FromArgb(0xCC, 0x1A, 0x19, 0x17));
            imgGrid.Children.Add(new WpfRectangle { Height = 1, Fill = crossBrush, VerticalAlignment = VerticalAlignment.Center });
            imgGrid.Children.Add(new WpfRectangle { Width = 1, Fill = crossBrush, HorizontalAlignment = HorizontalAlignment.Center });
            imgGrid.Children.Add(new Border
            {
                Width = 11,
                Height = 11,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            _loupeColorDot = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(0),
                BorderBrush = WpfBrushes.White,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 0)
            };
            _loupeInfoText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = WpfBrushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI")
            };

            var footer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(7, 6, 7, 6) };
            var row1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row1.Children.Add(_loupeColorDot);
            row1.Children.Add(_loupeInfoText);
            footer.Children.Add(row1);
            footer.Children.Add(new TextBlock
            {
                Text = "C 复制色值 · Shift 切格式",
                FontSize = 11,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0xBD, 0xB9, 0xB0)),
                Margin = new Thickness(0, 3, 0, 0)
            });

            var sp = new StackPanel();
            sp.Children.Add(imgGrid);
            sp.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(WpfColor.FromRgb(0x3B, 0x38, 0x33)) });
            sp.Children.Add(footer);

            var loupeBorder = new Border
            {
                Background = UiHelpers.PrimaryBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(0),
                ClipToBounds = true,
                Child = sp
            };
            _loupeCard = WrapWithHardShadow(loupeBorder, 3);
            _loupeCard.IsHitTestVisible = false;
        }

        private void BuildToolbar()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // Group 1: 5 Annotation tools (100% sharp 0px miter geometry, merged 1/T stamp+text)
            AddToolBtn(bar, DrawTool.Rect, "M3 3 H21 V21 H3 Z", "矩形框 (1 / R · Shift 正方)");
            AddToolBtn(bar, DrawTool.Arrow, "M5 19 L19 5 M19 5 H10 M19 5 V14", "箭头 (2 / A · Shift 正交)");
            AddToolBtn(bar, DrawTool.Brush, "M16 4 L20 8 L8 20 H4 V16 Z M13 7 L17 11", "画笔 (3 / P)");
            AddToolBtn(bar, DrawTool.Mosaic, "M4 4h5v5H4Z M10 10h4v4h-4Z M15 4h5v5h-5Z M4 15h5v5H4Z M15 15h5v5h-5Z", "马赛克打码 (4 / M · 滚轮调粒度)");
            AddToolBtn(bar, DrawTool.Stamp, "M3 4 H13 V14 H3 Z M7.5 6.8 V11.2 M14 11 H21 M17.5 11 V20", "序号与文字标签 (5 / T / N · 单击落序号，双击序号可写字)");

            bar.Children.Add(MakeBarSep());

            // Group 2: 3 Output / Utility actions (unified Icon + 2-char label)
            bar.Children.Add(MakeActionBtn("M4 8 V4 H8 M16 4 H20 V8 M20 16 V20 H16 M8 20 H4 V16 M8 9 H16 M8 12.5 H14 M8 16 H12", "提取图中文字 (OCR)", "提字", false, false, RunOcr));
            bar.Children.Add(MakeActionBtn("M7 4 H17 V7 L15 10 V14 H18 V16 H6 V14 H9 V10 L7 7 Z M12 16 V21", "钉在桌面置顶对比 (Ctrl+T)", "贴图", false, false, PinToDesktop));
            bar.Children.Add(MakeActionBtn("M12 3 V15 M7 10 L12 15 L17 10 M4 19 H20", "另存为 PNG 文件 (Ctrl+S)", "保存", false, false, SaveToFile));

            bar.Children.Add(MakeBarSep());

            // Group 3: 2 Finish actions (unified Icon + 2-char label, equal width)
            bar.Children.Add(MakeActionBtn("M6 6 L18 18 M18 6 L6 18", "取消截图 (Esc)", "取消", false, true, () => _closeSession()));
            bar.Children.Add(MakeActionBtn("M4 12 L9 17 L20 6", "复制到剪贴板 (Enter / 双击)", "复制", true, false, CopyAndFinish));

            var toolbarBorder = new Border
            {
                Background = UiHelpers.MainBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(5, 4, 5, 4),
                Child = bar,
                Cursor = Cursors.Arrow
            };
            _toolbar = WrapWithHardShadow(toolbarBorder, 3);

            // Color & Stroke sub-bar (0px architectural)
            var subSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // 3 stroke size squares (4x4 / 7x7 / 10x10)
            for (int i = 0; i < StrokePresets.Length; i++)
            {
                double sz = StrokePresets[i];
                double sqVisual = 4 + i * 3;
                var inner = new WpfRectangle
                {
                    Width = sqVisual,
                    Height = sqVisual,
                    Fill = UiHelpers.TextPrimary
                };
                var wrap = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = WpfBrushes.Transparent,
                    Background = WpfBrushes.Transparent,
                    Child = inner,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(1, 0, 1, 0),
                    ToolTip = i == 0 ? "细线条（滚轮也可调粗细）" : (i == 1 ? "中线条（滚轮也可调粗细）" : "粗线条（滚轮也可调粗细）")
                };
                wrap.MouseLeftButtonDown += (s, e) =>
                {
                    _activeThickness = sz;
                    if (_selectedAnnot != null)
                    {
                        _selectedAnnot.Thickness = sz;
                        RefreshAnnotationVisual(_selectedAnnot);
                    }
                    SyncSubBarVisuals();
                    e.Handled = true;
                };
                _sizeDots.Add(wrap);
                subSp.Children.Add(wrap);
            }

            subSp.Children.Add(MakeBarSep());

            // 6 fixed-size color swatches (18x18 inside 22x22 frame — zero layout jump!)
            for (int i = 0; i < PaletteColors.Length; i++)
            {
                WpfColor c = PaletteColors[i];
                var innerDot = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(0),
                    Background = new SolidColorBrush(c),
                    BorderBrush = c == WpfColors.White ? new SolidColorBrush(WpfColor.FromRgb(0xD2, 0xCF, 0xC7)) : WpfBrushes.Transparent,
                    BorderThickness = new Thickness(1)
                };
                var frame = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(0),
                    Background = WpfBrushes.White,
                    BorderBrush = WpfBrushes.Transparent,
                    BorderThickness = new Thickness(1.5),
                    Padding = new Thickness(1),
                    Margin = new Thickness(2, 0, 2, 0),
                    Child = innerDot,
                    Cursor = Cursors.Hand
                };
                frame.MouseLeftButtonDown += (s, e) =>
                {
                    _activeColor = c;
                    if (_activeTextBox != null) _activeTextBox.Foreground = new SolidColorBrush(c);
                    if (_selectedAnnot != null)
                    {
                        _selectedAnnot.Color = c;
                        RefreshAnnotationVisual(_selectedAnnot);
                    }
                    SyncSubBarVisuals();
                    e.Handled = true;
                };
                _colorFrames.Add(frame);
                _colorDots.Add(innerDot);
                subSp.Children.Add(frame);
            }

            var subBorder = new Border
            {
                Background = UiHelpers.MainBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(7, 4, 7, 4),
                Child = subSp,
                Cursor = Cursors.Arrow
            };
            _subBar = WrapWithHardShadow(subBorder, 3);

            SyncSubBarVisuals();
        }

        private Border MakeBarSep()
        {
            return new Border
            {
                Width = 1,
                Height = 18,
                Background = new SolidColorBrush(WpfColor.FromRgb(0xD2, 0xCF, 0xC7)),
                Margin = new Thickness(5, 0, 5, 0)
            };
        }

        private System.Windows.Shapes.Path MakeSvgIcon(string pathData, System.Windows.Media.Brush stroke)
        {
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(pathData),
                Stroke = stroke,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Square,
                StrokeEndLineCap = PenLineCap.Square,
                StrokeLineJoin = PenLineJoin.Miter,
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform
            };
        }

        private void AddToolBtn(StackPanel bar, DrawTool tool, string svgData, string tip)
        {
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(1, 0, 1, 0),
                Background = WpfBrushes.Transparent,
                BorderBrush = WpfBrushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                FocusVisualStyle = null,
                Content = MakeSvgIcon(svgData, UiHelpers.TextSecondary)
            };
            ApplyFlatButtonTemplate(btn, false, false);
            btn.Click += (s, e) => SelectTool(_activeTool == tool ? DrawTool.None : tool);
            _toolButtons[tool] = btn;
            bar.Children.Add(btn);
        }

        private Button MakeActionBtn(string svgData, string tip, string label, bool primary, bool dangerHover, Action onClick)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var fg = primary ? WpfBrushes.White : UiHelpers.TextPrimary;
            var icon = MakeSvgIcon(svgData, fg);
            if (!primary)
            {
                icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });
            }
            sp.Children.Add(icon);
            if (!string.IsNullOrEmpty(label))
            {
                var tb = new TextBlock
                {
                    Text = label,
                    FontSize = 12,
                    FontWeight = primary ? FontWeights.Bold : FontWeights.SemiBold,
                    Foreground = fg,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (!primary)
                {
                    tb.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
                    {
                        RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
                    });
                }
                sp.Children.Add(tb);
            }

            var btn = new Button
            {
                Height = 28,
                MinWidth = string.IsNullOrEmpty(label) ? 28 : ((primary || dangerHover) ? 60 : 58),
                Padding = string.IsNullOrEmpty(label) ? new Thickness(0) : new Thickness(8, 0, 8, 0),
                Margin = new Thickness(1.5, 0, 1.5, 0),
                Foreground = fg,
                Background = primary ? UiHelpers.PrimaryBg : WpfBrushes.Transparent,
                BorderBrush = primary ? UiHelpers.BorderStrong : (dangerHover ? new SolidColorBrush(WpfColor.FromRgb(0xD2, 0xCF, 0xC7)) : WpfBrushes.Transparent),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                FocusVisualStyle = null,
                Content = sp
            };
            ApplyFlatButtonTemplate(btn, primary, dangerHover);
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private void ApplyFlatButtonTemplate(Button btn, bool primary, bool dangerHover)
        {
            var tmpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tmpl.VisualTree = bd;

            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            if (primary)
            {
                hov.Setters.Add(new Setter(Border.BackgroundProperty, UiHelpers.PrimaryHover, "bd"));
            }
            else if (dangerHover)
            {
                hov.Setters.Add(new Setter(Border.BackgroundProperty, UiHelpers.DangerBg, "bd"));
                hov.Setters.Add(new Setter(Border.BorderBrushProperty, UiHelpers.BorderStrong, "bd"));
                hov.Setters.Add(new Setter(Control.ForegroundProperty, WpfBrushes.White));
            }
            else
            {
                hov.Setters.Add(new Setter(Border.BackgroundProperty, UiHelpers.HoverBg, "bd"));
            }
            tmpl.Triggers.Add(hov);
            btn.Template = tmpl;
        }

        private void SelectTool(DrawTool tool)
        {
            CommitActiveTextBox();
            _activeTool = tool;
            foreach (var kv in _toolButtons)
            {
                bool active = kv.Key == tool;
                kv.Value.Background = active ? (System.Windows.Media.Brush)_screentoneBrush : WpfBrushes.Transparent;
                kv.Value.BorderBrush = active ? UiHelpers.BorderStrong : WpfBrushes.Transparent;
                var path = kv.Value.Content as System.Windows.Shapes.Path;
                if (path != null)
                {
                    path.Stroke = active ? UiHelpers.TextPrimary : UiHelpers.TextSecondary;
                }
            }
            _root.Cursor = tool == DrawTool.None ? Cursors.Arrow : (tool == DrawTool.Text ? Cursors.IBeam : Cursors.Cross);
            UpdateToolbarPositions();
        }

        private void SyncSubBarVisuals()
        {
            for (int i = 0; i < _sizeDots.Count; i++)
            {
                bool match = Math.Abs(StrokePresets[i] - _activeThickness) < 0.45;
                _sizeDots[i].Background = match ? (System.Windows.Media.Brush)_screentoneBrush : WpfBrushes.Transparent;
                _sizeDots[i].BorderBrush = match ? UiHelpers.BorderStrong : WpfBrushes.Transparent;
            }
            for (int i = 0; i < _colorFrames.Count; i++)
            {
                bool match = PaletteColors[i] == _activeColor;
                _colorFrames[i].BorderBrush = match ? UiHelpers.BorderStrong : WpfBrushes.Transparent;
            }
        }

        // ==================== MOUSE & KEYBOARD INTERACTION ====================

        private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsMouseOverUi(e.OriginalSource as DependencyObject)) return;

            WpfPoint pt = PointerPosition(e);
            CommitActiveTextBox();

            if (!_hasSelection)
            {
                double uiScale;
                _selectionMonitor = MonitorAt(pt, out uiScale);
                if (_selectionMonitor.IsEmpty || !_canvas.CaptureMouse()) return;
                ScaleSelectionControls(uiScale);
                _isDraggingSelection = true;
                _dragAnchor = pt;
                _selRect = new WpfRect(pt, pt);
                BeginOutline();
                return;
            }

            // Check if clicking on an existing annotation
            AnnotationItem hit = FindAnnotationAt(pt);
            if (hit != null)
            {
                SelectAnnotation(hit);
                if (e.ClickCount == 2 && (hit.Tool == DrawTool.Stamp || hit.Tool == DrawTool.Text))
                {
                    SpawnInlineTextBoxForAnnotation(hit);
                    e.Handled = true;
                    return;
                }
                if (_canvas.CaptureMouse())
                {
                    _isMovingAnnot = true;
                    _annotMoveAnchor = pt;
                    _annotOrigStart = hit.Start;
                    _annotOrigEnd = hit.End;
                    _annotOrigPoints = hit.Points != null ? new List<WpfPoint>(hit.Points) : null;
                }
                e.Handled = true;
                return;
            }

            // Clicked empty canvas inside selection => clear selected annotation
            SelectAnnotation(null);

            // Double-click inside selection with no active tool => Copy & Finish!
            if (_activeTool == DrawTool.None && e.ClickCount == 2 && _selRect.Contains(pt))
            {
                CopyAndFinish();
                return;
            }

            if (_activeTool == DrawTool.None)
            {
                if (_selRect.Contains(pt) && _canvas.CaptureMouse())
                {
                    _isMovingSelection = true;
                    _dragAnchor = pt;
                    _dragOrigRect = _selRect;
                    BeginOutline();
                }
                return;
            }

            // Drawing inside selection
            WpfPoint clamped = ClampToSelection(pt);
            if (_activeTool == DrawTool.Text)
            {
                SpawnInlineTextBox(clamped);
                return;
            }

            if (_activeTool == DrawTool.Stamp)
            {
                int nextNum = _annotations.Count(a => a.Tool == DrawTool.Stamp) + 1;
                var stamp = new AnnotationItem
                {
                    Tool = DrawTool.Stamp,
                    Color = _activeColor,
                    Thickness = _activeThickness,
                    Start = clamped,
                    StampNumber = nextNum
                };
                _redoStack.Clear();
                _annotations.Add(stamp);
                AddAnnotationVisual(stamp);
                SelectAnnotation(stamp);
                return;
            }

            _currentAnnot = new AnnotationItem
            {
                Tool = _activeTool,
                Color = _activeColor,
                Thickness = _activeThickness,
                MosaicBlockSize = _mosaicBlockSize,
                Start = clamped,
                End = clamped,
                Points = _activeTool == DrawTool.Brush ? new List<WpfPoint> { clamped } : null
            };
            _redoStack.Clear();
            _annotations.Add(_currentAnnot);
            _canvas.CaptureMouse();
            UpdateCurrentAnnotationVisual();
        }

        private void BeginOutline()
        {
            _outlineActive = true;
            CancelPointerFrame();
            _hoverWinRect = null;
            _hoverFrame.Visibility = Visibility.Collapsed;
            _loupeCard.Visibility = Visibility.Collapsed;
            if (_topHintBar != null) _topHintBar.Visibility = Visibility.Collapsed;
            if (_annotSelBox != null) _annotSelBox.Visibility = Visibility.Collapsed;
            _selFrame.Visibility = _sizePill.Visibility = _toolbar.Visibility = _subBar.Visibility = Visibility.Collapsed;
            foreach (var handle in _handles) handle.Visibility = Visibility.Collapsed;

            _dragPen = new System.Windows.Media.Pen(_selFrame.BorderBrush, _selFrame.BorderThickness.Left);
            _dragPen.Freeze();
            GdiRect pixels = ToBitmapRect(_selRect);
            string sizeLabel = (_selRect.Width >= 2 && _selRect.Height >= 2) ? string.Format("{0} × {1}", pixels.Width, pixels.Height) : null;
            _shade.Paint(_selectionMonitor, _selRect, _dragPen, sizeLabel, _selectionUiScale);
        }

        private void UpdateDragGeometry(WpfPoint point)
        {
            if (_isDraggingSelection)
            {
                var clamped = new WpfPoint(
                    Math.Max(_selectionMonitor.Left, Math.Min(_selectionMonitor.Right, point.X)),
                    Math.Max(_selectionMonitor.Top, Math.Min(_selectionMonitor.Bottom, point.Y)));
                _selRect = MakeRect(_dragAnchor, clamped);
            }
            else if (_activeHandle >= 0) ResizeSelectionByHandle(_activeHandle, point);
            else if (_isMovingSelection)
            {
                double x = Math.Max(_selectionMonitor.Left, Math.Min(_selectionMonitor.Right - _dragOrigRect.Width, _dragOrigRect.X + point.X - _dragAnchor.X));
                double y = Math.Max(_selectionMonitor.Top, Math.Min(_selectionMonitor.Bottom - _dragOrigRect.Height, _dragOrigRect.Y + point.Y - _dragAnchor.Y));
                _selRect = new WpfRect(x, y, _dragOrigRect.Width, _dragOrigRect.Height);
            }
        }

        private void TrackAnnotation(WpfPoint point)
        {
            WpfPoint clamped = ClampToSelection(point);
            if (_currentAnnot.Tool == DrawTool.Brush)
            {
                if ((_currentAnnot.Points.Last() - clamped).Length > 1)
                    _currentAnnot.Points.Add(clamped);
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                double dx = clamped.X - _currentAnnot.Start.X;
                double dy = clamped.Y - _currentAnnot.Start.Y;
                if (_currentAnnot.Tool == DrawTool.Rect)
                {
                    double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    double sx = _currentAnnot.Start.X + (dx >= 0 ? side : -side);
                    double sy = _currentAnnot.Start.Y + (dy >= 0 ? side : -side);
                    clamped = ClampToSelection(new WpfPoint(sx, sy));
                    double actualSide = Math.Min(Math.Abs(clamped.X - _currentAnnot.Start.X), Math.Abs(clamped.Y - _currentAnnot.Start.Y));
                    clamped = new WpfPoint(
                        _currentAnnot.Start.X + (dx >= 0 ? actualSide : -actualSide),
                        _currentAnnot.Start.Y + (dy >= 0 ? actualSide : -actualSide));
                }
                else if (_currentAnnot.Tool == DrawTool.Arrow)
                {
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len > 1)
                    {
                        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4.0)) * (Math.PI / 4.0);
                        clamped = ClampToSelection(new WpfPoint(
                            _currentAnnot.Start.X + Math.Cos(angle) * len,
                            _currentAnnot.Start.Y + Math.Sin(angle) * len));
                    }
                }
            }

            _currentAnnot.End = clamped;
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            _latestPointer = PointerPosition(e);
            if (_isMovingAnnot && _selectedAnnot != null)
            {
                MoveSelectedAnnotation(_latestPointer);
                return;
            }
            if (_currentAnnot != null) TrackAnnotation(_latestPointer);
            if (_outlineActive || _currentAnnot != null || !_hasSelection)
            {
                if (_pointerFrameQueued) return;
                _pointerFrameQueued = true;
                CompositionTarget.Rendering += OnPointerFrame;
            }
            else
            {
                var hit = FindAnnotationAt(_latestPointer);
                if (hit != null) _root.Cursor = Cursors.SizeAll;
                else if (_activeTool == DrawTool.None) _root.Cursor = _selRect.Contains(_latestPointer) ? Cursors.SizeAll : Cursors.Arrow;
                else _root.Cursor = _activeTool == DrawTool.Text ? Cursors.IBeam : Cursors.Cross;
            }
        }

        private void CancelPointerFrame()
        {
            if (!_pointerFrameQueued) return;
            _pointerFrameQueued = false;
            CompositionTarget.Rendering -= OnPointerFrame;
        }

        private void OnPointerFrame(object sender, EventArgs e)
        {
            CancelPointerFrame();
            if (_closing || _displayChanging) return;
            if (_outlineActive)
            {
                var previous = _selRect;
                UpdateDragGeometry(_latestPointer);
                if (previous != _selRect)
                {
                    GdiRect pixels = ToBitmapRect(_selRect);
                    string sizeLabel = string.Format("{0} × {1}", pixels.Width, pixels.Height);
                    _shade.Paint(_selectionMonitor, _selRect, _dragPen, sizeLabel, _selectionUiScale);
                }
                if ((_isDraggingSelection || _activeHandle >= 0) && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
                    UpdateLoupe(_latestPointer);
                else
                    _loupeCard.Visibility = Visibility.Collapsed;
            }
            else if (_currentAnnot != null) UpdateCurrentAnnotationVisual();
            else if (!_hasSelection)
            {
                UpdateHoverWindow(_latestPointer);
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) UpdateLoupe(_latestPointer);
            }
        }

        private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
        {
            CancelPointerFrame();
            var point = PointerPosition(e);
            if (_outlineActive)
            {
                UpdateDragGeometry(point);
                _outlineActive = false;
            }
            if (_currentAnnot != null) TrackAnnotation(point);
            if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();

            if (_isDraggingSelection)
            {
                _isDraggingSelection = false;
                _loupeCard.Visibility = Visibility.Collapsed;

                if (_selRect.Width < 5 && _selRect.Height < 5)
                {
                    var target = FindWindowAt(point, _selectionMonitor);
                    if (target.HasValue)
                    {
                        _selRect = target.Value;
                        _hasSelection = true;
                    }
                    else
                    {
                        _selRect = new WpfRect();
                        _hasSelection = false;
                    }
                }
                else
                {
                    _hasSelection = true;
                }

                if (_hasSelection) UpdateToolbarPositions();
                else
                {
                    PreviewCursorWindow();
                    UpdateDimLayout();
                }
                return;
            }

            if (_activeHandle >= 0)
            {
                _activeHandle = -1;
                UpdateToolbarPositions();
                return;
            }

            if (_isMovingAnnot)
            {
                _isMovingAnnot = false;
                UpdateSelectedAnnotationOutline();
                return;
            }

            if (_isMovingSelection)
            {
                _isMovingSelection = false;
                UpdateToolbarPositions();
                return;
            }

            if (_currentAnnot != null)
            {
                // Discard accidental zero-size shapes
                bool tiny = (_currentAnnot.Tool == DrawTool.Rect || _currentAnnot.Tool == DrawTool.Arrow || _currentAnnot.Tool == DrawTool.Mosaic)
                    && (_currentAnnot.End - _currentAnnot.Start).Length < 4.0;
                if (tiny)
                {
                    if (_currentAnnot.CachedVisual != null) _annotCanvas.Children.Remove(_currentAnnot.CachedVisual);
                    _annotations.Remove(_currentAnnot);
                    SelectAnnotation(null);
                }
                else
                {
                    UpdateCurrentAnnotationVisual();
                    SelectAnnotation(_currentAnnot);
                }
                _currentAnnot = null;
            }
        }

        private void OnCanvasLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_closing || !_outlineActive || _canvas.IsMouseCaptured) return;

            CancelPointerFrame();
            _outlineActive = _isDraggingSelection = _isMovingSelection = false;
            _activeHandle = -1;
            if (_hasSelection)
            {
                _selRect = _dragOrigRect;
                UpdateToolbarPositions();
            }
            else
            {
                _selRect = WpfRect.Empty;
                PreviewCursorWindow();
                UpdateDimLayout();
            }
        }

        private void OnCanvasRightUp(object sender, MouseButtonEventArgs e)
        {
            if (_activeTextBox != null)
            {
                CommitActiveTextBox();
                return;
            }
            if (_selectedAnnot != null)
            {
                SelectAnnotation(null);
                return;
            }
            if (_activeTool != DrawTool.None)
            {
                SelectTool(DrawTool.None);
                return;
            }
            if (_hasSelection)
            {
                // Clear selection back to fresh capture mode (Snipaste/Feishu muscle memory!)
                _hasSelection = false;
                _annotations.Clear();
                _redoStack.Clear();
                SelectAnnotation(null);
                RedrawAnnotations();
                _toolbar.Visibility = Visibility.Collapsed;
                _subBar.Visibility = Visibility.Collapsed;
                _selFrame.Visibility = Visibility.Collapsed;
                _sizePill.Visibility = Visibility.Collapsed;
                for (int i = 0; i < 8; i++) _handles[i].Visibility = Visibility.Collapsed;
                _root.Cursor = Cursors.Cross;
                PreviewCursorWindow();
                UpdateDimLayout();
                return;
            }
            _closeSession();
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_hasSelection) return;
            if (_activeTextBox != null)
            {
                double nextFs = Math.Max(12, Math.Min(48, _activeTextBox.FontSize + (e.Delta > 0 ? 2 : -2)));
                _activeTextBox.FontSize = nextFs;
                ShowStrokeHud(string.Format("字号 {0:0}px", nextFs));
                e.Handled = true;
                return;
            }
            if (_activeTool == DrawTool.Mosaic)
            {
                _mosaicBlockSize = Math.Max(4, Math.Min(28, _mosaicBlockSize + (e.Delta > 0 ? 2 : -2)));
                ShowStrokeHud(string.Format("马赛克粒度 {0}px", _mosaicBlockSize));
                e.Handled = true;
                return;
            }
            if (_activeTool == DrawTool.None) return;
            double step = e.Delta > 0 ? 1.0 : -1.0;
            _activeThickness = Math.Max(1.5, Math.Min(16.0, _activeThickness + step));
            SyncSubBarVisuals();
            ShowStrokeHud(string.Format("线条粗细 {0:0.#}px", _activeThickness));
            e.Handled = true;
        }

        private void ShowStrokeHud(string text)
        {
            _strokeHud.Text = text;
            _strokeHudWrap.Visibility = Visibility.Visible;
            _strokeHudWrap.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double margin = 10 * _selectionUiScale;
            double center = _selRect.Left + _selRect.Width / 2;
            Canvas.SetLeft(_strokeHudWrap, Math.Max(_selectionMonitor.Left + margin, center - _strokeHudWrap.DesiredSize.Width / 2));
            Canvas.SetTop(_strokeHudWrap, _selRect.Top + margin);
            if (_strokeHudTimer == null)
            {
                _strokeHudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                _strokeHudTimer.Tick += (s, ev) =>
                {
                    _strokeHudTimer.Stop();
                    _strokeHudWrap.Visibility = Visibility.Collapsed;
                };
            }
            _strokeHudTimer.Stop();
            _strokeHudTimer.Start();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool altDown = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if ((e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt) && (!_hasSelection || _isDraggingSelection || _activeHandle >= 0))
            {
                UpdateLoupe(PointerPosition(null));
                return;
            }

            if ((e.Key == Key.LeftShift || e.Key == Key.RightShift) && !_hasSelection && _loupeCard.Visibility == Visibility.Visible)
            {
                _loupeHexMode = !_loupeHexMode;
                UpdateLoupe(PointerPosition(null));
                e.Handled = true;
                return;
            }

            if (_activeTextBox != null)
            {
                if (e.Key == Key.Escape)
                {
                    _canvas.Children.Remove(_activeTextBox);
                    _activeTextBox = null;
                    _editingAnnot = null;
                    if (_selectedAnnot != null) RefreshAnnotationVisual(_selectedAnnot);
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && !shift)
                {
                    CommitActiveTextBox();
                    e.Handled = true;
                }
                return;
            }

            if (_selectedAnnot != null && !ctrl && !altDown && (e.Key == Key.Delete || e.Key == Key.Back || e.Key == Key.Enter))
            {
                DeleteSelectedAnnotation();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                _closeSession();
                e.Handled = true;
                return;
            }

            if (!_hasSelection && e.Key == Key.C && !ctrl && !altDown)
            {
                UpdateLoupe(PointerPosition(null));
                string clipErr;
                ClipboardHelper.TrySetText(_lastColorText, out clipErr);
                _closeSession();
                _notify("已复制色值 " + _lastColorText, false);
                e.Handled = true;
                return;
            }

            if (!_hasSelection) return;

            if (altDown && e.SystemKey == Key.C)
            {
                SaveTempAndCopyPath();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.Z && shift) { RedoLast(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Y) { RedoLast(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Z) { UndoLast(); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { SaveToFile(); e.Handled = true; }
            else if (ctrl && e.Key == Key.C) { CopyAndFinish(); e.Handled = true; }
            else if (ctrl && e.Key == Key.T) { PinToDesktop(); e.Handled = true; }
            else if (e.Key == Key.Enter) { CopyAndFinish(); e.Handled = true; }
            else if (e.Key == Key.D1 || e.Key == Key.R) { SelectTool(DrawTool.Rect); e.Handled = true; }
            else if (e.Key == Key.D2 || e.Key == Key.A) { SelectTool(DrawTool.Arrow); e.Handled = true; }
            else if (e.Key == Key.D3 || e.Key == Key.P) { SelectTool(DrawTool.Brush); e.Handled = true; }
            else if (e.Key == Key.D4 || e.Key == Key.M) { SelectTool(DrawTool.Mosaic); e.Handled = true; }
            else if (e.Key == Key.D5 || e.Key == Key.T || e.Key == Key.D6 || e.Key == Key.N) { SelectTool(DrawTool.Stamp); e.Handled = true; }
            else if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
            {
                NudgeSelectionByArrow(e.Key, shift);
                e.Handled = true;
            }
        }

        private void NudgeSelectionByArrow(Key key, bool resize)
        {
            double stepX = 1.0 / ScaleX;
            double stepY = 1.0 / ScaleY;
            double dx = key == Key.Left ? -stepX : (key == Key.Right ? stepX : 0);
            double dy = key == Key.Up ? -stepY : (key == Key.Down ? stepY : 0);

            if (resize)
            {
                double w = Math.Max(12 * stepX, Math.Min(_selectionMonitor.Right - _selRect.Left, _selRect.Width + dx));
                double h = Math.Max(12 * stepY, Math.Min(_selectionMonitor.Bottom - _selRect.Top, _selRect.Height + dy));
                _selRect = new WpfRect(_selRect.Left, _selRect.Top, w, h);
            }
            else
            {
                double x = Math.Max(_selectionMonitor.Left, Math.Min(_selectionMonitor.Right - _selRect.Width, _selRect.Left + dx));
                double y = Math.Max(_selectionMonitor.Top, Math.Min(_selectionMonitor.Bottom - _selRect.Height, _selRect.Top + dy));
                _selRect = new WpfRect(x, y, _selRect.Width, _selRect.Height);
            }
            UpdateToolbarPositions();
        }

        private bool IsMouseOverUi(DependencyObject src)
        {
            while (src != null)
            {
                if (src == _toolbar || src == _subBar || src == _activeTextBox) return true;
                for (int i = 0; i < 8; i++) if (src == _handles[i]) return true;
                src = VisualTreeHelper.GetParent(src);
            }
            return false;
        }

        // ==================== LAYOUT, HOVER & LOUPE ====================

        private WpfRect? FindWindowAt(WpfPoint point, WpfRect monitor)
        {
            if (monitor.IsEmpty || !monitor.Contains(point)) return null;
            foreach (var rect in _windowRects)
            {
                var target = WpfRect.Intersect(monitor, ToCanvasRect(rect));
                if (!target.IsEmpty && target.Width > 0 && target.Height > 0
                    && point.X >= target.Left && point.X < target.Right && point.Y >= target.Top && point.Y < target.Bottom)
                    return target;
            }
            return null;
        }

        private void UpdateHoverWindow(WpfPoint pt)
        {
            double uiScale;
            var monitor = MonitorAt(pt, out uiScale);
            var match = FindWindowAt(pt, monitor);
            if (monitor == _hoverMonitor && match == _hoverWinRect && (!match.HasValue || _hoverFrame.Visibility == Visibility.Visible)) return;
            _hoverMonitor = monitor;
            _hoverWinRect = match;
            UpdateDimLayout();
            if (match.HasValue)
            {
                var r = match.Value;
                _hoverFrame.BorderThickness = new Thickness(2.2 * uiScale);
                _hoverFrame.Width = Math.Max(0, r.Width);
                _hoverFrame.Height = Math.Max(0, r.Height);
                Canvas.SetLeft(_hoverFrame, r.Left);
                Canvas.SetTop(_hoverFrame, r.Top);
                _hoverFrame.Visibility = Visibility.Visible;
            }
            else
            {
                _hoverFrame.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateLoupe(WpfPoint pt)
        {
            if (_rawPixels == null || _rawWidth <= 0 || _rawHeight <= 0 || _root.ActualWidth <= 0 || _root.ActualHeight <= 0) return;
            double uiScale;
            var monitor = MonitorAt(pt, out uiScale);
            if (monitor.IsEmpty) return;

            int px = Math.Max(0, Math.Min(_rawWidth - 1, (int)Math.Round(pt.X * ScaleX)));
            int py = Math.Max(0, Math.Min(_rawHeight - 1, (int)Math.Round(pt.Y * ScaleY)));

            int centerOff = py * _rawStride + px * 4;
            byte cb = _rawPixels[centerOff];
            byte cg = _rawPixels[centerOff + 1];
            byte cr = _rawPixels[centerOff + 2];

            _lastColorText = _loupeHexMode
                ? string.Format("#{0:X2}{1:X2}{2:X2}", cr, cg, cb)
                : string.Format("rgb({0}, {1}, {2})", cr, cg, cb);
            _loupeColorDot.Background = new SolidColorBrush(WpfColor.FromRgb(cr, cg, cb));
            if (_isDraggingSelection || _activeHandle >= 0)
            {
                GdiRect pixels = ToBitmapRect(_selRect);
                _loupeInfoText.Text = string.Format("{0}×{1}  {2}", pixels.Width, pixels.Height, _lastColorText);
            }
            else
            {
                _loupeInfoText.Text = string.Format("{0}  ({1},{2})", _lastColorText, px, py);
            }

            // Lock-free 11x11 pixel copy into reusable WriteableBitmap
            const int half = 5;
            const int size = 11;
            int dst = 0;
            for (int dy = -half; dy <= half; dy++)
            {
                int sy = py + dy;
                bool rowValid = sy >= 0 && sy < _rawHeight;
                int rowBase = sy * _rawStride;
                for (int dx = -half; dx <= half; dx++)
                {
                    int sx = px + dx;
                    if (rowValid && sx >= 0 && sx < _rawWidth)
                    {
                        int off = rowBase + sx * 4;
                        _loupeBuf[dst++] = _rawPixels[off];
                        _loupeBuf[dst++] = _rawPixels[off + 1];
                        _loupeBuf[dst++] = _rawPixels[off + 2];
                        _loupeBuf[dst++] = 0xFF;
                    }
                    else
                    {
                        _loupeBuf[dst++] = 0x17;
                        _loupeBuf[dst++] = 0x19;
                        _loupeBuf[dst++] = 0x1A;
                        _loupeBuf[dst++] = 0xFF;
                    }
                }
            }
            _loupeBitmap.WritePixels(new Int32Rect(0, 0, size, size), _loupeBuf, size * 4, 0);

            var transform = _loupeCard.LayoutTransform as ScaleTransform;
            if (transform == null || transform.ScaleX != uiScale)
                _loupeCard.LayoutTransform = new ScaleTransform(uiScale, uiScale);
            double lx = pt.X + 18 * uiScale;
            double ly = pt.Y + 18 * uiScale;
            if (lx + 160 * uiScale > monitor.Right) lx = pt.X - 170 * uiScale;
            if (ly + 180 * uiScale > monitor.Bottom) ly = pt.Y - 185 * uiScale;
            Canvas.SetLeft(_loupeCard, Math.Max(monitor.Left + 6 * uiScale, lx));
            Canvas.SetTop(_loupeCard, Math.Max(monitor.Top + 6 * uiScale, ly));
            _loupeCard.Visibility = Visibility.Visible;
        }

        private void UpdateDimLayout()
        {
            if (_outlineActive || _source == null || _root.ActualWidth <= 0 || _root.ActualHeight <= 0) return;

            bool selecting = _hasSelection || _isDraggingSelection;
            var monitor = selecting ? _selectionMonitor : _hoverMonitor;
            WpfRect highlight = selecting ? _selRect : (_hoverWinRect ?? WpfRect.Empty);
            _shade.Paint(monitor, highlight);
            _annotCanvas.Clip = _hasSelection ? new RectangleGeometry(_selRect) : null;

            if (_topHintBar != null)
            {
                if (!selecting)
                {
                    WpfRect hintMonitor = !monitor.IsEmpty ? monitor : (_monitors.Count > 0 ? ToCanvasRect(_monitors[0].Bounds) : WpfRect.Empty);
                    if (!hintMonitor.IsEmpty)
                    {
                        _topHintBar.Visibility = Visibility.Visible;
                        _topHintBar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                        double hw = _topHintBar.DesiredSize.Width;
                        Canvas.SetLeft(_topHintBar, hintMonitor.Left + (hintMonitor.Width - hw) / 2.0);
                        Canvas.SetTop(_topHintBar, hintMonitor.Top + 12.0);
                    }
                }
                else
                {
                    _topHintBar.Visibility = Visibility.Collapsed;
                }
            }

            if (!selecting || monitor.IsEmpty) return;

            double l = Math.Max(monitor.Left, Math.Min(monitor.Right, highlight.Left));
            double t = Math.Max(monitor.Top, Math.Min(monitor.Bottom, highlight.Top));
            double r = Math.Max(l, Math.Min(monitor.Right, highlight.Right));
            double b = Math.Max(t, Math.Min(monitor.Bottom, highlight.Bottom));

            _selFrame.Width = Math.Max(0, r - l);
            _selFrame.Height = Math.Max(0, b - t);
            Canvas.SetLeft(_selFrame, l);
            Canvas.SetTop(_selFrame, t);
            _selFrame.Visibility = Visibility.Visible;

            // Size badge
            GdiRect pixels = ToBitmapRect(_selRect);
            _sizeText.Text = string.Format("{0} × {1}", pixels.Width, pixels.Height);
            double margin = 6 * _selectionUiScale;
            double above = t - 26 * _selectionUiScale;
            double py = above >= _selectionMonitor.Top + margin ? above : t + margin;
            Canvas.SetLeft(_sizePill, l);
            Canvas.SetTop(_sizePill, py);
            _sizePill.Visibility = Visibility.Visible;

            // 8 resize handles (8x8 sharp white squares)
            bool showHandles = _hasSelection && !_isDraggingSelection && _activeTool == DrawTool.None;
            double mx = (l + r) / 2.0;
            double my = (t + b) / 2.0;
            WpfPoint[] pts = new[]
            {
                new WpfPoint(l, t), new WpfPoint(mx, t), new WpfPoint(r, t), new WpfPoint(r, my),
                new WpfPoint(r, b), new WpfPoint(mx, b), new WpfPoint(l, b), new WpfPoint(l, my)
            };
            for (int i = 0; i < 8; i++)
            {
                _handles[i].Visibility = showHandles ? Visibility.Visible : Visibility.Collapsed;
                if (showHandles)
                {
                    Canvas.SetLeft(_handles[i], pts[i].X - 4.0 * _selectionUiScale);
                    Canvas.SetTop(_handles[i], pts[i].Y - 4.0 * _selectionUiScale);
                }
            }
        }

        private void UpdateToolbarPositions()
        {
            if (!_hasSelection)
            {
                _toolbar.Visibility = Visibility.Collapsed;
                _subBar.Visibility = Visibility.Collapsed;
                return;
            }

            _toolbar.Visibility = Visibility.Visible;
            _toolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double tbW = _toolbar.DesiredSize.Width;
            double tbH = _toolbar.DesiredSize.Height;

            double scale = _selectionUiScale;
            double margin = 8 * scale;
            double minX = _selectionMonitor.Left + margin, maxX = _selectionMonitor.Right - margin;
            double minY = _selectionMonitor.Top + margin, maxY = _selectionMonitor.Bottom - margin;
            double left = Math.Max(minX, Math.Min(maxX - tbW, _selRect.Right - tbW));

            bool below = _selRect.Bottom + tbH + 38 * scale <= maxY;
            double top;
            if (below) top = _selRect.Bottom + margin;
            else if (_selRect.Top - tbH - 46 * scale >= minY) top = _selRect.Top - tbH - margin;
            else top = Math.Max(minY, _selRect.Bottom - tbH - 10 * scale);

            Canvas.SetLeft(_toolbar, left);
            Canvas.SetTop(_toolbar, top);

            bool needSub = _activeTool == DrawTool.Rect || _activeTool == DrawTool.Arrow
                || _activeTool == DrawTool.Brush || _activeTool == DrawTool.Text || _activeTool == DrawTool.Stamp;
            _subBar.Visibility = needSub ? Visibility.Visible : Visibility.Collapsed;
            if (needSub)
            {
                _subBar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                double sbW = _subBar.DesiredSize.Width;
                double sbH = _subBar.DesiredSize.Height;
                double sLeft = Math.Max(minX, Math.Min(maxX - sbW, left));
                double gap = 6 * scale;
                double belowToolbar = top + tbH + gap;
                double sTop = belowToolbar + sbH <= maxY + 4 * scale ? belowToolbar : Math.Max(minY, top - sbH - gap);
                Canvas.SetLeft(_subBar, sLeft);
                Canvas.SetTop(_subBar, sTop);
            }

            UpdateDimLayout();
        }

        private void ResizeSelectionByHandle(int idx, WpfPoint pt)
        {
            double l = _dragOrigRect.Left, t = _dragOrigRect.Top, r = _dragOrigRect.Right, b = _dragOrigRect.Bottom;
            double x = Math.Max(_selectionMonitor.Left, Math.Min(_selectionMonitor.Right, pt.X));
            double y = Math.Max(_selectionMonitor.Top, Math.Min(_selectionMonitor.Bottom, pt.Y));
            double minWidth = Math.Min(12, _dragOrigRect.Width);
            double minHeight = Math.Min(12, _dragOrigRect.Height);

            if (idx == 0 || idx == 6 || idx == 7) l = Math.Min(x, r - minWidth);
            if (idx == 2 || idx == 3 || idx == 4) r = Math.Max(x, l + minWidth);
            if (idx == 0 || idx == 1 || idx == 2) t = Math.Min(y, b - minHeight);
            if (idx == 4 || idx == 5 || idx == 6) b = Math.Max(y, t + minHeight);
            _selRect = new WpfRect(l, t, r - l, b - t);
        }

        private static WpfRect MakeRect(WpfPoint a, WpfPoint b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            return new WpfRect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private WpfPoint ClampToSelection(WpfPoint p)
        {
            return new WpfPoint(
                Math.Max(_selRect.Left, Math.Min(_selRect.Right, p.X)),
                Math.Max(_selRect.Top, Math.Min(_selRect.Bottom, p.Y)));
        }

        // ==================== ANNOTATION HIT-TESTING, SELECTION, MOVE & INLINE TEXT ====================

        private static double DistanceToSegment(WpfPoint p, WpfPoint a, WpfPoint b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 0.001) return (p - a).Length;
            double t = Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            WpfPoint proj = new WpfPoint(a.X + t * dx, a.Y + t * dy);
            return (p - proj).Length;
        }

        private WpfRect GetAnnotationBounds(AnnotationItem item)
        {
            if (item == null) return WpfRect.Empty;
            if (item.Tool == DrawTool.Rect || item.Tool == DrawTool.Mosaic)
            {
                var r = MakeRect(item.Start, item.End);
                r.Inflate(Math.Max(4, item.Thickness), Math.Max(4, item.Thickness));
                return r;
            }
            if (item.Tool == DrawTool.Arrow)
            {
                var r = MakeRect(item.Start, item.End);
                r.Inflate(Math.Max(8, item.Thickness * 2.2), Math.Max(8, item.Thickness * 2.2));
                return r;
            }
            if (item.Tool == DrawTool.Brush && item.Points != null && item.Points.Count > 0)
            {
                double minX = item.Points.Min(pt => pt.X);
                double maxX = item.Points.Max(pt => pt.X);
                double minY = item.Points.Min(pt => pt.Y);
                double maxY = item.Points.Max(pt => pt.Y);
                var r = new WpfRect(minX, minY, Math.Max(2, maxX - minX), Math.Max(2, maxY - minY));
                r.Inflate(Math.Max(6, item.Thickness), Math.Max(6, item.Thickness));
                return r;
            }
            if (item.Tool == DrawTool.Text)
            {
                double fs = item.FontSize > 0 ? item.FontSize : Math.Max(14, 12 + item.Thickness * 2.0);
                double w = 72;
                double h = fs * 1.45;
                var fe = item.CachedVisual as FrameworkElement;
                if (fe != null)
                {
                    if (fe.ActualWidth <= 0) fe.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                    w = Math.Max(28, fe.ActualWidth > 0 ? fe.ActualWidth : fe.DesiredSize.Width);
                    h = Math.Max(20, fe.ActualHeight > 0 ? fe.ActualHeight : fe.DesiredSize.Height);
                }
                else if (!string.IsNullOrEmpty(item.Text))
                {
                    w = Math.Max(32, item.Text.Length * fs * 0.9);
                }
                var r = new WpfRect(item.Start.X, item.Start.Y, w, h);
                r.Inflate(4, 4);
                return r;
            }
            if (item.Tool == DrawTool.Stamp)
            {
                double boxSize = Math.Max(22, 18 + item.Thickness * 1.8);
                double w = boxSize + 2;
                double h = boxSize + 2;
                var fe = item.CachedVisual as FrameworkElement;
                if (fe != null)
                {
                    if (fe.ActualWidth <= 0) fe.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                    w = Math.Max(w, fe.ActualWidth > 0 ? fe.ActualWidth : fe.DesiredSize.Width);
                    h = Math.Max(h, fe.ActualHeight > 0 ? fe.ActualHeight : fe.DesiredSize.Height);
                }
                else if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    w += 18 + item.Text.Length * 13;
                }
                var r = new WpfRect(item.Start.X - boxSize / 2.0, item.Start.Y - boxSize / 2.0, w, h);
                r.Inflate(4, 4);
                return r;
            }
            return WpfRect.Empty;
        }

        private AnnotationItem FindAnnotationAt(WpfPoint pt)
        {
            for (int i = _annotations.Count - 1; i >= 0; i--)
            {
                var item = _annotations[i];
                if (item == null) continue;
                var bounds = GetAnnotationBounds(item);
                if (bounds.IsEmpty || !bounds.Contains(pt)) continue;

                if (item.Tool == DrawTool.Arrow)
                {
                    if (DistanceToSegment(pt, item.Start, item.End) <= Math.Max(10, item.Thickness * 2.6))
                        return item;
                    continue;
                }
                if (item.Tool == DrawTool.Brush && item.Points != null && item.Points.Count >= 2)
                {
                    double tol = Math.Max(8, item.Thickness * 1.6);
                    for (int j = 0; j < item.Points.Count - 1; j++)
                    {
                        if (DistanceToSegment(pt, item.Points[j], item.Points[j + 1]) <= tol)
                            return item;
                    }
                    continue;
                }
                return item;
            }
            return null;
        }

        private void SelectAnnotation(AnnotationItem item)
        {
            _selectedAnnot = item;
            UpdateSelectedAnnotationOutline();
        }

        private void UpdateSelectedAnnotationOutline()
        {
            if (_annotSelBox == null) return;
            if (_selectedAnnot == null || !_annotations.Contains(_selectedAnnot) || !_hasSelection)
            {
                _annotSelBox.Visibility = Visibility.Collapsed;
                return;
            }
            var r = GetAnnotationBounds(_selectedAnnot);
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0)
            {
                _annotSelBox.Visibility = Visibility.Collapsed;
                return;
            }
            _annotSelBox.Width = r.Width;
            _annotSelBox.Height = r.Height;
            Canvas.SetLeft(_annotSelBox, r.Left);
            Canvas.SetTop(_annotSelBox, r.Top);
            _annotSelBox.Visibility = Visibility.Visible;
        }

        private void RefreshAnnotationVisual(AnnotationItem item)
        {
            if (item == null) return;
            int idx = -1;
            if (item.CachedVisual != null)
            {
                idx = _annotCanvas.Children.IndexOf(item.CachedVisual);
                _annotCanvas.Children.Remove(item.CachedVisual);
            }
            var el = BuildAnnotationElement(item);
            item.CachedVisual = el;
            if (el != null)
            {
                if (idx >= 0 && idx <= _annotCanvas.Children.Count) _annotCanvas.Children.Insert(idx, el);
                else _annotCanvas.Children.Add(el);
            }
            UpdateSelectedAnnotationOutline();
        }

        private void MoveSelectedAnnotation(WpfPoint pt)
        {
            if (_selectedAnnot == null) return;
            double dx = pt.X - _annotMoveAnchor.X;
            double dy = pt.Y - _annotMoveAnchor.Y;
            if (_selectedAnnot.Tool == DrawTool.Brush && _annotOrigPoints != null)
            {
                var moved = new List<WpfPoint>(_annotOrigPoints.Count);
                for (int i = 0; i < _annotOrigPoints.Count; i++)
                    moved.Add(new WpfPoint(_annotOrigPoints[i].X + dx, _annotOrigPoints[i].Y + dy));
                _selectedAnnot.Points = moved;
            }
            else
            {
                _selectedAnnot.Start = new WpfPoint(_annotOrigStart.X + dx, _annotOrigStart.Y + dy);
                _selectedAnnot.End = new WpfPoint(_annotOrigEnd.X + dx, _annotOrigEnd.Y + dy);
            }
            RefreshAnnotationVisual(_selectedAnnot);
        }

        private void DeleteSelectedAnnotation()
        {
            if (_selectedAnnot == null) return;
            var target = _selectedAnnot;
            _selectedAnnot = null;
            _annotations.Remove(target);
            if (target.CachedVisual != null)
            {
                _annotCanvas.Children.Remove(target.CachedVisual);
                target.CachedVisual = null;
            }
            if (target.Tool == DrawTool.Stamp)
            {
                int num = 1;
                foreach (var a in _annotations.Where(x => x.Tool == DrawTool.Stamp))
                {
                    a.StampNumber = num++;
                    RefreshAnnotationVisual(a);
                }
            }
            UpdateSelectedAnnotationOutline();
        }

        private void SpawnInlineTextBoxForAnnotation(AnnotationItem item)
        {
            if (item == null) return;
            CommitActiveTextBox();
            _editingAnnot = item;
            double boxSize = Math.Max(22, 18 + item.Thickness * 1.8);
            double fontSize = item.Tool == DrawTool.Text && item.FontSize > 0
                ? item.FontSize
                : Math.Max(13, Math.Round(boxSize * 0.56));
            double left = item.Tool == DrawTool.Stamp
                ? item.Start.X + boxSize / 2.0 + 4
                : item.Start.X;
            double top = item.Tool == DrawTool.Stamp
                ? item.Start.Y - boxSize / 2.0
                : item.Start.Y;
            double maxW = Math.Max(110, _selRect.Right - left - 6);

            _activeTextBox = new TextBox
            {
                MinWidth = 100,
                MaxWidth = maxW,
                Text = item.Text ?? "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                FontFamily = UiHelpers.MainFont,
                Foreground = new SolidColorBrush(item.Color),
                Background = new SolidColorBrush(WpfColor.FromArgb(0xF4, 0xFF, 0xFF, 0xFF)),
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(6, 2, 6, 2),
                AcceptsReturn = false
            };
            Canvas.SetLeft(_activeTextBox, left);
            Canvas.SetTop(_activeTextBox, top);
            _canvas.Children.Add(_activeTextBox);
            _activeTextBox.Focus();
            _activeTextBox.SelectAll();
        }

        private void SpawnInlineTextBox(WpfPoint pos)
        {
            CommitActiveTextBox();
            _editingAnnot = null;
            _activeTextPos = pos;
            double fontSize = Math.Max(14, 12 + _activeThickness * 2.0);
            double maxW = Math.Max(90, _selRect.Right - pos.X - 6);
            _activeTextBox = new TextBox
            {
                MinWidth = 90,
                MaxWidth = maxW,
                TextWrapping = TextWrapping.Wrap,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                FontFamily = UiHelpers.MainFont,
                Foreground = new SolidColorBrush(_activeColor),
                Background = new SolidColorBrush(WpfColor.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(5, 2, 5, 2),
                AcceptsReturn = true
            };
            Canvas.SetLeft(_activeTextBox, pos.X);
            Canvas.SetTop(_activeTextBox, pos.Y - fontSize * 0.6);
            _canvas.Children.Add(_activeTextBox);
            _activeTextBox.Focus();
        }

        private void CommitActiveTextBox()
        {
            if (_activeTextBox == null) return;
            string txt = (_activeTextBox.Text ?? "").Trim();
            double fs = _activeTextBox.FontSize;
            double x = Canvas.GetLeft(_activeTextBox) + 4;
            double y = Canvas.GetTop(_activeTextBox) + 2;
            var editing = _editingAnnot;
            _editingAnnot = null;
            _canvas.Children.Remove(_activeTextBox);
            _activeTextBox = null;

            if (editing != null)
            {
                editing.Text = txt;
                if (editing.Tool == DrawTool.Text && string.IsNullOrEmpty(txt))
                {
                    if (editing.CachedVisual != null) _annotCanvas.Children.Remove(editing.CachedVisual);
                    _annotations.Remove(editing);
                    SelectAnnotation(null);
                }
                else
                {
                    editing.FontSize = fs;
                    RefreshAnnotationVisual(editing);
                    SelectAnnotation(editing);
                }
                return;
            }

            if (!string.IsNullOrEmpty(txt))
            {
                var item = new AnnotationItem
                {
                    Tool = DrawTool.Text,
                    Color = _activeColor,
                    Thickness = _activeThickness,
                    Start = new WpfPoint(x, y),
                    Text = txt,
                    FontSize = fs
                };
                _redoStack.Clear();
                _annotations.Add(item);
                AddAnnotationVisual(item);
                SelectAnnotation(item);
            }
        }

        private void UndoLast()
        {
            CommitActiveTextBox();
            if (_annotations.Count > 0)
            {
                var last = _annotations[_annotations.Count - 1];
                _annotations.RemoveAt(_annotations.Count - 1);
                if (last.CachedVisual != null)
                {
                    _annotCanvas.Children.Remove(last.CachedVisual);
                    last.CachedVisual = null;
                }
                _redoStack.Add(last);
                if (_selectedAnnot == last) SelectAnnotation(null);
            }
        }

        private void RedoLast()
        {
            CommitActiveTextBox();
            if (_redoStack.Count > 0)
            {
                var item = _redoStack[_redoStack.Count - 1];
                _redoStack.RemoveAt(_redoStack.Count - 1);
                _annotations.Add(item);
                AddAnnotationVisual(item);
                SelectAnnotation(item);
            }
        }

        private void AddAnnotationVisual(AnnotationItem item)
        {
            if (_hasSelection)
                _annotCanvas.Clip = new RectangleGeometry(_selRect);
            var el = BuildAnnotationElement(item);
            item.CachedVisual = el;
            if (el != null) _annotCanvas.Children.Add(el);
        }

        private void UpdateCurrentAnnotationVisual()
        {
            if (_currentAnnot == null) return;
            if (_hasSelection)
                _annotCanvas.Clip = new RectangleGeometry(_selRect);
            if (_currentAnnot.CachedVisual != null)
                _annotCanvas.Children.Remove(_currentAnnot.CachedVisual);
            var el = BuildAnnotationElement(_currentAnnot);
            _currentAnnot.CachedVisual = el;
            if (el != null) _annotCanvas.Children.Add(el);
        }

        private void RedrawAnnotations()
        {
            _annotCanvas.Children.Clear();
            if (_hasSelection)
                _annotCanvas.Clip = new RectangleGeometry(_selRect);

            foreach (var item in _annotations)
            {
                var el = BuildAnnotationElement(item);
                item.CachedVisual = el;
                if (el != null) _annotCanvas.Children.Add(el);
            }
            UpdateSelectedAnnotationOutline();
        }

        private UIElement BuildAnnotationElement(AnnotationItem item)
        {
            var brush = new SolidColorBrush(item.Color);
            brush.Freeze();

            if (item.Tool == DrawTool.Rect)
            {
                var r = MakeRect(item.Start, item.End);
                var shape = new WpfRectangle
                {
                    Width = r.Width,
                    Height = r.Height,
                    RadiusX = 0,
                    RadiusY = 0,
                    Stroke = brush,
                    StrokeThickness = item.Thickness
                };
                Canvas.SetLeft(shape, r.Left);
                Canvas.SetTop(shape, r.Top);
                return shape;
            }
            if (item.Tool == DrawTool.Arrow)
            {
                var poly = BuildTaperedArrowPolygon(item.Start, item.End, item.Thickness);
                if (poly != null) poly.Fill = brush;
                return poly;
            }
            if (item.Tool == DrawTool.Brush && item.Points != null && item.Points.Count >= 2)
            {
                return new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = item.Thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = new PointCollection(item.Points)
                };
            }
            if (item.Tool == DrawTool.Mosaic)
            {
                var r = MakeRect(item.Start, item.End);
                if (r.Width >= 4 && r.Height >= 4)
                {
                    var bmpSrc = BuildMosaicPreview(r, item.MosaicBlockSize);
                    if (bmpSrc != null)
                    {
                        var img = new System.Windows.Controls.Image { Source = bmpSrc, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                        Canvas.SetLeft(img, r.Left);
                        Canvas.SetTop(img, r.Top);
                        return img;
                    }
                }
                return null;
            }
            if (item.Tool == DrawTool.Text && !string.IsNullOrEmpty(item.Text))
            {
                var container = new Grid();
                var shadow = new TextBlock
                {
                    Text = item.Text,
                    FontSize = item.FontSize,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = UiHelpers.MainFont,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(0xA6, 0x1A, 0x19, 0x17)),
                    Margin = new Thickness(1, 1, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                var fg = new TextBlock
                {
                    Text = item.Text,
                    FontSize = item.FontSize,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = UiHelpers.MainFont,
                    Foreground = brush,
                    TextWrapping = TextWrapping.Wrap
                };
                container.Children.Add(shadow);
                container.Children.Add(fg);
                Canvas.SetLeft(container, item.Start.X);
                Canvas.SetTop(container, item.Start.Y);
                return container;
            }
            if (item.Tool == DrawTool.Stamp)
            {
                double boxSize = Math.Max(22, 18 + item.Thickness * 1.8);
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var wrap = new Grid { VerticalAlignment = VerticalAlignment.Center };
                wrap.Children.Add(new WpfRectangle
                {
                    Width = boxSize,
                    Height = boxSize,
                    Fill = UiHelpers.BorderStrong,
                    Margin = new Thickness(2, 2, 0, 0)
                });
                bool lightFill = item.Color == WpfColors.White;
                var box = new Border
                {
                    Width = boxSize,
                    Height = boxSize,
                    Background = brush,
                    BorderBrush = UiHelpers.BorderStrong,
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(0),
                    Margin = new Thickness(0, 0, 2, 2),
                    Child = new TextBlock
                    {
                        Text = item.StampNumber.ToString("D2"),
                        FontSize = Math.Max(11, Math.Round(boxSize * 0.48)),
                        FontWeight = FontWeights.Bold,
                        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI"),
                        Foreground = lightFill ? UiHelpers.TextPrimary : WpfBrushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                wrap.Children.Add(box);
                row.Children.Add(wrap);

                if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    var labelGrid = new Grid { Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    labelGrid.Children.Add(new WpfRectangle
                    {
                        Fill = UiHelpers.BorderStrong,
                        Margin = new Thickness(2, 2, 0, 0)
                    });
                    var labelBorder = new Border
                    {
                        MinHeight = boxSize,
                        Background = WpfBrushes.White,
                        BorderBrush = UiHelpers.BorderStrong,
                        BorderThickness = new Thickness(1.5),
                        CornerRadius = new CornerRadius(0),
                        Padding = new Thickness(7, 2, 7, 2),
                        Margin = new Thickness(0, 0, 2, 2),
                        Child = new TextBlock
                        {
                            Text = item.Text,
                            FontSize = Math.Max(12, Math.Round(boxSize * 0.52)),
                            FontWeight = FontWeights.Bold,
                            FontFamily = UiHelpers.MainFont,
                            Foreground = brush,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    labelGrid.Children.Add(labelBorder);
                    row.Children.Add(labelGrid);
                }

                Canvas.SetLeft(row, item.Start.X - boxSize / 2.0);
                Canvas.SetTop(row, item.Start.Y - boxSize / 2.0);
                return row;
            }
            return null;
        }

        private static List<WpfPoint> ComputeArrowPoints(WpfPoint start, WpfPoint end, double thickness)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 4) return null;

            double ux = dx / len;
            double uy = dy / len;
            double px = -uy;
            double py = ux;

            double headLen = Math.Min(len * 0.48, Math.Max(14.0, thickness * 4.2));
            double headHalfW = Math.Max(6.5, thickness * 2.1);
            double tailHalfW = Math.Max(1.2, thickness * 0.36);
            double neckHalfW = Math.Max(2.2, thickness * 0.78);

            WpfPoint neck = new WpfPoint(end.X - ux * headLen * 0.78, end.Y - uy * headLen * 0.78);
            WpfPoint wingBase = new WpfPoint(end.X - ux * headLen, end.Y - uy * headLen);

            return new List<WpfPoint>
            {
                new WpfPoint(start.X + px * tailHalfW, start.Y + py * tailHalfW),
                new WpfPoint(neck.X + px * neckHalfW, neck.Y + py * neckHalfW),
                new WpfPoint(wingBase.X + px * headHalfW, wingBase.Y + py * headHalfW),
                end,
                new WpfPoint(wingBase.X - px * headHalfW, wingBase.Y - py * headHalfW),
                new WpfPoint(neck.X - px * neckHalfW, neck.Y - py * neckHalfW),
                new WpfPoint(start.X - px * tailHalfW, start.Y - py * tailHalfW)
            };
        }

        private static Polygon BuildTaperedArrowPolygon(WpfPoint start, WpfPoint end, double thickness)
        {
            var pts = ComputeArrowPoints(start, end, thickness);
            if (pts == null) return null;
            return new Polygon { Points = new PointCollection(pts) };
        }

        private BitmapSource BuildMosaicPreview(WpfRect dipRect, int blockDip = 10)
        {
            if (_rawBmp == null) return null;
            GdiRect pixels = ToBitmapRect(dipRect);
            if (pixels.Width <= 1 || pixels.Height <= 1) return null;

            int block = Math.Max(4, (int)Math.Round(Math.Max(4, blockDip) * ScaleX));
            int gw = Math.Max(1, pixels.Width / block);
            int gh = Math.Max(1, pixels.Height / block);
            using (var tiny = new Bitmap(gw, gh))
            using (var g = Graphics.FromImage(tiny))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(_rawBmp, new GdiRect(0, 0, gw, gh), pixels, GraphicsUnit.Pixel);
                return NativeScreen.ToBitmapSource(tiny);
            }
        }

        // ==================== HIGH-RES COMPOSITING & OUTPUT ACTIONS ====================

        private Bitmap BuildFinalBitmap()
        {
            if (_displayChanging) throw new InvalidOperationException("显示设置已变化，请重新开始截图。");
            CommitActiveTextBox();
            GdiRect crop = ToBitmapRect(_selRect);
            if (crop.Width <= 0 || crop.Height <= 0) throw new InvalidOperationException("请选择有面积的截图区域。");

            // The overlay shows this exact frame, even if the desktop has changed underneath it.
            var result = _rawBmp.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                if (_annotations.Count > 0)
                    CompositeWpfAnnotations(result, crop);
            }
            catch { result.Dispose(); throw; }
            return result;
        }

        private void CompositeWpfAnnotations(Bitmap result, GdiRect crop)
        {
            double sx = ScaleX;
            double sy = ScaleY;
            double dipW = crop.Width / sx;
            double dipH = crop.Height / sy;
            double dipX = crop.Left / sx;
            double dipY = crop.Top / sy;

            _annotCanvas.Measure(new System.Windows.Size(_root.ActualWidth, _root.ActualHeight));
            _annotCanvas.Arrange(new WpfRect(0, 0, _root.ActualWidth, _root.ActualHeight));
            _annotCanvas.UpdateLayout();

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var vb = new VisualBrush(_annotCanvas)
                {
                    Viewbox = new WpfRect(dipX, dipY, dipW, dipH),
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.Fill
                };
                dc.DrawRectangle(vb, null, new WpfRect(0, 0, dipW, dipH));
            }

            var rtb = new RenderTargetBitmap(crop.Width, crop.Height, 96.0 * sx, 96.0 * sy, PixelFormats.Pbgra32);
            rtb.Render(dv);

            using (var overlayBmp = new Bitmap(crop.Width, crop.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                BitmapData data = overlayBmp.LockBits(
                    new GdiRect(0, 0, crop.Width, crop.Height),
                    ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    rtb.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * crop.Height, data.Stride);
                }
                finally
                {
                    overlayBmp.UnlockBits(data);
                }

                using (var g = Graphics.FromImage(result))
                {
                    g.DrawImageUnscaled(overlayBmp, 0, 0);
                }
            }
        }

        private void CopyAndFinish()
        {
            try
            {
                string clipErr;
                bool ok;
                using (var finalBmp = BuildFinalBitmap())
                {
                    ok = ClipboardHelper.TrySetImage(finalBmp, out clipErr);
                }
                _closeSession();
                if (ok) _notify("截图已复制到剪贴板，可直接 Ctrl+V 粘贴", false);
                else _notify("复制截图失败：" + clipErr, true);
            }
            catch (Exception ex)
            {
                _closeSession();
                _notify("复制截图失败：" + ex.Message, true);
            }
        }

        private void SaveTempAndCopyPath()
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIBase_Shots");
                Directory.CreateDirectory(dir);
                PruneOldTempShots(dir);

                string filePath = System.IO.Path.Combine(dir, "shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                using (var finalBmp = BuildFinalBitmap())
                {
                    finalBmp.Save(filePath, ImageFormat.Png);
                }
                string clipErr;
                bool ok = ClipboardHelper.TrySetText(filePath, out clipErr);
                _closeSession();
                if (ok) _notify("已保存并复制路径：" + filePath, false);
                else _notify("已保存图片，但复制路径失败：" + clipErr, true);
            }
            catch (Exception ex)
            {
                _closeSession();
                _notify("保存临时路径失败：" + ex.Message, true);
            }
        }

        private static void PruneOldTempShots(string dir)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddHours(-24);
                foreach (string f in Directory.GetFiles(dir, "shot_*.png"))
                {
                    try
                    {
                        if (File.GetCreationTimeUtc(f) < cutoff) File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void SaveToFile()
        {
            Bitmap finalBmp = null;
            try
            {
                finalBmp = BuildFinalBitmap();
                _closeSession();

                string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                string defaultDir = System.IO.Path.Combine(pics, "Screenshots");
                if (!Directory.Exists(defaultDir)) defaultDir = pics;

                var sfd = new SaveFileDialog
                {
                    Title = "保存截图",
                    Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
                    InitialDirectory = defaultDir,
                    FileName = "AIBase_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png"
                };
                if (sfd.ShowDialog() == true)
                {
                    var fmt = sfd.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        ? ImageFormat.Jpeg
                        : ImageFormat.Png;
                    finalBmp.Save(sfd.FileName, fmt);
                    _notify("截图已保存：" + System.IO.Path.GetFileName(sfd.FileName), false);
                }
            }
            catch (Exception ex)
            {
                _closeSession();
                _notify("保存截图失败：" + ex.Message, true);
            }
            finally
            {
                if (finalBmp != null) finalBmp.Dispose();
            }
        }

        private void PinToDesktop()
        {
            try
            {
                GdiRect crop = ToBitmapRect(_selRect);
                Bitmap finalBmp = BuildFinalBitmap();
                var physTarget = new NativeScreen.RECT
                {
                    Left = _physRect.Left + crop.Left,
                    Top = _physRect.Top + crop.Top,
                    Right = _physRect.Left + crop.Right,
                    Bottom = _physRect.Top + crop.Bottom
                };
                _closeSession();

                var pinWin = new PinnedImageWindow(finalBmp, physTarget, _notify);
                pinWin.Show();
            }
            catch (Exception ex)
            {
                _closeSession();
                _notify("贴图失败：" + ex.Message, true);
            }
        }

        private void RunOcr()
        {
            Bitmap finalBmp = null;
            try
            {
                finalBmp = BuildFinalBitmap();
                _closeSession();

                var win = new OcrResultWindow(finalBmp, _notify);
                win.Show();
            }
            catch (Exception ex)
            {
                if (finalBmp != null) finalBmp.Dispose();
                _closeSession();
                _notify("提取文字失败：" + ex.Message, true);
            }
        }
    }

    /// <summary>
    /// Borderless always-on-top zoomable floating pin window for screenshots (v1.2 0px architectural).
    /// </summary>
    internal class PinnedImageWindow : Window
    {
        private Bitmap _bmp;
        private readonly NativeScreen.RECT _physRect;
        private double _baseW;
        private double _baseH;
        private double _zoom = 1.0;
        private double _pinOpacity = 1.0;
        private bool _isTopmost = true;
        private readonly Action<string, bool> _notify;
        private Grid _hudWrap;
        private TextBlock _hudText;
        private DispatcherTimer _hudTimer;
        private PinContextMenuWindow _activeMenu;

        public PinnedImageWindow(Bitmap bmp, NativeScreen.RECT physicalScreenRect, Action<string, bool> notify)
        {
            _bmp = bmp;
            _physRect = physicalScreenRect;
            _notify = notify;

            double dpiScale = 1.0;
            _baseW = Math.Max(40, _physRect.Width / dpiScale);
            _baseH = Math.Max(40, _physRect.Height / dpiScale);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = WpfBrushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var img = new System.Windows.Controls.Image
            {
                Source = NativeScreen.ToBitmapSource(_bmp),
                Stretch = Stretch.Fill
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            _hudText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = WpfBrushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI")
            };
            var hudBorder = new Border
            {
                Background = UiHelpers.PrimaryBg,
                BorderBrush = WpfBrushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(6, 2, 6, 2),
                Child = _hudText
            };
            _hudWrap = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _hudWrap.Children.Add(hudBorder);

            var imgLayer = new Grid();
            imgLayer.Children.Add(img);
            imgLayer.Children.Add(_hudWrap);

            var card = new Border
            {
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(0),
                Background = WpfBrushes.White,
                Child = imgLayer,
                Margin = new Thickness(0, 0, 3, 3)
            };

            var root = new Grid();
            root.Children.Add(new WpfRectangle
            {
                Fill = UiHelpers.BorderStrong,
                Margin = new Thickness(3, 3, 0, 0),
                IsHitTestVisible = false
            });
            root.Children.Add(card);
            Content = root;

            Width = _baseW + 6;
            Height = _baseH + 6;

            SourceInitialized += (s, e) => PlaceInOriginalPosition();

            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) Close();
                else { try { DragMove(); } catch { } }
            };
            MouseRightButtonUp += (s, e) =>
            {
                ShowContextMenu();
                e.Handled = true;
            };
            MouseWheel += OnPinMouseWheel;
            KeyDown += (s, e) =>
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (e.Key == Key.Escape) Close();
                else if (ctrl && e.Key == Key.C) CopyPin();
                else if (ctrl && e.Key == Key.S) SavePin();
                else if (e.Key == Key.D0 || e.Key == Key.NumPad0) ResetPin();
                else if (e.Key == Key.T) ToggleTopmost();
            };
            Closed += (s, e) =>
            {
                if (_hudTimer != null) _hudTimer.Stop();
                if (_activeMenu != null) { try { _activeMenu.Close(); } catch { } }
                try { if (_bmp != null) { _bmp.Dispose(); _bmp = null; } } catch { }
            };
        }

        private void PlaceInOriginalPosition()
        {
            try
            {
                var src = PresentationSource.FromVisual(this);
                double dpiX = 1.0, dpiY = 1.0;
                if (src != null && src.CompositionTarget != null)
                {
                    dpiX = src.CompositionTarget.TransformToDevice.M11;
                    dpiY = src.CompositionTarget.TransformToDevice.M22;
                }
                _baseW = Math.Max(40, _physRect.Width / dpiX);
                _baseH = Math.Max(40, _physRect.Height / dpiY);
                Width = _baseW + 6;
                Height = _baseH + 6;

                var helper = new WindowInteropHelper(this);
                int physX = _physRect.Left - (int)Math.Round(1.5 * dpiX);
                int physY = _physRect.Top - (int)Math.Round(1.5 * dpiY);
                int physW = (int)Math.Round(Width * dpiX);
                int physH = (int)Math.Round(Height * dpiY);
                NativeScreen.SetWindowPos(helper.Handle, NativeScreen.HWND_TOPMOST, physX, physY, physW, physH, NativeScreen.SWP_NOACTIVATE);
            }
            catch { }
        }

        private void OnPinMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                _pinOpacity = Math.Max(0.20, Math.Min(1.0, _pinOpacity + (e.Delta > 0 ? 0.08 : -0.08)));
                Opacity = _pinOpacity;
                ShowHud(string.Format("透明度 {0:0}%", _pinOpacity * 100));
                return;
            }

            double nextZoom = Math.Max(0.25, Math.Min(4.0, _zoom + (e.Delta > 0 ? 0.10 : -0.10)));
            if (Math.Abs(nextZoom - _zoom) < 0.001) return;

            double oldW = Width;
            double oldH = Height;
            double newW = _baseW * nextZoom + 6;
            double newH = _baseH * nextZoom + 6;
            _zoom = nextZoom;
            Left -= (newW - oldW) / 2.0;
            Top -= (newH - oldH) / 2.0;
            Width = newW;
            Height = newH;
            ShowHud(string.Format("{0:0}%", _zoom * 100));
        }

        private void ResetPin()
        {
            double oldW = Width;
            double oldH = Height;
            _zoom = 1.0;
            _pinOpacity = 1.0;
            Opacity = 1.0;
            double newW = _baseW + 6;
            double newH = _baseH + 6;
            Left -= (newW - oldW) / 2.0;
            Top -= (newH - oldH) / 2.0;
            Width = newW;
            Height = newH;
            ShowHud("100%");
        }

        private void ToggleTopmost()
        {
            _isTopmost = !_isTopmost;
            Topmost = _isTopmost;
            ShowHud(_isTopmost ? "已置顶" : "已取消置顶");
        }

        private void ShowHud(string text)
        {
            _hudText.Text = text;
            _hudWrap.Visibility = Visibility.Visible;
            if (_hudTimer == null)
            {
                _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                _hudTimer.Tick += (s, ev) => { _hudTimer.Stop(); _hudWrap.Visibility = Visibility.Collapsed; };
            }
            _hudTimer.Stop();
            _hudTimer.Start();
        }

        private void ShowContextMenu()
        {
            if (_activeMenu != null)
            {
                try { _activeMenu.Close(); } catch { }
                _activeMenu = null;
            }
            NativeScreen.POINT pt;
            NativeScreen.GetPhysicalCursorPos(out pt);
            _activeMenu = new PinContextMenuWindow(
                _isTopmost,
                CopyPin,
                SavePin,
                RunPinOcr,
                ToggleTopmost,
                ResetPin,
                Close);
            _activeMenu.Closed += (s, e) => { if (_activeMenu == s) _activeMenu = null; };
            _activeMenu.ShowAtPhysical(pt.X, pt.Y);
        }

        private void CopyPin()
        {
            if (_bmp == null) return;
            string err;
            if (ClipboardHelper.TrySetImage(_bmp, out err))
            {
                if (_notify != null) _notify("已复制贴图到剪贴板", false);
            }
            else if (_notify != null)
            {
                _notify("复制贴图失败：" + err, true);
            }
        }

        private void SavePin()
        {
            if (_bmp == null) return;
            bool prevTop = Topmost;
            try
            {
                Topmost = false;
                var sfd = new SaveFileDialog
                {
                    Title = "保存贴图",
                    Filter = "PNG 图片 (*.png)|*.png",
                    FileName = "AIBase_Pin_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png"
                };
                if (sfd.ShowDialog(this) == true)
                {
                    _bmp.Save(sfd.FileName, ImageFormat.Png);
                    if (_notify != null) _notify("贴图已保存：" + System.IO.Path.GetFileName(sfd.FileName), false);
                }
            }
            catch (Exception ex)
            {
                if (_notify != null) _notify("保存贴图失败：" + ex.Message, true);
            }
            finally
            {
                Topmost = prevTop;
            }
        }

        private void RunPinOcr()
        {
            if (_bmp == null) return;
            try
            {
                var clone = new Bitmap(_bmp);
                var win = new OcrResultWindow(clone, _notify);
                win.Show();
            }
            catch (Exception ex)
            {
                if (_notify != null) _notify("提取文字失败：" + ex.Message, true);
            }
        }
    }

    internal sealed class PinContextMenuWindow : Window
    {
        public PinContextMenuWindow(
            bool isTopmost,
            Action onCopy,
            Action onSave,
            Action onOcr,
            Action onToggleTopmost,
            Action onReset,
            Action onClosePin)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = WpfBrushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var sp = new StackPanel { Margin = new Thickness(4) };
            sp.Children.Add(MakeRow("复制图片 (Ctrl+C)", UiHelpers.PrimaryBg, false, onCopy));
            sp.Children.Add(MakeRow("保存为 PNG (Ctrl+S)", null, false, onSave));
            sp.Children.Add(MakeRow("提取文字 (OCR)", UiHelpers.OkAccent, false, onOcr));
            sp.Children.Add(MakeRow(isTopmost ? "取消置顶 (T)" : "置顶锁定 (T)", isTopmost ? UiHelpers.OkAccent : UiHelpers.TextMuted, false, onToggleTopmost));
            sp.Children.Add(MakeRow("重置 100% (0)", null, false, onReset));
            sp.Children.Add(new Border { Height = 1, Background = UiHelpers.Hairline, Margin = new Thickness(4, 3, 4, 3) });
            sp.Children.Add(MakeRow("关闭贴图 (Esc)", UiHelpers.DangerBg, true, onClosePin));

            var card = new Border
            {
                MinWidth = 176,
                Background = UiHelpers.MainBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(0),
                Child = sp,
                Margin = new Thickness(0, 0, 3, 3)
            };

            var root = new Grid();
            root.Children.Add(new WpfRectangle { Fill = UiHelpers.BorderStrong, Margin = new Thickness(3, 3, 0, 0), IsHitTestVisible = false });
            root.Children.Add(card);
            Content = root;

            Deactivated += (s, e) => { try { Close(); } catch { } };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private Button MakeRow(string label, System.Windows.Media.Brush dot, bool danger, Action action)
        {
            var btn = UiHelpers.MakeMenuRowButton(label, dot, danger ? "danger" : "normal", false);
            btn.Click += (s, e) =>
            {
                Close();
                if (action != null) action();
            };
            return btn;
        }

        public void ShowAtPhysical(int physX, int physY)
        {
            Show();
            var src = PresentationSource.FromVisual(this);
            double dpiX = 1.0, dpiY = 1.0;
            if (src != null && src.CompositionTarget != null)
            {
                dpiX = src.CompositionTarget.TransformToDevice.M11;
                dpiY = src.CompositionTarget.TransformToDevice.M22;
            }
            var work = SystemParameters.WorkArea;
            double x = physX / dpiX;
            double y = physY / dpiY;
            if (x + ActualWidth > work.Right) x = Math.Max(work.Left, work.Right - ActualWidth - 4);
            if (y + ActualHeight > work.Bottom) y = Math.Max(work.Top, work.Bottom - ActualHeight - 4);
            Left = x;
            Top = y;
            Activate();
        }
    }

    /// <summary>
    /// Displays the text returned by the local OCR engine (v1.2 0px architectural).
    /// </summary>
    internal class OcrResultWindow : Window
    {
        private Bitmap _sourceBmp;
        private readonly TextBox _textBox;
        private readonly TextBlock _statusText;
        private readonly StackPanel _loaderBox;
        private readonly WpfRectangle[] _loaderSquares = new WpfRectangle[3];
        private readonly DispatcherTimer _loaderTimer;
        private int _loaderTick = 0;

        private readonly Button _btnPaddle;
        private readonly Button _btnWindows;
        private readonly Button _btnPreview;
        private readonly Button _btnMerge;
        private readonly Border _previewWrap;
        private readonly Action<string, bool> _notify;

        private CancellationTokenSource _ocrCts;
        private string _preferredEngine = "auto";
        private string _rawOcrText = "";
        private bool _mergedMode = false;

        public OcrResultWindow(Bitmap bmp, Action<string, bool> notify)
        {
            _sourceBmp = bmp;
            _notify = notify;

            Title = "AI BASE · 提取文字（本机离线 OCR）";
            Width = 580;
            Height = 430;
            MinWidth = 440;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Background = UiHelpers.MainBg;
            FontFamily = UiHelpers.MainFont;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Icon = UiHelpers.LoadWindowIcon();
            UiHelpers.ApplySeamlessLightTitlebar(this);

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. Top status & toolbar strip
            var topBar = new Border
            {
                Background = UiHelpers.SubtleBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5, 1.5, 1.5, 0),
                Padding = new Thickness(10, 7, 10, 7)
            };
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _loaderBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            for (int i = 0; i < 3; i++)
            {
                var sq = new WpfRectangle
                {
                    Width = 5,
                    Height = 5,
                    Fill = UiHelpers.PrimaryBg,
                    Opacity = i == 0 ? 1.0 : 0.28,
                    Margin = new Thickness(0, 0, 2, 0)
                };
                _loaderSquares[i] = sq;
                _loaderBox.Children.Add(sq);
            }
            statusRow.Children.Add(_loaderBox);

            _statusText = new TextBlock
            {
                Text = "正在本机识别中英文文字…",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusRow.Children.Add(_statusText);
            Grid.SetColumn(statusRow, 0);
            topGrid.Children.Add(statusRow);

            var toolsRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _btnPaddle = MakeMiniBtn("PaddleOCR", () => SwitchEngine("paddle"));
            _btnWindows = MakeMiniBtn("Windows OCR", () => SwitchEngine("windows"));
            _btnWindows.Margin = new Thickness(-1, 0, 6, 0);
            _btnPreview = MakeMiniBtn("原图", TogglePreview);
            _btnPreview.Margin = new Thickness(0, 0, 6, 0);
            _btnMerge = MakeMiniBtn("智能合并换行", ToggleMergeLines);

            toolsRow.Children.Add(_btnPaddle);
            toolsRow.Children.Add(_btnWindows);
            toolsRow.Children.Add(_btnPreview);
            toolsRow.Children.Add(_btnMerge);
            Grid.SetColumn(toolsRow, 1);
            topGrid.Children.Add(toolsRow);
            topBar.Child = topGrid;
            Grid.SetRow(topBar, 0);
            root.Children.Add(topBar);

            // 2. Collapsible source image preview
            var previewImg = new System.Windows.Controls.Image
            {
                Source = _sourceBmp != null ? NativeScreen.ToBitmapSource(_sourceBmp) : null,
                MaxHeight = 140,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(previewImg, BitmapScalingMode.HighQuality);
            _previewWrap = new Border
            {
                Background = UiHelpers.HoverBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5, 1, 1.5, 0),
                Padding = new Thickness(8),
                Child = previewImg,
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(_previewWrap, 1);
            root.Children.Add(_previewWrap);

            // 3. Editable OCR text area
            _textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 13,
                Padding = new Thickness(10),
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                Background = WpfBrushes.White,
                Foreground = UiHelpers.TextPrimary
            };
            Grid.SetRow(_textBox, 2);
            root.Children.Add(_textBox);

            // 4. Footer
            var footerGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hintText = new TextBlock
            {
                Text = "Ctrl+Enter 复制并关闭 · Esc 关闭",
                FontSize = 11,
                Foreground = UiHelpers.TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hintText, 0);
            footerGrid.Children.Add(hintText);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            var closeBtn = new Button
            {
                Content = "关闭",
                FontSize = 12,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = WpfBrushes.White,
                BorderBrush = UiHelpers.BorderStrong,
                Foreground = UiHelpers.TextPrimary,
                BorderThickness = new Thickness(1.5),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null
            };
            ApplyOcrBtnTemplate(closeBtn, false);
            closeBtn.Click += (s, e) => Close();

            var copyBtn = new Button
            {
                Content = "复制全部文字",
                FontSize = 12,
                Padding = new Thickness(16, 6, 16, 6),
                Background = UiHelpers.PrimaryBg,
                Foreground = WpfBrushes.White,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                FocusVisualStyle = null
            };
            ApplyOcrBtnTemplate(copyBtn, true);
            copyBtn.Click += (s, e) => CopyAndClose();

            btnRow.Children.Add(closeBtn);
            btnRow.Children.Add(copyBtn);
            Grid.SetColumn(btnRow, 1);
            footerGrid.Children.Add(btnRow);
            Grid.SetRow(footerGrid, 3);
            root.Children.Add(footerGrid);

            Content = root;

            _loaderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _loaderTimer.Tick += (s, e) =>
            {
                _loaderTick = (_loaderTick + 1) % 3;
                for (int i = 0; i < 3; i++)
                    _loaderSquares[i].Opacity = i == _loaderTick ? 1.0 : 0.25;
            };

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Close();
                else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    CopyAndClose();
                    e.Handled = true;
                }
            };
            Loaded += (s, e) => StartOcr(_preferredEngine);
            Closed += (s, e) =>
            {
                _loaderTimer.Stop();
                if (_ocrCts != null)
                {
                    try { _ocrCts.Cancel(); _ocrCts.Dispose(); } catch { }
                    _ocrCts = null;
                }
                try { if (_sourceBmp != null) { _sourceBmp.Dispose(); _sourceBmp = null; } } catch { }
            };
        }

        private static Button MakeMiniBtn(string label, Action onClick)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(8, 3, 8, 3),
                Background = WpfBrushes.White,
                Foreground = UiHelpers.TextPrimary,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null
            };
            ApplyOcrBtnTemplate(btn, false);
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private static void ApplyOcrBtnTemplate(Button btn, bool primary)
        {
            var tmpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tmpl.VisualTree = bd;

            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(Border.BackgroundProperty, primary ? UiHelpers.PrimaryHover : UiHelpers.HoverBg, "bd"));
            tmpl.Triggers.Add(hov);
            btn.Template = tmpl;
        }

        private void SyncButtonStates(string activeEngine)
        {
            bool paddleActive = activeEngine != null && activeEngine.IndexOf("Paddle", StringComparison.OrdinalIgnoreCase) >= 0;
            bool winActive = activeEngine != null && activeEngine.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0;
            _btnPaddle.Background = paddleActive ? UiHelpers.PrimaryBg : WpfBrushes.White;
            _btnPaddle.Foreground = paddleActive ? WpfBrushes.White : UiHelpers.TextPrimary;
            _btnWindows.Background = winActive ? UiHelpers.PrimaryBg : WpfBrushes.White;
            _btnWindows.Foreground = winActive ? WpfBrushes.White : UiHelpers.TextPrimary;

            _btnMerge.Background = _mergedMode ? UiHelpers.PrimaryBg : WpfBrushes.White;
            _btnMerge.Foreground = _mergedMode ? WpfBrushes.White : UiHelpers.TextPrimary;

            bool previewShown = _previewWrap.Visibility == Visibility.Visible;
            _btnPreview.Background = previewShown ? UiHelpers.PrimaryBg : WpfBrushes.White;
            _btnPreview.Foreground = previewShown ? WpfBrushes.White : UiHelpers.TextPrimary;
        }

        private void TogglePreview()
        {
            _previewWrap.Visibility = _previewWrap.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            SyncButtonStates(_preferredEngine == "windows" ? "Windows" : (_preferredEngine == "paddle" ? "Paddle" : ""));
        }

        private void ToggleMergeLines()
        {
            _mergedMode = !_mergedMode;
            _textBox.Text = _mergedMode ? SmartMergeLines(_rawOcrText) : _rawOcrText;
            SyncButtonStates(_preferredEngine == "windows" ? "Windows" : (_preferredEngine == "paddle" ? "Paddle" : ""));
        }

        private static string SmartMergeLines(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw ?? "";
            string[] lines = raw.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string cur = lines[i].Trim();
                if (cur.Length == 0)
                {
                    if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n"))
                        sb.Append("\r\n\r\n");
                    continue;
                }
                if (sb.Length == 0)
                {
                    sb.Append(cur);
                    continue;
                }
                string soFar = sb.ToString();
                if (soFar.EndsWith("\n"))
                {
                    sb.Append(cur);
                    continue;
                }
                char prevLast = soFar[soFar.Length - 1];
                char curFirst = cur[0];
                bool prevEndsSentence = "。！？.!?:：；;".IndexOf(prevLast) >= 0;
                bool curIsList = cur.StartsWith("- ") || cur.StartsWith("* ") || cur.StartsWith("• ")
                    || (cur.Length >= 3 && char.IsDigit(cur[0]) && (cur[1] == '.' || cur[1] == '、'));
                if (prevEndsSentence || curIsList)
                {
                    sb.Append("\r\n").Append(cur);
                }
                else if (IsCjk(prevLast) && IsCjk(curFirst))
                {
                    sb.Append(cur);
                }
                else
                {
                    sb.Append(' ').Append(cur);
                }
            }
            return sb.ToString().Trim();
        }

        private static bool IsCjk(char ch)
        {
            return (ch >= '\u4e00' && ch <= '\u9fff') || (ch >= '\u3000' && ch <= '\u303f') || (ch >= '\uff00' && ch <= '\uffef');
        }

        private void SwitchEngine(string mode)
        {
            _preferredEngine = mode;
            StartOcr(mode);
        }

        private void StartOcr(string mode)
        {
            if (_sourceBmp == null) return;
            if (_ocrCts != null)
            {
                try { _ocrCts.Cancel(); _ocrCts.Dispose(); } catch { }
            }
            _ocrCts = new CancellationTokenSource();
            var token = _ocrCts.Token;

            Bitmap clone = new Bitmap(_sourceBmp);
            _loaderBox.Visibility = Visibility.Visible;
            _loaderTimer.Start();
            _statusText.Text = mode == "windows" ? "正在使用 Windows OCR 识别…" : "正在本机识别中英文文字…";
            SyncButtonStates(mode == "windows" ? "Windows" : (mode == "paddle" ? "Paddle" : ""));

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string engine, text;
                    using (clone) text = OcrService.RecognizeBitmap(clone, out engine, mode, token);
                    if (token.IsCancellationRequested) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!IsVisible || token.IsCancellationRequested) return;
                        _loaderTimer.Stop();
                        _loaderBox.Visibility = Visibility.Collapsed;
                        _rawOcrText = text ?? "";
                        _textBox.Text = _mergedMode ? SmartMergeLines(_rawOcrText) : _rawOcrText;
                        SyncButtonStates(engine);
                        if (string.IsNullOrWhiteSpace(_rawOcrText))
                        {
                            _statusText.Text = engine + " · 未在选区内识别到清晰文字";
                        }
                        else
                        {
                            _statusText.Text = string.Format("{0} · 共 {1} 个字符，可编辑或复制", engine, _rawOcrText.Length);
                            _textBox.Focus();
                        }
                    }));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!IsVisible || token.IsCancellationRequested) return;
                        _loaderTimer.Stop();
                        _loaderBox.Visibility = Visibility.Collapsed;
                        _statusText.Text = "识别失败：" + ex.Message;
                    }));
                }
            });
        }

        private void CopyAndClose()
        {
            string txt = _textBox.Text ?? "";
            if (!string.IsNullOrEmpty(txt))
            {
                string err;
                if (ClipboardHelper.TrySetText(txt, out err))
                {
                    if (_notify != null) _notify("已复制识别出的文字到剪贴板", false);
                }
                else if (_notify != null)
                {
                    _notify("复制文字失败：" + err, true);
                }
            }
            Close();
        }
    }
}
