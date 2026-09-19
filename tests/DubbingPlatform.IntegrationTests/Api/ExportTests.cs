using System.Text.Json;
using DubbingPlatform.Application.Exports;
using DubbingPlatform.Application.Storage;

namespace DubbingPlatform.IntegrationTests.Api;

/// <summary>
/// Task 034: on-demand export generators plus completeness and download-policy
/// checks. Fully hermetic (pure generator functions + policy constants, no
/// database, no ffmpeg, no Docker) so these run everywhere including CI
/// without containers.
/// </summary>
public sealed class ExportTests
{
    private static ExportRunData BuildCompleteData()
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var runId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var generatedAt = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var segments = new List<ExportSegment>
        {
            new(0, Guid.Parse("33333333-3333-3333-3333-333333333333"), 1000, 3500, "Completed", "proj:speaker-a", "Speaker 1", "Hello world", "Hola mundo"),
            new(1, Guid.Parse("44444444-4444-4444-4444-444444444444"), 4000, 6500, "Completed", "proj:speaker-b", "Speaker 2", "How are you", "Cómo estás"),
        };
        var speakers = new List<ExportSpeaker>
        {
            new("proj:speaker-a", "Speaker 1", 1000, 3500, "mock", "mock-voice-1", "v1", "Stock", "deterministic"),
            new("proj:speaker-b", "Speaker 2", 4000, 6500, "mock", "mock-voice-2", "v1", "Stock", "deterministic"),
        };
        var quality = new List<ExportQcEntry>
        {
            new("Project", "timeline", null, "TIMELINE_OK", "Info", "Pass", "Timeline valid."),
            new("Segment", "seg-0", 0, "SYNC_OK", "Info", "Pass", "In sync."),
        };
        var completeness = new ExportCompleteness(projectId, runId, "abc123hash", 2, 0, 0, 0, true, generatedAt);
        return new ExportRunData(projectId, runId, "abc123hash", true, segments, speakers, quality, completeness);
    }

    private static ExportRunData BuildPartialData()
    {
        var projectId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var runId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var generatedAt = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var segments = new List<ExportSegment>
        {
            new(0, Guid.Parse("77777777-7777-7777-7777-777777777777"), 0, 2000, "Completed", "proj:speaker-a", "Speaker 1", "Hello world", "Hola mundo"),
            new(1, Guid.Parse("88888888-8888-8888-8888-888888888888"), 2500, 5000, "Pending", "proj:speaker-a", "Speaker 1", "Untranslated line", null),
        };
        var speakers = new List<ExportSpeaker>
        {
            new("proj:speaker-a", "Speaker 1", 0, 5000, "mock", "mock-voice-1", "v1", "Stock", "deterministic"),
        };
        var quality = new List<ExportQcEntry>
        {
            new("Segment", "seg-1", 1, "TRANSLATION_MISSING", "Warning", "Warn", "No translation yet."),
        };
        var completeness = new ExportCompleteness(projectId, runId, "def456hash", 1, 1, 0, 1, false, generatedAt);
        return new ExportRunData(projectId, runId, "def456hash", false, segments, speakers, quality, completeness);
    }

    [Fact]
    public void Srt_Valid()
    {
        var data = BuildCompleteData();

        var srt = SrtGenerator.Generate(data);

        Assert.DoesNotContain("\r", srt, StringComparison.Ordinal);
        Assert.Contains("1\n00:00:01,000 --> 00:00:03,500\nHola mundo", srt, StringComparison.Ordinal);
        Assert.Contains("2\n00:00:04,000 --> 00:00:06,500\nCómo estás", srt, StringComparison.Ordinal);
        Assert.EndsWith("\n", srt, StringComparison.Ordinal);
    }

    [Fact]
    public void WebVtt_Valid()
    {
        var data = BuildCompleteData();

        var vtt = WebVttGenerator.Generate(data);

        Assert.StartsWith("WEBVTT\n\n", vtt, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", vtt, StringComparison.Ordinal);
        Assert.Contains("00:00:01.000 --> 00:00:03.500\nHola mundo", vtt, StringComparison.Ordinal);
        Assert.Contains("00:00:04.000 --> 00:00:06.500\nCómo estás", vtt, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_Matches()
    {
        var data = BuildCompleteData();

        var json = TimelineJsonGenerator.Generate(data);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(32, root.GetProperty("projectId").GetString()!.Length);
        Assert.Equal(32, root.GetProperty("runId").GetString()!.Length);
        Assert.False(root.GetProperty("isPartial").GetBoolean());
        var entries = root.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal(0, entries[0].GetProperty("sequence").GetInt32());
        Assert.Equal(1, entries[1].GetProperty("sequence").GetInt32());
        Assert.Equal("Hola mundo", entries[0].GetProperty("translation").GetString());
        var completeness = root.GetProperty("completeness");
        Assert.True(completeness.GetProperty("isComplete").GetBoolean());
        Assert.Equal(2, completeness.GetProperty("completed").GetInt32());
    }

    [Fact]
    public void Speaker_Stable()
    {
        var data = BuildCompleteData();

        var first = SpeakerMetadataGenerator.Generate(data);
        var second = SpeakerMetadataGenerator.Generate(data);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        var speakers = document.RootElement.GetProperty("speakers");
        Assert.Equal(2, speakers.GetArrayLength());
        Assert.Equal("proj:speaker-a", speakers[0].GetProperty("speakerKey").GetString());
        Assert.Equal("proj:speaker-b", speakers[1].GetProperty("speakerKey").GetString());
        Assert.Equal("mock-voice-1", speakers[0].GetProperty("voice").GetProperty("voiceId").GetString());
        Assert.Equal("mock", speakers[0].GetProperty("voice").GetProperty("provider").GetString());
    }

    [Fact]
    public void Qc_Included()
    {
        var data = BuildCompleteData();

        var json = QualityReportGenerator.Generate(data);

        using var document = JsonDocument.Parse(json);
        var entries = document.RootElement.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        var codes = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            codes.Add(entry.GetProperty("code").GetString()!);
        }

        Assert.Contains("TIMELINE_OK", codes, StringComparer.Ordinal);
        Assert.Contains("SYNC_OK", codes, StringComparer.Ordinal);
        var completeness = document.RootElement.GetProperty("completeness");
        Assert.Equal(0, completeness.GetProperty("reviewCount").GetInt32());
    }

    [Fact]
    public void Partial_Has_Completeness()
    {
        var data = BuildPartialData();

        var timeline = TimelineJsonGenerator.Generate(data);
        var transcript = TranscriptJsonGenerator.Generate(data);
        var translation = TranslationJsonGenerator.Generate(data);

        using var timelineDoc = JsonDocument.Parse(timeline);
        Assert.True(timelineDoc.RootElement.GetProperty("isPartial").GetBoolean());
        var completeness = timelineDoc.RootElement.GetProperty("completeness");
        Assert.False(completeness.GetProperty("isComplete").GetBoolean());
        Assert.Equal(1, completeness.GetProperty("completed").GetInt32());
        Assert.Equal(1, completeness.GetProperty("failed").GetInt32());
        Assert.Equal(1, completeness.GetProperty("reviewCount").GetInt32());
        Assert.Equal("def456hash", completeness.GetProperty("sourceHash").GetString());

        using var transcriptDoc = JsonDocument.Parse(transcript);
        Assert.Equal(2, transcriptDoc.RootElement.GetProperty("entries").GetArrayLength());
        Assert.True(transcriptDoc.RootElement.GetProperty("isPartial").GetBoolean());

        using var translationDoc = JsonDocument.Parse(translation);
        Assert.Equal(2, translationDoc.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Null(translationDoc.RootElement.GetProperty("entries")[1].GetProperty("text").GetString());
    }

    [Fact]
    public void Url_Expires()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), SignedUrlPolicy.DefaultExpiry);
        Assert.Equal(TimeSpan.FromHours(1), SignedUrlPolicy.MaxExpiry);
        Assert.Equal(TimeSpan.FromMinutes(15), SignedUrlPolicy.Resolve(null));

        Assert.Equal(".srt", DubbingPlatform.Domain.Enums.ExportFormat.Srt switch
        {
            DubbingPlatform.Domain.Enums.ExportFormat.Srt => ExportFormatParser.ExtensionFor(DubbingPlatform.Domain.Enums.ExportFormat.Srt),
            _ => string.Empty,
        });
        Assert.Equal(".vtt", ExportFormatParser.ExtensionFor(DubbingPlatform.Domain.Enums.ExportFormat.WebVtt));
        Assert.Equal(".json", ExportFormatParser.ExtensionFor(DubbingPlatform.Domain.Enums.ExportFormat.JsonTimeline));
        Assert.Equal("application/json", ExportFormatParser.ContentTypeFor(DubbingPlatform.Domain.Enums.ExportFormat.QualityReportJson));

        Assert.True(ExportFormatParser.TryParse("json-timeline", out var timelineFormat));
        Assert.Equal(DubbingPlatform.Domain.Enums.ExportFormat.JsonTimeline, timelineFormat);
        Assert.True(ExportFormatParser.TryParse("speaker-metadata", out var speakerFormat));
        Assert.Equal(DubbingPlatform.Domain.Enums.ExportFormat.SpeakerMetadataJson, speakerFormat);
        Assert.True(ExportFormatParser.TryParse("quality-report", out var qcFormat));
        Assert.Equal(DubbingPlatform.Domain.Enums.ExportFormat.QualityReportJson, qcFormat);
    }
}
