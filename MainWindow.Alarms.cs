using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DreamIsland
{
    /// <summary>
    /// Будильники (срабатывают в заданное время суток) и звонок по окончании таймера.
    /// Звонок: плашка на острове + мелодия, повторяется до конца «длительности звонка».
    /// Клик по острову во время звонка откладывает его (если отсрочка включена) или выключает.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class Snooze
        {
            public DateTime At;
            public string Label, Value;
            public int Sound;
        }

        private List<AlarmItem> _alarms = new List<AlarmItem>();
        private readonly List<Snooze> _snoozes = new List<Snooze>();
        private DateTime _alarmLast = DateTime.Now;

        private readonly DispatcherTimer _alarmTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _ringTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };

        private bool _ringing;
        private DateTime _ringEnd;
        private int _ringSound;
        private string _ringLabel = "", _ringValue = "";

        private void InitAlarms()
        {
            _alarms = AlarmStore.Load();
            _alarmLast = DateTime.Now;
            _alarmTick.Tick += (s, e) => AlarmTick();
            _ringTick.Tick += (s, e) => RingTick();
            _alarmTick.Start();
        }

        private void DisposeAlarms()
        {
            _alarmTick.Stop();
            _ringTick.Stop();
        }

        /// <summary>Окно настроек изменило список будильников.</summary>
        private void OnAlarmsChanged()
        {
            _alarms = AlarmStore.Load();
        }

        /// <summary>Таймер «на своё время» из окна настроек.</summary>
        private void StartCustomTimer(TimeSpan total, string label)
        {
            if (total <= TimeSpan.Zero) return;
            StopRing(false);
            StartTimer(TimerKind.Countdown, total, label);
        }

        private void AlarmTick()
        {
            var now = DateTime.Now;
            var from = _alarmLast;
            _alarmLast = now;

            if (now < from) return;                                   // часы перевели назад
            if (now - from > TimeSpan.FromMinutes(10)) from = now.AddMinutes(-10);   // после сна не вспоминаем старое

            bool changed = false;
            foreach (var a in _alarms)
            {
                if (!a.On) continue;

                for (var day = from.Date; day <= now.Date; day = day.AddDays(1))
                {
                    var t = day.AddHours(a.Hour).AddMinutes(a.Minute);
                    if (t <= from || t > now || !a.RunsOn(day.DayOfWeek)) continue;

                    StartRing(string.IsNullOrWhiteSpace(a.Label) ? "Будильник" : a.Label, a.TimeText, a.Sound);
                    if (!a.Repeats) { a.On = false; changed = true; }   // одноразовый выключается сам
                    break;
                }
            }
            if (changed) AlarmStore.Save(_alarms);

            for (int i = _snoozes.Count - 1; i >= 0; i--)
            {
                var z = _snoozes[i];
                if (z.At > now) continue;
                _snoozes.RemoveAt(i);
                StartRing(z.Label, z.Value, z.Sound);
            }
        }

        // ---------- звонок ----------

        private void StartRing(string label, string value, int sound)
        {
            int sec = Math.Max(5, AppSettings.GetInt("alarm_ring", 15));

            _ringing = true;
            _ringLabel = label;
            _ringValue = value;
            _ringSound = sound;
            _ringEnd = DateTime.UtcNow.AddSeconds(sec);

            AlarmStore.Play(sound);
            _ringTick.Stop();
            _ringTick.Start();

            ShowToast("\uE121", false, Orange, label, value, Orange, -1, sec * 1000);
        }

        private void RingTick()
        {
            if (!_ringing) { _ringTick.Stop(); return; }

            if (DateTime.UtcNow >= _ringEnd)
            {
                StopRing(true);
                return;
            }
            AlarmStore.Play(_ringSound);
        }

        /// <summary>Остановить звонок; hideToast — заодно убрать плашку.</summary>
        private void StopRing(bool hideToast)
        {
            if (!_ringing) return;

            _ringing = false;
            _ringTick.Stop();

            if (hideToast)
            {
                _toastTimer.Stop();
                _toast = false;
                Apply();
            }
        }

        /// <summary>Клик по острову во время звонка.</summary>
        private void OnRingClick()
        {
            string label = _ringLabel, value = _ringValue;
            int sound = _ringSound;
            StopRing(true);

            int minutes = AppSettings.GetInt("alarm_snooze", 5);
            if (minutes <= 0) return;

            _snoozes.Add(new Snooze { At = DateTime.Now.AddMinutes(minutes), Label = label, Value = value, Sound = sound });
            ShowToast("\uE121", false, Orange, "Отложено", minutes + " мин", Orange, -1, 1800);
        }
    }
}
