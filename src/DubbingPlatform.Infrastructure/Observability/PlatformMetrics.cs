using System.Diagnostics.Metrics;

namespace DubbingPlatform.Infrastructure.Observability;

/// <summary>
/// Platform SLO metrics on Meter <c>dubbing-platform</c>. Instrument names are
/// frozen: renaming breaks dashboards/alerts (Task 38).
/// Cardinality policy: the <c>tenant</c> tag (GUID <c>N</c> format, no PII)
/// appears only on low-cardinality aggregated counters below. Never attach
/// segment, execution, artifact, or user ids as tags (explosion). Histograms
/// carry only bounded tags (<c>provider</c>, <c>stage</c>, <c>route</c>) and
/// never tenant/segment ids.
/// Canonical aliases that already exist on other meters are NOT duplicated
/// here (duplicate instrument names across meters would double-export):
/// <c>quota.rejections</c> (<c>DubbingPlatform.Quota</c>),
/// <c>ratelimit.rejections</c> (<c>DubbingPlatform.RateLimit</c>),
/// <c>messaging.schema_mismatch_total</c> (<c>DubbingPlatform.Messaging</c>),
/// <c>dlq.depth</c> (<c>DubbingPlatform.Messaging</c>),
/// <c>storage.orphans_detected</c> (<c>DubbingPlatform.Storage</c>),
/// <c>cost.reserved/cost.reconciled</c> (<c>DubbingPlatform.Cost</c>).
/// Use the forwarding helpers (<c>QuotaRejected</c>, <c>RateLimited</c>,
/// <c>DlqDepthAdd</c>, <c>SchemaMismatch</c>) so both the canonical meter and
/// this facade stay consistent.
/// </summary>
public static class PlatformMetrics
{
    public const string MeterName = "dubbing-platform";

    public const string ProjectsStartedName = "projects.started";

    public const string ProjectsCompletedName = "projects.completed";

    public const string ProjectsFailedName = "projects.failed";

    public const string ProjectsCancelledName = "projects.cancelled";

    public const string StagesStartedName = "stages.started";

    public const string StagesCompletedName = "stages.completed";

    public const string StagesFailedName = "stages.failed";

    public const string StagesRetryName = "stages.retry";

    public const string ProviderCallsName = "provider.calls";

    public const string ProviderErrorsName = "provider.errors";

    public const string ProviderCostName = "provider.cost";

    public const string ReviewsOpenedName = "reviews.opened";

    public const string ReviewsResolvedName = "reviews.resolved";

    public const string ExportsGeneratedName = "exports.generated";

    public const string ExportsFailedName = "exports.failed";

    public const string StorageOrphansName = "storage.orphans";

    public const string LeasesRecoveredName = "leases.recovered";

    public const string ApiLatencyName = "api.latency";

    public const string ProviderLatencyName = "provider.latency";

    public const string SegmentDurationName = "segment.duration";

    // Canonical aliases (no new instruments; see class doc).
    public const string QuotaRejectionsName = "quota.rejections";

    public const string RateLimitRejectionsName = "ratelimit.rejections";

    public const string SchemaMismatchName = "messaging.schema_mismatch_total";

    public const string DlqDepthName = "dlq.depth";

    public const string StorageOrphansDetectedName = "storage.orphans_detected";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> ProjectsStarted =
        Meter.CreateCounter<long>(ProjectsStartedName);

    public static readonly Counter<long> ProjectsCompleted =
        Meter.CreateCounter<long>(ProjectsCompletedName);

    public static readonly Counter<long> ProjectsFailed =
        Meter.CreateCounter<long>(ProjectsFailedName);

    public static readonly Counter<long> ProjectsCancelled =
        Meter.CreateCounter<long>(ProjectsCancelledName);

    public static readonly Counter<long> StagesStarted =
        Meter.CreateCounter<long>(StagesStartedName);

    public static readonly Counter<long> StagesCompleted =
        Meter.CreateCounter<long>(StagesCompletedName);

    public static readonly Counter<long> StagesFailed =
        Meter.CreateCounter<long>(StagesFailedName);

    public static readonly Counter<long> StagesRetry =
        Meter.CreateCounter<long>(StagesRetryName);

    public static readonly Counter<long> ProviderCalls =
        Meter.CreateCounter<long>(ProviderCallsName);

    public static readonly Counter<long> ProviderErrors =
        Meter.CreateCounter<long>(ProviderErrorsName);

    public static readonly Counter<double> ProviderCost =
        Meter.CreateCounter<double>(ProviderCostName);

    public static readonly Counter<long> ReviewsOpened =
        Meter.CreateCounter<long>(ReviewsOpenedName);

    public static readonly Counter<long> ReviewsResolved =
        Meter.CreateCounter<long>(ReviewsResolvedName);

    public static readonly Counter<long> ExportsGenerated =
        Meter.CreateCounter<long>(ExportsGeneratedName);

    public static readonly Counter<long> ExportsFailed =
        Meter.CreateCounter<long>(ExportsFailedName);

    public static readonly Counter<long> StorageOrphans =
        Meter.CreateCounter<long>(StorageOrphansName);

    public static readonly Counter<long> LeasesRecovered =
        Meter.CreateCounter<long>(LeasesRecoveredName);

