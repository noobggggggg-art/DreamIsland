using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DreamIsland
{
    /// <summary>
    /// Определяет «аппаратный» mute микрофона (как на Fifine AM8) по звуку:
    /// если несколько секунд приходит абсолютная цифровая тишина — микрофон выключен.
    /// Для этого открывает микрофон на запись (Windows покажет значок «микрофон используется»).
    /// Записанный звук нигде не сохраняется: считается только пиковый уровень.
    /// </summary>
    public sealed class MicSilenceWatcher : IDisposable
    {
        private const float SilenceThreshold = 0.000001f;   // примерно -120 dBFS
        private const double MuteAfterSeconds = 1.2;

        private MMDeviceEnumerator _enum;
        private MMDevice _dev;
        private WasapiCapture _cap;

        private DateTime _quietSince = DateTime.MinValue;
        private bool _muted;
        private volatile bool _running;
        private double _peak;

        public bool Running { get { return _running; } }
        public double Peak { get { return _peak; } }
        public string DeviceName { get; private set; }

        /// <summary>true = микрофон выключен (тишина), false = снова включён. Вызывается не в UI-потоке.</summary>
        public event Action<bool> MutedChanged;

        public void Start()
        {
            if (_running) return;

            try
            {
                if (_enum == null) _enum = new MMDeviceEnumerator();

                MMDevice pick = null;
                var list = _enum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                for (int i = 0; i < list.Count; i++)
                {
                    string n = list[i].FriendlyName ?? "";
                    if (n.IndexOf("fifine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("AM8", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pick = list[i];
                        break;
                    }
                }

                if (pick == null && _enum.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
                    pick = _enum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

                if (pick == null) return;

                _dev = pick;
                DeviceName = pick.FriendlyName;

                _cap = new WasapiCapture(pick);
                _cap.DataAvailable += OnData;
                _cap.RecordingStopped += OnStopped;

                _quietSince = DateTime.MinValue;
                _cap.StartRecording();
                _running = true;
            }
            catch
            {
                Cleanup();
            }
        }

        public void Stop()
        {
            Cleanup();
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            try
            {
                var cap = sender as WasapiCapture;
                if (cap == null) return;

                float peak = ComputePeak(e.Buffer, e.BytesRecorded, cap.WaveFormat);
                _peak = peak;

                var now = DateTime.UtcNow;
                if (peak <= SilenceThreshold)
                {
                    if (_quietSince == DateTime.MinValue) _quietSince = now;

                    if (!_muted && (now - _quietSince).TotalSeconds >= MuteAfterSeconds)
                    {
                        _muted = true;
                        MutedChanged?.Invoke(true);
                    }
                }
                else
                {
                    _quietSince = DateTime.MinValue;
                    if (_muted)
                    {
                        _muted = false;
                        MutedChanged?.Invoke(false);
                    }
                }
            }
            catch { }
        }

        private void OnStopped(object sender, StoppedEventArgs e)
        {
            // микрофон отключили или он пропал — освобождаем ресурсы, позже попробуем снова
            _muted = false;
            Cleanup();
        }

        private static float ComputePeak(byte[] buf, int count, WaveFormat wf)
        {
            var std = wf is WaveFormatExtensible x ? x.ToStandardWaveFormat() : wf;
            int bits = std.BitsPerSample;
            float peak = 0;

            if (std.Encoding == WaveFormatEncoding.IeeeFloat && bits == 32)
            {
                for (int i = 0; i + 3 < count; i += 4)
                {
                    float f = Math.Abs(BitConverter.ToSingle(buf, i));
                    if (f > peak) peak = f;
                }
            }
            else if (bits == 16)
            {
                for (int i = 0; i + 1 < count; i += 2)
                {
                    float f = Math.Abs(BitConverter.ToInt16(buf, i) / 32768f);
                    if (f > peak) peak = f;
                }
            }
            else if (bits == 24)
            {
                for (int i = 0; i + 2 < count; i += 3)
                {
                    int v = buf[i] | (buf[i + 1] << 8) | (buf[i + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    float f = Math.Abs(v / 8388608f);
                    if (f > peak) peak = f;
                }
            }
            else if (bits == 32)
            {
                for (int i = 0; i + 3 < count; i += 4)
                {
                    float f = (float)(Math.Abs((double)BitConverter.ToInt32(buf, i)) / 2147483648.0);
                    if (f > peak) peak = f;
                }
            }

            return peak;
        }

        private void Cleanup()
        {
            _running = false;
            try
            {
                if (_cap != null)
                {
                    _cap.DataAvailable -= OnData;
                    _cap.RecordingStopped -= OnStopped;
                    try { _cap.StopRecording(); } catch { }
                    _cap.Dispose();
                }
            }
            catch { }
            _cap = null;

            try { _dev?.Dispose(); } catch { }
            _dev = null;
        }

        public void Dispose()
        {
            Cleanup();
            try { _enum?.Dispose(); } catch { }
        }
    }
}
