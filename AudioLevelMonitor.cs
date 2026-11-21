using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace DreamIsland
{
    /// <summary>
    /// «Слушает» то, что играет в системе (loopback) и считает:
    ///  — три полосы (басы / середина / верха) для живого эквалайзера;
    ///  — спектр из 16 полос, «энергию» и удары (бочка, малый барабан) для острова «Эмоции».
    /// Звук нигде не сохраняется — считаются только числа.
    /// Включается только пока нужен (играет музыка / видео и виден остров).
    /// </summary>
    public sealed class AudioLevelMonitor : IDisposable
    {
        public const int Bands = 16;

        private const int FftSize = 2048;       // 2^11
        private const int FftPower = 11;
        private const int Hop = FftSize / 2;

        private WasapiLoopbackCapture _cap;
        private int _channels, _bits;
        private bool _float;

        // три полосы
        private BiQuadFilter _bassLp, _midHp, _midLp, _highHp;
        private volatile float _bass, _mid, _high;

        // спектр и удары
        private readonly float[] _buf = new float[FftSize];
        private int _pos;
        private readonly Complex[] _fft = new Complex[FftSize];
        private readonly float[] _hann = new float[FftSize];
        private readonly float[] _mag = new float[FftSize / 2];
        private readonly float[] _prevMag = new float[FftSize / 2];
        private readonly float[] _spec = new float[Bands];
        private readonly object _specLock = new object();
        private int[] _bandLo, _bandHi;
        private int _kickLo, _kickHi, _snareLo, _snareHi;
        private readonly float[] _kickHist = new float[40];
        private readonly float[] _snareHist = new float[40];
        private int _kickIdx, _snareIdx;
        private long _lastKick, _lastSnare;
        private volatile int _kicks, _snares;
        private volatile float _rms;
        private long _lastTick;

        public AudioLevelMonitor()
        {
            for (int i = 0; i < FftSize; i++)
                _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        }

        public bool Running { get { return _cap != null; } }

        /// <summary>Сколько ударов бочки было с момента запуска (смотри, как растёт число).</summary>
        public int KickCount { get { return _kicks; } }

        /// <summary>Сколько ударов малого барабана / хлопков.</summary>
        public int SnareCount { get { return _snares; } }

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

                PrepareBands(sr);
                _pos = 0;
                Array.Clear(_prevMag, 0, _prevMag.Length);
                Array.Clear(_kickHist, 0, _kickHist.Length);
                Array.Clear(_snareHist, 0, _snareHist.Length);

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
            _rms = 0;
            lock (_specLock) Array.Clear(_spec, 0, _spec.Length);

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

        private bool Stale()
        {
            // loopback не присылает данных, когда ничего не играет — тогда всё нулевое
            return Environment.TickCount64 - Interlocked.Read(ref _lastTick) > 150;
        }

        public void GetLevels(out float bass, out float mid, out float high)
        {
            if (Stale()) { bass = mid = high = 0; return; }
            bass = _bass;
            mid = _mid;
            high = _high;
        }

        /// <summary>Спектр: 16 полос от низких к высоким (значения нужно нормализовать).</summary>
        public void GetSpectrum(float[] dst)
        {
            if (Stale()) { Array.Clear(dst, 0, Math.Min(dst.Length, Bands)); return; }
            lock (_specLock) Array.Copy(_spec, dst, Math.Min(dst.Length, Bands));
        }

        public float Rms { get { return Stale() ? 0 : _rms; } }

        // ---------- разметка полос ----------

        private void PrepareBands(float sr)
        {
            float binHz = sr / FftSize;
            _bandLo = new int[Bands];
            _bandHi = new int[Bands];

            double f0 = 40, f1 = 16000;
            double ratio = Math.Pow(f1 / f0, 1.0 / Bands);
            int maxBin = FftSize / 2 - 1;

            for (int b = 0; b < Bands; b++)
            {
                double a = f0 * Math.Pow(ratio, b);
                double c = f0 * Math.Pow(ratio, b + 1);
                int lo = Math.Max(1, (int)Math.Floor(a / binHz));
                int hi = Math.Max(lo, (int)Math.Ceiling(c / binHz) - 1);
                _bandLo[b] = Math.Min(lo, maxBin);
                _bandHi[b] = Math.Min(hi, maxBin);
            }

            _kickLo = Math.Max(1, (int)(40 / binHz));
            _kickHi = Math.Max(_kickLo, (int)(140 / binHz));
            _snareLo = Math.Max(_kickHi + 1, (int)(180 / binHz));
            _snareHi = Math.Min(maxBin, (int)(4000 / binHz));
        }

        // ---------- обработка звука ----------

        private void OnData(object sender, WaveInEventArgs e)
        {
            try
            {
                int bytesPer = _bits / 8;
                int frameSize = bytesPer * _channels;
                if (frameSize <= 0) return;

                int frames = e.BytesRecorded / frameSize;
                if (frames == 0) return;

                double sb = 0, sm = 0, sh = 0, sa = 0;

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
                    sa += mono * mono;

                    _buf[_pos++] = mono;
                    if (_pos == FftSize)
                    {
                        ProcessFrame();
                        Array.Copy(_buf, Hop, _buf, 0, Hop);   // окно сдвигается на половину
                        _pos = Hop;
                    }
                }

                _bass = (float)Math.Sqrt(sb / frames);
                _mid = (float)Math.Sqrt(sm / frames);
                _high = (float)Math.Sqrt(sh / frames);
                _rms = (float)Math.Sqrt(sa / frames);
                Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
            }
            catch { }
        }

        private void ProcessFrame()
        {
            for (int i = 0; i < FftSize; i++)
            {
                _fft[i].X = _buf[i] * _hann[i];
                _fft[i].Y = 0;
            }
            FastFourierTransform.FFT(true, FftPower, _fft);

            int half = FftSize / 2;
            for (int k = 0; k < half; k++)
                _mag[k] = (float)Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y) / half;

            // спектр по 16 полосам (корень — чтобы тихие полосы тоже были заметны)
            lock (_specLock)
            {
                for (int b = 0; b < Bands; b++)
                {
                    float sum = 0;
                    int n = 0;
                    for (int k = _bandLo[b]; k <= _bandHi[b]; k++) { sum += _mag[k]; n++; }
                    _spec[b] = (float)Math.Sqrt(n > 0 ? sum / n : 0);
                }
            }

            // «поток» — насколько вырос звук в диапазоне бочки и малого барабана по сравнению с прошлым окном
            float fk = 0, fs = 0;
            for (int k = _kickLo; k <= _kickHi; k++) fk += Math.Max(0, _mag[k] - _prevMag[k]);
            for (int k = _snareLo; k <= _snareHi; k++) fs += Math.Max(0, _mag[k] - _prevMag[k]);
            Array.Copy(_mag, _prevMag, half);

            if (_rms > 0.003f)
            {
                if (Detect(fk, _kickHist, ref _kickIdx, ref _lastKick, 1.5f, 0.0004f, 130)) _kicks++;
                if (Detect(fs, _snareHist, ref _snareIdx, ref _lastSnare, 1.6f, 0.0007f, 100)) _snares++;
            }
        }

        /// <summary>Удар — это резкий скачок «потока» выше среднего за последние ~0,8 секунды.</summary>
        private static bool Detect(float flux, float[] hist, ref int idx, ref long last,
                                   float mult, float floor, int cooldownMs)
        {
            float sum = 0;
            for (int i = 0; i < hist.Length; i++) sum += hist[i];
            float mean = sum / hist.Length;

            hist[idx] = flux;
            idx = (idx + 1) % hist.Length;

            long now = Environment.TickCount64;
            if (flux > mean * mult + floor && now - last > cooldownMs)
            {
                last = now;
                return true;
            }
            return false;
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
