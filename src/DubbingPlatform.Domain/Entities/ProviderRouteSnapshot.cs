using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ProviderRouteSnapshot
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public string RouteConfigHash { get; private set; }

    public string CapabilityHash { get; private set; }

    public string PrivacyHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ProviderRouteSnapshot()
    {
        RouteConfigHash = string.Empty;
        CapabilityHash = string.Empty;
        PrivacyHash = string.Empty;
    }

    public ProviderRouteSnapshot(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        string routeConfigHash,
        string capabilityHash,
        string privacyHash,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        RouteConfigHash = routeConfigHash;
        CapabilityHash = capabilityHash;
        PrivacyHash = privacyHash;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ProviderRouteSnapshot Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ProviderRouteSnapshot TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("ProviderRouteSnapshot ProjectId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("ProviderRouteSnapshot ProcessingRunId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(RouteConfigHash))
        {
            throw new DomainException("ProviderRouteSnapshot RouteConfigHash must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(CapabilityHash))
        {
            throw new DomainException("ProviderRouteSnapshot CapabilityHash must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(PrivacyHash))
        {
            throw new DomainException("ProviderRouteSnapshot PrivacyHash must not be empty.");
        }
    }
}
