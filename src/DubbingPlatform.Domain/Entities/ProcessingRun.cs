using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ProcessingRun
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public int Attempt { get; private set; }

    public ProcessingRunStatus Status { get; private set; }

    public string PipelineVersion { get; private set; }

    public string ConfigurationHash { get; private set; }

    public string ProviderRouteHash { get; private set; }

    public string ExecutionSnapshotHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    private ProcessingRun()
    {
        PipelineVersion = string.Empty;
        ConfigurationHash = string.Empty;
        ProviderRouteHash = string.Empty;
        ExecutionSnapshotHash = string.Empty;
    }

    public ProcessingRun(
        Guid id,
        Guid tenantId,
        Guid projectId,
        int attempt,
        ProcessingRunStatus status,
        string pipelineVersion,
        string configurationHash,
        string providerRouteHash,
        string executionSnapshotHash,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        Attempt = attempt;
        Status = status;
        PipelineVersion = pipelineVersion;
        ConfigurationHash = configurationHash;
        ProviderRouteHash = providerRouteHash;
        ExecutionSnapshotHash = executionSnapshotHash;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ProcessingRun Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ProcessingRun TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("ProcessingRun ProjectId must not be empty.");
        }

        if (Attempt < 0)
        {
            throw new DomainException("ProcessingRun Attempt must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(PipelineVersion))
        {
            throw new DomainException("ProcessingRun PipelineVersion must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ConfigurationHash))
        {
            throw new DomainException("ProcessingRun ConfigurationHash must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ProviderRouteHash))
        {
            throw new DomainException("ProcessingRun ProviderRouteHash must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ExecutionSnapshotHash))
        {
            throw new DomainException("ProcessingRun ExecutionSnapshotHash must not be empty.");
        }

        if (StartedAt.HasValue && CompletedAt.HasValue && CompletedAt.Value < StartedAt.Value)
        {
            throw new DomainException("ProcessingRun CompletedAt must not be before StartedAt.");
        }
    }
}
