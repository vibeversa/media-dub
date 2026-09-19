using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Deployment identity. Binds to the <c>Deployment</c> section.
/// </summary>
public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    [Required]
    [MinLength(1)]
    [MaxLength(64)]
    public string Environment { get; set; } = "Development";

    [Required]
    [MinLength(1)]
    [MaxLength(64)]
    public string Region { get; set; } = "local";
}

/// <summary>
/// Fail-fast startup validation for <see cref="DeploymentOptions"/>.
/// </summary>
public sealed class DeploymentOptionsValidator : IValidateOptions<DeploymentOptions>
{
    public ValidateOptionsResult Validate(string? name, DeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Environment))
        {
            return ValidateOptionsResult.Fail($"{nameof(DeploymentOptions)}.{nameof(DeploymentOptions.Environment)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.Region))
        {
            return ValidateOptionsResult.Fail($"{nameof(DeploymentOptions)}.{nameof(DeploymentOptions.Region)} must not be empty.");
        }

        return ValidateOptionsResult.Success;
    }
}
