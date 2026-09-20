using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JadeAudioControl.Audio;
using JadeAudioControl.Cloud;
using JadeAudioControl.Compat;
using JadeAudioControl.Controls;
using JadeAudioControl.Protocol;
using Microsoft.Win32;

namespace JadeAudioControl;

public partial class MainWindow : Window
{
    private JadeDevice? _device;
    private Capabilities _caps = new();
    private List<Band> _bands = new();
    private readonly List<BandRow> _rows = new();
    private readonly CloudClient _cloud = new();

    private bool _suspend;
    private DispatcherTimer? _bandWriteTimer;
    private readonly HashSet<int> _dirtyBands = new();
    private DispatcherTimer? _knobTimer;
    private Func<Task>? _pendingKnob;

    private string _tab = "community";
    private int _page = 1;
    private const int PageSize = 20;
    private List<CloudPreset> _libraryPresets = new();
    private CloudPreset? _selectedPreset;
    private (int Major, int Minor) _firmware = (-1, -1);
    private LoopbackCapture? _capture;
    private SpectrumAnalyser? _analyser;
    private DispatcherTimer? _spectrumTimer;
    private FirmwareCheck? _firmwareCheck;

    public MainWindow()
    {
        InitializeComponent();
        // Fit the work area: a 768-high screen would otherwise put the status
        // bar behind the taskbar.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Loaded += async (_, _) =>
        {
            await ConnectAsync();
            // On by default, but only once the device is known: the capture
            // picks its endpoint by name, and before ConnectAsync there is no
            // name to match, so it would settle for the Windows default.
            SpectrumToggle.IsChecked = true;
            await RestoreSessionAsync();
        };
        Curve.BandDragged += Curve_BandDragged;
        Curve.BandSelected += (_, index) => HighlightBand(index);
        SearchBox.TextChanged += (_, _) =>
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // -- chrome --------------------------------------------------------------

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            Maximise_Click(sender, e);
        else if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageEq is null)
            return;

        string tag = (string)((RadioButton)sender).Tag;
        PageEq.Visibility = tag == "eq" ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = tag == "library" ? Visibility.Visible : Visibility.Collapsed;
        PageDevice.Visibility = tag == "device" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = tag switch
        {
            "library" => "Preset library",
            "device" => "Device",
            _ => "Equalizer",
        };

