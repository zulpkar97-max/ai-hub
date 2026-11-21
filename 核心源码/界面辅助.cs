using System;
using System.Collections.Generic;
using System.Drawing;
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
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexLauncher
{
    public static class UiHelpers
    {
        public static readonly FontFamily MainFont = new FontFamily("Microsoft YaHei UI, Segoe UI Variable, Segoe UI, sans-serif");
        public static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI, monospace");

        private static readonly Dictionary<string, ImageSource> IconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public static SolidColorBrush BrushFromHex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        // Refined Light-Mode Palette
        public static readonly SolidColorBrush SidebarBg = BrushFromHex("#F7F7F8");
        public static readonly SolidColorBrush MainBg = BrushFromHex("#FFFFFF");
        public static readonly SolidColorBrush SubtleBg = BrushFromHex("#FAFAFB");
        public static readonly SolidColorBrush HoverBg = BrushFromHex("#F1F2F4");
        public static readonly SolidColorBrush ActiveNavBg = BrushFromHex("#EAECEF");

        public static readonly SolidColorBrush Hairline = BrushFromHex("#EAECEF");
        public static readonly SolidColorBrush BorderSubtle = BrushFromHex("#E2E4E9");
        public static readonly SolidColorBrush BorderStrong = BrushFromHex("#D0D4DC");

        public static readonly SolidColorBrush TextPrimary = BrushFromHex("#111827");
        public static readonly SolidColorBrush TextSecondary = BrushFromHex("#4B5563");
        public static readonly SolidColorBrush TextMuted = BrushFromHex("#8A909E");

        public static readonly SolidColorBrush PrimaryBg = BrushFromHex("#18181B");
        public static readonly SolidColorBrush PrimaryHover = BrushFromHex("#2E2E33");

        public static readonly SolidColorBrush AccentBg = BrushFromHex("#0F766E");
        public static readonly SolidColorBrush AccentHover = BrushFromHex("#115E59");

        public static readonly SolidColorBrush OkText = BrushFromHex("#047857");
        public static readonly SolidColorBrush OkBg = BrushFromHex("#ECFDF5");
        public static readonly SolidColorBrush OkBorder = BrushFromHex("#BBF7D0");
        public static readonly SolidColorBrush OkRing = BrushFromHex("#10B981");

        public static readonly SolidColorBrush WarnText = BrushFromHex("#B45309");
        public static readonly SolidColorBrush WarnBg = BrushFromHex("#FFFBEB");
        public static readonly SolidColorBrush WarnBorder = BrushFromHex("#FDE68A");
        public static readonly SolidColorBrush WarnRing = BrushFromHex("#D97706");

        public static readonly SolidColorBrush DangerText = BrushFromHex("#B91C1C");
        public static readonly SolidColorBrush DangerBg = BrushFromHex("#FEF2F2");
        public static readonly SolidColorBrush DangerBorder = BrushFromHex("#FECACA");
        public static readonly SolidColorBrush DangerBtnBg = BrushFromHex("#DC2626");
        public static readonly SolidColorBrush DangerBtnHover = BrushFromHex("#B91C1C");

        public static readonly SolidColorBrush ProText = BrushFromHex("#4338CA");
        public static readonly SolidColorBrush ProBg = BrushFromHex("#EEF2FF");
        public static readonly SolidColorBrush ProBorder = BrushFromHex("#C7D2FE");

        public static void ApplySeamlessLightTitlebar(Window win)
        {
            win.SourceInitialized += (s, e) =>
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(win).Handle;
                    int captionColor = 0x00F8F7F7;
                    DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
                    int textColor = 0x00271811;
                    DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));
                    int borderColor = 0x00E9E4E2;
                    DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));
                }
                catch { }
            };
        }

        public static void ApplyGlobalNoFocusDottedStyle(Window win)
        {
            var svStyle = new Style(typeof(ScrollViewer));
            svStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            svStyle.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            win.Resources[typeof(ScrollViewer)] = svStyle;

            foreach (Type t in new[] { typeof(Button), typeof(CheckBox), typeof(RadioButton) })
            {
                var st = new Style(t);
                st.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
                win.Resources[t] = st;
            }
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

        public static ImageSource LoadChatGptAppPng()
        {
            return LoadPngOrBestIco("__CHATGPT_APP_256__", new[]
            {
                CoreService.BundledChatGptPng,
                System.IO.Path.Combine(CoreService.DataDir, "chatgpt-256.png"),
                CoreService.PermanentChatGptIcon,
                CoreService.PermanentPng256
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

        public static ImageSource LoadExeIcon(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return LoadCrispBrandPng();
            if (exePath.IndexOf("chatgpt", StringComparison.OrdinalIgnoreCase) >= 0)
                return LoadChatGptAppPng();

            ImageSource cached;
            if (IconCache.TryGetValue(exePath, out cached)) return cached;

            try
            {
                if (File.Exists(exePath))
                {
                    using (var ico = Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (ico != null)
                        {
                            using (var bmp = ico.ToBitmap())
                            {
                                IntPtr hBmp = bmp.GetHbitmap();
                                try
                                {
                                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                                        hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                                    src.Freeze();
                                    IconCache[exePath] = src;
                                    return src;
                                }
                                finally { DeleteObject(hBmp); }
                            }
                        }
                    }
                }
            }
            catch { }
            return LoadCrispBrandPng();
        }

        public static Image MakeCrispImage(ImageSource src, double size)
        {
            var img = new Image
            {
                Source = src,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        public static Button MakeButton(string text, string variant, double fontSize, Thickness padding)
        {
            Brush bg = MainBg;
            Brush hoverBg = HoverBg;
            Brush fg = TextPrimary;
            Brush border = BorderSubtle;

            if (variant == "primary")
            {
                bg = PrimaryBg; hoverBg = PrimaryHover; fg = Brushes.White; border = PrimaryBg;
            }
            else if (variant == "accent")
            {
                bg = AccentBg; hoverBg = AccentHover; fg = Brushes.White; border = AccentBg;
            }
            else if (variant == "danger")
            {
                bg = DangerBtnBg; hoverBg = DangerBtnHover; fg = Brushes.White; border = DangerBtnBg;
            }
            else if (variant == "danger-outline")
            {
                bg = MainBg; hoverBg = DangerBg; fg = DangerText; border = DangerBorder;
            }
            else if (variant == "ghost")
            {
                bg = Brushes.Transparent; hoverBg = HoverBg; fg = TextSecondary; border = Brushes.Transparent;
            }
            else if (variant == "subtle")
            {
                bg = SubtleBg; hoverBg = HoverBg; fg = TextSecondary; border = Hairline;
            }

            var btn = new Button
            {
                Content = text,
                FontFamily = MainFont,
                FontSize = fontSize,
                FontWeight = (variant == "primary" || variant == "danger" || variant == "accent") ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = fg,
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Padding = padding,
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null,
                SnapsToDevicePixels = true
            };

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "bd";
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cpFactory);
            template.VisualTree = borderFactory;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "bd"));
            template.Triggers.Add(hoverTrigger);

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "bd"));
            template.Triggers.Add(disabledTrigger);

            btn.Template = template;
            return btn;
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

        public static Border MakeTag(string text, Brush bg, Brush fg, Brush borderBrush)
        {
            return new Border
            {
                Background = bg,
                BorderBrush = borderBrush ?? Brushes.Transparent,
                BorderThickness = new Thickness(borderBrush != null ? 1 : 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = MainFont,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = fg
                }
            };
        }

        public static TextBox MakeTextBox(string text)
        {
            return new TextBox
            {
                Text = text ?? "",
                FontFamily = MainFont,
                FontSize = 13,
                Padding = new Thickness(9, 7, 9, 7),
                BorderBrush = BorderStrong,
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Foreground = TextPrimary,
                FocusVisualStyle = null
            };
        }

        public static UIElement MakeUsageRing(double leftPercent, double size)
        {
            double pct = Math.Max(0.0, Math.Min(100.0, leftPercent));
            double stroke = 2.4;
            double radius = (size - stroke) / 2.0;
            double cx = size / 2.0;
            double cy = size / 2.0;

            Brush ringColor = OkRing;
            if (pct <= 10.0) ringColor = DangerBtnBg;
            else if (pct <= 30.0) ringColor = WarnRing;

            var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(new Ellipse { Width = size - stroke, Height = size - stroke, Stroke = BrushFromHex("#E5E7EB"), StrokeThickness = stroke });

            if (pct >= 99.5)
            {
                grid.Children.Add(new Ellipse { Width = size - stroke, Height = size - stroke, Stroke = ringColor, StrokeThickness = stroke });
            }
            else if (pct > 0.5)
            {
                double angle = (pct / 100.0) * 360.0;
                double rad = (angle - 90.0) * Math.PI / 180.0;
                var fig = new PathFigure { StartPoint = new Point(cx, cy - radius), IsClosed = false };
                fig.Segments.Add(new ArcSegment
                {
                    Point = new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad)),
                    Size = new Size(radius, radius),
                    IsLargeArc = angle > 180.0,
                    SweepDirection = SweepDirection.Clockwise
                });
                var geom = new PathGeometry();
                geom.Figures.Add(fig);
                grid.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = geom,
                    Stroke = ringColor,
                    StrokeThickness = stroke,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }
            return grid;
        }
    }
}
