// Task 039C: notifications unit gap closure.
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Notifications;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DubbingPlatform.UnitTests.Notifications;

/// <summary>
/// Notification dedup, recipient resolution, event mapping, and the
/// <see cref="Notification"/> entity guard/threshold matrix. Dedup runs through
/// the real projector over an in-process InMemory <see cref="AppDbContext"/> so
/// the (tenant, source event, recipient) collapse is asserted against stored
/// rows, not a mock. No containers, no network, no database server.
/// </summary>
public sealed class NotificationLogicTests
{
    // NotificationProjector stamps rows with the system clock and exposes no
    // TimeProvider seam, so the fixture anchor is captured once here. Every
    // assertion below is an explicit offset from this anchor (hours/days), never
    // a wall-clock comparison, and nothing sleeps.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task SameTenantTypeAndDedupKey_CollapsesToOneNotification()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, ownerUserId: recipient);
        var projector = new NotificationProjector(factory);

        var input = new NotificationInput(
            tenantId, projectId, NotificationType.ProcessingFailed, NotificationSeverity.Error,
            "Processing failed", "Run abcd1234 failed (PROVIDER_TIMEOUT).",
            "ProcessingRun", Guid.NewGuid().ToString("N"), sourceEventId, Now.AddDays(30));

        var first = await projector.ProjectAsync(input);
        var second = await projector.ProjectAsync(input);

