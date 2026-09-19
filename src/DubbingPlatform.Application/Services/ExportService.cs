using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Exports;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Result of an export generation.
/// </summary>
public sealed record ExportGenerationResult(
    Guid ExportJobId,
    Guid ArtifactId,
    bool IsPartial,
    string CompletenessJson);

/// <summary>
/// On-demand durable exports, independent of the core DAG. Exports are
/// generated from immutable selected-version snapshots at generation time
/// (selected transcripts/translations, speakers + voice assignments, QC
/// results); artifacts are immutable (<c>ArtifactType.Export</c> under
/// <c>StageType.Render</c> — there is no <c>Export</c> stage type — with empty
/// parents and auditable <c>metadata_json</c>). Rendered media is never
/// exported: subtitle/JSON formats carry <c>isPartial</c> when the run is not
/// <c>Completed</c> instead of failing. Zero segments fails with
/// <c>EXPORT_NOT_READY</c> (409 per the frozen catalog; the task text says 400
/// but the catalog owns the mapping). Ownership is enforced on every method;
/// downloads use 15-minute presigned URLs. Only ids, formats, counts, and
/// hashes are logged by callers — never export text.
/// </summary>
public sealed class ExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ProcessingRunStatus[] ExportableRunStatuses =
    [
        ProcessingRunStatus.Completed,
        ProcessingRunStatus.Failed,
        ProcessingRunStatus.Cancelled,
        ProcessingRunStatus.ManualReviewRequired,
    ];

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly AuditService _audit;

    public ExportService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        AuditService audit)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(audit);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _audit = audit;
    }

    /// <summary>
    /// Creates a <c>Pending</c> export job for the latest exportable run and
    /// audits <c>export.create</c>. The caller publishes
    /// <c>ExportJobRequested</c> best effort; the worker performs generation.
    /// Throws <c>EXPORT_NOT_READY</c> when no exportable run exists or the run
    /// has zero segments.
    /// </summary>
    public async Task<Guid> RequestAsync(
        Guid tenantId,
        Guid projectId,
        ExportFormat format,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var run = await FindExportableRunAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            throw new ErrorCodeException(
                ErrorCodes.ExportNotReady,
                "No completed or partial run exists for this project; exports are not ready.");
        }

        var segmentCount = await CountSegmentsAsync(tenantId, run.Id, cancellationToken).ConfigureAwait(false);
        if (segmentCount == 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.ExportNotReady,
                "The run has no segments; exports are not ready.");
        }

        var exportId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<ExportJob>().Add(new ExportJob(
                exportId, tenantId, projectId, run.Id, format,
                ExportJobStatus.Pending, null, null, false, now, now));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, projectId, actor.Trim(), "export.create",
            "export", exportId.ToString("N"),
            JsonSerializer.Serialize(new { format = ExportFormatParser.ToWireName(format), runId = run.Id.ToString("N") }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        return exportId;
    }

    /// <summary>
    /// Generates export content for a job: claims <c>Pending→Running</c>
    /// (a <c>Running</c> job is resumed — generation is deterministic and
    /// republish creates new immutable rows), snapshots immutable run data,
    /// publishes one <c>Export</c> artifact, links <c>ExportArtifact</c>,
    /// marks <c>Completed</c> with <c>CompletenessJson</c>, and audits
    /// <c>export.complete</c>. Idempotent: a <c>Completed</c> job returns its
    /// existing artifact without regenerating. A <c>Cancelled</c> job is left
    /// untouched (no artifact). Permanent failures mark the job
    /// <c>Failed</c> (no artifact) and rethrow for the worker's poison
    /// contract; transient storage failures rethrow without marking so a
    /// retry can resume.
    /// </summary>
    public async Task<ExportGenerationResult> GenerateAsync(
        Guid tenantId,
        Guid projectId,
        Guid exportJobId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(exportJobId, nameof(exportJobId));

        var job = await LoadOwnedJobAsync(tenantId, projectId, exportJobId, cancellationToken).ConfigureAwait(false);
        if (job.Status == ExportJobStatus.Completed)
        {
            var existing = ParseArtifactRef(job.ArtifactIdRef);
            return new ExportGenerationResult(job.Id, existing, job.IsPartial, job.CompletenessJson ?? "{}");
        }

        if (job.Status == ExportJobStatus.Cancelled)
        {
            throw new ErrorCodeException(ErrorCodes.ExportNotReady, $"Export '{exportJobId}' is cancelled; no artifact will be produced.");
        }

        if (job.Status != ExportJobStatus.Pending && job.Status != ExportJobStatus.Running)
        {
            throw new ErrorCodeException(ErrorCodes.ExportNotReady, $"Export '{exportJobId}' is {job.Status}; generation is not available.");
        }

        try
        {
            await TransitionAsync(tenantId, job.Id, ExportJobStatus.Running, cancellationToken).ConfigureAwait(false);
            var snapshot = await BuildSnapshotAsync(tenantId, projectId, job.ProcessingRunId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Segments.Count == 0)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ExportNotReady,
                    "The run has no segments; exports are not ready.");
            }

            var content = GenerateContent(job.Format, snapshot);
            var bytes = Encoding.UTF8.GetBytes(content);
            var extension = ExportFormatParser.ExtensionFor(job.Format);
            var contentType = ExportFormatParser.ContentTypeFor(job.Format);

            PublishResult published;
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                published = await _artifacts.PublishAsync(
                    tenantId, projectId, job.ProcessingRunId,
                    StageType.Render, ArtifactType.Export,
                    stream, extension, contentType,
                    null, null, null, null,
                    [],
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            var completenessJson = JsonSerializer.Serialize(snapshot.Completeness, JsonOptions);
            var metadataJson = JsonSerializer.Serialize(new
            {
                schemaVersion = "1",
                exportJobId = job.Id.ToString("N"),
                runId = job.ProcessingRunId.ToString("N"),
                format = ExportFormatParser.ToWireName(job.Format),
                isPartial = !snapshot.Completeness.IsComplete,
                completed = snapshot.Completeness.Completed,
                failed = snapshot.Completeness.Failed,
                skipped = snapshot.Completeness.Skipped,
                reviewCount = snapshot.Completeness.ReviewCount,
                sourceHash = snapshot.Completeness.SourceHash,
            }, JsonOptions);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                    metadataJson, published.ArtifactId, tenantId).ConfigureAwait(false);
                db.Set<ExportArtifact>().Add(new ExportArtifact(
                    Guid.NewGuid(), tenantId, job.Id, published.ArtifactId, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await CompleteAsync(
                tenantId, job.Id, published.ArtifactId,
                completenessJson, !snapshot.Completeness.IsComplete,
                cancellationToken).ConfigureAwait(false);

            await _audit.LogAsync(
                tenantId, projectId, "export-worker", "export.complete",
                "export", job.Id.ToString("N"),
                JsonSerializer.Serialize(new { format = ExportFormatParser.ToWireName(job.Format), artifactId = published.ArtifactId.ToString("N") }, JsonOptions),
                cancellationToken).ConfigureAwait(false);

            return new ExportGenerationResult(job.Id, published.ArtifactId, !snapshot.Completeness.IsComplete, completenessJson);
        }
        catch (Exception ex) when (!IsTransient(ex) && !IsNotReady(ex))
        {
            await TryMarkFailedAsync(tenantId, job.Id, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (IsNotReady(ex))
        {
            await TryMarkFailedAsync(tenantId, job.Id, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Builds the deterministic snapshot for a run. Public for worker reuse
    /// and test seeding verification; pure ordering (segments by sequence,
    /// speakers by key, QC by scope/code).
    /// </summary>
    public async Task<ExportRunData> BuildSnapshotAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));

        ProcessingRun? run;
        string sourceHash;
        List<SpeechSegment> segments;
        List<Speaker> speakers;
        List<SpeakerVoiceAssignment> assignments;
        List<VoiceProfile> voices;
        List<TranscriptVersion> transcripts;
        List<TranslationVersion> translations;
        List<QualityResult> quality;
        int openReviews;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.TenantId != tenantId || run.ProjectId != projectId)
            {
                throw new NotFoundException($"Processing run '{runId}' was not found.");
            }

            var asset = await db.Set<MediaAsset>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            sourceHash = asset?.ContentHash ?? run.ConfigurationHash;

            segments = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .Where(s => s.RunId == runId)
                .OrderBy(s => s.Sequence)
                .ThenBy(s => s.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            speakers = await db.Set<Speaker>()
                .AsNoTracking()
                .Where(s => s.ProjectId == projectId)
                .OrderBy(s => s.SpeakerKey)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            assignments = await db.Set<SpeakerVoiceAssignment>()
                .AsNoTracking()
                .Where(a => a.RunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var voiceIds = assignments.Select(a => a.VoiceProfileId).Distinct().ToList();
            voices = voiceIds.Count == 0
                ? []
                : await db.Set<VoiceProfile>()
                    .AsNoTracking()
                    .Where(v => voiceIds.Contains(v.Id))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

            transcripts = await db.Set<TranscriptVersion>()
                .AsNoTracking()
                .Where(t => t.RunId == runId && t.IsSelected)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            translations = await db.Set<TranslationVersion>()
                .AsNoTracking()
                .Where(t => t.RunId == runId && t.IsSelected)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            quality = await db.Set<QualityResult>()
                .AsNoTracking()
                .Where(q => q.ProcessingRunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            openReviews = await db.Set<ReviewItem>()
                .AsNoTracking()
                .CountAsync(r => r.ProcessingRunId == runId && r.Status == ReviewStatus.Open, cancellationToken).ConfigureAwait(false);
        }

        var speakerById = speakers.ToDictionary(s => s.Id);
        var transcriptBySegment = transcripts
            .GroupBy(t => t.SegmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.CreatedAt).First().Text);
        var translationBySegment = translations
            .GroupBy(t => t.SegmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.CreatedAt).First().PrimaryText);
        var voiceBySpeaker = assignments
            .GroupBy(a => a.SpeakerId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.CreatedAt).First());
        var voiceProfileById = voices.ToDictionary(v => v.Id);
        var sequenceBySegment = segments.ToDictionary(s => s.Id, s => s.Sequence);

        var exportSegments = segments
            .OrderBy(s => s.Sequence)
            .ThenBy(s => s.Id)
            .Select(s =>
            {
                speakerById.TryGetValue(s.SpeakerId ?? Guid.Empty, out var speaker);
                transcriptBySegment.TryGetValue(s.Id, out var transcript);
                translationBySegment.TryGetValue(s.Id, out var translation);
                return new ExportSegment(
                    s.Sequence, s.Id, s.StartMs, s.EndMs, s.Status,
                    speaker?.SpeakerKey, speaker?.DisplayName,
                    transcript, translation);
            })
            .ToList();

        var exportSpeakers = speakers
            .OrderBy(s => s.SpeakerKey, StringComparer.Ordinal)
            .Select(s =>
            {
                voiceBySpeaker.TryGetValue(s.Id, out var assignment);
                VoiceProfile? voice = null;
                if (assignment is not null)
                {
                    voiceProfileById.TryGetValue(assignment.VoiceProfileId, out voice);
                }

                return new ExportSpeaker(
                    s.SpeakerKey, s.DisplayName, s.FirstAppearanceMs, s.LastAppearanceMs,
                    voice?.Provider, voice?.VoiceId, voice?.VoiceVersion,
                    voice is null ? null : voice.Type.ToString(),
                    assignment?.AssignmentReason);
            })
            .ToList();

        var exportQuality = quality
            .OrderBy(q => q.ScopeType.ToString(), StringComparer.Ordinal)
            .ThenBy(q => q.ScopeId, StringComparer.Ordinal)
            .ThenBy(q => q.Code, StringComparer.Ordinal)
            .Select(q => new ExportQcEntry(
                q.ScopeType.ToString(), q.ScopeId,
                q.SegmentId.HasValue && sequenceBySegment.TryGetValue(q.SegmentId.Value, out var seq) ? seq : null,
                q.Code, q.Severity, q.Status.ToString(), q.Message))
            .ToList();

        var skipped = exportSegments.Count(s => string.Equals(s.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var completed = exportSegments.Count(s =>
            !string.Equals(s.Status, "Skipped", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(s.TranscriptText)
            && !string.IsNullOrWhiteSpace(s.TranslationText));
        var failed = exportSegments.Count - skipped - completed;
        var isComplete = run.Status == ProcessingRunStatus.Completed && failed == 0 && skipped == 0 && openReviews == 0;

        var completeness = new ExportCompleteness(
            projectId, runId, sourceHash,
            completed, Math.Max(0, failed), skipped, openReviews,
            isComplete, DateTimeOffset.UtcNow);

        return new ExportRunData(
            projectId, runId, sourceHash,
            run.Status == ProcessingRunStatus.Completed,
            exportSegments, exportSpeakers, exportQuality, completeness);
    }

    /// <summary>
    /// Paginated export listing (newest last, matching controller order).
    /// </summary>
    public async Task<(IReadOnlyList<ExportJob> Items, long Total)> ListAsync(
        Guid tenantId,
        Guid projectId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<ExportJob>().AsNoTracking().Where(e => e.ProjectId == projectId);
            var total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var items = await scoped
                .OrderBy(e => e.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return (items, total);
        }
    }

    /// <summary>
    /// Loads one owned export job (404 vs 403 split preserved).
    /// </summary>
    public async Task<ExportJob> GetAsync(
        Guid tenantId,
        Guid projectId,
        Guid exportJobId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(exportJobId, nameof(exportJobId));
        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        return await LoadOwnedJobAsync(tenantId, projectId, exportJobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues a 15-minute presigned download URL for a completed export.
    /// </summary>
    public async Task<(string Url, DateTimeOffset ExpiresAt)> GetDownloadUrlAsync(
        Guid tenantId,
        Guid projectId,
        Guid exportJobId,
        CancellationToken cancellationToken = default)
    {
        var job = await GetAsync(tenantId, projectId, exportJobId, cancellationToken).ConfigureAwait(false);
        if (job.Status != ExportJobStatus.Completed)
        {
            throw new ErrorCodeException(
                ErrorCodes.ExportNotReady,
                $"Export '{exportJobId}' is {job.Status}; download is not ready.");
        }

        var artifactId = ParseArtifactRef(job.ArtifactIdRef);
        var url = await _artifacts.GetDownloadUrlAsync(
            tenantId, projectId, artifactId, SignedUrlPolicy.DefaultExpiry, cancellationToken).ConfigureAwait(false);
        return (url, DateTimeOffset.UtcNow.Add(SignedUrlPolicy.DefaultExpiry));
    }

    /// <summary>
    /// Generates export content for a snapshot. Pure dispatch; deterministic
    /// per generator. Public so workers and tests share one path.
    /// </summary>
    public static string GenerateContent(ExportFormat format, ExportRunData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return format switch
        {
            ExportFormat.Srt => SrtGenerator.Generate(data),
            ExportFormat.WebVtt => WebVttGenerator.Generate(data),
            ExportFormat.JsonTimeline => TimelineJsonGenerator.Generate(data),
            ExportFormat.SpeakerMetadataJson => SpeakerMetadataGenerator.Generate(data),
            ExportFormat.TranscriptJson => TranscriptJsonGenerator.Generate(data),
            ExportFormat.TranslationJson => TranslationJsonGenerator.Generate(data),
            ExportFormat.QualityReportJson => QualityReportGenerator.Generate(data),
            _ => throw new DomainException($"Unknown export format '{format}'."),
        };
    }

    private async Task<ProcessingRun?> FindExportableRunAsync(
        Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId && ExportableRunStatuses.Contains(r.Status))
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> CountSegmentsAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<SpeechSegment>()
                .AsNoTracking()
                .CountAsync(s => s.RunId == runId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExportJob> LoadOwnedJobAsync(
        Guid tenantId, Guid projectId, Guid exportJobId, CancellationToken cancellationToken)
    {
        ExportJob? job;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            job = await db.Set<ExportJob>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == exportJobId, cancellationToken).ConfigureAwait(false);
        }

        if (job is null)
        {
            throw new NotFoundException($"Export '{exportJobId}' was not found.");
        }

        if (job.TenantId != tenantId || job.ProjectId != projectId)
        {
            throw new ForbiddenException($"Export '{exportJobId}' does not belong to the current tenant/project.");
        }

        return job;
    }

    private async Task RequireProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }
    }

    private async Task TransitionAsync(
        Guid tenantId, Guid exportJobId, ExportJobStatus to, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var job = await db.Set<ExportJob>()
                .FirstOrDefaultAsync(e => e.Id == exportJobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                throw new NotFoundException($"Export '{exportJobId}' was not found.");
            }

            ExportStateMachine.EnsureCanTransition(job.Status, to);
            if (job.Status == to)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE export_jobs SET status = {0}, updated_at = {1} WHERE id = {2} AND tenant_id = {3}",
                to.ToString(), now, exportJobId, tenantId).ConfigureAwait(false);
        }
    }

    private async Task CompleteAsync(
        Guid tenantId,
        Guid exportJobId,
        Guid artifactId,
        string completenessJson,
        bool isPartial,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var job = await db.Set<ExportJob>()
                .FirstOrDefaultAsync(e => e.Id == exportJobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                throw new NotFoundException($"Export '{exportJobId}' was not found.");
            }

            ExportStateMachine.EnsureCanTransition(job.Status, ExportJobStatus.Completed);
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE export_jobs SET status = {0}, artifact_id_ref = {1}, completeness_json = {2}, is_partial = {3}, updated_at = {4} WHERE id = {5} AND tenant_id = {6}",
                ExportJobStatus.Completed.ToString(), artifactId.ToString("N"), completenessJson, isPartial, now,
                exportJobId, tenantId).ConfigureAwait(false);
        }
    }

    private async Task TryMarkFailedAsync(Guid tenantId, Guid exportJobId, CancellationToken cancellationToken)
    {
        try
        {
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                var job = await db.Set<ExportJob>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == exportJobId, cancellationToken).ConfigureAwait(false);
                if (job is null || job.Status is ExportJobStatus.Completed or ExportJobStatus.Cancelled or ExportJobStatus.Failed)
                {
                    return;
                }

                try
                {
                    ExportStateMachine.EnsureCanTransition(job.Status, ExportJobStatus.Failed);
                }
                catch (DomainException)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE export_jobs SET status = {0}, updated_at = {1} WHERE id = {2} AND tenant_id = {3}",
                    ExportJobStatus.Failed.ToString(), now, exportJobId, tenantId).ConfigureAwait(false);
            }

            await _audit.LogAsync(
                tenantId, null, "export-worker", "export.failed",
                "export", exportJobId.ToString("N"), null, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort failure marking; the original exception propagates.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private static Guid ParseArtifactRef(string? artifactRef)
    {
        if (string.IsNullOrWhiteSpace(artifactRef)
            || !Guid.TryParse(artifactRef.Trim(), out var artifactId)
            || artifactId == Guid.Empty)
        {
            throw new ErrorCodeException(ErrorCodes.ExportNotReady, "Export has no downloadable artifact yet.");
        }

        return artifactId;
    }

    private static bool IsTransient(Exception exception)
    {
        return exception is HttpRequestException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or IOException;
    }

    private static bool IsNotReady(Exception exception)
    {
        return exception is ErrorCodeException coded
            && string.Equals(coded.ErrorCode, ErrorCodes.ExportNotReady, StringComparison.Ordinal);
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException($"{name} must not be empty.");
        }
    }
}
