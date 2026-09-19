using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JadeAudioControl.Compat;
using JadeAudioControl.Protocol;

namespace JadeAudioControl.Controls;

/// <summary>Fired while a band handle is dragged.</summary>
public sealed class BandDragEventArgs : EventArgs
{
    public int Index { get; init; }
    public int Frequency { get; init; }
    public double Gain { get; init; }
}

/// <summary>
/// The frequency response plot: a log frequency axis, one translucent trace per
/// band, the summed curve on top with a gradient fill under it, and a draggable
/// handle per band.
/// </summary>
public sealed class EqCurve : FrameworkElement
{
    private const double FreqMin = 20, FreqMax = 20000;
    private const double SampleRate = 48000;
    private const int Resolution = 320;

    private static readonly double[] GridFreqs = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
    private static readonly double[] GridDb = { -12, -6, 0, 6, 12 };

    public static readonly Color[] BandColors =
    {
        Color.FromRgb(0xFF, 0x8A, 0x65), Color.FromRgb(0xFF, 0xCA, 0x5C),
        Color.FromRgb(0x7B, 0xE3, 0x95), Color.FromRgb(0x59, 0xB8, 0xFF),
        Color.FromRgb(0xC2, 0x8B, 0xFF), Color.FromRgb(0x5C, 0xE1, 0xD2),
        Color.FromRgb(0xFF, 0x7A, 0xA8), Color.FromRgb(0xA9, 0xD6, 0x6B),
        Color.FromRgb(0xFF, 0xB0, 0x8A), Color.FromRgb(0x9B, 0xA7, 0xFF),
    };

    private readonly Typeface _typeface = new("Segoe UI");
    private List<Band> _bands = new();
    private int _dragging = -1;
    private int _hover = -1;

    /// <summary>Live spectrum drawn behind everything, 0..1 per bar. Null hides it.</summary>
    public double[]? Spectrum { get; set; }
    public double[]? SpectrumPeaks { get; set; }

    public double DbSpan { get; set; } = 15;
    public (double Min, double Max) GainRange { get; set; } = (-12, 12);

    public event EventHandler<BandDragEventArgs>? BandDragged;
    public event EventHandler<int>? BandSelected;

    public int SelectedIndex { get; private set; } = -1;

    public EqCurve()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetBands(IEnumerable<Band> bands)
    {
        _bands = bands.Select(b => b.Clone()).ToList();
        InvalidateVisual();
    }

    public void SetSelected(int index)
    {
        SelectedIndex = index;
        InvalidateVisual();
    }

    // -- geometry ------------------------------------------------------------

    private double XOf(double freq)
    {
        double lo = Math.Log10(FreqMin), hi = Math.Log10(FreqMax);
        double f = MathEx.Clamp(freq, FreqMin, FreqMax);
        return (Math.Log10(f) - lo) / (hi - lo) * ActualWidth;
    }

    private double FreqOf(double x)
    {
        double lo = Math.Log10(FreqMin), hi = Math.Log10(FreqMax);
        double t = MathEx.Clamp(x / Math.Max(ActualWidth, 1), 0, 1);
        return Math.Pow(10, lo + t * (hi - lo));
    }

    private double YOf(double db)
    {
        double half = ActualHeight / 2;
        return half - db / DbSpan * (half - 14);
    }

    private double DbOf(double y)
    {
        double half = ActualHeight / 2;
        return (half - y) / Math.Max(half - 14, 1) * DbSpan;
    }

    // -- filter maths --------------------------------------------------------

