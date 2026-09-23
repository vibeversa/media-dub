using System.Diagnostics.Metrics;
using System.Text.Json;

namespace DubbingPlatform.Api.Sse;

/// <summary>
/// Frozen SSE event-type set (Task 013). Exactly these 14 types may be emitted;
/// emitting any other type fails serialization tests (closed-enum assertion).
/// </summary>
public static class SseEventTypes
{
    public const string ProjectStatusChanged = "project.status_changed";
    public const string RunStatusChanged = "run.status_changed";
    public const string StageStarted = "stage.started";
    public const string StageProgress = "stage.progress";
    public const string StageCompleted = "stage.completed";
    public const string StageFailed = "stage.failed";
    public const string StageReviewRequired = "stage.review_required";
    public const string ReviewCreated = "review.created";
    public const string ReviewResolved = "review.resolved";
    public const string ExportCreated = "export.created";
    public const string ExportCompleted = "export.completed";
    public const string ExportFailed = "export.failed";
    public const string NotificationCreated = "notification.created";
    public const string OutputReady = "output.ready";

    /// <summary>
    /// All 14 frozen event types.
    /// </summary>
    public static readonly string[] All =
    [
        ProjectStatusChanged,
        RunStatusChanged,
        StageStarted,
        StageProgress,
        StageCompleted,
        StageFailed,
        StageReviewRequired,
        ReviewCreated,
        ReviewResolved,
        ExportCreated,
        ExportCompleted,
        ExportFailed,
        NotificationCreated,
        OutputReady,
    ];

    /// <summary>
    /// Determines whether the given type is in the frozen set.
    /// </summary>
    public static bool IsKnown(string? eventType)
    {
        return !string.IsNullOrEmpty(eventType) && All.Contains(eventType, StringComparer.Ordinal);
    }
}

/// <summary>
/// SSE instrumentation. Counter names are frozen: renaming breaks dashboards.
/// </summary>
public static class SseMetrics
{
    public const string MeterName = "DubbingPlatform.Sse";

    public const string PayloadDroppedMetricName = "sse.payload_dropped_total";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> PayloadDropped =
        Meter.CreateCounter<long>(PayloadDroppedMetricName);

    public static void PayloadDroppedObserved()
    {
        PayloadDropped.Add(1);
    }
}

/// <summary>
/// Frozen SSE envelope (Task 013): <c>{ eventId, schemaVersion: 1, eventType,
/// tenantId, projectId?, processingRunId?, occurredAt, payload }</c> plus
/// <c>correlationId</c> on every frame (security requirement; logs carry IDs,
/// never bodies). SSE is an invalidation hint only — clients refetch the
/// matching HTTP API as source of truth. Payloads obey
/// <see cref="SsePayloadPolicy"/> (IDs, statuses, percents, counts, codes,
/// timestamps only). Frames exceeding 64KB are dropped (metric
/// <c>sse.payload_dropped_total</c>) and the stream stays open. Replay keeps
/// the last 100 envelopes per stream key; resume beyond the window sets the
/// <c>replayTruncated: true</c> response header and the client falls back to
/// polling (Task 026).
/// </summary>
public sealed record SseEnvelope(
    string EventId,
    int SchemaVersion,
    string EventType,
    string TenantId,
    string? ProjectId,
    string? ProcessingRunId,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, object?> Payload,
    string CorrelationId)
{
    /// <summary>Frozen schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Maximum serialized payload bytes before drop.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>Replay window per stream key.</summary>
    public const int ReplayWindow = 100;

    private static readonly JsonSerializerOptions FrameOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serializes the envelope to an SSE frame
    /// (<c>id:</c>/<c>event:</c>/<c>data:</c>). Unknown event types throw;
    /// forbidden payload keys throw via <see cref="SsePayloadPolicy"/>;
    /// oversize payloads return <c>null</c> (dropped + metric, stream open).
    /// </summary>
    public string? ToFrame()
    {
        if (!SseEventTypes.IsKnown(EventType))
        {
            throw new InvalidOperationException($"Unknown SSE event type '{EventType}'.");
        }

        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"SSE schemaVersion must be {CurrentSchemaVersion}.");
        }

        SsePayloadPolicy.Validate(Payload);

        var json = JsonSerializer.Serialize(this, FrameOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes)
        {
            SseMetrics.PayloadDroppedObserved();
            return null;
        }

        return string.Concat("id: ", EventId, "\nevent: ", EventType, "\ndata: ", json, "\n\n");
    }

    /// <summary>
    /// Header-only replay line for a missed envelope (envelope headers without
    /// payload backfill): <c>id:</c>/<c>event:</c> plus the envelope ids so the
    /// client can refetch the source-of-truth API.
    /// </summary>
    public string ToHeaderOnlyFrame()
    {
        if (!SseEventTypes.IsKnown(EventType))
        {
            throw new InvalidOperationException($"Unknown SSE event type '{EventType}'.");
        }

        var pointer = JsonSerializer.Serialize(new
        {
            eventId = EventId,
            schemaVersion = SchemaVersion,
            eventType = EventType,
            tenantId = TenantId,
            projectId = ProjectId,
            processingRunId = ProcessingRunId,
            occurredAt = OccurredAt,
            correlationId = CorrelationId,
        }, FrameOptions);
        return string.Concat("id: ", EventId, "\nevent: ", EventType, "\ndata: ", pointer, "\n\n");
    }
}

