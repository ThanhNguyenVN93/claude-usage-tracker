using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeUsageTracker.Models;

namespace ClaudeUsageTracker.Views
{
    public partial class ToastNotificationWindow : Window
    {
        private readonly Action? _onFinished;
        private readonly DispatcherTimer _timer;

        public ToastNotificationWindow(UsageInfo info, Action? onFinished = null)
        {
            InitializeComponent();
            _onFinished = onFinished;

            TxtUsage.Text = $"Quota: {info.PercentUsed:0}% đã dùng | {info.MessagesRemaining}";
            TxtReset.Text = $"Reset chu kỳ 5h: {info.ResetCountdown}";

            PositionAtBottomRight();

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _timer.Tick += (s, e) =>
            {
                _timer.Stop();
                var fadeOut = (Storyboard)FindResource("FadeOut");
                fadeOut.Begin(this);
            };
        }

        private void PositionAtBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 16;
            Top = workArea.Bottom - Height - 16;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var fadeIn = (Storyboard)FindResource("FadeIn");
            fadeIn.Begin(this);
            _timer.Start();
        }

        private void FadeOut_Completed(object? sender, EventArgs e)
        {
            Close();
            _onFinished?.Invoke();
        }
    }
}
