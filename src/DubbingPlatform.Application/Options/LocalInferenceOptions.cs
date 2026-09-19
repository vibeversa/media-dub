using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Local-inference sidecar settings. Binds to the <c>LocalInference</c> section.
/// Covers the model registry entry (id/version/hash), device profile,
/// concurrency policy, warmup state, and optional mTLS. The sidecar is HTTP by
/// default; <c>Protocol=grpc</c> is accepted for forward-compat (043 owns true
/// gRPC) and uses the same HTTP mapping with a grpc marker header. When
/// <c>RequireMtls</c> is true the endpoint must be https and a client
/// certificate is presented from <c>ClientCertificatePath</c>.
/// </summary>
/// <summary>
/// One registry model entry. Binds to <c>LocalInference:Models[]</c>.
/// </summary>
public sealed class LocalInferenceModelOptions
{
    [MaxLength(128)]
    public string Id { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Version { get; set; } = "1";

    [MaxLength(128)]
    public string ArtifactHash { get; set; } = string.Empty;

    [MaxLength(64)]
    public string DeviceProfile { get; set; } = "cpu";

    [MaxLength(64)]
    public string Capability { get; set; } = "LocalInference";

    [MaxLength(1024)]
    public string RuntimeRequirements { get; set; } = string.Empty;
}

/// <summary>
/// Local-inference sidecar settings. Binds to the <c>LocalInference</c> section.
/// Covers the model registry entry (id/version/hash), device profile,
/// concurrency policy, warmup state, and optional mTLS. The sidecar is HTTP by
/// default; <c>Protocol=grpc</c> is accepted for forward-compat (043 owns true
/// gRPC) and uses the same HTTP mapping with a grpc marker header. When
/// <c>RequireMtls</c> is true the endpoint must be https and a client
/// certificate is presented from <c>ClientCertificatePath</c>.
/// <c>Models</c> is the multi-model registry (043); when empty the legacy
/// single-model fields (<c>ModelName/ModelVersion/ModelHash/Device</c>) are
/// used. GPU workers set <c>MaxConcurrency=1</c> (CPU default 2).
/// Local-only routing reuses <c>ExternalProvidersAllowed=false</c> +
/// <c>LocalInferenceAllowed=true</c> (no new policy column).
/// </summary>
public sealed class LocalInferenceOptions
{
    public const string SectionName = "LocalInference";

    [MaxLength(2048)]
    public string Endpoint { get; set; } = "http://localhost:8081";

    [MaxLength(16)]
    public string Protocol { get; set; } = "http";

    [MaxLength(128)]
    public string ModelName { get; set; } = "local-small";

    [MaxLength(64)]
    public string ModelVersion { get; set; } = "1";

