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
        private readonly Action _openSettings;
        private readonly AppConfig _config;
        private DateTime? _lastSnapshotAt;
        private bool _hasSavedPosition;

        // Khi true, cho phép cửa sổ đóng thật (dùng lúc thoát hẳn ứng dụng).
        public bool AllowRealClose { get; set; }

        // Người dùng bấm ✕ / thu nhỏ thì HUD phải ở yên. Trước đây chỉ Hide(), nên lần sau
        // quay lại VS Code hay terminal là ForegroundAppWatcher tự Show() lại — nút ✕ thành vô dụng.
        public bool HiddenByUser { get; private set; }

        public void ShowFromWatcher()
        {
            if (HiddenByUser) return;
            Show();
        }

        // Bật lại qua tray/hotkey là ý định rõ ràng của người dùng → gỡ cờ ẩn.
        public void ShowByUser()
        {
            HiddenByUser = false;
            Show();
        }

        public void HideByUser()
        {
            HiddenByUser = true;
            Hide();
        }

        public MiniHudWindow(UsageInfo info, Func<Task<UsageInfo>> fetch, Action openStats, Action openSettings, AppConfig config)
        {
            InitializeComponent();
            _fetch = fetch;
            _openStats = openStats;
            _openSettings = openSettings;
            _config = config;

            ApplyData(info);
            RestoreOrDefaultPosition();

            // Chiều cao tự co theo nội dung: giữ nguyên mép dưới để HUD không "nhảy" lên/xuống.
            SizeChanged += (s, e) =>
            {
                if (e.PreviousSize.Height > 0)
                {
                    Top += e.PreviousSize.Height - e.NewSize.Height;
                }
                else if (!_hasSavedPosition)
                {
                    // Lần layout đầu tiên (PreviousSize = 0) mới được tự căn mép dưới.
                    // Có vị trí người dùng đã lưu thì phải để yên, nếu không Top vừa khôi phục
                    // sẽ bị ghi đè ngay và chỉ toạ độ X sống sót.
                    Top = ScreenPlacement.WorkAreaForCursor(this).Bottom - e.NewSize.Height - 16;
                }
            };

            // Đừng để Alt+F4 / đóng cửa sổ hệ thống huỷ luôn instance — chỉ ẩn đi,
            // để ForegroundAppWatcherService còn tự hiện lại khi quay về app liên quan.
            Closing += (s, e) =>
            {
                if (AllowRealClose) return;
                e.Cancel = true;
                HideByUser();   // Alt+F4 cũng là ý định ẩn, không phải ẩn tạm
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

            var brush = BrushCache.Get(info.StatusColorHex);
            TxtPercent.Foreground = brush;
            PbUsage.Foreground = brush;

            TxtMessages.Text = $"• Đã dùng: {info.MessagesRemaining}";
            TxtCountdown.Text = $"• Reset chu kỳ 5h: {info.ResetCountdown}";

            var extraLines = new System.Collections.Generic.List<string>();
            if (info.WeeklyResetTime is DateTime wr && wr > DateTime.Now)
                extraLines.Add($"• Reset tuần: {StatsService.Fmt(wr - DateTime.Now)} nữa (lúc {wr:HH:mm dd/MM})");
            if (info.SessionCostUsd is double cost && cost > 0)
                extraLines.Add($"• Chi phí phiên: ${cost:0.00}");
            if (extraLines.Count > 0)
            {
                TxtWeekly.Text = string.Join(Environment.NewLine, extraLines);
                TxtWeekly.Visibility = Visibility.Visible;
            }
            else
            {
                TxtWeekly.Visibility = Visibility.Collapsed;
            }

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
                TxtInsight.Foreground = BrushCache.Get(
                    info.ConditionLevel == AlertLevel.Critical ? "#EF4444"
                    : info.ConditionLevel == AlertLevel.Warn ? "#F59E0B" : "#A6ADC8");
                TxtInsight.Visibility = Visibility.Visible;
            }
            else
            {
                TxtInsight.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrEmpty(info.Note))
            {
                TxtNote.Text = info.Note.StartsWith("⚠") ? info.Note : $"ℹ {info.Note}";
                TxtNote.Foreground = BrushCache.Get(
                    (info.IsStale || !info.DesktopRunning) ? "#F59E0B" : "#6C7086");
                TxtNote.Visibility = Visibility.Visible;
            }
            else
            {
                TxtNote.Visibility = Visibility.Collapsed;
            }
        }

        // Vị trí người dùng đã kéo tới được giữ lại qua các lần khởi động; nếu màn hình đó
        // không còn (rút cáp, đổi cấu hình) thì quay về góc dưới phải của màn hình đang dùng.
        private void RestoreOrDefaultPosition()
        {
            if (!double.IsNaN(_config.HudLeft) && !double.IsNaN(_config.HudTop)
                && ScreenPlacement.IsOnAnyScreen(_config.HudLeft, _config.HudTop, Width, 250))
            {
                Left = _config.HudLeft;
                Top = _config.HudTop;
                _hasSavedPosition = true;
                return;
            }

            var workArea = ScreenPlacement.WorkAreaForCursor(this);
            Left = workArea.Right - Width - 16;
            // Chưa đo layout nên ước lượng chiều cao; SizeChanged sẽ tự căn lại mép dưới.
            Top = workArea.Bottom - 250 - 16;
        }

        private void RememberPosition()
        {
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;
            if (_config.HudLeft == Left && _config.HudTop == Top) return;
            _config.HudLeft = Left;
            _config.HudTop = Top;
            _hasSavedPosition = true;
            ConfigService.Save(_config);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
                RememberPosition();   // DragMove chặn cho tới khi thả chuột
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => HideByUser();

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => HideByUser();

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

        // Phải đi qua App.OpenSettings: chỉ đường đó mới đăng ký lại hotkey, khởi động lại vòng sync
        // và chặn mở hai hộp thoại cùng lúc. Tự mở SettingsWindow ở đây là lưu được config nhưng không áp dụng.
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => _openSettings();
    }
}
