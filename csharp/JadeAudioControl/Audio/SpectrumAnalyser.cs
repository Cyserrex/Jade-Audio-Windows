using System;

namespace JadeAudioControl.Audio;

/// <summary>
/// Turns captured samples into a spectrum laid out on the same log frequency
/// axis the EQ curve uses, so the two can be read against each other.
///
/// The bars fall slowly rather than snapping to each frame: a spectrum that
/// tracks the FFT exactly flickers too much to read.
/// </summary>
public sealed class SpectrumAnalyser
{
    private const int FftSize = 8192;
    private const double MinDb = -78;
    private const double MaxDb = -6;

    private readonly float[] _samples = new float[FftSize];
    private readonly double[] _window = new double[FftSize];
    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imaginary = new double[FftSize];
    private readonly double[] _magnitude = new double[FftSize / 2];

    private readonly double[] _levels;
    private readonly double[] _peaks;
    private readonly double[] _binLow;
    private readonly double[] _binHigh;

    /// <summary>Level per bar, 0 at the noise floor through 1 at full scale.</summary>
    public double[] Levels => _levels;
    public double[] Peaks => _peaks;
    public int BarCount => _levels.Length;

    /// <summary>True while something is actually playing.</summary>
    public bool HasSignal { get; private set; }

    public SpectrumAnalyser(int bars, double minHz, double maxHz)
    {
        _levels = new double[bars];
        _peaks = new double[bars];
        _binLow = new double[bars];
        _binHigh = new double[bars];

        for (int i = 0; i < FftSize; i++)  // Hann
            _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));

        double logMin = Math.Log10(minHz), logMax = Math.Log10(maxHz);
        for (int i = 0; i < bars; i++)
        {
            _binLow[i] = Math.Pow(10, logMin + (logMax - logMin) * i / bars);
            _binHigh[i] = Math.Pow(10, logMin + (logMax - logMin) * (i + 1) / bars);
        }
    }

    public void Update(LoopbackCapture capture)
    {
        if (!capture.TryRead(_samples))
            return;
        Update(_samples, capture.SampleRate);
    }

    /// <summary>Split out from the capture so the maths can be tested on its own.</summary>
    public void Update(float[] samples, int sampleRate)
    {
        if (!ReferenceEquals(samples, _samples))
            Array.Copy(samples, _samples, Math.Min(samples.Length, FftSize));

        // A captured stream carries a small DC offset. Windowed, that smears
        // across the lowest bins and plants a permanent bar in the bass that
        // drowns whatever is really playing there, so take the mean out first.
        double mean = 0;
        for (int i = 0; i < FftSize; i++)
            mean += _samples[i];
        mean /= FftSize;

        double energy = 0;
        for (int i = 0; i < FftSize; i++)
        {
            double centred = _samples[i] - mean;
            _real[i] = centred * _window[i];
            _imaginary[i] = 0;
            energy += centred * centred;
        }
        HasSignal = energy / FftSize > 1e-7;

        Fft(_real, _imaginary);

        double scale = 2.0 / FftSize;
        for (int i = 0; i < _magnitude.Length; i++)
        {
            double m = Math.Sqrt(_real[i] * _real[i] + _imaginary[i] * _imaginary[i]) * scale;
            _magnitude[i] = m;
        }

        double hzPerBin = (double)sampleRate / FftSize;
        for (int bar = 0; bar < _levels.Length; bar++)
        {
            int first = (int)Math.Floor(_binLow[bar] / hzPerBin);
            int last = (int)Math.Ceiling(_binHigh[bar] / hzPerBin);
            first = Math.Max(first, 2);
            last = Math.Min(Math.Max(last, first + 1), _magnitude.Length - 1);

            double peak = 0;
            for (int bin = first; bin <= last; bin++)
                peak = Math.Max(peak, _magnitude[bin]);

            double db = peak > 1e-10 ? 20 * Math.Log10(peak) : MinDb;
            double level = (db - MinDb) / (MaxDb - MinDb);
            level = level < 0 ? 0 : level > 1 ? 1 : level;

            // Rise quickly, fall slowly - the shape stays readable at 30 fps.
            _levels[bar] = level > _levels[bar]
                ? level
                : _levels[bar] + (level - _levels[bar]) * 0.28;

            _peaks[bar] = _levels[bar] > _peaks[bar]
                ? _levels[bar]
                : Math.Max(_levels[bar], _peaks[bar] - 0.012);
        }
    }

    public void Clear()
    {
        Array.Clear(_levels, 0, _levels.Length);
        Array.Clear(_peaks, 0, _peaks.Length);
        HasSignal = false;
    }

    /// <summary>In-place radix-2 Cooley-Tukey. FftSize is a power of two.</summary>
    private static void Fft(double[] real, double[] imaginary)
    {
        int n = real.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double wReal = Math.Cos(angle), wImaginary = Math.Sin(angle);
            for (int i = 0; i < n; i += length)
            {
                double curReal = 1, curImaginary = 0;
                for (int j = 0; j < length / 2; j++)
                {
                    int a = i + j, b = i + j + length / 2;
                    double tReal = real[b] * curReal - imaginary[b] * curImaginary;
                    double tImaginary = real[b] * curImaginary + imaginary[b] * curReal;

                    real[b] = real[a] - tReal;
                    imaginary[b] = imaginary[a] - tImaginary;
                    real[a] += tReal;
                    imaginary[a] += tImaginary;

                    double nextReal = curReal * wReal - curImaginary * wImaginary;
                    curImaginary = curReal * wImaginary + curImaginary * wReal;
                    curReal = nextReal;
                }
            }
        }
    }
}
