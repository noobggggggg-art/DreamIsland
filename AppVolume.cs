using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DreamIsland
{
    /// <summary>
    /// Громкость отдельных приложений (как в «Микшере громкости» Windows), а не всей системы:
    /// SoundCloud (приложение или браузер), TeamSpeak 3, браузер.
    /// Ищет приложение на ВСЕХ устройствах вывода (колонки, наушники, гарнитура), а не только на основном.
    /// </summary>
    public static class AppVolume
    {
        private static readonly string[] Browsers =
            { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "browser", "yandex", "arc" };

        // ---------- кто нужен ----------

        public static bool IsBrowserName(string process)
        {
            foreach (var b in Browsers)
                if (process.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>SoundCloud: его приложение или браузер, который прямо сейчас воспроизводит звук.</summary>
        public static bool IsSoundCloud(string process, AudioSessionControl s)
        {
            if (process.IndexOf("soundcloud", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return IsBrowserName(process) && s.State == AudioSessionState.AudioSessionStateActive;
        }

        /// <summary>TeamSpeak 3 (ts3client_win64 / ts3client_win32).</summary>
        public static bool IsTeamSpeak(string process, AudioSessionControl s)
        {
            return process.IndexOf("ts3client", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Запущен ли клиент TeamSpeak 3 (независимо от звука).</summary>
        public static bool IsTeamSpeakRunning()
        {
            foreach (var name in new[] { "ts3client_win64", "ts3client_win32" })
            {
                try
                {
                    var list = Process.GetProcessesByName(name);
                    bool any = list.Length > 0;
                    foreach (var p in list) p.Dispose();
                    if (any) return true;
                }
                catch { }
            }
            return false;
        }

        // ---------- обход всех устройств ----------

        private static void ForEachSession(Action<AudioSessionControl> action)
        {
            using (var en = new MMDeviceEnumerator())
            {
                var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (int d = 0; d < devices.Count; d++)
                {
                    try
                    {
                        using (var dev = devices[d])
                        {
                            var mgr = dev.AudioSessionManager;
                            mgr.RefreshSessions();
                            var list = mgr.Sessions;
                            for (int i = 0; i < list.Count; i++) action(list[i]);
                        }
                    }
                    catch
                    {
                        // это устройство недоступно — пробуем следующее
                    }
                }
            }
        }

        // ---------- изменить ----------

        public static bool TryAdjust(double delta, out string app, out int percent)
        {
            return TryAdjust(IsSoundCloud, delta, out app, out percent);
        }

        /// <summary>Меняет громкость подходящих приложений на delta (0.02 = 2%).</summary>
        /// <returns>false, если подходящего приложения сейчас нет</returns>
        public static bool TryAdjust(Func<string, AudioSessionControl, bool> match, double delta,
                                     out string app, out int percent)
        {
            string foundApp = null;
            float? target = null;   // одна новая громкость на все найденные сессии

            try
            {
                ForEachSession(s =>
                {
                    if (s.IsSystemSoundsSession) return;

                    string name = ProcessName((int)s.GetProcessID);
                    if (name == null || !match(name, s)) return;

                    var v = s.SimpleAudioVolume;
                    if (target == null)
                        target = (float)Math.Max(0, Math.Min(1, v.Volume + delta));

                    v.Volume = target.Value;
                    if (delta > 0 && v.Mute) v.Mute = false;
                    foundApp = name;
                });
            }
            catch { }

            app = foundApp;
            percent = target == null ? 0 : (int)Math.Round(target.Value * 100);
            return target != null;
        }

        // ---------- прочитать ----------

        /// <summary>Громкость браузера (активного, а если нет — любого) для маленького индикатора.</summary>
        public static bool TryReadBrowser(out int percent, out bool muted)
        {
            bool found = false, foundActive = false;
            float vol = 0;
            bool mute = false;

            try
            {
                ForEachSession(s =>
                {
                    if (s.IsSystemSoundsSession) return;

                    string name = ProcessName((int)s.GetProcessID);
                    if (name == null || !IsBrowserName(name)) return;

                    bool active = s.State == AudioSessionState.AudioSessionStateActive;
                    if (found && foundActive && !active) return;   // активная сессия важнее

                    var v = s.SimpleAudioVolume;
                    if (!found || (active && !foundActive) || v.Volume > vol)
                    {
                        vol = v.Volume;
                        mute = v.Mute;
                    }
                    found = true;
                    foundActive = foundActive || active;
                });
            }
            catch { }

            percent = found ? (int)Math.Round(vol * 100) : 0;
            muted = found && mute;
            return found;
        }

        private static string ProcessName(int pid)
        {
            if (pid <= 0) return null;
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return p.ProcessName;
            }
            catch
            {
                return null;
            }
        }
    }
}
