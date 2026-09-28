// Task 039C: activity unit gap closure.
using System.Text.Json;
using DubbingPlatform.Application.Activity;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DubbingPlatform.UnitTests.Activity;

/// <summary>
/// Event to projection mapping for the activity log: known event types, safe
/// defaults for unknown types and missing actors, correlation-id and timestamp
/// fallbacks, the advanced-detail allowlist (URLs, bearer tokens, and secret
/// keys never reach stored metadata), and the audit-event fan-out. Entity paths
/// run over an in-process InMemory <see cref="AppDbContext"/> — no container,
/// no network, no database server.
/// </summary>
public sealed class ActivityProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromUploadCompleted_MapsKnownType_AndCarriesIdsOnly()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var message = new MediaUploaded(
            Guid.NewGuid(), "corr-abc", tenantId, projectId, Guid.NewGuid(), null, null, null, null, null,
            1, Now, 1, null, null, null, "ups-1", "internal/storage/key", "a".PadRight(64, 'a'));

        var activity = ActivityEventMapper.FromUploadCompleted(message);

        Assert.Equal(ActivityType.UploadCompleted, activity.Type);
        Assert.Equal(ActivityActorType.System, activity.ActorType);
        Assert.Null(activity.ActorUserId);
        Assert.Equal(ActivitySeverity.Info, activity.Severity);
        Assert.Equal("Upload completed.", activity.Summary);
        Assert.Equal("corr-abc", activity.CorrelationId);
        Assert.Equal(Now, activity.OccurredAt);
        Assert.Equal(tenantId, activity.TenantId);
        Assert.Equal(projectId, activity.ProjectId);
        Assert.Null(activity.ProcessingRunId);

        var metadata = Parse(activity.MetadataJson);
        Assert.Equal("ups-1", metadata.GetProperty("uploadSessionId").GetString()!);
        Assert.Equal(projectId.ToString("N"), metadata.GetProperty("projectId").GetString()!);
        // The storage key is never mapped into the projection.
        Assert.False(metadata.TryGetProperty("storageKey", out _));
        Assert.DoesNotContain("internal/storage/key", activity.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void FromUploadRejected_MapsWarning_AndRejectsValidMedia()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var invalid = new MediaValidated(
            Guid.NewGuid(), "corr", tenantId, projectId, Guid.NewGuid(), null, null, null, null, null,
            1, Now, 1, null, null, null, "media-1", false);

        var activity = ActivityEventMapper.FromUploadRejected(invalid);

        Assert.Equal(ActivityType.UploadRejected, activity.Type);
        Assert.Equal(ActivitySeverity.Warning, activity.Severity);
        Assert.Equal("media-1", Parse(activity.MetadataJson).GetProperty("mediaAssetId").GetString()!);

        var ex = Assert.Throws<DomainException>(() => ActivityEventMapper.FromUploadRejected(invalid with { IsValid = true }));
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromProcessingStarted_MapsRun_AndDefaultsMissingPipelineVersion()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var message = new RunStarted(
            Guid.NewGuid(), "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, Now, 1, null, null, null, "  ", "route-hash");

        var activity = ActivityEventMapper.FromProcessingStarted(message);

        Assert.Equal(ActivityType.ProcessingStarted, activity.Type);
        Assert.Equal(runId, activity.ProcessingRunId);
        Assert.Contains("pipeline default", activity.Summary, StringComparison.Ordinal);
        Assert.Equal("default", Parse(activity.MetadataJson).GetProperty("pipelineVersion").GetString()!);

        var named = ActivityEventMapper.FromProcessingStarted(message with { PipelineVersion = " v2 " });
        Assert.Contains("pipeline v2", named.Summary, StringComparison.Ordinal);
        Assert.Equal("v2", Parse(named.MetadataJson).GetProperty("pipelineVersion").GetString()!);
    }

    [Fact]
    public void FromProcessingStarted_RejectsEmptyRunId()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var message = new RunStarted(
            Guid.NewGuid(), "corr", tenantId, projectId, Guid.Empty, null, null, null, null, null,
            1, Now, 1, null, null, null, "v1", "route-hash");

        var ex = Assert.Throws<DomainException>(() => ActivityEventMapper.FromProcessingStarted(message));
        Assert.Contains("ProcessingRunId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromTranslationCompleted_NullsEmptyRunId_AndMapsStage()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var message = new StageCompleted(
            Guid.NewGuid(), "corr", tenantId, projectId, Guid.Empty, null, null, null, null, null,
            1, Now, 1, null, null, null, " Translation ", ["art-1", "art-2"]);

        var activity = ActivityEventMapper.FromTranslationCompleted(message);

        Assert.Equal(ActivityType.TranslationCompleted, activity.Type);
        Assert.Null(activity.ProcessingRunId);
        Assert.Equal("Stage Translation completed.", activity.Summary);
        var metadata = Parse(activity.MetadataJson);
        Assert.Equal("Translation", metadata.GetProperty("stageType").GetString()!);
        // Output artifact ids are not projected.
        Assert.False(metadata.TryGetProperty("outputArtifactIds", out _));
    }

    [Fact]
    public void FromReviewRequested_MapsWarning_AndDefaultsMissingStage()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var message = new StageReviewRequired(
            Guid.NewGuid(), "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, Now, 1, null, null, null, "   ", "rev-1");

        var activity = ActivityEventMapper.FromReviewRequested(message);

        Assert.Equal(ActivityType.ReviewRequested, activity.Type);
        Assert.Equal(ActivitySeverity.Warning, activity.Severity);
        Assert.Equal(runId, activity.ProcessingRunId);
        Assert.Contains("a stage", activity.Summary, StringComparison.Ordinal);
        var metadata = Parse(activity.MetadataJson);
        Assert.Equal("rev-1", metadata.GetProperty("reviewItemId").GetString()!);
        Assert.Equal("a stage", metadata.GetProperty("stageType").GetString()!);
    }

    [Fact]
    public void FromReviewResolved_MapsDecision_AndSafeDefaults()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var message = new ReviewResolved(
            Guid.NewGuid(), "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, Now, 1, null, null, null, "rev-1", "  ");

        var activity = ActivityEventMapper.FromReviewResolved(message);

        Assert.Equal(ActivityType.ReviewResolved, activity.Type);
        Assert.Equal(ActivitySeverity.Info, activity.Severity);
        Assert.Contains("resolved", activity.Summary, StringComparison.Ordinal);
        Assert.Equal("resolved", Parse(activity.MetadataJson).GetProperty("decision").GetString()!);
    }

    [Fact]
    public void FromEditApplied_MapsActor_AndFallsBackToSystem()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        var withActor = ActivityEventMapper.FromEditApplied(
            tenantId, projectId, Guid.NewGuid(), reviewId, actorId, "corr", Now);
        Assert.Equal(ActivityType.EditApplied, withActor.Type);
        Assert.Equal(ActivityActorType.User, withActor.ActorType);
        Assert.Equal(actorId, withActor.ActorUserId);
        Assert.Equal(reviewId.ToString("N"), Parse(withActor.MetadataJson).GetProperty("reviewId").GetString()!);

        // Missing actor => explicit System actor, never a throw.
        var withoutActor = ActivityEventMapper.FromEditApplied(
            tenantId, projectId, null, reviewId, null, "corr", Now);
        Assert.Equal(ActivityActorType.System, withoutActor.ActorType);
        Assert.Null(withoutActor.ActorUserId);
        Assert.Null(withoutActor.ProcessingRunId);
    }

    [Fact]
    public void FromExportCompleted_MapsSuccessAndFailure_WithSafeFormatDefault()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var exportJobId = Guid.NewGuid();

        var ok = ActivityEventMapper.FromExportCompleted(tenantId, projectId, Guid.NewGuid(), exportJobId, "srt", true, "corr", Now);
        Assert.Equal(ActivityType.ExportCompleted, ok.Type);
        Assert.Equal(ActivitySeverity.Info, ok.Severity);
        Assert.Equal("Export (srt) completed.", ok.Summary);
        var metadata = Parse(ok.MetadataJson);
        Assert.Equal(exportJobId.ToString("N"), metadata.GetProperty("exportJobId").GetString()!);
        Assert.Equal("srt", metadata.GetProperty("format").GetString()!);

        var failed = ActivityEventMapper.FromExportCompleted(tenantId, projectId, null, exportJobId, "  ", false, "corr", Now);
        Assert.Equal(ActivityType.ExportFailed, failed.Type);
        Assert.Equal(ActivitySeverity.Error, failed.Severity);
        Assert.Equal("Export (export) failed.", failed.Summary);
        Assert.Equal("export", Parse(failed.MetadataJson).GetProperty("format").GetString()!);
    }

    [Fact]
    public void FromProcessingTerminal_MapsRunOutcome_AndNullsEmptyRunId()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var completed = ActivityEventMapper.FromProcessingCompleted(new RunCompleted(
            Guid.NewGuid(), "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, Now, 1, null, null, null, "out-1"));
        Assert.Equal(ActivityType.ProcessingCompleted, completed.Type);
        Assert.Equal(runId, completed.ProcessingRunId);

        var failed = ActivityEventMapper.FromProcessingFailed(new RunFailed(
            Guid.NewGuid(), "corr", tenantId, projectId, Guid.Empty, null, null, null, null, null,
            1, Now, 1, null, null, null, "  ", "may embed a path"));
        Assert.Equal(ActivityType.ProcessingFailed, failed.Type);
        Assert.Equal(ActivitySeverity.Error, failed.Severity);
        Assert.Null(failed.ProcessingRunId);
        Assert.Contains("UNKNOWN", failed.Summary, StringComparison.Ordinal);
        Assert.Equal("UNKNOWN", Parse(failed.MetadataJson).GetProperty("errorCode").GetString()!);
        Assert.DoesNotContain("may embed a path", failed.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "message-id-fallback")]
    [InlineData("", "message-id-fallback")]
    [InlineData("   ", "message-id-fallback")]
    [InlineData("  corr-xyz  ", "corr-xyz")]
    public void CorrelationFor_FallsBackToMessageId_WhenMissing(string? correlationId, string expected)
    {
        var messageId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var activity = ActivityEventMapper.FromUploadCompleted(new MediaUploaded(
            messageId, correlationId!, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, null,
            1, Now, 1, null, null, null, "ups-1", "key", null));

        if (expected == "message-id-fallback")
        {
            Assert.Equal(messageId.ToString("N"), activity.CorrelationId);
        }
        else
        {
            Assert.Equal(expected, activity.CorrelationId);
        }
    }

    [Fact]
    public void RequireCorrelation_RejectsMissingIds_AndTrims()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var ex = Assert.Throws<DomainException>(() => ActivityEventMapper.FromEditApplied(
            tenantId, projectId, null, Guid.NewGuid(), null, "   ", Now));
        Assert.Contains("CorrelationId", ex.Message, StringComparison.Ordinal);

        var ok = ActivityEventMapper.FromEditApplied(
            tenantId, projectId, null, Guid.NewGuid(), null, "  corr-1  ", Now);
        Assert.Equal("corr-1", ok.CorrelationId);
    }

    [Fact]
    public void EventMapper_RejectsEmptyIdentity_AcrossEveryMapper()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        Assert.Throws<ArgumentNullException>(() => ActivityEventMapper.FromUploadCompleted(null!));
        Assert.Throws<ArgumentNullException>(() => ActivityEventMapper.FromUploadRejected(null!));
        Assert.Throws<ArgumentNullException>(() => ActivityEventMapper.FromProcessingStarted(null!));
        Assert.Throws<ArgumentNullException>(() => ActivityEventMapper.FromTranslationCompleted(null!));
        Assert.Throws<ArgumentNullException>(() => ActivityEventMapper.FromReviewRequested(null!));
        Assert.Throws<ArgumentNullException>(() => ActivityEventMapper.FromReviewResolved(null!));
        Assert.Throws<ArgumentNullException>(() => ActivityEventMapper.FromProcessingCompleted(null!));
        Assert.Throws<ArgumentNullException>(() => ActivityEventMapper.FromProcessingFailed(null!));

        var noTenant = new MediaUploaded(Guid.NewGuid(), "corr", Guid.Empty, projectId, runId, null, null, null, null, null, 1, Now, 1, null, null, null, "u", "k", null);
        var noProject = new MediaUploaded(Guid.NewGuid(), "corr", tenantId, Guid.Empty, runId, null, null, null, null, null, 1, Now, 1, null, null, null, "u", "k", null);
        Assert.Throws<DomainException>(() => ActivityEventMapper.FromUploadCompleted(noTenant));
        Assert.Throws<DomainException>(() => ActivityEventMapper.FromUploadCompleted(noProject));

        Assert.Throws<DomainException>(() => ActivityEventMapper.FromEditApplied(tenantId, projectId, null, Guid.Empty, null, "corr", Now));
        Assert.Throws<DomainException>(() => ActivityEventMapper.FromExportCompleted(tenantId, projectId, null, Guid.Empty, "srt", true, "corr", Now));
        Assert.Throws<DomainException>(() => ActivityEventMapper.FromEditApplied(Guid.Empty, projectId, null, Guid.NewGuid(), null, "corr", Now));
        Assert.Throws<DomainException>(() => ActivityEventMapper.FromEditApplied(tenantId, Guid.Empty, null, Guid.NewGuid(), null, "corr", Now));
        Assert.Throws<DomainException>(() => ActivityEventMapper.FromExportCompleted(Guid.Empty, projectId, null, Guid.NewGuid(), "srt", true, "corr", Now));
        Assert.Throws<DomainException>(() => ActivityEventMapper.FromExportCompleted(tenantId, Guid.Empty, null, Guid.NewGuid(), "srt", true, "corr", Now));
    }

    [Fact]
    public void BuildMetadata_ReturnsNull_ForNullEntries()
    {
        Assert.Null(ActivityProjector.BuildMetadata(null));
    }

    [Fact]
    public void BuildMetadata_DropsUrlValues_SoSignedUrlNeverCarriesTheLink()
    {
        var json = ActivityProjector.BuildMetadata(new Dictionary<string, object?>
        {
            ["signedUrl"] = "https://cdn.example.test/object?sig=SECRETSIG",
            ["resourceUrl"] = "http://internal.example.test/path",
        });

        var metadata = Parse(json);
        Assert.Equal("[redacted-url]", metadata.GetProperty("signedUrl").GetString()!);
        Assert.Equal("[redacted-url]", metadata.GetProperty("resourceUrl").GetString()!);
        Assert.DoesNotContain("SECRETSIG", json!, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", json!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", json!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("apiKey")]
    [InlineData("secret")]
    [InlineData("password")]
    [InlineData("credential")]
    [InlineData("privateKey")]
    [InlineData("leaseToken")]
    public void BuildMetadata_RedactsSensitiveKeys_AndNeverLeaksTheirValues(string key)
    {
        var json = ActivityProjector.BuildMetadata(new Dictionary<string, object?> { [key] = "SUPER-SECRET-VALUE" });

        var metadata = Parse(json);
        Assert.Equal("[REDACTED]", metadata.GetProperty(key).GetString()!);
        Assert.DoesNotContain("SUPER-SECRET-VALUE", json!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMetadata_StripsBearerTokens_FromValues()
    {
        var json = ActivityProjector.BuildMetadata(new Dictionary<string, object?>
        {
            ["note"] = "auth bearer abc.def-123 trailing",
        });

        Assert.DoesNotContain("abc.def-123", json!, StringComparison.Ordinal);
        Assert.Contains("[redacted-token]", json!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMetadata_KeepsOpaqueCountersAndTypes_AndNeutralisesUrls()
    {
        var json = ActivityProjector.BuildMetadata(new Dictionary<string, object?>
        {
            ["segmentCount"] = 3,
            ["stageType"] = "Translation",
            ["missing"] = null,
            ["link"] = "https://example.test/x",
        });

        var metadata = Parse(json);
        Assert.Equal(3, metadata.GetProperty("segmentCount").GetInt32());
        Assert.Equal("Translation", metadata.GetProperty("stageType").GetString()!);
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("missing").ValueKind);
        Assert.Equal("[redacted-url]", metadata.GetProperty("link").GetString()!);
    }

    [Fact]
    public void BuildMetadata_DocumentedResidual_SecretAssignmentsInValuesSurvive()
    {
        // Explicit documentation of current behaviour: BuildMetadata redacts by
        // KEY name and by URL/bearer-token value shape only. A secret assignment
        // buried inside an otherwise innocuous value is not redacted here; the
        // end-to-end guarantee comes from the projector's own metadata pass (see
        // AppendAsync_StoresSanitizedSummary_AndDropsUrlsFromMetadata).
        var json = ActivityProjector.BuildMetadata(new Dictionary<string, object?>
        {
            ["note"] = "api_key=abcdef123",
        });

        Assert.Equal("api_key=abcdef123", Parse(json).GetProperty("note").GetString()!);
    }

    [Fact]
    public void BuildMetadata_RedactsStorageKeyLikeKeys_SoNoCredentialBearingKeySurvives()
    {
        var json = ActivityProjector.BuildMetadata(new Dictionary<string, object?>
        {
            ["storageKey"] = "tenant-abc/object.wav",
            ["apiKey"] = "ak-123",
            ["credentialBlob"] = "cred-123",
        });

        var metadata = Parse(json);
        Assert.Equal("[REDACTED]", metadata.GetProperty("storageKey").GetString()!);
        Assert.Equal("[REDACTED]", metadata.GetProperty("apiKey").GetString()!);
        Assert.Equal("[REDACTED]", metadata.GetProperty("credentialBlob").GetString()!);
        Assert.DoesNotContain("ak-123", json!, StringComparison.Ordinal);
        Assert.DoesNotContain("cred-123", json!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMetadata_StripsUrlsSoNoSignedLinkSurvives_UnderAnyKeyName()
    {
        var json = ActivityProjector.BuildMetadata(new Dictionary<string, object?>
        {
            ["downloadLink"] = "https://cdn.example.test/o?sig=SIG1&x=1",
            ["canonicalLink"] = "http://internal.example.test/p",
            ["nested"] = "bearer abc.def",
        });

        var metadata = Parse(json);
        Assert.Equal("[redacted-url]", metadata.GetProperty("downloadLink").GetString()!);
        Assert.Equal("[redacted-url]", metadata.GetProperty("canonicalLink").GetString()!);
        Assert.Equal("[redacted-token]", metadata.GetProperty("nested").GetString()!);
        Assert.DoesNotContain("SIG1", json!, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", json!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", json!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMetadata_DocumentedResidual_PlainInternalPathsAreNotRedacted()
    {
        // Explicit, honest documentation of current behaviour: only
        // secret/password/passwd/pwd/token/key/credential key names and
        // URL/token-bearing values are neutralised. A bare internal path with no
        // scheme passes through, so callers must never put one in metadata.
        var json = ActivityProjector.BuildMetadata(new Dictionary<string, object?>
        {
            ["internalPath"] = "/var/lib/media/synthetic.wav",
        });

        Assert.Equal("/var/lib/media/synthetic.wav", Parse(json).GetProperty("internalPath").GetString()!);
    }

    [Theory]
    [InlineData(ActivityType.ReviewRequested, true, "review.requested")]
    [InlineData(ActivityType.ReviewResolved, true, "review.resolved")]
    [InlineData(ActivityType.EditApplied, true, "review.edit_applied")]
    [InlineData(ActivityType.ExportCompleted, true, "export.completed")]
    [InlineData(ActivityType.ExportFailed, true, "export.failed")]
    [InlineData(ActivityType.UploadCompleted, false, "activity.uploadcompleted")]
    [InlineData(ActivityType.ProcessingCompleted, false, "activity.processingcompleted")]
    public void AuditFanOut_CoversSecurityRelevantTypes_WithSafeDefault(
        ActivityType type,
        bool expectedRelevant,
        string expectedAction)
    {
        Assert.Equal(expectedRelevant, ActivityProjector.IsSecurityRelevant(type));
        Assert.Equal(expectedAction, ActivityProjector.AuditActionFor(type));
    }

    [Fact]
    public void AuditActionFor_UnknownType_FallsBackToSafeDefault_WithoutThrowing()
    {
        var unknown = (ActivityType)999;

        // An unknown enum never throws; it lands in the documented default bucket.
        Assert.False(ActivityProjector.IsSecurityRelevant(unknown));
        Assert.Equal("activity.999", ActivityProjector.AuditActionFor(unknown));
    }

    [Fact]
    public void Sanitize_RedactsUrlsTokensAndSecrets_ThenTrimsAndTruncates()
    {
        Assert.Equal("[redacted-url]", ActivityProjector.Sanitize("https://example.test/x", 100));
        Assert.Equal("[redacted-token]", ActivityProjector.Sanitize("bearer abc123", 100));
        Assert.Equal("trimmed", ActivityProjector.Sanitize("  trimmed  ", 100));
        Assert.Equal("abc", ActivityProjector.Sanitize("abcdef", 3));
        Assert.Equal(ActivityEvent.MaxSummaryLength, ActivityProjector.Sanitize(new string('x', 5000), ActivityEvent.MaxSummaryLength).Length);

        var redacted = ActivityProjector.Sanitize("password=hunter2", 100);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);

        Assert.Throws<DomainException>(() => ActivityProjector.Sanitize("   ", 100));
        Assert.Throws<DomainException>(() => ActivityProjector.Sanitize(string.Empty, 100));
    }

    [Fact]
    public async Task AppendAsync_StoresSanitizedSummary_AndDropsUrlsFromMetadata()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        var projector = new ActivityProjector(factory);

        var @event = await projector.AppendAsync(new ActivityInput(
            tenantId, projectId, Guid.NewGuid(),
            ActivityType.ProcessingCompleted, ActivityActorType.System, null,
            "  Completed https://example.test/x  ", ActivitySeverity.Info,
            "corr-1", Now,
            "{\"note\":\"see https://example.test/y and bearer abc123 now\",\"runId\":\"run-1\"}"));

        Assert.Equal(ActivityEvent.SupportedSchemaVersion, @event.SchemaVersion);
        Assert.DoesNotContain("https://", @event.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", @event.MetadataJson!, StringComparison.Ordinal);
        Assert.Contains("[redacted-url]", @event.MetadataJson!, StringComparison.Ordinal);
        Assert.Contains("[redacted-token]", @event.MetadataJson!, StringComparison.Ordinal);
        Assert.Equal("corr-1", @event.CorrelationId);
        Assert.Equal(Now, @event.OccurredAt);
    }

    [Fact]
    public async Task AppendAsync_RejectsEmptyTenant_AndInvalidMetadata()
    {
        var tenantId = Guid.NewGuid();
        using var factory = CreateFactory();
        var projector = new ActivityProjector(factory);

        await Assert.ThrowsAsync<ArgumentNullException>(() => projector.AppendAsync(null!));
        Assert.Throws<ArgumentNullException>(() => new ActivityProjector(null!));

        await Assert.ThrowsAsync<DomainException>(() => projector.AppendAsync(ActivityInput(
            Guid.Empty, Guid.NewGuid(), "corr", Now)));
        await Assert.ThrowsAsync<DomainException>(() => projector.AppendAsync(ActivityInput(
            tenantId, Guid.NewGuid(), "corr", Now, metadataJson: "   ")));
        await Assert.ThrowsAsync<DomainException>(() => projector.AppendAsync(ActivityInput(
            tenantId, Guid.NewGuid(), "corr", Now, metadataJson: "not json")));
    }

    [Fact]
    public async Task AppendAsync_IsIdempotent_ForRedeliveredEvents()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        using var factory = CreateFactory();
        var projector = new ActivityProjector(factory);
        var input = ActivityInput(tenantId, projectId, "corr-dup", Now, runId);

        var first = await projector.AppendAsync(input);
        var second = await projector.AppendAsync(input);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, CountAll(factory));

        // A different correlation id appends a new row (dedup is correlation-scoped).
        var other = await projector.AppendAsync(ActivityInput(tenantId, projectId, "corr-other", Now, runId));
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal(2, CountAll(factory));
    }

    [Fact]
    public async Task AppendAsync_WritesAuditEvent_OnlyForSecurityRelevantTypes()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        using var factory = CreateFactory();
        var projector = new ActivityProjector(factory);

        await projector.AppendAsync(new ActivityInput(
            tenantId, projectId, Guid.NewGuid(),
            ActivityType.ReviewRequested, ActivityActorType.User, actorId,
            "Review requested for stage Translation.", ActivitySeverity.Warning,
            "corr-review", Now, "{\"reviewItemId\":\"rev-1\"}"));
        await projector.AppendAsync(ActivityInput(tenantId, projectId, "corr-plain", Now));

        using var scope = TenantContext.BeginScope(tenantId);
        using var db = factory.CreateSetup();
        var audits = db.Set<AuditEvent>().AsNoTracking().ToList();
        var audit = Assert.Single(audits);
        Assert.Equal("review.requested", audit.Action);
        Assert.Equal(actorId.ToString("D"), audit.Actor);
        Assert.Equal("DubbingProject", audit.ResourceType);
        Assert.Equal(projectId.ToString("D"), audit.ResourceId);
    }

    [Fact]
    public async Task AppendAsync_MissingActor_AuditsAsLowercaseSystemActor()
    {
        var tenantId = Guid.NewGuid();
        using var factory = CreateFactory();
        var projector = new ActivityProjector(factory);

        await projector.AppendAsync(new ActivityInput(
            tenantId, null, null,
            ActivityType.EditApplied, ActivityActorType.System, null,
            "Reviewer edit applied.", ActivitySeverity.Info,
            "corr-tls", Now, "{\"reviewId\":\"rev-1\"}"));

        using var scope = TenantContext.BeginScope(tenantId);
        using var db = factory.CreateSetup();
        var audit = Assert.Single(db.Set<AuditEvent>().AsNoTracking().ToList());
        Assert.Equal("system", audit.Actor);
        Assert.Equal("Tenant", audit.ResourceType);
        Assert.Equal(tenantId.ToString("D"), audit.ResourceId);
    }

    [Fact]
    public async Task ListAsync_PagesByOccurrence_AndClampsArguments()
    {
        var tenantId = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        var projector = new ActivityProjector(factory);

        for (var index = 0; index < 3; index++)
        {
            await projector.AppendAsync(ActivityInput(tenantId, projectId, $"corr-{index}", Now.AddMinutes(index)));
        }

        await projector.AppendAsync(ActivityInput(otherTenant, projectId, "corr-other", Now));

        var (all, total) = await projector.ListAsync(tenantId, projectId, 1, 50);
        Assert.Equal(3, total);
        Assert.Equal(3, all.Count);
        // Oldest first.
        Assert.Equal([Now, Now.AddMinutes(1), Now.AddMinutes(2)], all.Select(e => e.OccurredAt).ToArray());

        // page/pageSize are clamped, never negative.
        var (clamped, clampedTotal) = await projector.ListAsync(tenantId, projectId, 0, 5000);
        Assert.Equal(3, clampedTotal);
        Assert.Equal(3, clamped.Count);

        var (page2, _) = await projector.ListAsync(tenantId, projectId, 2, 2);
        Assert.Single(page2);

        await Assert.ThrowsAsync<DomainException>(() => projector.ListAsync(Guid.Empty, projectId, 1, 10));
        await Assert.ThrowsAsync<DomainException>(() => projector.ListAsync(tenantId, Guid.Empty, 1, 10));
    }

    [Fact]
    public void SchemaVersion_IsFrozen_ToOne()
    {
        Assert.Equal(1, ActivityEvent.SupportedSchemaVersion);
        Assert.Equal(128, ActivityEvent.MaxCorrelationIdLength);
    }

    private static ActivityInput ActivityInput(
        Guid tenantId,
        Guid? projectId,
        string correlationId,
        DateTimeOffset occurredAt,
        Guid? processingRunId = null,
        string? metadataJson = null)
    {
        return new ActivityInput(
            tenantId, projectId, processingRunId,
            ActivityType.ProcessingCompleted, ActivityActorType.System, null,
            "Processing completed.", ActivitySeverity.Info,
            correlationId, occurredAt, metadataJson);
    }

    private static JsonElement Parse(string? json)
    {
        Assert.False(string.IsNullOrWhiteSpace(json), "Expected metadata JSON to be present.");
        using var document = JsonDocument.Parse(json!);
        return document.RootElement.Clone();
    }

    private static int CountAll(TestContextFactory factory)
    {
        using var scope = TenantContext.BeginMaintenanceScope();
        using var db = ((IStageExecutionContextFactory)factory).CreateDbContext();
        return db.Set<ActivityEvent>().AsNoTracking().Count();
    }

    private static TestContextFactory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("ActivityProjectionTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
        return new TestContextFactory(options);
    }

    private sealed class TestAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public bool SealDispose { get; set; }

        public override void Dispose()
        {
            if (!SealDispose)
            {
                base.Dispose();
            }
        }

        public override ValueTask DisposeAsync()
        {
            if (!SealDispose)
            {
                return base.DisposeAsync();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestContextFactory(DbContextOptions<AppDbContext> options) : IStageExecutionContextFactory, IDisposable
    {
        private readonly List<TestAppDbContext> _all = [];

        public TestAppDbContext CreateSetup()
        {
            var context = new TestAppDbContext(options);
            _all.Add(context);
            return context;
        }

        DbContext IStageExecutionContextFactory.CreateDbContext()
        {
            var context = new TestAppDbContext(options) { SealDispose = true };
            _all.Add(context);
            return context;
        }

        public void Dispose()
        {
            foreach (var context in _all)
            {
                context.SealDispose = false;
                context.Dispose();
            }
        }
    }
}
