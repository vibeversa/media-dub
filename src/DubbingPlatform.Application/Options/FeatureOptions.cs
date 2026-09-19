using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Feature flags for optional capabilities. Binds to the <c>Features</c> section.
/// Optional capabilities stay disabled by default.
/// </summary>
public sealed class FeatureOptions
{
    public const string SectionName = "Features";

    public bool VideoIntelligenceEnabled { get; set; } = false;

    public bool LipSyncEnabled { get; set; } = false;

    public bool LocalInferenceEnabled { get; set; } = false;
}

/// <summary>
/// Fail-fast startup validation for <see cref="FeatureOptions"/>.
/// Flags are independent booleans, so defaults always pass.
/// </summary>
public sealed class FeatureOptionsValidator : IValidateOptions<FeatureOptions>
{
    public ValidateOptionsResult Validate(string? name, FeatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ValidateOptionsResult.Success;
    }
}
