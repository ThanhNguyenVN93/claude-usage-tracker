using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ClaudeUsageTracker.Models;
using ClaudeUsageTracker.Services;

namespace ClaudeUsageTracker.Views
{
    public partial class StatsWindow : Window
    {
        private readonly StatsSnapshot _snap;
        private bool _burn7d;
        private bool _chart7d;

        public StatsWindow(StatsSnapshot snapshot, UsageInfo info)
        {
            InitializeComponent();
            _snap = snapshot;

            TxtProjection.Text = _snap.Projection.Text;
            TxtProjection.Foreground = Brush(_snap.Projection.HitsBeforeReset ? "#EF4444" : "#CDD6F4");

            if (_snap.ContextPercent.HasValue)
            {
                TxtContext.Text = $"🧠 Ngữ cảnh phiên hiện tại: {_snap.ContextPercent.Value:0}%";
                TxtContext.Visibility = Visibility.Visible;
            }
            else
            {
                TxtContext.Visibility = Visibility.Collapsed;
            }

            RenderBurn();
            RenderRangeButtons();

            // Đặt bên trái HUD (HUD nằm góc dưới phải), căn mép dưới.
            Loaded += (s, e) =>
            {
                var work = SystemParameters.WorkArea;
                Left = Math.Max(work.Left, work.Right - 16 - 340 - Width - 4);
                Top = work.Bottom - ActualHeight - 16;
                ((Storyboard)FindResource("FadeIn")).Begin(this);
                DrawChart();
                DrawTimeline();
            };

            // Giống toast: tự đóng khi người dùng bấm sang chỗ khác. Phải chặn gọi Close() lần hai:
            // bấm ✕ cũng làm cửa sổ mất focus ngay trong lúc đang đóng, và Close() lúc đó sẽ ném
            // InvalidOperationException (đã từng làm sập cả tool).
            bool closing = false;
            Closing += (s, e) => closing = true;
            Deactivated += (s, e) =>
            {
                if (closing) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!closing && IsLoaded) Close();
                }));
            };
        }

        private static SolidColorBrush Brush(string hex) =>
            (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;

        private void RenderBurn()
        {
            BurnPanel.Children.Clear();
            var report = _burn7d ? _snap.Burn7d : _snap.Burn24h;

            if (!report.HasData)
            {
                BurnPanel.Children.Add(new TextBlock
                {
                    Text = "Chưa có dữ liệu theo model — cần Claude Code CLI với statusline.js (API claude.ai không trả per-model). Biểu đồ lịch sử ở dưới vẫn hiện nếu đã sync API.",
                    FontSize = 11, Foreground = Brush("#6C7086"), TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var item in report.Items)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });

                row.Children.Add(new TextBlock
                {
                    Text = item.Model, FontSize = 11, Foreground = Brush("#CDD6F4"),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                });

                // Thanh tỉ lệ: chia theo star nên không cần đo kích thước.
                var bar = new Grid { Margin = new Thickness(4, 0, 6, 0), Height = 8, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                double share = Math.Clamp(item.Share, 0.02, 1.0);
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0.0001), GridUnitType.Star) });
                var fill = new Border { Background = Brush("#FAB387"), CornerRadius = new CornerRadius(4) };
                bar.Children.Add(new Border { Background = Brush("#313244"), CornerRadius = new CornerRadius(4) });
                bar.Children.Add(fill);
                Grid.SetColumnSpan(bar.Children[0], 2);
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);

                var pct = new TextBlock
                {
                    Text = $"{item.Percent:0}% ({item.Share:P0})", FontSize = 10, Foreground = Brush("#A6ADC8"),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };
                Grid.SetColumn(pct, 2);
                row.Children.Add(pct);

                BurnPanel.Children.Add(row);
            }

            BurnPanel.Children.Add(new TextBlock
            {
                Text = $"Tổng ≈ {report.TotalPercent:0}% quota 5h đã tiêu (tính từ CLI) trong {(_burn7d ? "7 ngày" : "24 giờ")}",
                FontSize = 10, Foreground = Brush("#6C7086"), Margin = new Thickness(0, 4, 0, 0)
            });
        }

        private void RenderRangeButtons()
        {
            MarkActive(BtnBurn24h, !_burn7d);
            MarkActive(BtnBurn7d, _burn7d);
            MarkActive(BtnChart24h, !_chart7d);
            MarkActive(BtnChart7d, _chart7d);
        }

        private static void MarkActive(System.Windows.Controls.Button b, bool active)
        {
            b.Background = Brush(active ? "#F59E0B" : "#313244");
            b.Foreground = Brush(active ? "#11111B" : "#A6ADC8");
        }

        private void DrawChart()
        {
            ChartCanvas.Children.Clear();
            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var fh = _chart7d ? _snap.Fh7d : _snap.Fh24h;
            var windowStart = DateTime.Now - (_chart7d ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24));
            var sd = _snap.Sd7d.Where(p => p.Time >= windowStart).ToList();
            TxtChartRange.Text = _chart7d ? "7 ngày gần nhất" : "24 giờ gần nhất";

            foreach (double level in new[] { 0.0, 50.0, 100.0 })
            {
                double y = YOf(level, h);
                var line = new Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = Brush("#313244"), StrokeThickness = 1,
                    StrokeDashArray = level == 50 ? new DoubleCollection { 2, 3 } : null
                };
                ChartCanvas.Children.Add(line);
            }

            if (fh.Count < 2 && sd.Count < 2)
            {
                var empty = new TextBlock { Text = "Chưa đủ dữ liệu lịch sử", FontSize = 11, Foreground = Brush("#6C7086") };
                Canvas.SetLeft(empty, 10);
                Canvas.SetTop(empty, h / 2 - 8);
                ChartCanvas.Children.Add(empty);
                return;
            }

            var window = _chart7d ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);
            var end = DateTime.Now;
            var start = end - window;

            AddSeries(sd, start, window, w, h, "#F38BA8");
            AddSeries(fh, start, window, w, h, "#89B4FA");
        }

        private void AddSeries(IReadOnlyList<SeriesPoint> series, DateTime start, TimeSpan window, double w, double h, string hex)
        {
            if (series.Count < 2) return;

            var poly = new Polyline { Stroke = Brush(hex), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            foreach (var p in series)
            {
                double x = (p.Time - start).TotalSeconds / window.TotalSeconds * w;
                poly.Points.Add(new System.Windows.Point(Math.Clamp(x, 0, w), YOf(p.Value, h)));
            }
            ChartCanvas.Children.Add(poly);
        }

        private static double YOf(double percent, double height)
        {
            const double pad = 4;
            return pad + (height - 2 * pad) * (1 - Math.Clamp(percent, 0, 100) / 100.0);
        }

        private void DrawTimeline()
        {
            TimelineCanvas.Children.Clear();
            double canvasW = TimelineCanvas.ActualWidth;
            double canvasH = TimelineCanvas.ActualHeight;
            if (canvasW <= 0) canvasW = 350;

            var series = _snap.Fh24h;
            if (series.Count == 0)
            {
                var empty = new TextBlock { Text = "Chưa có dữ liệu", FontSize = 10, Foreground = Brush("#6C7086") };
                Canvas.SetLeft(empty, 8); Canvas.SetTop(empty, 10);
                TimelineCanvas.Children.Add(empty);
                return;
            }

            // Group samples by hour over last 24h
            var now = DateTime.Now;
            var hourlyMax = new double[24];
            foreach (var p in series)
            {
                double hoursAgo = (now - p.Time).TotalHours;
                if (hoursAgo < 0 || hoursAgo >= 24) continue;
                int slot = 23 - (int)hoursAgo; // slot 0 = 23h ago, slot 23 = current hour
                if (p.Value > hourlyMax[slot]) hourlyMax[slot] = p.Value;
            }

            double slotW = canvasW / 24.0;
            double pad = 2;

            for (int i = 0; i < 24; i++)
            {
                double val = hourlyMax[i];
                string color = val >= 90 ? "#F38BA8"
                             : val >= 70 ? "#FAB387"
                             : val >= 30 ? "#F9E2AF"
                             : val >  0  ? "#A6E3A1"
                                         : "#313244";

                var rect = new Border
                {
                    Width  = Math.Max(slotW - pad, 1),
                    Height = canvasH - 4,
                    Background = Brush(color),
                    CornerRadius = new CornerRadius(2),
                    ToolTip = $"{(now.Hour - 23 + i + 24) % 24:D2}:xx — {(val > 0 ? val.ToString("0.#") + "%" : "không có data")}"
                };
                Canvas.SetLeft(rect, i * slotW + pad / 2);
                Canvas.SetTop(rect, 2);
                TimelineCanvas.Children.Add(rect);
            }

            // Hour labels at 0h, 6h, 12h, 18h, now
            foreach (int markerHour in new[] { 0, 6, 12, 18 })
            {
                int slot = (markerHour - now.Hour + 24) % 24;
                // Label below
                var lbl = new TextBlock { Text = $"{markerHour:D2}", FontSize = 8, Foreground = Brush("#6C7086") };
                Canvas.SetLeft(lbl, slot * slotW);
                Canvas.SetTop(lbl, canvasH - 10);
                TimelineCanvas.Children.Add(lbl);
            }
        }

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

        private void BtnBurn24h_Click(object sender, RoutedEventArgs e) { _burn7d = false; RenderBurn(); RenderRangeButtons(); }
        private void BtnBurn7d_Click(object sender, RoutedEventArgs e) { _burn7d = true; RenderBurn(); RenderRangeButtons(); }
        private void BtnChart24h_Click(object sender, RoutedEventArgs e) { _chart7d = false; DrawChart(); RenderRangeButtons(); }
        private void BtnChart7d_Click(object sender, RoutedEventArgs e) { _chart7d = true; DrawChart(); RenderRangeButtons(); }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
