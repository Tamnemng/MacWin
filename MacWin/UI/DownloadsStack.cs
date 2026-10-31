using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>The Downloads "stack": a frosted grid of the newest files, popping out of the Dock.</summary>
    public class DownloadsStack : PanelWindow
    {
        static DownloadsStack _open;
        public static string Folder => Native.Win32.KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"))
                                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        public static void Toggle(Window dock, FrameworkElement anchor)
        {
            if (_open != null) { _open.Close(); return; }
            _open = new DownloadsStack(dock, anchor);
            _open.Closed += (_, _) => _open = null;
            _open.Show();
            Ui.ForceActivate(_open);
        }

        DownloadsStack(Window dock, FrameworkElement anchor) : base(ThemeManager.PanelRadius(14), false, 0x40)
        {
            Width = 420;
            SizeToContent = SizeToContent.Height;
            var root = ContinuousBorder.Glass(ThemeManager.PanelRadius(14));
            root.Padding = new Thickness(14);
            root.Opacity = 0; // stay invisible until the backdrop snapshot is taken
            var stack = new StackPanel();
            root.Child = stack;
            Content = root;

            var header = new DockPanel { Margin = new Thickness(4, 0, 4, 10) };
            var openBtn = new Button { Content = "Open in File Explorer", Style = Ui.Style("MacButton"), Padding = new Thickness(10, 2, 10, 3), FontSize = 12 };
            openBtn.Click += (_, _) => { Shell.Open(Folder); Close(); };
            DockPanel.SetDock(openBtn, Dock.Right);
            header.Children.Add(openBtn);
            header.Children.Add(Ui.Text("Downloads", 15, FontWeights.SemiBold));
            stack.Children.Add(header);

            var grid = new WrapPanel();
            stack.Children.Add(grid);
            try
            {
                var dir = new DirectoryInfo(Folder);
                var files = dir.EnumerateFileSystemInfos().Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden))
                    .OrderByDescending(f => f.LastWriteTime).Take(12).ToList();
                if (files.Count == 0) stack.Children.Add(Ui.Text("No downloads yet", 13, null, "Text.Secondary"));
                foreach (var f in files) grid.Children.Add(Tile(f));
            }
            catch { stack.Children.Add(Ui.Text("Downloads folder not available", 13, null, "Text.Secondary")); }

            Deactivated += (_, _) => { try { Close(); } catch { } };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

            Loaded += (_, _) =>
            {
                var tl = anchor.PointToScreen(new Point(0, 0));
                double s = Ui.Scale(this);
                double ax = tl.X / s + anchor.ActualWidth / 2, ay = tl.Y / s;
                switch (App.Settings.DockPosition)
                {
                    case DockPosition.Bottom: Left = ax - ActualWidth / 2; Top = ay - ActualHeight - 14; break;
                    case DockPosition.Left: Left = tl.X / s + anchor.ActualWidth + 14; Top = ay + anchor.ActualHeight / 2 - ActualHeight / 2; break;
                    default: Left = tl.X / s - ActualWidth - 14; Top = ay + anchor.ActualHeight / 2 - ActualHeight / 2; break;
                }
                Left = Math.Clamp(Left, 6, SystemParameters.PrimaryScreenWidth - ActualWidth - 6);
                Top = Math.Clamp(Top, MenuBarWindow.BarHeight + 6, SystemParameters.PrimaryScreenHeight - ActualHeight - 6);
                root.Opacity = 0;
                RefreshBackdrop(root);
                var st = new ScaleTransform(0.9, 0.9, ActualWidth / 2, ActualHeight);
                root.RenderTransform = st;
                root.Opacity = 0;
                Ui.Animate(root, OpacityProperty, 1, 160);
                Ui.Animate(st, ScaleTransform.ScaleXProperty, 1, 200);
                Ui.Animate(st, ScaleTransform.ScaleYProperty, 1, 200);
            };
        }

        FrameworkElement Tile(FileSystemInfo f)
        {
            var img = new Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 0, 4) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            _ = Load(img, f.FullName);
            var name = Ui.Text(f.Name, 11, null, "Text.Primary");
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.MaxHeight = 30;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var sp = new StackPanel { Width = 84 };
            sp.Children.Add(img);
            sp.Children.Add(name);
            var b = new Button { Content = sp, Style = Ui.Style("GhostButton"), Padding = new Thickness(4, 6, 4, 6), Margin = new Thickness(2), ToolTip = f.Name };
            b.Click += (_, _) => { Shell.Open(f.FullName); Close(); };
            b.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var cm = Menus.Build(Menus.Item("Open", () => { Shell.Open(f.FullName); Close(); }),
                    Menus.Item("Show in File Explorer", () => { Shell.Reveal(f.FullName); Close(); }),
                    Menus.Sep(),
                    Menus.Item("Move to Trash", () => { Native.Win32.MoveToRecycleBin(new[] { f.FullName }); Close(); }));
                cm.PlacementTarget = b;
                cm.IsOpen = true;
            };
            return b;
        }

        static async System.Threading.Tasks.Task Load(Image img, string path)
        {
            try { img.Source = await IconProvider.ForFile(path, 96); } catch { }
        }
    }
}
