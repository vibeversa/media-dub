using System.Text;
using System.Text.Json;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class UserPreference
{
    public const int MaxValueBytes = 4096;

    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "locale",
        "timezone",
        "theme",
        "defaultProjectFilters",
        "timelineZoom",
        "notificationPreferences",
    };

    private static readonly string[] SecretPropertyFragments =
    [
        "secret", "password", "passwd", "pwd", "token", "credential", "private_key", "api_key", "apikey", "client_secret",
    ];

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string Key { get; private set; }

    public string ValueJson { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private UserPreference()
    {
        Key = string.Empty;
        ValueJson = string.Empty;
    }

    public UserPreference(
        Guid tenantId,
        Guid userId,
        string key,
        string valueJson,
        DateTimeOffset updatedAt)
    {
        TenantId = tenantId;
        UserId = userId;
        Key = key;
        ValueJson = valueJson;
        UpdatedAt = updatedAt;

        Validate();
    }

    public void Validate()
    {
        if (TenantId == Guid.Empty)
        {
            throw new DomainException("UserPreference TenantId must not be empty.");
        }

        if (UserId == Guid.Empty)
        {
            throw new DomainException("UserPreference UserId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new DomainException("UserPreference Key must not be empty.");
        }

        if (!AllowedKeys.Contains(Key))
        {
            throw new DomainException($"UserPreference Key '{Key}' is not allowed.");
        }

        if (ValueJson is null)
        {
            throw new DomainException("UserPreference ValueJson must not be null.");
        }

        var byteCount = Encoding.UTF8.GetByteCount(ValueJson);
        if (byteCount > MaxValueBytes)
        {
            throw new DomainException($"UserPreference ValueJson must be at most {MaxValueBytes} bytes.");
        }

        ThrowIfSecretObject(ValueJson);
    }

    public void UpdateValue(string valueJson, DateTimeOffset updatedAt)
    {
        ValueJson = valueJson;
        UpdatedAt = updatedAt;
        Validate();
    }

    private static void ThrowIfSecretObject(string valueJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(valueJson);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                foreach (var fragment in SecretPropertyFragments)
                {
                    if (property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DomainException(
                            $"UserPreference ValueJson must not contain secret material (property '{property.Name}').");
                    }
                }
            }
        }
    }
}
