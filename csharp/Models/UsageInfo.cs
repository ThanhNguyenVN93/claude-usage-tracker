using System;
using ClaudeUsageTracker.Services;

namespace ClaudeUsageTracker.Models
{
    public class UsageInfo
    {
        public string AccountName { get; set; } = "Claude Desktop User";
        public string Tier { get; set; } = "Claude Pro";
        public double PercentUsed { get; set; } = 42.0;
        public string MessagesRemaining { get; set; } = "26 / 45 tin nhắn";
        public string ResetCountdown { get; set; } = "2h 45m";
        public DateTime ResetTime { get; set; } = DateTime.Now.AddHours(2).AddMinutes(45);
        public bool IsDemo { get; set; } = true;
        public string Note { get; set; } = string.Empty;
        public bool IsStale { get; set; } = false;
        public bool DesktopRunning { get; set; } = true;
        public DateTime LastRefreshedAt { get; set; } = DateTime.Now;
        public double? ExtraUsagePercent { get; set; }
        public string? ExtraUsageDetail { get; set; }
        public double WeeklyPercent { get; set; }
        public double? ContextPercent { get; set; }
        public string? SessionId { get; set; }
        public string? ProjectionText { get; set; }
        // Mức cảnh báo đang tồn tại (khác với cảnh báo "bắn một lần"): dùng để đẩy màu icon.
        public AlertLevel ConditionLevel { get; set; } = AlertLevel.None;

        public string StatusColorHex
        {
            get
            {
                if (PercentUsed < 60) return "#10B981"; // Emerald green
                if (PercentUsed < 85) return "#F59E0B"; // Amber orange
                return "#EF4444"; // Red
            }
        }
    }
}
