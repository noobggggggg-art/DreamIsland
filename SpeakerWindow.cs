using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DreamIsland
{
    /// <summary>
    /// Одна «колонка» — маленькое окно в чёрной полосе сбоку от видео. Не ловит клики, не забирает фокус
    /// и не попадает в захват экрана (поэтому не мешает определять полосы).
    ///
    /// Анимация: свой цикл на каждом кадре отрисовки (60 к/с и больше), движение — на пружинах с настоящей физикой
    /// (удар даёт толчок, динамик пружинит и успокаивается), двигаются только RenderTransform и Opacity — без пересчёта
    /// раскладки. Окно при этом не двигается и не меняет размер.
    /// </summary>
    public sealed class SpeakerWindow : Window
    {
        // ---------- пружина: duration — период, ratio — затухание (1 = без отскока) ----------

        private sealed class Spring
        {
            public double X, V, Target;
            private readonly double _k, _c;

            public Spring(double duration, double ratio, double start)
            {
                double w = 2 * Math.PI / duration;
                _k = w * w;
                _c = 2 * ratio * w;
                X = Target = start;
            }

            public void Step(double dt)
            {
                V += (-_k * (X - Target) - _c * V) * dt;   // полунеявный Эйлер: устойчив при малых шагах
                X += V * dt;
            }
        }

        private readonly Grid _root = new Grid();
        private readonly Border _body;
        private readonly Border[] _rings = new Border[2];
        private readonly ScaleTransform[] _ringScale = { new ScaleTransform(1, 1), new ScaleTransform(1, 1) };
        private readonly float[] _ringPhase = new float[2];
        private int _ringIdx;

        private readonly Ellipse _tweeter, _woofer, _cap;
        private readonly ScaleTransform _wooferScale = new ScaleTransform(1, 1);
        private readonly ScaleTransform _tweeterScale = new ScaleTransform(1, 1);
        private readonly ScaleTransform _bodyScale = new ScaleTransform(1, 1);
        private readonly ScaleTransform _enterScale = new ScaleTransform(1, 1);
        private readonly TranslateTransform _enterTx = new TranslateTransform();

        private readonly SolidColorBrush _accent = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        private readonly SolidColorBrush _bg = new SolidColorBrush(Colors.Black);

        // пружины (периоды подобраны на глаз: динамик быстрее и «резиновее», корпус тяжелее)
        private Spring _wooferS = new Spring(0.30, 0.42, 1);
        private Spring _tweeterS = new Spring(0.16, 0.70, 1);
        private Spring _bodyS = new Spring(0.36, 0.50, 1);
        private Spring _enterS = new Spring(0.45, 0.80, 0);

        private double _tint, _tintTarget, _level;
        private Color _color = Color.FromRgb(0x30, 0xD1, 0x58);
        private Color _lastBg = Colors.Black;

        private bool _shown, _looping, _reduced;
        private double _bodyW = 64;
        private double _dir = 1;          // откуда выезжает: левая колонка смещена к центру экрана
        private double _targetLeft;
        private long _lastTicks;

        public SpeakerWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;

            // волны звука: кольца вокруг колонки, расходятся на каждый удар
            for (int i = 0; i < 2; i++)
            {
                _rings[i] = new Border
                {
                    BorderBrush = _accent,
                    BorderThickness = new Thickness(2.5),
                    Background = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = _ringScale[i],
                    Opacity = 0
                };
                _root.Children.Add(_rings[i]);
            }

            _tweeter = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                     Stroke = _accent, StrokeThickness = 2, Fill = Brushes.Black,
                                     RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _tweeterScale };
            _woofer = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                    Stroke = _accent, StrokeThickness = 3, Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)),
                                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _wooferScale };
            _cap = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                 Fill = _accent, Opacity = 0.85,
                                 RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _wooferScale };

            var rows = new Grid();
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.2, GridUnitType.Star) });
            Grid.SetRow(_tweeter, 0);
            var wooferGrid = new Grid();
            wooferGrid.Children.Add(_woofer);
            wooferGrid.Children.Add(_cap);
            Grid.SetRow(wooferGrid, 1);
            rows.Children.Add(_tweeter);
            rows.Children.Add(wooferGrid);

            _body = new Border
            {
                Background = _bg,
                BorderBrush = _accent,
                BorderThickness = new Thickness(2.5),
                Child = rows,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _bodyScale
            };
            _root.Children.Add(_body);

            // появление: масштаб 0.9→1 + сдвиг + прозрачность (не из нуля), всё через transform / opacity
            var group = new TransformGroup();
            group.Children.Add(_enterScale);
            group.Children.Add(_enterTx);
            _root.RenderTransformOrigin = new Point(0.5, 0.5);
            _root.RenderTransform = group;
            _root.Opacity = 0;

            Content = _root;
            SetSize(64);

            SourceInitialized += (s, e) => MakeOverlay();
            Closed += (s, e) => StopLoop();
        }

        // ---------- размер (меняется редко: только когда меняется полоса) ----------

        /// <summary>w — ширина самой колонки; окно больше неё, чтобы волны были видны вокруг.</summary>
        public void SetSize(double w)
        {
            if (w < 24) w = 24;
            if (Math.Abs(w - _bodyW) < 0.5 && _body.Width == Math.Round(_bodyW)) return;
            _bodyW = w;

            double bw = Math.Round(w), bh = Math.Round(w * 2.6);
            _body.Width = bw;
            _body.Height = bh;
            foreach (var r in _rings) { r.Width = bw; r.Height = bh; r.CornerRadius = new CornerRadius(w * 0.28); }

            Width = Math.Round(bw * 1.6);
            Height = Math.Round(bh * 1.35);

            _body.CornerRadius = new CornerRadius(w * 0.28);
            _body.Padding = new Thickness(w * 0.10);

            _tweeter.Width = _tweeter.Height = Math.Round(w * 0.30);
            _woofer.Width = _woofer.Height = Math.Round(w * 0.62);
            _cap.Width = _cap.Height = Math.Round(w * 0.20);
        }

        // ---------- вход от музыки (вызывается ~30 раз в секунду, рисует цикл кадров) ----------

        /// <summary>
        /// Цвет колонки и «эмоции»: level 0..1 — как сильно бьёт, high 0..1 — верхние частоты,
        /// beat — только что был удар (толчок пружинам + волна), flash 0..1 — вспышка фона.
        /// </summary>
        public void Pulse(Color color, float level, float high, bool beat, float flash)
        {
            level = Math.Max(0f, Math.Min(1f, level));
            high = Math.Max(0f, Math.Min(1f, high));
            flash = Math.Max(0f, Math.Min(1f, flash));

            _color = color;
            _level = level;
            if (_accent.Color != color) _accent.Color = color;

            // цели пружин: куда динамик, твитер и корпус «хотят» прийти
            _wooferS.Target = 0.80 + 0.28 * level;
            _tweeterS.Target = 0.80 + 0.55 * high;
            _bodyS.Target = 1 + 0.03 * level + 0.04 * flash;
            _tintTarget = Math.Min(0.75, 0.05 + 0.30 * level + 0.7 * flash);

            if (beat && !_reduced)
            {
                // удар — это толчок скорости, а не прыжок значения: динамик пружинит и успокаивается сам
                double p = 0.55 + 0.45 * level;
                _wooferS.V += 6.5 * p;
                _bodyS.V += 2.4 * p;
                _tweeterS.V += 3.5 * high;

                _ringPhase[_ringIdx] = 0.0001f;
                _ringIdx ^= 1;
            }

            StartLoop();
        }

        // ---------- появление и уход ----------

        /// <summary>Показывает колонку в нужном месте. leftSide — колонка в левой полосе (выезжает от центра экрана).</summary>
        public void PlaceAt(double left, double top, bool leftSide)
        {
            Top = top;
            _dir = leftSide ? 1 : -1;

            if (!IsVisible)
            {
                _reduced = !SystemParameters.ClientAreaAnimation;   // «Анимация в Windows» выключена — только плавное появление
                ApplyReduced();

                _enterS.X = 0;
                _enterS.V = 0;
                _targetLeft = left;
                Left = left;
                Show();
            }
            else if (Math.Abs(left - _targetLeft) > 4)
            {
                _targetLeft = left;
                Left = left;
            }

            _shown = true;
            _enterS.Target = 1;
            StartLoop();
        }

        public void Retire()
        {
            if (!_shown) return;
            _shown = false;
            _enterS.Target = 0;
            StartLoop();
        }

        private void ApplyReduced()
        {
            // уменьшенное движение: без отскока, без сдвига и волн, остаются прозрачность и цвет
            _enterS = new Spring(_reduced ? 0.25 : 0.45, _reduced ? 1.0 : 0.80, 0);
            if (_reduced)
            {
                _wooferS = new Spring(0.20, 1.0, _wooferS.X);
                _bodyS = new Spring(0.25, 1.0, _bodyS.X);
                _tweeterS = new Spring(0.15, 1.0, _tweeterS.X);
            }
            else
            {
                _wooferS = new Spring(0.30, 0.42, _wooferS.X);
                _bodyS = new Spring(0.36, 0.50, _bodyS.X);
                _tweeterS = new Spring(0.16, 0.70, _tweeterS.X);
            }
        }

        // ---------- цикл кадров ----------

        private void StartLoop()
        {
            if (_looping) return;
            _looping = true;
            _lastTicks = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnFrame;
        }

        private void StopLoop()
        {
            if (!_looping) return;
            _looping = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;
            if (dt <= 0) return;
            if (dt > 0.05) dt = 0.05;   // после паузы не «догоняем» скачком

            // мелкие шаги, чтобы пружины оставались устойчивыми при любой частоте кадров
            int steps = (int)Math.Ceiling(dt / 0.008);
            double h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                _wooferS.Step(h);
                _tweeterS.Step(h);
                _bodyS.Step(h);
                _enterS.Step(h);
            }

            // динамик, твитер, корпус
            double w = Clamp(_wooferS.X, 0.55, 1.30);
            _wooferScale.ScaleX = _wooferScale.ScaleY = w;
            double t = Clamp(_tweeterS.X, 0.55, 1.6);
            _tweeterScale.ScaleX = _tweeterScale.ScaleY = t;
            double b = Clamp(_bodyS.X, 0.9, 1.18);
            _bodyScale.ScaleX = _bodyScale.ScaleY = b;
            _cap.Opacity = 0.55 + 0.45 * Clamp(_level, 0, 1);

            // подсветка корпуса: мягкое приближение, без рывков
            _tint += (_tintTarget - _tint) * (1 - Math.Exp(-dt * 16));
            Color bg = Color.FromRgb((byte)(_color.R * _tint), (byte)(_color.G * _tint), (byte)(_color.B * _tint));
            if (bg != _lastBg) { _bg.Color = bg; _lastBg = bg; }

            // волны: по времени, а не по числу кадров; быстро разбегаются и плавно гаснут
            for (int i = 0; i < 2; i++)
            {
                float ph = _ringPhase[i];
                if (ph <= 0f) continue;

                ph += (float)(dt / 0.55);
                if (ph >= 1f)
                {
                    _ringPhase[i] = 0f;
                    _rings[i].Opacity = 0;
                    _ringScale[i].ScaleX = _ringScale[i].ScaleY = 1;
                    continue;
                }

                _ringPhase[i] = ph;
                double inv = 1 - ph;
                double sc = 1 + 0.6 * (1 - inv * inv * inv);   // ease-out
                _ringScale[i].ScaleX = sc;
                _ringScale[i].ScaleY = sc;
                _rings[i].Opacity = 0.85 * inv * Math.Sqrt(inv);
            }

            // появление / уход
            double en = Clamp(_enterS.X, 0, 1.05);
            double e1 = Math.Min(1, en);
            double amt = _reduced ? 0 : 1;
            _root.Opacity = e1;
            _enterScale.ScaleX = _enterScale.ScaleY = 1 - 0.10 * amt * (1 - e1);
            _enterTx.X = _dir * 20 * amt * (1 - e1);

            if (_enterS.Target == 0 && _enterS.X < 0.02 && Math.Abs(_enterS.V) < 0.2)
            {
                _root.Opacity = 0;
                StopLoop();
                try { Hide(); } catch { }
            }
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // ---------- «призрачное» окно ----------

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
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
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);   // не попадает в захват экрана (Windows 10 2004+)
            }
            catch { }
        }
    }
}
