using System.Globalization;

namespace DubbingPlatform.Domain.ValueObjects;

/// <summary>
/// Timeline range in integer milliseconds.
/// </summary>
public readonly record struct TimeRange
{
    public int StartMs { get; init; }

    public int EndMs { get; init; }

    public TimeRange(int startMs, int endMs)
    {
        if (startMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startMs), "StartMs must be >= 0.");
        }

        if (endMs <= startMs)
        {
            throw new ArgumentOutOfRangeException(nameof(endMs), "EndMs must be greater than StartMs.");
        }

        StartMs = startMs;
        EndMs = endMs;
    }

    public int DurationMs => EndMs - StartMs;

    public int GetDurationMs() => DurationMs;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{StartMs}..{EndMs})");
}
