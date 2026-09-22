using System.Text.Json;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Identity;

namespace DubbingPlatform.Api.Models;

/// <summary>
/// Public-id parsing helper. Accepts raw GUIDs (<c>D</c>/<c>N</c>) and
/// prefixed ids (<c>prj_</c>, <c>upl_</c>, <c>run_</c>, <c>rev_</c>,
/// <c>exp_</c>, <c>seg_</c>); rejects empty ids.
/// </summary>
public static class PublicIdParser
{
    /// <summary>
    /// Parses a project id (<c>prj_</c> or raw GUID).
    /// </summary>
    public static Guid ParseProjectId(string? raw)
    {
        return Parse(raw, PublicIdMapper.DubbingProjectPrefix, "project");
    }

    /// <summary>
    /// Parses an upload id (<c>upl_</c> or raw GUID).
    /// </summary>
    public static Guid ParseUploadId(string? raw)
    {
        return Parse(raw, PublicIdMapper.UploadSessionPrefix, "upload");
    }

    /// <summary>
    /// Parses a run id (<c>run_</c> or raw GUID).
    /// </summary>
    public static Guid ParseRunId(string? raw)
    {
        return Parse(raw, PublicIdMapper.ProcessingRunPrefix, "run");
    }

    /// <summary>
    /// Parses a review id (<c>rev_</c> or raw GUID).
    /// </summary>
    public static Guid ParseReviewId(string? raw)
    {
        return Parse(raw, PublicIdMapper.ReviewItemPrefix, "review");
    }

    /// <summary>
    /// Parses an export id (<c>exp_</c> or raw GUID).
    /// </summary>
    public static Guid ParseExportId(string? raw)
    {
        return Parse(raw, PublicIdMapper.ExportJobPrefix, "export");
    }

    /// <summary>
    /// Parses a segment id (<c>seg_</c> or raw GUID).
    /// </summary>
    public static Guid ParseSegmentId(string? raw)
    {
        return Parse(raw, PublicIdMapper.SpeechSegmentPrefix, "segment");
    }

    private static Guid Parse(string? raw, string expectedPrefix, string kind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new Domain.Exceptions.DomainException($"{kind} id must not be empty.");
        }

        var trimmed = raw.Trim();
        if (Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty)
        {
            return guid;
        }

        var compact = trimmed.Replace("-", string.Empty, StringComparison.Ordinal);
        if (Guid.TryParseExact(compact, "N", out var compactGuid) && compactGuid != Guid.Empty)
        {
            return compactGuid;
        }

        try
        {
            var (prefix, id) = PublicIdMapper.FromPublic(trimmed);
            if (!string.Equals(prefix, expectedPrefix, StringComparison.Ordinal))
            {
                throw new Domain.Exceptions.DomainException($"{kind} id has an unexpected prefix '{prefix}'.");
            }

            return id;
        }
        catch (Domain.Exceptions.DomainException ex) when (ex.Message.Contains("Unknown", StringComparison.Ordinal) || ex.Message.Contains("unknown", StringComparison.Ordinal))
        {
            throw new Domain.Exceptions.DomainException($"{kind} id '{trimmed}' is not a valid identifier.");
        }
    }

    /// <summary>
    /// Formats a project id as <c>prj_</c>.
    /// </summary>
    public static string ToProjectId(Guid id)
    {
        return PublicIdMapper.ToPublic(id, PublicIdMapper.DubbingProjectPrefix);
    }

    /// <summary>
    /// Formats an upload id as <c>upl_</c>.
    /// </summary>
    public static string ToUploadId(Guid id)
    {
        return PublicIdMapper.ToPublic(id, PublicIdMapper.UploadSessionPrefix);
    }

    /// <summary>
    /// Formats a run id as <c>run_</c>.
    /// </summary>
    public static string ToRunId(Guid id)
    {
        return PublicIdMapper.ToPublic(id, PublicIdMapper.ProcessingRunPrefix);
    }

    /// <summary>
    /// Formats a review id as <c>rev_</c>.
    /// </summary>
    public static string ToReviewId(Guid id)
    {
        return PublicIdMapper.ToPublic(id, PublicIdMapper.ReviewItemPrefix);
    }

    /// <summary>
    /// Formats an export id as <c>exp_</c>.
    /// </summary>
    public static string ToExportId(Guid id)
    {
        return PublicIdMapper.ToPublic(id, PublicIdMapper.ExportJobPrefix);
    }

    /// <summary>
    /// Formats a segment id as <c>seg_</c>.
    /// </summary>
    public static string ToSegmentId(Guid id)
    {
        return PublicIdMapper.ToPublic(id, PublicIdMapper.SpeechSegmentPrefix);
    }

    /// <summary>
    /// Parses a speaker id (<c>spk_</c> or raw GUID).
    /// </summary>
    public static Guid ParseSpeakerId(string? raw)
    {
        return Parse(raw, PublicIdMapper.SpeakerPrefix, "speaker");
    }

    /// <summary>
    /// Formats a speaker id as <c>spk_</c>.
    /// </summary>
    public static string ToSpeakerId(Guid id)
    {
        return PublicIdMapper.ToPublic(id, PublicIdMapper.SpeakerPrefix);
    }
}
