using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace DreamIsland
{
    /// <summary>
    /// Громкость динамиков и состояние микрофонов (mute / нулевая громкость).
    /// Следит за всеми активными микрофонами + запасной опрос раз в секунду.
    /// </summary>
    public sealed class AudioWatcher : IDisposable
    {
        private MMDeviceEnumerator _enum;
        private MMDevice _render;
        private readonly List<MMDevice> _mics = new List<MMDevice>();
        private readonly Dictionary<string, bool> _micState = new Dictionary<string, bool>();
        private readonly object _lock = new object();
        private DispatcherTimer _poll;

        private int _lastVol = -1;
        private bool _lastMuted;
        private string _defaultMicId;

        /// <summary>Выключен ли микрофон по умолчанию (средствами Windows)</summary>
        public bool MicMuted { get; private set; }

        /// <summary>Громкость динамиков в процентах (-1, если ещё неизвестна)</summary>
        public int Volume { get { return _lastVol; } }

        /// <summary>Выключен ли звук в системе</summary>
        public bool Muted { get { return _lastMuted; } }

        /// <summary>(громкость 0..100, выключен ли звук)</summary>
        public event Action<int, bool> VolumeChanged;

        /// <summary>true = микрофон выключен</summary>
        public event Action<bool> MicMuteChanged;

        public void Start()
        {
            try
            {
                _enum = new MMDeviceEnumerator();

                // динамики (громкость)
                if (_enum.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    _render = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _lastVol = (int)Math.Round(_render.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                    _lastMuted = _render.AudioEndpointVolume.Mute;
                    _render.AudioEndpointVolume.OnVolumeNotification += OnRender;
                }

                // все активные микрофоны
                var caps = _enum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                for (int i = 0; i < caps.Count; i++)
                {
                    var dev = caps[i];
                    string id = dev.ID;
                    try
                    {
                        _micState[id] = IsMuted(dev);
                        dev.AudioEndpointVolume.OnVolumeNotification += d => OnCapture(id, d.Muted || d.MasterVolume <= 0.001f);
                        _mics.Add(dev);
                    }
                    catch { }
                }

                // микрофон по умолчанию — для постоянного индикатора
                if (_enum.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
                {
                    using (var d = _enum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
                    {
                        _defaultMicId = d.ID;
                        MicMuted = IsMuted(d);
                    }
                }

                // запасной опрос — некоторые драйверы не присылают события
                _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _poll.Tick += (s, e) => Poll();
                _poll.Start();
            }
            catch
            {
                // нет аудиоустройств — просто ничего не показываем
            }
        }

        private static bool IsMuted(MMDevice dev)
        {
            var v = dev.AudioEndpointVolume;
            return v.Mute || v.MasterVolumeLevelScalar <= 0.001f;
        }

        private void Poll()
        {
            foreach (var dev in _mics)
            {
                try { OnCapture(dev.ID, IsMuted(dev)); }
                catch { }
            }
        }

        private void OnRender(AudioVolumeNotificationData data)
        {
            int vol = (int)Math.Round(data.MasterVolume * 100);
            bool muted = data.Muted;
            if (vol == _lastVol && muted == _lastMuted) return;
            _lastVol = vol;
            _lastMuted = muted;
            Post(() => VolumeChanged?.Invoke(vol, muted));
        }

        private void OnCapture(string id, bool muted)
        {
            if (id == _defaultMicId) MicMuted = muted;
            lock (_lock)
            {
                if (_micState.TryGetValue(id, out bool last) && last == muted) return;
                _micState[id] = muted;
            }
            Post(() => MicMuteChanged?.Invoke(muted));
        }

        private static void Post(Action a)
        {
            Application.Current?.Dispatcher.BeginInvoke(a);
        }

        /// <summary>Меняет громкость динамиков (delta в долях: 0.02 = 2%).</summary>
        public void AdjustVolume(double delta)
        {
            try
            {
                if (_render == null) return;
                var v = _render.AudioEndpointVolume;

                float next = (float)Math.Max(0, Math.Min(1, v.MasterVolumeLevelScalar + delta));
                if (delta > 0 && v.Mute) v.Mute = false;
                v.MasterVolumeLevelScalar = next;
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                _poll?.Stop();
                if (_render != null) _render.AudioEndpointVolume.OnVolumeNotification -= OnRender;
                _render?.Dispose();
                foreach (var m in _mics) m.Dispose();
                _enum?.Dispose();
            }
            catch { }
        }
    }
}
