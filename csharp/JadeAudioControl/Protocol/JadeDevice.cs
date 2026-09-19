using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JadeAudioControl.Compat;
using JadeAudioControl.Hid;

namespace JadeAudioControl.Protocol;

/// <summary>What the attached dongle actually answers on the wire.</summary>
public sealed class Capabilities
{
    public HashSet<Reg> Supported { get; } = new();
    public int BandCount { get; set; }
    public (double Min, double Max) GainRange { get; set; } = (-12, 12);
    public (double Min, double Max) QRange { get; set; } = (0.25, 8);
    public int MaxVolume { get; set; } = 60;
    public IReadOnlyList<(string Name, byte Value)> Presets { get; set; } = Array.Empty<(string, byte)>();
    public IReadOnlyList<FilterType> FilterTypes { get; set; } =
        new[] { FilterType.Peak, FilterType.LowShelf, FilterType.HighShelf };

    public bool Has(Reg reg) => Supported.Contains(reg);
}

/// <summary>
/// A connected dongle. Every exchange is serialised behind one lock so that
/// concurrent UI actions cannot interleave requests on the wire.
/// </summary>
public sealed class JadeDevice : IDisposable
{
    public const ushort FiioVendorId = 0x2972;

    /// <summary>The firmware needs a moment after a preset switch before its band registers settle.</summary>
    public static readonly TimeSpan PresetSettle = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan BandSettle = TimeSpan.FromMilliseconds(250);

    private static readonly Dictionary<string, byte> ReportIds = new()
    {
        ["FIIO KA17"] = 1,
        ["JadeAudio JA11"] = 2,
        ["USB20"] = 0,
    };

    private static readonly (string Name, byte Value)[] Ja11Presets =
    {
        ("Vocal", 0), ("Classic", 1), ("Bass", 2), ("USER1", 3), ("EQ Off", 4)
    };

    private readonly HidDevice _hid;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string ProductName { get; }
    public byte ReportId { get; }
    public HidDeviceInfo Info => _hid.Info;

    private JadeDevice(HidDevice hid)
    {
        _hid = hid;
        ProductName = hid.Info.Product;
        ReportId = ReportIds.TryGetValue(ProductName, out var id) ? id : (byte)7;
    }

    public static IReadOnlyList<HidDeviceInfo> List() => HidDevice.Enumerate(FiioVendorId);

    public static JadeDevice Open()
    {
        var candidates = List();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No FiiO or JadeAudio dongle found.");
        // Prefer an interface that declares reports big enough for our frames.
        var chosen = candidates.OrderByDescending(c => c.OutputReportLength).First();
        return new JadeDevice(HidDevice.Open(chosen));
    }

    // -- raw exchange --------------------------------------------------------

