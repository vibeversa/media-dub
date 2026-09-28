// Task 039C: exports unit gap closure.
using DubbingPlatform.Application.Exports;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Exports;

/// <summary>
/// Wire-format parsing: aliases, case, separator folding, explicit failure for
/// unknown values, and the frozen extension/content-type mapping.
/// </summary>
public sealed class ExportFormatParserMatrixTests
{
    [Theory]
    [InlineData("srt", ExportFormat.Srt)]
    [InlineData("SRT", ExportFormat.Srt)]
    [InlineData("  srt  ", ExportFormat.Srt)]
    [InlineData("webvtt", ExportFormat.WebVtt)]
    [InlineData("WebVTT", ExportFormat.WebVtt)]
    [InlineData("vtt", ExportFormat.WebVtt)]
    [InlineData("json-timeline", ExportFormat.JsonTimeline)]
    [InlineData("JSON-TIMELINE", ExportFormat.JsonTimeline)]
    [InlineData("json_timeline", ExportFormat.JsonTimeline)]
    [InlineData("json   timeline", ExportFormat.JsonTimeline)]
    [InlineData("jsontimeline", ExportFormat.JsonTimeline)]
    [InlineData("timeline", ExportFormat.JsonTimeline)]
    [InlineData("timeline-json", ExportFormat.JsonTimeline)]
    [InlineData("speaker-metadata", ExportFormat.SpeakerMetadataJson)]
    [InlineData("speaker_metadata", ExportFormat.SpeakerMetadataJson)]
    [InlineData("speakermetadata", ExportFormat.SpeakerMetadataJson)]
    [InlineData("speakermetadatajson", ExportFormat.SpeakerMetadataJson)]
    [InlineData("speakers", ExportFormat.SpeakerMetadataJson)]
    [InlineData("speaker-metadata-json", ExportFormat.SpeakerMetadataJson)]
    [InlineData("transcript", ExportFormat.TranscriptJson)]
    [InlineData("transcript-json", ExportFormat.TranscriptJson)]
    [InlineData("transcriptjson", ExportFormat.TranscriptJson)]
    [InlineData("transcripts", ExportFormat.TranscriptJson)]
    [InlineData("translation", ExportFormat.TranslationJson)]
    [InlineData("translation-json", ExportFormat.TranslationJson)]
    [InlineData("translationjson", ExportFormat.TranslationJson)]
    [InlineData("translations", ExportFormat.TranslationJson)]
    [InlineData("quality-report", ExportFormat.QualityReportJson)]
    [InlineData("quality_report", ExportFormat.QualityReportJson)]
    [InlineData("qualityreport", ExportFormat.QualityReportJson)]
    [InlineData("qualityreportjson", ExportFormat.QualityReportJson)]
    [InlineData("quality-report-json", ExportFormat.QualityReportJson)]
    [InlineData("qc", ExportFormat.QualityReportJson)]
    [InlineData("qc-report", ExportFormat.QualityReportJson)]
    [InlineData("qcreport", ExportFormat.QualityReportJson)]
    public void TryParse_Known_Formats(string raw, ExportFormat expected)
    {
        Assert.True(ExportFormatParser.TryParse(raw, out var format));
        Assert.Equal(expected, format);
        Assert.Equal(expected, ExportFormatParser.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("bogus")]
    [InlineData("srt2")]
    [InlineData("subrip")]
    [InlineData("--srt--")]
    [InlineData("srt-")]
    [InlineData("-srt")]
    [InlineData("srt vtt")]
    [InlineData("../../etc/passwd")]
    [InlineData("../srt")]
    [InlineData("srt;drop")]
    [InlineData("srt.vtt")]
    public void TryParse_Unknown_Formats_Fail(string? raw)
    {
        Assert.False(ExportFormatParser.TryParse(raw, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("qualityreportjsonn")]
    public void Parse_Unknown_Throws_Domain_Exception(string? raw)
    {
        var ex = Assert.Throws<DomainException>(() => ExportFormatParser.Parse(raw));
        Assert.Contains("is not a supported export format", ex.Message, StringComparison.Ordinal);
        Assert.Contains("json-timeline", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_Canonical_Wire_Name_Round_Trips()
    {
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var wire = ExportFormatParser.ToWireName(format);
            Assert.True(ExportFormatParser.TryParse(wire, out var parsed), wire);
            Assert.Equal(format, parsed);
        }
    }

    [Theory]
    [InlineData(ExportFormat.Srt, "srt")]
    [InlineData(ExportFormat.WebVtt, "webvtt")]
    [InlineData(ExportFormat.JsonTimeline, "json-timeline")]
    [InlineData(ExportFormat.SpeakerMetadataJson, "speaker-metadata")]
    [InlineData(ExportFormat.TranscriptJson, "transcript")]
    [InlineData(ExportFormat.TranslationJson, "translation")]
    [InlineData(ExportFormat.QualityReportJson, "quality-report")]
    public void ToWireName_Is_Canonical(ExportFormat format, string expected)
    {
        Assert.Equal(expected, ExportFormatParser.ToWireName(format));
    }

    [Fact]
    public void ToWireName_Unknown_Enum_Falls_Back_To_Lowercased_Name()
    {
        Assert.Equal("99", ExportFormatParser.ToWireName((ExportFormat)99));
    }

    [Theory]
    [InlineData(ExportFormat.Srt, ".srt")]
    [InlineData(ExportFormat.WebVtt, ".vtt")]
    [InlineData(ExportFormat.JsonTimeline, ".json")]
    [InlineData(ExportFormat.SpeakerMetadataJson, ".json")]
    [InlineData(ExportFormat.TranscriptJson, ".json")]
    [InlineData(ExportFormat.TranslationJson, ".json")]
    [InlineData(ExportFormat.QualityReportJson, ".json")]
    [InlineData((ExportFormat)42, ".json")]
    public void ExtensionFor_Is_Frozen(ExportFormat format, string expected)
    {
        Assert.Equal(expected, ExportFormatParser.ExtensionFor(format));
    }

    [Theory]
    [InlineData(ExportFormat.Srt, "application/x-subrip")]
    [InlineData(ExportFormat.WebVtt, "text/vtt")]
    [InlineData(ExportFormat.JsonTimeline, "application/json")]
    [InlineData(ExportFormat.SpeakerMetadataJson, "application/json")]
    [InlineData(ExportFormat.TranscriptJson, "application/json")]
    [InlineData(ExportFormat.TranslationJson, "application/json")]
    [InlineData(ExportFormat.QualityReportJson, "application/json")]
    [InlineData((ExportFormat)42, "application/json")]
    public void ContentTypeFor_Is_Frozen(ExportFormat format, string expected)
    {
        Assert.Equal(expected, ExportFormatParser.ContentTypeFor(format));
    }
}

/// <summary>
/// Export profile allowlist: normalization, the 64-char cap, path-traversal
/// rejection and the kebab-case charset gate.
/// </summary>
public sealed class ExportProfileValidatorMatrixTests
{
    [Fact]
    public void MaxLength_Is_64()
    {
        Assert.Equal(64, ExportProfileValidator.MaxLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Blank_Profile_Normalizes_To_Null(string? raw)
    {
        Assert.Null(ExportProfileValidator.Normalize(raw));
    }

    [Theory]
    [InlineData("default", "default")]
    [InlineData("Default", "default")]
    [InlineData("DEFAULT", "default")]
    [InlineData("  broadcast-hd  ", "broadcast-hd")]
    [InlineData("Broadcast_HD", "broadcast_hd")]
    [InlineData("social9", "social9")]
    [InlineData("a", "a")]
    [InlineData("0", "0")]
    public void Valid_Profiles_Are_Trimmed_And_Lowercased(string raw, string expected)
    {
        Assert.Equal(expected, ExportProfileValidator.Normalize(raw));
    }

    [Fact]
    public void Max_Length_Is_Accepted_And_One_Over_Is_Rejected()
    {
        var atLimit = new string('a', ExportProfileValidator.MaxLength);
        Assert.Equal(atLimit, ExportProfileValidator.Normalize(atLimit));

        var overLimit = new string('a', ExportProfileValidator.MaxLength + 1);
        var ex = Assert.Throws<DomainException>(() => ExportProfileValidator.Normalize(overLimit));
        Assert.Contains("at most 64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Length_Is_Measured_After_Trimming()
    {
        // 70 a's wrapped in spaces trims to 70 chars -> still rejected.
        var padded = "   " + new string('a', 70) + "   ";
        Assert.Throws<DomainException>(() => ExportProfileValidator.Normalize(padded));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("..a")]
    [InlineData("a/b")]
    [InlineData("/absolute")]
    [InlineData("a\\b")]
    [InlineData("\\\\server\\share")]
    [InlineData("nul\0byte")]
    public void Traversal_Characters_Are_Rejected(string raw)
    {
        var ex = Assert.Throws<DomainException>(() => ExportProfileValidator.Normalize(raw));
        Assert.Contains("path traversal", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has!")]
    [InlineData("dot.profile")]
    [InlineData("plus+profile")]
    [InlineData("semi;profile")]
    [InlineData("at@sign")]
    [InlineData("colon:profile")]
    [InlineData("percent%20")]
    public void Non_Kebab_Charset_Is_Rejected(string raw)
    {
        var ex = Assert.Throws<DomainException>(() => ExportProfileValidator.Normalize(raw));
        Assert.Contains("kebab-case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Traversal_Is_Rejected_Before_Charset_Gate()
    {
        // "/" is both a traversal character and outside the kebab charset; the
        // traversal rule must win so the message stays specific.
        var ex = Assert.Throws<DomainException>(() => ExportProfileValidator.Normalize("a!b/c"));
        Assert.Contains("path traversal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_Is_Idempotent()
    {
        var once = ExportProfileValidator.Normalize("  Broadcast-HD  ");
        Assert.Equal("broadcast-hd", once);
        Assert.Equal(once, ExportProfileValidator.Normalize(once));
    }
}
