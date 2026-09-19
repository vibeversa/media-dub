using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class PromptTemplateVersion
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PromptTemplateId { get; private set; }

    public int Version { get; private set; }

    public string SystemInstruction { get; private set; }

    public string TemplateBody { get; private set; }

    public string SafetySettingsJson { get; private set; }

    public string PromptHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private PromptTemplateVersion()
    {
        SystemInstruction = string.Empty;
        TemplateBody = string.Empty;
        SafetySettingsJson = string.Empty;
        PromptHash = string.Empty;
    }

    public PromptTemplateVersion(
        Guid id,
        Guid tenantId,
        Guid promptTemplateId,
        int version,
        string systemInstruction,
        string templateBody,
        string safetySettingsJson,
        string promptHash,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        PromptTemplateId = promptTemplateId;
        Version = version;
        SystemInstruction = systemInstruction;
        TemplateBody = templateBody;
        SafetySettingsJson = safetySettingsJson;
        PromptHash = promptHash;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("PromptTemplateVersion Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("PromptTemplateVersion TenantId must not be empty.");
        }

        if (PromptTemplateId == Guid.Empty)
        {
            throw new DomainException("PromptTemplateVersion PromptTemplateId must not be empty.");
        }

        if (Version < 1)
        {
            throw new DomainException("PromptTemplateVersion Version must be >= 1.");
        }

        if (string.IsNullOrWhiteSpace(SystemInstruction))
        {
            throw new DomainException("PromptTemplateVersion SystemInstruction must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(TemplateBody))
        {
            throw new DomainException("PromptTemplateVersion TemplateBody must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(SafetySettingsJson))
        {
            throw new DomainException("PromptTemplateVersion SafetySettingsJson must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(PromptHash))
        {
            throw new DomainException("PromptTemplateVersion PromptHash must not be empty.");
        }
    }
}
