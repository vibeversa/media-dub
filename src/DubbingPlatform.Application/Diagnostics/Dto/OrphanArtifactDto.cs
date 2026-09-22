namespace DubbingPlatform.Application.Diagnostics.Dto;

/// <summary>
/// One orphaned content object (no owning artifact, media asset, or generated
/// audio reference). Carries ids, sizes, hashes, and timestamps only — never
/// storage keys (internal paths) or media bytes.
/// </summary>
public sealed record OrphanArtifactDto(
    string CorrelationId,
    Guid ContentObjectId,
    long SizeBytes,
    string MediaFormat,
    string? ContentHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastReferencedAt);
