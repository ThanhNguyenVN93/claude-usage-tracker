using System;

namespace ClaudeUsageTracker.Services
{
    public enum ModelAction { None, Apply, Restore, Forget }

    // RestoreMargin: quota phải hạ dưới (SonnetAt - RestoreMargin) mới khôi phục model gốc.
    // StepMargin: quota phải hạ dưới (HaikuAt - StepMargin) mới nới từ haiku về sonnet.
    public record ModelSwitchOptions(double SonnetAt, double HaikuAt, double RestoreMargin = 10, double StepMargin = 5);

    // Trạng thái tool đã ghi: model gốc của người dùng và model tool đã đặt.
    public class ModelSwitchState
    {
        public string? Original { get; set; }
        public string? Applied { get; set; }
        public DateTime? At { get; set; }
    }

    public record ModelDecision(ModelAction Action, string? Target, string Reason);

    // Logic thuần: quyết định có nên đổi model mặc định (cho phiên MỚI) hay không.
    public static class ModelSwitchEngine
    {
        public static int Rank(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return 3; // không rõ => coi là đắt nhất
            string m = model.ToLowerInvariant();
            if (m.Contains("haiku")) return 1;
            if (m.Contains("sonnet")) return 2;
            return 3; // opus hoặc tên lạ
        }

        public static string? DesiredTier(double quota, ModelSwitchOptions o) =>
            quota >= o.HaikuAt ? "haiku" : quota >= o.SonnetAt ? "sonnet" : null;

        public static ModelDecision Decide(double quota, ModelSwitchOptions o, ModelSwitchState state, string? currentModel)
        {
            string? desired = DesiredTier(quota, o);

            // Tool chưa đặt gì.
            if (state.Applied == null)
            {
                if (desired == null) return new(ModelAction.None, null, "quota còn dư");
                if (Rank(currentModel) <= Rank(desired)) return new(ModelAction.None, null, "model hiện tại đã đủ rẻ");
                return new(ModelAction.Apply, desired, $"quota {quota:0}% đạt ngưỡng");
            }

            // Người dùng tự đổi model sau khi tool đặt: không đè lên, bỏ theo dõi.
            if (!string.Equals(currentModel, state.Applied, StringComparison.OrdinalIgnoreCase))
                return new(ModelAction.Forget, null, "người dùng đã tự đổi model");

            if (desired == null)
            {
                // Chỉ khôi phục khi quota đã hạ rõ rệt (chống nhấp nháy quanh ngưỡng).
                if (quota < o.SonnetAt - o.RestoreMargin)
                    return new(ModelAction.Restore, state.Original, "quota đã hạ");
                return new(ModelAction.None, null, "chờ quota hạ thêm");
            }

            if (Rank(desired) < Rank(state.Applied))
                return new(ModelAction.Apply, desired, "quota tăng thêm");

            // Đang haiku mà quota tụt về mức sonnet: nới lại, nhưng có trễ.
            if (Rank(desired) > Rank(state.Applied) && quota < o.HaikuAt - o.StepMargin)
                return new(ModelAction.Apply, desired, "quota hạ bớt");

            return new(ModelAction.None, null, "giữ nguyên");
        }
    }
}
