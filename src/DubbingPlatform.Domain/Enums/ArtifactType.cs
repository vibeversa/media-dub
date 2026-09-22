namespace DubbingPlatform.Domain.Enums;

/// <summary>
/// Artifact types. Plan A pipeline types mirror <see cref="AssetType"/> 1:1;
/// an artifact is the persisted, content-addressed form of an asset. The five
/// preview-lane types below (Task B-004) have no <see cref="AssetType"/>
/// counterpart: they are produced outside the final pipeline for fast UI
/// playback and QC evidence, and never reuse final-audio artifact ids.
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
    Enrichment,
    MediaPreviewAudio,
    WaveformPeaks,
    VideoPreview,
    VoicePreviewAudio,
    QcEvidenceArtifact
}
