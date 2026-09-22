using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// Diagnostics read-layer budgets. Binds to the <c>Diagnostics</c> section.
/// <c>StaleLeaseTtlSeconds</c> is the lease-age cutoff for stale-lease
/// detection (matches the worker <c>StageLeaseTimeout</c> TTL of 5 minutes by
/// default); it is read from configuration, never hardcoded at call sites.
/// <c>KnownWorkers</c> is the explicit worker roster surfaced as
/// <c>Unknown</c> when no runtime lease state exists for them.
/// </summary>
public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>
    /// Lease age after which a running execution with an expired lease is
    /// reported stale. Seconds. Default 300 (5 minutes).
    /// </summary>
    [Range(1, 86400)]
    public int StaleLeaseTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Default orphan-artifact page size. Default 50.
    /// </summary>
    [Range(1, 200)]
    public int OrphanPageDefaultSize { get; set; } = 50;

    /// <summary>
    /// Maximum orphan-artifact page size. Default 200.
    /// </summary>
    [Range(1, 1000)]
    public int OrphanPageMaxSize { get; set; } = 200;

    /// <summary>
    /// Maximum provider-execution rows sampled per provider-health call.
    /// Default 500.
    /// </summary>
    [Range(10, 10000)]
    public int MaxProviderExecutionSample { get; set; } = 500;

    /// <summary>
    /// Explicit worker roster. Roster entries with no runtime lease state are
    /// reported <c>Unknown</c> with a null heartbeat.
    /// </summary>
    public string[] KnownWorkers { get; set; } = [];

    /// <summary>
    /// Gets the stale-lease TTL as a <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan StaleLeaseTtl => TimeSpan.FromSeconds(StaleLeaseTtlSeconds);
}

/// <summary>
/// Fail-fast startup validation for <see cref="DiagnosticsOptions"/>.
/// </summary>
public sealed class DiagnosticsOptionsValidator : IValidateOptions<DiagnosticsOptions>
{
    public ValidateOptionsResult Validate(string? name, DiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.StaleLeaseTtlSeconds < 1 || options.StaleLeaseTtlSeconds > 86400)
        {
            return ValidateOptionsResult.Fail($"{nameof(DiagnosticsOptions)}.{nameof(DiagnosticsOptions.StaleLeaseTtlSeconds)} must be in 1..86400.");
        }

        if (options.OrphanPageDefaultSize < 1 || options.OrphanPageDefaultSize > 200)
        {
            return ValidateOptionsResult.Fail($"{nameof(DiagnosticsOptions)}.{nameof(DiagnosticsOptions.OrphanPageDefaultSize)} must be in 1..200.");
        }

        if (options.OrphanPageMaxSize < 1 || options.OrphanPageMaxSize > 1000)
        {
            return ValidateOptionsResult.Fail($"{nameof(DiagnosticsOptions)}.{nameof(DiagnosticsOptions.OrphanPageMaxSize)} must be in 1..1000.");
        }

        if (options.OrphanPageDefaultSize > options.OrphanPageMaxSize)
        {
            return ValidateOptionsResult.Fail($"{nameof(DiagnosticsOptions)}.{nameof(DiagnosticsOptions.OrphanPageDefaultSize)} must not exceed {nameof(DiagnosticsOptions)}.{nameof(DiagnosticsOptions.OrphanPageMaxSize)}.");
        }

        if (options.MaxProviderExecutionSample < 10 || options.MaxProviderExecutionSample > 10000)
        {
            return ValidateOptionsResult.Fail($"{nameof(DiagnosticsOptions)}.{nameof(DiagnosticsOptions.MaxProviderExecutionSample)} must be in 10..10000.");
        }

        if (options.KnownWorkers is null)
        {
            return ValidateOptionsResult.Fail($"{nameof(DiagnosticsOptions)}.{nameof(DiagnosticsOptions.KnownWorkers)} must not be null.");
        }

        foreach (var worker in options.KnownWorkers)
        {
            if (string.IsNullOrWhiteSpace(worker))
            {
                return ValidateOptionsResult.Fail($"{nameof(DiagnosticsOptions)}.{nameof(DiagnosticsOptions.KnownWorkers)} must not contain empty worker names.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
