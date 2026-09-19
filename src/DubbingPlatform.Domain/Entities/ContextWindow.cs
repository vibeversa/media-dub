using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ContextWindow
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    public int Sequence { get; private set; }

    public string ContextText { get; private set; }

    public string ContextHash { get; private set; }

    public int TokenCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ContextWindow()
    {
        ContextText = string.Empty;
        ContextHash = string.Empty;
    }

    public ContextWindow(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        int sequence,
        string contextText,
        string contextHash,
        int tokenCount,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        RunId = runId;
        Sequence = sequence;
        ContextText = contextText;
        ContextHash = contextHash;
        TokenCount = tokenCount;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ContextWindow Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ContextWindow TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("ContextWindow ProjectId must not be empty.");
        }

        if (RunId == Guid.Empty)
        {
            throw new DomainException("ContextWindow RunId must not be empty.");
        }

        if (Sequence < 0)
        {
            throw new DomainException("ContextWindow Sequence must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(ContextText))
        {
            throw new DomainException("ContextWindow ContextText must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ContextHash))
        {
            throw new DomainException("ContextWindow ContextHash must not be empty.");
        }

        if (TokenCount < 0)
        {
            throw new DomainException("ContextWindow TokenCount must be >= 0.");
        }
    }

    /// <summary>
    /// Refreshes derived content on rebuild (transcript selection changed, so
    /// the planned text/hash differs). Identity (<see cref="Id"/>,
    /// <see cref="Sequence"/>) is immutable so barrier scope ids and window
    /// references stay stable across rebuilds; old artifact rows remain as
    /// immutable history (artifacts are never deleted).
    /// </summary>
    public void Update(string contextText, string contextHash, int tokenCount)
    {
        if (string.IsNullOrWhiteSpace(contextText))
        {
            throw new DomainException("ContextWindow ContextText must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(contextHash))
        {
            throw new DomainException("ContextWindow ContextHash must not be empty.");
        }

        if (tokenCount < 0)
        {
            throw new DomainException("ContextWindow TokenCount must be >= 0.");
        }

        ContextText = contextText;
        ContextHash = contextHash;
        TokenCount = tokenCount;

        Validate();
    }
}
