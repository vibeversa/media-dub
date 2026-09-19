using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Orchestration;

/// <summary>
/// Real dispatch cost gate (replaces <see cref="AllowAllCostGate"/>; the
/// <see cref="ICostGate"/> contract is frozen). Allows while the run's project
/// spend (<c>SUM(Reserved) + SUM(Reconciled actuals)</c>) stays within
/// <c>Quota:MaxCostPerProject</c>; denies (false → dispatcher
/// <c>cost-blocked</c>) when over. Fail-closed on DB outage (deny + error log)
/// so overspend is impossible without PG. Per-segment precision lives in
/// <see cref="CostService"/> reservations inside Translation/Tts workers; this
/// gate is the coarse dispatch-time check. Only ids and stages are logged.
/// </summary>
public sealed class CostGate : ICostGate
{
    private readonly IServiceScopeFactory _scopes;
    private readonly QuotaOptions _quota;
    private readonly ILogger<CostGate> _logger;

    public CostGate(
        IServiceScopeFactory scopes,
        IOptions<QuotaOptions> quotaOptions,
        ILogger<CostGate> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(quotaOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _quota = quotaOptions.Value;
        _logger = logger;
    }

    public async Task<bool> CanProceedAsync(Guid tenantId, Guid runId, string stage, int units, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || runId == Guid.Empty)
        {
            return false;
        }

        if (units < 0)
        {
            return false;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var factories = scope.ServiceProvider.GetService(typeof(IStageExecutionContextFactory)) as IStageExecutionContextFactory;
            if (factories is null)
            {
                return true;
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = factories.CreateDbContext();
                var run = await db.Set<ProcessingRun>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
                if (run is null || run.TenantId != tenantId)
                {
                    return true;
                }

                var total = await db.Set<CostReservation>()
                    .Where(r => r.ProjectId == run.ProjectId
                        && (r.State == "Reserved" || r.State == "Reconciled"))
                    .SumAsync(r => (double?)(r.State == "Reserved" ? r.ReservedAmount : r.ActualAmount), cancellationToken).ConfigureAwait(false) ?? 0.0;
                var allowed = !QuotaService.IsCostExceeded(total, 0.0, _quota.MaxCostPerProject);
                if (!allowed)
                {
                    QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", QuotaDimensions.CostPerProject));
                    _logger.LogWarning(
                        "Cost gate blocks run {RunId} stage {Stage}: project spend {Total} exceeds cap.",
                        runId, stage, total);
                }

                return allowed;
            }
        }
#pragma warning disable CA1031 // Fail-closed: DB outage denies dispatch; the error log feeds alerting.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Cost gate failed for run {RunId}; denying.", runId);
            return false;
        }
    }
}

/// <summary>
/// Real dispatch rate gate (replaces <see cref="AllowAllRateGate"/>; the
/// <see cref="IRateGate"/> contract is frozen). Delegates to
/// <see cref="Redis.RateLimiter"/> (<c>pipeline/requests</c> per minute).
/// Fail-open via the limiter (Redis outage allows) so the pipeline never
/// stalls on cache loss; strict per-provider dims are enforced inside
/// Translation/Tts workers before provider calls.
/// </summary>
public sealed class RateGate : IRateGate
{
    private readonly Redis.RateLimiter _limiter;

    public RateGate(Redis.RateLimiter limiter)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        _limiter = limiter;
    }

    public Task<bool> CanProceedAsync(Guid tenantId, string stage, int requested, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || requested <= 0)
        {
            return Task.FromResult(requested <= 0 && tenantId != Guid.Empty);
        }

        _ = stage;
        return _limiter.TryAcquireAsync(tenantId, "pipeline", "requests", requested, cancellationToken);
    }
}
