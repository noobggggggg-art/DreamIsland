using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;

namespace DreamIsland
{
    /// <summary>
    /// Статистика программ: остров замечает запуск выбранных программ по имени процесса (в программу не вмешивается),
    /// считает время за сегодня и за 7 дней и напоминает о перерыве. Список программ выбирается во вкладке «Статистика».
    /// Время хранится в настройках по дням.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class TrackedApp
        {
            public string Id, Name;
            public string[] Procs;     // имена процессов (без .exe)
            public string[] Hints;     // если не пусто — заголовок окна должен содержать одно из слов (нужно для java)
            public bool Enabled = true;
            public bool Running;
            public double Session, NextBreak;
        }

        private readonly DispatcherTimer _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly List<TrackedApp> _tracked = new List<TrackedApp>();
        private readonly Dictionary<string, double> _gameSec = new Dictionary<string, double>();   // "id|yyyyMMdd" -> секунды
        private DateTime _gameLast = DateTime.Now;
        private int _gameSaveTick;
        private int _breakMinutes = 60;   // 0 — не напоминать

        private static bool IsBuiltin(string id)
        {
            return id == "mc" || id == "rb";
        }

        // готовые программы: их нельзя удалить, только выключить
        private static List<TrackedApp> BuiltinApps()
        {
            return new List<TrackedApp>
            {
                new TrackedApp
                {
                    Id = "mc", Name = "Minecraft",
                    Procs = new[] { "javaw", "java", "Minecraft.Windows" },
                    Hints = new[] { "Minecraft", "Lunar Client", "Badlion", "Feather" }
                },
                new TrackedApp
                {
                    Id = "rb", Name = "Roblox",
                    Procs = new[] { "RobloxPlayerBeta" },
                    Hints = new string[0]
                }
            };
        }

        private void InitGames()
        {
            _breakMinutes = AppSettings.GetInt("break_min", 60);
            LoadTracked();
            foreach (var a in _tracked) LoadDays(a);

            _gameLast = DateTime.Now;
            _gameTimer.Tick += (s, e) => GameTick();
            _gameTimer.Start();
            Closed += (s, e) => SaveGames();
        }

        // ---------- список программ ----------

        private static string[] SplitList(string s)
        {
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace("~", " ").Replace("^", " ").Replace(",", " ").Replace("\r", "").Replace("\n", "").Trim();
        }

