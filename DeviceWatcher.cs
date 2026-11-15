using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using NAudio.CoreAudioApi;

namespace DreamIsland
{
    /// <summary>
    /// Следит за подключением наушников (аудиоустройств вывода) и флешек.
    /// Проверка раз в 2 секунды в ФОНОВОМ потоке, чтобы не подвешивать интерфейс.
    /// </summary>
    public sealed class DeviceWatcher : IDisposable
    {
        // служебные и «внутренние» устройства, о которых плашки не нужны
        private static readonly string[] IgnoreWords =
        {
            "hdmi", "displayport", "display port", "nvidia", "digital", "s/pdif", "spdif",
            "monitor", "virtual", "vb-audio", "voicemeeter", "steam streaming", "oculus", "cable"
        };

        // хвосты в названиях Bluetooth-устройств («WH-1000XM5 Stereo», «... Hands-Free»)
        private static readonly Regex Suffix = new Regex(
            @"\s*\(?(Stereo|Hands-?Free(\s+(HF|AG)\s+Audio)?|(HF|AG)\s+Audio|A2DP)\)?\s*$",
            RegexOptions.IgnoreCase);

        private Timer _timer;
        private int _busy;
        private Dictionary<string, string> _audio;
        private Dictionary<string, string> _drives;

        /// <summary>(вид: "audio" или "drive", название, подключено ли). Вызывается в UI-потоке.</summary>
        public event Action<string, string, bool> Changed;

        public void Start()
        {
            // первый проход запоминает текущие устройства (без плашек), дальше — раз в 2 секунды
            _timer = new Timer(_ => Poll(), null, 0, 2000);
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var a = ReadAudio();
                if (a != null)
                {
                    if (_audio != null) Diff(_audio, a, "audio");
                    _audio = a;
                }

                var d = ReadDrives();
                if (d != null)
                {
                    if (_drives != null) Diff(_drives, d, "drive");
                    _drives = d;
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private void Diff(Dictionary<string, string> old, Dictionary<string, string> now, string kind)
        {
            foreach (var kv in now)
                if (!old.ContainsKey(kv.Key)) Raise(kind, kv.Value, true);

            foreach (var kv in old)
                if (!now.ContainsKey(kv.Key)) Raise(kind, kv.Value, false);
        }

        private void Raise(string kind, string name, bool connected)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke(kind, name, connected)));
        }

        private static string Clean(string name)
        {
            string prev;
            do
            {
                prev = name;
                name = Suffix.Replace(name, "").Trim();
            }
            while (name != prev);
            return name;
        }

        private static Dictionary<string, string> ReadAudio()
        {
            try
            {
                var res = new Dictionary<string, string>();

                using (var en = new MMDeviceEnumerator())
                {
                    var list = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                    for (int i = 0; i < list.Count; i++)
                    {
                        using (var dev = list[i])
                        {
                            string raw = dev.DeviceFriendlyName;
                            if (string.IsNullOrWhiteSpace(raw)) raw = dev.FriendlyName;
                            if (string.IsNullOrWhiteSpace(raw)) continue;

                            string lower = raw.ToLowerInvariant();
                            bool skip = false;
                            foreach (var w in IgnoreWords)
                            {
                                if (lower.Contains(w)) { skip = true; break; }
                            }
                            if (skip) continue;

                            string name = Clean(raw);
                            if (name.Length == 0) name = raw;
                            res[name.ToLowerInvariant()] = name;
                        }
                    }
                }
                return res;
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string> ReadDrives()
        {
            try
            {
                var res = new Dictionary<string, string>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    // сначала тип (без обращения к диску), чтобы не зависать на сетевых дисках
                    if (d.DriveType != DriveType.Removable) continue;
                    if (!d.IsReady) continue;

                    string label = "";
                    try { label = d.VolumeLabel; } catch { }

                    string letter = d.Name.TrimEnd('\\');
                    res[letter] = string.IsNullOrWhiteSpace(label) ? "Диск " + letter : label + " (" + letter + ")";
                }
                return res;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            try { if (_timer != null) _timer.Dispose(); } catch { }
            _timer = null;
        }
    }
}
