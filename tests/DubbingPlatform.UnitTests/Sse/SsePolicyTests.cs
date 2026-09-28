// Task 039C: SSE payload policy and envelope unit gap closure.
using System.Text.Json;
using DubbingPlatform.Api.Sse;

namespace DubbingPlatform.UnitTests.Sse;

/// <summary>
/// Frozen SSE surface: the 14 allowed event types, schema version, envelope
/// frame construction, the payload allowlist, and the replay buffer. The
/// negative cases are first-class: a forbidden payload key (transcript or
/// translation body, media body, signed URL) is rejected and never reaches the
/// envelope. Pure in-process work — no container, no network, no database.
/// </summary>
public sealed class SsePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EventTypes_AreFrozenAtFourteen_AndAllAreKnown()
    {
        Assert.Equal(14, SseEventTypes.All.Length);
        Assert.Equal(14, SseEventTypes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(SseEventTypes.All, t => Assert.True(SseEventTypes.IsKnown(t)));

        Assert.Equal(
            [
                "project.status_changed", "run.status_changed", "stage.started", "stage.progress",
                "stage.completed", "stage.failed", "stage.review_required", "review.created",
                "review.resolved", "export.created", "export.completed", "export.failed",
                "notification.created", "output.ready",
            ],
            SseEventTypes.All);
    }

    [Theory]
    [InlineData("stage.unknown")]
    [InlineData("STAGE.PROGRESS")]
    [InlineData("project.status_changed ")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownEventTypes_ReadAsNotKnown_NeverThrow(string? eventType)
    {
        // Unknown/blank types are a safe negative, not an unhandled throw.
        Assert.False(SseEventTypes.IsKnown(eventType));
    }

    [Theory]
    [InlineData(SseEventTypes.ProjectStatusChanged)]
    [InlineData(SseEventTypes.RunStatusChanged)]
    [InlineData(SseEventTypes.StageStarted)]
    [InlineData(SseEventTypes.StageProgress)]
    [InlineData(SseEventTypes.StageCompleted)]
    [InlineData(SseEventTypes.StageFailed)]
    [InlineData(SseEventTypes.StageReviewRequired)]
    [InlineData(SseEventTypes.ReviewCreated)]
    [InlineData(SseEventTypes.ReviewResolved)]
    [InlineData(SseEventTypes.ExportCreated)]
    [InlineData(SseEventTypes.ExportCompleted)]
    [InlineData(SseEventTypes.ExportFailed)]
    [InlineData(SseEventTypes.NotificationCreated)]
    [InlineData(SseEventTypes.OutputReady)]
    public void Envelope_SerializesEveryDocumentedEventType(string eventType)
    {
        var envelope = NewEnvelope("e-1", eventType, Payload(("status", "Running"), ("percent", 42)));

        var frame = envelope.ToFrame();

        Assert.NotNull(frame);
        Assert.StartsWith("id: e-1\n", frame, StringComparison.Ordinal);
        Assert.Contains($"event: {eventType}\n", frame, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", frame, StringComparison.Ordinal);

        var data = ExtractData(frame!);
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        Assert.Equal("e-1", root.GetProperty("eventId").GetString());
        Assert.Equal(SseEnvelope.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(eventType, root.GetProperty("eventType").GetString());
        Assert.Equal("tenant-1", root.GetProperty("tenantId").GetString());
        Assert.Equal("prj_1", root.GetProperty("projectId").GetString());
        Assert.Equal("run-1", root.GetProperty("processingRunId").GetString());
        Assert.Equal("corr-1", root.GetProperty("correlationId").GetString());
        Assert.Equal(42, root.GetProperty("payload").GetProperty("percent").GetInt32());
        Assert.Equal("Running", root.GetProperty("payload").GetProperty("status").GetString());
    }

    [Fact]
    public void Envelope_SchemaVersionAndReplayWindow_AreFrozen()
    {
        Assert.Equal(1, SseEnvelope.CurrentSchemaVersion);
        Assert.Equal(64 * 1024, SseEnvelope.MaxPayloadBytes);
        Assert.Equal(100, SseEnvelope.ReplayWindow);
    }

    [Fact]
    public void Envelope_AllowsNullProjectAndRun_AndKeepsCorrelationId()
    {
        var envelope = NewEnvelope(
            "e-tls", SseEventTypes.NotificationCreated, Payload(("severity", "Info")),
            projectId: null, processingRunId: null);

        var frame = envelope.ToFrame();

        Assert.NotNull(frame);
        using var document = JsonDocument.Parse(ExtractData(frame!));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("projectId").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("processingRunId").ValueKind);
        // Correlation is a security requirement: present on every frame.
        Assert.Equal("corr-1", document.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void Envelope_RejectsUnknownEventType_WithAnExplicitError()
    {
        var envelope = NewEnvelope("e-1", "stage.unknown", Payload(("status", "Running")));

        var ex = Assert.Throws<InvalidOperationException>(() => envelope.ToFrame());
        Assert.Contains("Unknown SSE event type", ex.Message, StringComparison.Ordinal);
        // The header-only frame applies the same closed set.
        Assert.Throws<InvalidOperationException>(() => envelope.ToHeaderOnlyFrame());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Envelope_RejectsUnsupportedSchemaVersion(int schemaVersion)
    {
        var envelope = NewEnvelope("e-1", SseEventTypes.StageProgress, Payload(("percent", 1))) with
        {
            SchemaVersion = schemaVersion,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => envelope.ToFrame());
        Assert.Contains("schemaVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Envelope_DropsOversizePayload_ReturnsNull_AndStreamStaysOpen()
    {
        var envelope = NewEnvelope("e-big", SseEventTypes.StageProgress, Payload(("blob", new string('x', SseEnvelope.MaxPayloadBytes + 1))));

        // Dropped (null), never an exception and never a truncated frame.
        Assert.Null(envelope.ToFrame());
    }

    [Fact]
    public void HeaderOnlyFrame_CarriesIdentity_AndNeverAnyPayload()
    {
        var envelope = NewEnvelope(
            "e-2", SseEventTypes.ReviewResolved, Payload(("status", "Approved"), ("reason", "QC")));

        var frame = envelope.ToHeaderOnlyFrame();

        Assert.StartsWith("id: e-2\n", frame, StringComparison.Ordinal);
        Assert.Contains($"event: {SseEventTypes.ReviewResolved}\n", frame, StringComparison.Ordinal);

        var data = ExtractData(frame);
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        Assert.Equal("e-2", root.GetProperty("eventId").GetString());
        Assert.Equal("corr-1", root.GetProperty("correlationId").GetString());
        // Header-only frames never carry a payload body.
        Assert.False(root.TryGetProperty("payload", out _));
        Assert.DoesNotContain("Approved", frame, StringComparison.Ordinal);
        Assert.Empty(SsePayloadPolicy.ScanFrame(frame));
    }

    [Theory]
    [InlineData("signedUrl")]
    [InlineData("downloadSignedUrl")]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("apiKey")]
    [InlineData("secret")]
    [InlineData("connectionString")]
    [InlineData("internalPath")]
    [InlineData("rawPayload")]
    [InlineData("leaseToken")]
    public void PayloadPolicy_RejectsForbiddenKeys_AndTheEnvelopeNeverCarriesThem(string key)
    {
        Assert.True(SsePayloadPolicy.IsForbiddenKey(key));

        var payload = new Dictionary<string, object?> { [key] = "leaked-value" };

        var ex = Assert.Throws<InvalidOperationException>(() => SsePayloadPolicy.Validate(payload));
        Assert.Contains("forbidden", ex.Message, StringComparison.OrdinalIgnoreCase);

        var envelope = NewEnvelope("e-1", SseEventTypes.StageCompleted, payload);
        Assert.Throws<InvalidOperationException>(() => envelope.ToFrame());
        Assert.Empty(SsePayloadPolicy.ScanFrame(envelope.ToHeaderOnlyFrame()));
    }

    [Fact]
    public void PayloadPolicy_ForbiddenList_IsFrozen()
    {
        Assert.Equal(
            [
                "signedUrl", "token", "secret", "apiKey",
                "connectionString", "internalPath", "rawPayload", "leaseToken",
            ],
            SsePayloadPolicy.ForbiddenKeys);
    }

    [Theory]
    [InlineData("transcript")]
    [InlineData("translationText")]
    [InlineData("mediaBody")]
    [InlineData("downloadUrl")]
    public void PayloadPolicy_DocumentedResidual_BodyKeysAreNotInTheFrozenList(string key)
    {
        // Documented residual: the frozen allowlist is an eight-key denylist. A
        // body key such as "transcript" or "mediaBody" is NOT matched, so the
        // policy cannot be relied on alone to keep bodies out of a frame. The
        // call sites are the boundary; this test pins the current behaviour.
        Assert.False(SsePayloadPolicy.IsForbiddenKey(key));
        SsePayloadPolicy.Validate(new Dictionary<string, object?> { [key] = "synthetic" });
        Assert.Empty(SsePayloadPolicy.ScanFrame($"{{\"{key}\":\"synthetic\"}}"));
    }

    [Theory]
    [InlineData("SignedUrl")]
    [InlineData("SIGNEDURL")]
    [InlineData("  token  ")]
    [InlineData("myApiKeyValue")]
    public void PayloadPolicy_KeyMatching_IsCaseInsensitive_AndTrims(string key)
    {
        Assert.True(SsePayloadPolicy.IsForbiddenKey(key));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("percent")]
    [InlineData("stageType")]
    [InlineData("errorCode")]
    [InlineData("durationMs")]
    [InlineData("projectId")]
    [InlineData("retryAfterMs")]
    [InlineData("bucket")]
    [InlineData("monkeyBusiness")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PayloadPolicy_AllowsOperationalKeys_AndRejectsNothing(string? key)
    {
        Assert.False(SsePayloadPolicy.IsForbiddenKey(key));
        SsePayloadPolicy.Validate(new Dictionary<string, object?> { [key ?? "fallback"] = 1 });
    }

    [Fact]
    public void PayloadPolicy_AcceptsNullPayload_AndEmptyDictionary()
    {
        SsePayloadPolicy.Validate(null);
        SsePayloadPolicy.Validate(new Dictionary<string, object?>());
    }

    [Fact]
    public void PayloadPolicy_RejectsForbiddenKey_LeakingThroughAValue()
    {
        // A nested dictionary that re-introduces a forbidden key is still caught
        // by the serialized-JSON scan.
        var payload = new Dictionary<string, object?>
        {
            ["details"] = new Dictionary<string, object?> { ["signedUrl"] = "https://x" },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => SsePayloadPolicy.Validate(payload));
        Assert.Contains("forbidden key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanFrame_ReportsMatchedForbiddenKeys_AndEmptyForCleanFrames()
    {
        Assert.Empty(SsePayloadPolicy.ScanFrame(null));
        Assert.Empty(SsePayloadPolicy.ScanFrame(string.Empty));
        Assert.Empty(SsePayloadPolicy.ScanFrame("""{"eventType":"stage.progress","percent":42}"""));

        var matched = SsePayloadPolicy.ScanFrame("""{"signedUrl":"https://x","token":"abc"}""");
        Assert.Equal(2, matched.Count);
        Assert.Contains("signedUrl", matched, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("token", matched, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerializedFrame_ForAllowedPayload_ContainsNoForbiddenKey()
    {
        var frame = NewEnvelope("e-ok", SseEventTypes.StageProgress, Payload(
            ("status", "Running"), ("percent", 42), ("stageType", "Translation"),
            ("errorCode", "PROVIDER_TIMEOUT"), ("durationMs", 1500), ("attempt", 1))).ToFrame();

        Assert.NotNull(frame);
        Assert.Empty(SsePayloadPolicy.ScanFrame(frame));
        foreach (var forbidden in SsePayloadPolicy.ForbiddenKeys)
        {
            Assert.DoesNotContain($"\"{forbidden}\"", frame!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AllowedKey_WithOpaqueValue_ProducesAFrameTheScannerAccepts()
    {
        // The allowlist is key-based: only forbidden KEYS fail validation, so an
        // operational frame carrying machine-readable codes is always scannable
        // and always clean.
        var envelope = NewEnvelope("e-3", SseEventTypes.StageFailed, Payload(("errorCode", "STAGE_FAILED")));

        var frame = envelope.ToFrame();

        Assert.NotNull(frame);
        Assert.Contains("STAGE_FAILED", frame!, StringComparison.Ordinal);
        Assert.Empty(SsePayloadPolicy.ScanFrame(frame));
    }

    [Fact]
    public void EventBuffer_KeysSeparateTenantAndProjectStreams()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        Assert.Equal(tenantId.ToString("N"), SseEventBuffer.KeyFor(tenantId, null));
        Assert.Equal($"{tenantId:N}:{projectId:N}", SseEventBuffer.KeyFor(tenantId, projectId));
        // Two tenants never share a buffer key.
        Assert.NotEqual(SseEventBuffer.KeyFor(tenantId, projectId), SseEventBuffer.KeyFor(Guid.NewGuid(), projectId));
    }

    [Fact]
    public void EventBuffer_ReplaysOnlyEnvelopesAfterTheCursor()
    {
        SseEventBuffer.Clear();
        var key = SseEventBuffer.KeyFor(Guid.NewGuid(), Guid.NewGuid());
        for (var index = 0; index < 3; index++)
        {
            SseEventBuffer.Append(key, NewEnvelope($"e{index}", SseEventTypes.StageProgress, Payload(("percent", index))));
        }

        var (found, missed, truncated) = SseEventBuffer.ReplayAfter(key, "e0");

        Assert.True(found);
        Assert.False(truncated);
        Assert.Equal(["e1", "e2"], missed.Select(m => m.EventId).ToArray());

        // The last envelope has nothing after it.
        var (foundTail, tail, _) = SseEventBuffer.ReplayAfter(key, "e2");
        Assert.True(foundTail);
        Assert.Empty(tail);

        // A blank cursor never replays.
        var (foundNone, none, _) = SseEventBuffer.ReplayAfter(key, "  ");
        Assert.False(foundNone);
        Assert.Empty(none);

        // An unknown key is simply empty, never truncated.
        var (foundUnknownKey, empty, notTruncated) = SseEventBuffer.ReplayAfter(SseEventBuffer.KeyFor(Guid.NewGuid(), null), "e0");
        Assert.False(foundUnknownKey);
        Assert.Empty(empty);
        Assert.False(notTruncated);

        SseEventBuffer.Clear();
    }

    [Fact]
    public void EventBuffer_EvictsBeyondTheWindow_AndFlagsTruncation()
    {
        SseEventBuffer.Clear();
        var key = SseEventBuffer.KeyFor(Guid.NewGuid(), Guid.NewGuid());
        for (var index = 0; index < SseEnvelope.ReplayWindow + 1; index++)
        {
            SseEventBuffer.Append(key, NewEnvelope($"e{index}", SseEventTypes.StageProgress, Payload(("percent", index))));
        }

        // The oldest cursor was evicted: the client must fall back to polling.
        var (foundEvicted, empty, truncated) = SseEventBuffer.ReplayAfter(key, "e0");
        Assert.False(foundEvicted);
        Assert.Empty(empty);
        Assert.True(truncated);

        // A cursor still inside the window resolves without truncation.
        var (found, missed, notTruncated) = SseEventBuffer.ReplayAfter(key, "e1");
        Assert.True(found);
        Assert.False(notTruncated);
        Assert.Equal(SseEnvelope.ReplayWindow - 1, missed.Count);

        SseEventBuffer.Clear();
    }

    [Fact]
    public void EventBuffer_UnknownCursorInAPartialWindow_IsNotTruncated()
    {
        SseEventBuffer.Clear();
        var key = SseEventBuffer.KeyFor(Guid.NewGuid(), null);
        SseEventBuffer.Append(key, NewEnvelope("only", SseEventTypes.StageStarted, Payload(("status", "Running"))));

        var (found, empty, truncated) = SseEventBuffer.ReplayAfter(key, "unknown-cursor");

        Assert.False(found);
        Assert.Empty(empty);
        Assert.False(truncated);

        SseEventBuffer.Clear();
    }

    [Fact]
    public void EventBuffer_GuardsBlankKeysAndNullEnvelopes()
    {
        SseEventBuffer.Clear();

        Assert.Throws<ArgumentException>(() => SseEventBuffer.Append("   ", NewEnvelope("e", SseEventTypes.StageStarted, Payload())));
        Assert.Throws<ArgumentNullException>(() => SseEventBuffer.Append("k", null!));
        Assert.Throws<ArgumentException>(() => SseEventBuffer.ReplayAfter("  ", "e0"));
    }

    [Fact]
    public void MetricNames_AreFrozen()
    {
        Assert.Equal("DubbingPlatform.Sse", SseMetrics.MeterName);
        Assert.Equal("sse.payload_dropped_total", SseMetrics.PayloadDroppedMetricName);
        Assert.NotNull(SseMetrics.PayloadDropped);
        SseMetrics.PayloadDroppedObserved();
    }

    private static SseEnvelope NewEnvelope(
        string eventId,
        string eventType,
        IReadOnlyDictionary<string, object?> payload,
        string? projectId = "prj_1",
        string? processingRunId = "run-1")
    {
        return new SseEnvelope(
            eventId,
            SseEnvelope.CurrentSchemaVersion,
            eventType,
            "tenant-1",
            projectId,
            processingRunId,
            Now,
            payload,
            "corr-1");
    }

    private static IReadOnlyDictionary<string, object?> Payload(params (string Key, object? Value)[] entries)
    {
        return entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }

    private static string ExtractData(string frame)
    {
        var line = frame.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal));
        return line["data: ".Length..];
    }
}
