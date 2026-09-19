using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Rendered-output downloads nested under projects:
/// <c>GET /api/v1/projects/{projectId}/output/download</c>.
/// Issues 15-minute signed URLs only when a completed output exists;
/// otherwise returns structured errors (404 ARTIFACT_UNAVAILABLE when no
/// output row, never 501).
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId}/output")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class OutputController : ControllerBase
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;

    public OutputController(IStageExecutionContextFactory contextFactory, ArtifactService artifacts)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
    }

    [HttpGet("download")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(DownloadUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(
        [FromRoute] string projectId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            // Core download selects the latest RenderedOutput asset only.
            // Optional enrichment (Task 042) registers separate Video/mp4
            // OutputAssets for annotated previews; they must never shadow the
            // core render. Filtering by ArtifactType.Enrichment exclusion keeps
            // core completion/download identical when enrichment is enabled.
            var output = await (
                from o in db.Set<OutputAsset>().AsNoTracking()
                join a in db.Set<Artifact>().AsNoTracking() on o.ArtifactId equals a.Id
                where o.ProjectId == projectGuid && a.Type == ArtifactType.RenderedOutput
                orderby o.CreatedAt descending
                select o).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (output is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, $"Project '{projectGuid}' has no rendered output available for download.");
            }

            var url = await _artifacts.GetDownloadUrlAsync(
                tenantId, projectGuid, output.ArtifactId, SignedUrlPolicy.DefaultExpiry, cancellationToken).ConfigureAwait(false);
            var expiresAt = DateTimeOffset.UtcNow.Add(SignedUrlPolicy.DefaultExpiry);
            return Ok(new DownloadUrlResponse(url, expiresAt));
        }
    }

    private async Task RequireProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }
    }
}
