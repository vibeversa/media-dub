// GAP-020: WorkerHealthService exists but had no operator endpoint. Pin the
// route, the elevated authz, and the secret-free contract shape.
using System.Reflection;
using DubbingPlatform.Api.Controllers;
using DubbingPlatform.Application.Diagnostics;
using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DubbingPlatform.UnitTests.Diagnostics;

public sealed class WorkerHealthEndpointTests
{
    private static MethodInfo Endpoint() =>
        typeof(AdminController).GetMethod("GetDiagnosticsWorkers")
        ?? throw new InvalidOperationException("AdminController.GetDiagnosticsWorkers is missing.");

    [Fact]
    public void Diagnostics_Workers_Route_Is_Registered()
    {
        var route = Endpoint().GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(route);
        Assert.Contains("diagnostics/workers", route!.Template!, StringComparison.Ordinal);

        // Nested under the admin controller route so it stays operator-only.
        Assert.Equal("api/v1/admin", typeof(AdminController).GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void Diagnostics_Workers_Endpoint_Returns_Worker_Health()
    {
        var produces = Endpoint().GetCustomAttributes<ProducesResponseTypeAttribute>().ToList();
        Assert.Contains(produces, p => p.StatusCode == StatusCodes.Status200OK
            && (p.Type == typeof(IReadOnlyList<WorkerHealthDto>) || p.Type == typeof(List<WorkerHealthDto>)));
    }

    [Fact]
    public void Admin_Controller_Takes_The_Worker_Health_Service()
    {
        // The service is registered in DI (DiagnosticsRegistration); an endpoint
        // must actually consume it or the read stays unreachable.
        var ctor = typeof(AdminController).GetConstructors().Single();
        Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(WorkerHealthService));
    }

    [Fact]
    public void Worker_Health_Response_Is_Secret_Free()
    {
        // Only ids, worker names, statuses, counts, and timestamps.
        var names = typeof(WorkerHealthDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(
            ["CorrelationId", "Worker", "Status", "LastHeartbeatAt", "ActiveJobs", "Version"],
            names);
    }

    [Fact]
    public void Worker_Health_Status_Values_Are_Frozen()
    {
        Assert.Equal("Active", WorkerHealthService.StatusActive);
        Assert.Equal("Stale", WorkerHealthService.StatusStale);
        Assert.Equal("Unknown", WorkerHealthService.StatusUnknown);
    }
}