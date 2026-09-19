using System.Text.Json;

namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Shared JSON settings for message serialization. Payloads use camelCase;
/// readers are tolerant: property names match case-insensitively and unknown
/// properties are ignored so additive schema evolution never breaks old
/// consumers.
/// </summary>
public static class MessagingJson
{
    /// <summary>
    /// CamelCase, case-insensitive, tolerant reader (unknown properties ignored).
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}
