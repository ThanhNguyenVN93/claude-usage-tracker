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
        private readonly UsageInfo _info;
        private bool _burn7d;
        private bool _chart7d;

        public StatsWindow(StatsSnapshot snapshot, UsageInfo info)
        {
            InitializeComponent();
            _snap = snapshot;
            _info = info;

            TxtProjection.Text = _snap.Projection.Text;
            TxtProjection.Foreground = Brush(_snap.Projection.HitsBeforeReset ? "#EF4444" : "#CDD6F4");

            if (!string.IsNullOrEmpty(info.WeeklyBudgetNote))
            {
                TxtWeeklyBudget.Text = $"🛡 {info.WeeklyBudgetNote}";
                TxtWeeklyBudget.Visibility = Visibility.Visible;
            }
            else
            {
                TxtWeeklyBudget.Visibility = Visibility.Collapsed;
            }

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
                UpdateLayout();   // ActualHeight có thể còn 0 nếu chưa arrange
                var work = ScreenPlacement.WorkAreaForCursor(this);
                Left = Math.Max(work.Left, work.Right - 16 - 340 - Width - 4);
                Top = Math.Max(work.Top, work.Bottom - ActualHeight - 16);
                ((Storyboard)FindResource("FadeIn")).Begin(this);
                DrawChart();
                DrawTimeline();
            };

            // Trước đây cửa sổ tự đóng khi mất focus: alt-tab đi tra cứu một thứ rồi quay lại
            // là bảng thống kê biến mất, phải mở lại từ đầu. Giờ chỉ đóng khi người dùng bảo đóng.
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            };
        }

        private static SolidColorBrush Brush(string hex) => BrushCache.Get(hex);

        private void RenderBurn()
        {
            var modelReport = _burn7d ? _snap.Burn7d : _snap.Burn24h;
            RenderBurnBars(
                BurnPanel, modelReport, "#FAB387",
                "Chưa có dữ liệu theo model — cần Claude Code CLI với statusline.js.",
                $"Tổng ≈ {modelReport.TotalPercent:0}% quota 5h đã tiêu (tính từ CLI) trong {(_burn7d ? "7 ngày" : "24 giờ")}");

            var projReport = _burn7d ? _snap.ProjectBurn7d : _snap.ProjectBurn24h;
            RenderBurnBars(
                ProjectBurnPanel, projReport, "#89B4FA",
                "Chưa có dữ liệu theo thư mục dự án (cần cập nhật statusline.js mới).",
                null);
        }

        private static void RenderBurnBars(System.Windows.Controls.Panel targetPanel, ModelBurnReport report, string fillHex, string emptyMsg, string? footer)
        {
            targetPanel.Children.Clear();
            if (!report.HasData)
            {
                targetPanel.Children.Add(new TextBlock
                {
                    Text = emptyMsg,
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
                    VerticalAlignment = System.Windows.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = item.Model
                });

                var bar = new Grid { Margin = new Thickness(4, 0, 6, 0), Height = 8, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                double share = Math.Clamp(item.Share, 0.02, 1.0);
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0.0001), GridUnitType.Star) });
                var fill = new Border { Background = Brush(fillHex), CornerRadius = new CornerRadius(4) };
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

                targetPanel.Children.Add(row);
            }

            if (!string.IsNullOrEmpty(footer))
            {
                targetPanel.Children.Add(new TextBlock
                {
                    Text = footer,
                    FontSize = 10, Foreground = Brush("#6C7086"), Margin = new Thickness(0, 4, 0, 0)
                });
            }
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
            if (canvasH <= 0) canvasH = 36;

            var series = _snap.Fh24h;
            if (series.Count == 0)
            {
                var empty = new TextBlock { Text = "Chưa có dữ liệu", FontSize = 10, Foreground = Brush("#6C7086") };
                Canvas.SetLeft(empty, 8); Canvas.SetTop(empty, 10);
                TimelineCanvas.Children.Add(empty);
                return;
            }

            // Group samples by hour over last 24h
            // Gom theo GIỜ ĐỒNG HỒ (không phải cửa sổ trượt 60 phút tính từ now): tooltip "HH:xx" và nhãn 0/6/12/18
            // đều giả định slot i = giờ (now.Hour - 23 + i). Với cửa sổ trượt, slot 23 chứa cả mẫu của giờ trước
            // → tooltip/nhãn lệch tới 1 giờ.
            var now = DateTime.Now;
            var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind);
            var hourlyMax = new double[24];
            foreach (var p in series)
            {
                var t = p.Time;
                var sampleHour = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);
                int hoursAgo = (int)Math.Round((currentHour - sampleHour).TotalHours);
                if (hoursAgo < 0 || hoursAgo >= 24) continue;
                int slot = 23 - hoursAgo; // slot 0 = 23 giờ trước, slot 23 = giờ hiện tại
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
                int slot = (markerHour - now.Hour + 47) % 24; // +47 = +23+24 để khớp với slot = 23 - floor(hoursAgo)
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

        private void BtnExportReport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Xuất báo cáo sử dụng Claude",
                FileName = $"claude-usage-report-{DateTime.Now:yyyy-MM-dd}.md",
                Filter = "Markdown (*.md)|*.md|CSV (*.csv)|*.csv",
                DefaultExt = ".md"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                bool isCsv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                var sb = new System.Text.StringBuilder();
                if (isCsv)
                {
                    sb.AppendLine("Category,Name,Window,QuotaPercent,SharePercent");
                    foreach (var m in _snap.Burn24h.Items)
                        sb.AppendLine($"Model,\"{CsvField(m.Model)}\",24h,{m.Percent:0.0},{m.Share * 100:0.0}");
                    foreach (var m in _snap.Burn7d.Items)
                        sb.AppendLine($"Model,\"{CsvField(m.Model)}\",7d,{m.Percent:0.0},{m.Share * 100:0.0}");
                    foreach (var p in _snap.ProjectBurn24h.Items)
                        sb.AppendLine($"Project,\"{CsvField(p.Model)}\",24h,{p.Percent:0.0},{p.Share * 100:0.0}");
                    foreach (var p in _snap.ProjectBurn7d.Items)
                        sb.AppendLine($"Project,\"{CsvField(p.Model)}\",7d,{p.Percent:0.0},{p.Share * 100:0.0}");
                }
                else
                {
                    sb.AppendLine($"# Báo cáo sử dụng Claude — {DateTime.Now:dd/MM/yyyy HH:mm}");
                    sb.AppendLine();
                    sb.AppendLine($"- **Tài khoản**: {_info.AccountName} ({_info.Tier})");
                    sb.AppendLine($"- **Quota chu kỳ 5h**: {_info.PercentUsed:0}% (Reset: {_info.ResetCountdown})");
                    sb.AppendLine($"- **Quota tuần (7 ngày)**: {_info.WeeklyPercent:0}%");
                    if (!string.IsNullOrEmpty(_info.WeeklyBudgetNote))
                        sb.AppendLine($"- **Ngân sách tuần**: {_info.WeeklyBudgetNote}");
                    if (_info.BurnRatePerHour is double r)
                        sb.AppendLine($"- **Tốc độ đốt hiện tại**: {r:0}%/giờ");
                    sb.AppendLine($"- **Dự báo**: {_snap.Projection.Text}");
                    sb.AppendLine();
                    AppendReportTable(sb, "Tiêu thụ theo Model (24h)", _snap.Burn24h);
                    AppendReportTable(sb, "Tiêu thụ theo Model (7 ngày)", _snap.Burn7d);
                    AppendReportTable(sb, "Tiêu thụ theo Dự án (24h)", _snap.ProjectBurn24h);
                    AppendReportTable(sb, "Tiêu thụ theo Dự án (7 ngày)", _snap.ProjectBurn7d);
                }

                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                ToastWindow.ShowToast("✔ Đã xuất báo cáo", System.IO.Path.GetFileName(dlg.FileName), "📄");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Không xuất được báo cáo:\n{ex.Message}", "Lỗi xuất báo cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Tên dự án lấy từ tên thư mục (hợp lệ cả khi bắt đầu bằng =, +, -, @) — Excel/Sheets
        // diễn giải các ô CSV bắt đầu bằng ký tự đó thành công thức (CSV injection).
        // Thêm nháy đơn phía trước để ép về dạng văn bản, vẫn giữ nguyên nội dung hiển thị.
        private static string CsvField(string value)
        {
            string escaped = value.Replace("\"", "\"\"");
            return escaped.Length > 0 && "=+-@\t\r".IndexOf(escaped[0]) >= 0 ? "'" + escaped : escaped;
        }

        private static void AppendReportTable(System.Text.StringBuilder sb, string title, ModelBurnReport report)
        {
            sb.AppendLine($"## {title}");
            if (!report.HasData)
            {
                sb.AppendLine("_Chưa có dữ liệu CLI._\n");
                return;
            }
            sb.AppendLine("| Tên | Quota 5h tiêu thụ | Tỉ trọng |");
            sb.AppendLine("|---|---:|---:|");
            foreach (var item in report.Items)
                sb.AppendLine($"| {item.Model} | {item.Percent:0.0}% | {item.Share:P1} |");
            sb.AppendLine();
        }
    }
}
