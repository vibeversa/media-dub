using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Validated source-media bundle for run-scoped media stages.
/// </summary>
public sealed record ValidSourceMedia(
    MediaAsset Asset,
    ContentObject Content,
    Guid? SourceArtifactId);

/// <summary>
/// Shared source-media resolution for run-scoped media stages (analysis,
/// preparation). Prefers <c>DubbingProject.SourceMediaAssetId</c> when it
/// points at a Valid asset of the project, else the oldest Valid asset.
/// Missing or cross-tenant rows are permanent pipeline faults (never retried).
/// </summary>
internal static class SourceMediaLoader
{
    public static async Task<ValidSourceMedia> LoadAsync(
        IStageExecutionContextFactory contextFactory,
        Guid tenantId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        DubbingProject? project;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
        }

        if (project is null)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        MediaAsset? asset;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = contextFactory.CreateDbContext();
            asset = null;
            if (project.SourceMediaAssetId.HasValue)
            {
                asset = await db.Set<MediaAsset>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        a => a.Id == project.SourceMediaAssetId.Value
                            && a.ProjectId == projectId
                            && a.Status == MediaAssetStatus.Valid,
                        cancellationToken).ConfigureAwait(false);
            }

            asset ??= await db.Set<MediaAsset>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (asset is null)
            {
                throw new ErrorCodeException(
                    Errors.ErrorCodes.ArtifactUnavailable,
                    $"Project '{projectId}' has no validated media asset.");
            }

            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == asset.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null || content.Status != ContentObjectStatus.Committed)
            {
                throw new ErrorCodeException(
                    Errors.ErrorCodes.ArtifactUnavailable,
                    $"Source content for project '{projectId}' is unavailable.");
            }

            var sourceArtifactId = await db.Set<Artifact>()
                .Where(a => a.ContentObjectId == content.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            return new ValidSourceMedia(asset, content, sourceArtifactId);
        }
    }
}
