using System.Text.Json;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Contracts.Messages;

namespace DubbingPlatform.Application.Enrichment;

/// <summary>
/// Dual-gate for optional enrichment: a kind is requested only when the global
/// <c>Features</c> flag is true AND the project opted in via
/// <c>settings.enrichment: {videoIntelligence: true, lipSync: true}</c>.
/// Settings parsing is tolerant: missing/invalid JSON means no opt-in (never
/// throws). Flag toggles take effect only for new runs.
/// </summary>
public static class EnrichmentGate
{
    /// <summary>
    /// Parses per-project enrichment opt-ins from <c>SettingsJson</c>.
    /// Accepts <c>{"enrichment":{"videoIntelligence":true,"lipSync":true}}</c>
    /// (camelCase or PascalCase keys, case-insensitive). Never throws.
    /// </summary>
    public static (bool VideoIntelligence, bool LipSync) ParseSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return (false, false);
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (false, false);
            }

            if (!TryGetProperty(document.RootElement, "enrichment", out var enrichment)
                || enrichment.ValueKind != JsonValueKind.Object)
            {
                return (false, false);
            }

            var video = TryGetBoolean(enrichment, "videoIntelligence");
            var lip = TryGetBoolean(enrichment, "lipSync");
            return (video, lip);
        }
        catch (JsonException)
        {
            return (false, false);
        }
    }

    /// <summary>
    /// Returns true when video-intelligence enrichment should be requested.
    /// </summary>
    public static bool ShouldRequestVideoIntelligence(FeatureOptions features, string? settingsJson)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (!features.VideoIntelligenceEnabled)
        {
            return false;
        }

        return ParseSettings(settingsJson).VideoIntelligence;
    }

    /// <summary>
    /// Returns true when lip-sync enrichment should be requested.
    /// </summary>
    public static bool ShouldRequestLipSync(FeatureOptions features, string? settingsJson)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (!features.LipSyncEnabled)
        {
            return false;
        }

        return ParseSettings(settingsJson).LipSync;
    }

    /// <summary>
    /// Returns the requested kinds in publish order (VideoIntelligence, LipSync).
    /// Empty when disabled or not opted in: the caller publishes nothing and the
    /// core run completes identically to a build without enrichment.
    /// </summary>
    public static IReadOnlyList<string> RequestedKinds(FeatureOptions features, string? settingsJson)
    {
        ArgumentNullException.ThrowIfNull(features);
        var kinds = new List<string>(capacity: 2);
        if (ShouldRequestVideoIntelligence(features, settingsJson))
        {
            kinds.Add(EnrichmentKinds.VideoIntelligence);
        }

        if (ShouldRequestLipSync(features, settingsJson))
        {
            kinds.Add(EnrichmentKinds.LipSync);
        }

        return kinds;
    }

    /// <summary>
    /// Builds <see cref="EnrichmentRequested"/> messages for the requested kinds.
    /// Pure (no I/O) for hermetic tests. Messages carry no stage identity (null
    /// StageType/ScopeType/ScopeId) so <c>BaseConsumer</c> skips stage claiming;
    /// they route to <c>ai.gpu</c> via the bus topology.
    /// </summary>
    public static IReadOnlyList<EnrichmentRequested> BuildRequests(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string correlationId,
        IReadOnlyList<string> kinds,
        int attempt = 0)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var requests = new List<EnrichmentRequested>(kinds.Count);
        foreach (var kind in kinds)
        {
            if (!EnrichmentKinds.IsKnown(kind))
            {
                continue;
            }

            requests.Add(new EnrichmentRequested(
                Guid.NewGuid(),
                string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
                tenantId,
                projectId,
                runId,
                null, null, null, null, null,
                MessageVersionPolicy.CurrentVersion,
                DateTimeOffset.UtcNow,
                attempt,
                null, null, null,
                kind.Trim()));
        }

        return requests;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false,
        };
    }
}
