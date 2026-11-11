using System;

namespace Aether.Island;

/// <summary>
/// Real-time spectrum from loopback audio: Hann-windowed 2048-point FFT, log-spaced bands,
/// fast attack / slow release smoothing. Fed from the capture thread, read from the UI.
/// </summary>
public sealed class Spectrum
{
    private const int N = 2048;
    private readonly float[] _ring = new float[N];
    private int _ringPos;
    private int _sinceLast;
    private readonly double[] _re = new double[N];
    private readonly double[] _im = new double[N];
    private readonly double[] _window = new double[N];
    private readonly float[] _bands;
    private readonly float[] _smooth;
    private readonly int[] _edges;
    private readonly object _gate = new();
    private int _sampleRate = 48000;

    public Spectrum(int bandCount = 32)
    {
        _bands = new float[bandCount];
        _smooth = new float[bandCount];
        _edges = new int[bandCount + 1];
        for (int i = 0; i < N; i++) _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1)));
        SetSampleRate(48000);
    }

    public int BandCount => _bands.Length;

    public void SetSampleRate(int rate)
    {
        _sampleRate = rate;
        double lo = 45, hi = Math.Min(16000, rate / 2.0 - 1);
        for (int i = 0; i <= _bands.Length; i++)
        {
            double f = lo * Math.Pow(hi / lo, i / (double)_bands.Length);
            _edges[i] = Math.Clamp((int)Math.Round(f * N / rate), 1, N / 2 - 1);
        }
        for (int i = 1; i <= _bands.Length; i++)
            if (_edges[i] <= _edges[i - 1]) _edges[i] = Math.Min(_edges[i - 1] + 1, N / 2 - 1);
    }

    /// <summary>Interleaved float samples from the capture thread.</summary>
    public void Feed(ReadOnlySpan<float> samples, int channels)
    {
        for (int i = 0; i + channels - 1 < samples.Length; i += channels)
        {
            float mono = 0;
            for (int c = 0; c < channels; c++) mono += samples[i + c];
            _ring[_ringPos] = mono / channels;
            _ringPos = (_ringPos + 1) % N;
            if (++_sinceLast >= N) // no overlap → ~23 updates/s at 48 kHz, plenty for small bars
            {
                _sinceLast = 0;
                Compute();
            }
        }
    }

    private void Compute()
    {
        for (int i = 0; i < N; i++)
        {
            _re[i] = _ring[(_ringPos + i) % N] * _window[i];
            _im[i] = 0;
        }
        Fft(_re, _im);

        lock (_gate)
        {
            for (int b = 0; b < _bands.Length; b++)
            {
                double peak = 0;
                for (int k = _edges[b]; k < _edges[b + 1] || k == _edges[b]; k++)
                {
                    double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
                    if (mag > peak) peak = mag;
                    if (k >= _edges[b + 1]) break;
                }
                // dB scale, -60..0 dB → 0..1, with a gentle tilt so highs aren't always flat.
                double db = 20 * Math.Log10(peak / (N / 4.0) + 1e-9) + b * 0.35;
                float v = (float)Math.Clamp((db + 60) / 60, 0, 1);
                float s = _smooth[b];
                _smooth[b] = v > s ? s + (v - s) * 0.65f : s + (v - s) * 0.18f;
            }
        }
    }

    /// <summary>Copy the current band levels (0..1). Decays to zero when not fed.</summary>
    public void Read(float[] target)
    {
        lock (_gate)
            Array.Copy(_smooth, target, Math.Min(target.Length, _smooth.Length));
    }

    public void Decay()
    {
        lock (_gate)
            for (int i = 0; i < _smooth.Length; i++) _smooth[i] *= 0.82f;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
