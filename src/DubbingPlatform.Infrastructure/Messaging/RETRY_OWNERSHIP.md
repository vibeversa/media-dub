# Retry Ownership

Single owner per failure class. No layer retries another layer's failures.

| Failure class | Owner | Policy | Exhaustion |
|---|---|---|---|
| Transport (broker disconnect, DB transient, send/publish I/O) | MassTransit `UseMessageRetry` | `Interval(3, 5s)` on every receive endpoint (RabbitMQ and InMemory) | Fault to `_error` |
| Schema mismatch (`SchemaVersion` unsupported) | `BaseConsumer` | No retry; `Send` to `_skipped` + `messaging.schema_mismatch_total` | Terminal in `_skipped` |
| Empty Tenant/Project/Run ids | `BaseConsumer` | No retry; `Send` to `_skipped` + `messaging.schema_mismatch_total` | Terminal in `_skipped` |
| Cross-tenant message (run tenant differs) | `BaseConsumer` | No retry, no side effects; `Send` to `_error` + `messaging.cross_tenant_reject_total` | Terminal in `_error` |
| Poison (repeated deserialization failure) | MassTransit error pipeline | Transport retry `Interval(3, 5s)`, then fault to `_error` | Terminal in `_error`; operators replay or move to `_skipped` manually |
| Provider HTTP (429/5xx, timeouts) | Polly `providers` HttpClient (`AddStandardResilienceHandler`) | Retry 3x exponential backoff + jitter, circuit breaker, 30s total timeout | Throw; stage `FailAsync` with `IsRetryable` per outcome |
| Provider rate-limit (429 with retry-after) | Provider adapter (tasks 014+) | Delayed retry per `Retry.RateLimitDelaySec` | Stage stays `Running` under renewed lease; sweeper re-queues on expiry |
| Provider invalid/config errors | Provider adapter | Fail fast, no retry | Stage `FailAsync` (`IsRetryable=false`) |
| Fallback per route | Provider router (task 014) | Up to `Retry.FallbackMaxAttempts` alternate providers | Stage `FailAsync` when routes exhausted |
| Logical per stage | Saga/dispatcher (task 012) | Up to `Retry.LogicalStageMaxAttempts` (or `PerStageMaxAttempts[stage]` override) attempts | `RunFailed` or manual review |
| Manual via API | API + `RunStateMachine`/`StageStateMachine` | Up to `Retry.ManualRetryMaxAttempts`; `Failed->Scheduled/Running` only via explicit manual retry | Terminal otherwise |
| Lease lost (zero-row conditional commit) | `StageExecutionService` + `BaseConsumer` | No retry of the lost unit; throw `LeaseLostException` (`LEASE_LOST`), discard result, publish nothing | Sweeper `RecoverStaleAsync` re-queues expired `Running` leases as `RetryPending` (bounded) or `Failed` when the retry budget is exhausted |
| Run cancelling/cancelled | `BaseConsumer` + `StageExecutionService` | Cancellation blocks scheduling (`ConflictException`); workers re-check run state before commit and ack without work | Terminal (`Cancelled`) |
| Expired lease takeover | `StageExecutionService.ClaimAsync` duplicate path | Fenced `UPDATE ... WHERE id=@id AND lease_owner=@old AND lease_token=@old AND status IN ('Running','RetryPending') AND lease_expires_at < now` rotates owner/token; exactly one claimant wins, active leases never stolen | Winner proceeds as `Running`; loser fails fencing later |

## Retry Budgets

| Layer | Option key | Default | Per-stage override | Exhaustion |
|---|---|---|---|---|
| Transport (MassTransit) | `Retry.TransportMaxAttempts` | 5 | — | Fault to `_error` |
| Provider HTTP (Polly) | `Retry.ProviderRequestMaxAttempts` | 3 | — | Stage `FailAsync` (`IsRetryable` per outcome) |
| Fallback per route | `Retry.FallbackMaxAttempts` | 2 | — | Stage `FailAsync` when routes exhausted |
| Logical per stage (saga/dispatcher/sweeper) | `Retry.LogicalStageMaxAttempts` | 3 | `Retry.PerStageMaxAttempts[stage]` | `RunFailed` (`Failed`) or manual review |
| Manual via API | `Retry.ManualRetryMaxAttempts` | 3 | — | Terminal otherwise |
| Rate-limit delay | `Retry.RateLimitDelaySec` | 30 | — | Delayed redelivery; sweeper backstops |

Per-stage overrides in `Retry.PerStageMaxAttempts` are keyed by `StageType` name
(`"Transcription"`, `"VoiceGeneration"`, …) and enforced uniformly in the saga
fail path, the sweeper recovery (`RecoverStaleAsync`), and the dispatcher pump.
Fail-fast codes (`VALIDATION_FAILED`, `PROVIDER_CONFIGURATION_ERROR`,
`POLICY_DENIED`, `CONSENT_REQUIRED`, `PIPELINE_INVARIANT_VIOLATION`) never
consume budget; rate-limit codes (`PROVIDER_RATE_LIMITED`, `RATE_LIMITED`)
consume a delayed retry after `RateLimitDelaySec`.

## Notes

- Delayed scheduling uses the MassTransit delayed-message scheduler
  (`AddDelayedMessageScheduler` + `UseDelayedMessageScheduler`, primary for
  `StageLeaseTimeout` at lease TTL + 30s and for rate-limit retries). The full
  RabbitMQ profile needs the delayed-message-exchange plugin; without a
  scheduler, sends degrade to `false` and the 60s sweeper covers recovery.
- Dead-letter routing is explicit application sends: schema/version/identity
  problems and handler poison go to `_skipped` (+ `messaging.schema_mismatch_total`
  and/or `dlq.depth`); missing runs and cross-tenant messages go to `_error`
  (+ `dlq.depth`). Transport-exhausted faults additionally follow the MassTransit
  default per-endpoint fault pipeline. Alert on `dlq.depth` plus broker
  error-queue depth (Task 38).

- At-least-once everywhere; exactly-once nowhere. Idempotency comes from
  the stage unique index `(processing_run_id, stage_type, scope_type,
  scope_id, attempt)` (duplicate claim returns existing, `IsNew=false`) and
  the EF outbox/inbox tables (`outbox_message`, `outbox_state`,
  `inbox_state`).
- Lease fencing: `UPDATE ... WHERE id=@id AND lease_owner=@owner AND
  lease_token=@token AND status='Running'`. Zero rows means loss.
- Recovery query `WHERE status='Running' AND lease_expires_at < now`
  uses the partial `status='Running'` index plus
  `(processing_run_id, status, lease_expires_at)`.
- Queues are workload classes (`control.orchestration`,
  `media.preparation`, `media.render`, `ai.provider`, `ai.gpu`, `export`,
  `maintenance`), never per micro-stage. `_skipped` holds version/skipped
  messages; `_error` holds faults/poison and cross-tenant rejects.
