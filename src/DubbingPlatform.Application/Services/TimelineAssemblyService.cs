using System.Globalization;
using System.Text.Json;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// One timeline input segment (source placement + selected audio).
/// <see cref="AudioArtifactId"/> is the selected audio <c>Artifact</c> id
/// (timing-chosen preview when stretching won, else the final);<see cref="AudioDurationMs"/>
/// is its generated duration. <see cref="Status"/> equal to <c>Skipped</c>
/// (case-insensitive) marks the segment omitted from entries.
/// </summary>
public sealed record TimelineSegmentInput(
    Guid SegmentId,
    int Sequence,
    int StartMs,
    int EndMs,
    string Status,
    Guid AudioArtifactId,
    int AudioDurationMs);

/// <summary>
/// One segment-to-group membership (a segment may appear in several groups;
/// an overlapping pair is intentional when their group sets intersect).
/// </summary>
public sealed record TimelineOverlapInput(Guid SegmentId, Guid OverlapGroupId);

/// <summary>
/// One placed timeline entry. <see cref="OverlapGroupId"/> is the shared
/// group (first ordered) when the segment belongs to at least one group,
/// else null. <see cref="Sequence"/> is persisted for the
/// StartMs-then-Sequence deterministic order.
/// </summary>
public sealed record TimelineEntry(
    string SegmentId,
    int Sequence,
    int StartMs,
    int DurationMs,
    string AudioArtifactId,
    string? OverlapGroupId);

/// <summary>
/// Pure timeline build output: placed entries plus skipped ids and provenance.
/// </summary>
public sealed record TimelineBuildResult(
    IReadOnlyList<TimelineEntry> Entries,
    IReadOnlyList<string> SkippedSegmentIds,
    string? BackgroundArtifactId,
    int SourceDurationMs);

/// <summary>
/// Outcome of one run timeline assembly (artifact persisted; stage commit
/// stays worker-owned per the task's claim/call/Complete split).
/// </summary>
public sealed record TimelineAssemblyResult(
    Guid ArtifactId,
    int EntryCount,
    int SkippedCount,
    int SourceDurationMs,
    Guid? BackgroundArtifactId);

/// <summary>
/// Deterministic run-scoped timeline assembly with integrity checks.
/// Placement: each selected generated segment at its source
/// <c>StartMs</c> with the generated audio duration (silence gaps stay
/// silent; no filling or shifting — onset/lead-lag placement is fixed here).
/// Intentional overlaps (members sharing an <c>OverlapGroup</c>) are kept;
/// any placed-audio overlap without a shared group throws
/// <c>PIPELINE_INVARIANT_VIOLATION</c>. Any entry end beyond
/// <c>sourceDurationMs + 100ms</c> throws <c>QC_BLOCKED</c> before any
/// artifact commit (the task's <c>BLOCKED</c> maps to the only blocking code
/// in <see cref="ErrorCodes"/>). Missing audio throws
/// <c>ARTIFACT_UNAVAILABLE</c> unless the segment status is
/// <c>Skipped</c> (omitted + listed in timeline metadata). JSON is canonical:
/// entries sorted by StartMs then Sequence then SegmentId, keys alphabetical,
/// no indentation. Never logs ids beyond run/segment counts and durations.
/// </summary>
public sealed class TimelineAssemblyService
{
    /// <summary>Artifact/row schema version for every timeline payload.</summary>
    public const string SchemaVersion = "1";

    /// <summary>Overflow tolerance past the source duration (auditable in metadata).</summary>
    public const int OverflowToleranceMs = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly ILogger<TimelineAssemblyService> _logger;

