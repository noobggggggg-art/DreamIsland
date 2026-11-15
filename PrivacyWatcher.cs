using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace DreamIsland
{
    /// <summary>
    /// Показывает, какие программы СЕЙЧАС используют микрофон или камеру.
    /// Данные берутся из журнала приватности Windows (раздел «Конфиденциальность» в параметрах).
    /// Проверка раз в 1,5 секунды в фоновом потоке.
    /// </summary>
    public sealed class PrivacyWatcher : IDisposable
    {
        private const string Base =
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        private Timer _timer;
        private int _busy;
        private string _lastMic = null;
        private string _lastCam = null;

        /// <summary>(программы с микрофоном, программы с камерой). Вызывается в UI-потоке.</summary>
        public event Action<List<string>, List<string>> Changed;

        public void Start()
        {
            Stop();
            _lastMic = null;
            _lastCam = null;
            _timer = new Timer(_ => Poll(), null, 0, 1500);
        }

        public void Stop()
        {
            try { if (_timer != null) _timer.Dispose(); } catch { }
            _timer = null;
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var mic = ReadUsers("microphone");
                var cam = ReadUsers("webcam");

                string km = string.Join("|", mic);
                string kc = string.Join("|", cam);

                if (km != _lastMic || kc != _lastCam)
                {
                    _lastMic = km;
                    _lastCam = kc;

                    var app = Application.Current;
                    if (app != null)
                    {
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            var h = Changed;
                            if (h != null) h(mic, cam);
                        }));
                    }
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static List<string> ReadUsers(string capability)
        {
            var res = new List<string>();

            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(Base + capability))
                {
                    if (root == null) return res;

                    Scan(root, false, res);
                    using (var np = root.OpenSubKey("NonPackaged"))
                    {
                        if (np != null) Scan(np, true, res);
                    }
                }
            }
            catch { }

            res.Sort(StringComparer.OrdinalIgnoreCase);
            return res;
        }

        private static void Scan(RegistryKey parent, bool nonPackaged, List<string> res)
        {
            foreach (var name in parent.GetSubKeyNames())
            {
                if (!nonPackaged && name == "NonPackaged") continue;

                using (var k = parent.OpenSubKey(name))
                {
                    if (k == null) continue;

                    long start = ToLong(k.GetValue("LastUsedTimeStart"));
                    long stop = ToLong(k.GetValue("LastUsedTimeStop"));

                    // «начало» позже «конца» (конец = 0) — значит, используется прямо сейчас
                    if (start > stop)
                    {
                        string pretty = Pretty(name, nonPackaged);
                        if (!pretty.Equals("DreamIsland", StringComparison.OrdinalIgnoreCase) && !res.Contains(pretty))
                            res.Add(pretty);
                    }
                }
            }
        }

        private static long ToLong(object v)
        {
            if (v is long) return (long)v;
            var b = v as byte[];
            if (b != null && b.Length >= 8) return BitConverter.ToInt64(b, 0);
            return 0;
        }

        private static string Pretty(string name, bool nonPackaged)
        {
            if (nonPackaged)
            {
                // C:#Program Files#Discord#Discord.exe  ->  Discord
                int i = name.LastIndexOf('#');
                string file = i >= 0 ? name.Substring(i + 1) : name;
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    file = file.Substring(0, file.Length - 4);
                return file;
            }

            // Microsoft.WindowsCamera_8wekyb3d8bbwe  ->  WindowsCamera
            int u = name.IndexOf('_');
            string pkg = u > 0 ? name.Substring(0, u) : name;
            int dot = pkg.LastIndexOf('.');
            return dot >= 0 && dot < pkg.Length - 1 ? pkg.Substring(dot + 1) : pkg;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
