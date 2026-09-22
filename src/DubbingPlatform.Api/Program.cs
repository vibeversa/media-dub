using DubbingPlatform.Api.Auth;
using DubbingPlatform.Api.Filters;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.OpenApi;
using DubbingPlatform.Application.Auth;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Diagnostics;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Infrastructure.Diagnostics;
using DubbingPlatform.Infrastructure.Health;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Observability;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using DubbingPlatform.Infrastructure.Previews;
using DubbingPlatform.Infrastructure.Providers;
using DubbingPlatform.Infrastructure.Storage;
using EFCore.NamingConventions;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, loggerConfig) =>
{
    ObservabilitySetup.ConfigureLogger(loggerConfig);
});

builder.Services.AddOptions<ObservabilityOptions>()
    .BindConfiguration(ObservabilityOptions.SectionName)
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
builder.Services.AddOptions<StorageOptions>()
    .BindConfiguration(StorageOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ProviderOptions>()
    .BindConfiguration(ProviderOptions.SectionName)
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
builder.Services.AddOptions<AuthRateLimitOptions>()
    .BindConfiguration(AuthRateLimitOptions.SectionName)
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
builder.Services.AddOptions<TransportOptions>()
    .BindConfiguration(TransportOptions.SectionName)
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
builder.Services.AddSingleton<IValidateOptions<MediaOptions>, MediaOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<RetryOptions>, RetryOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<TimingOptions>, TimingOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ProviderOptions>, ProviderOptionsValidator>();
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
builder.Services.AddSingleton<IValidateOptions<AuthRateLimitOptions>, AuthRateLimitOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<RetentionOptions>, RetentionOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<TransportOptions>, TransportOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<MockBehaviorOptions>, MockBehaviorOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<AzureProviderOptions>, AzureProviderOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<OpenAiProviderOptions>, OpenAiProviderOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<GoogleProviderOptions>, GoogleProviderOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<LocalInferenceOptions>, LocalInferenceOptionsValidator>();

builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssembly(typeof(DubbingPlatform.Application.Placeholder).Assembly);
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IdempotencyService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<IPermissionResolver, PermissionResolver>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<DubbingPlatform.Application.Projects.ProjectSettingsGuard>();
builder.Services.AddScoped<DubbingPlatform.Application.Dashboard.DashboardService>();
builder.Services.AddScoped<UploadService>();
builder.Services.AddScoped<ProcessingStartService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<ProgressService>();
builder.Services.AddScoped<CancellationService>();
builder.Services.AddScoped<RetryService>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<DubbingPlatform.Application.Segments.SegmentSelectionService>();
builder.Services.AddScoped<DubbingPlatform.Application.Segments.ISegmentSelectionEventPublisher, DubbingPlatform.Infrastructure.Messaging.MassTransitSegmentSelectionEventPublisher>();
builder.Services.AddScoped<CostService>();
builder.Services.AddScoped<QuotaService>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddScoped<DubbingPlatform.Application.Processing.ProcessingIdempotency>();
builder.Services.AddScoped<DubbingPlatform.Application.Workspace.WorkspaceService>();
builder.Services.AddScoped<DubbingPlatform.Application.Notifications.NotificationProjector>();
builder.Services.AddScoped<DubbingPlatform.Application.Activity.ActivityProjector>();
builder.Services.AddScoped<DubbingPlatform.Application.Abstractions.IQuotaGate>(provider => provider.GetRequiredService<QuotaService>());
builder.Services.AddSingleton<DubbingPlatform.Infrastructure.Redis.RateLimiter>(provider => new DubbingPlatform.Infrastructure.Redis.RateLimiter(
    provider.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
    provider.GetRequiredService<IOptions<RateLimitOptions>>(),
    provider.GetRequiredService<ILogger<DubbingPlatform.Infrastructure.Redis.RateLimiter>>()));
builder.Services.AddSingleton<DubbingPlatform.Infrastructure.Redis.TenantFairnessGate>(provider => new DubbingPlatform.Infrastructure.Redis.TenantFairnessGate(
    provider.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
    provider.GetRequiredService<IOptions<QuotaOptions>>(),
    provider.GetRequiredService<IOptions<RateLimitOptions>>(),
    provider.GetRequiredService<IServiceScopeFactory>(),
    provider.GetRequiredService<DubbingPlatform.Infrastructure.Redis.RateLimiter>(),
    provider.GetRequiredService<ILogger<DubbingPlatform.Infrastructure.Redis.TenantFairnessGate>>()));
builder.Services.AddSingleton<DubbingPlatform.Infrastructure.Orchestration.ICostGate, DubbingPlatform.Infrastructure.Orchestration.CostGate>();
builder.Services.AddSingleton<DubbingPlatform.Infrastructure.Orchestration.IRateGate, DubbingPlatform.Infrastructure.Orchestration.RateGate>();
builder.Services.AddScoped<IdempotencyFilter>();
builder.Services.AddSingleton<DubbingPlatform.Application.Abstractions.IMultipartUploadClient, DubbingPlatform.Infrastructure.Storage.S3MultipartUploadClient>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
OpenApiConfiguration.AddDubbingOpenApi(builder.Services);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(
        DubbingPlatform.Api.Controllers.AuthController.LoginPolicy,
        context =>
        {
            var limits = context.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!limits.Enabled)
            {
                return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(ip);
            }

            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                ip,
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.LoginPerMinutePerIp,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                });
        });
    options.AddPolicy(
        DubbingPlatform.Api.Controllers.AuthController.RefreshPolicy,
        context =>
        {
            var limits = context.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;
            var subject = context.User.GetSubject();
            var key = string.IsNullOrWhiteSpace(subject) || string.Equals(subject, "unknown", StringComparison.Ordinal)
                ? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                : subject;
            if (!limits.Enabled)
            {
                return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(key);
            }

            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                key,
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.RefreshPerMinutePerUser,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                });
        });
});

