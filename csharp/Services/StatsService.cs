using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeUsageTracker.Models;

namespace ClaudeUsageTracker.Services
{
    public record ModelBurn(string Model, double Percent, double Share);

    public record ModelBurnReport(IReadOnlyList<ModelBurn> Items, double TotalPercent)
    {
        public bool HasData => Items.Count > 0;
    }

    public record Projection(
        bool CanProject,
        DateTime? HitTime,
        double RatePerHour,
        bool HitsBeforeReset,
        TimeSpan? MarginBeforeReset,
        string Text);

    public record SeriesPoint(DateTime Time, double Value);

    public record Estimate(double Value, double Delta, TimeSpan SampleAge);

    // Logic thuần (không WPF, không IO) để dễ kiểm thử.
    public static class StatsService
    {
        // Hai mẫu cách nhau quá lâu thì phần chênh lệch không thể quy cho model nào.
        public static readonly TimeSpan MaxPairGap = TimeSpan.FromMinutes(10);

        // % tụt ít hơn mức này coi là nhiễu (làm tròn, 2 nguồn lệch nhau), không phải reset chu kỳ.
        public const double ResetDropThreshold = 5;

        // Ước lượng % quota 5h mỗi model đã tiêu trong cửa sổ thời gian.
        // Quota dùng chung nhiều nơi, nên đây chỉ là ước lượng.
        public static ModelBurnReport ComputeModelBurn(IReadOnlyList<UsageSample> samples, DateTime now, TimeSpan window) =>
            ComputeBurnBy(samples, now, window, s => s.Model);

        // Ước lượng % quota 5h mỗi thư mục dự án (CLI) đã tiêu trong cửa sổ thời gian.
        public static ModelBurnReport ComputeProjectBurn(IReadOnlyList<UsageSample> samples, DateTime now, TimeSpan window) =>
            ComputeBurnBy(samples, now, window, s => s.Project);

        private static ModelBurnReport ComputeBurnBy(IReadOnlyList<UsageSample> samples, DateTime now, TimeSpan window, Func<UsageSample, string?> keySelector)
        {
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            DateTime from = now - window;

            for (int i = 1; i < samples.Count; i++)
            {
                var a = samples[i - 1];
                var b = samples[i];

                // Cả hai đầu phải nằm trong cửa sổ: cặp bắc qua biên sẽ quy trọn phần tiêu
                // của khoảng thời gian trước đó vào cửa sổ này.
                if (a.Time < from || b.Time > now) continue;
                string? key = keySelector(b);
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (b.Time - a.Time > MaxPairGap || b.Time < a.Time) continue;

                double delta = b.FiveHour - a.FiveHour;
                if (delta <= 0) continue; // 0 = không tiêu; âm = đã reset chu kỳ

                totals[key] = totals.GetValueOrDefault(key) + delta;
            }

            double sum = totals.Values.Sum();
            var items = totals
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new ModelBurn(kv.Key, kv.Value, sum > 0 ? kv.Value / sum : 0))
                .ToList();

            return new ModelBurnReport(items, sum);
        }

        // Tính ngân sách quota tuần an toàn mỗi ngày (Weekly Quota Guard).
        public static string? ComputeWeeklyBudgetNote(double weeklyPercent, DateTime? weeklyResetTime, DateTime now)
        {
            if (!weeklyResetTime.HasValue || weeklyResetTime.Value <= now) return null;
            double remaining = Math.Max(0, 100 - weeklyPercent);
            double daysLeft = (weeklyResetTime.Value - now).TotalDays;
            if (daysLeft <= 0.05) return null;

            double perDay = remaining / Math.Max(1.0, daysLeft);
            return daysLeft >= 1.0
                ? $"Ngân sách tuần: còn {remaining:0}% / {daysLeft:0.0} ngày (~{perDay:0}%/ngày)"
                : $"Ngân sách tuần: còn {remaining:0}% cho {Fmt(weeklyResetTime.Value - now)} cuối";
        }

        // Ước lượng nhanh số tin nhắn an toàn còn lại theo từng dòng model trong chu kỳ 5h.
        public static string? EstimateRemainingPromptsNote(double percentUsed)
        {
            double rem = Math.Max(0, 100 - percentUsed);
            if (rem < 1) return null;
            int opus = Math.Max(1, (int)Math.Floor(rem * 0.45));   // ~45 tin / 100%
            int sonnet = Math.Max(1, (int)Math.Floor(rem * 1.2));  // ~120 tin / 100%
            int haiku = Math.Max(1, (int)Math.Floor(rem * 3.5));   // ~350 tin / 100%
            return $"Ước còn: ~{opus} Opus | ~{sonnet} Sonnet | ~{haiku} Haiku";
        }

