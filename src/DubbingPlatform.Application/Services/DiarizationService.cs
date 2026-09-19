using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Provider label aligned to one of our segments. <see cref="SegmentId"/> is
/// the <c>SpeechSegment.Id</c>; <see cref="Label"/> is the opaque provider
/// speaker tag (for example <c>spk_0</c>); <see cref="Confidence"/> is the
/// provider confidence for that assignment.
/// </summary>
public sealed record DiarLabel(Guid SegmentId, string Label, double Confidence);

/// <summary>
/// Planned speaker mapping for one distinct provider label.
/// </summary>
public sealed record PlannedSpeaker(
    string SpeakerKey,
    Guid SpeakerId,
    string DisplayName,
    string ProviderLabel,
    double Confidence,
    int FirstAppearanceMs,
    int LastAppearanceMs,
    string MappingMethod,
    string MappingVersion,
    bool IsNew);

/// <summary>
/// Result of diarization mapping. <see cref="SegmentToSpeaker"/> maps
/// <c>SpeechSegment.Id</c> to <c>Speaker.Id</c>.
/// </summary>
public sealed record DiarizationMapResult(
    IReadOnlyList<PlannedSpeaker> Speakers,
    IReadOnlyDictionary<Guid, Guid> SegmentToSpeaker,
    bool IsFallback,
    string? FallbackReason);

/// <summary>
/// Maps provider diarization labels to stable project-scoped speakers.
/// Same provider label always resolves to the same <c>Speaker</c> row
/// (key <c>{project:N}:{label}</c>, deterministic id); distinct labels yield
/// distinct rows. Speakers are never deleted on rerun: a conflicting label
/// for a segment simply reassigns the segment while old speaker rows remain
/// (history is queried by <c>RunId</c> via segments). First/last appearance
/// expand monotonically across runs; confidence is the current-run average.
/// </summary>
public sealed class DiarizationService
{
    /// <summary>
    /// QC and stage-note code for the single-speaker fallback. Queryable via
    /// <c>quality_results.code</c> and <c>stage_executions.error_code</c>.
    /// </summary>
    public const string FallbackCode = "DIARIZATION_FALLBACK";

    /// <summary>Speaker key for the single-speaker fallback.</summary>
    public const string FallbackSpeakerKey = "single";

    /// <summary>Display name for the single-speaker fallback.</summary>
    public const string FallbackDisplayName = "Single Speaker";

    /// <summary>Mapping version for all diarization mappings.</summary>
    public const string MappingVersion = "1";

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ILogger<DiarizationService> _logger;

    public DiarizationService(
        IStageExecutionContextFactory contextFactory,
        ILogger<DiarizationService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Normalizes a provider label (trims; rejects empty).
    /// </summary>
    public static string NormalizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new DomainException("Diarization label must not be empty.");
        }

