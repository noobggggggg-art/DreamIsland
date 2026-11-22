using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace DreamIsland
{
    /// <summary>
    /// Чувствительность острова «Эмоции» (тихо / обычно / жёстко) и скрытая отладочная панель с числами.
    /// Пресеты заменяют константы, которые раньше приходилось править в коде.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class Tune
        {
            public string Name;
            public float BuildRise, BuildKeep, BuildArm, DropBurst, LullRatio;
            public double BuildSeconds;
            public long DropCooldownMs;
            public double SparkInten, SparkLevel, SparkGap;
        }

        // «тихо» — реагирует только на самые сильные моменты, «жёстко» — на всё, что хоть немного похоже на нарастание или удар
        private static readonly Tune[] Tunes =
        {
            new Tune { Name = "Тихо",   BuildRise = 1.09f, BuildKeep = 1.03f, BuildSeconds = 5.0, BuildArm = 0.65f, DropBurst = 1.55f, DropCooldownMs = 9000, LullRatio = 0.40f, SparkInten = 0.70, SparkLevel = 0.80, SparkGap = 0.50 },
            new Tune { Name = "Обычно", BuildRise = 1.06f, BuildKeep = 1.02f, BuildSeconds = 4.0, BuildArm = 0.55f, DropBurst = 1.35f, DropCooldownMs = 7000, LullRatio = 0.55f, SparkInten = 0.55, SparkLevel = 0.65, SparkGap = 0.32 },
            new Tune { Name = "Жёстко", BuildRise = 1.04f, BuildKeep = 1.01f, BuildSeconds = 3.0, BuildArm = 0.40f, DropBurst = 1.20f, DropCooldownMs = 4500, LullRatio = 0.68f, SparkInten = 0.35, SparkLevel = 0.50, SparkGap = 0.20 }
        };

        private int _sens = 1;
        private Tune _tune = Tunes[1];

        private DebugWindow _dbg;
        private bool _dbgEnabled;
        private int _dbgTick;
        private string _dbgText = "";
        private readonly Queue<string> _dbgHist = new Queue<string>();
        private long _dbgLastHist;
        private const int DbgHistMax = 60;   // минута истории, по строке в секунду

        private void InitTune()
        {
            SetSens(AppSettings.GetInt("emo_sens", 1), false);
            _spkFloat = AppSettings.GetBool("spkfloat");
            SetMenu("spkfloat", _spkFloat);
        }

        private void SetSens(int sens, bool save)
        {
            if (sens < 0 || sens >= Tunes.Length) sens = 1;
            _sens = sens;
            _tune = Tunes[sens];

            BuildRise = _tune.BuildRise;
            BuildKeep = _tune.BuildKeep;
            BuildSeconds = _tune.BuildSeconds;
            BuildArm = _tune.BuildArm;
            DropBurst = _tune.DropBurst;
            DropCooldownMs = _tune.DropCooldownMs;
            LullRatio = _tune.LullRatio;

            for (int i = 0; i < Tunes.Length; i++)
            {
                var mi = FindMenuDeep("sens" + i);
                if (mi != null) mi.IsChecked = i == sens;
            }

            if (save) AppSettings.SetString("emo_sens", sens.ToString());
        }

        private void Sens_Click(object sender, RoutedEventArgs e)
        {
            var mi = (MenuItem)sender;
            string tag = mi.Tag as string;
            int n;
            if (tag != null && tag.Length > 4 && int.TryParse(tag.Substring(4), out n))
                SetSens(n, true);
        }

        // ---------- отладочная панель ----------

        private void Debug_Toggle(bool on)
        {
            _dbgEnabled = on;   // нарочно не сохраняется: после перезапуска панель снова скрыта
            if (!on) HideDebug();
        }

        private void HideDebug()
        {
            if (_dbg == null) return;
            try { _dbg.Hide(); } catch { }
        }

        private void DisposeDebug()
        {
            if (_dbg == null) return;
            try { _dbg.Close(); } catch { }
            _dbg = null;
        }

        private static string Bar(float v, int n = 10)
        {
            int k = (int)Math.Round(Math.Max(0f, Math.Min(1f, v)) * n);
            return new string('\u2588', k) + new string('\u2591', n - k);
        }

        private static string F(float v, string fmt = "0.00")
        {
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        /// <summary>Числа, по которым подбираются пороги: интенсивность, нарастание, момент дропа, затишье, баланс каналов.</summary>
        private void UpdateDebug(float rms, float rise, bool drop, long now)
        {
            if (!_dbgEnabled) return;
            if (++_dbgTick % 3 != 0 && !drop) return;   // обновляем ~10 раз в секунду

            if (_dbg == null) _dbg = new DebugWindow { Owner = this, CopyRequested = CopyDebug };
            if (!_dbg.IsVisible) _dbg.Show();

            float burstK = _eBurst / (_eB1 + 1e-4f);
            long armLeft = Math.Max(_armUntil, _lullArmUntil) - now;
            string armTxt = armLeft > 0 ? (armLeft / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " с" : "нет";
            string lastDrop = _lastDrop == 0 ? "ещё не было" : ((now - _lastDrop) / 1000) + " с назад";
            float bal = (_shareR - _shareL) / 2f * 100f;

            var sb = new StringBuilder();
            sb.AppendLine("ЭМОЦИИ · ОТЛАДКА      чувствительность: " + _tune.Name);
            sb.AppendLine("громкость   " + F(rms, "0.000"));
            sb.AppendLine("интенсивн.  " + Bar(_intensity) + " " + F(_intensity));
            sb.AppendLine("нарастание  " + Bar(_build) + " " + F(_build) + "  (дроп с " + F(BuildArm) + ")");
            sb.AppendLine("рост 1с/4с  " + F(rise, "0.000") + "  (нужно > " + F(_build > 0.05f ? BuildKeep : BuildRise, "0.000") + ")");
            sb.AppendLine("всплеск     " + F(burstK) + "  (нужно > " + F(DropBurst) + ")");
            sb.AppendLine("затишье     " + Bar(_lull) + " " + F(_lull) + "  (энергия < " + F(LullRatio) + " от средней" + (_slowLull ? ", медленное" : "") + ")");
            sb.AppendLine("дроп можно  " + armTxt);
            sb.AppendLine("дропов      " + _dropCount + "  (последний: " + lastDrop + ")");
            sb.AppendLine("баланс      L " + F(_shareL) + "  R " + F(_shareR) + "  (" + (bal >= 0 ? "+" : "") + F(bal, "0") + "% вправо)");
            sb.AppendLine("громк. L/R  " + F(_rmsLSm, "0.000") + " / " + F(_rmsRSm, "0.000") + (_levels.Stereo ? "" : "  (моно)"));
            sb.Append("удары       бас " + _levels.BeatCount + "  бочка " + _levels.KickCount + "  малый " + _levels.SnareCount + "  акцентов " + _accentCount);
            _dbgText = sb.ToString();
            _dbg.SetText(_dbgText);

            // история для копирования: раз в секунду короткая строка, плюс отдельная строка на каждый дроп
            if (drop) AddHist(now, "ДРОП  (бонус после затишья +" + F(_dropBoost) + ")");
            else if (now - _dbgLastHist >= 1000)
            {
                _dbgLastHist = now;
                AddHist(now, "гр " + F(rms, "0.000") + "  инт " + F(_intensity) + "  нар " + F(_build) +
                             "  рост " + F(rise, "0.000") + "  всп " + F(burstK) + "  зат " + F(_lull) +
                             "  L/R " + F(_shareL) + "/" + F(_shareR));
            }
        }

        private void AddHist(long now, string line)
        {
            _dbgHist.Enqueue(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
            while (_dbgHist.Count > DbgHistMax) _dbgHist.Dequeue();
        }

        /// <summary>Копирует текущие числа и историю за последнюю минуту в буфер обмена.</summary>
        private bool CopyDebug()
        {
            var sb = new StringBuilder();
            sb.AppendLine(_dbgText);
            sb.AppendLine();
            sb.AppendLine("История (гр — громкость, инт — интенсивность, нар — нарастание, рост — 1с/4с, всп — всплеск, зат — затишье):");
            foreach (var line in _dbgHist) sb.AppendLine(line);

            try { Clipboard.SetText(sb.ToString()); return true; }
            catch { return false; }
        }
    }
}
