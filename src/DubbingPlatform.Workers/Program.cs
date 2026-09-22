using DubbingPlatform.Application.Diagnostics;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Infrastructure.Diagnostics;
using DubbingPlatform.Infrastructure.Health;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Observability;
using DubbingPlatform.Infrastructure.Orchestration;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using DubbingPlatform.Infrastructure.Previews;
using DubbingPlatform.Infrastructure.Providers;
using DubbingPlatform.Infrastructure.Storage;
using DubbingPlatform.Workers.Services;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, loggerConfig) =>
{
    ObservabilitySetup.ConfigureLogger(loggerConfig);
});

builder.Services.AddOptions<ObservabilityOptions>()
    .BindConfiguration(ObservabilityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<StorageOptions>()
    .BindConfiguration(StorageOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ProviderOptions>()
    .BindConfiguration(ProviderOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<TransportOptions>()
    .BindConfiguration(TransportOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<MediaOptions>()
    .BindConfiguration(MediaOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<RetryOptions>()
    .BindConfiguration(RetryOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<TimingOptions>()
    .BindConfiguration(TimingOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<QuotaOptions>()
    .BindConfiguration(QuotaOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<PreviewOptions>()
    .BindConfiguration(PreviewOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DiagnosticsOptions>()
    .BindConfiguration(DiagnosticsOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SegmentOptions>()
    .BindConfiguration(SegmentOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DiarizationOptions>()
    .BindConfiguration(DiarizationOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<TranscriptionOptions>()
    .BindConfiguration(TranscriptionOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ContextOptions>()
    .BindConfiguration(ContextOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<TranslationOptions>()
    .BindConfiguration(TranslationOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<VoiceOptions>()
    .BindConfiguration(VoiceOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<TtsOptions>()
    .BindConfiguration(TtsOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<MixingOptions>()
    .BindConfiguration(MixingOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<QcOptions>()
    .BindConfiguration(QcOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<RateLimitOptions>()
    .BindConfiguration(RateLimitOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<PrivacyOptions>()
    .BindConfiguration(PrivacyOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<FeatureOptions>()
    .BindConfiguration(FeatureOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DeploymentOptions>()
    .BindConfiguration(DeploymentOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<AuthOptions>()
    .BindConfiguration(AuthOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<RetentionOptions>()
    .BindConfiguration(RetentionOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SecurityOptions>()
    .BindConfiguration(SecurityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<MockBehaviorOptions>()
    .BindConfiguration(MockBehaviorOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<AzureProviderOptions>()
    .BindConfiguration(AzureProviderOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<OpenAiProviderOptions>()
    .BindConfiguration(OpenAiProviderOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<GoogleProviderOptions>()
    .BindConfiguration(GoogleProviderOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<LocalInferenceOptions>()
    .BindConfiguration(LocalInferenceOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ProviderOptions>, ProviderOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<TransportOptions>, TransportOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<MediaOptions>, MediaOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<RetryOptions>, RetryOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<TimingOptions>, TimingOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<QuotaOptions>, QuotaOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<PreviewOptions>, PreviewOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<DiagnosticsOptions>, DiagnosticsOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<SegmentOptions>, SegmentOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<DiarizationOptions>, DiarizationOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<TranscriptionOptions>, TranscriptionOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ContextOptions>, ContextOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<TranslationOptions>, TranslationOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<VoiceOptions>, VoiceOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<TtsOptions>, TtsOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<MixingOptions>, MixingOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<QcOptions>, QcOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<PrivacyOptions>, PrivacyOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<FeatureOptions>, FeatureOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<DeploymentOptions>, DeploymentOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<RetentionOptions>, RetentionOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<MockBehaviorOptions>, MockBehaviorOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<AzureProviderOptions>, AzureProviderOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<OpenAiProviderOptions>, OpenAiProviderOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<GoogleProviderOptions>, GoogleProviderOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<LocalInferenceOptions>, LocalInferenceOptionsValidator>();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=dubbing;Username=dubbing;Password=dubbing";
builder.Services.AddDbContextFactory<AppDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(new TenantSessionInterceptor())
    .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>());
builder.Services.AddScoped<IStageExecutionContextFactory, StageExecutionContextFactory>();
builder.Services.AddScoped<StageExecutionService>();
builder.Services.AddScoped<DubbingPlatform.Application.Notifications.NotificationProjector>();
builder.Services.AddScoped<DubbingPlatform.Application.Activity.ActivityProjector>();
StorageRegistration.AddDubbingStorage(builder.Services, builder.Configuration);
DubbingPlatform.Infrastructure.Media.MediaRegistration.AddDubbingMedia(builder.Services);
OrchestrationRegistration.AddDubbingOrchestration(builder.Services);
ProviderRegistration.AddDubbingProviders(builder.Services, builder.Configuration);
PreviewRegistration.AddDubbingPreviews(builder.Services);
DiagnosticsRegistration.AddDubbingDiagnostics(builder.Services);
builder.Services.AddHostedService<WorkerRecoverySweeper>();
builder.Services.AddHostedService<OrphanObjectReconciler>();
builder.Services.AddHostedService<RetentionSweeper>();
MassTransitConfig.AddDubbingMassTransit(
    builder.Services,
    builder.Configuration,
    includeOrchestration: true,
    configureExtra: x =>
    {
        x.AddConsumer<DubbingPlatform.Workers.Consumers.MediaIngestionWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.MediaAnalyzerWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.AudioPreparationWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.SourceSeparationWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.VadWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.SegmentBuilderWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.DiarizationWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.TranscriptionWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.ContextBuilderWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.TranslationWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.VoiceAssignmentWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.VoiceGenerationWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.TimingOptimizationWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.TimelineAssemblerWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.AudioMixerWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.QualityControlWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.RenderWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.VideoIntelligenceWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.LipSyncWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.ExportWorker>();
        x.AddConsumer<DubbingPlatform.Workers.Consumers.DeletionJobWorker>();
        x.AddConsumer<DubbingPlatform.Infrastructure.Messaging.NotificationActivityProjectionConsumer>();
    });

ObservabilitySetup.AddDubbingOpenTelemetry(builder, includeAspNetCore: false);
HealthRegistration.AddWorkerHealthChecks(builder.Services, builder.Configuration);

var host = builder.Build();
host.Run();
