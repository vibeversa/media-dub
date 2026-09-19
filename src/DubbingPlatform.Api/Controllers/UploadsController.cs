using DubbingPlatform.Api.Filters;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Multipart upload sessions nested under projects:
/// <c>POST /api/v1/projects/{projectId}/uploads</c> (201),
/// <c>GET /api/v1/projects/{projectId}/uploads</c> (200 paginated),
/// <c>GET .../uploads/{uploadId}</c> (200 status, S3-authoritative parts),
/// <c>POST .../uploads/{uploadId}/parts</c> (200 presigned PUTs, 15min),
/// <c>POST .../uploads/{uploadId}/complete</c> (200 + <c>MediaUploaded</c>),
/// <c>POST .../uploads/{uploadId}/abort</c> (200).
/// Ids accept raw GUIDs and <c>prj_/upl_</c>; responses expose <c>upl_</c>.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId}/uploads")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class UploadsController : ControllerBase
{
    private readonly UploadService _uploads;
    private readonly IPublishEndpoint _publish;

    public UploadsController(UploadService uploads, IPublishEndpoint publish)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(publish);
        _uploads = uploads;
        _publish = publish;
    }

    /// <summary>
    /// Creates an upload session (S3 multipart initiated).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(CreateUploadResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromRoute] string projectId,
        [FromBody] CreateUploadRequest request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);

        var result = await _uploads.CreateSessionAsync(
            tenantId, projectGuid, request.FileName, request.ContentType,
            request.DeclaredSize, User.GetSubject(), cancellationToken).ConfigureAwait(false);

        var response = new CreateUploadResponse(
            PublicIdParser.ToUploadId(result.UploadId),
            result.MultipartUploadId,
            result.PartSizeBytes,
            result.ExpiresAt,
            result.Status);
        return CreatedAtAction(
            nameof(GetStatus),
            new { projectId = PublicIdParser.ToProjectId(projectGuid), uploadId = response.UploadId },
            response);
    }

    /// <summary>
    /// Lists upload sessions for a project (pagination envelope).
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<UploadListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        [FromRoute] string projectId,
        [FromQuery] PaginationParams? query,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        query ??= new PaginationParams();
        query.Normalize();
        var (page, pageSize) = query.Normalized();

        var (items, total) = await _uploads.ListAsync(tenantId, projectGuid, page, pageSize, cancellationToken).ConfigureAwait(false);
        var responses = items.Select(UploadListItem.From).ToList();
        return Ok(PaginatedResult<UploadListItem>.Create(responses, page, pageSize, total));
    }

    /// <summary>
    /// Gets upload status (S3-authoritative parts + DB reconcile).
    /// </summary>
    [HttpGet("{uploadId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(UploadStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(
        [FromRoute] string projectId,
        [FromRoute] string uploadId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var uploadGuid = PublicIdParser.ParseUploadId(uploadId);

        var status = await _uploads.GetStatusAsync(tenantId, projectGuid, uploadGuid, cancellationToken).ConfigureAwait(false);
        return Ok(UploadStatusResponse.From(
            status.UploadId, status.Status, status.CompletedParts, status.MissingParts,
            status.PartSizeBytes, status.DeclaredSizeBytes, status.ExpiresAt));
    }

    /// <summary>
    /// Issues presigned part-upload URLs (15 minutes each).
    /// </summary>
    [HttpPost("{uploadId}/parts")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(PartUrlsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetPartUrls(
        [FromRoute] string projectId,
        [FromRoute] string uploadId,
        [FromBody] GetPartUrlsRequest request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var uploadGuid = PublicIdParser.ParseUploadId(uploadId);

        var urls = await _uploads.GetPartUrlsAsync(
            tenantId, projectGuid, uploadGuid, request.PartNumbers, cancellationToken).ConfigureAwait(false);
        var shaped = urls.ToDictionary(
            pair => pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pair => pair.Value,
            StringComparer.Ordinal);
        return Ok(new PartUrlsResponse(shaped));
    }

    /// <summary>
    /// Completes an upload (requires all expected parts, else 400
    /// UPLOAD_INCOMPLETE) and publishes <c>MediaUploaded</c> (best effort).
    /// </summary>
    [HttpPost("{uploadId}/complete")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(UploadStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Complete(
        [FromRoute] string projectId,
        [FromRoute] string uploadId,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var uploadGuid = PublicIdParser.ParseUploadId(uploadId);

        var result = await _uploads.CompleteAsync(
            tenantId, projectGuid, uploadGuid, User.GetSubject(), cancellationToken).ConfigureAwait(false);

        await PublishMediaUploadedAsync(tenantId, projectGuid, result, cancellationToken).ConfigureAwait(false);

        var status = await _uploads.GetStatusAsync(tenantId, projectGuid, uploadGuid, cancellationToken).ConfigureAwait(false);
        return Ok(UploadStatusResponse.From(
            status.UploadId, status.Status, status.CompletedParts, status.MissingParts,
            status.PartSizeBytes, status.DeclaredSizeBytes, status.ExpiresAt));
    }

    /// <summary>
    /// Aborts an upload session.
    /// </summary>
    [HttpPost("{uploadId}/abort")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(UploadStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Abort(
        [FromRoute] string projectId,
        [FromRoute] string uploadId,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var uploadGuid = PublicIdParser.ParseUploadId(uploadId);

        await _uploads.AbortAsync(tenantId, projectGuid, uploadGuid, User.GetSubject(), cancellationToken).ConfigureAwait(false);

        var status = await _uploads.GetStatusAsync(tenantId, projectGuid, uploadGuid, cancellationToken).ConfigureAwait(false);
        return Ok(UploadStatusResponse.From(
            status.UploadId, status.Status, status.CompletedParts, status.MissingParts,
            status.PartSizeBytes, status.DeclaredSizeBytes, status.ExpiresAt));
    }

    private async Task PublishMediaUploadedAsync(
        Guid tenantId,
        Guid projectId,
        UploadCompleteResult result,
        CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var message = new MediaUploaded(
            Guid.NewGuid(), correlationId, tenantId, projectId, Guid.Empty,
            null, null, null, null, null, 1, DateTimeOffset.UtcNow, 0,
            null, null, null,
            PublicIdParser.ToUploadId(result.UploadId), result.StorageKey, null);

        try
        {
            await _publish.Publish(message, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Publish is best effort: the DB commit already succeeded; bus outage must not fail completion.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }
}
