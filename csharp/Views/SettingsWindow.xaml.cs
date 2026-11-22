using System;
using System.Collections.Generic;
using System.Globalization;
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
            TxtContextWarn.Text = Num(_config.ContextWarnPercent, "0");
            ChkResetReminder.IsChecked = _config.EnableResetReminder;
            ChkSoundAlert.IsChecked = _config.EnableSoundAlert;
            ChkUpdateCheck.IsChecked = _config.EnableUpdateCheck;
            TxtHudOpacity.Text = Num(Math.Round(_config.HudOpacity * 100), "0");

            ChkEstimate.IsChecked = _config.EnableEstimate;
            TxtDivergence.Text = Num(_config.DivergenceThreshold, "0.#");

            _pendingMod = _config.HotkeyModifiers;
            _pendingVk  = _config.HotkeyVirtualKey;
            TxtHotkey.Text = FormatHotkey(_pendingMod, _pendingVk);

            ChkAutoModel.IsChecked = _config.EnableAutoModel;
            TxtSonnetAt.Text = Num(_config.AutoModelSonnetAt, "0");
            TxtHaikuAt.Text = Num(_config.AutoModelHaikuAt, "0");

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
            // Giá trị sai trước đây bị bỏ qua không một lời nào: hộp thoại đóng lại,
            // người dùng tưởng đã lưu còn thiết lập thì vẫn như cũ.
            var rejected = new List<string>();
            // SessionKey — chỉ ghi nếu hợp lệ hoặc xoá
            string newKey = PwdSessionKey.Password.Trim();
            if (newKey.StartsWith("sk-ant-") || string.IsNullOrEmpty(newKey))
                _config.SessionKey = newKey;

            if (TryParseInt(TxtApiSyncInterval.Text, out int apiInterval) && apiInterval >= 0)
                _config.ApiSyncIntervalSeconds = apiInterval;
            else rejected.Add("Chu kỳ sync API (giây, >= 0)");

            if (TryParseInt(TxtInterval.Text, out int interval) && interval > 0)
                _config.CheckIntervalSeconds = interval;
            else rejected.Add("Chu kỳ kiểm tra (giây, > 0)");

            if (TryParseInt(TxtStaleMinutes.Text, out int staleMinutes) && staleMinutes > 0)
                _config.StaleThresholdMinutes = staleMinutes;
            else rejected.Add("Ngưỡng dữ liệu cũ (phút, > 0)");

            AutoStartService.SetEnabled(ChkAutoStart.IsChecked == true);

            _config.EnableAlerts = ChkAlerts.IsChecked == true;

            // Chỉ nhận danh sách ngưỡng hợp lệ (số nguyên 1–100), sai thì giữ giá trị cũ.
            var parsed = TxtThresholds.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => TryParseInt(t, out int v) ? v : -1)
                .ToList();
            if (parsed.Count > 0 && parsed.All(v => v is > 0 and <= 100))
                _config.AlertThresholds = string.Join(",", parsed.Distinct().OrderBy(v => v));
            else rejected.Add("Ngưỡng cảnh báo % (danh sách 1–100)");

            if (TryParseNumber(TxtContextWarn.Text, out double ctxWarn) && ctxWarn is > 0 and <= 100)
                _config.ContextWarnPercent = ctxWarn;
            else rejected.Add("Ngưỡng cảnh báo ngữ cảnh (1–100)");

            _config.EnableResetReminder = ChkResetReminder.IsChecked == true;
            _config.EnableSoundAlert = ChkSoundAlert.IsChecked == true;
            _config.EnableUpdateCheck = ChkUpdateCheck.IsChecked == true;
            if (TryParseInt(TxtHudOpacity.Text, out int opacityPct) && opacityPct is >= 50 and <= 100)
                _config.HudOpacity = opacityPct / 100.0;
            else rejected.Add("Độ rõ cửa sổ HUD (50–100%)");
            _config.EnableEstimate = ChkEstimate.IsChecked == true;

            if (TryParseNumber(TxtDivergence.Text, out double divergence) && divergence is > 0 and <= 100)
                _config.DivergenceThreshold = divergence;
            else rejected.Add("Ngưỡng lệch 2 nguồn (1–100)");

            _config.EnableAutoModel = ChkAutoModel.IsChecked == true;

            // Ngưỡng Haiku phải cao hơn ngưỡng Sonnet, nếu không thì giữ giá trị cũ.
            if (TryParseNumber(TxtSonnetAt.Text, out double sonnetAt) && TryParseNumber(TxtHaikuAt.Text, out double haikuAt)
                && sonnetAt is > 0 and < 100 && haikuAt is > 0 and <= 100 && haikuAt > sonnetAt)
            {
                _config.AutoModelSonnetAt = sonnetAt;
                _config.AutoModelHaikuAt = haikuAt;
            }
            else rejected.Add("Ngưỡng tự đổi model (Haiku phải lớn hơn Sonnet)");

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

            // Chặn ngay tại đây và nói rõ lý do: App chỉ POST qua https, nếu lặng lẽ bỏ
            // thì người dùng cấu hình xong mà không bao giờ nhận được cảnh báo nào.
            string webhook = TxtWebhookUrl.Text.Trim();
            if (webhook.Length > 0 && !IsHttpsUrl(webhook))
            {
                TxtWebhookHint.Text = "✖ Webhook phải là URL https:// hợp lệ (số liệu usage là dữ liệu riêng tư).";
                TxtWebhookHint.Foreground = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
                TxtWebhookUrl.Focus();
                return;   // không đóng hộp thoại, không lưu gì cả
            }
            _config.WebhookUrl = webhook;

            if (rejected.Count > 0)
            {
                System.Windows.MessageBox.Show(
                    "Cac muc sau khong hop le nen giu nguyen gia tri cu:\n\n- " +
                    string.Join("\n- ", rejected) +
                    "\n\nSo thap phan dung dau cham hay dau phay deu duoc (vi du 4.5).",
                    "Mot so thiet lap chua duoc luu", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            DialogResult = true;
            Close();
        }

        // Trên máy dùng dấu phẩy thập phân (vi-VN, de-DE...), double.TryParse theo culture
        // đọc "4.5" thành 45 — sai 10 lần mà vẫn lọt kiểm tra 0..100. Nhận cả hai kiểu dấu.
        private static bool TryParseNumber(string text, out double value)
        {
            text = text.Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseInt(string text, out int value) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        // Hiển thị cố định bằng dấu chấm để khớp với cách đọc ở trên.
        private static string Num(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

        private static bool IsHttpsUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

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

        private void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            string settingsPath = TrackerPaths.ClaudeSettings;
            string bakPath = settingsPath + ".tracker.bak";
            if (!File.Exists(bakPath))
            {
                System.Windows.MessageBox.Show(
                    "Chưa có file sao lưu settings.json.tracker.bak nào (chỉ tạo ra khi tính năng Tự đổi model ghi đè lần đầu).",
                    "Khôi phục cấu hình", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Backup chỉ chụp MỘT LẦN, ngay lúc tính năng ghi đè lần đầu — mọi chỉnh sửa
            // settings.json (của người dùng hoặc Claude Code CLI) SAU thời điểm đó sẽ mất
            // sạch vì đây là ghi đè cả file, không phải chỉ khoá "model". Phải hỏi trước.
            DateTime bakTime = File.GetLastWriteTime(bakPath);
            var confirm = System.Windows.MessageBox.Show(
                $"Thao tác này sẽ GHI ĐÈ TOÀN BỘ ~/.claude/settings.json hiện tại bằng bản sao lưu từ " +
                $"{bakTime:dd/MM/yyyy HH:mm}.\n\n" +
                "Mọi thay đổi bạn (hoặc Claude Code CLI) đã thực hiện với settings.json SAU thời điểm đó " +
                "sẽ bị mất, không chỉ riêng khoá \"model\".\n\nBạn có chắc chắn muốn tiếp tục?",
                "Khôi phục cấu hình — xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                File.Copy(bakPath, settingsPath, overwrite: true);
                System.Windows.MessageBox.Show(
                    "Đã khôi phục ~/.claude/settings.json về bản sao lưu gốc thành công!",
                    "Khôi phục cấu hình", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Không khôi phục được:\n{ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCleanCache_Click(object sender, RoutedEventArgs e)
        {
            long freedBytes = 0;
            int filesRemoved = 0;

            try
            {
                string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTracker");
                if (Directory.Exists(appDataDir))
                {
                    foreach (var file in Directory.EnumerateFiles(appDataDir))
                    {
                        string name = Path.GetFileName(file);
                        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "error.log", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "error.log.old", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                freedBytes += new FileInfo(file).Length;
                                File.Delete(file);
                                filesRemoved++;
                            }
                            catch { }
                        }
                    }
                }

                // Dọn thêm file tạm update trong %TEMP%
                string tempUpdate = Path.Combine(Path.GetTempPath(), "ClaudeUsageTrackerUpdate");
                if (Directory.Exists(tempUpdate))
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(tempUpdate, "*", SearchOption.AllDirectories))
                            freedBytes += new FileInfo(f).Length;
                        Directory.Delete(tempUpdate, true);
                        filesRemoved++;
                    }
                    catch { }
                }

                string sizeText = freedBytes >= 1024 * 1024
                    ? $"{freedBytes / (1024.0 * 1024.0):0.0} MB"
                    : $"{freedBytes / 1024.0:0.0} KB";

                System.Windows.MessageBox.Show(
                    filesRemoved > 0
                        ? $"Đã dọn dẹp xong {filesRemoved} mục tạm/log cũ, giải phóng {sizeText}."
                        : "Thư mục dữ liệu hiện tại đang sạch sẽ, không có file rác cần dọn.",
                    "Dọn Cache & Log cũ", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Lỗi khi dọn dẹp:\n{ex.Message}", "Dọn Cache", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
