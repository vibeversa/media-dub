using System.Text.Json;
using System.Text.Json.Nodes;
using DubbingPlatform.Application.Configuration;

namespace DubbingPlatform.Application.Projects;

/// <summary>
/// Deterministic project configuration hash. Canonicalizes
/// <c>{ settings, sourceLanguage, targetLanguage, processingSettings? }</c>
/// with sorted keys and SHA-256 (lowercase hex) via
/// <see cref="ConfigurationHashCalculator"/>. Secrets are stripped by the
/// calculator so keys/tokens never influence the hash. Pure for hermetic tests.
/// </summary>
public static class ProjectConfigHash
{
    /// <summary>
    /// Computes the hash over source/target plus normalized settings JSON.
    /// </summary>
    public static string Compute(string sourceLanguage, string targetLanguage, string normalizedSettingsJson, string? normalizedProcessingSettingsJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedSettingsJson);

        JsonNode settingsNode;
        try
        {
            settingsNode = JsonNode.Parse(normalizedSettingsJson) ?? new JsonObject();
        }
        catch (JsonException)
        {
            settingsNode = new JsonObject();
        }

        var canonical = new JsonObject
        {
            ["settings"] = settingsNode,
            ["sourceLanguage"] = sourceLanguage.Trim(),
            ["targetLanguage"] = targetLanguage.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(normalizedProcessingSettingsJson))
        {
            try
            {
                canonical["processingSettings"] = JsonNode.Parse(normalizedProcessingSettingsJson) ?? new JsonObject();
            }
            catch (JsonException)
            {
                canonical["processingSettings"] = new JsonObject();
            }
        }

        return ConfigurationHashCalculator.Compute(canonical);
    }
}
