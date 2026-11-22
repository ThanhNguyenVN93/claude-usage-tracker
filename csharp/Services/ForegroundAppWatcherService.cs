using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClaudeUsageTracker.Services
{
    // Theo dõi cửa sổ đang ở foreground; báo true khi đó là Claude Desktop,
    // Claude Code CLI (terminal), VS Code. Bỏ qua taskbar và chính ClaudeUsageTracker
    // để không làm mất menu chuột phải ở khay hệ thống (tray) hoặc làm bật dậy HUD ngoài ý muốn.
    public class ForegroundAppWatcherService : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        private static readonly string[] TargetProcessNames =
        {
            "Claude",               // Claude Desktop
            "Code", "Code - Insiders", // VS Code
            "WindowsTerminal", "cmd", "powershell", "pwsh",
            "ConEmu64", "ConEmu", "mintty", "Terminal", // Các loại terminal chạy CLI
        };

        private const string SelfProcessName = "ClaudeUsageTracker";

        private System.Threading.Timer? _timer;
        private bool? _lastAllowed;
        private int _polling;

        /// <summary>Cửa sổ Claude/Terminal/IDE gần nhất đang hoạt động.</summary>
        public IntPtr LastTargetHwnd { get; private set; }

        public event Action<bool>? ForegroundAllowedChanged;

        public void Start(int pollIntervalMs = 1000)
        {
            _timer = new System.Threading.Timer(Poll, null, 0, pollIntervalMs);
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(256);
            int len = GetClassName(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        private void Poll(object? state)
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                // Taskbar hoặc tray notification overflow flyout: người dùng đang tương tác với khay hệ thống,
                // KHÔNG coi là chuyển app (để không làm ẩn/hiện HUD hoặc đóng menu tray).
                string className = GetWindowClassName(hwnd);
                if (string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(className, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return;

                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                // Nếu là chính ứng dụng ClaudeUsageTracker (HUD, Settings, Stats, ContextMenu...):
                // KHÔNG thay đổi trạng thái _lastAllowed.
                // - Nếu HUD đang hiện: giữ nguyên hiện.
                // - Nếu HUD đang ẩn: giữ nguyên ẩn (click tray menu hay toast không làm bật dậy HUD).
                if (string.Equals(name, SelfProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bool allowed = false;
                foreach (var target in TargetProcessNames)
                {
                    if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                    {
                        allowed = true;
                        break;
                    }
                }

                if (allowed) LastTargetHwnd = hwnd;

                if (_lastAllowed != allowed)
                {
                    _lastAllowed = allowed;
                    ForegroundAllowedChanged?.Invoke(allowed);
                }
            }
            catch
            {
                // bỏ qua lỗi truy vấn tiến trình (ví dụ process vừa thoát)
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
