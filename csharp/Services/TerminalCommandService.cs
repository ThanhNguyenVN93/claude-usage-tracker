using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace ClaudeUsageTracker.Services
{
    /// <summary>
    /// Gửi lệnh nhanh (/model haiku, /compact...) vào cửa sổ Terminal hoặc VS Code đang chạy Claude Code CLI.
    /// Dùng Clipboard + Ctrl+V + Enter thay vì gõ từng phím để miễn nhiễm với bộ gõ tiếng Việt (Unikey/EVKey).
    /// </summary>
    public static class TerminalCommandService
    {
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int SW_RESTORE = 9;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12; // Alt

        private static readonly string[] TerminalProcesses =
        {
            "WindowsTerminal", "Code", "Code - Insiders",
            "cmd", "powershell", "pwsh", "ConEmu64", "ConEmu", "mintty", "Terminal"
        };

        public static async Task<(bool Ok, string Message)> SendCommandAsync(IntPtr preferredHwnd, string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return (false, "Lệnh rỗng");

            IntPtr target = preferredHwnd;
            if (target == IntPtr.Zero || !IsWindow(target))
            {
                target = FindFallbackTerminalWindow();
            }

            if (target == IntPtr.Zero || !IsWindow(target))
            {
                return (false, "Không tìm thấy cửa sổ Terminal / VS Code nào đang mở.");
            }

            try
            {
                if (IsIconic(target))
                    ShowWindow(target, SW_RESTORE);

                SetForegroundWindow(target);
                await Task.Delay(90);

                // Nếu người dùng vừa bấm Global Hotkey (Ctrl+Shift+H/C), chờ tối đa 400ms cho tới khi
                // thả phím Ctrl/Shift/Alt để tránh SendKeys gửi nhầm Ctrl+Shift+V hoặc Shift+Enter.
                for (int i = 0; i < 20; i++)
                {
                    bool modDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0
                                || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                                || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                    if (!modDown) break;
                    await Task.Delay(20);
                }

                // Lưu tạm nội dung clipboard cũ để khôi phục sau khi dán
                string? oldClip = null;
                bool hadText = false;
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        oldClip = System.Windows.Clipboard.GetText();
                        hadText = true;
                    }
                }
                catch { /* bỏ qua nếu clipboard đang bị khóa */ }

                try
                {
                    System.Windows.Clipboard.SetText(command);
                }
                catch
                {
                    await Task.Delay(35);
                    try { System.Windows.Clipboard.SetText(command); }
                    catch (Exception ex) { return (false, $"Không thể ghi Clipboard: {ex.Message}"); }
                }

                await Task.Delay(30);
                // Git Bash (mintty) dùng Shift+Insert để dán thay vì Ctrl+V
                string pasteKeys = IsMintty(target) ? "+{INSERT}{ENTER}" : "^v{ENTER}";
                System.Windows.Forms.SendKeys.SendWait(pasteKeys);
                await Task.Delay(200);

                if (hadText && oldClip != null)
                {
                    try { System.Windows.Clipboard.SetText(oldClip); } catch { }
                }

                return (true, $"Đã gửi '{command}' tới Terminal.");
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi gửi lệnh: {ex.Message}");
            }
        }

        private static bool IsMintty(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return false;
                using var proc = Process.GetProcessById((int)pid);
                return string.Equals(proc.ProcessName, "mintty", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr FindFallbackTerminalWindow()
        {
            try
            {
                foreach (var procName in TerminalProcesses)
                {
                    var procs = Process.GetProcessesByName(procName);
                    IntPtr found = IntPtr.Zero;
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (found == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle))
                                found = p.MainWindowHandle;
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                    if (found != IntPtr.Zero) return found;
                }
            }
            catch { }
            return IntPtr.Zero;
        }
    }
}
