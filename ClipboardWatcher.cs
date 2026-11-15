using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DreamIsland
{
    /// <summary>
    /// Сообщает об изменении буфера обмена (без опроса — через системное уведомление)
    /// и умеет нажать Ctrl+V в активном окне.
    /// </summary>
    public sealed class ClipboardWatcher : IDisposable
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        private HwndSource _src;
        private IntPtr _hwnd;
        private bool _on;

        /// <summary>Вызывается в UI-потоке.</summary>
        public event Action Changed;

        public void Start(Window window)
        {
            if (_on) return;

            _hwnd = new WindowInteropHelper(window).Handle;
            if (_hwnd == IntPtr.Zero) return;

            _src = HwndSource.FromHwnd(_hwnd);
            if (_src == null) return;

            _src.AddHook(Hook);
            _on = AddClipboardFormatListener(_hwnd);
        }

        public void Stop()
        {
            if (!_on) return;
            _on = false;

            try { RemoveClipboardFormatListener(_hwnd); } catch { }
            try { if (_src != null) _src.RemoveHook(Hook); } catch { }
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                var h = Changed;
                if (h != null) h();
            }
            return IntPtr.Zero;
        }

        /// <summary>Нажимает Ctrl+V в том окне, которое сейчас активно.</summary>
        public static void PasteCtrlV()
        {
            const byte VK_CONTROL = 0x11;
            const byte VK_V = 0x56;
            const uint KEYUP = 0x0002;

            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_V, 0, 0, UIntPtr.Zero);
            keybd_event(VK_V, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
        }

        public void Dispose()
        {
            Stop();
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    }
}
