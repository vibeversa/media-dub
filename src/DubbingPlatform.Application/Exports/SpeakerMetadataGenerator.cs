using System.Text.Json;

namespace DubbingPlatform.Application.Exports;

/// <summary>
/// Deterministic speaker-metadata export: project/run identity plus speakers
/// ordered by <c>SpeakerKey</c> (ordinal) with their stable voice assignments.
/// Pure function of <see cref="ExportRunData"/>; single trailing newline.
/// </summary>
public static class SpeakerMetadataGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Generates speaker-metadata content for a run snapshot.
    /// </summary>
    public static string Generate(ExportRunData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var speakers = data.Speakers
            .OrderBy(s => s.SpeakerKey, StringComparer.Ordinal)
            .Select(s => new SpeakerEntry(
                s.SpeakerKey,
                s.DisplayName,
                s.FirstMs,
                s.LastMs,
                s.VoiceProvider is null && s.VoiceId is null
                    ? null
                    : new VoiceEntry(s.VoiceProvider, s.VoiceId, s.VoiceVersion, s.VoiceType, s.AssignmentReason)))
            .ToList();

        var document = new SpeakerDocument(
            "1",
            data.ProjectId.ToString("N"),
            data.RunId.ToString("N"),
            data.Completeness.GeneratedAt,
            speakers);

        return JsonSerializer.Serialize(document, JsonOptions) + "\n";
    }

    private sealed record SpeakerDocument(
        string SchemaVersion,
        string ProjectId,
        string RunId,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<SpeakerEntry> Speakers);

    private sealed record SpeakerEntry(
        string SpeakerKey,
        string DisplayName,
        int FirstMs,
        int LastMs,
        VoiceEntry? Voice);

    private sealed record VoiceEntry(
        string? Provider,
        string? VoiceId,
        string? Version,
        string? Type,
        string? AssignmentReason);
}
