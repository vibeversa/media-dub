using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Enums;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.LocalInference;

/// <summary>
/// Model registry for the local-inference sidecar (Task 043, optional).
/// Loads <c>LocalInference:Models[]</c> entries
/// <c>{id, version, artifactHash, deviceProfile (cpu|cuda:0), capability,
/// runtimeRequirements}</c> and validates hash (64 hex) + known capability +
/// device profile. <see cref="ResolveModel(string)"/> returns the first entry
/// matching the capability (case-insensitive); when <c>Models</c> is empty it
/// synthesizes an entry from the legacy single-model fields so core behavior
/// is unchanged when the flag is disabled. Invalid versions fail fast with
/// <c>PROVIDER_CONFIGURATION_ERROR</c> (no retry).
/// </summary>
public sealed class ModelRegistry
{
    private readonly LocalInferenceOptions _options;

    public ModelRegistry(IOptions<LocalInferenceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>
    /// All configured registry entries (may be empty).
    /// </summary>
    public IReadOnlyList<LocalInferenceModelOptions> Models => _options.Models;

    /// <summary>
    /// Resolves the registry entry for a capability name (case-insensitive).
    /// </summary>
    public LocalInferenceModelOptions ResolveModel(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (!Enum.TryParse<ProviderCapability>(capability.Trim(), ignoreCase: true, out var parsed))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"LocalInference:Models has unknown capability '{capability}'.");
        }

        return ResolveModel(parsed);
    }

    /// <summary>
    /// Resolves the registry entry for a capability.
    /// </summary>
    public LocalInferenceModelOptions ResolveModel(ProviderCapability capability)
    {
        var models = _options.Models ?? [];
        foreach (var entry in models)
        {
            if (entry is null)
            {
                continue;
            }

            if (string.Equals(entry.Capability?.Trim(), capability.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                ValidateEntry(entry);
                return entry;
            }
        }

        if (models.Count > 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"LocalInference:Models has no entry for capability '{capability}'.");
        }

        return SynthesizeLegacy(capability);
    }

    /// <summary>
    /// Validates one registry entry, throwing fail-fast on invalid version/hash.
    /// </summary>
    public static void ValidateEntry(LocalInferenceModelOptions entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "LocalInference:Models entry id must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(entry.Version))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"LocalInference:Models['{entry.Id}'] version must not be empty (invalid version, fail-fast).");
        }

        if (!LocalInferenceOptions.IsValidArtifactHash(entry.ArtifactHash))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"LocalInference:Models['{entry.Id}'] artifactHash must be 64 hex chars.");
        }

        if (!LocalInferenceOptions.IsValidDeviceProfile(entry.DeviceProfile))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"LocalInference:Models['{entry.Id}'] deviceProfile must be 'cpu', 'cuda', or 'cuda:N'.");
        }

        if (!Enum.TryParse<ProviderCapability>(entry.Capability, ignoreCase: true, out _))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"LocalInference:Models['{entry.Id}'] has unknown capability '{entry.Capability}'.");
        }
    }

    private LocalInferenceModelOptions SynthesizeLegacy(ProviderCapability capability)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelVersion))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "LocalInference:ModelVersion must not be empty (invalid version, fail-fast).");
        }

        var device = string.IsNullOrWhiteSpace(_options.Device) ? "cpu" : _options.Device.Trim();
        if (!LocalInferenceOptions.IsValidDeviceProfile(device))
        {
            device = "cpu";
        }

        return new LocalInferenceModelOptions
        {
            Id = _options.ModelName,
            Version = _options.ModelVersion,
            ArtifactHash = _options.ModelHash ?? string.Empty,
            DeviceProfile = device,
            Capability = capability.ToString(),
            RuntimeRequirements = string.Empty,
        };
    }
}
