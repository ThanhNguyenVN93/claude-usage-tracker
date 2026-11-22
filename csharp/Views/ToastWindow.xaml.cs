using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClaudeUsageTracker.Views
{
    public partial class ToastWindow : Window
    {
        private static ToastWindow? _current;

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

        public static ToastWindow ShowToast(string title, string body, string icon = "🔔", Action? onAction = null)
        {
            _current?.Dismiss();
            var t = new ToastWindow(icon, title, body);
            if (onAction != null) t.ActionClicked += onAction;
            _current = t;
            t.Show();
            return t;
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
            if (_current == this) _current = null;
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
            if (_current == this) _current = null;
            base.OnClosed(e);
        }

        private void PositionBottomRight()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right  - ActualWidth  - 16;
            Top  = area.Bottom - ActualHeight - 16;
        }
    }
}
