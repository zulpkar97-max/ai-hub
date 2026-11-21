using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace CodexLauncher
{
    internal static class VoiceRecorderShortcut
    {
        internal const string Display = "Ctrl + Win + A";
        private const ushort VkControl = 0x11, VkShift = 0x10, VkAlt = 0x12;
        private const ushort VkLeftControl = 0xA2, VkLeftWin = 0x5B, VkRightWin = 0x5C, VkA = 0x41;
        private const uint KeyUp = 0x0002, ExtendedKey = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey, ScanCode;
            public uint Flags, Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputData
        {
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputData Data;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private static Input Key(ushort virtualKey, uint flags = 0)
        {
            return new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags } } };
        }

        internal static void Send()
        {
            foreach (var key in new[] { VkControl, VkShift, VkAlt, VkLeftWin, VkRightWin, VkA })
            {
                if ((GetAsyncKeyState(key) & 0x8000) != 0)
                    throw new InvalidOperationException("请松开快捷键相关按键后再打开录音笔。");
            }

            var inputs = new[]
            {
                Key(VkLeftControl), Key(VkLeftWin, ExtendedKey), Key(VkA),
                Key(VkA, KeyUp), Key(VkLeftWin, ExtendedKey | KeyUp), Key(VkLeftControl, KeyUp)
            };
            int size = Marshal.SizeOf(typeof(Input));
            uint sent = SendInput((uint)inputs.Length, inputs, size);
            if (sent == inputs.Length) return;

            int error = Marshal.GetLastWin32Error();
            var release = new List<Input>();
            if (sent == 3) release.Add(inputs[3]);
            if (sent >= 2 && sent < 5) release.Add(inputs[4]);
            if (sent >= 1) release.Add(inputs[5]);
            if (release.Count > 0) SendInput((uint)release.Count, release.ToArray(), size);
            throw new Win32Exception(error, "无法发送微信输入法的录音笔快捷键。");
        }
    }

    public static class UiHelpers
    {
        public static readonly FontFamily MainFont = new FontFamily("Microsoft YaHei UI, Segoe UI Variable, Segoe UI, sans-serif");

        private static readonly Dictionary<string, ImageSource> IconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static SolidColorBrush BrushFromHex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        // Refined Light-Mode Palette
        public static readonly SolidColorBrush SidebarBg = BrushFromHex("#F7F7F8");
        public static readonly SolidColorBrush MainBg = BrushFromHex("#FFFFFF");
        public static readonly SolidColorBrush SubtleBg = BrushFromHex("#FFFFFF");
        public static readonly SolidColorBrush HoverBg = BrushFromHex("#F0F1F4");

        public static readonly SolidColorBrush Hairline = BrushFromHex("#E4E6EB");
        public static readonly SolidColorBrush BorderStrong = BrushFromHex("#232B35");

        public static readonly SolidColorBrush TextPrimary = BrushFromHex("#141920");
        public static readonly SolidColorBrush TextSecondary = BrushFromHex("#505B6A");

        public static readonly SolidColorBrush PrimaryBg = BrushFromHex("#1D242E");
        public static readonly SolidColorBrush PrimaryHover = BrushFromHex("#2E3745");

        public static readonly SolidColorBrush WarnText = BrushFromHex("#B45C08");
        public static readonly SolidColorBrush WarnBg = BrushFromHex("#FFF5E7");

        public static readonly SolidColorBrush DangerText = BrushFromHex("#C52828");
        public static readonly SolidColorBrush DangerBg = BrushFromHex("#FDF1F1");
        public static readonly SolidColorBrush DangerBorder = BrushFromHex("#C52828");

        public static void ApplySeamlessLightTitlebar(Window win)
        {
            win.SourceInitialized += (s, e) =>
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(win).Handle;
                    int captionColor = 0x00F8F7F7;
                    DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
                    int textColor = 0x00201914;
                    DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));
                    int borderColor = 0x00352B23;
                    DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));
                }
                catch { }
            };
        }

        public static ImageSource LoadWindowIcon()
        {
            return LoadCrispBrandPng();
        }

        public static ImageSource LoadCrispBrandPng()
        {
            return LoadPngOrBestIco("__AIHUB_BRAND_256__", new[]
            {
                CoreService.PermanentPng256,
                CoreService.BundledPng256,
                CoreService.PermanentIcon,
                CoreService.BundledIcon
            });
        }

        private static ImageSource LoadPngOrBestIco(string cacheKey, string[] candidates)
        {
            ImageSource cached;
            if (IconCache.TryGetValue(cacheKey, out cached)) return cached;

            foreach (string path in candidates)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                try
                {
                    if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var fs = File.OpenRead(path))
                        {
                            var decoder = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            BitmapFrame best = decoder.Frames[0];
                            foreach (var f in decoder.Frames)
                                if (f.PixelWidth > best.PixelWidth) best = f;
                            best.Freeze();
                            IconCache[cacheKey] = best;
                            return best;
                        }
                    }
                    else
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(path, UriKind.Absolute);
                        bmp.EndInit();
                        bmp.Freeze();
                        IconCache[cacheKey] = bmp;
                        return bmp;
                    }
                }
                catch { }
            }
            return null;
        }

        public static Button MakeMenuRowButton(string label, Brush dotColor, string tone, bool isBold)
        {
            Brush textBrush = TextPrimary;
            Brush hoverBrush = HoverBg;
            if (tone == "danger") { textBrush = DangerText; hoverBrush = DangerBg; }
            else if (tone == "warn") { textBrush = WarnText; hoverBrush = WarnBg; }

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (dotColor != null)
            {
                row.Children.Add(new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = dotColor,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = MainFont,
                FontSize = 12.5,
                FontWeight = isBold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            var btn = new Button
            {
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 7, 10, 7),
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null
            };

            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "mbd";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "mbd"));
            tpl.Triggers.Add(hov);
            btn.Template = tpl;
            return btn;
        }
    }
}
