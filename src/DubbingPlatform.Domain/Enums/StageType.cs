namespace DubbingPlatform.Domain.Enums;

public enum StageType
{
    MediaValidation,
    MediaAnalysis,
    AudioPreparation,
    SourceSeparation,
    Vad,
    SegmentBuild,
    Diarization,
    Transcription,
    ContextBuild,
    Translation,
    VoiceAssignment,
    VoiceGeneration,
    TimingOptimization,
    TimelineAssembly,
    AudioMixing,
    QualityControl,
    Render
}
