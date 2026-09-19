using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Replayable idempotency record.
/// Allowed <see cref="State"/> values: Started, Succeeded, Failed.
/// Consumers must reject unknown states.
/// </summary>
public sealed class IdempotencyRecord
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Endpoint { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; }

    public string State { get; private set; }

    public string? ResponseStatus { get; private set; }

    public string? ResponseBody { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    private IdempotencyRecord()
    {
        Endpoint = string.Empty;
        IdempotencyKey = string.Empty;
        RequestHash = string.Empty;
        State = string.Empty;
    }

    public IdempotencyRecord(
        Guid id,
        Guid tenantId,
        string endpoint,
        string idempotencyKey,
        string requestHash,
        string state,
        string? responseStatus,
        string? responseBody,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        TenantId = tenantId;
        Endpoint = endpoint;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        State = state;
        ResponseStatus = responseStatus;
        ResponseBody = responseBody;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("IdempotencyRecord Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("IdempotencyRecord TenantId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new DomainException("IdempotencyRecord Endpoint must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(IdempotencyKey))
        {
            throw new DomainException("IdempotencyRecord IdempotencyKey must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(RequestHash))
        {
            throw new DomainException("IdempotencyRecord RequestHash must not be empty.");
        }

        if (State != "Started" && State != "Succeeded" && State != "Failed")
        {
            throw new DomainException("IdempotencyRecord State must be one of Started, Succeeded, Failed.");
        }

        if (ResponseStatus is not null && string.IsNullOrWhiteSpace(ResponseStatus))
        {
            throw new DomainException("IdempotencyRecord ResponseStatus must not be empty when set.");
        }

        if (ResponseBody is not null && string.IsNullOrWhiteSpace(ResponseBody))
        {
            throw new DomainException("IdempotencyRecord ResponseBody must not be empty when set.");
        }

        if (ExpiresAt < CreatedAt)
        {
            throw new DomainException("IdempotencyRecord ExpiresAt must not be before CreatedAt.");
        }
    }
}
