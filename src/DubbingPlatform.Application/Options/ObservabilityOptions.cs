using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Observability configuration. Binds to the <c>Observability</c> section.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    [Required]
    [MinLength(1)]
    [MaxLength(128)]
    public string ServiceName { get; set; } = "dubbing-platform";

    [MaxLength(2048)]
    public string OtlpEndpoint { get; set; } = string.Empty;

    public bool EnableTracing { get; set; } = true;

    public bool EnableMetrics { get; set; } = true;
}

/// <summary>
/// Fail-fast startup validation for <see cref="ObservabilityOptions"/>.
/// </summary>
public sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            return ValidateOptionsResult.Fail($"{nameof(ObservabilityOptions)}.{nameof(ObservabilityOptions.ServiceName)} must not be empty.");
        }

        if (options.ServiceName.Length > 128)
        {
            return ValidateOptionsResult.Fail($"{nameof(ObservabilityOptions)}.{nameof(ObservabilityOptions.ServiceName)} must be at most 128 characters.");
        }

        if (!string.IsNullOrEmpty(options.OtlpEndpoint) &&
            !Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail($"{nameof(ObservabilityOptions)}.{nameof(ObservabilityOptions.OtlpEndpoint)} must be an absolute URI when set.");
        }

        return ValidateOptionsResult.Success;
    }
}
