// Task 039C: exports unit gap closure.
using System.Text.Json;
using DubbingPlatform.Application.Exports;

namespace DubbingPlatform.UnitTests.Exports;

/// <summary>
/// Shared deterministic snapshot fixtures. Every value is a fixed literal so
/// generated payloads are byte-comparable across runs and machines.
/// </summary>
internal static class ExportFixtures
{
    internal static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid RunId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid SegmentA = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid SegmentB = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal static readonly Guid SegmentC = Guid.Parse("55555555-5555-5555-5555-555555555555");
    internal static readonly DateTimeOffset GeneratedAt = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    internal const string SourceHash = "abc123hash";
    internal const string ProjectN = "11111111111111111111111111111111";
    internal const string RunN = "22222222222222222222222222222222";

    /// <summary>Includes the quotes System.Text.Json puts around the timestamp.</summary>
    internal const string GeneratedAtJson = "\"2026-09-17T12:00:00+00:00\"";

    internal static ExportSegment Segment(
        int sequence,
        Guid segmentId,
        int startMs,
        int endMs,
        string? transcript = null,
        string? translation = null,
        string status = "Completed",
        string? speakerKey = "proj:speaker-a")
        => new(sequence, segmentId, startMs, endMs, status, speakerKey, "Speaker 1", transcript, translation);

    internal static ExportSpeaker Speaker(
        string key,
        string? provider = "mock",
        string? voiceId = "mock-voice-1",
        int firstMs = 1000,
        int lastMs = 3500)
        => new(key, "Speaker " + key, firstMs, lastMs, provider, voiceId, "v1", "Stock", "deterministic");

    internal static ExportQcEntry Qc(
        string scopeType,
        string scopeId,
        int? segmentSequence,
        string code,
        string severity = "Info",
        string status = "Pass",
        string message = "ok")
        => new(scopeType, scopeId, segmentSequence, code, severity, status, message);

    /// <summary>
    /// Builds a snapshot; the completeness block is derived from the segments so
    /// fixtures never hand-wave "completed" counts.
    /// </summary>
    internal static ExportRunData Build(
        IReadOnlyList<ExportSegment> segments,
        IReadOnlyList<ExportSpeaker>? speakers = null,
        IReadOnlyList<ExportQcEntry>? quality = null,
        bool completenessComplete = true,
        int reviewCount = 0)
    {
        var segs = segments ?? [];
        var spk = speakers ?? [];
        var qc = quality ?? [];
        var completed = segs.Count(s => s.TranscriptText is not null && s.TranslationText is not null);
        var skipped = segs.Count(s => string.Equals(s.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
        var completeness = new ExportCompleteness(
            ProjectId,
            RunId,
            SourceHash,
            completed,
            segs.Count - completed,
            skipped,
            reviewCount,
            completenessComplete,
            GeneratedAt);
        return new ExportRunData(ProjectId, RunId, SourceHash, completenessComplete, segs, spk, qc, completeness);
    }

    internal static string CompletenessJson(bool isComplete, int completed, int failed, int skipped, int reviews)
        => string.Concat(
            "{\"projectId\":", JsonSerializer.Serialize(ProjectN),
            ",\"runId\":", JsonSerializer.Serialize(RunN),
            ",\"sourceHash\":", JsonSerializer.Serialize(SourceHash),
            ",\"completed\":", completed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ",\"failed\":", failed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ",\"skipped\":", skipped.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ",\"reviewCount\":", reviews.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ",\"isComplete\":", isComplete ? "true" : "false",
            ",\"generatedAt\":", GeneratedAtJson,
            "}");
}

/// <summary>
/// SRT is the primary player-facing payload: assert byte-exact output for
/// empty/single/many segments, dense renumbering after skips, negative and
/// out-of-order timings, and line-ending normalization of hostile text.
/// </summary>
public sealed class SrtGeneratorMatrixTests
{
    [Fact]
    public void Empty_Input_Produces_Empty_Payload()
    {
        Assert.Equal(string.Empty, SrtGenerator.Generate(ExportFixtures.Build([])));
    }

    [Fact]
    public void Null_Snapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SrtGenerator.Generate(null!));
    }

