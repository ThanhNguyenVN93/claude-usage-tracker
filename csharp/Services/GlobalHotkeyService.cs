using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClaudeUsageTracker.Services
{
    public sealed class GlobalHotkeyService : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int  WM_HOTKEY = 0x0312;
        private const int  HotkeyId  = 0x9001;

        private HwndSource?  _source;
        private readonly Action _onTriggered;
        private uint _modifiers;
        private uint _vk;

        public GlobalHotkeyService(Action onTriggered)
        {
            _onTriggered = onTriggered;
        }

        public void Register(AppConfig config) => Register(config.HotkeyModifiers, config.HotkeyVirtualKey);

        public void Register(uint modifiers, uint vk)
        {
            _modifiers = modifiers;
            _vk        = vk;

            if (_source == null)
            {
                var parms = new HwndSourceParameters("ClaudeUsageTracker_HotkeyHost")
                {
                    Width = 0, Height = 0,
                    WindowStyle = 0x800000,       // WS_POPUP
                    ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                };
                _source = new HwndSource(parms);
                _source.AddHook(WndProc);
            }
            else
            {
                UnregisterHotKey(_source.Handle, HotkeyId);
            }

            RegisterHotKey(_source.Handle, HotkeyId, modifiers, vk);
        }

        // Đổi hotkey không cần tạo lại cửa sổ.
        public void Reregister(uint modifiers, uint vk)
        {
            if (_source == null) return;
            UnregisterHotKey(_source.Handle, HotkeyId);
            _modifiers = modifiers;
            _vk        = vk;
            RegisterHotKey(_source.Handle, HotkeyId, modifiers, vk);
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
