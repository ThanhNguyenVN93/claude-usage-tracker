using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeUsageTracker.Models;

namespace ClaudeUsageTracker.Services
{
    public record StatsSnapshot(
        ModelBurnReport Burn24h,
        ModelBurnReport Burn7d,
        Projection Projection,
        IReadOnlyList<SeriesPoint> Fh24h,
        IReadOnlyList<SeriesPoint> Fh7d,
        IReadOnlyList<SeriesPoint> Sd7d,
        double? ContextPercent);

    // Gắn dự báo / mức cảnh báo vào UsageInfo, sinh cảnh báo mới và dựng số liệu cho cửa sổ Thống kê.
    public class InsightsService
    {
        private readonly HistoryService _history;
        private readonly AppConfig _config;
        private readonly AlertState _state;

        public InsightsService(HistoryService history, AppConfig config)
        {
            _history = history;
            _config = config;
            _state = AlertStateStore.Load(TrackerPaths.AlertState);
        }

        public List<Alert> Process(UsageInfo info)
        {
            var now = DateTime.Now;
            var samples = _history.Load(now.AddDays(-7));
            var projection = StatsService.ComputeProjection(samples, now, ValidReset(info, now));

            info.ProjectionText = info.IsDemo ? null : projection.Text;
            info.ConditionLevel = ComputeCondition(info, projection);
            AttachEstimate(info, samples, now);

            if (info.IsDemo || info.IsStale) return new List<Alert>();

            var input = new AlertInput(info.PercentUsed, info.WeeklyPercent, info.ContextPercent, info.SessionId, projection, now);
            var alerts = AlertEngine.Evaluate(input, BuildAlertConfig(), _state);

            if (alerts.Count > 0) AlertStateStore.Save(TrackerPaths.AlertState, _state);
            return alerts;
        }

        // Hai cách bù độ trễ làm fallback cho nhau: nếu 2 nguồn lệch nhau thì cảnh báo và KHÔNG ước tính
        // (không biết tin nguồn nào); nếu không ước tính được thì giữ số thật + tuổi dữ liệu.
        private void AttachEstimate(UsageInfo info, List<UsageSample> samples, DateTime now)
        {
            info.EstimatedPercent = null;
            info.EstimateNote = null;
            info.DivergenceNote = null;
            if (info.IsDemo) return;

            bool diverged = false;
            if (info.OtherSourcePercent.HasValue && info.OtherSourceTime.HasValue
                && Math.Abs((info.LastRefreshedAt - info.OtherSourceTime.Value).TotalMinutes) <= 5)
            {
                double diff = Math.Abs(info.PercentUsed - info.OtherSourcePercent.Value);
                if (diff >= _config.DivergenceThreshold)
                {
                    diverged = true;
                    info.DivergenceNote =
                        $"⚠ 2 nguồn lệch {diff:0} điểm: {info.SourceLabel} {info.PercentUsed:0}% / " +
                        $"{info.OtherSourceLabel} {info.OtherSourcePercent.Value:0}% (đang dùng nguồn mới hơn)";
                }
            }

            if (!_config.EnableEstimate || diverged || info.IsStale) return;

            var est = StatsService.EstimateNow(samples, now, ValidReset(info, now));
            if (est == null) return;

            // Chỉ tin ước tính khi mẫu cuối trong lịch sử đúng là mẫu đang hiển thị.
            var infoAge = now - info.LastRefreshedAt;
            if (Math.Abs((infoAge - est.SampleAge).TotalMinutes) > 2) return;

            info.EstimatedPercent = Math.Min(100, info.PercentUsed + est.Delta);
            info.EstimateNote =
                $"≈ {info.EstimatedPercent.Value:0}% ước tính hiện tại (+{est.Delta:0}% sau {(int)est.SampleAge.TotalMinutes} phút)";
        }

        public StatsSnapshot BuildSnapshot(UsageInfo info)
        {
            var now = DateTime.Now;
            var samples = _history.Load(now.AddDays(-7));

            return new StatsSnapshot(
                StatsService.ComputeModelBurn(samples, now, TimeSpan.FromHours(24)),
                StatsService.ComputeModelBurn(samples, now, TimeSpan.FromDays(7)),
                StatsService.ComputeProjection(samples, now, ValidReset(info, now)),
                StatsService.BuildSeries(samples, now, TimeSpan.FromHours(24), s => s.FiveHour),
                StatsService.BuildSeries(samples, now, TimeSpan.FromDays(7), s => s.FiveHour),
                StatsService.BuildSeries(samples, now, TimeSpan.FromDays(7), s => s.SevenDay),
                info.ContextPercent);
        }

        public void PruneHistory() => _history.PruneCliHistory(_config.HistoryRetentionDays);

        private static DateTime? ValidReset(UsageInfo info, DateTime now) =>
            info.ResetTime > now ? info.ResetTime : null;

        private AlertConfig BuildAlertConfig()
        {
            var thresholds = _config.AlertThresholds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out int v) ? v : -1)
                .Where(v => v is > 0 and <= 100)
                .Distinct()
                .ToArray();

            return new AlertConfig
            {
                Enabled = _config.EnableAlerts,
                Thresholds = thresholds.Length > 0 ? thresholds : new[] { 80, 95 },
                ContextWarnPercent = _config.ContextWarnPercent
            };
        }

        private AlertLevel ComputeCondition(UsageInfo info, Projection projection)
        {
            if (info.IsDemo || !_config.EnableAlerts) return AlertLevel.None;
            if (projection.HitsBeforeReset && info.PercentUsed < 100) return AlertLevel.Critical;
            if (info.ContextPercent >= _config.ContextWarnPercent) return AlertLevel.Warn;
            return AlertLevel.None;
        }
    }
}