    public TimelineAssemblyService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        ILogger<TimelineAssemblyService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _logger = logger;
    }

    /// <summary>
    /// Pure deterministic build + validation (hermetic; no I/O). Throws
    /// <see cref="ErrorCodeException"/> (<c>ARTIFACT_UNAVAILABLE</c>) for
    /// missing audio on non-skipped segments,
    /// <c>PIPELINE_INVARIANT_VIOLATION</c> for overlaps without a shared
    /// group, and <c>QC_BLOCKED</c> for overflow. Pure.
    /// </summary>
    public static TimelineBuildResult BuildTimeline(
        IReadOnlyList<TimelineSegmentInput> segments,
        IReadOnlyList<TimelineOverlapInput> overlaps,
        int sourceDurationMs,
        Guid? backgroundArtifactId)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(overlaps);
        if (sourceDurationMs < 0)
        {
            throw new DomainException("Source duration must be >= 0.");
        }

        if (backgroundArtifactId.HasValue && backgroundArtifactId.Value == Guid.Empty)
        {
            throw new DomainException("BackgroundArtifactId must not be empty when set.");
        }

        var groupsBySegment = new Dictionary<Guid, SortedSet<string>>();
        foreach (var overlap in overlaps)
        {
            if (overlap.SegmentId == Guid.Empty || overlap.OverlapGroupId == Guid.Empty)
            {
                throw new DomainException("Overlap ids must not be empty.");
            }

            if (!groupsBySegment.TryGetValue(overlap.SegmentId, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                groupsBySegment[overlap.SegmentId] = set;
            }

            set.Add(overlap.OverlapGroupId.ToString("N"));
        }

        var entries = new List<TimelineEntry>(segments.Count);
        var skipped = new List<string>();
        var seen = new HashSet<Guid>();
        foreach (var segment in segments)
        {
            if (segment.SegmentId == Guid.Empty)
            {
                throw new DomainException("SegmentId must not be empty.");
            }

            if (!seen.Add(segment.SegmentId))
            {
                throw new DomainException("Duplicate segment in timeline input.");
            }

            if (segment.Sequence < 0)
            {
                throw new DomainException("Sequence must be >= 0.");
            }

            if (segment.StartMs < 0 || segment.EndMs <= segment.StartMs)
            {
                throw new DomainException("Segment window is invalid.");
            }

            if (IsSkipped(segment.Status))
            {
                skipped.Add(segment.SegmentId.ToString("N"));
                continue;
            }

            if (segment.AudioArtifactId == Guid.Empty || segment.AudioDurationMs <= 0)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ArtifactUnavailable,
                    string.Concat("Selected audio for segment '", segment.SegmentId.ToString("D"), "' is unavailable."));
            }

            string? groupId = null;
            if (groupsBySegment.TryGetValue(segment.SegmentId, out var groups) && groups.Count > 0)
            {
                groupId = groups.Min;
            }

            entries.Add(new TimelineEntry(
                segment.SegmentId.ToString("N"),
                segment.Sequence,
                segment.StartMs,
                segment.AudioDurationMs,
                segment.AudioArtifactId.ToString("N"),
                groupId));
        }

        var ordered = entries
            .OrderBy(e => e.StartMs)
            .ThenBy(e => e.Sequence)
            .ThenBy(e => e.SegmentId, StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var aStart = (long)ordered[i].StartMs;
                var aEnd = aStart + ordered[i].DurationMs;
                var bStart = (long)ordered[j].StartMs;
                var bEnd = bStart + ordered[j].DurationMs;
                var overlap = Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart);
                if (overlap <= 0)
                {
                    continue;
                }

                var aGroups = GroupsOf(ordered[i].SegmentId, groupsBySegment);
                var bGroups = GroupsOf(ordered[j].SegmentId, groupsBySegment);
                if (aGroups.Count == 0 || bGroups.Count == 0 || !aGroups.Overlaps(bGroups))
                {
                    throw new ErrorCodeException(
                        ErrorCodes.PipelineInvariantViolation,
                        string.Concat(
                            "Timeline has an invalid overlap between segments '",
                            ordered[i].SegmentId,
                            "' and '",
                            ordered[j].SegmentId,
                            "' with no shared overlap group."));
                }
            }
        }

        var limit = (long)sourceDurationMs + OverflowToleranceMs;
        foreach (var entry in ordered)
        {
            var end = (long)entry.StartMs + entry.DurationMs;
            if (end > limit)
            {
                throw new ErrorCodeException(
                    ErrorCodes.QcBlocked,
                    string.Concat(
                        "Timeline entry '",
                        entry.SegmentId,
                        "' ends at ",
                        end.ToString(CultureInfo.InvariantCulture),
                        "ms, beyond the source duration ",
                        sourceDurationMs.ToString(CultureInfo.InvariantCulture),
                        "ms + 100ms tolerance."));
            }
        }

        skipped.Sort(StringComparer.Ordinal);
        return new TimelineBuildResult(
            ordered,
            skipped,
            backgroundArtifactId?.ToString("N"),
            sourceDurationMs);
    }

    /// <summary>
    /// Canonical timeline JSON (sorted keys, no indentation). Pure. Keys are
    /// alphabetical at every level; entries keep the BuildTimeline order.
    /// </summary>
    public static string BuildJson(Guid runId, TimelineBuildResult timeline)
    {
        if (runId == Guid.Empty)
        {
            throw new DomainException("RunId must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(timeline);

        var entries = timeline.Entries.Select(e =>
        {
            var map = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["audioArtifactId"] = e.AudioArtifactId,
                ["durationMs"] = e.DurationMs,
                ["overlapGroupId"] = e.OverlapGroupId,
                ["segmentId"] = e.SegmentId,
                ["sequence"] = e.Sequence,
                ["startMs"] = e.StartMs,
            };
            return map;
        }).ToList();

        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["backgroundArtifactId"] = timeline.BackgroundArtifactId,
            ["entries"] = entries,
            ["overflowToleranceMs"] = OverflowToleranceMs,
            ["runId"] = runId.ToString("N"),
            ["schemaVersion"] = SchemaVersion,
            ["skippedSegmentIds"] = timeline.SkippedSegmentIds.ToList(),
            ["sourceDurationMs"] = timeline.SourceDurationMs,
        };

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    /// <summary>
    /// Assembles the run timeline end to end (load, pure build, artifact
    /// persist). The caller (worker) owns the stage commit. Throws without
    /// committing any artifact when validation fails.
    /// </summary>
    public async Task<TimelineAssemblyResult> AssembleAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));

        await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        await EnsureRunActiveAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);

        var segments = await LoadSegmentsAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        var sourceDurationMs = await LoadSourceDurationAsync(tenantId, projectId, segments, cancellationToken).ConfigureAwait(false);
        var backgroundId = await LoadBackgroundArtifactIdAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        var overlaps = await LoadOverlapsAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);

        var inputs = new List<TimelineSegmentInput>(segments.Count);
        var chosenIds = new List<Guid>(segments.Count);
        foreach (var segment in segments)
        {
            if (IsSkipped(segment.Status))
            {
                inputs.Add(new TimelineSegmentInput(
                    segment.Id, segment.Sequence, segment.StartMs, segment.EndMs,
                    segment.Status, Guid.Empty, 0));
                continue;
            }

            var chosen = await ResolveSelectedAudioAsync(tenantId, runId, segment, cancellationToken).ConfigureAwait(false);
            inputs.Add(new TimelineSegmentInput(
                segment.Id, segment.Sequence, segment.StartMs, segment.EndMs,
                segment.Status, chosen.ArtifactId, chosen.DurationMs));
            chosenIds.Add(chosen.ArtifactId);
        }

        var built = BuildTimeline(inputs, overlaps, sourceDurationMs, backgroundId);
        var json = BuildJson(runId, built);

        PublishResult published;
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json), writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, projectId, runId,
                StageType.TimelineAssembly, ArtifactType.Timeline,
                stream, ".json", "application/json",
                null, null,
                null, null,
                chosenIds.Distinct().ToList(),
                null,
                cancellationToken).ConfigureAwait(false);
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                json, published.ArtifactId, tenantId).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Timeline assembled for run {RunId}: {Entries} entries, {Skipped} skipped, source {Source}ms.",
            runId, built.Entries.Count, built.SkippedSegmentIds.Count, sourceDurationMs);

        return new TimelineAssemblyResult(
            published.ArtifactId,
            built.Entries.Count,
            built.SkippedSegmentIds.Count,
            sourceDurationMs,
            backgroundId);
    }

    private static bool IsSkipped(string? status)
    {
        return string.Equals(status?.Trim(), "Skipped", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> GroupsOf(string segmentIdN, Dictionary<Guid, SortedSet<string>> groupsBySegment)
    {
        if (Guid.TryParseExact(segmentIdN, "N", out var id)
            && groupsBySegment.TryGetValue(id, out var set))
        {
            return new HashSet<string>(set, StringComparer.Ordinal);
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    private sealed record ChosenAudio(Guid ArtifactId, int DurationMs);

    private async Task<ChosenAudio> ResolveSelectedAudioAsync(
        Guid tenantId,
        Guid runId,
        SpeechSegment segment,
        CancellationToken cancellationToken)
    {
        var timingArtifactId = await LoadTimingOutputAsync(tenantId, runId, segment.Id, cancellationToken).ConfigureAwait(false);
        if (timingArtifactId.HasValue && timingArtifactId.Value != Guid.Empty)
        {
            var duration = await LoadArtifactDurationAsync(tenantId, runId, timingArtifactId.Value, segment.Id, cancellationToken).ConfigureAwait(false);
            if (duration.HasValue && duration.Value > 0)
            {
                return new ChosenAudio(timingArtifactId.Value, duration.Value);
            }
        }

        var finalArtifactId = await FindFinalArtifactAsync(tenantId, runId, segment.Id, cancellationToken).ConfigureAwait(false);
        if (finalArtifactId.HasValue && finalArtifactId.Value != Guid.Empty)
        {
            var duration = await LoadArtifactDurationAsync(tenantId, runId, finalArtifactId.Value, segment.Id, cancellationToken).ConfigureAwait(false);
            if (duration.HasValue && duration.Value > 0)
            {
                return new ChosenAudio(finalArtifactId.Value, duration.Value);
            }
        }

        throw new ErrorCodeException(
            ErrorCodes.ArtifactUnavailable,
            string.Concat("Selected audio for segment '", segment.Id.ToString("D"), "' is unavailable."));
    }

    private async Task<Guid?> LoadTimingOutputAsync(
        Guid tenantId,
        Guid runId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var outputs = await db.Set<StageExecution>()
                .AsNoTracking()
                .Where(e => e.ProcessingRunId == runId
                    && e.StageType == StageType.TimingOptimization
                    && e.SegmentId == segmentId
                    && e.Status == StageStatus.Completed)
                .OrderByDescending(e => e.CompletedAt)
                .ThenByDescending(e => e.CreatedAt)
                .Select(e => e.OutputArtifactIdsJson)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var output in outputs)
            {
                if (string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                try
                {
                    var ids = JsonSerializer.Deserialize<string[]>(output, JsonOptions);
                    if (ids is not null && ids.Length > 0
                        && Guid.TryParseExact(ids[0].Trim(), "N", out var parsed)
                        && parsed != Guid.Empty)
                    {
                        return parsed;
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return null;
        }
    }

    private async Task<Guid?> FindFinalArtifactAsync(
        Guid tenantId,
        Guid runId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.GeneratedAudioFinal)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new { a.Id, a.MetadataJson })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var needle = segmentId.ToString("N");
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.MetadataJson)
                    && row.MetadataJson.Contains(needle, StringComparison.Ordinal))
                {
                    return row.Id;
                }
            }

            return null;
        }
    }

    private async Task<int?> LoadArtifactDurationAsync(
        Guid tenantId,
        Guid runId,
        Guid artifactId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var artifact = await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken).ConfigureAwait(false);
            if (artifact is null || artifact.TenantId != tenantId || artifact.ProcessingRunId != runId)
            {
                return null;
            }

            var generated = await db.Set<GeneratedAudioArtifact>()
                .AsNoTracking()
                .Where(g => g.ContentObjectId == artifact.ContentObjectId)
                .OrderByDescending(g => g.CreatedAt)
                .Select(g => (int?)g.DurationMs)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (generated.HasValue && generated.Value > 0)
            {
                return generated.Value;
            }

            var fallback = await db.Set<GeneratedAudioArtifact>()
                .AsNoTracking()
                .Where(g => g.RunId == runId && g.SegmentId == segmentId)
                .OrderByDescending(g => g.CreatedAt)
                .Select(g => (int?)g.DurationMs)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (fallback.HasValue && fallback.Value > 0)
            {
                return fallback.Value;
            }

            if (!string.IsNullOrWhiteSpace(artifact.MetadataJson))
            {
                var parsed = TryParseDuration(artifact.MetadataJson);
                if (parsed.HasValue && parsed.Value > 0)
                {
                    return parsed.Value;
                }
            }

            return null;
        }
    }

    private static int? TryParseDuration(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "actualMs", "durationMs", "duration" })
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out var value)
                        && value > 0)
                    {
                        return value;
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<int> LoadSourceDurationAsync(
        Guid tenantId,
        Guid projectId,
        IReadOnlyList<SpeechSegment> segments,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var duration = await db.Set<MediaAsset>()
                .Where(a => a.ProjectId == projectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .Select(a => (int?)a.DurationMs)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (duration.HasValue && duration.Value > 0)
            {
                return duration.Value;
            }
        }

        if (segments.Count == 0)
        {
            return 0;
        }

        var max = segments.Max(s => s.EndMs);
        if (max <= 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                "Source duration is unavailable for timeline assembly.");
        }

        return max;
    }

    private async Task<Guid?> LoadBackgroundArtifactIdAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var outputs = await db.Set<StageExecution>()
                .AsNoTracking()
                .Where(e => e.ProcessingRunId == runId && e.StageType == StageType.SourceSeparation)
                .OrderByDescending(e => e.CompletedAt)
                .ThenByDescending(e => e.CreatedAt)
                .Select(e => new { e.Status, e.OutputArtifactIdsJson })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var output in outputs)
            {
                if (string.IsNullOrWhiteSpace(output.OutputArtifactIdsJson))
                {
                    continue;
                }

                try
                {
                    var ids = JsonSerializer.Deserialize<string[]>(output.OutputArtifactIdsJson, JsonOptions);
                    if (ids is not null && ids.Length > 1
                        && Guid.TryParseExact(ids[1].Trim(), "N", out var background)
                        && background != Guid.Empty)
                    {
                        var exists = await db.Set<Artifact>()
                            .AsNoTracking()
                            .AnyAsync(a => a.Id == background && a.ProcessingRunId == runId, cancellationToken).ConfigureAwait(false);
                        if (exists)
                        {
                            return background;
                        }
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.BackgroundStem)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<TimelineOverlapInput>> LoadOverlapsAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var groupIds = await db.Set<OverlapGroup>()
                .AsNoTracking()
                .Where(g => g.RunId == runId)
                .Select(g => g.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (groupIds.Count == 0)
            {
                return [];
            }

            var set = new HashSet<Guid>(groupIds);
            var rows = await db.Set<SegmentOverlap>()
                .AsNoTracking()
                .Where(o => o.TenantId == tenantId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows
                .Where(o => set.Contains(o.OverlapGroupId))
                .Select(o => new TimelineOverlapInput(o.SegmentId, o.OverlapGroupId))
                .ToList();
        }
    }

    private async Task<IReadOnlyList<SpeechSegment>> LoadSegmentsAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<SpeechSegment>()
                .AsNoTracking()
                .Where(s => s.RunId == runId)
                .OrderBy(s => s.Sequence)
                .ThenBy(s => s.StartMs)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadOwnedProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
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

    private async Task EnsureRunActiveAsync(Guid tenantId, Guid runId, Guid projectId, CancellationToken cancellationToken)
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
                throw new LeaseLostException($"Processing run '{runId}' is '{run.Status}'; aborting before commit.");
            }
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
            throw new DomainException($"{name} must not be empty.");
        }
    }
}
