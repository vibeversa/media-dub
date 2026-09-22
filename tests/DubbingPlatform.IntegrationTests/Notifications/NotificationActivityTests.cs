using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;
using DubbingPlatform.Application.Activity;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Notifications;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Notifications;

public sealed class NotificationActivityTests
{
    private readonly ITestOutputHelper _output;

    public NotificationActivityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Notification_ProjectId_Null_Rejected_For_Project_Types()
    {
        var now = DateTimeOffset.UtcNow;
        var ex = Assert.Throws<DomainException>(() => new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            NotificationType.ProcessingCompleted, NotificationSeverity.Info,
            "Processing completed", "Run done.",
            "ProcessingRun", Guid.NewGuid().ToString("N"), Guid.NewGuid(),
            null, now, null));
        Assert.Contains("ProjectId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Notification_ProjectId_Null_Allowed_For_Quota_Types()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            NotificationType.QuotaWarning, NotificationSeverity.Warning,
            "Quota warning", "Quota near limit.",
            "Tenant", Guid.NewGuid().ToString("N"), Guid.NewGuid(),
            null, now, now.AddDays(30));
        Assert.Equal(NotificationType.QuotaWarning, quota.Type);

        var policy = new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            NotificationType.ProviderPolicyWarning, NotificationSeverity.Warning,
            "Provider policy warning", "Policy needs attention.",
            "Tenant", Guid.NewGuid().ToString("N"), null,
            null, now, null);
        Assert.Null(policy.SourceEventId);
    }

    [Fact]
    public void Notification_Url_Content_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<DomainException>(() => new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            NotificationType.ProcessingCompleted, NotificationSeverity.Info,
            "See https://example.com/download", "Run done.",
            "ProcessingRun", Guid.NewGuid().ToString("N"), null,
            null, now, null));
        Assert.Throws<DomainException>(() => new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            NotificationType.ProcessingFailed, NotificationSeverity.Error,
            "Processing failed", "Bearer sk-secret-token",
            "ProcessingRun", Guid.NewGuid().ToString("N"), null,
            null, now, null));
    }

    [Fact]
    public void Notification_Sanitize_Strips_Urls_And_Tokens()
    {
        var cleaned = NotificationProjector.Sanitize(
            "Run done, see https://example.com/x with Bearer abc123", 1000);
        Assert.DoesNotContain("https://", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer ", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(string.Empty, cleaned);

        // Sanitized output must satisfy entity validation (no poison on dirty input).
        var now = DateTimeOffset.UtcNow;
        var notification = new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            NotificationType.ProcessingFailed, NotificationSeverity.Error,
            "Processing failed", cleaned,
            "ProcessingRun", Guid.NewGuid().ToString("N"), null,
            null, now, null);
        Assert.Equal(cleaned, notification.Body);
    }

    [Fact]
    public void Notification_Meters_Use_Frozen_Counter_Name()
    {
        Assert.Equal("notifications.skipped_total", NotificationMeters.SkippedMetricName);
        Assert.Equal("DubbingPlatform.Notifications", NotificationMeters.MeterName);
        Assert.NotNull(NotificationMeters.Skipped);
    }

    [Fact]
    public void Notification_Mappers_Contain_No_Secrets()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var inputs = new NotificationInput[]
        {
            NotificationEventMapper.FromRunCompleted(NewRunCompleted(tenantId, projectId, runId, now)),
            NotificationEventMapper.FromRunFailed(NewRunFailed(tenantId, projectId, runId, now)),
            NotificationEventMapper.FromReviewRequired(NewReviewRequired(tenantId, projectId, runId, now)),
            NotificationEventMapper.FromReviewResolved(NewReviewResolved(tenantId, projectId, runId, now)),
            NotificationEventMapper.FromUploadRejected(NewMediaValidated(tenantId, projectId, false, now)),
            NotificationEventMapper.FromExport(tenantId, projectId, Guid.NewGuid(), "mp4", true, Guid.NewGuid()),
            NotificationEventMapper.FromExport(tenantId, projectId, Guid.NewGuid(), "mp4", false, Guid.NewGuid()),
            NotificationEventMapper.FromQuotaWarning(tenantId, projectId, "storage", Guid.NewGuid()),
            NotificationEventMapper.FromQuotaWarning(tenantId, null, "storage", Guid.NewGuid()),
            NotificationEventMapper.FromProviderPolicyWarning(tenantId, null, "region-block", Guid.NewGuid()),
        };

        foreach (var input in inputs)
        {
            AssertNoSecrets(input.Title);
            AssertNoSecrets(input.Body);
            AssertNoSecrets(input.ResourceId);
            Assert.InRange(input.Title.Length, 1, Notification.MaxTitleLength);
            Assert.InRange(input.Body.Length, 1, Notification.MaxBodyLength);
        }
    }

    [Fact]
    public void Activity_Mappers_Contain_No_Secrets()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var inputs = new ActivityInput[]
        {
            ActivityEventMapper.FromUploadCompleted(NewMediaUploaded(tenantId, projectId, now)),
            ActivityEventMapper.FromUploadRejected(NewMediaValidated(tenantId, projectId, false, now)),
            ActivityEventMapper.FromProcessingStarted(NewRunStarted(tenantId, projectId, runId, now)),
            ActivityEventMapper.FromReviewRequested(NewReviewRequired(tenantId, projectId, runId, now)),
            ActivityEventMapper.FromReviewResolved(NewReviewResolved(tenantId, projectId, runId, now)),
            ActivityEventMapper.FromProcessingCompleted(NewRunCompleted(tenantId, projectId, runId, now)),
            ActivityEventMapper.FromProcessingFailed(NewRunFailed(tenantId, projectId, runId, now)),
            ActivityEventMapper.FromEditApplied(tenantId, projectId, runId, Guid.NewGuid(), Guid.NewGuid(), "corr-1", now),
            ActivityEventMapper.FromExportCompleted(tenantId, projectId, runId, Guid.NewGuid(), "mp4", true, "corr-2", now),
        };

        foreach (var input in inputs)
        {
            AssertNoSecrets(input.Summary);
            AssertNoSecrets(input.CorrelationId);
            Assert.InRange(input.Summary.Length, 1, ActivityEvent.MaxSummaryLength);
            if (input.MetadataJson is not null)
            {
                AssertNoSecrets(input.MetadataJson);
                using var _ = JsonDocument.Parse(input.MetadataJson);
            }
        }
    }

    [Fact]
    public void Activity_Security_Relevant_Matrix()
    {
        Assert.True(ActivityProjector.IsSecurityRelevant(ActivityType.ReviewRequested));
        Assert.True(ActivityProjector.IsSecurityRelevant(ActivityType.ReviewResolved));
        Assert.True(ActivityProjector.IsSecurityRelevant(ActivityType.EditApplied));
        Assert.True(ActivityProjector.IsSecurityRelevant(ActivityType.ExportCompleted));
        Assert.True(ActivityProjector.IsSecurityRelevant(ActivityType.ExportFailed));
        Assert.False(ActivityProjector.IsSecurityRelevant(ActivityType.ProcessingStarted));
        Assert.False(ActivityProjector.IsSecurityRelevant(ActivityType.TranslationCompleted));
        Assert.False(ActivityProjector.IsSecurityRelevant(ActivityType.ProcessingCompleted));
        Assert.Equal("review.requested", ActivityProjector.AuditActionFor(ActivityType.ReviewRequested));
        Assert.Equal("review.resolved", ActivityProjector.AuditActionFor(ActivityType.ReviewResolved));
        Assert.Equal("export.completed", ActivityProjector.AuditActionFor(ActivityType.ExportCompleted));
    }

    [SkippableFact]
    public async Task Dedup_Same_Source_Event_Projects_Once()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);

            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var seed = await SeedProjectWithMembersAsync(options, tenantId, projectId).ConfigureAwait(true);

            var message = NewRunCompleted(tenantId, projectId, runId, now);
            var input = NotificationEventMapper.FromRunCompleted(message);

            IReadOnlyList<Notification> first;
            var firstProjector = CreateNotificationProjector(options);
            {
                first = await firstProjector.ProjectAsync(input).ConfigureAwait(true);
            }

            Assert.Equal(3, first.Count);
            Assert.All(first, n => Assert.Equal(message.MessageId, n.SourceEventId));

            // Simulate a restart: brand-new projector and contexts re-project the same event.
            IReadOnlyList<Notification> second;
            var secondProjector = CreateNotificationProjector(options);
            {
                second = await secondProjector.ProjectAsync(input).ConfigureAwait(true);
            }

            Assert.Equal(3, second.Count);
            Assert.Equal(
                first.Select(n => n.Id).OrderBy(id => id),
                second.Select(n => n.Id).OrderBy(id => id));

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                Assert.Equal(3, await db.Set<Notification>().CountAsync().ConfigureAwait(true));
                foreach (var recipient in seed.Recipients)
                {
                    Assert.Equal(1, await db.Set<Notification>()
                        .CountAsync(n => n.RecipientUserId == recipient).ConfigureAwait(true));
                }
            }
        }
    }

    [SkippableFact]
    public async Task Missing_Recipient_Falls_Back_To_Owner_Then_Skips()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);

            var tenantId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            // Project with an owner but no memberships: owner is the fallback.
            var ownerProject = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedProjectAsync(options, tenantId, ownerProject, ownerId).ConfigureAwait(true);
            var ownerInput = NotificationEventMapper.FromRunFailed(
                NewRunFailed(tenantId, ownerProject, Guid.NewGuid(), now));
            IReadOnlyList<Notification> ownerResult;
            var ownerProjector = CreateNotificationProjector(options);
            {
                ownerResult = await ownerProjector.ProjectAsync(ownerInput).ConfigureAwait(true);
            }

            Assert.Single(ownerResult);
            Assert.Equal(ownerId, ownerResult[0].RecipientUserId);

            // Project with neither owner nor memberships: skipped without rows.
            var orphanProject = Guid.NewGuid();
            await SeedProjectAsync(options, tenantId, orphanProject, null).ConfigureAwait(true);
            var orphanInput = NotificationEventMapper.FromRunFailed(
                NewRunFailed(tenantId, orphanProject, Guid.NewGuid(), now));
            IReadOnlyList<Notification> orphanResult;
            var orphanProjector = CreateNotificationProjector(options);
            {
                orphanResult = await orphanProjector.ProjectAsync(orphanInput).ConfigureAwait(true);
            }

            Assert.Empty(orphanResult);
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                Assert.Equal(0, await db.Set<Notification>()
                    .CountAsync(n => n.SourceEventId == orphanInput.SourceEventId).ConfigureAwait(true));
            }
        }
    }

    [SkippableFact]
    public async Task Activity_Pagination_Returns_OccurredAt_Order()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);

            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var baseTime = DateTimeOffset.UtcNow;
            await SeedProjectAsync(options, tenantId, projectId, null).ConfigureAwait(true);

            var appendProjector = CreateActivityProjector(options);
            {
                for (var i = 0; i < 5; i++)
                {
                    await appendProjector.AppendAsync(new ActivityInput(
                        tenantId, projectId, runId,
                        ActivityType.TranslationCompleted, ActivityActorType.System, null,
                        string.Concat("Stage batch ", i.ToString(System.Globalization.CultureInfo.InvariantCulture), " completed."),
                        ActivitySeverity.Info,
                        string.Concat("corr-page-", i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        baseTime.AddSeconds(i),
                        ActivityProjector.BuildMetadata(new Dictionary<string, object?>
                        {
                            ["batch"] = i,
                        }))).ConfigureAwait(false);
                }
            }

            var listProjector = CreateActivityProjector(options);
            {
                var (page1, total) = await listProjector.ListAsync(tenantId, projectId, 1, 2).ConfigureAwait(true);
                var (page2, _) = await listProjector.ListAsync(tenantId, projectId, 2, 2).ConfigureAwait(true);
                var (page3, _) = await listProjector.ListAsync(tenantId, projectId, 3, 2).ConfigureAwait(true);

                Assert.Equal(5, total);
                Assert.Equal(2, page1.Count);
                Assert.Equal(2, page2.Count);
                Assert.Single(page3);
                var ordered = page1.Concat(page2).Concat(page3).ToList();
                for (var i = 0; i < ordered.Count - 1; i++)
                {
                    Assert.True(ordered[i].OccurredAt <= ordered[i + 1].OccurredAt);
                }

                Assert.Equal("corr-page-0", ordered[0].CorrelationId);
                Assert.Equal("corr-page-4", ordered[4].CorrelationId);
            }
        }
    }

    [SkippableFact]
    public async Task Expired_Notifications_Excluded_But_Retained()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);

            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await SeedProjectAsync(options, tenantId, projectId, null).ConfigureAwait(true);
            await SeedUserAsync(options, tenantId, recipient).ConfigureAwait(true);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                db.Set<Notification>().Add(new Notification(
                    Guid.NewGuid(), tenantId, recipient, projectId,
                    NotificationType.ProcessingCompleted, NotificationSeverity.Info,
                    "Active", "Still visible.",
                    "ProcessingRun", Guid.NewGuid().ToString("N"), Guid.NewGuid(),
                    null, now, now.AddDays(90)));
                db.Set<Notification>().Add(new Notification(
                    Guid.NewGuid(), tenantId, recipient, projectId,
                    NotificationType.QuotaWarning, NotificationSeverity.Warning,
                    "Expired", "No longer listed.",
                    "Tenant", tenantId.ToString("N"), Guid.NewGuid(),
                    null, now.AddDays(-10), now.AddDays(-9)));
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            var expiryProjector = CreateNotificationProjector(options);
            {
                var (items, total) = await expiryProjector.ListActiveAsync(tenantId, recipient, 1, 10).ConfigureAwait(true);
                Assert.Equal(1, total);
                Assert.Single(items);
                Assert.Equal("Active", items[0].Title);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                Assert.Equal(2, await db.Set<Notification>()
                    .CountAsync(n => n.RecipientUserId == recipient).ConfigureAwait(true));
            }
        }
    }

    [SkippableFact]
    public async Task CrossTenant_Isolation_Enforced_With_Rls()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var projectA = Guid.NewGuid();
            var recipientA = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await SeedProjectAsync(options, tenantA, projectA, null).ConfigureAwait(true);
            await SeedUserAsync(options, tenantA, recipientA).ConfigureAwait(true);

            using (TenantContext.BeginScope(tenantA))
            {
                using var db = new AppDbContext(options);
                db.Set<Notification>().Add(new Notification(
                    Guid.NewGuid(), tenantA, recipientA, projectA,
                    NotificationType.ProcessingCompleted, NotificationSeverity.Info,
                    "Tenant A", "Only for A.",
                    "ProcessingRun", Guid.NewGuid().ToString("N"), Guid.NewGuid(),
                    null, now, null));
                db.Set<ActivityEvent>().Add(new ActivityEvent(
                    Guid.NewGuid(), tenantA, projectA, Guid.NewGuid(),
                    ActivityType.ProcessingCompleted, ActivityActorType.System, null,
                    "Processing completed.", ActivitySeverity.Info,
                    "corr-a", now, ActivityEvent.SupportedSchemaVersion, null));
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            var notificationListProjector = CreateNotificationProjector(options);
            {
                var (itemsB, totalB) = await notificationListProjector.ListActiveAsync(tenantB, recipientA, 1, 10).ConfigureAwait(true);
                Assert.Equal(0, totalB);
                Assert.Empty(itemsB);
            }

            var activityListProjector = CreateActivityProjector(options);
            {
                var (itemsB, totalB) = await activityListProjector.ListAsync(tenantB, projectA, 1, 10).ConfigureAwait(true);
                Assert.Equal(0, totalB);
                Assert.Empty(itemsB);
            }

            using (TenantContext.BeginScope(tenantB))
            {
                using var db = new AppDbContext(options);
                Assert.Equal(0, await db.Set<Notification>().CountAsync().ConfigureAwait(true));
                Assert.Equal(0, await db.Set<ActivityEvent>().CountAsync().ConfigureAwait(true));
            }

            await using var connection = new NpgsqlConnection(container.GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(true);
            await EnsureAppRoleAsync(connection).ConfigureAwait(true);
            Assert.Equal(0, await CountAsRoleAsync(connection, "app_role", "notifications", tenantB).ConfigureAwait(true));
            Assert.Equal(1, await CountAsRoleAsync(connection, "app_role", "notifications", tenantA).ConfigureAwait(true));
            Assert.Equal(0, await CountAsRoleAsync(connection, "app_role", "activity_events", tenantB).ConfigureAwait(true));
            Assert.Equal(1, await CountAsRoleAsync(connection, "app_role", "activity_events", tenantA).ConfigureAwait(true));

            using (TenantContext.BeginMaintenanceScope())
            {
                using var db = new AppDbContext(options);
                var policies = await QuerySingleColumnAsync(
                    db, "SELECT policyname FROM pg_policies WHERE schemaname = 'public' AND tablename IN ('notifications','activity_events')")
                    .ConfigureAwait(true);
                Assert.Equal(2, policies.Count(p => string.Equals(p, "tenant_isolation", StringComparison.Ordinal)));

                var rls = await QuerySingleColumnAsync(
                    db, "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND rowsecurity = true AND tablename IN ('notifications','activity_events')")
                    .ConfigureAwait(true);
                Assert.Equal(2, rls.Count);
            }
        }
    }

    [SkippableFact]
    public async Task Failed_Run_Error_Message_Never_Persisted()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);

            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await SeedProjectWithMembersAsync(options, tenantId, projectId).ConfigureAwait(true);

            var hostile = "Bearer sk-hostile-secret https://evil.example/collect?token=abc transcript body";
            var failed = NewRunFailed(tenantId, projectId, runId, now) with { ErrorMessage = hostile };
            var notificationInput = NotificationEventMapper.FromRunFailed(failed);
            var activityInput = ActivityEventMapper.FromProcessingFailed(failed);

            var hostileProjector = CreateNotificationProjector(options);
            {
                await hostileProjector.ProjectAsync(notificationInput).ConfigureAwait(true);
            }

            var hostileActivityProjector = CreateActivityProjector(options);
            {
                await hostileActivityProjector.AppendAsync(activityInput).ConfigureAwait(true);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var stored = await db.Set<Notification>().ToListAsync().ConfigureAwait(true);
                Assert.NotEmpty(stored);
                foreach (var notification in stored)
                {
                    AssertNoSecrets(notification.Title);
                    AssertNoSecrets(notification.Body);
                    AssertNoSecrets(notification.ResourceId);
                    Assert.DoesNotContain("evil.example", notification.Body, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("sk-hostile-secret", notification.Body, StringComparison.Ordinal);
                }

                var activities = await db.Set<ActivityEvent>().ToListAsync().ConfigureAwait(true);
                Assert.Single(activities);
                AssertNoSecrets(activities[0].Summary);
                if (activities[0].MetadataJson is not null)
                {
                    AssertNoSecrets(activities[0].MetadataJson!);
                }

                Assert.DoesNotContain(hostile, notificationInput.Body, StringComparison.Ordinal);
            }
        }
    }

    [SkippableFact]
    public async Task Security_Relevant_Activity_Writes_Audit_Event()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);

            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await SeedProjectAsync(options, tenantId, projectId, null).ConfigureAwait(true);

            var reviewer = Guid.NewGuid();
            await SeedUserAsync(options, tenantId, reviewer).ConfigureAwait(true);

            var auditProjector = CreateActivityProjector(options);
            {
                await auditProjector.AppendAsync(
                    ActivityEventMapper.FromEditApplied(tenantId, projectId, runId, Guid.NewGuid(), reviewer, "corr-audit", now),
                    CancellationToken.None).ConfigureAwait(true);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                Assert.Equal(1, await db.Set<ActivityEvent>()
                    .CountAsync(e => e.CorrelationId == "corr-audit").ConfigureAwait(true));
                var audit = await db.Set<AuditEvent>()
                    .SingleAsync(a => a.Action == "review.edit_applied").ConfigureAwait(true);
                Assert.Equal(reviewer.ToString("D"), audit.Actor);
                Assert.Equal(projectId, audit.ProjectId);
            }
        }
    }

    [SkippableFact]
    public async Task Mark_As_Read_Persists()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);

            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await SeedProjectAsync(options, tenantId, projectId, null).ConfigureAwait(true);
            await SeedUserAsync(options, tenantId, recipient).ConfigureAwait(true);

            Guid notificationId;
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var notification = new Notification(
                    Guid.NewGuid(), tenantId, recipient, projectId,
                    NotificationType.ProcessingCompleted, NotificationSeverity.Info,
                    "Processing completed", "Run done.",
                    "ProcessingRun", Guid.NewGuid().ToString("N"), Guid.NewGuid(),
                    null, now, null);
                db.Set<Notification>().Add(notification);
                await db.SaveChangesAsync().ConfigureAwait(true);
                notificationId = notification.Id;
            }

            var readProjector = CreateNotificationProjector(options);
            {
                await readProjector.MarkAsReadAsync(tenantId, recipient, notificationId).ConfigureAwait(true);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var reloaded = await db.Set<Notification>().AsNoTracking()
                    .SingleAsync(n => n.Id == notificationId).ConfigureAwait(true);
                Assert.NotNull(reloaded.ReadAt);
            }
        }
    }

    private static void AssertNoSecrets(string value)
    {
        Assert.DoesNotContain("http://", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer ", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN ", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", value, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", value, StringComparison.OrdinalIgnoreCase);
    }

    private static RunCompleted NewRunCompleted(Guid tenantId, Guid projectId, Guid runId, DateTimeOffset now)
    {
        return new RunCompleted(
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, projectId, runId,
            null, null, null, null, null, 1, now, 0, null, null, null,
            string.Concat("art_", Guid.NewGuid().ToString("N")));
    }

    private static RunFailed NewRunFailed(Guid tenantId, Guid projectId, Guid runId, DateTimeOffset now)
    {
        return new RunFailed(
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, projectId, runId,
            null, null, null, null, null, 1, now, 0, null, null, null,
            "PROVIDER_TIMEOUT", "Provider timed out after 30s.");
    }

    private static StageReviewRequired NewReviewRequired(Guid tenantId, Guid projectId, Guid runId, DateTimeOffset now)
    {
        return new StageReviewRequired(
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, projectId, runId,
            null, "Translation", "Segment", Guid.NewGuid().ToString("N"), null,
            1, now, 0, null, null, null,
            "Translation", string.Concat("rev_", Guid.NewGuid().ToString("N")));
    }

    private static ReviewResolved NewReviewResolved(Guid tenantId, Guid projectId, Guid runId, DateTimeOffset now)
    {
        return new ReviewResolved(
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, projectId, runId,
            null, "Translation", "Segment", Guid.NewGuid().ToString("N"), null,
            1, now, 0, null, null, null,
            string.Concat("rev_", Guid.NewGuid().ToString("N")), "Approve");
    }

    private static MediaUploaded NewMediaUploaded(Guid tenantId, Guid projectId, DateTimeOffset now)
    {
        return new MediaUploaded(
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, projectId, Guid.Empty,
            null, null, null, null, null, 1, now, 0, null, null, null,
            string.Concat("upl_", Guid.NewGuid().ToString("N")),
            string.Concat(tenantId.ToString("N"), "/upload.mp4"), null);
    }

    private static MediaValidated NewMediaValidated(Guid tenantId, Guid projectId, bool isValid, DateTimeOffset now)
    {
        return new MediaValidated(
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, projectId, Guid.Empty,
            null, "MediaValidation", "Project", projectId.ToString("N"), null,
            1, now, 0, null, null, null,
            string.Concat("asset_", Guid.NewGuid().ToString("N")), isValid);
    }

    private static RunStarted NewRunStarted(Guid tenantId, Guid projectId, Guid runId, DateTimeOffset now)
    {
        return new RunStarted(
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, projectId, runId,
            null, null, null, null, null, 1, now, 0, null, null, null,
            "v1", new string('a', 64));
    }

    private static NotificationProjector CreateNotificationProjector(DbContextOptions<AppDbContext> options)
    {
        return new NotificationProjector(new TestFactory(options));
    }

    private static ActivityProjector CreateActivityProjector(DbContextOptions<AppDbContext> options)
    {
        return new ActivityProjector(new TestFactory(options));
    }

    private sealed record SeedResult(Guid OwnerId, IReadOnlyList<Guid> Recipients);

    private static async Task<SeedResult> SeedProjectWithMembersAsync(
        DbContextOptions<AppDbContext> options, Guid tenantId, Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var ownerId = Guid.NewGuid();
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Tenants.Add(new Tenant(tenantId, "Notify", $"notify-{tenantId:N}", now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "de", ProjectStatus.Created,
                "{}", new string('a', 64), null, null, now, now,
                "Notify project", null, ownerId));
            db.Set<TenantUser>().Add(new TenantUser(
                ownerId, tenantId, "sub-owner", "owner@example.com", "Owner",
                TenantUserStatus.Active, now, now));
            db.Set<TenantUser>().Add(new TenantUser(
                memberA, tenantId, "sub-a", "a@example.com", "Member A",
                TenantUserStatus.Active, now, now));
            db.Set<TenantUser>().Add(new TenantUser(
                memberB, tenantId, "sub-b", "b@example.com", "Member B",
                TenantUserStatus.Active, now, now));
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, memberA, ProjectRole.ProjectEditor, null, now));
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, memberB, ProjectRole.Reviewer, null, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return new SeedResult(ownerId, [ownerId, memberA, memberB]);
    }

    private static async Task SeedProjectAsync(
        DbContextOptions<AppDbContext> options, Guid tenantId, Guid projectId, Guid? ownerId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true))
            {
                db.Tenants.Add(new Tenant(tenantId, "Notify", $"notify-{tenantId:N}", now));
            }

            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "de", ProjectStatus.Created,
                "{}", new string('a', 64), null, null, now, now,
                "Notify project", null, ownerId));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task SeedUserAsync(
        DbContextOptions<AppDbContext> options, Guid tenantId, Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, string.Concat("sub-", userId.ToString("N")), "user@example.com", "User",
                TenantUserStatus.Active, now, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task MigrateAsync(DbContextOptions<AppDbContext> options)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = new AppDbContext(options);
            await db.Database.MigrateAsync().ConfigureAwait(true);
        }
    }

    private static DbContextOptions<AppDbContext> CreateOptions(PostgreSqlContainer container)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    private async Task<PostgreSqlContainer> StartContainerAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any transport-level failure means "skip", domain failures surface later.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping notification test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private static async Task EnsureAppRoleAsync(NpgsqlConnection connection)
    {
        using var role = connection.CreateCommand();
        role.CommandText = "DO $$ BEGIN CREATE ROLE app_role NOLOGIN; EXCEPTION WHEN duplicate_object THEN NULL; END $$;";
        await role.ExecuteNonQueryAsync().ConfigureAwait(true);

        using var grants = connection.CreateCommand();
        grants.CommandText = "GRANT USAGE ON SCHEMA public TO app_role; GRANT SELECT ON ALL TABLES IN SCHEMA public TO app_role;";
        await grants.ExecuteNonQueryAsync().ConfigureAwait(true);
    }

    private static async Task<long> CountAsRoleAsync(NpgsqlConnection connection, string role, string table, Guid tenantId)
    {
        using var setRole = connection.CreateCommand();
        setRole.CommandText = string.Concat("SET ROLE ", role);
        await setRole.ExecuteNonQueryAsync().ConfigureAwait(true);

        try
        {
            using var setTenant = connection.CreateCommand();
            setTenant.CommandText = "SELECT set_config('app.tenant_id', $1, false)";
            setTenant.Parameters.Add(new NpgsqlParameter { Value = tenantId.ToString("D") });
            await setTenant.ExecuteNonQueryAsync().ConfigureAwait(true);

            using var count = connection.CreateCommand();
            count.CommandText = string.Concat("SELECT COUNT(*) FROM ", table);
            var result = await count.ExecuteScalarAsync().ConfigureAwait(true);
            return (long)(result ?? 0L);
        }
        finally
        {
            using var reset = connection.CreateCommand();
            reset.CommandText = "RESET ROLE";
            await reset.ExecuteNonQueryAsync().ConfigureAwait(true);
        }
    }

    private static async Task<HashSet<string>> QuerySingleColumnAsync(AppDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(true);
            shouldClose = true;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var values = new HashSet<string>(StringComparer.Ordinal);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(true);
            while (await reader.ReadAsync().ConfigureAwait(true))
            {
                values.Add(reader.GetString(0));
            }

            return values;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(true);
            }
        }
    }

    private static bool IsInfrastructureUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var name = current.GetType().FullName ?? string.Empty;
            if (name.Contains("Docker", StringComparison.Ordinal) ||
                name.Contains("Testcontainers", StringComparison.Ordinal))
            {
                return true;
            }

            if (current is HttpRequestException
                or TimeoutException
                or ObjectDisposedException
                or UnauthorizedAccessException
                or IOException
                or SocketException
                or DbException
                or InvalidOperationException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class TestFactory : IStageExecutionContextFactory
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public DbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }
    }
}