        // Dự báo thời điểm chạm 100% của chu kỳ 5h dựa trên nhịp tiêu gần đây.
        public static Projection ComputeProjection(
            IReadOnlyList<UsageSample> samples, DateTime now, DateTime? resetTime,
            TimeSpan? lookback = null)
        {
            var look = lookback ?? TimeSpan.FromMinutes(60);

            var run = LatestRun(samples, now, look);
            if (run.Count == 0)
                return None("Chưa có dữ liệu để dự báo");

            var last = run[^1];
            if (last.FiveHour >= 100)
                return new Projection(true, last.Time, 0, true, null, "Đã chạm trần 5h");

            if (run.Count < 2 || (last.Time - run[0].Time) < TimeSpan.FromMinutes(10))
                return None("Chưa đủ dữ liệu để dự báo");

            double rate = RegressionSlopePerHour(run);
            if (rate < 0.5)
                return new Projection(false, null, Math.Max(rate, 0), false, null, "Nhịp tiêu thấp — an toàn tới khi reset");

            DateTime hit = last.Time + TimeSpan.FromHours((100 - last.FiveHour) / rate);
            if (hit < now) hit = now;

            bool before = resetTime.HasValue && resetTime.Value > now && hit < resetTime.Value;
            TimeSpan? margin = before ? resetTime!.Value - hit : null;

            string text;
            if (before)
                text = $"Với nhịp {rate:0}%/giờ, sẽ chạm 100% lúc {hit:HH:mm} — trước giờ reset {Fmt(margin!.Value)}";
            else if (resetTime.HasValue && resetTime.Value > now)
                text = $"Nhịp {rate:0}%/giờ — an toàn tới khi reset ({resetTime.Value:HH:mm})";
            else
                text = $"Nhịp {rate:0}%/giờ — ước chạm 100% lúc {hit:HH:mm}";

            return new Projection(true, hit, rate, before, margin, text);
        }

        // Đoạn mẫu liên tục sau lần reset gần nhất trong cửa sổ `look` (reset = % tụt ≥ ResetDropThreshold).
        private static List<UsageSample> LatestRun(IReadOnlyList<UsageSample> samples, DateTime now, TimeSpan look)
        {
            var recent = samples.Where(s => s.Time >= now - look && s.Time <= now).OrderBy(s => s.Time).ToList();

            int start = 0;
            for (int i = 1; i < recent.Count; i++)
            {
                if (recent[i - 1].FiveHour - recent[i].FiveHour >= ResetDropThreshold) start = i;
            }
            return recent.Skip(start).ToList();
        }

        // Ước tính % 5h ngay lúc này khi mẫu cuối đã hơi cũ (nguồn chỉ ghi mỗi ~15 phút).
        // Bảo thủ: nhịp = min(hồi quy 60 phút, nhịp 2 mẫu cuối), chỉ khi mẫu cuối cũ 2–20 phút,
        // cộng tối đa MaxEstimateDelta điểm, và không ước tính qua giờ reset.
        public const double MaxEstimateDelta = 10;
        public static readonly TimeSpan MinEstimateAge = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan MaxEstimateAge = TimeSpan.FromMinutes(20);

        public static Estimate? EstimateNow(IReadOnlyList<UsageSample> samples, DateTime now, DateTime? resetTime)
        {
            var run = LatestRun(samples, now, TimeSpan.FromMinutes(60));
            if (run.Count < 2) return null;

            var last = run[^1];
            var prev = run[^2];
            var age = now - last.Time;

            if (age < MinEstimateAge || age > MaxEstimateAge) return null;
            if (last.FiveHour >= 100) return null;
            if (resetTime.HasValue && resetTime.Value <= now) return null;
            if ((last.Time - run[0].Time) < TimeSpan.FromMinutes(10)) return null;

            double lastGapHours = (last.Time - prev.Time).TotalHours;
            if (lastGapHours <= 0) return null;

            double regression = RegressionSlopePerHour(run);
            double recentRate = (last.FiveHour - prev.FiveHour) / lastGapHours;
            double rate = Math.Min(regression, recentRate);
            if (rate < 0.5) return null;

            double delta = Math.Min(rate * age.TotalHours, MaxEstimateDelta);
            if (delta < 0.5) return null;

            return new Estimate(Math.Min(100, last.FiveHour + delta), delta, age);
        }

        public static IReadOnlyList<SeriesPoint> BuildSeries(
            IReadOnlyList<UsageSample> samples, DateTime now, TimeSpan window,
            Func<UsageSample, double> selector)
        {
            DateTime from = now - window;
            return samples
                .Where(s => s.Time >= from && s.Time <= now)
                .OrderBy(s => s.Time)
                .Select(s => new SeriesPoint(s.Time, selector(s)))
                .ToList();
        }

        // Độ dốc bình phương tối thiểu (%/giờ): bền hơn so với chỉ lấy hai đầu mút khi dữ liệu nhiễu.
        private static double RegressionSlopePerHour(IReadOnlyList<UsageSample> run)
        {
            var t0 = run[0].Time;
            double n = run.Count;
            double[] x = run.Select(s => (s.Time - t0).TotalHours).ToArray();
            double[] y = run.Select(s => s.FiveHour).ToArray();
            double mx = x.Average();
            double my = y.Average();

            double sxx = x.Sum(v => (v - mx) * (v - mx));
            if (sxx <= 0) return 0;

            double sxy = 0;
            for (int i = 0; i < x.Length; i++) sxy += (x[i] - mx) * (y[i] - my);
            return sxy / sxx;
        }

        public static string Fmt(TimeSpan span)
        {
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{Math.Max(1, (int)span.TotalMinutes)}m";
        }

        // Phát hiện dấu hiệu Claude Code bị kẹt vòng lặp (đốt >= 10% quota trong vòng 5 phút).
        public static double? DetectInfiniteLoop(IReadOnlyList<UsageSample> samples, DateTime now, string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            DateTime cutoff = now.AddMinutes(-5);
            var recent = samples
                .Where(s => s.Time >= cutoff && s.Time <= now && string.Equals(s.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(s => s.Time)
                .ToList();

            if (recent.Count < 4) return null;
            double delta = recent[^1].FiveHour - recent[0].FiveHour;
            return delta >= 10 ? delta : null;
        }

        private static Projection None(string text) => new(false, null, 0, false, null, text);
    }
}
