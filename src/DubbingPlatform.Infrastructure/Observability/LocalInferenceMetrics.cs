using System.Diagnostics.Metrics;

namespace DubbingPlatform.Infrastructure.Observability;

/// <summary>
/// Local-inference GPU meters on <c>dubbing-platform</c> (Task 043, optional).
/// Instrument <c>localinference.gpu_exhausted</c> is the scale signal for GPU
/// exhaustion (429/503 with <c>exhausted:true</c>, mapped to
/// <c>PROVIDER_RATE_LIMITED</c> for delayed retry). Tags carry only the bounded
/// <c>device</c> value (cpu/cuda); never tenant/run/model ids.
/// </summary>
public static class LocalInferenceMetrics
{
    public const string MeterName = PlatformMetrics.MeterName;

    public const string GpuExhaustedName = "localinference.gpu_exhausted";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> GpuExhausted =
        Meter.CreateCounter<long>(GpuExhaustedName);

    /// <summary>
    /// Records one GPU-exhaustion signal for <paramref name="device"/>.
    /// </summary>
    public static void Exhausted(string? device)
    {
        var normalized = string.IsNullOrWhiteSpace(device) ? "unknown" : device.Trim().ToLowerInvariant();
        if (normalized.StartsWith("cuda", StringComparison.Ordinal))
        {
            normalized = "cuda";
        }
        else if (!string.Equals(normalized, "cpu", StringComparison.Ordinal))
        {
            normalized = "unknown";
        }

        GpuExhausted.Add(1, new KeyValuePair<string, object?>("device", normalized));
    }
}
