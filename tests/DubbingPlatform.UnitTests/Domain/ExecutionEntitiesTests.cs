using System;
using System.Reflection;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Domain;

public sealed class ExecutionEntitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StageExecution_Has_Lease_Fields()
    {
        var execution = new StageExecution(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            StageType.Transcription,
            ScopeType.Segment,
            "seg-1",
            null,
            1,
            StageStatus.Running,
            "worker-1",
            "token-abc",
            3,
            Now.AddMinutes(5),
            Now,
            null,
            "input-hash",
            "config-hash",
            "snapshot-hash",
            null,
            null,
            null,
            Now,
            Now);

        Assert.Equal("worker-1", execution.LeaseOwner);
        Assert.Equal("token-abc", execution.LeaseToken);
        Assert.Equal(3, execution.LeaseTokenVersion);
        Assert.Equal(Now.AddMinutes(5), execution.LeaseExpiresAt);
    }

    [Fact]
    public void ArtifactParent_Links_Child_Parent()
    {
        var childId = Guid.NewGuid();
        var parentId = Guid.NewGuid();

        var link = new ArtifactParent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            childId,
            parentId,
            Now);

        Assert.Equal(childId, link.ChildArtifactId);
        Assert.Equal(parentId, link.ParentArtifactId);
    }

    [Fact]
    public void ProviderExecution_Requires_RequestHash()
    {
        Assert.Throws<DomainException>(() => new ProviderExecution(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            ProviderType.Mock,
            ProviderCapability.Transcription,
            "model",
            null,
            null,
            null,
            null,
            0,
            string.Empty,
            null,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            OutcomeClass.Success,
            null,
            null,
            null,
            null,
            null,
            null,
            Now));

        Assert.Throws<DomainException>(() => new ProviderExecution(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            ProviderType.Mock,
            ProviderCapability.Transcription,
            "model",
            null,
            null,
            null,
            null,
            0,
            "   ",
            null,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            OutcomeClass.Success,
            null,
            null,
            null,
            null,
            null,
            null,
            Now));
    }

    [Fact]
    public void All_Execution_Entities_Have_TenantId()
    {
        var executionTypes = new Type[]
        {
            typeof(TranscriptVersion),
            typeof(TranslationVersion),
            typeof(GeneratedAudioArtifact),
            typeof(SyncResult),
            typeof(StageExecution),
            typeof(RunStageSummary),
            typeof(StageUnitCompletion),
            typeof(ContentObject),
            typeof(Artifact),
            typeof(ArtifactParent),
            typeof(StageInputArtifact),
            typeof(StageOutputArtifact),
            typeof(ProviderExecution),
            typeof(ProviderCapabilityDescriptor),
            typeof(ProviderRouteSnapshot),
            typeof(PromptTemplate),
            typeof(PromptTemplateVersion),
            typeof(QualityResult),
            typeof(ReviewItem),
            typeof(ReviewDecision),
            typeof(OutputAsset),
            typeof(ExportJob),
            typeof(ExportArtifact),
            typeof(AuditEvent),
            typeof(IdempotencyRecord),
            typeof(CostReservation),
            typeof(QuotaUsage),
            typeof(ProcessingPolicy),
            typeof(RetentionHold),
            typeof(DeletionJob),
        };

        Assert.Equal(30, executionTypes.Length);

        foreach (var type in executionTypes)
        {
            var property = type.GetProperty("TenantId", BindingFlags.Public | BindingFlags.Instance);
            Assert.True(property is not null, $"{type.Name} must have TenantId.");
            Assert.Equal(typeof(Guid), property!.PropertyType);
        }
    }
}
