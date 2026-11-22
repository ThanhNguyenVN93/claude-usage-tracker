using System;
using System.Windows;
using System.Windows.Media;

namespace ClaudeUsageTracker.Views
{
    // SystemParameters.WorkArea chỉ mô tả màn hình chính, nên mọi cửa sổ nổi của tool
    // đều nhảy về màn hình đó dù người dùng đang làm việc ở màn phụ.
    public static class ScreenPlacement
    {
        // Vùng làm việc (đơn vị WPF) của màn hình đang chứa con trỏ chuột.
        public static Rect WorkAreaForCursor(Visual? visual)
        {
            try
            {
                var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
                var wa = screen.WorkingArea;

                // WinForms trả pixel vật lý; Left/Top của WPF là đơn vị độc lập thiết bị.
                // Lưu ý: khi mỗi màn một mức scale khác nhau, DPI lấy được là của màn hình
                // cửa sổ đang đứng — đủ đúng cho trường hợp phổ biến (cùng mức scale).
                double sx = 1, sy = 1;
                if (visual != null)
                {
                    var dpi = VisualTreeHelper.GetDpi(visual);
                    if (dpi.DpiScaleX > 0) sx = dpi.DpiScaleX;
                    if (dpi.DpiScaleY > 0) sy = dpi.DpiScaleY;
                }

                return new Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        // Toạ độ đã lưu có thể trỏ vào màn hình đã rút ra: kéo về vùng nhìn thấy được.
        public static bool IsOnAnyScreen(double left, double top, double width, double height)
        {
            try
            {
                var rect = new System.Drawing.Rectangle(
                    (int)left, (int)top, (int)Math.Max(width, 1), (int)Math.Max(height, 1));

                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                    if (screen.WorkingArea.IntersectsWith(rect)) return true;

                return false;
            }
            catch { return true; }   // không kiểm tra được thì cứ tin toạ độ đã lưu
        }
    }
}
