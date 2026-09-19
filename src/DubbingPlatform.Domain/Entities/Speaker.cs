using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class Speaker
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public string SpeakerKey { get; private set; }

    public string DisplayName { get; private set; }

    public int FirstAppearanceMs { get; private set; }

    public int LastAppearanceMs { get; private set; }

    public string MappingMethod { get; private set; }

    public string MappingVersion { get; private set; }

    public double Confidence { get; private set; }

    public string? ProviderLabel { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private Speaker()
    {
        SpeakerKey = string.Empty;
        DisplayName = string.Empty;
        MappingMethod = string.Empty;
        MappingVersion = string.Empty;
    }

    public Speaker(
        Guid id,
        Guid tenantId,
        Guid projectId,
        string speakerKey,
        string displayName,
        int firstAppearanceMs,
        int lastAppearanceMs,
        string mappingMethod,
        string mappingVersion,
        double confidence,
        string? providerLabel,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        SpeakerKey = speakerKey;
        DisplayName = displayName;
        FirstAppearanceMs = firstAppearanceMs;
        LastAppearanceMs = lastAppearanceMs;
        MappingMethod = mappingMethod;
        MappingVersion = mappingVersion;
        Confidence = confidence;
        ProviderLabel = providerLabel;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("Speaker Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("Speaker TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("Speaker ProjectId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(SpeakerKey))
        {
            throw new DomainException("Speaker SpeakerKey must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new DomainException("Speaker DisplayName must not be empty.");
        }

        if (FirstAppearanceMs < 0)
        {
            throw new DomainException("Speaker FirstAppearanceMs must be >= 0.");
        }

        if (LastAppearanceMs < FirstAppearanceMs)
        {
            throw new DomainException("Speaker LastAppearanceMs must be >= FirstAppearanceMs.");
        }

        if (string.IsNullOrWhiteSpace(MappingMethod))
        {
            throw new DomainException("Speaker MappingMethod must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(MappingVersion))
        {
            throw new DomainException("Speaker MappingVersion must not be empty.");
        }

        if (double.IsNaN(Confidence) || Confidence < 0.0 || Confidence > 1.0)
        {
            throw new DomainException("Speaker Confidence must be in 0..1.");
        }
    }

    /// <summary>
    /// Refreshes derived mapping data on rerun (first/last appearance expand
    /// monotonically, method/confidence/label track the latest run).
    /// Identity (<see cref="Id"/>, <see cref="SpeakerKey"/>,
    /// <see cref="DisplayName"/>) is immutable for history stability.
    /// </summary>
    public void UpdateMapping(
        int firstAppearanceMs,
        int lastAppearanceMs,
        string mappingMethod,
        double confidence,
        string? providerLabel)
    {
        if (firstAppearanceMs < 0)
        {
            throw new DomainException("Speaker FirstAppearanceMs must be >= 0.");
        }

        if (lastAppearanceMs < firstAppearanceMs)
        {
            throw new DomainException("Speaker LastAppearanceMs must be >= FirstAppearanceMs.");
        }

        if (string.IsNullOrWhiteSpace(mappingMethod))
        {
            throw new DomainException("Speaker MappingMethod must not be empty.");
        }

        if (double.IsNaN(confidence) || confidence < 0.0 || confidence > 1.0)
        {
            throw new DomainException("Speaker Confidence must be in 0..1.");
        }

        FirstAppearanceMs = firstAppearanceMs;
        LastAppearanceMs = lastAppearanceMs;
        MappingMethod = mappingMethod;
        Confidence = confidence;
        ProviderLabel = providerLabel;

        Validate();
    }
}
