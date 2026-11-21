using System;
using System.IO;

namespace ClaudeUsageTracker.Services
{
    // Gom mọi đường dẫn dữ liệu. Biến môi trường CLAUDE_USAGE_TRACKER_DIR cho phép
    // trỏ sang thư mục khác (dùng khi kiểm thử, không đụng dữ liệu thật).
    public static class TrackerPaths
    {
        public static string DataDir
        {
            get
            {
                string? overrideDir = Environment.GetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DIR");
                if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClaudeUsageTracker");
            }
        }

        public static string CliUsage => Path.Combine(DataDir, "cli-usage.json");
        public static string CliHistory => Path.Combine(DataDir, "cli-history.jsonl");
        public static string AlertState => Path.Combine(DataDir, "alert-state.json");

        public static string DesktopHistory
        {
            get
            {
                string? overridePath = Environment.GetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DESKTOP_HISTORY");
                if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Claude", "plan-usage-history.json");
            }
        }
    }
}
