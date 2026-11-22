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

        // ритм по басу (40–120 Гц): пик над средним за ~1 с, с защитой от дребезга
        private readonly float[] _bassHist = new float[64];
        private int _bassPos, _beatLo, _beatHi;
        private long _bassFrames, _lastBeatFrame = -1000;
        private float _sr = 48000f;
        private volatile int _beats;
        private volatile float _rms;
        private long _lastTick;

        // стерео: левый и правый канал анализируются отдельно (спектр, громкость, резкие удары в своём канале)
        private sealed class Chan
        {
            public readonly float[] Buf = new float[FftSize];
            public readonly Complex[] Fft = new Complex[FftSize];
            public readonly float[] Mag = new float[FftSize / 2];
            public readonly float[] Prev = new float[FftSize / 2];
            public readonly float[] Spec = new float[Bands];
            public readonly float[] Hist = new float[40];
            public int HistIdx, Onsets;
            public long LastOnset;
            public volatile float Rms;

            public void Reset()
            {
                Array.Clear(Buf, 0, Buf.Length);
                Array.Clear(Prev, 0, Prev.Length);
                Array.Clear(Spec, 0, Spec.Length);
                Array.Clear(Hist, 0, Hist.Length);
                HistIdx = 0;
                Rms = 0;
            }
        }

        private readonly Chan _chL = new Chan();
        private readonly Chan _chR = new Chan();
        private volatile bool _stereo;

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

        /// <summary>Растёт на каждый удар ритма (по басу).</summary>
        public int BeatCount { get { return _beats; } }

        /// <summary>Есть ли у звука два и больше каналов (иначе левый и правый одинаковые).</summary>
        public bool Stereo { get { return _stereo; } }

        /// <summary>Громкость левого / правого канала.</summary>
        public float RmsL { get { return Stale() ? 0 : _chL.Rms; } }
        public float RmsR { get { return Stale() ? 0 : _chR.Rms; } }

        /// <summary>Сколько резких ударов (малый барабан, гитарный удар, хлопок) было в левом / правом канале.</summary>
        public int OnsetCountL { get { return _chL.Onsets; } }
        public int OnsetCountR { get { return _chR.Onsets; } }

        public void Start()
        {
            if (_cap != null) return;

            try
            {
                var cap = new WasapiLoopbackCapture();
                var wf = cap.WaveFormat;

                _channels = Math.Max(1, wf.Channels);
                _stereo = _channels >= 2;
                _chL.Reset();
                _chR.Reset();
                _chL.Onsets = _chR.Onsets = 0;
                _bits = wf.BitsPerSample;
                _float = wf.Encoding == WaveFormatEncoding.IeeeFloat;
                var ext = wf as WaveFormatExtensible;
                if (ext != null && ext.ToStandardWaveFormat().Encoding == WaveFormatEncoding.IeeeFloat) _float = true;

                float sr = wf.SampleRate;
                _sr = sr;
                Array.Clear(_bassHist, 0, _bassHist.Length);
                _bassFrames = 0;
                _lastBeatFrame = -1000;
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
            _chL.Rms = _chR.Rms = 0;
            lock (_specLock)
            {
                Array.Clear(_spec, 0, _spec.Length);
                Array.Clear(_chL.Spec, 0, Bands);
                Array.Clear(_chR.Spec, 0, Bands);
            }

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

        /// <summary>Спектр левого и правого канала отдельно. Для моно в оба массива попадает один и тот же спектр.</summary>
        public void GetSpectrumLR(float[] l, float[] r)
        {
            int n = Math.Min(Math.Min(l.Length, r.Length), Bands);
            if (Stale()) { Array.Clear(l, 0, n); Array.Clear(r, 0, n); return; }

            lock (_specLock)
            {
                if (_stereo)
                {
                    Array.Copy(_chL.Spec, l, n);
                    Array.Copy(_chR.Spec, r, n);
                }
                else
                {
                    Array.Copy(_spec, l, n);
                    Array.Copy(_spec, r, n);
                }
            }
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
            _beatLo = _kickLo;
            _beatHi = Math.Min(maxBin, Math.Max(_beatLo, (int)(120 / binHz)));
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

                double sb = 0, sm = 0, sh = 0, sa = 0, sl = 0, sr = 0;
                bool stereo = _stereo;

                for (int f = 0; f < frames; f++)
                {
                    int off = f * frameSize;
                    float mono = 0;
                    for (int c = 0; c < _channels; c++)
                        mono += ReadSample(e.Buffer, off + c * bytesPer);
                    mono /= _channels;

                    if (stereo)
                    {
                        // в многоканальном звуке (5.1 и т. д.) первые два канала — передние левый и правый
                        float l = ReadSample(e.Buffer, off);
                        float r = ReadSample(e.Buffer, off + bytesPer);
                        sl += l * l;
                        sr += r * r;
                        _chL.Buf[_pos] = l;
                        _chR.Buf[_pos] = r;
                    }

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
                        if (stereo)
                        {
                            ProcessChannel(_chL);
                            ProcessChannel(_chR);
                            Array.Copy(_chL.Buf, Hop, _chL.Buf, 0, Hop);
                            Array.Copy(_chR.Buf, Hop, _chR.Buf, 0, Hop);
                        }
                        _pos = Hop;
                    }
                }

                _bass = (float)Math.Sqrt(sb / frames);
                _mid = (float)Math.Sqrt(sm / frames);
                _high = (float)Math.Sqrt(sh / frames);
                _rms = (float)Math.Sqrt(sa / frames);
                if (stereo)
                {
                    _chL.Rms = (float)Math.Sqrt(sl / frames);
                    _chR.Rms = (float)Math.Sqrt(sr / frames);
                }
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

            float bassSum = 0;
            for (int k = _beatLo; k <= _beatHi; k++) bassSum += _mag[k];
            DetectBeat(bassSum);

            if (_rms > 0.003f)
            {
                if (Detect(fk, _kickHist, ref _kickIdx, ref _lastKick, 1.5f, 0.0004f, 130)) _kicks++;
                if (Detect(fs, _snareHist, ref _snareIdx, ref _lastSnare, 1.6f, 0.0007f, 100)) _snares++;
            }
        }

        /// <summary>Спектр одного канала и резкие удары в нём (диапазон малого барабана и выше — там живут гитары, вокал, тарелки).</summary>
        private void ProcessChannel(Chan ch)
        {
            for (int i = 0; i < FftSize; i++)
            {
                ch.Fft[i].X = ch.Buf[i] * _hann[i];
                ch.Fft[i].Y = 0;
            }
            FastFourierTransform.FFT(true, FftPower, ch.Fft);

            int half = FftSize / 2;
            for (int k = 0; k < half; k++)
                ch.Mag[k] = (float)Math.Sqrt(ch.Fft[k].X * ch.Fft[k].X + ch.Fft[k].Y * ch.Fft[k].Y) / half;

            lock (_specLock)
            {
                for (int b = 0; b < Bands; b++)
                {
                    float sum = 0;
                    int n = 0;
                    for (int k = _bandLo[b]; k <= _bandHi[b]; k++) { sum += ch.Mag[k]; n++; }
                    ch.Spec[b] = (float)Math.Sqrt(n > 0 ? sum / n : 0);
                }
            }

            float fs = 0;
            for (int k = _snareLo; k <= _snareHi; k++) fs += Math.Max(0, ch.Mag[k] - ch.Prev[k]);
            Array.Copy(ch.Mag, ch.Prev, half);

            if (_rms > 0.003f && Detect(fs, ch.Hist, ref ch.HistIdx, ref ch.LastOnset, 1.6f, 0.0007f, 120))
                ch.Onsets++;
        }

        private void DetectBeat(float e0)
        {
            int H = _bassHist.Length;

            // e1 — прошлый кадр (кандидат на пик), e2 — позапрошлый; решение с задержкой в один кадр (~20 мс)
            float e1 = _bassHist[(_bassPos - 1 + H) % H];
            float e2 = _bassHist[(_bassPos - 2 + H) % H];

            _bassHist[_bassPos] = e0;
            _bassPos = (_bassPos + 1) % H;
            _bassFrames++;

            if (_bassFrames < 16 || _rms <= 0.003f) return;

            int w = Math.Max(8, Math.Min(H - 14, (int)Math.Round(_sr / Hop)));   // около секунды
            float sum = 0;
            for (int i = 0; i < w; i++) sum += _bassHist[(_bassPos - 2 - i + 3 * H) % H];
            float mean = sum / w;

            float mn = float.MaxValue;
            for (int i = 1; i <= 12; i++)
            {
                float v = _bassHist[(_bassPos - 2 - i + 3 * H) % H];
                if (v < mn) mn = v;
            }

            int refractory = (int)Math.Round(0.21 * _sr / Hop);
            bool peak = e1 >= e2 && e1 > e0;
            if (peak && e1 > mean * 1.3f && e1 > mn * 1.15f && _bassFrames - _lastBeatFrame >= refractory)
            {
                _lastBeatFrame = _bassFrames;
                _beats++;
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
