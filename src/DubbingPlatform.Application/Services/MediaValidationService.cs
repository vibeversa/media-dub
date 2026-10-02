using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Run-scoped media validation result: the validated asset plus the artifacts
/// the stage declares as outputs.
/// </summary>
public sealed record MediaValidationResult(
    Guid AssetId,
    Guid ContentObjectId,
    IReadOnlyList<Guid> OutputArtifactIds);

/// <summary>
/// Run-scoped <c>MediaValidation</c> stage (GAP-013). Validation itself happens
/// pre-run during ingestion (<c>MediaIngestionService</c> probes the uploaded
/// bytes and persists a <c>Valid</c>/<c>Invalid</c> <see cref="MediaAsset"/>
/// plus a <c>MediaValidation</c> stage execution). The run-scoped stage
/// re-affirms that verdict for the run's own
/// <see cref="StageExecution"/> so the DAG barrier advances with the same
/// lease-fenced, instrumented path as every other stage — it deliberately does
/// not re-probe (MediaAnalysis does that on the same bytes one stage later).
///
/// Fails permanently (<c>ARTIFACT_UNAVAILABLE</c>) when the project has no
/// Valid asset, no committed source content, or no ingested validation
/// evidence; those are pipeline faults, not transient faults.
/// </summary>
public sealed class MediaValidationService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ILogger<MediaValidationService> _logger;

    public MediaValidationService(
        IStageExecutionContextFactory contextFactory,
        ILogger<MediaValidationService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Re-affirms ingestion validation for the run and completes the run-scoped
    /// stage execution with the source (and, when present, the ingestion probe
    /// analysis) artifact as outputs.
    /// </summary>
    public async Task<MediaValidationResult> ValidateAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid executionId,
        string owner,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var source = await SourceMediaLoader.LoadAsync(_contextFactory, tenantId, projectId, cancellationToken).ConfigureAwait(false);

        var outputs = new List<Guid>();
        if (source.SourceArtifactId.HasValue)
        {
            outputs.Add(source.SourceArtifactId.Value);
        }

        var probeArtifactId = await LoadIngestionProbeArtifactAsync(tenantId, source.Content.Id, cancellationToken).ConfigureAwait(false);
        if (probeArtifactId.HasValue)
        {
            outputs.Add(probeArtifactId.Value);
        }

        var outputIds = outputs.Select(id => id.ToString("N")).ToList();
        var stages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
        await stages.CompleteAsync(
            tenantId, executionId, owner, token,
            [.. outputIds],
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Media validation re-affirmed for run {RunId}: asset {AssetId} ({ContentHash}).",
            runId, source.Asset.Id, source.Content.ContentHash);

        return new MediaValidationResult(source.Asset.Id, source.Content.Id, outputs);
    }

    /// <summary>
    /// The ingestion-published <c>FfprobeAnalysis</c> artifact for the source
    /// content. Null when the deployment predates the artifact; validation
    /// evidence is then the Valid asset status alone.
    /// </summary>
    private async Task<Guid?> LoadIngestionProbeArtifactAsync(
        Guid tenantId,
        Guid contentObjectId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ContentObjectId == contentObjectId && a.Type == ArtifactType.FfprobeAnalysis)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}