        Assert.Single(first);
        Assert.Single(second);
        // Idempotent: the redelivery returns the already-stored row.
        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(1, CountAll(factory));
        Assert.Equal(sourceEventId, second[0].SourceEventId);
    }

    [Fact]
    public async Task DifferentTenants_NeverCollapseTogether()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var sharedSourceEventId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantA, projectA, recipient);
        SeedProject(factory, tenantB, projectB, recipient);
        var projector = new NotificationProjector(factory);

        var a = await projector.ProjectAsync(Input(tenantA, projectA, sharedSourceEventId));
        var b = await projector.ProjectAsync(Input(tenantB, projectB, sharedSourceEventId));

        Assert.Single(a);
        Assert.Single(b);
        Assert.NotEqual(a[0].Id, b[0].Id);
        Assert.Equal(tenantA, a[0].TenantId);
        Assert.Equal(tenantB, b[0].TenantId);
        // Same dedup key, one row per tenant: never shared across the boundary.
        Assert.Equal(2, CountAll(factory));
        Assert.Equal(1, CountAllInScope(factory, tenantA));
        Assert.Equal(1, CountAllInScope(factory, tenantB));
    }

    [Fact]
    public async Task DifferentTypes_ShareTheSameDedupKey_AndCollapseToOneRow()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, ownerUserId: recipient);
        var projector = new NotificationProjector(factory);

        var completed = await projector.ProjectAsync(
            new NotificationInput(tenantId, projectId, NotificationType.ProcessingCompleted, NotificationSeverity.Info,
                "Processing completed", "Run abcd1234 completed.", "ProcessingRun", "run-1", sourceEventId, Now.AddDays(30)));
        var failed = await projector.ProjectAsync(
            new NotificationInput(tenantId, projectId, NotificationType.ProcessingFailed, NotificationSeverity.Error,
                "Processing failed", "Run abcd1234 failed (E1).", "ProcessingRun", "run-1", sourceEventId, Now.AddDays(30)));

        // The stored dedup key is (tenant, source event, recipient): a redelivered
        // event never re-appends, so exactly one row survives.
        Assert.Single(completed);
        Assert.Single(failed);
        Assert.Equal(completed[0].Id, failed[0].Id);
        Assert.Equal(1, CountAll(factory));
    }

    [Fact]
    public async Task NullOrEmptyDedupKey_DisablesDedup()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, ownerUserId: recipient);
        var projector = new NotificationProjector(factory);

        var first = await projector.ProjectAsync(Input(tenantId, projectId, sourceEventId: null));
        var second = await projector.ProjectAsync(Input(tenantId, projectId, sourceEventId: null));

        Assert.Single(first);
        Assert.Single(second);
        Assert.NotEqual(first[0].Id, second[0].Id);
        Assert.Null(first[0].SourceEventId);
        // No dedup key => every delivery appends.
        Assert.Equal(2, CountAll(factory));
    }

    [Fact]
    public async Task DistinctRecipients_EachGetTheirOwnRow_ForTheSameEvent()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, ownerUserId: owner);
        SeedMembership(factory, tenantId, projectId, member, ProjectRole.Reviewer);
        var projector = new NotificationProjector(factory);

        var created = await projector.ProjectAsync(Input(tenantId, projectId, sourceEventId));

        Assert.Equal(2, created.Count);
        Assert.Equal(2, created.Select(n => n.RecipientUserId).Distinct().Count());
        Assert.All(created, n => Assert.Equal(sourceEventId, n.SourceEventId));

        // A redelivery collapses for both recipients without duplicating.
        var again = await projector.ProjectAsync(Input(tenantId, projectId, sourceEventId));
        Assert.Equal(2, again.Count);
        Assert.Equal(2, CountAll(factory));
    }

    [Fact]
    public async Task Recipients_ResolveToOwnerOnly_WhenNoMembershipExists()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, ownerUserId: owner);
        var projector = new NotificationProjector(factory);

        var created = await projector.ProjectAsync(Input(tenantId, projectId, Guid.NewGuid()));

        var notification = Assert.Single(created);
        Assert.Equal(owner, notification.RecipientUserId);
    }

    [Fact]
    public async Task Recipients_ResolveToProjectOwner_WhenMembershipIsAbsent()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, ownerUserId: owner);
        var projector = new NotificationProjector(factory);

        // No ProjectMembership rows at all: the owner remains the fallback.
        var created = await projector.ProjectAsync(Input(tenantId, projectId, Guid.NewGuid()));

        Assert.Equal(owner, Assert.Single(created).RecipientUserId);
    }

    [Fact]
    public async Task Recipients_SkipEmptyUserIds_AndDeduplicateOwnerOverlap()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, ownerUserId: owner);
        // The owner is also a member: the recipient set collapses to one row.
        SeedMembership(factory, tenantId, projectId, owner, ProjectRole.ProjectEditor);
        var projector = new NotificationProjector(factory);

        var created = await projector.ProjectAsync(Input(tenantId, projectId, Guid.NewGuid()));

        Assert.Equal(owner, Assert.Single(created).RecipientUserId);
    }

    [Fact]
    public async Task TenantLevelEvents_ResolveToActiveTenantUsers_Only()
    {
        var tenantId = Guid.NewGuid();
        var active = Guid.NewGuid();
        var disabled = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedUser(factory, tenantId, active, TenantUserStatus.Active);
        SeedUser(factory, tenantId, disabled, TenantUserStatus.Disabled);
        var projector = new NotificationProjector(factory);

        var created = await projector.ProjectAsync(new NotificationInput(
            tenantId, null, NotificationType.QuotaWarning, NotificationSeverity.Warning,
            "Quota warning", "Quota 'storage' is near its limit.", "Tenant", tenantId.ToString("N"), Guid.NewGuid(), Now.AddDays(30)));

        Assert.Equal(active, Assert.Single(created).RecipientUserId);
        Assert.DoesNotContain(created, n => n.RecipientUserId == disabled);
    }

    [Fact]
    public async Task NoRecipients_SkipsProjection_AndReturnsEmpty()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        // Disabled, so the tenant-level recipient query finds no active users.
        SeedUser(factory, tenantId, Guid.NewGuid(), TenantUserStatus.Disabled);
        var projector = new NotificationProjector(factory);

        // Ownerless, memberless project => no recipients => skipped (never a throw).
        var created = await projector.ProjectAsync(Input(tenantId, projectId, Guid.NewGuid()));
        Assert.Empty(created);
        Assert.Equal(0, CountAll(factory));

        // Tenant-level event with no active users is skipped too.
        var tenantLevel = await projector.ProjectAsync(new NotificationInput(
            tenantId, null, NotificationType.ProviderPolicyWarning, NotificationSeverity.Warning,
            "Provider policy warning", "Provider policy 'x' needs attention.", "Tenant", tenantId.ToString("N"), Guid.NewGuid(), Now.AddDays(30)));
        Assert.Empty(tenantLevel);
    }

    [Fact]
    public async Task ProjectAsync_SanitizesText_AndStripsUrlsAndTokens()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, ownerUserId: recipient);
        var projector = new NotificationProjector(factory);

        var created = await projector.ProjectAsync(new NotificationInput(
            tenantId, projectId, NotificationType.ProcessingFailed, NotificationSeverity.Error,
            "  Failed  ",
            "See https://example.test/x with bearer abc123def456 and token=shhh",
            "ProcessingRun", Guid.NewGuid().ToString("N"), Guid.NewGuid(), Now.AddDays(30)));

        var notification = Assert.Single(created);
        Assert.DoesNotContain("https://", notification.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123def456", notification.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("shhh", notification.Body, StringComparison.Ordinal);
        Assert.Contains("[redacted-url]", notification.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectAsync_RejectsEmptyTenant_AndUnsafeResourceFields()
    {
        using var factory = CreateFactory();
        var projector = new NotificationProjector(factory);

        await Assert.ThrowsAsync<DomainException>(() => projector.ProjectAsync(
            Input(Guid.Empty, Guid.NewGuid(), Guid.NewGuid())));

        await Assert.ThrowsAsync<DomainException>(() => projector.ProjectAsync(new NotificationInput(
            Guid.NewGuid(), Guid.NewGuid(), NotificationType.QuotaWarning, NotificationSeverity.Warning,
            "Quota warning", "body", "  ", "tenant-1", null, null)));

        await Assert.ThrowsAsync<DomainException>(() => projector.ProjectAsync(new NotificationInput(
            Guid.NewGuid(), Guid.NewGuid(), NotificationType.QuotaWarning, NotificationSeverity.Warning,
            "Quota warning", "body", "Tenant", "   ", null, null)));

        await Assert.ThrowsAsync<DomainException>(() => projector.ProjectAsync(new NotificationInput(
            Guid.NewGuid(), Guid.NewGuid(), NotificationType.QuotaWarning, NotificationSeverity.Warning,
            "Quota warning", "body", "Tenant", "https://example.test/x", null, null)));

        await Assert.ThrowsAsync<DomainException>(() => projector.ProjectAsync(new NotificationInput(
            Guid.NewGuid(), Guid.NewGuid(), NotificationType.QuotaWarning, NotificationSeverity.Warning,
            "Quota warning", "body", "Tenant", "bearer abc123", null, null)));

        await Assert.ThrowsAsync<ArgumentNullException>(() => projector.ProjectAsync(null!));
        Assert.Throws<ArgumentNullException>(() => new NotificationProjector(null!));
    }

    [Fact]
    public async Task ListActiveAsync_ExcludesExpired_AndFiltersUnread()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedNotification(factory, tenantId, userId, "a", readAt: null, createdAt: Now.AddHours(-2), expiresAt: Now.AddHours(2));
        SeedNotification(factory, tenantId, userId, "b", readAt: null, createdAt: Now.AddHours(-1), expiresAt: Now.AddHours(3));
        SeedNotification(factory, tenantId, userId, "c", readAt: Now.AddMinutes(-5), createdAt: Now.AddHours(-3), expiresAt: Now.AddHours(1));
        SeedNotification(factory, tenantId, userId, "d", readAt: null, createdAt: Now.AddHours(-5), expiresAt: Now.AddHours(-4));
        var projector = new NotificationProjector(factory);

        var (all, total) = await projector.ListActiveAsync(tenantId, userId, 1, 50);
        Assert.Equal(3, total);
        Assert.Equal(3, all.Count);
        Assert.DoesNotContain(all, n => n.ResourceId == "d");

        var (unread, unreadTotal) = await projector.ListActiveAsync(tenantId, userId, true, 1, 50);
        Assert.Equal(2, unreadTotal);
        Assert.Equal(["b", "a"], unread.Select(n => n.ResourceId).ToArray());

        Assert.Equal(2, await projector.CountUnreadAsync(tenantId, userId));
    }

    [Fact]
    public async Task ListActiveAsync_ClampsPaging_AndGuardsArguments()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var factory = CreateFactory();
        for (var index = 0; index < 5; index++)
        {
            SeedNotification(factory, tenantId, userId, $"n{index}", readAt: null, createdAt: Now.AddMinutes(-index), expiresAt: null);
        }

        var projector = new NotificationProjector(factory);

        // pageSize clamps to 100, page clamps to 1: never a negative skip.
        var (_, total) = await projector.ListActiveAsync(tenantId, userId, 0, 5000);
        Assert.Equal(5, total);

        var (first, _) = await projector.ListActiveAsync(tenantId, userId, 1, 2);
        Assert.Equal(2, first.Count);
        var (second, _) = await projector.ListActiveAsync(tenantId, userId, 2, 2);
        Assert.Equal(2, second.Count);
        Assert.Empty(first.Select(n => n.Id).Intersect(second.Select(n => n.Id)));

        await Assert.ThrowsAsync<DomainException>(() => projector.ListActiveAsync(Guid.Empty, userId, 1, 10));
        await Assert.ThrowsAsync<DomainException>(() => projector.ListActiveAsync(tenantId, Guid.Empty, 1, 10));
        await Assert.ThrowsAsync<DomainException>(() => projector.CountUnreadAsync(Guid.Empty, userId));
        await Assert.ThrowsAsync<DomainException>(() => projector.CountUnreadAsync(tenantId, Guid.Empty));
    }

    [Fact]
    public async Task MarkAllReadAsync_IsIdempotent_AndSkipsExpired()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedNotification(factory, tenantId, userId, "a", readAt: null, createdAt: Now.AddHours(-2), expiresAt: Now.AddHours(2));
        SeedNotification(factory, tenantId, userId, "b", readAt: null, createdAt: Now.AddHours(-1), expiresAt: null);
        SeedNotification(factory, tenantId, userId, "c", readAt: null, createdAt: Now.AddHours(-5), expiresAt: Now.AddHours(-4));
        var projector = new NotificationProjector(factory);

        Assert.Equal(2, await projector.MarkAllReadAsync(tenantId, userId));
        // Second call marks nothing: idempotent, never 409.
        Assert.Equal(0, await projector.MarkAllReadAsync(tenantId, userId));
        Assert.Equal(0, await projector.CountUnreadAsync(tenantId, userId));

        await Assert.ThrowsAsync<DomainException>(() => projector.MarkAllReadAsync(Guid.Empty, userId));
        await Assert.ThrowsAsync<DomainException>(() => projector.MarkAllReadAsync(tenantId, Guid.Empty));
    }

    [Fact]
    public async Task MarkAsReadAsync_IsIdempotent_AndLeaksNothing()
    {
        var tenantId = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        using var factory = CreateFactory();
        var id = SeedNotification(factory, tenantId, userId, "a", readAt: null, createdAt: Now.AddHours(-2), expiresAt: Now.AddHours(2));
        var expiredId = SeedNotification(factory, tenantId, userId, "b", readAt: null, createdAt: Now.AddHours(-5), expiresAt: Now.AddHours(-4));
        var foreignTenantId = SeedNotification(factory, otherTenant, userId, "c", readAt: null, createdAt: Now.AddHours(-2), expiresAt: Now.AddHours(2));
        var projector = new NotificationProjector(factory);

        await projector.MarkAsReadAsync(tenantId, userId, id);
        // Re-marking an already-read row is a no-op, never a throw.
        await projector.MarkAsReadAsync(tenantId, userId, id);
        // Only the live unread row counted; the expired row was never eligible.
        Assert.Equal(0, await projector.CountUnreadAsync(tenantId, userId));

        // Expired rows read as gone.
        await Assert.ThrowsAsync<NotFoundException>(
            () => projector.MarkAsReadAsync(tenantId, userId, expiredId));
        // A row owned by another recipient or tenant is not found (no leak).
        await Assert.ThrowsAsync<NotFoundException>(
            () => projector.MarkAsReadAsync(tenantId, strangerId, id));
        await Assert.ThrowsAsync<NotFoundException>(
            () => projector.MarkAsReadAsync(tenantId, userId, foreignTenantId));
        await Assert.ThrowsAsync<NotFoundException>(
            () => projector.MarkAsReadAsync(tenantId, userId, Guid.NewGuid()));

        await Assert.ThrowsAsync<DomainException>(() => projector.MarkAsReadAsync(Guid.Empty, userId, id));
        await Assert.ThrowsAsync<DomainException>(() => projector.MarkAsReadAsync(tenantId, Guid.Empty, id));
        await Assert.ThrowsAsync<DomainException>(() => projector.MarkAsReadAsync(tenantId, userId, Guid.Empty));
    }

    [Fact]
    public void EventMapper_MapsEveryTypeSeverityAndResource()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var createdAt = Now;
        var run = runId.ToString("N");

        var completed = NotificationEventMapper.FromRunCompleted(new RunCompleted(
            messageId, "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, createdAt, 1, null, null, null, "out-1"));
        Assert.Equal(NotificationType.ProcessingCompleted, completed.Type);
        Assert.Equal(NotificationSeverity.Info, completed.Severity);
        Assert.Equal("ProcessingRun", completed.ResourceType);
        Assert.Equal(run, completed.ResourceId);
        Assert.Equal(messageId, completed.SourceEventId);
        Assert.Equal("Processing completed", completed.Title);
        Assert.Contains(runId.ToString("N").Substring(0, 8), completed.Body, StringComparison.Ordinal);

        var failed = NotificationEventMapper.FromRunFailed(new RunFailed(
            messageId, "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, createdAt, 1, null, null, null, "PROVIDER_TIMEOUT", "may embed a path"));
        Assert.Equal(NotificationType.ProcessingFailed, failed.Type);
        Assert.Equal(NotificationSeverity.Error, failed.Severity);
        Assert.Contains("PROVIDER_TIMEOUT", failed.Body, StringComparison.Ordinal);
        // Error messages are excluded: they may embed storage paths.
        Assert.DoesNotContain("may embed a path", failed.Body, StringComparison.Ordinal);

        var blankCode = NotificationEventMapper.FromRunFailed(new RunFailed(
            messageId, "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, createdAt, 1, null, null, null, "   ", "msg"));
        Assert.Contains("UNKNOWN", blankCode.Body, StringComparison.Ordinal);

        var review = NotificationEventMapper.FromReviewRequired(new StageReviewRequired(
            messageId, "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, createdAt, 1, null, null, null, "Translation", "rev-1"));
        Assert.Equal(NotificationType.ManualReviewRequired, review.Type);
        Assert.Equal(NotificationSeverity.Warning, review.Severity);
        Assert.Equal("ReviewItem", review.ResourceType);
        Assert.Equal("rev-1", review.ResourceId);

        var blankStage = NotificationEventMapper.FromReviewRequired(new StageReviewRequired(
            messageId, "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, createdAt, 1, null, null, null, "  ", "rev-1"));
        Assert.Contains("a stage", blankStage.Body, StringComparison.Ordinal);

        var resolved = NotificationEventMapper.FromReviewResolved(new ReviewResolved(
            messageId, "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, createdAt, 1, null, null, null, "rev-1", "Approved"));
        Assert.Equal(NotificationType.ReviewResolved, resolved.Type);
        Assert.Equal(NotificationSeverity.Info, resolved.Severity);
        Assert.Contains("Approved", resolved.Body, StringComparison.Ordinal);

        var blankDecision = NotificationEventMapper.FromReviewResolved(new ReviewResolved(
            messageId, "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, createdAt, 1, null, null, null, "rev-1", "  "));
        Assert.Contains("resolved", blankDecision.Body, StringComparison.Ordinal);

        var exportOk = NotificationEventMapper.FromExport(tenantId, projectId, Guid.NewGuid(), "srt", true, messageId);
        Assert.Equal(NotificationType.ExportCompleted, exportOk.Type);
        Assert.Equal(NotificationSeverity.Info, exportOk.Severity);
        Assert.Equal("ExportJob", exportOk.ResourceType);

        var exportFail = NotificationEventMapper.FromExport(tenantId, projectId, Guid.NewGuid(), "srt", false, messageId);
        Assert.Equal(NotificationType.ExportFailed, exportFail.Type);
        Assert.Equal(NotificationSeverity.Error, exportFail.Severity);

        var exportBlankFormat = NotificationEventMapper.FromExport(tenantId, projectId, Guid.NewGuid(), "  ", true, messageId);
        Assert.Contains("(export)", exportBlankFormat.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void EventMapper_MapsUploadRejected_AndRejectsValidMedia()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var rejected = NotificationEventMapper.FromUploadRejected(new MediaValidated(
            messageId, "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, Now, 1, null, null, null, "media-1", false));
        Assert.Equal(NotificationType.UploadRejected, rejected.Type);
        Assert.Equal(NotificationSeverity.Warning, rejected.Severity);
        Assert.Equal("MediaAsset", rejected.ResourceType);
        Assert.Equal("media-1", rejected.ResourceId);

        // A valid MediaValidated is the wrong input: an explicit domain error,
        // never a silently mis-mapped notification.
        var ex = Assert.Throws<DomainException>(() => NotificationEventMapper.FromUploadRejected(new MediaValidated(
            messageId, "corr", tenantId, projectId, runId, null, null, null, null, null,
            1, Now, 1, null, null, null, "media-1", true)));
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EventMapper_MapsTenantAndProjectScopedWarnings_WithRetentionWindows()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var sourceMessageId = Guid.NewGuid();

        var projectQuota = NotificationEventMapper.FromQuotaWarning(tenantId, projectId, "  storage  ", sourceMessageId);
        Assert.Equal(NotificationType.QuotaWarning, projectQuota.Type);
        Assert.Equal(NotificationSeverity.Warning, projectQuota.Severity);
        Assert.Equal("DubbingProject", projectQuota.ResourceType);
        Assert.Equal(projectId.ToString("N"), projectQuota.ResourceId);
        Assert.Contains("'storage'", projectQuota.Body, StringComparison.Ordinal);

        var tenantQuota = NotificationEventMapper.FromQuotaWarning(tenantId, null, "storage", sourceMessageId);
        Assert.Equal("Tenant", tenantQuota.ResourceType);
        Assert.Equal(tenantId.ToString("N"), tenantQuota.ResourceId);

        var policy = NotificationEventMapper.FromProviderPolicyWarning(tenantId, projectId, "no-retention", sourceMessageId);
        Assert.Equal(NotificationType.ProviderPolicyWarning, policy.Type);
        Assert.Equal(NotificationSeverity.Warning, policy.Severity);
        Assert.Equal("DubbingProject", policy.ResourceType);
        Assert.Contains("no-retention", policy.Body, StringComparison.Ordinal);

        var tenantPolicy = NotificationEventMapper.FromProviderPolicyWarning(tenantId, null, "p", sourceMessageId);
        Assert.Equal("Tenant", tenantPolicy.ResourceType);

        // Warnings use the documented 30d retention window, measured from the
        // mapper's own clock with a one-day tolerance (no wall-clock assertion).
        var quotaRetention = tenantQuota.ExpiresAt!.Value - DateTimeOffset.UtcNow;
        var policyRetention = policy.ExpiresAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(quotaRetention, TimeSpan.FromDays(29), TimeSpan.FromDays(31));
        Assert.InRange(policyRetention, TimeSpan.FromDays(29), TimeSpan.FromDays(31));
        // Completions keep the longer default retention window.
        var completed = NotificationEventMapper.FromRunCompleted(new RunCompleted(
            sourceMessageId, "corr", tenantId, projectId, Guid.NewGuid(), null, null, null, null, null,
            1, Now, 1, null, null, null, "out-1"));
        Assert.InRange(
            completed.ExpiresAt!.Value - DateTimeOffset.UtcNow,
            TimeSpan.FromDays(89),
            TimeSpan.FromDays(91));
    }

    [Fact]
    public void EventMapper_RejectsEmptyIdentity_InsteadOfThrowingLate()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        // A null message is an explicit argument failure, not a domain error.
        Assert.Throws<ArgumentNullException>(() => NotificationEventMapper.FromRunCompleted(null!));
        Assert.Throws<ArgumentNullException>(() => NotificationEventMapper.FromRunFailed(null!));
        Assert.Throws<ArgumentNullException>(() => NotificationEventMapper.FromReviewRequired(null!));
        Assert.Throws<ArgumentNullException>(() => NotificationEventMapper.FromReviewResolved(null!));
        Assert.Throws<ArgumentNullException>(() => NotificationEventMapper.FromUploadRejected(null!));

        Assert.Throws<DomainException>(() => NotificationEventMapper.FromRunCompleted(new RunCompleted(
            messageId, "corr", Guid.Empty, projectId, runId, null, null, null, null, null, 1, Now, 1, null, null, null, "o")));
        Assert.Throws<DomainException>(() => NotificationEventMapper.FromRunCompleted(new RunCompleted(
            messageId, "corr", tenantId, Guid.Empty, runId, null, null, null, null, null, 1, Now, 1, null, null, null, "o")));
        Assert.Throws<DomainException>(() => NotificationEventMapper.FromExport(tenantId, projectId, Guid.Empty, "srt", true, messageId));
        Assert.Throws<DomainException>(() => NotificationEventMapper.FromExport(Guid.Empty, projectId, Guid.NewGuid(), "srt", true, messageId));
        Assert.Throws<DomainException>(() => NotificationEventMapper.FromExport(tenantId, Guid.Empty, Guid.NewGuid(), "srt", true, messageId));

        Assert.Throws<DomainException>(() => NotificationEventMapper.FromQuotaWarning(Guid.Empty, projectId, "storage", messageId));
        Assert.Throws<DomainException>(() => NotificationEventMapper.FromQuotaWarning(tenantId, Guid.Empty, "storage", messageId));
        Assert.Throws<DomainException>(() => NotificationEventMapper.FromQuotaWarning(tenantId, projectId, "  ", messageId));

        Assert.Throws<DomainException>(() => NotificationEventMapper.FromProviderPolicyWarning(Guid.Empty, projectId, "p", messageId));
        Assert.Throws<DomainException>(() => NotificationEventMapper.FromProviderPolicyWarning(tenantId, Guid.Empty, "p", messageId));
        Assert.Throws<DomainException>(() => NotificationEventMapper.FromProviderPolicyWarning(tenantId, projectId, " ", messageId));
    }

    [Fact]
    public void NotificationEntity_AcceptsAWellFormedRow_AndExposesThresholds()
    {
        var notification = NewNotification(Now);

        Assert.Equal(Notification.MaxTitleLength, 200);
        Assert.Equal(Notification.MaxBodyLength, 1000);
        Assert.Equal(Notification.MaxResourceTypeLength, 128);
        Assert.Equal(Notification.MaxResourceIdLength, 256);
        Assert.Null(notification.ReadAt);
        Assert.Equal(Now, notification.CreatedAt);
        Assert.Equal(Now.AddDays(1), notification.ExpiresAt);
    }

    [Fact]
    public void NotificationEntity_RejectsIdentityGuards()
    {
        var valid = NewNotification(Now);

        AssertGuard(() => NewNotification(Now, id: Guid.Empty), "Id");
        AssertGuard(() => NewNotification(Now, tenantId: Guid.Empty), "TenantId");
        AssertGuard(() => NewNotification(Now, recipientUserId: Guid.Empty), "RecipientUserId");
        AssertGuard(() => NewNotification(Now, projectId: Guid.Empty), "ProjectId");
        AssertGuard(() => NewNotification(Now, noProject: true, type: NotificationType.ProcessingCompleted), "ProjectId is required");
        AssertGuard(() => NewNotification(Now, noProject: true, type: NotificationType.QuotaWarning, severity: (NotificationSeverity)99), "Severity");
        AssertGuard(() => NewNotification(Now, type: (NotificationType)42), "Type");
        AssertGuard(() => NewNotification(Now, sourceEventId: Guid.Empty), "SourceEventId");

        // Sanity: the baseline row is valid, so the guards above are the cause.
        Assert.NotNull(valid);
    }

    [Fact]
    public void NotificationEntity_RejectsTextThresholdViolations()
    {
        AssertGuard(() => NewNotification(Now, title: "  "), "Title");
        AssertGuard(() => NewNotification(Now, title: new string('t', Notification.MaxTitleLength + 1)), "Title");
        AssertGuard(() => NewNotification(Now, body: string.Empty), "Body");
        AssertGuard(() => NewNotification(Now, body: new string('b', Notification.MaxBodyLength + 1)), "Body");
        AssertGuard(() => NewNotification(Now, resourceType: " "), "ResourceType");
        AssertGuard(() => NewNotification(Now, resourceType: new string('r', Notification.MaxResourceTypeLength + 1)), "ResourceType");
        AssertGuard(() => NewNotification(Now, resourceId: "  "), "ResourceId");
        AssertGuard(() => NewNotification(Now, resourceId: new string('i', Notification.MaxResourceIdLength + 1)), "ResourceId");
    }

    [Fact]
    public void NotificationEntity_RejectsUnsafeContent_InEveryTextField()
    {
        AssertGuard(() => NewNotification(Now, title: "https://example.test/x"), "Title");
        AssertGuard(() => NewNotification(Now, title: "http://example.test/x"), "Title");
        AssertGuard(() => NewNotification(Now, title: "Bearer abc123"), "Title");
        AssertGuard(() => NewNotification(Now, body: "https://example.test/x"), "Body");
        AssertGuard(() => NewNotification(Now, body: "Bearer abc123"), "Body");
        AssertGuard(() => NewNotification(Now, resourceId: "https://example.test/x"), "ResourceId");
        AssertGuard(() => NewNotification(Now, resourceId: "Bearer abc123"), "ResourceId");
    }

    [Fact]
    public void NotificationEntity_EnforcesTimestampThresholds()
    {
        // Boundary: ReadAt == CreatedAt is allowed.
        var atBoundary = NewNotification(Now, readAt: Now);
        Assert.Equal(Now, atBoundary.ReadAt);

        AssertGuard(() => NewNotification(Now, readAt: Now.AddSeconds(-1)), "ReadAt");
        // Boundary: ExpiresAt == CreatedAt is rejected.
        AssertGuard(() => NewNotification(Now, expiresAt: Now), "ExpiresAt");
        AssertGuard(() => NewNotification(Now, expiresAt: Now.AddSeconds(-1)), "ExpiresAt");

        // A null expiry never expires.
        Assert.Null(NewNotification(Now, noExpiry: true).ExpiresAt);
    }

    [Fact]
    public void NotificationEntity_MarkAsRead_IsIdempotent_AndStampsTheTimestamp()
    {
        var notification = NewNotification(Now);
        Assert.Null(notification.ReadAt);

        notification.MarkAsRead(Now.AddMinutes(1));
        Assert.Equal(Now.AddMinutes(1), notification.ReadAt);

        // Re-marking simply overwrites: never a throw, never a counter drift.
        notification.MarkAsRead(Now.AddMinutes(2));
        Assert.Equal(Now.AddMinutes(2), notification.ReadAt);

        var ex = Assert.Throws<DomainException>(() => notification.MarkAsRead(Now.AddMinutes(-1)));
        Assert.Contains("ReadAt", ex.Message, StringComparison.Ordinal);
        // A rejected mark leaves the previous read stamp untouched.
        Assert.Equal(Now.AddMinutes(2), notification.ReadAt);
    }

    [Fact]
    public void NotificationEntity_Validate_RerunsGuards()
    {
        var notification = NewNotification(Now);

        notification.Validate();

        // Explicit unknown-enum assertion: a safe, explicit domain error.
        var badType = Assert.Throws<DomainException>(() => new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            (NotificationType)999, NotificationSeverity.Info,
            "title", "body", "Tenant", "tenant-1", null, null, Now, Now.AddDays(30)));
        Assert.Contains("Type is not defined", badType.Message, StringComparison.Ordinal);

        var badSeverity = Assert.Throws<DomainException>(() => new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            NotificationType.QuotaWarning, (NotificationSeverity)(-1),
            "title", "body", "Tenant", "tenant-1", null, null, Now, Now.AddDays(30)));
        Assert.Contains("Severity is not defined", badSeverity.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_RedactsUrls_Tokens_AndSecretAssignments()
    {
        Assert.Equal("[redacted-url]", NotificationProjector.Sanitize("https://example.test/x", 100));
        Assert.Equal("[redacted-token]", NotificationProjector.Sanitize("bearer abc123", 100));

        var redacted = NotificationProjector.Sanitize("token=abc123", 100);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);

        Assert.Equal("trimmed", NotificationProjector.Sanitize("  trimmed  ", 100));
        Assert.Equal("abc", NotificationProjector.Sanitize("abcdef", 3));
        // Truncation never exceeds the requested maximum.
        Assert.Equal(Notification.MaxBodyLength, NotificationProjector.Sanitize(new string('x', 5000), Notification.MaxBodyLength).Length);

        Assert.Throws<DomainException>(() => NotificationProjector.Sanitize("   ", 100));
        Assert.Throws<DomainException>(() => NotificationProjector.Sanitize(string.Empty, 100));
    }

    [Fact]
    public void MetricNames_AreFrozen()
    {
        Assert.Equal("DubbingPlatform.Notifications", NotificationMeters.MeterName);
        Assert.Equal("notifications.skipped_total", NotificationMeters.SkippedMetricName);
        Assert.NotNull(NotificationMeters.Skipped);
    }

    private static void AssertGuard(Action action, string expectedFragment)
    {
        var ex = Assert.Throws<DomainException>(action);
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    private static Notification NewNotification(
        DateTimeOffset createdAt,
        Guid? id = null,
        Guid? tenantId = null,
        Guid? recipientUserId = null,
        Guid? projectId = null,
        NotificationType type = NotificationType.ProcessingCompleted,
        NotificationSeverity severity = NotificationSeverity.Info,
        string title = "title",
        string body = "body",
        string resourceType = "ProcessingRun",
        string resourceId = "run-1",
        Guid? sourceEventId = null,
        DateTimeOffset? readAt = null,
        DateTimeOffset? expiresAt = null,
        bool noExpiry = false,
        bool noProject = false)
    {
        return new Notification(
            id ?? Guid.NewGuid(),
            tenantId ?? Guid.NewGuid(),
            recipientUserId ?? Guid.NewGuid(),
            noProject ? null : projectId ?? Guid.NewGuid(),
            type,
            severity,
            title,
            body,
            resourceType,
            resourceId,
            sourceEventId,
            readAt,
            createdAt,
            noExpiry ? null : expiresAt ?? createdAt.AddDays(1));
    }

    private static NotificationInput Input(Guid tenantId, Guid projectId, Guid? sourceEventId)
    {
        return new NotificationInput(
            tenantId, projectId, NotificationType.ProcessingCompleted, NotificationSeverity.Info,
            "Processing completed", "Run abcd1234 completed.",
            "ProcessingRun", Guid.NewGuid().ToString("N"), sourceEventId, Now.AddDays(30));
    }

    private static int CountAll(TestContextFactory factory)
    {
        using var scope = TenantContext.BeginMaintenanceScope();
        using var db = ((IStageExecutionContextFactory)factory).CreateDbContext();
        return db.Set<Notification>().AsNoTracking().Count();
    }

    private static int CountAllInScope(TestContextFactory factory, Guid tenantId)
    {
        using var scope = TenantContext.BeginScope(tenantId);
        using var db = ((IStageExecutionContextFactory)factory).CreateDbContext();
        return db.Set<Notification>().AsNoTracking().Count();
    }

    private static TestContextFactory CreateFactory()
    {
        return new TestContextFactory(CreateOptions());
    }

    private static DbContextOptions<AppDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("NotificationLogicTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
    }

    private static void SeedUser(TestContextFactory factory, Guid tenantId, Guid userId, TenantUserStatus status)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, "sub-" + userId.ToString("N"), "user@example.test", "User", status, Now, Now));
            db.SaveChanges();
        }
    }

    private static void SeedMembership(TestContextFactory factory, Guid tenantId, Guid projectId, Guid userId, ProjectRole role)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, userId, role, null, Now));
            db.SaveChanges();
        }
    }

    private static void SeedProject(TestContextFactory factory, Guid tenantId, Guid projectId, Guid? ownerUserId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<DubbingProject>().Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Created, "{}", "cfg-hash",
                null, null, Now, Now, "Synthetic project", null, ownerUserId));
            db.SaveChanges();
        }
    }

    private static Guid SeedNotification(
        TestContextFactory factory,
        Guid tenantId,
        Guid recipientUserId,
        string resourceId,
        DateTimeOffset? readAt,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            var notification = new Notification(
                Guid.NewGuid(), tenantId, recipientUserId, Guid.NewGuid(),
                NotificationType.ProcessingCompleted, NotificationSeverity.Info,
                "title", "body", "ProcessingRun", resourceId, null,
                readAt, createdAt, expiresAt);
            db.Set<Notification>().Add(notification);
            db.SaveChanges();
            return notification.Id;
        }
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
