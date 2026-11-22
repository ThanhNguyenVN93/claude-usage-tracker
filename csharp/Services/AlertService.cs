using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeUsageTracker.Services
{
    public enum AlertLevel { None = 0, Info = 1, Warn = 2, Critical = 3 }

    public record Alert(string Title, string Message, AlertLevel Level);

    public class AlertConfig
    {
        public bool Enabled { get; set; } = true;
        public int[] Thresholds { get; set; } = { 80, 95 };
        public double ContextWarnPercent { get; set; } = 70;
        public bool ResetReminder { get; set; } = true;
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

        // Trạng thái đổi mà không sinh cảnh báo (ghi mốc "đã chạm trần", gỡ mốc để tái vũ trang)
        // vẫn phải được lưu, nếu không restart là mất và nhắc reset không bao giờ bắn.
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Dirty { get; private set; }

        public void Mark(string key, DateTime at)
        {
            Fired[key] = at;
            Dirty = true;
        }

        public void Unmark(string key)
        {
            if (Fired.Remove(key)) Dirty = true;
        }

        public void ClearDirty() => Dirty = false;
    }

    // Logic thuần: nhận số liệu + trạng thái, trả về cảnh báo mới (và cập nhật trạng thái).
    public static class AlertEngine
    {
        private const double ReArmMargin = 5;          // % phải tụt xuống dưới ngưỡng bao nhiêu mới báo lại
        private const double ContextReArmMargin = 15;  // sau /compact ngữ cảnh tụt mạnh
        private static readonly TimeSpan ProjectionCooldown = TimeSpan.FromMinutes(30);
        private const double HitLimitAt = 95;       // coi là đã chạm trần từ mức này
        private const double ResetBelow = 50;       // sau khi chạm trần mà tụt dưới mức này => đã reset
        private const int MaxStateKeys = 100;

        public static List<Alert> Evaluate(AlertInput input, AlertConfig cfg, AlertState state)
        {
            var alerts = new List<Alert>();
            if (!cfg.Enabled) return alerts;

            EvaluateQuota("fh", "Chu kỳ 5 giờ", input.FiveHour, cfg.Thresholds, input.Now, state, alerts);
            if (input.SevenDay.HasValue)
                EvaluateQuota("sd", "Hạn ngạch tuần", input.SevenDay.Value, cfg.Thresholds, input.Now, state, alerts);

            if (cfg.ResetReminder)
            {
                EvaluateResetReminder("fh", "Chu kỳ 5 giờ", input.FiveHour, input.Now, state, alerts);
                if (input.SevenDay.HasValue)
                    EvaluateResetReminder("sd", "Hạn ngạch tuần", input.SevenDay.Value, input.Now, state, alerts);
            }

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
                        state.Mark(key, now);
                        crossed = t; // nếu nhảy qua nhiều mốc cùng lúc chỉ báo mốc cao nhất
                    }
                }
                else if (value < t - ReArmMargin)
                {
                    state.Unmark(key);
                }
            }

            if (crossed.HasValue)
            {
                var level = crossed.Value >= 95 ? AlertLevel.Critical : AlertLevel.Warn;
                string hint = crossed.Value >= 95
                    ? " Gõ /model haiku trong Claude Code CLI để tiết kiệm quota (hoặc bật 'Tự đổi model' trong Cài đặt)."
                    : crossed.Value >= 85
                    ? " Gõ /model sonnet trong Claude Code CLI để tiết kiệm quota."
                    : "";
                alerts.Add(new Alert($"{label}: {value:0}%", $"Đã vượt mốc {crossed.Value}%.{hint}", level));
            }
        }

        // Nhắc một lần khi hạn ngạch đã reset sau lúc người dùng chạm trần ("quay lại làm tiếp").
        // Chỉ nhắc, không tự chạy lệnh Claude nào.
        private static void EvaluateResetReminder(string prefix, string label, double value,
            DateTime now, AlertState state, List<Alert> alerts)
        {
            string key = $"hitlimit:{prefix}";

            if (value >= HitLimitAt)
            {
                if (!state.Fired.ContainsKey(key)) state.Mark(key, now);
                return;
            }

            if (state.Fired.ContainsKey(key) && value < ResetBelow)
            {
                state.Unmark(key);
                alerts.Add(new Alert(
                    $"{label} đã reset",
                    $"Hạn ngạch đã về {value:0}% — quay lại làm tiếp được rồi.",
                    AlertLevel.Info));
            }
        }

        private static void EvaluateProjection(AlertInput input, AlertState state, List<Alert> alerts)
        {
            const string key = "proj";
            var p = input.Projection;

            if (p == null || !p.HitsBeforeReset || input.FiveHour >= 100)
            {
                state.Unmark(key);
                return;
            }

            if (state.Fired.TryGetValue(key, out var last) && input.Now - last < ProjectionCooldown)
                return;

            state.Mark(key, input.Now);
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
                    state.Mark(key, input.Now);
                    alerts.Add(new Alert(
                        $"Ngữ cảnh phiên đã {ctx:0}%",
                        "Mỗi lượt đều đọc lại toàn bộ ngữ cảnh nên rất tốn quota. Cân nhắc /compact hoặc mở phiên mới.",
                        AlertLevel.Warn));
                }
            }
            else if (ctx < cfg.ContextWarnPercent - ContextReArmMargin)
            {
                state.Unmark(key);
            }
        }

        // Chỉ "ctx:{sessionId}" mới phình vô hạn; fh/sd/hitlimit/proj là tập hữu hạn và mang ngữ nghĩa
        // (xoá nhầm hitlimit là mất luôn nhắc reset), nên chỉ tỉa key ngữ cảnh.
        private static void PruneState(AlertState state)
        {
            if (state.Fired.Count <= MaxStateKeys) return;

            var ctxKeys = state.Fired.Where(kv => kv.Key.StartsWith("ctx:", StringComparison.Ordinal))
                                     .OrderBy(kv => kv.Value)
                                     .Select(kv => kv.Key)
                                     .ToList();

            int toRemove = state.Fired.Count - MaxStateKeys;
            foreach (var key in ctxKeys.Take(toRemove)) state.Unmark(key);
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
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(state));
                state.ClearDirty();
            }
            catch { /* không lưu được thì lần sau có thể báo lặp, chấp nhận */ }
        }
    }
}
