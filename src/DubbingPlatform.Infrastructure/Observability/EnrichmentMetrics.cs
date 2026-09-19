using System.Diagnostics.Metrics;

namespace DubbingPlatform.Infrastructure.Observability;

/// <summary>
/// Isolated enrichment meters on <c>dubbing-platform</c>.
/// Instrument <c>enrichment.failed</c> carries only the bounded <c>kind</c> tag
/// (<c>VideoIntelligence|LipSync</c>); never tenant/run/artifact ids.
/// Success is observed via existing <c>provider.calls</c>; only failures need a
/// dedicated enrichment counter for isolated-failure alerting (Task 38).
/// </summary>
public static class EnrichmentMetrics
{
    public const string MeterName = PlatformMetrics.MeterName;

    public const string EnrichmentFailedName = "enrichment.failed";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> EnrichmentFailed =
        Meter.CreateCounter<long>(EnrichmentFailedName);

    /// <summary>
    /// Records one isolated enrichment failure for <paramref name="kind"/>.
    /// </summary>
    public static void Failed(string? kind)
    {
        var normalized = string.IsNullOrWhiteSpace(kind) ? "Unknown" : kind.Trim();
        EnrichmentFailed.Add(1, new KeyValuePair<string, object?>("kind", normalized));
    }
}
