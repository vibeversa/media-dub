namespace DubbingPlatform.Application.Exports;

/// <summary>
/// Immutable snapshot rows for on-demand exports. All collections are
/// pre-ordered deterministically by <see cref="ExportService"/> (segments by
/// sequence, speakers by key, QC entries by scope/code) so every generator is
/// a pure function of this record. No secrets, no media bytes: text and
/// counts only.
/// </summary>
public sealed record ExportSegment(
    int Sequence,
    Guid SegmentId,
    int StartMs,
    int EndMs,
    string Status,
    string? SpeakerKey,
    string? SpeakerDisplayName,
    string? TranscriptText,
    string? TranslationText);

/// <summary>
/// One speaker row for speaker-metadata exports, ordered by
/// <c>SpeakerKey</c> (ordinal) for stability.
/// </summary>
public sealed record ExportSpeaker(
    string SpeakerKey,
    string DisplayName,
    int FirstMs,
    int LastMs,
    string? VoiceProvider,
    string? VoiceId,
    string? VoiceVersion,
    string? VoiceType,
    string? AssignmentReason);

/// <summary>
/// One QC/result row for quality-report exports, ordered by
/// <c>(ScopeType, ScopeId, Code)</c> ordinal.
/// </summary>
public sealed record ExportQcEntry(
    string ScopeType,
    string ScopeId,
    int? SegmentSequence,
    string Code,
    string Severity,
    string Status,
    string Message);

/// <summary>
/// Machine-readable completeness block embedded in every JSON export and
/// persisted as <c>ExportJob.CompletenessJson</c>. Counts are segment counts:
/// <c>Completed</c> = segments with both selected transcript and translation,
/// <c>Failed</c> = segments missing either, <c>Skipped</c> = segments with
/// status <c>Skipped</c> (case-insensitive). <c>IsComplete</c> is true only for
/// a <c>Completed</c> run with zero failed, zero skipped, and zero open
/// reviews.
/// </summary>
public sealed record ExportCompleteness(
    Guid ProjectId,
    Guid RunId,
    string SourceHash,
    int Completed,
    int Failed,
    int Skipped,
    int ReviewCount,
    bool IsComplete,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Full snapshot for one export job.
/// </summary>
public sealed record ExportRunData(
    Guid ProjectId,
    Guid RunId,
    string SourceHash,
    bool IsCompleteRun,
    IReadOnlyList<ExportSegment> Segments,
    IReadOnlyList<ExportSpeaker> Speakers,
    IReadOnlyList<ExportQcEntry> QualityEntries,
    ExportCompleteness Completeness);
