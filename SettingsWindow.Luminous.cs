using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DreamIsland
{
    /// <summary>
    /// Luminous UI для окна настроек: глубина (фон → панель → карточки → кнопки), свет (мягкое свечение
    /// у главного элемента, блик по кромке), стекло (полупрозрачные поверхности с видимой кромкой).
    /// WPF не умеет размывать то, что лежит ЗА окном, поэтому стекло собрано из градиентов и подсветки,
    /// а атмосфера — из мягких радиальных «пятен» света за содержимым.
    /// </summary>
    public sealed partial class SettingsWindow
    {
        // ---------- токены ----------

        private static Color Cl(uint argb)
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        private static GradientStop Gs(uint argb, double offset)
        {
            return new GradientStop(Cl(argb), offset);
        }

        private static Brush Lin(double x1, double y1, double x2, double y2, params GradientStop[] stops)
        {
            var b = new LinearGradientBrush(new GradientStopCollection(stops), new Point(x1, y1), new Point(x2, y2));
            b.Freeze();
            return b;
        }

        private static DropShadowEffect Glow(Color c, double blur, double opacity)
        {
            var e = new DropShadowEffect { Color = c, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };
            e.Freeze();
            return e;
        }

        // «Экономия видеопамяти» из меню острова отключает свечения и дрейф фона
        private static readonly bool Fancy = !AppSettings.GetBool("lowgpu");

        // стекло: блик сверху, кромка со светом из верхнего левого угла
        private static readonly Brush GlassFill = Lin(0, 0, 0, 1, Gs(0x1CFFFFFF, 0), Gs(0x09FFFFFF, 1));
        private static readonly Brush GlassFaint = Lin(0, 0, 0, 1, Gs(0x0DFFFFFF, 0), Gs(0x04FFFFFF, 1));
        private static readonly Brush GlassEdge = Lin(0, 0, 1, 1, Gs(0x42FFFFFF, 0), Gs(0x1CA78BFA, 0.5), Gs(0x14FFFFFF, 1));
        private static readonly Brush GlassEdgeHot = Lin(0, 0, 1, 1, Gs(0xE6C4B5FD, 0), Gs(0xFF8B5CF6, 0.55), Gs(0x99A78BFA, 1));
        private static readonly Brush InsetFill = Lin(0, 0, 0, 1, Gs(0x66000000, 0), Gs(0x33000000, 1));

        private static readonly Brush ChipSelFill = Lin(0, 0, 1, 1, Gs(0xFF8B5CF6, 0), Gs(0xFF5B21B6, 1));
        private static readonly Brush CatSelFill = Lin(0, 0, 1, 1, Gs(0x628B5CF6, 0), Gs(0x1C6D28D9, 1));
        private static readonly Brush ToggleOn = Lin(0, 0, 1, 1, Gs(0xFF8B5CF6, 0), Gs(0xFF6D28D9, 1));
        private static readonly Brush CardFill = Lin(0, 0, 0, 1, Gs(0xB8050508, 0), Gs(0xD00A0A10, 1));

        private static readonly Brush PanelBase = Lin(0, 0, 0.45, 1, Gs(0xFF120C24, 0), Gs(0xFF0A0A13, 0.55), Gs(0xFF06060B, 1));
        private static readonly Brush PanelRim = Lin(0, 0, 1, 1, Gs(0xB8FFFFFF, 0), Gs(0xCC8B5CF6, 0.35), Gs(0x663B82F6, 0.7), Gs(0x338B5CF6, 1));

        // тёплый акцент для всего, что про время: свечение вокруг главного элемента (время будильника)
        private static readonly Color AmberC = Cl(0xFFFF9F0A);
        private static readonly Brush AmberSoft = Mk(0xFFFFC266);

        private static readonly Effect GlowChip = Fancy ? Glow(Cl(0xFF8B5CF6), 14, 0.55) : null;
        private static readonly Effect GlowCat = Fancy ? Glow(Cl(0xFF8B5CF6), 18, 0.42) : null;
        private static readonly Effect GlowAmber = Fancy ? Glow(AmberC, 18, 0.45) : null;

        private static readonly CultureInfo RuCulture = new CultureInfo("ru-RU");

        private Border _halo;

        /// <summary>Стекло острова: включено ли (даёт главное окно) и переключатель.</summary>
        public Func<bool> IslandGlass;
        public Action<bool> SetIslandGlass;

        private UIElement GlassRow()
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(T("Стеклянный остров", 14, White, FontWeights.Normal));
            var h = T("Сквозь остров виден размытый экран за ним. Выключите, если важна каждая капля производительности", 11.5, Dim, FontWeights.Normal);
            h.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(h);

            var sw = Switch(IslandGlass != null && IslandGlass(), on => { if (SetIslandGlass != null) SetIslandGlass(on); });
            sw.VerticalAlignment = VerticalAlignment.Center;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            Grid.SetColumn(sw, 1);
            grid.Children.Add(sw);
            return RowBox(grid);
        }

        // ---------- мелкие помощники ----------

        private static void PaintTrack(Border track, bool on)
        {
            track.BorderBrush = on ? PurpleLight : GlassEdge;
            track.Background = on ? ToggleOn : Black;
            track.Effect = on ? GlowChip : null;
        }

        private static TextBlock Hint(string text)
        {
            var h = T(text, 11.5, Dim, FontWeights.Normal);
            h.Margin = new Thickness(4, 0, 0, 8);
            return h;
        }

        /// <summary>Однострочное поле ввода в стиле стекла; фокус подсвечивает кромку.</summary>
        private static Border TextField(string init, int maxLength, Action<string> changed)
        {
            var tb = new TextBox
            {
                Text = init ?? "",
                MaxLength = maxLength,
                FontSize = 14,
                Foreground = White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                CaretBrush = White,
                SelectionBrush = Purple,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var box = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.2),
                BorderBrush = GlassEdge,
                Background = InsetFill,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 6, 0, 0),
                Child = tb
            };

            tb.GotKeyboardFocus += (s, e) => { box.BorderBrush = GlassEdgeHot; };
            tb.LostKeyboardFocus += (s, e) => { box.BorderBrush = GlassEdge; };
            tb.TextChanged += (s, e) => { if (changed != null) changed(tb.Text); };
            return box;
        }

        // ---------- глубина: гало, атмосфера, блик ----------

        private bool _glass;   // есть снимок экрана: панель полупрозрачная и сквозь неё видно размытый рабочий стол

        private static readonly Brush PanelGlass = Lin(0, 0, 0.45, 1, Gs(0xD2150E2A, 0), Gs(0xCC0B0B15, 0.55), Gs(0xC8060610, 1));

        /// <summary>Затемнённый размытый рабочий стол на весь экран; клик по нему закрывает окно.</summary>
        private Border BuildScrim(System.Windows.Media.Imaging.BitmapSource shot)
        {
            var scrim = new Border { Opacity = 0, Background = Scrim };
            if (shot == null) return scrim;

            _glass = true;
            var img = new Image
            {
                Source = shot,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = 14, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
                CacheMode = new BitmapCache()
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var g = new Grid();
            g.Children.Add(img);
            g.Children.Add(new Border { Background = Mk(0x73000000) });   // затемнение: контраст текста важнее красоты
            scrim.Background = null;
            scrim.Child = g;
            return scrim;
        }

        /// <summary>Свечение вокруг панели: рисуется только СНАРУЖИ, иначе чёрная подложка закрыла бы стекло.</summary>
        private UIElement BuildHalo(FrameworkElement panel)
        {
            var host = new Grid { IsHitTestVisible = false };
            _halo = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0
            };
            _halo.SetBinding(WidthProperty, new Binding("ActualWidth") { Source = panel });
            _halo.SetBinding(HeightProperty, new Binding("ActualHeight") { Source = panel });
            if (Fancy)
            {
                _halo.Effect = Glow(Cl(0xFF7C3AED), 70, 0.5);
                _halo.CacheMode = new BitmapCache();
            }
            host.Children.Add(_halo);

            Action cut = () =>
            {
                double w = host.ActualWidth, h = host.ActualHeight, pw = panel.ActualWidth, ph = panel.ActualHeight;
                if (w <= 0 || h <= 0) return;
                var hole = new RectangleGeometry(new Rect((w - pw) / 2, (h - ph) / 2, pw, ph), 14, 14);
                host.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, w, h)), hole);
            };
            host.SizeChanged += (s, e) => cut();
            panel.SizeChanged += (s, e) => cut();
            return host;
        }

        /// <summary>Фон панели: пятна света разных оттенков (фиолетовый, синий, чуть янтарного) и блик по верхней кромке.</summary>
        private static UIElement BuildAtmosphere()
        {
            var g = new Grid { IsHitTestVisible = false };

            g.Children.Add(Orb(0xFF8B5CF6, 0x46, 560, HorizontalAlignment.Left, VerticalAlignment.Top, -170, -200, 26, 20, 22));
            g.Children.Add(Orb(0xFF3B82F6, 0x2E, 520, HorizontalAlignment.Right, VerticalAlignment.Bottom, 150, 170, -24, -22, 28));
            g.Children.Add(Orb(0xFFFF9F0A, 0x12, 380, HorizontalAlignment.Right, VerticalAlignment.Top, 120, -130, -16, 18, 34));

            // блик по верхней кромке: свет падает сверху
            g.Children.Add(new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(26, 0, 26, 0),
                IsHitTestVisible = false,
                Background = Lin(0, 0, 1, 0, Gs(0x00FFFFFF, 0), Gs(0x70FFFFFF, 0.5), Gs(0x00FFFFFF, 1))
            });
            return g;
        }

        private static UIElement Orb(uint rgb, byte alpha, double size, HorizontalAlignment ha, VerticalAlignment va,
                                     double ox, double oy, double driftX, double driftY, double seconds)
        {
            byte r = (byte)(rgb >> 16), gr = (byte)(rgb >> 8), b = (byte)rgb;

            var brush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, r, gr, b), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.35), r, gr, b), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, r, gr, b), 1));
            brush.Freeze();

            var move = new TranslateTransform(ox, oy);
            var orb = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = brush,
                HorizontalAlignment = ha,
                VerticalAlignment = va,
                IsHitTestVisible = false,
                RenderTransform = move
            };

            // медленный дрейф; при отключённых анимациях Windows и в режиме экономии фон стоит на месте
            if (Fancy && SystemParameters.ClientAreaAnimation)
            {
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(ox, ox + driftX, TimeSpan.FromSeconds(seconds))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(oy, oy + driftY, TimeSpan.FromSeconds(seconds * 1.2))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            }
            return orb;
        }

        /// <summary>Скругляет содержимое по форме панели (ClipToBounds режет только по прямоугольнику).</summary>
        private static void RoundClip(FrameworkElement el, double radius)
        {
            el.SizeChanged += (s, e) =>
            {
                el.Clip = new RectangleGeometry(new Rect(0, 0, el.ActualWidth, el.ActualHeight), radius, radius);
            };
        }
    }
}
