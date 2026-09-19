using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Processes;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// Health-check registration. Liveness (<c>live</c> tag) is process-local only
/// and never depends on PostgreSQL, RabbitMQ, Redis, storage, providers, or
/// FFmpeg. Readiness (<c>ready</c> tag) reflects dependencies: PostgreSQL and
/// storage always; RabbitMQ/Redis only when
/// <c>Transport:Provider=RabbitMq</c> (full profile, skipped for fast/InMemory);
/// FFmpeg only for media worker roles; provider config only for AI roles;
/// local-inference warmup only for the GPU role (Healthy when
/// <c>Features:LocalInferenceEnabled=false</c>, the default).
/// </summary>
public static class HealthRegistration
{
    public const string WorkerRoleEnvironmentVariable = "DOTNET_WORKER_ROLE";

    /// <summary>
    /// Adds liveness + readiness checks for the API host.
    /// </summary>
    public static void AddApiHealthChecks(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = services.AddHealthChecks();
        builder.AddCheck<LiveCheck>(LiveCheck.Name, tags: ["live"]);

        var connectionString = configuration.GetConnectionString("Default") ?? string.Empty;
        builder.AddCheck(NpgSqlHealthCheck.Name, new NpgSqlHealthCheck(connectionString), tags: ["ready"]);
        builder.AddCheck<S3HealthCheck>(S3HealthCheck.Name, tags: ["ready"]);

        if (IsFullProfile(configuration))
        {
            var (host, port) = RabbitEndpoint(configuration);
            builder.AddCheck(RabbitMqHealthCheck.Name, new RabbitMqHealthCheck(host, port), tags: ["ready"]);
            builder.AddCheck(RedisHealthCheck.Name, new RedisHealthCheck(RedisConnection(configuration)), tags: ["ready"]);
        }

        services.AddSingleton<ProcessRunner>();
    }

    /// <summary>
    /// Adds liveness + readiness checks for worker hosts, including role-specific
    /// readiness (FFmpeg for media roles, provider config for AI roles, sidecar
    /// warmup for the GPU role).
    /// </summary>
    public static void AddWorkerHealthChecks(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = services.AddHealthChecks();
        builder.AddCheck<LiveCheck>(LiveCheck.Name, tags: ["live"]);

        var connectionString = configuration.GetConnectionString("Default") ?? string.Empty;
        builder.AddCheck(NpgSqlHealthCheck.Name, new NpgSqlHealthCheck(connectionString), tags: ["ready"]);
        builder.AddCheck<S3HealthCheck>(S3HealthCheck.Name, tags: ["ready"]);

        if (IsFullProfile(configuration))
        {
            var (host, port) = RabbitEndpoint(configuration);
            builder.AddCheck(RabbitMqHealthCheck.Name, new RabbitMqHealthCheck(host, port), tags: ["ready"]);
            builder.AddCheck(RedisHealthCheck.Name, new RedisHealthCheck(RedisConnection(configuration)), tags: ["ready"]);
        }

        services.AddSingleton<ProcessRunner>();

        var role = (Environment.GetEnvironmentVariable(WorkerRoleEnvironmentVariable) ?? string.Empty).Trim();
        if (IsMediaRole(role))
        {
            builder.AddCheck<FfmpegVersionCheck>(FfmpegVersionCheck.Name, tags: ["ready"]);
        }

        if (IsAiRole(role))
        {
            builder.AddCheck<ProviderConfigCheck>(ProviderConfigCheck.Name, tags: ["ready"]);
        }

        if (IsGpuRole(role))
        {
            builder.AddCheck<LocalInferenceWarmupCheck>(LocalInferenceWarmupCheck.Name, tags: ["ready"]);
        }
    }

    public static bool IsFullProfile(IConfiguration configuration)
    {
        var provider = configuration["Transport:Provider"]
            ?? configuration["Messaging:Transport"]
            ?? TransportOptions.InMemory;
        return string.Equals(provider, TransportOptions.RabbitMq, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMediaRole(string role)
    {
        return role.StartsWith("media-", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAiRole(string role)
    {
        return string.Equals(role, "ai", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "gpu", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGpuRole(string role)
    {
        return string.Equals(role, "gpu", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Host, int Port) RabbitEndpoint(IConfiguration configuration)
    {
        var host = configuration["RabbitMq:Host"] ?? "localhost";
        var portText = configuration["RabbitMq:Port"];
        return int.TryParse(portText, CultureInfo.InvariantCulture, out var port) ? (host, port) : (host, 5672);
    }

    private static string RedisConnection(IConfiguration configuration)
    {
        return configuration["Redis:Connection"] ?? configuration.GetConnectionString("Redis") ?? string.Empty;
    }
}
