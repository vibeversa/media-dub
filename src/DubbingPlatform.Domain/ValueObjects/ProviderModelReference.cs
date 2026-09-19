using System.Globalization;

namespace DubbingPlatform.Domain.ValueObjects;

/// <summary>
/// Identifies a provider model, deployment, region and API version.
/// </summary>
public sealed record ProviderModelReference
{
    public string Provider { get; init; }

    public string Model { get; init; }

    public string? ModelVersion { get; init; }

    public string? Deployment { get; init; }

    public string? Region { get; init; }

    public string? ApiVersion { get; init; }

    public ProviderModelReference(
        string provider,
        string model,
        string? modelVersion = null,
        string? deployment = null,
        string? region = null,
        string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Provider must not be empty.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model must not be empty.", nameof(model));
        }

        Provider = provider;
        Model = model;
        ModelVersion = modelVersion;
        Deployment = deployment;
        Region = region;
        ApiVersion = apiVersion;
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Provider}/{Model}:{ModelVersion ?? "latest"}");
}
