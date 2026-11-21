using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeUsageTracker.Models;
using ClaudeUsageTracker.Services;

namespace ClaudeUsageTracker.Views
{
    public partial class MiniHudWindow : Window
    {
        private readonly Func<Task<UsageInfo>> _fetch;
        private readonly Action _openStats;
        private readonly AppConfig _config;
        private DateTime? _lastSnapshotAt;

        // Khi true, cho phép cửa sổ đóng thật (dùng lúc thoát hẳn ứng dụng).
        public bool AllowRealClose { get; set; }

        public MiniHudWindow(UsageInfo info, Func<Task<UsageInfo>> fetch, Action openStats, AppConfig config)
        {
            InitializeComponent();
            _fetch = fetch;
            _openStats = openStats;
            _config = config;

            ApplyData(info);
            PositionAtBottomRight();

            // Chiều cao tự co theo nội dung: giữ nguyên mép dưới để HUD không "nhảy" lên/xuống.
            SizeChanged += (s, e) =>
            {
                if (e.PreviousSize.Height > 0)
                    Top += e.PreviousSize.Height - e.NewSize.Height;
                else
                    Top = SystemParameters.WorkArea.Bottom - e.NewSize.Height - 16;
            };

            // Đừng để Alt+F4 / đóng cửa sổ hệ thống huỷ luôn instance — chỉ ẩn đi,
            // để ForegroundAppWatcherService còn tự hiện lại khi quay về app liên quan.
            Closing += (s, e) =>
            {
                if (AllowRealClose) return;
                e.Cancel = true;
                Hide();
            };
        }

        public void ApplyData(UsageInfo info)
        {
            _lastSnapshotAt = info.LastRefreshedAt;
            TxtAccount.Text = $"{info.AccountName} ({info.Tier})";
            TxtAccount.ToolTip = TxtAccount.Text;
            if (!string.IsNullOrEmpty(info.ModelName))
            {
                TxtModel.Text = $"🤖 {info.ModelName}";
                TxtModel.Visibility = Visibility.Visible;
            }
            else
            {
                TxtModel.Visibility = Visibility.Collapsed;
            }
            TxtRefreshStatus.Visibility = Visibility.Collapsed;
            TxtPercent.Text = $"{info.PercentUsed:0}%";
            PbUsage.Value = info.PercentUsed;

            var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(info.StatusColorHex)!;
            TxtPercent.Foreground = brush;
            PbUsage.Foreground = brush;

            TxtMessages.Text = $"• Đã dùng: {info.MessagesRemaining}";
            TxtCountdown.Text = $"• Reset chu kỳ 5h: {info.ResetCountdown}";

            if (info.ExtraUsagePercent.HasValue)
            {
                string detail = string.IsNullOrEmpty(info.ExtraUsageDetail) ? string.Empty : $" ({info.ExtraUsageDetail})";
                TxtExtraUsage.Text = $"• Dùng thêm (overage): {info.ExtraUsagePercent.Value:0}%{detail}";
                TxtExtraUsage.Visibility = Visibility.Visible;
            }
            else
            {
                TxtExtraUsage.Visibility = Visibility.Collapsed;
            }

            var insight = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(info.DivergenceNote)) insight.Add(info.DivergenceNote);
            if (!string.IsNullOrEmpty(info.EstimateNote)) insight.Add(info.EstimateNote);
            if (!string.IsNullOrEmpty(info.ProjectionText)) insight.Add($"📈 {info.ProjectionText}");
            if (info.ContextPercent.HasValue) insight.Add($"🧠 Ngữ cảnh phiên: {info.ContextPercent.Value:0}%");
            if (insight.Count > 0)
            {
                TxtInsight.Text = string.Join(Environment.NewLine, insight);
                TxtInsight.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(
                    info.ConditionLevel == AlertLevel.Critical ? "#EF4444"
                    : info.ConditionLevel == AlertLevel.Warn ? "#F59E0B" : "#A6ADC8")!;
                TxtInsight.Visibility = Visibility.Visible;
            }
            else
            {
                TxtInsight.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrEmpty(info.Note))
            {
                TxtNote.Text = info.Note.StartsWith("⚠") ? info.Note : $"ℹ {info.Note}";
                TxtNote.Foreground = (info.IsStale || !info.DesktopRunning)
                    ? (SolidColorBrush)new BrushConverter().ConvertFrom("#F59E0B")!
                    : (SolidColorBrush)new BrushConverter().ConvertFrom("#6C7086")!;
                TxtNote.Visibility = Visibility.Visible;
            }
            else
            {
                TxtNote.Visibility = Visibility.Collapsed;
            }
        }

        private void PositionAtBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 16;
            // Chưa đo layout nên ước lượng chiều cao; SizeChanged sẽ tự căn lại mép dưới.
            Top = workArea.Bottom - 250 - 16;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            var previousSnapshot = _lastSnapshotAt;

            TxtRefreshStatus.Text = "⏳ Đang làm mới...";
            TxtRefreshStatus.Visibility = Visibility.Visible;
            await Task.Delay(300); // để người dùng thấy rõ nút có phản ứng
            var info = await _fetch();
            ApplyData(info);

            // Nút này chỉ đọc lại file, không ép được Claude đi hỏi server —
            // nên phải nói rõ là có số mới hay vẫn là ảnh chụp cũ.
            bool gotNewer = previousSnapshot == null || info.LastRefreshedAt > previousSnapshot;
            TxtRefreshStatus.Text = gotNewer ? "✔ Có số mới" : "— Chưa có số mới";
            TxtRefreshStatus.Visibility = Visibility.Visible;
        }

        private void BtnStats_Click(object sender, RoutedEventArgs e)
        {
            _openStats();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWnd = new SettingsWindow(_config);
            bool saved = settingsWnd.ShowDialog() == true;

            if (saved)
            {
                ConfigService.Save(_config);
                BtnRefresh_Click(sender, e);
            }
        }
    }
}
