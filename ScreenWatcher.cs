using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;

namespace DreamIsland
{
    /// <summary>Что видно на экране в данный момент (результат одного анализа кадра).</summary>
    public sealed class ScreenSnapshot
    {
        public bool Valid;                 // кадр не чёрный и картинку удалось разобрать
        public Color Color;                // главный цвет картинки (без чёрных полос)
        public double Brightness;          // средняя яркость картинки, 0..255
        public int LeftBarPx, RightBarPx;  // ширина чёрных полос слева и справа, в пикселях экрана
        public int ScreenW, ScreenH;       // размер основного экрана в пикселях
        public int FlashCount;             // растёт на каждой вспышке или смене сцены
    }

    /// <summary>
    /// Смотрит на экран маленькими кадрами (96×54, около 8 раз в секунду) и только пока включён режим видео.
    /// Ничего не сохраняет и никуда не отправляет — кадр живёт доли секунды в памяти.
    /// </summary>
    public sealed class ScreenWatcher : IDisposable
    {
        public const int W = 96, H = 54;
        private const int SkipTopRows = 5;   // верхние строки пропускаем: там сам остров
        private const int SkipBottomRows = 6; // нижние — панель плеера, панель задач, трей
        // полосы по бокам ищем только в средней части экрана (20–80 % высоты): ни панель задач, ни заголовок окна,
        // ни кнопки плеера, ни часы в трее не мешают
        private const int BandA = H * 20 / 100, BandB = H * 80 / 100;

        private Thread _thread;
        private volatile bool _stop;
        private volatile ScreenSnapshot _snap;

        public bool Running { get { return _thread != null; } }
        public ScreenSnapshot Latest { get { return _snap; } }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _snap = null;
            _thread = new Thread(Run) { IsBackground = true, Name = "DreamIsland.Screen", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            var t = _thread;
            _thread = null;
            if (t != null) t.Join(1500);
            _snap = null;
        }

        public void Dispose()
        {
            Stop();
        }

        // ---------- WinAPI (GDI) ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                              IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

        private const int HALFTONE = 4;
        private const uint SRCCOPY = 0x00CC0020;

        // ---------- рабочий поток ----------

