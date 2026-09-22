using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Projects;

/// <summary>
/// Settings-change guard: a <c>PATCH</c> carrying <c>processingSettings</c> is
/// rejected with 409 <c>SETTINGS_LOCKED_ACTIVE_RUN</c> while any active run
/// exists for the project, and <c>DELETE</c> is rejected with 409
/// <c>PROJECT_HAS_ACTIVE_RUN</c> under the same condition. Active means
/// <c>Pending|Running|Cancelling|ManualReviewRequired</c> (matches quota
/// active-run definition). All queries are tenant-scoped; cross-tenant runs
/// never lock.
/// </summary>
public sealed class ProjectSettingsGuard
{
    public static readonly ProcessingRunStatus[] ActiveRunStatuses =
    [
        ProcessingRunStatus.Pending,
        ProcessingRunStatus.Running,
        ProcessingRunStatus.Cancelling,
        ProcessingRunStatus.ManualReviewRequired,
    ];

    private readonly IStageExecutionContextFactory _contextFactory;

    public ProjectSettingsGuard(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Determines whether the project has any active run.
    /// </summary>
    public async Task<bool> HasActiveRunAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken = default)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId && ActiveRunStatuses.Contains(r.Status))
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Throws <see cref="SettingsLockedActiveRunException"/> when a settings
    /// change must be blocked.
    /// </summary>
    public async Task ThrowIfSettingsLockedAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken = default)
    {
        if (await HasActiveRunAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false))
        {
            throw new SettingsLockedActiveRunException($"Project '{projectId}' has an active run; settings changes are locked.");
        }
    }

    /// <summary>
    /// Throws <see cref="ProjectHasActiveRunException"/> when a delete must be blocked.
    /// </summary>
    public async Task ThrowIfDeleteBlockedAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken = default)
    {
        if (await HasActiveRunAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false))
        {
            throw new ProjectHasActiveRunException($"Project '{projectId}' has an active run and cannot be deleted.");
        }
    }

    /// <summary>
    /// Pure active-status check for hermetic tests.
    /// </summary>
    public static bool IsActiveStatus(ProcessingRunStatus status)
    {
        return ActiveRunStatuses.Contains(status);
    }
}
