using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DreamIsland
{
    /// <summary>
    /// Следит только за клавишей Caps Lock (глобальный хук клавиатуры).
    /// Остальные клавиши игнорируются и нигде не сохраняются.
    ///
    /// Хук работает в СВОЁМ отдельном потоке: если основной поток острова занят
    /// (анимации, игра грузит процессор), нажатия клавиш в системе не задерживаются.
    /// </summary>
    public sealed class KeyWatcher : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint WM_QUIT = 0x0012;
        private const int VK_CAPITAL = 0x14;
        private const int VK_DELETE = 0x2E;

        /// <summary>Когда (TickCount64) последний раз нажимали Delete — нужно, чтобы отличить удаление от перемещения.</summary>
        public static long LastDeleteKeyTick;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        private readonly HookProc _proc;   // держим ссылку, чтобы сборщик мусора не удалил
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private Thread _thread;
        private uint _threadId;
        private IntPtr _hook = IntPtr.Zero;
        private bool _capsDown;
        private bool _caps;

        /// <summary>true = Caps Lock включился. Вызывается НЕ в UI-потоке, обработчик должен быть быстрым.</summary>
        public event Action<bool> CapsLockChanged;

        public KeyWatcher()
        {
            _proc = Callback;
        }

        public void Start()
        {
            _caps = (GetKeyState(VK_CAPITAL) & 1) != 0;

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "DreamIsland.KeyHook",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
            _ready.Wait(1000);
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            _ready.Set();

            // минимальный цикл сообщений: хук вызывается из него
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vk = Marshal.ReadInt32(lParam);
                if (vk == VK_DELETE)
                {
                    int dm = (int)wParam;
                    if (dm == WM_KEYDOWN || dm == WM_SYSKEYDOWN)
                        Interlocked.Exchange(ref LastDeleteKeyTick, Environment.TickCount64);
                }

                if (vk == VK_CAPITAL)
                {
                    int msg = (int)wParam;
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        if (!_capsDown)
                        {
                            _capsDown = true;
                            _caps = !_caps;
                            CapsLockChanged?.Invoke(_caps);
                        }
                    }
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        _capsDown = false;
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _threadId = 0;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
    }
}
