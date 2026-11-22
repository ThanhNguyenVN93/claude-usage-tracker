using System.Diagnostics;

namespace ClaudeUsageTracker.Services
{
    public static class ProcessUtil
    {
        // Process[] từ GetProcessesByName giữ handle OS; không Dispose là rò rỉ handle
        // (ProcessWatcher gọi mỗi 2 giây).
        public static bool IsClaudeRunning()
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName("Claude");
                return procs.Length > 0;
            }
            catch { return false; }
            finally
            {
                if (procs != null)
                    foreach (var p in procs) p.Dispose();
            }
        }
    }
}
