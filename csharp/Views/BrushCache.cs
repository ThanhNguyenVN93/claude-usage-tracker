using System.Collections.Generic;
using System.Windows.Media;

namespace ClaudeUsageTracker.Views
{
    // Brush đông lạnh dùng lại theo mã hex. Chỉ gọi từ UI thread nên không cần khoá.
    public static class BrushCache
    {
        private static readonly Dictionary<string, SolidColorBrush> Cache = new();

        public static SolidColorBrush Get(string hex)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;

            // Qualify đầy đủ: UseWindowsForms kéo System.Drawing.Color/ColorConverter vào cùng phạm vi.
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
    }
}
