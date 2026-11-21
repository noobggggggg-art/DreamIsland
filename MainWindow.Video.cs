using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DreamIsland
{
    /// <summary>
    /// Режим видео для острова «Эмоции»: цвет по картинке, вспышки на смену сцены / стробоскоп,
    /// колонки в чёрных полосах по бокам. Экран анализируется только пока идёт видео.
    /// </summary>
    public partial class MainWindow
    {
        private readonly ScreenWatcher _scr = new ScreenWatcher();
        private SpeakerWindow _spkL, _spkR;
        private bool _scrEnabled = true, _spkEnabled = true;

        private Color _picColor = Colors.White;
        private bool _havePic;
        private Color _lastGlowCol = Colors.Transparent;
        private int _lastScrFlash;
        private int _missL, _missR;

        // колонка показывается, если полоса не уже этого (в независимых от dpi единицах)
        private const double MinBarDip = 56;
        private const int MissLimit = 14;   // ~0,5 с без полосы — колонка уходит

        private void InitVideo()
        {
            _scrEnabled = !AppSettings.GetBool("noscreen");
            _spkEnabled = !AppSettings.GetBool("nospk");
            SetMenu("screen", _scrEnabled);
            SetMenu("spk", _spkEnabled);
        }

        private string _curApp = "";

        private static string ShortApp(string app)
        {
            if (string.IsNullOrEmpty(app)) return "";
            int bang = app.LastIndexOf('!');
            string a = bang >= 0 ? app.Substring(bang + 1) : app;
            a = a.Replace("\\", "/");
            int slash = a.LastIndexOf('/');
            if (slash >= 0) a = a.Substring(slash + 1);
            if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a.Substring(0, a.Length - 4);
            return a;
        }

        /// <summary>Пункт меню «Этот плеер показывает видео»: для плееров, которые сами не сообщают, что идёт видео.</summary>
        private void UpdateVideoAppMenu()
        {
            var mi = FindMenu("vidapp");
            if (mi == null) return;

            bool has = !string.IsNullOrEmpty(_curApp);
            mi.IsEnabled = has;
            mi.IsChecked = has && MediaMonitor.IsUserVideoApp(_curApp);
            mi.Header = has ? "Плеер «" + ShortApp(_curApp) + "» показывает видео" : "Этот плеер показывает видео";
        }

        private void VideoApp_Click(object sender, RoutedEventArgs e)
        {
            var mi = (MenuItem)sender;
            if (string.IsNullOrEmpty(_curApp)) { mi.IsChecked = false; return; }

            MediaMonitor.SetUserVideoApp(_curApp, mi.IsChecked);
            _isVideo = mi.IsChecked;   // действует сразу, без смены трека
            Apply();
            UpdateLiveEq();
        }

        private bool VideoNeedsScreen { get { return _scrEnabled || _spkEnabled; } }

        /// <summary>Запускается вместе с островом «Эмоции».</summary>
        private void StartVideo()
        {
            if (VideoNeedsScreen && !_scr.Running) _scr.Start();
            _havePic = false;
            _lastScrFlash = 0;
            _lastGlowCol = Colors.Transparent;
            _missL = _missR = 0;
        }

        private void StopVideo()
        {
            _scr.Stop();
            _havePic = false;
            RetireSpeakers();
        }

        private void RetireSpeakers()
        {
            if (_spkL != null) _spkL.Retire();
            if (_spkR != null) _spkR.Retire();
        }

        private void DisposeVideo()
        {
            _scr.Dispose();
            try { if (_spkL != null) _spkL.Close(); } catch { }
            try { if (_spkR != null) _spkR.Close(); } catch { }
        }

        private void Video_Toggle(string tag, bool on)
        {
            if (tag == "screen")
            {
                _scrEnabled = on;
                AppSettings.SetBool("noscreen", !on);
            }
            else
            {
                _spkEnabled = on;
                AppSettings.SetBool("nospk", !on);
                if (!on) RetireSpeakers();
            }

            if (_emoRunning)
            {
                if (VideoNeedsScreen && !_scr.Running) _scr.Start();
                else if (!VideoNeedsScreen && _scr.Running) { _scr.Stop(); _havePic = false; }
            }
        }

        // ---------- вызывается из EmoTick ----------

        /// <summary>Подмешивает цвет картинки. Если картинки нет (чёрный кадр, защищённое видео) — цвет остаётся по звуку.</summary>
        private Color ApplyPictureColor(Color audioColor)
        {
            if (!_scrEnabled) return audioColor;

            var snap = _scr.Latest;
            if (snap == null || !snap.Valid) return audioColor;

            // яркий цвет, видный на чёрном; серая картинка — светло-серый
            double h, s, v;
            ToHsv(snap.Color, out h, out s, out v);
            Color target = s < 0.12
                ? Color.FromRgb(0xE5, 0xE5, 0xEA)
                : FromHsv(h, Math.Min(1.0, Math.Max(s, 0.6)), Math.Min(1.0, Math.Max(v, 0.88)));

            if (!_havePic) { _picColor = target; _havePic = true; }
            else _picColor = Lerp(_picColor, target, 0.12);

            return _picColor;
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t),
                                 (byte)(a.G + (b.G - a.G) * t),
                                 (byte)(a.B + (b.B - a.B) * t));
        }

        private static int ColorDist(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        }

        /// <summary>Была ли новая вспышка / смена сцены в видео с прошлого раза.</summary>
        private bool TakeScreenFlash()
        {
            if (!_scrEnabled) return false;
            var snap = _scr.Latest;
            if (snap == null) return false;

            bool flash = snap.FlashCount != _lastScrFlash;
            _lastScrFlash = snap.FlashCount;
            return flash;
        }

        /// <summary>Двигает колонки: ставит в чёрные полосы, красит и качает под музыку.</summary>
        private void UpdateSpeakers(Color color, float kickLevel, float high, bool beat, float flash)
        {
            if (!_spkEnabled) return;

            var snap = _scr.Latest;
            if (snap == null || snap.ScreenW <= 0) return;

            var src = PresentationSource.FromVisual(this);
            double k = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.M11 : 1.0;

            double screenW = snap.ScreenW * k, screenH = snap.ScreenH * k;
            double leftBar = snap.LeftBarPx * k, rightBar = snap.RightBarPx * k;

            if (_spkL == null) _spkL = new SpeakerWindow { Owner = this };
            if (_spkR == null) _spkR = new SpeakerWindow { Owner = this };

            Place(_spkL, leftBar, true, screenW, screenH, color, kickLevel, high, beat, flash, ref _missL);
            Place(_spkR, rightBar, false, screenW, screenH, color, kickLevel, high, beat, flash, ref _missR);
        }

        private void Place(SpeakerWindow w, double bar, bool left, double screenW, double screenH,
                           Color color, float kickLevel, float high, bool beat, float flash, ref int miss)
        {
            if (bar >= MinBarDip)
            {
                miss = 0;

                double width = Math.Min(110, bar * 0.55);
                w.SetSize(width);

                double centerX = left ? bar / 2 : screenW - bar / 2;
                double x = centerX - w.Width / 2;
                double y = screenH / 2 - w.Height / 2;

                w.PlaceAt(x, y, left);
                w.Pulse(color, kickLevel, high, beat, flash);
            }
            else if (++miss > MissLimit)
            {
                miss = MissLimit;
                w.Retire();
            }
        }
    }
}
