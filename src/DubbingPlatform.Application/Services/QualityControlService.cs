using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// One QC check outcome. <see cref="Status"/> is the <see cref="QualityStatus"/>
/// classification, <see cref="Severity"/> one of <c>Info</c>/<c>Warning</c>/
/// <c>Error</c>/<c>Blocking</c>. <see cref="DetailsJson"/> carries ids, counts,
/// durations, and levels only (never transcript/translation text, audio, or
/// secrets).
/// </summary>
public sealed record QcFinding(
    string Code,
    ScopeType ScopeType,
    string ScopeId,
    Guid? SegmentId,
    QualityStatus Status,
    string Severity,
    string Message,
    string? DetailsJson,
    Guid? ArtifactId);

/// <summary>
/// Outcome of one QC run. <see cref="Allowed"/> is true only for
/// <c>Pass</c>/<c>PassWithWarnings</c> (render proceeds); blocked, review, and
/// retry verdicts all block the render. <see cref="ReviewItemId"/> is set when
/// a blocked/manual verdict created the single project-level review.
/// </summary>
public sealed record QcOutcome(
    QualityStatus Verdict,
    IReadOnlyList<QcFinding> Findings,
    Guid ReportArtifactId,
    Guid? ReviewItemId,
    bool Allowed,
    int BlockedCount,
    int ReviewCount,
    int RetryCount,
    int WarningCount);

/// <summary>
/// Segment, project, and signal quality control. Runs in order: per-segment
/// checks (selected transcript non-empty, translation non-empty, voice
/// assigned, generated final audio present with matching content, sync result
/// acceptable or review resolved, no open segment review), project checks
/// (segments present and complete vs expected units and timeline entries,
/// provider metadata fresh for the run attempt, no open project review,
/// overlap validity from <c>SegmentOverlap</c> membership, overflow vs source,
/// gaps, voice 1:1 stability, terminology when strict), then signal assertions
/// on the staged mixed file (decodable, 48kHz stereo, duration drift,
/// loudness ±1 LU, peak ≤ -1 dBTP, no clipping, channel routing, timeline
/// placement ±50ms, recorded duck attenuation within ±3 dB of configured,
/// long-silence and silence-boundary warnings, crossfade audibility).
/// Every failure or warning persists one <see cref="QualityResult"/> row; a
/// <c>QcReport</c> JSON artifact (schema v1) is always published with
/// parents <c>[mixed + timeline]</c>; blocked/manual verdicts create one
/// project-level <see cref="ReviewItem"/>. Re-runs append new rows and a new
/// immutable report (history, like the timeline stage). Stage commits and
/// saga events stay worker-owned. Never logs text, audio, or secrets: only
/// ids, counts, durations, and levels.
/// </summary>
public sealed class QualityControlService
{
    /// <summary>Report artifact/row schema version.</summary>
    public const string SchemaVersion = "1";

    /// <summary>Review reason for blocking QC findings.</summary>
    public const string ReviewReasonBlocked = "QC_BLOCKED";

    /// <summary>Review reason for manual-review QC findings.</summary>
    public const string ReviewReasonReview = "QC_REVIEW";

    public const string SeverityInfo = "Info";
    public const string SeverityWarning = "Warning";
    public const string SeverityError = "Error";
    public const string SeverityBlocking = "Blocking";

    public const string CodeMissingTranscript = "QC_MISSING_TRANSCRIPT";
    public const string CodeEmptyTranslation = "QC_EMPTY_TRANSLATION";
    public const string CodeMissingVoice = "QC_MISSING_VOICE";
    public const string CodeMissingAudio = "QC_MISSING_AUDIO";
    public const string CodeChecksumMismatch = ErrorCodes.ArtifactChecksumMismatch;
    public const string CodeSyncFailure = "QC_SYNC_FAILURE";
    public const string CodeStaleMetadata = "QC_STALE_METADATA";
    public const string CodeUnresolvedReview = "QC_UNRESOLVED_REVIEW";
    public const string CodeNoSegments = "QC_NO_SEGMENTS";
    public const string CodeMissingSegment = "QC_MISSING_SEGMENT";
    public const string CodeInvalidOverlap = "QC_INVALID_OVERLAP";
    public const string CodeOverflow = "QC_OVERFLOW";
    public const string CodeGap = "QC_GAP";
    public const string CodeDrift = "QC_DRIFT";
    public const string CodeClipping = "QC_CLIPPING";
    public const string CodePeak = "QC_PEAK";
    public const string CodeSilence = "QC_SILENCE";
    public const string CodeCorrupt = "QC_CORRUPT";
    public const string CodeRateMismatch = "QC_RATE_MISMATCH";
    public const string CodeChannelMismatch = "QC_CHANNEL_MISMATCH";
    public const string CodeVoiceInstability = "QC_VOICE_INSTABILITY";
    public const string CodeTerminology = "QC_TERMINOLOGY";
    public const string CodeRouting = "QC_SIGNAL_ROUTING";
    public const string CodePlacement = "QC_PLACEMENT";
    public const string CodeAttenuation = "QC_ATTENUATION";
    public const string CodeLoudness = "QC_LOUDNESS";
    public const string CodeSilenceBoundary = "QC_SILENCE_BOUNDARY";
    public const string CodeCrossfade = "QC_CROSSFADE";
    public const string CodeMissingTimeline = "QC_MISSING_TIMELINE";

    /// <summary>Unexpected gap threshold (warning).</summary>
    public const int GapWarningMs = 5000;

    /// <summary>Mixed-vs-source duration drift tolerance (warning).</summary>
    public const int DriftWarningMs = 500;

    /// <summary>Long-silence warning threshold (seconds at -60 dB).</summary>
    public const double SilenceWarningSec = 10.0;

    /// <summary>Silence detector threshold.</summary>
    public const double SilenceThresholdDb = -60.0;

    /// <summary>Timeline placement tolerance (each entry start within mixed + tolerance).</summary>
    public const int PlacementToleranceMs = 50;

    /// <summary>Overflow tolerance past source duration (mirrors timeline assembly).</summary>
    public const int OverflowToleranceMs = 100;

    /// <summary>Integrated loudness tolerance (±LU around target).</summary>
    public const double LoudnessToleranceLu = 1.0;

    /// <summary>True-peak ceiling (dBTP).</summary>
    public const double TruePeakLimitDbtp = -1.0;

    /// <summary>Duck attenuation tolerance (±dB around configured).</summary>
    public const double AttenuationToleranceDb = 3.0;

    /// <summary>Channel routing silence floor (a channel at or below is dead).</summary>
    public const double RoutingSilenceDb = -70.0;

    /// <summary>Gap silence expectation without background (a louder gap leaks).</summary>
    public const double BoundarySilenceDb = -50.0;

    /// <summary>Overlap audibility floor (a quieter overlap dropped audio).</summary>
    public const double CrossfadeAudibleDb = -60.0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly IArtifactStorage _storage;
    private readonly IFFprobeService _ffprobe;
    private readonly IFFmpegService _ffmpeg;
    private readonly QcOptions _qc;
    private readonly MixingOptions _mixing;
    private readonly ILogger<QualityControlService> _logger;