        if (tag == "library" && _libraryPresets.Count == 0)
            _ = LoadLibraryAsync();
    }

    // -- status --------------------------------------------------------------

    private void Status(string text) => StatusText.Text = text;

    private IDisposable BusyScope()
    {
        Busy.Visibility = Visibility.Visible;
        return new Scope(() => Busy.Visibility = Visibility.Collapsed);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _onDispose;
        public Scope(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }

    // -- connection ----------------------------------------------------------

    private async Task ConnectAsync()
    {
        using var _ = BusyScope();
        Status("Looking for a device...");
        _device?.Dispose();
        _device = null;

        try
        {
            var device = await Task.Run(JadeDevice.Open);
            var caps = await device.ProbeAsync();
            string firmware = caps.Has(Reg.FirmwareVersion) ? await device.GetFirmwareAsync() : "?";
            _firmware = caps.Has(Reg.FirmwareVersion)
                ? await device.GetFirmwareVersionAsync()
                : (-1, -1);
            byte preset = caps.Has(Reg.PeqPre) ? await device.GetPresetAsync() : (byte)0;
            var bands = caps.BandCount > 0 ? await device.ReadAllBandsAsync(caps.BandCount) : new List<Band>();
            double gain = caps.Has(Reg.GlobalGain) ? await device.GetGlobalGainAsync() : 0;
            int volume = caps.Has(Reg.VolOutput) ? await device.GetVolumeAsync() : 0;
            DacFilterMode filter = caps.Has(Reg.FilterMode) ? await device.GetFilterModeAsync() : DacFilterMode.SdSharp;

            _device = device;
            _caps = caps;
            _bands = bands;

            StatusDot.Fill = (Brush)Application.Current.Resources["Accent"];
            DeviceName.Text = device.ProductName;
            DeviceSub.Text = $"Firmware {firmware}";

            _suspend = true;
            PresetBox.Items.Clear();
            foreach (var (name, _) in caps.Presets)
                PresetBox.Items.Add(name);
            int index = caps.Presets.ToList().FindIndex(p => p.Value == preset);
            PresetBox.SelectedIndex = index >= 0 ? index : 0;

            PreampCard.Visibility = caps.Has(Reg.GlobalGain) ? Visibility.Visible : Visibility.Collapsed;
            if (caps.Has(Reg.GlobalGain))
            {
                PreampSlider.Minimum = caps.GainRange.Min;
                PreampSlider.Maximum = caps.GainRange.Max;
                PreampSlider.Value = MathEx.Clamp(gain, caps.GainRange.Min, caps.GainRange.Max);
                PreampValue.Text = FormatDb(gain);
            }

            VolumeCard.Visibility = caps.Has(Reg.VolOutput) ? Visibility.Visible : Visibility.Collapsed;
            if (caps.Has(Reg.VolOutput))
            {
                VolumeSlider.Maximum = caps.MaxVolume;
                VolumeSlider.Value = MathEx.Clamp(volume, 0, caps.MaxVolume);
                VolumeValue.Text = volume.ToString(CultureInfo.InvariantCulture);
            }

            FilterCard.Visibility = caps.Has(Reg.FilterMode) ? Visibility.Visible : Visibility.Collapsed;
            if (caps.Has(Reg.FilterMode))
            {
                FilterBox.Items.Clear();
                foreach (DacFilterMode mode in Enum.GetValues(typeof(DacFilterMode)))
                    FilterBox.Items.Add(SplitCamel(mode.ToString()));
                FilterBox.SelectedIndex = (int)filter;
            }
            _suspend = false;

            Curve.GainRange = caps.GainRange;
            BuildBandRows();
            BuildDeviceInfo(firmware);

            _firmwareCheck = null;
            FirmwareLine.Text = _firmware.Major >= 0
                ? $"Installed: {FirmwareCatalogue.Describe(_firmware.Major, _firmware.Minor)}"
                : "This device does not report a firmware version.";
            FirmwareCheckButton.IsEnabled = _firmware.Major >= 0;
            FirmwarePageButton.Visibility = Visibility.Collapsed;
            FirmwareHowToButton.Visibility = Visibility.Collapsed;

            Status($"Connected. Registers: {string.Join(", ", caps.Supported.Select(r => r.ToString()))}");
        }
        catch (Exception exc)
        {
            StatusDot.Fill = (Brush)Application.Current.Resources["Danger"];
            DeviceName.Text = "No device";
            DeviceSub.Text = "Plug one in, then Reconnect";
            Status(exc.Message);
        }
    }

    private static string FormatDb(double value) =>
        value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";

    private static string SplitCamel(string text) =>
        string.Concat(text.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));

    private void BuildDeviceInfo(string firmware)
    {
        DeviceInfoGrid.Children.Clear();
        DeviceInfoGrid.RowDefinitions.Clear();
        DeviceInfoGrid.ColumnDefinitions.Clear();
        DeviceInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        DeviceInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (_device is null)
            return;

        var info = _device.Info;
        var rows = new (string, string)[]
        {
            ("Product", _device.ProductName),
            ("Manufacturer", info.Manufacturer),
            ("Firmware", firmware),
            ("Vendor / product id", $"0x{info.VendorId:X4} / 0x{info.ProductId:X4}"),
            ("HID report id", _device.ReportId.ToString(CultureInfo.InvariantCulture)),
            ("Report size", $"{info.InputReportLength} in / {info.OutputReportLength} out"),
            ("EQ bands", _caps.BandCount.ToString(CultureInfo.InvariantCulture)),
            ("Registers answered", string.Join(", ", _caps.Supported.Select(r => r.ToString()))),
        };

        for (int i = 0; i < rows.Length; i++)
        {
            DeviceInfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock
            {
                Text = rows[i].Item1,
                Style = (Style)Application.Current.Resources["Caption"],
                Margin = new Thickness(0, 3, 0, 3),
            };
            var value = new TextBlock
            {
                Text = rows[i].Item2,
                Style = (Style)Application.Current.Resources["Body"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 3),
            };
            Grid.SetRow(key, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            DeviceInfoGrid.Children.Add(key);
            DeviceInfoGrid.Children.Add(value);
        }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    // -- live spectrum -------------------------------------------------------

    /// <summary>
    /// Listen to what the dongle is playing and draw it behind the EQ curve.
    ///
    /// This is WASAPI loopback on the endpoint itself, so it shows the audio
    /// after the device's own EQ has been applied - which is the point: you can
    /// see what a band is doing to real music while you drag it.
    /// </summary>
    private void Spectrum_Toggled(object sender, RoutedEventArgs e)
    {
        if (SpectrumToggle.IsChecked == true)
            StartSpectrum();
        else
            StopSpectrum();
    }

    private void StartSpectrum()
    {
        if (_capture is not null)
            return;

        _capture = new LoopbackCapture();
        _analyser = new SpectrumAnalyser(96, 20, 20000);

        if (!_capture.Start(_device?.ProductName))
        {
            Status(_capture.Error ?? "Could not listen to the playback device.");
            _capture.Dispose();
            _capture = null;
            _analyser = null;
            SpectrumToggle.IsChecked = false;
            return;
        }

        Status($"Listening to {_capture.EndpointName}.");
        _spectrumTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _spectrumTimer.Tick += (_, _) =>
        {
            if (_capture is null || _analyser is null)
                return;
            _analyser.Update(_capture);
            Curve.Spectrum = _analyser.Levels;
            Curve.SpectrumPeaks = _analyser.Peaks;
            Curve.InvalidateVisual();
        };
        _spectrumTimer.Start();
    }

    private void StopSpectrum()
    {
        _spectrumTimer?.Stop();
        _spectrumTimer = null;
        _capture?.Dispose();
        _capture = null;
        _analyser = null;
        Curve.Spectrum = null;
        Curve.SpectrumPeaks = null;
        Curve.InvalidateVisual();
    }

    // -- firmware ------------------------------------------------------------

    private async void CheckFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (_device is null || _firmware.Major < 0)
            return;

        using var _ = BusyScope();
        FirmwareCheckButton.IsEnabled = false;
        FirmwareLine.Text = "Checking...";
        try
        {
            var check = await FirmwareCatalogue.CheckAsync(
                _cloud.Http, _device.ProductName, _firmware.Major, _firmware.Minor);
            _firmwareCheck = check;

            string installed = FirmwareCatalogue.Describe(_firmware.Major, _firmware.Minor);
            FirmwareLine.Text = check.State switch
            {
                FirmwareState.UpToDate => check.Message,
                FirmwareState.UpdateAvailable => check.Message,
                FirmwareState.Ahead => check.Message,
                _ => $"Installed: {installed}. {check.Message}",
            };

            FirmwarePageButton.Visibility = Visibility.Visible;
            FirmwareHowToButton.Visibility =
                string.IsNullOrEmpty(check.Info?.InstructionsUrl) ? Visibility.Collapsed : Visibility.Visible;

            if (!string.IsNullOrEmpty(check.Info?.Notes))
                FirmwareNote.Text = check.Info!.Notes +
                    " Nothing here writes to the dongle.";

            Status(check.State == FirmwareState.UpdateAvailable
                ? "A newer firmware is published."
                : "");
        }
        catch (Exception exc)
        {
            FirmwareLine.Text = exc.Message;
        }
        finally
        {
            FirmwareCheckButton.IsEnabled = true;
        }
    }

    private void FirmwarePage_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(_firmwareCheck?.BestUrl ?? "https://www.fiio.com/supports");

    private void FirmwareHowTo_Click(object sender, RoutedEventArgs e)
    {
        string url = _firmwareCheck?.Info?.InstructionsUrl ?? "";
        if (url.Length > 0)
            OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }

    // -- equaliser -----------------------------------------------------------

    private void BuildBandRows()
    {
        BandList.Items.Clear();
        _rows.Clear();
        var allowed = _caps.FilterTypes.ToArray();

        for (int i = 0; i < _bands.Count; i++)
        {
            var row = new BandRow(_bands[i], i, _caps.GainRange, allowed, BandRow.Layout.Strip);
            int index = i;
            row.Changed += (_, _) =>
            {
                Curve.SetBands(_bands);
                QueueBandWrite(index);
            };
            row.Focused += (_, _) => HighlightBand(index);
            _rows.Add(row);
            BandList.Items.Add(row);
        }
        Curve.SetBands(_bands);
    }

    private void HighlightBand(int index)
    {
        Curve.SetSelected(index);
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].SetActive(i == index);

        if (index >= 0 && index < _bands.Count)
            ShowBandZone(index);
    }

    /// <summary>
    /// Say which part of the spectrum the band sits in, and what that part
    /// does - a frequency is only meaningful to someone who already knows the
    /// map, and this is where the map gets read out.
    /// </summary>
    private void ShowBandZone(int index)
    {
        var band = _bands[index];
        Status($"Band {index + 1} at {band.Frequency} Hz - {FrequencyZones.Describe(band.Frequency)}");
    }

    private void Curve_BandDragged(object? sender, BandDragEventArgs e)
    {
        var band = _bands[e.Index];
        band.Frequency = e.Frequency;
        band.Gain = e.Gain;
        _rows[e.Index].Sync(band);
        HighlightBand(e.Index);
        QueueBandWrite(e.Index);
    }

    /// <summary>Coalesce rapid edits so a drag does not flood the device.</summary>
    private void QueueBandWrite(int index)
    {
        if (_suspend || _device is null)
            return;

        _dirtyBands.Add(index);
        _bandWriteTimer ??= CreateTimer(120, async () =>
        {
            var pending = _dirtyBands.ToArray();
            _dirtyBands.Clear();
            foreach (int i in pending.OrderBy(i => i))
            {
                try { await _device!.SetBandAsync(_bands[i]); }
                catch (Exception exc) { Status(exc.Message); return; }
            }
        });
        _bandWriteTimer.Stop();
        _bandWriteTimer.Start();
    }

    private DispatcherTimer CreateTimer(int milliseconds, Func<Task> action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await action();
        };
        return timer;
    }

    private void QueueKnob(Func<Task> action)
    {
        if (_suspend || _device is null)
            return;

        _pendingKnob = action;
        _knobTimer ??= CreateTimer(120, async () =>
        {
            var pending = _pendingKnob;
            _pendingKnob = null;
            if (pending is null)
                return;
            try { await pending(); }
            catch (Exception exc) { Status(exc.Message); }
        });
        _knobTimer.Stop();
        _knobTimer.Start();
    }

    private async void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend || _device is null || PresetBox.SelectedIndex < 0)
            return;

        var (name, value) = _caps.Presets[PresetBox.SelectedIndex];
        using var _ = BusyScope();
        Status($"Loading '{name}'...");
        try
        {
            await _device.SetPresetAsync(value);
            await Task.Delay(JadeDevice.PresetSettle);
            _bands = await _device.ReadAllBandsAsync(_caps.BandCount);
            BuildBandRows();
            Status($"Preset '{name}' loaded from the device.");
        }
        catch (Exception exc)
        {
            Status(exc.Message);
        }
    }

    private void Preamp_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PreampValue.Text = FormatDb(e.NewValue);
        double value = Math.Round(e.NewValue, 1);
        QueueKnob(() => _device!.SetGlobalGainAsync(value));
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int value = (int)Math.Round(e.NewValue);
        VolumeValue.Text = value.ToString(CultureInfo.InvariantCulture);
        QueueKnob(() => _device!.SetVolumeAsync(value));
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend || _device is null || FilterBox.SelectedIndex < 0)
            return;
        var mode = (DacFilterMode)FilterBox.SelectedIndex;
        QueueKnob(() => _device!.SetFilterModeAsync(mode));
    }

    // -- import / export -----------------------------------------------------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_bands.Count == 0)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "Preset (*.json)|*.json",
            FileName = (PresetBox.SelectedItem?.ToString() ?? "preset").ToLowerInvariant() + ".json",
        };
        if (dialog.ShowDialog() != true)
            return;

        var json = new Dictionary<string, object?>
        {
            ["device"] = _device?.ProductName ?? "",
            ["preset"] = PresetBox.SelectedItem?.ToString(),
            ["globalGain"] = _caps.Has(Reg.GlobalGain) ? Math.Round(PreampSlider.Value, 1) : (double?)null,
            ["bands"] = BandsToJson(_bands),
        };
        File.WriteAllText(dialog.FileName, Json.Write(json, indented: true));
        Status($"Exported to {dialog.FileName}");
    }

    private static List<Dictionary<string, object>> BandsToJson(IEnumerable<Band> bands) =>
        bands.Select(band => new Dictionary<string, object>
        {
            ["index"] = band.Index,
            ["frequency"] = band.Frequency,
            ["gain"] = band.Gain,
            ["q"] = band.Q,
            ["filterType"] = (int)band.Type,
        }).ToList();

    private static List<Band> BandsFromJson(Json node)
    {
        var bands = new List<Band>();
        int i = 0;
        foreach (var item in node.Items)
        {
            bands.Add(new Band
            {
                Index = item["index"].AsInt(i),
                Frequency = item["frequency"].AsInt(1000),
                Gain = item["gain"].AsDouble(0),
                Q = item["q"].AsDouble(0.7),
                Type = (FilterType)item["filterType"].AsInt(0),
            });
            i++;
        }
        return bands;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Preset (*.json)|*.json" };
        if (dialog.ShowDialog() != true || _device is null)
            return;

        var json = Json.Parse(File.ReadAllText(dialog.FileName));
        var bands = BandsFromJson(json["bands"]);
        if (bands.Count != _caps.BandCount)
        {
            MessageBox.Show(
                $"That preset has {bands.Count} bands and this device has {_caps.BandCount}.",
                "Jade Audio Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        double? globalGain = json["globalGain"].Exists ? json["globalGain"].AsDouble() : (double?)null;
        await WriteBandsAsync(bands, globalGain);
        Status($"Imported {Path.GetFileName(dialog.FileName)}");
    }

    private async Task WriteBandsAsync(List<Band> bands, double? globalGain)
    {
        if (_device is null)
            return;

        using var _ = BusyScope();
        for (int i = 0; i < bands.Count; i++)
        {
            bands[i].Index = i;
            await _device.SetBandAsync(bands[i]);
            await Task.Delay(JadeDevice.BandSettle);
        }

        if (globalGain is { } gain && _caps.Has(Reg.GlobalGain))
        {
            double clamped = MathEx.Clamp(gain, _caps.GainRange.Min, _caps.GainRange.Max);
            await _device.SetGlobalGainAsync(Math.Round(clamped, 1));
            _suspend = true;
            PreampSlider.Value = clamped;
            PreampValue.Text = FormatDb(clamped);
            _suspend = false;
        }

        await Task.Delay(200);
        _bands = await _device.ReadAllBandsAsync(_caps.BandCount);
        BuildBandRows();
    }

    // -- backup / restore ----------------------------------------------------

    private async void BackupAll_Click(object sender, RoutedEventArgs e)
    {
        if (_device is null)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "Backup (*.json)|*.json",
            FileName = "presets-backup.json",
        };
        if (dialog.ShowDialog() != true)
            return;

        using var _ = BusyScope();
        try
        {
            byte original = await _device.GetPresetAsync();
            var snapshot = new Dictionary<string, object>();
            foreach (var (name, value) in _caps.Presets)
            {
                Status($"Reading '{name}'...");
                await _device.SetPresetAsync(value);
                await Task.Delay(JadeDevice.PresetSettle);
                snapshot[name] = BandsToJson(await _device.ReadAllBandsAsync(_caps.BandCount));
            }
            await _device.SetPresetAsync(original);
            await Task.Delay(JadeDevice.PresetSettle);

            var json = new Dictionary<string, object>
            {
                ["device"] = _device.ProductName,
                ["presets"] = snapshot,
            };
            File.WriteAllText(dialog.FileName, Json.Write(json, indented: true));
            _bands = await _device.ReadAllBandsAsync(_caps.BandCount);
            BuildBandRows();
            Status($"Backed up every preset to {dialog.FileName}");
        }
        catch (Exception exc)
        {
            Status(exc.Message);
        }
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_device is null)
            return;

        var dialog = new OpenFileDialog { Filter = "Backup (*.json)|*.json" };
        if (dialog.ShowDialog() != true)
            return;

        var json = Json.Parse(File.ReadAllText(dialog.FileName));
        var presets = json["presets"].Properties.ToList();
        if (presets.Count == 0)
        {
            MessageBox.Show("That file has no presets in it.", "Jade Audio Control");
            return;
        }

        if (MessageBox.Show(
                $"Overwrite {presets.Count} presets on the device with this backup?",
                "Jade Audio Control", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        using var _ = BusyScope();
        var byName = _caps.Presets.ToDictionary(p => p.Name, p => p.Value);
        foreach (var entry in presets)
        {
            if (!byName.TryGetValue(entry.Key, out byte value))
                continue;
            Status($"Writing '{entry.Key}'...");
            await _device.SetPresetAsync(value);
            await Task.Delay(JadeDevice.PresetSettle);
            var bands = BandsFromJson(entry.Value);
            for (int i = 0; i < Math.Min(bands.Count, _caps.BandCount); i++)
            {
                bands[i].Index = i;
                await _device.SetBandAsync(bands[i]);
                await Task.Delay(JadeDevice.BandSettle);
            }
        }

        if (PresetBox.SelectedIndex >= 0)
        {
            await _device.SetPresetAsync(_caps.Presets[PresetBox.SelectedIndex].Value);
            await Task.Delay(JadeDevice.PresetSettle);
        }
        _bands = await _device.ReadAllBandsAsync(_caps.BandCount);
        BuildBandRows();
        Status("Backup restored.");
    }

    // -- library -------------------------------------------------------------

    private int DeviceType => CloudClient.DeviceTypeFor(_device?.ProductName ?? "");

    private void BrowseOnline_Click(object sender, RoutedEventArgs e) => NavLibrary.IsChecked = true;

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PresetList is null)
            return;

        _tab = (string)((RadioButton)sender).Tag;
        _page = 1;
        DeleteButton.Visibility = _tab == "personal" ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = false;
        SearchHint.Text = _tab == "share"
            ? "Paste a share code, e.g. FiiOSC-a1b2c3..."
            : "Search presets by name or description";
        _ = LoadLibraryAsync();
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        _page = 1;
        _ = LoadLibraryAsync();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _page = 1;
            _ = LoadLibraryAsync();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadLibraryAsync();

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 1)
        {
            _page--;
            _ = LoadLibraryAsync();
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        _ = LoadLibraryAsync();
    }

    private async Task LoadLibraryAsync()
    {
        using var _ = BusyScope();
        Status("Loading presets...");
        PresetList.Items.Clear();
        ApplyButton.IsEnabled = false;
        _selectedPreset = null;

        try
        {
            string keyword = SearchBox.Text.Trim();
            PresetPage page;

            if (_tab == "share")
            {
                if (keyword.Length == 0)
                {
                    ShowDetail(null);
                    Status("Paste a share code and press Search.");
                    PageLabel.Text = "";
                    return;
                }
                page = new PresetPage(new[] { await _cloud.ByShareCodeAsync(keyword) }, 1);
            }
            else if (keyword.Length > 0 && _tab != "personal")
            {
                page = await _cloud.SearchAsync(keyword, DeviceType);
            }
            else
            {
                page = _tab switch
                {
                    "official" => await _cloud.OfficialAsync(DeviceType, _page, PageSize),
                    "personal" => await _cloud.PersonalAsync(DeviceType),
                    _ => await _cloud.CommunityAsync(DeviceType, _page, PageSize),
                };
            }

            _libraryPresets = page.Presets.ToList();
            foreach (var preset in _libraryPresets)
                PresetList.Items.Add(BuildPresetRow(preset));

            int pages = Math.Max(1, (int)Math.Ceiling(page.Total / (double)PageSize));
            bool paged = _tab is "community" or "official" && keyword.Length == 0;
            PageLabel.Text = paged
                ? $"page {_page} of {pages}   ·   {page.Total} presets"
                : $"{page.Total} preset(s)";
            PrevButton.IsEnabled = paged && _page > 1;
            NextButton.IsEnabled = paged && _page < pages;

            Status(_libraryPresets.Count == 0 ? "Nothing here." : "");
            if (_libraryPresets.Count == 0)
                ShowDetail(null);
            else if (_tab == "share")
                PresetList.SelectedIndex = 0;
        }
        catch (AuthException exc)
        {
            PageLabel.Text = "";
            ShowDetail(null);
            Status(exc.Message);
        }
        catch (Exception exc)
        {
            PageLabel.Text = "";
            ShowDetail(null);
            Status(exc.Message);
        }
    }

    private UIElement BuildPresetRow(CloudPreset preset)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = preset.Name,
            Style = (Style)Application.Current.Resources["H2"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var subtitle = preset.Author.Length > 0 ? $"by {preset.Author}  ·  {preset.Summary}" : preset.Summary;
        left.Children.Add(new TextBlock
        {
            Text = subtitle,
            Style = (Style)Application.Current.Resources["Caption"],
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        grid.Children.Add(left);

        var badge = new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceHigh"],
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(9, 4, 9, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"{preset.Downloads} ↓",
                Style = (Style)Application.Current.Resources["Caption"],
            }
        };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);
        return grid;
    }

    private void Preset_Selected(object sender, SelectionChangedEventArgs e)
    {
        int index = PresetList.SelectedIndex;
        _selectedPreset = index >= 0 && index < _libraryPresets.Count ? _libraryPresets[index] : null;
        ApplyButton.IsEnabled = _selectedPreset is not null && _device is not null;
        DeleteButton.Visibility = _tab == "personal" ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = _tab == "personal" && _selectedPreset is { Id: >= 0 };
        ShowDetail(_selectedPreset);
    }

    private void ShowDetail(CloudPreset? preset)
    {
        DetailPanel.Children.Clear();
        if (preset is null)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = "Pick a preset to see its bands.",
                Style = (Style)Application.Current.Resources["Caption"],
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        void Add(UIElement element) => DetailPanel.Children.Add(element);

        Add(new TextBlock
        {
            Text = preset.Name,
            Style = (Style)Application.Current.Resources["Display"],
            FontSize = 17,
            TextWrapping = TextWrapping.Wrap,
        });

        if (preset.Author.Length > 0)
            Add(new TextBlock
            {
                Text = $"by {preset.Author}  ·  {preset.Downloads} downloads",
                Style = (Style)Application.Current.Resources["Caption"],
                Margin = new Thickness(0, 3, 0, 0),
            });

        if (preset.Description.Length > 0)
            Add(new TextBlock
            {
                Text = preset.Description,
                Style = (Style)Application.Current.Resources["Body"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });

        // The shape of a preset says more than five rows of numbers do, so draw
        // it the same way the equaliser page does. Hit testing is off: this one
        // is for looking at, not dragging.
        if (preset.Bands.Count > 0)
        {
            var preview = new EqCurve
            {
                Height = 150,
                IsHitTestVisible = false,
                GainRange = _caps.BandCount > 0 ? _caps.GainRange : (-12, 12),
            };
            preview.SetBands(preset.Bands);
            Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1A)),
                BorderBrush = (Brush)Application.Current.Resources["Stroke"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(2),
                Child = preview,
            });
        }

        Add(new TextBlock
        {
            Text = $"Global gain {FormatDb(preset.GlobalGain)}",
            Style = (Style)Application.Current.Resources["Caption"],
            Margin = new Thickness(0, 12, 0, 6),
        });

        for (int i = 0; i < preset.Bands.Count; i++)
        {
            var band = preset.Bands[i];
            var color = EqCurve.BandColors[i % EqCurve.BandColors.Length];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            row.Children.Add(new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{band.Frequency,6} Hz   {band.Gain,6:+0.0;-0.0}   Q {band.Q:0.00}",
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["Text"],
            });
            Add(row);
        }

        if (preset.ShareCode.Length > 0)
        {
            Add(new TextBlock
            {
                Text = "Share code",
                Style = (Style)Application.Current.Resources["Caption"],
                Margin = new Thickness(0, 14, 0, 3),
            });
            Add(new TextBox
            {
                Text = preset.ShareCode,
                IsReadOnly = true,
                Style = (Style)Application.Current.Resources["ModernTextBox"],
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                FontSize = 10.5,
            });
        }

        if (preset.DeviceType != DeviceType && _device is not null)
            Add(new TextBlock
            {
                Text = $"Made for device type {preset.DeviceType}, not your {_device.ProductName} ({DeviceType}).",
                Style = (Style)Application.Current.Resources["Caption"],
                Foreground = (Brush)Application.Current.Resources["Danger"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });
    }

    private async void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset is not { } preset || _device is null)
            return;

        var bands = preset.Bands.Take(_caps.BandCount).Select(b => b.Clone()).ToList();
        if (bands.Count == 0)
        {
            MessageBox.Show("That preset has no bands in it.", "Jade Audio Control");
            return;
        }
        while (bands.Count < _caps.BandCount)
            bands.Add(new Band { Index = bands.Count, Frequency = 1000, Gain = 0, Q = 0.7 });

        string note = preset.Bands.Count > _caps.BandCount
            ? $"\n\nIt has {preset.Bands.Count} bands and this device has {_caps.BandCount}; the extra ones are dropped."
            : "";
        string target = PresetBox.SelectedItem?.ToString() ?? "the current preset";

        if (MessageBox.Show(
                $"Write '{preset.Name}' into the '{target}' preset?\n\n" +
                $"That overwrites the bands currently in it.{note}",
                "Jade Audio Control", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await WriteBandsAsync(bands, preset.GlobalGain);
        Status($"Applied '{preset.Name}' to {target}.");
        NavEq.IsChecked = true;
    }

    private void SaveOnline_Click(object sender, RoutedEventArgs e)
    {
        if (_device is null || _bands.Count == 0)
            return;

        if (!_cloud.LoggedIn)
        {
            Status("Sign in first - the preset is saved to your FiiO account.");
            var login = new LoginWindow(_cloud) { Owner = this };
            if (login.ShowDialog() != true)
                return;
            ShowSignedIn();
        }

        double gain = _caps.Has(Reg.GlobalGain) ? Math.Round(PreampSlider.Value, 1) : 0;
        var dialog = new SavePresetWindow(_cloud, _device.ProductName, DeviceType, _bands, gain)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
            return;

        Status("Saved to your account.");
        if (_tab == "personal")
            _ = LoadLibraryAsync();
    }

    private async void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset is not { } preset || preset.Id < 0)
            return;

        if (MessageBox.Show(
                $"Delete '{preset.Name}' from your FiiO account?\n\nThere is no undo.",
                "Jade Audio Control", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        using var _ = BusyScope();
        try
        {
            await _cloud.DeletePresetAsync(preset);
            Status($"Deleted '{preset.Name}'.");
            await LoadLibraryAsync();
        }
        catch (Exception exc)
        {
            Status(exc.Message);
        }
    }

    // -- account -------------------------------------------------------------

    /// <summary>Pick up a "stay signed in" session, quietly if there is none.</summary>
    private async Task RestoreSessionAsync()
    {
        bool hadStoredSession = SessionStore.Exists;
        try
        {
            if (await _cloud.TryRestoreSessionAsync())
                ShowSignedIn();
            else if (hadStoredSession)
                Status("The saved sign-in is no longer accepted - sign in again.");
        }
        catch (Exception)
        {
            // Never block startup on the account server.
        }
    }

    private void ShowSignedIn()
    {
        AccountLine.Text = $"Signed in as {(_cloud.UserName.Length > 0 ? _cloud.UserName : "you")}";
        AccountButton.Content = "Sign out";
    }

    private void ShowSignedOut()
    {
        AccountLine.Text = "Not signed in";
        AccountButton.Content = "Sign in";
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_cloud.LoggedIn)
        {
            _cloud.Logout();
            ShowSignedOut();
            if (_tab == "personal")
                _ = LoadLibraryAsync();
            Status("Signed out. The stored session was erased.");
            return;
        }

        var login = new LoginWindow(_cloud) { Owner = this };
        if (login.ShowDialog() == true)
        {
            ShowSignedIn();
            Status("Signed in.");
            NavLibrary.IsChecked = true;
            TabPersonal.IsChecked = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopSpectrum();
        _device?.Dispose();
        base.OnClosed(e);
    }
}
