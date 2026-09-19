using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Moq;

namespace DubbingPlatform.UnitTests.Storage;

/// <summary>
/// Offline publication validation (no PG/MinIO): argument shapes fail fast
/// before any storage or database I/O (strict fake storage proves it).
/// </summary>
public sealed class ArtifactServiceValidationTests
{
    private static ArtifactService Create()
    {
        var factory = new Mock<IStageExecutionContextFactory>(MockBehavior.Strict);
        var storage = new Mock<IArtifactStorage>(MockBehavior.Strict);
        return new ArtifactService(factory.Object, storage.Object);
    }

    [Fact]
    public async Task Publish_Rejects_Empty_Tenant()
    {
        var service = Create();
        using var stream = new MemoryStream("x"u8.ToArray(), writable: false);
        await Assert.ThrowsAsync<DomainException>(() => service.PublishAsync(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(),
            StageType.Transcription, ArtifactType.Transcript,
            stream, ".txt", "text/plain", null, null, null, null, []));
    }

    [Fact]
    public async Task Publish_Rejects_Empty_ContentType()
    {
        var service = Create();
        using var stream = new MemoryStream("x"u8.ToArray(), writable: false);
        await Assert.ThrowsAsync<DomainException>(() => service.PublishAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            StageType.Transcription, ArtifactType.Transcript,
            stream, ".txt", " ", null, null, null, null, []));
    }

    [Fact]
    public async Task Publish_Rejects_Empty_Parent_Id()
    {
        var service = Create();
        using var stream = new MemoryStream("x"u8.ToArray(), writable: false);
        await Assert.ThrowsAsync<DomainException>(() => service.PublishAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            StageType.Transcription, ArtifactType.Transcript,
            stream, ".txt", "text/plain", null, null, null, null, [Guid.Empty]));
    }

    [Fact]
    public async Task Publish_Rejects_Bad_Extension_Before_Upload()
    {
        var service = Create();
        using var stream = new MemoryStream("x"u8.ToArray(), writable: false);
        // Strict fakes: any storage/factory call throws MockException, so a
        // DomainException proves validation happened before I/O.
        await Assert.ThrowsAsync<DomainException>(() => service.PublishAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            StageType.Transcription, ArtifactType.Transcript,
            stream, "no-leading-dot", "text/plain", null, null, null, null, []));
    }
}
