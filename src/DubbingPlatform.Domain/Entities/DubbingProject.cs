using System.Text.Json;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class DubbingProject
{
    public const int MaxNameLength = 200;

    public const int MaxDescriptionLength = 2000;

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

    public string? Name { get; private set; }

    public string? Description { get; private set; }

    public Guid? OwnerUserId { get; private set; }

    public Guid? CreatedByUserId { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public int SettingsVersion { get; private set; }

    public string? ProcessingSettingsJson { get; private set; }

    private DubbingProject()
    {
        SourceLanguage = string.Empty;
        TargetLanguage = string.Empty;
        SettingsJson = string.Empty;
        ConfigurationHash = string.Empty;
        SettingsVersion = 1;
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
        DateTimeOffset updatedAt,
        string? name = null,
        string? description = null,
        Guid? ownerUserId = null,
        Guid? createdByUserId = null,
        Guid? updatedByUserId = null,
        bool isArchived = false,
        DateTimeOffset? archivedAt = null,
        int settingsVersion = 1,
        string? processingSettingsJson = null)
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
        Name = string.IsNullOrWhiteSpace(name) ? "Untitled project" : name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description;
        OwnerUserId = ownerUserId;
        CreatedByUserId = createdByUserId;
        UpdatedByUserId = updatedByUserId;
        IsArchived = isArchived;
        ArchivedAt = archivedAt;
        SettingsVersion = settingsVersion;
        ProcessingSettingsJson = processingSettingsJson;

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

        if (Name is not null)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new DomainException("DubbingProject Name must not be empty when set.");
            }

            if (Name.Length > MaxNameLength)
            {
                throw new DomainException($"DubbingProject Name must be at most {MaxNameLength} chars.");
            }
        }

        if (Description is not null)
        {
            if (string.IsNullOrWhiteSpace(Description))
            {
                throw new DomainException("DubbingProject Description must not be empty when set.");
            }

            if (Description.Length > MaxDescriptionLength)
            {
                throw new DomainException($"DubbingProject Description must be at most {MaxDescriptionLength} chars.");
            }
        }

        if (OwnerUserId.HasValue && OwnerUserId.Value == Guid.Empty)
        {
            throw new DomainException("DubbingProject OwnerUserId must not be empty when set.");
        }

        if (CreatedByUserId.HasValue && CreatedByUserId.Value == Guid.Empty)
        {
            throw new DomainException("DubbingProject CreatedByUserId must not be empty when set.");
        }

        if (UpdatedByUserId.HasValue && UpdatedByUserId.Value == Guid.Empty)
        {
            throw new DomainException("DubbingProject UpdatedByUserId must not be empty when set.");
        }

        if (!IsArchived && ArchivedAt.HasValue)
        {
            throw new DomainException("DubbingProject ArchivedAt must be null when IsArchived is false.");
        }

        if (ArchivedAt.HasValue && ArchivedAt.Value < CreatedAt)
        {
            throw new DomainException("DubbingProject ArchivedAt must not be before CreatedAt.");
        }

        if (SettingsVersion < 1)
        {
            throw new DomainException("DubbingProject SettingsVersion must be at least 1.");
        }

        if (ProcessingSettingsJson is not null)
        {
            if (string.IsNullOrWhiteSpace(ProcessingSettingsJson))
            {
                throw new DomainException("DubbingProject ProcessingSettingsJson must not be empty when set.");
            }

            try
            {
                using var _ = JsonDocument.Parse(ProcessingSettingsJson);
            }
            catch (JsonException ex)
            {
                throw new DomainException("DubbingProject ProcessingSettingsJson must be valid JSON.", ex);
            }
        }
    }

    public void Rename(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("DubbingProject Name must not be empty.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException($"DubbingProject Name must be at most {MaxNameLength} chars.");
        }

        if (description is not null)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                throw new DomainException("DubbingProject Description must not be empty when set.");
            }

            if (description.Length > MaxDescriptionLength)
            {
                throw new DomainException($"DubbingProject Description must be at most {MaxDescriptionLength} chars.");
            }
        }

        Name = trimmed;
        Description = string.IsNullOrWhiteSpace(description) ? null : description;
        UpdatedAt = DateTimeOffset.UtcNow;

        Validate();
    }

    public void SetOwnership(Guid? ownerUserId, Guid? updatedByUserId)
    {
        if (ownerUserId.HasValue && ownerUserId.Value == Guid.Empty)
        {
            throw new DomainException("DubbingProject OwnerUserId must not be empty when set.");
        }

        if (updatedByUserId.HasValue && updatedByUserId.Value == Guid.Empty)
        {
            throw new DomainException("DubbingProject UpdatedByUserId must not be empty when set.");
        }

        OwnerUserId = ownerUserId;
        UpdatedByUserId = updatedByUserId;
        UpdatedAt = DateTimeOffset.UtcNow;

        Validate();
    }

    public void Archive(DateTimeOffset archivedAt)
    {
        if (archivedAt < CreatedAt)
        {
            throw new DomainException("DubbingProject ArchivedAt must not be before CreatedAt.");
        }

        IsArchived = true;
        ArchivedAt = archivedAt;
        UpdatedAt = DateTimeOffset.UtcNow;

        Validate();
    }

    public void Unarchive()
    {
        IsArchived = false;
        ArchivedAt = null;
        UpdatedAt = DateTimeOffset.UtcNow;

        Validate();
    }

    public void UpdateProcessingSettings(string processingSettingsJson, int settingsVersion)
    {
        if (string.IsNullOrWhiteSpace(processingSettingsJson))
        {
            throw new DomainException("DubbingProject ProcessingSettingsJson must not be empty.");
        }

        if (settingsVersion < 1)
        {
            throw new DomainException("DubbingProject SettingsVersion must be at least 1.");
        }

        try
        {
            using var _ = JsonDocument.Parse(processingSettingsJson);
        }
        catch (JsonException ex)
        {
            throw new DomainException("DubbingProject ProcessingSettingsJson must be valid JSON.", ex);
        }

        ProcessingSettingsJson = processingSettingsJson;
        SettingsVersion = settingsVersion;
        UpdatedAt = DateTimeOffset.UtcNow;

        Validate();
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
