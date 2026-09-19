using System.Text.Json;

namespace DubbingPlatform.Application.Exports;

/// <summary>
/// Deterministic JSON-timeline export. Mirrors the <c>Timeline</c> artifact
/// entry shape (sequence-ordered segments with timing, speaker, transcript,
/// and translation) plus the machine-readable <c>completeness</c> block so
/// partial runs stay valid JSON. Pure function of <see cref="ExportRunData"/>;
/// keys are emitted in declaration order, entries sorted by sequence then
/// segment id, no indentation, LF only (single trailing newline).
/// </summary>
public static class TimelineJsonGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Generates JSON-timeline content for a run snapshot.
    /// </summary>
    public static string Generate(ExportRunData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var entries = data.Segments
            .OrderBy(s => s.Sequence)
            .ThenBy(s => s.SegmentId)
            .Select(s => new TimelineEntry(
                s.Sequence,
                s.SegmentId.ToString("N"),
                s.StartMs,
                s.EndMs,
                s.SpeakerKey,
                s.TranscriptText,
                s.TranslationText,
                s.Status))
            .ToList();

        var document = new TimelineDocument(
            "1",
            data.ProjectId.ToString("N"),
            data.RunId.ToString("N"),
            data.SourceHash,
            !data.Completeness.IsComplete,
            new CompletenessBlock(
                data.Completeness.ProjectId.ToString("N"),
                data.Completeness.RunId.ToString("N"),
                data.Completeness.SourceHash,
                data.Completeness.Completed,
                data.Completeness.Failed,
                data.Completeness.Skipped,
                data.Completeness.ReviewCount,
                data.Completeness.IsComplete,
                data.Completeness.GeneratedAt),
            entries);

        return JsonSerializer.Serialize(document, JsonOptions) + "\n";
    }

    private sealed record TimelineDocument(
        string SchemaVersion,
        string ProjectId,
        string RunId,
        string SourceHash,
        bool IsPartial,
        CompletenessBlock Completeness,
        IReadOnlyList<TimelineEntry> Entries);

    private sealed record TimelineEntry(
        int Sequence,
        string SegmentId,
        int StartMs,
        int EndMs,
        string? SpeakerKey,
        string? Transcript,
        string? Translation,
        string Status);

    private sealed record CompletenessBlock(
        string ProjectId,
        string RunId,
        string SourceHash,
        int Completed,
        int Failed,
        int Skipped,
        int ReviewCount,
        bool IsComplete,
        DateTimeOffset GeneratedAt);
}