/// <summary>
/// In-memory per-stream replay buffer (last 100 envelopes). Single-node;
/// clients tolerate duplicates by <c>eventId</c>. Thread-safe.
/// </summary>
public static class SseEventBuffer
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, LinkedList<SseEnvelope>> Buffers = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds the buffer key for a tenant/project stream.
    /// </summary>
    public static string KeyFor(Guid tenantId, Guid? projectId)
    {
        return projectId.HasValue
            ? string.Concat(tenantId.ToString("N"), ":", projectId.Value.ToString("N"))
            : tenantId.ToString("N");
    }

    /// <summary>
    /// Appends an envelope, evicting the oldest beyond <see cref="SseEnvelope.ReplayWindow"/>.
    /// </summary>
    public static void Append(string key, SseEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(envelope);
        var list = Buffers.GetOrAdd(key, _ => new LinkedList<SseEnvelope>());
        lock (list)
        {
            list.AddLast(envelope);
            while (list.Count > SseEnvelope.ReplayWindow)
            {
                list.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// Replays envelopes after <paramref name="lastEventId"/>. Returns
    /// <c>(found, items, truncated)</c>: <c>found</c> when the cursor was in
    /// window; <c>truncated</c> when the cursor is unknown and the window is
    /// full (client must fall back to polling).
    /// </summary>
    public static (bool Found, IReadOnlyList<SseEnvelope> Items, bool Truncated) ReplayAfter(string key, string? lastEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (string.IsNullOrWhiteSpace(lastEventId))
        {
            return (false, [], false);
        }

        if (!Buffers.TryGetValue(key, out var list))
        {
            return (false, [], false);
        }

        lock (list)
        {
            var node = list.First;
            while (node is not null)
            {
                if (string.Equals(node.Value.EventId, lastEventId.Trim(), StringComparison.Ordinal))
                {
                    var missed = new List<SseEnvelope>();
                    var cursor = node.Next;
                    while (cursor is not null)
                    {
                        missed.Add(cursor.Value);
                        cursor = cursor.Next;
                    }

                    return (true, missed, false);
                }

                node = node.Next;
            }

            var truncated = list.Count >= SseEnvelope.ReplayWindow;
            return (false, [], truncated);
        }
    }

    /// <summary>
    /// Clears all buffers (tests only).
    /// </summary>
    public static void Clear()
    {
        Buffers.Clear();
    }
}
