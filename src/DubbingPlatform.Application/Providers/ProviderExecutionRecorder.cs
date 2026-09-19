using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Persists every provider call, including fallback attempts. Callers build the
/// full <see cref="ProviderExecution"/> (all 25 execution fields) and record it;
/// duplicates on <c>ProviderIdempotencyKey</c> reconcile by request+response
/// hash (same hashes return the existing id without double billing; differing
/// hashes throw <c>CONFLICT</c>). Idempotency keys use
/// <c>{run:N}:{stage}:{scope}:{attempt}</c> where the provider supports them.
/// </summary>
public sealed class ProviderExecutionRecorder
{
    private readonly IStageExecutionContextFactory _contextFactory;

    public ProviderExecutionRecorder(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Builds a provider idempotency key. Run uses <c>N</c> format.
    /// </summary>
    public static string BuildIdempotencyKey(Guid runId, string stage, string scope, int attempt)
    {
        if (runId == Guid.Empty)
        {
            throw new DomainException("RunId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new DomainException("Stage must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new DomainException("Scope must not be empty.");
        }

        if (attempt < 0)
        {
            throw new DomainException("Attempt must be >= 0.");
        }

        return string.Concat(runId.ToString("N"), ":", stage.Trim(), ":", scope.Trim(), ":", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Records one execution, reconciling idempotency-key duplicates.
    /// Returns the persisted (or pre-existing) row id.
    /// Trace enrichment mirrors <c>Infrastructure.Observability.TraceEnricher</c>
    /// (direct <c>Activity</c> tags here to avoid an Application→Infrastructure
    /// reference; same tag names, ids only, never secrets).
    /// </summary>
    public async Task<Guid> RecordAsync(ProviderExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        execution.Validate();
        EnrichActivity(execution);

        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            if (!string.IsNullOrWhiteSpace(execution.ProviderIdempotencyKey))
            {
                var existing = await db.Set<ProviderExecution>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        e => e.ProviderIdempotencyKey == execution.ProviderIdempotencyKey,
                        cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (string.Equals(existing.RequestHash, execution.RequestHash, StringComparison.Ordinal)
                        && string.Equals(existing.ResponseHash, execution.ResponseHash, StringComparison.Ordinal))
                    {
                        return existing.Id;
                    }

                    throw new ConflictException(
                        $"Duplicate provider idempotency key '{execution.ProviderIdempotencyKey}' with different payload hashes.");
                }
            }

            db.Set<ProviderExecution>().Add(execution);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return execution.Id;
        }
    }

    private static void EnrichActivity(ProviderExecution execution)
    {
        var activity = System.Diagnostics.Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetTag("tenant.id", execution.TenantId.ToString("N"));
        activity.SetTag("project.id", execution.ProjectId.ToString("N"));
        activity.SetTag("run.id", execution.ProcessingRunId.ToString("N"));
        activity.SetTag("stage", execution.Capability.ToString());
        activity.SetTag("provider", execution.Provider.ToString());
        activity.SetTag("model", execution.Model);
        activity.SetTag("attempt", execution.Attempt);
    }
}
