using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
using System.Windows.Media.Effects;
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
using GdiPointF = System.Drawing.PointF;
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

            string[] parts = spec.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string p = raw.Trim().ToUpperInvariant();
                if (p == "CTRL" || p == "CONTROL") modifiers |= MOD_CONTROL;
                else if (p == "SHIFT") modifiers |= MOD_SHIFT;
                else if (p == "ALT") modifiers |= MOD_ALT;
                else if (p == "WIN") modifiers |= MOD_WIN;
                else if (p.Length == 1 && p[0] >= 'A' && p[0] <= 'Z') vk = (uint)p[0];
                else if (p.Length == 1 && p[0] >= '0' && p[0] <= '9') vk = (uint)p[0];
                else if (p.StartsWith("F") && p.Length >= 2)
                {
                    int fn;
                    if (int.TryParse(p.Substring(1), out fn) && fn >= 1 && fn <= 12)
                        vk = (uint)(0x70 + fn - 1);
                }
            }
            return vk != 0;
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
            for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < columns.Length; x++)
            {
                var pixel = bitmap.GetPixel((2 * x + 1) * bitmap.Width / (2 * columns.Length),
                    (2 * y + 1) * bitmap.Height / (2 * rows.Length));
                if (pixel.R > 3 || pixel.G > 3 || pixel.B > 3) rows[y] = columns[x] = true;
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
        Text
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
    }

    internal sealed class SelectionShade : FrameworkElement
    {
        private readonly DrawingVisual _visual = new DrawingVisual();
        private readonly SolidColorBrush _shade = new SolidColorBrush(WpfColor.FromArgb(0x6E, 0x09, 0x0D, 0x16));

        internal SelectionShade()
        {
            _shade.Freeze();
            IsHitTestVisible = false;
            AddVisualChild(_visual);
        }

        protected override int VisualChildrenCount { get { return 1; } }
        protected override Visual GetVisualChild(int index)
        {
            if (index != 0) throw new ArgumentOutOfRangeException("index");
            return _visual;
        }

        internal void Paint(WpfRect monitor, WpfRect opening, System.Windows.Media.Pen outline = null)
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
                        cutout.Inflate(-inset, -inset);
                        drawing.DrawRectangle(null, outline, cutout);
                    }
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

        private Grid _root;
        private HwndSource _source;
        private Canvas _canvas;
        private SelectionShade _shade;
        private Border _selFrame;
        private Border _hoverFrame;
        private Border _sizePill;
        private TextBlock _sizeText;
        private Canvas _annotCanvas;

        // Magnifier loupe
        private Border _loupeCard;
        private System.Windows.Controls.Image _loupeImg;
        private Border _loupeColorDot;
        private TextBlock _loupeInfoText;
        private string _lastHexColor = "#000000";

        // Floating toolbar & color sub-bar
        private Border _toolbar;
        private Border _subBar;
        private readonly Dictionary<DrawTool, Button> _toolButtons = new Dictionary<DrawTool, Button>();
        private readonly List<Border> _colorDots = new List<Border>();
        private readonly List<Border> _sizeDots = new List<Border>();
        private TextBlock _strokeHud;
        private Border _strokeHudWrap;

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
        private WpfColor _activeColor = WpfColor.FromRgb(0xEF, 0x44, 0x44);
        private double _activeThickness = 3.5;

        private readonly List<AnnotationItem> _annotations = new List<AnnotationItem>();
        private AnnotationItem _currentAnnot = null;
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
            WpfColor.FromRgb(0xEF, 0x44, 0x44), // Red
            WpfColor.FromRgb(0xF5, 0x9E, 0x0B), // Amber
            WpfColor.FromRgb(0x10, 0xB9, 0x81), // Emerald
            WpfColor.FromRgb(0x3B, 0x82, 0xF6), // Blue
            WpfColor.FromRgb(0xFF, 0xFF, 0xFF), // White
            WpfColor.FromRgb(0x18, 0x18, 0x1B)  // Charcoal
        };

        private static readonly double[] StrokePresets = new[] { 2.0, 3.5, 6.0 };

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

        internal void ShowOverlay()
        {
            const int wsPopup = unchecked((int)0x80000000);
            const int wsExTopmost = 0x00000008, wsExToolWindow = 0x00000080;
            var placement = new HwndSourceParameters("AI Hub 截图", _physRect.Width, _physRect.Height)
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
            var source = _source;
            _source = null;
            try
            {
                if (source != null && !source.IsDisposed) source.Dispose();
            }
            finally
            {
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
            var root = new Grid { Background = WpfBrushes.Black, Cursor = Cursors.Cross, Focusable = true };
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

            // 5. Smart window hover highlight frame
            _hoverFrame = new Border
            {
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x10, 0xB9, 0x81)),
                BorderThickness = new Thickness(2.2),
                Background = new SolidColorBrush(WpfColor.FromArgb(0x18, 0x10, 0xB9, 0x81)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_hoverFrame);

            // 6. Active selection border
            _selFrame = new Border
            {
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x10, 0xB9, 0x81)),
                BorderThickness = new Thickness(1.5),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_selFrame);

            // 7. 8 resize handles
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
                    Width = 9,
                    Height = 9,
                    CornerRadius = new CornerRadius(5),
                    Background = WpfBrushes.White,
                    BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x10, 0xB9, 0x81)),
                    BorderThickness = new Thickness(1.6),
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

            // 8. Selection size pill
            _sizeText = new TextBlock
            {
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfBrushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI")
            };
            _sizePill = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(0xDC, 0x18, 0x1B, 0x20)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 3, 7, 3),
                Child = _sizeText,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_sizePill);

            // 9. Pixel Magnifier Loupe
            BuildLoupe();
            _canvas.Children.Add(_loupeCard);

            // 10. Stroke wheel feedback HUD
            _strokeHud = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = WpfBrushes.White };
            _strokeHudWrap = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(0xDC, 0x18, 0x1B, 0x20)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Child = _strokeHud,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_strokeHudWrap);

            // 11. Contextual Floating Toolbar + Color/Weight Sub-bar
            BuildToolbar();
            _canvas.Children.Add(_toolbar);
            _canvas.Children.Add(_subBar);

        }

        private void BuildLoupe()
        {
            _loupeImg = new System.Windows.Controls.Image { Width = 121, Height = 121, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_loupeImg, BitmapScalingMode.NearestNeighbor);

            var imgGrid = new Grid { Width = 121, Height = 121, ClipToBounds = true };
            imgGrid.Children.Add(_loupeImg);

            // Crosshair lines
            var crossBrush = new SolidColorBrush(WpfColor.FromArgb(0xCC, 0x10, 0xB9, 0x81));
            imgGrid.Children.Add(new WpfRectangle { Height = 1, Fill = crossBrush, VerticalAlignment = VerticalAlignment.Center });
            imgGrid.Children.Add(new WpfRectangle { Width = 1, Fill = crossBrush, HorizontalAlignment = HorizontalAlignment.Center });
            imgGrid.Children.Add(new Border
            {
                Width = 11,
                Height = 11,
                BorderBrush = WpfBrushes.White,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            _loupeColorDot = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(3),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 5, 0)
            };
            _loupeInfoText = new TextBlock
            {
                FontSize = 10.5,
                Foreground = WpfBrushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI")
            };

            var footer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6, 5, 6, 5) };
            var row1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row1.Children.Add(_loupeColorDot);
            row1.Children.Add(_loupeInfoText);
            footer.Children.Add(row1);
            footer.Children.Add(new TextBlock
            {
                Text = "按 C 复制色值 · Esc 取消",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x94, 0xA3, 0xB8)),
                Margin = new Thickness(0, 2, 0, 0)
            });

            var sp = new StackPanel();
            sp.Children.Add(imgGrid);
            sp.Children.Add(footer);

            _loupeCard = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(0xEA, 0x18, 0x1B, 0x20)),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Child = sp,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { Color = WpfColors.Black, BlurRadius = 14, ShadowDepth = 3, Opacity = 0.35 }
            };
        }

        private void BuildToolbar()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // Annotation tools with crisp 1.5px vector SVG geometry
            AddToolBtn(bar, DrawTool.Rect, "M3 5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z", "矩形框 (1 / R)");
            AddToolBtn(bar, DrawTool.Arrow, "M5 19 L19 5 M19 5 H10 M19 5 V14", "箭头 (2 / A)");
            AddToolBtn(bar, DrawTool.Brush, "M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z", "画笔 (3 / P)");
            AddToolBtn(bar, DrawTool.Mosaic, "M4 4h5v5H4ZM10 10h5v5h-5ZM15 4h5v5h-5ZM4 15h5v5H4ZM15 15h5v5h-5Z", "马赛克打码 (4 / M)");
            AddToolBtn(bar, DrawTool.Text, "M4 6h16 M12 6v13 M9 19h6", "文字标注 (5 / T)");

            bar.Children.Add(MakeBarSep());

            bar.Children.Add(MakeActionBtn("M9 14L4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 0 11H11", "撤销 (Ctrl+Z)", null, false, UndoLast));
            bar.Children.Add(MakeActionBtn("M4 7V5a1 1 0 0 1 1-1h2 M17 4h2a1 1 0 0 1 1 1v2 M20 17v2a1 1 0 0 1-1 1h-2 M7 20H5a1 1 0 0 1-1-1v-2 M8 9h8 M8 13h6 M8 17h4", "提取文字 OCR", "提字", false, RunOcr));
            bar.Children.Add(MakeActionBtn("M12 17v5 M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16h14v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 1 1 0 0 0 1-1V4H7v1a1 1 0 0 0 1 1 1 1 0 0 1 1 1Z", "钉在桌面置顶贴图 (Ctrl+T)", null, false, PinToDesktop));
            bar.Children.Add(MakeActionBtn("M12 3v12 M7 10l5 5 5-5 M5 20h14", "下载保存为 PNG (Ctrl+S)", null, false, SaveToFile));

            bar.Children.Add(MakeBarSep());

            bar.Children.Add(MakeActionBtn("M18 6L6 18 M6 6l12 12", "取消 (Esc)", null, false, () => _closeSession()));
            bar.Children.Add(MakeActionBtn("M20 6L9 17l-5-5", "复制到剪贴板 (Enter / 双击)", "复制", true, CopyAndFinish));

            _toolbar = new Border
            {
                Background = UiHelpers.SubtleBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 5, 6, 5),
                Child = bar,
                Cursor = Cursors.Arrow,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { Color = UiHelpers.TextPrimary.Color, BlurRadius = 2, ShadowDepth = 3, Direction = 315, Opacity = 0.22 }
            };

            // Color & Stroke sub-bar
            var subSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // 3 stroke size dots
            for (int i = 0; i < StrokePresets.Length; i++)
            {
                double sz = StrokePresets[i];
                double dotVisual = 5 + i * 3.5;
                var inner = new Ellipse
                {
                    Width = dotVisual,
                    Height = dotVisual,
                    Fill = UiHelpers.TextSecondary
                };
                var wrap = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(6),
                    Background = WpfBrushes.Transparent,
                    Child = inner,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(1, 0, 1, 0),
                    ToolTip = i == 0 ? "细线条（滚轮也可调粗细）" : (i == 1 ? "中线条（滚轮也可调粗细）" : "粗线条（滚轮也可调粗细）")
                };
                wrap.MouseLeftButtonDown += (s, e) =>
                {
                    _activeThickness = sz;
                    SyncSubBarVisuals();
                    e.Handled = true;
                };
                _sizeDots.Add(wrap);
                subSp.Children.Add(wrap);
            }

            subSp.Children.Add(MakeBarSep());

            // 6 color swatches
            for (int i = 0; i < PaletteColors.Length; i++)
            {
                WpfColor c = PaletteColors[i];
                var dot = new Border
                {
                    Width = 17,
                    Height = 17,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(c),
                    BorderBrush = c == WpfColors.White
                        ? new SolidColorBrush(WpfColor.FromRgb(0xCB, 0xD5, 0xE1))
                        : WpfBrushes.Transparent,
                    BorderThickness = new Thickness(1.5),
                    Margin = new Thickness(3, 0, 3, 0),
                    Cursor = Cursors.Hand
                };
                dot.MouseLeftButtonDown += (s, e) =>
                {
                    _activeColor = c;
                    if (_activeTextBox != null) _activeTextBox.Foreground = new SolidColorBrush(c);
                    SyncSubBarVisuals();
                    e.Handled = true;
                };
                _colorDots.Add(dot);
                subSp.Children.Add(dot);
            }

            _subBar = new Border
            {
                Background = UiHelpers.SubtleBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 8, 5),
                Child = subSp,
                Cursor = Cursors.Arrow,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { Color = UiHelpers.TextPrimary.Color, BlurRadius = 2, ShadowDepth = 3, Direction = 315, Opacity = 0.22 }
            };

            SyncSubBarVisuals();
        }

        private Border MakeBarSep()
        {
            return new Border
            {
                Width = 1,
                Height = 18,
                Background = UiHelpers.Hairline,
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
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform
            };
        }

        private void AddToolBtn(StackPanel bar, DrawTool tool, string svgData, string tip)
        {
            var btn = new Button
            {
                Width = 30,
                Height = 28,
                Margin = new Thickness(1, 0, 1, 0),
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Content = MakeSvgIcon(svgData, UiHelpers.TextSecondary)
            };
            ApplyFlatButtonTemplate(btn);
            btn.Click += (s, e) => SelectTool(_activeTool == tool ? DrawTool.None : tool);
            _toolButtons[tool] = btn;
            bar.Children.Add(btn);
        }

        private Button MakeActionBtn(string svgData, string tip, string label, bool primary, Action onClick)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var fg = primary ? WpfBrushes.White : UiHelpers.TextSecondary;
            sp.Children.Add(MakeSvgIcon(svgData, fg));
            if (!string.IsNullOrEmpty(label))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 11.5,
                    FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
                    Foreground = fg,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var btn = new Button
            {
                Height = 28,
                MinWidth = string.IsNullOrEmpty(label) ? 30 : 54,
                Padding = string.IsNullOrEmpty(label) ? new Thickness(0) : new Thickness(8, 0, 9, 0),
                Margin = new Thickness(1.5, 0, 1.5, 0),
                Background = primary ? UiHelpers.PrimaryBg : WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Content = sp
            };
            ApplyFlatButtonTemplate(btn, primary);
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private void ApplyFlatButtonTemplate(Button btn, bool primary = false)
        {
            var tmpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tmpl.VisualTree = bd;

            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(
                Border.BackgroundProperty,
                primary
                    ? UiHelpers.PrimaryHover
                    : UiHelpers.HoverBg,
                "bd"));
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
                kv.Value.Background = active
                    ? new SolidColorBrush(WpfColor.FromRgb(0xEC, 0xFD, 0xF5))
                    : WpfBrushes.Transparent;
                var path = kv.Value.Content as System.Windows.Shapes.Path;
                if (path != null)
                {
                    path.Stroke = active
                        ? new SolidColorBrush(WpfColor.FromRgb(0x05, 0x96, 0x69))
                        : new SolidColorBrush(WpfColor.FromRgb(0x33, 0x41, 0x55));
                }
            }
            _root.Cursor = tool == DrawTool.None ? Cursors.Arrow : (tool == DrawTool.Text ? Cursors.IBeam : Cursors.Cross);
            UpdateToolbarPositions();
        }

        private void SyncSubBarVisuals()
        {
            for (int i = 0; i < _sizeDots.Count; i++)
            {
                bool match = Math.Abs(StrokePresets[i] - _activeThickness) < 0.8;
                _sizeDots[i].Background = match
                    ? new SolidColorBrush(WpfColor.FromRgb(0xEC, 0xFD, 0xF5))
                    : WpfBrushes.Transparent;
            }
            for (int i = 0; i < _colorDots.Count; i++)
            {
                bool match = PaletteColors[i] == _activeColor;
                _colorDots[i].Width = match ? 20 : 16;
                _colorDots[i].Height = match ? 20 : 16;
                _colorDots[i].BorderBrush = match
                    ? new SolidColorBrush(WpfColor.FromRgb(0x10, 0xB9, 0x81))
                    : (PaletteColors[i] == WpfColors.White ? new SolidColorBrush(WpfColor.FromRgb(0xCB, 0xD5, 0xE1)) : WpfBrushes.Transparent);
                _colorDots[i].BorderThickness = new Thickness(match ? 2.2 : 1.2);
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

            _currentAnnot = new AnnotationItem
            {
                Tool = _activeTool,
                Color = _activeColor,
                Thickness = _activeThickness,
                Start = clamped,
                End = clamped,
                Points = _activeTool == DrawTool.Brush ? new List<WpfPoint> { clamped } : null
            };
            _annotations.Add(_currentAnnot);
            _canvas.CaptureMouse();
            RedrawAnnotations();
        }

        private void BeginOutline()
        {
            _outlineActive = true;
            CancelPointerFrame();
            _hoverWinRect = null;
            _hoverFrame.Visibility = Visibility.Collapsed;
            _loupeCard.Visibility = Visibility.Collapsed;
            _selFrame.Visibility = _sizePill.Visibility = _toolbar.Visibility = _subBar.Visibility = Visibility.Collapsed;
            foreach (var handle in _handles) handle.Visibility = Visibility.Collapsed;

            _dragPen = new System.Windows.Media.Pen(_selFrame.BorderBrush, _selFrame.BorderThickness.Left);
            _dragPen.Freeze();
            _shade.Paint(_selectionMonitor, _selRect, _dragPen);
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
            }
            else _currentAnnot.End = clamped;
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            _latestPointer = PointerPosition(e);
            if (_currentAnnot != null) TrackAnnotation(_latestPointer);
            if (_outlineActive || _currentAnnot != null || !_hasSelection)
            {
                if (_pointerFrameQueued) return;
                _pointerFrameQueued = true;
                CompositionTarget.Rendering += OnPointerFrame;
            }
            else if (_activeTool == DrawTool.None)
            {
                _root.Cursor = _selRect.Contains(_latestPointer) ? Cursors.SizeAll : Cursors.Arrow;
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
                if (previous != _selRect) _shade.Paint(_selectionMonitor, _selRect, _dragPen);
            }
            else if (_currentAnnot != null) RedrawAnnotations();
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
                if (tiny) _annotations.Remove(_currentAnnot);
                _currentAnnot = null;
                RedrawAnnotations();
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
            if (!_hasSelection || _activeTool == DrawTool.None) return;
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
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (s, ev) => { t.Stop(); _strokeHudWrap.Visibility = Visibility.Collapsed; };
            t.Start();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_hasSelection && !_outlineActive && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
                UpdateLoupe(PointerPosition(null));
            if (_activeTextBox != null)
            {
                if (e.Key == Key.Escape)
                {
                    _canvas.Children.Remove(_activeTextBox);
                    _activeTextBox = null;
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    CommitActiveTextBox();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Escape)
            {
                _closeSession();
                e.Handled = true;
                return;
            }

            if (!_hasSelection && e.Key == Key.C)
            {
                UpdateLoupe(PointerPosition(null));
                try { Clipboard.SetText(_lastHexColor); } catch { }
                _closeSession();
                _notify("已复制色值 " + _lastHexColor, false);
                e.Handled = true;
                return;
            }

            if (!_hasSelection) return;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.Z) { UndoLast(); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { SaveToFile(); e.Handled = true; }
            else if (ctrl && e.Key == Key.C) { CopyAndFinish(); e.Handled = true; }
            else if (ctrl && e.Key == Key.T) { PinToDesktop(); e.Handled = true; }
            else if (e.Key == Key.Enter) { CopyAndFinish(); e.Handled = true; }
            else if (e.Key == Key.D1 || e.Key == Key.R) { SelectTool(DrawTool.Rect); e.Handled = true; }
            else if (e.Key == Key.D2 || e.Key == Key.A) { SelectTool(DrawTool.Arrow); e.Handled = true; }
            else if (e.Key == Key.D3 || e.Key == Key.P) { SelectTool(DrawTool.Brush); e.Handled = true; }
            else if (e.Key == Key.D4 || e.Key == Key.M) { SelectTool(DrawTool.Mosaic); e.Handled = true; }
            else if (e.Key == Key.D5 || e.Key == Key.T) { SelectTool(DrawTool.Text); e.Handled = true; }
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
            if (_rawBmp == null || _root.ActualWidth <= 0 || _root.ActualHeight <= 0) return;
            double uiScale;
            var monitor = MonitorAt(pt, out uiScale);
            if (monitor.IsEmpty) return;

            int px = Math.Max(0, Math.Min(_rawBmp.Width - 1, (int)Math.Round(pt.X * ScaleX)));
            int py = Math.Max(0, Math.Min(_rawBmp.Height - 1, (int)Math.Round(pt.Y * ScaleY)));

            GdiColor c = _rawBmp.GetPixel(px, py);
            _lastHexColor = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            _loupeColorDot.Background = new SolidColorBrush(WpfColor.FromRgb(c.R, c.G, c.B));
            if (_isDraggingSelection)
            {
                GdiRect pixels = ToBitmapRect(_selRect);
                _loupeInfoText.Text = string.Format("{0}×{1}  {2}", pixels.Width, pixels.Height, _lastHexColor);
            }
            else
            {
                _loupeInfoText.Text = string.Format("{0},{1}  {2}", px, py, _lastHexColor);
            }

            // Build 11x11 pixel zoom crop
            const int half = 5;
            const int size = 11;
            using (var mini = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                for (int dy = -half; dy <= half; dy++)
                {
                    for (int dx = -half; dx <= half; dx++)
                    {
                        int sx = px + dx;
                        int sy = py + dy;
                        GdiColor col = (sx >= 0 && sx < _rawBmp.Width && sy >= 0 && sy < _rawBmp.Height)
                            ? _rawBmp.GetPixel(sx, sy)
                            : GdiColor.Black;
                        mini.SetPixel(dx + half, dy + half, col);
                    }
                }
                _loupeImg.Source = NativeScreen.ToBitmapSource(mini);
            }

            var transform = _loupeCard.LayoutTransform as ScaleTransform;
            if (transform == null || transform.ScaleX != uiScale)
                _loupeCard.LayoutTransform = new ScaleTransform(uiScale, uiScale);
            double lx = pt.X + 18 * uiScale;
            double ly = pt.Y + 18 * uiScale;
            if (lx + 145 * uiScale > monitor.Right) lx = pt.X - 155 * uiScale;
            if (ly + 170 * uiScale > monitor.Bottom) ly = pt.Y - 175 * uiScale;
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

            // 8 resize handles (show only when selection is settled and no draw tool active)
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
                    Canvas.SetLeft(_handles[i], pts[i].X - 4.5 * _selectionUiScale);
                    Canvas.SetTop(_handles[i], pts[i].Y - 4.5 * _selectionUiScale);
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

            bool needSub = _activeTool == DrawTool.Rect || _activeTool == DrawTool.Arrow || _activeTool == DrawTool.Brush || _activeTool == DrawTool.Text;
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

        // ==================== INLINE TEXT & ANNOTATION RENDERING ====================

        private void SpawnInlineTextBox(WpfPoint pos)
        {
            CommitActiveTextBox();
            _activeTextPos = pos;
            double fontSize = Math.Max(14, 12 + _activeThickness * 2.0);
            _activeTextBox = new TextBox
            {
                MinWidth = 90,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                FontFamily = UiHelpers.MainFont,
                Foreground = new SolidColorBrush(_activeColor),
                Background = new SolidColorBrush(WpfColor.FromArgb(0x28, 0x00, 0x00, 0x00)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x10, 0xB9, 0x81)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
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
            _canvas.Children.Remove(_activeTextBox);
            _activeTextBox = null;

            if (!string.IsNullOrEmpty(txt))
            {
                _annotations.Add(new AnnotationItem
                {
                    Tool = DrawTool.Text,
                    Color = _activeColor,
                    Thickness = _activeThickness,
                    Start = new WpfPoint(x, y),
                    Text = txt,
                    FontSize = fs
                });
                RedrawAnnotations();
            }
        }

        private void UndoLast()
        {
            CommitActiveTextBox();
            if (_annotations.Count > 0)
            {
                _annotations.RemoveAt(_annotations.Count - 1);
                RedrawAnnotations();
            }
        }

        private void RedrawAnnotations()
        {
            _annotCanvas.Children.Clear();
            if (_hasSelection)
                _annotCanvas.Clip = new RectangleGeometry(_selRect);

            foreach (var item in _annotations)
            {
                var brush = new SolidColorBrush(item.Color);
                if (item.Tool == DrawTool.Rect)
                {
                    var r = MakeRect(item.Start, item.End);
                    var shape = new WpfRectangle
                    {
                        Width = r.Width,
                        Height = r.Height,
                        RadiusX = 2,
                        RadiusY = 2,
                        Stroke = brush,
                        StrokeThickness = item.Thickness
                    };
                    Canvas.SetLeft(shape, r.Left);
                    Canvas.SetTop(shape, r.Top);
                    _annotCanvas.Children.Add(shape);
                }
                else if (item.Tool == DrawTool.Arrow)
                {
                    var poly = BuildTaperedArrowPolygon(item.Start, item.End, item.Thickness);
                    if (poly != null)
                    {
                        poly.Fill = brush;
                        _annotCanvas.Children.Add(poly);
                    }
                }
                else if (item.Tool == DrawTool.Brush && item.Points != null && item.Points.Count >= 2)
                {
                    var pl = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = item.Thickness,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Points = new PointCollection(item.Points)
                    };
                    _annotCanvas.Children.Add(pl);
                }
                else if (item.Tool == DrawTool.Mosaic)
                {
                    var r = MakeRect(item.Start, item.End);
                    if (r.Width >= 4 && r.Height >= 4)
                    {
                        var bmpSrc = BuildMosaicPreview(r);
                        if (bmpSrc != null)
                        {
                            var img = new System.Windows.Controls.Image { Source = bmpSrc, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill };
                            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                            Canvas.SetLeft(img, r.Left);
                            Canvas.SetTop(img, r.Top);
                            _annotCanvas.Children.Add(img);
                        }
                    }
                }
                else if (item.Tool == DrawTool.Text && !string.IsNullOrEmpty(item.Text))
                {
                    var tb = new TextBlock
                    {
                        Text = item.Text,
                        FontSize = item.FontSize,
                        FontWeight = FontWeights.SemiBold,
                        FontFamily = UiHelpers.MainFont,
                        Foreground = brush,
                        Effect = new DropShadowEffect { Color = WpfColors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 0.45 }
                    };
                    Canvas.SetLeft(tb, item.Start.X);
                    Canvas.SetTop(tb, item.Start.Y);
                    _annotCanvas.Children.Add(tb);
                }
            }
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

        private BitmapSource BuildMosaicPreview(WpfRect dipRect)
        {
            if (_rawBmp == null) return null;
            GdiRect pixels = ToBitmapRect(dipRect);
            if (pixels.Width <= 1 || pixels.Height <= 1) return null;

            int block = Math.Max(8, (int)Math.Round(10 * ScaleX));
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
            var result = _rawBmp.Clone(crop, _rawBmp.PixelFormat);
            try { DrawFinalAnnotations(result, crop); }
            catch { result.Dispose(); throw; }
            return result;
        }

        private void DrawFinalAnnotations(Bitmap result, GdiRect crop)
        {
            if (_annotations.Count == 0) return;
            using (var g = Graphics.FromImage(result))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                double sx = ScaleX;
                double sy = ScaleY;
                double ox = crop.Left / sx;
                double oy = crop.Top / sy;

                foreach (var item in _annotations)
                {
                    GdiColor col = GdiColor.FromArgb(item.Color.A, item.Color.R, item.Color.G, item.Color.B);
                    float penW = (float)(item.Thickness * sx);

                    if (item.Tool == DrawTool.Rect)
                    {
                        var r = MakeRect(item.Start, item.End);
                        using (var pen = new System.Drawing.Pen(col, penW))
                        {
                            pen.LineJoin = LineJoin.Round;
                            g.DrawRectangle(pen, (float)((r.Left - ox) * sx), (float)((r.Top - oy) * sy), (float)(r.Width * sx), (float)(r.Height * sy));
                        }
                    }
                    else if (item.Tool == DrawTool.Arrow)
                    {
                        var pts = ComputeArrowPoints(item.Start, item.End, item.Thickness);
                        if (pts != null)
                        {
                            var gdiPts = pts.Select(p => new GdiPointF((float)((p.X - ox) * sx), (float)((p.Y - oy) * sy))).ToArray();
                            using (var br = new SolidBrush(col))
                                g.FillPolygon(br, gdiPts);
                        }
                    }
                    else if (item.Tool == DrawTool.Brush && item.Points != null && item.Points.Count >= 2)
                    {
                        var gdiPts = item.Points.Select(p => new GdiPointF((float)((p.X - ox) * sx), (float)((p.Y - oy) * sy))).ToArray();
                        using (var pen = new System.Drawing.Pen(col, penW))
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            pen.LineJoin = LineJoin.Round;
                            g.DrawLines(pen, gdiPts);
                        }
                    }
                    else if (item.Tool == DrawTool.Mosaic)
                    {
                        var r = MakeRect(item.Start, item.End);
                        GdiRect pixels = GdiRect.Intersect(crop, ToBitmapRect(r));
                        if (pixels.Width <= 0 || pixels.Height <= 0) continue;
                        var output = new GdiRect(pixels.Left - crop.Left, pixels.Top - crop.Top, pixels.Width, pixels.Height);
                        int block = Math.Max(8, (int)Math.Round(10 * sx));
                        int gw = Math.Max(1, output.Width / block);
                        int gh = Math.Max(1, output.Height / block);
                        using (var tiny = new Bitmap(gw, gh))
                        using (var gt = Graphics.FromImage(tiny))
                        {
                            gt.InterpolationMode = InterpolationMode.Bilinear;
                            gt.DrawImage(result, new GdiRect(0, 0, gw, gh), output, GraphicsUnit.Pixel);
                            var prevInterp = g.InterpolationMode;
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.DrawImage(tiny, output, new GdiRect(0, 0, gw, gh), GraphicsUnit.Pixel);
                            g.InterpolationMode = prevInterp;
                        }
                    }
                    else if (item.Tool == DrawTool.Text && !string.IsNullOrEmpty(item.Text))
                    {
                        float pxFont = (float)(item.FontSize * sy * 0.96);
                        using (var font = new Font("Microsoft YaHei UI", pxFont, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var shadowBr = new SolidBrush(GdiColor.FromArgb(110, 0, 0, 0)))
                        using (var br = new SolidBrush(col))
                        {
                            float tx = (float)((item.Start.X - ox) * sx);
                            float ty = (float)((item.Start.Y - oy) * sy);
                            g.DrawString(item.Text, font, shadowBr, tx + 1.5f, ty + 1.5f);
                            g.DrawString(item.Text, font, br, tx, ty);
                        }
                    }
                }
            }
        }

        private void CopyAndFinish()
        {
            try
            {
                using (var finalBmp = BuildFinalBitmap())
                {
                    WinForms.Clipboard.SetImage(finalBmp);
                }
                _closeSession();
                _notify("截图已复制到剪贴板，可直接 Ctrl+V 粘贴", false);
            }
            catch (Exception ex)
            {
                _closeSession();
                _notify("复制截图失败：" + ex.Message, true);
            }
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
                    FileName = "AIHub_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png"
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
                Bitmap finalBmp = BuildFinalBitmap();
                double dispW = _selRect.Width;
                double dispH = _selRect.Height;
                _closeSession();

                var pinWin = new PinnedImageWindow(finalBmp, dispW, dispH, _notify);
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
    /// Borderless always-on-top zoomable floating pin window for screenshots.
    /// </summary>
    internal class PinnedImageWindow : Window
    {
        private Bitmap _bmp;
        private readonly double _baseW;
        private readonly double _baseH;
        private double _zoom = 1.0;
        private readonly Action<string, bool> _notify;

        public PinnedImageWindow(Bitmap bmp, double displayW, double displayH, Action<string, bool> notify)
        {
            _bmp = bmp;
            _baseW = Math.Max(80, displayW);
            _baseH = Math.Max(60, displayH);
            _notify = notify;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = WpfBrushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = _baseW + 20;
            Height = _baseH + 20;

            var img = new System.Windows.Controls.Image
            {
                Source = NativeScreen.ToBitmapSource(_bmp),
                Stretch = Stretch.Fill
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var card = new Border
            {
                Margin = new Thickness(10),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x10, 0xB9, 0x81)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(4),
                Child = img,
                Effect = new DropShadowEffect { Color = WpfColors.Black, BlurRadius = 16, ShadowDepth = 4, Opacity = 0.30 }
            };
            Content = card;

            var cm = new ContextMenu();
            var mCopy = new MenuItem { Header = "复制图片 (Ctrl+C)" };
            mCopy.Click += (s, e) => CopyPin();
            var mSave = new MenuItem { Header = "保存为 PNG..." };
            mSave.Click += (s, e) => SavePin();
            var mClose = new MenuItem { Header = "关闭贴图 (Esc)" };
            mClose.Click += (s, e) => Close();
            cm.Items.Add(mCopy);
            cm.Items.Add(mSave);
            cm.Items.Add(new Separator());
            cm.Items.Add(mClose);
            ContextMenu = cm;

            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) Close();
                else { try { DragMove(); } catch { } }
            };
            MouseWheel += (s, e) =>
            {
                _zoom = Math.Max(0.3, Math.Min(3.0, _zoom + (e.Delta > 0 ? 0.1 : -0.1)));
                Width = _baseW * _zoom + 20;
                Height = _baseH * _zoom + 20;
            };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Close();
                else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) CopyPin();
            };
            Closed += (s, e) => { try { if (_bmp != null) { _bmp.Dispose(); _bmp = null; } } catch { } };
        }

        private void CopyPin()
        {
            try { WinForms.Clipboard.SetImage(_bmp); if (_notify != null) _notify("已复制贴图到剪贴板", false); } catch { }
        }

        private void SavePin()
        {
            var sfd = new SaveFileDialog
            {
                Title = "保存贴图",
                Filter = "PNG 图片 (*.png)|*.png",
                FileName = "AIHub_Pin_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png"
            };
            if (sfd.ShowDialog() == true)
            {
                _bmp.Save(sfd.FileName, ImageFormat.Png);
                if (_notify != null) _notify("贴图已保存：" + System.IO.Path.GetFileName(sfd.FileName), false);
            }
        }
    }

    /// <summary>
    /// Displays the text returned by the local OCR engine.
    /// </summary>
    internal class OcrResultWindow : Window
    {
        private Bitmap _bmp;
        private TextBox _textBox;
        private TextBlock _statusText;
        private readonly Action<string, bool> _notify;

        public OcrResultWindow(Bitmap bmp, Action<string, bool> notify)
        {
            _bmp = bmp;
            _notify = notify;

            Title = "AI Hub · 提取文字（本机离线 OCR）";
            Width = 520;
            Height = 380;
            MinWidth = 380;
            MinHeight = 260;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Background = UiHelpers.MainBg;
            FontFamily = UiHelpers.MainFont;
            Icon = UiHelpers.LoadWindowIcon();
            UiHelpers.ApplySeamlessLightTitlebar(this);

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _statusText = new TextBlock
            {
                Text = "正在本机识别中英文文字…",
                FontSize = 12.5,
                Foreground = UiHelpers.TextSecondary,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(_statusText, 0);
            grid.Children.Add(_statusText);

            _textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 13.5,
                Padding = new Thickness(10),
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                Background = UiHelpers.SubtleBg,
                Foreground = UiHelpers.TextPrimary
            };
            Grid.SetRow(_textBox, 1);
            grid.Children.Add(_textBox);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var closeBtn = new Button
            {
                Content = "关闭",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = WpfBrushes.White,
                BorderBrush = UiHelpers.DangerBorder,
                Foreground = UiHelpers.DangerText,
                BorderThickness = new Thickness(1.5),
                Cursor = Cursors.Hand
            };
            closeBtn.Click += (s, e) => Close();

            var copyBtn = new Button
            {
                Content = "复制全部文字",
                Padding = new Thickness(16, 6, 16, 6),
                Background = UiHelpers.PrimaryBg,
                Foreground = WpfBrushes.White,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand
            };
            copyBtn.Click += (s, e) =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(_textBox.Text))
                    {
                        Clipboard.SetText(_textBox.Text);
                        if (_notify != null) _notify("已复制识别出的文字到剪贴板", false);
                    }
                    Close();
                }
                catch { }
            };

            footer.Children.Add(closeBtn);
            footer.Children.Add(copyBtn);
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);

            Content = grid;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += OnLoaded;
            Closed += (s, e) => { try { if (_bmp != null) { _bmp.Dispose(); _bmp = null; } } catch { } };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Bitmap input = _bmp;
            if (input == null) return;
            _bmp = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string engine, text;
                    using (input) text = OcrService.RecognizeBitmap(input, out engine);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!IsVisible) return;
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            _statusText.Text = engine + " · 未在选区内识别到清晰文字";
                            _textBox.Text = "";
                        }
                        else
                        {
                            _statusText.Text = string.Format("{0} · 共 {1} 个字符，可编辑或复制", engine, text.Length);
                            _textBox.Text = text;
                            _textBox.Focus();
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (IsVisible) _statusText.Text = "识别失败：" + ex.Message;
                    }));
                }
            });
        }
    }
}
