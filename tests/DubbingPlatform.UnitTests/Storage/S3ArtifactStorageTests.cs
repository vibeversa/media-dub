using Amazon.S3;
using Amazon.S3.Model;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Moq;

namespace DubbingPlatform.UnitTests.Storage;

/// <summary>
/// Offline S3 adapter checks with a mocked client (no MinIO required).
/// </summary>
public sealed class S3ArtifactStorageTests
{
    private static S3ArtifactStorage Create(out Mock<IAmazonS3> client)
    {
        client = new Mock<IAmazonS3>(MockBehavior.Strict);
        var options = Microsoft.Extensions.Options.Options.Create(new StorageOptions
        {
            Endpoint = "localhost:9000",
            Bucket = "dubbing-test",
            UseSsl = false,
            AccessKey = "minioadmin",
            SecretKey = "minioadmin",
        });
        return new S3ArtifactStorage(client.Object, options);
    }

    [Fact]
    public async Task Presigned_Download_Returns_Sdk_Url()
    {
        var storage = Create(out var client);
        client.Setup(c => c.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
            .Returns("https://example/download");

        var url = await storage.GetPresignedDownloadUrlAsync("tenant/key", TimeSpan.FromMinutes(15), CancellationToken.None);
        Assert.Equal("https://example/download", url);
    }

    [Fact]
    public async Task Presigned_Rejects_Non_Positive_Expiry()
    {
        var storage = Create(out _);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.GetPresignedDownloadUrlAsync(
            "tenant/key", TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.GetPresignedUploadUrlAsync(
            "tenant/key", TimeSpan.FromSeconds(-1), CancellationToken.None));
    }

    [Fact]
    public async Task All_Methods_Reject_Traversal_Keys()
    {
        var storage = Create(out _);
        await Assert.ThrowsAsync<DomainException>(() => storage.ExistsAsync("../evil", CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => storage.DeleteAsync("/absolute", CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => storage.GetPresignedDownloadUrlAsync(
            "no-slash", TimeSpan.FromMinutes(15), CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => storage.GetStorageChecksumAsync(
            "a\\b", CancellationToken.None));
    }

    [Fact]
    public async Task Exists_True_When_Metadata_Succeeds()
    {
        var storage = Create(out var client);
        client.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse());

        Assert.True(await storage.ExistsAsync("tenant/key", CancellationToken.None));
    }

    [Fact]
    public async Task Checksum_Null_When_No_Sha_Metadata()
    {
        var storage = Create(out var client);
        client.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse());

        Assert.Null(await storage.GetStorageChecksumAsync("tenant/key", CancellationToken.None));
    }

    [Fact]
    public async Task Checksum_Returns_Sha_When_Present()
    {
        var storage = Create(out var client);
        var sha = new string('c', 64);
        var response = new GetObjectMetadataResponse
        {
            ChecksumSHA256 = sha,
        };
        client.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        Assert.Equal(sha, await storage.GetStorageChecksumAsync("tenant/key", CancellationToken.None));
    }
}
