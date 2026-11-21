using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;

namespace DreamIsland
{
    /// <summary>Одна идущая загрузка.</summary>
    public sealed class DownloadInfo
    {
        public string Id;          // steam:730 или epic:AppName
        public string Source;      // "Steam" или "Epic"
        public string Name;
        public double Percent;     // 0..100
        public double SpeedBps;    // байт в секунду, 0 — пока неизвестно
    }

    /// <summary>
    /// Каждые 2 секунды смотрит, качается ли что-то в Steam или Epic Games Launcher.
    /// Ничего не трогает и не подключается к сети: читает только файлы самих лаунчеров.
    ///
    /// Steam: appmanifest_*.acf в папках библиотек (BytesDownloaded / BytesToDownload) — точные проценты.
    /// Epic: официального файла с прогрессом у лаунчера нет, поэтому проценты оценочные — по размеру папки
    /// недоустановленной игры относительно её полного размера.
    /// </summary>
    public sealed class DownloadWatcher : IDisposable
    {
        public event Action<List<DownloadInfo>> Updated;

        private Thread _thread;
        private volatile bool _stop;

        private readonly Dictionary<string, long[]> _prev = new Dictionary<string, long[]>();   // id → { байты, время }
        private readonly Dictionary<string, double> _speed = new Dictionary<string, double>();
        private readonly Dictionary<string, long[]> _epicSize = new Dictionary<string, long[]>(); // id → { размер папки, время замера }

        private static readonly Regex Kv = new Regex("\"([^\"]+)\"\\s+\"([^\"]*)\"", RegexOptions.Compiled);

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "DreamIsland.Downloads", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            var t = _thread;
            _thread = null;
            if (t != null) t.Join(2500);
            _prev.Clear();
            _speed.Clear();
            _epicSize.Clear();
        }

        public void Dispose()
        {
            Stop();
        }

        private void Run()
        {
            while (!_stop)
            {
                var list = new List<DownloadInfo>();
                try { PollSteam(list); } catch { }
                try { PollEpic(list); } catch { }

                // забыть тех, кого больше нет (чтобы скорость не считалась от старого замера)
                var alive = new HashSet<string>();
                foreach (var d in list) alive.Add(d.Id);
                var gone = new List<string>();
                foreach (var k in _prev.Keys) if (!alive.Contains(k)) gone.Add(k);
                foreach (var k in gone) { _prev.Remove(k); _speed.Remove(k); _epicSize.Remove(k); }

                var handler = Updated;
                if (handler != null && !_stop) handler(list);

                for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
            }
        }

        // ---------- скорость по двум замерам ----------

        private double Speed(string id, long bytes)
        {
            long now = Environment.TickCount64;
            long[] p;
            double s = 0;
            _speed.TryGetValue(id, out s);

            if (_prev.TryGetValue(id, out p))
            {
                double dt = (now - p[1]) / 1000.0;
                long db = bytes - p[0];
                if (dt >= 1.0)
                {
                    double inst = db > 0 ? db / dt : 0;
                    s = s <= 0 ? inst : s * 0.5 + inst * 0.5;   // сглаживание
                    _prev[id] = new[] { bytes, now };
                }
            }
            else
            {
                _prev[id] = new[] { bytes, now };
            }

            _speed[id] = s;
            return s;
        }

        // ---------- Steam ----------

        private static string SteamRoot()
        {
            try
            {
                var v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return v.Replace('/', '\\');
            }
            catch { }

            string def = @"C:\Program Files (x86)\Steam";
            return Directory.Exists(def) ? def : null;
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                return sr.ReadToEnd();
        }

        private static long L(Dictionary<string, string> kv, string key)
        {
            string v;
            long n;
            return kv.TryGetValue(key, out v) && long.TryParse(v, out n) ? n : 0;
        }

