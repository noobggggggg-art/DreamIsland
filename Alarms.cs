using System;
using System.Collections.Generic;
using System.Globalization;
using System.Media;

namespace DreamIsland
{
    /// <summary>Один будильник: время, дни повтора, подпись и мелодия.</summary>
    internal sealed class AlarmItem
    {
        public int Hour;
        public int Minute;
        public int Days;          // биты: Пн = 1, Вт = 2 … Вс = 64; 0 — сработать один раз
        public bool On = true;
        public int Sound;
        public string Label = "";

        public bool Repeats { get { return Days != 0; } }

        public string TimeText
        {
            get { return Hour.ToString("00") + ":" + Minute.ToString("00"); }
        }

        public bool RunsOn(DayOfWeek d)
        {
            if (Days == 0) return true;
            int bit = 1 << (((int)d + 6) % 7);   // понедельник = 0
            return (Days & bit) != 0;
        }

        public AlarmItem Copy()
        {
            return new AlarmItem { Hour = Hour, Minute = Minute, Days = Days, On = On, Sound = Sound, Label = Label };
        }

        // формат одной записи: ЧЧ:ММ|дни|вкл|мелодия|подпись
        public string Serialize()
        {
            string label = (Label ?? "").Replace("|", " ").Replace(";", " ").Replace("\r", "").Replace("\n", " ");
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}|{2}|{3}|{4}|{5}",
                Hour, Minute, Days, On ? 1 : 0, Sound, label);
        }

        public static AlarmItem Parse(string s)
        {
            try
            {
                var p = s.Split(new[] { '|' }, 5);
                if (p.Length < 5) return null;

                var t = p[0].Split(':');
                int h = int.Parse(t[0], CultureInfo.InvariantCulture);
                int m = int.Parse(t[1], CultureInfo.InvariantCulture);
                if (h < 0 || h > 23 || m < 0 || m > 59) return null;

                return new AlarmItem
                {
                    Hour = h,
                    Minute = m,
                    Days = int.Parse(p[1], CultureInfo.InvariantCulture) & 127,
                    On = p[2] == "1",
                    Sound = Math.Max(0, Math.Min(AlarmStore.SoundNames.Length - 1, int.Parse(p[3], CultureInfo.InvariantCulture))),
                    Label = p[4]
                };
            }
            catch { return null; }
        }
    }

    /// <summary>Хранение будильников в настройках и мелодии (системные звуки Windows, без файлов).</summary>
    internal static class AlarmStore
    {
        public static readonly string[] SoundNames = { "Мягкий", "Яркий", "Строгий", "Короткий", "Вопрос" };

        public static List<AlarmItem> Load()
        {
            var list = new List<AlarmItem>();
            string raw = AppSettings.GetString("alarms", "");
            if (raw.Length == 0) return list;

            foreach (var part in raw.Split(';'))
            {
                if (part.Length == 0) continue;
                var a = AlarmItem.Parse(part);
                if (a != null) list.Add(a);
            }
            return list;
        }

        public static void Save(List<AlarmItem> list)
        {
            var parts = new List<string>();
            foreach (var a in list) parts.Add(a.Serialize());
            AppSettings.SetString("alarms", string.Join(";", parts));
        }

        public static void Play(int sound)
        {
            try
            {
                switch (sound)
                {
                    case 1: SystemSounds.Exclamation.Play(); break;
                    case 2: SystemSounds.Hand.Play(); break;
                    case 3: SystemSounds.Beep.Play(); break;
                    case 4: SystemSounds.Question.Play(); break;
                    default: SystemSounds.Asterisk.Play(); break;
                }
            }
            catch { }
        }

        /// <summary>Ближайшее срабатывание после now или null.</summary>
        public static DateTime? Next(AlarmItem a, DateTime now)
        {
            for (int d = 0; d <= 7; d++)
            {
                var t = now.Date.AddDays(d).AddHours(a.Hour).AddMinutes(a.Minute);
                if (t > now && a.RunsOn(t.DayOfWeek)) return t;
            }
            return null;
        }

        public static string Until(DateTime t, DateTime now)
        {
            var span = t - now;
            if (span.TotalMinutes < 1) return "меньше минуты";

            int days = (int)span.TotalDays;
            int hours = span.Hours;
            int mins = span.Minutes + (span.Seconds >= 30 ? 1 : 0);
            if (mins == 60) { mins = 0; hours++; }

            if (days > 0) return days + " д " + hours + " ч";
            if (hours > 0) return hours + " ч " + mins + " мин";
            return mins + " мин";
        }

        private static readonly string[] DayShort = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
        public static string DayName(int i) { return DayShort[i]; }

        public static string DaysText(int days)
        {
            if (days == 0) return "Один раз";
            if (days == 127) return "Каждый день";
            if (days == 31) return "По будням";
            if (days == 96) return "По выходным";

            var parts = new List<string>();
            for (int i = 0; i < 7; i++)
                if ((days & (1 << i)) != 0) parts.Add(DayShort[i]);
            return string.Join(", ", parts);
        }
    }
}
