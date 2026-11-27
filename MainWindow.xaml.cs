using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DreamIsland
{
    public partial class MainWindow : Window
    {
        private enum IslandState { Idle, IdleHover, Compact, Expanded, Toast, Clip, Ts, TsHover, Emo }

        // Размеры состояний: ширина, высота, скругление, размер обложки
        private static readonly (double W, double H, double R, double Art) IdleSize      = (244, 32, 16, 22);
        private static readonly (double W, double H, double R, double Art) IdleHoverSize = (334, 40, 20, 22);
        private static readonly (double W, double H, double R, double Art) CompactSize   = (344, 34, 17, 22);
        private static readonly (double W, double H, double R, double Art) ExpandedSize  = (420, 144, 32, 56);
        private static readonly (double W, double H, double R, double Art) ToastSize     = (320, 44, 22, 22);
        private static readonly (double W, double H, double R, double Art) TsSize        = (330, 34, 17, 22);
        private static readonly (double W, double H, double R, double Art) TsHoverSize   = (420, 120, 28, 22);
        private static readonly (double W, double H, double R, double Art) EmoSize       = (440, 56, 28, 22);

        private const double ExpandedTopRowHeight = 76;

        private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
        private static readonly Color Red   = Color.FromRgb(0xFF, 0x45, 0x3A);
        private static readonly Color Gray  = Color.FromRgb(0x8E, 0x8E, 0x93);

        private static readonly FontFamily IconFont   = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly FontFamily SymbolFont = new FontFamily("Segoe UI Symbol");

        // Анимируемое свойство для скругления углов
        private static readonly DependencyProperty CornerProp =
            DependencyProperty.Register("Corner", typeof(double), typeof(MainWindow),
                new PropertyMetadata(16.0, (d, e) =>
                {
                    var w = (MainWindow)d;
                    w.Island.CornerRadius = new CornerRadius((double)e.NewValue);
                }));

        private readonly MediaMonitor _media = new MediaMonitor();
        private readonly KeyWatcher _keys = new KeyWatcher();
        private readonly AudioWatcher _audio = new AudioWatcher();
        private readonly MicSilenceWatcher _mic = new MicSilenceWatcher();
        private readonly VpnWatcher _vpn = new VpnWatcher();
        private readonly DispatcherTimer _micRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _micDebug = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private bool _micWatchWanted;

        // пункты контекстного меню (x:Name внутри ContextMenu в WPF полей не создаёт)
        private System.Windows.Controls.MenuItem MicWatchItem
        {
            get { return (System.Windows.Controls.MenuItem)Island.ContextMenu.Items[0]; }
        }

        private System.Windows.Controls.MenuItem MicDebugItem
        {
            get { return (System.Windows.Controls.MenuItem)Island.ContextMenu.Items[1]; }
        }

        private readonly DispatcherTimer _alertTimer    = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private readonly DispatcherTimer _dateTimer     = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        private readonly DispatcherTimer _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _toastTimer    = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _layoutTimer   = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        // Цвет акцента (из обложки) и свечение
        private readonly SolidColorBrush _accent = new SolidColorBrush(Green);
        private readonly DropShadowEffect _glow = new DropShadowEffect
        {
            Color = Green, BlurRadius = 32, ShadowDepth = 0, Opacity = 0
        };

        private IslandState? _state;
        private bool _hasMedia, _playing, _hover, _alert, _toast, _barsRunning, _glowOn;
        private DateTime _seekHoldUntil = DateTime.MinValue;
        private int _lastLang;
        private string _layoutCode = "--";
        private bool _micMuted, _vpnOn;

        private readonly DeviceWatcher _devices = new DeviceWatcher();
        private bool _devicesEnabled = true;

        // таймер / секундомер / помодоро
        private enum TimerKind { None, Countdown, Stopwatch, Pomodoro }
        private TimerKind _tk = TimerKind.None;
        private bool _tRunning;
        private DateTime _tStart;          // момент последнего старта или продолжения
        private TimeSpan _tBefore;         // накоплено до паузы
        private TimeSpan _tTotal;          // длина фазы (таймер, помодоро)
        private bool _pomWork = true;
        private string _timerText = "";
        private string _tLabel = "";         // своя подпись таймера
        private const double TimerExtra = 56; // на сколько шире остров, пока идёт таймер и играет музыка
        private readonly DispatcherTimer _timerTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        public MainWindow()
        {
            InitializeComponent();

            // Luminous: тонкая кромка со светом сверху-слева, остров остаётся чёрным
            Island.BorderThickness = new Thickness(1);
            Island.BorderBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x5A, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0x10, 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(0x2A, 0xA7, 0x8B, 0xFA), 1)
                }, new Point(0, 0), new Point(1, 1));

            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 6;

            // эквалайзер, прогресс и свечение используют один цвет
            foreach (UIElement el in Bars.Children)
                if (el is Rectangle r) r.Fill = _accent;
            ProgressFill.Background = _accent;
            InitRing();
            GlowBack.Effect = _glow;

            RefreshDateText();
            _dateTimer.Tick += (s, e) => RefreshDateText();
            _dateTimer.Start();

            SourceInitialized += (s, e) => MakeToolWindow();

            _alertTimer.Tick += (s, e) => { _alertTimer.Stop(); _alert = false; Apply(); };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); _toast = false; Apply(); };
            _progressTimer.Tick += (s, e) => UpdateProgress();
            _layoutTimer.Tick += (s, e) => LayoutTick();

            _media.Changed += OnMediaChanged;
            _media.ArtChanged += SetArt;
            _keys.CapsLockChanged += on => Dispatcher.BeginInvoke(new Action(() => OnCaps(on)));
            _audio.VolumeChanged += OnVolume;
            _audio.MicMuteChanged += OnMic;
            _vpn.Changed += OnVpn;
            _devices.Changed += OnDevice;
            _timerTick.Tick += (s, e) => TimerTickUpdate();
            _mic.MutedChanged += muted => Dispatcher.BeginInvoke(new Action(() => OnMic(muted)));
            _micRetry.Tick += (s, e) => { if (_micWatchWanted && !_mic.Running) _mic.Start(); };
            _micDebug.Tick += (s, e) => MicDebugTick();

            Loaded += async (s, e) =>
            {
                Apply();
                try { _keys.Start(); } catch { }
                try { _audio.Start(); } catch { }
                _layoutTimer.Start();

                _micWatchWanted = AppSettings.GetBool("micwatch");
                MicWatchItem.IsChecked = _micWatchWanted;
                if (_micWatchWanted) _mic.Start();
                _micRetry.Start();
                _vpn.Start();

                // подключение устройств и автозапуск (состояние пунктов меню)
                _devicesEnabled = !AppSettings.GetBool("nodevices");
                var devItem = FindMenu("devices");
                if (devItem != null) devItem.IsChecked = _devicesEnabled;
                AutoStart.MigrateLegacy();
                var autoItem = FindMenu("autostart");
                if (autoItem != null) autoItem.IsChecked = AutoStart.IsEnabled();
                _devices.Start();
                InitFeatures();

                // начальные значения индикаторов
                _micMuted = _audio.MicMuted;
                _vpnOn = _vpn.IsConnected;
                UpdateStatus();
                try { await _media.StartAsync(); } catch { }
            };

            Closed += (s, e) =>
            {
                _keys.Dispose();
                _audio.Dispose();
                _mic.Dispose();
                _vpn.Dispose();
                _devices.Dispose();
                DisposeFeatures();
            };
        }

        // ---------- Дата ----------

        private static readonly CultureInfo Ru = new CultureInfo("ru-RU");

        private static string Cap(string t)
        {
            return t.Length == 0 ? t : char.ToUpper(t[0], Ru) + t.Substring(1);
        }

        private static string ShortDate() { return Cap(DateTime.Now.ToString("ddd, d MMM", Ru)); }
        private static string FullDate()  { return Cap(DateTime.Now.ToString("dddd, d MMMM", Ru)); }

        private void RefreshDateText()
        {
            // если идёт таймер, в «пустом» острове вместо даты показываем его
            if (_tk != TimerKind.None)
            {
                string label = _tk == TimerKind.Stopwatch ? "Секундомер"
                             : _tk == TimerKind.Pomodoro ? (_pomWork ? "Фокус" : "Перерыв")
                             : (string.IsNullOrEmpty(_tLabel) ? "Таймер" : _tLabel);
                if (!_tRunning) label += " (пауза)";

                DateText.Text = _state == IslandState.IdleHover ? label + " · " + _timerText : _timerText;
                DateText.Foreground = TimerBrush();
                return;
            }

            DateText.Foreground = Brushes.White;
            DateText.Text = _state == IslandState.IdleHover ? FullDate() : ShortDate();
        }

        // ---------- Данные о музыке ----------

        private void OnMediaChanged(MediaInfo info, bool trackChanged)
        {
            // у видео с телефона (IMG_1536.MOV) в метаданных нет названия — но оно идёт, значит остров должен реагировать
            _hasMedia = !string.IsNullOrWhiteSpace(info.Title) || (info.Playing && !string.IsNullOrEmpty(info.App));
            _playing = info.Playing;
            _isVideo = info.Video;
            _curApp = info.App ?? "";
            UpdateVideoAppMenu();
            PlayGlyph.Text = _playing ? "\uE769" : "\uE768";

            if (trackChanged)
            {
                TitleText.Text = !string.IsNullOrWhiteSpace(info.Title) ? info.Title : (info.Video ? "Видео" : ShortApp(info.App));
                ArtistText.Text = info.Artist;
                SetArt(info.Thumb);

                // при смене трека остров ненадолго раскрывается
                if (_hasMedia && _playing)
                {
                    _alert = true;
                    _alertTimer.Stop();
                    _alertTimer.Start();
                }
            }

            Apply();
            if (trackChanged)
            {
                RefreshMarquee();
                if (_state == IslandState.Expanded) UpdateProgress();
            }
        }

        private void SetArt(byte[] data)
        {
            if (data == null)
            {
                Art.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
                SetAccent(Green);
                return;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 112; // маленький размер = мало памяти
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                Art.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch
            {
                Art.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            }

            SetAccent(ComputeAccent(data));
        }

        // ---------- Цвет из обложки ----------

        private static Color ComputeAccent(byte[] data)
        {
            try
            {
                var small = new BitmapImage();
                small.BeginInit();
                small.CacheOption = BitmapCacheOption.OnLoad;
                small.DecodePixelWidth = 24;
                small.StreamSource = new MemoryStream(data);
                small.EndInit();
                small.Freeze();

                var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
                var px = new byte[h * stride];
                conv.CopyPixels(px, stride, 0);

                double sr = 0, sg = 0, sb = 0, sw = 0;   // взвешенная сумма (яркие насыщенные пиксели важнее)
                double ar = 0, ag = 0, ab = 0;           // обычное среднее
                int n = w * h;

                for (int i = 0; i < n; i++)
                {
                    double b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    double v = max / 255.0;
                    double s = max == 0 ? 0 : (max - min) / max;
                    double weight = s * v + 0.001;

                    sr += r * weight; sg += g * weight; sb += b * weight; sw += weight;
                    ar += r; ag += g; ab += b;
                }

                var c = Color.FromRgb((byte)(sr / sw), (byte)(sg / sw), (byte)(sb / sw));
                ToHsv(c, out double hue, out double sat, out double val);

                // совсем серая обложка — светло-серый акцент
                if (sat < 0.12)
                    return Color.FromRgb(0xE5, 0xE5, 0xEA);

                // делаем цвет ярким, чтобы он был виден на чёрном
                sat = Math.Max(sat, 0.55);
                val = Math.Max(val, 0.88);
                return FromHsv(hue, Math.Min(sat, 1.0), Math.Min(val, 1.0));
            }
            catch
            {
                return Green;
            }
        }

        private static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max == 0 ? 0 : d / max;

            if (d == 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
        }

        private static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;
            double r = 0, g = 0, b = 0;

            if (h < 60)       { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else              { r = c; b = x; }

            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        private void SetAccent(Color c)
        {
            _curAccent = c;
            var d = TimeSpan.FromMilliseconds(500);
            _accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, d));
            _glow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(c, d));
        }

        private void UpdateGlow()
        {
            bool on = _state == IslandState.Emo || (TsActive ? _tsTalking : (_hasMedia && _playing));
            if (on == _glowOn) return;
            _glowOn = on;
            _glow.BeginAnimation(DropShadowEffect.OpacityProperty,
                new DoubleAnimation(on ? 0.5 : 0, TimeSpan.FromMilliseconds(500)));
        }

        // ---------- Состояния и анимации ----------

        private void Apply()
        {
            IslandState want;
            if (_toast) want = IslandState.Toast;
            else if (WantClip()) want = IslandState.Clip;
            else if (TsActive) want = _hover ? IslandState.TsHover : IslandState.Ts;   // TeamSpeak 3 важнее музыки
            else if (!_hasMedia) want = IslandState.Idle;
            else if (_hover || _alert) want = IslandState.Expanded;
            else if (EmoActive) want = IslandState.Emo;                                 // видео — остров «Эмоции»
            else if (_playing) want = IslandState.Compact;
            else want = IslandState.Idle;

            // музыки нет — при наведении показываем полную дату
            if (want == IslandState.Idle && _hover) want = IslandState.IdleHover;

            bool showRoot = want == IslandState.Compact || want == IslandState.Expanded;
            UpdateBars(_playing && showRoot);
            UpdateGlow();
            UpdateLiveEq();

            if (want == _state) return;
            _state = want;

            var size = want == IslandState.Clip ? ClipSizeNow()
                     : want == IslandState.TsHover ? TsHoverSize
                     : want == IslandState.Ts ? TsSize
                     : want == IslandState.Emo ? EmoSize
                     : want == IslandState.Expanded ? ExpandedSize
                     : want == IslandState.Compact ? CompactSize
                     : want == IslandState.IdleHover ? IdleHoverSize
                     : want == IslandState.Toast ? ToastSize
                     : IdleSize;

            double extraW = ExtraFor(want);
            Island.BeginAnimation(WidthProperty, Spring(size.W + extraW));
            Island.BeginAnimation(HeightProperty, Spring(size.H));
            BeginAnimation(CornerProp, Spring(size.R));
            Art.BeginAnimation(WidthProperty, Spring(size.Art));
            Art.BeginAnimation(HeightProperty, Spring(size.Art));
            Root.BeginAnimation(HeightProperty,
                Spring(want == IslandState.Expanded ? ExpandedTopRowHeight : Math.Max(size.H, 24)));

            RefreshDateText();
            Fade(IdleRow, want == IslandState.Idle || want == IslandState.IdleHover);
            Fade(Root, showRoot);
            Fade(TextPanel, want == IslandState.Expanded, 140, 250);
            Fade(Controls, want == IslandState.Expanded, 200, 250);
            Fade(MarqueeHost, want == IslandState.Compact, 200, 220);
            Fade(Ring, want == IslandState.Compact, 200, 220);
            Fade(ToastPanel, want == IslandState.Toast, 120, 220);
            Fade(ClipPanel, want == IslandState.Clip, 140, 220);
            Fade(TsPanel, want == IslandState.Ts || want == IslandState.TsHover, 120, 220);
            Fade(TsMore, want == IslandState.TsHover, 120, 220);
            Fade(EmoPanel, want == IslandState.Emo, 120, 220);

            if (want == IslandState.Expanded || want == IslandState.Compact)
            {
                _progressTimer.Interval = TimeSpan.FromMilliseconds(want == IslandState.Expanded ? 300 : 500);
                _progressTimer.Start();
                UpdateProgress();
            }
            else
            {
                _progressTimer.Stop();
            }

            RefreshMarquee();
            UpdateLiveEq();
            UpdateTsTick();
        }

        private static DoubleAnimation Spring(double to)
        {
            return new DoubleAnimation(to, TimeSpan.FromMilliseconds(480))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
            };
        }

        /// <summary>Плавно показывает или скрывает элемент (скрытый элемент не ловит клики).</summary>
        private static void Fade(FrameworkElement el, bool show, int delayMs = 0, int ms = 200)
        {
            el.Tag = show;

            if (show)
            {
                el.Visibility = Visibility.Visible;
                el.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, TimeSpan.FromMilliseconds(ms))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(delayMs)
                    });
            }
            else
            {
                var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
                a.Completed += (s, e) =>
                {
                    if (el.Tag is bool b && !b) el.Visibility = Visibility.Collapsed;
                };
                el.BeginAnimation(OpacityProperty, a);
            }
        }

        // ---------- Эквалайзер ----------

        private void UpdateBars(bool run)
        {
            run = run && !_liveEqEnabled; // при «живом» эквалайзере полоски двигает звук, а не анимация
            if (run == _barsRunning) return;
            _barsRunning = run;

            AnimateBar(Bar1, 380, run);
            AnimateBar(Bar2, 520, run);
            AnimateBar(Bar3, 450, run);
        }

        private static void AnimateBar(ScaleTransform t, int ms, bool run)
        {
            if (run)
            {
                var anim = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(ms))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                Timeline.SetDesiredFrameRate(anim, 20); // эквалайзеру хватает 20 кадров/с — меньше нагрузка
                t.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
            else
            {
                t.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                t.ScaleY = 0.3;
            }
        }

        // ---------- Бегущая строка ----------

        private void RefreshMarquee()
        {
            // останавливаем прошлую анимацию (не крутим её, когда не видно — экономим процессор)
            MarqueeShift.BeginAnimation(TranslateTransform.XProperty, null);

            if (_state != IslandState.Compact) return;

            MarqueeText.Text = TitleText.Text;
            MarqueeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double tw = MarqueeText.DesiredSize.Width;
            double aw = MarqueeHost.ActualWidth > 40
                ? MarqueeHost.ActualWidth                                       // реальная ширина свободного места
                : CompactSize.W + ExtraFor(IslandState.Compact) - 189;           // запасной вариант, пока размеры не посчитаны
            const double pad = 8;

            if (tw <= aw - 2 * pad)
            {
                MarqueeShift.X = pad; // короткое название — слева, вплотную к обложке
                return;
            }

            // длинное название — плавно прокручиваем
            double shift = tw - aw + 2 * pad;
            double from = pad;
            double to = -(tw - aw + pad);
            var hold = TimeSpan.FromSeconds(1.5);
            var scroll = TimeSpan.FromSeconds(shift / 35.0); // 35 px/сек
            var endHold = TimeSpan.FromSeconds(1.2);
            var back = TimeSpan.FromSeconds(0.5);

            MarqueeShift.X = from;

            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(hold)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(hold + scroll)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(hold + scroll + endHold)));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromTimeSpan(hold + scroll + endHold + back),
                new CubicEase { EasingMode = EasingMode.EaseInOut }));

            Timeline.SetDesiredFrameRate(anim, 30);
            MarqueeShift.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        // ---------- Кольцо прогресса вокруг обложки ----------

        private const double RingThickness = 2;
        private const double RingBox = 26, RingRadius = 8;
        private DoubleCollection _ringDash;

        private static double RingPerimeter
        {
            get { return 4 * (RingBox - 2 * RingRadius) + 2 * Math.PI * RingRadius; }
        }

        private void InitRing()
        {
            // скруглённый квадрат вокруг обложки; начало — сверху по центру, идём по часовой
            double x0 = 1, y0 = 1, x1 = x0 + RingBox, y1 = y0 + RingBox, r = RingRadius;
            var sz = new Size(r, r);

            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x0 + RingBox / 2, y0), false, true);
                c.LineTo(new Point(x1 - r, y0), true, false);
                c.ArcTo(new Point(x1, y0 + r), sz, 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(x1, y1 - r), true, false);
                c.ArcTo(new Point(x1 - r, y1), sz, 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(x0 + r, y1), true, false);
                c.ArcTo(new Point(x0, y1 - r), sz, 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(x0, y0 + r), true, false);
                c.ArcTo(new Point(x0 + r, y0), sz, 0, false, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();

            RingTrack.Data = g;
            RingFill.Data = g;
            RingFill.Stroke = _accent; // тот же цвет, что у эквалайзера (из обложки)

            // штрих: [закрашенная часть, пропуск]; длины считаются в толщинах линии
            _ringDash = new DoubleCollection { 0, (RingPerimeter + 10) / RingThickness };
            RingFill.StrokeDashArray = _ringDash;
        }

        private void UpdateRing(double ratio)
        {
            ratio = Math.Max(0, Math.Min(1, ratio));

            if (ratio < 0.004)
            {
                RingFill.Visibility = Visibility.Collapsed;
                return;
            }

            _ringDash[0] = ratio * RingPerimeter / RingThickness;
            RingFill.Visibility = Visibility.Visible;
        }

        // ---------- Прогресс трека ----------

        private static string Fmt(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        private void UpdateProgress()
        {
            if (DateTime.UtcNow < _seekHoldUntil) return; // сразу после перемотки не перезаписываем

            if (_media.TryGetTimeline(out var pos, out var dur))
            {
                double r = Math.Max(0, Math.Min(1, pos.TotalSeconds / dur.TotalSeconds));
                ProgressScale.ScaleX = r;
                UpdateRing(r);
                TimeLeft.Text = Fmt(pos);
                TimeRight.Text = Fmt(dur);
            }
            else
            {
                ProgressScale.ScaleX = 0;
                UpdateRing(0);
                TimeLeft.Text = "0:00";
                TimeRight.Text = "--:--";
            }
        }

        private void Progress_Down(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (ProgressHost.ActualWidth <= 0) return;

            double ratio = e.GetPosition(ProgressHost).X / ProgressHost.ActualWidth;
            ratio = Math.Max(0, Math.Min(1, ratio));

            if (_media.TryGetTimeline(out _, out var dur))
            {
                var target = TimeSpan.FromSeconds(dur.TotalSeconds * ratio);
                _media.SeekTo(target);
                ProgressScale.ScaleX = ratio;
                UpdateRing(ratio);
                TimeLeft.Text = Fmt(target);
                _seekHoldUntil = DateTime.UtcNow.AddMilliseconds(1200);
            }
        }

        // ---------- Всплывающие события (toast) ----------

        private void ShowToast(string icon, bool symbolFont, Color iconColor,
                               string label, string value, Color valueColor,
                               double volume = -1, int ms = 2000, ImageSource thumb = null)
        {
            if (thumb != null)
            {
                ToastThumb.Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };
                ToastThumb.Visibility = Visibility.Visible;
                ToastIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                ToastThumb.Visibility = Visibility.Collapsed;
                ToastIcon.Visibility = Visibility.Visible;
            }

            ToastIcon.FontFamily = symbolFont ? SymbolFont : IconFont;
            ToastIcon.Text = icon;
            ToastIcon.Foreground = new SolidColorBrush(iconColor);
            ToastValue.Text = value;
            ToastValue.Foreground = new SolidColorBrush(valueColor);

            if (volume >= 0)
            {
                ToastText.Visibility = Visibility.Collapsed;
                VolHost.Visibility = Visibility.Visible;
                VolScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(Math.Max(0, Math.Min(1, volume)), TimeSpan.FromMilliseconds(140))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }
            else
            {
                VolHost.Visibility = Visibility.Collapsed;
                ToastText.Visibility = Visibility.Visible;
                ToastText.Text = label;
            }

            _toast = true;
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(ms);
            _toastTimer.Start();
            Apply();
        }

        private void OnVolume(int percent, bool muted)
        {
            _volSys = percent;
            _volSysMuted = muted;
            UpdateVolMini();

            string icon = muted || percent == 0 ? "\uE74F"
                        : percent < 34 ? "\uE993"
                        : percent < 67 ? "\uE994"
                        : "\uE995";

            ShowToast(icon, false, Color.FromRgb(255, 255, 255), "",
                      muted ? "Выкл" : percent + "%", Color.FromRgb(255, 255, 255),
                      muted ? 0 : percent / 100.0, 1600);
        }

        private void OnMic(bool muted)
        {
            _micMuted = muted;
            UpdateStatus();
            ShowToast("\uE720", false, muted ? Red : Green,
                      "Микрофон", muted ? "Выкл" : "Вкл", muted ? Red : Green);
        }

        // ---------- Индикаторы рядом с музыкой: раскладка, микрофон, VPN ----------

        private static SolidColorBrush Br(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static readonly SolidColorBrush GreenBrush = Br(Green);
        private static readonly SolidColorBrush RedBrush = Br(Red);
        private static readonly SolidColorBrush GrayBrush = Br(Gray);

        private void UpdateStatus()
        {
            LayoutText.Text = _layoutCode;

            MicIcon.Foreground = _micMuted ? RedBrush : GreenBrush;

            VpnIcon.Text = _vpnOn ? "\uE72E" : "\uE785";   // закрытый / открытый замок
            VpnIcon.Foreground = _vpnOn ? GreenBrush : GrayBrush;
            VpnIcon.Opacity = _vpnOn ? 1 : 0.6;

            // то же самое в «пустом» острове, рядом с датой
            IdleLayoutText.Text = _layoutCode;
            IdleMicIcon.Foreground = _micMuted ? RedBrush : GreenBrush;
            IdleVpnIcon.Text = VpnIcon.Text;
            IdleVpnIcon.Foreground = VpnIcon.Foreground;
            IdleVpnIcon.Opacity = VpnIcon.Opacity;

            UpdateTsUi();   // остров TeamSpeak показывает раскладку и микрофон Windows
        }

        private void OnVpn(bool on, string info)
        {
            _vpnOn = on;
            UpdateStatus();

            string label = string.IsNullOrWhiteSpace(info) ? "VPN" : "VPN · " + info;

            ShowToast(on ? "\uE72E" : "\uE785", false, on ? Green : Gray,
                      label, on ? "Вкл" : "Выкл", on ? Green : Gray, -1, 2500);
        }

        private void OnCaps(bool on)
        {
            ShowToast("\u21EA", true, on ? Color.FromRgb(255, 255, 255) : Gray,
                      "Caps Lock", on ? "Вкл" : "Выкл", on ? Green : Gray);
        }

        // ---------- Раскладка клавиатуры ----------

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint idThread);

        private void LayoutTick()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return;

                uint tid = GetWindowThreadProcessId(fg, out _);
                int lang = (int)(GetKeyboardLayout(tid).ToInt64() & 0xFFFF);
                if (lang == 0) return;

                bool first = _lastLang == 0;
                if (lang == _lastLang) return;
                _lastLang = lang;

                string code;
                try { code = new CultureInfo(lang).TwoLetterISOLanguageName.ToUpperInvariant(); }
                catch { code = "??"; }

                _layoutCode = code;
                UpdateStatus();
                if (first) return; // при первом определении плашку не показываем

                ShowToast("\uE765", false, Color.FromRgb(255, 255, 255),
                          "Раскладка", code, Color.FromRgb(255, 255, 255), -1, 1500);
            }
            catch { }
        }

        // ---------- Колёсико мыши: громкость ----------

        private void Island_Wheel(object sender, MouseWheelEventArgs e)
        {
            // Alt + колёсико — громкость TeamSpeak 3
            if (IsAltDown())
            {
                AdjustTs3(e.Delta / 120.0 * 0.02);
                e.Handled = true;
                return;
            }

            // Shift + колёсико — громкость SoundCloud, просто колёсико — громкость всей системы
            if (_scVolEnabled && IsShiftDown())
            {
                AdjustSoundCloud(e.Delta / 120.0 * 0.02);
                e.Handled = true;
                return;
            }

            _audio.AdjustVolume(e.Delta / 120.0 * 0.02); // 2% за один щелчок
            e.Handled = true;
        }

        // ---------- Таймер, секундомер, помодоро ----------

        private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
        private static readonly SolidColorBrush OrangeBrush = Br(Orange);

        private Brush TimerBrush()
        {
            if (!_tRunning) return GrayBrush;
            if (_tk == TimerKind.Stopwatch) return Brushes.White;
            if (_tk == TimerKind.Pomodoro && !_pomWork) return GreenBrush;
            return OrangeBrush;
        }

        private TimeSpan TimerElapsed()
        {
            return _tBefore + (_tRunning ? DateTime.UtcNow - _tStart : TimeSpan.Zero);
        }

        private void StartTimer(TimerKind kind, TimeSpan total, string label = null)
        {
            _tk = kind;
            _tLabel = label ?? "";
            _tTotal = total;
            _tBefore = TimeSpan.Zero;
            _tStart = DateTime.UtcNow;
            _tRunning = true;
            _pomWork = true;
            _timerText = "";

            _timerTick.Start();
            OnTimerStateChanged();
            TimerTickUpdate();
        }

        private void StopTimer()
        {
            _timerTick.Stop();
            _tLabel = "";
            _tk = TimerKind.None;
            _tRunning = false;
            _timerText = "";
            OnTimerStateChanged();
        }

        private void ToggleTimerPause()
        {
            if (_tk == TimerKind.None) return;

            if (_tRunning)
            {
                _tBefore += DateTime.UtcNow - _tStart;
                _tRunning = false;
            }
            else
            {
                _tStart = DateTime.UtcNow;
                _tRunning = true;
            }

            TimerText.Foreground = TimerBrush();
            RefreshDateText();
        }

        private void OnTimerStateChanged()
        {
            bool on = _tk != TimerKind.None;

            TimerText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            TimerText.Foreground = TimerBrush();
            if (!on) TimerText.Text = "";
            RefreshDateText();

            // остров становится шире, чтобы поместились все индикаторы
            RefreshWidth();
        }

        private void TimerTickUpdate()
        {
            if (_tk == TimerKind.None) return;

            var el = TimerElapsed();
            TimeSpan shown;

            if (_tk == TimerKind.Stopwatch)
            {
                shown = el;
            }
            else
            {
                var remaining = _tTotal - el;
                if (remaining <= TimeSpan.Zero)
                {
                    TimerPhaseFinished();
                    return;
                }
                shown = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
            }

            string text = Fmt(shown);
            if (text == _timerText) return;

            _timerText = text;
            TimerText.Text = text;
            RefreshDateText();
        }

        private void TimerPhaseFinished()
        {
            System.Media.SystemSounds.Asterisk.Play();

            if (_tk == TimerKind.Pomodoro)
            {
                // фокус 25 мин → перерыв 5 мин → снова фокус…
                _pomWork = !_pomWork;
                _tTotal = TimeSpan.FromMinutes(_pomWork ? 25 : 5);
                _tBefore = TimeSpan.Zero;
                _tStart = DateTime.UtcNow;
                _tRunning = true;
                _timerText = "";

                TimerText.Foreground = TimerBrush();
                ShowToast("\uE121", false, _pomWork ? Orange : Green, "Помодоро",
                          _pomWork ? "Фокус" : "Перерыв", _pomWork ? Orange : Green, -1, 3500);
                TimerTickUpdate();
                return;
            }

            string doneLabel = string.IsNullOrEmpty(_tLabel) ? "Таймер" : _tLabel;
            StopTimer();
            StartRing(doneLabel, "Готово", AppSettings.GetInt("timer_sound", 0));
        }

        private void Timer_Click(object sender, RoutedEventArgs e)
        {
            var mi = (System.Windows.Controls.MenuItem)sender;
            int minutes;
            if (int.TryParse(mi.Tag as string, out minutes))
                StartTimer(TimerKind.Countdown, TimeSpan.FromMinutes(minutes));
        }

        private void Stopwatch_Click(object sender, RoutedEventArgs e) { StartTimer(TimerKind.Stopwatch, TimeSpan.Zero); }
        private void Pomodoro_Click(object sender, RoutedEventArgs e) { StartTimer(TimerKind.Pomodoro, TimeSpan.FromMinutes(25)); }
        private void TimerPause_Click(object sender, RoutedEventArgs e) { ToggleTimerPause(); }
        private void TimerStop_Click(object sender, RoutedEventArgs e) { StopTimer(); }

        // ---------- Подключение устройств и автозапуск ----------

        private System.Windows.Controls.MenuItem FindMenu(string tag)
        {
            foreach (var item in Island.ContextMenu.Items)
            {
                var mi = item as System.Windows.Controls.MenuItem;
                if (mi != null && tag.Equals(mi.Tag as string)) return mi;
            }
            return null;
        }

        private void OnDevice(string kind, string name, bool connected)
        {
            if (!_devicesEnabled) return;

            string icon = kind == "drive" ? "\uE88E" : "\uE7F6"; // USB / наушники
            ShowToast(icon, false, connected ? Green : Gray, name,
                      connected ? "Подключено" : "Отключено", connected ? Green : Gray, -1, 2500);
        }

        private void Devices_Click(object sender, RoutedEventArgs e)
        {
            var mi = (System.Windows.Controls.MenuItem)sender;
            _devicesEnabled = mi.IsChecked;
            AppSettings.SetBool("nodevices", !_devicesEnabled);
        }

        private void AutoStart_Click(object sender, RoutedEventArgs e)
        {
            var mi = (System.Windows.Controls.MenuItem)sender;
            if (!AutoStart.Set(mi.IsChecked))
            {
                mi.IsChecked = AutoStart.IsEnabled();
                ShowToast("\uE7BA", false, Red, "Автозапуск", "Ошибка", Red, -1, 2500);
            }
        }

        // ---------- Слежение за микрофоном по звуку (для аппаратных mute) ----------

        private void MicWatch_Click(object sender, RoutedEventArgs e)
        {
            _micWatchWanted = MicWatchItem.IsChecked;
            AppSettings.SetBool("micwatch", _micWatchWanted);

            if (_micWatchWanted) _mic.Start();
            else if (!MicDebugItem.IsChecked) _mic.Stop();
        }

        private void MicDebug_Click(object sender, RoutedEventArgs e)
        {
            if (MicDebugItem.IsChecked)
            {
                if (!_mic.Running) _mic.Start();
                _micDebug.Start();
            }
            else
            {
                _micDebug.Stop();
                _toastTimer.Stop();
                _toast = false;
                Apply();
                if (!_micWatchWanted) _mic.Stop();
            }
        }

        private void MicDebugTick()
        {
            if (!_mic.Running)
            {
                ShowToast("\uE720", false, Red, "Нет доступа к микрофону", "", Gray, -1, 600);
                return;
            }

            double p = _mic.Peak;
            double db = p > 1e-7 ? 20 * Math.Log10(p) : -120;
            string text = db <= -119 ? "тишина" : string.Format("{0:0} dB", db);

            ShowToast("\uE720", false, Color.FromRgb(255, 255, 255), "", text,
                      Color.FromRgb(255, 255, 255), Math.Max(0, Math.Min(1, (db + 90) / 90)), 600);
        }

        // ---------- События мыши ----------

        private void Island_MouseEnter(object sender, MouseEventArgs e)
        {
            _hover = true;
            _clipForceExit = false;
            Apply();
        }

        private void Island_MouseLeave(object sender, MouseEventArgs e)
        {
            _hover = false;
            Apply();
        }

        private void Island_Click(object sender, MouseButtonEventArgs e)
        {
            // клик по кнопкам и полосе прогресса обрабатывается ими самими,
            // клик по любому другому месту острова = пауза / плей
            if (_ringing) { OnRingClick(); return; }   // звонит будильник или таймер: клик отложит или выключит
            if (_state == IslandState.Clip) return; // в списке буфера клики обрабатывают строки
            if (IsInside(e.OriginalSource as DependencyObject, Controls)) return;
            if (_hasMedia) _media.TogglePlayPause();
            else if (_tk != TimerKind.None) ToggleTimerPause(); // музыки нет — клик ставит таймер на паузу
        }

        private static bool IsInside(DependencyObject node, DependencyObject container)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, container)) return true;

                node = (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        private void Art_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_hasMedia) _media.Next(); // клик по обложке = следующий трек
        }

        // кнопка тускнеет сразу при нажатии — реакция мгновенная, не ждём ответа плеера
        private void Btn_Down(object sender, MouseButtonEventArgs e) { ((UIElement)sender).Opacity = 0.4; }
        private void Btn_Reset(object sender, MouseEventArgs e) { ((UIElement)sender).Opacity = 1; }

        private void Prev_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ((UIElement)sender).Opacity = 1;
            _media.Previous();
        }

        private void Next_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ((UIElement)sender).Opacity = 1;
            _media.Next();
        }

        private void PlayPause_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ((UIElement)sender).Opacity = 1;

            // значок меняется сразу; настоящее состояние придёт от плеера и при необходимости поправит
            PlayGlyph.Text = PlayGlyph.Text == "\uE769" ? "\uE768" : "\uE769";
            _media.TogglePlayPause();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        // ---------- Окно не появляется в Alt+Tab и не забирает фокус ----------

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        private void MakeToolWindow()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }
    }
}
