using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DreamIsland
{
    /// <summary>
    /// Интерфейс DreamIslands (по правому клику на остров): четыре кубика сходятся в центре экрана,
    /// превращаются в прямоугольник, растут вширь и ввысь, затем по очереди появляются разделы.
    /// Чёрный фон, белый текст, фиолетовые края у всего, что можно нажать.
    /// Окно ничего не решает само: каждый переключатель и кнопка управляют соответствующим пунктом
    /// контекстного меню острова (оно осталось «движком» и больше не показывается).
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        // ---------- палитра: чёрный, фиолетовый, белый ----------
        private static Brush Mk(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        private static readonly Brush Black = Mk(0xFF0A0A0F);
        private static readonly Brush PanelBg = Mk(0xFF0D0D14);
        private static readonly Brush RowBg = Mk(0xFF12121B);
        private static readonly Brush Purple = Mk(0xFF8B5CF6);
        private static readonly Brush PurpleLight = Mk(0xFFA78BFA);
        private static readonly Brush PurpleDeep = Mk(0xFF6D28D9);
        private static readonly Brush PurpleEdge = Mk(0xFF3A2670);
        private static readonly Brush PurpleFill = Mk(0xFF1B1230);
        private static readonly Brush White = Mk(0xFFFFFFFF);
        private static readonly Brush Dim = Mk(0xFFA1A1AA);
        private static readonly Brush Scrim = Mk(0x99000000);

        private readonly Func<string, MenuItem> _find;
        private readonly Action<MenuItem> _raise;
        private readonly string _version;

        private readonly Grid _root = new Grid();
        private readonly Border _scrim;
        private readonly Border _panel;
        private readonly Grid _cubes;
        private readonly Grid _inner;
        private readonly TextBlock _title;
        private readonly Border _close;
        private readonly Border _exit;
        private readonly Border _card;
        private readonly ScrollViewer _scroll;
        private readonly Border[] _cat = new Border[6];

        private static readonly string[] CatIcons = { "\u25F7", "\u266A", "\u2726", "\u25A6", "\u2197", "\u2699" };
        private static readonly string[] CatNames = { "Часы", "Звук", "Удобство", "Сервисы", "Статистика", "Настройки" };

        private readonly double _pw, _ph;

        // статистика программ (даёт главное окно)
        public Func<List<TrackInfo>> TrackedApps;
        public Action<string, bool> SetTracked;
        public Action<string> RemoveTracked;
        public Action<string, string> AddTracked;
        public Func<List<string[]>> RunningApps;
        public Func<int> BreakMinutes;
        public Action<int> SetBreakMinutes;

        private readonly DispatcherTimer _statTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private readonly Dictionary<string, Action<TrackInfo>> _statLive = new Dictionary<string, Action<TrackInfo>>();
        private TextBlock _statTotal;
        private bool _addOpen;

        // микшер громкости по программам
        private sealed class MixRow
        {
            public Border Box, Mute;
            public MixerApp App;
            public Grid Slider;
            public Border Fill;
            public TranslateTransform ThumbTx;
            public TextBlock Name, Pct;
            public Ellipse Dot;
            public double Val;
            public bool Drag;

            public void Apply()
            {
                double aw = Slider.ActualWidth;
                if (aw <= 0) return;
                Fill.Width = Val * aw;
                ThumbTx.X = Val * Math.Max(0, aw - 14);
                Pct.Text = ((int)Math.Round(Val * 100)) + "%";
            }
        }

        private StackPanel _mixHost;
        private TextBlock _mixEmpty;
        private MixerScan _scan;
        private readonly Dictionary<string, MixRow> _rows = new Dictionary<string, MixRow>();
        private readonly DispatcherTimer _mixTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        private int _selected = -1;
        private bool _closing;

        public SettingsWindow(Func<string, MenuItem> find, Action<MenuItem> raise, string version)
        {
            _find = find;
            _raise = raise;
            _version = version;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            _pw = Math.Min(940, Width * 0.72);
            _ph = Math.Min(660, Height * 0.80);

            // затемнение за интерфейсом: клик по нему закрывает окно
            _scrim = new Border { Background = Scrim, Opacity = 0 };
            _scrim.MouseLeftButtonDown += (s, e) => CloseAnimated(null);
            _root.Children.Add(_scrim);

            var stage = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _root.Children.Add(stage);

            // сама панель: сначала 32 × 32, потом растёт
            _panel = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(14),
                Background = PanelBg,
                BorderBrush = Purple,
                BorderThickness = new Thickness(1.6),
                Opacity = 0,
                ClipToBounds = true
            };
            stage.Children.Add(_panel);

            // четыре кубика
            _cubes = new Grid { Width = 32, Height = 32, IsHitTestVisible = false };
            var cubeBrush = new LinearGradientBrush(Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x6D, 0x28, 0xD9), 45);
            for (int k = 0; k < 4; k++)
            {
                int dx = (k % 2 == 0) ? -1 : 1;
                int dy = k < 2 ? -1 : 1;
                _cubes.Children.Add(new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(4),
                    Background = cubeBrush,
                    HorizontalAlignment = dx < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    VerticalAlignment = dy < 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                    RenderTransform = new TranslateTransform(dx * 72, dy * 72),
                    Opacity = 0
                });
            }
            stage.Children.Add(_cubes);

            // содержимое панели (невидимо, пока панель растёт)
            _inner = new Grid
            {
                Width = _pw - 4,
                Height = _ph - 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0
            };
            _inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
            _inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _panel.Child = _inner;

            // шапка: DreamIslands по центру и крестик справа
            var header = new Grid { Margin = new Thickness(0, 16, 0, 12) };
            _title = T("DreamIslands", 22, White, FontWeights.SemiBold);
            _title.HorizontalAlignment = HorizontalAlignment.Center;
            _title.Opacity = 0;
            header.Children.Add(_title);

            var closeText = T("\u2715", 13, White, FontWeights.SemiBold);
            closeText.HorizontalAlignment = HorizontalAlignment.Center;
            closeText.VerticalAlignment = VerticalAlignment.Center;
            _close = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1.2),
                BorderBrush = PurpleEdge,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 18, 0),
                Child = closeText,
                Cursor = Cursors.Hand,
                Opacity = 0
            };
            Hover(_close, () => false);
            _close.MouseLeftButtonUp += (s, e) => CloseAnimated(null);
            header.Children.Add(_close);
            Grid.SetRow(header, 0);
            _inner.Children.Add(header);

            var line = new Border { Height = 1, Background = PurpleEdge, Margin = new Thickness(18, 0, 18, 0) };
            Grid.SetRow(line, 1);
            _inner.Children.Add(line);

            // тело: слева разделы, справа содержимое
            var body = new Grid { Margin = new Thickness(18, 14, 18, 18) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 2);
            _inner.Children.Add(body);

            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var catList = new StackPanel();
            for (int i = 0; i < _cat.Length; i++)
            {
                _cat[i] = MakeCategory(i);
                catList.Children.Add(_cat[i]);
            }
            Grid.SetRow(catList, 0);
            left.Children.Add(catList);

            var exitText = T("Выйти из DreamIsland", 13, White, FontWeights.SemiBold);
            exitText.HorizontalAlignment = HorizontalAlignment.Center;
            _exit = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.2),
                BorderBrush = PurpleEdge,
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 10, 10, 10),
                Child = exitText,
                Cursor = Cursors.Hand,
                Opacity = 0
            };
            Hover(_exit, () => false);
            _exit.MouseLeftButtonUp += (s, e) => Act("exit");
            Grid.SetRow(_exit, 1);
            left.Children.Add(_exit);
            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 10, 0)
            };
            TryApplyScrollStyle(_scroll);
            _card = new Border
            {
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1.2),
                BorderBrush = PurpleEdge,
                Background = Black,
                Padding = new Thickness(16, 14, 6, 10),
                Child = _scroll,
                Opacity = 0
            };
            Grid.SetColumn(_card, 2);
            body.Children.Add(_card);

            Content = _root;
            Select(0);

            _mixTimer.Tick += (s, e) => RefreshMixer();
            _statTimer.Tick += (s, e) => RefreshStats();
            Closed += (s, e) => { StopMixer(); StopStats(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) CloseAnimated(null); };
            Loaded += (s, e) => { Intro(); Activate(); Focus(); };
        }

        // ---------- анимация появления и ухода ----------

        private static DoubleAnimation Anim(double from, double to, int ms, int begin = 0, bool easeIn = false)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                BeginTime = TimeSpan.FromMilliseconds(begin),
                EasingFunction = new CubicEase { EasingMode = easeIn ? EasingMode.EaseIn : EasingMode.EaseOut }
            };
        }

        private void Intro()
        {
            _scrim.BeginAnimation(OpacityProperty, Anim(0, 1, 250));

            // 1. четыре кубика слетаются к центру и встают вплотную друг к другу
            foreach (Border cube in _cubes.Children)
            {
                var tt = (TranslateTransform)cube.RenderTransform;
                var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
                var ax = new DoubleAnimation(tt.X, 0, TimeSpan.FromMilliseconds(400)) { EasingFunction = ease };
                var ay = new DoubleAnimation(tt.Y, 0, TimeSpan.FromMilliseconds(400)) { EasingFunction = ease };
                tt.BeginAnimation(TranslateTransform.XProperty, ax);
                tt.BeginAnimation(TranslateTransform.YProperty, ay);
                cube.BeginAnimation(OpacityProperty, Anim(0, 1, 180));
            }

            // 2. кубики сливаются в одну плашку
            _panel.BeginAnimation(OpacityProperty, Anim(0, 1, 120, 420));
            _cubes.BeginAnimation(OpacityProperty, Anim(1, 0, 160, 450));

            // 3. плашка растёт вширь, затем вверх и вниз
            _panel.BeginAnimation(WidthProperty, Anim(32, _pw, 320, 480));
            var grow = Anim(32, _ph, 360, 800);
            grow.Completed += (s, e) => ShowContent();
            _panel.BeginAnimation(HeightProperty, grow);
        }

        // 4. появляются название и разделы сверху вниз, потом само содержимое
        private void ShowContent()
        {
            if (_closing) return;

            _inner.Opacity = 1;
            _title.BeginAnimation(OpacityProperty, Anim(0, 1, 260));
            _close.BeginAnimation(OpacityProperty, Anim(0, 1, 260));

            for (int i = 0; i < _cat.Length; i++)
            {
                int begin = 140 + i * 85;
                _cat[i].BeginAnimation(OpacityProperty, Anim(0, 1, 240, begin));
                var tt = (TranslateTransform)_cat[i].RenderTransform;
                tt.BeginAnimation(TranslateTransform.XProperty, Anim(-18, 0, 300, begin));
            }

            int end = 140 + _cat.Length * 85;
            _exit.BeginAnimation(OpacityProperty, Anim(0, 1, 240, end));
            _card.BeginAnimation(OpacityProperty, Anim(0, 1, 300, end + 60));
        }

        /// <summary>Плавно закрывает окно; after выполняется уже после закрытия (чтобы диалоги не оказались под ним).</summary>
        private void CloseAnimated(Action after)
        {
            if (_closing) return;
            _closing = true;

            var fade = Anim(1, 0, 170, 0, true);
            fade.Completed += (s, e) =>
            {
                try { Close(); } catch { }
                if (after != null) after();
            };
            _root.BeginAnimation(OpacityProperty, fade);
        }

        // ---------- разделы ----------

        private Border MakeCategory(int idx)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = T(CatIcons[idx], 17, PurpleLight, FontWeights.Normal);
            icon.VerticalAlignment = VerticalAlignment.Center;
            var name = T(CatNames[idx], 15, White, FontWeights.SemiBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            grid.Children.Add(icon);
            grid.Children.Add(name);

            var b = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.2),
                BorderBrush = PurpleEdge,
                Background = Brushes.Transparent,
                Padding = new Thickness(12, 11, 12, 11),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
                Cursor = Cursors.Hand,
                RenderTransform = new TranslateTransform(-18, 0),
                Opacity = 0
            };
            int me = idx;
            Hover(b, () => _selected == me);
            b.MouseLeftButtonUp += (s, e) => Select(me);
            return b;
        }

        private void Select(int idx)
        {
            if (idx == _selected) return;
            _selected = idx;
            StopMixer();
            StopStats();

            for (int i = 0; i < _cat.Length; i++)
            {
                bool sel = i == idx;
                _cat[i].BorderBrush = sel ? Purple : PurpleEdge;
                _cat[i].Background = sel ? PurpleFill : Brushes.Transparent;
            }

            UIElement page;
            switch (idx)
            {
                case 0: page = PageClock(); break;
                case 1: page = PageSound(); break;
                case 2: page = PageComfort(); break;
                case 3: page = PageServices(); break;
                case 4: page = PageStats(); break;
                default: page = PageSettings(); break;
            }

            _scroll.Content = page;
            _scroll.ScrollToTop();
            page.BeginAnimation(OpacityProperty, Anim(0, 1, 160));
        }

        private UIElement PageClock()
        {
            var p = new StackPanel();

            p.Children.Add(Section("Таймер"));
            p.Children.Add(Chips(
                Btn("5 минут", "5"), Btn("10 минут", "10"), Btn("15 минут", "15"),
                Btn("25 минут", "25"), Btn("45 минут", "45"), Btn("1 час", "60")));
            p.Children.Add(Section("Режимы"));
            p.Children.Add(Chips(Btn("Секундомер", "stopwatch"), Btn("Помодоро 25 / 5", "pomodoro")));
            p.Children.Add(Section("Управление"));
            p.Children.Add(Chips(Btn("Пауза / продолжить", "timerpause"), Btn("Остановить таймер", "timerstop")));
            return p;
        }

        private UIElement PageSound()
        {
            var p = new StackPanel();

            p.Children.Add(Section("Громкость программ"));
            var mixHint = T("Каждая программа отдельно, даже если весь звук идёт через Voicemeeter", 11.5, Dim, FontWeights.Normal);
            mixHint.Margin = new Thickness(4, 0, 0, 8);
            p.Children.Add(mixHint);
            _mixHost = new StackPanel();
            p.Children.Add(_mixHost);
            StartMixer();

            p.Children.Add(Section("Микрофон"));
            p.Children.Add(Toggle("Включать микрофон по звуку", "micwatch", "Слежение по звуку, для кнопок mute на самом устройстве"));
            p.Children.Add(Toggle("Отладка микрофона", "micdebug", "Показывает уровень микрофона прямо на острове"));
            return p;
        }

        private UIElement PageComfort()
        {
            var p = new StackPanel();
            p.Children.Add(Section("Живой эквалайзер"));
            p.Children.Add(Toggle("Эквалайзер под звук", "liveeq"));

            p.Children.Add(Section("Эмоции"));
            p.Children.Add(Toggle("Эмоции для видео", "emo"));
            p.Children.Add(Toggle("Цвет и вспышки по картинке видео", "screen"));
            p.Children.Add(Toggle("Колонки в чёрных полосах по бокам", "spk"));
            p.Children.Add(Toggle("Колонки поверх картинки, если нет полос", "spkfloat"));
            p.Children.Add(Segmented("Стиль колонок", new[] { "spkstyle0", "spkstyle1", "spkstyle2" }, new[] { "Неон", "Ретро", "Минимализм" }));
            p.Children.Add(Segmented("Чувствительность", new[] { "sens0", "sens1", "sens2" }, new[] { "Тихо", "Обычно", "Жёстко" }));
            p.Children.Add(Segmented("Анимация острова", new[] { "emo0", "emo1", "emo2", "emo3" }, new[] { "Авто", "Спектр", "Пульс", "Вспышка" }));
            p.Children.Add(Toggle("Этот плеер показывает видео", "vidapp", "Доступно, когда открыт плеер с видео"));
            p.Children.Add(Toggle("Отладочная панель с числами", "emodebug", "Клик по панели копирует числа в буфер"));

            p.Children.Add(Section("Плеер"));
            p.Children.Add(Toggle("Shift + колёсико: громкость SoundCloud", "scvol"));
            return p;
        }

        private UIElement PageServices()
        {
            var p = new StackPanel();
            p.Children.Add(Section("TeamSpeak 3"));
            p.Children.Add(Toggle("Следить за TeamSpeak 3", "ts3"));
            p.Children.Add(Chips(Btn("API-ключ…", "ts3key"), Btn("Название сервера…", "ts3name"), Btn("Проверить подключение", "ts3check")));

            p.Children.Add(Section("Steam / Epic"));
            p.Children.Add(Toggle("Показывать загрузки", "dl"));
            p.Children.Add(Chips(Btn("Показать загрузки сейчас", "dlnow")));

            p.Children.Add(Section("Проводник"));
            p.Children.Add(Toggle("Плашка файлов в проводнике", "files"));

            p.Children.Add(Section("Буфер обмена"));
            p.Children.Add(Toggle("История буфера обмена", "clip"));
            p.Children.Add(Chips(Btn("Очистить историю", "clipclear")));

            p.Children.Add(Section("Скриншоты"));
            p.Children.Add(Toggle("Плашка скриншотов", "shots"));

            p.Children.Add(Section("OBS"));
            p.Children.Add(Toggle("Следить за OBS", "obs"));
            p.Children.Add(Chips(Btn("Пароль…", "obspass")));
            return p;
        }

        private UIElement PageSettings()
        {
            var p = new StackPanel();
            p.Children.Add(Section("Основное"));
            p.Children.Add(Toggle("Показывать подключение устройств", "devices"));
            p.Children.Add(Toggle("Запускать вместе с Windows", "autostart"));
            p.Children.Add(Toggle("Точка записи (микрофон / камера)", "priv"));
            p.Children.Add(Toggle("Экономия видеопамяти", "lowgpu", "Вступает в силу после перезапуска"));

            p.Children.Add(Section("Обновления"));
            p.Children.Add(Toggle("Автоматически проверять обновления", "autoupdate"));
            p.Children.Add(Chips(Btn("Проверить обновления", "updcheck")));

            p.Children.Add(Section("Версия"));
            var ver = T("DreamIsland " + _version, 14, White, FontWeights.Normal);
            p.Children.Add(RowBox(ver));
            return p;
        }

        // ---------- элементы интерфейса ----------

        private static TextBlock T(string text, double size, Brush fg, FontWeight weight)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        }

        private static TextBlock Section(string text)
        {
            var t = T(text, 12.5, PurpleLight, FontWeights.SemiBold);
            t.Margin = new Thickness(4, 10, 0, 8);
            return t;
        }

        /// <summary>Подсветка краёв при наведении; keepBright — когда край должен остаться ярким (выбранный элемент).</summary>
        private static void Hover(Border b, Func<bool> keepBright)
        {
            b.MouseEnter += (s, e) => { b.BorderBrush = Purple; };
            b.MouseLeave += (s, e) => { b.BorderBrush = keepBright() ? Purple : PurpleEdge; };
        }

        private static Border RowBox(UIElement child)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.1),
                BorderBrush = PurpleEdge,
                Background = RowBg,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = child
            };
            return b;
        }

        private Border Chip(string text, Action onClick, bool selected)
        {
            var tb = T(text, 13, White, FontWeights.SemiBold);
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Center;

            var b = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.2),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 8),
                Child = tb,
                Cursor = Cursors.Hand,
                Tag = selected
            };
            PaintChip(b);
            b.MouseEnter += (s, e) => { b.BorderBrush = Purple; };
            b.MouseLeave += (s, e) => PaintChip(b);
            if (onClick != null) b.MouseLeftButtonUp += (s, e) => onClick();
            return b;
        }

        private static void PaintChip(Border b)
        {
            bool sel = b.Tag is bool && (bool)b.Tag;
            b.BorderBrush = sel ? Purple : PurpleEdge;
            b.Background = sel ? PurpleDeep : PurpleFill;
        }

        private static WrapPanel Chips(params UIElement[] items)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var it in items) w.Children.Add(it);
            return w;
        }

        /// <summary>Кнопка действия: закрывает окно и нажимает соответствующий пункт меню острова.</summary>
        private Border Btn(string text, string tag)
        {
            return Chip(text, () => Act(tag), false);
        }

        private void Act(string tag)
        {
            if (tag == "exit")
            {
                CloseAnimated(() => ExitRequested?.Invoke());
                return;
            }

            var mi = _find(tag);
            if (mi == null) return;
            CloseAnimated(() => _raise(mi));
        }

        /// <summary>Нужно выйти из приложения (пункт «Выход» меню острова).</summary>
        public Action ExitRequested;

        private UIElement Toggle(string label, string tag, string hint = null)
        {
            var mi = _find(tag);
            bool enabled = mi != null && mi.IsEnabled;
            bool on = mi != null && mi.IsChecked;

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(T(label, 14, enabled ? White : Dim, FontWeights.Normal));
            if (hint != null)
            {
                var h = T(hint, 11.5, Dim, FontWeights.Normal);
                h.Margin = new Thickness(0, 2, 0, 0);
                text.Children.Add(h);
            }

            var move = new TranslateTransform(on ? 20 : 0, 0);
            var knob = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = White,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(3, 0, 0, 0),
                RenderTransform = move
            };
            var track = new Border
            {
                Width = 44,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1.2),
                BorderBrush = on ? Purple : PurpleEdge,
                Background = on ? PurpleDeep : Black,
                Child = knob,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            Grid.SetColumn(track, 1);
            grid.Children.Add(track);

            var row = RowBox(grid);
            if (!enabled) row.Opacity = 0.5;
            else
            {
                row.Cursor = Cursors.Hand;
                row.MouseEnter += (s, e) => { row.BorderBrush = Purple; };
                row.MouseLeave += (s, e) => { row.BorderBrush = PurpleEdge; };
                row.MouseLeftButtonUp += (s, e) =>
                {
                    if (!mi.IsEnabled) return;
                    mi.IsChecked = !mi.IsChecked;
                    _raise(mi);

                    bool v = mi.IsChecked;   // обработчик мог вернуть значение обратно (например, при ошибке автозапуска)
                    track.BorderBrush = v ? Purple : PurpleEdge;
                    track.Background = v ? PurpleDeep : Black;
                    move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(v ? 20 : 0, TimeSpan.FromMilliseconds(130)));
                };
            }
            return row;
        }

        /// <summary>Выбор одного варианта из нескольких; варианты — это пункты-«радио» меню острова.</summary>
        private UIElement Segmented(string label, string[] tags, string[] names)
        {
            var chips = new List<Border>();
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

            for (int i = 0; i < tags.Length; i++)
            {
                int me = i;
                var item = _find(tags[i]);
                bool sel = item != null && item.IsChecked;
                var chip = Chip(names[i], () =>
                {
                    for (int j = 0; j < tags.Length; j++)
                    {
                        var m = _find(tags[j]);
                        if (m != null) m.IsChecked = j == me;
                        chips[j].Tag = j == me;
                        PaintChip(chips[j]);
                    }
                    var picked = _find(tags[me]);
                    if (picked != null) _raise(picked);
                }, sel);
                chips.Add(chip);
                wrap.Children.Add(chip);
            }

            var box = new StackPanel();
            box.Children.Add(T(label, 14, White, FontWeights.Normal));
            box.Children.Add(wrap);
            return RowBox(box);
        }

        /// <summary>Выбор одного значения из нескольких (не связан с меню): значение читается и записывается через делегаты.</summary>
        private UIElement Choice(string label, string[] names, int[] values, Func<int> get, Action<int> set)
        {
            var chips = new List<Border>();
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            int cur = get();

            for (int i = 0; i < names.Length; i++)
            {
                int me = i;
                var chip = Chip(names[i], () =>
                {
                    set(values[me]);
                    for (int j = 0; j < chips.Count; j++)
                    {
                        chips[j].Tag = j == me;
                        PaintChip(chips[j]);
                    }
                }, values[i] == cur);
                chips.Add(chip);
                wrap.Children.Add(chip);
            }

            var box = new StackPanel();
            box.Children.Add(T(label, 14, White, FontWeights.Normal));
            box.Children.Add(wrap);
            return RowBox(box);
        }

        // ---------- микшер громкости ----------

        private void StartMixer()
        {
            _mixEmpty = T("Сейчас нет программ со звуком", 13, Dim, FontWeights.Normal);
            _mixEmpty.Margin = new Thickness(4, 0, 0, 8);
            _mixHost.Children.Add(_mixEmpty);
            RefreshMixer();
            _mixTimer.Start();
        }

        private void StopMixer()
        {
            _mixTimer.Stop();
            _rows.Clear();
            _mixHost = null;
            _mixEmpty = null;
            if (_scan != null)
            {
                _scan.Dispose();
                _scan = null;
            }
        }

        private void RefreshMixer()
        {
            if (_mixHost == null || _closing) return;
            foreach (var r in _rows.Values)
                if (r.Drag) return;   // не мешаем, пока тянут ползунок

            var scan = MixerScan.Run();
            var old = _scan;
            _scan = scan;

            var seen = new HashSet<string>();
            foreach (var app in scan.Apps)
            {
                seen.Add(app.Key);

                MixRow row;
                if (!_rows.TryGetValue(app.Key, out row))
                {
                    row = BuildMixRow();
                    _rows[app.Key] = row;
                    _mixHost.Children.Add(row.Box);
                }

                row.App = app;
                row.Name.Text = app.Name;
                row.Name.Opacity = app.Active ? 1 : 0.6;
                row.Dot.Fill = app.Active ? Purple : PurpleEdge;
                row.Mute.Tag = app.Muted;
                PaintChip(row.Mute);
                row.Val = app.Volume;
                row.Apply();
            }

            // программы, которых больше нет
            var gone = new List<string>();
            foreach (var kv in _rows)
                if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var key in gone)
            {
                _mixHost.Children.Remove(_rows[key].Box);
                _rows.Remove(key);
            }

            _mixEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (old != null) old.Dispose();   // только после того, как строки перешли на новые сессии
        }

        private MixRow BuildMixRow()
        {
            var row = new MixRow();

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // имя и точка «сейчас играет»
            var nameBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Dot = new Ellipse { Width = 8, Height = 8, Fill = PurpleEdge, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Name = new TextBlock
            {
                FontSize = 14,
                Foreground = White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 146,
                VerticalAlignment = VerticalAlignment.Center
            };
            nameBox.Children.Add(row.Dot);
            nameBox.Children.Add(row.Name);
            grid.Children.Add(nameBox);

            // ползунок громкости
            row.Slider = new Grid { Height = 26, Background = Brushes.Transparent, Margin = new Thickness(6, 0, 10, 0), Cursor = Cursors.Hand };
            row.Slider.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = PurpleEdge, VerticalAlignment = VerticalAlignment.Center });
            row.Fill = new Border
            {
                Height = 6,
                Width = 0,
                CornerRadius = new CornerRadius(3),
                Background = Purple,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Slider.Children.Add(row.Fill);
            row.ThumbTx = new TranslateTransform();
            row.Slider.Children.Add(new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = White,
                Stroke = Purple,
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = row.ThumbTx
            });
            Grid.SetColumn(row.Slider, 1);
            grid.Children.Add(row.Slider);

            row.Pct = T("0%", 13, Dim, FontWeights.Normal);
            row.Pct.TextAlignment = TextAlignment.Right;
            row.Pct.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(row.Pct, 2);
            grid.Children.Add(row.Pct);

            row.Mute = Chip("Без звука", () =>
            {
                if (row.App == null) return;
                bool m = !row.App.Muted;
                row.App.SetMuted(m);
                row.Mute.Tag = m;
                PaintChip(row.Mute);
            }, false);
            row.Mute.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(row.Mute, 3);
            grid.Children.Add(row.Mute);

            var slider = row.Slider;
            slider.SizeChanged += (s, e) => row.Apply();
            slider.MouseLeftButtonDown += (s, e) =>
            {
                row.Drag = true;
                slider.CaptureMouse();
                DragMix(row, e.GetPosition(slider).X);
                e.Handled = true;
            };
            slider.MouseMove += (s, e) =>
            {
                if (row.Drag) DragMix(row, e.GetPosition(slider).X);
            };
            slider.MouseLeftButtonUp += (s, e) =>
            {
                if (!row.Drag) return;
                row.Drag = false;
                slider.ReleaseMouseCapture();
            };

            row.Box = RowBox(grid);
            row.Box.Padding = new Thickness(14, 8, 10, 8);
            return row;
        }

        private static void DragMix(MixRow row, double x)
        {
            double aw = row.Slider.ActualWidth;
            if (aw <= 0) return;

            double v = Math.Max(0, Math.Min(1, x / aw));
            row.Val = v;
            row.Apply();

            if (row.App == null) return;
            if (v > 0 && row.App.Muted)
            {
                row.App.SetMuted(false);   // потянули ползунок — звук возвращается
                row.Mute.Tag = false;
                PaintChip(row.Mute);
            }
            row.App.SetVolume((float)v);
        }

        // ---------- вкладка «Статистика» ----------

        private static string Span(double seconds)
        {
            int m = (int)(seconds / 60);
            if (m < 60) return m + " мин";
            return (m / 60) + " ч " + (m % 60).ToString("00") + " мин";
        }

        private UIElement PageStats()
        {
            var p = new StackPanel();
            if (TrackedApps == null)
            {
                p.Children.Add(T("Статистика недоступна", 14, Dim, FontWeights.Normal));
                return p;
            }

            _statLive.Clear();

            p.Children.Add(Section("Что отслеживать"));
            var hint = T("Остров замечает запуск включённых программ и считает время. В сами программы он не вмешивается.", 11.5, Dim, FontWeights.Normal);
            hint.Margin = new Thickness(4, 0, 0, 8);
            p.Children.Add(hint);

            foreach (var info in TrackedApps())
                p.Children.Add(BuildTrackRow(info));

            p.Children.Add(Section("Добавить программу"));
            var addHost = new StackPanel();
            p.Children.Add(addHost);
            BuildAddPanel(addHost);

            p.Children.Add(Section("Итого"));
            _statTotal = T("", 14, White, FontWeights.Normal);
            p.Children.Add(RowBox(_statTotal));

            if (BreakMinutes != null && SetBreakMinutes != null)
            {
                p.Children.Add(Section("Перерыв"));
                p.Children.Add(Choice("Напоминать, если одна программа работает дольше",
                    new[] { "Выкл", "30 мин", "1 час", "1,5 часа", "2 часа" },
                    new[] { 0, 30, 60, 90, 120 }, BreakMinutes, SetBreakMinutes));
            }

            RefreshStats();
            _statTimer.Start();
            return p;
        }

        private UIElement BuildTrackRow(TrackInfo info)
        {
            string id = info.Id;

            var name = T(info.Name, 14, White, FontWeights.Normal);
            var sub = T("", 12, Dim, FontWeights.Normal);
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(name);
            left.Children.Add(sub);

            var sw = Switch(info.Enabled, on => { if (SetTracked != null) SetTracked(id, on); });
            sw.VerticalAlignment = VerticalAlignment.Center;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);
            Grid.SetColumn(sw, 1);
            grid.Children.Add(sw);

            if (!info.Builtin)
            {
                var x = Chip("\u2715", () =>
                {
                    if (RemoveTracked != null) RemoveTracked(id);
                    Rebuild();
                }, false);
                x.Margin = new Thickness(10, 0, 0, 0);
                x.Padding = new Thickness(9, 5, 9, 5);
                x.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(x, 2);
                grid.Children.Add(x);
            }

            Action<TrackInfo> update = i =>
            {
                string line = (i.Running ? "сейчас запущено, " + Span(i.Session) + "  ·  " : "") +
                              "сегодня " + Span(i.Today) + "  ·  7 дней " + Span(i.Week);
                sub.Text = line;
                name.Opacity = i.Enabled ? 1 : 0.6;
            };
            update(info);
            _statLive[id] = update;

            return RowBox(grid);
        }

        private void BuildAddPanel(StackPanel host)
        {
            host.Children.Clear();

            host.Children.Add(Chips(Chip(_addOpen ? "Скрыть список" : "+ Выбрать из запущенных…", () =>
            {
                _addOpen = !_addOpen;
                BuildAddPanel(host);
            }, _addOpen)));
            if (!_addOpen) return;

            var apps = RunningApps != null ? RunningApps() : new List<string[]>();
            if (apps.Count == 0)
            {
                host.Children.Add(T("Запущенных программ с окнами не найдено. Запустите нужную и откройте список снова.", 13, Dim, FontWeights.Normal));
                return;
            }

            var wrap = new WrapPanel();
            foreach (var a in apps)
            {
                string proc = a[0], title = a[1];
                wrap.Children.Add(Chip(title, () =>
                {
                    if (AddTracked != null) AddTracked(proc, title);
                    _addOpen = false;
                    Rebuild();
                }, false));
            }
            host.Children.Add(wrap);
        }

        /// <summary>Обновляет числа на странице без перестройки (раз в несколько секунд).</summary>
        private void RefreshStats()
        {
            if (TrackedApps == null || _closing) return;

            var list = TrackedApps();
            double today = 0, week = 0;
            foreach (var i in list)
            {
                today += i.Today;
                week += i.Week;

                Action<TrackInfo> update;
                if (_statLive.TryGetValue(i.Id, out update)) update(i);
            }

            if (_statTotal != null)
                _statTotal.Text = "Сегодня: " + Span(today) + "   ·   За 7 дней: " + Span(week);
        }

        private void StopStats()
        {
            _statTimer.Stop();
            _statLive.Clear();
            _statTotal = null;
            _addOpen = false;
        }

        /// <summary>Перестраивает текущую вкладку (после добавления или удаления программы).</summary>
        private void Rebuild()
        {
            int cur = _selected;
            _selected = -1;
            Select(cur);
        }

        /// <summary>Маленький переключатель сам по себе (без пункта меню): клик меняет значение и сообщает об этом.</summary>
        private Border Switch(bool on, Action<bool> changed)
        {
            var move = new TranslateTransform(on ? 20 : 0, 0);
            var knob = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = White,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(3, 0, 0, 0),
                RenderTransform = move
            };
            var track = new Border
            {
                Width = 44,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1.2),
                BorderBrush = on ? Purple : PurpleEdge,
                Background = on ? PurpleDeep : Black,
                Child = knob,
                Margin = new Thickness(12, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            track.MouseLeftButtonUp += (s, e) =>
            {
                on = !on;
                track.BorderBrush = on ? Purple : PurpleEdge;
                track.Background = on ? PurpleDeep : Black;
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 20 : 0, TimeSpan.FromMilliseconds(130)));
                changed(on);
                e.Handled = true;
            };
            return track;
        }

        // тонкая фиолетовая полоса прокрутки вместо стандартной серой
        private static void TryApplyScrollStyle(ScrollViewer sv)
        {
            const string xaml =
                "<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "<Style TargetType='ScrollBar'>" +
                "<Setter Property='Width' Value='8'/>" +
                "<Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ScrollBar'><Grid Background='Transparent'>" +
                "<Track x:Name='PART_Track' IsDirectionReversed='True'>" +
                "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton>" +
                "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton>" +
                "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='#6D28D9' CornerRadius='4' Margin='1'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                "</Track></Grid></ControlTemplate>" +
                "</Setter.Value></Setter></Style></ResourceDictionary>";
            try
            {
                sv.Resources = (ResourceDictionary)XamlReader.Parse(xaml);
            }
            catch
            {
                // не вышло — останется стандартная полоса прокрутки
            }
        }
    }
}
