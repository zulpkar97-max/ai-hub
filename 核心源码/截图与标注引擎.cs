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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
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

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool GetMonitorDevice(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr CreateDisplayDC(string driver, string device, IntPtr port, IntPtr devmode);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

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
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOACTIVATE = 0x0010;
        private const uint GW_HWNDNEXT = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;

        public static List<RECT> GetAllMonitors()
        {
            var list = new List<RECT>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(hMon, ref mi))
                    list.Add(mi.rcMonitor);
                else
                    list.Add(rc);
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

        public static Bitmap CaptureRect(RECT r)
        {
            if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("屏幕范围无效。");

            Bitmap frame = null;
            Exception desktopError = null;
            IntPtr desktop = GetDC(IntPtr.Zero);
            if (desktop != IntPtr.Zero)
            {
                try
                {
                    frame = CaptureFromDC(desktop, r.Left, r.Top, r.Width, r.Height);
                    if (HasVisibleSample(frame)) return frame;
                }
                catch (Exception ex) { desktopError = ex; }
                finally { ReleaseDC(IntPtr.Zero, desktop); }
            }

            Bitmap alternate = null;
            try
            {
                alternate = CaptureFromMonitor(r);
                if (alternate != null && (frame == null || HasVisibleSample(alternate)))
                {
                    if (frame != null) frame.Dispose();
                    var result = alternate;
                    alternate = null;
                    return result;
                }
            }
            catch (Exception ex)
            {
                if (desktopError == null) desktopError = ex;
            }
            finally { if (alternate != null) alternate.Dispose(); }

            // Keep an otherwise valid dark image, even if both sources return it.
            if (frame != null) return frame;
            throw new InvalidOperationException("无法抓取该屏幕。", desktopError);
        }

        private static Bitmap CaptureFromMonitor(RECT r)
        {
            IntPtr monitor = MonitorFromPoint(new POINT { X = r.Left, Y = r.Top }, 0);
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (monitor == IntPtr.Zero || !GetMonitorDevice(monitor, ref info) || string.IsNullOrEmpty(info.device)) return null;
            IntPtr display = CreateDisplayDC("DISPLAY", info.device, IntPtr.Zero, IntPtr.Zero);
            if (display == IntPtr.Zero) return null;
            try { return CaptureFromDC(display, r.Left - info.rcMonitor.Left, r.Top - info.rcMonitor.Top, r.Width, r.Height); }
            finally { DeleteDC(display); }
        }

        private static bool HasVisibleSample(Bitmap bitmap)
        {
            for (int i = 1; i <= 16; i++)
            for (int j = 1; j <= 12; j++)
            {
                var pixel = bitmap.GetPixel(bitmap.Width * i / 17, bitmap.Height * j / 13);
                if (pixel.R > 3 || pixel.G > 3 || pixel.B > 3) return true;
            }
            return false;
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
        private static readonly List<ScreenshotOverlayWindow> ActiveOverlays = new List<ScreenshotOverlayWindow>();
        private static Action<string, bool> _notifyCallback;

        public static bool IsActive { get { return _active; } }

        public static void Start(Action<string, bool> onNotify)
        {
            if (_active) return;
            _active = true;
            _notifyCallback = onNotify;

            try
            {
                var monitors = NativeScreen.GetAllMonitors();
                var winRects = NativeScreen.GetVisibleTopWindowRects();
                if (monitors.Count == 0) throw new InvalidOperationException("未找到可用的显示器。");

                ActiveOverlays.Clear();
                var failures = new List<string>();

                foreach (var mon in monitors)
                {
                    Bitmap bmp = null;
                    try
                    {
                        bmp = NativeScreen.CaptureRect(mon);
                        var localWins = new List<NativeScreen.RECT>();
                        foreach (var wr in winRects)
                        {
                            int l = Math.Max(mon.Left, wr.Left) - mon.Left;
                            int t = Math.Max(mon.Top, wr.Top) - mon.Top;
                            int r = Math.Min(mon.Right, wr.Right) - mon.Left;
                            int b = Math.Min(mon.Bottom, wr.Bottom) - mon.Top;
                            if (r > l && b > t)
                                localWins.Add(new NativeScreen.RECT { Left = l, Top = t, Right = r, Bottom = b });
                        }

                        var overlay = new ScreenshotOverlayWindow(mon, bmp, localWins, CloseAll, Notify);
                        ActiveOverlays.Add(overlay);
                        bmp = null;
                    }
                    catch (Exception ex) { failures.Add(ex.Message); }
                    finally { if (bmp != null) bmp.Dispose(); }
                }

                // Use completed image buffers before exposing any topmost windows.
                foreach (var overlay in ActiveOverlays.ToList())
                {
                    try { overlay.ShowOverlay(); }
                    catch (Exception ex)
                    {
                        overlay.ForceClose();
                        ActiveOverlays.Remove(overlay);
                        failures.Add(ex.Message);
                    }
                }
                if (ActiveOverlays.Count == 0) CloseAll();
                else
                {
                    var focus = ActiveOverlays.FirstOrDefault(overlay => overlay.PreviewCursorWindow()) ?? ActiveOverlays[0];
                    focus.Activate();
                    focus.Focus();
                }

                if (failures.Count > 0)
                    Notify("有 " + failures.Count + " 块屏幕无法截图，已跳过：" + failures[0], true);
            }
            catch (Exception ex)
            {
                CloseAll();
                Notify("启动截图失败：" + ex.Message, true);
            }
        }

        private static void Notify(string msg, bool isError)
        {
            if (_notifyCallback != null)
            {
                try { _notifyCallback(msg, isError); } catch { }
            }
        }

        internal static Bitmap CaptureSelection(NativeScreen.RECT selection)
        {
            foreach (var overlay in ActiveOverlays) overlay.Hide();
            NativeScreen.DwmFlush();
            return NativeScreen.CaptureRect(selection);
        }

        public static void CloseAll()
        {
            var copy = ActiveOverlays.ToList();
            ActiveOverlays.Clear();
            foreach (var ov in copy)
            {
                try { ov.ForceClose(); } catch { }
            }
            _active = false;
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

    internal sealed class DragOutline : IDisposable
    {
        private sealed class Edge : WinForms.Form
        {
            public Edge()
            {
                ShowInTaskbar = false;
                TopMost = true;
                FormBorderStyle = WinForms.FormBorderStyle.None;
                StartPosition = WinForms.FormStartPosition.Manual;
                BackColor = GdiColor.FromArgb(16, 185, 129);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override WinForms.CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= 0x080000A0; // no activation, tool window, transparent to hit testing
                    return parameters;
                }
            }

            internal void MoveEdge(int x, int y, int width, int height)
            {
                if (!Visible)
                {
                    NativeScreen.SetWindowPos(Handle, NativeScreen.HWND_TOPMOST, x, y, width, height, NativeScreen.SWP_NOACTIVATE);
                    Show();
                }
                NativeScreen.SetWindowPos(Handle, NativeScreen.HWND_TOPMOST, x, y, width, height, NativeScreen.SWP_NOACTIVATE);
            }
        }

        private readonly Edge[] _edges = { new Edge(), new Edge(), new Edge(), new Edge() };
        private GdiRect _last;

        internal void Move(int left, int top, int right, int bottom)
        {
            const int thickness = 2;
            var next = GdiRect.FromLTRB(left, top, right, bottom);
            if (next.Width < thickness || next.Height < thickness) { Hide(); return; }
            if (_last == next) return;
            _last = next;
            _edges[0].MoveEdge(left, top, next.Width, thickness);
            _edges[1].MoveEdge(left, bottom - thickness, next.Width, thickness);
            _edges[2].MoveEdge(left, top, thickness, next.Height);
            _edges[3].MoveEdge(right - thickness, top, thickness, next.Height);
        }

        internal void Hide()
        {
            _last = GdiRect.Empty;
            foreach (var edge in _edges) if (edge.Visible) edge.Hide();
        }

        public void Dispose()
        {
            foreach (var edge in _edges) edge.Dispose();
        }
    }

    internal class ScreenshotOverlayWindow : Window
    {
        private readonly NativeScreen.RECT _physRect;
        private Bitmap _rawBmp;
        private readonly List<NativeScreen.RECT> _windowRects;
        private readonly Action _closeSession;
        private readonly Action<string, bool> _notify;

        private Canvas _canvas;
        private WpfRectangle _dimTop, _dimBottom, _dimLeft, _dimRight;
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
        private readonly DispatcherTimer _pointerFrame;
        private WpfPoint _latestPointer;
        private DragOutline _dragOutline;
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
            List<NativeScreen.RECT> windowRects,
            Action closeSession,
            Action<string, bool> notify)
        {
            _physRect = physRect;
            _rawBmp = rawBmp;
            _windowRects = windowRects ?? new List<NativeScreen.RECT>();
            _closeSession = closeSession;
            _notify = notify;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Topmost = true;
            AllowsTransparency = true;
            // Keep the undimmed selection hit-testable without hiding the desktop.
            Background = new SolidColorBrush(WpfColor.FromArgb(1, 0, 0, 0));
            Cursor = Cursors.Cross;
            FontFamily = UiHelpers.MainFont;

            BuildVisualTree();
            _pointerFrame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _pointerFrame.Tick += (s, e) =>
            {
                _pointerFrame.Stop();
                PaintPointerFrame();
            };

            SizeChanged += (s, e) =>
            {
                if (!_hasSelection && !_isDraggingSelection) PreviewCursorWindow();
                UpdateDimLayout();
            };
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                    _loupeCard.Visibility = Visibility.Collapsed;
            };
            PreviewMouseWheel += OnPreviewMouseWheel;
        }

        internal void ShowOverlay()
        {
            // Fix the hidden HWND to the monitor before WPF's first visible layout.
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            var source = HwndSource.FromHwnd(hwnd);
            if (source != null) source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            PositionOverlay(hwnd);
            Show();
            PositionOverlay(hwnd);
            UpdateLayout();
            NativeScreen.RECT windowRect;
            if (!NativeScreen.GetWindowRect(hwnd, out windowRect) || windowRect.Left != _physRect.Left
                || windowRect.Top != _physRect.Top || windowRect.Width != _physRect.Width || windowRect.Height != _physRect.Height)
                throw new InvalidOperationException("截图遮罩未能对齐显示器。");
            UpdateDimLayout();
        }

        private void PositionOverlay(IntPtr hwnd)
        {
            if (!NativeScreen.SetWindowPos(hwnd, NativeScreen.HWND_TOPMOST, _physRect.Left, _physRect.Top,
                _physRect.Width, _physRect.Height, NativeScreen.SWP_NOACTIVATE))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位截图遮罩。");
        }

        internal bool PreviewCursorWindow()
        {
            NativeScreen.POINT cursor;
            if (!IsVisible || !NativeScreen.GetCursorPos(out cursor) || cursor.X < _physRect.Left || cursor.X >= _physRect.Right
                || cursor.Y < _physRect.Top || cursor.Y >= _physRect.Bottom || ActualWidth <= 0 || ActualHeight <= 0)
                return false;

            _latestPointer = new WpfPoint((cursor.X - _physRect.Left) / ScaleX, (cursor.Y - _physRect.Top) / ScaleY);
            UpdateHoverWindow(_latestPointer);
            return true;
        }

        public void ForceClose()
        {
            if (_closing) return;
            _closing = true;
            _pointerFrame.Stop();
            if (_dragOutline != null) _dragOutline.Dispose();
            try { Close(); } catch { }
            try
            {
                if (_rawBmp != null) { _rawBmp.Dispose(); _rawBmp = null; }
            }
            catch { }
        }

        private double ScaleX { get { return ActualWidth > 0 && _rawBmp != null ? _rawBmp.Width / ActualWidth : 1.0; } }
        private double ScaleY { get { return ActualHeight > 0 && _rawBmp != null ? _rawBmp.Height / ActualHeight : 1.0; } }

        private void BuildVisualTree()
        {
            var root = new Grid();

            // 2. Interactive canvas
            _canvas = new Canvas { Background = WpfBrushes.Transparent, ClipToBounds = true };
            _canvas.MouseLeftButtonDown += OnCanvasMouseDown;
            _canvas.MouseMove += OnCanvasMouseMove;
            _canvas.MouseLeave += (s, e) =>
            {
                if (_hasSelection || _isDraggingSelection) return;
                _pointerFrame.Stop();
                _hoverWinRect = null;
                _hoverFrame.Visibility = Visibility.Collapsed;
                _loupeCard.Visibility = Visibility.Collapsed;
                UpdateDimLayout();
            };
            _canvas.MouseLeftButtonUp += OnCanvasMouseUp;
            _canvas.MouseRightButtonUp += OnCanvasRightUp;
            root.Children.Add(_canvas);

            // 3. Annotation preview layer (clipped to selection during render)
            _annotCanvas = new Canvas { IsHitTestVisible = false };
            _canvas.Children.Add(_annotCanvas);

            // 4. Four dark dim panels surrounding selection
            var dimBrush = new SolidColorBrush(WpfColor.FromArgb(0x6E, 0x09, 0x0D, 0x16));
            _dimTop = new WpfRectangle { Fill = dimBrush, IsHitTestVisible = false };
            _dimBottom = new WpfRectangle { Fill = dimBrush, IsHitTestVisible = false };
            _dimLeft = new WpfRectangle { Fill = dimBrush, IsHitTestVisible = false };
            _dimRight = new WpfRectangle { Fill = dimBrush, IsHitTestVisible = false };
            _canvas.Children.Add(_dimTop);
            _canvas.Children.Add(_dimBottom);
            _canvas.Children.Add(_dimLeft);
            _canvas.Children.Add(_dimRight);

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
                    _activeHandle = idx;
                    _dragAnchor = e.GetPosition(_canvas);
                    _dragOrigRect = _selRect;
                    BeginOutline();
                    _canvas.CaptureMouse();
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

            Content = root;
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
                Background = WpfBrushes.White,
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xE2, 0xE8, 0xF0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 5, 6, 5),
                Child = bar,
                Cursor = Cursors.Arrow,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { Color = WpfColors.Black, BlurRadius = 18, ShadowDepth = 4, Opacity = 0.22 }
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
                    Fill = new SolidColorBrush(WpfColor.FromRgb(0x33, 0x41, 0x55))
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
                    CornerRadius = new CornerRadius(9),
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
                Background = WpfBrushes.White,
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xE2, 0xE8, 0xF0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 5, 8, 5),
                Child = subSp,
                Cursor = Cursors.Arrow,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { Color = WpfColors.Black, BlurRadius = 14, ShadowDepth = 3, Opacity = 0.18 }
            };

            SyncSubBarVisuals();
        }

        private Border MakeBarSep()
        {
            return new Border
            {
                Width = 1,
                Height = 18,
                Background = new SolidColorBrush(WpfColor.FromRgb(0xE2, 0xE8, 0xF0)),
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
                Content = MakeSvgIcon(svgData, new SolidColorBrush(WpfColor.FromRgb(0x33, 0x41, 0x55)))
            };
            ApplyFlatButtonTemplate(btn);
            btn.Click += (s, e) => SelectTool(_activeTool == tool ? DrawTool.None : tool);
            _toolButtons[tool] = btn;
            bar.Children.Add(btn);
        }

        private Button MakeActionBtn(string svgData, string tip, string label, bool primary, Action onClick)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var fg = primary ? WpfBrushes.White : new SolidColorBrush(WpfColor.FromRgb(0x33, 0x41, 0x55));
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
                Background = primary ? new SolidColorBrush(WpfColor.FromRgb(0x10, 0xB9, 0x81)) : WpfBrushes.Transparent,
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
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
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
                    ? new SolidColorBrush(WpfColor.FromRgb(0x05, 0x96, 0x69))
                    : new SolidColorBrush(WpfColor.FromRgb(0xF1, 0xF5, 0xF9)),
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
            Cursor = tool == DrawTool.None ? Cursors.Arrow : (tool == DrawTool.Text ? Cursors.IBeam : Cursors.Cross);
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

            WpfPoint pt = e.GetPosition(_canvas);
            CommitActiveTextBox();

            if (!_hasSelection)
            {
                _isDraggingSelection = true;
                _dragAnchor = pt;
                _selRect = new WpfRect(pt, pt);
                BeginOutline();
                _canvas.CaptureMouse();
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
                if (_selRect.Contains(pt))
                {
                    _isMovingSelection = true;
                    _dragAnchor = pt;
                    _dragOrigRect = _selRect;
                    BeginOutline();
                    _canvas.CaptureMouse();
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
            _pointerFrame.Stop();
            _hoverWinRect = null;
            _hoverFrame.Visibility = Visibility.Collapsed;
            _loupeCard.Visibility = Visibility.Collapsed;
            _dimTop.Visibility = _dimBottom.Visibility = _dimLeft.Visibility = _dimRight.Visibility = Visibility.Collapsed;
            _selFrame.Visibility = _sizePill.Visibility = _toolbar.Visibility = _subBar.Visibility = Visibility.Collapsed;
            foreach (var handle in _handles) handle.Visibility = Visibility.Collapsed;
        }

        private void EndOutline()
        {
            _outlineActive = false;
            if (_dragOutline != null) _dragOutline.Hide();
            _dimTop.Visibility = _dimBottom.Visibility = _dimLeft.Visibility = _dimRight.Visibility = Visibility.Visible;
        }

        private void UpdateDragGeometry(WpfPoint point)
        {
            if (_isDraggingSelection)
            {
                var clamped = new WpfPoint(Math.Max(0, Math.Min(ActualWidth, point.X)), Math.Max(0, Math.Min(ActualHeight, point.Y)));
                _selRect = MakeRect(_dragAnchor, clamped);
            }
            else if (_activeHandle >= 0) ResizeSelectionByHandle(_activeHandle, point);
            else if (_isMovingSelection)
            {
                double x = Math.Max(0, Math.Min(ActualWidth - _dragOrigRect.Width, _dragOrigRect.X + point.X - _dragAnchor.X));
                double y = Math.Max(0, Math.Min(ActualHeight - _dragOrigRect.Height, _dragOrigRect.Y + point.Y - _dragAnchor.Y));
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
            _latestPointer = e.GetPosition(_canvas);
            if (_currentAnnot != null) TrackAnnotation(_latestPointer);
            if (_outlineActive || _currentAnnot != null || !_hasSelection)
            {
                if (!_pointerFrame.IsEnabled) _pointerFrame.Start();
            }
            else if (_activeTool == DrawTool.None)
            {
                Cursor = _selRect.Contains(_latestPointer) ? Cursors.SizeAll : Cursors.Arrow;
            }
        }

        private void PaintPointerFrame()
        {
            if (_closing) return;
            if (_outlineActive)
            {
                UpdateDragGeometry(_latestPointer);
                if (_dragOutline == null) _dragOutline = new DragOutline();
                int left = _physRect.Left + (int)Math.Round(_selRect.Left * ScaleX);
                int top = _physRect.Top + (int)Math.Round(_selRect.Top * ScaleY);
                int right = _physRect.Left + (int)Math.Round(_selRect.Right * ScaleX);
                int bottom = _physRect.Top + (int)Math.Round(_selRect.Bottom * ScaleY);
                _dragOutline.Move(left, top, right, bottom);
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
            _pointerFrame.Stop();
            var point = e.GetPosition(_canvas);
            if (_outlineActive)
            {
                UpdateDragGeometry(point);
                EndOutline();
            }
            if (_currentAnnot != null) TrackAnnotation(point);
            if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();

            if (_isDraggingSelection)
            {
                _isDraggingSelection = false;
                _loupeCard.Visibility = Visibility.Collapsed;

                if (_selRect.Width < 5 && _selRect.Height < 5)
                {
                    var target = FindWindowAt(e.GetPosition(_canvas));
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

                UpdateDimLayout();
                UpdateToolbarPositions();
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
                Cursor = Cursors.Cross;
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
            Canvas.SetLeft(_strokeHudWrap, Math.Max(10, _selRect.Left + _selRect.Width / 2 - 48));
            Canvas.SetTop(_strokeHudWrap, Math.Max(10, _selRect.Top + 10));
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (s, ev) => { t.Stop(); _strokeHudWrap.Visibility = Visibility.Collapsed; };
            t.Start();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_hasSelection && !_outlineActive && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
                UpdateLoupe(Mouse.GetPosition(_canvas));
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
                UpdateLoupe(Mouse.GetPosition(_canvas));
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

        private WpfRect? FindWindowAt(WpfPoint point)
        {
            double px = point.X * ScaleX;
            double py = point.Y * ScaleY;
            foreach (var rect in _windowRects)
            {
                if (px >= rect.Left && px < rect.Right && py >= rect.Top && py < rect.Bottom)
                    return new WpfRect(rect.Left / ScaleX, rect.Top / ScaleY, rect.Width / ScaleX, rect.Height / ScaleY);
            }
            return null;
        }

        private void UpdateHoverWindow(WpfPoint pt)
        {
            var match = FindWindowAt(pt);
            if (match == _hoverWinRect && (!match.HasValue || _hoverFrame.Visibility == Visibility.Visible)) return;
            _hoverWinRect = match;
            UpdateDimLayout();
            if (match.HasValue)
            {
                var r = match.Value;
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
            if (_rawBmp == null || ActualWidth <= 0 || ActualHeight <= 0) return;

            int px = Math.Max(0, Math.Min(_rawBmp.Width - 1, (int)Math.Round(pt.X * ScaleX)));
            int py = Math.Max(0, Math.Min(_rawBmp.Height - 1, (int)Math.Round(pt.Y * ScaleY)));

            GdiColor c = _rawBmp.GetPixel(px, py);
            _lastHexColor = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            _loupeColorDot.Background = new SolidColorBrush(WpfColor.FromRgb(c.R, c.G, c.B));
            if (_isDraggingSelection)
            {
                int sw = (int)Math.Round(_selRect.Width * ScaleX);
                int sh = (int)Math.Round(_selRect.Height * ScaleY);
                _loupeInfoText.Text = string.Format("{0}×{1}  {2}", sw, sh, _lastHexColor);
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

            double lx = pt.X + 18;
            double ly = pt.Y + 18;
            if (lx + 145 > ActualWidth) lx = pt.X - 155;
            if (ly + 170 > ActualHeight) ly = pt.Y - 175;
            Canvas.SetLeft(_loupeCard, Math.Max(6, lx));
            Canvas.SetTop(_loupeCard, Math.Max(6, ly));
            _loupeCard.Visibility = Visibility.Visible;
        }

        private void UpdateDimLayout()
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (_outlineActive || w <= 0 || h <= 0) return;

            bool selecting = _hasSelection || _isDraggingSelection;
            WpfRect highlight = selecting ? _selRect : (_hoverWinRect ?? new WpfRect(0, h, 0, 0));

            double l = Math.Max(0, Math.Min(w, highlight.Left));
            double t = Math.Max(0, Math.Min(h, highlight.Top));
            double r = Math.Max(l, Math.Min(w, highlight.Right));
            double b = Math.Max(t, Math.Min(h, highlight.Bottom));

            SetRect(_dimTop, 0, 0, w, t);
            SetRect(_dimBottom, 0, b, w, h - b);
            SetRect(_dimLeft, 0, t, l, b - t);
            SetRect(_dimRight, r, t, w - r, b - t);
            if (!selecting) return;

            _selFrame.Width = Math.Max(0, r - l);
            _selFrame.Height = Math.Max(0, b - t);
            Canvas.SetLeft(_selFrame, l);
            Canvas.SetTop(_selFrame, t);
            _selFrame.Visibility = Visibility.Visible;

            // Size badge
            int pw = Math.Max(1, (int)Math.Round((r - l) * ScaleX));
            int ph = Math.Max(1, (int)Math.Round((b - t) * ScaleY));
            _sizeText.Text = string.Format("{0} × {1}", pw, ph);
            double py = t - 26 >= 6 ? t - 26 : t + 6;
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
                    Canvas.SetLeft(_handles[i], pts[i].X - 4.5);
                    Canvas.SetTop(_handles[i], pts[i].Y - 4.5);
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

            double left = _selRect.Right - tbW;
            if (left < 8) left = Math.Max(8, _selRect.Left);
            if (left + tbW > ActualWidth - 8) left = Math.Max(8, ActualWidth - tbW - 8);

            bool below = _selRect.Bottom + tbH + 46 <= ActualHeight;
            double top;
            if (below) top = _selRect.Bottom + 8;
            else if (_selRect.Top - tbH - 46 >= 8) top = _selRect.Top - tbH - 8;
            else top = Math.Max(8, _selRect.Bottom - tbH - 10);

            Canvas.SetLeft(_toolbar, left);
            Canvas.SetTop(_toolbar, top);

            bool needSub = _activeTool == DrawTool.Rect || _activeTool == DrawTool.Arrow || _activeTool == DrawTool.Brush || _activeTool == DrawTool.Text;
            _subBar.Visibility = needSub ? Visibility.Visible : Visibility.Collapsed;
            if (needSub)
            {
                _subBar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                double sbW = _subBar.DesiredSize.Width;
                double sbH = _subBar.DesiredSize.Height;
                double sLeft = Math.Max(8, Math.Min(ActualWidth - sbW - 8, left));
                double sTop = (top + tbH + 6 + sbH <= ActualHeight - 4) ? (top + tbH + 6) : Math.Max(8, top - sbH - 6);
                Canvas.SetLeft(_subBar, sLeft);
                Canvas.SetTop(_subBar, sTop);
            }

            UpdateDimLayout();
        }

        private void ResizeSelectionByHandle(int idx, WpfPoint pt)
        {
            double l = _dragOrigRect.Left, t = _dragOrigRect.Top, r = _dragOrigRect.Right, b = _dragOrigRect.Bottom;
            double x = Math.Max(0, Math.Min(ActualWidth, pt.X));
            double y = Math.Max(0, Math.Min(ActualHeight, pt.Y));

            if (idx == 0 || idx == 6 || idx == 7) l = Math.Min(x, r - 12);
            if (idx == 2 || idx == 3 || idx == 4) r = Math.Max(x, l + 12);
            if (idx == 0 || idx == 1 || idx == 2) t = Math.Min(y, b - 12);
            if (idx == 4 || idx == 5 || idx == 6) b = Math.Max(y, t + 12);
            _selRect = new WpfRect(l, t, Math.Max(12, r - l), Math.Max(12, b - t));
        }

        private static void SetRect(WpfRectangle rect, double x, double y, double w, double h)
        {
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            rect.Width = Math.Max(0, w);
            rect.Height = Math.Max(0, h);
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
            int sx = Math.Max(0, (int)Math.Floor(dipRect.Left * ScaleX));
            int sy = Math.Max(0, (int)Math.Floor(dipRect.Top * ScaleY));
            int sw = Math.Min(_rawBmp.Width - sx, Math.Max(1, (int)Math.Ceiling(dipRect.Width * ScaleX)));
            int sh = Math.Min(_rawBmp.Height - sy, Math.Max(1, (int)Math.Ceiling(dipRect.Height * ScaleY)));
            if (sw <= 1 || sh <= 1) return null;

            int block = Math.Max(8, (int)Math.Round(10 * ScaleX));
            int gw = Math.Max(1, sw / block);
            int gh = Math.Max(1, sh / block);
            using (var tiny = new Bitmap(gw, gh))
            using (var g = Graphics.FromImage(tiny))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(_rawBmp, new GdiRect(0, 0, gw, gh), new GdiRect(sx, sy, sw, sh), GraphicsUnit.Pixel);
                return NativeScreen.ToBitmapSource(tiny);
            }
        }

        // ==================== HIGH-RES COMPOSITING & OUTPUT ACTIONS ====================

        private Bitmap BuildFinalBitmap()
        {
            CommitActiveTextBox();
            int cx = Math.Max(0, (int)Math.Round(_selRect.Left * ScaleX));
            int cy = Math.Max(0, (int)Math.Round(_selRect.Top * ScaleY));
            int cw = Math.Min(_rawBmp.Width - cx, Math.Max(1, (int)Math.Round(_selRect.Width * ScaleX)));
            int ch = Math.Min(_rawBmp.Height - cy, Math.Max(1, (int)Math.Round(_selRect.Height * ScaleY)));

            var result = ScreenshotSession.CaptureSelection(new NativeScreen.RECT
            {
                Left = _physRect.Left + cx,
                Top = _physRect.Top + cy,
                Right = _physRect.Left + cx + cw,
                Bottom = _physRect.Top + cy + ch
            });
            using (var g = Graphics.FromImage(result))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                double sx = ScaleX;
                double sy = ScaleY;
                double ox = _selRect.Left;
                double oy = _selRect.Top;

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
                        int mx = Math.Max(0, (int)Math.Floor((r.Left - ox) * sx));
                        int my = Math.Max(0, (int)Math.Floor((r.Top - oy) * sy));
                        int mw = Math.Min(cw - mx, Math.Max(1, (int)Math.Ceiling(r.Width * sx)));
                        int mh = Math.Min(ch - my, Math.Max(1, (int)Math.Ceiling(r.Height * sy)));
                        int block = Math.Max(8, (int)Math.Round(10 * sx));
                        int gw = Math.Max(1, mw / block);
                        int gh = Math.Max(1, mh / block);
                        using (var tiny = new Bitmap(gw, gh))
                        using (var gt = Graphics.FromImage(tiny))
                        {
                            gt.InterpolationMode = InterpolationMode.Bilinear;
                            gt.DrawImage(result, new GdiRect(0, 0, gw, gh), new GdiRect(mx, my, mw, mh), GraphicsUnit.Pixel);
                            var prevInterp = g.InterpolationMode;
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.DrawImage(tiny, new GdiRect(mx, my, mw, mh), new GdiRect(0, 0, gw, gh), GraphicsUnit.Pixel);
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
            return result;
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
    /// Modern floating dialog showing Windows 11 local offline OCR results.
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
            Background = WpfBrushes.White;
            FontFamily = UiHelpers.MainFont;
            Icon = UiHelpers.LoadWindowIcon();

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _statusText = new TextBlock
            {
                Text = "正在通过 Windows 11 本机引擎识别中英文文字...",
                FontSize = 12.5,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x64, 0x74, 0x8B)),
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
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xE2, 0xE8, 0xF0)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(WpfColor.FromRgb(0xF8, 0xFA, 0xFC)),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x0F, 0x17, 0x2A))
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
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xCB, 0xD5, 0xE1)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            closeBtn.Click += (s, e) => Close();

            var copyBtn = new Button
            {
                Content = "复制全部文字",
                Padding = new Thickness(16, 6, 16, 6),
                Background = new SolidColorBrush(WpfColor.FromRgb(0x10, 0xB9, 0x81)),
                Foreground = WpfBrushes.White,
                BorderThickness = new Thickness(0),
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
            Bitmap bmpCopy = _bmp;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string text = OcrService.RecognizeBitmap(bmpCopy);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            _statusText.Text = "未在选区内识别到清晰文字";
                            _textBox.Text = "";
                        }
                        else
                        {
                            _statusText.Text = string.Format("识别完成 · 共 {0} 个字符（可直接编辑或一键复制）", text.Length);
                            _textBox.Text = text;
                            _textBox.Focus();
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _statusText.Text = "识别失败：" + ex.Message;
                    }));
                }
            });
        }
    }
}