    public static readonly Histogram<double> ApiLatency =
        Meter.CreateHistogram<double>(ApiLatencyName, "ms");

    public static readonly Histogram<double> ProviderLatency =
        Meter.CreateHistogram<double>(ProviderLatencyName, "ms");

    public static readonly Histogram<double> SegmentDuration =
        Meter.CreateHistogram<double>(SegmentDurationName, "ms");

    private static KeyValuePair<string, object?> TenantTag(Guid tenantId)
    {
        return new KeyValuePair<string, object?>("tenant", tenantId.ToString("N"));
    }

    public static void ProjectStarted(Guid tenantId)
    {
        ProjectsStarted.Add(1, TenantTag(tenantId));
    }

    public static void ProjectCompleted(Guid tenantId)
    {
        ProjectsCompleted.Add(1, TenantTag(tenantId));
    }

    public static void ProjectFailed(Guid tenantId)
    {
        ProjectsFailed.Add(1, TenantTag(tenantId));
    }

    public static void ProjectCancelled(Guid tenantId)
    {
        ProjectsCancelled.Add(1, TenantTag(tenantId));
    }

    public static void StageStarted(Guid tenantId, string? stage = null)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            StagesStarted.Add(1, TenantTag(tenantId));
            return;
        }

        StagesStarted.Add(1, TenantTag(tenantId), new KeyValuePair<string, object?>("stage", stage.Trim()));
    }

    public static void StageCompleted(Guid tenantId, string? stage = null)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            StagesCompleted.Add(1, TenantTag(tenantId));
            return;
        }

        StagesCompleted.Add(1, TenantTag(tenantId), new KeyValuePair<string, object?>("stage", stage.Trim()));
    }

    public static void StageFailed(Guid tenantId, string? stage = null)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            StagesFailed.Add(1, TenantTag(tenantId));
            return;
        }

        StagesFailed.Add(1, TenantTag(tenantId), new KeyValuePair<string, object?>("stage", stage.Trim()));
    }

    public static void StageRetry(Guid tenantId, string? stage = null)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            StagesRetry.Add(1, TenantTag(tenantId));
            return;
        }

        StagesRetry.Add(1, TenantTag(tenantId), new KeyValuePair<string, object?>("stage", stage.Trim()));
    }

    public static void ProviderCall(Guid tenantId, string? provider = null, string? model = null)
    {
        var tags = BuildProviderTags(tenantId, provider, model);
        ProviderCalls.Add(1, tags);
    }

    public static void ProviderError(Guid tenantId, string? provider = null, string? model = null)
    {
        var tags = BuildProviderTags(tenantId, provider, model);
        ProviderErrors.Add(1, tags);
    }

    public static void ProviderCostObserved(Guid tenantId, double costUsd, string? provider = null)
    {
        if (double.IsNaN(costUsd) || double.IsInfinity(costUsd) || costUsd < 0.0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            ProviderCost.Add(costUsd, TenantTag(tenantId));
            return;
        }

        ProviderCost.Add(costUsd, TenantTag(tenantId), new KeyValuePair<string, object?>("provider", provider.Trim()));
    }

    public static void ReviewOpened(Guid tenantId)
    {
        ReviewsOpened.Add(1, TenantTag(tenantId));
    }

    public static void ReviewResolved(Guid tenantId)
    {
        ReviewsResolved.Add(1, TenantTag(tenantId));
    }

    public static void ExportGenerated(Guid tenantId)
    {
        ExportsGenerated.Add(1, TenantTag(tenantId));
    }

    public static void ExportFailed(Guid tenantId)
    {
        ExportsFailed.Add(1, TenantTag(tenantId));
    }

    public static void StorageOrphanObserved(Guid tenantId, long count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        StorageOrphans.Add(count, TenantTag(tenantId));
    }

    public static void LeaseRecovered(Guid tenantId, long count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        LeasesRecovered.Add(count, TenantTag(tenantId));
    }

    public static void ObserveApiLatency(double milliseconds, string? route = null)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(route))
        {
            ApiLatency.Record(milliseconds);
            return;
        }

        ApiLatency.Record(milliseconds, new KeyValuePair<string, object?>("route", route.Trim()));
    }

    public static void ObserveProviderLatency(double milliseconds, string? provider = null)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            ProviderLatency.Record(milliseconds);
            return;
        }

        ProviderLatency.Record(milliseconds, new KeyValuePair<string, object?>("provider", provider.Trim()));
    }

    public static void ObserveSegmentDuration(double milliseconds, string? stage = null)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(stage))
        {
            SegmentDuration.Record(milliseconds);
            return;
        }

        SegmentDuration.Record(milliseconds, new KeyValuePair<string, object?>("stage", stage.Trim()));
    }

    private static KeyValuePair<string, object?>[] BuildProviderTags(Guid tenantId, string? provider, string? model)
    {
        var tenant = TenantTag(tenantId);
        if (string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(model))
        {
            return [tenant];
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return [tenant, new KeyValuePair<string, object?>("provider", provider!.Trim())];
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            return [tenant, new KeyValuePair<string, object?>("model", model!.Trim())];
        }

        return [tenant, new KeyValuePair<string, object?>("provider", provider!.Trim()), new KeyValuePair<string, object?>("model", model!.Trim())];
    }
}