    /// <summary>Audio EQ Cookbook biquad, matching what the device computes.</summary>
    private static (double b0, double b1, double b2, double a0, double a1, double a2) Biquad(Band band)
    {
        double w = 2 * band.Frequency / SampleRate;
        double a = Math.Pow(10, band.Gain / 40);
        double sqrtA = Math.Sqrt(a);
        double sn = Math.Sin(Math.PI * w);
        double cs = Math.Cos(Math.PI * w);
        double q = band.Q > 0 ? band.Q : 1;
        double alpha = sn / (2 * q);

        return band.Type switch
        {
            FilterType.LowShelf => (
                a * (a + 1 - (a - 1) * cs + 2 * sqrtA * alpha),
                2 * a * (a - 1 - (a + 1) * cs),
                a * (a + 1 - (a - 1) * cs - 2 * sqrtA * alpha),
                a + 1 + (a - 1) * cs + 2 * sqrtA * alpha,
                -2 * (a - 1 + (a + 1) * cs),
                a + 1 + (a - 1) * cs - 2 * sqrtA * alpha),
            FilterType.HighShelf => (
                a * (a + 1 + (a - 1) * cs + 2 * sqrtA * alpha),
                -2 * a * (a - 1 + (a + 1) * cs),
                a * (a + 1 + (a - 1) * cs - 2 * sqrtA * alpha),
                a + 1 - (a - 1) * cs + 2 * sqrtA * alpha,
                2 * (a - 1 - (a + 1) * cs),
                a + 1 - (a - 1) * cs - 2 * sqrtA * alpha),
            FilterType.LowPass => ((1 - cs) / 2, 1 - cs, (1 - cs) / 2, 1 + alpha, -2 * cs, 1 - alpha),
            FilterType.HighPass => ((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + alpha, -2 * cs, 1 - alpha),
            FilterType.BandPass => (alpha, 0, -alpha, 1 + alpha, -2 * cs, 1 - alpha),
            FilterType.AllPass => (1 - alpha, -2 * cs, 1 + alpha, 1 + alpha, -2 * cs, 1 - alpha),
            _ => (1 + alpha * a, -2 * cs, 1 - alpha * a, 1 + alpha / a, -2 * cs, 1 - alpha / a),
        };
    }

    private static double[] Response(Band band, double[] freqs)
    {
        var (b0, b1, b2, a0, a1, a2) = Biquad(band);
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;

        var result = new double[freqs.Length];
        for (int i = 0; i < freqs.Length; i++)
        {
            double omega = -2 * Math.PI * freqs[i] / SampleRate;
            var z = new Complex(Math.Cos(omega), Math.Sin(omega));
            var num = b0 + b1 * z + b2 * z * z;
            var den = Complex.One + a1 * z + a2 * z * z;
            double mag = (num / den).Magnitude;
            result[i] = mag > 1e-12 ? 20 * Math.Log10(mag) : -240;
        }
        return result;
    }

    private double[] LogFreqs()
    {
        var freqs = new double[Resolution];
        double lo = Math.Log10(FreqMin), hi = Math.Log10(FreqMax);
        for (int i = 0; i < Resolution; i++)
            freqs[i] = Math.Pow(10, lo + (hi - lo) * i / (Resolution - 1.0));
        return freqs;
    }

    // -- rendering -----------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10)
            return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        DrawSpectrum(dc, w, h);

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
        var zeroPen = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x2F, 0xD4, 0xB5)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x77, 0x85));

        foreach (double db in GridDb)
        {
            double y = Math.Round(YOf(db)) + 0.5;
            dc.DrawLine(db == 0 ? zeroPen : gridPen, new Point(0, y), new Point(w, y));
            dc.DrawText(Label($"{db:+0;-0;0}", 9.5, labelBrush), new Point(6, y - 14));
        }

        foreach (double f in GridFreqs)
        {
            double x = Math.Round(XOf(f)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            string text = f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";
            dc.DrawText(Label(text, 9.5, labelBrush), new Point(x + 4, h - 16));
        }

        if (_bands.Count == 0)
            return;

        var freqs = LogFreqs();
        var total = new double[freqs.Length];

        for (int i = 0; i < _bands.Count; i++)
        {
            var response = Response(_bands[i], freqs);
            for (int j = 0; j < total.Length; j++)
                total[j] += response[j];

            var color = BandColors[i % BandColors.Length];
            bool active = i == SelectedIndex || i == _hover || i == _dragging;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(active ? 0xFF : 0x8C), color.R, color.G, color.B)),
                              active ? 1.8 : 1.1);
            dc.DrawGeometry(null, pen, Trace(freqs, response));
        }

        // Filled area under the summed curve.
        var curve = Trace(freqs, total);
        var fill = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(XOf(freqs[0]), YOf(0)), IsClosed = true, IsFilled = true };
        for (int i = 0; i < freqs.Length; i++)
            figure.Segments.Add(new LineSegment(new Point(XOf(freqs[i]), YOf(total[i])), false));
        figure.Segments.Add(new LineSegment(new Point(XOf(freqs[freqs.Length - 1]), YOf(0)), false));
        fill.Figures.Add(figure);

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x40, 0x2F, 0xD4, 0xB5), 0),
                new GradientStop(Color.FromArgb(0x00, 0x2F, 0xD4, 0xB5), 1),
            }
        };
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, YOf(0))));
        dc.DrawGeometry(gradient, null, fill);
        dc.Pop();

        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF3)), 2.2), curve);

        for (int i = 0; i < _bands.Count; i++)
            DrawHandle(dc, i);
    }

    /// <summary>
    /// The spectrum sits behind the grid, dim enough that the EQ curve stays the
    /// thing being read. Bars share the curve's log axis, so a peak in the music
    /// lines up with the band that would move it.
    /// </summary>
    private void DrawSpectrum(DrawingContext dc, double w, double h)
    {
        var bars = Spectrum;
        if (bars is null || bars.Length == 0)
            return;

        double floor = h - 18;
        double usable = floor - 8;
        double barWidth = w / bars.Length;

        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1),
            EndPoint = new Point(0, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x1A, 0x2F, 0xD4, 0xB5), 0),
                new GradientStop(Color.FromArgb(0x42, 0x59, 0xB8, 0xFF), 1),
            },
        };
        fill.Freeze();

        for (int i = 0; i < bars.Length; i++)
        {
            double level = bars[i];
            if (level <= 0.004)
                continue;

            double height = level * usable;
            double x = i * barWidth;
            dc.DrawRectangle(fill, null,
                new Rect(x + 0.5, floor - height, Math.Max(barWidth - 1, 0.8), height));
        }

        var peaks = SpectrumPeaks;
        if (peaks is null)
            return;

        var peakBrush = new SolidColorBrush(Color.FromArgb(0x52, 0x9B, 0xE8, 0xD8));
        peakBrush.Freeze();
        for (int i = 0; i < peaks.Length && i < bars.Length; i++)
        {
            if (peaks[i] <= 0.01)
                continue;
            double y = floor - peaks[i] * usable;
            dc.DrawRectangle(peakBrush, null,
                new Rect(i * barWidth + 0.5, y, Math.Max(barWidth - 1, 0.8), 1.4));
        }
    }

    private static FormattedText Label(string text, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, 1.25);

    private Geometry Trace(double[] freqs, double[] values)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(XOf(freqs[0]), YOf(values[0])), false, false);
            for (int i = 1; i < freqs.Length; i++)
                ctx.LineTo(new Point(XOf(freqs[i]), YOf(values[i])), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawHandle(DrawingContext dc, int index)
    {
        var band = _bands[index];
        var color = BandColors[index % BandColors.Length];
        double x = XOf(band.Frequency), y = YOf(band.Gain);
        bool active = index == SelectedIndex || index == _hover || index == _dragging;
        double radius = active ? 12 : 10;

        if (active)
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B)),
                           null, new Point(x, y), radius + 8, radius + 8);

        dc.DrawEllipse(new SolidColorBrush(color),
                       new Pen(new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x13)), 2.5),
                       new Point(x, y), radius, radius);

        var text = Label((index + 1).ToString(), 10.5, new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x13)));
        dc.DrawText(text, new Point(x - text.Width / 2, y - text.Height / 2));
    }

    // -- interaction ---------------------------------------------------------

    private int HitTest(Point p)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _bands.Count; i++)
        {
            double dx = XOf(_bands[i].Frequency) - p.X;
            double dy = YOf(_bands[i].Gain) - p.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return bestDistance <= 26 ? best : -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        _dragging = HitTest(point);
        if (_dragging >= 0)
        {
            SelectedIndex = _dragging;
            BandSelected?.Invoke(this, _dragging);
            CaptureMouse();
            InvalidateVisual();
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);

        if (_dragging >= 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            int freq = (int)Math.Round(MathEx.Clamp(FreqOf(point.X), FreqMin, FreqMax));
            double gain = Math.Round(MathEx.Clamp(DbOf(point.Y), GainRange.Min, GainRange.Max), 1);
            _bands[_dragging].Frequency = freq;
            _bands[_dragging].Gain = gain;
            InvalidateVisual();
            BandDragged?.Invoke(this, new BandDragEventArgs { Index = _dragging, Frequency = freq, Gain = gain });
        }
        else
        {
            int hover = HitTest(point);
            if (hover != _hover)
            {
                _hover = hover;
                Cursor = hover >= 0 ? Cursors.SizeAll : Cursors.Arrow;
                InvalidateVisual();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging >= 0)
        {
            _dragging = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
        base.OnMouseLeave(e);
    }
}
