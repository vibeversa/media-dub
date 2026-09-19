using System.Globalization;
using System.Text;

namespace DubbingPlatform.Application.Exports;

/// <summary>
/// Deterministic WebVTT subtitle generation. Pure function of
/// <see cref="ExportRunData"/>: <c>WEBVTT</c> header, blank line, then cues
/// ordered by sequence with <c>HH:MM:SS.mmm</c> timestamps. Text selection and
/// empty-skip semantics mirror <see cref="SrtGenerator"/>. Output uses LF and
/// ends with a trailing LF when non-empty.
/// </summary>
public static class WebVttGenerator
{
    /// <summary>
    /// Generates WebVTT content for a run snapshot.
    /// </summary>
    public static string Generate(ExportRunData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var builder = new StringBuilder();
        builder.Append("WEBVTT\n\n");

        foreach (var segment in data.Segments.OrderBy(s => s.Sequence).ThenBy(s => s.SegmentId))
        {
            var text = PickText(segment);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.Append(FormatTimestamp(segment.StartMs)).Append(" --> ").Append(FormatTimestamp(segment.EndMs)).Append('\n');
            builder.Append(NormalizeText(text!)).Append("\n\n");
        }

        return builder.ToString();
    }

    internal static string? PickText(ExportSegment segment)
    {
        if (!string.IsNullOrWhiteSpace(segment.TranslationText))
        {
            return segment.TranslationText;
        }

        return segment.TranscriptText;
    }

    internal static string NormalizeText(string text)
    {
        return text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>
    /// Formats milliseconds as <c>HH:MM:SS.mmm</c>. Pure. Negative values
    /// clamp to zero.
    /// </summary>
    public static string FormatTimestamp(int totalMs)
    {
        var clamped = Math.Max(0, totalMs);
        var hours = clamped / 3600000;
        var minutes = (clamped % 3600000) / 60000;
        var seconds = (clamped % 60000) / 1000;
        var millis = clamped % 1000;
        return string.Concat(
            hours.ToString("D2", CultureInfo.InvariantCulture), ":",
            minutes.ToString("D2", CultureInfo.InvariantCulture), ":",
            seconds.ToString("D2", CultureInfo.InvariantCulture), ".",
            millis.ToString("D3", CultureInfo.InvariantCulture));
    }
}