        return label.Trim();
    }

    /// <summary>
    /// Builds the stable speaker key for a project label. The key is
    /// <c>{project:N}:{label}</c>; labels that would exceed the 128-char
    /// column are replaced by their SHA-256 hex (still prefixed) so the key
    /// always fits while staying deterministic.
    /// </summary>
    public static string BuildSpeakerKey(Guid projectId, string label)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        var normalized = NormalizeLabel(label);
        var key = string.Concat(projectId.ToString("N"), ":", normalized);
        if (key.Length <= 128)
        {
            return key;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Concat(projectId.ToString("N"), ":", hex);
    }

    /// <summary>
    /// Deterministic speaker id for a speaker key within a project. Uses
    /// <see cref="GuidUtility"/> so same-attempt redelivery yields identical
    /// ids. Never passes secrets (project ids and labels only).
    /// </summary>
    public static Guid SpeakerIdFor(Guid projectId, string speakerKey)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(speakerKey))
        {
            throw new DomainException("SpeakerKey must not be empty.");
        }

        var key = speakerKey.Trim();
        string namespaced = key.Contains(':')
            ? string.Concat("speaker:", key)
            : string.Concat("speaker:", projectId.ToString("N"), ":", key);
        return GuidUtility.From(namespaced);
    }

    /// <summary>
    /// Builds the mapping method provenance tag for a provider.
    /// </summary>
    public static string BuildMappingMethod(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new DomainException("Provider must not be empty.");
        }

        return string.Concat("provider:", provider.Trim());
    }

    /// <summary>
    /// Pure deterministic planning with no I/O (no existing speakers).
    /// Distinct labels sorted ordinally map to <c>Speaker 1..N</c>. Used by
    /// hermetic unit tests and by <see cref="MapAsync"/> for new labels.
    /// </summary>
    public static DiarizationMapResult Plan(
        Guid projectId,
        IReadOnlyList<SpeechSegment> segments,
        IReadOnlyList<DiarLabel> labels,
        string provider)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(labels);

        var method = BuildMappingMethod(provider);
        var bySegment = IndexSegments(segments);
        var labelBySegment = LastWinsBySegment(labels, bySegment);
        var groups = GroupByLabel(segments, labelBySegment);
        var orderedLabels = groups.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        var speakers = new List<PlannedSpeaker>(orderedLabels.Count);
        var segmentToSpeaker = new Dictionary<Guid, Guid>(labelBySegment.Count);
        for (var i = 0; i < orderedLabels.Count; i++)
        {
            var label = orderedLabels[i];
            var memberSegments = groups[label];
            var first = memberSegments.Min(s => s.StartMs);
            var last = memberSegments.Max(s => s.EndMs);
            var confidence = Clamp01(AverageConfidence(labelBySegment, memberSegments, label));
            var speakerKey = BuildSpeakerKey(projectId, label);
            var speakerId = SpeakerIdFor(projectId, speakerKey);
            var displayName = string.Concat("Speaker ", (i + 1).ToString(CultureInfo.InvariantCulture));
            speakers.Add(new PlannedSpeaker(
                speakerKey, speakerId, displayName, label, confidence,
                first, last, method, MappingVersion, true));

            foreach (var segment in memberSegments)
            {
                segmentToSpeaker[segment.Id] = speakerId;
            }
        }

        return new DiarizationMapResult(speakers, segmentToSpeaker, false, null);
    }

    /// <summary>
    /// Pure single-speaker fallback plan (no I/O). All segments map to the
    /// <c>single</c> speaker.
    /// </summary>
    public static DiarizationMapResult PlanFallback(
        Guid projectId,
        IReadOnlyList<SpeechSegment> segments,
        string reason)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(segments);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("FallbackReason must not be empty.");
        }

        if (segments.Count == 0)
        {
            return new DiarizationMapResult([], new Dictionary<Guid, Guid>(), true, reason.Trim());
        }

        var first = segments.Min(s => s.StartMs);
        var last = segments.Max(s => s.EndMs);
        var speakerId = SpeakerIdFor(projectId, FallbackSpeakerKey);
        var speaker = new PlannedSpeaker(
            FallbackSpeakerKey, speakerId, FallbackDisplayName, FallbackSpeakerKey,
            1.0, first, last, "provider:fallback", MappingVersion, true);
        var map = segments.ToDictionary(s => s.Id, _ => speakerId);
        return new DiarizationMapResult([speaker], map, true, reason.Trim());
    }

    /// <summary>
    /// Persists label mappings: creates/finds project speakers (never
    /// deletes) and assigns <c>SpeechSegment.SpeakerId</c> for the run in one
    /// transaction. Reuses existing speakers by key (expanding first/last
    /// appearance, refreshing confidence/method, keeping the display name and
    /// id for history); new labels get the next <c>Speaker N</c> numbers after
    /// the existing project count.
    /// </summary>
    public async Task<DiarizationMapResult> MapAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        IReadOnlyList<SpeechSegment> segments,
        IReadOnlyList<DiarLabel> labels,
        string provider,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(labels);

        var method = BuildMappingMethod(provider);
        var bySegment = IndexSegments(segments);
        var labelBySegment = LastWinsBySegment(labels, bySegment);
        var groups = GroupByLabel(segments, labelBySegment);
        var orderedLabels = groups.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = await db.Set<Speaker>()
                    .Where(s => s.ProjectId == projectId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var existingByKey = existing.ToDictionary(s => s.SpeakerKey, s => s, StringComparer.Ordinal);
                var existingCount = existing.Count;

                var planned = new List<PlannedSpeaker>(orderedLabels.Count);
                var segmentToSpeaker = new Dictionary<Guid, Guid>(labelBySegment.Count);
                var newPosition = 0;
                var now = DateTimeOffset.UtcNow;

                foreach (var label in orderedLabels)
                {
                    var memberSegments = groups[label];
                    var first = memberSegments.Min(s => s.StartMs);
                    var last = memberSegments.Max(s => s.EndMs);
                    var confidence = Clamp01(AverageConfidence(labelBySegment, memberSegments, label));
                    var speakerKey = BuildSpeakerKey(projectId, label);
                    var providerLabel = TruncateProviderLabel(label);

                    if (existingByKey.TryGetValue(speakerKey, out var current))
                    {
                        var expandedFirst = Math.Min(current.FirstAppearanceMs, first);
                        var expandedLast = Math.Max(current.LastAppearanceMs, last);
                        current.UpdateMapping(expandedFirst, expandedLast, method, confidence, providerLabel);
                        planned.Add(new PlannedSpeaker(
                            speakerKey, current.Id, current.DisplayName, providerLabel,
                            confidence, expandedFirst, expandedLast, method, MappingVersion, false));
                        foreach (var segment in memberSegments)
                        {
                            segmentToSpeaker[segment.Id] = current.Id;
                        }
                    }
                    else
                    {
                        newPosition++;
                        var displayName = string.Concat(
                            "Speaker ",
                            (existingCount + newPosition).ToString(CultureInfo.InvariantCulture));
                        var speakerId = SpeakerIdFor(projectId, speakerKey);
                        db.Set<Speaker>().Add(new Speaker(
                            speakerId, tenantId, projectId, speakerKey, displayName,
                            first, last, method, MappingVersion, confidence,
                            providerLabel, now));
                        planned.Add(new PlannedSpeaker(
                            speakerKey, speakerId, displayName, providerLabel,
                            confidence, first, last, method, MappingVersion, true));
                        foreach (var segment in memberSegments)
                        {
                            segmentToSpeaker[segment.Id] = speakerId;
                        }
                    }
                }

                if (segmentToSpeaker.Count > 0)
                {
                    var ids = segmentToSpeaker.Keys.ToList();
                    var tracked = await db.Set<SpeechSegment>()
                        .Where(s => s.RunId == runId && ids.Contains(s.Id))
                        .ToListAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var segment in tracked)
                    {
                        segment.AssignSpeaker(segmentToSpeaker[segment.Id]);
                    }
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Diarization mapped {Segments} segments to {Speakers} speakers for run {RunId}.",
                    segmentToSpeaker.Count, planned.Count, runId);

                return new DiarizationMapResult(planned, segmentToSpeaker, false, null);
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

    /// <summary>
    /// Persists the single-speaker fallback: finds or creates the
    /// <c>single</c> speaker and assigns every run segment to it in one
    /// transaction. Never deletes speakers.
    /// </summary>
    public async Task<DiarizationMapResult> MapSingleSpeakerFallbackAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        IReadOnlyList<SpeechSegment> segments,
        string reason,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        ArgumentNullException.ThrowIfNull(segments);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("FallbackReason must not be empty.");
        }

        var trimmedReason = reason.Trim();
        if (segments.Count == 0)
        {
            return new DiarizationMapResult([], new Dictionary<Guid, Guid>(), true, trimmedReason);
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var first = segments.Min(s => s.StartMs);
                var last = segments.Max(s => s.EndMs);
                var now = DateTimeOffset.UtcNow;

                var current = await db.Set<Speaker>()
                    .FirstOrDefaultAsync(
                        s => s.ProjectId == projectId && s.SpeakerKey == FallbackSpeakerKey,
                        cancellationToken).ConfigureAwait(false);

                Guid speakerId;
                string displayName;
                if (current is null)
                {
                    speakerId = SpeakerIdFor(projectId, FallbackSpeakerKey);
                    displayName = FallbackDisplayName;
                    db.Set<Speaker>().Add(new Speaker(
                        speakerId, tenantId, projectId, FallbackSpeakerKey, displayName,
                        first, last, "provider:fallback", MappingVersion, 1.0,
                        FallbackSpeakerKey, now));
                }
                else
                {
                    speakerId = current.Id;
                    displayName = current.DisplayName;
                    current.UpdateMapping(
                        Math.Min(current.FirstAppearanceMs, first),
                        Math.Max(current.LastAppearanceMs, last),
                        "provider:fallback", 1.0, FallbackSpeakerKey);
                }

                var ids = segments.Select(s => s.Id).ToList();
                var tracked = await db.Set<SpeechSegment>()
                    .Where(s => s.RunId == runId && ids.Contains(s.Id))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var segment in tracked)
                {
                    segment.AssignSpeaker(speakerId);
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Diarization fallback to single speaker for run {RunId}: {Reason}.",
                    runId, trimmedReason);

                var planned = new PlannedSpeaker(
                    FallbackSpeakerKey, speakerId, displayName, FallbackSpeakerKey,
                    1.0, first, last, "provider:fallback", MappingVersion, current is null);
                var map = segments.ToDictionary(s => s.Id, _ => speakerId);
                return new DiarizationMapResult([planned], map, true, trimmedReason);
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

    private static Dictionary<Guid, SpeechSegment> IndexSegments(IReadOnlyList<SpeechSegment> segments)
    {
        var bySegment = new Dictionary<Guid, SpeechSegment>(segments.Count);
        foreach (var segment in segments)
        {
            ArgumentNullException.ThrowIfNull(segment);
            if (segment.Id == Guid.Empty)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    "Speech segment id must not be empty.");
            }

            bySegment[segment.Id] = segment;
        }

        return bySegment;
    }

    private static Dictionary<Guid, DiarLabel> LastWinsBySegment(
        IReadOnlyList<DiarLabel> labels,
        IReadOnlyDictionary<Guid, SpeechSegment> bySegment)
    {
        var result = new Dictionary<Guid, DiarLabel>();
        foreach (var label in labels)
        {
            ArgumentNullException.ThrowIfNull(label);
            if (label.SegmentId == Guid.Empty)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    "Diarization label segment id must not be empty.");
            }

            if (!bySegment.ContainsKey(label.SegmentId))
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    "Diarization label references an unknown segment.");
            }

            var normalized = NormalizeLabel(label.Label);
            result[label.SegmentId] = new DiarLabel(label.SegmentId, normalized, label.Confidence);
        }

        return result;
    }

    private static Dictionary<string, List<SpeechSegment>> GroupByLabel(
        IReadOnlyList<SpeechSegment> segments,
        IReadOnlyDictionary<Guid, DiarLabel> labelBySegment)
    {
        var groups = new Dictionary<string, List<SpeechSegment>>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (!labelBySegment.TryGetValue(segment.Id, out var diar))
            {
                continue;
            }

            if (!groups.TryGetValue(diar.Label, out var members))
            {
                members = [];
                groups[diar.Label] = members;
            }

            members.Add(segment);
        }

        return groups;
    }

    private static double AverageConfidence(
        IReadOnlyDictionary<Guid, DiarLabel> labelBySegment,
        IReadOnlyList<SpeechSegment> members,
        string label)
    {
        double sum = 0;
        var count = 0;
        foreach (var segment in members)
        {
            if (labelBySegment.TryGetValue(segment.Id, out var diar)
                && string.Equals(diar.Label, label, StringComparison.Ordinal))
            {
                sum += Clamp01(diar.Confidence);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0.0, 1.0);
    }

    private static string TruncateProviderLabel(string label)
    {
        return label.Length <= 256 ? label : label.Substring(0, 256);
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
