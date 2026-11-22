using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ClaudeUsageTracker.Services
{
    // Lắng nghe thay đổi của các file usage (Claude Desktop + Claude Code CLI statusline)
    // để cập nhật ngay khi có dữ liệu mới, không cần người dùng tự bấm refresh.
    public class UsageFileWatcherService : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly System.Threading.Timer _debounceTimer;
        private readonly object _lock = new();
        private bool _pending;

        public event Action? OnUsageFileChanged;

        public UsageFileWatcherService()
        {
            _debounceTimer = new System.Threading.Timer(FlushPending, null, Timeout.Infinite, Timeout.Infinite);

            AddWatcher(ClaudeUsageService.UsageHistoryPath);
            AddWatcher(ClaudeUsageService.CliUsagePath);
        }

        private void AddWatcher(string path)
        {
            string dir = Path.GetDirectoryName(path)!;
            string file = Path.GetFileName(path);

            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch { return; }
            }

            // FileName bắt buộc: cả ApiSync lẫn statusline.js đều ghi qua file tạm rồi đổi tên
            // (để không ai đọc phải nội dung cụt). Thao tác đó sinh sự kiện Renamed, mà Renamed
            // chỉ nổ khi có NotifyFilters.FileName — thiếu nó là watcher im hoàn toàn,
            // tool chỉ còn cập nhật theo nhịp 30 giây.
            var watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            watcher.Changed += (s, e) => ScheduleNotify();
            watcher.Created += (s, e) => ScheduleNotify();
            watcher.Renamed += (s, e) => ScheduleNotify();
            _watchers.Add(watcher);
        }

        public void Start()
        {
            foreach (var w in _watchers) w.EnableRaisingEvents = true;
        }

        private volatile bool _disposed;

        private void ScheduleNotify()
        {
            if (_disposed) return;
            lock (_lock)
            {
                _pending = true;
            }
            // Debounce 1.5s: nguồn ghi có thể ghi file nhiều lần liên tiếp.
            try { _debounceTimer.Change(1500, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private void FlushPending(object? state)
        {
            lock (_lock)
            {
                if (!_pending) return;
                _pending = false;
            }
            OnUsageFileChanged?.Invoke();
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var w in _watchers) w.Dispose();
            _debounceTimer.Dispose();
        }
    }
}
