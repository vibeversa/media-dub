using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Data-retention windows in days. Binds to the <c>Retention</c> section.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    [Range(1, 3650)]
    public int IntermediateDays { get; set; } = 30;

    [Range(1, 3650)]
    public int FinalDays { get; set; } = 90;

    [Range(1, 3650)]
    public int AuditDays { get; set; } = 365;
}

/// <summary>
/// Fail-fast startup validation for <see cref="RetentionOptions"/>.
/// </summary>
public sealed class RetentionOptionsValidator : IValidateOptions<RetentionOptions>
{
    public ValidateOptionsResult Validate(string? name, RetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.IntermediateDays < 1 || options.IntermediateDays > 3650)
        {
            return ValidateOptionsResult.Fail($"{nameof(RetentionOptions)}.{nameof(RetentionOptions.IntermediateDays)} must be in 1..3650.");
        }

        if (options.FinalDays < options.IntermediateDays)
        {
            return ValidateOptionsResult.Fail($"{nameof(RetentionOptions)}.{nameof(RetentionOptions.FinalDays)} must be at least {nameof(RetentionOptions)}.{nameof(RetentionOptions.IntermediateDays)}.");
        }

        if (options.AuditDays < options.FinalDays)
        {
            return ValidateOptionsResult.Fail($"{nameof(RetentionOptions)}.{nameof(RetentionOptions.AuditDays)} must be at least {nameof(RetentionOptions)}.{nameof(RetentionOptions.FinalDays)}.");
        }

        return ValidateOptionsResult.Success;
    }
}
