using System.Globalization;

namespace DubbingPlatform.Domain.ValueObjects;

/// <summary>
/// Timing placement window. Preferred tolerance 50ms, max tolerance 100ms,
/// max rate change 15%, max stretch factor 1.15x.
/// </summary>
public sealed record TimingWindow
{
    public int TargetOnsetMs { get; init; }

    public int TargetDurationMs { get; init; }

    public int AllowableLeadMs { get; init; }

    public int AllowableLagMs { get; init; }

    public double MaxRateChangePercent { get; init; }

    public double MaxStretchFactor { get; init; }

    public int PreferredToleranceMs { get; init; }

    public int MaxToleranceMs { get; init; }

    public TimingWindow(
        int targetOnsetMs = 0,
        int targetDurationMs = 0,
        int allowableLeadMs = 50,
        int allowableLagMs = 50,
        double maxRateChangePercent = 15.0,
        double maxStretchFactor = 1.15,
        int preferredToleranceMs = 50,
        int maxToleranceMs = 100)
    {
        if (targetOnsetMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetOnsetMs), "TargetOnsetMs must be >= 0.");
        }

        if (targetDurationMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetDurationMs), "TargetDurationMs must be >= 0.");
        }

        if (allowableLeadMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allowableLeadMs), "AllowableLeadMs must be >= 0.");
        }

        if (allowableLagMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allowableLagMs), "AllowableLagMs must be >= 0.");
        }

        if (maxRateChangePercent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRateChangePercent), "MaxRateChangePercent must be > 0.");
        }

        if (maxStretchFactor < 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStretchFactor), "MaxStretchFactor must be >= 1.0.");
        }

        if (preferredToleranceMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredToleranceMs), "PreferredToleranceMs must be >= 0.");
        }

        if (maxToleranceMs < preferredToleranceMs)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToleranceMs), "MaxToleranceMs must be >= PreferredToleranceMs.");
        }

        TargetOnsetMs = targetOnsetMs;
        TargetDurationMs = targetDurationMs;
        AllowableLeadMs = allowableLeadMs;
        AllowableLagMs = allowableLagMs;
        MaxRateChangePercent = maxRateChangePercent;
        MaxStretchFactor = maxStretchFactor;
        PreferredToleranceMs = preferredToleranceMs;
        MaxToleranceMs = maxToleranceMs;
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Onset={TargetOnsetMs} Duration={TargetDurationMs} Lead={AllowableLeadMs} Lag={AllowableLagMs}");
}
