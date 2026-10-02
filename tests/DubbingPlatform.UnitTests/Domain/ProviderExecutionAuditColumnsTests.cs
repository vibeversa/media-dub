// GAP-011: ProviderExecution must carry audit/reconciliation columns, and the
// recorder must derive them from provider responses (usage + output content).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Domain;

public sealed class ProviderExecutionAuditColumnsTests
{
    private static ProviderExecution Build(
        string? usageDimensionsJson = null,
        string? systemInstructionHash = null,
        string? safetySettingsHash = null,
        string? promptTemplateVersion = null,
        string? outputContentHash = null) => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ProviderType.Azure, ProviderCapability.Transcription, "whisper-1", "1", null, null, null,
            0, "a".PadRight(64, 'a'), "b".PadRight(64, 'b'), 5L, 1, 2, 3.5, 0.01, 0.01, null,
            OutcomeClass.Success, null, null, null, null, null, "idem-1", DateTimeOffset.UtcNow,
            promptTemplateVersion, systemInstructionHash, safetySettingsHash, outputContentHash, usageDimensionsJson);

    [Theory]
    [InlineData("UsageDimensionsJson")]
    [InlineData("SystemInstructionHash")]
    [InlineData("SafetySettingsHash")]
    [InlineData("PromptTemplateVersion")]
    [InlineData("OutputContentHash")]
    public void ProviderExecution_Has_Audit_Column(string propertyName)
    {
        var property = typeof(ProviderExecution).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(typeof(string), property.PropertyType);
    }

    [Fact]
    public void Audit_Columns_Are_Persisted_And_Validated()
    {
        var execution = Build(
            usageDimensionsJson: "{\"tokens_in\":\"7\"}",
            systemInstructionHash: "c".PadRight(64, 'c'),
            safetySettingsHash: "d".PadRight(64, 'd'),
            promptTemplateVersion: "3",
            outputContentHash: "e".PadRight(64, 'e'));

        Assert.Equal("{\"tokens_in\":\"7\"}", execution.UsageDimensionsJson);
        Assert.Equal("3", execution.PromptTemplateVersion);
        Assert.Equal("e".PadRight(64, 'e'), execution.OutputContentHash);
        Assert.Equal("c".PadRight(64, 'c'), execution.SystemInstructionHash);
        Assert.Equal("d".PadRight(64, 'd'), execution.SafetySettingsHash);
        execution.Validate();

        Assert.Throws<DomainException>(() => Build(usageDimensionsJson: " ").Validate());
        Assert.Throws<DomainException>(() => Build(systemInstructionHash: "").Validate());
        Assert.Throws<DomainException>(() => Build(safetySettingsHash: "").Validate());
        Assert.Throws<DomainException>(() => Build(promptTemplateVersion: " ").Validate());
        Assert.Throws<DomainException>(() => Build(outputContentHash: "").Validate());
    }

    [Fact]
    public void Old_28_Argument_Callers_Still_Compile_And_Default_New_Columns_To_Null()
    {
        var execution = new ProviderExecution(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            ProviderType.Mock, ProviderCapability.Tts, "m", null, null, null, null,
            0, "a".PadRight(64, 'a'), null, 1L, null, null, null, null, null, null,
            OutcomeClass.Success, null, null, null, null, null, null, DateTimeOffset.UtcNow);

        Assert.Null(execution.UsageDimensionsJson);
        Assert.Null(execution.SystemInstructionHash);
        Assert.Null(execution.SafetySettingsHash);
        Assert.Null(execution.PromptTemplateVersion);
        Assert.Null(execution.OutputContentHash);
    }

    [Fact]
    public void HashContent_Is_Deterministic_And_Null_Safe()
    {
        Assert.Null(ProviderExecutionRecorder.HashContent(null));
        Assert.Null(ProviderExecutionRecorder.HashContent("   "));

        var first = ProviderExecutionRecorder.HashContent("hello");
        Assert.Equal(first, ProviderExecutionRecorder.HashContent("hello"));
        Assert.Equal(64, first!.Length);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hello"))).ToLowerInvariant(),
            first);
        Assert.NotEqual(first, ProviderExecutionRecorder.HashContent("hello "));
    }

    [Fact]
    public void Usage_Dimensions_Json_Captures_Metered_Fields_And_Usage_Metadata()
    {
        var usage = new ProviderUsage(11, 22, 1.25, 0.5);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["usage.characters"] = "512",
            ["usage.apiKey"] = "sk-live-should-never-persist",
            ["usage.huge"] = new string('x', 500),
            ["audioBase64"] = "AAAA",
            ["mock.job_id"] = "job-1",
        };

        var json = ProviderExecutionRecorder.BuildUsageDimensionsJson(usage, metadata);
        Assert.NotNull(json);

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json!);
        Assert.NotNull(parsed);
        Assert.Equal("11", parsed!["tokens_in"]);
        Assert.Equal("22", parsed["tokens_out"]);
        Assert.Equal("1.250", parsed["audio_seconds"]);
        Assert.Equal("0.500000", parsed["estimated_cost_usd"]);
        Assert.Equal("512", parsed["usage.characters"]);

        // Secrets, oversized values, payloads, and job handles never persist.
        Assert.DoesNotContain("sk-live", json!, StringComparison.Ordinal);
        Assert.DoesNotContain("usage.apiKey", json!, StringComparison.Ordinal);
        Assert.DoesNotContain("usage.huge", json!, StringComparison.Ordinal);
        Assert.DoesNotContain("audioBase64", json!, StringComparison.Ordinal);
        Assert.DoesNotContain("mock.job_id", json!, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_Dimensions_Json_Is_Null_When_Nothing_Is_Reported()
    {
        Assert.Null(ProviderExecutionRecorder.BuildUsageDimensionsJson(null, null));
        Assert.Null(ProviderExecutionRecorder.BuildUsageDimensionsJson(
            new ProviderUsage(null, null, null, null),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["model.hash"] = "abc" }));
        Assert.Null(ProviderExecutionRecorder.BuildUsageDimensionsJson(
            new ProviderUsage(null, null, double.NaN, double.NaN),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["usage.blank"] = "  " }));
    }

    [Fact]
    public void Usage_Dimensions_Json_Key_Order_Is_Deterministic()
    {
        var usage = new ProviderUsage(3, 4, null, null);
        var a = new Dictionary<string, string>(StringComparer.Ordinal) { ["usage.b"] = "2", ["usage.a"] = "1" };
        var b = new Dictionary<string, string>(StringComparer.Ordinal) { ["usage.a"] = "1", ["usage.b"] = "2" };

        Assert.Equal(
            ProviderExecutionRecorder.BuildUsageDimensionsJson(usage, a),
            ProviderExecutionRecorder.BuildUsageDimensionsJson(usage, b));
    }
}
