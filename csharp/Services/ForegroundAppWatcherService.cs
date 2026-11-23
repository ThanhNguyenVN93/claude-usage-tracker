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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const string SelfProcessName = "ClaudeUsageTracker";

        private readonly Func<bool>? _onlyWithClaude;
        private readonly System.Collections.Generic.Dictionary<int, (DateTime At, bool Value)> _childCache = new();
        private System.Threading.Timer? _timer;
        private bool? _lastAllowed;
        private int _polling;

        public ForegroundAppWatcherService(Func<bool>? onlyWithClaude = null)
        {
            _onlyWithClaude = onlyWithClaude;
        }

        /// <summary>Đặt lại trạng thái để lần poll kế tiếp phát lại sự kiện (dùng sau khi đổi cấu hình).</summary>
        public void Reevaluate()
        {
            lock (_childCache) _childCache.Clear();
            _lastAllowed = null;
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(512);
            int len = GetWindowText(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        // Duyệt cây tiến trình con của pid tìm claude/claude.exe. Cache 2 giây theo PID.
        private bool HasClaudeChild(int rootPid)
        {
            var now = DateTime.UtcNow;
            lock (_childCache)
            {
                if (_childCache.TryGetValue(rootPid, out var c) && (now - c.At).TotalSeconds < 2) return c.Value;
            }

            bool found = false;
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap != IntPtr.Zero && snap != new IntPtr(-1))
            {
                try
                {
                    var children = new System.Collections.Generic.Dictionary<uint, System.Collections.Generic.List<(uint Pid, string Exe)>>();
                    var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                    for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    {
                        if (!children.TryGetValue(e.th32ParentProcessID, out var list))
                            children[e.th32ParentProcessID] = list = new();
                        list.Add((e.th32ProcessID, e.szExeFile));
                    }

                    var stack = new System.Collections.Generic.Stack<uint>();
                    var seen = new System.Collections.Generic.HashSet<uint> { (uint)rootPid };
                    stack.Push((uint)rootPid);
                    while (stack.Count > 0 && !found)
                    {
                        if (!children.TryGetValue(stack.Pop(), out var kids)) continue;
                        foreach (var (pid, exe) in kids)
                        {
                            if (!seen.Add(pid)) continue;
                            string n = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
                            if (string.Equals(n, "claude", StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                            stack.Push(pid);
                        }
                    }
                }
                finally { CloseHandle(snap); }
            }

            lock (_childCache) _childCache[rootPid] = (now, found);
            return found;
        }

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

                bool onlyWithClaude = _onlyWithClaude?.Invoke() ?? false;
                string title = string.Empty;
                bool hasClaudeChild = false;
                if (onlyWithClaude && ForegroundTargetPolicy.IsTerminalOrIde(name))
                {
                    title = GetWindowTitle(hwnd);
                    hasClaudeChild = HasClaudeChild((int)pid);
                }
                bool allowed = ForegroundTargetPolicy.IsHudAllowed(name, title, hasClaudeChild, onlyWithClaude);

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
