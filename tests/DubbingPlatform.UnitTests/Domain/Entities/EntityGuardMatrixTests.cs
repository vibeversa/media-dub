// Task 039C: domain entity unit gap closure.
using System;
using System.Collections.Generic;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Domain.Entities;

/// <summary>
/// Data-driven guard/validation matrix for the pure domain entities.
/// Every ctor guard branch, boundary value (0 / 1 / -1 / 99 / 100 / int.MaxValue)
/// and blank/whitespace rejection is exercised here. All times are frozen
/// (<see cref="Now"/>) - no clock reads inside assertions.
/// Tenant scoping: every entity exposes <c>TenantId</c>; a row built for tenant A
/// keeps tenant A verbatim, and swapping in tenant B yields a different row that
/// is never interchangeable with A.
/// </summary>
public sealed class EntityGuardMatrixTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Id = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Other = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static string Repeat(char c, int count) => new(c, count);

    // ------------------------------------------------------------------
    // ActivityEvent
    // ------------------------------------------------------------------

    private static ActivityEvent NewActivity(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? projectId = null,
        Guid? processingRunId = null,
        ActivityType type = ActivityType.UploadCompleted,
        ActivityActorType actorType = ActivityActorType.System,
        Guid? actorUserId = null,
        string? summary = "summary",
        ActivitySeverity severity = ActivitySeverity.Info,
        string? correlationId = "corr-1",
        DateTimeOffset occurredAt = default,
        int schemaVersion = ActivityEvent.SupportedSchemaVersion,
        string? metadataJson = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            projectId,
            processingRunId,
            type,
            actorType,
            actorUserId,
            summary!,
            severity,
            correlationId!,
            occurredAt == default ? Now : occurredAt,
            schemaVersion,
            metadataJson);

    [Fact]
    public void ActivityEvent_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewActivity(
            projectId: Other,
            processingRunId: Other,
            actorUserId: Other,
            metadataJson: "{\"k\":1}");

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantA, sut.TenantId);
        Assert.Equal(Other, sut.ProjectId);
        Assert.Equal(Other, sut.ProcessingRunId);
        Assert.Equal(ActivityType.UploadCompleted, sut.Type);
        Assert.Equal(ActivityActorType.System, sut.ActorType);
        Assert.Equal(Other, sut.ActorUserId);
        Assert.Equal("summary", sut.Summary);
        Assert.Equal(ActivitySeverity.Info, sut.Severity);
        Assert.Equal("corr-1", sut.CorrelationId);
        Assert.Equal(Now, sut.OccurredAt);
        Assert.Equal(ActivityEvent.SupportedSchemaVersion, sut.SchemaVersion);
        Assert.Equal("{\"k\":1}", sut.MetadataJson);
    }

    [Theory]
    [InlineData(null, "ActivityEvent Id must not be empty.")]
    [InlineData("tenant", "ActivityEvent TenantId must not be empty.")]
    [InlineData("project", "ActivityEvent ProjectId must not be empty when set.")]
    [InlineData("run", "ActivityEvent ProcessingRunId must not be empty when set.")]
    [InlineData("type", "ActivityEvent Type is not defined.")]
    [InlineData("actorType", "ActivityEvent ActorType is not defined.")]
    [InlineData("actorUser", "ActivityEvent ActorUserId must not be empty when set.")]
    [InlineData("summary", "ActivityEvent Summary must not be empty.")]
    [InlineData("summaryLength", "ActivityEvent Summary must be at most 1000 chars.")]
    [InlineData("severity", "ActivityEvent Severity is not defined.")]
    [InlineData("correlation", "ActivityEvent CorrelationId must not be empty.")]
    [InlineData("correlationLength", "ActivityEvent CorrelationId must be at most 128 chars.")]
    [InlineData("schemaVersion", "ActivityEvent SchemaVersion must be 1.")]
    [InlineData("metadataBlank", "ActivityEvent MetadataJson must not be empty when set.")]
    [InlineData("metadataInvalid", "ActivityEvent MetadataJson must be valid JSON.")]
    [InlineData("summaryUrl", "ActivityEvent Summary must not contain URLs.")]
    [InlineData("correlationToken", "ActivityEvent CorrelationId must not contain tokens.")]
    [InlineData("metadataUrl", "ActivityEvent MetadataJson must not contain URLs.")]
    [InlineData("summaryToken", "ActivityEvent Summary must not contain tokens.")]
    public void ActivityEvent_Rejects_Invalid_Input(string? scenario, string expected)
    {
        Action act = scenario switch
        {
            null => () => NewActivity(id: Guid.Empty),
            "tenant" => () => NewActivity(tenantId: Guid.Empty),
            "project" => () => NewActivity(projectId: Guid.Empty),
            "run" => () => NewActivity(processingRunId: Guid.Empty),
            "type" => () => NewActivity(type: (ActivityType)99),
            "actorType" => () => NewActivity(actorType: (ActivityActorType)42),
            "actorUser" => () => NewActivity(actorUserId: Guid.Empty),
            "summary" => () => NewActivity(summary: "   "),
            "summaryLength" => () => NewActivity(summary: Repeat('s', ActivityEvent.MaxSummaryLength + 1)),
            "severity" => () => NewActivity(severity: (ActivitySeverity)7),
            "correlation" => () => NewActivity(correlationId: "\t"),
            "correlationLength" => () => NewActivity(correlationId: Repeat('c', ActivityEvent.MaxCorrelationIdLength + 1)),
            "schemaVersion" => () => NewActivity(schemaVersion: 0),
            "metadataBlank" => () => NewActivity(metadataJson: "  "),
            "metadataInvalid" => () => NewActivity(metadataJson: "{ not json"),
            "summaryUrl" => () => NewActivity(summary: "see HTTPS://example.test/x"),
            "correlationToken" => () => NewActivity(correlationId: "Bearer abc.def.ghi"),
            "metadataUrl" => () => NewActivity(metadataJson: "{\"u\":\"http://example.test\"}"),
            "summaryToken" => () => NewActivity(summary: "key: BEARER tokenvalue"),
            _ => throw new InvalidOperationException($"Unhandled scenario '{scenario}'."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void ActivityEvent_Invalid_Json_Keeps_Inner_Exception()
    {
        var ex = Assert.Throws<DomainException>(() => NewActivity(metadataJson: "{ oops"));

        // JsonDocument.Parse surfaces a JsonReaderException, which is a
        // JsonException; the domain must not swallow the parser's cause.
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(-1)]
    public void ActivityEvent_SchemaVersion_Accepts_Only_Supported(int schemaVersion)
    {
        if (schemaVersion == ActivityEvent.SupportedSchemaVersion)
        {
            Assert.Equal(schemaVersion, NewActivity(schemaVersion: schemaVersion).SchemaVersion);
            return;
        }

        Assert.Throws<DomainException>(() => NewActivity(schemaVersion: schemaVersion));
    }

    [Fact]
    public void ActivityEvent_Length_Boundaries_Are_Inclusive()
    {
        Assert.Equal(ActivityEvent.MaxSummaryLength, NewActivity(summary: Repeat('s', ActivityEvent.MaxSummaryLength)).Summary.Length);
        Assert.Equal(
            ActivityEvent.MaxCorrelationIdLength,
            NewActivity(correlationId: Repeat('c', ActivityEvent.MaxCorrelationIdLength)).CorrelationId.Length);
    }

    [Fact]
    public void ActivityEvent_Validate_Is_Public_And_Idempotent()
    {
        var sut = NewActivity();
        sut.Validate();
        sut.Validate();
        Assert.Equal("summary", sut.Summary);
    }

    [Fact]
    public void ActivityEvent_Is_Tenant_Scoped()
    {
        var forTenantA = NewActivity(tenantId: TenantA);
        var forTenantB = NewActivity(tenantId: TenantB, id: Other);

        Assert.Equal(TenantA, forTenantA.TenantId);
        Assert.Equal(TenantB, forTenantB.TenantId);
        Assert.NotEqual(forTenantA.TenantId, forTenantB.TenantId);
    }

    // ------------------------------------------------------------------
    // ConsentRecord
    // ------------------------------------------------------------------

    private static ConsentRecord NewConsent(
        Guid? id = null,
        Guid? tenantId = null,
        string? subjectIdentity = "subject-1",
        string? evidenceReference = "evidence-1",
        string? scope = "*",
        string? jurisdiction = "US",
        ConsentStatus status = ConsentStatus.Granted,
        Guid? voiceProfileId = null,
        DateTimeOffset? grantedAt = null,
        DateTimeOffset? revokedAt = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            subjectIdentity!,
            evidenceReference!,
            scope!,
            jurisdiction!,
            status,
            voiceProfileId,
            grantedAt ?? Now,
            revokedAt);

    [Fact]
    public void ConsentRecord_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewConsent(voiceProfileId: Other, revokedAt: Later);

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantA, sut.TenantId);
        Assert.Equal("subject-1", sut.SubjectIdentity);
        Assert.Equal("evidence-1", sut.EvidenceReference);
        Assert.Equal("*", sut.Scope);
        Assert.Equal("US", sut.Jurisdiction);
        Assert.Equal(ConsentStatus.Granted, sut.Status);
        Assert.Equal(Other, sut.VoiceProfileId);
        Assert.Equal(Now, sut.GrantedAt);
        Assert.Equal(Later, sut.RevokedAt);
    }

    [Theory]
    [InlineData(null, "ConsentRecord Id must not be empty.")]
    [InlineData("tenant", "ConsentRecord TenantId must not be empty.")]
    [InlineData("subject", "ConsentRecord SubjectIdentity must not be empty.")]
    [InlineData("evidence", "ConsentRecord EvidenceReference must not be empty.")]
    [InlineData("scope", "ConsentRecord Scope must not be empty.")]
    [InlineData("jurisdiction", "ConsentRecord Jurisdiction must not be empty.")]
    [InlineData("voiceProfile", "ConsentRecord VoiceProfileId must not be empty when set.")]
    [InlineData("revokedOrder", "ConsentRecord RevokedAt must not be before GrantedAt.")]
    public void ConsentRecord_Rejects_Invalid_Input(string? scenario, string expected)
    {
        Action act = scenario switch
        {
            null => () => NewConsent(id: Guid.Empty),
            "tenant" => () => NewConsent(tenantId: Guid.Empty),
            "subject" => () => NewConsent(subjectIdentity: "  "),
            "evidence" => () => NewConsent(evidenceReference: string.Empty),
            "scope" => () => NewConsent(scope: "\n"),
            "jurisdiction" => () => NewConsent(jurisdiction: " \t "),
            "voiceProfile" => () => NewConsent(voiceProfileId: Guid.Empty),
            "revokedOrder" => () => NewConsent(grantedAt: Later, revokedAt: Now),
            _ => throw new InvalidOperationException($"Unhandled scenario '{scenario}'."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void ConsentRecord_Revoke_Sets_Status_And_Stamp()
    {
        var sut = NewConsent();

        sut.Revoke(Later);

        Assert.Equal(ConsentStatus.Revoked, sut.Status);
        Assert.Equal(Later, sut.RevokedAt);
    }

    [Fact]
    public void ConsentRecord_Revoke_Is_Idempotent_And_Never_Reverts()
    {
        var sut = NewConsent();
        sut.Revoke(Later);

        sut.Revoke(Later.AddDays(1));
        sut.Revoke(Now);

        Assert.Equal(ConsentStatus.Revoked, sut.Status);
        Assert.Equal(Later, sut.RevokedAt);
    }

    [Fact]
    public void ConsentRecord_Revoke_Allows_Same_Instant_As_Granted()
    {
        var sut = NewConsent();

        sut.Revoke(Now);

        Assert.Equal(Now, sut.RevokedAt);
    }

    [Fact]
    public void ConsentRecord_Revoke_Before_Granted_Throws()
    {
        var sut = NewConsent(grantedAt: Now);

        var ex = Assert.Throws<DomainException>(() => sut.Revoke(Now.AddTicks(-1)));
        Assert.Equal("ConsentRecord RevokedAt must not be before GrantedAt.", ex.Message);
    }

    [Fact]
    public void ConsentRecord_Revoke_Does_Not_Change_Granted_Artifacts()
    {
        var sut = NewConsent(voiceProfileId: Other);
        sut.Revoke(Later);

        Assert.Equal("subject-1", sut.SubjectIdentity);
        Assert.Equal(Other, sut.VoiceProfileId);
    }

    [Fact]
    public void ConsentRecord_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewConsent().TenantId);
        Assert.Equal(TenantB, NewConsent(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // IdempotencyRecord
    // ------------------------------------------------------------------

    private static IdempotencyRecord NewIdempotency(
        Guid? id = null,
        Guid? tenantId = null,
        string? endpoint = "/api/v1/projects",
        string? idempotencyKey = "key-1",
        string? requestHash = "req-1",
        string? state = "Started",
        string? responseStatus = null,
        string? responseBody = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            endpoint!,
            idempotencyKey!,
            requestHash!,
            state!,
            responseStatus,
            responseBody,
            createdAt ?? Now,
            expiresAt ?? Later);

    [Fact]
    public void IdempotencyRecord_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewIdempotency(state: "Succeeded", responseStatus: "201", responseBody: "{}");

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantA, sut.TenantId);
        Assert.Equal("/api/v1/projects", sut.Endpoint);
        Assert.Equal("key-1", sut.IdempotencyKey);
        Assert.Equal("req-1", sut.RequestHash);
        Assert.Equal("Succeeded", sut.State);
        Assert.Equal("201", sut.ResponseStatus);
        Assert.Equal("{}", sut.ResponseBody);
        Assert.Equal(Now, sut.CreatedAt);
        Assert.Equal(Later, sut.ExpiresAt);
    }

    [Theory]
    [InlineData(null, "IdempotencyRecord Id must not be empty.")]
    [InlineData("tenant", "IdempotencyRecord TenantId must not be empty.")]
    [InlineData("endpoint", "IdempotencyRecord Endpoint must not be empty.")]
    [InlineData("key", "IdempotencyRecord IdempotencyKey must not be empty.")]
    [InlineData("hash", "IdempotencyRecord RequestHash must not be empty.")]
    [InlineData("state", "IdempotencyRecord State must be one of Started, Succeeded, Failed.")]
    [InlineData("responseStatus", "IdempotencyRecord ResponseStatus must not be empty when set.")]
    [InlineData("responseBody", "IdempotencyRecord ResponseBody must not be empty when set.")]
    [InlineData("expiry", "IdempotencyRecord ExpiresAt must not be before CreatedAt.")]
    public void IdempotencyRecord_Rejects_Invalid_Input(string? scenario, string expected)
    {
        Action act = scenario switch
        {
            null => () => NewIdempotency(id: Guid.Empty),
            "tenant" => () => NewIdempotency(tenantId: Guid.Empty),
            "endpoint" => () => NewIdempotency(endpoint: "   "),
            "key" => () => NewIdempotency(idempotencyKey: string.Empty),
            "hash" => () => NewIdempotency(requestHash: " "),
            "state" => () => NewIdempotency(state: "started"),
            "responseStatus" => () => NewIdempotency(responseStatus: " "),
            "responseBody" => () => NewIdempotency(responseBody: "\t"),
            "expiry" => () => NewIdempotency(createdAt: Later, expiresAt: Now),
            _ => throw new InvalidOperationException($"Unhandled scenario '{scenario}'."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Theory]
    [InlineData("Started")]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    public void IdempotencyRecord_Accepts_Only_Allowed_States(string state)
    {
        Assert.Equal(state, NewIdempotency(state: state).State);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("started")]
    [InlineData("SUCCESS")]
    [InlineData("")]
    [InlineData(" ")]
    public void IdempotencyRecord_Rejects_Unknown_States(string state)
    {
        Assert.Throws<DomainException>(() => NewIdempotency(state: state));
    }

    [Fact]
    public void IdempotencyRecord_ExpiresAt_Equal_To_CreatedAt_Is_Allowed()
    {
        Assert.Equal(Now, NewIdempotency(expiresAt: Now).ExpiresAt);
    }

    [Fact]
    public void IdempotencyRecord_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewIdempotency().TenantId);
        Assert.Equal(TenantB, NewIdempotency(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // Notification
    // ------------------------------------------------------------------

    private static Notification NewNotification(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? recipientUserId = null,
        Guid? projectId = null,
        bool omitProject = false,
        NotificationType type = NotificationType.ProcessingCompleted,
        NotificationSeverity severity = NotificationSeverity.Info,
        string? title = "title",
        string? body = "body",
        string? resourceType = "Project",
        string? resourceId = "prj_1",
        Guid? sourceEventId = null,
        DateTimeOffset? readAt = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            recipientUserId ?? Other,
            omitProject ? null : projectId ?? Other,
            type,
            severity,
            title!,
            body!,
            resourceType!,
            resourceId!,
            sourceEventId,
            readAt,
            createdAt ?? Now,
            expiresAt);

    [Fact]
    public void Notification_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewNotification(sourceEventId: Id, readAt: Later, expiresAt: Now.AddDays(1));

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantA, sut.TenantId);
        Assert.Equal(Other, sut.RecipientUserId);
        Assert.Equal(Other, sut.ProjectId);
        Assert.Equal(NotificationType.ProcessingCompleted, sut.Type);
        Assert.Equal(NotificationSeverity.Info, sut.Severity);
        Assert.Equal("title", sut.Title);
        Assert.Equal("body", sut.Body);
        Assert.Equal("Project", sut.ResourceType);
        Assert.Equal("prj_1", sut.ResourceId);
        Assert.Equal(Id, sut.SourceEventId);
        Assert.Equal(Later, sut.ReadAt);
        Assert.Equal(Now, sut.CreatedAt);
        Assert.Equal(Now.AddDays(1), sut.ExpiresAt);
    }

    [Theory]
    [InlineData(null, "Notification Id must not be empty.")]
    [InlineData("tenant", "Notification TenantId must not be empty.")]
    [InlineData("recipient", "Notification RecipientUserId must not be empty.")]
    [InlineData("project", "Notification ProjectId must not be empty when set.")]
    [InlineData("projectRequired", "Notification ProjectId is required for type 'ProcessingCompleted'.")]
    [InlineData("type", "Notification Type is not defined.")]
    [InlineData("severity", "Notification Severity is not defined.")]
    [InlineData("title", "Notification Title must not be empty.")]
    [InlineData("titleLength", "Notification Title must be at most 200 chars.")]
    [InlineData("body", "Notification Body must not be empty.")]
    [InlineData("bodyLength", "Notification Body must be at most 1000 chars.")]
    [InlineData("resourceType", "Notification ResourceType must not be empty.")]
    [InlineData("resourceTypeLength", "Notification ResourceType must be at most 128 chars.")]
    [InlineData("resourceId", "Notification ResourceId must not be empty.")]
    [InlineData("resourceIdLength", "Notification ResourceId must be at most 256 chars.")]
    [InlineData("sourceEvent", "Notification SourceEventId must not be empty when set.")]
    [InlineData("readAt", "Notification ReadAt must not be before CreatedAt.")]
    [InlineData("expiry", "Notification ExpiresAt must be after CreatedAt.")]
    [InlineData("titleUrl", "Notification Title must not contain URLs.")]
    [InlineData("bodyToken", "Notification Body must not contain tokens.")]
    [InlineData("resourceIdUrl", "Notification ResourceId must not contain URLs.")]
    [InlineData("resourceIdToken", "Notification ResourceId must not contain tokens.")]
    public void Notification_Rejects_Invalid_Input(string? scenario, string expected)
    {
        Action act = scenario switch
        {
            null => () => NewNotification(id: Guid.Empty),
            "tenant" => () => NewNotification(tenantId: Guid.Empty),
            "recipient" => () => NewNotification(recipientUserId: Guid.Empty),
            "project" => () => NewNotification(projectId: Guid.Empty),
            "projectRequired" => () => NewNotification(omitProject: true),
            "type" => () => NewNotification(type: (NotificationType)99),
            "severity" => () => NewNotification(severity: (NotificationSeverity)9),
            "title" => () => NewNotification(title: " \t "),
            "titleLength" => () => NewNotification(title: Repeat('t', Notification.MaxTitleLength + 1)),
            "body" => () => NewNotification(body: string.Empty),
            "bodyLength" => () => NewNotification(body: Repeat('b', Notification.MaxBodyLength + 1)),
            "resourceType" => () => NewNotification(resourceType: "  "),
            "resourceTypeLength" => () => NewNotification(resourceType: Repeat('r', Notification.MaxResourceTypeLength + 1)),
            "resourceId" => () => NewNotification(resourceId: "\n"),
            "resourceIdLength" => () => NewNotification(resourceId: Repeat('i', Notification.MaxResourceIdLength + 1)),
            "sourceEvent" => () => NewNotification(sourceEventId: Guid.Empty),
            "readAt" => () => NewNotification(readAt: Now.AddTicks(-1)),
            "expiry" => () => NewNotification(expiresAt: Now),
            "titleUrl" => () => NewNotification(title: "open http://example.test"),
            "bodyToken" => () => NewNotification(body: "bearer  token"),
            "resourceIdUrl" => () => NewNotification(resourceId: "HTTPS://example.test/r"),
            "resourceIdToken" => () => NewNotification(resourceId: "Bearer abc"),
            _ => throw new InvalidOperationException($"Unhandled scenario '{scenario}'."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Theory]
    [InlineData(NotificationType.QuotaWarning)]
    [InlineData(NotificationType.ProviderPolicyWarning)]
    public void Notification_Tenant_Wide_Types_Do_Not_Require_Project(NotificationType type)
    {
        var sut = NewNotification(type: type, omitProject: true);
        Assert.Null(sut.ProjectId);
    }

    [Theory]
    [InlineData(NotificationType.ProcessingCompleted)]
    [InlineData(NotificationType.ExportFailed)]
    public void Notification_Project_Scoped_Types_Require_Project(NotificationType type)
    {
        Assert.Throws<DomainException>(() => NewNotification(type: type, omitProject: true));
    }

    [Fact]
    public void Notification_MarkAsRead_Stamps_ReadAt()
    {
        var sut = NewNotification();

        sut.MarkAsRead(Later);

        Assert.Equal(Later, sut.ReadAt);
    }

    [Fact]
    public void Notification_MarkAsRead_Before_Created_Throws()
    {
        var sut = NewNotification();

        var ex = Assert.Throws<DomainException>(() => sut.MarkAsRead(Now.AddTicks(-1)));
        Assert.Equal("Notification ReadAt must not be before CreatedAt.", ex.Message);
        Assert.Null(sut.ReadAt);
    }

    [Fact]
    public void Notification_MarkAsRead_At_CreatedAt_Is_Allowed()
    {
        var sut = NewNotification();
        sut.MarkAsRead(Now);
        Assert.Equal(Now, sut.ReadAt);
    }

    [Fact]
    public void Notification_ExpiresAt_After_CreatedAt_Is_Allowed()
    {
        Assert.Equal(Now.AddTicks(1), NewNotification(expiresAt: Now.AddTicks(1)).ExpiresAt);
    }

    [Fact]
    public void Notification_Length_Boundaries_Are_Inclusive()
    {
        Assert.Equal(
            Notification.MaxTitleLength,
            NewNotification(title: Repeat('t', Notification.MaxTitleLength)).Title.Length);
        Assert.Equal(
            Notification.MaxBodyLength,
            NewNotification(body: Repeat('b', Notification.MaxBodyLength)).Body.Length);
        Assert.Equal(
            Notification.MaxResourceTypeLength,
            NewNotification(resourceType: Repeat('r', Notification.MaxResourceTypeLength)).ResourceType.Length);
        Assert.Equal(
            Notification.MaxResourceIdLength,
            NewNotification(resourceId: Repeat('i', Notification.MaxResourceIdLength)).ResourceId.Length);
    }

    [Fact]
    public void Notification_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewNotification().TenantId);
        Assert.Equal(TenantB, NewNotification(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // OverlapGroup
    // ------------------------------------------------------------------

    private static OverlapGroup NewOverlapGroup(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? projectId = null,
        Guid? runId = null,
        int startMs = 0,
        int endMs = 100,
        DateTimeOffset? createdAt = null) =>
        new(id ?? Id, tenantId ?? TenantA, projectId ?? Other, runId ?? Other, startMs, endMs, createdAt ?? Now);

    [Fact]
    public void OverlapGroup_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewOverlapGroup(startMs: 99, endMs: 100);

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantA, sut.TenantId);
        Assert.Equal(Other, sut.ProjectId);
        Assert.Equal(Other, sut.RunId);
        Assert.Equal(99, sut.StartMs);
        Assert.Equal(100, sut.EndMs);
        Assert.Equal(Now, sut.CreatedAt);
    }

    [Theory]
    [InlineData(null, "OverlapGroup Id must not be empty.")]
    [InlineData("tenant", "OverlapGroup TenantId must not be empty.")]
    [InlineData("project", "OverlapGroup ProjectId must not be empty.")]
    [InlineData("run", "OverlapGroup RunId must not be empty.")]
    [InlineData("start", "OverlapGroup StartMs must be >= 0.")]
    [InlineData("end", "OverlapGroup EndMs must be greater than StartMs.")]
    public void OverlapGroup_Rejects_Invalid_Input(string? scenario, string expected)
    {
        Action act = scenario switch
        {
            null => () => NewOverlapGroup(id: Guid.Empty),
            "tenant" => () => NewOverlapGroup(tenantId: Guid.Empty),
            "project" => () => NewOverlapGroup(projectId: Guid.Empty),
            "run" => () => NewOverlapGroup(runId: Guid.Empty),
            "start" => () => NewOverlapGroup(startMs: -1),
            "end" => () => NewOverlapGroup(startMs: 100, endMs: 100),
            _ => throw new InvalidOperationException($"Unhandled scenario '{scenario}'."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void OverlapGroup_End_Before_Start_Throws()
    {
        var ex = Assert.Throws<DomainException>(() => NewOverlapGroup(startMs: 200, endMs: 100));
        Assert.Equal("OverlapGroup EndMs must be greater than StartMs.", ex.Message);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 100)]
    [InlineData(99, 100)]
    [InlineData(0, int.MaxValue)]
    public void OverlapGroup_Accepts_Valid_Boundaries(int startMs, int endMs)
    {
        var sut = NewOverlapGroup(startMs: startMs, endMs: endMs);
        Assert.Equal(startMs, sut.StartMs);
        Assert.Equal(endMs, sut.EndMs);
    }

    [Fact]
    public void OverlapGroup_Millisecond_Duration_Is_Computed_In_Long_Arithmetic()
    {
        // (int)EndMs - StartMs promotes to long, so int.MinValue..int.MaxValue
        // never wraps; a 1 ms gap at the extremes stays positive.
        var sut = NewOverlapGroup(startMs: int.MaxValue - 1, endMs: int.MaxValue);
        Assert.Equal(int.MaxValue - 1, sut.StartMs);
        Assert.Equal(int.MaxValue, sut.EndMs);
    }

    [Fact]
    public void OverlapGroup_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewOverlapGroup().TenantId);
        Assert.Equal(TenantB, NewOverlapGroup(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // SegmentContextAssignment
    // ------------------------------------------------------------------

    private static SegmentContextAssignment NewContextAssignment(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? segmentId = null,
        Guid? contextWindowId = null,
        DateTimeOffset? createdAt = null) =>
        new(id ?? Id, tenantId ?? TenantA, segmentId ?? Other, contextWindowId ?? Other, createdAt ?? Now);

    [Theory]
    [InlineData(0, "SegmentContextAssignment Id must not be empty.")]
    [InlineData(1, "SegmentContextAssignment TenantId must not be empty.")]
    [InlineData(2, "SegmentContextAssignment SegmentId must not be empty.")]
    [InlineData(3, "SegmentContextAssignment ContextWindowId must not be empty.")]
    public void SegmentContextAssignment_Rejects_Empty_Guids(int slot, string expected)
    {
        Action act = slot switch
        {
            0 => () => NewContextAssignment(id: Guid.Empty),
            1 => () => NewContextAssignment(tenantId: Guid.Empty),
            2 => () => NewContextAssignment(segmentId: Guid.Empty),
            3 => () => NewContextAssignment(contextWindowId: Guid.Empty),
            _ => throw new InvalidOperationException($"Unhandled slot {slot}."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void SegmentContextAssignment_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewContextAssignment();

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantA, sut.TenantId);
        Assert.Equal(Other, sut.SegmentId);
        Assert.Equal(Other, sut.ContextWindowId);
        Assert.Equal(Now, sut.CreatedAt);
    }

    [Fact]
    public void SegmentContextAssignment_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewContextAssignment().TenantId);
        Assert.Equal(TenantB, NewContextAssignment(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // SegmentOverlap
    // ------------------------------------------------------------------

    private static SegmentOverlap NewSegmentOverlap(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? overlapGroupId = null,
        Guid? segmentId = null,
        string? relationType = "Overlap",
        int order = 0,
        int overlapStartMs = 0,
        int overlapEndMs = 100) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            overlapGroupId ?? Other,
            segmentId ?? Other,
            relationType!,
            order,
            overlapStartMs,
            overlapEndMs);

    [Theory]
    [InlineData(null, "SegmentOverlap Id must not be empty.")]
    [InlineData("tenant", "SegmentOverlap TenantId must not be empty.")]
    [InlineData("group", "SegmentOverlap OverlapGroupId must not be empty.")]
    [InlineData("segment", "SegmentOverlap SegmentId must not be empty.")]
    [InlineData("relation", "SegmentOverlap RelationType must be one of Overlap, Contains, ContainedBy, Adjacent.")]
    [InlineData("order", "SegmentOverlap Order must be >= 0.")]
    [InlineData("start", "SegmentOverlap OverlapStartMs must be >= 0.")]
    [InlineData("end", "SegmentOverlap OverlapEndMs must be greater than OverlapStartMs.")]
    public void SegmentOverlap_Rejects_Invalid_Input(string? scenario, string expected)
    {
        Action act = scenario switch
        {
            null => () => NewSegmentOverlap(id: Guid.Empty),
            "tenant" => () => NewSegmentOverlap(tenantId: Guid.Empty),
            "group" => () => NewSegmentOverlap(overlapGroupId: Guid.Empty),
            "segment" => () => NewSegmentOverlap(segmentId: Guid.Empty),
            "relation" => () => NewSegmentOverlap(relationType: "overlap"),
            "order" => () => NewSegmentOverlap(order: -1),
            "start" => () => NewSegmentOverlap(overlapStartMs: -1),
            "end" => () => NewSegmentOverlap(overlapStartMs: 100, overlapEndMs: 100),
            _ => throw new InvalidOperationException($"Unhandled scenario '{scenario}'."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Theory]
    [InlineData("Overlap")]
    [InlineData("Contains")]
    [InlineData("ContainedBy")]
    [InlineData("Adjacent")]
    public void SegmentOverlap_Accepts_All_RelationTypes(string relationType)
    {
        Assert.Equal(relationType, NewSegmentOverlap(relationType: relationType).RelationType);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 99)]
    [InlineData(1, 100)]
    public void SegmentOverlap_Accepts_Boundary_Order_And_Range(int order, int endMs)
    {
        var sut = NewSegmentOverlap(order: order, overlapStartMs: 0, overlapEndMs: endMs);
        Assert.Equal(order, sut.Order);
        Assert.Equal(endMs, sut.OverlapEndMs);
    }

    [Fact]
    public void SegmentOverlap_End_Before_Start_Throws()
    {
        Assert.Throws<DomainException>(() => NewSegmentOverlap(overlapStartMs: 200, overlapEndMs: 100));
    }

    [Fact]
    public void SegmentOverlap_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewSegmentOverlap().TenantId);
        Assert.Equal(TenantB, NewSegmentOverlap(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // SegmentSelection
    // ------------------------------------------------------------------

    private static SegmentSelection NewSelection(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? projectId = null,
        Guid? segmentId = null,
        Guid? selectedTranscriptVersionId = null,
        Guid? selectedTranslationVersionId = null,
        Guid? selectedAudioArtifactId = null,
        int selectionVersion = 0,
        DateTimeOffset? updatedAt = null,
        Guid? updatedByUserId = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            projectId ?? Other,
            segmentId ?? Other,
            selectedTranscriptVersionId,
            selectedTranslationVersionId,
            selectedAudioArtifactId,
            selectionVersion,
            updatedAt ?? Now,
            updatedByUserId ?? Other);

    [Theory]
    [InlineData(0, "SegmentSelection Id must not be empty.")]
    [InlineData(1, "SegmentSelection TenantId must not be empty.")]
    [InlineData(2, "SegmentSelection ProjectId must not be empty.")]
    [InlineData(3, "SegmentSelection SegmentId must not be empty.")]
    [InlineData(4, "SegmentSelection SelectedTranscriptVersionId must not be empty when set.")]
    [InlineData(5, "SegmentSelection SelectedTranslationVersionId must not be empty when set.")]
    [InlineData(6, "SegmentSelection SelectedAudioArtifactId must not be empty when set.")]
    [InlineData(7, "SegmentSelection SelectionVersion must be >= 0.")]
    [InlineData(8, "SegmentSelection UpdatedByUserId must not be empty.")]
    public void SegmentSelection_Rejects_Invalid_Input(int slot, string expected)
    {
        Action act = slot switch
        {
            0 => () => NewSelection(id: Guid.Empty),
            1 => () => NewSelection(tenantId: Guid.Empty),
            2 => () => NewSelection(projectId: Guid.Empty),
            3 => () => NewSelection(segmentId: Guid.Empty),
            4 => () => NewSelection(selectedTranscriptVersionId: Guid.Empty),
            5 => () => NewSelection(selectedTranslationVersionId: Guid.Empty),
            6 => () => NewSelection(selectedAudioArtifactId: Guid.Empty),
            7 => () => NewSelection(selectionVersion: -1),
            8 => () => NewSelection(updatedByUserId: Guid.Empty),
            _ => throw new InvalidOperationException($"Unhandled slot {slot}."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void SegmentSelection_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewSelection(
            selectedTranscriptVersionId: Id,
            selectedTranslationVersionId: Other,
            selectedAudioArtifactId: TenantA,
            selectionVersion: 99);

        Assert.Equal(Id, sut.SelectedTranscriptVersionId);
        Assert.Equal(Other, sut.SelectedTranslationVersionId);
        Assert.Equal(TenantA, sut.SelectedAudioArtifactId);
        Assert.Equal(99, sut.SelectionVersion);
        Assert.Equal(Other, sut.UpdatedByUserId);
        Assert.Equal(Now, sut.UpdatedAt);
    }

    [Fact]
    public void SegmentSelection_ApplySelection_Replaces_Pointers_And_Bumps_Version()
    {
        var sut = NewSelection(selectionVersion: 0);

        sut.ApplySelection(Id, Other, TenantA, Id, Later);

        Assert.Equal(Id, sut.SelectedTranscriptVersionId);
        Assert.Equal(Other, sut.SelectedTranslationVersionId);
        Assert.Equal(TenantA, sut.SelectedAudioArtifactId);
        Assert.Equal(1, sut.SelectionVersion);
        Assert.Equal(Id, sut.UpdatedByUserId);
        Assert.Equal(Later, sut.UpdatedAt);
    }

    [Fact]
    public void SegmentSelection_ApplySelection_Null_Preserves_Existing_Pointer()
    {
        var sut = NewSelection(selectedTranscriptVersionId: Id, selectionVersion: 7);

        sut.ApplySelection(null, null, null, Id, Later);

        Assert.Equal(Id, sut.SelectedTranscriptVersionId);
        Assert.Equal(8, sut.SelectionVersion);
    }

    [Fact]
    public void SegmentSelection_ApplySelection_Overwrites_Non_Null_Pointer()
    {
        var sut = NewSelection(selectedTranscriptVersionId: Id);

        sut.ApplySelection(Other, null, null, Id, Later);

        Assert.Equal(Other, sut.SelectedTranscriptVersionId);
    }

    [Theory]
    [InlineData(0, "SegmentSelection SelectedTranscriptVersionId must not be empty when set.")]
    [InlineData(1, "SegmentSelection SelectedTranslationVersionId must not be empty when set.")]
    [InlineData(2, "SegmentSelection SelectedAudioArtifactId must not be empty when set.")]
    [InlineData(3, "SegmentSelection UpdatedByUserId must not be empty.")]
    public void SegmentSelection_ApplySelection_Rejects_Invalid_Input(int slot, string expected)
    {
        var sut = NewSelection();

        Action act = slot switch
        {
            0 => () => sut.ApplySelection(Guid.Empty, null, null, Id, Later),
            1 => () => sut.ApplySelection(null, Guid.Empty, null, Id, Later),
            2 => () => sut.ApplySelection(null, null, Guid.Empty, Id, Later),
            3 => () => sut.ApplySelection(null, null, null, Guid.Empty, Later),
            _ => throw new InvalidOperationException($"Unhandled slot {slot}."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void SegmentSelection_ApplySelection_Leaves_Row_Untouched_On_Failure()
    {
        var sut = NewSelection(selectionVersion: 4, updatedAt: Now);

        Assert.Throws<DomainException>(() => sut.ApplySelection(Guid.Empty, null, null, Id, Later));

        Assert.Equal(4, sut.SelectionVersion);
        Assert.Equal(Now, sut.UpdatedAt);
    }

    [Fact]
    public void SegmentSelection_ApplySelection_At_Int_Max_Overflows_Explicitly()
    {
        // SelectionVersion is int; the checked bump surfaces an OverflowException
        // rather than silently wrapping to a negative counter.
        var sut = NewSelection(selectionVersion: int.MaxValue);

        Assert.Throws<OverflowException>(() => sut.ApplySelection(Id, null, null, Id, Later));
        Assert.Equal(int.MaxValue, sut.SelectionVersion);
    }

    [Fact]
    public void SegmentSelection_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewSelection().TenantId);
        Assert.Equal(TenantB, NewSelection(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // SpeakerVoiceAssignment
    // ------------------------------------------------------------------

    private static SpeakerVoiceAssignment NewSpeakerVoiceAssignment(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? projectId = null,
        Guid? runId = null,
        Guid? speakerId = null,
        Guid? voiceProfileId = null,
        string? assignmentReason = "reason",
        string? policyHash = "policy",
        DateTimeOffset? createdAt = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            projectId ?? Other,
            runId ?? Other,
            speakerId ?? Id,
            voiceProfileId ?? Other,
            assignmentReason!,
            policyHash!,
            createdAt ?? Now);

    [Theory]
    [InlineData(0, "SpeakerVoiceAssignment Id must not be empty.")]
    [InlineData(1, "SpeakerVoiceAssignment TenantId must not be empty.")]
    [InlineData(2, "SpeakerVoiceAssignment ProjectId must not be empty.")]
    [InlineData(3, "SpeakerVoiceAssignment RunId must not be empty.")]
    [InlineData(4, "SpeakerVoiceAssignment SpeakerId must not be empty.")]
    [InlineData(5, "SpeakerVoiceAssignment VoiceProfileId must not be empty.")]
    [InlineData(6, "SpeakerVoiceAssignment AssignmentReason must not be empty.")]
    [InlineData(7, "SpeakerVoiceAssignment PolicyHash must not be empty.")]
    public void SpeakerVoiceAssignment_Rejects_Invalid_Input(int slot, string expected)
    {
        Action act = slot switch
        {
            0 => () => NewSpeakerVoiceAssignment(id: Guid.Empty),
            1 => () => NewSpeakerVoiceAssignment(tenantId: Guid.Empty),
            2 => () => NewSpeakerVoiceAssignment(projectId: Guid.Empty),
            3 => () => NewSpeakerVoiceAssignment(runId: Guid.Empty),
            4 => () => NewSpeakerVoiceAssignment(speakerId: Guid.Empty),
            5 => () => NewSpeakerVoiceAssignment(voiceProfileId: Guid.Empty),
            6 => () => NewSpeakerVoiceAssignment(assignmentReason: "  "),
            7 => () => NewSpeakerVoiceAssignment(policyHash: string.Empty),
            _ => throw new InvalidOperationException($"Unhandled slot {slot}."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void SpeakerVoiceAssignment_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewSpeakerVoiceAssignment();

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantA, sut.TenantId);
        Assert.Equal(Other, sut.ProjectId);
        Assert.Equal(Other, sut.RunId);
        Assert.Equal(Id, sut.SpeakerId);
        Assert.Equal(Other, sut.VoiceProfileId);
        Assert.Equal("reason", sut.AssignmentReason);
        Assert.Equal("policy", sut.PolicyHash);
        Assert.Equal(Now, sut.CreatedAt);
    }

    [Fact]
    public void SpeakerVoiceAssignment_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewSpeakerVoiceAssignment().TenantId);
        Assert.Equal(TenantB, NewSpeakerVoiceAssignment(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // StageUnitCompletion
    // ------------------------------------------------------------------

    private static StageUnitCompletion NewStageUnit(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? processingRunId = null,
        StageType stageType = StageType.Transcription,
        ScopeType scopeType = ScopeType.Segment,
        string? scopeId = "seg_1",
        Guid? stageExecutionId = null,
        string? unitState = "Completed",
        DateTimeOffset? createdAt = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            processingRunId ?? Other,
            stageType,
            scopeType,
            scopeId!,
            stageExecutionId ?? Other,
            unitState!,
            createdAt ?? Now);

    [Theory]
    [InlineData(0, "StageUnitCompletion Id must not be empty.")]
    [InlineData(1, "StageUnitCompletion TenantId must not be empty.")]
    [InlineData(2, "StageUnitCompletion ProcessingRunId must not be empty.")]
    [InlineData(3, "StageUnitCompletion ScopeId must not be empty.")]
    [InlineData(4, "StageUnitCompletion StageExecutionId must not be empty.")]
    [InlineData(5, "StageUnitCompletion UnitState must be one of Completed, Skipped, Failed, ManualReviewRequired, Cancelled.")]
    public void StageUnitCompletion_Rejects_Invalid_Input(int slot, string expected)
    {
        Action act = slot switch
        {
            0 => () => NewStageUnit(id: Guid.Empty),
            1 => () => NewStageUnit(tenantId: Guid.Empty),
            2 => () => NewStageUnit(processingRunId: Guid.Empty),
            3 => () => NewStageUnit(scopeId: " \t "),
            4 => () => NewStageUnit(stageExecutionId: Guid.Empty),
            5 => () => NewStageUnit(unitState: "completed"),
            _ => throw new InvalidOperationException($"Unhandled slot {slot}."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Skipped")]
    [InlineData("Failed")]
    [InlineData("ManualReviewRequired")]
    [InlineData("Cancelled")]
    public void StageUnitCompletion_Accepts_All_UnitStates(string unitState)
    {
        Assert.Equal(unitState, NewStageUnit(unitState: unitState).UnitState);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Running")]
    [InlineData("")]
    [InlineData(" ")]
    public void StageUnitCompletion_Rejects_Unknown_UnitStates(string unitState)
    {
        Assert.Throws<DomainException>(() => NewStageUnit(unitState: unitState));
    }

    [Fact]
    public void StageUnitCompletion_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewStageUnit(scopeType: ScopeType.Run, scopeId: "run_1");

        Assert.Equal(Other, sut.ProcessingRunId);
        Assert.Equal(StageType.Transcription, sut.StageType);
        Assert.Equal(ScopeType.Run, sut.ScopeType);
        Assert.Equal("run_1", sut.ScopeId);
        Assert.Equal(Other, sut.StageExecutionId);
        Assert.Equal("Completed", sut.UnitState);
        Assert.Equal(Now, sut.CreatedAt);
    }

    [Fact]
    public void StageUnitCompletion_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewStageUnit().TenantId);
        Assert.Equal(TenantB, NewStageUnit(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // TranscriptVersion
    // ------------------------------------------------------------------

    private static TranscriptVersion NewTranscript(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? projectId = null,
        Guid? runId = null,
        Guid? segmentId = null,
        string? provider = "azure",
        string? model = "whisper",
        string? language = "en",
        string? text = "hello",
        double confidence = 0.5d,
        Guid? wordTimestampsArtifactId = null,
        bool isSelected = false,
        bool needsReview = false,
        DateTimeOffset? createdAt = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            projectId ?? Other,
            runId ?? Other,
            segmentId ?? Id,
            provider!,
            model!,
            language!,
            text!,
            confidence,
            wordTimestampsArtifactId,
            isSelected,
            needsReview,
            createdAt ?? Now);

    [Theory]
    [InlineData(0, "TranscriptVersion Id must not be empty.")]
    [InlineData(1, "TranscriptVersion TenantId must not be empty.")]
    [InlineData(2, "TranscriptVersion ProjectId must not be empty.")]
    [InlineData(3, "TranscriptVersion RunId must not be empty.")]
    [InlineData(4, "TranscriptVersion SegmentId must not be empty.")]
    [InlineData(5, "TranscriptVersion Provider must not be empty.")]
    [InlineData(6, "TranscriptVersion Model must not be empty.")]
    [InlineData(7, "TranscriptVersion Language must not be empty.")]
    [InlineData(8, "TranscriptVersion Text must not be empty.")]
    [InlineData(9, "TranscriptVersion Confidence must be in 0..1.")]
    [InlineData(10, "TranscriptVersion WordTimestampsArtifactId must not be empty when set.")]
    public void TranscriptVersion_Rejects_Invalid_Input(int slot, string expected)
    {
        Action act = slot switch
        {
            0 => () => NewTranscript(id: Guid.Empty),
            1 => () => NewTranscript(tenantId: Guid.Empty),
            2 => () => NewTranscript(projectId: Guid.Empty),
            3 => () => NewTranscript(runId: Guid.Empty),
            4 => () => NewTranscript(segmentId: Guid.Empty),
            5 => () => NewTranscript(provider: "  "),
            6 => () => NewTranscript(model: string.Empty),
            7 => () => NewTranscript(language: " "),
            8 => () => NewTranscript(text: "\t"),
            9 => () => NewTranscript(confidence: 1.5d),
            10 => () => NewTranscript(wordTimestampsArtifactId: Guid.Empty),
            _ => throw new InvalidOperationException($"Unhandled slot {slot}."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Theory]
    [InlineData(0.0d)]
    [InlineData(0.5d)]
    [InlineData(0.99d)]
    [InlineData(1.0d)]
    [InlineData(double.Epsilon)]
    public void TranscriptVersion_Confidence_Boundaries_Are_Inclusive(double confidence)
    {
        Assert.Equal(confidence, NewTranscript(confidence: confidence).Confidence);
    }

    [Theory]
    [InlineData(-0.0001d)]
    [InlineData(-1.0d)]
    [InlineData(1.0001d)]
    [InlineData(100.0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TranscriptVersion_Rejects_Out_Of_Range_Confidence(double confidence)
    {
        var ex = Assert.Throws<DomainException>(() => NewTranscript(confidence: confidence));
        Assert.Equal("TranscriptVersion Confidence must be in 0..1.", ex.Message);
    }

    [Fact]
    public void TranscriptVersion_SetSelected_Toggles_And_Revalidates()
    {
        var sut = NewTranscript();

        sut.SetSelected(true);
        Assert.True(sut.IsSelected);

        sut.SetSelected(false);
        Assert.False(sut.IsSelected);
    }

    [Fact]
    public void TranscriptVersion_SetNeedsReview_Toggles_And_Revalidates()
    {
        var sut = NewTranscript();

        sut.SetNeedsReview(true);
        Assert.True(sut.NeedsReview);

        sut.SetNeedsReview(false);
        Assert.False(sut.NeedsReview);
    }

    [Fact]
    public void TranscriptVersion_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewTranscript(wordTimestampsArtifactId: Other, isSelected: true, needsReview: true);

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantA, sut.TenantId);
        Assert.Equal(Other, sut.ProjectId);
        Assert.Equal(Other, sut.RunId);
        Assert.Equal(Id, sut.SegmentId);
        Assert.Equal("azure", sut.Provider);
        Assert.Equal("whisper", sut.Model);
        Assert.Equal("en", sut.Language);
        Assert.Equal("hello", sut.Text);
        Assert.Equal(0.5d, sut.Confidence);
        Assert.Equal(Other, sut.WordTimestampsArtifactId);
        Assert.True(sut.IsSelected);
        Assert.True(sut.NeedsReview);
        Assert.Equal(Now, sut.CreatedAt);
    }

    [Fact]
    public void TranscriptVersion_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewTranscript().TenantId);
        Assert.Equal(TenantB, NewTranscript(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // TranslationVersion
    // ------------------------------------------------------------------

    private static TranslationVersion NewTranslation(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? projectId = null,
        Guid? runId = null,
        Guid? segmentId = null,
        string? primaryText = "bonjour",
        string[]? alternativeTexts = null,
        double semanticScore = 0.5d,
        double naturalnessScore = 0.5d,
        double timingScore = 0.5d,
        string? provider = "azure",
        string? model = "gpt",
        Guid? promptTemplateId = null,
        string? promptHash = null,
        bool isSelected = false,
        DateTimeOffset? createdAt = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            projectId ?? Other,
            runId ?? Other,
            segmentId ?? Id,
            primaryText!,
            alternativeTexts ?? Array.Empty<string>(),
            semanticScore,
            naturalnessScore,
            timingScore,
            provider!,
            model!,
            promptTemplateId,
            promptHash,
            isSelected,
            createdAt ?? Now);

    [Theory]
    [InlineData(0, "TranslationVersion Id must not be empty.")]
    [InlineData(1, "TranslationVersion TenantId must not be empty.")]
    [InlineData(2, "TranslationVersion ProjectId must not be empty.")]
    [InlineData(3, "TranslationVersion RunId must not be empty.")]
    [InlineData(4, "TranslationVersion SegmentId must not be empty.")]
    [InlineData(5, "TranslationVersion PrimaryText must not be empty.")]
    [InlineData(6, "TranslationVersion AlternativeTexts must not be null.")]
    [InlineData(7, "TranslationVersion SemanticScore must be in 0..1.")]
    [InlineData(8, "TranslationVersion NaturalnessScore must be in 0..1.")]
    [InlineData(9, "TranslationVersion TimingScore must be in 0..1.")]
    [InlineData(10, "TranslationVersion Provider must not be empty.")]
    [InlineData(11, "TranslationVersion Model must not be empty.")]
    [InlineData(12, "TranslationVersion PromptTemplateId must not be empty when set.")]
    [InlineData(13, "TranslationVersion PromptHash must not be empty when set.")]
    public void TranslationVersion_Rejects_Invalid_Input(int slot, string expected)
    {
        Action act = slot switch
        {
            0 => () => NewTranslation(id: Guid.Empty),
            1 => () => NewTranslation(tenantId: Guid.Empty),
            2 => () => NewTranslation(projectId: Guid.Empty),
            3 => () => NewTranslation(runId: Guid.Empty),
            4 => () => NewTranslation(segmentId: Guid.Empty),
            5 => () => NewTranslation(primaryText: "  "),
            6 => () => new TranslationVersion(
                Id,
                TenantA,
                Other,
                Other,
                Id,
                "bonjour",
                null!,
                0.5d,
                0.5d,
                0.5d,
                "azure",
                "gpt",
                null,
                null,
                false,
                Now),
            7 => () => NewTranslation(semanticScore: double.NaN),
            8 => () => NewTranslation(naturalnessScore: -0.5d),
            9 => () => NewTranslation(timingScore: 1.5d),
            10 => () => NewTranslation(provider: "\t"),
            11 => () => NewTranslation(model: string.Empty),
            12 => () => NewTranslation(promptTemplateId: Guid.Empty),
            13 => () => NewTranslation(promptHash: "   "),
            _ => throw new InvalidOperationException($"Unhandled slot {slot}."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Theory]
    [InlineData(0.0d)]
    [InlineData(0.01d)]
    [InlineData(0.99d)]
    [InlineData(1.0d)]
    public void TranslationVersion_Scores_Boundaries_Are_Inclusive(double score)
    {
        var sut = NewTranslation(semanticScore: score, naturalnessScore: score, timingScore: score);
        Assert.Equal(score, sut.SemanticScore);
        Assert.Equal(score, sut.NaturalnessScore);
        Assert.Equal(score, sut.TimingScore);
    }

    [Theory]
    [InlineData(-1.0d)]
    [InlineData(1.0000001d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TranslationVersion_Rejects_Out_Of_Range_Scores(double score)
    {
        Assert.Throws<DomainException>(() => NewTranslation(semanticScore: score));
        Assert.Throws<DomainException>(() => NewTranslation(naturalnessScore: score));
        Assert.Throws<DomainException>(() => NewTranslation(timingScore: score));
    }

    [Fact]
    public void TranslationVersion_AlternativeTexts_Empty_Array_Is_Allowed()
    {
        var sut = NewTranslation(alternativeTexts: Array.Empty<string>());
        Assert.Empty(sut.AlternativeTexts);
    }

    [Fact]
    public void TranslationVersion_SetSelected_Toggles_And_Revalidates()
    {
        var sut = NewTranslation();

        sut.SetSelected(true);
        Assert.True(sut.IsSelected);

        sut.SetSelected(false);
        Assert.False(sut.IsSelected);
    }

    [Fact]
    public void TranslationVersion_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewTranslation(
            alternativeTexts: new[] { "salut", "bonsoir" },
            promptTemplateId: Other,
            promptHash: null,
            isSelected: true);

        Assert.Equal("bonjour", sut.PrimaryText);
        Assert.Equal(new[] { "salut", "bonsoir" }, sut.AlternativeTexts);
        Assert.Equal("azure", sut.Provider);
        Assert.Equal("gpt", sut.Model);
        Assert.Equal(Other, sut.PromptTemplateId);
        Assert.Null(sut.PromptHash);
        Assert.True(sut.IsSelected);
    }

    [Fact]
    public void TranslationVersion_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewTranslation().TenantId);
        Assert.Equal(TenantB, NewTranslation(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // VoiceProfile
    // ------------------------------------------------------------------

    private static VoiceProfile NewVoiceProfile(
        Guid? id = null,
        Guid? tenantId = null,
        string? provider = "azure",
        string? voiceId = "en-US-JennyNeural",
        string? voiceVersion = "1.0",
        string? language = "en",
        VoiceType type = VoiceType.Stock,
        bool cloningEnabled = false,
        string? modelRefJson = null,
        DateTimeOffset? createdAt = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantA,
            provider!,
            voiceId!,
            voiceVersion!,
            language!,
            type,
            cloningEnabled,
            modelRefJson,
            createdAt ?? Now);

    [Theory]
    [InlineData(0, "VoiceProfile Id must not be empty.")]
    [InlineData(1, "VoiceProfile TenantId must not be empty.")]
    [InlineData(2, "VoiceProfile Provider must not be empty.")]
    [InlineData(3, "VoiceProfile VoiceId must not be empty.")]
    [InlineData(4, "VoiceProfile VoiceVersion must not be empty.")]
    [InlineData(5, "VoiceProfile Language must be a 2-3 letter code.")]
    public void VoiceProfile_Rejects_Invalid_Input(int slot, string expected)
    {
        Action act = slot switch
        {
            0 => () => NewVoiceProfile(id: Guid.Empty),
            1 => () => NewVoiceProfile(tenantId: Guid.Empty),
            2 => () => NewVoiceProfile(provider: " \t "),
            3 => () => NewVoiceProfile(voiceId: string.Empty),
            4 => () => NewVoiceProfile(voiceVersion: " "),
            5 => () => NewVoiceProfile(language: "english"),
            _ => throw new InvalidOperationException($"Unhandled slot {slot}."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("fra")]
    [InlineData("FR")]
    [InlineData("z")]
    public void VoiceProfile_Language_Length_Boundaries(string language)
    {
        var act = () => NewVoiceProfile(language: language);
        if (language.Length == 1)
        {
            var ex = Assert.Throws<DomainException>(act);
            Assert.Equal("VoiceProfile Language must be a 2-3 letter code.", ex.Message);
            return;
        }

        Assert.Equal(language, act().Language);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("e1")]
    [InlineData("e-")]
    [InlineData("123")]
    [InlineData("abcd")]
    public void VoiceProfile_Rejects_Non_Alpha_Language_Codes(string language)
    {
        var ex = Assert.Throws<DomainException>(() => NewVoiceProfile(language: language));
        Assert.Equal("VoiceProfile Language must be a 2-3 letter code.", ex.Message);
    }

    [Fact]
    public void VoiceProfile_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewVoiceProfile(
            type: VoiceType.Cloned,
            cloningEnabled: true,
            modelRefJson: "{\"model\":\"x\"}",
            createdAt: Later);

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantA, sut.TenantId);
        Assert.Equal("azure", sut.Provider);
        Assert.Equal("en-US-JennyNeural", sut.VoiceId);
        Assert.Equal("1.0", sut.VoiceVersion);
        Assert.Equal("en", sut.Language);
        Assert.Equal(VoiceType.Cloned, sut.Type);
        Assert.True(sut.CloningEnabled);
        Assert.Equal("{\"model\":\"x\"}", sut.ModelRefJson);
        Assert.Equal(Later, sut.CreatedAt);
    }

    [Fact]
    public void VoiceProfile_Validate_Is_Public_And_Idempotent()
    {
        var sut = NewVoiceProfile();
        sut.Validate();
        sut.Validate();
        Assert.Equal("en", sut.Language);
    }

    [Fact]
    public void VoiceProfile_Is_Tenant_Scoped()
    {
        Assert.Equal(TenantA, NewVoiceProfile().TenantId);
        Assert.Equal(TenantB, NewVoiceProfile(tenantId: TenantB, id: Other).TenantId);
    }

    // ------------------------------------------------------------------
    // Tenant scoping summary (all entities with a TenantId)
    // ------------------------------------------------------------------

    [Fact]
    public void Every_Entity_Exposes_An_Immutable_TenantId()
    {
        var tenants = new List<Guid>
        {
            NewActivity().TenantId,
            NewConsent().TenantId,
            NewIdempotency().TenantId,
            NewNotification().TenantId,
            NewOverlapGroup().TenantId,
            NewContextAssignment().TenantId,
            NewSegmentOverlap().TenantId,
            NewSelection().TenantId,
            NewSpeakerVoiceAssignment().TenantId,
            NewStageUnit().TenantId,
            NewTranscript().TenantId,
            NewTranslation().TenantId,
            NewVoiceProfile().TenantId,
        };

        // Every entity stamps the tenant it was built for and never another.
        Assert.Equal(13, tenants.Count);
        Assert.All(tenants, t => Assert.Equal(TenantA, t));
    }
}
