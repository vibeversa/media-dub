using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Domain.Entities;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Builds <see cref="ProviderRouteSnapshot"/> rows. Route, capability, and
/// privacy inputs are hashed separately via
/// <see cref="ConfigurationHashCalculator"/> (secrets stripped) so auditors can
/// tell which input changed without seeing values.
/// </summary>
public static class ProviderRouteSnapshotBuilder
{
    public static ProviderRouteSnapshot Build(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        object? routeConfig,
        object? descriptors,
        object? privacy)
    {
        if (tenantId == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("TenantId must not be empty.");
        }

        if (projectId == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("ProjectId must not be empty.");
        }

        if (runId == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("ProcessingRunId must not be empty.");
        }

        return new ProviderRouteSnapshot(
            Guid.NewGuid(),
            tenantId,
            projectId,
            runId,
            ConfigurationHashCalculator.Compute(routeConfig),
            ConfigurationHashCalculator.Compute(descriptors),
            ConfigurationHashCalculator.Compute(privacy),
            DateTimeOffset.UtcNow);
    }
}