        private void LoadTracked()
        {
            _tracked.Clear();

            // формат: запись~запись, поля через ^ : id ^ имя ^ процессы ^ слова заголовка ^ включено(0/1)
            var saved = new List<string[]>();
            foreach (var entry in AppSettings.GetString("track_list", "").Split(new[] { '~' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split('^');
                if (f.Length >= 5) saved.Add(f);
            }

            foreach (var b in BuiltinApps())
            {
                foreach (var f in saved)
                    if (f[0] == b.Id) b.Enabled = f[4] == "1";
                _tracked.Add(b);
            }

            foreach (var f in saved)
            {
                if (IsBuiltin(f[0]) || f[0].Length == 0) continue;
                _tracked.Add(new TrackedApp { Id = f[0], Name = f[1], Procs = SplitList(f[2]), Hints = SplitList(f[3]), Enabled = f[4] == "1" });
            }
        }

        private void SaveTracked()
        {
            var parts = new List<string>();
            foreach (var a in _tracked)
                parts.Add(a.Id + "^" + Clean(a.Name) + "^" + string.Join(",", a.Procs) + "^" + string.Join(",", a.Hints) + "^" + (a.Enabled ? "1" : "0"));
            AppSettings.SetString("track_list", string.Join("~", parts));
        }

        private void LoadDays(TrackedApp a)
        {
            for (int i = 0; i < 7; i++)
            {
                string day = Day(i);
                int sec;
                if (int.TryParse(AppSettings.GetString("play_" + a.Id + "_" + day, ""), out sec))
                    _gameSec[a.Id + "|" + day] = sec;
            }
        }

        private void SetTracked(string id, bool enabled)
        {
            foreach (var a in _tracked)
            {
                if (a.Id != id) continue;
                a.Enabled = enabled;
                if (!enabled) { a.Running = false; a.Session = 0; }
            }
            SaveTracked();
        }

        private void RemoveTracked(string id)
        {
            if (IsBuiltin(id)) return;
            _tracked.RemoveAll(a => a.Id == id);
            SaveTracked();
        }

        private void AddTracked(string proc, string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in proc.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            string id = "p_" + sb;

            foreach (var a in _tracked)
            {
                if (a.Id != id) continue;
                a.Enabled = true;   // уже есть — просто включаем
                SaveTracked();
                return;
            }

            var app = new TrackedApp { Id = id, Name = Clean(name).Length > 0 ? Clean(name) : Clean(proc), Procs = new[] { Clean(proc) }, Hints = new string[0] };
            _tracked.Add(app);
            LoadDays(app);
            SaveTracked();
        }

        /// <summary>Запущенные программы с окнами (для выбора): [имя процесса, название]. Уже добавленные не показываются.</summary>
        private List<string[]> GetRunningApps()
        {
            var result = new List<string[]>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var skip in new[] { "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "dllhost",
                                         "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "LockApp", "taskmgr", "DreamIsland" })
                seen.Add(skip);
            foreach (var a in _tracked)
                foreach (var pr in a.Procs) seen.Add(pr);

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;

                    string proc = p.ProcessName;
                    if (!seen.Add(proc)) continue;

                    string name = null;
                    try { name = p.MainModule.FileVersionInfo.FileDescription; } catch { }
                    if (string.IsNullOrWhiteSpace(name)) name = proc;

                    result.Add(new[] { proc, name.Trim() });
                }
                catch { }
                finally { p.Dispose(); }
            }

            result.Sort((x, y) => string.Compare(x[1], y[1], StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        // ---------- подсчёт времени ----------

        private static string Day(int daysAgo)
        {
            return DateTime.Today.AddDays(-daysAgo).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        private static bool IsRunning(TrackedApp a)
        {
            foreach (var name in a.Procs)
            {
                var ps = Process.GetProcessesByName(name);
                try
                {
                    if (a.Hints.Length == 0)
                    {
                        if (ps.Length > 0) return true;
                        continue;
                    }

                    foreach (var p in ps)
                    {
                        string title = "";
                        try { title = p.MainWindowTitle ?? ""; } catch { }
                        foreach (var hint in a.Hints)
                            if (title.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
                finally
                {
                    foreach (var p in ps) p.Dispose();
                }
            }
            return false;
        }

        private void GameTick()
        {
            DateTime now = DateTime.Now;
            double dt = (now - _gameLast).TotalSeconds;
            _gameLast = now;
            if (dt < 0 || dt > 30) dt = 5;   // сон компьютера или смена времени не должны накрутить часы

            foreach (var a in _tracked)
            {
                if (!a.Enabled) continue;

                if (!IsRunning(a))
                {
                    if (a.Running)
                    {
                        a.Running = false;
                        a.Session = 0;
                        SaveGames();
                    }
                    continue;
                }

                if (!a.Running)
                {
                    a.Running = true;
                    a.Session = 0;
                    a.NextBreak = _breakMinutes * 60.0;
                }

                a.Session += dt;
                string key = a.Id + "|" + Day(0);
                double total;
                _gameSec.TryGetValue(key, out total);
                _gameSec[key] = total + dt;

                if (_breakMinutes > 0 && a.Session >= a.NextBreak)
                {
                    a.NextBreak += _breakMinutes * 60.0;
                    ShowToast("\uE121", false, Orange, "Пора сделать перерыв", a.Name + ": уже " + FormatSpan(a.Session), Orange, -1, 5000);
                }
            }

            if (++_gameSaveTick >= 12)   // раз в минуту
            {
                _gameSaveTick = 0;
                SaveGames();
            }
        }

        private void SaveGames()
        {
            string day = Day(0);
            foreach (var a in _tracked)
            {
                double sec;
                if (_gameSec.TryGetValue(a.Id + "|" + day, out sec))
                    AppSettings.SetString("play_" + a.Id + "_" + day, ((int)sec).ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string FormatSpan(double seconds)
        {
            int m = (int)(seconds / 60);
            if (m < 60) return m + " мин";
            return (m / 60) + " ч " + (m % 60).ToString("00", CultureInfo.InvariantCulture) + " мин";
        }

        private double PlayedOn(string id, string day)
        {
            double sec;
            return _gameSec.TryGetValue(id + "|" + day, out sec) ? sec : 0;
        }

        /// <summary>Данные для вкладки «Статистика».</summary>
        private List<TrackInfo> GetTracked()
        {
            var list = new List<TrackInfo>();
            foreach (var a in _tracked)
            {
                double week = 0;
                for (int i = 0; i < 7; i++) week += PlayedOn(a.Id, Day(i));

                list.Add(new TrackInfo
                {
                    Id = a.Id,
                    Name = a.Name,
                    Enabled = a.Enabled,
                    Builtin = IsBuiltin(a.Id),
                    Running = a.Running,
                    Session = a.Session,
                    Today = PlayedOn(a.Id, Day(0)),
                    Week = week
                });
            }
            return list;
        }

        private void SetBreakMinutes(int minutes)
        {
            _breakMinutes = minutes;
            AppSettings.SetString("break_min", minutes.ToString(CultureInfo.InvariantCulture));
            foreach (var a in _tracked) a.NextBreak = a.Session + minutes * 60.0;
        }
    }
}