    [Fact]
    public void Single_Segment_Is_Byte_Exact()
    {
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 1000, 3500, "Hello world", "Hola mundo")]);

        var srt = SrtGenerator.Generate(data);

        Assert.Equal("1\n00:00:01,000 --> 00:00:03,500\nHola mundo\n\n", srt);
    }

    [Fact]
    public void Translation_Wins_Then_Transcript_Fallback_Then_Skip()
    {
        var data = ExportFixtures.Build(
        [
            ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1000, "transcript-only", "translation"),
            ExportFixtures.Segment(1, ExportFixtures.SegmentB, 2000, 3000, "fallback-used", null),
            ExportFixtures.Segment(2, ExportFixtures.SegmentC, 4000, 5000, "  \t ", "   ", "Skipped"),
        ]);

        var srt = SrtGenerator.Generate(data);

        // Whitespace-only translation falls back to the transcript; the fully
        // blank segment is skipped and never consumes a cue number.
        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:01,000\ntranslation\n\n"
            + "2\n00:00:02,000 --> 00:00:03,000\nfallback-used\n\n",
            srt);
    }

    [Fact]
    public void Many_Out_Of_Order_Segments_Are_Sequence_Ordered_And_Clamped()
    {
        var data = ExportFixtures.Build(
        [
            // Deliberately out of order; the generator sorts by sequence.
            ExportFixtures.Segment(2, ExportFixtures.SegmentC, -1500, 200, "third", null),
            ExportFixtures.Segment(1, ExportFixtures.SegmentB, 61_000, 61_500, "dos", "  "),
            ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 0, "uno", "uno"),
        ]);

        var srt = SrtGenerator.Generate(data);

        // Negative start clamps to 00:00:00,000; zero-length cue is still emitted.
        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:00,000\nuno\n\n"
            + "2\n00:01:01,000 --> 00:01:01,500\ndos\n\n"
            + "3\n00:00:00,000 --> 00:00:00,200\nthird\n\n",
            srt);
    }

    [Fact]
    public void Equal_Sequences_Tie_Break_On_Segment_Id()
    {
        var data = ExportFixtures.Build(
        [
            ExportFixtures.Segment(0, ExportFixtures.SegmentB, 2000, 3000, "second-id", null),
            ExportFixtures.Segment(0, ExportFixtures.SegmentA, 1000, 1500, "first-id", null),
        ]);

        var srt = SrtGenerator.Generate(data);

        Assert.Equal(
            "1\n00:00:01,000 --> 00:00:01,500\nfirst-id\n\n"
            + "2\n00:00:02,000 --> 00:00:03,000\nsecond-id\n\n",
            srt);
    }

    [Fact]
    public void Hostile_Text_Is_Normalized_Not_HTML_Escaped()
    {
        // Quotes, CR/CRLF/LF, '<', '&' and a literal "-->" arrow are emitted
        // verbatim; only line endings are normalized so a stray CR can never
        // desynchronize a cue from its timestamp line.
        var text = "He said \"hi\"\nLine2\r\nLine3\rLine4 <b>&amp;</b> --> done";
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1000, text, null)]);

        var srt = SrtGenerator.Generate(data);

        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:01,000\n"
            + "He said \"hi\"\nLine2\nLine3\nLine4 <b>&amp;</b> --> done\n\n",
            srt);
        Assert.DoesNotContain("\r", srt, StringComparison.Ordinal);
        // Exactly one arrow belongs to the cue timing line; the second is
        // literal payload text (SRT has no arrow escaping).
        Assert.Equal(2, CountOccurrences(srt, "-->"));
        Assert.StartsWith("1\n00:00:00,000 --> 00:00:01,000\n", srt, StringComparison.Ordinal);
    }

    [Fact]
    public void Leading_And_Trailing_Whitespace_Is_Trimmed()
    {
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1000, "   \r\n padded \r\n   ", null)]);

        Assert.Equal("1\n00:00:00,000 --> 00:00:01,000\npadded\n\n", SrtGenerator.Generate(data));
    }

    [Fact]
    public void Payload_Always_Ends_With_Blank_Line_Separator()
    {
        var data = ExportFixtures.Build(
        [
            ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1000, "one", null),
            ExportFixtures.Segment(1, ExportFixtures.SegmentB, 1000, 2000, "two", null),
        ]);

        var srt = SrtGenerator.Generate(data);

        Assert.EndsWith("\n\n", srt, StringComparison.Ordinal);
        Assert.Equal(srt, SrtGenerator.Generate(data));
    }

    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(1, "00:00:00,001")]
    [InlineData(999, "00:00:00,999")]
    [InlineData(1000, "00:00:01,000")]
    [InlineData(59_999, "00:00:59,999")]
    [InlineData(60_000, "00:01:00,000")]
    [InlineData(3_600_000, "01:00:00,000")]
    [InlineData(3_661_001, "01:01:01,001")]
    [InlineData(-1, "00:00:00,000")]
    [InlineData(-999_999, "00:00:00,000")]
    [InlineData(int.MinValue, "00:00:00,000")]
    [InlineData(int.MaxValue, "596:31:23,647")]
    public void FormatTimestamp_Boundaries(int totalMs, string expected)
    {
        Assert.Equal(expected, SrtGenerator.FormatTimestamp(totalMs));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

/// <summary>
/// WebVTT mirrors SRT selection/skip semantics but keeps the header and uses a
/// dot before milliseconds.
/// </summary>
public sealed class WebVttGeneratorMatrixTests
{
    [Fact]
    public void Empty_Input_Still_Emits_Header()
    {
        Assert.Equal("WEBVTT\n\n", WebVttGenerator.Generate(ExportFixtures.Build([])));
    }

    [Fact]
    public void Null_Snapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WebVttGenerator.Generate(null!));
    }

    [Fact]
    public void Single_Segment_Is_Byte_Exact()
    {
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 1000, 3500, "Hello world", "Hola mundo")]);

        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:03.500\nHola mundo\n\n", WebVttGenerator.Generate(data));
    }

    [Fact]
    public void Many_Out_Of_Order_Segments_Are_Sequence_Ordered_And_Clamped()
    {
        var data = ExportFixtures.Build(
        [
            ExportFixtures.Segment(2, ExportFixtures.SegmentC, -1500, 200, "third", null),
            ExportFixtures.Segment(1, ExportFixtures.SegmentB, 61_000, 61_500, "dos", "  "),
            ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 0, "uno", "uno"),
        ]);

        Assert.Equal(
            "WEBVTT\n\n"
            + "00:00:00.000 --> 00:00:00.000\nuno\n\n"
            + "00:01:01.000 --> 00:01:01.500\ndos\n\n"
            + "00:00:00.000 --> 00:00:00.200\nthird\n\n",
            WebVttGenerator.Generate(data));
    }

    [Fact]
    public void Blank_Segment_Is_Skipped_Without_A_Cue()
    {
        var data = ExportFixtures.Build(
        [
            ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1000, null, "hola"),
            ExportFixtures.Segment(1, ExportFixtures.SegmentB, 1000, 2000, "   ", "   "),
            ExportFixtures.Segment(2, ExportFixtures.SegmentC, 2000, 3000, "bye", null),
        ]);

        Assert.Equal(
            "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nhola\n\n00:00:02.000 --> 00:00:03.000\nbye\n\n",
            WebVttGenerator.Generate(data));
    }

    [Fact]
    public void Hostile_Text_Has_Carriage_Returns_Stripped()
    {
        var text = "Quote \" and <tag> & more\r\nsecond\rthird --> arrow";
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1000, text, null)]);

        var vtt = WebVttGenerator.Generate(data);

        Assert.Equal(
            "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nQuote \" and <tag> & more\nsecond\nthird --> arrow\n\n",
            vtt);
        Assert.DoesNotContain("\r", vtt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(1, "00:00:00.001")]
    [InlineData(999, "00:00:00.999")]
    [InlineData(1000, "00:00:01.000")]
    [InlineData(60_000, "00:01:00.000")]
    [InlineData(3_600_000, "01:00:00.000")]
    [InlineData(-1, "00:00:00.000")]
    [InlineData(int.MaxValue, "596:31:23.647")]
    public void FormatTimestamp_Boundaries(int totalMs, string expected)
    {
        Assert.Equal(expected, WebVttGenerator.FormatTimestamp(totalMs));
    }

    [Fact]
    public void FormatTimestamp_Differs_From_Srt_Only_In_The_Separator()
    {
        foreach (var ms in new[] { 0, 1, 999, 1000, 60_000, 3_600_000, -1, int.MaxValue })
        {
            var srt = SrtGenerator.FormatTimestamp(ms);
            var vtt = WebVttGenerator.FormatTimestamp(ms);

            // The millisecond separator always sits four characters from the end
            // (the hours field may be wider than two digits).
            Assert.Equal(',', srt[srt.Length - 4]);
            Assert.Equal('.', vtt[vtt.Length - 4]);
            Assert.Equal(srt.Replace(',', '.'), vtt);
        }
    }
}

