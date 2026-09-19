# Task 12 — Saga DAG Barriers and Recovery

## Goal

Implement the scoped DAG orchestration: ProcessingRunSaga, StageGraph, barrier ledgers, bounded dispatcher, delayed lease timeouts, sweeper, and explicit DLQ behavior.

## Context

Binding: orchestration via MassTransit saga + durable PG; workers execute, never decide global progression; sweeper safety-only (60s), primary timeout via delayed messages. DAG: MediaValidation→MediaAnalysis→AudioPreparation→SourceSeparation→Vad (after sep completed-or-skipped)→SegmentBuild→Diarization→Transcription→ContextBuild→Translation; VoiceAssignment after Diarization; VoiceGeneration after Translation+VoiceAssignment; TimingOptimization→TimelineAssembly→AudioMixing→QualityControl→Render. Scopes: run/project = MediaValidation,MediaAnalysis,AudioPreparation,SourceSeparation,Vad,SegmentBuild,Diarization,TimelineAssembly,AudioMixing,QualityControl,Render; speaker=VoiceAssignment; window=ContextBuild; segment=Transcription,Translation,VoiceGeneration,TimingOptimization,segment-QC. Eligible unit states Completed/Skipped; terminal Completed/Skipped/Failed/ManualReviewRequired/Cancelled. Barriers via StageUnitCompletion ledger + RunStageSummary counters (no SELECT COUNT per event; ledger unique prevents double-count). Dispatcher bounded batches respecting concurrency/rate/cost. StageLeaseTimeout delayed message + WorkerRecoverySweeper 60s via partial index. Retry budgets configurable per stage/capability. DLQ: poison→_skipped, exhausted→_error, alerts on depth.

## Starting State

Bus + BaseConsumer + StageExecutionService + lease fencing exist. Message contracts exist. No saga, no StageGraph, no barrier logic, no dispatcher, no sweeper.

## Scope

Must implement: ProcessingRunSaga, StageGraph metadata, barrier increment, dispatcher, lease-timeout handler, sweeper, retry-budget enforcement, DLQ wiring. Must not implement: business stage logic (later tasks).

## Instructions

1. Create `src/DubbingPlatform.Application/Orchestration/StageGraph.cs`: static `IReadOnlyList<StageNode> Nodes` where `StageNode { StageType StageType; ScopeType Scope; string[] Prerequisites; string FanOutRule; string CompletionCriterion; string FailureAggregation; string SkipPolicy; string ReviewPolicy; string RetryPolicy; bool IsOnDemand; }`. Encode DAG + scopes exactly as Context. Example: `new(StageType.MediaValidation, ScopeType.Project, [], "single", "one-unit", "fail-fast", "never-skip", "no-review", "stage-budget", false)`; Transcription `ScopeType.Segment, prereqs [Diarization], fanout per-segment, completion all-units-or-review`.
2. Create `src/DubbingPlatform.Infrastructure/Orchestration/ProcessingRunSaga.cs` : MassTransit `SagaStateMachineInstance` with states Pending,Running,Cancelling,Cancelled,Completed,Failed,ManualReviewRequired + events RunStarted,StageCompleted,StageFailed,StageCancelled,StageReviewRequired,ReviewResolved,RunCancelledRequested. On RunStarted→Running + schedule first stage (MediaValidation). On StageCompleted→ check barrier via summary; if complete schedule successors per StageGraph; if all terminal and no blocking reviews→Completed+publish RunCompleted. On StageFailed→ apply retry budget or mark Failed+RunFailed. On RunCancelledRequested→Cancelling, block new scheduling (check flag before every schedule). On ReviewResolved→ resume blocked stage.
3. Barrier: `src/DubbingPlatform.Application/Services/BarrierService.cs` `RecordUnitCompletionAsync(run,stage,scope,scopeId,execId,unitState)`: insert StageUnitCompletion (catch unique → ignore double-count), atomic `UPDATE run_stage_summaries SET completed_units=... WHERE ...` + publish StageCompleted once when completed+skipped==expected. Never SELECT COUNT per event except to set ExpectedUnits initially.
4. Dispatcher `src/DubbingPlatform.Infrastructure/Orchestration/WorkDispatcher.cs`: `DispatchSegmentWorkAsync(run, stage, batchSize=50)`: query pending segments, publish StageWorkRequested bounded batches, respect `QuotaOptions.MaxConcurrentStagesPerTenant`, Redis rate-limit check, CostService reservation check (interfaces defined here as `ICostGate, IRateGate` with stub allow implementations; real logic Task 36 but contract frozen: `Task<bool> CanProceedAsync(...)`).
5. Delayed timeout: when stage starts, `ScheduleSend<StageLeaseTimeout>(delay=leaseTtl+30s)`; handler checks token+status, recovers only if still stale.
6. `src/DubbingPlatform.Workers/Services/WorkerRecoverySweeper.cs` : BackgroundService every 60s, query `WHERE status='Running' AND lease_expires_at < now()` (partial index), call RecoverStaleAsync; log count; never recover active leases.
7. Retry budgets: `RetryOptions.PerStageMaxAttempts` enforced in saga Fail path; transport/Polly/fallback/logical/manual ownership documented (reuse RETRY_OWNERSHIP.md, extend with budgets table).
8. DLQ: configure `cfg.ReceiveEndpoint(...).ConfigureDeadLetter(x=>x.SetQueueName("_error"))`; poison→_skipped via BaseConsumer catch; alert metric `dlq.depth`.

## Requirements

- R1: DAG + scopes exactly as specified.
- R2: Barrier exactly-once via ledger unique.
- R3: Sweeper 60s + delayed timeout both exist; sweeper never replaces timeout.
- R4: Cancellation blocks scheduling.
- R5: Budgets configurable per stage/capability.

## Edge Cases and Error Handling

- Duplicate completion → ledger unique prevents double increment.
- Worker death → delayed timeout or sweeper recovers.
- Invalid input/config errors → fail fast, no retry.
- Rate-limit errors → delayed retry (schedule with delay, not immediate).
- Schema mismatch → _skipped + metric.

## Security and Safety Requirements

- Saga validates tenant per event; cross-tenant events rejected. No secrets in saga state.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Orchestration/BarrierTests.cs`: `Duplicate_Completion_Does_Not_Double_Count`, `Barrier_Advances_Exactly_Once`, `Cancellation_Blocks_New_Work`, `Expired_Lease_Recovered_Active_Not`, `Schema_Mismatch_Goes_To_Skipped`, `Dlq_Routes_Poison`. Use InMemory transport + PG Testcontainers.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~BarrierTests
```

## Completion Criteria

- Saga+DAG+barrier+dispatcher+timeouts+sweeper+DLQ work; tests pass.

## Traceability

- Plan Section 4 actions 15–28, 37; Assumptions 29–32,39–40,44–54; Error checklist worker-death/duplicate/AI-bounds/cancel.
