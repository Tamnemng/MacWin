using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>Activity: live CPU, memory, GPU / VRAM, disk and network with small history graphs and top apps.</summary>
    public class ActivityWindow : PanelWindow
    {
        const double W = 340;
        readonly ContinuousBorder _frame;
        readonly StackPanel _root = new() { Margin = new Thickness(12) };
        readonly Dictionary<string, (TextBlock value, TextBlock detail, Polyline line, Polygon fill)> _sections = new();
        readonly StackPanel _procs = new();

        public ActivityWindow() : base(ThemeManager.PanelRadius(16), false, 0x40)
        {
            Title = "Activity";
            Width = W;
            SizeToContent = SizeToContent.Height;
            _frame = ContinuousBorder.Glass(ThemeManager.PanelRadius(16));
            _frame.Child = _root;
            Content = _frame;

            var head = new DockPanel { Margin = new Thickness(4, 0, 4, 8) };
            var tm = new Button { Content = "Task Manager", Style = Ui.Style("MacButton"), Padding = new Thickness(10, 2, 10, 3), FontSize = 12 };
            tm.Click += (_, _) => { HideAnimated(); Shell.TaskManager(); };
            DockPanel.SetDock(tm, Dock.Right);
            head.Children.Add(tm);
            head.Children.Add(Ui.Text("Activity", 15, FontWeights.SemiBold));
            _root.Children.Add(head);

            Section("cpu", "CPU", "#0A84FF");
            Section("mem", "Memory", "#34C759");
            Section("gpu", "GPU", "#BF5AF2");
            Section("net", "Network", "#FF9F0A");

            var procBox = new StackPanel();
            var ph = Ui.Text("Using the most", 12, FontWeights.SemiBold);
            ph.Margin = new Thickness(0, 0, 0, 4);
            procBox.Children.Add(ph);
            procBox.Children.Add(_procs);
            _root.Children.Add(Module(procBox));

            SystemStats.Updated += Refresh;
            HideOnDeactivate(HideAnimated);
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideAnimated(); };
        }

        static ContinuousBorder Module(UIElement child)
        {
            var b = ContinuousBorder.Glass(ThemeManager.PanelRadius(12), "Tile.Background");
            b.Padding = new Thickness(10, 8, 10, 8);
            b.Margin = new Thickness(0, 4, 0, 4);
            b.Child = child;
            b.SetResourceReference(ContinuousBorder.BorderBrushProperty, "Glass.Rim");
            b.ClearValue(ContinuousBorder.OuterBrushProperty);
            return b;
        }

        void Section(string key, string title, string color)
        {
            var c = (Color)ColorConverter.ConvertFromString(color);
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            var t = Ui.Text(title, 12, FontWeights.SemiBold);
            var value = Ui.Text("—", 15, FontWeights.SemiBold);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.Foreground = new SolidColorBrush(c);
            g.Children.Add(t);
            g.Children.Add(value);
            var detail = Ui.Text("", 11, null, "Text.Secondary");
            Grid.SetRow(detail, 1);
            g.Children.Add(detail);
            var fill = new Polygon { Fill = new LinearGradientBrush(Color.FromArgb(90, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90), IsHitTestVisible = false };
            var line = new Polyline { Stroke = new SolidColorBrush(c), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
            var graph = new Grid { Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true };
            graph.Children.Add(fill);
            graph.Children.Add(line);
            Grid.SetRow(graph, 2);
            g.Children.Add(graph);
            _sections[key] = (value, detail, line, fill);
            _root.Children.Add(Module(g));
        }

        static void Plot((TextBlock value, TextBlock detail, Polyline line, Polygon fill) s, IEnumerable<double> data, double max, double width)
        {
            var arr = data.ToArray();
            var pts = new PointCollection(arr.Length + 2);
            double h = 30, step = width / (SystemStats.HistoryLength - 1);
            double x0 = width - (arr.Length - 1) * step;
            for (int i = 0; i < arr.Length; i++)
                pts.Add(new Point(x0 + i * step, h - Math.Clamp(arr[i] / Math.Max(max, 1e-9), 0, 1) * (h - 2) + 1));
            s.line.Points = pts;
            var poly = new PointCollection(pts) { };
            if (pts.Count > 0)
            {
                poly.Add(new Point(pts[^1].X, h + 2));
                poly.Add(new Point(pts[0].X, h + 2));
            }
            s.fill.Points = poly;
        }

        void Refresh()
        {
            if (!IsVisible) return;
            double gw = Math.Max(100, W - 24 - 20);

            var cpu = _sections["cpu"];
            cpu.value.Text = $"{SystemStats.Cpu:0}%";
            cpu.detail.Text = $"{Environment.ProcessorCount} logical cores · {SystemInfo.Cpu()}";
            Plot(cpu, SystemStats.CpuHistory, 100, gw);

            var mem = _sections["mem"];
            mem.value.Text = $"{SystemStats.MemoryPercent:0}%";
            mem.detail.Text = $"{SystemStats.Bytes(SystemStats.MemoryUsed)} of {SystemStats.Bytes(SystemStats.MemoryTotal)} used";
            Plot(mem, SystemStats.MemHistory, 100, gw);

            var gpu = _sections["gpu"];
            gpu.value.Text = double.IsNaN(SystemStats.Gpu) ? "—" : $"{SystemStats.Gpu:0}%";
            string vram = SystemStats.VramTotal > 0
                ? $"VRAM {SystemStats.Bytes(SystemStats.VramUsed)} of {SystemStats.Bytes(SystemStats.VramTotal)}"
                : $"VRAM {SystemStats.Bytes(SystemStats.VramUsed)} used";
            gpu.detail.Text = vram;
            Plot(gpu, SystemStats.GpuHistory, 100, gw);

            var net = _sections["net"];
            net.value.Text = $"↓ {SystemStats.Bytes(SystemStats.NetDown)}/s";
            net.detail.Text = $"↑ {SystemStats.Bytes(SystemStats.NetUp)}/s · Disk {SystemStats.Bytes(SystemStats.DiskUsed)} of {SystemStats.Bytes(SystemStats.DiskTotal)}";
            Plot(net, SystemStats.NetHistory, Math.Max(64 * 1024, SystemStats.NetHistory.DefaultIfEmpty(0).Max()), gw);

            _procs.Children.Clear();
            foreach (var p in SystemStats.TopProcesses)
            {
                var row = new Grid { Height = 20 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                var n = Ui.Text(p.Name, 12);
                var c = Ui.Text($"{p.Cpu:0.0}%", 12, null, "Text.Secondary");
                c.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(c, 1);
                var m = Ui.Text(SystemStats.Bytes(p.MemoryBytes), 12, null, "Text.Secondary");
                m.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(m, 2);
                row.Children.Add(n);
                row.Children.Add(c);
                row.Children.Add(m);
                _procs.Children.Add(row);
            }
            if (SystemStats.TopProcesses.Count == 0) _procs.Children.Add(Ui.Text("Measuring…", 12, null, "Text.Secondary"));
        }

        public void ShowAnimated(FrameworkElement anchor)
        {
            MarkShown();
            SystemStats.SetDetailed(true);
            double s = Ui.Scale(this);
            double right = SystemParameters.PrimaryScreenWidth - 8;
            if (anchor != null && anchor.IsVisible)
            {
                var p = anchor.PointToScreen(new Point(anchor.ActualWidth, 0));
                right = Math.Min(right, p.X / Math.Max(0.1, Ui.Scale(anchor)) + 40);
            }
            Left = Math.Max(8, right - W);
            Top = MenuBarWindow.BarHeight + 6;
            Opacity = 0;
            var tt = new TranslateTransform(0, -8);
            _frame.RenderTransform = tt;
            Show();
            RefreshBackdrop(_frame, 520);
            Ui.ForceActivate(this);
            Refresh();
            Ui.Animate(this, OpacityProperty, 1, 160);
            Ui.Animate(tt, TranslateTransform.YProperty, 0, 220);
        }

        public void HideAnimated()
        {
            MarkHidden();
            SystemStats.SetDetailed(false);
            Ui.Animate(this, OpacityProperty, 0, 120, done: () => { if (Opacity == 0) Hide(); });
        }
    }
}
