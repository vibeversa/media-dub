using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class SpeechSegment
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    public int Sequence { get; private set; }

    public int StartMs { get; private set; }

    public int EndMs { get; private set; }

    public int DurationMs { get; private set; }

    public string Status { get; private set; }

    public Guid? SpeakerId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private SpeechSegment()
    {
        Status = string.Empty;
    }

    public SpeechSegment(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        int sequence,
        int startMs,
        int endMs,
        string status,
        Guid? speakerId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        RunId = runId;
        Sequence = sequence;
        StartMs = startMs;
        EndMs = endMs;
        DurationMs = checked(endMs - startMs);
        Status = status;
        SpeakerId = speakerId;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("SpeechSegment Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("SpeechSegment TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("SpeechSegment ProjectId must not be empty.");
        }

        if (RunId == Guid.Empty)
        {
            throw new DomainException("SpeechSegment RunId must not be empty.");
        }

        if (Sequence < 0)
        {
            throw new DomainException("SpeechSegment Sequence must be >= 0.");
        }

        if (StartMs < 0)
        {
            throw new DomainException("SpeechSegment StartMs must be >= 0.");
        }

        if (EndMs <= StartMs)
        {
            throw new DomainException("SpeechSegment EndMs must be greater than StartMs.");
        }

        var duration = (long)EndMs - StartMs;
        if (duration <= 0 || duration > int.MaxValue)
        {
            throw new DomainException("SpeechSegment duration is out of range.");
        }

        if (DurationMs != EndMs - StartMs)
        {
            throw new DomainException("SpeechSegment DurationMs must equal EndMs - StartMs.");
        }

        if (string.IsNullOrWhiteSpace(Status))
        {
            throw new DomainException("SpeechSegment Status must not be empty.");
        }

        if (SpeakerId.HasValue && SpeakerId.Value == Guid.Empty)
        {
            throw new DomainException("SpeechSegment SpeakerId must not be empty when set.");
        }
    }

    /// <summary>
    /// Assigns (or clears) the diarization speaker. Used by the diarization
    /// stage to map segments to project-scoped speakers; null clears the
    /// mapping for remaps. Empty GUIDs are rejected.
    /// </summary>
    public void AssignSpeaker(Guid? speakerId)
    {
        if (speakerId.HasValue && speakerId.Value == Guid.Empty)
        {
            throw new DomainException("SpeechSegment SpeakerId must not be empty when set.");
        }

        SpeakerId = speakerId;
        Validate();
    }
}
