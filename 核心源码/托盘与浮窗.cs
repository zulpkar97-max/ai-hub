using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace CodexLauncher
{
    /// <summary>
    /// Modern borderless WPF popup menu for the System Tray icon.
    /// </summary>
    public class TrayPopupWindow : Window
    {
        public TrayPopupWindow(
            List<AccountViewModel> accounts,
            ProxyStatusInfo proxy,
            string screenshotHotkey,
            Action onOpenMain,
            Action onScreenshot,
            Action<string> onLaunchAccount,
            Action onOpenProxy,
            Action onBatchExit,
            Action onQuit)
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
                Width = 248,
                Background = UiHelpers.MainBg,
                BorderBrush = UiHelpers.BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6),
                Margin = new Thickness(10),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 4, Opacity = 0.16 }
            };

            var stack = new StackPanel();

            var openBtn = UiHelpers.MakeMenuRowButton("打开 AI Hub", null, "normal", true);
            openBtn.Click += (s, e) => { Close(); onOpenMain(); };
            stack.Children.Add(openBtn);

            string shotLabel = string.IsNullOrEmpty(screenshotHotkey)
                ? "快捷截图与提字"
                : string.Format("快捷截图（{0}）", screenshotHotkey);
            var shotBtn = UiHelpers.MakeMenuRowButton(shotLabel, UiHelpers.OkRing, "normal", false);
            shotBtn.Click += (s, e) => { Close(); onScreenshot(); };
            stack.Children.Add(shotBtn);

            if (accounts != null && accounts.Count > 0)
            {
                stack.Children.Add(MakeSep());
                stack.Children.Add(new TextBlock
                {
                    Text = "快速启动 Codex 账号",
                    FontSize = 10.5,
                    Foreground = UiHelpers.TextMuted,
                    Margin = new Thickness(10, 2, 8, 3)
                });
                foreach (var vm in accounts)
                {
                    string id = vm.Record.id;
                    var dot = vm.Running ? UiHelpers.OkRing : UiHelpers.BorderStrong;
                    var b = UiHelpers.MakeMenuRowButton(vm.Record.name, dot, "normal", false);
                    b.Click += (s, e) => { Close(); onLaunchAccount(id); };
                    stack.Children.Add(b);
                }
            }

            stack.Children.Add(MakeSep());
            bool pRun = proxy != null && proxy.Running;
            var pBtn = UiHelpers.MakeMenuRowButton(
                pRun ? "Clash Verge · 运行中（打开面板）" : "Clash Verge · 未启动（启动代理）",
                pRun ? UiHelpers.OkRing : UiHelpers.BorderStrong,
                "normal", false);
            pBtn.Click += (s, e) => { Close(); onOpenProxy(); };
            stack.Children.Add(pBtn);

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
            Loaded += (s, e) => PositionNearCursor();
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