var authConfig = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = string.IsNullOrWhiteSpace(authConfig.Authority) ? null : authConfig.Authority.Trim();
        o.Audience = authConfig.Audience;
        o.RequireHttpsMetadata = authConfig.RequireHttps;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(authConfig.Authority),
            ValidIssuer = authConfig.Authority,
            ValidateAudience = true,
            ValidAudience = authConfig.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = string.IsNullOrWhiteSpace(authConfig.SigningKey)
                ? null
                : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authConfig.SigningKey)),
            NameClaimType = "sub",
            RoleClaimType = "roles",
        };
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // SSE transport shell: EventSource cannot set Authorization
                // headers, so ?access_token= carries the same short-TTL bearer
                // for /stream paths only. The value is never logged.
                var path = context.HttpContext.Request.Path.Value ?? string.Empty;
                if (path.Contains("/stream", StringComparison.OrdinalIgnoreCase))
                {
                    var token = context.HttpContext.Request.Query["access_token"].ToString();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        context.Token = token.Trim();
                    }
                }

                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                context.HandleResponse();
                return AuthEnvelopeWriter.WriteUnauthorizedAsync(context.HttpContext, "Authentication is required.");
            },
            OnForbidden = context => AuthEnvelopeWriter.WriteForbiddenAsync(context.HttpContext, "The caller is not allowed to access this resource."),
        };
    });
builder.Services.AddAuthorization(AuthRegistration.AddPolicies);
builder.Services.AddScoped<IAuthorizationHandler, ProjectOwnershipHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, EnvelopeAuthorizationMiddlewareResultHandler>();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=dubbing;Username=dubbing;Password=dubbing";
builder.Services.AddDbContextFactory<AppDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(new TenantSessionInterceptor())
    .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>());
builder.Services.AddScoped<IStageExecutionContextFactory, StageExecutionContextFactory>();
builder.Services.AddScoped<StageExecutionService>();
StorageRegistration.AddDubbingStorage(builder.Services, builder.Configuration);
ProviderRegistration.AddDubbingProviders(builder.Services, builder.Configuration);
PreviewRegistration.AddDubbingPreviews(builder.Services);
DiagnosticsRegistration.AddDubbingDiagnostics(builder.Services);
MassTransitConfig.AddDubbingMassTransit(builder.Services, builder.Configuration);

ObservabilitySetup.AddDubbingOpenTelemetry(builder, includeAspNetCore: true);
HealthRegistration.AddApiHealthChecks(builder.Services, builder.Configuration);

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapOpenApi().AllowAnonymous();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live", StringComparer.Ordinal),
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready", StringComparer.Ordinal),
});
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();

public partial class Program
{
}
