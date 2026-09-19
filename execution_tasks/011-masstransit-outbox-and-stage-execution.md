# Task 11 — MassTransit Outbox and Stage Execution

## Goal

Configure MassTransit with RabbitMQ/in-memory transports, EF outbox/inbox, workload queues, BaseConsumer, and StageExecutionService with atomic claiming and lease-fenced commits.

## Context

Binding: RabbitMQ durable queues + error/_skipped DLQ; EF outbox+inbox; workload queues control.orchestration/media.preparation/media.render/ai.provider/ai.gpu/export/maintenance (no per-micro-stage queues). Stage identity (Run,StageType,ScopeType,ScopeId,Attempt); leases with fencing tokens incremented per grant; conditional commit SQL `UPDATE stage_executions SET status=...,completed_at=...,output_artifact_ids=... WHERE id=@id AND lease_owner=@owner AND lease_token=@token AND status='Running'`; zero rows → discard, no downstream publish, mark aborted. At-least-once + idempotent consumers. Transport retry only for transport failures; Polly for provider HTTP; fallback per route; logical per stage; manual via API. Provider rate-limit → delayed retry; invalid/config errors fail fast. Cancellation blocks scheduling; workers re-check run state before commit.

## Starting State

Message contracts exist (15 types + QueueNames + version policy). AppDbContext has outbox/inbox tables + stage tables. No bus config, no consumers, no StageExecutionService.

## Scope

Must implement: bus config, outbox/inbox wiring, BaseConsumer, StageExecutionService (9 methods), lease SQL, idempotency claim. Must not implement: saga/StageGraph/barriers/dispatcher/sweeper (next task), business workers.

## Instructions

1. In `src/DubbingPlatform.Infrastructure/Messaging/MassTransitConfig.cs`: `services.AddMassTransit(x=>{ x.AddEntityFrameworkOutbox<AppDbContext>(o=>{o.QueryDelay=TimeSpan.FromSeconds(1);o.UsePostgres();o.UseBusOutbox();}); x.UsingRabbitMq((ctx,cfg)=>{ cfg.Host(GetRabbitHost()); cfg.ReceiveEndpoint(QueueNames.ControlOrchestration,...); /* repeat for 7 queues */ cfg.UseMessageRetry(r=>r.Interval(3, TimeSpan.FromSeconds(5))); cfg.UseInMemoryOutbox(); }); })` plus `AddTransactionalInbox`. Support `Transport:Provider=InMemory` for fast profile: `x.UsingInMemory()` when configured. Durable queues (`Durable=true, AutoDelete=false`), error queue `_error`, skipped `_skipped`. Schema-mismatch → `Send to _skipped` + metric.
2. Create `src/DubbingPlatform.Infrastructure/Messaging/BaseConsumer<TMessage>.cs` where TMessage:IntegrationMessage: steps in order: extract CorrelationId→LogContext; check SchemaVersion supported else move _skipped; validate Tenant/Project/Run Guids non-empty else _skipped; load run, if Cancelling/Cancelled → ack without work; claim-or-return StageExecution via service; enforce idempotency (unique constraint catch → return existing); renew lease; execute abstract `HandleAsync`; re-check lease before commit.
3. Create `src/DubbingPlatform.Application/Services/StageExecutionService.cs` with methods `ScheduleAsync, ClaimAsync, StartAsync, CompleteAsync, FailAsync, CancelAsync, MarkReviewRequiredAsync, RenewLeaseAsync, ReleaseLeaseAsync, RecoverStaleAsync` (signatures: `(Guid tenant, Guid project, Guid run, string stageType, string scopeType, string scopeId, Guid? segmentId, int attempt, string owner, TimeSpan leaseTtl, CancellationToken)` etc.). `ClaimAsync`: transaction: `SELECT ... FOR UPDATE` or insert; on unique violation select existing and return it with `IsNew=false`. `LeaseToken = Guid.NewGuid():N`, `LeaseTokenVersion++`, `LeaseExpiresAt=UtcNow+Ttl`.
4. Conditional commit in `CompleteAsync/FailAsync`: raw SQL with lease_owner/token/status check as above; if 0 rows → throw `LeaseLostException` (maps to LEASE_LOST) and caller discards result, publishes nothing.
5. `RenewLeaseAsync`: extends expiry only if owner+token match and status Running. `RecoverStaleAsync`: `WHERE status='Running' AND lease_expires_at < now()` using partial index, mark RetryPending + increment attempt (bounded by RetryOptions).
6. Register service + MassTransit in Api and Workers Programs. Document retry ownership table in `RETRY_OWNERSHIP.md` at `src/DubbingPlatform.Infrastructure/Messaging/`.

## Requirements

- R1: Bus supports RabbitMq + InMemory switch.
- R2: 7 workload queues durable; _skipped/_error exist.
- R3: Claim atomic, duplicate returns existing.
- R4: Commit conditional on lease; stale cannot commit.
- R5: Outbox+inbox enabled.

## Edge Cases and Error Handling

- Duplicate message → one execution (unique constraint path).
- Lease expired → RecoverStale; active lease never recovered.
- Zero-row commit → LEASE_LOST, no downstream publish.
- Cross-tenant message (tenant mismatch with run): reject to _error + metric, no side effects.
- Poison (repeated deserialization fail) → _skipped after 3 redeliveries.

## Security and Safety Requirements

- Validate tenant before side effects. No secrets in headers/logs. Outbox ensures no lost messages on DB commit.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Messaging/StageExecutionTests.cs` (Testcontainers PG+Rabbit or InMemory): `Duplicate_Message_Produces_One_Execution`, `Stale_Worker_Cannot_Commit` (old token commit throws LeaseLost), `Active_Lease_Not_Recovered`, `Expired_Lease_Recovered`, `Cancellation_Blocks_Scheduling`. Unit `LeaseSqlTests` asserting SQL string contains lease_owner/token/status predicates.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~StageExecutionTests
dotnet test --filter FullyQualifiedName~LeaseSqlTests
```

## Completion Criteria

- Bus + outbox + claim/lease + BaseConsumer work; tests pass.

## Traceability

- Plan Section 4 actions 5–10,13–14,29–36; Assumptions 8,10,30–33,45–48; Error checklist duplicate/lease/cancel/outbox/inbox.
