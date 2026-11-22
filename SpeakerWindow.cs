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
        private const int Rings = 4;
        private readonly Border[] _rings = new Border[Rings];
        private readonly ScaleTransform[] _ringScale = new ScaleTransform[Rings];
        private readonly float[] _ringPhase = new float[Rings];
        private readonly float[] _ringGain = new float[Rings];
        private int _ringIdx;

        // «свечение» за колонкой и дрожь при сильных ударах
        private readonly Border _bloom;
        private readonly ScaleTransform _bloomScale = new ScaleTransform(1, 1);
        private readonly TranslateTransform _shakeTx = new TranslateTransform();
        private Spring _shakeX = new Spring(0.14, 0.30, 0);
        private Spring _shakeY = new Spring(0.14, 0.30, 0);
        private readonly Random _rnd = new Random();
        private double _inten;

        // нарастание перед «дропом»: сжимающиеся кольца, накопление свечения; и сама вспышка на «дропе»
        private const int Charges = 2;
        private readonly Border[] _charge = new Border[Charges];
        private readonly ScaleTransform[] _chargeScale = new ScaleTransform[Charges];
        private readonly double[] _chargePhase = { 0.0, 0.5 };
        private double _buildT, _buildS, _dropEnv;

        // стили: 0 — неон, 1 — ретро, 2 — минимализм
        private int _style = -1;
        private bool _lite;
        private readonly SolidColorBrush _frame = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));      // рамка корпуса, обводка динамика
        private readonly SolidColorBrush _wooferFill = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12));
        private static readonly Color Cream = Color.FromRgb(0xE6, 0xD6, 0xAE);
        private static readonly Color Amber = Color.FromRgb(0xF2, 0xA9, 0x3B);
        private Color _baseColor = Colors.Black, _lastFrame = Colors.Transparent, _lastHalo = Colors.Transparent;
        private double _haloK = 1, _bloomK = 1, _tintK = 1, _sparkOn = 1;

        // свет на чёрной полосе вокруг колонки: два полупрозрачных градиентных слоя (без размытия — видеокарту не нагружает)
        private readonly Ellipse _haloA, _haloB;
        private readonly GradientStop[] _haloStops = new GradientStop[6];

        // детали корпуса: кольцо подвеса, блик на диффузоре, блик корпуса, порт фазоинвертора
        private readonly Ellipse _surround, _gloss;
        private readonly Border _cabGloss, _port;

        // искры на сильных ударах
        private const int SparkN = 12;
        private readonly Ellipse[] _spark = new Ellipse[SparkN];
        private readonly TranslateTransform[] _sparkTx = new TranslateTransform[SparkN];
        private readonly ScaleTransform[] _sparkSc = new ScaleTransform[SparkN];
        private readonly double[] _spX = new double[SparkN], _spY = new double[SparkN];
        private readonly double[] _spVx = new double[SparkN], _spVy = new double[SparkN];
        private readonly double[] _spLife = new double[SparkN], _spDur = new double[SparkN];
        private readonly SolidColorBrush _sparkBrush = new SolidColorBrush(Colors.White);
        private long _lastSpark;

        // пороги искр (меняются пресетом чувствительности в меню)
        public double SparkMinInten = 0.55, SparkMinLevel = 0.65, SparkGap = 0.32;

        // колонка висит поверх картинки (нет чёрных полос): всё мягче и прозрачнее
        public bool Floating;

        // затишье перед дропом (0..1) и сила дропа (1 = обычный, до 1,8 после затишья)
        private double _lullT, _lullS, _dropGain = 1, _high;
        private double _time;

        // множители новых эффектов по стилям
        private double _ledK = 1, _rayK = 1, _kymK = 1, _shockK = 1, _runK = 1, _flyK = 1, _glitchK = 1;

        // 1. LED-шкалы по бокам корпуса (16 полос спектра своего канала)
        private readonly StackPanel[] _ledPanel = new StackPanel[2];
        private readonly TranslateTransform[] _ledTx = new TranslateTransform[2];
        private readonly Rectangle[][] _led = new Rectangle[2][];
        private readonly double[] _ledV = new double[AudioLevelMonitor.Bands];
        private float[] _bands;

        // 2. лучи прожекторов
        private const int RayN = 5;
        private readonly Canvas _rayLayer = new Canvas { IsHitTestVisible = false };
        private readonly Polygon[] _ray = new Polygon[RayN];
        private readonly RotateTransform[] _rayRot = new RotateTransform[RayN];
        private readonly GradientStop[] _rayStops = new GradientStop[3];
        private Color _lastRay = Colors.Transparent;

        // 3. кимати: рябь на диффузоре
        private const int Kyms = 3;
        private readonly Ellipse[] _kym = new Ellipse[Kyms];
        private readonly ScaleTransform[] _kymSc = new ScaleTransform[Kyms];
        private readonly double[] _kymPh = new double[Kyms];
        private int _kymIdx;
        private double _kymTimer;

        // 4. ударная волна дропа: два кольца с цветовым разносом и яркая вспышка
        private readonly Ellipse[] _shock = new Ellipse[2];
        private readonly ScaleTransform[] _shockSc = new ScaleTransform[2];
        private readonly Ellipse _flare;
        private readonly ScaleTransform _flareSc = new ScaleTransform(1, 1);
        private double _shockPh;   // 0 — волны нет

        // 5. бегущий огонь по рамке и обводка на дропе
        private readonly Border _outline;
        private readonly Ellipse[] _run = new Ellipse[3];
        private readonly TranslateTransform[] _runTx = new TranslateTransform[3];
        private readonly ScaleTransform[] _runSc = new ScaleTransform[3];
        private readonly SolidColorBrush _runBrush = new SolidColorBrush(Colors.White);
        private Color _lastRun = Colors.Transparent;
        private double _runU;

        // 6. светлячки: на нарастании и в затишье слетаются к колонке, на дропе разлетаются салютом
        private const int FlyN = 24;
        private readonly Ellipse[] _fly = new Ellipse[FlyN];
        private readonly TranslateTransform[] _flyTx = new TranslateTransform[FlyN];
        private readonly ScaleTransform[] _flySc = new ScaleTransform[FlyN];
        private readonly double[] _fX = new double[FlyN], _fY = new double[FlyN];
        private readonly double[] _fVx = new double[FlyN], _fVy = new double[FlyN];
        private readonly double[] _fLife = new double[FlyN], _fDur = new double[FlyN];
        private readonly bool[] _fIn = new bool[FlyN];
        private readonly SolidColorBrush _flyBrush = new SolidColorBrush(Colors.White);
        private double _flyAcc;

        // 7. глитч на малом барабане
        private readonly Rectangle[] _gl = new Rectangle[3];
        private readonly TranslateTransform[] _glTx = new TranslateTransform[3];
        private double _glT, _glJx;
        private long _lastGlitch;
        private bool _glOn;

        // пауза: колонки тускнеют и тихо «дышат»
        private bool _resting;
        private double _restK, _restT;

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

            // свет на чёрной полосе: большой мягкий овал и поменьше поярче (радиальные градиенты, прозрачные к краю)
            _haloA = MakeHalo(0);
            _haloB = MakeHalo(3);
            _root.Children.Add(_haloA);
            _root.Children.Add(_haloB);

            // лучи прожекторов: узкие клинья из колонки, вращаются вокруг её центра
            var rayBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 1), EndPoint = new Point(0.5, 0) };
            byte[] rayAlpha = { 170, 50, 0 };
            double[] rayOffs = { 0.0, 0.55, 1.0 };
            for (int i = 0; i < 3; i++)
            {
                var gs = new GradientStop(Color.FromArgb(rayAlpha[i], 0x30, 0xD1, 0x58), rayOffs[i]);
                rayBrush.GradientStops.Add(gs);
                _rayStops[i] = gs;
            }
            for (int i = 0; i < RayN; i++)
            {
                _rayRot[i] = new RotateTransform(0);
                _ray[i] = new Polygon
                {
                    Fill = rayBrush,
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = _rayRot[i],
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                _rayLayer.Children.Add(_ray[i]);
            }
            _root.Children.Add(_rayLayer);

            // свечение за колонкой: яркий мягкий прямоугольник, растёт вместе с интенсивностью музыки
            _bloom = new Border
            {
                Background = _accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _bloomScale,
                Opacity = 0
            };
            _root.Children.Add(_bloom);

            // волны звука: кольца вокруг колонки, расходятся на каждый удар
            for (int i = 0; i < Rings; i++)
            {
                _ringScale[i] = new ScaleTransform(1, 1);
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

            // кольца «накачки»: на нарастании сходятся к колонке, чем выше напряжение — тем чаще
            for (int i = 0; i < Charges; i++)
            {
                _chargeScale[i] = new ScaleTransform(1, 1);
                _charge[i] = new Border
                {
                    BorderBrush = _accent,
                    BorderThickness = new Thickness(2),
                    Background = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = _chargeScale[i],
                    Opacity = 0
                };
                _root.Children.Add(_charge[i]);
            }

            // ударная волна дропа: красное и синее кольца (цветовой разнос) и яркая вспышка в центре
            var shockBrushes = new[] { new SolidColorBrush(Color.FromRgb(255, 60, 80)), new SolidColorBrush(Color.FromRgb(60, 170, 255)) };
            _flare = new Ellipse
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _flareSc,
                IsHitTestVisible = false,
                Opacity = 0,
                Fill = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(230, 255, 255, 255), 0.0),
                        new GradientStop(Color.FromArgb(80, 255, 255, 255), 0.45),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
                    }
                }
            };
            _root.Children.Add(_flare);
            for (int i = 0; i < 2; i++)
            {
                _shockSc[i] = new ScaleTransform(1, 1);
                _shock[i] = new Ellipse
                {
                    Stroke = shockBrushes[i],
                    StrokeThickness = 3.5,
                    Fill = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = _shockSc[i],
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                _root.Children.Add(_shock[i]);
            }

            // LED-шкалы по бокам корпуса: сверху вниз от высоких частот к низким
            for (int p = 0; p < 2; p++)
            {
                _led[p] = new Rectangle[AudioLevelMonitor.Bands];
                _ledTx[p] = new TranslateTransform();
                var sp = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = _ledTx[p],
                    IsHitTestVisible = false
                };
                for (int i = 0; i < AudioLevelMonitor.Bands; i++)
                {
                    var r = new Rectangle { Fill = _frame, RadiusX = 1, RadiusY = 1, Opacity = 0 };
                    sp.Children.Add(r);
                    _led[p][i] = r;
                }
                _ledPanel[p] = sp;
                _root.Children.Add(sp);
            }

            // кимати: кольца-рябь на диффузоре (лежат внутри динамика)
            for (int i = 0; i < Kyms; i++)
            {
                _kymSc[i] = new ScaleTransform(0.15, 0.15);
                _kym[i] = new Ellipse
                {
                    Stroke = _frame,
                    StrokeThickness = 1.4,
                    Fill = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = _kymSc[i],
                    IsHitTestVisible = false,
                    Opacity = 0
                };
            }

            _tweeter = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                     Stroke = _frame, StrokeThickness = 2, Fill = Brushes.Black,
                                     RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _tweeterScale };
            _woofer = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                    Stroke = _frame, StrokeThickness = 3, Fill = _wooferFill,
                                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _wooferScale };
            _cap = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                 Fill = _accent, Opacity = 0.85,
                                 RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _wooferScale };

            _surround = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                      Stroke = _frame, StrokeThickness = 1.2, Fill = Brushes.Transparent, Opacity = 0.35,
                                      RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _wooferScale };
            _gloss = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                   RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _wooferScale, IsHitTestVisible = false,
                                   Fill = new LinearGradientBrush
                                   {
                                       StartPoint = new Point(0.2, 0.0), EndPoint = new Point(0.7, 1.0),
                                       GradientStops =
                                       {
                                           new GradientStop(Color.FromArgb(120, 255, 255, 255), 0.0),
                                           new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55)
                                       }
                                   } };
            _port = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                 BorderBrush = _frame, BorderThickness = new Thickness(1.2), Background = Brushes.Black };
            _cabGloss = new Border { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch,
                                     IsHitTestVisible = false, Opacity = 0.5,
                                     Background = new LinearGradientBrush
                                     {
                                         StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                                         GradientStops =
                                         {
                                             new GradientStop(Color.FromArgb(60, 255, 255, 255), 0.0),
                                             new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
                                         }
                                     } };

            var rows = new Grid();
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.2, GridUnitType.Star) });
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.35, GridUnitType.Star) });
            Grid.SetRow(_tweeter, 0);
            var wooferGrid = new Grid();
            wooferGrid.Children.Add(_woofer);
            wooferGrid.Children.Add(_surround);
            foreach (var k in _kym) wooferGrid.Children.Add(k);
            wooferGrid.Children.Add(_cap);
            wooferGrid.Children.Add(_gloss);
            Grid.SetRow(wooferGrid, 1);
            Grid.SetRow(_port, 2);
            rows.Children.Add(_tweeter);
            rows.Children.Add(wooferGrid);
            rows.Children.Add(_port);

            var face = new Grid();
            face.Children.Add(rows);
            face.Children.Add(_cabGloss);

            _body = new Border
            {
                Background = _bg,
                BorderBrush = _frame,
                BorderThickness = new Thickness(2.5),
                Child = face,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _bodyScale
            };
            _root.Children.Add(_body);

            // обводка на дропе и бегущий огонь по рамке (двигаются по рамке корпуса вместе с его сжатием)
            _outline = new Border
            {
                BorderBrush = _runBrush,
                BorderThickness = new Thickness(2.5),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _bodyScale,
                IsHitTestVisible = false,
                Opacity = 0
            };
            _root.Children.Add(_outline);
            for (int i = 0; i < 3; i++)
            {
                _runTx[i] = new TranslateTransform();
                _runSc[i] = new ScaleTransform(1, 1);
                var rg = new TransformGroup();
                rg.Children.Add(_runSc[i]);
                rg.Children.Add(_runTx[i]);
                _run[i] = new Ellipse
                {
                    Fill = _runBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = rg,
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                _root.Children.Add(_run[i]);
            }

            // глитч: три цветные полосы, на пару кадров перечёркивают корпус
            Color[] glColors = { Color.FromArgb(150, 255, 255, 255), Color.FromArgb(150, 0, 230, 255), Color.FromArgb(150, 255, 0, 170) };
            for (int i = 0; i < 3; i++)
            {
                _glTx[i] = new TranslateTransform();
                _gl[i] = new Rectangle
                {
                    Fill = new SolidColorBrush(glColors[i]),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = _glTx[i],
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                _root.Children.Add(_gl[i]);
            }

            // искры: заранее созданные точки, двигаются только transform и opacity
            for (int i = 0; i < SparkN; i++)
            {
                _sparkTx[i] = new TranslateTransform();
                _sparkSc[i] = new ScaleTransform(1, 1);
                var g = new TransformGroup();
                g.Children.Add(_sparkSc[i]);
                g.Children.Add(_sparkTx[i]);
                _spark[i] = new Ellipse
                {
                    Fill = _sparkBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = g,
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                _root.Children.Add(_spark[i]);
            }

            // светлячки: заранее созданные точки, двигаются только transform и opacity
            for (int i = 0; i < FlyN; i++)
            {
                _flyTx[i] = new TranslateTransform();
                _flySc[i] = new ScaleTransform(1, 1);
                var fg = new TransformGroup();
                fg.Children.Add(_flySc[i]);
                fg.Children.Add(_flyTx[i]);
                _fly[i] = new Ellipse
                {
                    Fill = _flyBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = fg,
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                _root.Children.Add(_fly[i]);
            }

            // появление: масштаб 0.9→1 + сдвиг + прозрачность (не из нуля), всё через transform / opacity
            var group = new TransformGroup();
            group.Children.Add(_enterScale);
            group.Children.Add(_enterTx);
            group.Children.Add(_shakeTx);
            _root.RenderTransformOrigin = new Point(0.5, 0.5);
            _root.RenderTransform = group;
            _root.Opacity = 0;

            Content = _root;
            SetSize(64);
            StyleId = 0;

            SourceInitialized += (s, e) => MakeOverlay();
            Closed += (s, e) => StopLoop();
        }

        // ---------- стиль ----------

        /// <summary>0 — неон, 1 — ретро, 2 — минимализм.</summary>
        public int StyleId
        {
            get { return _style; }
            set
            {
                if (value < 0 || value > 2) value = 0;
                if (value == _style) return;
                _style = value;
                ApplyStyle();
            }
        }

        /// <summary>Экономия видеопамяти: без света на полосе и без искр.</summary>
        public bool Lite
        {
            get { return _lite; }
            set
            {
                if (value == _lite) return;
                _lite = value;
                if (_style >= 0) ApplyStyle();
            }
        }

        private void ApplyStyle()
        {
            bool retro = _style == 1, mini = _style == 2;

            _baseColor = retro ? Color.FromRgb(0x3A, 0x26, 0x16) : Colors.Black;
            _wooferFill.Color = retro ? Color.FromRgb(0x24, 0x17, 0x0E) : Color.FromRgb(0x10, 0x10, 0x12);

            _haloK = _lite ? 0 : (retro ? 0.45 : (mini ? 0 : 1));
            _sparkOn = (_lite || retro || mini) ? 0 : 1;
            _bloomK = mini ? 0.5 : (retro ? 0.8 : 1);
            _tintK = mini ? 0.55 : 1;

            _body.BorderThickness = new Thickness(retro ? 3.5 : (mini ? 1.5 : 2.5));
            _woofer.StrokeThickness = retro ? 4 : (mini ? 2 : 3);
            _tweeter.StrokeThickness = retro ? 2.5 : (mini ? 1.5 : 2);
            foreach (var r in _rings) r.BorderThickness = new Thickness(mini ? 1.5 : 2.5);

            _surround.Opacity = mini ? 0 : (retro ? 0.75 : 0.35);
            _gloss.Opacity = mini ? 0 : (retro ? 0.4 : 0.85);
            _cabGloss.Opacity = mini ? 0 : (retro ? 0.25 : 0.5);
            _port.Visibility = mini ? Visibility.Hidden : Visibility.Visible;

            var hv = _haloK > 0 ? Visibility.Visible : Visibility.Collapsed;
            _haloA.Visibility = hv;
            _haloB.Visibility = hv;

            // новые эффекты: неон — все; ретро — мягкая часть; минимализм — только затишье и обычные волны
            _ledK = mini ? 0 : (retro ? 0.7 : 1);
            _rayK = (_lite || retro || mini) ? 0 : 1;
            _kymK = mini ? 0 : (retro ? 0.6 : 1);
            _shockK = (_lite || mini) ? 0 : (retro ? 0.6 : 1);
            _runK = mini ? 0 : 1;
            _flyK = (_lite || retro || mini) ? 0 : 1;
            _glitchK = (_lite || retro || mini) ? 0 : 1;

            _rayLayer.Visibility = _rayK > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var lp in _ledPanel) lp.Visibility = _ledK > 0 ? Visibility.Visible : Visibility.Collapsed;

            _lastFrame = Colors.Transparent;   // обводку нужно перекрасить
            _lastHalo = Colors.Transparent;
        }

        private Ellipse MakeHalo(int first)
        {
            var br = new RadialGradientBrush();
            byte[] alpha = { 200, 90, 0 };
            double[] offs = { 0.0, 0.5, 1.0 };
            for (int i = 0; i < 3; i++)
            {
                var gs = new GradientStop(Color.FromArgb(alpha[i], 0x30, 0xD1, 0x58), offs[i]);
                br.GradientStops.Add(gs);
                _haloStops[first + i] = gs;
            }

            return new Ellipse
            {
                Fill = br,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Opacity = 0
            };
        }

        private void SetHaloColor(Color c)
        {
            for (int i = 0; i < _haloStops.Length; i++)
                _haloStops[i].Color = Color.FromArgb(_haloStops[i].Color.A, c.R, c.G, c.B);
        }

        // ---------- искры ----------

        private void SpawnSparks(int count)
        {
            double R = _bodyW;
            Color sc = Lerp(_color, Colors.White, 0.55);
            _sparkBrush.Color = sc;

            for (int k = 0; k < SparkN && count > 0; k++)
            {
                if (_spLife[k] > 0) continue;
                count--;

                double ang = _rnd.NextDouble() * 2 * Math.PI;
                double speed = (1.2 + 1.0 * _rnd.NextDouble()) * R;      // пикселей в секунду
                double r0 = 0.30 * R;                                    // вылетают от края динамика

                _spX[k] = Math.Cos(ang) * r0;
                _spY[k] = 0.45 * R + Math.Sin(ang) * r0;                  // динамик чуть ниже центра окна
                _spVx[k] = Math.Cos(ang) * speed;
                _spVy[k] = Math.Sin(ang) * speed - 0.25 * R;
                _spLife[k] = 1.0;
                _spDur[k] = 0.55 + 0.25 * _rnd.NextDouble();
            }
        }

        // ---------- пауза ----------

        /// <summary>Видео на паузе: колонка остаётся на месте, тускнеет и тихо «дышит».</summary>
        public void Rest(Color color)
        {
            _color = color;
            _level = 0;
            _inten = 0;
            _buildT = 0;
            _lullT = 0;
            _bands = null;
            _resting = true;
            _wooferS.Target = 0.78;
            _tweeterS.Target = 0.80;
            _bodyS.Target = 1.0;
            StartLoop();
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
            _bloom.Width = bw; _bloom.Height = bh; _bloom.CornerRadius = new CornerRadius(w * 0.32);
            foreach (var c in _charge) { c.Width = bw; c.Height = bh; c.CornerRadius = new CornerRadius(w * 0.28); }

            Width = Math.Round(bw * 2.2);
            Height = Math.Round(bh * 1.7);

            _haloA.Width = Width; _haloA.Height = Height;
            _haloB.Width = Math.Round(Width * 0.72); _haloB.Height = Math.Round(Height * 0.72);

            double sk = Math.Max(3, Math.Round(w * 0.075));
            foreach (var sp in _spark) { sp.Width = sp.Height = sk; }

            _port.Width = Math.Round(w * 0.46);
            _port.Height = Math.Max(4, Math.Round(w * 0.09));
            _port.CornerRadius = new CornerRadius(_port.Height / 2);
            _cabGloss.Height = Math.Round(bh * 0.26);
            _cabGloss.CornerRadius = new CornerRadius(w * 0.2, w * 0.2, w * 0.1, w * 0.1);
            _cabGloss.Margin = new Thickness(w * 0.04, 0, w * 0.04, 0);

            _body.CornerRadius = new CornerRadius(w * 0.28);
            _body.Padding = new Thickness(w * 0.10);

            _tweeter.Width = _tweeter.Height = Math.Round(w * 0.30);
            _woofer.Width = _woofer.Height = Math.Round(w * 0.62);
            _cap.Width = _cap.Height = Math.Round(w * 0.20);
            _surround.Width = _surround.Height = Math.Round(w * 0.46);
            _gloss.Width = _gloss.Height = Math.Round(w * 0.56);

            // лучи: клин от центра динамика вверх, поворачивается вокруг острия
            double cx = Width / 2, cy = Height / 2 + 0.1 * bh;
            double rayL = Height * 0.40, rayA = rayL * 0.075;
            _rayLayer.Width = Width;
            _rayLayer.Height = Height;
            foreach (var rp in _ray)
            {
                rp.Points = new PointCollection { new Point(rayA, rayL), new Point(0, 0), new Point(2 * rayA, 0) };
                rp.Width = 2 * rayA;
                rp.Height = rayL;
                Canvas.SetLeft(rp, cx - rayA);
                Canvas.SetTop(rp, cy - rayL);
            }

            // LED-шкалы по бокам корпуса
            double ledW = Math.Max(2, Math.Round(w * 0.06));
            double gapY = Math.Max(1.5, w * 0.02);
            double ledH = Math.Max(2, (bh * 0.82) / AudioLevelMonitor.Bands - gapY);
            double ledX = bw / 2 + w * 0.07 + ledW / 2;
            for (int pn = 0; pn < 2; pn++)
            {
                _ledTx[pn].X = (pn == 0 ? -1 : 1) * ledX;
                foreach (var lr in _led[pn])
                {
                    lr.Width = ledW;
                    lr.Height = ledH;
                    lr.Margin = new Thickness(0, gapY / 2, 0, gapY / 2);
                }
            }

            // кимати, ударная волна, обводка, огонь, глитч, светлячки
            foreach (var ky in _kym) ky.Width = ky.Height = Math.Round(w * 0.56);
            foreach (var sh in _shock) { sh.Width = Width; sh.Height = Height; }
            _flare.Width = Math.Round(Width * 0.9);
            _flare.Height = Math.Round(Height * 0.9);
            _outline.Width = bw;
            _outline.Height = bh;
            _outline.CornerRadius = new CornerRadius(w * 0.28);
            double runD = Math.Max(3, Math.Round(w * 0.09));
            foreach (var rn in _run) rn.Width = rn.Height = runD;
            foreach (var g in _gl) g.Width = Math.Round(bw * 1.1);
            double flyD = Math.Max(3, Math.Round(w * 0.055));
            foreach (var fl in _fly) fl.Width = fl.Height = flyD;
        }

        // ---------- вход от музыки (вызывается ~30 раз в секунду, рисует цикл кадров) ----------

        /// <summary>
        /// Цвет колонки и «эмоции»: level 0..1 — как сильно бьёт, high 0..1 — верхние частоты,
        /// beat — только что был удар (толчок пружинам + волна), flash 0..1 — вспышка фона.
        /// </summary>
        public void Pulse(Color color, float level, float high, bool beat, float flash, float intensity,
                          float beatPower = 1f, float build = 0f, bool drop = false,
                          float lull = 0f, float dropBoost = 0f, bool snare = false, float[] bands = null)
        {
            level = Math.Max(0f, Math.Min(1f, level));
            high = Math.Max(0f, Math.Min(1f, high));
            flash = Math.Max(0f, Math.Min(1f, flash));

            _color = color;
            _level = level;
            _high = high;
            _bands = bands;
            _resting = false;
            _inten = Clamp(intensity, 0, 1);
            double I = _inten;
            _buildT = Clamp(build, 0, 1);
            double B = _buildT;
            _lullT = Clamp(lull, 0, 1);
            double L = _lullT, lk = 1 - L;
            beatPower = Math.Max(0f, Math.Min(1f, beatPower));

            // цели пружин: куда динамик, твитер и корпус «хотят» прийти (на пике музыки — шире и ярче);
            // в затишье всё сжимается и тускнеет: колонка «вдыхает» перед взрывом
            _wooferS.Target = 0.78 + (0.08 * I + 0.22 * level + 0.05 * B) * lk - 0.12 * L;
            _tweeterS.Target = 0.80 + (0.55 * high + 0.15 * I) * lk - 0.10 * L;
            _bodyS.Target = 1 + (0.03 * level + 0.04 * flash + 0.03 * I) * lk - 0.03 * B - 0.05 * L;   // на нарастании корпус «сжимается», на дропе — отпускает
            _tintTarget = Math.Min(0.85, (0.05 + 0.28 * level + 0.7 * flash + 0.30 * I + 0.22 * B) * (1 - 0.75 * L));

            if (beat && !_reduced)
            {
                // удар — это толчок скорости, а не прыжок значения; чем интенсивнее музыка, тем сильнее толчок
                // beatPower < 1 — удар только в своём канале (гитара слева, например): толчок слабее
                double p = (0.55 + 0.45 * level) * beatPower;
                _wooferS.V += (5.0 + 11.0 * I) * p;
                _bodyS.V += (2.0 + 5.0 * I) * p;
                _tweeterS.V += (3.5 + 3.0 * I) * high * beatPower;

                SpawnRing(1.0f);
                if (I > 0.5 && beatPower > 0.8) SpawnRing(1.3f);   // на пике — двойная волна
                SpawnKym();                                         // рябь на диффузоре

                // искры только на самых сильных ударах и не чаще, чем позволяет пресет
                if (_sparkOn > 0 && beatPower > 0.8 && I > SparkMinInten && level > SparkMinLevel)
                {
                    long t = Stopwatch.GetTimestamp();
                    if ((t - _lastSpark) / (double)Stopwatch.Frequency > SparkGap)
                    {
                        _lastSpark = t;
                        SpawnSparks(8 + (int)Math.Round(4 * I));   // 8…12 штук
                    }
                }

                // на сильной музыке колонку трясёт
                if (I > 0.3)
                {
                    _shakeX.V += (_rnd.NextDouble() * 2 - 1) * (40 + 170 * I) * beatPower;
                    _shakeY.V += (_rnd.NextDouble() * 2 - 1) * (25 + 110 * I) * beatPower;
                }
            }

            // глитч: малый барабан на заметной музыке, не чаще пяти раз в секунду
            if (snare && _glitchK > 0 && !_reduced && I > 0.25 && L < 0.3)
            {
                long tg = Stopwatch.GetTimestamp();
                if ((tg - _lastGlitch) / (double)Stopwatch.Frequency > 0.2)
                {
                    _lastGlitch = tg;
                    _glT = 0.07 + 0.04 * _rnd.NextDouble();
                }
            }

            // «дроп»: после нарастания или затишья — одна большая вспышка; после затишья она сильнее
            // (при выключенной анимации Windows остаётся только свет и цвет)
            if (drop)
            {
                _dropEnv = 1;
                _dropGain = 1 + Clamp(dropBoost, 0, 0.8);
                double g = _dropGain;
                _shockPh = 0.0001;
                if (!_reduced)
                {
                    _wooferS.V += 24 * g;
                    _bodyS.V += 14 * g;
                    _tweeterS.V += 8 * g;
                    SpawnRing(1.0f);
                    SpawnRing(1.18f);
                    if (_sparkOn > 0) { _lastSpark = Stopwatch.GetTimestamp(); SpawnSparks(SparkN); }
                    ExplodeFlies(g);
                    _shakeX.V += (_rnd.NextDouble() * 2 - 1) * 330 * g;
                    _shakeY.V += (_rnd.NextDouble() * 2 - 1) * 220 * g;
                }
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
            _time += dt;

            // пауза: тихое дыхание раз в ~4,5 с
            _restT += dt;
            double breath = 0.5 - 0.5 * Math.Cos(2 * Math.PI * _restT / 4.5);
            _restK += ((_resting ? 1.0 : 0.0) - _restK) * (1 - Math.Exp(-dt * 3));
            if (_resting && !_reduced)
            {
                _wooferS.Target = 0.78 + 0.03 * breath;
                _tweeterS.Target = 0.80;
                _bodyS.Target = 1 + 0.008 * breath;
            }
            if (_resting) _tintTarget = 0.03 + 0.05 * breath;

            // затишье перед дропом: плавно входим, быстро выходим; lk — во сколько раз гаснет свет
            _lullS += (_lullT - _lullS) * (1 - Math.Exp(-dt * (_lullT > _lullS ? 4.0 : 12.0)));
            double lk = 1 - 0.85 * _lullS;
            double fk = Floating ? 0.45 : 1.0;   // поверх картинки свет и лучи мягче

            // мелкие шаги, чтобы пружины оставались устойчивыми при любой частоте кадров
            int steps = (int)Math.Ceiling(dt / 0.008);
            double h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                _wooferS.Step(h);
                _tweeterS.Step(h);
                _bodyS.Step(h);
                _enterS.Step(h);
                _shakeX.Step(h);
                _shakeY.Step(h);
            }

            // динамик, твитер, корпус
            double w = Clamp(_wooferS.X, 0.55, 1.30);
            _wooferScale.ScaleX = _wooferScale.ScaleY = w;
            double t = Clamp(_tweeterS.X, 0.55, 1.6);
            _tweeterScale.ScaleX = _tweeterScale.ScaleY = t;
            double b = Clamp(_bodyS.X, 0.9, 1.18);
            _bodyScale.ScaleX = _bodyScale.ScaleY = b;
            _cap.Opacity = 0.55 + 0.45 * Clamp(_level, 0, 1);

            // нарастание плавно подтягивается к тому, что прислал остров; вспышка дропа гаснет за ~0,5 с
            _buildS += (_buildT - _buildS) * (1 - Math.Exp(-dt * 6));
            _dropEnv *= Math.Exp(-dt / 0.5);
            if (_dropEnv < 0.01) { _dropEnv = 0; _dropGain = 1; }
            double de = _dropEnv * _dropGain;   // вспышка дропа с учётом силы (после затишья до 1,8)

            // стекло «глитча»: корпус на пару кадров дёргается
            if (_glT > 0 && _glitchK > 0 && !_reduced)
            {
                _glT -= dt;
                if (_glT <= 0)
                {
                    _glT = 0;
                    _glJx = 0;
                    _glOn = false;
                    foreach (var gr in _gl) gr.Opacity = 0;
                }
                else
                {
                    _glOn = true;
                    _glJx = (_rnd.NextDouble() * 2 - 1) * 0.07 * _bodyW;
                    double bodyH = _body.Height;
                    for (int i = 0; i < 3; i++)
                    {
                        _glTx[i].X = (_rnd.NextDouble() * 2 - 1) * 0.14 * _bodyW;
                        _glTx[i].Y = (_rnd.NextDouble() * 2 - 1) * 0.45 * bodyH;
                        _gl[i].Height = Math.Max(2, bodyH * (0.04 + 0.05 * _rnd.NextDouble()));
                        _gl[i].Opacity = 0.85;
                    }
                }
            }
            else if (_glOn || _glT > 0)
            {
                _glT = 0;
                _glJx = 0;
                _glOn = false;
                foreach (var gr in _gl) gr.Opacity = 0;
            }

            _shakeTx.X = Clamp(_shakeX.X, -14, 14) + _glJx;
            _shakeTx.Y = Clamp(_shakeY.X, -10, 10);

            // «жар»: на сильной музыке и удар цвет колонки уходит в белое
            Color accent = Lerp(_color, Colors.White,
                Math.Min(0.8, 0.35 * _inten * Clamp(_level, 0, 1) * lk + 0.25 * _buildS + 0.6 * de));
            if (_style == 1) accent = Lerp(accent, Amber, 0.4);   // ретро: тёплый свет
            if (accent != _lastAccent) { _accent.Color = accent; _lastAccent = accent; }

            // рамка и обводка: у ретро — постоянный кремовый цвет, у остальных — цвет акцента
            Color frameC = _style == 1 ? Cream : accent;
            if (frameC != _lastFrame) { _frame.Color = frameC; _lastFrame = frameC; }

            // свечение за колонкой
            _bloom.Opacity = Math.Min(1.0, ((0.04 + 0.30 * _inten * (0.5 + 0.5 * Clamp(_level, 0, 1)) + 0.22 * _buildS) * lk + 0.55 * de) * _bloomK * (Floating ? 0.6 : 1.0)
                                           + 0.10 * _restK * breath);
            double bs = 1.06 + 0.22 * _inten * lk + 0.35 * (Clamp(_bodyS.X, 1, 1.18) - 1) + 0.10 * _buildS + 0.12 * de;
            _bloomScale.ScaleX = _bloomScale.ScaleY = bs;

            // свет на чёрной полосе вокруг колонки (два слоя; цвет меняем только когда он заметно сменился)
            if (_haloK > 0)
            {
                if (ColorDist(_color, _lastHalo) > 12) { SetHaloColor(_color); _lastHalo = _color; }

                double lv = Clamp(_level, 0, 1);
                double hA = (0.10 + 0.28 * _inten * (0.5 + 0.5 * lv) + 0.22 * _buildS) * lk + 0.45 * de + 0.10 * _restK * breath;
                _haloA.Opacity = Math.Min(1.0, hA * _haloK * fk);
                _haloB.Opacity = Math.Min(1.0, (0.6 * hA + 0.25 * de + 0.18 * lv * _inten * lk) * _haloK * fk);
            }

            // искры: у каждой своя скорость, лёгкое торможение и «гравитация»
            double R = _bodyW;
            double drag = Math.Exp(-3.6 * dt);
            for (int i = 0; i < SparkN; i++)
            {
                double life = _spLife[i];
                if (life <= 0) continue;

                life -= dt / _spDur[i];
                if (life <= 0)
                {
                    _spLife[i] = 0;
                    _spark[i].Opacity = 0;
                    continue;
                }

                _spLife[i] = life;
                _spVx[i] *= drag;
                _spVy[i] = _spVy[i] * drag + 0.9 * R * dt;
                _spX[i] += _spVx[i] * dt;
                _spY[i] += _spVy[i] * dt;

                _sparkTx[i].X = _spX[i];
                _sparkTx[i].Y = _spY[i];
                double ss = 0.4 + 0.6 * life;
                _sparkSc[i].ScaleX = _sparkSc[i].ScaleY = ss;
                _spark[i].Opacity = Math.Pow(life, 1.3);
            }

            // подсветка корпуса: мягкое приближение, без рывков
            _tint += (_tintTarget - _tint) * (1 - Math.Exp(-dt * 16));
            if (de > 0) _tint = Math.Max(_tint, Math.Min(1.0, 0.9 * de));   // на дропе корпус вспыхивает сразу
            double tt = Clamp(_tint * _tintK, 0, 1);
            Color bg = Color.FromRgb((byte)(_baseColor.R * (1 - tt) + _color.R * tt),
                                     (byte)(_baseColor.G * (1 - tt) + _color.G * tt),
                                     (byte)(_baseColor.B * (1 - tt) + _color.B * tt));
            if (bg != _lastBg) { _bg.Color = bg; _lastBg = bg; }

            // волны: по времени, а не по числу кадров; быстро разбегаются и плавно гаснут
            double ringDur = 0.62 - 0.22 * _inten;     // на сильной музыке волны быстрее
            double ringExt = 0.50 + 0.40 * _inten;     // и шире
            for (int i = 0; i < Rings; i++)
            {
                float ph = _ringPhase[i];
                if (ph <= 0f) continue;

                ph += (float)(dt / ringDur);
                if (ph >= 1f)
                {
                    _ringPhase[i] = 0f;
                    _rings[i].Opacity = 0;
                    _ringScale[i].ScaleX = _ringScale[i].ScaleY = 1;
                    continue;
                }

                _ringPhase[i] = ph;
                double inv = 1 - ph;
                double sc = 1 + ringExt * _ringGain[i] * (1 - inv * inv * inv);   // ease-out
                _ringScale[i].ScaleX = sc;
                _ringScale[i].ScaleY = sc;
                _rings[i].Opacity = 0.85 * inv * Math.Sqrt(inv);
            }

            // кольца «накачки»: сходятся к колонке тем быстрее, чем выше напряжение; в затишье — «вдох», шире и быстрее
            double charge = Math.Max(_buildS, 0.9 * _lullS);
            if (charge > 0.03 && !_reduced)
            {
                double speed = 1.0 / (1.0 - 0.6 * charge) * (1 + 0.8 * _lullS);   // оборотов в секунду
                for (int i = 0; i < Charges; i++)
                {
                    double u = _chargePhase[i] + dt * speed;
                    if (u >= 1) u -= Math.Floor(u);
                    _chargePhase[i] = u;

                    double sc = (1.5 + 0.35 * _lullS) - (0.5 + 0.35 * _lullS) * u;   // 1.5 → 1.0 (в затишье 1.85 → 1.0)
                    _chargeScale[i].ScaleX = _chargeScale[i].ScaleY = sc;
                    _charge[i].Opacity = 0.8 * charge * Math.Sin(Math.PI * u);
                }
            }
            else
            {
                for (int i = 0; i < Charges; i++) _charge[i].Opacity = 0;
            }

            // 1. LED-шкалы: быстро вверх, плавно вниз
            if (_ledK > 0)
            {
                int nb = AudioLevelMonitor.Bands;
                double atk = 1 - Math.Exp(-dt * 40), dec = 1 - Math.Exp(-dt * 9);
                bool live = _bands != null && !_resting;
                for (int i = 0; i < nb; i++)
                {
                    double tv = live ? Clamp(_bands[i], 0, 1) : 0;
                    double cv = _ledV[i];
                    _ledV[i] = cv + (tv - cv) * (tv > cv ? atk : dec);
                }

                double ledAmp = _ledK * (1 - 0.7 * _restK);
                for (int pn = 0; pn < 2; pn++)
                    for (int i = 0; i < nb; i++)
                        _led[pn][i].Opacity = (0.06 + 0.94 * _ledV[nb - 1 - i]) * ledAmp;
            }

            // 2. лучи прожекторов: качаются, мигают, на дропе раскрываются веером
            if (_rayK > 0 && !_reduced)
            {
                double rayAmt = _rayK * fk * (1 - _restK)
                    * Math.Min(1.0, (0.9 * Clamp(_inten - 0.25, 0, 1) * (0.4 + 0.6 * _high) + 0.5 * _buildS + 0.9 * de) * lk);
                if (rayAmt > 0.004)
                {
                    if (ColorDist(_color, _lastRay) > 12) { SetRayColor(_color); _lastRay = _color; }
                    double spread = 1 + 0.5 * Math.Min(1.0, de);
                    for (int i = 0; i < RayN; i++)
                    {
                        double rph = i * 1.3;
                        _rayRot[i].Angle = ((i - 2) * 13.0 + 10.0 * Math.Sin(_time * (0.7 + 0.17 * i) + rph)) * spread;
                        _ray[i].Opacity = Math.Min(1.0, rayAmt * (0.55 + 0.45 * Math.Sin(_time * (2.1 + 0.4 * i) + rph * 2)));
                    }
                }
                else
                {
                    for (int i = 0; i < RayN; i++) _ray[i].Opacity = 0;
                }
            }

            // 3. кимати: рябь расходится от центра динамика на каждый удар, а на сильной музыке — и сама по себе
            if (_kymK > 0 && !_reduced)
            {
                if (_inten > 0.25 && !_resting)
                {
                    _kymTimer -= dt;
                    if (_kymTimer <= 0) { SpawnKym(); _kymTimer = 0.55 - 0.30 * _inten; }
                }

                for (int i = 0; i < Kyms; i++)
                {
                    double kph = _kymPh[i];
                    if (kph <= 0) continue;

                    kph += dt / 0.6;
                    if (kph >= 1)
                    {
                        _kymPh[i] = 0;
                        _kym[i].Opacity = 0;
                        continue;
                    }

                    _kymPh[i] = kph;
                    double kin = 1 - kph;
                    double ksc = 0.15 + 0.85 * (1 - kin * kin);
                    _kymSc[i].ScaleX = _kymSc[i].ScaleY = ksc;
                    _kym[i].Opacity = 0.8 * _kymK * kin * kin;
                }
            }
            else
            {
                for (int i = 0; i < Kyms; i++) { _kymPh[i] = 0; _kym[i].Opacity = 0; }
            }

            // 4. ударная волна дропа: красное и синее кольца расходятся с небольшим отставанием, в центре — вспышка
            if (_shockPh > 0)
            {
                _shockPh += dt / (0.75 + 0.15 * (_dropGain - 1));
                if (_shockPh >= 1 || _shockK <= 0 || _reduced)
                {
                    _shockPh = 0;
                    _shock[0].Opacity = _shock[1].Opacity = _flare.Opacity = 0;
                }
                else
                {
                    double sph = _shockPh;
                    double sinv = 1 - sph;
                    double sEase = 1 - sinv * sinv * sinv;
                    double sphB = Math.Max(0, sph - 0.07);
                    double sEaseB = 1 - Math.Pow(1 - sphB, 3);
                    _shockSc[0].ScaleX = _shockSc[0].ScaleY = 0.2 + 0.8 * sEase;
                    _shockSc[1].ScaleX = _shockSc[1].ScaleY = 0.2 + 0.8 * sEaseB;
                    _shock[0].Opacity = _shockK * Math.Pow(sinv, 1.3) * 0.9;
                    _shock[1].Opacity = _shockK * Math.Pow(1 - sphB, 1.3) * 0.9;

                    double fph = Math.Min(1.0, sph * 1.4);
                    double fs = 0.15 + 0.85 * (1 - Math.Pow(1 - fph, 3));
                    _flareSc.ScaleX = _flareSc.ScaleY = fs;
                    _flare.Opacity = _shockK * Math.Max(0, 1 - sph * 1.7) * 0.75;
                }
            }

            // 5. бегущий огонь по рамке: чем напряжённее музыка, тем быстрее; на дропе — замыкается в обводку
            if (_runK > 0 && !_reduced)
            {
                double runSpeed = (0.18 + 0.7 * _buildS + 0.25 * _inten + 1.2 * Math.Min(1.0, de)) * (1 - 0.7 * _lullS);
                _runU += dt * runSpeed;
                _runU -= Math.Floor(_runU);

                double runAmt = _runK * (1 - _restK) * Math.Min(1.0, 0.22 + 0.5 * _inten + 0.5 * _buildS + 0.8 * de) * (1 - 0.6 * _lullS);
                Color rc = Lerp(_accent.Color, Colors.White, 0.6);
                if (rc != _lastRun) { _runBrush.Color = rc; _lastRun = rc; }

                double rhw = _body.Width / 2 - 1, rhh = _body.Height / 2 - 1;
                double rr = Math.Min(_bodyW * 0.28, rhw);
                for (int i = 0; i < 3; i++)
                {
                    double px, py;
                    Perim(_runU - 0.03 * i, rhw, rhh, rr, out px, out py);
                    _runTx[i].X = px * b;
                    _runTx[i].Y = py * b;
                    _runSc[i].ScaleX = _runSc[i].ScaleY = 1 - 0.25 * i;
                    _run[i].Opacity = runAmt * (i == 0 ? 1.0 : (i == 1 ? 0.55 : 0.28));
                }

                _outline.Opacity = _runK * Math.Min(1.0, 0.95 * de);
            }
            else
            {
                for (int i = 0; i < 3; i++) _run[i].Opacity = 0;
                _outline.Opacity = 0;
            }

            // 6. светлячки: на нарастании и в затишье слетаются к колонке; на дропе разлетаются (см. ExplodeFlies)
            if (_flyK > 0 && !_reduced)
            {
                double cyF = 0.3 * R;
                _flyAcc += dt * (18 * _buildS + 14 * _lullS) * (1 - _restK);
                while (_flyAcc >= 1) { _flyAcc -= 1; SpawnFlyIn(); }

                Color fc = Lerp(accent, Colors.White, 0.6);
                if (fc != _lastFly) { _flyBrush.Color = fc; _lastFly = fc; }

                double fdragIn = Math.Exp(-1.3 * dt), fdragOut = Math.Exp(-2.4 * dt);
                for (int i = 0; i < FlyN; i++)
                {
                    double fl = _fLife[i];
                    if (fl <= 0) continue;

                    fl -= dt / _fDur[i];
                    if (fl <= 0) { _fLife[i] = 0; _fly[i].Opacity = 0; continue; }
                    _fLife[i] = fl;

                    if (_fIn[i])
                    {
                        double dx = -_fX[i], dy = cyF - _fY[i];
                        double dd = Math.Sqrt(dx * dx + dy * dy) + 1e-3;
                        double ax = dx / dd, ay = dy / dd;
                        _fVx[i] += (ax * 7.0 * R - ay * 2.2 * R) * dt;    // тянет к центру и слегка закручивает
                        _fVy[i] += (ay * 7.0 * R + ax * 2.2 * R) * dt;
                        _fVx[i] *= fdragIn;
                        _fVy[i] *= fdragIn;
                        if (dd < 0.2 * R) { _fLife[i] = 0; _fly[i].Opacity = 0; continue; }

                        _fly[i].Opacity = 0.9 * Math.Sin(Math.PI * (1 - fl));
                        _flySc[i].ScaleX = _flySc[i].ScaleY = 0.5 + 0.7 * Math.Min(1.0, dd / R);
                    }
                    else
                    {
                        _fVx[i] *= fdragOut;
                        _fVy[i] = _fVy[i] * fdragOut + 0.5 * R * dt;
                        _fly[i].Opacity = Math.Pow(fl, 1.2);
                        _flySc[i].ScaleX = _flySc[i].ScaleY = 0.5 + 0.8 * fl;
                    }

                    _fX[i] += _fVx[i] * dt;
                    _fY[i] += _fVy[i] * dt;
                    _flyTx[i].X = _fX[i];
                    _flyTx[i].Y = _fY[i];
                }
            }
            else
            {
                for (int i = 0; i < FlyN; i++) { _fLife[i] = 0; _fly[i].Opacity = 0; }
            }

            // появление / уход
            double en = Clamp(_enterS.X, 0, 1.05);
            double e1 = Math.Min(1, en);
            double amt = _reduced ? 0 : 1;
            _root.Opacity = e1 * (1 - 0.45 * _restK + 0.08 * _restK * breath) * (1 - 0.5 * _lullS) * (Floating ? 0.88 : 1.0);
            _enterScale.ScaleX = _enterScale.ScaleY = 1 - 0.10 * amt * (1 - e1);
            _enterTx.X = _dir * 20 * amt * (1 - e1);

            if (_enterS.Target == 0 && _enterS.X < 0.02 && Math.Abs(_enterS.V) < 0.2)
            {
                _root.Opacity = 0;
                StopLoop();
                try { Hide(); } catch { }
            }
        }

        private Color _lastAccent = Colors.Transparent;

        private Color _lastFly = Colors.Transparent;

        private void SetRayColor(Color c)
        {
            for (int i = 0; i < _rayStops.Length; i++)
                _rayStops[i].Color = Color.FromArgb(_rayStops[i].Color.A, c.R, c.G, c.B);
        }

        private void SpawnKym()
        {
            if (_kymK <= 0 || _reduced) return;

            for (int k = 0; k < Kyms; k++)
            {
                int i = (_kymIdx + k) % Kyms;
                if (_kymPh[i] <= 0)
                {
                    _kymPh[i] = 0.0001;
                    _kymIdx = (i + 1) % Kyms;
                    return;
                }
            }

            _kymPh[_kymIdx] = 0.0001;
            _kymIdx = (_kymIdx + 1) % Kyms;
        }

        /// <summary>Светлячок вылетает издалека и летит к колонке.</summary>
        private void SpawnFlyIn()
        {
            double R = _bodyW;
            for (int i = 0; i < FlyN; i++)
            {
                if (_fLife[i] > 0) continue;

                double ang = _rnd.NextDouble() * 2 * Math.PI;
                double rad = R * (1.0 + 0.9 * _rnd.NextDouble());
                _fX[i] = Math.Cos(ang) * rad;
                _fY[i] = 0.3 * R + Math.Sin(ang) * rad * 1.2;
                _fVx[i] = -Math.Sin(ang) * R * 0.8;
                _fVy[i] = Math.Cos(ang) * R * 0.8;
                _fLife[i] = 1.0;
                _fDur[i] = 0.9 + 0.5 * _rnd.NextDouble();
                _fIn[i] = true;
                return;
            }
        }

        /// <summary>Дроп: все светлячки (и те, что ещё летели к колонке) разлетаются салютом.</summary>
        private void ExplodeFlies(double gain)
        {
            if (_flyK <= 0) return;

            double R = _bodyW;
            for (int i = 0; i < FlyN; i++)
            {
                double ang = _rnd.NextDouble() * 2 * Math.PI;
                double speed = (1.4 + 2.6 * _rnd.NextDouble()) * R * gain;
                _fX[i] = Math.Cos(ang) * 0.25 * R;
                _fY[i] = 0.3 * R + Math.Sin(ang) * 0.25 * R;
                _fVx[i] = Math.Cos(ang) * speed;
                _fVy[i] = Math.Sin(ang) * speed - 0.2 * R;
                _fLife[i] = 1.0;
                _fDur[i] = 0.8 + 0.6 * _rnd.NextDouble();
                _fIn[i] = false;
            }
        }

        /// <summary>Точка на рамке со скруглёнными углами; u — доля периметра от 0 до 1, старт сверху по центру, по часовой стрелке.</summary>
        private static void Perim(double u, double hw, double hh, double r, out double x, out double y)
        {
            double ax = hw - r, ay = hh - r;               // центры дуг
            double sx = 2 * ax, sy = 2 * ay, arc = Math.PI * r / 2;
            double total = 2 * sx + 2 * sy + 4 * arc;
            double s = (u - Math.Floor(u)) * total;
            double seg, a;

            seg = ax;  if (s < seg) { x = s; y = -hh; return; }  s -= seg;
            seg = arc; if (s < seg) { a = -Math.PI / 2 + s / r; x = ax + r * Math.Cos(a); y = -ay + r * Math.Sin(a); return; }  s -= seg;
            seg = sy;  if (s < seg) { x = hw; y = -ay + s; return; }  s -= seg;
            seg = arc; if (s < seg) { a = s / r; x = ax + r * Math.Cos(a); y = ay + r * Math.Sin(a); return; }  s -= seg;
            seg = sx;  if (s < seg) { x = ax - s; y = hh; return; }  s -= seg;
            seg = arc; if (s < seg) { a = Math.PI / 2 + s / r; x = -ax + r * Math.Cos(a); y = ay + r * Math.Sin(a); return; }  s -= seg;
            seg = sy;  if (s < seg) { x = -hw; y = ay - s; return; }  s -= seg;
            seg = arc; if (s < seg) { a = Math.PI + s / r; x = -ax + r * Math.Cos(a); y = -ay + r * Math.Sin(a); return; }  s -= seg;
            x = -ax + s;
            y = -hh;
        }

        private void SpawnRing(float gain)
        {
            // берём свободное кольцо; если все заняты — самое старое
            for (int k = 0; k < Rings; k++)
            {
                int i = (_ringIdx + k) % Rings;
                if (_ringPhase[i] <= 0f)
                {
                    _ringPhase[i] = 0.0001f;
                    _ringGain[i] = gain;
                    _ringIdx = (i + 1) % Rings;
                    return;
                }
            }

            _ringPhase[_ringIdx] = 0.0001f;
            _ringGain[_ringIdx] = gain;
            _ringIdx = (_ringIdx + 1) % Rings;
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        private static int ColorDist(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
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
