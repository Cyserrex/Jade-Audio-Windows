using System;
using System.Collections.Generic;
using System.Globalization;

namespace JadeAudioControl.Controls;

/// <summary>One named stretch of the spectrum, and what moving it tends to do.</summary>
public sealed class FrequencyZone
{
    public FrequencyZone(string name, string shortName, double low, double high, string effect)
    {
        Name = name;
        ShortName = shortName;
        Low = low;
        High = high;
        Effect = effect;
    }

    public string Name { get; }
    /// <summary>Used when the zone is too narrow on screen for the full name.</summary>
    public string ShortName { get; }
    public double Low { get; }
    public double High { get; }
    /// <summary>What a listener hears when this range moves.</summary>
    public string Effect { get; }

    public string Range => High >= 1000
        ? $"{Format(Low)}-{Format(High)}"
        : $"{Low:0}-{High:0} Hz";

    private static string Format(double hz) =>
        hz >= 1000
            ? (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " kHz"
            : hz.ToString("0", CultureInfo.InvariantCulture) + " Hz";
}

/// <summary>
/// The conventional names for parts of the audible band, so the plot says what
/// a region is for rather than leaving the reader to know it already.
/// </summary>
public static class FrequencyZones
{
    public static readonly IReadOnlyList<FrequencyZone> All = new[]
    {
        new FrequencyZone("Sub-bass", "Sub", 20, 60,
            "weight and rumble you feel more than hear"),
        new FrequencyZone("Bass", "Bass", 60, 250,
            "the body of kick, bass guitar and low synths"),
        new FrequencyZone("Low mids", "Low mid", 250, 500,
            "warmth; too much here is the classic muddy sound"),
        new FrequencyZone("Midrange", "Mid", 500, 2000,
            "where most voices and instruments actually live"),
        new FrequencyZone("Upper mids", "Upper mid", 2000, 6000,
            "presence and bite; too much is shouty and tiring"),
        new FrequencyZone("Treble", "Treble", 6000, 20000,
            "detail, cymbals and air; too much turns sibilant"),
    };

    public static FrequencyZone For(double hz)
    {
        foreach (var zone in All)
        {
            if (hz < zone.High)
                return zone;
        }
        return All[All.Count - 1];
    }

    /// <summary>"Midrange - where most voices and instruments actually live".</summary>
    public static string Describe(double hz)
    {
        var zone = For(hz);
        return $"{zone.Name} ({zone.Range}) - {zone.Effect}";
    }
}
