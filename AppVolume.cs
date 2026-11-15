using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DreamIsland
{
    /// <summary>
    /// Громкость отдельного приложения (как в «Микшере громкости» Windows), а не всей системы.
    /// Используется для SoundCloud: приложение SoundCloud или браузер, в котором он играет.
    /// </summary>
    public static class AppVolume
    {
        private static readonly string[] Browsers =
            { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "browser", "yandex", "arc" };

        /// <summary>
        /// Меняет громкость SoundCloud на delta (0.02 = 2%).
        /// </summary>
        /// <returns>false, если SoundCloud сейчас не найден</returns>
        public static bool TryAdjust(double delta, out string app, out int percent)
        {
            app = null;
            percent = 0;

            try
            {
                using (var en = new MMDeviceEnumerator())
                using (var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    var mgr = dev.AudioSessionManager;
                    mgr.RefreshSessions();
                    var list = mgr.Sessions;

                    float? target = null; // одна новая громкость на все найденные сессии
                    for (int i = 0; i < list.Count; i++)
                    {
                        var s = list[i];
                        if (s.IsSystemSoundsSession) continue;

                        string name = ProcessName((int)s.GetProcessID);
                        if (name == null || !IsTarget(name, s)) continue;

                        var v = s.SimpleAudioVolume;
                        if (target == null)
                            target = (float)Math.Max(0, Math.Min(1, v.Volume + delta));

                        v.Volume = target.Value;
                        if (delta > 0 && v.Mute) v.Mute = false;
                        app = name;
                    }

                    if (target == null) return false;
                    percent = (int)Math.Round(target.Value * 100);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTarget(string process, AudioSessionControl s)
        {
            // приложение SoundCloud
            if (process.IndexOf("soundcloud", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // SoundCloud в браузере: берём браузер, который прямо сейчас воспроизводит звук
            if (s.State == AudioSessionState.AudioSessionStateActive)
            {
                foreach (var b in Browsers)
                    if (process.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
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
