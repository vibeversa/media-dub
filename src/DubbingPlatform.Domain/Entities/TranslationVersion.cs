using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class TranslationVersion
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    public Guid SegmentId { get; private set; }

    public string PrimaryText { get; private set; }

    public string[] AlternativeTexts { get; private set; }

    public double SemanticScore { get; private set; }

    public double NaturalnessScore { get; private set; }

    public double TimingScore { get; private set; }

    public string Provider { get; private set; }

    public string Model { get; private set; }

    public Guid? PromptTemplateId { get; private set; }

    public string? PromptHash { get; private set; }

    public bool IsSelected { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private TranslationVersion()
    {
        PrimaryText = string.Empty;
        AlternativeTexts = Array.Empty<string>();
        Provider = string.Empty;
        Model = string.Empty;
    }

    public TranslationVersion(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        string primaryText,
        string[] alternativeTexts,
        double semanticScore,
        double naturalnessScore,
        double timingScore,
        string provider,
        string model,
        Guid? promptTemplateId,
        string? promptHash,
        bool isSelected,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        RunId = runId;
        SegmentId = segmentId;
        PrimaryText = primaryText;
        AlternativeTexts = alternativeTexts;
        SemanticScore = semanticScore;
        NaturalnessScore = naturalnessScore;
        TimingScore = timingScore;
        Provider = provider;
        Model = model;
        PromptTemplateId = promptTemplateId;
        PromptHash = promptHash;
        IsSelected = isSelected;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("TranslationVersion Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("TranslationVersion TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("TranslationVersion ProjectId must not be empty.");
        }

        if (RunId == Guid.Empty)
        {
            throw new DomainException("TranslationVersion RunId must not be empty.");
        }

        if (SegmentId == Guid.Empty)
        {
            throw new DomainException("TranslationVersion SegmentId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(PrimaryText))
        {
            throw new DomainException("TranslationVersion PrimaryText must not be empty.");
        }

        if (AlternativeTexts is null)
        {
            throw new DomainException("TranslationVersion AlternativeTexts must not be null.");
        }

        if (double.IsNaN(SemanticScore) || SemanticScore < 0.0 || SemanticScore > 1.0)
        {
            throw new DomainException("TranslationVersion SemanticScore must be in 0..1.");
        }

        if (double.IsNaN(NaturalnessScore) || NaturalnessScore < 0.0 || NaturalnessScore > 1.0)
        {
            throw new DomainException("TranslationVersion NaturalnessScore must be in 0..1.");
        }

        if (double.IsNaN(TimingScore) || TimingScore < 0.0 || TimingScore > 1.0)
        {
            throw new DomainException("TranslationVersion TimingScore must be in 0..1.");
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new DomainException("TranslationVersion Provider must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new DomainException("TranslationVersion Model must not be empty.");
        }

        if (PromptTemplateId.HasValue && PromptTemplateId.Value == Guid.Empty)
        {
            throw new DomainException("TranslationVersion PromptTemplateId must not be empty when set.");
        }

        if (PromptHash is not null && string.IsNullOrWhiteSpace(PromptHash))
        {
            throw new DomainException("TranslationVersion PromptHash must not be empty when set.");
        }
    }

    /// <summary>
    /// Marks (or clears) this version as the selected translation for its
    /// segment. Used by deterministic best-candidate selection; alternatives
    /// stay stored in the winning row for timing optimization.
    /// </summary>
    public void SetSelected(bool isSelected)
    {
        IsSelected = isSelected;
        Validate();
    }
}
