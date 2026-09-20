using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JadeAudioControl.Compat;
using JadeAudioControl.Protocol;

namespace JadeAudioControl.Controls;

/// <summary>
/// One editable band: colour chip, frequency, gain slider, Q and filter shape.
/// Raises <see cref="Changed"/> whenever the user moves something.
/// </summary>
public sealed class BandRow : Border
{
    private TextBox _freq = null!;
    private Slider _gain = null!;
    private TextBlock _gainLabel = null!;
    private TextBox _q = null!;
    private ComboBox _type = null!;
    private bool _suspend;

    public int Index { get; }
    public Band Band { get; }

    public event EventHandler? Changed;
    public event EventHandler? Focused;

    /// <summary>Row lays the band out across the window; Strip stands it on end.</summary>
    public enum Layout
    {
        Row,
        Strip,
    }

    public BandRow(Band band, int index, (double Min, double Max) gainRange, FilterType[] allowed,
                   Layout layout = Layout.Row)
    {
        Band = band;
        Index = index;
        _allowed = allowed;

        Background = (Brush)Application.Current.Resources["Surface"];
        BorderBrush = (Brush)Application.Current.Resources["Stroke"];
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        if (layout == Layout.Strip)
        {
            BuildStrip(band, index, gainRange, allowed);
            return;
        }

        Padding = new Thickness(12, 7, 12, 7);
        Margin = new Thickness(0, 0, 0, 6);

        var grid = new Grid();
        foreach (var width in new[] { 34.0, 112, -1, 70, 92, 134 })
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width)
            });

        // Colour chip with the band number.
        var color = EqCurve.BandColors[index % EqCurve.BandColors.Length];
        var chip = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                Foreground = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x13)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
        Grid.SetColumn(chip, 0);
        grid.Children.Add(chip);

        _freq = MakeTextBox(band.Frequency.ToString(CultureInfo.InvariantCulture), 74);
        _freq.ToolTip = "Centre frequency in Hz";
        var freqPanel = Labelled("Hz", _freq);
        Grid.SetColumn(freqPanel, 1);
        grid.Children.Add(freqPanel);

        _gain = new Slider
        {
            Minimum = gainRange.Min,
            Maximum = gainRange.Max,
            Value = MathEx.Clamp(band.Gain, gainRange.Min, gainRange.Max),
            TickFrequency = 0.1,
            IsSnapToTickEnabled = true,
            // Gain swings either side of zero, so it gets the detented fader.
            Style = (Style)Application.Current.Resources["CentredSlider"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
        };
        _gain.ValueChanged += (_, _) =>
        {
            if (_gainLabel is not null)
                _gainLabel.Text = FormatGain(_gain.Value);
            OnEdit();
        };
        Grid.SetColumn(_gain, 2);
        grid.Children.Add(_gain);

        _gainLabel = new TextBlock
        {
            Text = FormatGain(band.Gain),
            Width = 66,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["Text"],
            FontSize = 12.5,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
        };
        Grid.SetColumn(_gainLabel, 3);
        grid.Children.Add(_gainLabel);

        _q = MakeTextBox(band.Q.ToString("0.00", CultureInfo.InvariantCulture), 60);
        _q.ToolTip = "Q - how wide the band is";
        var qPanel = Labelled("Q", _q);
        Grid.SetColumn(qPanel, 4);
        grid.Children.Add(qPanel);

        _type = new ComboBox
        {
            Style = (Style)Application.Current.Resources["ModernCombo"],
            Width = 122,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        foreach (var option in allowed)
            _type.Items.Add(Pretty(option));
        _type.SelectedIndex = Math.Max(0, Array.IndexOf(allowed, band.Type));
        _type.SelectionChanged += (_, _) => OnEdit();
        Grid.SetColumn(_type, 5);
        grid.Children.Add(_type);

        Child = grid;

        MouseLeftButtonDown += (_, _) => Focused?.Invoke(this, EventArgs.Empty);
        _gain.GotMouseCapture += (_, _) => Focused?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A mixer channel: number and frequency on top, the fader down the middle,
    /// then the reading, Q and filter shape - read top to bottom like a strip.
    /// </summary>
    private void BuildStrip(Band band, int index, (double Min, double Max) gainRange,
                            FilterType[] allowed)
    {
        Padding = new Thickness(8, 10, 8, 10);
        Margin = new Thickness(0, 0, 6, 0);
        Width = 118;

        var stack = new StackPanel();

        var color = EqCurve.BandColors[index % EqCurve.BandColors.Length];
        stack.Children.Add(new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                Foreground = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x13)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });

        _freq = MakeTextBox(band.Frequency.ToString(CultureInfo.InvariantCulture), 84);
        _freq.ToolTip = "Centre frequency in Hz";
        _freq.TextAlignment = TextAlignment.Center;
        _freq.Margin = new Thickness(0, 10, 0, 0);
        _freq.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(_freq);
        stack.Children.Add(Caption("Hz", 2));

        _gain = new Slider
        {
            Minimum = gainRange.Min,
            Maximum = gainRange.Max,
            Value = MathEx.Clamp(band.Gain, gainRange.Min, gainRange.Max),
            TickFrequency = 0.1,
            IsSnapToTickEnabled = true,
            Style = (Style)Application.Current.Resources["VerticalCentredSlider"],
            Height = 152,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _gain.ValueChanged += (_, _) =>
        {
            if (_gainLabel is not null)
                _gainLabel.Text = FormatGain(_gain.Value);
            OnEdit();
        };
        stack.Children.Add(_gain);

        _gainLabel = new TextBlock
        {
            Text = FormatGain(band.Gain),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["Text"],
            FontSize = 12.5,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            Margin = new Thickness(0, 8, 0, 0),
        };
        stack.Children.Add(_gainLabel);

        _q = MakeTextBox(band.Q.ToString("0.00", CultureInfo.InvariantCulture), 84);
        _q.ToolTip = "Q - how wide the band is";
        _q.TextAlignment = TextAlignment.Center;
        _q.Margin = new Thickness(0, 10, 0, 0);
        _q.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(_q);
        stack.Children.Add(Caption("Q", 2));

        _type = new ComboBox
        {
            Style = (Style)Application.Current.Resources["ModernCombo"],
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 11.5,
        };
        foreach (var option in allowed)
            _type.Items.Add(Pretty(option));
        _type.SelectedIndex = Math.Max(0, Array.IndexOf(allowed, band.Type));
        _type.SelectionChanged += (_, _) => OnEdit();
        stack.Children.Add(_type);

        Child = stack;

        MouseLeftButtonDown += (_, _) => Focused?.Invoke(this, EventArgs.Empty);
        _gain.GotMouseCapture += (_, _) => Focused?.Invoke(this, EventArgs.Empty);
    }

    private static TextBlock Caption(string text, double top) => new()
    {
        Text = text,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Foreground = (Brush)Application.Current.Resources["Muted"],
        FontSize = 10.5,
        Margin = new Thickness(0, top, 0, 0),
    };

    private readonly FilterType[] _allowed;

    private static string Pretty(FilterType type) => type switch
    {
        FilterType.LowShelf => "Low shelf",
        FilterType.HighShelf => "High shelf",
        FilterType.BandPass => "Band pass",
        FilterType.LowPass => "Low pass",
        FilterType.HighPass => "High pass",
        FilterType.AllPass => "All pass",
        _ => "Peak",
    };

    private static string FormatGain(double db) =>
        db.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";

    private TextBox MakeTextBox(string text, double width)
    {
        var box = new TextBox
        {
            Text = text,
            Width = width,
            Style = (Style)Application.Current.Resources["ModernTextBox"],
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 5, 8, 5),
        };
        box.LostFocus += (_, _) => OnEdit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                OnEdit();
        };
        box.GotKeyboardFocus += (_, _) => Focused?.Invoke(this, EventArgs.Empty);
        return box;
    }

    private static StackPanel Labelled(string caption, UIElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(control);
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["Muted"],
            FontSize = 11,
        });
        return panel;
    }

    /// <summary>Push values in without raising Changed - used when the curve is dragged.</summary>
    public void Sync(Band band)
    {
        _suspend = true;
        _freq.Text = band.Frequency.ToString(CultureInfo.InvariantCulture);
        _gain.Value = band.Gain;
        _gainLabel.Text = FormatGain(band.Gain);
        _q.Text = band.Q.ToString("0.00", CultureInfo.InvariantCulture);
        int position = Array.IndexOf(_allowed, band.Type);
        if (position >= 0)
            _type.SelectedIndex = position;
        _suspend = false;
    }

    private void OnEdit()
    {
        if (_suspend)
            return;

        if (int.TryParse(_freq.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int freq))
            Band.Frequency = MathEx.Clamp(freq, 20, 20000);
        if (double.TryParse(_q.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double q))
            Band.Q = MathEx.Clamp(q, 0.25, 8);
        Band.Gain = Math.Round(_gain.Value, 1);
        if (_type.SelectedIndex >= 0 && _type.SelectedIndex < _allowed.Length)
            Band.Type = _allowed[_type.SelectedIndex];

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Highlight the row that matches the handle being dragged.</summary>
    public void SetActive(bool active)
    {
        BorderBrush = active
            ? (Brush)Application.Current.Resources["Accent"]
            : (Brush)Application.Current.Resources["Stroke"];
    }
}
