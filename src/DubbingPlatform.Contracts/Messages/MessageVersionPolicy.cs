namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Message schema version policy. Schemas are additive-only: new optional
/// fields may be added, but existing fields are never renamed, removed, or
/// retyped. Readers are tolerant and ignore unknown JSON properties (see
/// <see cref="MessagingJson"/>). A message whose <c>SchemaVersion</c> is not
/// supported must not throw at deserialization; the consumer routes it to the
/// <c>_skipped</c> queue (see <see cref="QueueNames.Skipped"/>) and increments
/// the <c>messaging.schema_mismatch_total</c> metric (owned by Task 38
/// observability).
/// </summary>
public static class MessageVersionPolicy
{
    /// <summary>
    /// Current schema version carried by every message.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Counter emitted when an unsupported schema version is skipped.
    /// </summary>
    public const string SchemaMismatchMetricName = "messaging.schema_mismatch_total";

    /// <summary>
    /// Returns true only for supported schema versions (currently exactly 1).
    /// </summary>
    public static bool IsSupported(int version)
    {
        return version == CurrentVersion;
    }
}
