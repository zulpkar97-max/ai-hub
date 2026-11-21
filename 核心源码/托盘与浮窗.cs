using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using WinForms = System.Windows.Forms;

namespace CodexLauncher
{
    /// <summary>
    /// Modern borderless WPF popup menu for the System Tray icon.
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
                Width = 196,
                Background = UiHelpers.MainBg,
                BorderBrush = UiHelpers.BorderStrong,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Margin = new Thickness(10),
                Effect = new DropShadowEffect { Color = UiHelpers.TextPrimary.Color, BlurRadius = 2, ShadowDepth = 3, Direction = 315, Opacity = 0.22 }
            };

            var stack = new StackPanel();

            var batchBtn = UiHelpers.MakeMenuRowButton("一键全部退出", null, "danger", true);
            batchBtn.Click += (s, e) => { Close(); onBatchExit(); };
            stack.Children.Add(batchBtn);

            stack.Children.Add(MakeSep());
            var quitBtn = UiHelpers.MakeMenuRowButton("退出 AI Hub", null, "danger", false);
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
                var duration = new Duration(TimeSpan.FromMilliseconds(120));
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
                offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            };
        }

        private Border MakeSep()
        {
            return new Border { Height = 1, Background = UiHelpers.Hairline, Margin = new Thickness(6, 4, 6, 4) };
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
}
