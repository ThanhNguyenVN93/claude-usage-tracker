using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeUsageTracker.Services;

namespace ClaudeUsageTracker.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly AppConfig _config;

        // Hotkey đang chờ xác nhận (chưa Save)
        private uint _pendingMod;
        private uint _pendingVk;
        private bool _hotkeyCapturing;
        private uint _pendingStatsMod;
        private uint _pendingStatsVk;
        private bool _statsHotkeyCapturing;

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

            // SessionKey — dùng PasswordBox (tự che bằng •)
            if (!string.IsNullOrWhiteSpace(_config.SessionKey))
            {
                PwdSessionKey.Password = _config.SessionKey;
                TxtSessionKeyHint.Text = "✔ Đã cấu hình (để trống để xoá)";
                TxtSessionKeyHint.Foreground = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
            }
            TxtApiSyncInterval.Text = _config.ApiSyncIntervalSeconds.ToString();

            TxtInterval.Text = _config.CheckIntervalSeconds.ToString();
            TxtStaleMinutes.Text = _config.StaleThresholdMinutes.ToString();
            ChkAutoStart.IsChecked = AutoStartService.IsEnabled();

            ChkAlerts.IsChecked = _config.EnableAlerts;
            TxtThresholds.Text = _config.AlertThresholds;
            TxtContextWarn.Text = _config.ContextWarnPercent.ToString("0");
            ChkResetReminder.IsChecked = _config.EnableResetReminder;
            ChkUpdateCheck.IsChecked = _config.EnableUpdateCheck;

            ChkEstimate.IsChecked = _config.EnableEstimate;
            TxtDivergence.Text = _config.DivergenceThreshold.ToString("0.#");

            _pendingMod = _config.HotkeyModifiers;
            _pendingVk  = _config.HotkeyVirtualKey;
            TxtHotkey.Text = FormatHotkey(_pendingMod, _pendingVk);

            ChkAutoModel.IsChecked = _config.EnableAutoModel;
            TxtSonnetAt.Text = _config.AutoModelSonnetAt.ToString("0");
            TxtHaikuAt.Text = _config.AutoModelHaikuAt.ToString("0");

            _pendingStatsMod = _config.StatsHotkeyModifiers;
            _pendingStatsVk  = _config.StatsHotkeyVirtualKey;
            TxtStatsHotkey.Text = FormatHotkey(_pendingStatsMod, _pendingStatsVk);

            TxtWebhookUrl.Text = _config.WebhookUrl;

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
            // SessionKey — chỉ ghi nếu hợp lệ hoặc xoá
            string newKey = PwdSessionKey.Password.Trim();
            if (newKey.StartsWith("sk-ant-") || string.IsNullOrEmpty(newKey))
                _config.SessionKey = newKey;

            if (int.TryParse(TxtApiSyncInterval.Text, out int apiInterval) && apiInterval >= 0)
                _config.ApiSyncIntervalSeconds = apiInterval;

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
            _config.EnableUpdateCheck = ChkUpdateCheck.IsChecked == true;
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

            if (_pendingMod != 0 && _pendingVk != 0)
            {
                _config.HotkeyModifiers  = _pendingMod;
                _config.HotkeyVirtualKey = _pendingVk;
            }

            if (_pendingStatsMod != 0 && _pendingStatsVk != 0)
            {
                _config.StatsHotkeyModifiers  = _pendingStatsMod;
                _config.StatsHotkeyVirtualKey = _pendingStatsVk;
            }

            _config.WebhookUrl = TxtWebhookUrl.Text.Trim();

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ── Hotkey capture ────────────────────────────────────────────────────

        private void TxtHotkey_GotFocus(object sender, RoutedEventArgs e)
        {
            _hotkeyCapturing = true;
            TxtHotkey.Text = "Bấm tổ hợp phím…";
            TxtHotkey.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xC2, 0xE7));
            TxtHotkeyHint.Text = "Esc = huỷ · cần ít nhất Ctrl / Alt / Win kết hợp với phím khác";
        }

        private void TxtHotkey_LostFocus(object sender, RoutedEventArgs e)
        {
            _hotkeyCapturing = false;
            TxtHotkey.Text = FormatHotkey(_pendingMod, _pendingVk);
            TxtHotkey.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));
            TxtHotkeyHint.Text = "Click vào ô rồi bấm tổ hợp phím (cần ít nhất 1 modifier: Ctrl/Alt/Win)";
        }

        private void TxtHotkey_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!_hotkeyCapturing) return;
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                TxtHotkey.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                return;
            }

            // Bỏ qua nếu chỉ bấm modifier đơn thuần
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                     or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
                return;

            var mods = Keyboard.Modifiers;
            // Phải có ít nhất Ctrl, Alt, hoặc Win (tránh chiếm phím bình thường)
            if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            {
                TxtHotkey.Text = "Cần Ctrl / Alt / Win!";
                return;
            }

            uint win32Mod = 0;
            if ((mods & ModifierKeys.Control) != 0) win32Mod |= 0x0002;
            if ((mods & ModifierKeys.Shift)   != 0) win32Mod |= 0x0004;
            if ((mods & ModifierKeys.Alt)     != 0) win32Mod |= 0x0001;
            if ((mods & ModifierKeys.Windows) != 0) win32Mod |= 0x0008;

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

            _pendingMod = win32Mod;
            _pendingVk  = vk;

            TxtHotkey.Text = FormatHotkey(win32Mod, vk);
            TxtHotkey.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));
            TxtHotkey.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private void BtnResetHotkey_Click(object sender, RoutedEventArgs e)
        {
            _pendingMod = 0x0006; // Ctrl+Shift
            _pendingVk  = 0x55;   // U
            TxtHotkey.Text = FormatHotkey(_pendingMod, _pendingVk);
        }

        // ── Stats hotkey capture ─────────────────────────────────────────────

        private void TxtStatsHotkey_GotFocus(object sender, RoutedEventArgs e)
        {
            _statsHotkeyCapturing = true;
            TxtStatsHotkey.Text = "Bấm tổ hợp phím…";
            TxtStatsHotkey.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xC2, 0xE7));
        }

        private void TxtStatsHotkey_LostFocus(object sender, RoutedEventArgs e)
        {
            _statsHotkeyCapturing = false;
            TxtStatsHotkey.Text = FormatHotkey(_pendingStatsMod, _pendingStatsVk);
            TxtStatsHotkey.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));
        }

        private void TxtStatsHotkey_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!_statsHotkeyCapturing) return;
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                TxtStatsHotkey.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                return;
            }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                     or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
                return;

            var mods = Keyboard.Modifiers;
            if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            {
                TxtStatsHotkey.Text = "Cần Ctrl / Alt / Win!";
                return;
            }

            uint win32Mod = 0;
            if ((mods & ModifierKeys.Control) != 0) win32Mod |= 0x0002;
            if ((mods & ModifierKeys.Shift)   != 0) win32Mod |= 0x0004;
            if ((mods & ModifierKeys.Alt)     != 0) win32Mod |= 0x0001;
            if ((mods & ModifierKeys.Windows) != 0) win32Mod |= 0x0008;

            _pendingStatsMod = win32Mod;
            _pendingStatsVk  = (uint)KeyInterop.VirtualKeyFromKey(key);
            TxtStatsHotkey.Text = FormatHotkey(win32Mod, _pendingStatsVk);
            TxtStatsHotkey.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));
            TxtStatsHotkey.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private void BtnResetStatsHotkey_Click(object sender, RoutedEventArgs e)
        {
            _pendingStatsMod = 0x0006; // Ctrl+Shift
            _pendingStatsVk  = 0x53;   // S
            TxtStatsHotkey.Text = FormatHotkey(_pendingStatsMod, _pendingStatsVk);
        }

        // Win32 modifier flags → chuỗi dễ đọc; vk → tên phím
        private static string FormatHotkey(uint mod, uint vk)
        {
            if (mod == 0 || vk == 0) return "(chưa đặt)";
            var parts = new List<string>();
            if ((mod & 0x0002) != 0) parts.Add("Ctrl");
            if ((mod & 0x0004) != 0) parts.Add("Shift");
            if ((mod & 0x0001) != 0) parts.Add("Alt");
            if ((mod & 0x0008) != 0) parts.Add("Win");
            parts.Add(KeyInterop.KeyFromVirtualKey((int)vk).ToString());
            return string.Join(" + ", parts);
        }
    }
}
