using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace DreamIsland
{
    /// <summary>
    /// «Слушает» то, что играет в системе (loopback), и делит звук на три полосы:
    /// басы, середина, верха. Звук нигде не сохраняется — считается только громкость полос.
    /// Включается только пока играет музыка и виден остров.
    /// </summary>
    public sealed class AudioLevelMonitor : IDisposable
    {
        private WasapiLoopbackCapture _cap;
        private BiQuadFilter _bassLp, _midHp, _midLp, _highHp;
        private int _channels;
        private int _bits;
        private bool _float;

        private volatile float _bass, _mid, _high;
        private long _lastTick;

        public bool Running { get { return _cap != null; } }

        public void Start()
        {
            if (_cap != null) return;

            try
            {
                var cap = new WasapiLoopbackCapture();
                var wf = cap.WaveFormat;

                _channels = Math.Max(1, wf.Channels);
                _bits = wf.BitsPerSample;
                _float = wf.Encoding == WaveFormatEncoding.IeeeFloat;
                var ext = wf as WaveFormatExtensible;
                if (ext != null && ext.ToStandardWaveFormat().Encoding == WaveFormatEncoding.IeeeFloat) _float = true;

                float sr = wf.SampleRate;
                _bassLp = BiQuadFilter.LowPassFilter(sr, 150f, 0.707f);
                _midHp = BiQuadFilter.HighPassFilter(sr, 150f, 0.707f);
                _midLp = BiQuadFilter.LowPassFilter(sr, 2000f, 0.707f);
                _highHp = BiQuadFilter.HighPassFilter(sr, 2000f, 0.707f);

                cap.DataAvailable += OnData;
                cap.StartRecording();
                _cap = cap;
            }
            catch
            {
                Stop();
            }
        }

        public void Stop()
        {
            var c = _cap;
            _cap = null;
            _bass = _mid = _high = 0;

            if (c != null)
            {
                try
                {
                    c.DataAvailable -= OnData;
                    c.StopRecording();
                    c.Dispose();
                }
                catch { }
            }
        }

        /// <summary>Текущие уровни полос (0 и выше; масштаб зависит от музыки — нормализует вызывающий).</summary>
        public void GetLevels(out float bass, out float mid, out float high)
        {
            // loopback не присылает данных, когда ничего не играет — тогда уровни нулевые
            if (Environment.TickCount64 - Interlocked.Read(ref _lastTick) > 150)
            {
                bass = mid = high = 0;
                return;
            }
            bass = _bass;
            mid = _mid;
            high = _high;
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            try
            {
                int bytesPer = _bits / 8;
                int frameSize = bytesPer * _channels;
                if (frameSize <= 0) return;

                int frames = e.BytesRecorded / frameSize;
                if (frames == 0) return;

                double sb = 0, sm = 0, sh = 0;

                for (int f = 0; f < frames; f++)
                {
                    int off = f * frameSize;
                    float mono = 0;
                    for (int c = 0; c < _channels; c++)
                        mono += ReadSample(e.Buffer, off + c * bytesPer);
                    mono /= _channels;

                    float b = _bassLp.Transform(mono);
                    float m = _midLp.Transform(_midHp.Transform(mono));
                    float h = _highHp.Transform(mono);

                    sb += b * b;
                    sm += m * m;
                    sh += h * h;
                }

                _bass = (float)Math.Sqrt(sb / frames);
                _mid = (float)Math.Sqrt(sm / frames);
                _high = (float)Math.Sqrt(sh / frames);
                Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
            }
            catch { }
        }

        private float ReadSample(byte[] buf, int o)
        {
            if (_float && _bits == 32) return BitConverter.ToSingle(buf, o);
            if (_bits == 16) return BitConverter.ToInt16(buf, o) / 32768f;
            if (_bits == 24)
            {
                int v = buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16);
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                return v / 8388608f;
            }
            if (_bits == 32) return BitConverter.ToInt32(buf, o) / 2147483648f;
            return 0;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
