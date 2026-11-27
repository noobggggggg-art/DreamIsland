using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DreamIsland
{
    /// <summary>
    /// Снимок экрана за окном, уменьшенный в 8 раз. Размытое «стекло» в WPF делается так:
    /// снимок растягивается на весь экран (уменьшение + растяжение уже размывают), поверх идёт тонировка.
    /// Снимок живёт только в памяти окна и нигде не сохраняется.
    /// </summary>
    internal static class Backdrop
    {
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h,
                                              IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

        private const int HALFTONE = 4;
        private const uint SRCCOPY = 0x00CC0020;
        private const int Shrink = 8;

        /// <returns>уменьшенный снимок основного экрана или null, если снять не вышло</returns>
        public static BitmapSource Capture()
        {
            return CaptureRect(0, 0, 0, 0, Shrink);
        }

        /// <summary>Часть экрана в пикселях (w, h не больше нуля — весь экран), уменьшенная в shrink раз.</summary>
        public static BitmapSource CaptureRect(int x, int y, int rw, int rh, int shrink)
        {
            IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero;
            try
            {
                int sx = x, sy = y;
                int sw = rw > 0 ? rw : GetSystemMetrics(0), sh = rh > 0 ? rh : GetSystemMetrics(1);
                if (rw <= 0 || rh <= 0) { sx = 0; sy = 0; }
                if (sw <= 0 || sh <= 0) return null;

                int w = Math.Max(1, sw / shrink), h = Math.Max(1, sh / shrink);

                screen = GetDC(IntPtr.Zero);
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, w, h);
                IntPtr old = SelectObject(mem, bmp);

                SetStretchBltMode(mem, HALFTONE);   // усреднение при уменьшении
                bool ok = StretchBlt(mem, 0, 0, w, h, screen, sx, sy, sw, sh, SRCCOPY);
                SelectObject(mem, old);
                if (!ok) return null;

                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                var fixedFormat = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
                fixedFormat.Freeze();
                return fixedFormat;
            }
            catch { return null; }
            finally
            {
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
