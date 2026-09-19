using System.Text.Json;

namespace DubbingPlatform.Application.Exports;

/// <summary>
/// Deterministic quality-report export: every <c>QualityResult</c> row for the
/// run ordered by <c>(ScopeType, ScopeId, Code)</c> ordinal plus the
/// completeness block. Pure function of <see cref="ExportRunData"/>; single
/// trailing newline. Empty QC still yields a valid report with zero entries
/// (never null).
/// </summary>
public static class QualityReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Generates quality-report JSON content for a run snapshot.
    /// </summary>
    public static string Generate(ExportRunData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var entries = data.QualityEntries
            .OrderBy(e => e.ScopeType, StringComparer.Ordinal)
            .ThenBy(e => e.ScopeId, StringComparer.Ordinal)
            .ThenBy(e => e.Code, StringComparer.Ordinal)
            .Select(e => new QualityEntry(
                e.ScopeType,
                e.ScopeId,
                e.SegmentSequence,
                e.Code,
                e.Severity,
                e.Status,
                e.Message))
            .ToList();

        var document = new QualityDocument(
            "1",
            data.ProjectId.ToString("N"),
            data.RunId.ToString("N"),
            data.Completeness.GeneratedAt,
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

    private sealed record QualityDocument(
        string SchemaVersion,
        string ProjectId,
        string RunId,
        DateTimeOffset GeneratedAt,
        CompletenessBlock Completeness,
        IReadOnlyList<QualityEntry> Entries);

    private sealed record QualityEntry(
        string ScopeType,
        string ScopeId,
        int? SegmentSequence,
        string Code,
        string Severity,
        string Status,
        string Message);

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
