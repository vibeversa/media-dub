using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Orchestration;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Orchestration;

#pragma warning disable CS8618 // State/Event properties are assigned by the MassTransitStateMachine base constructor via reflection.

/// <summary>
/// Durable orchestration state for one processing run. Correlated by
/// <c>ProcessingRunId</c>. Carries tenant/project identity only; no secrets,
/// no payloads, no hashes. The PostgreSQL run row remains authoritative;
/// this state drives progression and guards scheduling.
/// </summary>
public sealed class ProcessingRunSagaState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }

    public string CurrentState { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public int RunAttempt { get; set; }
}

/// <summary>
/// Scoped DAG orchestration. Workers execute; the saga decides global
/// progression: it validates tenants per event, opens barriers via
/// <see cref="BarrierService"/>, dispatches successors via
/// <see cref="WorkDispatcher"/> per <see cref="StageGraph"/>, enforces
/// per-stage retry budgets, and publishes run-level outcomes. Terminal run
/// outcomes are published as messages that the saga itself consumes to
/// transition (Completed/Failed/Cancelled), keeping side effects and state
/// changes in native state-machine steps. Review attention flips the saga to
/// ManualReviewRequired (sticky until a terminal outcome); the run row stays
/// authoritative for projections. Cancellation flips to Cancelling and blocks
/// every schedule path (saga flag plus run-row check in the dispatcher).
/// </summary>
public sealed class ProcessingRunSaga : MassTransitStateMachine<ProcessingRunSagaState>
{
    /// <summary>Error codes that fail fast without consuming retry budget.</summary>
    public static readonly HashSet<string> FailFastErrorCodes = new(StringComparer.Ordinal)
    {
        "VALIDATION_FAILED",
        "PROVIDER_CONFIGURATION_ERROR",
        "POLICY_DENIED",
        "CONSENT_REQUIRED",
        "PIPELINE_INVARIANT_VIOLATION",
    };

    /// <summary>Error codes retried with a delay instead of immediately.</summary>
    public static readonly HashSet<string> DelayedRetryErrorCodes = new(StringComparer.Ordinal)
    {
        "PROVIDER_RATE_LIMITED",
        "RATE_LIMITED",
    };

    public State Pending { get; private set; }

    public State Running { get; private set; }

    public State Cancelling { get; private set; }

    public State Cancelled { get; private set; }

    public State Completed { get; private set; }

    public State Failed { get; private set; }

    public State ManualReviewRequired { get; private set; }

    public Event<RunStarted> RunStartedEvent { get; private set; }

    public Event<StageCompleted> StageCompletedEvent { get; private set; }

    public Event<StageFailed> StageFailedEvent { get; private set; }

    public Event<StageCancelled> StageCancelledEvent { get; private set; }

    public Event<StageReviewRequired> StageReviewRequiredEvent { get; private set; }

    public Event<ReviewResolved> ReviewResolvedEvent { get; private set; }

    public Event<RunCancelledRequested> RunCancelledRequestedEvent { get; private set; }

    public Event<RunCompleted> RunCompletedEvent { get; private set; }

    public Event<RunFailed> RunFailedEvent { get; private set; }

    public Event<RunCancelled> RunCancelledEvent { get; private set; }

    private readonly IServiceScopeFactory _scopes;
    private readonly RetryOptions _retry;
    private readonly ILogger<ProcessingRunSaga> _logger;

