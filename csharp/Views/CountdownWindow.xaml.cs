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
        private DateTime _resetTime;

        public CountdownWindow()
        {
            InitializeComponent();
            _resetTime = DateTime.Now.AddHours(5);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;

            Loaded += (s, e) =>
            {
                PositionTopRight();
                ((Storyboard)FindResource("FadeIn")).Begin(this);
                _timer.Start();
                UpdateDisplay();
            };
        }

        public void ApplyData(UsageInfo? info)
        {
            if (info == null) return;
            Dispatcher.Invoke(() =>
            {
                _resetTime = info.ResetTime != default ? info.ResetTime : DateTime.Now.AddHours(5);
                double pct = info.PercentUsed;
                TxtPercent.Text = pct > 0 ? $"{pct:0.#}% đã dùng" : string.Empty;
                UpdateDisplay();
            });
        }

        private void OnTick(object? sender, EventArgs e) => UpdateDisplay();

        private void UpdateDisplay()
        {
            TimeSpan remaining = _resetTime - DateTime.Now;
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
                TxtResetAt.Text = _resetTime.ToString("HH:mm dd/MM");

                // Color: green→yellow→red as time decreases
                if (remaining.TotalMinutes > 60)
                    TxtTime.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1)); // green
                else if (remaining.TotalMinutes > 15)
                    TxtTime.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xF9, 0xE2, 0xAF)); // yellow
                else
                    TxtTime.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8)); // red
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
    }
}