        private void Run()
        {
            IntPtr mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero, bits = IntPtr.Zero;

            try
            {
                IntPtr screen0 = GetDC(IntPtr.Zero);
                mem = CreateCompatibleDC(screen0);
                ReleaseDC(IntPtr.Zero, screen0);

                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = W;
                bmi.bmiHeader.biHeight = -H;          // сверху вниз
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;      // BI_RGB

                bmp = CreateDIBSection(mem, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return;
                old = SelectObject(mem, bmp);

                SetStretchBltMode(mem, HALFTONE);
                SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);

                var buf = new byte[W * H * 4];
                var lum = new byte[W * H];
                var prev = new byte[W * H];
                bool havePrev = false;

                double slowBright = -1;
                int flashes = 0;
                long lastFlashTick = 0;

                var colSum = new int[W];
                var colBright = new int[W];

                while (!_stop)
                {
                    long t0 = Environment.TickCount64;

                    int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);   // SM_CXSCREEN / SM_CYSCREEN
                    if (sw <= 0 || sh <= 0) { Thread.Sleep(250); continue; }

                    IntPtr screen = GetDC(IntPtr.Zero);
                    bool ok = false;
                    try { ok = StretchBlt(mem, 0, 0, W, H, screen, 0, 0, sw, sh, SRCCOPY); }
                    finally { ReleaseDC(IntPtr.Zero, screen); }

                    if (ok)
                    {
                        Marshal.Copy(bits, buf, 0, buf.Length);

                        // яркость каждой точки и по колонкам
                        Array.Clear(colSum, 0, W);
                        Array.Clear(colBright, 0, W);
                        for (int y = 0; y < H; y++)
                        {
                            int row = y * W;
                            for (int x = 0; x < W; x++)
                            {
                                int p = (row + x) * 4;
                                int b = buf[p], g = buf[p + 1], r = buf[p + 2];
                                int l = (r * 54 + g * 183 + b * 19) >> 8;
                                lum[row + x] = (byte)l;
                                if (y >= BandA && y < BandB)
                                {
                                    colSum[x] += l;
                                    if (l >= 40) colBright[x]++;
                                }
                            }
                        }

                        int rows = BandB - BandA;

                        // чёрные полосы по бокам: колонка почти чёрная сверху донизу
                        int left = 0;
                        while (left < W && IsBlackColumn(colSum[left], colBright[left], rows)) left++;
                        int right = 0;
                        while (right < W - left && IsBlackColumn(colSum[W - 1 - right], colBright[W - 1 - right], rows)) right++;

                        int x0 = left, x1 = W - right;   // [x0, x1) — сама картинка
                        var snap = new ScreenSnapshot
                        {
                            ScreenW = sw,
                            ScreenH = sh,
                            LeftBarPx = left * sw / W,
                            RightBarPx = right * sw / W
                        };

                        if (x1 - x0 >= 8)
                        {
                            double sr = 0, sg = 0, sb = 0, sweight = 0, bright = 0;
                            int cnt = 0;
                            long diff = 0;

                            for (int y = SkipTopRows; y < H - SkipBottomRows; y++)
                            {
                                int row = y * W;
                                for (int x = x0; x < x1; x++)
                                {
                                    int p = (row + x) * 4;
                                    int b = buf[p], g = buf[p + 1], r = buf[p + 2];
                                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                                    double s = max == 0 ? 0 : (double)(max - min) / max;
                                    double weight = s * max / 255.0 + 0.002;   // яркие и насыщенные точки важнее

                                    sr += r * weight; sg += g * weight; sb += b * weight; sweight += weight;
                                    bright += lum[row + x];
                                    cnt++;

                                    if (havePrev) diff += Math.Abs(lum[row + x] - prev[row + x]);
                                }
                            }

                            bright /= Math.Max(1, cnt);
                            double meanDiff = havePrev ? (double)diff / Math.Max(1, cnt) : 0;

                            if (bright > 4 && sweight > 0)
                            {
                                snap.Valid = true;
                                snap.Brightness = bright;
                                snap.Color = Color.FromRgb((byte)(sr / sweight), (byte)(sg / sweight), (byte)(sb / sweight));
                            }

                            // вспышка: резкая смена сцены или скачок яркости (стробоскоп, свет на сцене)
                            if (slowBright < 0) slowBright = bright;
                            bool cut = havePrev && meanDiff > 22;
                            bool strobe = havePrev && bright - slowBright > 32;
                            slowBright += (bright - slowBright) * 0.15;

                            if ((cut || strobe) && t0 - lastFlashTick > 180)
                            {
                                flashes++;
                                lastFlashTick = t0;
                            }
                        }

                        snap.FlashCount = flashes;
                        Buffer.BlockCopy(lum, 0, prev, 0, lum.Length);
                        havePrev = true;
                        _snap = snap;
                    }

                    // около 8 кадров в секунду
                    long spent = Environment.TickCount64 - t0;
                    int wait = (int)Math.Max(20, 120 - spent);
                    for (int t = 0; t < wait && !_stop; t += 20) Thread.Sleep(20);
                }
            }
            catch
            {
                // нет доступа к экрану — остров просто остаётся на цветах по звуку
                _snap = null;
            }
            finally
            {
                if (mem != IntPtr.Zero && old != IntPtr.Zero) SelectObject(mem, old);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
            }
        }

        private static bool IsBlackColumn(int sum, int brightPixels, int rows)
        {
            // почти чёрная колонка: не больше одной яркой точки и тёмное среднее
            return brightPixels <= 1 && sum / Math.Max(1, rows) < 14;
        }
    }
}
