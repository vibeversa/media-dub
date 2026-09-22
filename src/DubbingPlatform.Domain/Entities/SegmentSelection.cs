using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Explicit per-segment selection state. Transcript and translation content
/// versions are immutable; this row tracks which versions are selected plus a
/// <see cref="SelectionVersion"/> optimistic-concurrency counter (starts at 0,
/// bumped by every successful selection or manual edit). Unique per
/// (tenant, segment). Task 009 endpoints enforce <c>expectedSelectionVersion</c>
/// against this counter; stale writes fail with 409 instead of overwriting.
/// </summary>
public sealed class SegmentSelection
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid SegmentId { get; private set; }

    public Guid? SelectedTranscriptVersionId { get; private set; }

    public Guid? SelectedTranslationVersionId { get; private set; }

    public Guid? SelectedAudioArtifactId { get; private set; }

    public int SelectionVersion { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid UpdatedByUserId { get; private set; }

    private SegmentSelection()
    {
    }

    public SegmentSelection(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid segmentId,
        Guid? selectedTranscriptVersionId,
        Guid? selectedTranslationVersionId,
        Guid? selectedAudioArtifactId,
        int selectionVersion,
        DateTimeOffset updatedAt,
        Guid updatedByUserId)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        SegmentId = segmentId;
        SelectedTranscriptVersionId = selectedTranscriptVersionId;
        SelectedTranslationVersionId = selectedTranslationVersionId;
        SelectedAudioArtifactId = selectedAudioArtifactId;
        SelectionVersion = selectionVersion;
        UpdatedAt = updatedAt;
        UpdatedByUserId = updatedByUserId;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("SegmentSelection Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("SegmentSelection TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("SegmentSelection ProjectId must not be empty.");
        }

        if (SegmentId == Guid.Empty)
        {
            throw new DomainException("SegmentSelection SegmentId must not be empty.");
        }

        if (SelectedTranscriptVersionId.HasValue && SelectedTranscriptVersionId.Value == Guid.Empty)
        {
            throw new DomainException("SegmentSelection SelectedTranscriptVersionId must not be empty when set.");
        }

        if (SelectedTranslationVersionId.HasValue && SelectedTranslationVersionId.Value == Guid.Empty)
        {
            throw new DomainException("SegmentSelection SelectedTranslationVersionId must not be empty when set.");
        }

        if (SelectedAudioArtifactId.HasValue && SelectedAudioArtifactId.Value == Guid.Empty)
        {
            throw new DomainException("SegmentSelection SelectedAudioArtifactId must not be empty when set.");
        }

        if (SelectionVersion < 0)
        {
            throw new DomainException("SegmentSelection SelectionVersion must be >= 0.");
        }

        if (UpdatedByUserId == Guid.Empty)
        {
            throw new DomainException("SegmentSelection UpdatedByUserId must not be empty.");
        }
    }

    /// <summary>
    /// Applies a new selection: replaces the selected version pointers
    /// (null preserves the current pointer for that slot), bumps
    /// <see cref="SelectionVersion"/> by one, and records actor + timestamp.
    /// The service persists this via a conditional
    /// <c>UPDATE ... WHERE SelectionVersion = @expected</c> so concurrent
    /// writers serialize; this mutator documents the invariant for
    /// single-threaded use.
    /// </summary>
    public void ApplySelection(
        Guid? transcriptVersionId,
        Guid? translationVersionId,
        Guid? audioArtifactId,
        Guid updatedByUserId,
        DateTimeOffset updatedAt)
    {
        if (transcriptVersionId.HasValue && transcriptVersionId.Value == Guid.Empty)
        {
            throw new DomainException("SegmentSelection SelectedTranscriptVersionId must not be empty when set.");
        }

        if (translationVersionId.HasValue && translationVersionId.Value == Guid.Empty)
        {
            throw new DomainException("SegmentSelection SelectedTranslationVersionId must not be empty when set.");
        }

        if (audioArtifactId.HasValue && audioArtifactId.Value == Guid.Empty)
        {
            throw new DomainException("SegmentSelection SelectedAudioArtifactId must not be empty when set.");
        }

        if (updatedByUserId == Guid.Empty)
        {
            throw new DomainException("SegmentSelection UpdatedByUserId must not be empty.");
        }

        SelectedTranscriptVersionId = transcriptVersionId ?? SelectedTranscriptVersionId;
        SelectedTranslationVersionId = translationVersionId ?? SelectedTranslationVersionId;
        SelectedAudioArtifactId = audioArtifactId ?? SelectedAudioArtifactId;
        SelectionVersion = checked(SelectionVersion + 1);
        UpdatedByUserId = updatedByUserId;
        UpdatedAt = updatedAt;

        Validate();
    }
}
