// Task 039C: diagnostics unit gap closure.
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Diagnostics;
using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DubbingPlatform.UnitTests.Diagnostics;

/// <summary>
/// Aggregation math behind the diagnostics read layer: lease-age boundaries,
/// queue-depth/percentage math with a zero total, DLQ retention counts, provider
/// status derivation, worker heartbeat staleness, review-backlog age, the
/// correlation-id round trip, and the authorization predicate including its
/// negative case. Pure helpers run directly; the authorization guard and the
/// entity-backed services run over an in-process InMemory
/// <see cref="AppDbContext"/> (no container, no network, no database server).
/// </summary>
public sealed class DiagnosticsAggregationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    // Expired lease, age well past the TTL => stale.
    [InlineData(-3600, 60, true)]
    // Age 0s with an expired lease: younger than the 60s TTL.
    [InlineData(0, 60, false)]
    // 59s: one second short of the TTL.
    [InlineData(-59, 60, false)]
    // Exactly the TTL: stale (>= is inclusive).
    [InlineData(-60, 60, true)]
    // Hours.
    [InlineData(-3600, 300, true)]
    [InlineData(-3600, 7200, false)]
    // Days.
    [InlineData(-86400, 60, true)]
    [InlineData(-86400, 86400, true)]
    // Future/negative age: clock skew must not mark a lease stale.
    [InlineData(30, 60, false)]
    public void IsStaleLease_ComputesAge_AgainstTtl(int ageSeconds, int ttlSeconds, bool expected)
    {
        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        var startedAt = Now.AddSeconds(ageSeconds);
        var expired = Now.AddSeconds(-1);

        Assert.Equal(expected, LeaseOrphanService.IsStaleLease(startedAt, expired, Now, ttl));
    }

    [Fact]
    public void IsStaleLease_TreatsMissingStartAsStale_AndFutureExpiryAsFresh()
    {
        var ttl = TimeSpan.FromSeconds(60);

        // No start time + expired lease => stale.
        Assert.True(LeaseOrphanService.IsStaleLease(null, Now, Now, ttl));
        // No start time + lease still valid => fresh.
        Assert.False(LeaseOrphanService.IsStaleLease(null, Now.AddSeconds(1), Now, ttl));
        // Missing start + expiry exactly at now counts as expired (not > now).
        Assert.True(LeaseOrphanService.IsStaleLease(null, Now, Now, ttl));
    }

    [Fact]
    public void BuildOwnerHint_CarriesIdsOnly_NeverLeaseTokens()
    {
        var runId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var hint = LeaseOrphanService.BuildOwnerHint(runId, projectId);

        Assert.Equal($"run:{runId:N};project:{projectId:N}", hint);
        Assert.DoesNotContain("lease", hint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 50)]
    [InlineData(-5, 50)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(500, 200)]
    public void NormalizePageSize_ClampsToConfiguredDefaultAndMax(int? requested, int expected)
    {
        var options = new DiagnosticsOptions { OrphanPageDefaultSize = 50, OrphanPageMaxSize = 200 };

        Assert.Equal(expected, LeaseOrphanService.NormalizePageSize(requested, options));
    }

    [Fact]
    public void NormalizePageSize_RejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => LeaseOrphanService.NormalizePageSize(10, null!));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData(" 7 ", 7)]
    [InlineData("-1", 0)]
    [InlineData("abc", 0)]
    [InlineData("1.5", 0)]
    public void ParseCursor_RestartsAtZero_ForMissingOrInvalidCursors(string? cursor, int expected)
    {
        Assert.Equal(expected, LeaseOrphanService.ParseCursor(cursor));
    }

    [Fact]
    public void CorrelationId_RoundTrips_AndGenerates32CharId_WhenOmitted()
    {
        // Attach: caller-supplied id is trimmed and echoed verbatim.
        Assert.Equal("corr-1234", DiagnosticsCorrelation.Normalize("  corr-1234  "));

        // Extract: a generated id round-trips unchanged and is 32 hex chars.
        var generated = DiagnosticsCorrelation.Normalize(null);
        Assert.Equal(32, generated.Length);
        Assert.Equal(generated, DiagnosticsCorrelation.Normalize(generated));
        Assert.True(Guid.TryParseExact(generated, "N", out _));

        // Blank and whitespace-only inputs generate fresh ids (never a blank id).
        Assert.NotEqual(string.Empty, DiagnosticsCorrelation.Normalize(string.Empty).Trim());
        Assert.NotEqual(string.Empty, DiagnosticsCorrelation.Normalize("   ").Trim());
        Assert.NotEqual(generated, DiagnosticsCorrelation.Normalize(" "));
    }

    [Theory]
    [InlineData(null, "urn:message:Thing", "Thing")]
    [InlineData("", "DubbingPlatform.Workers.Step", "Step")]
    [InlineData("{}", "a.b.C", "C")]
    [InlineData("not json", "queue/Work", "Work")]
    [InlineData("{\"reason\":\"  TIMEOUT  \"}", "urn:message:Other", "TIMEOUT")]
    [InlineData("{\"errorCode\":\"E1\"}", "urn:message:Other", "E1")]
    [InlineData("{\"error-code\":\"E2\"}", "urn:message:Other", "E2")]
    [InlineData("{\"exceptionType\":\"E3\"}", "urn:message:Other", "E3")]
    [InlineData("{\"exception-type\":\"E4\"}", "urn:message:Other", "E4")]
    [InlineData("{\"faultReason\":\"E5\"}", "urn:message:Other", "E5")]
    [InlineData("{\"fault-reason\":\"E6\"}", "urn:message:Other", "E6")]
    [InlineData("{\"REASON\":\"E7\"}", "urn:message:Other", "E7")]
    // A blank reason value falls through to the message-type fallback.
    [InlineData("{\"reason\":\"   \"}", "urn:message:Fallback", "Fallback")]
    // Non-string JSON values are ignored (never a leaked non-string).
    [InlineData("{\"reason\":42}", "urn:message:Numbered", "Numbered")]
    // Non-object JSON roots are ignored.
    [InlineData("[1,2,3]", "urn:message:Array", "Array")]
    public void DeadLetterReasons_ExtractsCodes_WithSafeFallback(string? headersJson, string? messageType, string expected)
    {
        Assert.Equal(expected, DeadLetterReasons.Extract(headersJson, messageType));
    }

    [Fact]
    public void DeadLetterReasons_ReturnsUnknown_WhenNothingUsable()
    {
        Assert.Equal("unknown", DeadLetterReasons.Extract(null, null));
        Assert.Equal("unknown", DeadLetterReasons.Extract("   ", "  "));

        // A trailing separator is not a separator: the raw name is kept rather
        // than collapsing to an empty (and unmatchable) reason code.
        Assert.Equal(":", DeadLetterReasons.Extract(null, ":"));
        Assert.Equal("Trailing:", DeadLetterReasons.Extract(null, "Trailing:"));
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData("ai.provider", "ai.provider")]
    [InlineData("queue:ai.provider", "ai.provider")]
    [InlineData("queue://localhost/ai.provider", "ai.provider")]
    [InlineData("queue://localhost/vhost/ai.provider", "ai.provider")]
    [InlineData("  _error  ", "_error")]
    [InlineData("/", "/")]
    [InlineData("queue:host:", "queue:host:")]
    [InlineData("queue://host/", "host/")]
    public void NormalizeQueueName_StripsBrokerPrefixes_AndReadsEmptyAsUnknown(string? destination, string expected)
    {
        Assert.Equal(expected, QueueDiagnosticsService.NormalizeQueueName(destination));
    }

    [Fact]
    public void BuildDepths_ZeroFillsFrozenTaxonomy_AndAppendsExtras()
    {
        IReadOnlyList<QueuedMessageSnapshot> pending =
        [
            new("queue://localhost/ai.provider", "urn:message:A", null, Now),
            new("queue://localhost/ai.provider", "urn:message:B", null, Now),
            new("queue:media.render", "urn:message:C", null, Now),
            new("queue://localhost/custom.lane", "urn:message:D", null, Now),
        ];

        var depths = QueueDiagnosticsService.BuildDepths(pending, "corr-depths");

        // The nine frozen taxonomy queues are always present, in taxonomy order.
        Assert.Equal(9, depths.Count(d => QueueNamesTaxonomy().Contains(d.Queue)));
        Assert.Equal(
            [
                "control.orchestration", "media.preparation", "media.render", "ai.provider",
                "ai.gpu", "export", "maintenance", "_skipped", "_error",
            ],
            depths.Take(9).Select(d => d.Queue).ToArray());

        // Zero-filled lanes read 0, never negative and never missing.
        var export = depths.Single(d => d.Queue == "export");
        Assert.Equal(0, export.Depth);
        Assert.All(depths, d => Assert.True(d.Depth >= 0));

        Assert.Equal(2, depths.Single(d => d.Queue == "ai.provider").Depth);
        Assert.Equal(1, depths.Single(d => d.Queue == "media.render").Depth);
        Assert.Equal(0, depths.Single(d => d.Queue == "_error").Depth);

        // Non-taxonomy lanes are appended after the frozen set, ordinal by name.
        Assert.Equal("custom.lane", depths[^1].Queue);
        Assert.Equal(1, depths[^1].Depth);

        // Total depth equals the number of pending messages (percentage base).
        Assert.Equal(pending.Count, depths.Sum(d => d.Depth));
        Assert.All(depths, d => Assert.Equal("corr-depths", d.CorrelationId));
    }

    [Fact]
    public void BuildDepths_EmptyBacklog_ReportsZeroForEveryTaxonomyLane()
    {
        var depths = QueueDiagnosticsService.BuildDepths([], "corr-empty");

        Assert.Equal(9, depths.Count);
        Assert.All(depths, d => Assert.Equal(0, d.Depth));

        // Zero total: any share-of-total percentage is defined as 0, never NaN.
        var total = depths.Sum(d => d.Depth);
        Assert.Equal(0, total);
        var share = total == 0 ? 0d : (double)depths.Max(d => d.Depth) / total;
        Assert.Equal(0d, share, 10);
    }

    [Fact]
    public void BuildDepths_GuardsNullAndBlankCorrelation()
    {
        Assert.Throws<ArgumentNullException>(() => QueueDiagnosticsService.BuildDepths(null!, "corr"));
        Assert.Throws<ArgumentException>(() => QueueDiagnosticsService.BuildDepths([], "  "));
    }

    [Fact]
    public void BuildSummary_EmptyDlq_YieldsZeroDepth_NullAge_NoThrow()
    {
        var summary = QueueDiagnosticsService.BuildSummary(
            [new QueuedMessageSnapshot("queue://localhost/ai.provider", "urn:message:Work", null, Now)],
            "corr-dlq",
            Now);

        Assert.Equal(0, summary.Depth);
        Assert.Null(summary.OldestEnqueuedAt);
        Assert.Null(summary.OldestEntryAge);
        Assert.Empty(summary.TopReasons);
        Assert.Equal("corr-dlq", summary.CorrelationId);
    }

    [Fact]
    public void BuildSummary_ComputesOldestAge_AndTopReasonBreakdown()
    {
        var pending = new List<QueuedMessageSnapshot>
        {
            Dead("{\"errorCode\":\"PROVIDER_TIMEOUT\"}", Now.AddHours(-3)),
            Dead("{\"errorCode\":\"PROVIDER_TIMEOUT\"}", Now.AddHours(-1)),
            Dead("{\"reason\":\"QUOTA\"}", Now.AddMinutes(-20)),
            Dead(null, Now),
            new("queue://localhost/ai.provider", "urn:message:Live", null, Now),
        };

        var summary = QueueDiagnosticsService.BuildSummary(pending, "corr-dlq", Now);

        Assert.Equal(4, summary.Depth);
        Assert.Equal(Now.AddHours(-3), summary.OldestEnqueuedAt);
        Assert.Equal(TimeSpan.FromHours(3), summary.OldestEntryAge);
        // Descending count, then ordinal by code.
        Assert.Equal("PROVIDER_TIMEOUT", summary.TopReasons[0].Code);
        Assert.Equal(2, summary.TopReasons[0].Count);
        Assert.Equal("QUOTA", summary.TopReasons[1].Code);
        Assert.Equal(1, summary.TopReasons[1].Count);
        // The live lane never contributes to the DLQ.
        Assert.DoesNotContain(summary.TopReasons, r => r.Code == "Live");
    }

    [Fact]
    public void BuildSummary_CapsReasonBreakdownAtTen()
    {
        var pending = new List<QueuedMessageSnapshot>();
        for (var index = 0; index < 15; index++)
        {
            pending.Add(Dead($"{{\"reason\":\"CODE_{index:00}\"}}", Now));
        }

        var summary = QueueDiagnosticsService.BuildSummary(pending, "corr-cap", Now);

        Assert.Equal(15, summary.Depth);
        Assert.Equal(QueueDiagnosticsService.MaxReasonBreakdown, summary.TopReasons.Count);
        Assert.All(summary.TopReasons, r => Assert.Equal(1, r.Count));
        // Equal counts: ordinal tiebreak keeps CODE_00..CODE_09.
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"CODE_{i:00}").ToArray(), summary.TopReasons.Select(r => r.Code).ToArray());
    }

    [Fact]
    public void BuildSummary_IgnoresMessagesWithoutEnqueueTime_ForOldestAge()
    {
        var pending = new List<QueuedMessageSnapshot>
        {
            Dead("{}", null),
            Dead("{}", Now.AddMinutes(-30)),
        };

        var summary = QueueDiagnosticsService.BuildSummary(pending, "corr", Now);

        Assert.Equal(2, summary.Depth);
        Assert.Equal(Now.AddMinutes(-30), summary.OldestEnqueuedAt);
        Assert.Equal(TimeSpan.FromMinutes(30), summary.OldestEntryAge);
    }

    [Theory]
    // No samples and no probe calls => Unknown, even with a closed, healthy tracker.
    [InlineData(false, 0, true, "Unknown", "Healthy")]
    [InlineData(true, 0, true, "Unknown", "Down")]
    // An open circuit is always Down.
    [InlineData(true, 3, true, "Down", "Down")]
    [InlineData(true, 3, false, "Down", "Down")]
    // Closed and healthy => Healthy; closed and unhealthy => Degraded.
    [InlineData(false, 3, true, "Healthy", "Healthy")]
    [InlineData(false, 3, false, "Degraded", "Degraded")]
    public void ResolveStatus_DerivesProviderStatus_WithSafeUnknownDefault(
        bool circuitOpen,
        int totalCalls,
        bool isHealthy,
        string expectedWithoutSamples,
        string expectedWithSamples)
    {
        var state = HealthState(circuitOpen: circuitOpen, totalCalls: totalCalls, isHealthy: isHealthy);

        Assert.Equal(expectedWithoutSamples, ProviderHealthQueryService.ResolveStatus(hasSamples: false, state));
        Assert.Equal(expectedWithSamples, ProviderHealthQueryService.ResolveStatus(hasSamples: true, state));
    }

    [Fact]
    public void ResolveStatus_RejectsNullTrackerState()
    {
        Assert.Throws<ArgumentNullException>(() => ProviderHealthQueryService.ResolveStatus(true, null!));
    }

    [Fact]
    public void ComputeP95_UsesNearestRank_AndNullsEmptySamples()
    {
        Assert.Null(ProviderHealthQueryService.ComputeP95([]));

        var single = ProviderHealthQueryService.ComputeP95([42]);
        Assert.NotNull(single);
        Assert.Equal(42d, single!.Value, precision: 10);

        // Unsorted input is ordered before ranking.
        var unsorted = ProviderHealthQueryService.ComputeP95([300, 100, 200]);
        Assert.NotNull(unsorted);
        Assert.Equal(300d, unsorted!.Value, precision: 10);

        var hundred = ProviderHealthQueryService.ComputeP95(Enumerable.Range(1, 100).Select(i => (long)i).ToList());
        Assert.NotNull(hundred);
        Assert.Equal(95d, hundred!.Value, precision: 10);

        Assert.Throws<ArgumentNullException>(() => ProviderHealthQueryService.ComputeP95(null!));
    }

    [Fact]
    public void BuildRoutes_OrdersByCapability_AndHonoursEnabledFlags()
    {
        var providers = new ProviderOptions
        {
            RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["translation"] = ["Mock", " OpenAI ", "  "],
                ["transcription"] = ["mock"],
            },
            Enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["MOCK"] = false },
        };

        var routes = ProviderHealthQueryService.BuildRoutes(providers, "corr-routes");

        // Two capabilities; the blank provider entry in "translation" is dropped.
        Assert.Equal(3, routes.Count);
        // Capabilities are ordinal-sorted: "transcription" precedes "translation".
        Assert.Equal("transcription", routes[0].Capability);
        Assert.Equal("translation", routes[1].Capability);
        Assert.Equal("translation", routes[2].Capability);

        // Priority index is the position within the capability list.
        Assert.Equal(0, routes[0].Priority);
        Assert.Equal(0, routes[1].Priority);
        Assert.Equal(1, routes[2].Priority);

        Assert.Equal("mock", routes[0].Provider);
        Assert.Equal("Mock", routes[1].Provider);
        Assert.Equal("OpenAI", routes[2].Provider);

        // Case-insensitive enablement lookup; an absent flag defaults to enabled.
        Assert.False(routes[0].Enabled);
        Assert.False(routes[1].Enabled);
        Assert.True(routes[2].Enabled);
        Assert.All(routes, r => Assert.Equal("corr-routes", r.CorrelationId));
    }

    [Fact]
    public void BuildRoutes_HandlesNullDictionaries_AndGuardsArguments()
    {
        var providers = new ProviderOptions
        {
            RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["tts"] = ["mock"] },
            Enabled = null!,
        };

        var routes = ProviderHealthQueryService.BuildRoutes(providers, "corr");
        var only = Assert.Single(routes);
        Assert.True(only.Enabled);

        Assert.Throws<ArgumentNullException>(() => ProviderHealthQueryService.BuildRoutes(null!, "corr"));
        Assert.Throws<ArgumentException>(() => ProviderHealthQueryService.BuildRoutes(new ProviderOptions(), " "));
    }

    [Theory]
    [InlineData(0, 0, "Unknown")]
    [InlineData(3, 0, "Stale")]
    [InlineData(3, 1, "Active")]
    [InlineData(0, 2, "Active")]
    public void ResolveStatus_DerivesWorkerStatus_AndKeepsUnknownAsSafeDefault(
        int runningLeases,
        int unexpiredRunningLeases,
        string expected)
    {
        Assert.Equal(expected, WorkerHealthService.ResolveStatus(runningLeases, unexpiredRunningLeases));
    }

    [Fact]
    public void BuildWorkers_SplitsActiveStaleAndRosterUnknown_AndSkipsBlankOwners()
    {
        var executions = new List<(string LeaseOwner, StageStatus Status, DateTimeOffset LeaseExpiresAt, DateTimeOffset UpdatedAt)>
        {
            (" worker-active ", StageStatus.Running, Now.AddMinutes(5), Now.AddMinutes(-1)),
            ("worker-active", StageStatus.Running, Now.AddMinutes(-1), Now.AddMinutes(-2)),
            ("   ", StageStatus.Running, Now.AddMinutes(5), Now),
            ("worker-stale", StageStatus.Running, Now.AddMinutes(-5), Now.AddMinutes(-30)),
            ("worker-stale", StageStatus.Completed, Now.AddMinutes(5), Now.AddMinutes(-40)),
        };

        var workers = WorkerHealthService.BuildWorkers(
            executions,
            ["worker-stale", "worker-gone", " worker-gone ", "  "],
            "corr-workers",
            Now);

        // Blank owners never appear; trimmed names group together.
        Assert.Equal(3, workers.Count);
        Assert.DoesNotContain(workers, w => w.Worker == "worker-active ");

        var active = workers.Single(w => w.Worker == "worker-active");
        Assert.Equal(WorkerHealthService.StatusActive, active.Status);
        Assert.Equal(2, active.ActiveJobs);
        // LastHeartbeatAt is the max UpdatedAt over the group.
        Assert.Equal(Now.AddMinutes(-1), active.LastHeartbeatAt);
        Assert.Null(active.Version);

        var stale = workers.Single(w => w.Worker == "worker-stale");
        Assert.Equal(WorkerHealthService.StatusStale, stale.Status);
        Assert.Equal(1, stale.ActiveJobs);
        Assert.Equal(Now.AddMinutes(-30), stale.LastHeartbeatAt);

        // Roster entries with no runtime state read Unknown with a null heartbeat.
        var gone = workers.Single(w => w.Worker == "worker-gone");
        Assert.Equal(WorkerHealthService.StatusUnknown, gone.Status);
        Assert.Null(gone.LastHeartbeatAt);
        Assert.Equal(0, gone.ActiveJobs);

        Assert.All(workers, w => Assert.Equal("corr-workers", w.CorrelationId));
    }

    [Fact]
    public void BuildWorkers_IsOrderedByWorkerName_AndGuardsArguments()
    {
        var executions = new List<(string, StageStatus, DateTimeOffset, DateTimeOffset)>
        {
            ("worker-b", StageStatus.Running, Now.AddMinutes(5), Now),
            ("worker-a", StageStatus.Running, Now.AddMinutes(5), Now),
        };

        var workers = WorkerHealthService.BuildWorkers(executions, [], "corr", Now);
        Assert.Equal(["worker-a", "worker-b"], workers.Select(w => w.Worker).ToArray());

        Assert.Throws<ArgumentNullException>(() => WorkerHealthService.BuildWorkers(null!, [], "corr", Now));
        Assert.Throws<ArgumentNullException>(() => WorkerHealthService.BuildWorkers([], null!, "corr", Now));
        Assert.Throws<ArgumentException>(() => WorkerHealthService.BuildWorkers([], [], "  ", Now));
    }

    [Fact]
    public void BuildBacklog_CountsStatuses_Severities_AndOldestWaitingAge()
    {
        var oldest = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var reviews = new List<(Guid Id, Guid ProjectId, ReviewStatus Status, DateTimeOffset CreatedAt)>
        {
            (oldest, projectA, ReviewStatus.Open, Now.AddHours(-5)),
            (Guid.NewGuid(), projectA, ReviewStatus.Open, Now.AddHours(-1)),
            (Guid.NewGuid(), projectB, ReviewStatus.Approved, Now.AddHours(-4)),
            (Guid.NewGuid(), projectB, ReviewStatus.Rejected, Now),
        };
        var severities = new List<string> { "Error", "Error", "Warning", "   " };

        var backlog = ReviewBacklogService.BuildBacklog(reviews, severities, "corr-reviews");

        Assert.Equal(2, backlog.TotalOpen);
        // Every enum value is present, zero-filled.
        Assert.Equal(Enum.GetValues<ReviewStatus>().Length, backlog.ByStatus.Count);
        Assert.Equal(2, backlog.ByStatus[nameof(ReviewStatus.Open)]);
        Assert.Equal(1, backlog.ByStatus[nameof(ReviewStatus.Approved)]);
        Assert.Equal(1, backlog.ByStatus[nameof(ReviewStatus.Rejected)]);
        Assert.Equal(0, backlog.ByStatus[nameof(ReviewStatus.Requeued)]);
        Assert.Equal(0, backlog.ByStatus[nameof(ReviewStatus.ResolvedWithEdit)]);

        // Blank severities collapse into the documented "unknown" bucket.
        Assert.Equal(2, backlog.BySeverity["Error"]);
        Assert.Equal(1, backlog.BySeverity["Warning"]);
        Assert.Equal(1, backlog.BySeverity["unknown"]);

        // Oldest waiting: the review age source.
        Assert.Equal(Now.AddHours(-5), backlog.OldestWaitingAt);
        Assert.Equal(oldest, backlog.OldestWaitingReviewId);
        Assert.Equal(TimeSpan.FromHours(5), Now - backlog.OldestWaitingAt!.Value);

        // Per-project breakdown counts only open reviews, ordered oldest first.
        Assert.Single(backlog.PerProject);
        var entryA = backlog.PerProject.Single(p => p.ProjectId == projectA);
        Assert.Equal(2, entryA.OpenCount);
        Assert.Equal(Now.AddHours(-5), entryA.OldestWaitingAt);
        // The project with no open reviews is absent.
        Assert.DoesNotContain(backlog.PerProject, p => p.ProjectId == projectB);
        Assert.Equal(Now.AddHours(-5), backlog.PerProject[0].OldestWaitingAt);

        Assert.Equal("corr-reviews", backlog.CorrelationId);
    }

    [Fact]
    public void BuildBacklog_NoOpenReviews_YieldsNullAge_AndTieBreaksPerProjectById()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        if (projectA.CompareTo(projectB) > 0)
        {
            (projectA, projectB) = (projectB, projectA);
        }

        var reviews = new List<(Guid Id, Guid ProjectId, ReviewStatus Status, DateTimeOffset CreatedAt)>
        {
            (Guid.NewGuid(), projectA, ReviewStatus.Approved, Now),
            (Guid.NewGuid(), projectB, ReviewStatus.Approved, Now),
        };

        var backlog = ReviewBacklogService.BuildBacklog(reviews, [], "corr-none");

        Assert.Equal(0, backlog.TotalOpen);
        Assert.Null(backlog.OldestWaitingAt);
        Assert.Null(backlog.OldestWaitingReviewId);
        Assert.Empty(backlog.PerProject);
        Assert.Empty(backlog.BySeverity);
    }

    [Fact]
    public void BuildBacklog_TreatsUnknownStatusAsItsOwnBucket_AndGuardsArguments()
    {
        var reviews = new List<(Guid Id, Guid ProjectId, ReviewStatus Status, DateTimeOffset CreatedAt)>
        {
            (Guid.NewGuid(), Guid.NewGuid(), (ReviewStatus)999, Now),
        };

        var backlog = ReviewBacklogService.BuildBacklog(reviews, ["  Info  "], "corr");

        // An out-of-range enum value never throws; it lands in its own bucket.
        Assert.Equal(1, backlog.ByStatus["999"]);
        // Severities are trimmed.
        Assert.Equal(1, backlog.BySeverity["Info"]);

        Assert.Throws<ArgumentNullException>(() => ReviewBacklogService.BuildBacklog(null!, [], "corr"));
        Assert.Throws<ArgumentNullException>(() => ReviewBacklogService.BuildBacklog([], null!, "corr"));
        Assert.Throws<ArgumentException>(() => ReviewBacklogService.BuildBacklog([], [], " "));
    }

    [Fact]
    public void OptionsValidator_AcceptsDefaults_AndRejectsEveryOutOfRangeBudget()
    {
        var validator = new DiagnosticsOptionsValidator();

        Assert.True(validator.Validate(MsOptions.DefaultName, new DiagnosticsOptions()).Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(300), new DiagnosticsOptions().StaleLeaseTtl);
        Assert.Equal(300, new DiagnosticsOptions().StaleLeaseTtlSeconds);
        Assert.Equal(50, new DiagnosticsOptions().OrphanPageDefaultSize);
        Assert.Equal(200, new DiagnosticsOptions().OrphanPageMaxSize);
        Assert.Equal(500, new DiagnosticsOptions().MaxProviderExecutionSample);
        Assert.Empty(new DiagnosticsOptions().KnownWorkers);
        Assert.Equal("Diagnostics", DiagnosticsOptions.SectionName);

        AssertFail(validator, new DiagnosticsOptions { StaleLeaseTtlSeconds = 0 });
        AssertFail(validator, new DiagnosticsOptions { StaleLeaseTtlSeconds = 86401 });
        AssertFail(validator, new DiagnosticsOptions { OrphanPageDefaultSize = 0 });
        AssertFail(validator, new DiagnosticsOptions { OrphanPageDefaultSize = 201 });
        AssertFail(validator, new DiagnosticsOptions { OrphanPageMaxSize = 0 });
        AssertFail(validator, new DiagnosticsOptions { OrphanPageMaxSize = 1001 });
        // Default size must not exceed max size.
        AssertFail(validator, new DiagnosticsOptions { OrphanPageDefaultSize = 100, OrphanPageMaxSize = 50 });
        AssertFail(validator, new DiagnosticsOptions { MaxProviderExecutionSample = 9 });
        AssertFail(validator, new DiagnosticsOptions { MaxProviderExecutionSample = 10001 });
        AssertFail(validator, new DiagnosticsOptions { KnownWorkers = null! });
        AssertFail(validator, new DiagnosticsOptions { KnownWorkers = ["ok", "  "] });

        Assert.Throws<ArgumentNullException>(() => validator.Validate(MsOptions.DefaultName, null!));
    }

    [Fact]
    public void OptionsValidator_AcceptsBoundaryValues()
    {
        var validator = new DiagnosticsOptionsValidator();

        Assert.True(validator.Validate(MsOptions.DefaultName, new DiagnosticsOptions
        {
            StaleLeaseTtlSeconds = 1,
            OrphanPageDefaultSize = 200,
            OrphanPageMaxSize = 200,
            MaxProviderExecutionSample = 10,
            KnownWorkers = ["worker-a"],
        }).Succeeded);
    }

    [Theory]
    [InlineData("TenantAdmin", "", true)]
    [InlineData("Operator", "", true)]
    [InlineData("Service", "", true)]
    [InlineData("ProjectViewer", "diagnostics.view", true)]
    [InlineData("ProjectViewer", "  diagnostics.view  ", true)]
    [InlineData("ProjectViewer", "", false)]
    [InlineData("ProjectViewer", "diagnostics.read", false)]
    [InlineData("ProjectViewer", "project.read", false)]
    [InlineData("", "", false)]
    [InlineData("Diagnostics.View", "", false)]
    public void IsElevatedViewer_GrantsOnlyElevatedRolesOrDiagnosticsPermission(
        string role,
        string permission,
        bool expected)
    {
        // The role and the permission are independent grant paths.
        Assert.Equal(expected, DiagnosticsAccessPolicy.IsElevatedViewer([role], [permission]));
        Assert.Equal(
            !string.IsNullOrWhiteSpace(permission) && permission.Trim() == DiagnosticsAccessPolicy.ViewPermission,
            DiagnosticsAccessPolicy.IsElevatedViewer([], [permission]));
    }

    [Fact]
    public void IsElevatedViewer_IgnoresBlankEntries_AndGuardsNullArguments()
    {
        Assert.True(DiagnosticsAccessPolicy.IsElevatedViewer(["  ", "  "], ["diagnostics.view"]));
        Assert.False(DiagnosticsAccessPolicy.IsElevatedViewer(["  "], ["  ", ""]));
        // A permission that only shares a prefix never grants access.
        Assert.False(DiagnosticsAccessPolicy.IsElevatedViewer([], ["diagnostics.viewer"]));

        Assert.Throws<ArgumentNullException>(() => DiagnosticsAccessPolicy.IsElevatedViewer(null!, []));
        Assert.Throws<ArgumentNullException>(() => DiagnosticsAccessPolicy.IsElevatedViewer([], null!));
    }

    [Fact]
    public async Task ReaderWithoutDiagnosticsPermission_IsDenied_AndExposesNoRowData()
    {
        var tenantId = Guid.NewGuid();
        var reader = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        using var factory = CreateFactory();

        // The reader is an active tenant user holding only a viewer membership.
        SeedUser(factory, tenantId, reader, TenantUserStatus.Active);
        SeedMembership(factory, tenantId, projectId, reader, ProjectRole.ProjectViewer);
        // Diagnostics rows exist for this tenant: a denied call must expose none.
        SeedStageExecutions(factory, tenantId, [StageRow(tenantId, projectId, runId, "worker-secret-name", Now.AddHours(-2), Now.AddMinutes(-1))]);

        var checker = new DiagnosticsAccessChecker(factory, NullLogger<DiagnosticsAccessChecker>.Instance);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => checker.RequireDiagnosticsViewerAsync(tenantId, reader));

        Assert.Equal(ErrorCodes.Forbidden, exception.ErrorCode);
        Assert.Contains(DiagnosticsAccessPolicy.ForbiddenMarker, exception.Message, StringComparison.Ordinal);
        // No row data leaks through the denial: no worker name, lease, or project id.
        Assert.DoesNotContain("worker-secret-name", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(projectId.ToString("N"), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lease", exception.Message, StringComparison.OrdinalIgnoreCase);

        // A denied call returns no data and leaves the backing rows untouched.
        var service = new WorkerHealthService(
            factory,
            MsOptions.Create(new DiagnosticsOptions { KnownWorkers = ["worker-secret-name"] }),
            new DenyAccessChecker(),
            NullLogger<WorkerHealthService>.Instance);
        await Assert.ThrowsAsync<ForbiddenException>(() => service.GetWorkerHealthAsync(tenantId, reader));
        using (TenantContext.BeginScope(tenantId))
        {
            using var verify = factory.CreateSetup();
            Assert.Equal(1, verify.Set<StageExecution>().AsNoTracking().Count());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessChecker_RejectsEmptyIds_AndGrantsActiveOwnerOnly(bool useEmptyUser)
    {
        var tenantId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedUser(factory, tenantId, owner, TenantUserStatus.Active);
        SeedMembership(factory, tenantId, Guid.NewGuid(), owner, ProjectRole.ProjectOwner);

        var checker = new DiagnosticsAccessChecker(factory, NullLogger<DiagnosticsAccessChecker>.Instance);

        if (useEmptyUser)
        {
            await Assert.ThrowsAsync<DomainException>(() => checker.RequireDiagnosticsViewerAsync(tenantId, Guid.Empty));
        }
        else
        {
            await Assert.ThrowsAsync<DomainException>(() => checker.RequireDiagnosticsViewerAsync(Guid.Empty, owner));
        }

        await checker.RequireDiagnosticsViewerAsync(tenantId, owner);
    }

    [Fact]
    public async Task AccessChecker_DeniesDisabledAndCrossTenantUsers()
    {
        var tenantId = Guid.NewGuid();
        var neighbor = Guid.NewGuid();
        var disabled = Guid.NewGuid();
        using var factory = CreateFactory();

        SeedUser(factory, tenantId, disabled, TenantUserStatus.Disabled);
        SeedMembership(factory, tenantId, Guid.NewGuid(), disabled, ProjectRole.ProjectOwner);
        SeedUser(factory, neighbor, Guid.NewGuid(), TenantUserStatus.Active);

        var checker = new DiagnosticsAccessChecker(factory, NullLogger<DiagnosticsAccessChecker>.Instance);

        await Assert.ThrowsAsync<ForbiddenException>(() => checker.RequireDiagnosticsViewerAsync(tenantId, disabled));
        await Assert.ThrowsAsync<ForbiddenException>(() => checker.RequireDiagnosticsViewerAsync(tenantId, Guid.NewGuid()));
        // An active owner in another tenant never grants access here.
        using (TenantContext.BeginScope(neighbor))
        {
            using var db = factory.CreateSetup();
            var ownerId = db.Set<TenantUser>().AsNoTracking().Select(u => u.Id).First();
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), neighbor, Guid.NewGuid(), ownerId, ProjectRole.ProjectOwner, null, Now));
            db.SaveChanges();
            await Assert.ThrowsAsync<ForbiddenException>(() => checker.RequireDiagnosticsViewerAsync(tenantId, ownerId));
        }
    }

    [Fact]
    public async Task AccessChecker_GuardsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new DiagnosticsAccessChecker(null!, NullLogger<DiagnosticsAccessChecker>.Instance));

        using var factory = CreateFactory();
        Assert.Throws<ArgumentNullException>(() => new DiagnosticsAccessChecker(factory, null!));
    }

    [Fact]
    public async Task StaleLeases_ReportAgeRelativeToExplicitNow_AndFallbackHeartbeat()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedUser(factory, tenantId, userId, TenantUserStatus.Active);
        SeedMembership(factory, tenantId, projectId, userId, ProjectRole.ProjectOwner);

        // Lease with a start time 5 minutes old, expired 1 minute ago (TTL 60s).
        SeedStageExecutions(factory, tenantId, [StageRow(tenantId, projectId, runId, "worker-a", Now.AddMinutes(-5), Now.AddMinutes(-1))]);

        var service = new LeaseOrphanService(
            factory,
            MsOptions.Create(new DiagnosticsOptions { StaleLeaseTtlSeconds = 60 }),
            new AllowAccessChecker(),
            NullLogger<LeaseOrphanService>.Instance,
            new FixedTimeProvider(Now));

        var stale = await service.GetStaleLeasesAsync(tenantId, userId, "corr-stale");

        var lease = Assert.Single(stale);
        Assert.Equal("corr-stale", lease.CorrelationId);
        Assert.Equal(nameof(StageStatus.Running), lease.Status);
        Assert.Equal(Now.AddMinutes(-5), lease.StartedAt);
        Assert.Equal(Now.AddMinutes(-1), lease.LeaseExpiresAt);
        // The heartbeat proxy is UpdatedAt, which the seed set to Now.
        Assert.Equal(Now, lease.LastHeartbeatAt);
        Assert.Equal(LeaseOrphanService.BuildOwnerHint(runId, projectId), lease.OwnerHint);
    }

    [Fact]
    public async Task StaleLeases_ExcludeRunning_Leases_StillWithinTtl()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedUser(factory, tenantId, userId, TenantUserStatus.Active);
        SeedMembership(factory, tenantId, projectId, userId, ProjectRole.ProjectOwner);
        SeedStageExecutions(factory, tenantId, [StageRow(tenantId, projectId, runId, "worker-b", Now.AddSeconds(-59), Now.AddSeconds(-1))]);

        var service = new LeaseOrphanService(
            factory,
            MsOptions.Create(new DiagnosticsOptions { StaleLeaseTtlSeconds = 60 }),
            new AllowAccessChecker(),
            NullLogger<LeaseOrphanService>.Instance,
            new FixedTimeProvider(Now));

        Assert.Empty(await service.GetStaleLeasesAsync(tenantId, userId));
    }

    [Fact]
    public async Task ProviderHealth_ReportsZeroErrorRate_WhenThereAreNoSamples()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedUser(factory, tenantId, userId, TenantUserStatus.Active);
        SeedMembership(factory, tenantId, Guid.NewGuid(), userId, ProjectRole.ProjectOwner);

        var service = new ProviderHealthQueryService(
            factory,
            new NeverProbedHealthTracker(),
            MsOptions.Create(new ProviderOptions { RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal), Enabled = new Dictionary<string, bool>(StringComparer.Ordinal) }),
            MsOptions.Create(new DiagnosticsOptions()),
            new AllowAccessChecker(),
            NullLogger<ProviderHealthQueryService>.Instance);

        var snapshots = await service.GetProviderHealthAsync(tenantId, userId, "corr-providers");

        Assert.Equal(Enum.GetValues<ProviderType>().Length, snapshots.Count);
        Assert.All(snapshots, s =>
        {
            // Zero total samples => 0% error rate, not a division by zero.
            Assert.Equal(0.0, s.ErrorRate, precision: 10);
            Assert.Equal(ProviderHealthQueryService.StatusUnknown, s.Status);
            Assert.Null(s.LatencyMsP95);
            Assert.Null(s.LastSuccessAt);
            Assert.Equal(ProviderHealthQueryService.CircuitClosed, s.CircuitBreakerState);
            Assert.Empty(s.ActiveRoutes);
            Assert.Equal("corr-providers", s.CorrelationId);
        });
    }

    [Fact]
    public async Task ProviderHealth_ReportsOpenCircuit_AsDown()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedUser(factory, tenantId, userId, TenantUserStatus.Active);
        SeedMembership(factory, tenantId, Guid.NewGuid(), userId, ProjectRole.ProjectOwner);

        var service = new ProviderHealthQueryService(
            factory,
            new OpenCircuitHealthTracker(),
            MsOptions.Create(new ProviderOptions
            {
                RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["transcription"] = ["mock"] },
                Enabled = new Dictionary<string, bool>(StringComparer.Ordinal) { ["mock"] = true },
            }),
            MsOptions.Create(new DiagnosticsOptions()),
            new AllowAccessChecker(),
            NullLogger<ProviderHealthQueryService>.Instance);

        var snapshots = await service.GetProviderHealthAsync(tenantId, userId);

        var mock = snapshots.Single(s => s.Provider == ProviderType.Mock.ToString());
        Assert.Equal(ProviderHealthQueryService.StatusDown, mock.Status);
        Assert.Equal(ProviderHealthQueryService.CircuitOpen, mock.CircuitBreakerState);
        Assert.Equal(["transcription"], mock.ActiveRoutes);

        var routes = await service.GetProviderRoutesAsync(tenantId, userId, "corr-routes");
        var route = Assert.Single(routes);
        Assert.Equal("corr-routes", route.CorrelationId);
        Assert.True(route.Enabled);
    }

    [Fact]
    public async Task ReviewBacklog_ReportsOldestWaitingAge_OverEntityRows()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var oldestId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedUser(factory, tenantId, userId, TenantUserStatus.Active);
        SeedMembership(factory, tenantId, projectId, userId, ProjectRole.ProjectOwner);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<ReviewItem>().AddRange(
                new ReviewItem(oldestId, tenantId, projectId, runId, ScopeType.Segment, "seg-1", Guid.NewGuid(), ReviewStatus.Open, "QC", null, Now.AddHours(-3), Now.AddHours(-3), null),
                new ReviewItem(Guid.NewGuid(), tenantId, projectId, runId, ScopeType.Segment, "seg-2", Guid.NewGuid(), ReviewStatus.Open, "QC", null, Now, Now, null));
            db.Set<QualityResult>().Add(new QualityResult(
                Guid.NewGuid(), tenantId, projectId, runId, ScopeType.Segment, "seg-1", null,
                QualityStatus.ManualReviewRequired, "QC_SYNC", "Warning", "sync", null, null, Now.AddHours(-3)));
            db.SaveChanges();
        }

        var service = new ReviewBacklogService(factory, new AllowAccessChecker(), NullLogger<ReviewBacklogService>.Instance);
        var backlog = await service.GetBacklogAsync(tenantId, userId, "corr-backlog");

        Assert.Equal(2, backlog.TotalOpen);
        Assert.Equal(Now.AddHours(-3), backlog.OldestWaitingAt);
        Assert.Equal(oldestId, backlog.OldestWaitingReviewId);
        Assert.Equal(TimeSpan.FromHours(3), Now - backlog.OldestWaitingAt!.Value);
        Assert.Equal(1, backlog.BySeverity["Warning"]);
        // Passing QC results are excluded from the severity breakdown.
        Assert.Single(backlog.PerProject);
    }

    [Fact]
    public async Task WorkerHealth_ReportsHeartbeatStaleness_AtLeaseExpiryBoundary()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedUser(factory, tenantId, userId, TenantUserStatus.Active);
        SeedMembership(factory, tenantId, projectId, userId, ProjectRole.ProjectOwner);
        SeedStageExecutions(factory, tenantId,
        [
            // Expires exactly at now: not > now, so the lease counts as expired.
            StageRow(tenantId, projectId, runId, "worker-boundary", Now.AddMinutes(-30), Now),
            // Expires one second in the future: still unexpired.
            StageRow(tenantId, projectId, runId, "worker-live", Now.AddMinutes(-30), Now.AddSeconds(1)),
        ]);

        var service = new WorkerHealthService(
            factory,
            MsOptions.Create(new DiagnosticsOptions { KnownWorkers = ["worker-gone"] }),
            new AllowAccessChecker(),
            NullLogger<WorkerHealthService>.Instance,
            new FixedTimeProvider(Now));

        var workers = await service.GetWorkerHealthAsync(tenantId, userId, "corr-workers");

        var boundary = workers.Single(w => w.Worker == "worker-boundary");
        Assert.Equal(WorkerHealthService.StatusStale, boundary.Status);
        Assert.Equal(1, boundary.ActiveJobs);
        Assert.Equal(Now, boundary.LastHeartbeatAt);

        var live = workers.Single(w => w.Worker == "worker-live");
        Assert.Equal(WorkerHealthService.StatusActive, live.Status);

        var gone = workers.Single(w => w.Worker == "worker-gone");
        Assert.Equal(WorkerHealthService.StatusUnknown, gone.Status);
        Assert.Null(gone.LastHeartbeatAt);

        Assert.All(workers, w => Assert.Equal("corr-workers", w.CorrelationId));
        // Runtime lease holders come first (name-sorted), then roster-only
        // Unknown workers (name-sorted).
        Assert.Equal(["worker-boundary", "worker-live", "worker-gone"], workers.Select(w => w.Worker).ToArray());
    }

    [Fact]
    public async Task QueueDepths_AndDlq_ShareTheSamePendingBacklog()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var backlog = new FakeQueueBacklog(
        [
            Dead("{\"reason\":\"R1\"}", Now.AddHours(-2)),
            Dead("{\"reason\":\"R1\"}", Now.AddHours(-1)),
            new("queue://localhost/ai.provider", "urn:message:Live", null, Now),
        ]);
        var service = new QueueDiagnosticsService(
            backlog, new AllowAccessChecker(), NullLogger<QueueDiagnosticsService>.Instance, new FixedTimeProvider(Now));

        var depths = await service.GetQueueDepthsAsync(tenantId, userId, "corr-q");
        var summary = await service.GetDlqSummaryAsync(tenantId, userId, "corr-q");

        Assert.Equal(3, depths.Sum(d => d.Depth));
        Assert.Equal(2, depths.Single(d => d.Queue == "_error").Depth);
        Assert.Equal(1, depths.Single(d => d.Queue == "ai.provider").Depth);
        Assert.Equal(2, summary.Depth);
        Assert.Equal(TimeSpan.FromHours(2), summary.OldestEntryAge);
        // Both reads go through the same backlog seam.
        Assert.Equal(2, backlog.Calls);
    }

    [Fact]
    public void Services_GuardNullDependencies()
    {
        var options = MsOptions.Create(new DiagnosticsOptions());
        var access = new AllowAccessChecker();
        var time = new FixedTimeProvider(Now);

        Assert.Throws<ArgumentNullException>(() => new QueueDiagnosticsService(null!, access, NullLogger<QueueDiagnosticsService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new QueueDiagnosticsService(new FakeQueueBacklog([]), null!, NullLogger<QueueDiagnosticsService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new QueueDiagnosticsService(new FakeQueueBacklog([]), access, null!));

        Assert.Throws<ArgumentNullException>(() => new LeaseOrphanService(null!, options, access, NullLogger<LeaseOrphanService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new LeaseOrphanService(new ThrowingContextFactory(), null!, access, NullLogger<LeaseOrphanService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new LeaseOrphanService(new ThrowingContextFactory(), options, null!, NullLogger<LeaseOrphanService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new LeaseOrphanService(new ThrowingContextFactory(), options, access, null!));

        Assert.Throws<ArgumentNullException>(() => new WorkerHealthService(new ThrowingContextFactory(), null!, access, NullLogger<WorkerHealthService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new WorkerHealthService(new ThrowingContextFactory(), options, null!, NullLogger<WorkerHealthService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new WorkerHealthService(new ThrowingContextFactory(), options, access, null!));

        Assert.Throws<ArgumentNullException>(() => new ReviewBacklogService(null!, access, NullLogger<ReviewBacklogService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ReviewBacklogService(new ThrowingContextFactory(), null!, NullLogger<ReviewBacklogService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ReviewBacklogService(new ThrowingContextFactory(), access, null!));

        Assert.Throws<ArgumentNullException>(() => new ProviderHealthQueryService(null!, new NeverProbedHealthTracker(), MsOptions.Create(new ProviderOptions()), options, access, NullLogger<ProviderHealthQueryService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ProviderHealthQueryService(new ThrowingContextFactory(), null!, MsOptions.Create(new ProviderOptions()), options, access, NullLogger<ProviderHealthQueryService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ProviderHealthQueryService(new ThrowingContextFactory(), new NeverProbedHealthTracker(), null!, options, access, NullLogger<ProviderHealthQueryService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ProviderHealthQueryService(new ThrowingContextFactory(), new NeverProbedHealthTracker(), MsOptions.Create(new ProviderOptions()), null!, access, NullLogger<ProviderHealthQueryService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ProviderHealthQueryService(new ThrowingContextFactory(), new NeverProbedHealthTracker(), MsOptions.Create(new ProviderOptions()), options, null!, NullLogger<ProviderHealthQueryService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ProviderHealthQueryService(new ThrowingContextFactory(), new NeverProbedHealthTracker(), MsOptions.Create(new ProviderOptions()), options, access, null!));

        // A null TimeProvider falls back to the system clock without throwing.
        Assert.NotNull(new QueueDiagnosticsService(new FakeQueueBacklog([]), access, NullLogger<QueueDiagnosticsService>.Instance, null));
        Assert.NotNull(new LeaseOrphanService(new ThrowingContextFactory(), options, access, NullLogger<LeaseOrphanService>.Instance, null));
        Assert.NotNull(new WorkerHealthService(new ThrowingContextFactory(), options, access, NullLogger<WorkerHealthService>.Instance, null));
        Assert.NotNull(time);
    }

    private static void AssertFail(DiagnosticsOptionsValidator validator, DiagnosticsOptions options)
    {
        var result = validator.Validate(MsOptions.DefaultName, options);
        Assert.True(result.Failed, "Expected validation to fail.");
        Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    private static QueuedMessageSnapshot Dead(string? headersJson, DateTimeOffset? enqueuedAt)
    {
        return new QueuedMessageSnapshot("queue://localhost/_error", "urn:message:Work", headersJson, enqueuedAt);
    }

    private static string[] QueueNamesTaxonomy() =>
    [
        QueueNames.ControlOrchestration, QueueNames.MediaPreparation, QueueNames.MediaRender,
        QueueNames.AiProvider, QueueNames.AiGpu, QueueNames.Export, QueueNames.Maintenance,
        QueueNames.Skipped, QueueNames.Error,
    ];

    private static ProviderHealthState HealthState(bool circuitOpen, int totalCalls, bool isHealthy)
    {
        return new ProviderHealthState(ProviderType.Mock, true, true, circuitOpen, 0.0, totalCalls, false, null, isHealthy);
    }

    private static TestContextFactory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("DiagnosticsAggregationTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
        return new TestContextFactory(options);
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

    private static StageExecution StageRow(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string owner,
        DateTimeOffset startedAt,
        DateTimeOffset leaseExpiresAt)
    {
        return new StageExecution(
            Guid.NewGuid(), tenantId, projectId, runId, StageType.Transcription,
            ScopeType.Run, "run", null, 1, StageStatus.Running,
            owner, "lease-token-" + Guid.NewGuid().ToString("N"), 1, leaseExpiresAt,
            startedAt, null, null, "config", "snapshot", null, null, null, Now, Now);
    }

    private static void SeedStageExecutions(TestContextFactory factory, Guid tenantId, IReadOnlyList<StageExecution> rows)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<StageExecution>().AddRange(rows);
            db.SaveChanges();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowingContextFactory : IStageExecutionContextFactory
    {
        public Microsoft.EntityFrameworkCore.DbContext CreateDbContext() =>
            throw new InvalidOperationException("Context must not be created for this test.");
    }

    private sealed class AllowAccessChecker : IDiagnosticsAccessChecker
    {
        public Task RequireDiagnosticsViewerAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class DenyAccessChecker : IDiagnosticsAccessChecker
    {
        public Task RequireDiagnosticsViewerAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
            => throw new ForbiddenException($"{DiagnosticsAccessPolicy.ForbiddenMarker}: denied.");
    }

    private sealed class FakeQueueBacklog(IReadOnlyList<QueuedMessageSnapshot> pending) : IQueueBacklogStore
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<QueuedMessageSnapshot>> ListPendingAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(pending);
        }
    }

    private sealed class NeverProbedHealthTracker : IProviderHealthTracker
    {
        public bool IsHealthy(ProviderType provider) => true;

        public ProviderHealthState GetState(ProviderType provider) => HealthState(circuitOpen: false, totalCalls: 0, isHealthy: true);

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

    private sealed class OpenCircuitHealthTracker : IProviderHealthTracker
    {
        public bool IsHealthy(ProviderType provider) => false;

        public ProviderHealthState GetState(ProviderType provider) => HealthState(circuitOpen: true, totalCalls: 7, isHealthy: false);

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
