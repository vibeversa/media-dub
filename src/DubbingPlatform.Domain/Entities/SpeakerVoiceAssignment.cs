using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class SpeakerVoiceAssignment
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    public Guid SpeakerId { get; private set; }

    public Guid VoiceProfileId { get; private set; }

    public string AssignmentReason { get; private set; }

    public string PolicyHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private SpeakerVoiceAssignment()
    {
        AssignmentReason = string.Empty;
        PolicyHash = string.Empty;
    }

    public SpeakerVoiceAssignment(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid speakerId,
        Guid voiceProfileId,
        string assignmentReason,
        string policyHash,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        RunId = runId;
        SpeakerId = speakerId;
        VoiceProfileId = voiceProfileId;
        AssignmentReason = assignmentReason;
        PolicyHash = policyHash;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("SpeakerVoiceAssignment Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("SpeakerVoiceAssignment TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("SpeakerVoiceAssignment ProjectId must not be empty.");
        }

        if (RunId == Guid.Empty)
        {
            throw new DomainException("SpeakerVoiceAssignment RunId must not be empty.");
        }

        if (SpeakerId == Guid.Empty)
        {
            throw new DomainException("SpeakerVoiceAssignment SpeakerId must not be empty.");
        }

        if (VoiceProfileId == Guid.Empty)
        {
            throw new DomainException("SpeakerVoiceAssignment VoiceProfileId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(AssignmentReason))
        {
            throw new DomainException("SpeakerVoiceAssignment AssignmentReason must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(PolicyHash))
        {
            throw new DomainException("SpeakerVoiceAssignment PolicyHash must not be empty.");
        }
    }
}
