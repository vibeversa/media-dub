using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Exports;

/// <summary>
/// Wire-format parsing for export formats. The API contract uses kebab-case
/// (<c>srt|webvtt|json-timeline|speaker-metadata|transcript|translation|quality-report</c>,
/// case-insensitive); the domain enum uses PascalCase. Hyphens, underscores,
/// and spaces are treated as equivalent separators so
/// <c>json_timeline</c> and <c>JSON-TIMELINE</c> both resolve. Also exposes the
/// deterministic file extension and content type per format (used for the
/// <c>Export</c> artifact and presigned downloads).
/// </summary>
public static class ExportFormatParser
{
    /// <summary>
    /// Tries to parse a wire-format export format name.
    /// </summary>
    public static bool TryParse(string? raw, out ExportFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim().ToLowerInvariant()
            .Replace('_', '-')
            .Replace(' ', '-');
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        switch (normalized)
        {
            case "srt":
                format = ExportFormat.Srt;
                return true;
            case "webvtt":
            case "vtt":
                format = ExportFormat.WebVtt;
                return true;
            case "json-timeline":
            case "jsontimeline":
            case "timeline":
            case "timeline-json":
                format = ExportFormat.JsonTimeline;
                return true;
            case "speaker-metadata":
            case "speakermetadata":
            case "speakermetadatajson":
            case "speakers":
            case "speaker-metadata-json":
                format = ExportFormat.SpeakerMetadataJson;
                return true;
            case "transcript":
            case "transcript-json":
            case "transcriptjson":
            case "transcripts":
                format = ExportFormat.TranscriptJson;
                return true;
            case "translation":
            case "translation-json":
            case "translationjson":
            case "translations":
                format = ExportFormat.TranslationJson;
                return true;
            case "quality-report":
            case "qualityreport":
            case "qualityreportjson":
            case "quality-report-json":
            case "qc":
            case "qc-report":
            case "qcreport":
                format = ExportFormat.QualityReportJson;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parses or throws a <see cref="Domain.Exceptions.DomainException"/>
    /// (mapped to 400 VALIDATION_FAILED by the API envelope).
    /// </summary>
    public static ExportFormat Parse(string? raw)
    {
        if (TryParse(raw, out var format))
        {
            return format;
        }

        throw new Domain.Exceptions.DomainException(
            $"Format '{raw}' is not a supported export format. Expected one of srt|webvtt|json-timeline|speaker-metadata|transcript|translation|quality-report.");
    }

    /// <summary>
    /// Canonical wire name for a format (used in audit payloads and logs).
    /// </summary>
    public static string ToWireName(ExportFormat format)
    {
        return format switch
        {
            ExportFormat.Srt => "srt",
            ExportFormat.WebVtt => "webvtt",
            ExportFormat.JsonTimeline => "json-timeline",
            ExportFormat.SpeakerMetadataJson => "speaker-metadata",
            ExportFormat.TranscriptJson => "transcript",
            ExportFormat.TranslationJson => "translation",
            ExportFormat.QualityReportJson => "quality-report",
            _ => format.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>
    /// File extension (leading dot) for an export format.
    /// </summary>
    public static string ExtensionFor(ExportFormat format)
    {
        return format switch
        {
            ExportFormat.Srt => ".srt",
            ExportFormat.WebVtt => ".vtt",
            _ => ".json",
        };
    }

    /// <summary>
    /// Content type for an export format.
    /// </summary>
    public static string ContentTypeFor(ExportFormat format)
    {
        return format switch
        {
            ExportFormat.Srt => "application/x-subrip",
            ExportFormat.WebVtt => "text/vtt",
            _ => "application/json",
        };
    }
}
