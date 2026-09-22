using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Dashboard;
using DubbingPlatform.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Dashboard summary: <c>GET /api/v1/dashboard/summary</c> returns all seven
/// sections in one 200 response (project counts, recent outputs max 5,
/// storage, cost month-to-date, quota remaining/resetsAt, warnings with
/// machine-readable codes, backlog). Single aggregated tenant-scoped query via
/// <see cref="DashboardService"/>; no per-invoice or storage-key detail.
/// Example: <c>{ projectCounts: { active: 1, archived: 0, total: 1 },
/// recentOutputs: [], storage: { usedBytes: 0, quotaBytes: 107374182400 },
/// cost: { monthToDate: 0, currency: "USD" },
/// quota: { remaining: 50, resetsAt: "..." }, warnings: [],
/// backlog: { pendingReviews: 0, runningJobs: 0 } }</c>.
/// Errors: 401 <c>UNAUTHORIZED</c>, 403 <c>FORBIDDEN</c>.
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class DashboardController : ControllerBase
{
    private readonly DashboardService _dashboard;

    public DashboardController(DashboardService dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        _dashboard = dashboard;
    }

    /// <summary>
    /// Returns the seven-section dashboard summary for the current tenant.
    /// </summary>
    [HttpGet("summary")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(DashboardSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var summary = await _dashboard.GetSummaryAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return Ok(DashboardSummaryResponse.From(summary));
    }
}
