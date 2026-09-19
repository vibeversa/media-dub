using DubbingPlatform.Application.Storage;

namespace DubbingPlatform.UnitTests.Storage;

/// <summary>
/// Offline key-convention checks: tenant-prefixed, N-format Guids, hash, extension.
/// </summary>
public sealed class StorageKeyBuilderTests
{
    [Fact]
    public void BuildKey_Matches_Convention()
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var project = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var run = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var hash = new string('a', 64);

        var key = StorageKeyBuilder.BuildKey(tenant, project, run, "Transcription", "Transcript", hash, ".txt");

        Assert.Equal(
            "11111111111111111111111111111111/22222222222222222222222222222222/33333333333333333333333333333333/Transcription/Transcript/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.txt",
            key);
    }

    [Fact]
    public void BuildKey_Rejects_Empty_Tenant()
    {
        Assert.Throws<DubbingPlatform.Domain.Exceptions.DomainException>(() => StorageKeyBuilder.BuildKey(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "Transcription", "Transcript", new string('b', 64), ".txt"));
    }

    [Fact]
    public void BuildKey_Rejects_Bad_Hash()
    {
        Assert.Throws<DubbingPlatform.Domain.Exceptions.DomainException>(() => StorageKeyBuilder.BuildKey(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Transcription", "Transcript", "not-a-hash", ".txt"));
    }

    [Fact]
    public void ValidateKey_Rejects_Traversal()
    {
        Assert.Throws<DubbingPlatform.Domain.Exceptions.DomainException>(() => StorageKeyBuilder.ValidateKey("../evil"));
        Assert.Throws<DubbingPlatform.Domain.Exceptions.DomainException>(() => StorageKeyBuilder.ValidateKey("/absolute"));
        Assert.Throws<DubbingPlatform.Domain.Exceptions.DomainException>(() => StorageKeyBuilder.ValidateKey("no-slash"));
    }

    [Fact]
    public void Default_Presigned_Expiry_Is_15_Minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), StoragePresignedUrls.DefaultExpiry);
    }
}
