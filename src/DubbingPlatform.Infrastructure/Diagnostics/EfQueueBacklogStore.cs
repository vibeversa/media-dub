using DubbingPlatform.Application.Diagnostics;
using DubbingPlatform.Infrastructure.Persistence;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Infrastructure.Diagnostics;

/// <summary>
/// <see cref="IQueueBacklogStore"/> over the MassTransit EF outbox
/// (<c>outbox_message</c>). Rows still in the outbox are undelivered, so the
/// row set is the pending backlog; delivered rows are removed by the outbox
/// delivery service. Projects destination, message type, headers, and enqueue
/// time only — bodies are never loaded. Read-only (<c>AsNoTracking</c>, no
/// writes). The scan caps at <see cref="MaxRows"/> most recent rows so DLQ
/// triage stays bounded on large backlogs.
/// </summary>
public sealed class EfQueueBacklogStore : IQueueBacklogStore
{
    /// <summary>Maximum outbox rows scanned per call.</summary>
    public const int MaxRows = 10000;

    private readonly AppDbContext _db;

    public EfQueueBacklogStore(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<IReadOnlyList<QueuedMessageSnapshot>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<OutboxMessage>()
            .AsNoTracking()
            .OrderByDescending(m => m.SequenceNumber)
            .Take(MaxRows)
            .Select(m => new
            {
                m.DestinationAddress,
                m.MessageType,
                m.Headers,
                m.EnqueueTime,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(m => new QueuedMessageSnapshot(
                m.DestinationAddress?.ToString(),
                m.MessageType ?? string.Empty,
                m.Headers,
                m.EnqueueTime.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(m.EnqueueTime.Value, DateTimeKind.Utc))
                    : null))
            .ToList();
    }
}
