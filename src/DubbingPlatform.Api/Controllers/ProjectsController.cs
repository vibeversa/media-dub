using System.Text.Json;
using DubbingPlatform.Api.Filters;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Infrastructure.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Project CRUD. POST creates with race-safe idempotency
/// (<c>Idempotency-Key</c>, 7-day retention) and audits; GET single
/// distinguishes 404 (missing/deleted) from 403 (cross-tenant); GET list
/// returns the pagination envelope; DELETE is a logical delete
/// (<c>IsDeleted</c> shadow, default <c>false</c>; physical removal belongs
/// to Task 037) returning 202 plus audit. Ids accept raw GUIDs and
/// <c>prj_</c> prefixed ids; responses expose <c>prj_</c>.
/// </summary>
[ApiController]
[Route("api/v1/projects")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class ProjectsController : ControllerBase
{
    private readonly ProjectService _projects;

    public ProjectsController(ProjectService projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        _projects = projects;
    }

    /// <summary>
    /// Creates a project. Idempotent on <c>Idempotency-Key</c> (same key+body
    /// replays the stored 201; same key+different body → 409 CONFLICT).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateProjectRequest request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var settingsJson = request.Settings.HasValue
            ? request.Settings.Value.GetRawText()
            : "{}";

        var project = await _projects.CreateAsync(
            tenantId, request.SourceLanguage, request.TargetLanguage,
            settingsJson, User.GetSubject(), cancellationToken).ConfigureAwait(false);
        PlatformMetrics.ProjectStarted(tenantId);

        var response = ProjectResponse.From(project);
        return CreatedAtAction(nameof(GetById), new { projectId = response.Id }, response);
    }

    /// <summary>
    /// Lists projects for the current tenant in the pagination envelope.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<ProjectResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PaginationParams? query, CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        query ??= new PaginationParams();
        query.Normalize();
        var (page, pageSize) = query.Normalized();

        var (items, total) = await _projects.ListAsync(tenantId, page, pageSize, cancellationToken).ConfigureAwait(false);
        var responses = items.Select(ProjectResponse.From).ToList();
        return Ok(PaginatedResult<ProjectResponse>.Create(responses, page, pageSize, total));
    }

    /// <summary>
    /// Gets a project by id (raw GUID or <c>prj_</c>). Cross-tenant ids
    /// return 403 FORBIDDEN (not 404); deleted/missing return 404 NOT_FOUND.
    /// </summary>
    [HttpGet("{projectId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById([FromRoute] string projectId, CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var id = PublicIdParser.ParseProjectId(projectId);
        var project = await _projects.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        return Ok(ProjectResponse.From(project));
    }

    /// <summary>
    /// Logically deletes a project (202 + audit). Subsequent reads return 404.
    /// </summary>
    [HttpDelete("{projectId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectOwner)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(DeleteProjectResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(
        [FromRoute] string projectId,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var id = PublicIdParser.ParseProjectId(projectId);
        await _projects.DeleteAsync(tenantId, id, User.GetSubject(), cancellationToken).ConfigureAwait(false);
        PlatformMetrics.ProjectCancelled(tenantId);
        return Accepted(new DeleteProjectResponse(PublicIdParser.ToProjectId(id), true));
    }
}
