using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DubbingPlatform.Infrastructure.Observability;

namespace DubbingPlatform.Api.Observability;

/// <summary>
/// Task 038 product observability facade. Instruments the Task 038 metric set on
/// Meter <c>DubbingPlatform.Observability</c> (distinct from the frozen
/// <c>dubbing-platform</c> SLO facade and the <c>DubbingPlatform.Sse</c> /
/// <c>DubbingPlatform.Notifications</c> domain meters, so no instrument name is
/// duplicated across meters):
/// <list type="bullet">
/// <item>SSE connections/reconnects (<c>sse.connections_total</c>, <c>sse.reconnects_total</c>).</item>
/// <item>Notification projection failures (<c>notifications.projection_failures_total</c>).</item>
/// <item>Read-model query latency (<c>readmodel.query_duration_ms</c>).</item>
/// <item>Upload funnel stages (<c>upload.funnel_total</c>, stage in initiated/chunk_received/completed/failed).</item>
/// <item>Review/export/preview latencies (<c>review.latency_ms</c>, <c>export.latency_ms</c>, <c>preview.latency_ms</c>).</item>
/// <item>Legacy correlation minting (<c>correlation.minted_total</c>, propagated true/false).</item>
/// </list>
/// Label policy: tenant identity is a truncated SHA-256 hex of the tenant GUID
/// (<c>tenant_hash</c>, <c>none</c> when empty) — raw tenant/user ids never
/// appear. Routes are normalized to templates (<c>{id}</c> replaces GUID/numeric
/// segments; query/hash stripped) so high-cardinality ids cannot explode
/// cardinality. Values are never user content (counts, latencies, codes only).
/// Where a Task 038 histogram overlaps an existing <see cref="PlatformMetrics"/>
/// counter (reviews, exports), the emit helper forwards to both so the frozen
/// facade and this meter stay consistent.
/// </summary>
public static partial class BackendMetrics
{
    public const string MeterName = "DubbingPlatform.Observability";

    public const string SseConnectionsName = "sse.connections_total";

    public const string SseReconnectsName = "sse.reconnects_total";

    public const string NotificationProjectionFailuresName = "notifications.projection_failures_total";

    public const string ReadModelQueryLatencyName = "readmodel.query_duration_ms";

    public const string UploadFunnelName = "upload.funnel_total";

    public const string ReviewLatencyName = "review.latency_ms";

    public const string ExportLatencyName = "export.latency_ms";

    public const string PreviewLatencyName = "preview.latency_ms";

    public const string CorrelationMintedName = "correlation.minted_total";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> SseConnections =
        Meter.CreateCounter<long>(SseConnectionsName);

    public static readonly Counter<long> SseReconnects =
        Meter.CreateCounter<long>(SseReconnectsName);

    public static readonly Counter<long> NotificationProjectionFailures =
        Meter.CreateCounter<long>(NotificationProjectionFailuresName);

    public static readonly Histogram<double> ReadModelQueryLatency =
        Meter.CreateHistogram<double>(ReadModelQueryLatencyName, "ms");

    public static readonly Counter<long> UploadFunnel =
        Meter.CreateCounter<long>(UploadFunnelName);

    public static readonly Histogram<double> ReviewLatency =
        Meter.CreateHistogram<double>(ReviewLatencyName, "ms");

    public static readonly Histogram<double> ExportLatency =
        Meter.CreateHistogram<double>(ExportLatencyName, "ms");

    public static readonly Histogram<double> PreviewLatency =
        Meter.CreateHistogram<double>(PreviewLatencyName, "ms");

    public static readonly Counter<long> CorrelationMinted =
        Meter.CreateCounter<long>(CorrelationMintedName);

