using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Workers.Services;

/// <summary>
/// Safety-only recovery sweeper. Every 60 seconds it recovers Running executions
/// whose leases expired (<c>status='Running' AND lease_expires_at &lt; now</c>,
/// served by the partial running index) via
/// <see cref="StageExecutionService.RecoverStaleAsync"/>, which re-queues within
/// per-stage retry budgets or fails exhausted units. Active leases are never
/// touched. This is the backstop, not the primary timeout: the primary path is
/// the delayed <c>StageLeaseTimeout</c> message scheduled at stage start. Runs
/// in a maintenance scope (dedicated role, bypassing tenant filters) so stale
/// leases of every tenant are covered.
/// </summary>
public sealed class WorkerRecoverySweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WorkerRecoverySweeper> _logger;

    public WorkerRecoverySweeper(IServiceScopeFactory scopes, ILogger<WorkerRecoverySweeper> logger)
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
                    var service = scope.ServiceProvider.GetRequiredService<StageExecutionService>();
                    var recovered = await service.RecoverStaleAsync(stoppingToken).ConfigureAwait(false);
                    if (recovered > 0)
                    {
                        _logger.LogInformation("Recovery sweeper reclaimed {Recovered} stale execution(s).", recovered);
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
                _logger.LogWarning(ex, "Recovery sweep failed; retrying in 60s.");
            }
        }
    }
}
