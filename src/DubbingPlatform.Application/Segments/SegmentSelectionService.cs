using System.Text.Json;
using System.Text.RegularExpressions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Segments;

/// <summary>
/// Outcome of one successful selection mutation: the new counter, the current
/// selected version pointers, whether final output for the run already exists
/// (<see cref="OutputStale"/> with <see cref="WarningCode"/> set to
/// <c>OUTPUT_STALE</c>), and the new version id for manual edits (null for
/// plain selects). Only ids, versions, and flags are returned — never edited
/// text.
/// </summary>
public sealed record SegmentSelectionResult(
    Guid SegmentId,
    int SelectionVersion,
    Guid? SelectedTranscriptVersionId,
    Guid? SelectedTranslationVersionId,
    bool OutputStale,
    string? WarningCode,
    Guid? NewVersionId);

/// <summary>
/// Stale <c>expectedSelectionVersion</c> conflict. The public error code stays
/// the frozen <c>CONFLICT</c> (HTTP 409); <c>SELECTION_CONFLICT</c> travels in
/// the message so Task 009 endpoints can map it to their 409 refresh payload.
/// Carries the current counter and selected version ids so callers can refresh
/// without a second read.
/// </summary>
public sealed class SegmentSelectionConflictException : AppException
{
    public SegmentSelectionConflictException(
        int currentSelectionVersion,
        Guid? currentTranscriptVersionId,
        Guid? currentTranslationVersionId)
        : base(
            ErrorCodes.Conflict,
            string.Concat(
                "SELECTION_CONFLICT: expected selection version did not match the current version ",
                currentSelectionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ". Refresh and retry."))
    {
        CurrentSelectionVersion = currentSelectionVersion;
        CurrentTranscriptVersionId = currentTranscriptVersionId;
        CurrentTranslationVersionId = currentTranslationVersionId;
    }

    public int CurrentSelectionVersion { get; }

    public Guid? CurrentTranscriptVersionId { get; }

    public Guid? CurrentTranslationVersionId { get; }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Publishes <see cref="SegmentSelectionChanged"/> invalidation events.
/// Implemented in Infrastructure over MassTransit; tests substitute a fake.
/// Application must not reference MassTransit directly.
/// </summary>
public interface ISegmentSelectionEventPublisher
{
    Task PublishAsync(SegmentSelectionChanged message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit segment selection with expected-version concurrency control.
/// Transcript/translation content versions are immutable: selects flip the
/// <c>IsSelected</c> pointer and manual edits insert a new version row (never
/// an in-place content update). The <c>SelectionVersion</c> counter bumps
/// atomically via <c>UPDATE ... WHERE SelectionVersion = @expected</c> so
/// concurrent writers serialize to exactly one winner; stale writers get
/// <see cref="SegmentSelectionConflictException"/> (409) and never overwrite.
/// Every change records actor + sanitized reason + timestamp in
/// <see cref="AuditEvent"/> (<c>segment.selection_changed</c>) and publishes
/// <see cref="SegmentSelectionChanged"/> for dependent voice/timing
/// invalidation (no recompute here). When final output already exists for the
/// run, the result carries <c>OutputStale = true</c> plus the
/// <c>OUTPUT_STALE</c> warning and the staleness is recorded in the audit
/// entry. Mutations require tenant ownership plus project membership or
/// project ownership (defense in depth; Task 009 endpoints re-check roles).
/// Only ids, versions, counts, and hashes are logged — never edited text.
/// </summary>
public sealed class SegmentSelectionService
{
    /// <summary>Sub-code carried in conflict messages; the public code stays CONFLICT.</summary>
    public const string SelectionConflictMarker = "SELECTION_CONFLICT";

    /// <summary>Sub-code carried in missing-version messages; the public code stays NOT_FOUND.</summary>
    public const string VersionNotFoundMarker = "VERSION_NOT_FOUND";

    /// <summary>Sub-code carried in cross-segment messages; the public code stays VALIDATION_FAILED.</summary>
    public const string VersionSegmentMismatchMarker = "VERSION_SEGMENT_MISMATCH";

    /// <summary>Warning code returned (not thrown) when final output already exists.</summary>
    public const string OutputStaleWarningCode = "OUTPUT_STALE";

    /// <summary>Audit action appended for every selection change.</summary>
    public const string AuditAction = "segment.selection_changed";

    /// <summary>Maximum stored reason length; longer reasons are truncated.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Maximum manual edit text length; longer input is rejected.</summary>
    public const int MaxManualTextLength = 5000;

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]*>",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ISegmentSelectionEventPublisher _publisher;

    public SegmentSelectionService(
        IStageExecutionContextFactory contextFactory,
        ISegmentSelectionEventPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(publisher);
        _contextFactory = contextFactory;
        _publisher = publisher;
    }

    /// <summary>
    /// Sanitizes free-text reasons: strips HTML tags, trims, truncates to
    /// <see cref="MaxReasonLength"/>. Null/whitespace yields null. Pure.
    /// </summary>
    public static string? SanitizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var stripped = HtmlTagRegex.Replace(reason, string.Empty).Trim();
        if (stripped.Length == 0)
        {
            return null;
        }

        return stripped.Length > MaxReasonLength
            ? stripped.Substring(0, MaxReasonLength)
            : stripped;
    }

