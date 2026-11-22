using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DreamIsland
{
    /// <summary>Одна программа в микшере: все её звуковые сессии на всех устройствах вывода (в том числе Voicemeeter).</summary>
    internal sealed class MixerApp
    {
        public string Key, Name;
        public bool Active;        // сейчас что-то играет
        public float Volume;       // 0..1
        public bool Muted;
        internal readonly List<AudioSessionControl> Sessions = new List<AudioSessionControl>();

        public void SetVolume(float v)
        {
            v = Math.Max(0f, Math.Min(1f, v));
            foreach (var s in Sessions)
            {
                try { s.SimpleAudioVolume.Volume = v; } catch { }
            }
            Volume = v;
        }

        public void SetMuted(bool muted)
        {
            foreach (var s in Sessions)
            {
                try { s.SimpleAudioVolume.Mute = muted; } catch { }
            }
            Muted = muted;
        }
    }

    /// <summary>
    /// Снимок звуковых сессий Windows. Громкость каждой программы Windows применяет до виртуального
    /// устройства (Voicemeeter и т.п.), поэтому микшер работает и через него.
    /// Пока снимок не освобождён (Dispose), сессии внутри него можно менять.
    /// </summary>
    internal sealed class MixerScan : IDisposable
    {
        public readonly List<MixerApp> Apps = new List<MixerApp>();
        private readonly List<MMDevice> _devices = new List<MMDevice>();
        private MMDeviceEnumerator _enumerator;

        private static readonly Dictionary<string, string> Friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "javaw", "Minecraft / Java" },
            { "java", "Minecraft / Java" },
            { "Minecraft.Windows", "Minecraft" },
            { "RobloxPlayerBeta", "Roblox" },
            { "Telegram", "Telegram" },
            { "Discord", "Discord" },
            { "chrome", "Chrome" },
            { "msedge", "Edge" },
            { "firefox", "Firefox" },
            { "opera", "Opera" },
            { "yandex", "Яндекс Браузер" },
            { "Spotify", "Spotify" },
            { "vlc", "VLC" },
            { "obs64", "OBS" },
            { "steam", "Steam" },
            { "steamwebhelper", "Steam" },
            { "ts3client_win64", "TeamSpeak 3" },
            { "ts3client_win32", "TeamSpeak 3" }
        };

        public static MixerScan Run()
        {
            var scan = new MixerScan();
            try { scan.Collect(); } catch { }
            return scan;
        }

        private void Collect()
        {
            _enumerator = new MMDeviceEnumerator();
            var map = new Dictionary<string, MixerApp>();
            var names = new Dictionary<int, string>();

            // все устройства вывода, а не только устройство по умолчанию
            foreach (var dev in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _devices.Add(dev);

                SessionCollection sessions;
                try { sessions = dev.AudioSessionManager.Sessions; }
                catch { continue; }

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var s = sessions[i];
                        if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                        string key, name;
                        if (s.IsSystemSoundsSession)
                        {
                            key = "system";
                            name = "Системные звуки";
                        }
                        else
                        {
                            int pid = (int)s.GetProcessID;
                            if (pid == 0) continue;

                            string proc;
                            if (!names.TryGetValue(pid, out proc))
                            {
                                try { using (var p = Process.GetProcessById(pid)) proc = p.ProcessName; }
                                catch { proc = ""; }
                                names[pid] = proc;
                            }
                            if (proc.Length == 0) continue;

                            key = proc.ToLowerInvariant();
                            string friendly;
                            name = Friendly.TryGetValue(proc, out friendly) ? friendly : char.ToUpper(proc[0]) + proc.Substring(1);
                        }

                        MixerApp app;
                        if (!map.TryGetValue(key, out app))
                        {
                            app = new MixerApp { Key = key, Name = name };
                            app.Volume = s.SimpleAudioVolume.Volume;
                            app.Muted = s.SimpleAudioVolume.Mute;
                            map[key] = app;
                            Apps.Add(app);
                        }

                        app.Sessions.Add(s);
                        if (s.State == AudioSessionState.AudioSessionStateActive) app.Active = true;
                    }
                    catch { }
                }
            }

            // играющие сверху, дальше по алфавиту
            Apps.Sort((a, b) =>
            {
                if (a.Active != b.Active) return a.Active ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        public void Dispose()
        {
            foreach (var d in _devices)
            {
                try { d.Dispose(); } catch { }
            }
            _devices.Clear();
            try { if (_enumerator != null) _enumerator.Dispose(); } catch { }
            _enumerator = null;
        }
    }
}