/// <summary>
/// Timeline JSON: entry ordering, the completeness block, the <c>isPartial</c>
/// projection and null-field emission.
/// </summary>
public sealed class TimelineJsonGeneratorMatrixTests
{
    [Fact]
    public void Null_Snapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TimelineJsonGenerator.Generate(null!));
    }

    [Fact]
    public void Empty_Input_Is_Valid_Json_With_Empty_Entries()
    {
        var json = TimelineJsonGenerator.Generate(ExportFixtures.Build([]));

        Assert.EndsWith("\"entries\":[]}\n", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(0, document.RootElement.GetProperty("entries").GetArrayLength());
        Assert.False(document.RootElement.GetProperty("isPartial").GetBoolean());
    }

    [Fact]
    public void Single_Segment_Is_Byte_Exact()
    {
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 1000, 3500, "Hello world", "Hola mundo")]);

        var json = TimelineJsonGenerator.Generate(data);

        Assert.Equal(
            "{\"schemaVersion\":\"1\""
            + ",\"projectId\":\"" + ExportFixtures.ProjectN + "\""
            + ",\"runId\":\"" + ExportFixtures.RunN + "\""
            + ",\"sourceHash\":\"abc123hash\""
            + ",\"isPartial\":false"
            + ",\"completeness\":" + ExportFixtures.CompletenessJson(true, 1, 0, 0, 0)
            + ",\"entries\":[{"
            + "\"sequence\":0"
            + ",\"segmentId\":\"33333333333333333333333333333333\""
            + ",\"startMs\":1000"
            + ",\"endMs\":3500"
            + ",\"speakerKey\":\"proj:speaker-a\""
            + ",\"transcript\":\"Hello world\""
            + ",\"translation\":\"Hola mundo\""
            + ",\"status\":\"Completed\""
            + "}]}\n",
            json);
    }

    [Fact]
    public void Out_Of_Order_And_Null_Fields_Are_Emitted_Verbatim()
    {
        var data = ExportFixtures.Build(
        [
            ExportFixtures.Segment(2, ExportFixtures.SegmentC, 3, 4, "c", null, "Pending", null),
            ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1, "a", "A"),
        ],
        completenessComplete: false);

        var json = TimelineJsonGenerator.Generate(data);

        using var document = JsonDocument.Parse(json);
        var entries = document.RootElement.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal(0, entries[0].GetProperty("sequence").GetInt32());
        Assert.Equal(2, entries[1].GetProperty("sequence").GetInt32());
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("translation").ValueKind);
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("speakerKey").ValueKind);
        Assert.Equal("Pending", entries[1].GetProperty("status").GetString());
        // Partial run (missing translation) projects isPartial = true.
        Assert.True(document.RootElement.GetProperty("isPartial").GetBoolean());
    }

    [Fact]
    public void Source_Hash_And_Ids_Are_N_Format()
    {
        var json = TimelineJsonGenerator.Generate(ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1, "a", "A")]));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(32, document.RootElement.GetProperty("projectId").GetString()!.Length);
        Assert.Equal(32, document.RootElement.GetProperty("runId").GetString()!.Length);
        Assert.Equal(32, document.RootElement.GetProperty("entries")[0].GetProperty("segmentId").GetString()!.Length);
        Assert.Equal(ExportFixtures.GeneratedAt, document.RootElement.GetProperty("completeness").GetProperty("generatedAt").GetDateTimeOffset());
    }

    [Fact]
    public void Deterministic_For_Identical_Input()
    {
        var data = ExportFixtures.Build(
        [
            ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1, "a", "A"),
            ExportFixtures.Segment(1, ExportFixtures.SegmentB, 2, 3, "b", "B"),
        ]);

        Assert.Equal(TimelineJsonGenerator.Generate(data), TimelineJsonGenerator.Generate(data));
    }
}

