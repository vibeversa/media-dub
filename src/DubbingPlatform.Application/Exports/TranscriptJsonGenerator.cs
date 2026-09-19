using System.Text.Json;

namespace DubbingPlatform.Application.Exports;

/// <summary>
/// Deterministic transcript export: selected transcript per segment ordered by
/// sequence plus the completeness block. Pure function of
/// <see cref="ExportRunData"/>; single trailing newline.
/// </summary>
public static class TranscriptJsonGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Generates transcript JSON content for a run snapshot.
    /// </summary>
    public static string Generate(ExportRunData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var entries = data.Segments
            .OrderBy(s => s.Sequence)
            .ThenBy(s => s.SegmentId)
            .Select(s => new TranscriptEntry(
                s.Sequence,
                s.SegmentId.ToString("N"),
                s.StartMs,
                s.EndMs,
                s.SpeakerKey,
                s.TranscriptText,
                s.Status))
            .ToList();

        var document = new TranscriptDocument(
            "1",
            data.ProjectId.ToString("N"),
            data.RunId.ToString("N"),
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

    private sealed record TranscriptDocument(
        string SchemaVersion,
        string ProjectId,
        string RunId,
        bool IsPartial,
        CompletenessBlock Completeness,
        IReadOnlyList<TranscriptEntry> Entries);

    private sealed record TranscriptEntry(
        int Sequence,
        string SegmentId,
        int StartMs,
        int EndMs,
        string? SpeakerKey,
        string? Text,
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
