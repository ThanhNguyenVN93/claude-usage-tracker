using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeUsageTracker.Models;

namespace ClaudeUsageTracker.Views
{
    public partial class CountdownWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private DateTime? _resetTime;

        public CountdownWindow()
        {
            InitializeComponent();
            _resetTime = null;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;

            Loaded += (s, e) =>
            {
                UpdateLayout();   // ActualWidth có thể còn 0 nếu chưa arrange
                PositionTopRight();
                ((Storyboard)FindResource("FadeIn")).Begin(this);
                _timer.Start();
                UpdateDisplay();
            };

            // Nút ✕ chỉ Hide() → không có Closed, timer 1s chạy mãi khi cửa sổ ẩn. Dừng/chạy theo trạng thái hiển thị.
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { UpdateDisplay(); _timer.Start(); }
                else _timer.Stop();
            };
        }

        private static readonly System.Windows.Media.SolidColorBrush GreenBrush  = Frozen(0xA6, 0xE3, 0xA1);
        private static readonly System.Windows.Media.SolidColorBrush YellowBrush = Frozen(0xF9, 0xE2, 0xAF);
        private static readonly System.Windows.Media.SolidColorBrush RedBrush    = Frozen(0xF3, 0x8B, 0xA8);

        private static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        public void ApplyData(UsageInfo? info)
        {
            if (info == null) return;
            Dispatcher.Invoke(() =>
            {
                _resetTime = info.ResetTime != default ? info.ResetTime : null;
                double pct = info.PercentUsed;
                TxtPercent.Text = pct > 0 ? $"{pct:0.#}% đã dùng" : string.Empty;
                UpdateDisplay();
            });
        }

        private void OnTick(object? sender, EventArgs e) => UpdateDisplay();

        private void UpdateDisplay()
        {
            // Nguồn Desktop không kèm giờ reset — thà nói "chưa rõ" còn hơn bịa ra mốc 5 giờ
            // hoặc hiện "đã reset" ngay khi vừa mở.
            if (_resetTime == null)
            {
                TxtTime.Text = "--:--:--";
                TxtLabel.Text = "chưa rõ giờ reset";
                TxtResetAt.Text = string.Empty;
                TxtTime.Foreground = System.Windows.Media.Brushes.Gray;
                return;
            }

            TimeSpan remaining = _resetTime.Value - DateTime.Now;
            if (remaining <= TimeSpan.Zero)
            {
                TxtTime.Text = "00:00:00";
                TxtLabel.Text = "đã reset";
                TxtResetAt.Text = string.Empty;
                TxtTime.Foreground = System.Windows.Media.Brushes.Gray;
            }
            else
            {
                TxtTime.Text = $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                TxtLabel.Text = "đến khi reset";
                TxtResetAt.Text = _resetTime.Value.ToString("HH:mm dd/MM");

                // Color: green→yellow→red as time decreases
                // (brush tĩnh, đã Freeze — trước đây cấp phát brush mới mỗi giây)
                if (remaining.TotalMinutes > 60)
                    TxtTime.Foreground = GreenBrush;
                else if (remaining.TotalMinutes > 15)
                    TxtTime.Foreground = YellowBrush;
                else
                    TxtTime.Foreground = RedBrush;
            }
        }

        private void PositionTopRight()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top  = area.Top  + 16;
        }

        private void TitleBar_Drag(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Hide();

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            base.OnClosed(e);
        }
    }
}
