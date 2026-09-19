using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class VoiceProfile
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Provider { get; private set; }

    public string VoiceId { get; private set; }

    public string VoiceVersion { get; private set; }

    public string Language { get; private set; }

    public VoiceType Type { get; private set; }

    public bool CloningEnabled { get; private set; }

    public string? ModelRefJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private VoiceProfile()
    {
        Provider = string.Empty;
        VoiceId = string.Empty;
        VoiceVersion = string.Empty;
        Language = string.Empty;
    }

    public VoiceProfile(
        Guid id,
        Guid tenantId,
        string provider,
        string voiceId,
        string voiceVersion,
        string language,
        VoiceType type,
        bool cloningEnabled,
        string? modelRefJson,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Provider = provider;
        VoiceId = voiceId;
        VoiceVersion = voiceVersion;
        Language = language;
        Type = type;
        CloningEnabled = cloningEnabled;
        ModelRefJson = modelRefJson;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("VoiceProfile Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("VoiceProfile TenantId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new DomainException("VoiceProfile Provider must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(VoiceId))
        {
            throw new DomainException("VoiceProfile VoiceId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(VoiceVersion))
        {
            throw new DomainException("VoiceProfile VoiceVersion must not be empty.");
        }

        if (!IsValidLanguageCode(Language))
        {
            throw new DomainException("VoiceProfile Language must be a 2-3 letter code.");
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
