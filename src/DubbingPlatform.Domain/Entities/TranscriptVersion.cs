using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class TranscriptVersion
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    public Guid SegmentId { get; private set; }

    public string Provider { get; private set; }

    public string Model { get; private set; }

    public string Language { get; private set; }

    public string Text { get; private set; }

    public double Confidence { get; private set; }

    public Guid? WordTimestampsArtifactId { get; private set; }

    public bool IsSelected { get; private set; }

    public bool NeedsReview { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private TranscriptVersion()
    {
        Provider = string.Empty;
        Model = string.Empty;
        Language = string.Empty;
        Text = string.Empty;
    }

    public TranscriptVersion(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        string provider,
        string model,
        string language,
        string text,
        double confidence,
        Guid? wordTimestampsArtifactId,
        bool isSelected,
        bool needsReview,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        RunId = runId;
        SegmentId = segmentId;
        Provider = provider;
        Model = model;
        Language = language;
        Text = text;
        Confidence = confidence;
        WordTimestampsArtifactId = wordTimestampsArtifactId;
        IsSelected = isSelected;
        NeedsReview = needsReview;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("TranscriptVersion Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("TranscriptVersion TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("TranscriptVersion ProjectId must not be empty.");
        }

        if (RunId == Guid.Empty)
        {
            throw new DomainException("TranscriptVersion RunId must not be empty.");
        }

        if (SegmentId == Guid.Empty)
        {
            throw new DomainException("TranscriptVersion SegmentId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new DomainException("TranscriptVersion Provider must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new DomainException("TranscriptVersion Model must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Language))
        {
            throw new DomainException("TranscriptVersion Language must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new DomainException("TranscriptVersion Text must not be empty.");
        }

        if (double.IsNaN(Confidence) || Confidence < 0.0 || Confidence > 1.0)
        {
            throw new DomainException("TranscriptVersion Confidence must be in 0..1.");
        }

        if (WordTimestampsArtifactId.HasValue && WordTimestampsArtifactId.Value == Guid.Empty)
        {
            throw new DomainException("TranscriptVersion WordTimestampsArtifactId must not be empty when set.");
        }
    }

    /// <summary>
    /// Marks (or clears) this version as the selected transcript for its
    /// segment. Used by best-version selection; alternatives stay stored.
    /// </summary>
    public void SetSelected(bool isSelected)
    {
        IsSelected = isSelected;
        Validate();
    }

    /// <summary>
    /// Marks (or clears) the needs-review flag. Used when persistent
    /// low confidence routes a segment to manual review.
    /// </summary>
    public void SetNeedsReview(bool needsReview)
    {
        NeedsReview = needsReview;
        Validate();
    }
}
