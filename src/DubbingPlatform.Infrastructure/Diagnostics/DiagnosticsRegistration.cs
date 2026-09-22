using DubbingPlatform.Application.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DubbingPlatform.Infrastructure.Diagnostics;

/// <summary>
/// Diagnostics read-layer wiring. Registers the five query services plus the
/// service-layer access checker and the EF outbox backlog store as scoped.
/// Called by both the API host (Task 013 endpoints drive diagnostics) and the
/// worker host (operator support endpoints). No mutations, no consumers.
/// </summary>
public static class DiagnosticsRegistration
{
    public static void AddDubbingDiagnostics(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDiagnosticsAccessChecker, DiagnosticsAccessChecker>();
        services.AddScoped<IQueueBacklogStore, EfQueueBacklogStore>();
        services.AddScoped<ProviderHealthQueryService>();
        services.AddScoped<QueueDiagnosticsService>();
        services.AddScoped<LeaseOrphanService>();
        services.AddScoped<ReviewBacklogService>();
        services.AddScoped<WorkerHealthService>();
    }
}
