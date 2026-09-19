using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class DubbingProject
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string SourceLanguage { get; private set; }

    public string TargetLanguage { get; private set; }

    public ProjectStatus Status { get; private set; }

    public string SettingsJson { get; private set; }

    public string ConfigurationHash { get; private set; }

    public Guid? SourceMediaAssetId { get; private set; }

    public Guid? ActiveRunId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private DubbingProject()
    {
        SourceLanguage = string.Empty;
        TargetLanguage = string.Empty;
        SettingsJson = string.Empty;
        ConfigurationHash = string.Empty;
    }

    public DubbingProject(
        Guid id,
        Guid tenantId,
        string sourceLanguage,
        string targetLanguage,
        ProjectStatus status,
        string settingsJson,
        string configurationHash,
        Guid? sourceMediaAssetId,
        Guid? activeRunId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        Status = status;
        SettingsJson = settingsJson;
        ConfigurationHash = configurationHash;
        SourceMediaAssetId = sourceMediaAssetId;
        ActiveRunId = activeRunId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("DubbingProject Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("DubbingProject TenantId must not be empty.");
        }

        if (!IsValidLanguageCode(SourceLanguage))
        {
            throw new DomainException("DubbingProject SourceLanguage must be a 2-3 letter code.");
        }

        if (!IsValidLanguageCode(TargetLanguage))
        {
            throw new DomainException("DubbingProject TargetLanguage must be a 2-3 letter code.");
        }

        if (string.Equals(SourceLanguage, TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("DubbingProject SourceLanguage and TargetLanguage must differ.");
        }

        if (string.IsNullOrWhiteSpace(SettingsJson))
        {
            throw new DomainException("DubbingProject SettingsJson must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ConfigurationHash))
        {
            throw new DomainException("DubbingProject ConfigurationHash must not be empty.");
        }

        if (SourceMediaAssetId.HasValue && SourceMediaAssetId.Value == Guid.Empty)
        {
            throw new DomainException("DubbingProject SourceMediaAssetId must not be empty when set.");
        }

        if (ActiveRunId.HasValue && ActiveRunId.Value == Guid.Empty)
        {
            throw new DomainException("DubbingProject ActiveRunId must not be empty when set.");
        }
    }

    private static bool IsValidLanguageCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Length < 2 || value.Length > 3)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isLetter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            if (!isLetter)
            {
                return false;
            }
        }

        return true;
    }
}
