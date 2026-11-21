using System;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageTracker.Services
{
    public class AppConfig
    {
        public int CheckIntervalSeconds { get; set; } = 2;
        public int StaleThresholdMinutes { get; set; } = 15;
        public bool EnableAlerts { get; set; } = true;
        public string AlertThresholds { get; set; } = "80,95";
        public double ContextWarnPercent { get; set; } = 70;
        public int HistoryRetentionDays { get; set; } = 30;
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
