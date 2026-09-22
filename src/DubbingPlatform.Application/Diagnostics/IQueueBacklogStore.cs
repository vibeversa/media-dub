using System.Text.Json;

namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// One pending outbox message projected for diagnostics: destination queue,
/// message type, headers, and enqueue time. Bodies are never loaded.
/// </summary>
public sealed record QueuedMessageSnapshot(
    string? DestinationAddress,
    string MessageType,
    string? HeadersJson,
    DateTimeOffset? EnqueuedAt);

/// <summary>
/// Backlog over the MassTransit outbox store. Application must not reference
/// MassTransit directly, so queue diagnostics read through this seam;
/// Infrastructure implements it over <c>AppDbContext.OutboxMessages</c>
/// (AsNoTracking, bodies never loaded). Tests substitute a fake.
/// </summary>
public interface IQueueBacklogStore
{
    /// <summary>
    /// Lists pending outbox messages (destination, type, headers, enqueue
    /// time). Implementations cap the scan and never load bodies.
    /// </summary>
    Task<IReadOnlyList<QueuedMessageSnapshot>> ListPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Dead-letter reason extraction shared by the queue diagnostics service and
/// its store. Prefers an explicit reason/error/exception field from the
/// headers JSON, then falls back to the short message-type name, then
/// <c>unknown</c>. Pure and never throws.
/// </summary>
public static class DeadLetterReasons
{
    private static readonly string[] ReasonFields =
    [
        "reason",
        "errorCode",
        "error-code",
        "exceptionType",
        "exception-type",
        "faultReason",
        "fault-reason",
    ];

    /// <summary>
    /// Extracts a dead-letter reason code from headers JSON and message type.
    /// </summary>
    public static string Extract(string? headersJson, string? messageType)
    {
        if (!string.IsNullOrWhiteSpace(headersJson))
        {
            try
            {
                using var document = JsonDocument.Parse(headersJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in ReasonFields)
                    {
                        foreach (var property in document.RootElement.EnumerateObject())
                        {
                            if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase)
                                && property.Value.ValueKind == JsonValueKind.String)
                            {
                                var value = property.Value.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(value))
                                {
                                    return value;
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Unparsable headers fall through to the message-type fallback.
            }
        }

        if (!string.IsNullOrWhiteSpace(messageType))
        {
            var trimmed = messageType.Trim();
            var separator = Math.Max(
                trimmed.LastIndexOf(':'),
                Math.Max(trimmed.LastIndexOf('.'), trimmed.LastIndexOf('/')));
            var shortName = separator >= 0 && separator < trimmed.Length - 1
                ? trimmed[(separator + 1)..]
                : trimmed;
            if (!string.IsNullOrWhiteSpace(shortName))
            {
                return shortName;
            }
        }

        return "unknown";
    }
}