    public ProcessingRunSaga(
        IServiceScopeFactory scopes,
        IOptions<RetryOptions> retryOptions,
        ILogger<ProcessingRunSaga> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _retry = retryOptions.Value;
        _logger = logger;

        InstanceState(x => x.CurrentState);

        Event(() => RunStartedEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));
        Event(() => StageCompletedEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));
        Event(() => StageFailedEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));
        Event(() => StageCancelledEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));
        Event(() => StageReviewRequiredEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));
        Event(() => ReviewResolvedEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));
        Event(() => RunCancelledRequestedEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));
        Event(() => RunCompletedEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));
        Event(() => RunFailedEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));
        Event(() => RunCancelledEvent, e => e.CorrelateById(m => m.Message.ProcessingRunId));

        Initially(
            When(RunStartedEvent)
                .ThenAsync(OnRunStartedAsync)
                .TransitionTo(Running));

        During(Running, ManualReviewRequired,
            When(StageCompletedEvent).ThenAsync(OnStageCompletedAsync),
            When(StageFailedEvent).ThenAsync(OnStageFailedAsync),
            When(StageCancelledEvent).ThenAsync(OnStageCancelledAsync),
            When(StageReviewRequiredEvent).ThenAsync(OnStageReviewRequiredAsync).TransitionTo(ManualReviewRequired),
            When(ReviewResolvedEvent).ThenAsync(OnReviewResolvedAsync),
            When(RunCancelledRequestedEvent).ThenAsync(OnRunCancelledRequestedAsync).TransitionTo(Cancelling),
            When(RunCompletedEvent).TransitionTo(Completed),
            When(RunFailedEvent).TransitionTo(Failed));

        During(Cancelling,
            When(StageCompletedEvent).ThenAsync(OnStageCompletedAsync),
            When(StageFailedEvent).ThenAsync(OnStageFailedAsync),
            When(StageCancelledEvent).ThenAsync(OnStageCancelledAsync),
            When(StageReviewRequiredEvent).ThenAsync(OnStageReviewRequiredAsync),
            When(ReviewResolvedEvent).ThenAsync(OnReviewResolvedAsync),
            When(RunCancelledEvent).TransitionTo(Cancelled),
            Ignore(RunCompletedEvent),
            Ignore(RunFailedEvent),
            Ignore(RunStartedEvent));

        During(Completed, Failed, Cancelled,
            Ignore(RunStartedEvent),
            Ignore(StageCompletedEvent),
            Ignore(StageFailedEvent),
            Ignore(StageCancelledEvent),
            Ignore(StageReviewRequiredEvent),
            Ignore(ReviewResolvedEvent),
            Ignore(RunCancelledRequestedEvent));
    }

    private static bool SchedulingBlocked(ProcessingRunSagaState saga)
    {
        return string.Equals(saga.CurrentState, nameof(Cancelling), StringComparison.Ordinal)
            || string.Equals(saga.CurrentState, nameof(Cancelled), StringComparison.Ordinal)
            || string.Equals(saga.CurrentState, nameof(Completed), StringComparison.Ordinal)
            || string.Equals(saga.CurrentState, nameof(Failed), StringComparison.Ordinal);
    }

    private async Task OnRunStartedAsync(BehaviorContext<ProcessingRunSagaState, RunStarted> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!MessageVersionPolicy.IsSupported(message.SchemaVersion)
            || message.TenantId == Guid.Empty
            || message.ProjectId == Guid.Empty
            || message.ProcessingRunId == Guid.Empty)
        {
            MessagingMeters.SchemaMismatches.Add(1);
            MessagingMeters.DlqDepth.Add(1);
            await SendToQueueAsync(services, QueueNames.Skipped, message).ConfigureAwait(false);
            return;
        }

        saga.TenantId = message.TenantId;
        saga.ProjectId = message.ProjectId;
        saga.RunAttempt = message.Attempt;

        var dispatcher = services.GetRequiredService<WorkDispatcher>();
        await dispatcher.DispatchSingleWorkAsync(
            message.TenantId, message.ProjectId, message.ProcessingRunId,
            StageType.MediaValidation).ConfigureAwait(false);
        await UpdateRunStatusAsync(services, message.TenantId, message.ProcessingRunId, ProcessingRunStatus.Running, setCompleted: false).ConfigureAwait(false);

        _logger.LogInformation(
            "Run {RunId} started; MediaValidation dispatched.",
            message.ProcessingRunId);
    }

    private async Task OnStageCompletedAsync(BehaviorContext<ProcessingRunSagaState, StageCompleted> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!await CheckEventAsync(services, saga, message).ConfigureAwait(false))
        {
            return;
        }

        if (!Enum.TryParse<StageType>(message.CompletedStageType, ignoreCase: true, out var stage))
        {
            throw new InvalidOperationException($"Unknown completed stage '{message.CompletedStageType}'.");
        }

        if (!TryResolveUnit(message, stage, out var unitScope, out var scopeId) || message.StageExecutionId is null)
        {
            throw new InvalidOperationException($"Stage completion for '{message.CompletedStageType}' lacks unit identity.");
        }

        var node = StageGraph.NodeOf(stage);
        var unitState = message.OutputArtifactIds.Length == 0 && StageGraph.IsSkippable(node)
            ? "Skipped"
            : "Completed";

        var barrier = services.GetRequiredService<BarrierService>();
        var result = await barrier.RecordUnitCompletionAsync(
            message.TenantId, message.ProcessingRunId, stage, unitScope, scopeId,
            message.StageExecutionId.Value, unitState).ConfigureAwait(false);

        if (!result.StageComplete)
        {
            // Barrier still open: re-pump pending units of this stage so bounded
            // batches drain to completion.
            if (!SchedulingBlocked(saga))
            {
                await RepumpStageAsync(services, saga, message, stage, node).ConfigureAwait(false);
            }

            return;
        }

        if (SchedulingBlocked(saga))
        {
            return;
        }

        await DispatchSuccessorsAsync(services, saga, message, stage).ConfigureAwait(false);
        await MaybeCompleteRunAsync(services, saga, message).ConfigureAwait(false);
    }

    private async Task OnStageFailedAsync(BehaviorContext<ProcessingRunSagaState, StageFailed> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!await CheckEventAsync(services, saga, message).ConfigureAwait(false))
        {
            return;
        }

        if (!Enum.TryParse<StageType>(message.FailedStageType, ignoreCase: true, out var stage))
        {
            throw new InvalidOperationException($"Unknown failed stage '{message.FailedStageType}'.");
        }

        if (!TryResolveUnit(message, stage, out var unitScope, out var scopeId) || message.StageExecutionId is null)
        {
            throw new InvalidOperationException($"Stage failure for '{message.FailedStageType}' lacks unit identity.");
        }

        var barrier = services.GetRequiredService<BarrierService>();
        await barrier.RecordUnitCompletionAsync(
            message.TenantId, message.ProcessingRunId, stage, unitScope, scopeId,
            message.StageExecutionId.Value, "Failed").ConfigureAwait(false);

        if (SchedulingBlocked(saga))
        {
            return;
        }

        var maxAttempts = MaxAttemptsFor(stage.ToString());
        var failFast = FailFastErrorCodes.Contains(message.ErrorCode)
            || !message.IsRetryable
            || message.Attempt + 1 > maxAttempts;

        var publish = services.GetRequiredService<IPublishEndpoint>();
        if (failFast)
        {
            await publish.Publish(new RunFailed(
                Guid.NewGuid(), CorrelationOf(message), message.TenantId, message.ProjectId,
                message.ProcessingRunId, null, null, null, null, null,
                MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, message.Attempt,
                null, null, null, message.ErrorCode, message.ErrorMessage)).ConfigureAwait(false);
            await UpdateRunStatusAsync(services, message.TenantId, message.ProcessingRunId, ProcessingRunStatus.Failed, setCompleted: true).ConfigureAwait(false);
            _logger.LogWarning(
                "Run {RunId} failed at stage {Stage} ({ErrorCode}); budget exhausted or fail-fast.",
                message.ProcessingRunId, stage, message.ErrorCode);
            return;
        }

        var dispatcher = services.GetRequiredService<WorkDispatcher>();
        TimeSpan? delay = DelayedRetryErrorCodes.Contains(message.ErrorCode)
            ? TimeSpan.FromSeconds(_retry.RateLimitDelaySec)
            : null;
        var retry = await dispatcher.DispatchUnitAsync(
            message.TenantId, message.ProjectId, message.ProcessingRunId,
            stage, unitScope, scopeId, message.SegmentId, message.Attempt + 1, delay).ConfigureAwait(false);
        if (retry.Dispatched == 0)
        {
            _logger.LogWarning(
                "Retry for run {RunId} stage {Stage} not dispatched (reason {Reason}); manual retry may be required.",
                message.ProcessingRunId, stage, retry.Reason);
        }
    }

    private async Task OnStageCancelledAsync(BehaviorContext<ProcessingRunSagaState, StageCancelled> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!await CheckEventAsync(services, saga, message).ConfigureAwait(false))
        {
            return;
        }

        if (!Enum.TryParse<StageType>(message.CancelledStageType, ignoreCase: true, out var stage))
        {
            throw new InvalidOperationException($"Unknown cancelled stage '{message.CancelledStageType}'.");
        }

        if (TryResolveUnit(message, stage, out var unitScope, out var scopeId) && message.StageExecutionId is not null)
        {
            var barrier = services.GetRequiredService<BarrierService>();
            await barrier.RecordUnitCompletionAsync(
                message.TenantId, message.ProcessingRunId, stage, unitScope, scopeId,
                message.StageExecutionId.Value, "Cancelled").ConfigureAwait(false);
        }
    }

    private async Task OnStageReviewRequiredAsync(BehaviorContext<ProcessingRunSagaState, StageReviewRequired> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!await CheckEventAsync(services, saga, message).ConfigureAwait(false))
        {
            return;
        }

        if (Enum.TryParse<StageType>(message.BlockedStageType, ignoreCase: true, out var stage)
            && TryResolveUnit(message, stage, out var unitScope, out var scopeId)
            && message.StageExecutionId is not null)
        {
            var barrier = services.GetRequiredService<BarrierService>();
            await barrier.RecordUnitCompletionAsync(
                message.TenantId, message.ProcessingRunId, stage, unitScope, scopeId,
                message.StageExecutionId.Value, "ManualReviewRequired").ConfigureAwait(false);
        }

        await UpdateRunStatusAsync(services, message.TenantId, message.ProcessingRunId, ProcessingRunStatus.ManualReviewRequired, setCompleted: false).ConfigureAwait(false);
        _logger.LogInformation(
            "Run {RunId} blocked on review {ReviewItemId}.",
            message.ProcessingRunId, message.ReviewItemId);
    }

    private async Task OnReviewResolvedAsync(BehaviorContext<ProcessingRunSagaState, ReviewResolved> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!await CheckEventAsync(services, saga, message).ConfigureAwait(false))
        {
            return;
        }

        await UpdateRunStatusAsync(services, message.TenantId, message.ProcessingRunId, ProcessingRunStatus.Running, setCompleted: false).ConfigureAwait(false);

        if (SchedulingBlocked(saga))
        {
            return;
        }

        // Resume re-drives the blocked unit on a fresh attempt (clean lease and
        // fencing); review resumes are operator-driven, never budget retries.
        if (!string.IsNullOrWhiteSpace(message.StageType)
            && Enum.TryParse<StageType>(message.StageType, ignoreCase: true, out var stage)
            && TryResolveUnit(message, stage, out var unitScope, out var scopeId))
        {
            var dispatcher = services.GetRequiredService<WorkDispatcher>();
            var resume = await dispatcher.DispatchUnitAsync(
                message.TenantId, message.ProjectId, message.ProcessingRunId,
                stage, unitScope, scopeId, message.SegmentId, message.Attempt + 1).ConfigureAwait(false);
            if (resume.Dispatched == 0)
            {
                _logger.LogWarning(
                    "Review resume for run {RunId} stage {Stage} not dispatched (reason {Reason}).",
                    message.ProcessingRunId, stage, resume.Reason);
            }
        }
    }

    private async Task OnRunCancelledRequestedAsync(BehaviorContext<ProcessingRunSagaState, RunCancelledRequested> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!await CheckEventAsync(services, saga, message).ConfigureAwait(false))
        {
            return;
        }

        await UpdateRunStatusAsync(services, message.TenantId, message.ProcessingRunId, ProcessingRunStatus.Cancelling, setCompleted: false).ConfigureAwait(false);
        _logger.LogInformation("Run {RunId} cancelling; new scheduling blocked.", message.ProcessingRunId);
    }

    private async Task<bool> CheckEventAsync<T>(IServiceProvider services, ProcessingRunSagaState saga, T message)
        where T : IntegrationMessage
    {
        if (!MessageVersionPolicy.IsSupported(message.SchemaVersion)
            || message.TenantId == Guid.Empty
            || message.ProjectId == Guid.Empty
            || message.ProcessingRunId == Guid.Empty)
        {
            MessagingMeters.SchemaMismatches.Add(1);
            MessagingMeters.DlqDepth.Add(1);
            await SendToQueueAsync(services, QueueNames.Skipped, message).ConfigureAwait(false);
            return false;
        }

        if (saga.TenantId != Guid.Empty && saga.TenantId != message.TenantId)
        {
            MessagingMeters.CrossTenantRejects.Add(1);
            MessagingMeters.DlqDepth.Add(1);
            await SendToQueueAsync(services, QueueNames.Error, message).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task RepumpStageAsync(
        IServiceProvider services,
        ProcessingRunSagaState saga,
        IntegrationMessage message,
        StageType stage,
        StageNode node)
    {
        var dispatcher = services.GetRequiredService<WorkDispatcher>();
        switch (node.Scope)
        {
            case ScopeType.Segment:
                await dispatcher.DispatchSegmentWorkAsync(
                    saga.TenantId, saga.ProjectId, saga.CorrelationId, stage,
                    batchSize: 50, attempt: message.Attempt).ConfigureAwait(false);
                break;
            case ScopeType.Speaker:
                await dispatcher.DispatchSpeakerWorkAsync(
                    saga.TenantId, saga.ProjectId, saga.CorrelationId, stage,
                    attempt: message.Attempt).ConfigureAwait(false);
                break;
            case ScopeType.Window:
                await dispatcher.DispatchWindowWorkAsync(
                    saga.TenantId, saga.ProjectId, saga.CorrelationId, stage,
                    attempt: message.Attempt).ConfigureAwait(false);
                break;
            default:
                break;
        }
    }

    private async Task DispatchSuccessorsAsync(
        IServiceProvider services,
        ProcessingRunSagaState saga,
        IntegrationMessage message,
        StageType completed)
    {
        var dispatcher = services.GetRequiredService<WorkDispatcher>();
        var barrier = services.GetRequiredService<BarrierService>();
        foreach (var successor in StageGraph.GetSuccessors(completed))
        {
            if (SchedulingBlocked(saga))
            {
                return;
            }

            var prerequisites = StageGraph.GetPrerequisites(successor.StageType);
            var ready = await barrier.ArePrerequisitesCompleteAsync(
                saga.TenantId, saga.CorrelationId, successor.StageType, prerequisites).ConfigureAwait(false);
            if (!ready)
            {
                continue;
            }

            switch (successor.Scope)
            {
                case ScopeType.Project:
                case ScopeType.Run:
                    await dispatcher.DispatchSingleWorkAsync(
                        saga.TenantId, saga.ProjectId, saga.CorrelationId, successor.StageType).ConfigureAwait(false);
                    break;
                case ScopeType.Segment:
                    await dispatcher.DispatchSegmentWorkAsync(
                        saga.TenantId, saga.ProjectId, saga.CorrelationId, successor.StageType,
                        batchSize: 50, attempt: 0).ConfigureAwait(false);
                    break;
                case ScopeType.Speaker:
                    await dispatcher.DispatchSpeakerWorkAsync(
                        saga.TenantId, saga.ProjectId, saga.CorrelationId, successor.StageType).ConfigureAwait(false);
                    break;
                case ScopeType.Window:
                    await dispatcher.DispatchWindowWorkAsync(
                        saga.TenantId, saga.ProjectId, saga.CorrelationId, successor.StageType).ConfigureAwait(false);
                    break;
            }

            _logger.LogInformation(
                "Run {RunId}: stage {Stage} barrier complete; dispatched {Next}.",
                saga.CorrelationId, completed, successor.StageType);
        }
    }

    private async Task MaybeCompleteRunAsync(
        IServiceProvider services,
        ProcessingRunSagaState saga,
        IntegrationMessage message)
    {
        var factory = services.GetRequiredService<IStageExecutionContextFactory>();
        using (TenantContext.BeginScope(saga.TenantId))
        {
            using var db = factory.CreateDbContext();
            var summaries = await db.Set<RunStageSummary>()
                .AsNoTracking()
                .Where(s => s.ProcessingRunId == saga.CorrelationId)
                .ToListAsync().ConfigureAwait(false);

            var failed = false;
            var reviewBlocked = false;
            foreach (var node in StageGraph.Nodes)
            {
                var summary = summaries.FirstOrDefault(s => s.StageType == node.StageType);
                if (summary is null || summary.ExpectedUnits <= 0)
                {
                    return;
                }

                if (summary.FailedUnits > 0)
                {
                    failed = true;
                }

                if (summary.ReviewUnits > 0)
                {
                    reviewBlocked = true;
                }

                if (summary.CompletedUnits + summary.SkippedUnits < summary.ExpectedUnits)
                {
                    return;
                }
            }

            if (failed || reviewBlocked)
            {
                return;
            }

            var outputAssetId = await db.Set<OutputAsset>()
                .AsNoTracking()
                .Where(o => o.ProcessingRunId == saga.CorrelationId)
                .OrderByDescending(o => o.CreatedAt)
                .Select(o => o.Id.ToString())
                .FirstOrDefaultAsync().ConfigureAwait(false)
                ?? saga.CorrelationId.ToString("D");

            var publish = services.GetRequiredService<IPublishEndpoint>();
            await publish.Publish(new RunCompleted(
                Guid.NewGuid(), CorrelationOf(message), saga.TenantId, saga.ProjectId,
                saga.CorrelationId, null, null, null, null, null,
                MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, saga.RunAttempt,
                null, null, null, outputAssetId)).ConfigureAwait(false);
            await UpdateRunStatusAsync(services, saga.TenantId, saga.CorrelationId, ProcessingRunStatus.Completed, setCompleted: true).ConfigureAwait(false);
            _logger.LogInformation("Run {RunId} completed.", saga.CorrelationId);
        }
    }

    private async Task UpdateRunStatusAsync(
        IServiceProvider services,
        Guid tenantId,
        Guid runId,
        ProcessingRunStatus status,
        bool setCompleted)
    {
        var factory = services.GetRequiredService<IStageExecutionContextFactory>();
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            if (setCompleted)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE processing_runs SET status = {0}, updated_at = {1}, completed_at = {1} " +
                    "WHERE id = {2} AND tenant_id = {3} AND status <> 'Completed' AND status <> 'Cancelled'",
                    status.ToString(), now, runId, tenantId).ConfigureAwait(false);
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE processing_runs SET status = {0}, updated_at = {1}, started_at = COALESCE(started_at, {1}) " +
                    "WHERE id = {2} AND tenant_id = {3} AND status <> 'Completed' AND status <> 'Cancelled'",
                    status.ToString(), now, runId, tenantId).ConfigureAwait(false);
            }
        }
    }

    private static bool TryResolveUnit(IntegrationMessage message, StageType stage, out ScopeType scope, out string scopeId)
    {
        if (!string.IsNullOrWhiteSpace(message.ScopeType)
            && Enum.TryParse<ScopeType>(message.ScopeType, ignoreCase: true, out var envelopeScope)
            && !string.IsNullOrWhiteSpace(message.ScopeId))
        {
            scope = envelopeScope;
            scopeId = message.ScopeId;
            return true;
        }

        var node = StageGraph.NodeOf(stage);
        switch (node.Scope)
        {
            case ScopeType.Project:
            case ScopeType.Run:
                scope = ScopeType.Project;
                scopeId = message.ProjectId.ToString("D");
                return true;
            case ScopeType.Segment when message.SegmentId.HasValue && message.SegmentId.Value != Guid.Empty:
                scope = ScopeType.Segment;
                scopeId = message.SegmentId.Value.ToString("D");
                return true;
            default:
                scope = node.Scope;
                scopeId = string.Empty;
                return false;
        }
    }

    private static string CorrelationOf(IntegrationMessage message)
    {
        return string.IsNullOrWhiteSpace(message.CorrelationId)
            ? Guid.NewGuid().ToString("N")
            : message.CorrelationId;
    }

    private static async Task SendToQueueAsync<T>(IServiceProvider services, string queue, T message)
        where T : class
    {
        var provider = services.GetRequiredService<ISendEndpointProvider>();
        var endpoint = await provider.GetSendEndpoint(new Uri($"queue:{queue}", UriKind.Absolute)).ConfigureAwait(false);
        await endpoint.Send(message).ConfigureAwait(false);
    }

    private int MaxAttemptsFor(string stage)
    {
        if (_retry.PerStageMaxAttempts.TryGetValue(stage, out var perStage))
        {
            return perStage;
        }

        return _retry.LogicalStageMaxAttempts;
    }
}
