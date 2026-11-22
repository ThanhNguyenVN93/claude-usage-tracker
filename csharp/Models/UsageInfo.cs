using System;
using ClaudeUsageTracker.Services;

namespace ClaudeUsageTracker.Models
{
    public class UsageInfo
    {
        // Mặc định phải trung tính: trước đây là số demo (42%, "26 / 45 tin nhắn") nên bất kỳ
        // field nào quên gán là hiện số bịa ra như thể là dữ liệu thật.
        public string AccountName { get; set; } = string.Empty;
        public string Tier { get; set; } = string.Empty;
        public double PercentUsed { get; set; }
        public string MessagesRemaining { get; set; } = string.Empty;
        public string ResetCountdown { get; set; } = "--";
        public DateTime ResetTime { get; set; }        // default = chưa biết giờ reset
        public bool IsDemo { get; set; } = true;
        public string Note { get; set; } = string.Empty;
        public bool IsStale { get; set; } = false;
        public bool DesktopRunning { get; set; } = true;
        public DateTime LastRefreshedAt { get; set; } = DateTime.Now;
        public double? ExtraUsagePercent { get; set; }
        public string? ExtraUsageDetail { get; set; }
        public double WeeklyPercent { get; set; }
        public string SourceLabel { get; set; } = string.Empty;
        // Số của nguồn *không* được chọn, để so khớp hai nguồn với nhau.
        public double? OtherSourcePercent { get; set; }
        public string? OtherSourceLabel { get; set; }
        public DateTime? OtherSourceTime { get; set; }
        public double? EstimatedPercent { get; set; }
        public string? EstimateNote { get; set; }
        public string? DivergenceNote { get; set; }
        public double? ContextPercent { get; set; }
        public string? SessionId { get; set; }
        public string? ProjectionText { get; set; }
        // Mức cảnh báo đang tồn tại (khác với cảnh báo "bắn một lần"): dùng để đẩy màu icon.
        public AlertLevel ConditionLevel { get; set; } = AlertLevel.None;
        // Model đang dùng (chỉ có khi đọc từ CLI statusline).
        public string? ModelName { get; set; }

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
