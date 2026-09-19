using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Infrastructure.Messaging;

/// <summary>
/// Maps pipeline stages to workload queues. Queues are partitioned by workload
/// class, never per micro-stage: CPU media preparation, media rendering,
/// general AI providers, GPU-backed generation, on-demand export, and
/// maintenance/reconciliation. Orchestration control messages are published
/// (pub/sub) to <c>control.orchestration</c>; work items are sent
/// point-to-point to their workload queue.
/// </summary>
public static class WorkQueueRouter
{
    /// <summary>
    /// Returns the workload queue for a stage.
    /// </summary>
    public static string QueueFor(StageType stage)
    {
        return stage switch
        {
            StageType.MediaValidation => QueueNames.MediaPreparation,
            StageType.MediaAnalysis => QueueNames.MediaPreparation,
            StageType.AudioPreparation => QueueNames.MediaPreparation,
            StageType.SourceSeparation => QueueNames.MediaPreparation,
            StageType.Vad => QueueNames.MediaPreparation,
            StageType.SegmentBuild => QueueNames.MediaPreparation,
            StageType.Diarization => QueueNames.AiProvider,
            StageType.Transcription => QueueNames.AiProvider,
            StageType.ContextBuild => QueueNames.AiProvider,
            StageType.Translation => QueueNames.AiProvider,
            StageType.VoiceAssignment => QueueNames.AiProvider,
            StageType.VoiceGeneration => QueueNames.AiGpu,
            StageType.TimingOptimization => QueueNames.AiProvider,
            StageType.TimelineAssembly => QueueNames.MediaRender,
            StageType.AudioMixing => QueueNames.MediaRender,
            StageType.QualityControl => QueueNames.AiProvider,
            StageType.Render => QueueNames.MediaRender,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown stage type."),
        };
    }
}
