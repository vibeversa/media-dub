using Amazon.S3;
using Amazon.S3.Model;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Storage;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Storage;

/// <summary>
/// S3 multipart session client over <see cref="IAmazonS3"/>. Bucket comes from
/// <see cref="StorageOptions"/> (never hardcoded); keys are validated via
/// <see cref="StorageKeyBuilder"/>. Presigned part URLs are PUTs bound to
/// <c>(key, uploadId, partNumber)</c> with caller-provided expiry (callers
/// default to 15 minutes). List is authoritative for completion; complete
/// sends the store-reported ETags verbatim. Abort is idempotent.
/// </summary>
public sealed class S3MultipartUploadClient : IMultipartUploadClient
{
    private readonly IAmazonS3 _client;
    private readonly StorageOptions _options;

    public S3MultipartUploadClient(IAmazonS3 client, IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public async Task<string> CreateMultipartUploadAsync(string storageKey, string contentType, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("ContentType must not be empty.", nameof(contentType));
        }

        var response = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _options.Bucket,
            Key = storageKey,
            ContentType = contentType.Trim(),
        }, ct).ConfigureAwait(false);
        return response.UploadId ?? string.Empty;
    }

    public Task<string> GetPresignedPartUrlAsync(string storageKey, string multipartUploadId, int partNumber, TimeSpan expiry, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(multipartUploadId);
        if (partNumber is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber), "PartNumber must be in 1..10000.");
        }

        if (expiry <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expiry), expiry, "Expiry must be positive.");
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = storageKey,
            Verb = HttpVerb.PUT,
            UploadId = multipartUploadId.Trim(),
            PartNumber = partNumber,
            Expires = DateTime.UtcNow.Add(expiry),
        };
        return Task.FromResult(_client.GetPreSignedURL(request));
    }

    public async Task<IReadOnlyList<MultipartPart>> ListPartsAsync(string storageKey, string multipartUploadId, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(multipartUploadId);

        var parts = new List<MultipartPart>();
        int? marker = null;
        do
        {
            var response = await _client.ListPartsAsync(new ListPartsRequest
            {
                BucketName = _options.Bucket,
                Key = storageKey,
                UploadId = multipartUploadId.Trim(),
                PartNumberMarker = marker?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MaxParts = 1000,
            }, ct).ConfigureAwait(false);

            foreach (var part in response.Parts)
            {
                var number = part.PartNumber ?? 0;
                if (number is < 1 or > 10000)
                {
                    continue;
                }

                parts.Add(new MultipartPart(number, part.ETag ?? string.Empty, part.Size ?? 0L));
            }

            marker = response.IsTruncated == true && response.Parts.Count > 0
                ? response.Parts.Max(p => p.PartNumber ?? 0)
                : null;
            if (response.IsTruncated != true)
            {
                break;
            }
        }
        while (marker.HasValue);

        return parts.OrderBy(p => p.PartNumber).ToList();
    }

    public async Task CompleteMultipartUploadAsync(string storageKey, string multipartUploadId, IReadOnlyList<MultipartPart> parts, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(multipartUploadId);
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            throw new ArgumentException("Parts must not be empty.", nameof(parts));
        }

        var request = new CompleteMultipartUploadRequest
        {
            BucketName = _options.Bucket,
            Key = storageKey,
            UploadId = multipartUploadId.Trim(),
        };
        foreach (var part in parts.OrderBy(p => p.PartNumber))
        {
            request.AddPartETags(new PartETag(part.PartNumber, part.ETag));
        }

        await _client.CompleteMultipartUploadAsync(request, ct).ConfigureAwait(false);
    }

    public async Task AbortMultipartUploadAsync(string storageKey, string multipartUploadId, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(multipartUploadId);

        try
        {
            await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = _options.Bucket,
                Key = storageKey,
                UploadId = multipartUploadId.Trim(),
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            // Idempotent abort.
        }
    }

    private static bool IsNotFound(AmazonS3Exception exception)
    {
        return string.Equals(exception.ErrorCode, "NotFound", StringComparison.Ordinal)
            || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal)
            || string.Equals(exception.ErrorCode, "NoSuchUpload", StringComparison.Ordinal)
            || exception.StatusCode == System.Net.HttpStatusCode.NotFound;
    }
}