    public QualityControlService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        IArtifactStorage storage,
        IFFprobeService ffprobe,
        IFFmpegService ffmpeg,
        IOptions<QcOptions> qc,
        IOptions<MixingOptions> mixing,
        ILogger<QualityControlService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(qc);
        ArgumentNullException.ThrowIfNull(mixing);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _storage = storage;
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _qc = qc.Value;
        _mixing = mixing.Value;
        _logger = logger;
    }

    /// <summary>
    /// Classifies the overall verdict from findings. Pure. Any
    /// <c>Blocked</c> blocks; else any <c>ManualReviewRequired</c> reviews;
    /// else any <c>RetryRequired</c> retries; else any
    /// <c>PassWithWarnings</c> warns; else <c>Pass</c>.
    /// </summary>
    public static QualityStatus Classify(IReadOnlyList<QcFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var verdict = QualityStatus.Pass;
        foreach (var finding in findings)
        {
            ArgumentNullException.ThrowIfNull(finding);
            verdict = finding.Status switch
            {
                QualityStatus.Blocked => QualityStatus.Blocked,
                QualityStatus.ManualReviewRequired when verdict != QualityStatus.Blocked => QualityStatus.ManualReviewRequired,
                QualityStatus.RetryRequired when verdict is QualityStatus.Pass or QualityStatus.PassWithWarnings => QualityStatus.RetryRequired,
                QualityStatus.PassWithWarnings when verdict == QualityStatus.Pass => QualityStatus.PassWithWarnings,
                _ => verdict,
            };

            if (verdict == QualityStatus.Blocked)
            {
                return verdict;
            }
        }

        return verdict;
    }

    /// <summary>
    /// Parses <c>settings.terminologyStrict</c> (bool). Pure; returns null when
    /// absent or invalid (fail-closed to the <c>Qc:TerminologyStrict</c>
    /// default, mirroring context settings parsing: settings never fail a run).
    /// </summary>
    public static bool? ParseTerminologyStrict(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "terminologyStrict", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses <c>settings.loudnessProfile</c> (web|broadcast). Pure;
    /// fail-closed to <c>web</c>, mirroring the mixer contract.
    /// </summary>
    public static string ParseLoudnessProfile(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return "web";
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return "web";
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "loudnessProfile", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && string.Equals((property.Value.GetString() ?? string.Empty).Trim(), "broadcast", StringComparison.OrdinalIgnoreCase))
                {
                    return "broadcast";
                }
            }

            return "web";
        }
        catch (JsonException)
        {
            return "web";
        }
    }

    /// <summary>
    /// Whether settings JSON explicitly carries <c>loudnessProfile</c>. Pure.
    /// </summary>
    public static bool HasLoudnessProfile(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "loudnessProfile", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a profile name to its loudness target. Pure; unknown profiles
    /// fail closed to web (settings parsing already defaults; the mixer hard
    /// gate stays at mix time).
    /// </summary>
    public static LoudnessTarget ResolveTarget(string? profile)
    {
        return string.Equals((profile ?? string.Empty).Trim(), "broadcast", StringComparison.OrdinalIgnoreCase)
            ? LoudnessTarget.Broadcast
            : LoudnessTarget.WebDefault;
    }

    /// <summary>
    /// Parses the recorded duck attenuation from a mixer <c>filterComplex</c>
    /// string (<c>volume={x}dB</c>). Pure; returns null when absent/unparseable.
    /// </summary>
    public static double? ParseDuckDb(string? filterComplex)
    {
        if (string.IsNullOrWhiteSpace(filterComplex))
        {
            return null;
        }

        const string marker = "volume=";
        var found = filterComplex.IndexOf(marker, StringComparison.Ordinal);
        if (found < 0)
        {
            return null;
        }

        var rest = filterComplex.Substring(found + marker.Length);
        var end = rest.IndexOf("dB", StringComparison.Ordinal);
        if (end <= 0)
        {
            return null;
        }

        var token = rest.Substring(0, end).Trim();
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && !double.IsNaN(value) && !double.IsInfinity(value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Runs all QC checks for one run, persists one <see cref="QualityResult"/>
    /// per failure/warning, publishes the <c>QcReport</c> artifact, and creates
    /// the single project-level review for blocked/manual verdicts.
    /// </summary>
    public async Task<QcOutcome> RunAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var run = await EnsureRunUsableAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);
        var inputs = await LoadInputsAsync(tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);

        var findings = new List<QcFinding>();
        var checksumTargets = new List<ChecksumTarget>();

        RunSegmentChecks(projectId, inputs, findings, checksumTargets);
        RunProjectChecks(project, run, inputs, findings);

        await RunSignalChecksAsync(project, inputs, findings, checksumTargets, cancellationToken).ConfigureAwait(false);

        var verdict = Classify(findings);
        var reportId = await PersistAsync(tenantId, projectId, runId, verdict, findings, inputs, cancellationToken).ConfigureAwait(false);
        Guid? reviewId = null;
        if (verdict is QualityStatus.Blocked or QualityStatus.ManualReviewRequired)
        {
            reviewId = await CreateReviewAsync(tenantId, projectId, runId, verdict, findings, cancellationToken).ConfigureAwait(false);
        }

        var blocked = findings.Count(f => f.Status == QualityStatus.Blocked);
        var manual = findings.Count(f => f.Status == QualityStatus.ManualReviewRequired);
        var retry = findings.Count(f => f.Status == QualityStatus.RetryRequired);
        var warnings = findings.Count(f => f.Status == QualityStatus.PassWithWarnings);
        var allowed = verdict is QualityStatus.Pass or QualityStatus.PassWithWarnings;

        _logger.LogInformation(
            "QC for run {RunId}: verdict {Verdict} ({Findings} findings: {Blocked} blocked, {Manual} review, {Retry} retry, {Warnings} warnings; report {ReportId}).",
            runId, verdict, findings.Count, blocked, manual, retry, warnings, reportId);

        return new QcOutcome(verdict, findings, reportId, reviewId, allowed, blocked, manual, retry, warnings);
    }

    private sealed record ChecksumTarget(Guid SegmentId, int Sequence, Guid? AudioArtifactId, Guid ContentObjectId, string StorageKey, string ExpectedHash);

    private sealed record QcInputs(
        IReadOnlyList<SpeechSegment> Segments,
        IReadOnlyList<SegmentOverlap> Overlaps,
        IReadOnlyList<OverlapGroup> Groups,
        IReadOnlyList<RunStageSummary> Summaries,
        IReadOnlyList<ReviewItem> Reviews,
        IReadOnlyList<TranscriptVersion> Transcripts,
        IReadOnlyList<TranslationVersion> Translations,
        IReadOnlyList<SpeakerVoiceAssignment> Assignments,
        IReadOnlyList<GeneratedAudioArtifact> Finals,
        IReadOnlyList<Artifact> AudioArtifacts,
        IReadOnlyList<ContentObject> Contents,
        IReadOnlyList<SyncResult> Syncs,
        IReadOnlyList<ProviderExecution> Executions,
        IReadOnlyList<MediaAsset> MediaAssets,
        Guid? TimelineArtifactId,
        string? TimelineJson,
        string? TimelineStorageKey,
        Guid? MixedArtifactId,
        Guid? MixedContentObjectId,
        string? MixedStorageKey,
        string? MixedMetadataJson);

    private sealed record TimelineEntryLite(string SegmentId, int Sequence, int StartMs, int DurationMs, string AudioArtifactId);

    private sealed record TimelineLite(int SourceDurationMs, IReadOnlyList<TimelineEntryLite> Entries, string? BackgroundArtifactId);

    private void RunSegmentChecks(Guid projectId, QcInputs inputs, List<QcFinding> findings, List<ChecksumTarget> checksumTargets)
    {
        var active = ActiveSegments(inputs.Segments);
        if (active.Count == 0)
        {
            findings.Add(Blocked(
                CodeNoSegments, ScopeType.Project, projectId.ToString("D"), null,
                "Run has no active segments; render is blocked until segments exist.",
                new { segmentCount = inputs.Segments.Count }, null));
            return;
        }

        var transcripts = inputs.Transcripts
            .Where(t => t.IsSelected)
            .GroupBy(t => t.SegmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.CreatedAt).First());
        var translations = inputs.Translations
            .Where(t => t.IsSelected)
            .GroupBy(t => t.SegmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.CreatedAt).First());
        var finalsBySegment = inputs.Finals
            .GroupBy(f => f.SegmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.CreatedAt).First());
        var contents = inputs.Contents.ToDictionary(c => c.Id);
        var syncs = inputs.Syncs
            .GroupBy(s => s.SegmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CreatedAt).First());

        foreach (var segment in active)
        {
            var scopeId = segment.Id.ToString("D");

            if (!transcripts.TryGetValue(segment.Id, out var transcript)
                || string.IsNullOrWhiteSpace(transcript.Text))
            {
                findings.Add(Blocked(
                    CodeMissingTranscript, ScopeType.Segment, segment.Id.ToString("D"), segment.Id,
                    string.Concat("Segment sequence ", segment.Sequence.ToString(CultureInfo.InvariantCulture), " has no selected transcript."),
                    new { sequence = segment.Sequence }, null));
            }

            if (!translations.TryGetValue(segment.Id, out var translation)
                || string.IsNullOrWhiteSpace(translation.PrimaryText))
            {
                findings.Add(Blocked(
                    CodeEmptyTranslation, ScopeType.Segment, segment.Id.ToString("D"), segment.Id,
                    string.Concat("Segment sequence ", segment.Sequence.ToString(CultureInfo.InvariantCulture), " has no selected translation."),
                    new { sequence = segment.Sequence }, null));
            }

            if (segment.SpeakerId is null
                || !inputs.Assignments.Any(a => a.RunId == segment.RunId && a.SpeakerId == segment.SpeakerId))
            {
                findings.Add(Blocked(
                    CodeMissingVoice, ScopeType.Segment, segment.Id.ToString("D"), segment.Id,
                    string.Concat("Segment sequence ", segment.Sequence.ToString(CultureInfo.InvariantCulture), " has no assigned voice."),
                    new { sequence = segment.Sequence, hasSpeaker = segment.SpeakerId is not null }, null));
            }

            if (!finalsBySegment.TryGetValue(segment.Id, out var final)
                || !contents.TryGetValue(final.ContentObjectId, out var content))
            {
                findings.Add(Blocked(
                    CodeMissingAudio, ScopeType.Segment, segment.Id.ToString("D"), segment.Id,
                    string.Concat("Segment sequence ", segment.Sequence.ToString(CultureInfo.InvariantCulture), " has no generated final audio."),
                    new { sequence = segment.Sequence }, null));
            }
            else if (_qc.VerifyChecksums)
            {
                checksumTargets.Add(new ChecksumTarget(
                    segment.Id, segment.Sequence, AudioArtifactIdFor(final.Id, inputs), final.ContentObjectId, content.StorageKey, content.Sha256Hex));
            }

            var resolved = inputs.Reviews.Any(r =>
                r.SegmentId == segment.Id && r.Status != ReviewStatus.Open);
            if (!syncs.TryGetValue(segment.Id, out var sync)
                || (sync.Status == SyncStatus.ManualReviewRequired && !resolved))
            {
                findings.Add(new QcFinding(
                    CodeSyncFailure, ScopeType.Segment, scopeId, segment.Id,
                    QualityStatus.ManualReviewRequired, SeverityError,
                    sync is null
                        ? string.Concat("Segment sequence ", segment.Sequence.ToString(CultureInfo.InvariantCulture), " has no timing sync result; manual review required.")
                        : string.Concat("Segment sequence ", segment.Sequence.ToString(CultureInfo.InvariantCulture), " timing is out of tolerance; manual review required."),
                    JsonSerializer.Serialize(new { sequence = segment.Sequence, hasSync = sync is not null }),
                    null));
            }

            if (inputs.Reviews.Any(r => r.SegmentId == segment.Id && r.Status == ReviewStatus.Open))
            {
                findings.Add(Blocked(
                    CodeUnresolvedReview, ScopeType.Segment, segment.Id.ToString("D"), segment.Id,
                    string.Concat("Segment sequence ", segment.Sequence.ToString(CultureInfo.InvariantCulture), " has an open review; blocked until resolved."),
                    new { sequence = segment.Sequence }, null));
            }
        }

        // Terminology is a segment-grained check driven by project policy; it
        // runs in the project phase where settings strictness is resolved.
    }

    private void RunProjectChecks(
        DubbingProject project,
        ProcessingRun run,
        QcInputs inputs,
        List<QcFinding> findings)
    {
        var active = ActiveSegments(inputs.Segments);

        var fresh = inputs.Executions.Any(e => e.Attempt == run.Attempt);
        if (!fresh)
        {
            findings.Add(new QcFinding(
                CodeStaleMetadata, ScopeType.Project, project.Id.ToString("D"), null,
                QualityStatus.RetryRequired, SeverityWarning,
                string.Concat("No provider execution matches run attempt ", run.Attempt.ToString(CultureInfo.InvariantCulture), "; QC will retry after upstream attempts settle."),
                JsonSerializer.Serialize(new { runAttempt = run.Attempt, executionCount = inputs.Executions.Count }),
                null));
        }

        if (inputs.Reviews.Any(r => r.SegmentId is null && r.Status == ReviewStatus.Open))
        {
            findings.Add(Blocked(
                CodeUnresolvedReview, ScopeType.Project, project.Id.ToString("D"), null,
                "Run has an open project-level review; blocked until resolved.",
                new
                {
                    openCount = inputs.Reviews.Count(r => r.SegmentId is null && r.Status == ReviewStatus.Open),
                },
                null));
        }

        if (active.Count == 0)
        {
            return;
        }

        var timeline = TryParseTimeline(inputs.TimelineJson);
        var expected = timeline?.Entries.Count ?? 0;
        foreach (var summary in inputs.Summaries)
        {
            if (summary.StageType is StageType.SegmentBuild
                or StageType.Transcription
                or StageType.Translation
                or StageType.VoiceGeneration
                or StageType.TimingOptimization)
            {
                expected = Math.Max(expected, summary.ExpectedUnits);
            }
        }

        if (active.Count < expected)
        {
            findings.Add(Blocked(
                CodeMissingSegment, ScopeType.Project, project.Id.ToString("D"), null,
                string.Concat("Segment completeness ", active.Count.ToString(CultureInfo.InvariantCulture), " is below expected ", expected.ToString(CultureInfo.InvariantCulture), "."),
                new { actual = active.Count, expected }, null));
        }

        RunOverlapChecks(project.Id, active, inputs, findings);

        var sourceDurationMs = SourceDurationMs(inputs);
        if (sourceDurationMs > 0)
        {
            foreach (var segment in active)
            {
                if ((long)segment.EndMs > (long)sourceDurationMs + OverflowToleranceMs)
                {
                    findings.Add(Blocked(
                        CodeOverflow, ScopeType.Segment, segment.Id.ToString("D"), segment.Id,
                        string.Concat("Segment sequence ", segment.Sequence.ToString(CultureInfo.InvariantCulture), " ends past the source duration."),
                        new { sequence = segment.Sequence, endMs = segment.EndMs, sourceDurationMs }, null));
                }
            }
        }

        for (var i = 1; i < active.Count; i++)
        {
            var gap = (long)active[i].StartMs - active[i - 1].EndMs;
            if (gap > GapWarningMs)
            {
                findings.Add(new QcFinding(
                    CodeGap, ScopeType.Project, project.Id.ToString("D"), null,
                    QualityStatus.PassWithWarnings, SeverityWarning,
                    string.Concat("Silence gap of ", gap.ToString(CultureInfo.InvariantCulture), "ms between sequences ", active[i - 1].Sequence.ToString(CultureInfo.InvariantCulture), " and ", active[i].Sequence.ToString(CultureInfo.InvariantCulture), "."),
                    JsonSerializer.Serialize(new
                    {
                        previousSequence = active[i - 1].Sequence,
                        nextSequence = active[i].Sequence,
                        gapMs = gap,
                    }),
                    null));
            }
        }

        var unstable = inputs.Assignments
            .GroupBy(a => a.SpeakerId)
            .Where(g => g.Select(a => a.VoiceProfileId).Distinct().Count() > 1)
            .Select(g => g.Key.ToString("N"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (unstable.Count > 0)
        {
            findings.Add(Blocked(
                CodeVoiceInstability, ScopeType.Project, project.Id.ToString("D"), null,
                string.Concat(unstable.Count.ToString(CultureInfo.InvariantCulture), " speaker(s) map to more than one voice; speaker-voice stability violated."),
                new { speakerIds = unstable }, null));
        }

        RunTerminologyProjectChecks(project, inputs, active, findings);
    }

    private void RunTerminologyProjectChecks(
        DubbingProject project,
        QcInputs inputs,
        IReadOnlyList<SpeechSegment> active,
        List<QcFinding> findings)
    {
        var strict = ParseTerminologyStrict(project.SettingsJson) ?? _qc.TerminologyStrict;
        if (!strict)
        {
            return;
        }

        SortedDictionary<string, string> glossary = ContextBuilderService.ParseSettings(project.SettingsJson).Glossary;
        if (glossary.Count == 0)
        {
            return;
        }

        var transcripts = inputs.Transcripts
            .Where(t => t.IsSelected && !string.IsNullOrWhiteSpace(t.Text))
            .GroupBy(t => t.SegmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.CreatedAt).First());
        var translations = inputs.Translations
            .Where(t => t.IsSelected && !string.IsNullOrWhiteSpace(t.PrimaryText))
            .GroupBy(t => t.SegmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.CreatedAt).First());

        foreach (var segment in active)
        {
            if (!transcripts.TryGetValue(segment.Id, out var transcript)
                || !translations.TryGetValue(segment.Id, out var translation))
            {
                continue;
            }

            var compliant = TranslationService.IsGlossaryCompliant(transcript.Text, translation.PrimaryText, glossary);
            if (!compliant)
            {
                findings.Add(Blocked(
                    CodeTerminology, ScopeType.Segment, segment.Id.ToString("D"), segment.Id,
                    string.Concat("Segment sequence ", segment.Sequence.ToString(CultureInfo.InvariantCulture), " translation violates the configured glossary."),
                    new { sequence = segment.Sequence, glossaryTerms = glossary.Count }, null));
            }
        }
    }

    private void RunOverlapChecks(
        Guid projectId,
        IReadOnlyList<SpeechSegment> active,
        QcInputs inputs,
        List<QcFinding> findings)
    {
        var groupsBySegment = new Dictionary<Guid, SortedSet<string>>();
        var validGroups = new HashSet<Guid>(inputs.Groups.Select(g => g.Id));
        foreach (var overlap in inputs.Overlaps)
        {
            if (!validGroups.Contains(overlap.OverlapGroupId))
            {
                continue;
            }

            if (!groupsBySegment.TryGetValue(overlap.SegmentId, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                groupsBySegment[overlap.SegmentId] = set;
            }

            set.Add(overlap.OverlapGroupId.ToString("N"));
        }

        var pairs = 0;
        for (var i = 0; i < active.Count && pairs < 100; i++)
        {
            for (var j = i + 1; j < active.Count && pairs < 100; j++)
            {
                var aStart = (long)active[i].StartMs;
                var aEnd = active[i].EndMs;
                var bStart = (long)active[j].StartMs;
                var bEnd = active[j].EndMs;
                if (Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart) <= 0)
                {
                    continue;
                }

                var aGroups = groupsBySegment.TryGetValue(active[i].Id, out var aSet) ? aSet : null;
                var bGroups = groupsBySegment.TryGetValue(active[j].Id, out var bSet) ? bSet : null;
                if (aGroups is null || bGroups is null || aGroups.Count == 0 || bGroups.Count == 0 || !aGroups.Overlaps(bGroups))
                {
                    pairs++;
                    findings.Add(Blocked(
                        CodeInvalidOverlap, ScopeType.Project, projectId.ToString("D"), null,
                        string.Concat(
                            "Segments sequences ",
                            active[i].Sequence.ToString(CultureInfo.InvariantCulture),
                            " and ",
                            active[j].Sequence.ToString(CultureInfo.InvariantCulture),
                            " overlap with no shared overlap group."),
                        new
                        {
                            firstSequence = active[i].Sequence,
                            secondSequence = active[j].Sequence,
                            firstSegmentId = active[i].Id.ToString("N"),
                            secondSegmentId = active[j].Id.ToString("N"),
                        },
                        null));
                }
            }
        }
    }

    private async Task RunSignalChecksAsync(
        DubbingProject project,
        QcInputs inputs,
        List<QcFinding> findings,
        List<ChecksumTarget> checksumTargets,
        CancellationToken cancellationToken)
    {
        if (inputs.MixedArtifactId is null
            || string.IsNullOrWhiteSpace(inputs.MixedStorageKey))
        {
            findings.Add(Blocked(
                CodeMissingAudio, ScopeType.Project, project.Id.ToString("D"), null,
                "Mixed audio artifact is unavailable; render is blocked.",
                new { hasArtifact = inputs.MixedArtifactId is not null }, null));
            return;
        }

        var timeline = TryParseTimeline(inputs.TimelineJson);
        if (timeline is null)
        {
            findings.Add(Blocked(
                CodeMissingTimeline, ScopeType.Project, project.Id.ToString("D"), null,
                "Timeline artifact is unavailable or malformed; placement cannot be verified.",
                new { hasArtifact = inputs.TimelineArtifactId is not null }, null));
        }

        string workDir;
        try
        {
            workDir = CreateWorkDir();
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "QC cannot create temp dir; disk may be full.", ex);
        }

        try
        {
            var mixedFile = Path.Combine(workDir, "mixed.wav");
            await DownloadAsync(inputs.MixedStorageKey!, mixedFile, cancellationToken).ConfigureAwait(false);

            if (_qc.VerifyChecksums)
            {
                await RunChecksumChecksAsync(workDir, checksumTargets, findings, cancellationToken).ConfigureAwait(false);
            }

            FfprobeResult probe;
            try
            {
                probe = await _ffprobe.ProbeAsync(mixedFile, cancellationToken).ConfigureAwait(false);
            }
            catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
            {
                findings.Add(Blocked(
                    CodeCorrupt, ScopeType.Project, project.Id.ToString("D"), null,
                    "Mixed audio is undecodable; render is blocked.",
                    new { artifactId = inputs.MixedArtifactId.HasValue ? inputs.MixedArtifactId.Value.ToString("N") : null }, inputs.MixedArtifactId));
                return;
            }

            var audio = probe.Streams.FirstOrDefault(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
            if (audio is null)
            {
                findings.Add(Blocked(
                    CodeCorrupt, ScopeType.Project, project.Id.ToString("D"), null,
                    "Mixed audio has no audio stream; render is blocked.",
                    new { artifactId = inputs.MixedArtifactId.HasValue ? inputs.MixedArtifactId.Value.ToString("N") : null }, inputs.MixedArtifactId));
                return;
            }

            if (audio.SampleRate != 48000)
            {
                findings.Add(Blocked(
                    CodeRateMismatch, ScopeType.Project, project.Id.ToString("D"), null,
                    string.Concat("Mixed sample rate is ", audio.SampleRate?.ToString(CultureInfo.InvariantCulture) ?? "unknown", "Hz; expected 48000Hz."),
                    new { sampleRate = audio.SampleRate, expected = 48000 }, inputs.MixedArtifactId));
            }

            if (audio.Channels is null || audio.Channels != 2)
            {
                findings.Add(Blocked(
                    CodeChannelMismatch, ScopeType.Project, project.Id.ToString("D"), null,
                    string.Concat("Mixed channel count is ", audio.Channels?.ToString(CultureInfo.InvariantCulture) ?? "unknown", "; expected stereo."),
                    new { channels = audio.Channels, expected = 2 }, inputs.MixedArtifactId));
            }

            var mixedDurationMs = (int)Math.Min(Math.Max(probe.DurationMs, 0), int.MaxValue);
            var sourceDurationMs = SourceDurationMs(inputs);
            if (sourceDurationMs > 0 && Math.Abs((long)mixedDurationMs - sourceDurationMs) > DriftWarningMs)
            {
                findings.Add(new QcFinding(
                    CodeDrift, ScopeType.Project, project.Id.ToString("D"), null,
                    QualityStatus.PassWithWarnings, SeverityWarning,
                    string.Concat("Mixed duration ", mixedDurationMs.ToString(CultureInfo.InvariantCulture), "ms drifts from source ", sourceDurationMs.ToString(CultureInfo.InvariantCulture), "ms."),
                    JsonSerializer.Serialize(new { mixedDurationMs, sourceDurationMs }),
                    inputs.MixedArtifactId));
            }

            SignalLoudness? loudness = null;
            try
            {
                loudness = await _ffmpeg.MeasureSignalLoudnessAsync(mixedFile, workDir, cancellationToken).ConfigureAwait(false);
            }
            catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.ProviderInvalidResponse, StringComparison.Ordinal))
            {
                findings.Add(Blocked(
                    CodeLoudness, ScopeType.Project, project.Id.ToString("D"), null,
                    "Mixed loudness is unmeasurable; render is blocked.",
                    new { artifactId = inputs.MixedArtifactId.HasValue ? inputs.MixedArtifactId.Value.ToString("N") : null }, inputs.MixedArtifactId));
            }

            if (loudness is not null)
            {
                var target = ResolveTarget(HasLoudnessProfile(project.SettingsJson)
                    ? ParseLoudnessProfile(project.SettingsJson)
                    : _mixing.Profile);
                if (Math.Abs(loudness.IntegratedLufs - target.IntegratedLufs) > LoudnessToleranceLu)
                {
                    findings.Add(Blocked(
                        CodeLoudness, ScopeType.Project, project.Id.ToString("D"), null,
                        string.Concat(
                            "Mixed loudness ",
                            loudness.IntegratedLufs.ToString("F2", CultureInfo.InvariantCulture),
                            " LUFS differs from target ",
                            target.IntegratedLufs.ToString("F1", CultureInfo.InvariantCulture),
                            " LUFS by more than 1 LU."),
                        new
                        {
                            integratedLufs = loudness.IntegratedLufs,
                            targetLufs = target.IntegratedLufs,
                            truePeakDbtp = loudness.TruePeakDbtp,
                        },
                        inputs.MixedArtifactId));
                }

                if (loudness.TruePeakDbtp >= 0.0)
                {
                    findings.Add(Blocked(
                        CodeClipping, ScopeType.Project, project.Id.ToString("D"), null,
                        string.Concat("Mixed true-peak ", loudness.TruePeakDbtp.ToString("F2", CultureInfo.InvariantCulture), " dBTP clips; render is blocked."),
                        new { truePeakDbtp = loudness.TruePeakDbtp }, inputs.MixedArtifactId));
                }
                else if (loudness.TruePeakDbtp > TruePeakLimitDbtp)
                {
                    findings.Add(Blocked(
                        CodePeak, ScopeType.Project, project.Id.ToString("D"), null,
                        string.Concat("Mixed true-peak ", loudness.TruePeakDbtp.ToString("F2", CultureInfo.InvariantCulture), " dBTP exceeds the -1 dBTP limit."),
                        new { truePeakDbtp = loudness.TruePeakDbtp }, inputs.MixedArtifactId));
                }
            }

            await RunRoutingCheckAsync(project.Id, mixedFile, workDir, inputs, findings, cancellationToken).ConfigureAwait(false);

            if (timeline is not null)
            {
                RunPlacementChecks(project.Id, timeline, mixedDurationMs, inputs, findings);
                RunAttenuationCheck(project.Id, timeline, inputs, findings);
                await RunBoundaryChecksAsync(project.Id, mixedFile, workDir, timeline, inputs, findings, cancellationToken).ConfigureAwait(false);
                await RunCrossfadeChecksAsync(project.Id, mixedFile, workDir, inputs, findings, cancellationToken).ConfigureAwait(false);
            }

            await RunSilenceCheckAsync(project.Id, mixedFile, workDir, inputs, findings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    private async Task RunChecksumChecksAsync(
        string workDir,
        List<ChecksumTarget> targets,
        List<QcFinding> findings,
        CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var target in targets)
        {
            var local = Path.Combine(
                workDir,
                string.Concat("seg-", index.ToString(CultureInfo.InvariantCulture), "-", target.ContentObjectId.ToString("N"), ".wav"));
            index++;

            await DownloadAsync(target.StorageKey, local, cancellationToken).ConfigureAwait(false);

            string actual;
            try
            {
                using var sha = SHA256.Create();
                using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
                actual = Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch (IOException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "QC checksum staging hit disk limits.", ex);
            }

            if (!string.Equals(actual, target.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new QcFinding(
                    CodeChecksumMismatch, ScopeType.Segment, target.SegmentId.ToString("D"), target.SegmentId,
                    QualityStatus.Blocked, SeverityBlocking,
                    string.Concat("Segment sequence ", target.Sequence.ToString(CultureInfo.InvariantCulture), " audio checksum mismatch; render is blocked."),
                    JsonSerializer.Serialize(new
                    {
                        sequence = target.Sequence,
                        audioArtifactId = target.AudioArtifactId.HasValue ? target.AudioArtifactId.Value.ToString("N") : null,
                        expectedHash = target.ExpectedHash,
                        actualHash = actual,
                    }),
                    target.AudioArtifactId));
            }
        }
    }

    private async Task RunRoutingCheckAsync(
        Guid projectId,
        string mixedFile,
        string workDir,
        QcInputs inputs,
        List<QcFinding> findings,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<double> levels;
        try
        {
            levels = await _ffmpeg.MeasureChannelRmsDbAsync(mixedFile, workDir, null, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException)
        {
            // Loudness/decode gates already cover unmeasurable audio; routing
            // stays best-effort so a probe quirk never blocks alone.
            return;
        }

        if (levels.Count == 0)
        {
            return;
        }

        var dead = levels.Count(l => double.IsNegativeInfinity(l) || l <= RoutingSilenceDb);
        if (dead > 0)
        {
            findings.Add(Blocked(
                CodeRouting, ScopeType.Project, projectId.ToString("D"), null,
                string.Concat(dead.ToString(CultureInfo.InvariantCulture), " of ", levels.Count.ToString(CultureInfo.InvariantCulture), " mixed channels are silent; channel routing is broken."),
                new { channels = levels.Count, silentChannels = dead }, inputs.MixedArtifactId));
        }
    }

    private void RunPlacementChecks(
        Guid projectId,
        TimelineLite timeline,
        int mixedDurationMs,
        QcInputs inputs,
        List<QcFinding> findings)
    {
        foreach (var entry in timeline.Entries)
        {
            if ((long)entry.StartMs > (long)mixedDurationMs + PlacementToleranceMs)
            {
                findings.Add(Blocked(
                    CodePlacement, ScopeType.Project, projectId.ToString("D"), null,
                    string.Concat("Timeline entry at ", entry.StartMs.ToString(CultureInfo.InvariantCulture), "ms starts past the mixed duration ", mixedDurationMs.ToString(CultureInfo.InvariantCulture), "ms."),
                    new { segmentId = entry.SegmentId, startMs = entry.StartMs, mixedDurationMs },
                    inputs.MixedArtifactId));
            }
        }
    }

    private void RunAttenuationCheck(
        Guid projectId,
        TimelineLite timeline,
        QcInputs inputs,
        List<QcFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(timeline.BackgroundArtifactId)
            || !Guid.TryParse(timeline.BackgroundArtifactId, out var backgroundId)
            || backgroundId == Guid.Empty)
        {
            return;
        }

        var recorded = ParseDuckDb(inputs.MixedMetadataJson);
        if (!recorded.HasValue)
        {
            findings.Add(Blocked(
                CodeAttenuation, ScopeType.Project, projectId.ToString("D"), null,
                "Background duck attenuation is not recorded in the mix metadata; ducking cannot be verified.",
                new { backgroundArtifactId = timeline.BackgroundArtifactId }, inputs.MixedArtifactId));
            return;
        }

        if (Math.Abs(recorded.Value - _mixing.DuckDb) > AttenuationToleranceDb)
        {
            findings.Add(Blocked(
                CodeAttenuation, ScopeType.Project, projectId.ToString("D"), null,
                string.Concat(
                    "Recorded duck attenuation ",
                    recorded.Value.ToString("F1", CultureInfo.InvariantCulture),
                    "dB differs from configured ",
                    _mixing.DuckDb.ToString("F1", CultureInfo.InvariantCulture),
                    "dB by more than 3 dB."),
                new
                {
                    recordedDuckDb = recorded.Value,
                    configuredDuckDb = _mixing.DuckDb,
                    backgroundArtifactId = timeline.BackgroundArtifactId,
                },
                inputs.MixedArtifactId));
        }
    }

    private async Task RunBoundaryChecksAsync(
        Guid projectId,
        string mixedFile,
        string workDir,
        TimelineLite timeline,
        QcInputs inputs,
        List<QcFinding> findings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(timeline.BackgroundArtifactId))
        {
            // Background fills gaps by design; gap silence only applies to
            // dialogue-only mixes.
            return;
        }

        var checkedWindows = 0;
        foreach (var segment in ActiveGapWindows(timeline))
        {
            if (checkedWindows >= 20)
            {
                break;
            }

            checkedWindows++;
            IReadOnlyList<double> levels;
            try
            {
                levels = await _ffmpeg.MeasureChannelRmsDbAsync(
                    mixedFile, workDir, segment.StartMs, segment.DurationMs, cancellationToken).ConfigureAwait(false);
            }
            catch (ErrorCodeException)
            {
                continue;
            }

            if (levels.Count > 0 && levels.Max() > BoundarySilenceDb)
            {
                findings.Add(new QcFinding(
                    CodeSilenceBoundary, ScopeType.Project, projectId.ToString("D"), null,
                    QualityStatus.PassWithWarnings, SeverityWarning,
                    string.Concat("Dialogue-only gap at ", segment.StartMs.ToString(CultureInfo.InvariantCulture), "ms is audible; silence boundaries leak."),
                    JsonSerializer.Serialize(new { startMs = segment.StartMs, durationMs = segment.DurationMs }),
                    inputs.MixedArtifactId));
            }
        }
    }

    private async Task RunCrossfadeChecksAsync(
        Guid projectId,
        string mixedFile,
        string workDir,
        QcInputs inputs,
        List<QcFinding> findings,
        CancellationToken cancellationToken)
    {
        var checkedGroups = 0;
        foreach (var group in inputs.Groups.OrderBy(g => g.StartMs).ThenBy(g => g.EndMs))
        {
            if (checkedGroups >= 20)
            {
                break;
            }

            var duration = group.EndMs - group.StartMs;
            if (duration <= 0)
            {
                continue;
            }

            checkedGroups++;
            IReadOnlyList<double> levels;
            try
            {
                levels = await _ffmpeg.MeasureChannelRmsDbAsync(
                    mixedFile, workDir, group.StartMs, duration, cancellationToken).ConfigureAwait(false);
            }
            catch (ErrorCodeException)
            {
                continue;
            }

            if (levels.Count > 0 && levels.Max() <= CrossfadeAudibleDb)
            {
                findings.Add(new QcFinding(
                    CodeCrossfade, ScopeType.Project, projectId.ToString("D"), null,
                    QualityStatus.PassWithWarnings, SeverityWarning,
                    string.Concat("Overlap window at ", group.StartMs.ToString(CultureInfo.InvariantCulture), "ms is silent; crossfade may have dropped audio."),
                    JsonSerializer.Serialize(new
                    {
                        overlapGroupId = group.Id.ToString("N"),
                        startMs = group.StartMs,
                        endMs = group.EndMs,
                    }),
                    inputs.MixedArtifactId));
            }
        }
    }

    private async Task RunSilenceCheckAsync(
        Guid projectId,
        string mixedFile,
        string workDir,
        QcInputs inputs,
        List<QcFinding> findings,
        CancellationToken cancellationToken)
    {
        double longest;
        try
        {
            longest = await _ffmpeg.MeasureMaxSilenceSecAsync(
                mixedFile, workDir, SilenceThresholdDb, SilenceWarningSec, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException)
        {
            return;
        }

        if (longest >= SilenceWarningSec)
        {
            findings.Add(new QcFinding(
                CodeSilence, ScopeType.Project, projectId.ToString("D"), null,
                QualityStatus.PassWithWarnings, SeverityWarning,
                string.Concat("Mixed audio contains ", longest.ToString("F1", CultureInfo.InvariantCulture), "s of near-silence."),
                JsonSerializer.Serialize(new { longestSilenceSec = longest }),
                inputs.MixedArtifactId));
        }
    }

    private async Task<Guid> PersistAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        QualityStatus verdict,
        List<QcFinding> findings,
        QcInputs inputs,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            foreach (var finding in findings)
            {
                db.Set<QualityResult>().Add(new QualityResult(
                    Guid.NewGuid(), tenantId, projectId, runId,
                    finding.ScopeType, finding.ScopeId, finding.SegmentId,
                    finding.Status, finding.Code, finding.Severity,
                    Truncate(finding.Message), finding.DetailsJson,
                    finding.ArtifactId, now));
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var parents = new List<Guid>();
        if (inputs.MixedArtifactId.HasValue)
        {
            parents.Add(inputs.MixedArtifactId.Value);
        }

        if (inputs.TimelineArtifactId.HasValue)
        {
            parents.Add(inputs.TimelineArtifactId.Value);
        }

        var reportJson = BuildReportJson(runId, verdict, findings, now);
        PublishResult published;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(reportJson), writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, projectId, runId,
                StageType.QualityControl, ArtifactType.QcReport,
                stream, ".json", "application/json",
                null, null, null, null,
                parents.Distinct().ToList(),
                null,
                cancellationToken).ConfigureAwait(false);
        }

        var metadataJson = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            runId = runId.ToString("N"),
            verdict = verdict.ToString(),
            blocked = findings.Count(f => f.Status == QualityStatus.Blocked),
            review = findings.Count(f => f.Status == QualityStatus.ManualReviewRequired),
            retry = findings.Count(f => f.Status == QualityStatus.RetryRequired),
            warnings = findings.Count(f => f.Status == QualityStatus.PassWithWarnings),
            findingCount = findings.Count,
        }, JsonOptions);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                metadataJson, published.ArtifactId, tenantId).ConfigureAwait(false);
        }

        return published.ArtifactId;
    }

    private async Task<Guid> CreateReviewAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        QualityStatus verdict,
        List<QcFinding> findings,
        CancellationToken cancellationToken)
    {
        var reason = verdict == QualityStatus.Blocked ? ReviewReasonBlocked : ReviewReasonReview;
        var reviewId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var codes = findings
            .Where(f => f.Status is QualityStatus.Blocked or QualityStatus.ManualReviewRequired)
            .Select(f => f.Code)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        var payload = JsonSerializer.Serialize(new
        {
            reason,
            runId = runId.ToString("N"),
            verdict = verdict.ToString(),
            codes,
            findingCount = findings.Count,
        }, JsonOptions);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<ReviewItem>().Add(new ReviewItem(
                reviewId, tenantId, projectId, runId,
                ScopeType.Project, projectId.ToString("D"), null,
                ReviewStatus.Open, reason, payload, now, now, null));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return reviewId;
    }

    private string BuildReportJson(Guid runId, QualityStatus verdict, List<QcFinding> findings, DateTimeOffset now)
    {
        var results = findings
            .OrderBy(f => f.ScopeType.ToString(), StringComparer.Ordinal)
            .ThenBy(f => f.ScopeId, StringComparer.Ordinal)
            .ThenBy(f => f.Code, StringComparer.Ordinal)
            .Select(f => new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["artifactId"] = f.ArtifactId.HasValue ? f.ArtifactId.Value.ToString("N") : null,
                ["code"] = f.Code,
                ["details"] = f.DetailsJson is null ? null : JsonSerializer.Deserialize<object>(f.DetailsJson, JsonOptions),
                ["message"] = f.Message,
                ["scope"] = f.ScopeType.ToString(),
                ["scopeId"] = f.ScopeId,
                ["segmentId"] = f.SegmentId.HasValue ? f.SegmentId.Value.ToString("N") : null,
                ["severity"] = f.Severity,
                ["status"] = f.Status.ToString(),
            })
            .ToList();

        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["counts"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["blocked"] = findings.Count(f => f.Status == QualityStatus.Blocked),
                ["findingCount"] = findings.Count,
                ["retry"] = findings.Count(f => f.Status == QualityStatus.RetryRequired),
                ["review"] = findings.Count(f => f.Status == QualityStatus.ManualReviewRequired),
                ["warnings"] = findings.Count(f => f.Status == QualityStatus.PassWithWarnings),
            },
            ["generatedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["results"] = results,
            ["runId"] = runId.ToString("N"),
            ["schemaVersion"] = SchemaVersion,
            ["verdict"] = verdict.ToString(),
        };

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    private async Task<QcInputs> LoadInputsAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        List<SpeechSegment> segments;
        List<SegmentOverlap> overlaps;
        List<OverlapGroup> groups;
        List<RunStageSummary> summaries;
        List<ReviewItem> reviews;
        List<TranscriptVersion> transcripts;
        List<TranslationVersion> translations;
        List<SpeakerVoiceAssignment> assignments;
        List<GeneratedAudioArtifact> finals;
        List<Artifact> audioArtifacts;
        List<ContentObject> contents;
        List<SyncResult> syncs;
        List<ProviderExecution> executions;
        List<MediaAsset> assets;
        Guid? timelineArtifactId = null;
        string? timelineJson = null;
        string? timelineStorageKey = null;
        Guid? mixedArtifactId = null;
        Guid? mixedContentObjectId = null;
        string? mixedStorageKey = null;
        string? mixedMetadataJson = null;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            segments = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .Where(s => s.RunId == runId)
                .OrderBy(s => s.StartMs)
                .ThenBy(s => s.Sequence)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            groups = await db.Set<OverlapGroup>()
                .AsNoTracking()
                .Where(g => g.RunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var groupIds = groups.Select(g => g.Id).ToList();
            overlaps = groupIds.Count == 0
                ? []
                : await db.Set<SegmentOverlap>()
                    .AsNoTracking()
                    .Where(o => groupIds.Contains(o.OverlapGroupId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            summaries = await db.Set<RunStageSummary>()
                .AsNoTracking()
                .Where(s => s.ProcessingRunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            reviews = await db.Set<ReviewItem>()
                .AsNoTracking()
                .Where(r => r.ProcessingRunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            transcripts = await db.Set<TranscriptVersion>()
                .AsNoTracking()
                .Where(t => t.RunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            translations = await db.Set<TranslationVersion>()
                .AsNoTracking()
                .Where(t => t.RunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            assignments = await db.Set<SpeakerVoiceAssignment>()
                .AsNoTracking()
                .Where(a => a.RunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            finals = await db.Set<GeneratedAudioArtifact>()
                .AsNoTracking()
                .Where(f => f.RunId == runId && !f.IsPreview)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var contentIds = finals.Select(f => f.ContentObjectId).Distinct().ToList();
            audioArtifacts = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId
                    && (a.Type == ArtifactType.GeneratedAudioFinal || a.Type == ArtifactType.GeneratedAudioPreview))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            contents = contentIds.Count == 0
                ? []
                : await db.Set<ContentObject>()
                    .AsNoTracking()
                    .Where(c => contentIds.Contains(c.Id))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            syncs = await db.Set<SyncResult>()
                .AsNoTracking()
                .Where(s => s.RunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            executions = await db.Set<ProviderExecution>()
                .AsNoTracking()
                .Where(e => e.ProcessingRunId == runId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            assets = await db.Set<MediaAsset>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var timelineRow = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.Timeline)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new { a.Id, a.ContentObjectId })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (timelineRow is not null)
            {
                timelineArtifactId = timelineRow.Id;
                timelineStorageKey = await db.Set<ContentObject>()
                    .AsNoTracking()
                    .Where(c => c.Id == timelineRow.ContentObjectId)
                    .Select(c => c.StorageKey)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }

            var mixedRow = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.MixedAudio)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new { a.Id, a.ContentObjectId, a.MetadataJson })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (mixedRow is not null)
            {
                mixedArtifactId = mixedRow.Id;
                mixedContentObjectId = mixedRow.ContentObjectId;
                mixedMetadataJson = mixedRow.MetadataJson;
                mixedStorageKey = await db.Set<ContentObject>()
                    .AsNoTracking()
                    .Where(c => c.Id == mixedRow.ContentObjectId)
                    .Select(c => c.StorageKey)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrWhiteSpace(timelineStorageKey))
        {
            timelineJson = await DownloadTextAsync(timelineStorageKey!, cancellationToken).ConfigureAwait(false);
        }

        return new QcInputs(
            segments, overlaps, groups, summaries, reviews, transcripts, translations,
            assignments, finals, audioArtifacts, contents, syncs, executions, assets,
            timelineArtifactId, timelineJson, timelineStorageKey,
            mixedArtifactId, mixedContentObjectId, mixedStorageKey, mixedMetadataJson);
    }

    private async Task<string?> DownloadTextAsync(string storageKey, CancellationToken cancellationToken)
    {
        Stream download;
        try
        {
            download = await _storage.DownloadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DomainException || ex is AppException)
        {
            throw;
        }
#pragma warning disable CA1031 // Storage mapping: any transport failure surfaces as STORAGE_UNAVAILABLE for worker policy.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        using (download)
        {
            using var reader = new StreamReader(download, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    private async Task DownloadAsync(string storageKey, string destPath, CancellationToken cancellationToken)
    {
        Stream download;
        try
        {
            download = await _storage.DownloadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DomainException || ex is AppException)
        {
            throw;
        }
#pragma warning disable CA1031 // Storage mapping: any transport failure surfaces as STORAGE_UNAVAILABLE for worker policy.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        try
        {
            using (download)
            {
                using var local = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await download.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
                await local.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "QC staging hit disk limits.", ex);
        }
    }

    private async Task<DubbingProject> LoadOwnedProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
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

        return project;
    }

    private async Task<ProcessingRun> EnsureRunUsableAsync(Guid tenantId, Guid runId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                throw new NotFoundException($"Processing run '{runId}' was not found.");
            }

            if (run.TenantId != tenantId || run.ProjectId != projectId)
            {
                throw new ForbiddenException($"Processing run '{runId}' does not belong to the current tenant/project.");
            }

            if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                throw new LeaseLostException($"Processing run '{run.Id}' is '{run.Status}'; aborting before commit.");
            }

            return run;
        }
    }

    private static List<SpeechSegment> ActiveSegments(IReadOnlyList<SpeechSegment> segments)
    {
        return segments
            .Where(s => !IsSkipped(s.Status))
            .OrderBy(s => s.StartMs)
            .ThenBy(s => s.Sequence)
            .ToList();
    }

    private static bool IsSkipped(string? status)
    {
        return string.Equals((status ?? string.Empty).Trim(), "Skipped", StringComparison.OrdinalIgnoreCase);
    }

    private static int SourceDurationMs(QcInputs inputs)
    {
        var valid = inputs.MediaAssets.FirstOrDefault(a => a.Status == MediaAssetStatus.Valid);
        if (valid is not null && valid.DurationMs > 0)
        {
            return valid.DurationMs;
        }

        var timeline = TryParseTimeline(inputs.TimelineJson);
        return timeline is not null && timeline.SourceDurationMs > 0 ? timeline.SourceDurationMs : 0;
    }

    private static TimelineLite? TryParseTimeline(string? timelineJson)
    {
        if (string.IsNullOrWhiteSpace(timelineJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(timelineJson);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            var sourceDurationMs = 0;
            if (root.TryGetProperty("sourceDurationMs", out var durationElement)
                && durationElement.ValueKind == JsonValueKind.Number
                && durationElement.TryGetInt32(out var parsed)
                && parsed >= 0)
            {
                sourceDurationMs = parsed;
            }

            string? backgroundId = null;
            if (root.TryGetProperty("backgroundArtifactId", out var bgElement)
                && bgElement.ValueKind == JsonValueKind.String)
            {
                var text = (bgElement.GetString() ?? string.Empty).Trim();
                if (text.Length > 0)
                {
                    backgroundId = text;
                }
            }

            var entries = new List<TimelineEntryLite>();
            if (root.TryGetProperty("entries", out var entriesElement)
                && entriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in entriesElement.EnumerateArray())
                {
                    if (item.ValueKind is not JsonValueKind.Object)
                    {
                        continue;
                    }

                    var segmentId = item.TryGetProperty("segmentId", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        ? (idElement.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    var sequence = item.TryGetProperty("sequence", out var seqElement) && seqElement.ValueKind == JsonValueKind.Number && seqElement.TryGetInt32(out var seq) ? seq : 0;
                    var startMs = item.TryGetProperty("startMs", out var startElement) && startElement.ValueKind == JsonValueKind.Number && startElement.TryGetInt32(out var start) ? start : -1;
                    var durationMs = item.TryGetProperty("durationMs", out var durElement) && durElement.ValueKind == JsonValueKind.Number && durElement.TryGetInt32(out var dur) ? dur : -1;
                    var audioId = item.TryGetProperty("audioArtifactId", out var audioElement) && audioElement.ValueKind == JsonValueKind.String
                        ? (audioElement.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    if (segmentId.Length == 0 || startMs < 0 || durationMs <= 0 || sequence < 0 || audioId.Length == 0)
                    {
                        return null;
                    }

                    entries.Add(new TimelineEntryLite(segmentId, sequence, startMs, durationMs, audioId));
                }
            }

            entries.Sort(static (a, b) =>
            {
                var byStart = a.StartMs.CompareTo(b.StartMs);
                if (byStart != 0)
                {
                    return byStart;
                }

                var bySeq = a.Sequence.CompareTo(b.Sequence);
                return bySeq != 0 ? bySeq : string.Compare(a.SegmentId, b.SegmentId, StringComparison.Ordinal);
            });

            return new TimelineLite(sourceDurationMs, entries, backgroundId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<TimelineEntryLite> ActiveGapWindows(TimelineLite timeline)
    {
        // Dialogue-only gaps between placed entries (silence that must stay silent).
        var windows = new List<TimelineEntryLite>();
        for (var i = 1; i < timeline.Entries.Count; i++)
        {
            var prevEnd = (long)timeline.Entries[i - 1].StartMs + timeline.Entries[i - 1].DurationMs;
            var gap = (long)timeline.Entries[i].StartMs - prevEnd;
            if (gap > 500 && prevEnd >= 0 && prevEnd <= int.MaxValue)
            {
                windows.Add(new TimelineEntryLite($"gap-{i.ToString(CultureInfo.InvariantCulture)}", i, (int)prevEnd, (int)Math.Min(gap, int.MaxValue), string.Empty));
            }
        }

        return windows;
    }

    private static Guid? AudioArtifactIdFor(Guid generatedAudioId, QcInputs inputs)
    {
        // The timeline-selected audio id is the GeneratedAudioFinal Artifact id
        // (030 contract); resolve it via the shared content object so checksum
        // findings link the staged bytes. Null when unresolvable (details
        // still carry the generated row and content ids).
        var contentId = inputs.Finals.FirstOrDefault(f => f.Id == generatedAudioId)?.ContentObjectId;
        if (contentId is null)
        {
            return null;
        }

        return inputs.AudioArtifacts.FirstOrDefault(a => a.ContentObjectId == contentId.Value)?.Id;
    }

    private static QcFinding Blocked(
        string code,
        ScopeType scopeType,
        string scopeId,
        Guid? segmentId,
        string message,
        object details,
        Guid? artifactId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new DomainException("QC finding ScopeId must not be empty.");
        }

        return new QcFinding(
            code, scopeType, scopeId, segmentId,
            QualityStatus.Blocked, SeverityBlocking,
            message,
            JsonSerializer.Serialize(details, details.GetType(), JsonOptions),
            artifactId);
    }

    private static string Truncate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Quality check failed.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
    }

    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-qc-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDirQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort; OS temp cleaners cover leftovers.
        }
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
