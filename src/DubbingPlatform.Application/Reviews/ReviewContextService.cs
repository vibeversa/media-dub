using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Reviews;

/// <summary>
/// Single-handler review context aggregate (Task 011). All reads are
/// tenant-scoped and <c>AsNoTracking</c>; the ownership load runs under the
/// maintenance scope and any miss or tenant mismatch yields 404
/// <c>NOT_FOUND</c> (existence is never leaked cross-tenant). The call performs
/// a bounded batch of queries (currently 13 logical reads) — no per-row N+1:
/// the review has at most one segment, one selection, and one voice pointer.
/// Audio carries artifact IDs only; signed URLs are minted by the Task 012
/// output path at serve time. Only ids, versions, counts, and hashes are
/// logged — never transcript text, reasons, or subject identity.
/// </summary>
public sealed class ReviewContextService
{
    /// <summary>Maximum transcript/translation rows per list (oldest first).</summary>
    public const int MaxVersionsPerList = 50;

    /// <summary>Maximum decision rows in history (oldest first).</summary>
    public const int MaxHistoryEntries = 50;

    private readonly IStageExecutionContextFactory _contextFactory;

    public ReviewContextService(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Whether JWT roles grant <c>review.resolve</c> (Reviewer policy set).
    /// Pure; mirrors <see cref="AuthPolicies.RequireReviewer"/>.
    /// </summary>
    public static bool CanResolve(IEnumerable<string> jwtRoles)
    {
        ArgumentNullException.ThrowIfNull(jwtRoles);
        var held = new HashSet<string>(
            jwtRoles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()),
            StringComparer.Ordinal);
        return AuthPolicies.AllowedRoles(AuthPolicies.RequireReviewer).Any(held.Contains);
    }

