using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Contracts.Messages;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// Read-only queue diagnostics over the MassTransit outbox store: per-queue
/// pending depth across the frozen queue taxonomy plus DLQ depth, oldest-entry
/// age, and the top-10 dead-letter reason breakdown. An empty DLQ reads as
/// zero depth with null oldest age (never 404). Queue depths are bus-level
/// aggregates (the outbox carries no tenant); all entity reads elsewhere in
/// the diagnostics layer stay tenant-scoped. Counts and codes only — message
/// bodies are never loaded. No writes.
/// </summary>
public sealed class QueueDiagnosticsService
{
    /// <summary>Maximum dead-letter reasons returned in the breakdown.</summary>
    public const int MaxReasonBreakdown = 10;

    private readonly IQueueBacklogStore _backlog;
    private readonly IDiagnosticsAccessChecker _access;
    private readonly ILogger<QueueDiagnosticsService> _logger;
    private readonly TimeProvider _time;

    public QueueDiagnosticsService(
        IQueueBacklogStore backlog,
        IDiagnosticsAccessChecker access,
        ILogger<QueueDiagnosticsService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backlog);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(logger);
        _backlog = backlog;
        _access = access;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns pending depth per queue, zero-filled across the frozen queue
    /// taxonomy so operators always see the full lane picture.
    /// </summary>
    public async Task<IReadOnlyList<QueueDepthDto>> GetQueueDepthsAsync(
        Guid tenantId,
        Guid userId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await _access.RequireDiagnosticsViewerAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlation = DiagnosticsCorrelation.Normalize(correlationId);

        var pending = await _backlog.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        var depths = BuildDepths(pending, correlation);

        _logger.LogInformation(
            "Diagnostics queue depths queried. {CorrelationId} {TenantId} {QueueCount} {TotalDepth}",
            correlation,
            tenantId,
            depths.Count,
            depths.Sum(d => d.Depth));
        return depths;
    }

    /// <summary>
    /// Returns the DLQ summary: depth for the frozen <c>_error</c> queue, the
    /// oldest-entry age, and the top-10 reason breakdown.
    /// </summary>
    public async Task<DlqSummaryDto> GetDlqSummaryAsync(
        Guid tenantId,
        Guid userId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await _access.RequireDiagnosticsViewerAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlation = DiagnosticsCorrelation.Normalize(correlationId);

        var pending = await _backlog.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        var summary = BuildSummary(pending, correlation, _time.GetUtcNow());

        _logger.LogInformation(
            "Diagnostics DLQ summary queried. {CorrelationId} {TenantId} {DlqDepth}",
            correlation,
            tenantId,
            summary.Depth);
        return summary;
    }

    /// <summary>
    /// Groups pending snapshots into taxonomy-ordered queue depths. Pure.
    /// Broker address forms (<c>queue://host/name</c>, <c>queue:name</c>)
    /// are normalized to short queue names before grouping.
    /// </summary>
    public static IReadOnlyList<QueueDepthDto> BuildDepths(IReadOnlyList<QueuedMessageSnapshot> pending, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var message in pending)
        {
            var queue = NormalizeQueueName(message.DestinationAddress);
            counts[queue] = counts.TryGetValue(queue, out var current) ? current + 1 : 1;
        }

        var ordered = new List<QueueDepthDto>(counts.Count + 9);
        foreach (var queue in TaxonomyOrder())
        {
            ordered.Add(new QueueDepthDto(correlationId, queue, counts.TryGetValue(queue, out var depth) ? depth : 0));
            counts.Remove(queue);
        }

        foreach (var extra in counts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ordered.Add(new QueueDepthDto(correlationId, extra.Key, extra.Value));
        }

        return ordered;
    }

    /// <summary>
    /// Builds the DLQ summary from pending snapshots. Pure. Empty DLQ yields
    /// zero depth with null oldest age.
    /// </summary>
    public static DlqSummaryDto BuildSummary(IReadOnlyList<QueuedMessageSnapshot> pending, string correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var dead = pending
            .Where(m => string.Equals(NormalizeQueueName(m.DestinationAddress), QueueNames.Error, StringComparison.Ordinal))
            .ToList();

        DateTimeOffset? oldest = null;
        foreach (var message in dead)
        {
            if (message.EnqueuedAt.HasValue && (oldest is null || message.EnqueuedAt.Value < oldest.Value))
            {
                oldest = message.EnqueuedAt.Value;
            }
        }

        var reasons = dead
            .GroupBy(m => DeadLetterReasons.Extract(m.HeadersJson, m.MessageType), StringComparer.Ordinal)
            .Select(g => new DlqReasonCount(g.Key, g.LongCount()))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .Take(MaxReasonBreakdown)
            .ToList();

        return new DlqSummaryDto(
            correlationId,
            dead.Count,
            oldest,
            oldest.HasValue ? now - oldest.Value : null,
            reasons);
    }

    /// <summary>
    /// Normalizes a broker destination address to a short queue name:
    /// scheme prefixes (<c>queue://host/</c>) and short-form prefixes
    /// (<c>queue:</c>) are stripped. Empty destinations read
    /// <c>unknown</c>. Pure.
    /// </summary>
    public static string NormalizeQueueName(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return "unknown";
        }

        var trimmed = destination.Trim();
        var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            trimmed = trimmed[(scheme + 3)..];
        }

        var slash = trimmed.LastIndexOf('/');
        if (slash >= 0 && slash < trimmed.Length - 1)
        {
            trimmed = trimmed[(slash + 1)..];
        }

        var colon = trimmed.LastIndexOf(':');
        if (colon >= 0 && colon < trimmed.Length - 1)
        {
            trimmed = trimmed[(colon + 1)..];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "unknown" : trimmed.Trim();
    }

    private static IReadOnlyList<string> TaxonomyOrder()
    {
        return
        [
            QueueNames.ControlOrchestration,
            QueueNames.MediaPreparation,
            QueueNames.MediaRender,
            QueueNames.AiProvider,
            QueueNames.AiGpu,
            QueueNames.Export,
            QueueNames.Maintenance,
            QueueNames.Skipped,
            QueueNames.Error,
        ];
    }
}
