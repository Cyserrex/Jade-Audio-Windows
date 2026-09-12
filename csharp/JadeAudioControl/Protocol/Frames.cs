using System;
using System.Collections.Generic;
using System.Threading;

namespace JadeAudioControl.Protocol;

/// <summary>Register ids understood by FiiO / JadeAudio dongles.</summary>
public enum Reg : byte
{
    VolMax = 1,
    VolOutput = 2,
    FilterMode = 4,
    LedLight = 6,
    VolBalance = 7,
    VolOutputSwitch = 8,
    FirmwareVersion = 11,
    ScreenOrientation = 16,
    Language = 17,
    MicSwitch = 18,
    PeqParams = 21,
    PeqPre = 22,
    GlobalGain = 23,
    PeqCount = 24,
    PeqSave = 25,
    PeqSwitch = 26,
    ResetPre = 27,
    ResetAllPre = 28,
    MicMonitorSwitch = 29,
    MicMonitorVol = 30,
    ResetToFactory = 33,
    MicVol = 34,
    PeqName = 48,
    LedCtrl = 51,
    MicNrLevel = 52,
    Gain3d = 53,
    ReverbLevel = 54,
}

public enum FilterType : byte
{
    Peak = 0,
    LowShelf = 1,
    HighShelf = 2,
    BandPass = 3,
    LowPass = 4,
    HighPass = 5,
    AllPass = 6,
}

public enum DacFilterMode : byte
{
    SdSharp = 0,
    Sharp = 1,
    SdSlow = 2,
    Slow = 3,
    NoOversampling = 4,
}

/// <summary>One decoded frame.</summary>
public sealed class Frame
{
    public Frame(byte head, byte start, ushort seq, byte reg, byte[] payload)
    {
        Head = head;
        Start = start;
        Seq = seq;
        Reg = reg;
        Payload = payload;
    }

    public byte Head { get; }
    public byte Start { get; }
    public ushort Seq { get; }
    public byte Reg { get; }
    public byte[] Payload { get; }
}

/// <summary>One parametric EQ band.</summary>
public sealed class Band
{
    public int Index { get; set; }
    public int Frequency { get; set; } = 1000;
    public double Gain { get; set; }
    public double Q { get; set; } = 0.7;
    public FilterType Type { get; set; } = FilterType.Peak;

    public Band Clone() => new Band
    {
        Index = Index, Frequency = Frequency, Gain = Gain, Q = Q, Type = Type
    };
}

/// <summary>
/// Frame encoding and decoding.
///
/// Layout, both directions:
///   [0] head    0xAA write, 0xBB read
///   [1] start   0x0A write, 0x0B read
///   [2..3] seq  rolling counter, big endian
///   [4] reg     register id
///   [5] len     payload length
///   [6..] payload
///   [-2] crc8, [-1] 0xEE
/// </summary>
public static class Frames
{
    public const byte SetHead = 0xAA;
    public const byte SetStart = 0x0A;
    public const byte GetHead = 0xBB;
    public const byte GetStart = 0x0B;
    public const byte Stop = 0xEE;

    private static int _seq;

    private static ushort NextSeq()
    {
        int value = Interlocked.Increment(ref _seq) - 1;
        return (ushort)(value & 0xFFFF);
    }

    public static byte[] Build(byte head, byte start, Reg reg, params byte[] payload)
    {
        payload ??= Array.Empty<byte>();
        ushort seq = NextSeq();
        var body = new byte[6 + payload.Length];
        body[0] = head;
        body[1] = start;
        body[2] = (byte)(seq >> 8);
        body[3] = (byte)(seq & 0xFF);
        body[4] = (byte)reg;
        body[5] = (byte)payload.Length;
        Array.Copy(payload, 0, body, 6, payload.Length);

        var frame = new byte[body.Length + 2];
        Array.Copy(body, frame, body.Length);
        frame[frame.Length - 2] = Crc8.Compute(body);
        frame[frame.Length - 1] = Stop;
        return frame;
    }

    public static byte[] Write(Reg reg, params byte[] payload) => Build(SetHead, SetStart, reg, payload);

    public static byte[] Read(Reg reg, params byte[] payload) => Build(GetHead, GetStart, reg, payload);

    /// <summary>
    /// Pull a frame out of a raw input report, or null if there is not one.
    /// Reports are zero padded and carry a leading report id, so the header is
    /// searched for rather than assumed to sit at offset 0.
    /// </summary>
    public static Frame? Parse(byte[] data)
    {
        for (int i = 0; i + 8 <= data.Length; i++)
        {
            byte head = data[i], start = data[i + 1];
            bool valid = (head == SetHead && start == SetStart) || (head == GetHead && start == GetStart);
            if (!valid)
                continue;

            int length = data[i + 5];
            int end = i + 6 + length;
            if (end + 2 > data.Length || data[end + 1] != Stop)
                continue;

            int bodyLength = end - i;
            // Outgoing frames checksum the whole body; JA11 replies checksum
            // only from the sequence number on. Accept either.
            byte crc = data[end];
            if (crc != Crc8.Compute(data, i, bodyLength) &&
                crc != Crc8.Compute(data, i + 2, bodyLength - 2))
                continue;

            var payload = new byte[length];
            Array.Copy(data, i + 6, payload, 0, length);
            return new Frame(head, start, (ushort)((data[i + 2] << 8) | data[i + 3]), data[i + 4], payload);
        }
        return null;
    }

    // -- value codecs --------------------------------------------------------

    public static byte[] EncodeGain(double db)
    {
        short raw = (short)Math.Round(db * 10);
        return new[] { (byte)(raw >> 8), (byte)(raw & 0xFF) };
    }

    public static double DecodeGain(byte[] data, int offset = 0) =>
        (short)((data[offset] << 8) | data[offset + 1]) / 10.0;

    public static byte[] EncodeU16(int value) =>
        new[] { (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF) };

    public static int DecodeU16(byte[] data, int offset = 0) => (data[offset] << 8) | data[offset + 1];

    public static byte[] EncodeQ(double q) => EncodeU16((int)Math.Round(q * 100));

    public static double DecodeQ(byte[] data, int offset = 0) => DecodeU16(data, offset) / 100.0;

    public static byte[] SetBand(Band band)
    {
        var payload = new List<byte> { (byte)band.Index };
        payload.AddRange(EncodeGain(band.Gain));
        payload.AddRange(EncodeU16(band.Frequency));
        payload.AddRange(EncodeQ(band.Q));
        payload.Add((byte)band.Type);
        return Write(Reg.PeqParams, payload.ToArray());
    }

    public static Band DecodeBand(byte[] payload) => new Band
    {
        Index = payload[0],
        Gain = DecodeGain(payload, 1),
        Frequency = DecodeU16(payload, 3),
        Q = DecodeQ(payload, 5),
        Type = (FilterType)payload[7],
    };
}
