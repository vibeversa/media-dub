using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Creates short-lived <see cref="DbContext"/> instances for stage execution
/// work. Implemented in Infrastructure with <c>AppDbContext</c> so Application
/// never references Infrastructure (no circular dependency). Callers must
/// establish the <c>TenantContext</c> scope before calling
/// <see cref="CreateDbContext"/> so the new context captures the correct
/// tenant for query filters and row-level security.
/// </summary>
public interface IStageExecutionContextFactory
{
    /// <summary>
    /// Creates a new short-lived context. The caller owns disposal.
    /// </summary>
    DbContext CreateDbContext();
}
