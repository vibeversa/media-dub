using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Workers.Services;

/// <summary>
/// Daily physical-deletion backstop. Runs <see cref="RetentionService.SweepAsync"/>
/// every 24 hours in a maintenance scope (dedicated role bypassing tenant
/// filters and, at deploy, RLS) so expired logically-deleted rows of every
/// tenant are collected: hard-deleted artifact rows with zero live references
/// and no active hold, completed pending deletion jobs (including stale
/// <c>Running</c> takeovers), and orphaned blobs whose content objects satisfy
/// the retention gate. Skips (never fails) on holds, unexpired rows, and
/// storage faults; the next tick retries. This is the backstop, not the only
/// path: <c>DeletionJobWorker</c> executes jobs eagerly on publish.
/// </summary>
public sealed class RetentionSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RetentionSweeper> _logger;

    public RetentionSweeper(IServiceScopeFactory scopes, ILogger<RetentionSweeper> logger)
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
                using var scope = _scopes.CreateScope();
                using (TenantContext.BeginMaintenanceScope())
                {
                    var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
                    var result = await retention.SweepAsync(stoppingToken).ConfigureAwait(false);
                    if (result.ArtifactsDeleted > 0 || result.BlobsDeleted > 0 || result.JobsCompleted > 0)
                    {
                        _logger.LogInformation(
                            "Retention sweep deleted {Artifacts} artifact rows, {Blobs} blobs, {Jobs} jobs.",
                            result.ArtifactsDeleted, result.BlobsDeleted, result.JobsCompleted);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Sweeper must survive dependency outages; failures are logged and retried next tick.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "Retention sweep failed; retrying on the next tick.");
            }
        }
    }
}