    /// <summary>
    /// One-way tenant label: first 16 hex chars of SHA-256 over the tenant GUID
    /// (<c>N</c> format). Stable per tenant, irreversible, never the raw id.
    /// Empty tenants map to <c>none</c>. Pure.
    /// </summary>
    public static string HashTenantId(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            return "none";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId.ToString("N")));
        var builder = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            builder.Append(bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Normalizes a request path to a route template: trims, strips query/hash,
    /// collapses GUID (<c>N</c> and hyphenated) and numeric segments to
    /// <c>{id}</c>, and caps length at 128 chars. Never returns query strings,
    /// tokens, or raw ids. Pure.
    /// </summary>
    public static string NormalizeRoute(string? route)
    {
        if (route is null)
        {
            return "unknown";
        }

        if (string.IsNullOrWhiteSpace(route))
        {
            return "/";
        }

        var trimmed = route.Trim();
        var cut = trimmed.IndexOfAny(['?', '#']);
        var path = (cut >= 0 ? trimmed.Substring(0, cut) : trimmed).Trim();
        if (path.Length == 0)
        {
            return "/";
        }

        path = GuidSegmentPattern().Replace(path, "/{id}");
        path = HyphenatedGuidPattern().Replace(path, "/{id}");
        path = NumericSegmentPattern().Replace(path, "/{id}");
        path = path.TrimEnd('/');
        if (path.Length == 0)
        {
            return "/";
        }

        return path.Length > 128 ? path.Substring(0, 128) : path;
    }

    /// <summary>
    /// Records an SSE stream open. Tenant is hashed; the endpoint is a route template.
    /// </summary>
    public static void SseConnected(Guid tenantId, string? endpoint = null)
    {
        SseConnections.Add(1,
            new KeyValuePair<string, object?>("tenant_hash", HashTenantId(tenantId)),
            new KeyValuePair<string, object?>("endpoint", NormalizeRoute(endpoint)));
    }

    /// <summary>
    /// Records an SSE reconnect (Task 026 backoff keeps this bounded; the counter
    /// proves reconnect storms instead of amplifying them).
    /// </summary>
    public static void SseReconnected(Guid tenantId, string? endpoint = null)
    {
        SseReconnects.Add(1,
            new KeyValuePair<string, object?>("tenant_hash", HashTenantId(tenantId)),
            new KeyValuePair<string, object?>("endpoint", NormalizeRoute(endpoint)));
    }

    /// <summary>
    /// Records a failed notification projection (code only, never content).
    /// </summary>
    public static void NotificationProjectionFailed(Guid tenantId, string? operation = null)
    {
        NotificationProjectionFailures.Add(1,
            new KeyValuePair<string, object?>("tenant_hash", HashTenantId(tenantId)),
            new KeyValuePair<string, object?>("operation", NormalizeOperation(operation)));
    }

    /// <summary>
    /// Records a read-model query latency in milliseconds. Negative/NaN values are dropped.
    /// </summary>
    public static void ObserveReadModelLatency(double milliseconds, Guid tenantId, string? operation = null)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
        {
            return;
        }

        ReadModelQueryLatency.Record(milliseconds,
            new KeyValuePair<string, object?>("tenant_hash", HashTenantId(tenantId)),
            new KeyValuePair<string, object?>("operation", NormalizeOperation(operation)));
    }

    /// <summary>
    /// Records an upload-funnel transition. Stage must be one of
    /// initiated/chunk_received/completed/failed (anything else maps to unknown).
    /// </summary>
    public static void UploadFunnelStage(Guid tenantId, string stage)
    {
        UploadFunnel.Add(1,
            new KeyValuePair<string, object?>("tenant_hash", HashTenantId(tenantId)),
            new KeyValuePair<string, object?>("stage", NormalizeUploadStage(stage)));
    }

    /// <summary>
    /// Records review-resolution latency and forwards the count to the frozen
    /// <see cref="PlatformMetrics.ReviewsResolved"/> counter.
    /// </summary>
    public static void ObserveReviewLatency(double milliseconds, Guid tenantId, string? operation = null)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
        {
            return;
        }

        PlatformMetrics.ReviewResolved(tenantId);
        ReviewLatency.Record(milliseconds,
            new KeyValuePair<string, object?>("tenant_hash", HashTenantId(tenantId)),
            new KeyValuePair<string, object?>("operation", NormalizeOperation(operation)));
    }

    /// <summary>
    /// Records export-generation latency and forwards the count to the frozen
    /// <see cref="PlatformMetrics"/> export counter (generated vs failed by outcome).
    /// </summary>
    public static void ObserveExportLatency(double milliseconds, Guid tenantId, bool failed, string? operation = null)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
        {
            return;
        }

        if (failed)
        {
            PlatformMetrics.ExportFailed(tenantId);
        }
        else
        {
            PlatformMetrics.ExportGenerated(tenantId);
        }

        ExportLatency.Record(milliseconds,
            new KeyValuePair<string, object?>("tenant_hash", HashTenantId(tenantId)),
            new KeyValuePair<string, object?>("operation", NormalizeOperation(operation)));
    }

    /// <summary>
    /// Records voice-preview generation latency in milliseconds.
    /// </summary>
    public static void ObservePreviewLatency(double milliseconds, Guid tenantId, string? operation = null)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
        {
            return;
        }

        PreviewLatency.Record(milliseconds,
            new KeyValuePair<string, object?>("tenant_hash", HashTenantId(tenantId)),
            new KeyValuePair<string, object?>("operation", NormalizeOperation(operation)));
    }

    /// <summary>
    /// Counts correlation-id resolution outcomes: <c>propagated=true</c> when the
    /// caller supplied a safe id, <c>false</c> when the middleware minted one on
    /// the legacy path.
    /// </summary>
    public static void RecordCorrelationOutcome(bool propagated)
    {
        CorrelationMinted.Add(1,
            new KeyValuePair<string, object?>("propagated", propagated ? "true" : "false"));
    }

    private static string NormalizeOperation(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            return "unknown";
        }

        var trimmed = operation.Trim();
        return trimmed.Length > 64 ? trimmed.Substring(0, 64) : trimmed;
    }

    private static string NormalizeUploadStage(string? stage)
    {
        if (string.Equals(stage, "initiated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stage, "chunk_received", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stage, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stage, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return stage!.Trim().ToLowerInvariant();
        }

        return "unknown";
    }

    [GeneratedRegex("/[0-9a-fA-F]{32}(?=/|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GuidSegmentPattern();

    [GeneratedRegex("/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?=/|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HyphenatedGuidPattern();

    [GeneratedRegex("/\\d+(?=/|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumericSegmentPattern();
}
