using System.Diagnostics.Metrics;
using DubbingPlatform.Contracts.Messages;

namespace DubbingPlatform.Infrastructure.Messaging;

/// <summary>
/// Messaging meters. Counter names are frozen: renaming breaks dashboards.
/// <c>messaging.schema_mismatch_total</c> is owned by the version policy;
/// cross-tenant rejects use <c>messaging.cross_tenant_reject_total</c>;
/// <c>dlq.depth</c> tracks explicit parks into <c>_skipped</c>/<c>_error</c>
/// (broker queue depth remains the alerting source of truth in Task 38).
/// </summary>
public static class MessagingMeters
{
    public const string MeterName = "DubbingPlatform.Messaging";

    public const string CrossTenantRejectMetricName = "messaging.cross_tenant_reject_total";

    public const string DlqDepthMetricName = "dlq.depth";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> SchemaMismatches =
        Meter.CreateCounter<long>(MessageVersionPolicy.SchemaMismatchMetricName);

    public static readonly Counter<long> CrossTenantRejects =
        Meter.CreateCounter<long>(CrossTenantRejectMetricName);

    public static readonly UpDownCounter<long> DlqDepth =
        Meter.CreateUpDownCounter<long>(DlqDepthMetricName);
}
