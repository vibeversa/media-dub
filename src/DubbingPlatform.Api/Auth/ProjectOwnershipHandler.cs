using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Api.Auth;

/// <summary>
/// Resource requirement for project-tenant ownership.
/// </summary>
public sealed class ProjectOwnershipRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Verifies <c>route project.TenantId == claim tid</c>, else 403.
/// Looks for <c>projectId</c> or, on the projects controller, <c>id</c> route
/// values; loads the project with a maintenance scope (bypassing RLS so a
/// cross-tenant id yields 403 rather than 404); missing/invalid claims fail.
/// Non-project routes, missing projects, and database errors succeed here and
/// let the controller return 404/500 (never leak existence via 403 on DB outage).
/// Controllers additionally enforce ownership explicitly (defense in depth).
/// </summary>
public sealed class ProjectOwnershipHandler : AuthorizationHandler<ProjectOwnershipRequirement>
{
    private readonly IStageExecutionContextFactory _contextFactory;

    public ProjectOwnershipHandler(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ProjectOwnershipRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext)
        {
            context.Succeed(requirement);
            return;
        }

        if (!httpContext.User.TryGetTenantId(out var tenantId))
        {
            context.Fail();
            return;
        }

        var projectId = ResolveProjectId(httpContext);
        if (projectId is null)
        {
            context.Succeed(requirement);
            return;
        }

        try
        {
            Guid? owner;
            using (TenantContext.BeginMaintenanceScope())
            {
                using var db = _contextFactory.CreateDbContext();
                owner = await db.Set<DubbingProject>()
                    .Where(p => p.Id == projectId.Value)
                    .Select(p => (Guid?)p.TenantId)
                    .FirstOrDefaultAsync(httpContext.RequestAborted).ConfigureAwait(false);
            }

            if (owner is null)
            {
                context.Succeed(requirement);
                return;
            }

            if (owner.Value != tenantId)
            {
                context.Fail();
                return;
            }

            context.Succeed(requirement);
        }
#pragma warning disable CA1031 // Ownership probe must never turn a DB outage into a 403; controllers decide.
        catch (Exception)
        {
            context.Succeed(requirement);
        }
#pragma warning restore CA1031
    }

    internal static Guid? ResolveProjectId(HttpContext httpContext)
    {
        var values = httpContext.Request.RouteValues;
        foreach (var key in new[] { "projectId", "id" })
        {
            if (values.TryGetValue(key, out var raw) && raw is not null
                && Guid.TryParse(raw.ToString()?.Trim(), out var parsed) && parsed != Guid.Empty)
            {
                if (string.Equals(key, "id", StringComparison.Ordinal))
                {
                    var endpoint = httpContext.GetEndpoint();
                    var controller = (endpoint?.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()?.ControllerName)
                        ?? string.Empty;
                    if (!controller.Contains("Project", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                return parsed;
            }
        }

        return null;
    }
}