        private void PollSteam(List<DownloadInfo> into)
        {
            string root = SteamRoot();
            if (root == null) return;

            var libs = new List<string> { root };
            try
            {
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Kv.Matches(ReadShared(vdf)))
                        if (m.Groups[1].Value == "path")
                        {
                            string p = m.Groups[2].Value.Replace("\\\\", "\\");
                            if (Directory.Exists(p) && !libs.Contains(p)) libs.Add(p);
                        }
            }
            catch { }

            foreach (var lib in libs)
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;

                foreach (var file in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    var kv = new Dictionary<string, string>();
                    try
                    {
                        foreach (Match m in Kv.Matches(ReadShared(file)))
                            if (!kv.ContainsKey(m.Groups[1].Value)) kv[m.Groups[1].Value] = m.Groups[2].Value;
                    }
                    catch { continue; }

                    // флаги Steam: 256 — идёт обновление, 1024 — обновление запущено, 512 — на паузе
                    long flags = L(kv, "StateFlags");
                    if ((flags & (256 | 1024)) == 0 || (flags & 512) != 0) continue;

                    long total = L(kv, "BytesToDownload"), done = L(kv, "BytesDownloaded");
                    if (total <= 0 || done <= 0) continue;   // в очереди и ещё не начала качаться

                    string id = "steam:" + L(kv, "appid");
                    string name;
                    if (!kv.TryGetValue("name", out name) || name.Length == 0) name = "Игра Steam";

                    into.Add(new DownloadInfo
                    {
                        Id = id,
                        Source = "Steam",
                        Name = name,
                        Percent = Math.Min(100.0, done * 100.0 / total),
                        SpeedBps = Speed(id, done)
                    });
                }
            }
        }

        // ---------- Epic Games Launcher ----------

        private void PollEpic(List<DownloadInfo> into)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                      "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(dir)) return;
            if (Process.GetProcessesByName("EpicGamesLauncher").Length == 0) return;

            foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
            {
                string loc = null, name = null, app = null;
                long size = 0;

                try
                {
                    using (var doc = JsonDocument.Parse(ReadShared(file)))
                    {
                        var r = doc.RootElement;
                        JsonElement e;

                        // игра ещё не доустановлена — именно такие и качаются прямо сейчас
                        if (!r.TryGetProperty("bIsIncompleteInstall", out e) || e.ValueKind != JsonValueKind.True) continue;

                        if (r.TryGetProperty("InstallLocation", out e) && e.ValueKind == JsonValueKind.String) loc = e.GetString();
                        if (r.TryGetProperty("DisplayName", out e) && e.ValueKind == JsonValueKind.String) name = e.GetString();
                        if (r.TryGetProperty("AppName", out e) && e.ValueKind == JsonValueKind.String) app = e.GetString();
                        if (r.TryGetProperty("InstallSize", out e) && e.ValueKind == JsonValueKind.Number) size = e.GetInt64();
                    }
                }
                catch { continue; }

                if (string.IsNullOrEmpty(loc) || size <= 0 || !Directory.Exists(loc)) continue;

                string id = "epic:" + (app ?? Path.GetFileNameWithoutExtension(file));

                // размер папки считаем не чаще раза в 4 секунды: у больших игр много файлов
                long now = Environment.TickCount64;
                long[] c;
                long bytes;
                if (_epicSize.TryGetValue(id, out c) && now - c[1] < 4000) bytes = c[0];
                else { bytes = DirSize(loc); _epicSize[id] = new[] { bytes, now }; }

                if (bytes <= 0) continue;

                into.Add(new DownloadInfo
                {
                    Id = id,
                    Source = "Epic",
                    Name = string.IsNullOrEmpty(name) ? (app ?? "Игра Epic") : name,
                    Percent = Math.Min(99.0, bytes * 100.0 / size),
                    SpeedBps = Speed(id, bytes)
                });
            }
        }

        private static long DirSize(string root)
        {
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                string d = stack.Pop();
                try
                {
                    foreach (var f in new DirectoryInfo(d).EnumerateFiles()) total += f.Length;
                    foreach (var s in Directory.EnumerateDirectories(d)) stack.Push(s);
                }
                catch { }
            }

            return total;
        }
    }
}
