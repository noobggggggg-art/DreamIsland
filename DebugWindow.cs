using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DreamIsland
{
    /// <summary>
    /// Скрытая отладочная панель острова «Эмоции»: маленькое окно с числами в углу экрана.
    /// Клик по панели копирует числа (и историю за последнюю минуту) в буфер обмена.
    /// Фокус не забирает и не попадает в захват экрана (как и колонки).
    /// </summary>
    public sealed class DebugWindow : Window
    {
        private readonly TextBlock _text, _hint;
        private readonly DispatcherTimer _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        private const string HintIdle = "клик по панели — скопировать числа";

        /// <summary>Нажали на панель: нужно скопировать. Возвращает true, если копирование удалось.</summary>
        public Func<bool> CopyRequested;

        public DebugWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Left = 12;
            Top = 56;

            _text = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7B, 0xFF, 0x9A)),
                Margin = new Thickness(10, 8, 10, 2)
            };
            _hint = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90)),
                Text = HintIdle,
                Margin = new Thickness(10, 0, 10, 8)
            };

            var stack = new StackPanel();
            stack.Children.Add(_text);
            stack.Children.Add(_hint);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                Child = stack,
                Cursor = Cursors.Hand
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                bool ok = false;
                try { ok = CopyRequested != null && CopyRequested(); } catch { }
                _hint.Text = ok ? "\u2713 скопировано в буфер" : "не удалось скопировать";
                _hintTimer.Stop();
                _hintTimer.Start();
                e.Handled = true;
            };
            _hintTimer.Tick += (s, e) => { _hintTimer.Stop(); _hint.Text = HintIdle; };
            Content = border;

            SourceInitialized += (s, e) => MakeOverlay();
        }

        public void SetText(string text)
        {
            _text.Text = text;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        private void MakeOverlay()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);   // клики принимает, фокус не забирает
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            }
            catch { }
        }
    }
}