/// <summary>
/// Transcript and translation JSON share one shape; both are pinned separately
/// so a field swap between them fails.
/// </summary>
public sealed class TranscriptAndTranslationJsonGeneratorMatrixTests
{
    [Fact]
    public void Null_Snapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TranscriptJsonGenerator.Generate(null!));
        Assert.Throws<ArgumentNullException>(() => TranslationJsonGenerator.Generate(null!));
    }

    [Fact]
    public void Transcript_Single_Segment_Is_Byte_Exact()
    {
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 1000, 3500, "Hello world", "Hola mundo")]);

        Assert.Equal(
            "{\"schemaVersion\":\"1\""
            + ",\"projectId\":\"" + ExportFixtures.ProjectN + "\""
            + ",\"runId\":\"" + ExportFixtures.RunN + "\""
            + ",\"isPartial\":false"
            + ",\"completeness\":" + ExportFixtures.CompletenessJson(true, 1, 0, 0, 0)
            + ",\"entries\":[{"
            + "\"sequence\":0"
            + ",\"segmentId\":\"33333333333333333333333333333333\""
            + ",\"startMs\":1000"
            + ",\"endMs\":3500"
            + ",\"speakerKey\":\"proj:speaker-a\""
            + ",\"text\":\"Hello world\""
            + ",\"status\":\"Completed\""
            + "}]}\n",
            TranscriptJsonGenerator.Generate(data));
    }

    [Fact]
    public void Translation_Single_Segment_Is_Byte_Exact()
    {
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 1000, 3500, "Hello world", "Hola mundo")]);

        Assert.Equal(
            "{\"schemaVersion\":\"1\""
            + ",\"projectId\":\"" + ExportFixtures.ProjectN + "\""
            + ",\"runId\":\"" + ExportFixtures.RunN + "\""
            + ",\"isPartial\":false"
            + ",\"completeness\":" + ExportFixtures.CompletenessJson(true, 1, 0, 0, 0)
            + ",\"entries\":[{"
            + "\"sequence\":0"
            + ",\"segmentId\":\"33333333333333333333333333333333\""
            + ",\"startMs\":1000"
            + ",\"endMs\":3500"
            + ",\"speakerKey\":\"proj:speaker-a\""
            + ",\"text\":\"Hola mundo\""
            + ",\"status\":\"Completed\""
            + "}]}\n",
            TranslationJsonGenerator.Generate(data));
    }

    [Fact]
    public void Empty_Input_Produces_Empty_Entry_Arrays()
    {
        var data = ExportFixtures.Build([]);

        using var transcript = JsonDocument.Parse(TranscriptJsonGenerator.Generate(data));
        using var translation = JsonDocument.Parse(TranslationJsonGenerator.Generate(data));
        Assert.Equal(0, transcript.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Equal(0, translation.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void Out_Of_Order_Segments_Are_Reordered_And_Missing_Text_Stays_Null()
    {
        var data = ExportFixtures.Build(
        [
            ExportFixtures.Segment(3, ExportFixtures.SegmentC, 3, 4, "c", null, "Pending", null),
            ExportFixtures.Segment(1, ExportFixtures.SegmentB, 1, 2, "b", null, "Completed", null),
            ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1, "a", "A", "Completed", null),
        ]);

        using var transcript = JsonDocument.Parse(TranscriptJsonGenerator.Generate(data));
        using var translation = JsonDocument.Parse(TranslationJsonGenerator.Generate(data));

        var tEntries = transcript.RootElement.GetProperty("entries");
        var xEntries = translation.RootElement.GetProperty("entries");
        Assert.Equal(3, tEntries.GetArrayLength());
        Assert.Equal(0, tEntries[0].GetProperty("sequence").GetInt32());
        Assert.Equal(3, tEntries[2].GetProperty("sequence").GetInt32());
        Assert.Equal(JsonValueKind.Null, xEntries[1].GetProperty("text").ValueKind);
        Assert.Equal(JsonValueKind.Null, xEntries[2].GetProperty("text").ValueKind);
        Assert.Equal("A", xEntries[0].GetProperty("text").GetString());
        // Neither shape leaks the sibling field.
        Assert.False(tEntries[0].TryGetProperty("translation", out _));
        Assert.False(xEntries[0].TryGetProperty("transcript", out _));
    }

    [Fact]
    public void Completeness_Carries_Review_And_Skip_Counts()
    {
        var data = ExportFixtures.Build(
            [
                ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1, "a", "A"),
                ExportFixtures.Segment(1, ExportFixtures.SegmentB, 1, 2, "b", null, "skipped"),
            ],
            completenessComplete: false,
            reviewCount: 3);

        using var document = JsonDocument.Parse(TranscriptJsonGenerator.Generate(data));
        var completeness = document.RootElement.GetProperty("completeness");
        Assert.Equal(1, completeness.GetProperty("completed").GetInt32());
        Assert.Equal(1, completeness.GetProperty("failed").GetInt32());
        Assert.Equal(1, completeness.GetProperty("skipped").GetInt32());
        Assert.Equal(3, completeness.GetProperty("reviewCount").GetInt32());
        Assert.False(completeness.GetProperty("isComplete").GetBoolean());
        Assert.True(document.RootElement.GetProperty("isPartial").GetBoolean());
    }

    [Fact]
    public void Deterministic_For_Identical_Input()
    {
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1, "a", "A")]);

        Assert.Equal(TranscriptJsonGenerator.Generate(data), TranscriptJsonGenerator.Generate(data));
        Assert.Equal(TranslationJsonGenerator.Generate(data), TranslationJsonGenerator.Generate(data));
    }
}

