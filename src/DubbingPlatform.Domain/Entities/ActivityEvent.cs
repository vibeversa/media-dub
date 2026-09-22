using System.Text.Json;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ActivityEvent
{
    public const int SupportedSchemaVersion = 1;

    public const int MaxSummaryLength = 1000;

    public const int MaxCorrelationIdLength = 128;

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? ProjectId { get; private set; }

    public Guid? ProcessingRunId { get; private set; }

    public ActivityType Type { get; private set; }

    public ActivityActorType ActorType { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public string Summary { get; private set; }

    public ActivitySeverity Severity { get; private set; }

    public string CorrelationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public int SchemaVersion { get; private set; }

    public string? MetadataJson { get; private set; }

    private ActivityEvent()
    {
        Summary = string.Empty;
        CorrelationId = string.Empty;
        SchemaVersion = SupportedSchemaVersion;
    }

    public ActivityEvent(
        Guid id,
        Guid tenantId,
        Guid? projectId,
        Guid? processingRunId,
        ActivityType type,
        ActivityActorType actorType,
        Guid? actorUserId,
        string summary,
        ActivitySeverity severity,
        string correlationId,
        DateTimeOffset occurredAt,
        int schemaVersion,
        string? metadataJson)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        Type = type;
        ActorType = actorType;
        ActorUserId = actorUserId;
        Summary = summary;
        Severity = severity;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
        SchemaVersion = schemaVersion;
        MetadataJson = metadataJson;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ActivityEvent Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ActivityEvent TenantId must not be empty.");
        }

        if (ProjectId.HasValue && ProjectId.Value == Guid.Empty)
        {
            throw new DomainException("ActivityEvent ProjectId must not be empty when set.");
        }

        if (ProcessingRunId.HasValue && ProcessingRunId.Value == Guid.Empty)
        {
            throw new DomainException("ActivityEvent ProcessingRunId must not be empty when set.");
        }

        if (!Enum.IsDefined(Type))
        {
            throw new DomainException("ActivityEvent Type is not defined.");
        }

        if (!Enum.IsDefined(ActorType))
        {
            throw new DomainException("ActivityEvent ActorType is not defined.");
        }

        if (ActorUserId.HasValue && ActorUserId.Value == Guid.Empty)
        {
            throw new DomainException("ActivityEvent ActorUserId must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(Summary))
        {
            throw new DomainException("ActivityEvent Summary must not be empty.");
        }

        if (Summary.Length > MaxSummaryLength)
        {
            throw new DomainException($"ActivityEvent Summary must be at most {MaxSummaryLength} chars.");
        }

        if (!Enum.IsDefined(Severity))
        {
            throw new DomainException("ActivityEvent Severity is not defined.");
        }

        if (string.IsNullOrWhiteSpace(CorrelationId))
        {
            throw new DomainException("ActivityEvent CorrelationId must not be empty.");
        }

        if (CorrelationId.Length > MaxCorrelationIdLength)
        {
            throw new DomainException($"ActivityEvent CorrelationId must be at most {MaxCorrelationIdLength} chars.");
        }

        if (SchemaVersion != SupportedSchemaVersion)
        {
            throw new DomainException($"ActivityEvent SchemaVersion must be {SupportedSchemaVersion}.");
        }

        if (MetadataJson is not null)
        {
            if (string.IsNullOrWhiteSpace(MetadataJson))
            {
                throw new DomainException("ActivityEvent MetadataJson must not be empty when set.");
            }

            try
            {
                using var _ = JsonDocument.Parse(MetadataJson);
            }
            catch (JsonException ex)
            {
                throw new DomainException("ActivityEvent MetadataJson must be valid JSON.", ex);
            }

            ThrowIfUnsafeContent(MetadataJson, nameof(MetadataJson));
        }

        ThrowIfUnsafeContent(Summary, nameof(Summary));
        ThrowIfUnsafeContent(CorrelationId, nameof(CorrelationId));
    }

    internal static void ThrowIfUnsafeContent(string value, string fieldName)
    {
        if (value.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || value.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"ActivityEvent {fieldName} must not contain URLs.");
        }

        if (value.Contains("bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"ActivityEvent {fieldName} must not contain tokens.");
        }
    }
}
