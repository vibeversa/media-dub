using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Tenant quota limits. Binds to the <c>Quota</c> section.
/// </summary>
public sealed class QuotaOptions
{
    public const string SectionName = "Quota";

    [Range(1, 100000)]
    public int MaxActiveProjects { get; set; } = 10;

    [Range(1, 100000)]
    public int MaxProjectsPerDay { get; set; } = 50;

    [Range(0.01, 1000000.0)]
    public double MaxCostPerProject { get; set; } = 50.0;

    [Range(0.01, 1000000.0)]
    public double MaxCostPerSegment { get; set; } = 2.0;

    [Range(1, 1000000)]
    public int MaxSegmentCount { get; set; } = 2000;

    [Range(typeof(long), "1", "9223372036854775807")]
    public long MaxStorageBytes { get; set; } = 107374182400;

    [Range(1, 10000)]
    public int MaxConcurrentStagesPerTenant { get; set; } = 20;
}

/// <summary>
/// Fail-fast startup validation for <see cref="QuotaOptions"/>.
/// </summary>
public sealed class QuotaOptionsValidator : IValidateOptions<QuotaOptions>
{
    public ValidateOptionsResult Validate(string? name, QuotaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxActiveProjects < 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(QuotaOptions)}.{nameof(QuotaOptions.MaxActiveProjects)} must be at least 1.");
        }

        if (options.MaxProjectsPerDay < 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(QuotaOptions)}.{nameof(QuotaOptions.MaxProjectsPerDay)} must be at least 1.");
        }

        if (double.IsNaN(options.MaxCostPerProject) || options.MaxCostPerProject <= 0.0)
        {
            return ValidateOptionsResult.Fail($"{nameof(QuotaOptions)}.{nameof(QuotaOptions.MaxCostPerProject)} must be positive.");
        }

        if (double.IsNaN(options.MaxCostPerSegment) || options.MaxCostPerSegment <= 0.0)
        {
            return ValidateOptionsResult.Fail($"{nameof(QuotaOptions)}.{nameof(QuotaOptions.MaxCostPerSegment)} must be positive.");
        }

        if (options.MaxCostPerSegment > options.MaxCostPerProject)
        {
            return ValidateOptionsResult.Fail($"{nameof(QuotaOptions)}.{nameof(QuotaOptions.MaxCostPerSegment)} must not exceed {nameof(QuotaOptions)}.{nameof(QuotaOptions.MaxCostPerProject)}.");
        }

        if (options.MaxSegmentCount < 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(QuotaOptions)}.{nameof(QuotaOptions.MaxSegmentCount)} must be at least 1.");
        }

        if (options.MaxStorageBytes < 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(QuotaOptions)}.{nameof(QuotaOptions.MaxStorageBytes)} must be at least 1.");
        }

        if (options.MaxConcurrentStagesPerTenant < 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(QuotaOptions)}.{nameof(QuotaOptions.MaxConcurrentStagesPerTenant)} must be at least 1.");
        }

        return ValidateOptionsResult.Success;
    }
}
