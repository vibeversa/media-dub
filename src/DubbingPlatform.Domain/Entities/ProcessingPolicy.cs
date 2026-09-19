using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ProcessingPolicy
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public bool ExternalProvidersAllowed { get; private set; }

    public string[] AllowedProviders { get; private set; }

    public string? ResidencyConstraint { get; private set; }

    public string SensitivePolicy { get; private set; }

    public string VoicePolicy { get; private set; }

    public bool LocalInferenceAllowed { get; private set; }

    public string? RetentionOverride { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private ProcessingPolicy()
    {
        AllowedProviders = Array.Empty<string>();
        SensitivePolicy = string.Empty;
        VoicePolicy = string.Empty;
    }

    public ProcessingPolicy(
        Guid id,
        Guid tenantId,
        bool externalProvidersAllowed,
        string[] allowedProviders,
        string? residencyConstraint,
        string sensitivePolicy,
        string voicePolicy,
        bool localInferenceAllowed,
        string? retentionOverride,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        ExternalProvidersAllowed = externalProvidersAllowed;
        AllowedProviders = allowedProviders;
        ResidencyConstraint = residencyConstraint;
        SensitivePolicy = sensitivePolicy;
        VoicePolicy = voicePolicy;
        LocalInferenceAllowed = localInferenceAllowed;
        RetentionOverride = retentionOverride;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ProcessingPolicy Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ProcessingPolicy TenantId must not be empty.");
        }

        if (AllowedProviders is null)
        {
            throw new DomainException("ProcessingPolicy AllowedProviders must not be null.");
        }

        if (ResidencyConstraint is not null && string.IsNullOrWhiteSpace(ResidencyConstraint))
        {
            throw new DomainException("ProcessingPolicy ResidencyConstraint must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(SensitivePolicy))
        {
            throw new DomainException("ProcessingPolicy SensitivePolicy must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(VoicePolicy))
        {
            throw new DomainException("ProcessingPolicy VoicePolicy must not be empty.");
        }

        if (RetentionOverride is not null && string.IsNullOrWhiteSpace(RetentionOverride))
        {
            throw new DomainException("ProcessingPolicy RetentionOverride must not be empty when set.");
        }
    }
}
