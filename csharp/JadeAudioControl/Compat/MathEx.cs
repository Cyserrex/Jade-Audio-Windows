namespace JadeAudioControl.Compat;

/// <summary>
/// Math.Clamp arrived in .NET Core 2.0 and never made it into .NET Framework,
/// so this project uses these instead.
/// </summary>
internal static class MathEx
{
    public static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    public static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
}
