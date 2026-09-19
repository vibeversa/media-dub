namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Frozen enrichment kinds for <see cref="EnrichmentRequested.Kind"/>.
/// Values are frozen: renaming is a breaking change.
/// </summary>
public static class EnrichmentKinds
{
    /// <summary>Face detect/track, active-speaker, face-to-speaker association.</summary>
    public const string VideoIntelligence = "VideoIntelligence";

    /// <summary>Lip-movement analysis with optional mouth transform.</summary>
    public const string LipSync = "LipSync";

    /// <summary>All known kinds in publish order.</summary>
    public static readonly string[] All = [VideoIntelligence, LipSync];

    /// <summary>Returns true for a known enrichment kind (ordinal).</summary>
    public static bool IsKnown(string? kind)
    {
        return !string.IsNullOrWhiteSpace(kind)
            && All.Contains(kind.Trim(), StringComparer.Ordinal);
    }
}