    /// <summary>
    /// Validates manual edit text (non-empty, within length). Pure; throws
    /// <see cref="DomainException"/> (400 VALIDATION_FAILED) on empty or
    /// over-long input.
    /// </summary>
    public static string RequireManualText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DomainException("Manual edit requires non-empty text.");
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaxManualTextLength)
        {
            throw new DomainException(string.Concat(
                "Manual edit text must not exceed ",
                MaxManualTextLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                " characters."));
        }

        return trimmed;
    }

    /// <summary>
    /// Builds the <see cref="SegmentSelectionChanged"/> invalidation message.
    /// Pure. Carries ids, versions, and flags only — never text or secrets.
    /// </summary>
    public static SegmentSelectionChanged BuildMessage(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        int selectionVersion,
        Guid? transcriptVersionId,
        Guid? translationVersionId,
        string? reason,
        Guid updatedByUserId,
        bool outputStale)
    {
        return new SegmentSelectionChanged(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            tenantId,
            projectId,
            runId,
            null,
            null,
            "Segment",
            segmentId.ToString("D"),
            segmentId,
            MessageVersionPolicy.CurrentVersion,
            DateTimeOffset.UtcNow,
            0,
            null,
            null,
            null,
            selectionVersion,
            transcriptVersionId,
            translationVersionId,
            reason,
            updatedByUserId,
            outputStale);
    }

    /// <summary>
    /// Reads the current selection for a segment (null when never selected).
    /// Project-scoped read; endpoint role checks live in Task 009.
    /// </summary>
    public async Task<SegmentSelection?> GetAsync(
        Guid tenantId,
        Guid projectId,
        Guid segmentId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(segmentId, nameof(segmentId));
        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<SegmentSelection>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.SegmentId == segmentId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task<SegmentSelectionResult> SelectTranscriptAsync(
        Guid tenantId,
        Guid projectId,
        Guid segmentId,
        Guid transcriptVersionId,
        int expectedSelectionVersion,
        Guid actorUserId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(new MutationSpec(
            tenantId, projectId, segmentId, expectedSelectionVersion,
            actorUserId, reason, MutationKind.SelectTranscript,
            transcriptVersionId, null), cancellationToken);
    }

    public Task<SegmentSelectionResult> SelectTranslationAsync(
        Guid tenantId,
        Guid projectId,
        Guid segmentId,
        Guid translationVersionId,
        int expectedSelectionVersion,
        Guid actorUserId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(new MutationSpec(
            tenantId, projectId, segmentId, expectedSelectionVersion,
            actorUserId, reason, MutationKind.SelectTranslation,
            null, translationVersionId), cancellationToken);
    }

    public Task<SegmentSelectionResult> CreateManualTranscriptVersionAsync(
        Guid tenantId,
        Guid projectId,
        Guid segmentId,
        int expectedSelectionVersion,
        string text,
        Guid actorUserId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var cleaned = RequireManualText(text);
        return MutateAsync(new MutationSpec(
            tenantId, projectId, segmentId, expectedSelectionVersion,
            actorUserId, reason, MutationKind.ManualTranscript,
            null, null, cleaned), cancellationToken);
    }

    public Task<SegmentSelectionResult> CreateManualTranslationVersionAsync(
        Guid tenantId,
        Guid projectId,
        Guid segmentId,
        int expectedSelectionVersion,
        string text,
        Guid actorUserId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var cleaned = RequireManualText(text);
        return MutateAsync(new MutationSpec(
            tenantId, projectId, segmentId, expectedSelectionVersion,
            actorUserId, reason, MutationKind.ManualTranslation,
            null, null, cleaned), cancellationToken);
    }

    private enum MutationKind
    {
        SelectTranscript,
        SelectTranslation,
        ManualTranscript,
        ManualTranslation,
    }

    private sealed record MutationSpec(
        Guid TenantId,
        Guid ProjectId,
        Guid SegmentId,
        int ExpectedSelectionVersion,
        Guid ActorUserId,
        string? Reason,
        MutationKind Kind,
        Guid? TranscriptVersionId,
        Guid? TranslationVersionId,
        string? ManualText = null);

    private async Task<SegmentSelectionResult> MutateAsync(
        MutationSpec spec,
        CancellationToken cancellationToken)
    {
        RequireTenant(spec.TenantId);
        RequireId(spec.ProjectId, nameof(spec.ProjectId));
        RequireId(spec.SegmentId, nameof(spec.SegmentId));
        RequireId(spec.ActorUserId, nameof(spec.ActorUserId));
        if (spec.ExpectedSelectionVersion < 0)
        {
            throw new DomainException("ExpectedSelectionVersion must be >= 0.");
        }

        if (spec.Kind is MutationKind.SelectTranscript)
        {
            RequireId(spec.TranscriptVersionId!.Value, nameof(spec.TranscriptVersionId));
        }

        if (spec.Kind is MutationKind.SelectTranslation)
        {
            RequireId(spec.TranslationVersionId!.Value, nameof(spec.TranslationVersionId));
        }

        var project = await RequireProjectAsync(spec.TenantId, spec.ProjectId, cancellationToken).ConfigureAwait(false);
        var segment = await LoadOwnedSegmentAsync(spec.TenantId, spec.ProjectId, spec.SegmentId, cancellationToken).ConfigureAwait(false);
        await RequireMembershipAsync(spec.TenantId, spec.ProjectId, spec.ActorUserId, project, cancellationToken).ConfigureAwait(false);

        var reason = SanitizeReason(spec.Reason);
        var now = DateTimeOffset.UtcNow;

        int newVersion;
        Guid? currentTranscriptId;
        Guid? currentTranslationId;
        Guid? newVersionId;
        bool outputStale;

        using (TenantContext.BeginScope(spec.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = await db.Set<SegmentSelection>()
                    .FirstOrDefaultAsync(s => s.SegmentId == spec.SegmentId, cancellationToken)
                    .ConfigureAwait(false);

                Guid? resultingTranscriptId;
                Guid? resultingTranslationId;
                if (existing is null)
                {
                    if (spec.ExpectedSelectionVersion != 0)
                    {
                        throw new SegmentSelectionConflictException(0, null, null);
                    }

                    (resultingTranscriptId, resultingTranslationId, newVersionId) =
                        await ApplyContentChangeAsync(db, spec, project, segment, null, null, now, cancellationToken)
                            .ConfigureAwait(false);

                    var created = new SegmentSelection(
                        Guid.NewGuid(), spec.TenantId, spec.ProjectId, spec.SegmentId,
                        resultingTranscriptId, resultingTranslationId, null, 1, now, spec.ActorUserId);
                    db.Set<SegmentSelection>().Add(created);
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (DomainException ex) when (ex.Message.Contains("CONFLICT", StringComparison.Ordinal))
                    {
                        throw await ReadCurrentConflictAsync(spec, cancellationToken).ConfigureAwait(false);
                    }

                    newVersion = 1;
                }
                else
                {
                    if (existing.SelectionVersion != spec.ExpectedSelectionVersion)
                    {
                        throw new SegmentSelectionConflictException(
                            existing.SelectionVersion,
                            existing.SelectedTranscriptVersionId,
                            existing.SelectedTranslationVersionId);
                    }

                    (resultingTranscriptId, resultingTranslationId, newVersionId) =
                        await ApplyContentChangeAsync(
                            db, spec, project, segment,
                            existing.SelectedTranscriptVersionId,
                            existing.SelectedTranslationVersionId,
                            now, cancellationToken)
                            .ConfigureAwait(false);

                    var bumped = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE segment_selections SET selected_transcript_version_id = {0}, selected_translation_version_id = {1}, selection_version = {2}, updated_at = {3}, updated_by_user_id = {4} " +
                        "WHERE id = {5} AND tenant_id = {6} AND selection_version = {7}",
                        (object?)resultingTranscriptId ?? DBNull.Value, (object?)resultingTranslationId ?? DBNull.Value,
                        spec.ExpectedSelectionVersion + 1,
                        now,
                        spec.ActorUserId,
                        existing.Id,
                        spec.TenantId,
                        spec.ExpectedSelectionVersion).ConfigureAwait(false);
                    if (bumped == 0)
                    {
                        throw await ReadCurrentConflictAsync(spec, cancellationToken).ConfigureAwait(false);
                    }

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    newVersion = spec.ExpectedSelectionVersion + 1;
                }

                currentTranscriptId = resultingTranscriptId;
                currentTranslationId = resultingTranslationId;

                outputStale = await db.Set<OutputAsset>()
                    .AsNoTracking()
                    .AnyAsync(o => o.ProcessingRunId == segment.RunId, cancellationToken)
                    .ConfigureAwait(false);

                var details = SecretRedactor.Redact(JsonSerializer.Serialize(new
                {
                    selectionVersion = newVersion,
                    transcriptVersionId = currentTranscriptId.HasValue ? currentTranscriptId.Value.ToString("N") : null,
                    translationVersionId = currentTranslationId.HasValue ? currentTranslationId.Value.ToString("N") : null,
                    newVersionId = newVersionId.HasValue ? newVersionId.Value.ToString("N") : null,
                    reason,
                    outputStale,
                }, JsonOptions));

                db.Set<AuditEvent>().Add(new AuditEvent(
                    Guid.NewGuid(), spec.TenantId, spec.ProjectId,
                    spec.ActorUserId.ToString("D"), AuditAction,
                    "SegmentSelection", spec.SegmentId.ToString("N"),
                    details, now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await db.Database.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Rollback best effort; original error propagates.
                }

                throw;
            }
        }

        var result = new SegmentSelectionResult(
            spec.SegmentId, newVersion, currentTranscriptId, currentTranslationId,
            outputStale, outputStale ? OutputStaleWarningCode : null, newVersionId);

        var message = BuildMessage(
            spec.TenantId, spec.ProjectId, segment.RunId, spec.SegmentId,
            result.SelectionVersion, result.SelectedTranscriptVersionId,
            result.SelectedTranslationVersionId, reason,
            spec.ActorUserId, result.OutputStale);
        await _publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<(Guid? TranscriptId, Guid? TranslationId, Guid? NewVersionId)> ApplyContentChangeAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        MutationSpec spec,
        DubbingProject project,
        SpeechSegment segment,
        Guid? currentTranscriptId,
        Guid? currentTranslationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (spec.Kind)
        {
            case MutationKind.SelectTranscript:
            {
                var version = await LoadTranscriptVersionAsync(spec, spec.TranscriptVersionId!.Value, segment, cancellationToken)
                    .ConfigureAwait(false);
                _ = version;
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE transcript_versions SET is_selected = FALSE WHERE run_id = {0} AND segment_id = {1}",
                    segment.RunId, segment.Id).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE transcript_versions SET is_selected = TRUE WHERE id = {0} AND tenant_id = {1}",
                    spec.TranscriptVersionId!.Value, spec.TenantId).ConfigureAwait(false);
                return (spec.TranscriptVersionId, currentTranslationId, null);
            }

            case MutationKind.SelectTranslation:
            {
                var version = await LoadTranslationVersionAsync(spec, spec.TranslationVersionId!.Value, segment, cancellationToken)
                    .ConfigureAwait(false);
                _ = version;
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE translation_versions SET is_selected = FALSE WHERE run_id = {0} AND segment_id = {1}",
                    segment.RunId, segment.Id).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE translation_versions SET is_selected = TRUE WHERE id = {0} AND tenant_id = {1}",
                    spec.TranslationVersionId!.Value, spec.TenantId).ConfigureAwait(false);
                return (currentTranscriptId, spec.TranslationVersionId, null);
            }

            case MutationKind.ManualTranscript:
            {
                var versionId = Guid.NewGuid();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE transcript_versions SET is_selected = FALSE WHERE run_id = {0} AND segment_id = {1}",
                    segment.RunId, segment.Id).ConfigureAwait(false);
                db.Set<TranscriptVersion>().Add(new TranscriptVersion(
                    versionId, spec.TenantId, spec.ProjectId, segment.RunId, segment.Id,
                    ReviewService.ManualProvider, ReviewService.ManualModel,
                    project.SourceLanguage, spec.ManualText!, 1.0, null, true, false, now));
                return (versionId, currentTranslationId, versionId);
            }

            case MutationKind.ManualTranslation:
            {
                var versionId = Guid.NewGuid();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE translation_versions SET is_selected = FALSE WHERE run_id = {0} AND segment_id = {1}",
                    segment.RunId, segment.Id).ConfigureAwait(false);
                db.Set<TranslationVersion>().Add(new TranslationVersion(
                    versionId, spec.TenantId, spec.ProjectId, segment.RunId, segment.Id,
                    spec.ManualText!, [], 1.0, 1.0, 1.0,
                    ReviewService.ManualProvider, ReviewService.ManualModel,
                    null, null, true, now));
                return (currentTranscriptId, versionId, versionId);
            }

            default:
                throw new DomainException($"Unknown selection mutation '{spec.Kind}'.");
        }
    }

    private async Task<SegmentSelectionConflictException> ReadCurrentConflictAsync(
        MutationSpec spec,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(spec.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var current = await db.Set<SegmentSelection>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SegmentId == spec.SegmentId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                return new SegmentSelectionConflictException(0, null, null);
            }

            return new SegmentSelectionConflictException(
                current.SelectionVersion,
                current.SelectedTranscriptVersionId,
                current.SelectedTranslationVersionId);
        }
    }

    private async Task<TranscriptVersion> LoadTranscriptVersionAsync(
        MutationSpec spec,
        Guid versionId,
        SpeechSegment segment,
        CancellationToken cancellationToken)
    {
        TranscriptVersion? version;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            version = await db.Set<TranscriptVersion>()
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (version is null)
        {
            throw new NotFoundException(string.Concat(
                VersionNotFoundMarker, ": transcript version '", versionId.ToString("D"), "' was not found."));
        }

        if (version.TenantId != spec.TenantId
            || version.ProjectId != spec.ProjectId
            || version.SegmentId != segment.Id
            || version.RunId != segment.RunId)
        {
            throw new DomainException(string.Concat(
                VersionSegmentMismatchMarker, ": transcript version '", versionId.ToString("D"), "' does not belong to this segment."));
        }

        return version;
    }

    private async Task<TranslationVersion> LoadTranslationVersionAsync(
        MutationSpec spec,
        Guid versionId,
        SpeechSegment segment,
        CancellationToken cancellationToken)
    {
        TranslationVersion? version;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            version = await db.Set<TranslationVersion>()
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (version is null)
        {
            throw new NotFoundException(string.Concat(
                VersionNotFoundMarker, ": translation version '", versionId.ToString("D"), "' was not found."));
        }

        if (version.TenantId != spec.TenantId
            || version.ProjectId != spec.ProjectId
            || version.SegmentId != segment.Id
            || version.RunId != segment.RunId)
        {
            throw new DomainException(string.Concat(
                VersionSegmentMismatchMarker, ": translation version '", versionId.ToString("D"), "' does not belong to this segment."));
        }

        return version;
    }

    private async Task<SpeechSegment> LoadOwnedSegmentAsync(
        Guid tenantId,
        Guid projectId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        SpeechSegment? segment;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            segment = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == segmentId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (segment is null)
        {
            throw new NotFoundException(string.Concat("Segment '", segmentId.ToString("D"), "' was not found."));
        }

        if (segment.TenantId != tenantId || segment.ProjectId != projectId)
        {
            throw new ForbiddenException(string.Concat("Segment '", segmentId.ToString("D"), "' does not belong to the current tenant/project."));
        }

        return segment;
    }

    private async Task RequireMembershipAsync(
        Guid tenantId,
        Guid projectId,
        Guid actorUserId,
        DubbingProject project,
        CancellationToken cancellationToken)
    {
        if (project.OwnerUserId.HasValue && project.OwnerUserId.Value == actorUserId)
        {
            return;
        }

        bool isMember;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            isMember = await db.Set<ProjectMembership>()
                .AsNoTracking()
                .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorUserId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!isMember)
        {
            throw new ForbiddenException(string.Concat("User '", actorUserId.ToString("D"), "' is not a member of project '", projectId.ToString("D"), "'."));
        }
    }

    private async Task<DubbingProject> RequireProjectAsync(
        Guid tenantId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
                .ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException(string.Concat("Project '", projectId.ToString("D"), "' was not found."));
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException(string.Concat("Project '", projectId.ToString("D"), "' does not belong to the current tenant."));
        }

        if (isDeleted)
        {
            throw new NotFoundException(string.Concat("Project '", projectId.ToString("D"), "' was not found."));
        }

        return project;
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
            throw new DomainException(string.Concat(name, " must not be empty."));
        }
    }
}
