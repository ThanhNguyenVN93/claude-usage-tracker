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

    // Logic thuần (không WPF, không IO) để dễ kiểm thử.
    public static class StatsService
    {
        // Hai mẫu cách nhau quá lâu thì phần chênh lệch không thể quy cho model nào.
        public static readonly TimeSpan MaxPairGap = TimeSpan.FromMinutes(10);

        // % tụt ít hơn mức này coi là nhiễu (làm tròn, 2 nguồn lệch nhau), không phải reset chu kỳ.
        public const double ResetDropThreshold = 5;

        // Ước lượng % quota 5h mỗi model đã tiêu trong cửa sổ thời gian.
        // Quota dùng chung nhiều nơi, nên đây chỉ là ước lượng.
        public static ModelBurnReport ComputeModelBurn(IReadOnlyList<UsageSample> samples, DateTime now, TimeSpan window)
        {
            var totals = new Dictionary<string, double>();
            DateTime from = now - window;

            for (int i = 1; i < samples.Count; i++)
            {
                var a = samples[i - 1];
                var b = samples[i];

                if (b.Time < from || b.Time > now) continue;
                if (b.Model == null) continue;
                if (b.Time - a.Time > MaxPairGap || b.Time < a.Time) continue;

                double delta = b.FiveHour - a.FiveHour;
                if (delta <= 0) continue; // 0 = không tiêu; âm = đã reset chu kỳ

                totals[b.Model] = totals.GetValueOrDefault(b.Model) + delta;
            }

            double sum = totals.Values.Sum();
            var items = totals
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new ModelBurn(kv.Key, kv.Value, sum > 0 ? kv.Value / sum : 0))
                .ToList();

            return new ModelBurnReport(items, sum);
        }

        // Dự báo thời điểm chạm 100% của chu kỳ 5h dựa trên nhịp tiêu gần đây.
        public static Projection ComputeProjection(
            IReadOnlyList<UsageSample> samples, DateTime now, DateTime? resetTime,
            TimeSpan? lookback = null)
        {
            var look = lookback ?? TimeSpan.FromMinutes(60);

            var recent = samples.Where(s => s.Time >= now - look && s.Time <= now).OrderBy(s => s.Time).ToList();
            if (recent.Count == 0)
                return None("Chưa có dữ liệu để dự báo");

            // Chỉ giữ đoạn liên tục sau lần reset gần nhất (fh giảm).
            int start = 0;
            for (int i = 1; i < recent.Count; i++)
            {
                if (recent[i - 1].FiveHour - recent[i].FiveHour >= ResetDropThreshold) start = i;
            }
            var run = recent.Skip(start).ToList();

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

        private static Projection None(string text) => new(false, null, 0, false, null, text);
    }
}
