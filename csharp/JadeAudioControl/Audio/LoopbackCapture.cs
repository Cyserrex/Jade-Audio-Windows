using System;
using System.Runtime.InteropServices;
using System.Threading;
using static JadeAudioControl.Audio.WasapiInterop;

namespace JadeAudioControl.Audio;

/// <summary>
/// Listens to what a playback endpoint is sending out, using WASAPI loopback.
///
/// Nothing is recorded: samples land in a small ring buffer that is overwritten
/// continuously and read only to draw the spectrum. Loopback is a shared-mode
/// capture of the endpoint's own mix, so it needs no driver and no admin, and
/// it goes quiet - rather than failing - whenever nothing is playing.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private const int RingSize = 16384;

    private readonly float[] _ring = new float[RingSize];
    private readonly object _gate = new();
    private int _writeIndex;

    private Thread? _thread;
    private volatile bool _running;

    public int SampleRate { get; private set; } = 48000;
    public string EndpointName { get; private set; } = "";
    public string? Error { get; private set; }
    public bool IsRunning => _running;

    /// <summary>
    /// Start listening. <paramref name="preferredName"/> picks the endpoint whose
    /// name mentions it - the dongle rather than whatever Windows defaults to -
    /// falling back to the default endpoint when there is no match.
    /// </summary>
    public bool Start(string? preferredName = null)
    {
        if (_running)
            return true;

        Error = null;
        _running = true;
        var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() => Run(preferredName, ready))
        {
            IsBackground = true,
            Name = "Loopback capture",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        ready.Wait(3000);
        if (Error is not null)
            _running = false;
        return _running;
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;
    }

    /// <summary>
    /// Copy the most recent samples, oldest first. Returns false while too
    /// little has arrived to be worth transforming.
    /// </summary>
    public bool TryRead(float[] destination)
    {
        if (destination.Length > RingSize)
            return false;

        lock (_gate)
        {
            int start = _writeIndex - destination.Length;
            while (start < 0)
                start += RingSize;
            for (int i = 0; i < destination.Length; i++)
                destination[i] = _ring[(start + i) % RingSize];
        }
        return true;
    }

    // -- the capture thread --------------------------------------------------

    private void Run(string? preferredName, ManualResetEventSlim ready)
    {
        IAudioClient? client = null;
        IntPtr formatPtr = IntPtr.Zero;
        bool started = false;

        CoInitializeEx(IntPtr.Zero, 0); // MTA
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            var device = PickEndpoint(enumerator, preferredName, out string name);
            if (device is null)
            {
                Error = "No active playback device to listen to.";
                return;
            }
            EndpointName = name;

            var audioClientIid = typeof(IAudioClient).GUID;
            Check(device.Activate(ref audioClientIid, CLSCTX_ALL, IntPtr.Zero, out object clientObject),
                  "activate the audio client");
            client = (IAudioClient)clientObject;

            Check(client.GetMixFormat(out formatPtr), "read the mix format");
            var format = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
            SampleRate = format.nSamplesPerSec;
            int channels = format.nChannels;
            bool isFloat = IsFloat(format, formatPtr);

            if (!isFloat && format.wBitsPerSample != 16)
            {
                Error = $"Unsupported mix format ({format.wBitsPerSample}-bit).";
                return;
            }

            // 200 ms of buffer: long enough that a late poll loses nothing.
            Check(client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                                    2_000_000, 0, formatPtr, IntPtr.Zero),
                  "initialise loopback capture");

            var captureIid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref captureIid, out object captureObject), "get the capture client");
            var capture = (IAudioCaptureClient)captureObject;

            Check(client.Start(), "start capture");
            started = true;
            ready.Set();

            int frameBytes = format.nBlockAlign;
            while (_running)
            {
                if (capture.GetNextPacketSize(out int packet) != S_OK)
                    break;

                if (packet == 0)
                {
                    Thread.Sleep(8);
                    continue;
                }

                while (packet > 0 && _running)
                {
                    if (capture.GetBuffer(out IntPtr data, out int frames, out uint flags, out _, out _) != S_OK)
                        break;

                    // AUDCLNT_BUFFERFLAGS_SILENT: the buffer is not filled in.
                    bool silent = (flags & 0x2) != 0;
                    if (frames > 0)
                        Append(data, frames, channels, isFloat, silent);

                    capture.ReleaseBuffer(frames);
                    if (capture.GetNextPacketSize(out packet) != S_OK)
                        break;
                }
            }
        }
        catch (Exception exc)
        {
            Error ??= exc.Message;
        }
        finally
        {
            ready.Set();
            try
            {
                if (started)
                    client?.Stop();
            }
            catch (Exception)
            {
            }
            if (formatPtr != IntPtr.Zero)
                CoTaskMemFree(formatPtr);
            if (client is not null)
                Marshal.ReleaseComObject(client);
            CoUninitialize();
            _running = false;
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr != S_OK)
            throw new InvalidOperationException($"Could not {what} (0x{hr:X8}).");
    }

    private static bool IsFloat(WAVEFORMATEX format, IntPtr formatPtr)
    {
        if (format.wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
            return true;
        if (format.wFormatTag != WAVE_FORMAT_EXTENSIBLE)
            return false;

        // WAVEFORMATEXTENSIBLE: the real format sits in a SubFormat GUID after
        // the 18-byte header plus the union.
        var subFormat = new byte[16];
        Marshal.Copy(IntPtr.Add(formatPtr, 18 + 6), subFormat, 0, 16);
        return new Guid(subFormat) == new Guid("00000003-0000-0010-8000-00aa00389b71");
    }

    /// <summary>Fold the frame down to mono and push it into the ring.</summary>
    private void Append(IntPtr data, int frames, int channels, bool isFloat, bool silent)
    {
        lock (_gate)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                if (!silent)
                {
                    for (int channel = 0; channel < channels; channel++)
                    {
                        if (isFloat)
                        {
                            sum += Marshal.PtrToStructure<float>(
                                IntPtr.Add(data, (frame * channels + channel) * 4));
                        }
                        else
                        {
                            short raw = Marshal.ReadInt16(data, (frame * channels + channel) * 2);
                            sum += raw / 32768f;
                        }
                    }
                    sum /= channels;
                }

                _ring[_writeIndex] = sum;
                _writeIndex = (_writeIndex + 1) % RingSize;
            }
        }
    }

    private static IMMDevice? PickEndpoint(IMMDeviceEnumerator enumerator, string? preferredName, out string name)
    {
        name = "";
        IMMDevice? fallback = null;
        string fallbackName = "";

        if (enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out var collection) == S_OK
            && collection.GetCount(out int count) == S_OK)
        {
            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out var candidate) != S_OK)
                    continue;

                string candidateName = FriendlyName(candidate);
                if (!string.IsNullOrEmpty(preferredName) && Mentions(candidateName, preferredName!))
                {
                    name = candidateName;
                    return candidate;
                }
            }
        }

        if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out fallback) == S_OK)
        {
            fallbackName = FriendlyName(fallback);
            name = fallbackName;
            return fallback;
        }
        return null;
    }

    /// <summary>"Speakers (JadeAudio JA11)" should match a device called "JadeAudio JA11".</summary>
    private static bool Mentions(string endpointName, string product)
    {
        if (endpointName.IndexOf(product, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        foreach (string word in product.Split(' '))
        {
            if (word.Length >= 4 && endpointName.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private static string FriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != S_OK)
                return "";

            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var value) != S_OK)
                return "";

            string text = value.p != IntPtr.Zero ? Marshal.PtrToStringUni(value.p) ?? "" : "";
            PropVariantClear(ref value);
            return text;
        }
        catch (Exception)
        {
            return "";
        }
    }

    public void Dispose() => Stop();
}
