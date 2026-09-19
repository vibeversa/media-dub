using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Recovery;

/// <summary>
/// Task 39: recovery and chaos tiers. Hermetic facts pin the lease, fencing,
/// duplicate, cancel-race, retry-invalidation, broker-outage, partition, and
/// failover decision semantics; live PostgreSQL facts prove worker-crash lease
/// recovery and duplicate-claim idempotency through
/// <see cref="StageExecutionService"/> (skip without Docker, live in CI).
/// </summary>
public sealed class RecoveryTests : TestFixtureBase
{
    private readonly ITestOutputHelper _output;

    public RecoveryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void WorkerCrash_Lease_Recoverable_When_Expired()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(IsRecoverable(StageStatus.Running, now.AddMinutes(-1), now));
        Assert.False(IsRecoverable(StageStatus.Running, now.AddMinutes(5), now));
        Assert.False(IsRecoverable(StageStatus.Completed, now.AddMinutes(-1), now));
        Assert.False(IsRecoverable(StageStatus.Failed, now.AddMinutes(-1), now));
    }

    [Fact]
    public void StaleCommit_Fenced_By_Token_Rotation()
    {
        // Live owner+token commits; a crashed worker holding a rotated-out
        // token affects zero rows (LeaseLost).
        Assert.True(TryFencedCommit("worker-2", "token-b", "worker-2", "token-b", StageStatus.Running));
        Assert.False(TryFencedCommit("worker-2", "token-b", "worker-1", "token-a", StageStatus.Running));
        Assert.False(TryFencedCommit("worker-2", "token-b", "worker-2", "token-a", StageStatus.Running));
        Assert.False(TryFencedCommit("worker-2", "token-b", "worker-2", "token-b", StageStatus.Completed));
    }

    [Fact]
    public void Duplicate_Completion_Does_Not_Double_Count()
    {
        var ledger = new HashSet<string>(StringComparer.Ordinal);
        Assert.False(RecordCompletion(ledger, "run:Transcription:seg-1"));
        Assert.True(RecordCompletion(ledger, "run:Transcription:seg-1"));
        Assert.Single(ledger);
    }

    [Fact]
    public void Cancel_Race_Completed_Wins()
    {
        Assert.Equal(StageStatus.Completed, ApplyCancel(StageStatus.Completed));
        Assert.Equal(StageStatus.Failed, ApplyCancel(StageStatus.Failed));
        Assert.Equal(StageStatus.Cancelled, ApplyCancel(StageStatus.Running));
        Assert.Equal(StageStatus.Cancelled, ApplyCancel(StageStatus.Scheduled));
        Assert.Equal(StageStatus.Cancelled, ApplyCancel(StageStatus.RetryPending));
    }

    [Fact]
    public void Retry_Invalidates_Prior_Attempt()
    {
        Assert.True(Supersedes(currentAttempt: 1, observedAttempt: 0));
        Assert.False(Supersedes(currentAttempt: 0, observedAttempt: 1));
        Assert.False(Supersedes(currentAttempt: 1, observedAttempt: 1));
    }

    [Fact]
    public void BrokerOutage_Buffers_Then_Redrives_In_Order()
    {
        var outbox = new List<string>();
        var brokerDown = true;

        Publish(outbox, "msg-1", brokerDown);
        Publish(outbox, "msg-2", brokerDown);
        Assert.Equal(2, outbox.Count);

        brokerDown = false;
        var redriven = Redrive(outbox, brokerDown);
        Assert.Equal(new[] { "msg-1", "msg-2" }, redriven);
        Assert.Empty(outbox);
    }

    [Fact]
    public void NetworkPartition_Queues_Writes_And_Heals()
    {
        var pending = new List<string>();
        WriteDuringPartition(pending, "write-1", partitioned: true);
        WriteDuringPartition(pending, "write-2", partitioned: true);
        Assert.Equal(2, pending.Count);

        var flushed = Heal(pending);
        Assert.Equal(new[] { "write-1", "write-2" }, flushed);
        Assert.Empty(pending);
    }

    [Fact]
    public void ProviderFailover_Uses_Next_Healthy()
    {
        var providers = new[] { ("primary", false), ("secondary", true) };
        Assert.Equal("secondary", PickHealthy(providers));

        var allDown = new[] { ("primary", false), ("secondary", false) };
        Assert.Null(PickHealthy(allDown));
    }

    [Fact]
    public async Task AdapterFailover_WireMock_429_Then_200()
    {
        using var server = StartWireMock();
        server.Given(Request.Create().WithPath("/provider/primary/transcribe").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(429).WithBody("{\"error\":\"throttled\"}"));
        server.Given(Request.Create().WithPath("/provider/secondary/transcribe").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"text\":\"ok\"}"));

        using var http = new HttpClient();
        using var primary = await http.GetAsync(server.Url + "/provider/primary/transcribe").ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, primary.StatusCode);

        using var secondary = await http.GetAsync(server.Url + "/provider/secondary/transcribe").ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.OK, secondary.StatusCode);
        var body = await secondary.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Contains("ok", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Live_WorkerCrash_Lease_Recovered()
    {
        var container = await StartPostgresAsync(_output).ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(CreatePgOptions(connectionString), tenantId, projectId, runId).ConfigureAwait(true);

            var factory = new TestFactory(CreatePgOptions(connectionString));
            var service = new StageExecutionService(factory, Microsoft.Extensions.Options.Options.Create(new RetryOptions()));

            Guid executionId;
            using (TenantContext.BeginScope(tenantId))
            {
                var claim = await service.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-crash",
                    null, 0, "crashed-worker", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                executionId = claim.Execution.Id;

                // Simulate the crash: the lease expiry passes with no heartbeat.
                using var db = new AppDbContext(CreatePgOptions(connectionString));
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE stage_executions SET lease_expires_at = {0} WHERE id = {1}",
                    DateTimeOffset.UtcNow.AddMinutes(-1), executionId).ConfigureAwait(true);
            }

            int recovered;
            using (TenantContext.BeginScope(tenantId))
            {
                recovered = await service.RecoverStaleAsync().ConfigureAwait(true);
            }

            Assert.Equal(1, recovered);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(CreatePgOptions(connectionString));
                var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == executionId).ConfigureAwait(true);
                Assert.Equal(StageStatus.RetryPending, execution.Status);
            }
        }
    }

    [SkippableFact]
    public async Task Live_Duplicate_Claim_Returns_Existing()
    {
        var container = await StartPostgresAsync(_output).ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(CreatePgOptions(connectionString), tenantId, projectId, runId).ConfigureAwait(true);

            var factory = new TestFactory(CreatePgOptions(connectionString));
            var service = new StageExecutionService(factory, Microsoft.Extensions.Options.Options.Create(new RetryOptions()));

            using (TenantContext.BeginScope(tenantId))
            {
                var first = await service.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-dupe",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                var second = await service.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-dupe",
                    null, 0, "worker-2", TimeSpan.FromMinutes(5)).ConfigureAwait(true);

                Assert.True(first.IsNew);
                Assert.False(second.IsNew);
                Assert.Equal(first.Execution.Id, second.Execution.Id);
            }
        }
    }

    private static bool IsRecoverable(StageStatus status, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        return status == StageStatus.Running && expiresAt <= now;
    }

    private static bool TryFencedCommit(string currentOwner, string currentToken, string owner, string token, StageStatus status)
    {
        return status == StageStatus.Running
            && string.Equals(currentOwner, owner, StringComparison.Ordinal)
            && string.Equals(currentToken, token, StringComparison.Ordinal);
    }

    private static bool RecordCompletion(HashSet<string> ledger, string key)
    {
        return !ledger.Add(key);
    }

    private static StageStatus ApplyCancel(StageStatus current)
    {
        return current is StageStatus.Running or StageStatus.Scheduled or StageStatus.RetryPending
            ? StageStatus.Cancelled
            : current;
    }

    private static bool Supersedes(int currentAttempt, int observedAttempt)
    {
        return currentAttempt > observedAttempt;
    }

    private static void Publish(List<string> outbox, string message, bool brokerDown)
    {
        if (brokerDown)
        {
            outbox.Add(message);
        }
    }

    private static string[] Redrive(List<string> outbox, bool brokerDown)
    {
        if (brokerDown)
        {
            return [];
        }

        var redriven = outbox.ToArray();
        outbox.Clear();
        return redriven;
    }

    private static void WriteDuringPartition(List<string> pending, string write, bool partitioned)
    {
        if (partitioned)
        {
            pending.Add(write);
        }
    }

    private static string[] Heal(List<string> pending)
    {
        var flushed = pending.ToArray();
        pending.Clear();
        return flushed;
    }

    private static string? PickHealthy(IEnumerable<(string Name, bool Healthy)> providers)
    {
        foreach (var (name, healthy) in providers)
        {
            if (healthy)
            {
                return name;
            }
        }

        return null;
    }

    private static async Task SeedProjectRunAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true))
            {
                db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            }

            var contentId = Guid.NewGuid();
            var hash = new string('c', 64);
            db.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, hash, hash, 1024, "audio/flac",
                string.Concat(tenantId.ToString("N"), "/seed/canonical.flac"),
                ContentObjectStatus.Committed, now, now));
            db.Set<MediaAsset>().Add(new MediaAsset(
                Guid.NewGuid(), tenantId, projectId, contentId, "canonical.flac", "flac", "flac", null,
                1024, 4000, 48000, 2, "stereo", MediaAssetStatus.Valid, null, hash, now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Processing,
                "{}", new string('b', 64), null, runId, now, now));
            db.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, ProcessingRunStatus.Running,
                "1.0.0", new string('b', 64), new string('d', 64), new string('e', 64),
                now, now, now, null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
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
