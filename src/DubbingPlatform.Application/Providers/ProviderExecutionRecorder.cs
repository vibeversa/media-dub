using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Persists every provider call, including fallback attempts. Callers build the
/// full <see cref="ProviderExecution"/> (all execution fields) and record it;
/// duplicates on <c>ProviderIdempotencyKey</c> reconcile by request+response+
/// output-content hash (same hashes return the existing id without double
/// billing; differing hashes throw <c>CONFLICT</c>). Idempotency keys use
/// <c>{run:N}:{stage}:{scope}:{attempt}</c> where the provider supports them.
/// GAP-011: <c>OutputContentHash</c> is the distinct output-body hash used for
/// reconcile (separate from the <c>ResponseHash</c> envelope hash).
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
    /// SHA-256 (lowercase hex, 64 chars) of provider content, used for
    /// <see cref="ProviderExecution.OutputContentHash"/> (reconcile) and for
    /// <see cref="ProviderExecution.SystemInstructionHash"/> /
    /// <see cref="ProviderExecution.SafetySettingsHash"/>. Same encoding as
    /// <c>RequestHash</c>/<c>PromptHash</c> (raw SHA-256 over UTF-8), so the
    /// hashes are directly comparable. Null or whitespace content yields null,
    /// so capabilities without a system prompt or safety settings stay null
    /// rather than hashed-empty. Only hashes are persisted — never the content
    /// itself, so prompt text and provider payloads never reach the table.
    /// </summary>
    public static string? HashContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Canonical JSON of provider usage dimensions beyond the fixed columns:
    /// provider-reported <c>usage.*</c> metadata keys plus the metered
    /// <see cref="ProviderUsage"/> fields. Null when nothing is present. Secret-
    /// looking keys and large values are dropped so telemetry never carries
    /// credentials or payloads.
    /// </summary>
    public static string? BuildUsageDimensionsJson(
        ProviderUsage? usage,
        IReadOnlyDictionary<string, string>? rawMetadata)
    {
        var dimensions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (usage is null)
        {
            usage = new ProviderUsage(null, null, null, null);
        }

        if (usage.TokensIn.HasValue)
        {
            dimensions["tokens_in"] = usage.TokensIn.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (usage.TokensOut.HasValue)
        {
            dimensions["tokens_out"] = usage.TokensOut.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (usage.AudioSeconds.HasValue && !double.IsNaN(usage.AudioSeconds.Value) && !double.IsInfinity(usage.AudioSeconds.Value))
        {
            dimensions["audio_seconds"] = usage.AudioSeconds.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (usage.EstimatedCostUsd.HasValue && !double.IsNaN(usage.EstimatedCostUsd.Value) && !double.IsInfinity(usage.EstimatedCostUsd.Value))
        {
            dimensions["estimated_cost_usd"] = usage.EstimatedCostUsd.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (rawMetadata is not null)
        {
            foreach (var pair in rawMetadata)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                // Only provider-declared usage dimensions; never payloads,
                // base64 audio, or job handles (recorded separately).
                if (!pair.Key.StartsWith("usage.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ConfigurationHashCalculator.IsSecretKey(pair.Key) || pair.Value.Length > 128)
                {
                    continue;
                }

                dimensions[pair.Key] = pair.Value;
            }
        }

        if (dimensions.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(dimensions);
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
                        && string.Equals(existing.ResponseHash, execution.ResponseHash, StringComparison.Ordinal)
                        && string.Equals(existing.OutputContentHash, execution.OutputContentHash, StringComparison.Ordinal))
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
