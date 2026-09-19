using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ProviderCapabilityDescriptor
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public ProviderType Provider { get; private set; }

    public ProviderCapability Capability { get; private set; }

    public string[] SupportedLanguages { get; private set; }

    public string[] SupportedFormats { get; private set; }

    public long MaxInputBytes { get; private set; }

    public int MaxDurationMs { get; private set; }

    public bool Batching { get; private set; }

    public bool AsyncJob { get; private set; }

    public bool WordTimestamps { get; private set; }

    public bool Diarization { get; private set; }

    public string[] VoiceInventory { get; private set; }

    public bool VoiceCloning { get; private set; }

    public double[] TimingControls { get; private set; }

    public string ConfidenceSemantics { get; private set; }

    public string RateLimitDimsJson { get; private set; }

    public string CostDimsJson { get; private set; }

    public string PrivacyClass { get; private set; }

    public string Region { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ProviderCapabilityDescriptor()
    {
        SupportedLanguages = Array.Empty<string>();
        SupportedFormats = Array.Empty<string>();
        VoiceInventory = Array.Empty<string>();
        TimingControls = Array.Empty<double>();
        ConfidenceSemantics = string.Empty;
        RateLimitDimsJson = string.Empty;
        CostDimsJson = string.Empty;
        PrivacyClass = string.Empty;
        Region = string.Empty;
    }

    public ProviderCapabilityDescriptor(
        Guid id,
        Guid tenantId,
        ProviderType provider,
        ProviderCapability capability,
        string[] supportedLanguages,
        string[] supportedFormats,
        long maxInputBytes,
        int maxDurationMs,
        bool batching,
        bool asyncJob,
        bool wordTimestamps,
        bool diarization,
        string[] voiceInventory,
        bool voiceCloning,
        double[] timingControls,
        string confidenceSemantics,
        string rateLimitDimsJson,
        string costDimsJson,
        string privacyClass,
        string region,
        int version,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Provider = provider;
        Capability = capability;
        SupportedLanguages = supportedLanguages;
        SupportedFormats = supportedFormats;
        MaxInputBytes = maxInputBytes;
        MaxDurationMs = maxDurationMs;
        Batching = batching;
        AsyncJob = asyncJob;
        WordTimestamps = wordTimestamps;
        Diarization = diarization;
        VoiceInventory = voiceInventory;
        VoiceCloning = voiceCloning;
        TimingControls = timingControls;
        ConfidenceSemantics = confidenceSemantics;
        RateLimitDimsJson = rateLimitDimsJson;
        CostDimsJson = costDimsJson;
        PrivacyClass = privacyClass;
        Region = region;
        Version = version;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ProviderCapabilityDescriptor Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ProviderCapabilityDescriptor TenantId must not be empty.");
        }

        if (SupportedLanguages is null)
        {
            throw new DomainException("ProviderCapabilityDescriptor SupportedLanguages must not be null.");
        }

        if (SupportedFormats is null)
        {
            throw new DomainException("ProviderCapabilityDescriptor SupportedFormats must not be null.");
        }

        if (MaxInputBytes < 0)
        {
            throw new DomainException("ProviderCapabilityDescriptor MaxInputBytes must be >= 0.");
        }

        if (MaxDurationMs < 0)
        {
            throw new DomainException("ProviderCapabilityDescriptor MaxDurationMs must be >= 0.");
        }

        if (VoiceInventory is null)
        {
            throw new DomainException("ProviderCapabilityDescriptor VoiceInventory must not be null.");
        }

        if (TimingControls is null)
        {
            throw new DomainException("ProviderCapabilityDescriptor TimingControls must not be null.");
        }

        if (string.IsNullOrWhiteSpace(ConfidenceSemantics))
        {
            throw new DomainException("ProviderCapabilityDescriptor ConfidenceSemantics must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(RateLimitDimsJson))
        {
            throw new DomainException("ProviderCapabilityDescriptor RateLimitDimsJson must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(CostDimsJson))
        {
            throw new DomainException("ProviderCapabilityDescriptor CostDimsJson must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(PrivacyClass))
        {
            throw new DomainException("ProviderCapabilityDescriptor PrivacyClass must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Region))
        {
            throw new DomainException("ProviderCapabilityDescriptor Region must not be empty.");
        }

        if (Version < 1)
        {
            throw new DomainException("ProviderCapabilityDescriptor Version must be >= 1.");
        }
    }
}
