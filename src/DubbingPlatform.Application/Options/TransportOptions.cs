using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Messaging transport selection. Binds to the <c>Transport</c> section.
/// Fast local profile uses <c>InMemory</c> (no RabbitMQ/Redis required);
/// full profile uses <c>RabbitMq</c>.
/// </summary>
public sealed class TransportOptions
{
    public const string SectionName = "Transport";

    public const string InMemory = "InMemory";

    public const string RabbitMq = "RabbitMq";

    [Required]
    [MinLength(1)]
    [MaxLength(32)]
    public string Provider { get; set; } = InMemory;
}

/// <summary>
/// Fail-fast startup validation for <see cref="TransportOptions"/>.
/// </summary>
public sealed class TransportOptionsValidator : IValidateOptions<TransportOptions>
{
    public ValidateOptionsResult Validate(string? name, TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Provider))
        {
            return ValidateOptionsResult.Fail($"{nameof(TransportOptions)}.{nameof(TransportOptions.Provider)} must not be empty.");
        }

        if (!string.Equals(options.Provider, TransportOptions.InMemory, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Provider, TransportOptions.RabbitMq, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail($"{nameof(TransportOptions)}.{nameof(TransportOptions.Provider)} must be '{TransportOptions.InMemory}' or '{TransportOptions.RabbitMq}'.");
        }

        return ValidateOptionsResult.Success;
    }
}
