using DubbingPlatform.Application.Options;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace DubbingPlatform.Infrastructure.Observability;

/// <summary>
/// Shared Serilog + OpenTelemetry wiring for API and worker hosts.
/// Serilog writes JSON to the console, enriches from <c>LogContext</c> (so
/// CorrelationId, TenantId, ProjectId, ProcessingRunId, StageType, ScopeType,
/// ScopeId, SegmentId, Attempt, Provider, Model, LeaseToken appear when present),
/// and redacts secret values. Minimum level is Information with
/// Microsoft.EntityFrameworkCore at Warning. OpenTelemetry traces ASP.NET Core
/// (when available), HttpClient, EF Core, MassTransit, and DubbingPlatform
/// sources; metrics cover ASP.NET Core, HttpClient, runtime, process, plus all
/// DubbingPlatform meters (<c>dubbing-platform</c> SLO facade and every
/// <c>DubbingPlatform.*</c> domain meter). Missing OTLP endpoint falls back to
/// the local console/Prometheus exporters without crashing.
/// OTLP is enabled only when <c>Observability:OtlpEndpoint</c> is set;
/// Prometheus scraping (<c>/metrics</c>) is mapped by the API host.
/// </summary>
public static class ObservabilitySetup
{
    /// <summary>
    /// Applies the shared Serilog settings to <paramref name="config"/>.
    /// </summary>
    public static LoggerConfiguration ConfigureLogger(LoggerConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.With<SecretRedactingEnricher>()
            .Destructure.With(new SecretDestructuringPolicy())
            .WriteTo.Console(new RedactingJsonFormatter());
    }

    /// <summary>
    /// Builds the shared Serilog logger from configuration.
    /// </summary>
    public static LoggerConfiguration BuildLoggerConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return ConfigureLogger(new LoggerConfiguration());
    }

    /// <summary>
    /// Configures OpenTelemetry tracing + metrics on the host builder.
    /// </summary>
    /// <param name="builder">Host application builder (API or worker).</param>
    /// <param name="includeAspNetCore">Include ASP.NET Core instrumentation (API only).</param>
    public static void AddDubbingOpenTelemetry(IHostApplicationBuilder builder, bool includeAspNetCore)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var observability = builder.Configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        var otlpEndpoint = observability.OtlpEndpoint?.Trim();
        var hasOtlp = !string.IsNullOrEmpty(otlpEndpoint);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(observability.ServiceName))
            .WithTracing(tracing =>
            {
                if (includeAspNetCore)
                {
                    tracing.AddAspNetCoreInstrumentation();
                }

                tracing
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddSource("MassTransit")
                    .AddSource("DubbingPlatform.Orchestration")
                    .AddSource("DubbingPlatform.Media")
                    .AddSource("DubbingPlatform.Providers")
                    .AddSource("DubbingPlatform.FFmpeg");

                if (observability.EnableTracing && hasOtlp)
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint!));
                }
            })
            .WithMetrics(metrics =>
            {
                if (includeAspNetCore)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }

                metrics
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation()
                    .AddMeter(
                        PlatformMetrics.MeterName,
                        "DubbingPlatform.Messaging",
                        "DubbingPlatform.Storage",
                        "DubbingPlatform.Security",
                        "DubbingPlatform.Quota",
                        "DubbingPlatform.RateLimit",
                        "DubbingPlatform.Cost",
                        "DubbingPlatform.Providers",
                        "DubbingPlatform.Tts",
                        "DubbingPlatform.Translation",
                        "DubbingPlatform.Timing");

                if (observability.EnableMetrics && hasOtlp)
                {
                    metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint!));
                }

                if (includeAspNetCore && observability.EnableMetrics)
                {
                    metrics.AddPrometheusExporter();
                }
            });
    }
}
