using System.Diagnostics.Metrics;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Provider meters. Counter names are frozen: renaming breaks dashboards.
/// </summary>
public static class ProviderMeters
{
    public const string MeterName = "DubbingPlatform.Providers";

    public const string PolicyDeniedMetricName = "provider.policy_denied_total";

    public const string RouteSelectedMetricName = "provider.route_selected_total";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> PolicyDenied =
        Meter.CreateCounter<long>(PolicyDeniedMetricName);

    public static readonly Counter<long> RouteSelected =
        Meter.CreateCounter<long>(RouteSelectedMetricName);
}
