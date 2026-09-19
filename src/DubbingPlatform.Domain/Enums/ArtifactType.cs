namespace DubbingPlatform.Domain.Enums;

/// <summary>
/// Artifact types mirror <see cref="AssetType"/> 1:1 for simplicity.
/// An artifact is the persisted, content-addressed form of an asset.
/// </summary>
public enum ArtifactType
{
    SourceOriginal,
    CanonicalAudio,
    WorkingAudio,
    DialogueStem,
    BackgroundStem,
    VadRegions,
    Segments,
    DiarizationMap,
    Transcript,
    Translation,
    GeneratedAudioPreview,
    GeneratedAudioFinal,
    Timeline,
    MixedAudio,
    QcReport,
    RenderedOutput,
    Export,
    FfprobeAnalysis,
    ContextWindow,
    Enrichment
}
