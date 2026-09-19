using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// S3-compatible artifact storage settings. Binds to the <c>Storage</c> section.
/// Access and secret keys are marked <see cref="SecretAttribute"/> and must never
/// appear in logs, traces, hashes, or error responses.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required]
    [MinLength(1)]
    [MaxLength(512)]
    public string Endpoint { get; set; } = "localhost:9000";

    [Required]
    [MinLength(1)]
    [MaxLength(128)]
    public string Bucket { get; set; } = "dubbing";

    public bool UseSsl { get; set; } = false;

    [Secret]
    [MaxLength(512)]
    public string AccessKey { get; set; } = string.Empty;

    [Secret]
    [MaxLength(512)]
    public string SecretKey { get; set; } = string.Empty;

    [MaxLength(512)]
    public string KeyPrefix { get; set; } = string.Empty;
}

/// <summary>
/// Fail-fast startup validation for <see cref="StorageOptions"/>.
/// </summary>
public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return ValidateOptionsResult.Fail($"{nameof(StorageOptions)}.{nameof(StorageOptions.Endpoint)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.Bucket))
        {
            return ValidateOptionsResult.Fail($"{nameof(StorageOptions)}.{nameof(StorageOptions.Bucket)} must not be empty.");
        }

        return ValidateOptionsResult.Success;
    }
}
