using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Output;

/// <summary>
/// Single-handler output aggregate (Task 012 / 012A). All reads are
/// tenant-scoped and <c>AsNoTracking</c>; the ownership load runs under the
/// maintenance scope and any miss, soft-delete, or tenant mismatch on the
/// project yields 404 <c>NOT_FOUND</c> for this read surface (no existence
/// leak; the legacy <c>output/download</c> 403 split is preserved on that
/// route). The call performs a bounded batch of queries (currently 7 logical
/// reads) — no per-segment/per-artifact N+1. Every servable file is exposed
/// as a time-boxed signed URL (≤15 min, issued post-auth) — never storage
/// keys, bucket names, or internal paths. Only ids, states, counts, and
/// hashes are logged — never URLs, transcripts, or secrets.
/// </summary>
public sealed class OutputService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;

    public OutputService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
    }

    /// <summary>
    /// Builds the output aggregate for a project in one batched call.
    /// </summary>
    public async Task<OutputResponse> GetAsync(
        Guid tenantId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        DubbingProject project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException($"Project '{projectId}' was not found.");
            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId || isDeleted)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }

        ProcessingRun? run;
        List<SpeechSegment> segments;
        List<OutputAsset> outputs;
        List<Artifact> artifacts;
        List<QualityResult> quality;
        int openReviews;
        int speakerCount;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (run is null)
            {
                return Unavailable(project.UpdatedAt);
            }

            segments = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .Where(s => s.RunId == run.Id)
                .OrderBy(s => s.Sequence)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            outputs = await db.Set<OutputAsset>()
                .AsNoTracking()
                .Where(o => o.ProjectId == projectId)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            artifacts = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == run.Id)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            quality = await db.Set<QualityResult>()
                .AsNoTracking()
                .Where(q => q.ProcessingRunId == run.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            openReviews = await db.Set<ReviewItem>()
                .AsNoTracking()
                .CountAsync(r => r.ProcessingRunId == run.Id && r.Status == ReviewStatus.Open, cancellationToken).ConfigureAwait(false);

            speakerCount = await db.Set<Speaker>()
                .AsNoTracking()
                .CountAsync(s => s.ProjectId == projectId, cancellationToken).ConfigureAwait(false);
        }

        return await BuildAsync(tenantId, project, run, segments, outputs, artifacts, quality, openReviews, speakerCount, cancellationToken).ConfigureAwait(false);
    }

    private static OutputResponse Unavailable(DateTimeOffset updatedAt)
    {
        var missing = new List<string> { "NO_RUNS_YET" };
        OutputAssetEntryDto Entry()
        {
            return new OutputAssetEntryDto("unavailable", "unavailable", null, missing, new OutputCompletenessDto(0, 0));
        }

        return new OutputResponse(
            "Unavailable",
            "Unavailable",
            "NO_RUNS_YET",
            new OutputCompletenessDto(0, 0),
            null,
            null,
            new OutputItemsDto(
                Entry(), Entry(), [], Entry(), Entry(), Entry(), Entry(),
                new OutputQcDto("unavailable", "unavailable", "No runs yet.", null, missing)),
            [],
            updatedAt);
    }

    private async Task<OutputResponse> BuildAsync(
        Guid tenantId,
        DubbingProject project,
        ProcessingRun run,
        List<SpeechSegment> segments,
        List<OutputAsset> outputs,
        List<Artifact> artifacts,
        List<QualityResult> quality,
        int openReviews,
        int speakerCount,
        CancellationToken cancellationToken)
    {
        var total = segments.Count;
        var ready = segments.Count(s => string.Equals(s.Status, "Completed", StringComparison.OrdinalIgnoreCase));
        var completeness = new OutputCompletenessDto(ready, total);
        var updatedAt = new[] { project.UpdatedAt, run.UpdatedAt }
            .Concat(outputs.Select(o => o.CreatedAt))
            .Concat(artifacts.Select(a => a.CreatedAt))
            .Max();

        var warnings = new List<string>();
        if (openReviews > 0)
        {
            warnings.Add(string.Concat("review-open:", openReviews.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var blocked = quality.Any(q =>
            q.Status == QualityStatus.Blocked
            || string.Equals(q.Code, "QC_BLOCKED", StringComparison.OrdinalIgnoreCase));
        if (blocked)
        {
            warnings.Add("qc-blocked");
        }

        if (project.IsArchived)
        {
            warnings.Add("project-archived");
        }

        // Failed runs surface Failed with a catalog-neutral error code (run
        // status, never a stack trace). Generating runs surface Generating
        // with a display-only progress approximation.
        if (run.Status is ProcessingRunStatus.Failed or ProcessingRunStatus.Cancelled)
        {
            var missing = MissingFor(total, ready, blocked, openReviews);
            return new OutputResponse(
                "Failed",
                "Failed",
                null,
                completeness,
                null,
                run.Status.ToString().ToUpperInvariant(),
                await ItemsAsync(tenantId, project.Id, "failed", completeness, missing, outputs, artifacts, quality, speakerCount, cancellationToken).ConfigureAwait(false),
                warnings,
                updatedAt);
        }

        if (run.Status is ProcessingRunStatus.Pending or ProcessingRunStatus.Running or ProcessingRunStatus.Cancelling)
        {
            var progress = total == 0 ? 0.0 : Math.Round((double)ready / total * 100.0, 1);
            var missing = MissingFor(total, ready, blocked, openReviews);
            return new OutputResponse(
                "Generating",
                "Generating",
                null,
                completeness,
                progress,
                null,
                await ItemsAsync(tenantId, project.Id, "generating", completeness, missing, outputs, artifacts, quality, speakerCount, cancellationToken).ConfigureAwait(false),
                warnings,
                updatedAt);
        }

        // Terminal non-failed runs: Partial when any segment is not ready or
        // QC blocks, else Ready when rendered output exists, else Partial
        // (completed run with no rendered media is not silently Ready).
        var hasRendered = outputs.Count > 0;
        if (ready < total || blocked || openReviews > 0 || !hasRendered)
        {
            var missing = MissingFor(total, ready, blocked, openReviews);
            if (!hasRendered && ready == total && !blocked && openReviews == 0)
            {
                missing.Add("ARTIFACT_MISSING");
            }

            return new OutputResponse(
                "Partial",
                "Partial",
                null,
                completeness,
                null,
                null,
                await ItemsAsync(tenantId, project.Id, "partial", completeness, missing, outputs, artifacts, quality, speakerCount, cancellationToken).ConfigureAwait(false),
                warnings,
                updatedAt);
        }

        return new OutputResponse(
            "Ready",
            "Ready",
            null,
            completeness,
            null,
            null,
            await ItemsAsync(tenantId, project.Id, "ready", completeness, [], outputs, artifacts, quality, speakerCount, cancellationToken).ConfigureAwait(false),
            warnings,
            updatedAt);
    }

    private static List<string> MissingFor(int total, int ready, bool blocked, int openReviews)
    {
        var missing = new List<string>();
        if (total == 0)
        {
            missing.Add("SEGMENT_PENDING");
        }
        else if (ready < total)
        {
            missing.Add("SEGMENT_PENDING");
        }

        if (blocked)
        {
            missing.Add("QC_BLOCKED");
        }

        if (openReviews > 0)
        {
            missing.Add("REVIEW_OPEN");
        }

        if (missing.Count == 0)
        {
            missing.Add("ARTIFACT_MISSING");
        }

        return missing;
    }

    private async Task<OutputItemsDto> ItemsAsync(
        Guid tenantId,
        Guid projectId,
        string fallbackState,
        OutputCompletenessDto completeness,
        IReadOnlyList<string> missing,
        List<OutputAsset> outputs,
        List<Artifact> artifacts,
        List<QualityResult> quality,
        int speakerCount,
        CancellationToken cancellationToken)
    {
        var videoAsset = outputs.FirstOrDefault(o => string.Equals(o.MediaKind, "Video", StringComparison.OrdinalIgnoreCase));
        var audioAsset = outputs.FirstOrDefault(o => string.Equals(o.MediaKind, "Audio", StringComparison.OrdinalIgnoreCase));

        var transcript = LatestOf(artifacts, ArtifactType.Transcript);
        var translation = LatestOf(artifacts, ArtifactType.Translation);
        var timeline = LatestOf(artifacts, ArtifactType.Timeline);
        var mixed = LatestOf(artifacts, ArtifactType.MixedAudio)
            ?? LatestOf(artifacts, ArtifactType.GeneratedAudioFinal);
        var qcReport = LatestOf(artifacts, ArtifactType.QcReport);

        var video = await EntryAsync(tenantId, projectId, videoAsset?.ArtifactId, fallbackState, completeness, missing, cancellationToken).ConfigureAwait(false);
        var audio = audioAsset is not null
            ? await EntryAsync(tenantId, projectId, audioAsset.ArtifactId, fallbackState, completeness, missing, cancellationToken).ConfigureAwait(false)
            : await EntryAsync(tenantId, projectId, mixed?.Id, fallbackState, completeness, missing, cancellationToken).ConfigureAwait(false);

        var subtitles = new List<OutputAssetEntryDto>(2);
        if (translation is not null || fallbackState is "ready" or "partial")
        {
            subtitles.Add(await EntryAsync(tenantId, projectId, translation?.Id, fallbackState, completeness, missing, cancellationToken).ConfigureAwait(false));
            subtitles.Add(await EntryAsync(tenantId, projectId, translation?.Id, fallbackState, completeness, missing, cancellationToken).ConfigureAwait(false));
        }

        return new OutputItemsDto(
            video,
            audio,
            subtitles,
            await EntryAsync(tenantId, projectId, transcript?.Id, fallbackState, completeness, missing, cancellationToken).ConfigureAwait(false),
            await EntryAsync(tenantId, projectId, translation?.Id, fallbackState, completeness, missing, cancellationToken).ConfigureAwait(false),
            await EntryAsync(tenantId, projectId, timeline?.Id, fallbackState, completeness, missing, cancellationToken).ConfigureAwait(false),
            SpeakersEntry(speakerCount, fallbackState, completeness, missing),
            await QcEntryAsync(tenantId, projectId, qcReport?.Id, quality, fallbackState, missing, cancellationToken).ConfigureAwait(false));
    }

    private static Artifact? LatestOf(List<Artifact> artifacts, ArtifactType type)
    {
        return artifacts.FirstOrDefault(a => a.Type == type);
    }

    private static OutputAssetEntryDto SpeakersEntry(
        int speakerCount,
        string fallbackState,
        OutputCompletenessDto completeness,
        IReadOnlyList<string> missing)
    {
        if (speakerCount > 0 && (fallbackState is "ready" or "partial"))
        {
            var state = fallbackState is "ready" ? "ready" : "partial";
            return new OutputAssetEntryDto(
                state, state, null,
                state == "ready" ? [] : missing,
                state == "ready" ? null : completeness);
        }

        if (fallbackState is "generating")
        {
            return new OutputAssetEntryDto("generating", "generating", null, missing, completeness);
        }

        if (fallbackState is "failed")
        {
            return new OutputAssetEntryDto("failed", "failed", null, missing, completeness);
        }

        return new OutputAssetEntryDto("unavailable", "unavailable", null, missing, completeness);
    }

    private async Task<OutputAssetEntryDto> EntryAsync(
        Guid tenantId,
        Guid projectId,
        Guid? artifactId,
        string fallbackState,
        OutputCompletenessDto completeness,
        IReadOnlyList<string> missing,
        CancellationToken cancellationToken)
    {
        if (artifactId is null || artifactId.Value == Guid.Empty)
        {
            var state = fallbackState switch
            {
                "ready" => "unavailable",
                "partial" => "partial",
                "generating" => "generating",
                "failed" => "failed",
                _ => "unavailable",
            };
            return new OutputAssetEntryDto(state, state, null, missing, state is "partial" or "generating" ? completeness : null);
        }

        // Ready only when the artifact is downloadable; any storage or
        // ownership failure degrades to the fallback state (never a throw,
        // never an internal path in the payload).
        try
        {
            var url = await _artifacts.GetDownloadUrlAsync(
                tenantId, projectId, artifactId.Value, SignedUrlPolicy.DefaultExpiry, cancellationToken).ConfigureAwait(false);
            if (fallbackState is "ready")
            {
                return new OutputAssetEntryDto("ready", "ready", url, [], null);
            }

            if (fallbackState is "partial")
            {
                return new OutputAssetEntryDto("partial", "partial", url, missing, completeness);
            }

            return new OutputAssetEntryDto(fallbackState, fallbackState, null, missing, completeness);
        }
#pragma warning disable CA1031 // URL issuance is best effort in the aggregate; degrade, never fail the call.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new OutputAssetEntryDto(fallbackState, fallbackState, null, missing, completeness);
        }
    }

    private async Task<OutputQcDto> QcEntryAsync(
        Guid tenantId,
        Guid projectId,
        Guid? reportArtifactId,
        List<QualityResult> quality,
        string fallbackState,
        IReadOnlyList<string> missing,
        CancellationToken cancellationToken)
    {
        var summary = quality.Count == 0
            ? "No quality findings."
            : string.Concat(
                quality.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                " finding(s), ",
                quality.Count(q => q.Status == QualityStatus.Blocked).ToString(System.Globalization.CultureInfo.InvariantCulture),
                " blocked.");

        string? issuesUrl = null;
        if (reportArtifactId.HasValue && reportArtifactId.Value != Guid.Empty)
        {
            try
            {
                issuesUrl = await _artifacts.GetDownloadUrlAsync(
                    tenantId, projectId, reportArtifactId.Value, SignedUrlPolicy.DefaultExpiry, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best effort; missing URL never fails the aggregate.
            catch (Exception)
#pragma warning restore CA1031
            {
                issuesUrl = null;
            }
        }

        var state = fallbackState switch
        {
            "ready" => "ready",
            "partial" => "partial",
            "generating" => "generating",
            "failed" => "failed",
            _ => "unavailable",
        };
        return new OutputQcDto(state, state, summary, issuesUrl, missing);
    }
}