/// <summary>
/// Speaker metadata: ordinal key ordering and the "no voice at all" null
/// projection (provider-only or id-only still emits a voice block).
/// </summary>
public sealed class SpeakerMetadataGeneratorMatrixTests
{
    [Fact]
    public void Null_Snapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SpeakerMetadataGenerator.Generate(null!));
    }

    [Fact]
    public void Empty_Input_Produces_Empty_Speaker_Array()
    {
        var json = SpeakerMetadataGenerator.Generate(ExportFixtures.Build([]));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(0, document.RootElement.GetProperty("speakers").GetArrayLength());
        Assert.Equal("1", document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(ExportFixtures.GeneratedAt, document.RootElement.GetProperty("generatedAt").GetDateTimeOffset());
    }

    [Fact]
    public void Single_Speaker_With_Voice_Is_Byte_Exact()
    {
        var data = ExportFixtures.Build([], [ExportFixtures.Speaker("proj:speaker-a")]);

        Assert.Equal(
            "{\"schemaVersion\":\"1\""
            + ",\"projectId\":\"" + ExportFixtures.ProjectN + "\""
            + ",\"runId\":\"" + ExportFixtures.RunN + "\""
            + ",\"generatedAt\":" + ExportFixtures.GeneratedAtJson
            + ",\"speakers\":[{"
            + "\"speakerKey\":\"proj:speaker-a\""
            + ",\"displayName\":\"Speaker proj:speaker-a\""
            + ",\"firstMs\":1000"
            + ",\"lastMs\":3500"
            + ",\"voice\":{"
            + "\"provider\":\"mock\""
            + ",\"voiceId\":\"mock-voice-1\""
            + ",\"version\":\"v1\""
            + ",\"type\":\"Stock\""
            + ",\"assignmentReason\":\"deterministic\""
            + "}}]}\n",
            SpeakerMetadataGenerator.Generate(data));
    }

    [Fact]
    public void Speakers_Are_Ordered_By_Key_Ordinal()
    {
        var data = ExportFixtures.Build(
            [],
            [
                ExportFixtures.Speaker("proj:speaker-c"),
                ExportFixtures.Speaker("A-uppercase"),
                ExportFixtures.Speaker("proj:speaker-b"),
                ExportFixtures.Speaker("a-lowercase"),
            ]);

        using var document = JsonDocument.Parse(SpeakerMetadataGenerator.Generate(data));
        var speakers = document.RootElement.GetProperty("speakers");
        Assert.Equal(4, speakers.GetArrayLength());

        var keys = new List<string>();
        foreach (var speaker in speakers.EnumerateArray())
        {
            keys.Add(speaker.GetProperty("speakerKey").GetString()!);
        }

        // Ordinal (code-unit) order, not culture-aware case-insensitive order:
        // uppercase 'A' (0x41) sorts before lowercase 'a' (0x61) and 'p'.
        Assert.Equal(4, keys.Count);
        Assert.Equal("A-uppercase", keys[0]);
        Assert.Equal("a-lowercase", keys[1]);
        Assert.Equal("proj:speaker-b", keys[2]);
        Assert.Equal("proj:speaker-c", keys[3]);
    }

    [Fact]
    public void Voice_Block_Requires_Provider_Or_Id()
    {
        var data = ExportFixtures.Build(
            [],
            [
                ExportFixtures.Speaker("a-none", provider: null, voiceId: null),
                ExportFixtures.Speaker("b-provider-only", provider: "mock", voiceId: null),
                ExportFixtures.Speaker("c-id-only", provider: null, voiceId: "v-9"),
            ]);

        using var document = JsonDocument.Parse(SpeakerMetadataGenerator.Generate(data));
        var speakers = document.RootElement.GetProperty("speakers");
        Assert.Equal(JsonValueKind.Null, speakers[0].GetProperty("voice").ValueKind);
        Assert.Equal("mock", speakers[1].GetProperty("voice").GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, speakers[1].GetProperty("voice").GetProperty("voiceId").ValueKind);
        Assert.Equal(JsonValueKind.Null, speakers[2].GetProperty("voice").GetProperty("provider").ValueKind);
        Assert.Equal("v-9", speakers[2].GetProperty("voice").GetProperty("voiceId").GetString());
    }

    [Fact]
    public void Deterministic_For_Identical_Input()
    {
        var data = ExportFixtures.Build([], [ExportFixtures.Speaker("proj:speaker-a")]);

        Assert.Equal(
            SpeakerMetadataGenerator.Generate(data),
            SpeakerMetadataGenerator.Generate(data));
    }
}

