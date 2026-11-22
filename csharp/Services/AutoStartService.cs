using System;
using Microsoft.Win32;

namespace ClaudeUsageTracker.Services
{
    // Bật/tắt khởi động cùng Windows bằng registry Run key (chỉ áp dụng cho user hiện tại,
    // không cần quyền admin).
    public static class AutoStartService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ClaudeUsageTracker";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        }

        // Cài bản mới vào thư mục khác làm Run key trỏ vào exe không còn tồn tại.
        // Chỉ sửa đường dẫn khi mục đã có sẵn — người dùng tắt autostart thì phải giữ nguyên là tắt.
        public static void SyncPathIfEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(ValueName) is not string current) return;

                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;

                string expected = $"\"{exePath}\"";
                if (current != expected) key.SetValue(ValueName, expected);
            }
            catch { /* registry bị chặn: bỏ qua, không làm sập tool */ }
        }

        public static void SetEnabled(bool enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                string exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(ValueName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