    [MaxLength(128)]
    public string ModelHash { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Device { get; set; } = "cpu";

    [Range(1, 16)]
    public int MaxConcurrency { get; set; } = 2;

    [Range(1, 300)]
    public int WarmupTimeoutSec { get; set; } = 30;

    public bool RequireMtls { get; set; }

    [MaxLength(1024)]
    public string ClientCertificatePath { get; set; } = string.Empty;

    [Secret]
    [MaxLength(512)]
    public string ClientCertificatePassword { get; set; } = string.Empty;

    /// <summary>
    /// Multi-model registry. Empty means the legacy single-model fields apply.
    /// </summary>
    public List<LocalInferenceModelOptions> Models { get; set; } = [];

    /// <summary>
    /// Whether the sidecar endpoint is allowed. HTTPS anywhere, HTTP loopback,
    /// or plain HTTP to the in-cluster sidecar service
    /// (<c>http://local-inference:8000</c> and <c>*.svc*</c> /
    /// <c>*.cluster.local</c> cluster DNS). Public-internet cleartext stays
    /// rejected.
    /// </summary>
    public static bool IsSidecarEndpointAllowed(string? url)
    {
        if (ProviderEndpointValidator.IsAllowed(url))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host.Trim().Trim('[', ']');
        if (string.Equals(host, "local-inference", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".svc.cluster.local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".cluster.local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Trims and validates the sidecar endpoint for adapter use.
    /// </summary>
    public static string RequireSidecarEndpoint(string? url, string property)
    {
        if (!IsSidecarEndpointAllowed(url))
        {
            throw new ArgumentException(
                $"BaseUrl '{url}' for {property} is not allowed. Use https, http localhost, or http://local-inference:8000 for the in-cluster sidecar.",
                property);
        }

        return url!.Trim().TrimEnd('/');
    }

    /// <summary>
    /// Whether the artifact hash is 64 lowercase-or-uppercase hex chars.
    /// </summary>
    public static bool IsValidArtifactHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Trim().Length != 64)
        {
            return false;
        }

        foreach (var c in hash.Trim())
        {
            var isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the device profile is <c>cpu</c>, <c>cuda</c>, or <c>cuda:N</c>.
    /// </summary>
    public static bool IsValidDeviceProfile(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return false;
        }

        var trimmed = profile.Trim();
        if (string.Equals(trimmed, "cpu", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("cuda:", StringComparison.OrdinalIgnoreCase))
        {
            var index = trimmed.Substring("cuda:".Length);
            return int.TryParse(index, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ordinal)
                && ordinal >= 0 && ordinal <= 15;
        }

        return false;
    }
}

/// <summary>
/// Shape validation for <see cref="LocalInferenceOptions"/>.
/// </summary>
public sealed class LocalInferenceOptionsValidator : IValidateOptions<LocalInferenceOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalInferenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Endpoint)} must not be empty.");
        }

        if (!LocalInferenceOptions.IsSidecarEndpointAllowed(options.Endpoint))
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Endpoint)} is not allowed. Use https, http localhost, or http://local-inference:8000 for the in-cluster sidecar.");
        }

        if (!string.Equals(options.Protocol, "http", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Protocol)} must be 'http' or 'grpc'.");
        }

        if (string.IsNullOrWhiteSpace(options.ModelName))
        {
            return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.ModelName)} must not be empty.");
        }

        if (options.MaxConcurrency < 1 || options.MaxConcurrency > 16)
        {
            return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.MaxConcurrency)} must be in 1..16.");
        }

        if (options.Models is null)
        {
            return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Models)} must not be null.");
        }

        for (var i = 0; i < options.Models.Count; i++)
        {
            var entry = options.Models[i];
            if (entry is null)
            {
                return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Models)}[{i}] must not be null.");
            }

            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Models)}[{i}].{nameof(LocalInferenceModelOptions.Id)} must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(entry.Version))
            {
                return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Models)}[{i}].{nameof(LocalInferenceModelOptions.Version)} must not be empty.");
            }

            if (!LocalInferenceOptions.IsValidArtifactHash(entry.ArtifactHash))
            {
                return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Models)}[{i}].{nameof(LocalInferenceModelOptions.ArtifactHash)} must be 64 hex chars.");
            }

            if (!LocalInferenceOptions.IsValidDeviceProfile(entry.DeviceProfile))
            {
                return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Models)}[{i}].{nameof(LocalInferenceModelOptions.DeviceProfile)} must be 'cpu', 'cuda', or 'cuda:N'.");
            }

            if (!Enum.TryParse<DubbingPlatform.Domain.Enums.ProviderCapability>(entry.Capability, ignoreCase: true, out _))
            {
                return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Models)}[{i}].{nameof(LocalInferenceModelOptions.Capability)} has unknown capability '{entry.Capability}'.");
            }
        }

        if (options.RequireMtls)
        {
            if (!Uri.TryCreate(options.Endpoint.Trim(), UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.Endpoint)} must be https when RequireMtls is true.");
            }

            if (string.IsNullOrWhiteSpace(options.ClientCertificatePath))
            {
                return ValidateOptionsResult.Fail($"{nameof(LocalInferenceOptions)}.{nameof(LocalInferenceOptions.ClientCertificatePath)} is required when RequireMtls is true.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
