using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClaudeUsageTracker.Views
{
    public partial class ToastWindow : Window
    {
        private static ToastWindow? _current;

        // Nhiều cảnh báo nổ liên tiếp (vượt mốc 5h + vượt mốc tuần + ngữ cảnh phình) thì
        // trước đây cái sau đè cái trước ngay lập tức, người dùng không kịp đọc. Xếp hàng thay vì đè.
        private static readonly Queue<(string title, string body, string icon, Action? onAction)> Pending = new();
        private const int MaxPending = 5;

        private readonly DispatcherTimer _timer;
        private double _remaining;
        private double _barFullWidth;
        private const double Duration = 7.0;

        public event Action? ActionClicked;

        private ToastWindow(string icon, string title, string body)
        {
            InitializeComponent();
            TxtIcon.Text  = icon;
            TxtTitle.Text = title;
            TxtBody.Text  = body;
            _remaining    = Duration;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += OnTick;

            Loaded += (s, e) =>
            {
                // Loaded không bảo đảm đã arrange xong: ActualWidth/Height có thể còn 0 → toast lệch góc.
                UpdateLayout();
                PositionBottomRight();
                _barFullWidth = ((Border)PbFill.Parent).ActualWidth;
                PbFill.Width = _barFullWidth;
                ((Storyboard)FindResource("FadeIn")).Begin(this);
                _timer.Start();
            };

            MouseLeftButtonDown += (s, e) =>
            {
                if (_dismissed) return; // đang fade-out: không chạy action lần nữa
                var action = ActionClicked;
                Dismiss();
                action?.Invoke();
            };
        }

        public static void ShowToast(string title, string body, string icon = "🔔", Action? onAction = null)
        {
            if (_current != null)
            {
                // Hàng đợi có trần: cảnh báo cũ nhất bị bỏ chứ không dồn vô hạn.
                if (Pending.Count >= MaxPending) Pending.Dequeue();
                Pending.Enqueue((title, body, icon, onAction));
                return;
            }
            Display(title, body, icon, onAction);
        }

        private static void Display(string title, string body, string icon, Action? onAction)
        {
            var t = new ToastWindow(icon, title, body);
            if (onAction != null) t.ActionClicked += onAction;
            _current = t;
            t.Show();
        }

        private static void ShowNextPending()
        {
            if (_current != null || Pending.Count == 0) return;
            var next = Pending.Dequeue();
            Display(next.title, next.body, next.icon, next.onAction);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _remaining -= 0.05;
            double ratio = Math.Max(_remaining / Duration, 0);
            PbFill.Width = _barFullWidth * ratio;
            if (_remaining <= 0) Dismiss();
        }

        private bool _dismissed;

        public void Dismiss()
        {
            // Tick hết giờ + click/✕ + toast mới cùng lúc có thể gọi Dismiss nhiều lần → chạy FadeOut và Close() lặp.
            if (_dismissed) return;
            _dismissed = true;
            _timer.Stop();
            try
            {
                var sb = (Storyboard)FindResource("FadeOut");
                sb.Completed += (s, e) => { try { Close(); } catch { } };
                sb.Begin(this);
            }
            catch { try { Close(); } catch { } }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Dismiss();

        protected override void OnClosed(EventArgs e)
        {
            // Đóng ngoài luồng Dismiss (vd. Shutdown) vẫn phải dừng timer và không để _current trỏ vào cửa sổ đã đóng.
            _dismissed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            ActionClicked = null;
            if (_current == this)
            {
                _current = null;
                ShowNextPending();
            }
            base.OnClosed(e);
        }

        private void PositionBottomRight()
        {
            var area = ScreenPlacement.WorkAreaForCursor(this);
            Left = area.Right  - ActualWidth  - 16;
            Top  = area.Bottom - ActualHeight - 16;
        }
    }
}
