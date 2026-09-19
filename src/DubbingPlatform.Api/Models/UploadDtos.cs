using System.ComponentModel.DataAnnotations;
using DubbingPlatform.Domain.Entities;

namespace DubbingPlatform.Api.Models;

/// <summary>
/// Create-upload request body.
/// </summary>
public sealed class CreateUploadRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(256)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(256)]
    public string ContentType { get; set; } = string.Empty;

    [Range(1, long.MaxValue)]
    public long DeclaredSize { get; set; }

    [MaxLength(64)]
    public string? ClientSha256Hex { get; set; }
}

/// <summary>
/// Create-upload response body (201).
/// </summary>
public sealed record CreateUploadResponse(
    string UploadId,
    string MultipartUploadId,
    long PartSize,
    DateTimeOffset ExpiresAt,
    string Status);

/// <summary>
/// Part-URL request body.
/// </summary>
public sealed class GetPartUrlsRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(1000)]
    public int[] PartNumbers { get; set; } = [];
}

/// <summary>
/// Part-URL response body (200): <c>{ urls: { "1": "https://..." } }</c>.
/// </summary>
public sealed record PartUrlsResponse(Dictionary<string, string> Urls);

/// <summary>
/// Upload-status response body (200).
/// </summary>
public sealed record UploadStatusResponse(
    string UploadId,
    string Status,
    IReadOnlyList<int> CompletedParts,
    IReadOnlyList<int> MissingParts,
    long PartSize,
    long DeclaredSize,
    DateTimeOffset ExpiresAt)
{
    public static UploadStatusResponse From(
        Guid uploadId,
        string status,
        IReadOnlyList<int> completed,
        IReadOnlyList<int> missing,
        long partSize,
        long declaredSize,
        DateTimeOffset expiresAt)
    {
        return new UploadStatusResponse(
            PublicIdParser.ToUploadId(uploadId), status, completed, missing,
            partSize, declaredSize, expiresAt);
    }
}

/// <summary>
/// Upload list item.
/// </summary>
public sealed record UploadListItem(
    string UploadId,
    string FileName,
    string Status,
    long DeclaredSize,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    public static UploadListItem From(UploadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new UploadListItem(
            PublicIdParser.ToUploadId(session.Id),
            session.FileName,
            session.Status.ToString(),
            session.DeclaredSizeBytes,
            session.CreatedAt,
            session.ExpiresAt);
    }
}
