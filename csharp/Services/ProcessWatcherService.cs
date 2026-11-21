using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ClaudeUsageTracker.Services
{
    public class ProcessWatcherService : IDisposable
    {
        private System.Threading.Timer? _timer;
        private bool _isClaudeRunning = false;
        private DateTime _lastTriggerTime = DateTime.MinValue;
        private readonly int _checkIntervalMs;

        public event Action? OnClaudeLaunched;

        public ProcessWatcherService(int checkIntervalSeconds = 2)
        {
            _checkIntervalMs = Math.Max(1, checkIntervalSeconds) * 1000;
        }

        public void Start()
        {
            _timer = new System.Threading.Timer(CheckProcesses, null, 1000, _checkIntervalMs);
        }

        private void CheckProcesses(object? state)
        {
            try
            {
                var processes = Process.GetProcessesByName("Claude");
                bool isRunning = processes.Length > 0;

                // State transition: Not Running -> Running
                if (isRunning && !_isClaudeRunning)
                {
                    // Debounce 10 seconds to prevent child process spam
                    if ((DateTime.Now - _lastTriggerTime).TotalSeconds > 10)
                    {
                        _lastTriggerTime = DateTime.Now;
                        OnClaudeLaunched?.Invoke();
                    }
                    _isClaudeRunning = true;
                }
                else if (!isRunning)
                {
                    _isClaudeRunning = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessWatcher] Error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
