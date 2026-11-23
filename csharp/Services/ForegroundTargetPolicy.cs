using System;

namespace ClaudeUsageTracker.Services
{
    // Hàm thuần quyết định HUD có được hiện cho cửa sổ foreground hay không (dễ test, không P/Invoke).
    public static class ForegroundTargetPolicy
    {
        public static readonly string[] DesktopProcessNames = { "Claude" };

        public static readonly string[] TerminalOrIdeProcessNames =
        {
            "Code", "Code - Insiders", // VS Code
            "WindowsTerminal", "cmd", "powershell", "pwsh",
            "ConEmu64", "ConEmu", "mintty", "Terminal",
        };

        public static bool IsDesktop(string processName) => Matches(DesktopProcessNames, processName);

        public static bool IsTerminalOrIde(string processName) => Matches(TerminalOrIdeProcessNames, processName);

        public static bool IsHudAllowed(string processName, string? windowTitle, bool hasClaudeChild, bool onlyWithClaude)
        {
            if (IsDesktop(processName)) return true;
            if (!IsTerminalOrIde(processName)) return false;
            if (!onlyWithClaude) return true;

            string title = windowTitle ?? string.Empty;
            if (Contains(title, "agy") || Contains(title, "antigravity")) return false;
            return Contains(title, "claude") || hasClaudeChild;
        }

        private static bool Matches(string[] list, string name)
        {
            foreach (var n in list)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool Contains(string text, string part) =>
            text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
