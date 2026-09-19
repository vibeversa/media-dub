using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DubbingPlatform.Contracts.Messages;

namespace DubbingPlatform.ContractTests.Messaging;

/// <summary>
/// Verifies the durable message contracts: common envelope fields, no JobId,
/// version policy, and JSON round-trips with a tolerant camelCase reader.
/// </summary>
public sealed class MessageContractTests
{
    private static readonly string[] CommonFields =
    [
        nameof(IntegrationMessage.MessageId),
        nameof(IntegrationMessage.CorrelationId),
        nameof(IntegrationMessage.TenantId),
        nameof(IntegrationMessage.ProjectId),
        nameof(IntegrationMessage.ProcessingRunId),
        nameof(IntegrationMessage.StageExecutionId),
        nameof(IntegrationMessage.StageType),
        nameof(IntegrationMessage.ScopeType),
        nameof(IntegrationMessage.ScopeId),
        nameof(IntegrationMessage.SegmentId),
        nameof(IntegrationMessage.SchemaVersion),
        nameof(IntegrationMessage.CreatedAt),
        nameof(IntegrationMessage.Attempt),
        nameof(IntegrationMessage.InputHash),
        nameof(IntegrationMessage.ConfigurationHash),
        nameof(IntegrationMessage.ExecutionSnapshotHash),
    ];

    private static IReadOnlyList<Type> MessageTypes()
    {
        return typeof(IntegrationMessage).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(IntegrationMessage)) && !t.IsAbstract)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void All_Messages_Have_Common_Fields()
    {
        var types = MessageTypes();

        // 17 durable messages: the 16 core contracts plus optional
        // EnrichmentRequested (Task 042, ai.gpu enrichment, out-of-band).
        Assert.Equal(17, types.Count);
        foreach (var type in types)
        {
            foreach (var field in CommonFields)
            {
                var property = type.GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
                Assert.True(property is not null, $"{type.Name} is missing common field {field}.");
            }
        }
    }

    [Fact]
    public void No_JobId_Field_Exists()
    {
        var types = MessageTypes().Append(typeof(IntegrationMessage));

        foreach (var type in types)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.DoesNotContain(properties, p => string.Equals(p.Name, "JobId", StringComparison.Ordinal));
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.DoesNotContain(fields, f => string.Equals(f.Name, "JobId", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Unsupported_Version_Is_Detected()
    {
        Assert.True(MessageVersionPolicy.IsSupported(MessageVersionPolicy.CurrentVersion));
        Assert.True(MessageVersionPolicy.IsSupported(1));
        Assert.False(MessageVersionPolicy.IsSupported(999));
        Assert.False(MessageVersionPolicy.IsSupported(0));
        Assert.False(MessageVersionPolicy.IsSupported(2));
    }

    [Fact]
    public void Serialization_RoundTrips()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var started = new RunStarted(
            Guid.NewGuid(),
            "corr-1",
            tenantId,
            projectId,
            runId,
            null,
            null,
            "run",
            runId.ToString("N", CultureInfo.InvariantCulture),
            null,
            MessageVersionPolicy.CurrentVersion,
            createdAt,
            0,
            "input-hash",
            "config-hash",
            "snapshot-hash",
            "pipeline-1",
            "route-hash");

        var startedJson = JsonSerializer.Serialize(started, MessagingJson.Options);
        Assert.Contains("messageId", startedJson, StringComparison.Ordinal);
        Assert.Contains("pipelineVersion", startedJson, StringComparison.Ordinal);
        var startedBack = JsonSerializer.Deserialize<RunStarted>(startedJson, MessagingJson.Options);
        Assert.Equal(started, startedBack);

        var work = new StageWorkRequested(
            Guid.NewGuid(),
            "corr-2",
            tenantId,
            projectId,
            runId,
            Guid.NewGuid(),
            "transcription",
            "segment",
            "scope-1",
            Guid.NewGuid(),
            MessageVersionPolicy.CurrentVersion,
            createdAt,
            1,
            null,
            null,
            null,
            "transcription",
            "segment",
            "scope-1",
            """{"lang":"es"}""");

        var workJson = JsonSerializer.Serialize(work, MessagingJson.Options);
        Assert.Contains("stageTypeRequired", workJson, StringComparison.Ordinal);
        var workBack = JsonSerializer.Deserialize<StageWorkRequested>(workJson, MessagingJson.Options);
        Assert.Equal(work, workBack);

        var withUnknown = workJson.TrimEnd('}') + ""","futureField":"ignored"}""";
        var tolerant = JsonSerializer.Deserialize<StageWorkRequested>(withUnknown, MessagingJson.Options);
        Assert.Equal(work, tolerant);
    }
}
