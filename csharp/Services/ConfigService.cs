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
        // Sửa cấu hình của app khác (Claude Code) nên mặc định TẮT.
        public bool EnableAutoModel { get; set; } = false;
        public double AutoModelSonnetAt { get; set; } = 85;
        public double AutoModelHaikuAt { get; set; } = 95;
    }

    public static class ConfigService
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
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
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Save error: {ex.Message}");
            }
        }
    }
}
