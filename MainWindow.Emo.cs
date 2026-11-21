using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DreamIsland
{
    /// <summary>
    /// Остров «Эмоции»: включается только когда идёт видео (особенно .MOV).
    /// Слушает звук и «чувствует» музыку: столбики спектра, цвет по тембру, свечение на бочку,
    /// вспышка на малый барабан.
    /// </summary>
    public partial class MainWindow
    {
        private bool _emoEnabled = true, _emoRunning, _isVideo;
        private int _emoMode;   // 0 — авто, 1 — спектр, 2 — пульс, 3 — вспышка

        private readonly DispatcherTimer _emoTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly SolidColorBrush _emoBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        private readonly SolidColorBrush _islandBrush = new SolidColorBrush(Colors.Black);
        private Color _curAccent = Color.FromRgb(0x30, 0xD1, 0x58);

        private Rectangle[] _emoBars;
        private ScaleTransform[] _emoScale;

        private readonly float[] _spec = new float[AudioLevelMonitor.Bands];
        private float _specPeak = 0.05f, _kickEnv, _energySm, _centroidSm = 4f;
        private int _lastKick, _lastSnare;
        private double _hue = 20, _lastGlowHue = -100;

        // «пружина» острова на ритм и вспышки
        private readonly ScaleTransform _bounceI = new ScaleTransform(1, 1);
        private readonly ScaleTransform _bounceG = new ScaleTransform(1, 1);
        private readonly float[] _prevSpec = new float[AudioLevelMonitor.Bands];
        private float _fluxAvg = 0.2f, _flash;
        private long _lastOnset;

        private bool EmoActive { get { return _emoEnabled && _isVideo && _playing; } }

        private void InitEmo()
        {
            _emoEnabled = !AppSettings.GetBool("noemo");
            SetMenu("emo", _emoEnabled);
            SetEmoMode(AppSettings.GetInt("emo_mode", 0), false);

            BuildEmo();
            Island.Background = _islandBrush;      // чтобы фон острова можно было «вспыхивать»
            Island.RenderTransformOrigin = new Point(0.5, 0);
            Island.RenderTransform = _bounceI;
            GlowBack.RenderTransformOrigin = new Point(0.5, 0);
            GlowBack.RenderTransform = _bounceG;
            InitVideo();
            _emoTick.Tick += (s, e) => EmoTick();
        }

        private void BuildEmo()
        {
            int n = AudioLevelMonitor.Bands;
            _emoBars = new Rectangle[n];
            _emoScale = new ScaleTransform[n];

            for (int i = 0; i < n; i++)
            {
                var sc = new ScaleTransform(1, 0.2);
                var bar = new Rectangle
                {
                    Width = 10,
                    Height = 38,
                    RadiusX = 4,
                    RadiusY = 4,
                    Margin = new Thickness(2, 0, 2, 0),
                    Fill = _emoBrush,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = sc
                };
                EmoBars.Children.Add(bar);
                _emoBars[i] = bar;
                _emoScale[i] = sc;
            }
        }

        // ---------- выбор анимации ----------

        private static MenuItem FindIn(ItemCollection items, string tag)
        {
            foreach (var it in items)
            {
                var mi = it as MenuItem;
                if (mi == null) continue;
                if (tag.Equals(mi.Tag as string)) return mi;

                var inner = FindIn(mi.Items, tag);
                if (inner != null) return inner;
            }
            return null;
        }

        private MenuItem FindMenuDeep(string tag)
        {
            return FindIn(Island.ContextMenu.Items, tag);
        }

        private void SetEmoMode(int mode, bool save)
        {
            if (mode < 0 || mode > 3) mode = 0;
            _emoMode = mode;

            for (int i = 0; i < 4; i++)
            {
                var mi = FindMenuDeep("emo" + i);
                if (mi != null) mi.IsChecked = i == mode;
            }

            if (save) AppSettings.SetString("emo_mode", mode.ToString());
        }

        private void EmoMode_Click(object sender, RoutedEventArgs e)
        {
            var mi = (MenuItem)sender;
            string tag = mi.Tag as string;
            int mode;
            if (tag != null && tag.Length > 3 && int.TryParse(tag.Substring(3), out mode))
                SetEmoMode(mode, true);
        }

        // ---------- анимация ----------

        private void EmoTick()
        {
            _levels.GetSpectrum(_spec);
            float rms = _levels.Rms;

            int kicks = _levels.KickCount, snares = _levels.SnareCount;
            bool kick = kicks != _lastKick, snare = snares != _lastSnare;
            _lastKick = kicks;
            _lastSnare = snares;

            int n = AudioLevelMonitor.Bands;
            float mx = 0, sum = 0, wsum = 0, flux = 0;
            for (int i = 0; i < n; i++)
            {
                float v = _spec[i];
                if (v > mx) mx = v;
                sum += v;
                wsum += v * i;
                if (v > _prevSpec[i]) flux += v - _prevSpec[i];
                _prevSpec[i] = v;
            }

            // автоусиление: подстраиваемся под громкость видео
            _specPeak = Math.Max(mx, _specPeak * 0.99f);
            float norm = Math.Max(_specPeak, 0.02f);

            // любой резкий рост звука — это тоже «удар», даже если барабаны не распознаны (шумные записи с телефона)
            long now = Environment.TickCount64;
            float fl = flux / norm;
            _fluxAvg = _fluxAvg * 0.94f + fl * 0.06f;
            bool onset = rms > 0.003f && fl > Math.Max(0.5f, _fluxAvg * 1.6f) && now - _lastOnset > 100;
            if (onset) _lastOnset = now;
            bool beat = kick || onset;

            // «центр тяжести» спектра: низкий — тёплые цвета, высокий — холодные
            float centroid = sum > 0.0001f ? wsum / sum : _centroidSm;
            _centroidSm += (centroid - _centroidSm) * 0.15f;

            float energy = Math.Min(1f, rms * 7f);
            _energySm = energy > _energySm ? energy : _energySm * 0.90f + energy * 0.10f;

            _kickEnv = beat ? 1f : _kickEnv * 0.80f;
            _flash *= 0.78f;

            double targetHue = 10 + 270.0 * (_centroidSm / (n - 1));
            _hue += (targetHue - _hue) * 0.08;
            Color color = FromHsv(_hue, 0.9, 1.0);
            color = ApplyPictureColor(color);      // если видна картинка видео — цвет берётся из неё

            // малый барабан и смена сцены в видео — вспышка острова
            if (snare && (_emoMode == 0 || _emoMode == 3))
                _flash = Math.Max(_flash, _emoMode == 3 ? 0.85f : 0.5f);
            if (TakeScreenFlash() && (_emoMode == 0 || _emoMode == 3))
                _flash = Math.Max(_flash, _emoMode == 3 ? 0.95f : 0.65f);

            // сила фона острова: дышит на звук, подпрыгивает на удар, вспыхивает на барабан
            float beatK = _emoMode == 1 ? 0.3f : 1f;
            float bg = 0.08f + 0.22f * _energySm + 0.34f * _kickEnv * beatK + _flash;
            if (bg > 0.85f) bg = 0.85f;

            _islandBrush.Color = Color.FromRgb((byte)(color.R * bg), (byte)(color.G * bg), (byte)(color.B * bg));

            // столбики светлеют вместе с фоном, чтобы не сливаться с ним
            Color barColor = Lerp(color, Colors.White, Math.Min(1f, bg * 1.1f));
            _emoBrush.Color = barColor;

            for (int i = 0; i < n; i++)
            {
                float v = (float)Math.Pow(Math.Min(1f, _spec[i] / norm), 0.8);
                float h;

                if (_emoMode == 2)          // пульс: все столбики вместе, на каждый удар
                    h = 0.15f + 0.85f * Math.Max(_kickEnv, _energySm * 0.6f);
                else if (_emoMode == 3)     // вспышка: столбики спокойные, всё внимание — на вспышки
                    h = 0.15f + 0.35f * v + 0.25f * _kickEnv;
                else                        // спектр (и авто — с добавкой на удар)
                {
                    h = 0.14f + 0.86f * v;
                    if (_emoMode == 0) h = Math.Min(1f, h + 0.35f * _kickEnv);
                }

                // быстро вверх, плавно вниз
                double cur = _emoScale[i].ScaleY;
                _emoScale[i].ScaleY = h > cur ? cur + (h - cur) * 0.85 : cur * 0.70 + h * 0.30;
            }

            // остров «подпрыгивает» на ритм (свечение вместе с ним)
            double k = beatK * _kickEnv;
            double sx = 1 + 0.05 * k + 0.02 * _energySm * beatK;
            double sy = 1 + 0.09 * k + 0.03 * _energySm * beatK;
            _bounceI.ScaleX = sx; _bounceI.ScaleY = sy;
            _bounceG.ScaleX = sx; _bounceG.ScaleY = sy;

            // свечение дышит на бочку
            if (_emoMode != 1)
                GlowBack.Opacity = 0.35 + 0.65 * Math.Max(_kickEnv, _energySm * 0.7f);

            // цвет свечения следует за музыкой (не на каждом кадре, а когда цвет заметно изменился)
            if (ColorDist(color, _lastGlowCol) > 40)
            {
                _lastGlowCol = color;
                _glow.BeginAnimation(DropShadowEffect.ColorProperty,
                    new ColorAnimation(color, TimeSpan.FromMilliseconds(160)));
            }

            // колонки в чёрных полосах по бокам
            float hi = 0;
            for (int i = n - 5; i < n; i++) hi += Math.Min(1f, _spec[i] / norm);
            UpdateSpeakers(color, Math.Max(_kickEnv, _energySm * 0.6f), hi / 5f, beat, _flash);
        }

        private void ResetEmo()
        {
            if (_emoScale == null) return;

            foreach (var sc in _emoScale) sc.ScaleY = 0.2;

            _islandBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _islandBrush.Color = Colors.Black;

            _flash = 0;
            _kickEnv = 0;
            _bounceI.ScaleX = _bounceI.ScaleY = 1;
            _bounceG.ScaleX = _bounceG.ScaleY = 1;

            GlowBack.Opacity = 1;
            _lastGlowHue = -100;
            _lastGlowCol = Colors.Transparent;
            _glow.BeginAnimation(DropShadowEffect.ColorProperty,
                new ColorAnimation(_curAccent, TimeSpan.FromMilliseconds(300)));
        }
    }
}
