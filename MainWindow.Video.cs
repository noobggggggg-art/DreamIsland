using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

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
        private bool _spkFloat;   // нет чёрных полос — колонки висят по краям экрана поверх картинки в уменьшенном виде

        private Color _picColor = Colors.White;
        private bool _havePic;
        private Color _lastGlowCol = Colors.Transparent;
        private int _lastScrFlash;
        private int _missL, _missR;
        private double _picW;

        // цвет каждой колонки по своему краю картинки
        private Color _picColL = Colors.White, _picColR = Colors.White;
        private double _picWL, _picWR;
        private bool _havePicL, _havePicR;

        // стиль колонок: 0 — неон, 1 — ретро, 2 — минимализм
        private int _spkStyle;

        // видео на паузе: колонки остаются, тускнеют и «дышат»
        private bool _pzRunning;
        private readonly DispatcherTimer _pzTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

        // колонка показывается, если полоса не уже этого (в независимых от dpi единицах)
        private const double MinBarDip = 56;
        private const int MissLimit = 14;   // ~0,5 с без полосы — колонка уходит

        private void InitVideo()
        {
            _scrEnabled = !AppSettings.GetBool("noscreen");
            _spkEnabled = !AppSettings.GetBool("nospk");
            SetMenu("screen", _scrEnabled);
            SetMenu("spk", _spkEnabled);

            SetSpkStyle(AppSettings.GetInt("spk_style", 0), false);
            InitTune();
            _pzTick.Tick += (s, e) => PausedTick();
        }

        // ---------- стиль колонок ----------

        private void SetSpkStyle(int style, bool save)
        {
            if (style < 0 || style > 2) style = 0;
            _spkStyle = style;

            for (int i = 0; i < 3; i++)
            {
                var mi = FindMenuDeep("spkstyle" + i);
                if (mi != null) mi.IsChecked = i == style;
            }

            if (save) AppSettings.SetString("spk_style", style.ToString());
        }

        private void SpkStyle_Click(object sender, RoutedEventArgs e)
        {
            var mi = (MenuItem)sender;
            string tag = mi.Tag as string;
            int style;
            if (tag != null && tag.Length > 8 && int.TryParse(tag.Substring(8), out style))
                SetSpkStyle(style, true);
        }

        // ---------- видео на паузе ----------

        private bool PausedSpeakersWanted()
        {
            return _emoEnabled && _spkEnabled && _isVideo && _hasMedia && !_playing;
        }

        private void StartPausedSpeakers()
        {
            if (!_scr.Running) _scr.Start();
            _pzTick.Start();
        }

        private void StopPausedSpeakers()
        {
            _pzTick.Stop();
            if (!_emoRunning) StopVideo();   // если сейчас идёт остров «Эмоции», экран и колонки нужны ему
        }

        private void PausedTick()
        {
            if (!_spkEnabled) return;

            Color audio = FromHsv(_hue, 0.9, 1.0);
            Color color = ApplyPictureColor(audio);
            UpdateSpeakers(color, audio, new SpkFeed(), new SpkFeed(), 0f, 0f, false, true);
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
            _havePicL = _havePicR = false;
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
            _pzTick.Stop();
            _scr.Dispose();
            try { if (_spkL != null) _spkL.Close(); } catch { }
            try { if (_spkR != null) _spkR.Close(); } catch { }
            DisposeDebug();
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

            UpdateLiveEq();   // колонки на паузе включаются и выключаются вместе с этим пунктом

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

            // на концертах картинка в дыму почти бесцветная: чем она бледнее, тем меньше влияет, и цвет остаётся «по музыке»
            double want = Math.Max(0, Math.Min(1, (s - 0.15) / 0.35)) * 0.8;
            Color target = FromHsv(h, Math.Min(1.0, Math.Max(s, 0.6)), Math.Min(1.0, Math.Max(v, 0.88)));

            if (!_havePic) { _picColor = target; _picW = want; _havePic = true; }
            else { _picColor = Lerp(_picColor, target, 0.12); _picW += (want - _picW) * 0.1; }

            return _picW < 0.02 ? audioColor : Lerp(audioColor, _picColor, _picW);
        }

        /// <summary>Цвет одной колонки: яркий цвет своего края картинки; бледный (концерт в дыму) почти не влияет.</summary>
        private Color SideColor(Color audio, Color fallback, Color snapColor, bool valid,
                                ref Color pic, ref double pw, ref bool have)
        {
            if (!_scrEnabled || !valid) return fallback;

            double h, s, v;
            ToHsv(snapColor, out h, out s, out v);

            double want = Math.Max(0, Math.Min(1, (s - 0.15) / 0.35)) * 0.8;
            Color target = FromHsv(h, Math.Min(1.0, Math.Max(s, 0.6)), Math.Min(1.0, Math.Max(v, 0.88)));

            if (!have) { pic = target; pw = want; have = true; }
            else { pic = Lerp(pic, target, 0.12); pw += (want - pw) * 0.1; }

            return pw < 0.02 ? audio : Lerp(audio, pic, pw);
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

        /// <summary>
        /// Двигает колонки: ставит в чёрные полосы, красит и качает под музыку.
        /// color — общий цвет (звук + картинка), audioColor — только по звуку: из него и краёв картинки считается цвет каждой колонки.
        /// rest — видео на паузе: колонки только дышат.
        /// </summary>
        private void UpdateSpeakers(Color color, Color audioColor, SpkFeed l, SpkFeed r, float flash, float build, bool drop, bool rest = false,
                                    float lull = 0f, float dropBoost = 0f)
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

            // левая колонка берёт цвет левой части картинки, правая — правой (если картинка бледная — остаётся цвет по звуку)
            Color cL = SideColor(audioColor, color, snap.ColorL, snap.ValidL, ref _picColL, ref _picWL, ref _havePicL);
            Color cR = SideColor(audioColor, color, snap.ColorR, snap.ValidR, ref _picColR, ref _picWR, ref _havePicR);

            Place(_spkL, leftBar, true, screenW, screenH, cL, l, flash, build, drop, rest, lull, dropBoost, ref _missL);
            Place(_spkR, rightBar, false, screenW, screenH, cR, r, flash, build, drop, rest, lull, dropBoost, ref _missR);
        }

        private void Feed(SpeakerWindow w, Color color, SpkFeed f, float flash, float build, bool drop, bool rest, float lull, float dropBoost)
        {
            if (rest) w.Rest(color);
            else w.Pulse(color, f.Level, f.High, f.Beat, flash, f.Inten, f.Power, build, drop, lull, dropBoost, f.Snare, f.Bands);
        }

        private void Place(SpeakerWindow w, double bar, bool left, double screenW, double screenH,
                           Color color, SpkFeed f, float flash, float build, bool drop, bool rest, float lull, float dropBoost, ref int miss)
        {
            w.StyleId = _spkStyle;
            w.Lite = _lowGpu;
            w.SparkMinInten = _tune.SparkInten;
            w.SparkMinLevel = _tune.SparkLevel;
            w.SparkGap = _tune.SparkGap;

            if (bar >= MinBarDip)
            {
                miss = 0;
                w.Floating = false;

                double width = Math.Min(120, bar * 0.5);
                w.SetSize(width);

                double centerX = left ? bar / 2 : screenW - bar / 2;
                double x = centerX - w.Width / 2;
                double y = screenH / 2 - w.Height / 2;

                w.PlaceAt(x, y, left);
                Feed(w, color, f, flash, build, drop, rest, lull, dropBoost);
            }
            else if (++miss > MissLimit)
            {
                miss = MissLimit;

                if (_spkFloat)
                {
                    // полос нет (видео на весь экран): уменьшенная колонка висит у края экрана поверх картинки
                    w.Floating = true;

                    double width = Math.Max(34, Math.Min(64, screenH * 0.045));
                    w.SetSize(width);

                    double margin = Math.Max(16, screenW * 0.012);
                    double centerX = left ? margin + width / 2 : screenW - margin - width / 2;
                    double x = centerX - w.Width / 2;
                    double y = screenH / 2 - w.Height / 2;

                    w.PlaceAt(x, y, left);
                    Feed(w, color, f, flash, build, drop, rest, lull, dropBoost);
                }
                else
                {
                    w.Retire();
                }
            }
        }
    }
}
