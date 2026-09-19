using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Sockets;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Orchestration;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Orchestration;

/// <summary>
/// Verifies saga DAG barriers and recovery: ledger exactly-once, barrier
/// advancement, cancellation blocking, lease recovery, schema-mismatch routing,
/// and poison DLQ behavior. PostgreSQL tests use Testcontainers PG16 and skip
/// without Docker (CI runs live); transport tests use the in-memory bus and run
/// offline.
/// </summary>
public sealed class BarrierTests
{
    private readonly ITestOutputHelper _output;

    public BarrierTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Duplicate_Completion_Does_Not_Double_Count()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Running).ConfigureAwait(true);

            var factory = new TestFactory(options);
            var claims = new StageExecutionService(factory, Options.Create(new RetryOptions()));
            var barrier = new BarrierService(factory);
            await barrier.EnsureSummaryAsync(tenantId, runId, StageType.Transcription, 1).ConfigureAwait(true);

            Guid executionId;
            using (TenantContext.BeginScope(tenantId))
            {
                var claim = await claims.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-1",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                executionId = claim.Execution.Id;
            }

            BarrierResult first;
            BarrierResult second;
            using (TenantContext.BeginScope(tenantId))
            {
                first = await barrier.RecordUnitCompletionAsync(
                    tenantId, runId, StageType.Transcription, ScopeType.Segment, "seg-1",
                    executionId, "Completed").ConfigureAwait(true);
                second = await barrier.RecordUnitCompletionAsync(
                    tenantId, runId, StageType.Transcription, ScopeType.Segment, "seg-1",
                    executionId, "Completed").ConfigureAwait(true);
            }

            Assert.False(first.IsDuplicate);
            Assert.True(first.StageComplete);
            Assert.True(second.IsDuplicate);
            Assert.False(second.StageComplete);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var summary = await db.Set<RunStageSummary>()
                    .FirstAsync(s => s.ProcessingRunId == runId && s.StageType == StageType.Transcription)
                    .ConfigureAwait(true);
                Assert.Equal(1, summary.CompletedUnits);
                Assert.Equal(1, summary.ExpectedUnits);
                var ledger = await db.Set<StageUnitCompletion>()
                    .CountAsync(c => c.ProcessingRunId == runId && c.StageType == StageType.Transcription)
                    .ConfigureAwait(true);
                Assert.Equal(1, ledger);
            }
        }
    }

    [SkippableFact]
    public async Task Barrier_Advances_Exactly_Once()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Running).ConfigureAwait(true);

            var factory = new TestFactory(options);
            var claims = new StageExecutionService(factory, Options.Create(new RetryOptions()));
            var barrier = new BarrierService(factory);
            await barrier.EnsureSummaryAsync(tenantId, runId, StageType.Transcription, 2).ConfigureAwait(true);

            Guid firstId;
            Guid secondId;
            using (TenantContext.BeginScope(tenantId))
            {
                var first = await claims.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-a",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                var second = await claims.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-b",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                firstId = first.Execution.Id;
                secondId = second.Execution.Id;
            }

            BarrierResult firstResult;
            BarrierResult secondResult;
            using (TenantContext.BeginScope(tenantId))
            {
                firstResult = await barrier.RecordUnitCompletionAsync(
                    tenantId, runId, StageType.Transcription, ScopeType.Segment, "seg-a",
                    firstId, "Completed").ConfigureAwait(true);
                secondResult = await barrier.RecordUnitCompletionAsync(
                    tenantId, runId, StageType.Transcription, ScopeType.Segment, "seg-b",
                    secondId, "Completed").ConfigureAwait(true);
            }

            Assert.False(firstResult.StageComplete);
            Assert.True(secondResult.StageComplete);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var summary = await db.Set<RunStageSummary>()
                    .FirstAsync(s => s.ProcessingRunId == runId && s.StageType == StageType.Transcription)
                    .ConfigureAwait(true);
                Assert.Equal(2, summary.CompletedUnits);
            }
        }
    }

    [SkippableFact]
    public async Task Cancellation_Blocks_New_Work()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Cancelling).ConfigureAwait(true);
            await SeedSegmentsAsync(options, tenantId, projectId, runId, 2).ConfigureAwait(true);

            var sendEndpoint = new Mock<ISendEndpoint>();
            var sendProvider = new Mock<ISendEndpointProvider>();
            sendProvider
                .Setup(p => p.GetSendEndpoint(It.IsAny<Uri>()))
                .ReturnsAsync(sendEndpoint.Object);

            var dispatcher = new WorkDispatcher(
                new TestFactory(options),
                sendProvider.Object,
                new StubDeferredSender(),
                Options.Create(new QuotaOptions()),
                new AllowAllRateGate(),
                new AllowAllCostGate(),
                NullLogger<WorkDispatcher>.Instance);

            DispatchResult result;
            using (TenantContext.BeginScope(tenantId))
            {
                result = await dispatcher.DispatchSegmentWorkAsync(
                    tenantId, projectId, runId, StageType.Transcription).ConfigureAwait(true);
            }

            Assert.Equal(0, result.Dispatched);
            Assert.Equal("run-cancelling", result.Reason);
            sendProvider.Verify(p => p.GetSendEndpoint(It.IsAny<Uri>()), Times.Never);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var executions = await db.Set<StageExecution>()
                    .CountAsync(e => e.ProcessingRunId == runId).ConfigureAwait(true);
                Assert.Equal(0, executions);
            }
        }
    }

    [SkippableFact]
    public async Task Expired_Lease_Recovered_Active_Not()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Running).ConfigureAwait(true);

            var factory = new TestFactory(options);
            var service = new StageExecutionService(factory, Options.Create(new RetryOptions()));

            Guid activeId;
            Guid expiredId;
            using (TenantContext.BeginScope(tenantId))
            {
                var active = await service.ClaimAsync(
                    tenantId, projectId, runId, "Translation", "Segment", "seg-active",
                    null, 0, "worker-1", TimeSpan.FromMinutes(10)).ConfigureAwait(true);
                var expired = await service.ClaimAsync(
                    tenantId, projectId, runId, "Translation", "Segment", "seg-expired",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                activeId = active.Execution.Id;
                expiredId = expired.Execution.Id;
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE stage_executions SET lease_expires_at = {0} WHERE id = {1}",
                    DateTimeOffset.UtcNow.AddHours(-1),
                    expiredId).ConfigureAwait(true);
            }

            int recovered;
            using (TenantContext.BeginScope(tenantId))
            {
                recovered = await service.RecoverStaleAsync().ConfigureAwait(true);
            }

            Assert.Equal(1, recovered);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var active = await db.Set<StageExecution>().FirstAsync(e => e.Id == activeId).ConfigureAwait(true);
                var expired = await db.Set<StageExecution>().FirstAsync(e => e.Id == expiredId).ConfigureAwait(true);
                Assert.Equal(StageStatus.Running, active.Status);
                Assert.Equal(StageStatus.RetryPending, expired.Status);
                Assert.Equal(1, expired.Attempt);
            }
        }
    }

    [SkippableFact]
    public async Task Expired_Lease_Takeover_Allows_Redrive()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Running).ConfigureAwait(true);

            var factory = new TestFactory(options);
            var service = new StageExecutionService(factory, Options.Create(new RetryOptions()));

            string firstToken;
            Guid executionId;
            using (TenantContext.BeginScope(tenantId))
            {
                var first = await service.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-take",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                Assert.True(first.IsNew);
                executionId = first.Execution.Id;
                firstToken = first.Execution.LeaseToken;
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE stage_executions SET lease_expires_at = {0} WHERE id = {1}",
                    DateTimeOffset.UtcNow.AddHours(-1),
                    executionId).ConfigureAwait(true);
            }

            StageClaimResult second;
            using (TenantContext.BeginScope(tenantId))
            {
                second = await service.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-take",
                    null, 0, "worker-2", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }

            Assert.False(second.IsNew);
            Assert.Equal(executionId, second.Execution.Id);
            Assert.Equal("worker-2", second.Execution.LeaseOwner);
            Assert.NotEqual(firstToken, second.Execution.LeaseToken);
            Assert.Equal(StageStatus.Running, second.Execution.Status);

            using (TenantContext.BeginScope(tenantId))
            {
                await Assert.ThrowsAsync<LeaseLostException>(() => service.CompleteAsync(
                    tenantId, executionId, "worker-1", firstToken, ["artifact-1"])).ConfigureAwait(true);

                var completed = await service.CompleteAsync(
                    tenantId, executionId, "worker-2", second.Execution.LeaseToken, []).ConfigureAwait(true);
                Assert.Equal(StageStatus.Completed, completed.Status);
            }
        }
    }

    [Fact]
    public async Task Schema_Mismatch_Goes_To_Skipped()
    {
        Assert.Equal(
            MessageFate.Skipped,
            MessageDisposition.Decide(
                BadVersionMessage(), ProcessingRunStatus.Running, Guid.NewGuid()));
        Assert.Equal("messaging.schema_mismatch_total", MessageVersionPolicy.SchemaMismatchMetricName);

        var recorder = new SkippedRecorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddSingleton<IStageExecutionContextFactory>(new UnusedFactory());
        services.AddSingleton(Options.Create(new RetryOptions()));
        services.AddMassTransit(x =>
        {
            x.AddConsumer<ProbeWorkConsumer>();
            x.AddConsumer<SkippedProbe>();
            x.UsingInMemory((ctx, cfg) =>
            {
                cfg.ReceiveEndpoint(QueueNames.AiProvider, e => e.ConfigureConsumer<ProbeWorkConsumer>(ctx, _ => { }));
                cfg.ReceiveEndpoint(QueueNames.Skipped, e => e.ConfigureConsumer<SkippedProbe>(ctx, _ => { }));
            });
        });

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync(CancellationToken.None);
        try
        {
            await bus.Publish(BadVersionMessage());
            await recorder.First.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Empty(recorder.Handled);
            Assert.NotEmpty(recorder.Parked);
            Assert.All(recorder.Parked, m => Assert.Equal(99, m.SchemaVersion));
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Dlq_Routes_Poison()
    {
        Assert.Equal(MessageFate.Skipped, MessageDisposition.DecideFailure(new InvalidOperationException("poison")));
        Assert.Equal(MessageFate.Error, MessageDisposition.DecideFailure(new HttpRequestException("transient")));
        Assert.False(MessageDisposition.IsTransient(new InvalidOperationException("poison")));
        Assert.True(MessageDisposition.IsTransient(new TimeoutException("slow")));
        Assert.Equal(QueueNames.Error, MassTransitConfig.DeadLetterQueueName);
        Assert.Equal("_error", MassTransitConfig.DeadLetterQueueName);
        Assert.Equal("dlq.depth", MessagingMeters.DlqDepthMetricName);

        var recorder = new SkippedRecorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddMassTransit(x =>
        {
            x.AddConsumer<SkippedProbe>();
            x.UsingInMemory((ctx, cfg) =>
            {
                cfg.ReceiveEndpoint(QueueNames.Skipped, e => e.ConfigureConsumer<SkippedProbe>(ctx, _ => { }));
            });
        });

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync(CancellationToken.None);
        try
        {
            var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{QueueNames.Skipped}", UriKind.Absolute));
            await endpoint.Send(BadVersionMessage());
            await recorder.First.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Single(recorder.Parked);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduler_Delivers_Delayed_Message()
    {
        var recorder = new SkippedRecorder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(recorder);
        services.AddScoped<IDeferredSender, MassTransitDeferredSender>();
        services.AddMassTransit(x =>
        {
            x.AddDelayedMessageScheduler();
            x.AddConsumer<SkippedProbe>();
            x.UsingInMemory((ctx, cfg) =>
            {
                cfg.UseDelayedMessageScheduler();
                cfg.ReceiveEndpoint(QueueNames.Skipped, e => e.ConfigureConsumer<SkippedProbe>(ctx, _ => { }));
            });
        });

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync(CancellationToken.None);
        try
        {
            using var scope = provider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<IDeferredSender>();
            var sent = await sender.SendDelayedAsync(QueueNames.Skipped, BadVersionMessage(), TimeSpan.FromSeconds(1));
            Assert.True(sent);
            await recorder.First.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Single(recorder.Parked);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Saga_Schedules_First_Stage_On_RunStarted()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var pgOptions = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(pgOptions, tenantId, projectId, runId, ProcessingRunStatus.Pending).ConfigureAwait(true);

            var work = new WorkRecorder();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(work);
            services.AddSingleton(Options.Create(new RetryOptions()));
            services.AddSingleton(Options.Create(new QuotaOptions()));
            services.AddSingleton<IStageExecutionContextFactory>(new TestFactory(pgOptions));
            services.AddScoped<BarrierService>();
            services.AddScoped<WorkDispatcher>();
            services.AddSingleton<IRateGate, AllowAllRateGate>();
            services.AddSingleton<ICostGate, AllowAllCostGate>();
            services.AddScoped<IDeferredSender, MassTransitDeferredSender>();
            services.AddMassTransit(x =>
            {
                x.AddSagaStateMachine<ProcessingRunSaga, ProcessingRunSagaState>().InMemoryRepository();
                x.AddDelayedMessageScheduler();
                x.AddConsumer<WorkProbe>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.ReceiveEndpoint(
                        QueueNames.ControlOrchestration,
                        e => e.ConfigureSaga<ProcessingRunSagaState>(ctx, _ => { }));
                    cfg.ReceiveEndpoint(
                        QueueNames.MediaPreparation,
                        e => e.ConfigureConsumer<WorkProbe>(ctx, _ => { }));
                });
            });

            await using var provider = services.BuildServiceProvider();
            var bus = provider.GetRequiredService<IBusControl>();
            await bus.StartAsync(CancellationToken.None);
            try
            {
                await bus.Publish(new RunStarted(
                    Guid.NewGuid(), "saga-e2e", tenantId, projectId, runId,
                    null, null, null, null, null, MessageVersionPolicy.CurrentVersion,
                    DateTimeOffset.UtcNow, 0, null, null, null, "v1", new string('b', 64)));
                await work.First.Task.WaitAsync(TimeSpan.FromSeconds(30));

                var dispatched = Assert.Single(work.Messages);
                Assert.Equal("MediaValidation", dispatched.StageTypeRequired);
                Assert.Equal(runId, dispatched.ProcessingRunId);

                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var run = await db.Set<ProcessingRun>().FirstAsync(r => r.Id == runId).ConfigureAwait(true);
                    Assert.Equal(ProcessingRunStatus.Running, run.Status);
                }
            }
            finally
            {
                await bus.StopAsync(CancellationToken.None);
            }
        }
    }

    private static StageWorkRequested BadVersionMessage()
    {
        var tenantId = Guid.NewGuid();
        return new StageWorkRequested(
            Guid.NewGuid(), "bad-version", tenantId, Guid.NewGuid(), Guid.NewGuid(),
            null, "Transcription", "Segment", "seg-1", null, 99,
            DateTimeOffset.UtcNow, 0, null, null, null,
            "Transcription", "Segment", "seg-1", null);
    }

    private static async Task SeedSegmentsAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        int count)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            for (var i = 0; i < count; i++)
            {
                db.SpeechSegments.Add(new SpeechSegment(
                    Guid.NewGuid(), tenantId, projectId, runId, i,
                    i * 1000, (i * 1000) + 800, "Pending", null, now));
            }

            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task SeedTenantProjectRunAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        ProcessingRunStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "de", ProjectStatus.Created,
                "{}", new string('a', 64), null, null, now, now));
            db.ProcessingRuns.Add(new ProcessingRun(
                runId, tenantId, projectId, 0, status, "v1",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task MigrateAsync(PostgreSqlContainer container)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            var options = CreateOptions(container);
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
#pragma warning disable CA1031 // Docker availability probe: any transport-level failure means "skip".
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping barrier test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
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

    private sealed class UnusedFactory : IStageExecutionContextFactory
    {
        public DbContext CreateDbContext()
        {
            throw new NotImplementedException("Unreachable on the schema-mismatch path.");
        }
    }

    private sealed class StubDeferredSender : IDeferredSender
    {
        public Task<bool> SendDelayedAsync<T>(string queue, T message, TimeSpan delay, CancellationToken cancellationToken = default)
            where T : class
        {
            return Task.FromResult(true);
        }
    }

    private sealed class SkippedRecorder
    {
        public ConcurrentBag<StageWorkRequested> Parked = new();

        public ConcurrentBag<StageWorkRequested> Handled = new();

        public TaskCompletionSource<bool> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class WorkRecorder
    {
        public ConcurrentBag<StageWorkRequested> Messages = new();

        public TaskCompletionSource<bool> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ProbeWorkConsumer : BaseConsumer<StageWorkRequested>
    {
        private readonly SkippedRecorder _recorder;

        public ProbeWorkConsumer(IStageExecutionContextFactory factory, IOptions<RetryOptions> options, SkippedRecorder recorder)
            : base(factory, options)
        {
            _recorder = recorder;
        }

        protected override Task HandleAsync(
            ConsumeContext<StageWorkRequested> context,
            StageExecution? execution,
            CancellationToken cancellationToken)
        {
            _recorder.Handled.Add(context.Message);
            return Task.CompletedTask;
        }
    }

    private sealed class SkippedProbe : IConsumer<StageWorkRequested>
    {
        private readonly SkippedRecorder _recorder;

        public SkippedProbe(SkippedRecorder recorder)
        {
            _recorder = recorder;
        }

        public Task Consume(ConsumeContext<StageWorkRequested> context)
        {
            _recorder.Parked.Add(context.Message);
            _recorder.First.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class WorkProbe : IConsumer<StageWorkRequested>
    {
        private readonly WorkRecorder _recorder;

        public WorkProbe(WorkRecorder recorder)
        {
            _recorder = recorder;
        }

        public Task Consume(ConsumeContext<StageWorkRequested> context)
        {
            _recorder.Messages.Add(context.Message);
            _recorder.First.TrySetResult(true);
            return Task.CompletedTask;
        }
    }
}
