using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DreamIsland
{
    /// <summary>Вкладка «Будильник»: будильники на время суток, таймер на своё время и настройки звонка.</summary>
    public sealed partial class SettingsWindow
    {
        /// <summary>Запустить таймер на острове (даёт главное окно).</summary>
        public Action<TimeSpan, string> StartCountdown;

        /// <summary>Список будильников изменился — главное окно перечитывает его.</summary>
        public Action AlarmsChanged;

        private List<AlarmItem> _alarmList = new List<AlarmItem>();
        private AlarmItem _edit;            // будильник в редакторе (копия, пока не нажали «Сохранить»)
        private int _editIndex = -1;        // -1 — новый

        // ---------- «числовое колёсико»: стрелки, колёсико мыши, ввод цифрами ----------

        private sealed class Stepper
        {
            public int Value, Min, Max;
            public bool Wrap;
            public TextBox Box;
            public Action Changed;

            public void Set(int v, bool notify)
            {
                if (Wrap)
                {
                    int span = Max - Min + 1;
                    v = ((v - Min) % span + span) % span + Min;
                }
                else v = Math.Max(Min, Math.Min(Max, v));

                Value = v;
                if (Box != null) Box.Text = v.ToString("00");
                if (notify && Changed != null) Changed();
            }
        }

        /// <summary>Стрелка с повтором при удержании кнопки мыши.</summary>
        private static Border Arrow(string glyph, Action step)
        {
            var tb = T(glyph, 11, Dim, FontWeights.Normal);
            tb.HorizontalAlignment = HorizontalAlignment.Center;

            var b = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 5, 20, 5),
                Child = tb,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var repeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            repeat.Tick += (s, e) => { repeat.Interval = TimeSpan.FromMilliseconds(70); step(); };

            b.MouseEnter += (s, e) => { b.Background = GlassFill; tb.Foreground = White; };
            b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; tb.Foreground = Dim; };
            b.MouseLeftButtonDown += (s, e) =>
            {
                step();
                repeat.Interval = TimeSpan.FromMilliseconds(420);
                repeat.Start();
                b.CaptureMouse();
                e.Handled = true;
            };
            b.MouseLeftButtonUp += (s, e) => { repeat.Stop(); b.ReleaseMouseCapture(); e.Handled = true; };
            b.LostMouseCapture += (s, e) => repeat.Stop();
            return b;
        }

        private UIElement BuildStepper(Stepper st, string unit, double size)
        {
            st.Box = new TextBox
            {
                Width = size * 2.0,
                FontSize = size,
                FontWeight = FontWeights.Light,
                Foreground = White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                CaretBrush = White,
                SelectionBrush = Purple,
                TextAlignment = TextAlignment.Center,
                MaxLength = 2,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            st.Set(st.Value, false);

            Action commit = () =>
            {
                int v;
                if (int.TryParse(st.Box.Text, out v)) st.Set(v, true);
                else st.Set(st.Value, false);
            };

            st.Box.PreviewTextInput += (s, e) =>
            {
                foreach (char ch in e.Text)
                    if (!char.IsDigit(ch)) { e.Handled = true; return; }
            };
            st.Box.GotKeyboardFocus += (s, e) => Dispatcher.BeginInvoke(new Action(() => st.Box.SelectAll()), DispatcherPriority.Input);
            st.Box.LostKeyboardFocus += (s, e) => commit();
            st.Box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { commit(); e.Handled = true; }
                else if (e.Key == Key.Up) { st.Set(st.Value + 1, true); e.Handled = true; }
                else if (e.Key == Key.Down) { st.Set(st.Value - 1, true); e.Handled = true; }
            };

            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(Arrow("\u25B2", () => st.Set(st.Value + 1, true)));
            col.Children.Add(st.Box);
            col.Children.Add(Arrow("\u25BC", () => st.Set(st.Value - 1, true)));
            if (unit != null)
            {
                var u = T(unit, 11.5, Dim, FontWeights.Normal);
                u.HorizontalAlignment = HorizontalAlignment.Center;
                u.Margin = new Thickness(0, 2, 0, 0);
                col.Children.Add(u);
            }

            col.MouseWheel += (s, e) =>
            {
                st.Set(st.Value + (e.Delta > 0 ? 1 : -1), true);
                e.Handled = true;   // колёсико крутит число, а не страницу
            };
            return col;
        }

        // ---------- страница ----------

        private UIElement PageAlarm()
        {
            var p = new StackPanel();
            _alarmList = AlarmStore.Load();

            p.Children.Add(Section("Будильники"));
            if (_edit != null)
            {
                p.Children.Add(BuildAlarmEditor());
            }
            else
            {
                if (_alarmList.Count == 0)
                    p.Children.Add(Hint("Пока пусто. Поставьте время — остров зазвонит, даже если вы в игре или в другом окне."));
                for (int i = 0; i < _alarmList.Count; i++)
                    p.Children.Add(BuildAlarmCard(i));

                p.Children.Add(Chips(Chip("+ Новый будильник", () =>
                {
                    var soon = DateTime.Now.AddHours(1);
                    _edit = new AlarmItem { Hour = soon.Hour, Minute = 0, On = true };
                    _editIndex = -1;
                    Rebuild();
                }, false)));
            }

            p.Children.Add(Section("Таймер на своё время"));
            p.Children.Add(BuildTimerCard());

            p.Children.Add(Section("Звонок"));
            p.Children.Add(Choice("Сколько звонить",
                new[] { "5 сек", "15 сек", "30 сек", "60 сек" }, new[] { 5, 15, 30, 60 },
                () => AppSettings.GetInt("alarm_ring", 15), v => AppSettings.SetString("alarm_ring", v.ToString())));
            p.Children.Add(Choice("Клик по острову во время звонка",
                new[] { "Выключает", "Отложит на 5 мин", "Отложит на 10 мин" }, new[] { 0, 5, 10 },
                () => AppSettings.GetInt("alarm_snooze", 5), v => AppSettings.SetString("alarm_snooze", v.ToString())));
            p.Children.Add(Choice("Мелодия таймера (нажмите, чтобы послушать)",
                AlarmStore.SoundNames, new[] { 0, 1, 2, 3, 4 },
                () => AppSettings.GetInt("timer_sound", 0),
                v => { AppSettings.SetString("timer_sound", v.ToString()); AlarmStore.Play(v); }));
            return p;
        }

        private void SaveAlarms()
        {
            AlarmStore.Save(_alarmList);
            if (AlarmsChanged != null) AlarmsChanged();
        }

        /// <summary>Перестраивает вкладку, но оставляет прокрутку на месте.</summary>
        private void RebuildKeep()
        {
            double off = _scroll.VerticalOffset;
            Rebuild();
            Dispatcher.BeginInvoke(new Action(() => _scroll.ScrollToVerticalOffset(off)), DispatcherPriority.Loaded);
        }

        private static string WhenText(AlarmItem a)
        {
            var now = DateTime.Now;
            var next = AlarmStore.Next(a, now);
            if (next == null) return "";

            var t = next.Value;
            string day = t.Date == now.Date ? "сегодня"
                       : t.Date == now.Date.AddDays(1) ? "завтра"
                       : t.ToString("dddd", RuCulture);
            return "Сработает " + day + " в " + a.TimeText + "  ·  через " + AlarmStore.Until(t, now);
        }

        // ---------- карточка будильника в списке ----------

        private UIElement BuildAlarmCard(int index)
        {
            var a = _alarmList[index];

            var time = T(a.TimeText, 30, White, FontWeights.Light);
            time.VerticalAlignment = VerticalAlignment.Center;
            time.Margin = new Thickness(0, 0, 18, 0);

            var title = T(string.IsNullOrWhiteSpace(a.Label) ? "Будильник" : a.Label, 14, White, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.TextWrapping = TextWrapping.NoWrap;
            var sub = T("", 12, Dim, FontWeights.Normal);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sub.TextWrapping = TextWrapping.NoWrap;

            // включённый будильник светится янтарём — это главный элемент строки
            Action refresh = () =>
            {
                time.Foreground = a.On ? White : Dim;
                time.Effect = a.On ? GlowAmber : null;
                title.Opacity = a.On ? 1 : 0.6;

                var next = AlarmStore.Next(a, DateTime.Now);
                sub.Text = AlarmStore.DaysText(a.Days) + (a.On && next != null
                    ? "  ·  через " + AlarmStore.Until(next.Value, DateTime.Now)
                    : "  ·  выключен");
            };
            refresh();

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(title);
            info.Children.Add(sub);

            var sw = Switch(a.On, on =>
            {
                a.On = on;
                SaveAlarms();
                refresh();
            });
            sw.VerticalAlignment = VerticalAlignment.Center;

            var del = Chip("\u2715", () =>
            {
                _alarmList.RemoveAt(index);
                SaveAlarms();
                RebuildKeep();
            }, false);
            del.Margin = new Thickness(10, 0, 0, 0);
            del.Padding = new Thickness(9, 5, 9, 5);
            del.VerticalAlignment = VerticalAlignment.Center;
            del.MouseLeftButtonUp += (s, e) => e.Handled = true;   // клик по крестику не открывает редактор

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(time);
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);
            Grid.SetColumn(sw, 2);
            grid.Children.Add(sw);
            Grid.SetColumn(del, 3);
            grid.Children.Add(del);

            var row = RowBox(grid);
            row.Padding = new Thickness(16, 8, 10, 8);
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (s, e) => { row.BorderBrush = GlassEdgeHot; };
            row.MouseLeave += (s, e) => { row.BorderBrush = GlassEdge; };
            row.MouseLeftButtonUp += (s, e) =>
            {
                _edit = a.Copy();
                _editIndex = index;
                Rebuild();
            };
            return row;
        }

        // ---------- редактор будильника ----------

        private UIElement BuildAlarmEditor()
        {
            var a = _edit;
            var box = new StackPanel();

            box.Children.Add(T(_editIndex < 0 ? "Новый будильник" : "Изменить будильник", 14, White, FontWeights.SemiBold));

            // время: крупные числа на тёплом свечении
            var preview = T("", 12.5, AmberSoft, FontWeights.Normal);
            preview.HorizontalAlignment = HorizontalAlignment.Center;
            preview.Margin = new Thickness(0, 4, 0, 0);

            var hh = new Stepper { Min = 0, Max = 23, Wrap = true, Value = a.Hour };
            var mm = new Stepper { Min = 0, Max = 59, Wrap = true, Value = a.Minute };
            Action changed = () =>
            {
                a.Hour = hh.Value;
                a.Minute = mm.Value;
                preview.Text = WhenText(a);
            };
            hh.Changed = changed;
            mm.Changed = changed;

            var clock = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            clock.Children.Add(BuildStepper(hh, "часы", 40));
            var colon = T(":", 40, White, FontWeights.Light);
            colon.VerticalAlignment = VerticalAlignment.Center;
            colon.Margin = new Thickness(6, 0, 6, 16);
            clock.Children.Add(colon);
            clock.Children.Add(BuildStepper(mm, "минуты", 40));

            var focal = new Border
            {
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(0, 8, 0, 10),
                Background = new RadialGradientBrush(Cl(0x30FF9F0A), Cl(0x00FF9F0A))
                {
                    Center = new Point(0.5, 0.45),
                    GradientOrigin = new Point(0.5, 0.45),
                    RadiusX = 0.55,
                    RadiusY = 0.75
                }
            };
            var focalBody = new StackPanel();
            focalBody.Children.Add(clock);
            focalBody.Children.Add(preview);
            focal.Child = focalBody;
            box.Children.Add(focal);
            changed();

            // повтор по дням
            var daysTitle = T("Повтор", 13, Dim, FontWeights.SemiBold);
            daysTitle.Margin = new Thickness(0, 12, 0, 0);
            box.Children.Add(daysTitle);

            var dayChips = new List<Border>();
            var dayWrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            Action repaintDays = () =>
            {
                for (int j = 0; j < dayChips.Count; j++)
                {
                    dayChips[j].Tag = (a.Days & (1 << j)) != 0;
                    PaintChip(dayChips[j]);
                }
            };
            for (int i = 0; i < 7; i++)
            {
                int bit = 1 << i;
                var chip = Chip(AlarmStore.DayName(i), () => { a.Days ^= bit; repaintDays(); }, (a.Days & bit) != 0);
                chip.Padding = new Thickness(11, 7, 11, 7);
                chip.Margin = new Thickness(0, 0, 6, 6);
                dayChips.Add(chip);
                dayWrap.Children.Add(chip);
            }
            box.Children.Add(dayWrap);

            var quick = new WrapPanel();
            quick.Children.Add(Chip("Один раз", () => { a.Days = 0; repaintDays(); }, false));
            quick.Children.Add(Chip("По будням", () => { a.Days = 31; repaintDays(); }, false));
            quick.Children.Add(Chip("Каждый день", () => { a.Days = 127; repaintDays(); }, false));
            box.Children.Add(quick);
            box.Children.Add(Hint("Если дни не выбраны, будильник сработает один раз и выключится сам."));

            // подпись
            box.Children.Add(T("Подпись (необязательно)", 13, Dim, FontWeights.SemiBold));
            box.Children.Add(TextField(a.Label, 40, t => a.Label = t));

            // мелодия: нажатие сразу проигрывает её
            var soundTitle = T("Мелодия", 13, Dim, FontWeights.SemiBold);
            soundTitle.Margin = new Thickness(0, 12, 0, 0);
            box.Children.Add(soundTitle);

            var soundChips = new List<Border>();
            var soundWrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            for (int i = 0; i < AlarmStore.SoundNames.Length; i++)
            {
                int me = i;
                var chip = Chip(AlarmStore.SoundNames[i], () =>
                {
                    a.Sound = me;
                    for (int j = 0; j < soundChips.Count; j++)
                    {
                        soundChips[j].Tag = j == me;
                        PaintChip(soundChips[j]);
                    }
                    AlarmStore.Play(me);
                }, i == a.Sound);
                soundChips.Add(chip);
                soundWrap.Children.Add(chip);
            }
            box.Children.Add(soundWrap);

            // кнопки
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(Chip("Сохранить", () =>
            {
                a.On = true;
                if (_editIndex < 0) _alarmList.Add(a);
                else if (_editIndex < _alarmList.Count) _alarmList[_editIndex] = a;

                SaveAlarms();
                _edit = null;
                Rebuild();
            }, true));
            actions.Children.Add(Chip("Отмена", () => { _edit = null; Rebuild(); }, false));
            box.Children.Add(actions);

            return RowBox(box);
        }

        // ---------- таймер на своё время ----------

        private UIElement BuildTimerCard()
        {
            int h = 0, m = 10, s = 0;
            try
            {
                var parts = AppSettings.GetString("timer_custom", "0:10:0").Split(':');
                h = Math.Max(0, Math.Min(23, int.Parse(parts[0])));
                m = Math.Max(0, Math.Min(59, int.Parse(parts[1])));
                s = Math.Max(0, Math.Min(59, int.Parse(parts[2])));
            }
            catch { h = 0; m = 10; s = 0; }

            var endsAt = T("", 12.5, AmberSoft, FontWeights.Normal);
            endsAt.HorizontalAlignment = HorizontalAlignment.Center;
            endsAt.Margin = new Thickness(0, 4, 0, 0);

            var sh = new Stepper { Min = 0, Max = 23, Value = h };
            var sm = new Stepper { Min = 0, Max = 59, Value = m };
            var ss = new Stepper { Min = 0, Max = 59, Value = s };

            Func<TimeSpan> total = () => new TimeSpan(sh.Value, sm.Value, ss.Value);
            Action update = () =>
            {
                var t = total();
                endsAt.Text = t <= TimeSpan.Zero
                    ? "Задайте время"
                    : "Закончится в " + DateTime.Now.Add(t).ToString("HH:mm:ss");
            };
            sh.Changed = update;
            sm.Changed = update;
            ss.Changed = update;

            var clock = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            clock.Children.Add(BuildStepper(sh, "часы", 34));
            clock.Children.Add(Colon());
            clock.Children.Add(BuildStepper(sm, "минуты", 34));
            clock.Children.Add(Colon());
            clock.Children.Add(BuildStepper(ss, "секунды", 34));

            var focal = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(0, 8, 0, 10),
                Background = new RadialGradientBrush(Cl(0x30FF9F0A), Cl(0x00FF9F0A))
                {
                    Center = new Point(0.5, 0.45),
                    GradientOrigin = new Point(0.5, 0.45),
                    RadiusX = 0.55,
                    RadiusY = 0.75
                }
            };
            var focalBody = new StackPanel();
            focalBody.Children.Add(clock);
            focalBody.Children.Add(endsAt);
            focal.Child = focalBody;
            update();

            // быстрое добавление времени
            Action<int> add = secs =>
            {
                int sum = Math.Max(0, Math.Min(23 * 3600 + 59 * 60 + 59, (int)total().TotalSeconds + secs));
                sh.Set(sum / 3600, false);
                sm.Set(sum / 60 % 60, false);
                ss.Set(sum % 60, false);
                update();
            };
            var quick = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            quick.Children.Add(Chip("+1 мин", () => add(60), false));
            quick.Children.Add(Chip("+5 мин", () => add(300), false));
            quick.Children.Add(Chip("+15 мин", () => add(900), false));
            quick.Children.Add(Chip("+1 час", () => add(3600), false));
            quick.Children.Add(Chip("Сбросить", () => add(-24 * 3600), false));

            string label = "";
            var nameTitle = T("Название (необязательно)", 13, Dim, FontWeights.SemiBold);
            nameTitle.Margin = new Thickness(0, 4, 0, 0);

            var go = Chip("Запустить таймер", () =>
            {
                var t = total();
                if (t <= TimeSpan.Zero) { update(); return; }

                AppSettings.SetString("timer_custom", sh.Value + ":" + sm.Value + ":" + ss.Value);
                string name = label.Trim();
                CloseAnimated(() => { if (StartCountdown != null) StartCountdown(t, name); });
            }, true);

            var box = new StackPanel();
            box.Children.Add(focal);
            box.Children.Add(quick);
            box.Children.Add(nameTitle);
            box.Children.Add(TextField("", 30, t => label = t));
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(go);
            box.Children.Add(actions);
            return RowBox(box);
        }

        private static TextBlock Colon()
        {
            var c = T(":", 34, White, FontWeights.Light);
            c.VerticalAlignment = VerticalAlignment.Center;
            c.Margin = new Thickness(4, 0, 4, 16);
            return c;
        }
    }
}
