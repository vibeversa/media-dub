using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Infrastructure.Processes;
using DubbingPlatform.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DubbingPlatform.Infrastructure.Media;

/// <summary>
/// Media inspection, ingestion, preparation, separation, VAD, segmentation,
/// diarization, transcription, context-build, translation, voice-assignment,
/// consent, TTS, timing, timeline, mixing, quality-control, rendering, and
/// retention wiring. Registers the ffprobe
/// and ffmpeg services, the FFmpeg mixer, the storage quota gate, the
/// disk-space guard, the process-wide FFmpeg concurrency gate, and the
/// ingestion/analysis/preparation/separation/segmentation/diarization/transcription/context/translation/voice/tts/timing/timeline/qc/render
/// orchestrators. Called by the worker host (media work runs on
/// <c>media.preparation</c>, diarization, transcription, context build, translation,
/// voice assignment, timing optimization, and quality control on
/// <c>ai.provider</c>, TTS generation on <c>ai.gpu</c>, timeline assembly,
/// audio mixing, and final rendering on <c>media.render</c>); the API host does not ingest or prepare.
/// </summary>
public static class MediaRegistration
{
    public static void AddDubbingMedia(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ProcessRunner>();
        services.AddSingleton<IFFprobeService, FFprobeService>();
        services.AddSingleton<IFFmpegService, FFmpegService>();
        services.AddSingleton<IDiskSpaceChecker, DiskSpaceChecker>();
        services.AddSingleton<MediaJobGate>();
        services.AddSingleton<FFmpegMixer>();
        services.AddScoped<CostService>();
        services.AddScoped<QuotaService>();
        services.AddScoped<IQuotaGate>(provider => provider.GetRequiredService<QuotaService>());
        services.AddScoped<MediaIngestionService>();
        services.AddScoped<MediaAnalysisService>();
        services.AddScoped<AudioPreparationService>();
        services.AddScoped<SourceSeparationService>();
        services.AddScoped<SegmentBuilderService>();
        services.AddScoped<DiarizationService>();
        services.AddScoped<TranscriptionService>();
        services.AddScoped<ContextBuilderService>();
        services.AddScoped<TranslationService>();
        services.AddScoped<AuditService>();
        services.AddScoped<ConsentService>();
        services.AddScoped<VoiceAssignmentService>();
        services.AddScoped<TtsService>();
        services.AddScoped<TimingOptimizationService>();
        services.AddScoped<TimelineAssemblyService>();
        services.AddScoped<QualityControlService>();
        services.AddScoped<ExportService>();
        services.AddScoped<ProgressService>();
        services.AddScoped<CancellationService>();
        services.AddScoped<RetryService>();
        services.AddScoped<ReviewService>();
        services.AddScoped<RetentionService>();
        services.AddSingleton<RenderService>();
    }
}
