using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.MultiTenancy;

/// <summary>
/// Async-local holder for the current tenant. Flows with async execution and is
/// consumed by the persistence tenant-session interceptor (PostgreSQL RLS) and by
/// <c>AppDbContext</c> query filters. RLS remains the primary enforcement mechanism;
/// application query filters are defense in depth.
/// </summary>
public static class TenantContext
{
    private static readonly AsyncLocal<Guid?> CurrentId = new();

    private static readonly AsyncLocal<bool> MaintenanceFlag = new();

    /// <summary>
    /// Gets the current tenant id, or null when no tenant scope is active.
    /// </summary>
    public static Guid? CurrentTenantId => CurrentId.Value;

    /// <summary>
    /// Gets a value indicating whether the current scope is a maintenance scope
    /// that bypasses tenant isolation. Maintenance work must use a dedicated role.
    /// </summary>
    public static bool IsMaintenance => MaintenanceFlag.Value;

    /// <summary>
    /// Sets the current tenant for the async flow.
    /// </summary>
    public static void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        CurrentId.Value = tenantId;
        MaintenanceFlag.Value = false;
    }

    /// <summary>
    /// Begins a tenant scope, restoring the previous scope on dispose.
    /// </summary>
    public static IDisposable BeginScope(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        var previousId = CurrentId.Value;
        var previousMaintenance = MaintenanceFlag.Value;
        CurrentId.Value = tenantId;
        MaintenanceFlag.Value = false;
        return new Scope(() =>
        {
            CurrentId.Value = previousId;
            MaintenanceFlag.Value = previousMaintenance;
        });
    }

    /// <summary>
    /// Begins a maintenance scope that bypasses tenant isolation.
    /// Reserved for migration, reconciliation, and retention workers using a dedicated role.
    /// </summary>
    public static IDisposable BeginMaintenanceScope()
    {
        var previousId = CurrentId.Value;
        var previousMaintenance = MaintenanceFlag.Value;
        CurrentId.Value = null;
        MaintenanceFlag.Value = true;
        return new Scope(() =>
        {
            CurrentId.Value = previousId;
            MaintenanceFlag.Value = previousMaintenance;
        });
    }

    /// <summary>
    /// Clears the current tenant and maintenance flag for the async flow.
    /// </summary>
    public static void Clear()
    {
        CurrentId.Value = null;
        MaintenanceFlag.Value = false;
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            restore();
        }
    }
}
