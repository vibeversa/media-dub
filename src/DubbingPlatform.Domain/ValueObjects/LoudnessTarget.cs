using System.Globalization;

namespace DubbingPlatform.Domain.ValueObjects;

/// <summary>
/// Loudness normalization target. Web default -16 LUFS / -1 dBTP, broadcast -23 LUFS / -1 dBTP.
/// </summary>
public sealed record LoudnessTarget
{
    public double IntegratedLufs { get; init; }

    public double TruePeakDbtp { get; init; }

    public LoudnessTarget(double integratedLufs = -16.0, double truePeakDbtp = -1.0)
    {
        IntegratedLufs = integratedLufs;
        TruePeakDbtp = truePeakDbtp;
    }

    public static LoudnessTarget WebDefault { get; } = new(-16.0, -1.0);

    public static LoudnessTarget Broadcast { get; } = new(-23.0, -1.0);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{IntegratedLufs} LUFS, {TruePeakDbtp} dBTP");
}
