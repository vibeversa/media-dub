using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Diagnostics;
using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DubbingPlatform.UnitTests.Diagnostics;

/// <summary>
/// Unit coverage for the diagnostics read layer: elevated-authz denial for
/// non-viewers, secret-free DTO serialization, correlation-id presence,
/// stale-lease TTL boundaries, empty-DLQ shape, orphan pagination caps, and
/// EF read-only behavior (zero change-tracker entries). Entity-backed
/// services run over an InMemory <c>AppDbContext</c> via a tracking test
/// factory; the queue backlog and probe tracker are fakes.
/// </summary>
public sealed class DiagnosticsReadTests
{
    private static readonly string[] SecretMarkers =
    [
        "secret",
        "apikey",
        "api_key",
        "connectionstring",
        "connection_string",
        "internalpath",
        "internal_path",
        "rawpayload",
        "raw_payload",
    ];

    [Fact]
    public async Task NonViewer_IsDenied_WithDiagnosticsForbiddenMarker()
    {
        var tenantId = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, Guid.NewGuid());

        var checker = new DiagnosticsAccessChecker(factory, NullLogger<DiagnosticsAccessChecker>.Instance);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => checker.RequireDiagnosticsViewerAsync(tenantId, outsider));
        Assert.Equal(ErrorCodes.Forbidden, exception.ErrorCode);
        Assert.Contains(DiagnosticsAccessPolicy.ForbiddenMarker, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnerMember_IsGranted_ViewerAccess()
    {
        var tenantId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, owner);

        var checker = new DiagnosticsAccessChecker(factory, NullLogger<DiagnosticsAccessChecker>.Instance);
        await checker.RequireDiagnosticsViewerAsync(tenantId, owner);
    }

    [Fact]
    public async Task EveryQueryMethod_EnforcesAuthz_BeforeTouchingData()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var factory = CreateFactory();
        var deny = new DenyAccessChecker();
        var options = OptionsFor(ttlSeconds: 300);

        var health = new ProviderHealthQueryService(
            factory, new FakeHealthTracker(), MsOptions.Create(new ProviderOptions()),
            options, deny, NullLogger<ProviderHealthQueryService>.Instance);
        var queues = new QueueDiagnosticsService(
            new FakeQueueBacklog([]), deny, NullLogger<QueueDiagnosticsService>.Instance);
        var leases = new LeaseOrphanService(
            factory, options, deny, NullLogger<LeaseOrphanService>.Instance);
        var reviews = new ReviewBacklogService(
            factory, deny, NullLogger<ReviewBacklogService>.Instance);
        var workers = new WorkerHealthService(
            factory, options, deny, NullLogger<WorkerHealthService>.Instance);

        await Assert.ThrowsAsync<ForbiddenException>(() => health.GetProviderHealthAsync(tenantId, userId));
        await Assert.ThrowsAsync<ForbiddenException>(() => health.GetProviderRoutesAsync(tenantId, userId));
        await Assert.ThrowsAsync<ForbiddenException>(() => queues.GetQueueDepthsAsync(tenantId, userId));
        await Assert.ThrowsAsync<ForbiddenException>(() => queues.GetDlqSummaryAsync(tenantId, userId));
        await Assert.ThrowsAsync<ForbiddenException>(() => leases.GetStaleLeasesAsync(tenantId, userId));
        await Assert.ThrowsAsync<ForbiddenException>(() => leases.GetOrphanArtifactsAsync(tenantId, userId));
        await Assert.ThrowsAsync<ForbiddenException>(() => reviews.GetBacklogAsync(tenantId, userId));
        await Assert.ThrowsAsync<ForbiddenException>(() => workers.GetWorkerHealthAsync(tenantId, userId));
    }

    [Fact]
    public void SerializedDtos_ContainNoSecretBearingFields()
    {
        var correlation = "corr123";
        var now = DateTimeOffset.UtcNow;
        object[] dtos =
        [
            new ProviderHealthDto(correlation, "Mock", "Healthy", 12.0, 0.0, now, ["transcription"], "Closed"),
            new ProviderRouteDto(correlation, "transcription", "mock", 0, true),
            new QueueDepthDto(correlation, "ai.provider", 3),
            new DlqSummaryDto(correlation, 1, now, TimeSpan.FromMinutes(2), [new DlqReasonCount("PROVIDER_TIMEOUT", 1)]),
            new StaleLeaseDto(correlation, Guid.NewGuid(), "Transcription", "Running", Guid.NewGuid(), Guid.NewGuid(), "run:abc;project:def", now, now, now),
            new OrphanArtifactDto(correlation, Guid.NewGuid(), 1024, "wav", "hash", now, now),
            new ReviewBacklogDto(correlation, 2, new Dictionary<string, long> { ["Open"] = 2 }, new Dictionary<string, long> { ["Error"] = 1 }, now, Guid.NewGuid(), [new ReviewBacklogProjectEntry(Guid.NewGuid(), 2, now)]),
            new WorkerHealthDto(correlation, "worker-a", "Active", now, 2, null),
        ];

        foreach (var dto in dtos)
        {
            foreach (var property in dto.GetType().GetProperties())
            {
                var name = property.Name.ToLowerInvariant();
                foreach (var marker in SecretMarkers)
                {
                    Assert.DoesNotContain(marker, name);
                }
            }

            var json = JsonSerializer.Serialize(dto).ToLowerInvariant();
            foreach (var marker in SecretMarkers)
            {
                Assert.DoesNotContain($"\"{marker}", json);
            }
        }
    }

    [Fact]
    public async Task CorrelationId_IsGenerated_WhenOmitted_AndEchoed_WhenSupplied()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);
        var access = new AllowAccessChecker();
        var options = OptionsFor(ttlSeconds: 300);

        var queues = new QueueDiagnosticsService(
            new FakeQueueBacklog([]), access, NullLogger<QueueDiagnosticsService>.Instance);
        var generated = await queues.GetQueueDepthsAsync(tenantId, userId);
        Assert.All(generated, d => Assert.False(string.IsNullOrWhiteSpace(d.CorrelationId)));
        Assert.Single(generated.Select(d => d.CorrelationId).Distinct());

        var echoed = await queues.GetQueueDepthsAsync(tenantId, userId, correlationId: "caller-supplied");
        Assert.All(echoed, d => Assert.Equal("caller-supplied", d.CorrelationId));

        var leases = new LeaseOrphanService(
            factory, options, access, NullLogger<LeaseOrphanService>.Instance);
        var page = await leases.GetOrphanArtifactsAsync(tenantId, userId, correlationId: "caller-supplied");
        Assert.All(page.Items, d => Assert.Equal("caller-supplied", d.CorrelationId));

        var health = new ProviderHealthQueryService(
            factory, new FakeHealthTracker(), MsOptions.Create(new ProviderOptions()),
            options, access, NullLogger<ProviderHealthQueryService>.Instance);
        var snapshots = await health.GetProviderHealthAsync(tenantId, userId);
        Assert.All(snapshots, d => Assert.False(string.IsNullOrWhiteSpace(d.CorrelationId)));
        Assert.Single(snapshots.Select(d => d.CorrelationId).Distinct());
    }

    [Fact]
    public async Task StaleLease_RespectsConfiguredTtl_Boundary()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);
        SeedStageExecutions(factory, tenantId,
        [
            // Stale: older than the 300s TTL and expired.
            StageRow(tenantId, projectId, runId, "worker-a", now.AddSeconds(-301), now.AddSeconds(-10)),
            // Fresh by age: expired but younger than the TTL.
            StageRow(tenantId, projectId, runId, "worker-b", now.AddSeconds(-299), now.AddSeconds(-10)),
            // Fresh by heartbeat: older than the TTL but lease still valid.
            StageRow(tenantId, projectId, runId, "worker-c", now.AddSeconds(-600), now.AddMinutes(4)),
        ]);

        var service = new LeaseOrphanService(
            factory, OptionsFor(ttlSeconds: 300), new AllowAccessChecker(), NullLogger<LeaseOrphanService>.Instance);

        var stale = await service.GetStaleLeasesAsync(tenantId, userId);

        var staleLease = Assert.Single(stale);
        Assert.True(DateTimeOffset.UtcNow - staleLease.StartedAt >= TimeSpan.FromSeconds(300));
        Assert.StartsWith("run:", staleLease.OwnerHint, StringComparison.Ordinal);
        Assert.DoesNotContain("lease-token", staleLease.OwnerHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaleLease_PinsTtl_ViaOptionsOverride()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);
        SeedStageExecutions(factory, tenantId,
        [
            StageRow(tenantId, projectId, runId, "worker-a", now.AddSeconds(-61), now.AddSeconds(-5)),
        ]);

        var access = new AllowAccessChecker();
        var strict = new LeaseOrphanService(
            factory, OptionsFor(ttlSeconds: 60), access, NullLogger<LeaseOrphanService>.Instance);
        var lenient = new LeaseOrphanService(
            factory, OptionsFor(ttlSeconds: 3600), access, NullLogger<LeaseOrphanService>.Instance);

        Assert.Single(await strict.GetStaleLeasesAsync(tenantId, userId));
        Assert.Empty(await lenient.GetStaleLeasesAsync(tenantId, userId));
    }

    [Fact]
    public async Task EmptyDlq_ReturnsZeroDepth_WithNullOldestAge()
    {
        var service = new QueueDiagnosticsService(
            new FakeQueueBacklog([]),
            new AllowAccessChecker(),
            NullLogger<QueueDiagnosticsService>.Instance);

        var summary = await service.GetDlqSummaryAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(0, summary.Depth);
        Assert.Null(summary.OldestEnqueuedAt);
        Assert.Null(summary.OldestEntryAge);
        Assert.Empty(summary.TopReasons);
        Assert.False(string.IsNullOrWhiteSpace(summary.CorrelationId));
    }

    [Fact]
    public async Task DlqSummary_ReportsDepth_OldestAge_AndTopReasons()
    {
        var now = DateTimeOffset.UtcNow;
        var backlog = new FakeQueueBacklog(
        [
            new QueuedMessageSnapshot("queue://localhost/_error", "urn:message:ProviderTimeout", "{\"errorCode\":\"PROVIDER_TIMEOUT\"}", now.AddHours(-3)),
            new QueuedMessageSnapshot("queue://localhost/_error", "urn:message:ProviderTimeout", "{\"errorCode\":\"PROVIDER_TIMEOUT\"}", now.AddHours(-1)),
            new QueuedMessageSnapshot("queue://localhost/_error", "urn:message:Other", null, now.AddMinutes(-30)),
            new QueuedMessageSnapshot("queue://localhost/ai.provider", "urn:message:Work", null, now),
        ]);
        var service = new QueueDiagnosticsService(
            backlog, new AllowAccessChecker(), NullLogger<QueueDiagnosticsService>.Instance);

        var summary = await service.GetDlqSummaryAsync(Guid.NewGuid(), Guid.NewGuid());
        var depths = await service.GetQueueDepthsAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(3, summary.Depth);
        Assert.NotNull(summary.OldestEnqueuedAt);
        Assert.NotNull(summary.OldestEntryAge);
        Assert.True(summary.OldestEntryAge.Value >= TimeSpan.FromHours(2.9));
        Assert.Equal(2, summary.TopReasons.Count);
        Assert.Equal("PROVIDER_TIMEOUT", summary.TopReasons[0].Code);
        Assert.Equal(2, summary.TopReasons[0].Count);
        Assert.Contains(depths, d => d.Queue == "_error" && d.Depth == 3);
        Assert.Contains(depths, d => d.Queue == "control.orchestration" && d.Depth == 0);
    }

    [Fact]
    public void NormalizeQueueName_StripsBrokerPrefixes()
    {
        Assert.Equal("_error", QueueDiagnosticsService.NormalizeQueueName("queue://localhost/_error"));
        Assert.Equal("ai.provider", QueueDiagnosticsService.NormalizeQueueName("queue:ai.provider"));
        Assert.Equal("ai.provider", QueueDiagnosticsService.NormalizeQueueName("ai.provider"));
        Assert.Equal("unknown", QueueDiagnosticsService.NormalizeQueueName(null));
        Assert.Equal("unknown", QueueDiagnosticsService.NormalizeQueueName("   "));
    }

    [Fact]
    public async Task OrphanPagination_ClampsToMax_WithHasMoreCursor()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);
        SeedOrphans(factory, tenantId, count: 5);

        var service = new LeaseOrphanService(
            factory,
            MsOptions.Create(new DiagnosticsOptions { StaleLeaseTtlSeconds = 300, OrphanPageDefaultSize = 50, OrphanPageMaxSize = 2, MaxProviderExecutionSample = 500 }),
            new AllowAccessChecker(),
            NullLogger<LeaseOrphanService>.Instance);

        var first = await service.GetOrphanArtifactsAsync(tenantId, userId, pageSize: 500);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        var second = await service.GetOrphanArtifactsAsync(tenantId, userId, pageSize: 500, cursor: first.NextCursor);
        Assert.Equal(2, second.Items.Count);
        Assert.True(second.HasMore);

        var third = await service.GetOrphanArtifactsAsync(tenantId, userId, pageSize: 500, cursor: second.NextCursor);
        Assert.Single(third.Items);
        Assert.False(third.HasMore);
        Assert.Null(third.NextCursor);

        var ids = first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.ContentObjectId).ToList();
        Assert.Equal(5, ids.Distinct().Count());
    }

    [Fact]
    public async Task OrphanScan_ExcludesReferencedContent_AndStaysTenantScoped()
    {
        var tenantId = Guid.NewGuid();
        var neighborId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);

        var now = DateTimeOffset.UtcNow;
        var orphan = Guid.NewGuid();
        var owned = Guid.NewGuid();
        var neighborOrphan = Guid.NewGuid();
        using (BeginTenantScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<ContentObject>().AddRange(
                ContentRow(orphan, tenantId, now),
                ContentRow(owned, tenantId, now),
                ContentRow(neighborOrphan, neighborId, now));
            db.Set<Artifact>().Add(new Artifact(
                Guid.NewGuid(), tenantId, projectId, runId, StageType.Transcription,
                ArtifactType.Transcript, "v1", owned, "mock", "mock-1", null, null,
                ArtifactStatus.Committed, null, now));
            db.SaveChanges();
        }

        var service = new LeaseOrphanService(
            factory, OptionsFor(ttlSeconds: 300), new AllowAccessChecker(), NullLogger<LeaseOrphanService>.Instance);
        var page = await service.GetOrphanArtifactsAsync(tenantId, userId, pageSize: 50);

        var found = Assert.Single(page.Items);
        Assert.Equal(orphan, found.ContentObjectId);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task ProviderHealth_MarksNeverProbed_AsUnknown()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);

        var service = new ProviderHealthQueryService(
            factory, new FakeHealthTracker(), MsOptions.Create(new ProviderOptions()),
            OptionsFor(ttlSeconds: 300), new AllowAccessChecker(),
            NullLogger<ProviderHealthQueryService>.Instance);

        var snapshots = await service.GetProviderHealthAsync(tenantId, userId);

        Assert.Equal(Enum.GetValues<ProviderType>().Length, snapshots.Count);
        Assert.All(snapshots, s => Assert.Equal(ProviderHealthQueryService.StatusUnknown, s.Status));
        Assert.All(snapshots, s => Assert.Null(s.LatencyMsP95));
    }

    [Fact]
    public async Task ProviderHealth_AggregatesExecutions_AndRoutes()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);
        using (BeginTenantScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<ProviderExecution>().AddRange(
                ExecutionRow(Guid.NewGuid(), tenantId, projectId, runId, ProviderType.Mock, 100, OutcomeClass.Success, now.AddMinutes(-5)),
                ExecutionRow(Guid.NewGuid(), tenantId, projectId, runId, ProviderType.Mock, 200, OutcomeClass.Success, now.AddMinutes(-4)),
                ExecutionRow(Guid.NewGuid(), tenantId, projectId, runId, ProviderType.Mock, 300, OutcomeClass.ProviderTimeout, now.AddMinutes(-3)));
            db.SaveChanges();
        }

        var service = new ProviderHealthQueryService(
            factory, new FakeHealthTracker(), MsOptions.Create(new ProviderOptions()),
            OptionsFor(ttlSeconds: 300), new AllowAccessChecker(),
            NullLogger<ProviderHealthQueryService>.Instance);

        var snapshots = await service.GetProviderHealthAsync(tenantId, userId);
        var mock = snapshots.Single(s => s.Provider == ProviderType.Mock.ToString());

        Assert.Equal(ProviderHealthQueryService.StatusHealthy, mock.Status);
        Assert.Equal(300, mock.LatencyMsP95);
        Assert.Equal(1.0 / 3.0, mock.ErrorRate, precision: 5);
        Assert.NotNull(mock.LastSuccessAt);
        Assert.Contains("transcription", mock.ActiveRoutes);

        var routes = await service.GetProviderRoutesAsync(tenantId, userId);
        Assert.Contains(routes, r => r.Capability == "transcription" && r.Provider == "mock" && r.Enabled);
    }

    [Fact]
    public async Task ReviewBacklog_AggregatesStatus_Severity_AndPerProject()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);
        using (BeginTenantScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<ReviewItem>().AddRange(
                new ReviewItem(Guid.NewGuid(), tenantId, projectA, runId, ScopeType.Segment, "seg-1", Guid.NewGuid(), ReviewStatus.Open, "QC", null, now.AddHours(-5), now.AddHours(-5), null),
                new ReviewItem(Guid.NewGuid(), tenantId, projectA, runId, ScopeType.Segment, "seg-2", Guid.NewGuid(), ReviewStatus.Open, "QC", null, now.AddHours(-1), now.AddHours(-1), null),
                new ReviewItem(Guid.NewGuid(), tenantId, projectB, runId, ScopeType.Segment, "seg-3", Guid.NewGuid(), ReviewStatus.Approved, "QC", null, now.AddHours(-4), now.AddHours(-2), now.AddHours(-2)));
            db.Set<QualityResult>().AddRange(
                new QualityResult(Guid.NewGuid(), tenantId, projectA, runId, ScopeType.Segment, "seg-1", null, QualityStatus.ManualReviewRequired, "QC_SYNC", "Error", "sync", null, null, now.AddHours(-5)),
                new QualityResult(Guid.NewGuid(), tenantId, projectA, runId, ScopeType.Segment, "seg-2", null, QualityStatus.Pass, "QC_OK", "Info", "ok", null, null, now.AddHours(-1)));
            db.SaveChanges();
        }

        var service = new ReviewBacklogService(
            factory, new AllowAccessChecker(), NullLogger<ReviewBacklogService>.Instance);
        var backlog = await service.GetBacklogAsync(tenantId, userId);

        Assert.Equal(2, backlog.TotalOpen);
        Assert.Equal(2, backlog.ByStatus["Open"]);
        Assert.Equal(1, backlog.ByStatus["Approved"]);
        Assert.Equal(1, backlog.BySeverity["Error"]);
        Assert.DoesNotContain("Info", backlog.BySeverity.Keys);
        Assert.NotNull(backlog.OldestWaitingAt);
        Assert.NotNull(backlog.OldestWaitingReviewId);
        var entryA = backlog.PerProject.Single(p => p.ProjectId == projectA);
        Assert.Equal(2, entryA.OpenCount);
        Assert.Single(backlog.PerProject);
    }

    [Fact]
    public async Task WorkerHealth_ListsUnknown_WithNullHeartbeat_ForMissingRosterWorker()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);
        SeedStageExecutions(factory, tenantId,
        [
            StageRow(tenantId, projectId, runId, "worker-active", now.AddMinutes(-2), now.AddMinutes(3)),
            StageRow(tenantId, projectId, runId, "worker-stale", now.AddMinutes(-30), now.AddMinutes(-5)),
        ]);

        var options = MsOptions.Create(new DiagnosticsOptions
        {
            StaleLeaseTtlSeconds = 300,
            KnownWorkers = ["worker-active", "worker-stale", "worker-gone"],
        });
        var service = new WorkerHealthService(
            factory, options, new AllowAccessChecker(), NullLogger<WorkerHealthService>.Instance);

        var workers = await service.GetWorkerHealthAsync(tenantId, userId);

        Assert.Equal("Active", workers.Single(w => w.Worker == "worker-active").Status);
        Assert.Equal(1, workers.Single(w => w.Worker == "worker-active").ActiveJobs);
        Assert.NotNull(workers.Single(w => w.Worker == "worker-active").LastHeartbeatAt);
        Assert.Equal("Stale", workers.Single(w => w.Worker == "worker-stale").Status);
        var missing = workers.Single(w => w.Worker == "worker-gone");
        Assert.Equal("Unknown", missing.Status);
        Assert.Null(missing.LastHeartbeatAt);
        Assert.Equal(0, missing.ActiveJobs);
    }

    [Fact]
    public async Task Queries_AreReadOnly_WithZeroTrackedEntries()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var factory = CreateFactory();
        SeedMembership(factory, tenantId, userId);
        SeedStageExecutions(factory, tenantId,
        [
            StageRow(tenantId, projectId, runId, "worker-a", now.AddMinutes(-10), now.AddMinutes(-1)),
        ]);
        SeedOrphans(factory, tenantId, count: 2);
        using (BeginTenantScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<ProviderExecution>().Add(ExecutionRow(Guid.NewGuid(), tenantId, projectId, runId, ProviderType.Mock, 50, OutcomeClass.Success, now));
            db.Set<ReviewItem>().Add(new ReviewItem(Guid.NewGuid(), tenantId, projectId, runId, ScopeType.Run, "run", null, ReviewStatus.Open, "QC", null, now, now, null));
            db.SaveChanges();
        }

        factory.CreatedContexts.Clear();
        var access = new AllowAccessChecker();
        var options = OptionsFor(ttlSeconds: 300);
        var health = new ProviderHealthQueryService(
            factory, new FakeHealthTracker(), MsOptions.Create(new ProviderOptions()),
            options, access, NullLogger<ProviderHealthQueryService>.Instance);
        var leases = new LeaseOrphanService(
            factory, options, access, NullLogger<LeaseOrphanService>.Instance);
        var reviews = new ReviewBacklogService(
            factory, access, NullLogger<ReviewBacklogService>.Instance);
        var workers = new WorkerHealthService(
            factory, options, access, NullLogger<WorkerHealthService>.Instance);
        var queues = new QueueDiagnosticsService(
            new FakeQueueBacklog([new QueuedMessageSnapshot("queue://localhost/_error", "urn:message:X", null, now)]),
            access, NullLogger<QueueDiagnosticsService>.Instance);

        _ = await health.GetProviderHealthAsync(tenantId, userId);
        _ = await health.GetProviderRoutesAsync(tenantId, userId);
        _ = await leases.GetStaleLeasesAsync(tenantId, userId);
        _ = await leases.GetOrphanArtifactsAsync(tenantId, userId);
        _ = await reviews.GetBacklogAsync(tenantId, userId);
        _ = await workers.GetWorkerHealthAsync(tenantId, userId);
        _ = await queues.GetQueueDepthsAsync(tenantId, userId);
        _ = await queues.GetDlqSummaryAsync(tenantId, userId);

        Assert.NotEmpty(factory.CreatedContexts);
        foreach (var context in factory.CreatedContexts)
        {
            Assert.Empty(context.ChangeTracker.Entries());
        }
    }

    [Fact]
    public void DiagnosticsOptions_Defaults_PassValidation()
    {
        var validator = new DiagnosticsOptionsValidator();
        Assert.False(validator.Validate(MsOptions.DefaultName, new DiagnosticsOptions()).Failed);
        Assert.True(validator.Validate(MsOptions.DefaultName, new DiagnosticsOptions { StaleLeaseTtlSeconds = 0 }).Failed);
        Assert.True(validator.Validate(MsOptions.DefaultName, new DiagnosticsOptions { OrphanPageDefaultSize = 500 }).Failed);
    }

    private sealed class TestAppDbContext : AppDbContext
    {
        public TestAppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

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

    private sealed class TestContextFactory : IStageExecutionContextFactory, IDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<TestAppDbContext> _all = [];

        public TestContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public List<TestAppDbContext> CreatedContexts { get; } = [];

        DbContext IStageExecutionContextFactory.CreateDbContext()
        {
            var context = new TestAppDbContext(_options) { SealDispose = true };
            _all.Add(context);
            CreatedContexts.Add(context);
            return context;
        }

        public TestAppDbContext CreateSetup()
        {
            var context = new TestAppDbContext(_options);
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

    private sealed class AllowAccessChecker : IDiagnosticsAccessChecker
    {
        public Task RequireDiagnosticsViewerAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class DenyAccessChecker : IDiagnosticsAccessChecker
    {
        public Task RequireDiagnosticsViewerAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
        {
            throw new ForbiddenException($"{DiagnosticsAccessPolicy.ForbiddenMarker}: denied.");
        }
    }

    private sealed class FakeQueueBacklog : IQueueBacklogStore
    {
        private readonly IReadOnlyList<QueuedMessageSnapshot> _pending;

        public FakeQueueBacklog(IReadOnlyList<QueuedMessageSnapshot> pending)
        {
            _pending = pending;
        }

        public Task<IReadOnlyList<QueuedMessageSnapshot>> ListPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_pending);
        }
    }

    private sealed class FakeHealthTracker : IProviderHealthTracker
    {
        public bool IsHealthy(ProviderType provider) => true;

        public ProviderHealthState GetState(ProviderType provider)
        {
            return new ProviderHealthState(provider, true, true, false, 0.0, 0, false, null, true);
        }

        public void RecordSuccess(ProviderType provider)
        {
        }

        public void RecordFailure(ProviderType provider, OutcomeClass outcome)
        {
        }

        public void RecordRateLimited(ProviderType provider, TimeSpan? retryAfter = null)
        {
        }

        public void OpenCircuit(ProviderType provider)
        {
        }

        public void ResetCircuit(ProviderType provider)
        {
        }

        public void ValidateConfig(ProviderType provider)
        {
        }
    }

    private static TestContextFactory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("DiagnosticsReadTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
        return new TestContextFactory(options);
    }

    private static IDisposable BeginTenantScope(Guid tenantId)
    {
        return TenantContext.BeginScope(tenantId);
    }

    private static IOptions<DiagnosticsOptions> OptionsFor(int ttlSeconds)
    {
        return MsOptions.Create(new DiagnosticsOptions { StaleLeaseTtlSeconds = ttlSeconds });
    }

    private static void SeedMembership(TestContextFactory factory, Guid tenantId, Guid owner)
    {
        using (BeginTenantScope(tenantId))
        {
            using var db = factory.CreateSetup();
            var now = DateTimeOffset.UtcNow;
            db.Set<TenantUser>().Add(new TenantUser(
                owner, tenantId, "sub-" + owner.ToString("N"), "owner@example.com", "Owner", TenantUserStatus.Active, now, now));
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, Guid.NewGuid(), owner, ProjectRole.ProjectOwner, null, now));
            db.SaveChanges();
        }
    }

    private static StageExecution StageRow(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string owner,
        DateTimeOffset startedAt,
        DateTimeOffset leaseExpiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        return new StageExecution(
            Guid.NewGuid(), tenantId, projectId, runId, StageType.Transcription,
            ScopeType.Run, "run", null, 1, StageStatus.Running,
            owner, "lease-token-" + Guid.NewGuid().ToString("N"), 1, leaseExpiresAt,
            startedAt, null, null, "config", "snapshot", null, null, null, now, now);
    }

    private static void SeedStageExecutions(TestContextFactory factory, Guid tenantId, IReadOnlyList<StageExecution> rows)
    {
        using (BeginTenantScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<StageExecution>().AddRange(rows);
            db.SaveChanges();
        }
    }

    private static ContentObject ContentRow(Guid id, Guid tenantId, DateTimeOffset now)
    {
        return new ContentObject(
            id, tenantId, "hash-" + id.ToString("N"), new string('a', 64), 1024, "wav",
            "storage/" + id.ToString("N"), ContentObjectStatus.Committed, now, now);
    }

    private static void SeedOrphans(TestContextFactory factory, Guid tenantId, int count)
    {
        var now = DateTimeOffset.UtcNow;
        using (BeginTenantScope(tenantId))
        {
            using var db = factory.CreateSetup();
            for (var index = 0; index < count; index++)
            {
                db.Set<ContentObject>().Add(ContentRow(Guid.NewGuid(), tenantId, now.AddSeconds(index)));
            }

            db.SaveChanges();
        }
    }

    private static ProviderExecution ExecutionRow(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        ProviderType provider,
        long latencyMs,
        OutcomeClass outcome,
        DateTimeOffset createdAt)
    {
        return new ProviderExecution(
            id, tenantId, projectId, runId, null, provider, ProviderCapability.Transcription,
            "mock-1", null, null, null, null, 1, "request-hash", null, latencyMs,
            null, null, null, null, null, null, outcome, null, null, null, null, null, null, createdAt);
    }
}
