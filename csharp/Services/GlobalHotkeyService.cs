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

        private const int WM_HOTKEY = 0x0312;

        private readonly int    _id;
        private readonly string _windowName;
        private HwndSource?  _source;
        private readonly Action _onTriggered;
        private uint _modifiers;
        private uint _vk;

        public GlobalHotkeyService(Action onTriggered, int hotkeyId = 0x9001)
        {
            _onTriggered = onTriggered;
            _id          = hotkeyId;
            _windowName  = $"ClaudeUsageTracker_HotkeyHost_{hotkeyId:X4}";
        }

        /// <returns>false nếu Windows từ chối (tổ hợp đã bị app khác/instance khác chiếm).</returns>
        public bool Register(AppConfig config) => Register(config.HotkeyModifiers, config.HotkeyVirtualKey);

        public bool Register(uint modifiers, uint vk)
        {
            _modifiers = modifiers;
            _vk        = vk;

            if (_source == null)
            {
                var parms = new HwndSourceParameters(_windowName)
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
                UnregisterHotKey(_source.Handle, _id);
            }

            if (modifiers == 0 || vk == 0) return false;
            return RegisterHotKey(_source.Handle, _id, modifiers, vk);
        }

        // Nhả tổ hợp phím hiện giữ mà không huỷ cửa sổ ẩn — dùng khi cần nhường chỗ tạm thời
        // cho một GlobalHotkeyService khác đăng ký cùng tổ hợp (Windows chỉ cho MỘT nơi giữ mỗi tổ hợp).
        public void Unregister()
        {
            if (_source != null)
            {
                try { UnregisterHotKey(_source.Handle, _id); } catch { }
            }
        }

        // Đổi hotkey không cần tạo lại cửa sổ. Nếu chưa từng Register (vd. lần đầu ném lỗi) thì tạo mới.
        public bool Reregister(uint modifiers, uint vk)
        {
            if (_source == null) return Register(modifiers, vk);
            UnregisterHotKey(_source.Handle, _id);
            _modifiers = modifiers;
            _vk        = vk;
            if (modifiers == 0 || vk == 0) return false;
            return RegisterHotKey(_source.Handle, _id, modifiers, vk);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
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
                try { UnregisterHotKey(_source.Handle, _id); } catch { }
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
    }
}
