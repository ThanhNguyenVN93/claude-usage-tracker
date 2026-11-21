using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClaudeUsageTracker.Services
{
    // Theo dõi cửa sổ đang ở foreground; báo true khi đó là Claude Desktop,
    // Claude Code CLI (terminal), VS Code, hoặc chính HUD/Cài đặt của tool này.
    public class ForegroundAppWatcherService : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private static readonly string[] AllowedProcessNames =
        {
            "Claude",               // Claude Desktop
            "Code", "Code - Insiders", // VS Code
            "WindowsTerminal", "cmd", "powershell", "pwsh",
            "ConEmu64", "ConEmu", "mintty", "Terminal", // Các loại terminal chạy CLI
            "ClaudeUsageTracker"    // Chính HUD/Cài đặt của tool này
        };

        private System.Threading.Timer? _timer;
        private bool? _lastAllowed;

        public event Action<bool>? ForegroundAllowedChanged;

        public void Start(int pollIntervalMs = 1000)
        {
            _timer = new System.Threading.Timer(Poll, null, 0, pollIntervalMs);
        }

        private void Poll(object? state)
        {
            try
            {
                bool allowed = IsAllowedForeground();
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
        }

        private bool IsAllowedForeground()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;

            foreach (var allowed in AllowedProcessNames)
            {
                if (string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