/// <summary>
/// Quality report: (ScopeType, ScopeId, Code) ordinal ordering and a valid
/// zero-entry report for clean runs.
/// </summary>
public sealed class QualityReportGeneratorMatrixTests
{
    [Fact]
    public void Null_Snapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => QualityReportGenerator.Generate(null!));
    }

    [Fact]
    public void Empty_Qc_Still_Emits_Valid_Report()
    {
        var json = QualityReportGenerator.Generate(ExportFixtures.Build([]));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(0, document.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Equal("1", document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(ExportFixtures.GeneratedAt, document.RootElement.GetProperty("generatedAt").GetDateTimeOffset());
        Assert.EndsWith("\"entries\":[]}\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_Are_Ordered_By_Scope_Type_Scope_Id_Then_Code()
    {
        var data = ExportFixtures.Build(
            [],
            [],
            [
                ExportFixtures.Qc("Segment", "seg-2", 2, "Z_CODE"),
                ExportFixtures.Qc("Project", "timeline", null, "A_CODE"),
                ExportFixtures.Qc("Segment", "seg-1", 1, "B_CODE"),
                ExportFixtures.Qc("Segment", "seg-1", 1, "A_CODE"),
            ]);

        using var document = JsonDocument.Parse(QualityReportGenerator.Generate(data));
        var entries = document.RootElement.GetProperty("entries");
        Assert.Equal(4, entries.GetArrayLength());

        var codes = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            codes.Add(string.Concat(
                entry.GetProperty("scopeType").GetString(),
                "/",
                entry.GetProperty("scopeId").GetString(),
                "/",
                entry.GetProperty("code").GetString()));
        }

        Assert.Equal(4, codes.Count);
        Assert.Equal("Project/timeline/A_CODE", codes[0]);
        Assert.Equal("Segment/seg-1/A_CODE", codes[1]);
        Assert.Equal("Segment/seg-1/B_CODE", codes[2]);
        Assert.Equal("Segment/seg-2/Z_CODE", codes[3]);
    }

    [Fact]
    public void Nullable_Segment_Sequence_Round_Trips_As_Null()
    {
        var data = ExportFixtures.Build(
            [],
            [],
            [
                ExportFixtures.Qc("Project", "timeline", null, "TIMELINE_OK"),
                ExportFixtures.Qc("Segment", "seg-1", 7, "SYNC_OK"),
            ]);

        using var document = JsonDocument.Parse(QualityReportGenerator.Generate(data));
        var entries = document.RootElement.GetProperty("entries");
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("segmentSequence").ValueKind);
        Assert.Equal(7, entries[1].GetProperty("segmentSequence").GetInt32());
    }

    [Fact]
    public void Completeness_Block_And_Entries_Are_Both_Present()
    {
        var data = ExportFixtures.Build(
            [ExportFixtures.Segment(0, ExportFixtures.SegmentA, 0, 1, "a", "A")],
            [],
            [ExportFixtures.Qc("Project", "timeline", null, "TIMELINE_OK")],
            completenessComplete: false,
            reviewCount: 2);

        using var document = JsonDocument.Parse(QualityReportGenerator.Generate(data));
        var completeness = document.RootElement.GetProperty("completeness");
        Assert.Equal(1, completeness.GetProperty("completed").GetInt32());
        Assert.Equal(0, completeness.GetProperty("failed").GetInt32());
        Assert.Equal(2, completeness.GetProperty("reviewCount").GetInt32());
        Assert.False(completeness.GetProperty("isComplete").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void Deterministic_For_Identical_Input()
    {
        var data = ExportFixtures.Build(
            [],
            [],
            [ExportFixtures.Qc("Segment", "seg-1", 1, "SYNC_OK")]);

        Assert.Equal(QualityReportGenerator.Generate(data), QualityReportGenerator.Generate(data));
    }
}
