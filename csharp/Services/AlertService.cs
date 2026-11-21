using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeUsageTracker.Services
{
    public enum AlertLevel { None = 0, Warn = 1, Critical = 2 }

    public record Alert(string Title, string Message, AlertLevel Level);

    public class AlertConfig
    {
        public bool Enabled { get; set; } = true;
        public int[] Thresholds { get; set; } = { 80, 95 };
        public double ContextWarnPercent { get; set; } = 70;
    }

    public record AlertInput(
        double FiveHour,
        double? SevenDay,
        double? ContextPct,
        string? SessionId,
        Projection? Projection,
        DateTime Now);

    // Các cảnh báo đã bắn (key -> lúc bắn), lưu ra đĩa để không báo lặp sau khi restart.
    public class AlertState
    {
        public Dictionary<string, DateTime> Fired { get; set; } = new();
    }

    // Logic thuần: nhận số liệu + trạng thái, trả về cảnh báo mới (và cập nhật trạng thái).
    public static class AlertEngine
    {
        private const double ReArmMargin = 5;          // % phải tụt xuống dưới ngưỡng bao nhiêu mới báo lại
        private const double ContextReArmMargin = 15;  // sau /compact ngữ cảnh tụt mạnh
        private static readonly TimeSpan ProjectionCooldown = TimeSpan.FromMinutes(30);
        private const int MaxStateKeys = 100;

        public static List<Alert> Evaluate(AlertInput input, AlertConfig cfg, AlertState state)
        {
            var alerts = new List<Alert>();
            if (!cfg.Enabled) return alerts;

            EvaluateQuota("fh", "Chu kỳ 5 giờ", input.FiveHour, cfg.Thresholds, input.Now, state, alerts);
            if (input.SevenDay.HasValue)
                EvaluateQuota("sd", "Hạn ngạch tuần", input.SevenDay.Value, cfg.Thresholds, input.Now, state, alerts);

            EvaluateProjection(input, state, alerts);
            EvaluateContext(input, cfg, state, alerts);

            PruneState(state);
            return alerts;
        }

        public static AlertLevel MaxLevel(IEnumerable<Alert> alerts) =>
            alerts.Select(a => a.Level).DefaultIfEmpty(AlertLevel.None).Max();

        private static void EvaluateQuota(string prefix, string label, double value, int[] thresholds,
            DateTime now, AlertState state, List<Alert> alerts)
        {
            int? crossed = null;
            foreach (int t in thresholds.OrderBy(x => x))
            {
                string key = $"{prefix}:{t}";
                if (value >= t)
                {
                    if (!state.Fired.ContainsKey(key))
                    {
                        state.Fired[key] = now;
                        crossed = t; // nếu nhảy qua nhiều mốc cùng lúc chỉ báo mốc cao nhất
                    }
                }
                else if (value < t - ReArmMargin)
                {
                    state.Fired.Remove(key);
                }
            }

            if (crossed.HasValue)
            {
                var level = crossed.Value >= 95 ? AlertLevel.Critical : AlertLevel.Warn;
                alerts.Add(new Alert($"{label}: {value:0}%", $"Đã vượt mốc {crossed.Value}%.", level));
            }
        }

        private static void EvaluateProjection(AlertInput input, AlertState state, List<Alert> alerts)
        {
            const string key = "proj";
            var p = input.Projection;

            if (p == null || !p.HitsBeforeReset || input.FiveHour >= 100)
            {
                state.Fired.Remove(key);
                return;
            }

            if (state.Fired.TryGetValue(key, out var last) && input.Now - last < ProjectionCooldown)
                return;

            state.Fired[key] = input.Now;
            alerts.Add(new Alert("Sắp hết hạn ngạch 5h", p.Text, AlertLevel.Critical));
        }

        private static void EvaluateContext(AlertInput input, AlertConfig cfg, AlertState state, List<Alert> alerts)
        {
            if (!input.ContextPct.HasValue) return;

            string key = $"ctx:{input.SessionId ?? "?"}";
            double ctx = input.ContextPct.Value;

            if (ctx >= cfg.ContextWarnPercent)
            {
                if (!state.Fired.ContainsKey(key))
                {
                    state.Fired[key] = input.Now;
                    alerts.Add(new Alert(
                        $"Ngữ cảnh phiên đã {ctx:0}%",
                        "Mỗi lượt đều đọc lại toàn bộ ngữ cảnh nên rất tốn quota. Cân nhắc /compact hoặc mở phiên mới.",
                        AlertLevel.Warn));
                }
            }
            else if (ctx < cfg.ContextWarnPercent - ContextReArmMargin)
            {
                state.Fired.Remove(key);
            }
        }

        private static void PruneState(AlertState state)
        {
            if (state.Fired.Count <= MaxStateKeys) return;
            foreach (var key in state.Fired.OrderBy(kv => kv.Value).Take(state.Fired.Count - MaxStateKeys).Select(kv => kv.Key).ToList())
                state.Fired.Remove(key);
        }
    }

    public static class AlertStateStore
    {
        public static AlertState Load(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AlertState>(File.ReadAllText(path)) ?? new AlertState();
            }
            catch { /* file hỏng thì bắt đầu lại */ }
            return new AlertState();
        }

        public static void Save(string path, AlertState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(state));
            }
            catch { /* không lưu được thì lần sau có thể báo lặp, chấp nhận */ }
        }
    }
}
