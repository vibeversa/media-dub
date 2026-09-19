using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Run-scoped segment building on <c>media.preparation</c> (the workload queue
/// for SegmentBuild per <c>WorkQueueRouter</c>; the task text names
/// <c>control.orchestration</c>, but routing is workload-class based and
/// SegmentBuild is CPU preparation alongside Vad, so this worker shares
/// <c>media.preparation</c> with a pre-claim stage filter). Claims via
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced), loads
/// the VAD artifact (input hash when present, else latest <c>VadRegions</c>)
/// plus the media duration, builds deterministic segments via
/// <see cref="SegmentBuilderService"/> (quota failures throw
/// <c>QUOTA_EXCEEDED</c> before any insert, so no partial rows), replaces the
/// run's segments/overlap rows in one transaction (deterministic ids make
/// same-attempt redelivery idempotent; downstream segment stages have not
/// started because Diarization requires this barrier), persists one
/// <c>Segments</c> JSON artifact with <c>parents=[vad]</c>, initializes
/// <c>RunStageSummary.ExpectedUnits</c> for the segment-scoped stages
/// (Transcription, Translation, VoiceGeneration, TimingOptimization) to the
/// segment count (zero when silent, so the saga skips downstream units),
/// completes the execution, and publishes <c>StageCompleted</c>.
/// </summary>
public sealed class SegmentBuilderWorker : BaseConsumer<StageWorkRequested>
{
    private static readonly StageType[] SegmentScopedStages =
    [
        StageType.Transcription,
        StageType.Translation,
        StageType.VoiceGeneration,
        StageType.TimingOptimization,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly SegmentBuilderService _builder;
    private readonly ArtifactService _artifacts;
    private readonly IArtifactStorage _storage;
    private readonly BarrierService _barrier;
    private readonly ILogger<SegmentBuilderWorker> _logger;

    public SegmentBuilderWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        SegmentBuilderService builder,
        ArtifactService artifacts,
        IArtifactStorage storage,
        BarrierService barrier,
        ILogger<SegmentBuilderWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(barrier);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _builder = builder;
        _artifacts = artifacts;
        _storage = storage;
        _barrier = barrier;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.SegmentBuild), StringComparison.Ordinal);
    }

    protected override async Task HandleAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution? execution,
        CancellationToken cancellationToken)
    {
        if (execution is null)
        {
            return;
        }

        if (execution.Status is StageStatus.Completed or StageStatus.Skipped)
        {
            return;
        }

        try
        {
            if (execution.StageType != StageType.SegmentBuild)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not SegmentBuild.");
            }

            var vad = await LoadVadAsync(execution, cancellationToken).ConfigureAwait(false);
            var durationMs = await LoadDurationMsAsync(execution, vad.DurationMs, cancellationToken).ConfigureAwait(false);

            SegmentBuildResult built;
            if (vad.Regions.Count == 0 || durationMs <= 0)
            {
                if (vad.Regions.Count != 0 && durationMs <= 0)
                {
                    throw new ErrorCodeException(
                        ErrorCodes.PipelineInvariantViolation,
                        "Media duration must be positive.");
                }

                built = new SegmentBuildResult([], [], []);
            }
            else
            {
                built = await _builder.BuildAsync(
                    execution.ProcessingRunId, vad.Regions, durationMs,
                    null, null, cancellationToken).ConfigureAwait(false);
            }

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);
            await ReplaceSegmentsAsync(execution, built, cancellationToken).ConfigureAwait(false);
            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var segmentsJson = JsonSerializer.Serialize(new
            {
                schemaVersion = "1",
                vadArtifactId = vad.ArtifactId.ToString("N"),
                mediaDurationMs = durationMs,
                segments = built.Segments.Select(s => new
                {
                    id = s.Id.ToString("N"),
                    sequence = s.Sequence,
                    startMs = s.StartMs,
                    endMs = s.EndMs,
                    durationMs = s.DurationMs,
                    status = "Pending",
                }),
                overlaps = built.Groups.Select(g => new
                {
                    groupId = g.Id.ToString("N"),
                    startMs = g.StartMs,
                    endMs = g.EndMs,
                    members = built.Overlaps
                        .Where(o => o.OverlapGroupId == g.Id)
                        .OrderBy(o => o.Order)
                        .Select(o => new
                        {
                            segmentId = o.SegmentId.ToString("N"),
                            relation = o.RelationType,
                            order = o.Order,
                            overlapStartMs = o.OverlapStartMs,
                            overlapEndMs = o.OverlapEndMs,
                        }),
                }),
            });

            PublishResult published;
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(segmentsJson), writable: false))
            {
                published = await _artifacts.PublishAsync(
                    execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                    StageType.SegmentBuild, ArtifactType.Segments,
                    stream, ".json", "application/json",
                    null, null,
                    execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                    [vad.ArtifactId],
                    execution.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            using (TenantContext.BeginScope(execution.TenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                    segmentsJson, published.ArtifactId, execution.TenantId).ConfigureAwait(false);
            }

            foreach (var stage in SegmentScopedStages)
            {
                await _barrier.EnsureSummaryAsync(
                    execution.TenantId, execution.ProcessingRunId, stage,
                    built.Segments.Count, cancellationToken).ConfigureAwait(false);
            }

            var stages = new StageExecutionService(_contextFactory, _retryOptions);
            await stages.CompleteAsync(
                execution.TenantId, execution.Id,
                execution.LeaseOwner, execution.LeaseToken,
                [published.ArtifactId.ToString("N")],
                cancellationToken).ConfigureAwait(false);

            var inbound = context.Message;
            var correlationId = string.IsNullOrWhiteSpace(inbound.CorrelationId)
                ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
                : inbound.CorrelationId;
            await context.Publish(new StageCompleted(
                Guid.NewGuid(), correlationId,
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                execution.Id, execution.StageType.ToString(), execution.ScopeType.ToString(), execution.ScopeId,
                execution.SegmentId, MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, execution.Attempt,
                execution.InputHash, execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                nameof(StageType.SegmentBuild),
                [published.ArtifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Worker poison contract: permanent failures are recorded via TryFailAsync; transient rethrows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "Segment build failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.SegmentBuild), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private sealed record VadBundle(Guid ArtifactId, List<VadRegion> Regions, int DurationMs);

    private sealed record VadRegionDoc(int StartMs, int EndMs, double Confidence);

    private sealed record VadArtifactDoc(
        string? SchemaVersion,
        string? DialogueArtifactId,
        string? Language,
        int DurationMs,
        List<VadRegionDoc>? Regions);

    private async Task<VadBundle> LoadVadAsync(StageExecution execution, CancellationToken cancellationToken)
    {
        Guid vadId = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(execution.InputHash)
            && Guid.TryParseExact(execution.InputHash.Trim(), "N", out var inputId)
            && inputId != Guid.Empty)
        {
            vadId = inputId;
        }
        else
        {
            using (TenantContext.BeginScope(execution.TenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                vadId = await db.Set<Artifact>()
                    .Where(a => a.ProcessingRunId == execution.ProcessingRunId && a.Type == ArtifactType.VadRegions)
                    .OrderByDescending(a => a.CreatedAt)
                    .Select(a => a.Id)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (vadId == Guid.Empty)
        {
            throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "VAD regions artifact was not found.");
        }

        Artifact artifact;
        ContentObject content;
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var loaded = await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == vadId, cancellationToken).ConfigureAwait(false);
            if (loaded is null || loaded.TenantId != execution.TenantId || loaded.ProcessingRunId != execution.ProcessingRunId)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "VAD regions artifact was not found.");
            }

            artifact = loaded;
            var loadedContent = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == artifact.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (loadedContent is null || loadedContent.Status != ContentObjectStatus.Committed)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "VAD regions content is unavailable.");
            }

            content = loadedContent;

            if (!string.IsNullOrWhiteSpace(artifact.MetadataJson))
            {
                var parsed = TryParseVad(artifact.MetadataJson);
                if (parsed is not null)
                {
                    return new VadBundle(artifact.Id, parsed.Regions, parsed.DurationMs);
                }
            }
        }

        Stream download;
        try
        {
            download = await _storage.DownloadAsync(content.StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or System.Net.Sockets.SocketException or IOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DomainException || ex is AppException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        using (download)
        {
            using var reader = new StreamReader(download);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var parsed = TryParseVad(json);
            if (parsed is null)
            {
                throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "VAD regions artifact is unreadable.");
            }

            return new VadBundle(artifact.Id, parsed.Regions, parsed.DurationMs);
        }
    }

    private static VadBundle? TryParseVad(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<VadArtifactDoc>(json, JsonOptions);
            if (doc?.Regions is null)
            {
                return null;
            }

            var regions = doc.Regions
                .Select(r => new VadRegion(r.StartMs, r.EndMs, r.Confidence))
                .ToList();
            return new VadBundle(Guid.Empty, regions, doc.DurationMs);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<int> LoadDurationMsAsync(StageExecution execution, int vadDurationMs, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var duration = await db.Set<MediaAsset>()
                .Where(a => a.ProjectId == execution.ProjectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .Select(a => (int?)a.DurationMs)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (duration.HasValue && duration.Value > 0)
            {
                return duration.Value;
            }

            return Math.Max(0, vadDurationMs);
        }
    }

    private async Task ReplaceSegmentsAsync(
        StageExecution execution,
        SegmentBuildResult built,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var runId = execution.ProcessingRunId;
                var existingOverlaps = await db.Set<SegmentOverlap>()
                    .Where(o => o.TenantId == execution.TenantId)
                    .Join(
                        db.Set<OverlapGroup>().Where(g => g.RunId == runId),
                        o => o.OverlapGroupId,
                        g => g.Id,
                        (o, g) => o)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                db.Set<SegmentOverlap>().RemoveRange(existingOverlaps);

                var existingGroups = await db.Set<OverlapGroup>()
                    .Where(g => g.RunId == runId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                db.Set<OverlapGroup>().RemoveRange(existingGroups);

                var existingSegments = await db.Set<SpeechSegment>()
                    .Where(s => s.RunId == runId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                db.Set<SpeechSegment>().RemoveRange(existingSegments);

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                foreach (var segment in built.Segments)
                {
                    db.Set<SpeechSegment>().Add(new SpeechSegment(
                        segment.Id, execution.TenantId, execution.ProjectId, runId,
                        segment.Sequence, segment.StartMs, segment.EndMs,
                        "Pending", null, now));
                }

                foreach (var group in built.Groups)
                {
                    db.Set<OverlapGroup>().Add(new OverlapGroup(
                        group.Id, execution.TenantId, execution.ProjectId, runId,
                        group.StartMs, group.EndMs, now));
                }

                foreach (var overlap in built.Overlaps)
                {
                    db.Set<SegmentOverlap>().Add(new SegmentOverlap(
                        overlap.Id, execution.TenantId, overlap.OverlapGroupId, overlap.SegmentId,
                        overlap.RelationType, overlap.Order,
                        overlap.OverlapStartMs, overlap.OverlapEndMs));
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Rollback best effort; original exception propagates.
                }

                throw;
            }
        }
    }

    private async Task EnsureLeaseRunningAsync(StageExecution execution, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var current = await db.Set<StageExecution>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == execution.Id, cancellationToken).ConfigureAwait(false);
            if (current is null
                || current.Status != StageStatus.Running
                || !string.Equals(current.LeaseOwner, execution.LeaseOwner, StringComparison.Ordinal)
                || !string.Equals(current.LeaseToken, execution.LeaseToken, StringComparison.Ordinal))
            {
                throw new LeaseLostException($"Lease lost for stage execution '{execution.Id}'.");
            }

            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == execution.ProcessingRunId, cancellationToken).ConfigureAwait(false);
            if (run is not null
                && run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                throw new LeaseLostException($"Processing run '{run.Id}' is '{run.Status}'; aborting before commit.");
            }
        }
    }
}
