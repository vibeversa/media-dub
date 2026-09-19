using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Identity;

/// <summary>
/// Maps internal UUIDs to public prefixed IDs.
/// N-hex encoding (guid:N) is chosen for determinism without an extra ULID library.
/// Internal IDs remain ULID-compatible UUIDs; the public form is prefix + 32 lowercase hex.
/// </summary>
public static class PublicIdMapper
{
    public const string TenantPrefix = "tenant_";
    public const string DubbingProjectPrefix = "prj_";
    public const string ProcessingRunPrefix = "run_";
    public const string MediaAssetPrefix = "asset_";
    public const string UploadSessionPrefix = "upl_";
    public const string SpeechSegmentPrefix = "seg_";
    public const string SpeakerPrefix = "spk_";
    public const string ContextWindowPrefix = "ctx_";
    public const string VoiceProfilePrefix = "voice_";
    public const string ArtifactPrefix = "art_";
    public const string ContentObjectPrefix = "cnt_";
    public const string StageExecutionPrefix = "exe_";
    public const string ProviderExecutionPrefix = "prov_";
    public const string QualityResultPrefix = "qc_";
    public const string ReviewItemPrefix = "rev_";
    public const string ExportJobPrefix = "exp_";
    public const string JobPrefix = "job_";

    public static IReadOnlyList<string> AllPrefixes { get; } = new[]
    {
        TenantPrefix,
        DubbingProjectPrefix,
        ProcessingRunPrefix,
        MediaAssetPrefix,
        UploadSessionPrefix,
        SpeechSegmentPrefix,
        SpeakerPrefix,
        ContextWindowPrefix,
        VoiceProfilePrefix,
        ArtifactPrefix,
        ContentObjectPrefix,
        StageExecutionPrefix,
        ProviderExecutionPrefix,
        QualityResultPrefix,
        ReviewItemPrefix,
        ExportJobPrefix,
        JobPrefix,
    };

    public static string ToPublic(Guid id, string prefix)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Id must not be empty.");
        }

        if (string.IsNullOrEmpty(prefix) || !AllPrefixes.Contains(prefix))
        {
            throw new DomainException($"Unknown public ID prefix '{prefix}'.");
        }

        return string.Concat(prefix, id.ToString("N"));
    }

    public static (string Prefix, Guid Id) FromPublic(string publicId)
    {
        if (string.IsNullOrEmpty(publicId))
        {
            throw new DomainException("Public ID must not be empty.");
        }

        foreach (var prefix in AllPrefixes.OrderByDescending(p => p.Length))
        {
            if (publicId.StartsWith(prefix, StringComparison.Ordinal))
            {
                var hex = publicId.Substring(prefix.Length);
                if (hex.Length == 32 && Guid.TryParseExact(hex, "N", out var id) && id != Guid.Empty)
                {
                    return (prefix, id);
                }

                throw new DomainException($"Public ID '{publicId}' has an invalid identifier body.");
            }
        }

        throw new DomainException($"Public ID '{publicId}' has an unknown prefix.");
    }

    public static string PrefixFor<T>() => PrefixFor(typeof(T));

    public static string PrefixFor(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.Name switch
        {
            "Tenant" => TenantPrefix,
            "DubbingProject" => DubbingProjectPrefix,
            "ProcessingRun" => ProcessingRunPrefix,
            "MediaAsset" => MediaAssetPrefix,
            "UploadSession" => UploadSessionPrefix,
            "SpeechSegment" => SpeechSegmentPrefix,
            "Speaker" => SpeakerPrefix,
            "ContextWindow" => ContextWindowPrefix,
            "VoiceProfile" => VoiceProfilePrefix,
            "Artifact" => ArtifactPrefix,
            "ContentObject" => ContentObjectPrefix,
            "StageExecution" => StageExecutionPrefix,
            "ProviderExecution" => ProviderExecutionPrefix,
            "QualityResult" => QualityResultPrefix,
            "ReviewItem" => ReviewItemPrefix,
            "ExportJob" => ExportJobPrefix,
            "Job" => JobPrefix,
            _ => throw new DomainException($"No public ID prefix mapping for type '{type.Name}'."),
        };
    }
}
