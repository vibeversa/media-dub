namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Base envelope carried by every durable integration message.
/// Delivery is at-least-once with no exactly-once guarantee; consumers must be
/// idempotent and correlate via <see cref="MessageId"/> plus durable execution
/// records. <c>CorrelationId</c> is propagated via message headers by the bus.
/// Schema policy is additive-only: unknown JSON properties are ignored by
/// tolerant readers (see <see cref="MessagingJson"/>); messages with an
/// unsupported <see cref="SchemaVersion"/> must not throw at deserialization
/// and are routed to the <c>_skipped</c> queue (see
/// <see cref="MessageVersionPolicy"/>). No secrets may appear in messages;
/// <c>TenantId</c> is required and cross-tenant validation happens in consumers.
/// Payloads stay under 256KB by referencing artifacts instead of inline bytes.
/// </summary>
public abstract record IntegrationMessage(
    Guid MessageId,
    string CorrelationId,
    Guid TenantId,
    Guid ProjectId,
    Guid ProcessingRunId,
    Guid? StageExecutionId,
    string? StageType,
    string? ScopeType,
    string? ScopeId,
    Guid? SegmentId,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    int Attempt,
    string? InputHash,
    string? ConfigurationHash,
    string? ExecutionSnapshotHash);
