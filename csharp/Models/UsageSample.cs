using System;

namespace ClaudeUsageTracker.Models
{
    // Một điểm lịch sử usage, gộp từ file Desktop (không có model) và log CLI.
    public record UsageSample(
        DateTime Time,
        double FiveHour,
        double SevenDay,
        string? Model = null,
        double? ContextPct = null,
        string? SessionId = null);
}
