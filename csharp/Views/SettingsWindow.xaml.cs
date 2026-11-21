using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ClaudeUsageTracker.Services;

namespace ClaudeUsageTracker.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly AppConfig _config;

        public SettingsWindow(AppConfig config)
        {
            InitializeComponent();
            _config = config;

            SetPathStatus(TxtDesktopPath, TxtDesktopStatus, ClaudeUsageService.UsageHistoryPath,
                "✔ Đã tìm thấy dữ liệu Desktop",
                "✖ Chưa tìm thấy — hãy mở Claude Desktop ít nhất một lần");

            SetPathStatus(TxtCliPath, TxtCliStatus, ClaudeUsageService.CliUsagePath,
                "✔ Đã tìm thấy dữ liệu từ statusline",
                "✖ Chưa tìm thấy — hãy dùng Claude Code CLI ít nhất một lần (xem README)");

            TxtInterval.Text = _config.CheckIntervalSeconds.ToString();
            TxtStaleMinutes.Text = _config.StaleThresholdMinutes.ToString();
            ChkAutoStart.IsChecked = AutoStartService.IsEnabled();

            ChkAlerts.IsChecked = _config.EnableAlerts;
            TxtThresholds.Text = _config.AlertThresholds;
            TxtContextWarn.Text = _config.ContextWarnPercent.ToString("0");
            ChkResetReminder.IsChecked = _config.EnableResetReminder;

            ChkEstimate.IsChecked = _config.EnableEstimate;
            TxtDivergence.Text = _config.DivergenceThreshold.ToString("0.#");

            ChkAutoModel.IsChecked = _config.EnableAutoModel;
            TxtSonnetAt.Text = _config.AutoModelSonnetAt.ToString("0");
            TxtHaikuAt.Text = _config.AutoModelHaikuAt.ToString("0");

            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            TxtVersion.Text = $"Phiên bản {version?.ToString(3) ?? "?"} · nguồn dữ liệu usage và tuỳ chọn chạy nền";
        }

        private void SetPathStatus(System.Windows.Controls.TextBox pathBox, System.Windows.Controls.TextBlock statusBlock,
            string path, string foundText, string notFoundText)
        {
            pathBox.Text = path;
            bool found = File.Exists(path);
            statusBlock.Text = found ? foundText : notFoundText;
            statusBlock.Foreground = new SolidColorBrush(found
                ? System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)
                : System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtInterval.Text, out int interval) && interval > 0)
            {
                _config.CheckIntervalSeconds = interval;
            }

            if (int.TryParse(TxtStaleMinutes.Text, out int staleMinutes) && staleMinutes > 0)
            {
                _config.StaleThresholdMinutes = staleMinutes;
            }

            AutoStartService.SetEnabled(ChkAutoStart.IsChecked == true);

            _config.EnableAlerts = ChkAlerts.IsChecked == true;

            // Chỉ nhận danh sách ngưỡng hợp lệ (số nguyên 1–100), sai thì giữ giá trị cũ.
            var parsed = TxtThresholds.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => int.TryParse(t, out int v) ? v : -1)
                .ToList();
            if (parsed.Count > 0 && parsed.All(v => v is > 0 and <= 100))
            {
                _config.AlertThresholds = string.Join(",", parsed.Distinct().OrderBy(v => v));
            }

            if (double.TryParse(TxtContextWarn.Text, out double ctxWarn) && ctxWarn is > 0 and <= 100)
            {
                _config.ContextWarnPercent = ctxWarn;
            }

            _config.EnableResetReminder = ChkResetReminder.IsChecked == true;
            _config.EnableEstimate = ChkEstimate.IsChecked == true;

            if (double.TryParse(TxtDivergence.Text, out double divergence) && divergence is > 0 and <= 100)
            {
                _config.DivergenceThreshold = divergence;
            }

            _config.EnableAutoModel = ChkAutoModel.IsChecked == true;

            // Ngưỡng Haiku phải cao hơn ngưỡng Sonnet, nếu không thì giữ giá trị cũ.
            if (double.TryParse(TxtSonnetAt.Text, out double sonnetAt) && double.TryParse(TxtHaikuAt.Text, out double haikuAt)
                && sonnetAt is > 0 and < 100 && haikuAt is > 0 and <= 100 && haikuAt > sonnetAt)
            {
                _config.AutoModelSonnetAt = sonnetAt;
                _config.AutoModelHaikuAt = haikuAt;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
