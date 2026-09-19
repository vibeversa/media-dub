using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ProviderExecution
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public Guid? StageExecutionId { get; private set; }

    public ProviderType Provider { get; private set; }

    public ProviderCapability Capability { get; private set; }

    public string Model { get; private set; }

    public string? ModelVersion { get; private set; }

    public string? Deployment { get; private set; }

    public string? Region { get; private set; }

    public string? ApiVersion { get; private set; }

    public int Attempt { get; private set; }

    public string RequestHash { get; private set; }

    public string? ResponseHash { get; private set; }

    public long LatencyMs { get; private set; }

    public int? TokensIn { get; private set; }

    public int? TokensOut { get; private set; }

    public double? AudioSeconds { get; private set; }

    public double? EstimatedCost { get; private set; }

    public double? ActualCost { get; private set; }

    public string? PriceTableVersion { get; private set; }

    public OutcomeClass Outcome { get; private set; }

    public string? FallbackReason { get; private set; }

    public string? PromptTemplateId { get; private set; }

    public string? PromptHash { get; private set; }

    public string? VoiceProfileVersion { get; private set; }

    public string? ExternalJobId { get; private set; }

    public string? ProviderIdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ProviderExecution()
    {
        Model = string.Empty;
        RequestHash = string.Empty;
    }

    public ProviderExecution(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        Guid? stageExecutionId,
        ProviderType provider,
        ProviderCapability capability,
        string model,
        string? modelVersion,
        string? deployment,
        string? region,
        string? apiVersion,
        int attempt,
        string requestHash,
        string? responseHash,
        long latencyMs,
        int? tokensIn,
        int? tokensOut,
        double? audioSeconds,
        double? estimatedCost,
        double? actualCost,
        string? priceTableVersion,
        OutcomeClass outcome,
        string? fallbackReason,
        string? promptTemplateId,
        string? promptHash,
        string? voiceProfileVersion,
        string? externalJobId,
        string? providerIdempotencyKey,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        StageExecutionId = stageExecutionId;
        Provider = provider;
        Capability = capability;
        Model = model;
        ModelVersion = modelVersion;
        Deployment = deployment;
        Region = region;
        ApiVersion = apiVersion;
        Attempt = attempt;
        RequestHash = requestHash;
        ResponseHash = responseHash;
        LatencyMs = latencyMs;
        TokensIn = tokensIn;
        TokensOut = tokensOut;
        AudioSeconds = audioSeconds;
        EstimatedCost = estimatedCost;
        ActualCost = actualCost;
        PriceTableVersion = priceTableVersion;
        Outcome = outcome;
        FallbackReason = fallbackReason;
        PromptTemplateId = promptTemplateId;
        PromptHash = promptHash;
        VoiceProfileVersion = voiceProfileVersion;
        ExternalJobId = externalJobId;
        ProviderIdempotencyKey = providerIdempotencyKey;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ProviderExecution Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ProviderExecution TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("ProviderExecution ProjectId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("ProviderExecution ProcessingRunId must not be empty.");
        }

        if (StageExecutionId.HasValue && StageExecutionId.Value == Guid.Empty)
        {
            throw new DomainException("ProviderExecution StageExecutionId must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new DomainException("ProviderExecution Model must not be empty.");
        }

        if (Attempt < 0)
        {
            throw new DomainException("ProviderExecution Attempt must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(RequestHash))
        {
            throw new DomainException("ProviderExecution RequestHash must not be empty.");
        }

        if (LatencyMs < 0)
        {
            throw new DomainException("ProviderExecution LatencyMs must be >= 0.");
        }

        if (TokensIn.HasValue && TokensIn.Value < 0)
        {
            throw new DomainException("ProviderExecution TokensIn must be >= 0.");
        }

        if (TokensOut.HasValue && TokensOut.Value < 0)
        {
            throw new DomainException("ProviderExecution TokensOut must be >= 0.");
        }

        if (AudioSeconds.HasValue && (double.IsNaN(AudioSeconds.Value) || AudioSeconds.Value < 0.0))
        {
            throw new DomainException("ProviderExecution AudioSeconds must be >= 0.");
        }

        if (EstimatedCost.HasValue && (double.IsNaN(EstimatedCost.Value) || EstimatedCost.Value < 0.0))
        {
            throw new DomainException("ProviderExecution EstimatedCost must be >= 0.");
        }

        if (ActualCost.HasValue && (double.IsNaN(ActualCost.Value) || ActualCost.Value < 0.0))
        {
            throw new DomainException("ProviderExecution ActualCost must be >= 0.");
        }

        if (ModelVersion is not null && string.IsNullOrWhiteSpace(ModelVersion))
        {
            throw new DomainException("ProviderExecution ModelVersion must not be empty when set.");
        }

        if (Deployment is not null && string.IsNullOrWhiteSpace(Deployment))
        {
            throw new DomainException("ProviderExecution Deployment must not be empty when set.");
        }

        if (Region is not null && string.IsNullOrWhiteSpace(Region))
        {
            throw new DomainException("ProviderExecution Region must not be empty when set.");
        }

        if (ApiVersion is not null && string.IsNullOrWhiteSpace(ApiVersion))
        {
            throw new DomainException("ProviderExecution ApiVersion must not be empty when set.");
        }

        if (ResponseHash is not null && string.IsNullOrWhiteSpace(ResponseHash))
        {
            throw new DomainException("ProviderExecution ResponseHash must not be empty when set.");
        }

        if (PriceTableVersion is not null && string.IsNullOrWhiteSpace(PriceTableVersion))
        {
            throw new DomainException("ProviderExecution PriceTableVersion must not be empty when set.");
        }

        if (FallbackReason is not null && string.IsNullOrWhiteSpace(FallbackReason))
        {
            throw new DomainException("ProviderExecution FallbackReason must not be empty when set.");
        }

        if (PromptTemplateId is not null && string.IsNullOrWhiteSpace(PromptTemplateId))
        {
            throw new DomainException("ProviderExecution PromptTemplateId must not be empty when set.");
        }

        if (PromptHash is not null && string.IsNullOrWhiteSpace(PromptHash))
        {
            throw new DomainException("ProviderExecution PromptHash must not be empty when set.");
        }

        if (VoiceProfileVersion is not null && string.IsNullOrWhiteSpace(VoiceProfileVersion))
        {
            throw new DomainException("ProviderExecution VoiceProfileVersion must not be empty when set.");
        }

        if (ExternalJobId is not null && string.IsNullOrWhiteSpace(ExternalJobId))
        {
            throw new DomainException("ProviderExecution ExternalJobId must not be empty when set.");
        }

        if (ProviderIdempotencyKey is not null && string.IsNullOrWhiteSpace(ProviderIdempotencyKey))
        {
            throw new DomainException("ProviderExecution ProviderIdempotencyKey must not be empty when set.");
        }
    }
}
