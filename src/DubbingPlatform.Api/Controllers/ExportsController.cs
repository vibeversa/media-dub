using DubbingPlatform.Api.Filters;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Exports;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Export jobs nested under projects:
/// <c>POST /api/v1/projects/{projectId}/exports</c> (202 + audit +
/// <c>ExportJobRequested</c> best effort; 409 EXPORT_NOT_READY when no
/// exportable run or zero segments),
/// <c>GET .../exports</c> (200 paginated),
/// <c>GET .../exports/{exportId}</c> (200 with status + completeness),
/// <c>GET .../exports/{exportId}/download</c> (200 presigned 15min or 409).
/// Formats are kebab-case
/// (<c>srt|webvtt|json-timeline|speaker-metadata|transcript|translation|quality-report</c>).
/// Exports are on-demand and independent of the core DAG: the latest
/// <c>Completed</c> run is preferred, else the latest
/// <c>Failed/Cancelled/ManualReviewRequired</c> run with data (partial with
/// <c>isPartial:true</c> + completeness, never rendered media). Idempotency is
/// 24h per key via <see cref="IdempotencyFilter"/>.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId}/exports")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class ExportsController : ControllerBase
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ExportService _exports;
    private readonly IPublishEndpoint _publish;

    public ExportsController(
        IStageExecutionContextFactory contextFactory,
        ExportService exports,
        IPublishEndpoint publish)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(publish);
        _contextFactory = contextFactory;
        _exports = exports;
        _publish = publish;
    }

    [HttpPost]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ExportResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromRoute] string projectId,
        [FromBody] CreateExportRequest request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var format = ExportFormatParser.Parse(request.Format?.Trim());
        var exportId = await _exports.RequestAsync(
            tenantId, projectGuid, format, User.GetSubject(), cancellationToken).ConfigureAwait(false);

        var job = await _exports.GetAsync(tenantId, projectGuid, exportId, cancellationToken).ConfigureAwait(false);
        await PublishExportRequestedAsync(tenantId, projectGuid, job.ProcessingRunId, exportId, format, cancellationToken).ConfigureAwait(false);

        return Accepted(ToResponse(projectGuid, job));
    }

    [HttpGet]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<ExportResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        [FromRoute] string projectId,
        [FromQuery] PaginationParams? query,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        query ??= new PaginationParams();
        query.Normalize();
        var (page, pageSize) = query.Normalized();

        var (items, total) = await _exports.ListAsync(tenantId, projectGuid, page, pageSize, cancellationToken).ConfigureAwait(false);
        var responses = items.Select(j => ToResponse(projectGuid, j)).ToList();
        return Ok(PaginatedResult<ExportResponse>.Create(responses, page, pageSize, total));
    }

    [HttpGet("{exportId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ExportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        [FromRoute] string projectId,
        [FromRoute] string exportId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var exportGuid = PublicIdParser.ParseExportId(exportId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var job = await _exports.GetAsync(tenantId, projectGuid, exportGuid, cancellationToken).ConfigureAwait(false);
        return Ok(ToResponse(projectGuid, job));
    }

    [HttpGet("{exportId}/download")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(DownloadUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Download(
        [FromRoute] string projectId,
        [FromRoute] string exportId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var exportGuid = PublicIdParser.ParseExportId(exportId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var (url, expiresAt) = await _exports.GetDownloadUrlAsync(tenantId, projectGuid, exportGuid, cancellationToken).ConfigureAwait(false);
        return Ok(new DownloadUrlResponse(url, expiresAt));
    }

    private static ExportResponse ToResponse(Guid projectId, ExportJob job)
    {
        return new ExportResponse(
            PublicIdParser.ToExportId(job.Id),
            PublicIdParser.ToProjectId(projectId),
            ExportFormatParser.ToWireName(job.Format),
            job.Status.ToString(),
            job.IsPartial,
            job.CreatedAt,
            job.CompletenessJson);
    }

    private async Task PublishExportRequestedAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid exportId,
        ExportFormat format,
        CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var message = new ExportJobRequested(
            Guid.NewGuid(), correlationId, tenantId, projectId, runId,
            null, null, null, null, null, 1, DateTimeOffset.UtcNow, 0,
            null, null, null,
            PublicIdParser.ToExportId(exportId), format.ToString());

        try
        {
            await _publish.Publish(message, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Publish is best effort: the DB commit already succeeded.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
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
