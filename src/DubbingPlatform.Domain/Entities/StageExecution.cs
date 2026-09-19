using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class StageExecution
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public StageType StageType { get; private set; }

    public ScopeType ScopeType { get; private set; }

    public string ScopeId { get; private set; }

    public Guid? SegmentId { get; private set; }

    public int Attempt { get; private set; }

    public StageStatus Status { get; private set; }

    public string LeaseOwner { get; private set; }

    public string LeaseToken { get; private set; }

    public long LeaseTokenVersion { get; private set; }

    public DateTimeOffset LeaseExpiresAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? InputHash { get; private set; }

    public string ConfigurationHash { get; private set; }

    public string ExecutionSnapshotHash { get; private set; }

    public string? OutputArtifactIdsJson { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private StageExecution()
    {
        ScopeId = string.Empty;
        LeaseOwner = string.Empty;
        LeaseToken = string.Empty;
        ConfigurationHash = string.Empty;
        ExecutionSnapshotHash = string.Empty;
    }

    public StageExecution(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        StageType stageType,
        ScopeType scopeType,
        string scopeId,
        Guid? segmentId,
        int attempt,
        StageStatus status,
        string leaseOwner,
        string leaseToken,
        long leaseTokenVersion,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? inputHash,
        string configurationHash,
        string executionSnapshotHash,
        string? outputArtifactIdsJson,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        StageType = stageType;
        ScopeType = scopeType;
        ScopeId = scopeId;
        SegmentId = segmentId;
        Attempt = attempt;
        Status = status;
        LeaseOwner = leaseOwner;
        LeaseToken = leaseToken;
        LeaseTokenVersion = leaseTokenVersion;
        LeaseExpiresAt = leaseExpiresAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        InputHash = inputHash;
        ConfigurationHash = configurationHash;
        ExecutionSnapshotHash = executionSnapshotHash;
        OutputArtifactIdsJson = outputArtifactIdsJson;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("StageExecution Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("StageExecution TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("StageExecution ProjectId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("StageExecution ProcessingRunId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ScopeId))
        {
            throw new DomainException("StageExecution ScopeId must not be empty.");
        }

        if (SegmentId.HasValue && SegmentId.Value == Guid.Empty)
        {
            throw new DomainException("StageExecution SegmentId must not be empty when set.");
        }

        if (Attempt < 0)
        {
            throw new DomainException("StageExecution Attempt must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(LeaseOwner))
        {
            throw new DomainException("StageExecution LeaseOwner must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(LeaseToken))
        {
            throw new DomainException("StageExecution LeaseToken must not be empty.");
        }

        if (LeaseTokenVersion < 0)
        {
            throw new DomainException("StageExecution LeaseTokenVersion must be >= 0.");
        }

        if (StartedAt.HasValue && CompletedAt.HasValue && CompletedAt.Value < StartedAt.Value)
        {
            throw new DomainException("StageExecution CompletedAt must not be before StartedAt.");
        }

        if (InputHash is not null && string.IsNullOrWhiteSpace(InputHash))
        {
            throw new DomainException("StageExecution InputHash must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(ConfigurationHash))
        {
            throw new DomainException("StageExecution ConfigurationHash must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ExecutionSnapshotHash))
        {
            throw new DomainException("StageExecution ExecutionSnapshotHash must not be empty.");
        }

        if (OutputArtifactIdsJson is not null && string.IsNullOrWhiteSpace(OutputArtifactIdsJson))
        {
            throw new DomainException("StageExecution OutputArtifactIdsJson must not be empty when set.");
        }

        if (ErrorCode is not null && string.IsNullOrWhiteSpace(ErrorCode))
        {
            throw new DomainException("StageExecution ErrorCode must not be empty when set.");
        }

        if (ErrorMessage is not null && string.IsNullOrWhiteSpace(ErrorMessage))
        {
            throw new DomainException("StageExecution ErrorMessage must not be empty when set.");
        }
    }
}