    public async Task SendAsync(byte[] frame, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await SendLockedAsync(frame, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendLockedAsync(byte[] frame, CancellationToken token)
    {
        var report = new byte[_hid.OutputReportLength];
        report[0] = ReportId;
        Array.Copy(frame, 0, report, 1, Math.Min(frame.Length, report.Length - 1));
        await _hid.WriteAsync(report, token).ConfigureAwait(false);
    }

    /// <summary>Send a frame and wait for the reply carrying the same register.</summary>
    public async Task<Frame> RequestAsync(byte[] frame, TimeSpan? timeout = null, CancellationToken token = default)
    {
        var budget = timeout ?? TimeSpan.FromMilliseconds(1200);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte want = frame[4];
            // Drop anything stale before asking.
            while (await _hid.ReadAsync(TimeSpan.FromMilliseconds(1), token).ConfigureAwait(false) is not null) { }

            await SendLockedAsync(frame, token).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + budget;
            while (DateTime.UtcNow < deadline)
            {
                var data = await _hid.ReadAsync(deadline - DateTime.UtcNow, token).ConfigureAwait(false);
                if (data is null)
                    break;
                var reply = Frames.Parse(data);
                if (reply is { } value && value.Reg == want)
                    return value;
            }
            throw new TimeoutException($"No reply for register {(Reg)want}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    // -- typed accessors -----------------------------------------------------

    public async Task<string> GetFirmwareAsync()
    {
        var payload = (await RequestAsync(Frames.Read(Reg.FirmwareVersion))).Payload;
        if (payload.Length >= 2)
            return payload.Length > 2
                ? $"{payload[0]}.{payload[1]}.{payload[2]}"
                : $"{payload[0]}.{payload[1]}";
        return BitConverter.ToString(payload).Replace("-", string.Empty);
    }

    /// <summary>The raw version bytes, for comparing against a published list.</summary>
    public async Task<(int Major, int Minor)> GetFirmwareVersionAsync()
    {
        var payload = (await RequestAsync(Frames.Read(Reg.FirmwareVersion))).Payload;
        return payload.Length >= 2 ? (payload[0], payload[1]) : (-1, -1);
    }

    public async Task<byte> GetPresetAsync() =>
        (await RequestAsync(Frames.Read(Reg.PeqPre))).Payload[0];

    public Task SetPresetAsync(byte preset) =>
        SendAsync(Frames.Write(Reg.PeqPre, preset));

    public async Task<int> GetBandCountAsync() =>
        (await RequestAsync(Frames.Read(Reg.PeqCount))).Payload[0];

    public async Task<Band> GetBandAsync(int index) =>
        Frames.DecodeBand((await RequestAsync(Frames.Read(Reg.PeqParams, (byte)index))).Payload);

    public Task SetBandAsync(Band band) => SendAsync(Frames.SetBand(band));

    public async Task<double> GetGlobalGainAsync() =>
        Frames.DecodeGain((await RequestAsync(Frames.Read(Reg.GlobalGain))).Payload);

    public Task SetGlobalGainAsync(double db) =>
        SendAsync(Frames.Write(Reg.GlobalGain, Frames.EncodeGain(db)));

    public async Task<int> GetVolumeAsync() =>
        (await RequestAsync(Frames.Read(Reg.VolOutput))).Payload[0];

    public Task SetVolumeAsync(int value) =>
        SendAsync(Frames.Write(Reg.VolOutput, (byte)MathEx.Clamp(value, 0, 255)));

    public async Task<DacFilterMode> GetFilterModeAsync() =>
        (DacFilterMode)(await RequestAsync(Frames.Read(Reg.FilterMode))).Payload[0];

    public Task SetFilterModeAsync(DacFilterMode mode) =>
        SendAsync(Frames.Write(Reg.FilterMode, (byte)mode));

    public async Task<List<Band>> ReadAllBandsAsync(int count)
    {
        var bands = new List<Band>(count);
        for (int i = 0; i < count; i++)
            bands.Add(await GetBandAsync(i).ConfigureAwait(false));
        return bands;
    }

    // -- capability probe ----------------------------------------------------

    private static readonly Reg[] ProbeOrder =
    {
        Reg.FirmwareVersion, Reg.PeqPre, Reg.PeqCount, Reg.PeqSwitch, Reg.GlobalGain,
        Reg.VolOutput, Reg.VolMax, Reg.VolOutputSwitch, Reg.VolBalance,
        Reg.FilterMode, Reg.LedLight, Reg.MicSwitch, Reg.MicVol, Reg.Gain3d, Reg.ReverbLevel,
    };

    /// <summary>
    /// Ask the device which registers it answers. The official app gets this
    /// from a server-side table; probing keeps us honest about the hardware in
    /// front of us, so other models get a sensible subset instead of dead knobs.
    /// </summary>
    public async Task<Capabilities> ProbeAsync()
    {
        var caps = new Capabilities { Presets = Ja11Presets };
        var quick = TimeSpan.FromMilliseconds(200);

        foreach (var reg in ProbeOrder)
        {
            try
            {
                await RequestAsync(Frames.Read(reg), quick).ConfigureAwait(false);
                caps.Supported.Add(reg);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }

        try
        {
            await RequestAsync(Frames.Read(Reg.PeqParams, 0), quick).ConfigureAwait(false);
            caps.Supported.Add(Reg.PeqParams);
        }
        catch (TimeoutException) { }

        if (caps.Has(Reg.PeqCount))
        {
            try { caps.BandCount = await GetBandCountAsync().ConfigureAwait(false); }
            catch (TimeoutException) { caps.BandCount = 0; }
        }

        return caps;
    }

    public void Dispose()
    {
        _hid.Dispose();
        _gate.Dispose();
    }
}
