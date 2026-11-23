using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

        // Refined Light-Mode Palette (v1.2 Warm-Ink 0px Lineart & AA Vermilion)
        public static readonly SolidColorBrush SidebarBg = BrushFromHex("#FFFFFF");
        public static readonly SolidColorBrush MainBg = BrushFromHex("#FFFFFF");
        public static readonly SolidColorBrush SubtleBg = BrushFromHex("#FAF8F5");
        public static readonly SolidColorBrush HoverBg = BrushFromHex("#F3F1EC");

        public static readonly SolidColorBrush Hairline = BrushFromHex("#E5E3DC");
        public static readonly SolidColorBrush BorderStrong = BrushFromHex("#1A1917");

        public static readonly SolidColorBrush TextPrimary = BrushFromHex("#1A1917");
        public static readonly SolidColorBrush TextSecondary = BrushFromHex("#57534C");
        public static readonly SolidColorBrush TextMuted = BrushFromHex("#6F6B63");

        public static readonly SolidColorBrush PrimaryBg = BrushFromHex("#1A1917");
        public static readonly SolidColorBrush PrimaryHover = BrushFromHex("#3C3934");

        public static readonly SolidColorBrush OkAccent = BrushFromHex("#14A06D");
        public static readonly SolidColorBrush WarnText = BrushFromHex("#9A5005");
        public static readonly SolidColorBrush WarnBg = BrushFromHex("#E38D17");

        public static readonly SolidColorBrush DangerText = BrushFromHex("#A6261E");
        public static readonly SolidColorBrush DangerBg = BrushFromHex("#C24840");
        public static readonly SolidColorBrush DangerHover = BrushFromHex("#A6261E");
        public static readonly SolidColorBrush DangerBorder = BrushFromHex("#C24840");

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
            Brush textBrush = tone == "danger" ? TextSecondary : TextPrimary;
            Brush hoverBg = tone == "danger" ? DangerBg : HoverBg;
            Brush hoverText = tone == "danger" ? Brushes.White : TextPrimary;

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (dotColor != null)
            {
                row.Children.Add(new Rectangle
                {
                    Width = 6,
                    Height = 6,
                    Fill = dotColor,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            var tb = new TextBlock
            {
                Text = label,
                FontFamily = MainFont,
                FontSize = 12,
                FontWeight = isBold ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(tb);

            var btn = new Button
            {
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 7, 9, 7),
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null
            };

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
            btn.Style = style;
            tb.SetBinding(TextBlock.ForegroundProperty, new Binding { Path = new PropertyPath(Control.ForegroundProperty), Source = btn });

            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "mbd";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "mbd"));
            hov.Setters.Add(new Setter(Border.BorderBrushProperty, BorderStrong, "mbd"));
            hov.Setters.Add(new Setter(Control.ForegroundProperty, hoverText));
            tpl.Triggers.Add(hov);
            btn.Template = tpl;
            return btn;
        }
    }

    public static class TrayIconFactory
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private struct Item
        {
            public bool IsPoly;
            public string Role;
            public float[] Data;
            public Item(bool isPoly, string role, float[] data)
            {
                IsPoly = isPoly;
                Role = role;
                Data = data;
            }
        }

        private static readonly Dictionary<string, System.Drawing.Icon> Cache =
            new Dictionary<string, System.Drawing.Icon>(StringComparer.OrdinalIgnoreCase);

        private static readonly Item[] MicroItems = new[]
        {
            new Item(true, "#FFFFFF", new[] { 50.0f, 7.07f, 62.391f, 14.225f, 50.0f, 21.38f, 37.609f, 14.225f }),
            new Item(false, null, new[] { 50.0f, 7.07f, 62.391f, 14.225f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 21.38f, 37.609f, 14.225f, 3.074f }),
            new Item(false, null, new[] { 37.609f, 14.225f, 50.0f, 7.07f, 3.074f }),
            new Item(true, "#ECEAE4", new[] { 12.826f, 71.465f, 12.826f, 57.155f, 25.217f, 64.31f, 25.217f, 78.62f }),
            new Item(false, null, new[] { 12.826f, 71.465f, 12.826f, 57.155f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 64.31f, 25.217f, 78.62f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 78.62f, 12.826f, 71.465f, 3.074f }),
            new Item(true, "#D2CFC7", new[] { 87.174f, 71.465f, 74.783f, 78.62f, 74.783f, 64.31f, 87.174f, 57.155f }),
            new Item(false, null, new[] { 87.174f, 71.465f, 74.783f, 78.62f, 3.074f }),
            new Item(false, null, new[] { 74.783f, 64.31f, 87.174f, 57.155f, 3.074f }),
            new Item(false, null, new[] { 87.174f, 57.155f, 87.174f, 71.465f, 3.074f }),
            new Item(true, "#FFFFFF", new[] { 37.609f, 14.225f, 50.0f, 21.38f, 37.609f, 28.535f, 25.217f, 21.38f }),
            new Item(false, null, new[] { 37.609f, 14.225f, 50.0f, 21.38f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 21.38f, 37.609f, 28.535f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 21.38f, 37.609f, 14.225f, 3.074f }),
            new Item(true, "#D2CFC7", new[] { 50.0f, 35.69f, 37.609f, 42.845f, 37.609f, 28.535f, 50.0f, 21.38f }),
            new Item(false, null, new[] { 50.0f, 35.69f, 37.609f, 42.845f, 3.074f }),
            new Item(false, null, new[] { 37.609f, 28.535f, 50.0f, 21.38f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 21.38f, 50.0f, 35.69f, 3.074f }),
            new Item(true, "#D2CFC7", new[] { 37.609f, 57.155f, 25.217f, 64.31f, 25.217f, 50.0f, 37.609f, 42.845f }),
            new Item(false, null, new[] { 37.609f, 57.155f, 25.217f, 64.31f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 64.31f, 25.217f, 50.0f, 3.074f }),
            new Item(false, null, new[] { 37.609f, 42.845f, 37.609f, 57.155f, 3.074f }),
            new Item(true, "#ECEAE4", new[] { 12.826f, 57.155f, 12.826f, 42.845f, 25.217f, 50.0f, 25.217f, 64.31f }),
            new Item(false, null, new[] { 12.826f, 57.155f, 12.826f, 42.845f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 50.0f, 25.217f, 64.31f, 3.074f }),
            new Item(true, "#FFFFFF", new[] { 62.391f, 14.225f, 74.783f, 21.38f, 62.391f, 28.535f, 50.0f, 21.38f }),
            new Item(false, null, new[] { 62.391f, 14.225f, 74.783f, 21.38f, 3.074f }),
            new Item(false, null, new[] { 62.391f, 28.535f, 50.0f, 21.38f, 3.074f }),
            new Item(true, "#ECEAE4", new[] { 50.0f, 35.69f, 50.0f, 21.38f, 62.391f, 28.535f, 62.391f, 42.845f }),
            new Item(false, null, new[] { 50.0f, 35.69f, 50.0f, 21.38f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 21.38f, 62.391f, 28.535f, 3.074f }),
            new Item(false, null, new[] { 62.391f, 42.845f, 50.0f, 35.69f, 3.074f }),
            new Item(true, "core-top", new[] { 50.0f, 35.69f, 62.391f, 42.845f, 50.0f, 50.0f, 37.609f, 42.845f }),
            new Item(false, null, new[] { 50.0f, 35.69f, 62.391f, 42.845f, 3.074f }),
            new Item(false, null, new[] { 62.391f, 42.845f, 50.0f, 50.0f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 50.0f, 37.609f, 42.845f, 3.074f }),
            new Item(false, null, new[] { 37.609f, 42.845f, 50.0f, 35.69f, 3.074f }),
            new Item(true, "core-right", new[] { 62.391f, 57.155f, 50.0f, 64.31f, 50.0f, 50.0f, 62.391f, 42.845f }),
            new Item(false, null, new[] { 62.391f, 57.155f, 50.0f, 64.31f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 64.31f, 50.0f, 50.0f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 50.0f, 62.391f, 42.845f, 3.074f }),
            new Item(false, null, new[] { 62.391f, 42.845f, 62.391f, 57.155f, 3.074f }),
            new Item(true, "core-left", new[] { 37.609f, 57.155f, 37.609f, 42.845f, 50.0f, 50.0f, 50.0f, 64.31f }),
            new Item(false, null, new[] { 37.609f, 57.155f, 37.609f, 42.845f, 3.074f }),
            new Item(false, null, new[] { 37.609f, 42.845f, 50.0f, 50.0f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 50.0f, 50.0f, 64.31f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 64.31f, 37.609f, 57.155f, 3.074f }),
            new Item(true, "#FFFFFF", new[] { 37.609f, 57.155f, 50.0f, 64.31f, 37.609f, 71.465f, 25.217f, 64.31f }),
            new Item(false, null, new[] { 37.609f, 57.155f, 50.0f, 64.31f, 3.074f }),
            new Item(false, null, new[] { 37.609f, 71.465f, 25.217f, 64.31f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 64.31f, 37.609f, 57.155f, 3.074f }),
            new Item(true, "#ECEAE4", new[] { 25.217f, 78.62f, 25.217f, 64.31f, 37.609f, 71.465f, 37.609f, 85.775f }),
            new Item(false, null, new[] { 25.217f, 78.62f, 25.217f, 64.31f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 64.31f, 37.609f, 71.465f, 3.074f }),
            new Item(false, null, new[] { 37.609f, 85.775f, 25.217f, 78.62f, 3.074f }),
            new Item(true, "#D2CFC7", new[] { 87.174f, 57.155f, 74.783f, 64.31f, 74.783f, 50.0f, 87.174f, 42.845f }),
            new Item(false, null, new[] { 87.174f, 57.155f, 74.783f, 64.31f, 3.074f }),
            new Item(false, null, new[] { 74.783f, 64.31f, 74.783f, 50.0f, 3.074f }),
            new Item(false, null, new[] { 87.174f, 42.845f, 87.174f, 57.155f, 3.074f }),
            new Item(true, "#ECEAE4", new[] { 62.391f, 57.155f, 62.391f, 42.845f, 74.783f, 50.0f, 74.783f, 64.31f }),
            new Item(false, null, new[] { 62.391f, 57.155f, 62.391f, 42.845f, 3.074f }),
            new Item(false, null, new[] { 74.783f, 50.0f, 74.783f, 64.31f, 3.074f }),
            new Item(false, null, new[] { 74.783f, 64.31f, 62.391f, 57.155f, 3.074f }),
            new Item(true, "#FFFFFF", new[] { 62.391f, 57.155f, 74.783f, 64.31f, 62.391f, 71.465f, 50.0f, 64.31f }),
            new Item(false, null, new[] { 62.391f, 57.155f, 74.783f, 64.31f, 3.074f }),
            new Item(false, null, new[] { 74.783f, 64.31f, 62.391f, 71.465f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 64.31f, 62.391f, 57.155f, 3.074f }),
            new Item(true, "#D2CFC7", new[] { 74.783f, 78.62f, 62.391f, 85.775f, 62.391f, 71.465f, 74.783f, 64.31f }),
            new Item(false, null, new[] { 74.783f, 78.62f, 62.391f, 85.775f, 3.074f }),
            new Item(false, null, new[] { 62.391f, 71.465f, 74.783f, 64.31f, 3.074f }),
            new Item(true, "#FFFFFF", new[] { 25.217f, 21.38f, 37.609f, 28.535f, 25.217f, 35.69f, 12.826f, 28.535f }),
            new Item(false, null, new[] { 37.609f, 28.535f, 25.217f, 35.69f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 35.69f, 12.826f, 28.535f, 3.074f }),
            new Item(false, null, new[] { 12.826f, 28.535f, 25.217f, 21.38f, 3.074f }),
            new Item(true, "#D2CFC7", new[] { 37.609f, 42.845f, 25.217f, 50.0f, 25.217f, 35.69f, 37.609f, 28.535f }),
            new Item(false, null, new[] { 25.217f, 50.0f, 25.217f, 35.69f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 35.69f, 37.609f, 28.535f, 3.074f }),
            new Item(true, "#ECEAE4", new[] { 12.826f, 42.845f, 12.826f, 28.535f, 25.217f, 35.69f, 25.217f, 50.0f }),
            new Item(false, null, new[] { 12.826f, 42.845f, 12.826f, 28.535f, 3.074f }),
            new Item(false, null, new[] { 12.826f, 28.535f, 25.217f, 35.69f, 3.074f }),
            new Item(false, null, new[] { 25.217f, 35.69f, 25.217f, 50.0f, 3.074f }),
            new Item(true, "#FFFFFF", new[] { 74.783f, 21.38f, 87.174f, 28.535f, 74.783f, 35.69f, 62.391f, 28.535f }),
            new Item(false, null, new[] { 74.783f, 21.38f, 87.174f, 28.535f, 3.074f }),
            new Item(false, null, new[] { 87.174f, 28.535f, 74.783f, 35.69f, 3.074f }),
            new Item(false, null, new[] { 74.783f, 35.69f, 62.391f, 28.535f, 3.074f }),
            new Item(true, "#D2CFC7", new[] { 87.174f, 42.845f, 74.783f, 50.0f, 74.783f, 35.69f, 87.174f, 28.535f }),
            new Item(false, null, new[] { 74.783f, 50.0f, 74.783f, 35.69f, 3.074f }),
            new Item(false, null, new[] { 74.783f, 35.69f, 87.174f, 28.535f, 3.074f }),
            new Item(false, null, new[] { 87.174f, 28.535f, 87.174f, 42.845f, 3.074f }),
            new Item(true, "#ECEAE4", new[] { 62.391f, 42.845f, 62.391f, 28.535f, 74.783f, 35.69f, 74.783f, 50.0f }),
            new Item(false, null, new[] { 62.391f, 28.535f, 74.783f, 35.69f, 3.074f }),
            new Item(false, null, new[] { 74.783f, 35.69f, 74.783f, 50.0f, 3.074f }),
            new Item(true, "#FFFFFF", new[] { 50.0f, 64.31f, 62.391f, 71.465f, 50.0f, 78.62f, 37.609f, 71.465f }),
            new Item(false, null, new[] { 62.391f, 71.465f, 50.0f, 78.62f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 78.62f, 37.609f, 71.465f, 3.074f }),
            new Item(true, "#D2CFC7", new[] { 62.391f, 85.775f, 50.0f, 92.93f, 50.0f, 78.62f, 62.391f, 71.465f }),
            new Item(false, null, new[] { 62.391f, 85.775f, 50.0f, 92.93f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 92.93f, 50.0f, 78.62f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 78.62f, 62.391f, 71.465f, 3.074f }),
            new Item(true, "#ECEAE4", new[] { 37.609f, 85.775f, 37.609f, 71.465f, 50.0f, 78.62f, 50.0f, 92.93f }),
            new Item(false, null, new[] { 37.609f, 71.465f, 50.0f, 78.62f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 78.62f, 50.0f, 92.93f, 3.074f }),
            new Item(false, null, new[] { 50.0f, 92.93f, 37.609f, 85.775f, 3.074f })
        };

        public static System.Drawing.Icon GetStatusIcon(string state)
        {
            string key = string.IsNullOrEmpty(state) ? "normal" : state;
            System.Drawing.Icon cached;
            if (Cache.TryGetValue(key, out cached)) return cached;

            string topHex = "#F09C24", leftHex = "#D4810F", rightHex = "#AD6507";
            if (key == "danger") { topHex = "#D9544A"; leftHex = "#C24840"; rightHex = "#992A22"; }
            else if (key == "warn") { topHex = "#F09C24"; leftHex = "#D4810F"; rightHex = "#AD6507"; }
            else if (key == "idle") { topHex = "#BDB9B0"; leftHex = "#9E9A91"; rightHex = "#7F7C74"; }

            const int size = 32;
            const int ss = 4;
            const int hi = size * ss;
            using (var bmpHi = new System.Drawing.Bitmap(hi, hi, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var bmpOut = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmpHi))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.Clear(System.Drawing.ColorTranslator.FromHtml("#FAF9F6"));

                    float s = hi / 100.0f;
                    var ink = System.Drawing.ColorTranslator.FromHtml("#1A1917");
                    foreach (var it in MicroItems)
                    {
                        if (it.IsPoly)
                        {
                            string hex = it.Role == "core-top" ? topHex
                                : (it.Role == "core-left" ? leftHex : (it.Role == "core-right" ? rightHex : it.Role));
                            var col = System.Drawing.ColorTranslator.FromHtml(hex);
                            var pts = new[]
                            {
                                new System.Drawing.PointF(it.Data[0] * s, it.Data[1] * s),
                                new System.Drawing.PointF(it.Data[2] * s, it.Data[3] * s),
                                new System.Drawing.PointF(it.Data[4] * s, it.Data[5] * s),
                                new System.Drawing.PointF(it.Data[6] * s, it.Data[7] * s)
                            };
                            using (var br = new System.Drawing.SolidBrush(col)) g.FillPolygon(br, pts);
                            using (var sp = new System.Drawing.Pen(col, 0.4f * s)) g.DrawPolygon(sp, pts);
                        }
                        else
                        {
                            using (var pen = new System.Drawing.Pen(ink, it.Data[4] * s))
                            {
                                pen.StartCap = System.Drawing.Drawing2D.LineCap.Square;
                                pen.EndCap = System.Drawing.Drawing2D.LineCap.Square;
                                g.DrawLine(pen, it.Data[0] * s, it.Data[1] * s, it.Data[2] * s, it.Data[3] * s);
                            }
                        }
                    }
                }

                using (var gOut = System.Drawing.Graphics.FromImage(bmpOut))
                {
                    gOut.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    gOut.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    gOut.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    gOut.DrawImage(bmpHi, 0, 0, size, size);
                    using (var borderBrush = new System.Drawing.SolidBrush(System.Drawing.ColorTranslator.FromHtml("#1A1917")))
                    {
                        gOut.FillRectangle(borderBrush, 0, 0, size, 1);
                        gOut.FillRectangle(borderBrush, 0, size - 1, size, 1);
                        gOut.FillRectangle(borderBrush, 0, 0, 1, size);
                        gOut.FillRectangle(borderBrush, size - 1, 0, 1, size);
                    }
                }

                IntPtr hIcon = bmpOut.GetHicon();
                try
                {
                    using (var temp = System.Drawing.Icon.FromHandle(hIcon))
                    {
                        var cloned = (System.Drawing.Icon)temp.Clone();
                        Cache[key] = cloned;
                        return cloned;
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }
    }
}
