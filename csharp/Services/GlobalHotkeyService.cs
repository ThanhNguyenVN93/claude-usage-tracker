using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClaudeUsageTracker.Services
{
    // Đăng ký một global hotkey và gọi callback khi người dùng bấm.
    // Dùng message-only window (HwndSource) — không cần cửa sổ hiện.
    public sealed class GlobalHotkeyService : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT   = 0x0004;
        private const uint VK_U        = 0x55;
        private const int  WM_HOTKEY   = 0x0312;
        private const int  HotkeyId    = 0x9001;

        private HwndSource? _source;
        private readonly Action _onTriggered;

        public GlobalHotkeyService(Action onTriggered)
        {
            _onTriggered = onTriggered;
        }

        public void Register()
        {
            var parms = new HwndSourceParameters("ClaudeUsageTracker_HotkeyHost")
            {
                Width = 0, Height = 0,
                WindowStyle = 0x800000,  // WS_POPUP (gọn nhất)
                ParentWindow = new IntPtr(-3),  // HWND_MESSAGE
            };
            _source = new HwndSource(parms);
            _source.AddHook(WndProc);
            RegisterHotKey(_source.Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT, VK_U);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                _onTriggered();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_source != null)
            {
                try { UnregisterHotKey(_source.Handle, HotkeyId); } catch { }
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
    }
}
