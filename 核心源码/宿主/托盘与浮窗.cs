using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace CodexLauncher
{
    /// <summary>
    /// Modern 0px sharp-corner WPF popup menu for the System Tray icon.
    /// </summary>
    public class TrayPopupWindow : Window
    {
        public TrayPopupWindow(Action onBatchExit, Action onQuit)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            FontFamily = UiHelpers.MainFont;

            var card = new Border
            {
                Width = 178,
                Background = UiHelpers.MainBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(4),
                Margin = new Thickness(8),
                Effect = new DropShadowEffect { Color = UiHelpers.TextPrimary.Color, BlurRadius = 0, ShadowDepth = 2, Direction = 315, Opacity = 1.0 }
            };

            var stack = new StackPanel();

            var batchBtn = UiHelpers.MakeMenuRowButton("一键全部退出", UiHelpers.DangerBg, "neutral", true);
            batchBtn.Click += (s, e) => { Close(); onBatchExit(); };
            stack.Children.Add(batchBtn);

            stack.Children.Add(MakeSep());
            var quitBtn = UiHelpers.MakeMenuRowButton("退出 AI BASE", null, "danger", false);
            quitBtn.Click += (s, e) => { Close(); onQuit(); };
            stack.Children.Add(quitBtn);

            card.Child = stack;
            Content = card;

            Deactivated += (s, e) => { try { Close(); } catch { } };
            Loaded += (s, e) =>
            {
                PositionNearCursor();
                if (!SystemParameters.ClientAreaAnimation) return;
                var offset = new TranslateTransform();
                card.RenderTransform = offset;
                var duration = new Duration(TimeSpan.FromMilliseconds(110));
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
                offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            };
        }

        private Border MakeSep()
        {
            return new Border { Height = 1, Background = UiHelpers.Hairline, Margin = new Thickness(4, 3, 4, 3) };
        }

        private void PositionNearCursor()
        {
            try
            {
                var pt = WinForms.Cursor.Position;
                var source = PresentationSource.FromVisual(this);
                double dpiX = 1.0, dpiY = 1.0;
                if (source != null && source.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }
                double cx = pt.X / dpiX;
                double cy = pt.Y / dpiY;
                var work = SystemParameters.WorkArea;

                double left = cx - ActualWidth + 18;
                double top = cy - ActualHeight + 6;
                if (left < work.Left) left = work.Left + 4;
                if (left + ActualWidth > work.Right) left = work.Right - ActualWidth - 4;
                if (top < work.Top) top = cy + 6;
                if (top + ActualHeight > work.Bottom) top = work.Bottom - ActualHeight - 4;

                Left = left;
                Top = top;
                Activate();
            }
            catch { }
        }
    }

    /// <summary>
    /// Custom 0px sharp-corner desktop notification toast replacing native Windows balloon tips.
    /// </summary>
    public class DesktopToastWindow : Window
    {
        private static readonly List<DesktopToastWindow> ActiveToasts = new List<DesktopToastWindow>();
        private readonly DispatcherTimer _dismissTimer;
        private bool _closing;

        public static void ShowToast(string title, string message, string tone, int durationMs, Action onClick)
        {
            try
            {
                var toast = new DesktopToastWindow(title, message, tone, durationMs, onClick);
                ActiveToasts.Add(toast);
                toast.Show();
            }
            catch { }
        }

        public static void CloseAll()
        {
            var snapshot = ActiveToasts.ToArray();
            ActiveToasts.Clear();
            foreach (var toast in snapshot)
            {
                try { toast.Close(); } catch { }
            }
        }

        private DesktopToastWindow(string title, string message, string tone, int durationMs, Action onClick)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            FontFamily = UiHelpers.MainFont;

            Brush accent = UiHelpers.OkAccent;
            if (tone == "warn") accent = UiHelpers.WarnBg;
            else if (tone == "error" || tone == "danger") accent = UiHelpers.DangerBg;

            var shadowRect = new Rectangle
            {
                Fill = UiHelpers.BorderStrong,
                Margin = new Thickness(10, 10, 6, 6),
                IsHitTestVisible = false
            };

            var outer = new Border
            {
                Width = 318,
                Background = UiHelpers.MainBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(0),
                Margin = new Thickness(8, 8, 8, 8),
                Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow
            };

            var outerWrap = new Grid();
            outerWrap.Children.Add(shadowRect);
            outerWrap.Children.Add(outer);

            var rootGrid = new Grid();
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3.5) });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var stripe = new Border { Background = accent };
            Grid.SetColumn(stripe, 0);
            rootGrid.Children.Add(stripe);

            var contentStack = new StackPanel();
            Grid.SetColumn(contentStack, 1);

            // Top brand strip
            var headBorder = new Border
            {
                Background = UiHelpers.SubtleBg,
                BorderBrush = UiHelpers.Hairline,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 6, 6, 6)
            };
            var headGrid = new Grid();
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var brandRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var brandIcon = UiHelpers.LoadCrispBrandPng();
            if (brandIcon != null)
            {
                brandRow.Children.Add(new Image
                {
                    Source = brandIcon,
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            brandRow.Children.Add(new TextBlock
            {
                Text = "AI BASE",
                FontFamily = UiHelpers.MainFont,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = UiHelpers.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });
            brandRow.Children.Add(new Rectangle
            {
                Width = 5,
                Height = 5,
                Fill = accent,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(brandRow, 0);
            headGrid.Children.Add(brandRow);

            var closeBtn = MakeCloseButton();
            closeBtn.Click += (s, e) => { e.Handled = true; Dismiss(); };
            Grid.SetColumn(closeBtn, 1);
            headGrid.Children.Add(closeBtn);
            headBorder.Child = headGrid;
            contentStack.Children.Add(headBorder);

            // Body
            var body = new StackPanel { Margin = new Thickness(11, 9, 12, 11) };
            body.Children.Add(new TextBlock
            {
                Text = title ?? "AI BASE",
                FontFamily = UiHelpers.MainFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.TextPrimary,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(message))
            {
                body.Children.Add(new TextBlock
                {
                    Text = message,
                    FontFamily = UiHelpers.MainFont,
                    FontSize = 12,
                    Foreground = UiHelpers.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
            contentStack.Children.Add(body);
            rootGrid.Children.Add(contentStack);
            outer.Child = rootGrid;
            Content = outerWrap;

            outer.MouseLeftButtonUp += (s, e) =>
            {
                if (e.Handled) return;
                Dismiss();
                if (onClick != null) onClick();
            };

            _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1500, durationMs)) };
            _dismissTimer.Tick += (s, e) => { _dismissTimer.Stop(); Dismiss(); };

            MouseEnter += (s, e) => _dismissTimer.Stop();
            MouseLeave += (s, e) =>
            {
                if (_closing) return;
                _dismissTimer.Interval = TimeSpan.FromMilliseconds(1800);
                _dismissTimer.Start();
            };

            Loaded += (s, e) =>
            {
                ReflowToasts();
                if (SystemParameters.ClientAreaAnimation)
                {
                    var offset = new TranslateTransform();
                    outer.RenderTransform = offset;
                    var dur = new Duration(TimeSpan.FromMilliseconds(150));
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    outer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                    offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(14, 0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                }
                _dismissTimer.Start();
            };

            Closed += (s, e) =>
            {
                _dismissTimer.Stop();
                ActiveToasts.Remove(this);
                ReflowToasts();
            };
        }

        private void Dismiss()
        {
            if (_closing) return;
            _closing = true;
            _dismissTimer.Stop();
            try { Close(); } catch { }
        }

        private static Button MakeCloseButton()
        {
            var icon = new Canvas { Width = 9, Height = 9 };
            var l1 = new Line { X1 = 1, Y1 = 1, X2 = 8, Y2 = 8, StrokeThickness = 1.4 };
            var l2 = new Line { X1 = 8, Y1 = 1, X2 = 1, Y2 = 8, StrokeThickness = 1.4 };
            l1.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            l2.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            icon.Children.Add(l1);
            icon.Children.Add(l2);

            var btn = new Button
            {
                Width = 24,
                Height = 22,
                Content = icon,
                Foreground = UiHelpers.TextMuted,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null
            };

            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "cbd";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(Border.BackgroundProperty, UiHelpers.DangerBg, "cbd"));
            hov.Setters.Add(new Setter(Border.BorderBrushProperty, UiHelpers.BorderStrong, "cbd"));
            hov.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            tpl.Triggers.Add(hov);
            btn.Template = tpl;
            return btn;
        }

        private static void ReflowToasts()
        {
            var work = SystemParameters.WorkArea;
            double bottom = work.Bottom - 10;
            for (int i = ActiveToasts.Count - 1; i >= 0; i--)
            {
                var t = ActiveToasts[i];
                if (!t.IsLoaded) continue;
                double w = t.ActualWidth > 0 ? t.ActualWidth : 334;
                double h = t.ActualHeight > 0 ? t.ActualHeight : 104;
                t.Left = work.Right - w - 10;
                t.Top = bottom - h;
                bottom = t.Top + 4;
            }
        }
    }
}
