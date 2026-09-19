using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Storage;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Storage;

/// <summary>
/// S3-compatible artifact storage (MinIO locally, managed S3 in production).
/// Uploads stream via <c>TransferUtility</c> with an 8MB part size; downloads
/// stream without buffering. Checksums use the store's authoritative value
/// when available (<c>ChecksumSHA256</c>); ETag MD5/multipart values are not
/// SHA-256 and return null so callers never compare mismatched algorithms and
/// never re-download just to hash. Presigned URLs are issued by the SDK with
/// caller-provided expiry (callers default to 15 minutes). No secrets appear
/// in keys or logs: keys are tenant-prefixed content addresses only.
/// </summary>
public sealed class S3ArtifactStorage : IArtifactStorage, IStorageInventory
{
    private const long PartSizeBytes = 8L * 1024L * 1024L;

    private readonly IAmazonS3 _client;
    private readonly StorageOptions _options;

    public S3ArtifactStorage(IAmazonS3 client, IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public async Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        StorageKeyBuilder.ValidateKey(storageKey);
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("ContentType must not be empty.", nameof(contentType));
        }

        var request = new TransferUtilityUploadRequest
        {
            BucketName = _options.Bucket,
            Key = storageKey,
            InputStream = content,
            ContentType = contentType,
            PartSize = PartSizeBytes,
            StorageClass = S3StorageClass.Standard,
            AutoCloseStream = false,
        };

        var transfer = new TransferUtility(_client);
        await transfer.UploadAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<Stream> DownloadAsync(string storageKey, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        var response = await _client.GetObjectAsync(_options.Bucket, storageKey, ct).ConfigureAwait(false);
        return new ResponseStream(response);
    }

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        try
        {
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _options.Bucket,
                Key = storageKey,
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return false;
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _options.Bucket,
                Key = storageKey,
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            // Idempotent delete.
        }
    }

    public Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        if (expiry <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expiry), expiry, "Expiry must be positive.");
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = storageKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry),
        };
        return Task.FromResult(_client.GetPreSignedURL(request));
    }

    public Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        if (expiry <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expiry), expiry, "Expiry must be positive.");
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = storageKey,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(expiry),
        };
        return Task.FromResult(_client.GetPreSignedURL(request));
    }

    public async Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken ct)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        GetObjectMetadataResponse metadata;
        try
        {
            metadata = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _options.Bucket,
                Key = storageKey,
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }

        var sha = metadata.ChecksumSHA256;
        if (!string.IsNullOrWhiteSpace(sha))
        {
            var normalized = sha.Trim().ToLowerInvariant();
            if (StorageKeyBuilder.IsLowerHex64(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<StorageObjectInfo>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var results = new List<StorageObjectInfo>();
        string? continuation = null;
        do
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _options.Bucket,
                Prefix = prefix ?? string.Empty,
                ContinuationToken = continuation,
                MaxKeys = 1000,
            }, cancellationToken).ConfigureAwait(false);
            foreach (var entry in response.S3Objects)
            {
                var lastModified = entry.LastModified.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(entry.LastModified.Value, DateTimeKind.Utc))
                    : DateTimeOffset.UtcNow;
                results.Add(new StorageObjectInfo(entry.Key ?? string.Empty, lastModified, entry.Size ?? 0L));
            }

            continuation = (response.IsTruncated ?? false) ? response.NextContinuationToken : null;
        }
        while (continuation is not null);

        return results;
    }

    public async Task QuarantineAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        StorageKeyBuilder.ValidateKey(storageKey);
        var quarantineKey = string.Concat("quarantine/", storageKey);
        await _client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _options.Bucket,
            SourceKey = storageKey,
            DestinationBucket = _options.Bucket,
            DestinationKey = quarantineKey,
        }, cancellationToken).ConfigureAwait(false);
        await _client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _options.Bucket,
            Key = storageKey,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsNotFound(AmazonS3Exception exception)
    {
        return string.Equals(exception.ErrorCode, "NotFound", StringComparison.Ordinal)
            || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal)
            || exception.StatusCode == System.Net.HttpStatusCode.NotFound;
    }

    private sealed class ResponseStream : Stream
    {
        private readonly GetObjectResponse _response;
        private readonly Stream _inner;

        public ResponseStream(GetObjectResponse response)
        {
            _response = response;
            _inner = response.ResponseStream;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
