using System.Diagnostics.Metrics;

namespace DubbingPlatform.Infrastructure.Storage;

/// <summary>
/// Storage meters. Counter names are frozen: renaming breaks dashboards.
/// <c>storage.orphans_detected</c> counts blobs quarantined plus content rows
/// marked orphaned by the reconciler (Task 38 alerts on growth).
/// </summary>
public static class StorageMeters
{
    public const string MeterName = "DubbingPlatform.Storage";

    public const string OrphansDetectedMetricName = "storage.orphans_detected";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> OrphansDetected =
        Meter.CreateCounter<long>(OrphansDetectedMetricName);
}
