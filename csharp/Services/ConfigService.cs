using System;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageTracker.Services
{
    public class AppConfig
    {
        // sessionKey từ cookie claude.ai (sk-ant-sid...) — dùng để sync quota thời gian thực
        public string SessionKey { get; set; } = string.Empty;
        public int ApiSyncIntervalSeconds { get; set; } = 300;
        public int CheckIntervalSeconds { get; set; } = 2;
        public int StaleThresholdMinutes { get; set; } = 15;
        public bool EnableAlerts { get; set; } = true;
        public string AlertThresholds { get; set; } = "80,95";
        public double ContextWarnPercent { get; set; } = 70;
        public int HistoryRetentionDays { get; set; } = 30;
        public bool EnableEstimate { get; set; } = true;
        public double DivergenceThreshold { get; set; } = 4;
        public bool EnableResetReminder { get; set; } = true;
        // Chỉ kiểm tra và thông báo; tải về luôn phải hỏi người dùng trước.
        public bool EnableUpdateCheck { get; set; } = true;
        // Global hotkey toggle HUD: MOD_CONTROL|MOD_SHIFT=6, VK_U=0x55
        public uint HotkeyModifiers { get; set; } = 0x0006;  // Ctrl+Shift
        public uint HotkeyVirtualKey { get; set; } = 0x55;   // U
        // Sửa cấu hình của app khác (Claude Code) nên mặc định TẮT.
        public bool EnableAutoModel { get; set; } = false;
        public double AutoModelSonnetAt { get; set; } = 85;
        public double AutoModelHaikuAt { get; set; } = 95;
        // Hotkey mở Stats window: MOD_CONTROL|MOD_SHIFT=6, VK_S=0x53
        public uint StatsHotkeyModifiers { get; set; } = 0x0006;
        public uint StatsHotkeyVirtualKey { get; set; } = 0x53;
        // Webhook URL để gửi cảnh báo usage (Discord, Slack, generic)
        public string WebhookUrl { get; set; } = string.Empty;
        // Vị trí HUD người dùng đã kéo tới (NaN = chưa đặt, dùng góc dưới phải mặc định)
        public double HudLeft { get; set; } = double.NaN;
        public double HudTop { get; set; } = double.NaN;
        // Âm thanh cảnh báo khi chạm ngưỡng nguy hiểm hoặc phát hiện lặp vô tận
        public bool EnableSoundAlert { get; set; } = false;
        // Chế độ riêng tư: ẩn HUD và tắt toast khi chia sẻ màn hình / họp
        public bool PrivacyMode { get; set; } = false;
        // Độ mờ của HUD (0.65 - 1.0)
        public double HudOpacity { get; set; } = 1.0;
    }

    public static class ConfigService
    {
        // Lưu trong %APPDATA%\ClaudeUsageTracker\ để tồn tại qua các lần update/cài lại.
        private static readonly string ConfigDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTracker");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        // Đường dẫn cũ (cùng thư mục exe) — dùng để migrate một lần.
        private static readonly string LegacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        public static AppConfig Load()
        {
            Directory.CreateDirectory(ConfigDir);

            // Migration một lần: chuyển config cũ từ thư mục exe sang %APPDATA%.
            if (!File.Exists(ConfigPath) && File.Exists(LegacyPath))
            {
                try
                {
                    File.Copy(LegacyPath, ConfigPath);
                    File.Delete(LegacyPath);
                }
                catch { /* migration thất bại: đọc thẳng file cũ dưới */ }
            }

            try
            {
                string? pathToRead = File.Exists(ConfigPath) ? ConfigPath
                                   : File.Exists(LegacyPath) ? LegacyPath
                                   : null;
                if (pathToRead != null)
                {
                    string json = File.ReadAllText(pathToRead);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Read error: {ex.Message}");
            }

            var defaultCfg = new AppConfig();
            Save(defaultCfg);
            return defaultCfg;
        }

        public static void Save(AppConfig config)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(config, options);
                AtomicFile.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Save error: {ex.Message}");
            }
        }
    }
}
