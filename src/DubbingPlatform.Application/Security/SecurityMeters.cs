using System.Diagnostics.Metrics;

namespace DubbingPlatform.Application.Security;

/// <summary>
/// Security meters. Counter names are frozen: renaming breaks dashboards
/// (Task 38). The <c>security.cross_tenant_rejected</c> counter increments on
/// every denied cross-tenant access (API ownership checks via
/// <c>TenantGuard</c>, storage ownership checks, and consumer tenant
/// validation). No tenant ids or resource ids appear as tags (cardinality).
/// </summary>
public static class SecurityMeters
{
    public const string MeterName = "DubbingPlatform.Security";

    public const string CrossTenantRejectedMetricName = "security.cross_tenant_rejected";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> CrossTenantRejects =
        Meter.CreateCounter<long>(CrossTenantRejectedMetricName);
}
