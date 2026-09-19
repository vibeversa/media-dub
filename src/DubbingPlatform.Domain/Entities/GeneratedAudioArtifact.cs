using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class GeneratedAudioArtifact
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    public Guid SegmentId { get; private set; }

    public string Provider { get; private set; }

    public string Model { get; private set; }

    public Guid VoiceProfileId { get; private set; }

    public Guid ContentObjectId { get; private set; }

    public int DurationMs { get; private set; }

    public bool IsPreview { get; private set; }

    public int Attempt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private GeneratedAudioArtifact()
    {
        Provider = string.Empty;
        Model = string.Empty;
    }

    public GeneratedAudioArtifact(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        string provider,
        string model,
        Guid voiceProfileId,
        Guid contentObjectId,
        int durationMs,
        bool isPreview,
        int attempt,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        RunId = runId;
        SegmentId = segmentId;
        Provider = provider;
        Model = model;
        VoiceProfileId = voiceProfileId;
        ContentObjectId = contentObjectId;
        DurationMs = durationMs;
        IsPreview = isPreview;
        Attempt = attempt;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("GeneratedAudioArtifact Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("GeneratedAudioArtifact TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("GeneratedAudioArtifact ProjectId must not be empty.");
        }

        if (RunId == Guid.Empty)
        {
            throw new DomainException("GeneratedAudioArtifact RunId must not be empty.");
        }

        if (SegmentId == Guid.Empty)
        {
            throw new DomainException("GeneratedAudioArtifact SegmentId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new DomainException("GeneratedAudioArtifact Provider must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new DomainException("GeneratedAudioArtifact Model must not be empty.");
        }

        if (VoiceProfileId == Guid.Empty)
        {
            throw new DomainException("GeneratedAudioArtifact VoiceProfileId must not be empty.");
        }

        if (ContentObjectId == Guid.Empty)
        {
            throw new DomainException("GeneratedAudioArtifact ContentObjectId must not be empty.");
        }

        if (DurationMs < 0)
        {
            throw new DomainException("GeneratedAudioArtifact DurationMs must be >= 0.");
        }

        if (Attempt < 0)
        {
            throw new DomainException("GeneratedAudioArtifact Attempt must be >= 0.");
        }
    }
}
