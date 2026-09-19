using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Observability;
using DubbingPlatform.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Workers.Services;

/// <summary>
/// Result of one reconciliation pass.
/// </summary>
public sealed record ReconcileResult(int QuarantinedBlobs, int MarkedOrphaned);

/// <summary>
/// Orphan/dangling reconciler. Runs daily; <see cref="ReconcileAsync"/> is the
/// manual trigger used by tests and ops. Two passes, both in a maintenance
/// scope (dedicated role, bypassing tenant filters) so every tenant is covered:
/// <list type="number">
/// <item>Blobs without committed metadata older than 24h are quarantined to
/// <c>quarantine/{key}</c> (copy + delete). Already-quarantined keys are
/// skipped. Failed commits leave exactly such orphans.</item>
/// <item>Committed content rows with zero artifact references older than the
/// 7-day grace are marked <c>Orphaned</c>. Physical deletion requires the
/// <c>ContentObjectService.CanDeleteContentObjectAsync</c> retention hook and
/// runs in Task 37, never here.</item>
/// </list>
/// Active holds never appear here: orphaned rows have no referencing artifacts,
/// and blob quarantine only touches keys with no committed metadata.
/// Every action logs and increments <c>storage.orphans_detected</c>.
/// </summary>
public sealed class OrphanObjectReconciler : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static readonly TimeSpan BlobOrphanAge = TimeSpan.FromHours(24);

    public static readonly TimeSpan CommittedOrphanGrace = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OrphanObjectReconciler> _logger;

    public OrphanObjectReconciler(IServiceScopeFactory scopes, ILogger<OrphanObjectReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var result = await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                if (result.QuarantinedBlobs > 0 || result.MarkedOrphaned > 0)
                {
                    _logger.LogInformation(
                        "Orphan reconciliation quarantined {Quarantined} blob(s), marked {Orphaned} content row(s) orphaned.",
                        result.QuarantinedBlobs, result.MarkedOrphaned);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Reconciler must survive dependency outages; failures are logged and retried next tick.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Orphan reconciliation failed; retrying in 24h.");
            }
        }
    }

    /// <summary>
    /// Runs one reconciliation pass immediately. Callable from tests and ops.
    /// </summary>
    public async Task<ReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        using (TenantContext.BeginMaintenanceScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<IStorageInventory>();
            var factory = scope.ServiceProvider.GetRequiredService<IStageExecutionContextFactory>();
            using var db = factory.CreateDbContext();
            var now = DateTimeOffset.UtcNow;

            var quarantined = 0;
            var objects = await inventory.ListObjectsAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            foreach (var entry in objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Key.StartsWith("quarantine/", StringComparison.Ordinal))
                {
                    continue;
                }

                var hasCommitted = await db.Set<ContentObject>()
                    .AnyAsync(
                        c => c.StorageKey == entry.Key && c.Status == ContentObjectStatus.Committed,
                        cancellationToken).ConfigureAwait(false);
                if (hasCommitted)
                {
                    continue;
                }

                if (entry.LastModified.Add(BlobOrphanAge) > now)
                {
                    continue;
                }

                await inventory.QuarantineAsync(entry.Key, cancellationToken).ConfigureAwait(false);
                quarantined++;
                StorageMeters.OrphansDetected.Add(1);
                PlatformMetrics.StorageOrphans.Add(1);
                _logger.LogInformation("Quarantined orphan blob without committed metadata.");
            }

            var cutoff = now.Subtract(CommittedOrphanGrace);
            var candidates = await db.Set<ContentObject>()
                .Where(c => c.Status == ContentObjectStatus.Committed && c.LastReferencedAt < cutoff)
                .Select(c => new { c.Id, c.LastReferencedAt })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var orphaned = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasRefs = await db.Set<Artifact>()
                    .AnyAsync(a => a.ContentObjectId == candidate.Id, cancellationToken).ConfigureAwait(false);
                if (hasRefs)
                {
                    continue;
                }

                var rows = await db.Database.ExecuteSqlRawAsync(
                    "UPDATE content_objects SET status = {0} WHERE id = {1} AND status = 'Committed'",
                    ContentObjectStatus.Orphaned.ToString(), candidate.Id).ConfigureAwait(false);
                if (rows > 0)
                {
                    orphaned++;
                    StorageMeters.OrphansDetected.Add(1);
                    PlatformMetrics.StorageOrphans.Add(1);
                }
            }

            if (quarantined > 0 || orphaned > 0)
            {
                _logger.LogInformation(
                    "Reconciliation pass quarantined {Quarantined} blob(s) and orphaned {Orphaned} content row(s).",
                    quarantined, orphaned);
            }

            return new ReconcileResult(quarantined, orphaned);
        }
    }
}
