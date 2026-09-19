using System.Diagnostics;

namespace DubbingPlatform.Infrastructure.Observability;

/// <summary>
/// Trace enrichment helper. Every <see cref="Activity"/> produced by the API,
/// consumers, provider calls, and FFmpeg execution must carry the
/// tenant/project/run/stage/provider/model/attempt tags (ids only, never
/// secrets or media content). All parameters are optional so call sites can
/// enrich with whatever identity they hold; empty values are skipped.
/// </summary>
public static class TraceEnricher
{
    /// <summary>
    /// Sets enrichment tags on <paramref name="activity"/> (defaults to
    /// <see cref="Activity.Current"/>). Guids are formatted <c>N</c> (no PII).
    /// </summary>
    public static void Set(
        Activity? activity,
        Guid? tenantId = null,
        Guid? projectId = null,
        Guid? runId = null,
        string? stage = null,
        string? provider = null,
        string? model = null,
        int? attempt = null)
    {
        var target = activity ?? Activity.Current;
        if (target is null)
        {
            return;
        }

        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            target.SetTag("tenant.id", tenantId.Value.ToString("N"));
        }

        if (projectId.HasValue && projectId.Value != Guid.Empty)
        {
            target.SetTag("project.id", projectId.Value.ToString("N"));
        }

        if (runId.HasValue && runId.Value != Guid.Empty)
        {
            target.SetTag("run.id", runId.Value.ToString("N"));
        }

        if (!string.IsNullOrWhiteSpace(stage))
        {
            target.SetTag("stage", stage.Trim());
        }

        if (!string.IsNullOrWhiteSpace(provider))
        {
            target.SetTag("provider", provider.Trim());
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            target.SetTag("model", model.Trim());
        }

        if (attempt.HasValue && attempt.Value >= 0)
        {
            target.SetTag("attempt", attempt.Value);
        }
    }

    /// <summary>
    /// Enriches the current activity. Convenience overload for call sites that
    /// already hold string ids.
    /// </summary>
    public static void SetCurrent(
        Guid? tenantId = null,
        Guid? projectId = null,
        Guid? runId = null,
        string? stage = null,
        string? provider = null,
        string? model = null,
        int? attempt = null)
    {
        Set(Activity.Current, tenantId, projectId, runId, stage, provider, model, attempt);
    }
}
