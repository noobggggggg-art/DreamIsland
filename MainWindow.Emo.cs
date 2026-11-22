using System;
using System.Diagnostics;
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
        private long _lastOnset, _lastBeatTick;
        private float _eFast, _eSlow, _intensity;
        private int _lastBeat;
        private double _hueBase = 20;
        private float _centAvg = 6f;
        private readonly float[] _bandAvg = new float[AudioLevelMonitor.Bands];
        private readonly float[] _bandV = new float[AudioLevelMonitor.Bands];

        // стерео: у каждой колонки свои громкость, верха и «удары в своём канале»
        private readonly float[] _specL = new float[AudioLevelMonitor.Bands];
        private readonly float[] _specR = new float[AudioLevelMonitor.Bands];
        private readonly float[] _hiAvgL = new float[5];
        private readonly float[] _hiAvgR = new float[5];
        private float _rmsLSm, _rmsRSm, _shareL = 1f, _shareR = 1f, _kickEnvL, _kickEnvR;
        private int _lastOnL, _lastOnR;
        private long _lastSideL, _lastSideR;

        // нарастание и «дроп»
        private float BuildRise = 1.06f;       // во сколько раз «энергия за ~1 с» должна превышать «энергию за ~4 с», чтобы считать, что звук растёт
        private float BuildKeep = 1.02f;       // а так — пока рост не прекратился
        private double BuildSeconds = 4.0;     // сколько секунд непрерывного роста до полного «напряжения»
        private float BuildArm = 0.55f;        // с какого напряжения дроп вообще возможен
        private float DropBurst = 1.35f;       // во сколько раз мгновенная энергия выше «энергии за ~1 с» на самом взрыве
        private long DropCooldownMs = 7000;
        private float _eB1, _eB2, _eBurst, _build, _dropEnv;
        private long _armUntil, _lastDrop, _lastEmoStamp;

        // затишье перед дропом (брейкдаун): мгновенная энергия резко упала относительно «энергии за ~4 с»
        private float LullRatio = 0.55f;             // во сколько раз мгновенная энергия должна упасть, чтобы считать это затишьем
        private float _lull, _lullHold, _lullPeak, _dropBoost;
        private long _lullArmUntil;
        private int _dropCount;

        // медленное затишье: звук не обрывается, а плавно стихает (интенсивность падает почти до нуля после громкого куска)
        private float _intPeak, _slowHold, _slowAge;
        private bool _slowLull;

        // акценты: резкие скачки спектра (запасной признак «удара», когда детекторы бочки и малого барабана молчат)
        private long _lastAccent;
        private int _accentCount;

        // спектр каждого канала для LED-шкал колонок
        private readonly float[] _bandAvgL = new float[AudioLevelMonitor.Bands];
        private readonly float[] _bandAvgR = new float[AudioLevelMonitor.Bands];
        private readonly float[] _bandVL = new float[AudioLevelMonitor.Bands];
        private readonly float[] _bandVR = new float[AudioLevelMonitor.Bands];

        private struct SpkFeed { public float Level, High, Power, Inten; public bool Beat, Snare; public float[] Bands; }

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

            // настоящий ритм: пики баса (40–120 Гц) с защитой от дребезга; «барабаны» на шумных концертных записях ловят лишнее
            long now = Environment.TickCount64;
            int beats = _levels.BeatCount;
            bool bassBeat = beats != _lastBeat;
            _lastBeat = beats;
            if (bassBeat) _lastBeatTick = now;

            // запасной вариант: если баса давно нет (мелодия без ударных), ловим резкий рост любого звука
            float fl = flux / norm;
            _fluxAvg = _fluxAvg * 0.94f + fl * 0.06f;
            bool onset = rms > 0.003f && now - _lastBeatTick > 1500 && fl > Math.Max(0.6f, _fluxAvg * 1.8f) && now - _lastOnset > 250;
            if (onset) _lastOnset = now;
            bool beat = bassBeat || onset;

            // акцент: резкий скачок спектра в любой момент (на плотной громкой музыке бочка и малый барабан могут не срабатывать вовсе)
            bool hitAcc = rms > 0.003f && fl > Math.Max(0.6f, _fluxAvg * 1.8f) && now - _lastAccent > 180;
            if (hitAcc) { _lastAccent = now; _accentCount++; }

            // «центр тяжести» спектра: низкий — тёплые цвета, высокий — холодные
            float centroid = sum > 0.0001f ? wsum / sum : _centroidSm;
            _centroidSm += (centroid - _centroidSm) * 0.15f;

            // интенсивность: насколько музыка сейчас громче и плотнее своего обычного уровня (припев, взрыв, дроп).
            // Абсолютная громкость на концертах всегда «в потолок», поэтому сравниваем с собой же за последние секунды.
            if (_eSlow <= 0f) { _eFast = _eSlow = rms; }
            _eFast += (rms - _eFast) * 0.09f;      // около 0,35 с
            _eSlow += (rms - _eSlow) * 0.0028f;    // около 12 с
            float ratio = _eFast / (_eSlow + 1e-4f);
            float iTarget = rms > 0.003f ? Math.Max(0f, Math.Min(1f, (ratio - 0.8f) / 0.55f)) : 0f;
            _intensity += (iTarget - _intensity) * (iTarget > _intensity ? 0.30f : 0.06f);
            _energySm = 0.1f + 0.9f * _intensity;

            // нарастание и «дроп»: сравниваем энергию за ~1 с с энергией за ~4 с. Пока первая стабильно выше — звук растёт
            long stamp = Stopwatch.GetTimestamp();
            double dtS = _lastEmoStamp == 0 ? 0.033 : (stamp - _lastEmoStamp) / (double)Stopwatch.Frequency;
            _lastEmoStamp = stamp;
            dtS = Math.Max(0.001, Math.Min(0.1, dtS));

            if (_eB2 <= 0f) { _eB1 = _eB2 = _eBurst = rms; }
            _eB1 += (rms - _eB1) * (1f - (float)Math.Exp(-dtS / 1.0));
            _eB2 += (rms - _eB2) * (1f - (float)Math.Exp(-dtS / 4.0));
            _eBurst += (rms - _eBurst) * (1f - (float)Math.Exp(-dtS / 0.10));

            bool live = rms > 0.003f;
            float rise = _eB1 / (_eB2 + 1e-4f);
            if (live && rise > (_build > 0.05f ? BuildKeep : BuildRise)) _build += (float)(dtS / BuildSeconds);
            else _build -= (float)(dtS / 1.5);
            _build = Math.Max(0f, Math.Min(1f, _build));
            if (_build >= BuildArm) _armUntil = now + 2500;   // после конца нарастания взрыв ещё можно «поймать» ~2,5 с

            // затишье (брейкдаун): трек был громким, а сейчас мгновенная энергия сильно ниже средней за ~4 с.
            // Колонки в этот момент гаснут и замирают, а после затишья дроп разрешён и получается сильнее
            bool quietNow = _eB2 > 0.012f && _eBurst < _eB2 * LullRatio;
            if (quietNow) _lullHold = Math.Min(8f, _lullHold + (float)dtS);
            else _lullHold = Math.Max(0f, _lullHold - (float)(dtS * 4));

            // медленное затишье: после громкого куска интенсивность упала почти до нуля и держится там
            _intPeak = Math.Max(_intensity, _intPeak * (float)Math.Exp(-dtS / 25.0));
            if (!_slowLull)
            {
                if (_intensity < 0.15f && _intPeak > 0.6f)
                {
                    _slowHold += (float)dtS;
                    if (_slowHold > 0.8f) { _slowLull = true; _slowAge = 0f; }
                }
                else _slowHold = 0f;
            }
            else
            {
                _slowAge += (float)dtS;
                if (_intensity > 0.30f || _slowAge > 14f)
                {
                    _slowLull = false;
                    _slowHold = 0f;
                    _intPeak = _intensity;   // чтобы сразу не войти в затишье снова
                }
            }

            float lullTarget = quietNow && _lullHold > 0.25f && _lullHold < 7f ? 1f : 0f;
            if (_slowLull) lullTarget = Math.Max(lullTarget, 0.8f);
            _lull += (lullTarget - _lull) * (1f - (float)Math.Exp(-dtS * (lullTarget > _lull ? 4.0 : 10.0)));
            if (_lull > 0.5f && (_lullHold > 0.4f || _slowLull)) _lullArmUntil = now + 3500;
            _lullPeak = Math.Max(_lullPeak * (float)Math.Exp(-dtS / 3.0), _lull);

            _dropEnv *= (float)Math.Exp(-dtS / 0.5);
            bool drop = false;
            // после затишья отдельный удар баса не обязателен: хватает очень резкого скачка энергии
            bool lullArmed = now < _lullArmUntil;
            bool hitDrop = bassBeat || (lullArmed && _eBurst > _eB1 * DropBurst * 1.25f);
            if (live && (now < _armUntil || lullArmed) && now - _lastDrop > DropCooldownMs &&
                hitDrop && _eBurst > _eB1 * DropBurst)
            {
                drop = true;
                _lastDrop = now;
                _armUntil = 0;
                _lullArmUntil = 0;
                _build = 0f;
                _dropEnv = 1f;
                _dropBoost = Math.Min(0.8f, 0.9f * _lullPeak);   // после затишья взрыв сильнее
                _lullPeak = 0f;
                _lullHold = 0f;
                _lull = 0f;
                _dropCount++;
            }

            _kickEnv = beat ? 1f : _kickEnv * 0.80f;
            _flash *= 0.78f;

            // палитра «течёт»: сама понемногу, шагает на каждый удар и слегка зависит от тембра
            _centAvg += (_centroidSm - _centAvg) * 0.01f;
            _hueBase += 0.05 + (beat ? 8.0 : 0.0) + (snare ? 4.0 : 0.0);
            if (_hueBase >= 360) _hueBase -= 360;
            double timbre = Math.Max(-50, Math.Min(50, (_centroidSm - _centAvg) * 14.0));
            double targetHue = (_hueBase + timbre + 360) % 360;
            double dh = targetHue - _hue;
            if (dh > 180) dh -= 360;
            if (dh < -180) dh += 360;
            _hue = (_hue + dh * 0.18 + 360) % 360;
            Color audioColor = FromHsv(_hue, 0.9, 1.0);
            Color color = ApplyPictureColor(audioColor);      // если видна картинка видео — цвет берётся из неё

            // малый барабан и смена сцены в видео — вспышка острова
            if (snare && (_emoMode == 0 || _emoMode == 3))
                _flash = Math.Max(_flash, _emoMode == 3 ? 0.85f : 0.5f);
            if (TakeScreenFlash() && (_emoMode == 0 || _emoMode == 3))
                _flash = Math.Max(_flash, _emoMode == 3 ? 0.95f : 0.65f);

            if (drop) _flash = 1f;

            // сила фона острова: дышит на звук, подпрыгивает на удар, вспыхивает на барабан;
            // на нарастании понемногу разгорается, на дропе — большая вспышка
            float beatK = _emoMode == 1 ? 0.3f : 1f;
            float bg = 0.08f + 0.22f * _energySm + 0.34f * _kickEnv * beatK + _flash + 0.16f * _build + 0.30f * _dropEnv;
            bg *= 1f - 0.35f * _lull;   // в затишье остров тоже притихает
            float bgCap = 0.85f + 0.12f * _dropEnv;
            if (bg > bgCap) bg = bgCap;

            _islandBrush.Color = Color.FromRgb((byte)(color.R * bg), (byte)(color.G * bg), (byte)(color.B * bg));

            // столбики светлеют вместе с фоном, чтобы не сливаться с ним
            Color barColor = Lerp(color, Colors.White, Math.Min(1f, bg * 1.1f));
            _emoBrush.Color = barColor;

            // каждая полоса сравнивается со своим средним: на концертах бас громче всего, и без этого правые столбики мёртвые
            float gate = Math.Min(1f, rms / 0.008f);
            for (int i = 0; i < n; i++)
            {
                _bandAvg[i] = _bandAvg[i] * 0.98f + _spec[i] * 0.02f;
                float v = (float)Math.Pow(Math.Min(1f, _spec[i] / (_bandAvg[i] * 1.9f + 1e-4f)), 1.1) * gate;
                _bandV[i] = v;
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

                // на нарастании столбики приподнимаются, на дропе — все вверх
                h = Math.Min(1f, h + 0.22f * _build);
                if (_dropEnv > 0.02f) h = Math.Max(h, 0.2f + 0.8f * _dropEnv);

                // быстро вверх, плавно вниз
                double cur = _emoScale[i].ScaleY;
                _emoScale[i].ScaleY = h > cur ? cur + (h - cur) * 0.85 : cur * 0.70 + h * 0.30;
            }

            // остров «подпрыгивает» на ритм (свечение вместе с ним)
            double k = beatK * _kickEnv * (0.7 + 0.9 * _intensity) + 1.4 * _dropEnv;
            double sx = 1 + 0.05 * k + 0.02 * _energySm * beatK;
            double sy = 1 + 0.09 * k + 0.03 * _energySm * beatK;
            _bounceI.ScaleX = sx; _bounceI.ScaleY = sy;
            _bounceG.ScaleX = sx; _bounceG.ScaleY = sy;

            // свечение дышит на бочку
            if (_emoMode != 1)
                GlowBack.Opacity = Math.Min(1.0, 0.35 + 0.65 * Math.Max(_kickEnv, _energySm * 0.7f) + 0.30 * _build + 0.5 * _dropEnv);

            // цвет свечения следует за музыкой (не на каждом кадре, а когда цвет заметно изменился)
            if (ColorDist(color, _lastGlowCol) > 40)
            {
                _lastGlowCol = color;
                _glow.BeginAnimation(DropShadowEffect.ColorProperty,
                    new ColorAnimation(color, TimeSpan.FromMilliseconds(160)));
            }

            // колонки в чёрных полосах по бокам: у каждой свой канал (стерео), а нарастание и дроп общие
            float hi = 0;
            for (int i = n - 5; i < n; i++) hi += _bandV[i];
            hi /= 5f;

            SpkFeed spL = new SpkFeed(), spR = new SpkFeed();
            spL.Level = spR.Level = Math.Max(_kickEnv * (0.5f + 0.5f * _intensity), _intensity * 0.75f);
            spL.High = spR.High = hi;
            spL.Beat = spR.Beat = beat;
            spL.Power = spR.Power = 1f;
            spL.Inten = spR.Inten = _intensity;
            spL.Snare = spR.Snare = snare || hitAcc;
            spL.Bands = spR.Bands = _bandV;

            if (_levels.Stereo)
            {
                // кто громче: 1 — поровну, 2 — звук целиком в одном канале (сглаживаем, чтобы колонки не дёргались)
                _rmsLSm += (_levels.RmsL - _rmsLSm) * 0.25f;
                _rmsRSm += (_levels.RmsR - _rmsRSm) * 0.25f;
                float tot = _rmsLSm + _rmsRSm;
                float shL = 1f, shR = 1f;
                if (tot > 0.004f) { shL = 2f * _rmsLSm / tot; shR = 2f * _rmsRSm / tot; }
                _shareL += (shL - _shareL) * 0.2f;
                _shareR += (shR - _shareR) * 0.2f;

                // верха (гитара, тарелки, вокал) у каждого канала свои
                _levels.GetSpectrumLR(_specL, _specR);
                float hiL = HighOf(_specL, _hiAvgL, gate), hiR = HighOf(_specR, _hiAvgR, gate);

                // для LED-шкал: каждая полоса каждого канала сравнивается со своим средним
                NormBands(_specL, _bandAvgL, _bandVL, gate);
                NormBands(_specR, _bandAvgR, _bandVR, gate);
                spL.Bands = _bandVL;
                spR.Bands = _bandVR;

                // резкий удар только в одном канале (гитарный удар слева) — толкает только свою колонку
                int oL = _levels.OnsetCountL, oR = _levels.OnsetCountR;
                bool onL = oL != _lastOnL, onR = oR != _lastOnR;
                _lastOnL = oL; _lastOnR = oR;

                bool sideL = false, sideR = false;
                if (!beat && rms > 0.003f)
                {
                    if (onL && !onR && now - _lastSideL > 220) { sideL = true; _lastSideL = now; }
                    else if (onR && !onL && now - _lastSideR > 220) { sideR = true; _lastSideR = now; }
                }

                _kickEnvL = beat ? 1f : (sideL ? 0.8f : _kickEnvL * 0.80f);
                _kickEnvR = beat ? 1f : (sideR ? 0.8f : _kickEnvR * 0.80f);

                float mL = 0.6f + 0.4f * _shareL, mR = 0.6f + 0.4f * _shareR;    // 0,6…1,4 — перекос в сторону громкого канала
                float iL = Math.Min(1f, _intensity * mL), iR = Math.Min(1f, _intensity * mR);

                spL.Level = Math.Min(1f, Math.Max(_kickEnvL * (0.5f + 0.5f * iL), iL * 0.75f));
                spR.Level = Math.Min(1f, Math.Max(_kickEnvR * (0.5f + 0.5f * iR), iR * 0.75f));
                spL.High = hiL; spR.High = hiR;
                spL.Inten = iL; spR.Inten = iR;
                spL.Beat = beat || sideL; spR.Beat = beat || sideR;
                spL.Snare = snare || hitAcc || sideL; spR.Snare = snare || hitAcc || sideR;
                spL.Power = beat ? 1f : 0.7f; spR.Power = beat ? 1f : 0.7f;
            }

            UpdateSpeakers(color, audioColor, spL, spR, _flash, _build, drop, false, _lull, drop ? _dropBoost : 0f);

            UpdateDebug(rms, rise, drop, now);
        }

        /// <summary>Нормализованный спектр одного канала (как у общих столбиков острова).</summary>
        private static void NormBands(float[] spec, float[] avg, float[] outv, float gate)
        {
            for (int i = 0; i < AudioLevelMonitor.Bands; i++)
            {
                avg[i] = avg[i] * 0.98f + spec[i] * 0.02f;
                outv[i] = (float)Math.Pow(Math.Min(1f, spec[i] / (avg[i] * 1.9f + 1e-4f)), 1.1) * gate;
            }
        }

        /// <summary>Насколько «живы» верхние 5 полос канала относительно их же среднего (как у общих столбиков).</summary>
        private static float HighOf(float[] spec, float[] avg, float gate)
        {
            int n = AudioLevelMonitor.Bands;
            float sum = 0;
            for (int i = 0; i < 5; i++)
            {
                float v = spec[n - 5 + i];
                avg[i] = avg[i] * 0.98f + v * 0.02f;
                sum += (float)Math.Pow(Math.Min(1f, v / (avg[i] * 1.9f + 1e-4f)), 1.1) * gate;
            }
            return sum / 5f;
        }

        private void ResetEmo()
        {
            if (_emoScale == null) return;

            foreach (var sc in _emoScale) sc.ScaleY = 0.2;

            _islandBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _islandBrush.Color = Colors.Black;

            _flash = 0;
            _kickEnv = 0;
            _eFast = _eSlow = _intensity = 0;
            _eB1 = _eB2 = _eBurst = 0;
            _build = _dropEnv = 0;
            _lull = _lullHold = _lullPeak = _dropBoost = 0f;
            _intPeak = _slowHold = _slowAge = 0f;
            _slowLull = false;
            _lullArmUntil = 0;
            Array.Clear(_bandAvgL, 0, _bandAvgL.Length);
            Array.Clear(_bandAvgR, 0, _bandAvgR.Length);
            Array.Clear(_bandVL, 0, _bandVL.Length);
            Array.Clear(_bandVR, 0, _bandVR.Length);
            HideDebug();
            _armUntil = 0;
            _lastEmoStamp = 0;
            _kickEnvL = _kickEnvR = 0;
            _rmsLSm = _rmsRSm = 0;
            _shareL = _shareR = 1f;
            Array.Clear(_hiAvgL, 0, _hiAvgL.Length);
            Array.Clear(_hiAvgR, 0, _hiAvgR.Length);
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