    /// <summary>
    /// Whether JWT roles grant <c>project.edit</c> (Editor policy set). Pure.
    /// </summary>
    public static bool CanEdit(IEnumerable<string> jwtRoles)
    {
        ArgumentNullException.ThrowIfNull(jwtRoles);
        var held = new HashSet<string>(
            jwtRoles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()),
            StringComparer.Ordinal);
        return AuthPolicies.AllowedRoles(AuthPolicies.RequireProjectEditor).Any(held.Contains);
    }

    /// <summary>
    /// Allowed mutation names for a status plus resolve capability. Pure.
    /// Open + resolve yields resolve/dismiss/resolve-with-edit; terminal +
    /// resolve yields reopen; otherwise empty.
    /// </summary>
    public static IReadOnlyList<string> AllowedActionsFor(ReviewStatus status, bool canResolve)
    {
        if (!canResolve)
        {
            return [];
        }

        return status == ReviewStatus.Open
            ? ["resolve", "dismiss", "resolve-with-edit"]
            : ["reopen"];
    }

    /// <summary>
    /// Ranks a free-form severity string to <c>high|medium|low</c>. Pure.
    /// </summary>
    public static string RankSeverity(string? severity)
    {
        var normalized = (severity ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("high", StringComparison.Ordinal)
            || normalized.Contains("block", StringComparison.Ordinal)
            || normalized.Contains("crit", StringComparison.Ordinal)
            || normalized.Contains("error", StringComparison.Ordinal))
        {
            return "high";
        }

        if (normalized.Contains("low", StringComparison.Ordinal)
            || normalized.Contains("info", StringComparison.Ordinal))
        {
            return "low";
        }

        return "medium";
    }

    /// <summary>
    /// Builds the review context in one batched call.
    /// </summary>
    public async Task<ReviewContextResponse> GetAsync(
        Guid tenantId,
        Guid reviewId,
        IReadOnlyList<string> jwtRoles,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (reviewId == Guid.Empty)
        {
            throw new DomainException("ReviewId must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(jwtRoles);

        var review = await LoadOwnedReviewAsync(tenantId, reviewId, cancellationToken).ConfigureAwait(false);

        DubbingProject project;
        ProcessingRun run;
        List<ReviewDecision> decisions;
        SpeechSegment? segment;
        List<TranscriptVersion> transcripts;
        List<TranslationVersion> translations;
        SegmentSelection? selection;
        List<QualityResult> qualities;
        SyncResult? sync;
        SpeakerVoiceAssignment? assignment;
        VoiceProfile? voiceProfile;
        bool hasCoveringConsent;
        VoicePreviewJob? preview;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == review.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException($"Project '{review.ProjectId}' was not found.");

            run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == review.ProcessingRunId, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException($"Run '{review.ProcessingRunId}' was not found.");

            decisions = await db.Set<ReviewDecision>()
                .AsNoTracking()
                .Where(d => d.ReviewItemId == review.Id)
                .OrderBy(d => d.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            segment = review.SegmentId.HasValue
                ? await db.Set<SpeechSegment>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == review.SegmentId.Value, cancellationToken).ConfigureAwait(false)
                : null;

            if (segment is null)
            {
                transcripts = [];
                translations = [];
                selection = null;
                sync = null;
            }
            else
            {
                transcripts = await db.Set<TranscriptVersion>()
                    .AsNoTracking()
                    .Where(v => v.SegmentId == segment.Id)
                    .OrderBy(v => v.CreatedAt)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                translations = await db.Set<TranslationVersion>()
                    .AsNoTracking()
                    .Where(v => v.SegmentId == segment.Id)
                    .OrderBy(v => v.CreatedAt)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                selection = await db.Set<SegmentSelection>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.SegmentId == segment.Id, cancellationToken).ConfigureAwait(false);

                sync = await db.Set<SyncResult>()
                    .AsNoTracking()
                    .Where(y => y.SegmentId == segment.Id)
                    .OrderByDescending(y => y.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }

            qualities = segment is null
                ? await db.Set<QualityResult>()
                    .AsNoTracking()
                    .Where(q => q.ProcessingRunId == review.ProcessingRunId && q.SegmentId == null)
                    .OrderBy(q => q.CreatedAt)
                    .ToListAsync(cancellationToken).ConfigureAwait(false)
                : await db.Set<QualityResult>()
                    .AsNoTracking()
                    .Where(q => q.SegmentId == segment.Id)
                    .OrderBy(q => q.CreatedAt)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

            assignment = segment?.SpeakerId.HasValue == true
                ? await db.Set<SpeakerVoiceAssignment>()
                    .AsNoTracking()
                    .Where(a => a.ProjectId == review.ProjectId && a.SpeakerId == segment.SpeakerId.Value)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                : null;

            voiceProfile = assignment is null
                ? null
                : await db.Set<VoiceProfile>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == assignment.VoiceProfileId, cancellationToken).ConfigureAwait(false);

            hasCoveringConsent = voiceProfile is not null && RequiresConsent(voiceProfile)
                ? await db.Set<ConsentRecord>()
                    .AsNoTracking()
                    .Where(c => c.Status == ConsentStatus.Granted && c.RevokedAt == null)
                    .OrderByDescending(c => c.GrantedAt)
                    .ToListAsync(cancellationToken).ConfigureAwait(false)
                    is List<ConsentRecord> records && HasCover(records, review.ProjectId, voiceProfile.Id)
                : false;

            preview = segment?.SpeakerId.HasValue == true
                ? await db.Set<VoicePreviewJob>()
                    .AsNoTracking()
                    .Where(j => j.ProjectId == review.ProjectId && j.SpeakerId == segment.SpeakerId.Value)
                    .OrderByDescending(j => j.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }

        return ToResponse(review, project, run, decisions, segment, transcripts, translations, selection, qualities, sync, assignment, voiceProfile, hasCoveringConsent, preview, jwtRoles);
    }

    private static bool RequiresConsent(VoiceProfile voice)
    {
        return voice.Type == VoiceType.Cloned || voice.CloningEnabled;
    }

    private static bool HasCover(List<ConsentRecord> records, Guid projectId, Guid voiceProfileId)
    {
        foreach (var record in records)
        {
            if (!ConsentService.ScopeCoversProject(record.Scope, projectId))
            {
                continue;
            }

            if (record.VoiceProfileId.HasValue
                && record.VoiceProfileId.Value != Guid.Empty
                && record.VoiceProfileId.Value != voiceProfileId)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static ReviewContextResponse ToResponse(
        ReviewItem review,
        DubbingProject project,
        ProcessingRun run,
        List<ReviewDecision> decisions,
        SpeechSegment? segment,
        List<TranscriptVersion> transcripts,
        List<TranslationVersion> translations,
        SegmentSelection? selection,
        List<QualityResult> qualities,
        SyncResult? sync,
        SpeakerVoiceAssignment? assignment,
        VoiceProfile? voiceProfile,
        bool hasCoveringConsent,
        VoicePreviewJob? preview,
        IReadOnlyList<string> jwtRoles)
    {
        var version = decisions.Count;
        var severity = qualities.Count == 0
            ? ((review.Reason ?? string.Empty).Contains("BLOCKED", StringComparison.OrdinalIgnoreCase) ? "high" : "medium")
            : qualities.Select(q => RankSeverity(q.Severity)).OrderBy(RankOrder).First();

        var canResolve = CanResolve(jwtRoles);
        var canEdit = CanEdit(jwtRoles);

        var transcriptCapped = transcripts.Count > MaxVersionsPerList;
        var translationCapped = translations.Count > MaxVersionsPerList;
        var historyCapped = decisions.Count > MaxHistoryEntries;

        var transcriptRows = transcripts
            .Take(MaxVersionsPerList)
            .Select(v => new ReviewContextTranscriptRowDto(
                v.Id.ToString("D"), v.Provider, v.Model, v.Text, v.IsSelected, v.CreatedAt))
            .ToList();
        var translationRows = translations
            .Take(MaxVersionsPerList)
            .Select(v => new ReviewContextTranslationRowDto(
                v.Id.ToString("D"), v.PrimaryText, v.Provider, v.Model, v.IsSelected, v.CreatedAt))
            .ToList();

        var history = decisions
            .Take(MaxHistoryEntries)
            .Select(d => new ReviewContextHistoryDto(
                d.Id.ToString("D"), d.Type.ToString(), d.Reviewer, d.Reason, d.CreatedAt))
            .ToList();

        var consentState = voiceProfile is null
            ? "not_applicable"
            : !RequiresConsent(voiceProfile)
                ? "not_required"
                : hasCoveringConsent ? "granted" : "required";

        var evidence = qualities
            .Where(q => q.ArtifactId.HasValue && q.ArtifactId.Value != Guid.Empty)
            .Select(q => q.ArtifactId!.Value.ToString("D"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return new ReviewContextResponse(
            new ReviewContextItemDto(
                PublicIdMapper.ToPublic(review.Id, PublicIdMapper.ReviewItemPrefix),
                review.ScopeType.ToString(), severity, review.Status.ToString(), version),
            new ReviewContextProjectDto(
                PublicIdMapper.ToPublic(project.Id, PublicIdMapper.DubbingProjectPrefix), project.Name),
            new ReviewContextRunDto(
                PublicIdMapper.ToPublic(run.Id, PublicIdMapper.ProcessingRunPrefix),
                run.Status.ToString(), run.ConfigurationHash),
            segment is null
                ? null
                : new ReviewContextSegmentDto(
                    PublicIdMapper.ToPublic(segment.Id, PublicIdMapper.SpeechSegmentPrefix),
                    segment.StartMs, segment.EndMs,
                    segment.SpeakerId.HasValue
                        ? PublicIdMapper.ToPublic(segment.SpeakerId.Value, PublicIdMapper.SpeakerPrefix)
                        : null),
            new ReviewContextVersionsDto(
                transcriptRows, translationRows,
                selection?.SelectedTranscriptVersionId?.ToString("D"),
                selection?.SelectedTranslationVersionId?.ToString("D"),
                selection?.SelectionVersion ?? 0,
                transcriptCapped || translationCapped),
            new ReviewContextVoiceDto(
                segment?.SpeakerId.HasValue == true
                    ? PublicIdMapper.ToPublic(segment.SpeakerId.Value, PublicIdMapper.SpeakerPrefix)
                    : null,
                voiceProfile is null ? null : PublicIdMapper.ToPublic(voiceProfile.Id, PublicIdMapper.VoiceProfilePrefix),
                voiceProfile?.VoiceId,
                consentState),
            new ReviewContextAudioDto(
                preview?.ArtifactId?.ToString("D"),
                null),
            new ReviewContextSyncDto(
                sync is null ? null : sync.ActualDurationMs - sync.TargetWindowMs,
                sync is not null && sync.Status != SyncStatus.SyncAcceptable),
            new ReviewContextQcDto(
                qualities.Select(q => new ReviewContextQcIssueDto(
                    q.Id.ToString("D"), q.Code, q.Severity, q.Message,
                    q.ArtifactId?.ToString("D"))).ToList(),
                evidence),
            new ReviewContextActionsDto(AllowedActionsFor(review.Status, canResolve)),
            new ReviewContextPermissionsDto(canResolve, canEdit),
            history,
            historyCapped || transcriptCapped || translationCapped);
    }

    private static int RankOrder(string rank)
    {
        return rank switch
        {
            "high" => 0,
            "medium" => 1,
            _ => 2,
        };
    }

    private async Task<ReviewItem> LoadOwnedReviewAsync(
        Guid tenantId,
        Guid reviewId,
        CancellationToken cancellationToken)
    {
        ReviewItem? item;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            item = await db.Set<ReviewItem>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken).ConfigureAwait(false);
        }

        if (item is null || item.TenantId != tenantId)
        {
            throw new NotFoundException($"Review '{reviewId}' was not found.");
        }

        return item;
    }
}
