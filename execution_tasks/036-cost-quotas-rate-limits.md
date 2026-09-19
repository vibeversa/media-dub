# Task 36 — Cost Quotas Rate Limits

## Goal

Implement atomic cost reservations with reconciliation plus quotas, provider rate limiting, and tenant fairness caps.

## Context

Binding: CostService with price tables versioned, estimate + usage dims + provider-reported + reconciled actuals. Atomic reservation (project/segment/capability) before call, reconcile after, release/adjust on fail; fail QUOTA_EXCEEDED when reservation fails. Quotas: max active projects, per-day, cost/project, cost/segment, segment count, storage, concurrent stages per tenant. Active projects via partial unique/tx count; storage quota tx with artifact commit. Rate limiters per provider dims (requests/tokens/chars/audioSecs/concurrency) in config, Redis only for limiting, PG for quota correctness. Preflight at start + before expensive stages. Fairness caps active segment-stages + concurrent provider calls per tenant. Metrics + rejection records.

## Starting State

ICostGate/IQuotaGate/IRateGate stubs used by prior tasks. No real CostService/QuotaService/RateLimiter. Redis + PG available. Price tables not defined.

## Scope

Must implement: CostService + reservations repo, QuotaService, Redis RateLimiter, preflights, metrics. Must not implement: provider adapters, API billing UI.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/CostService.cs`: `PriceTable { string Version="1.0.0"; Dictionary<string,double> UnitPrices; }` with defaults `transcription_per_min=0.02, translation_per_char=0.00002, tts_per_char=0.00003, separation_per_min=0.05, local_per_min=0.01` (USD, document as deterministic defaults); `EstimateAsync(capability, usageDims)` → amount; `ReserveAsync(tenant,project,run,segment?,capability,amount)` atomic: `INSERT cost_reservations (Reserved) + check sum(reserved for project) ≤ MaxCostPerProject else throw QuotaExceededException`; `ReconcileAsync(reservationId, actualUsage, providerReported)` sets Actual + State Reconciled; `ReleaseAsync` on fail sets Released. All in PG transactions.
2. Create `QuotaService.cs`: `CheckAsync(tenant,project,dimension)` enforcing QuotaOptions (MaxActiveProjects via count where status non-terminal, MaxProjectsPerDay via createdAt, MaxCost*, MaxSegmentCount, MaxStorageBytes via sum ContentObject sizes tx with artifact commit hook `EnforceStorageOnCommit`, MaxConcurrentStages via count Running). Throw QuotaExceededException (429 QUOTA_EXCEEDED) with dimension detail. Storage check called in ArtifactService commit path (wire here).
3. Create `src/DubbingPlatform.Infrastructure/Redis/RateLimiter.cs`: `Task<bool> TryAcquireAsync(provider, dimension, amount)` using Redis Lua token-bucket per `RateLimitOptions` (requests/tokens/chars/audioSecs/concurrency keys `ratelimit:{tenant}:{provider}:{dim}`); return false → caller throws RateLimitedException (429 RATE_LIMITED) with Retry-After; record metric `ratelimit.rejections`.
4. Preflights: `PreflightAsync(project)` at processing start (sum estimated transcription+translation+tts for segment count estimate) + before TTS/translation per segment; wire into ProcessingController start + Translation/Tts services (replace stubs with real calls).
5. Fairness: `TenantFairnessGate` (same Redis): `maxActiveSegmentStages=QuotaOptions.MaxConcurrentStagesPerTenant` + `maxConcurrentProviderCalls=RateLimitOptions.Concurrency`; dispatcher + workers call before publish/call.
6. Metrics: `cost.reserved/reconciled`, `quota.rejections{dimension}`, `ratelimit.rejections{provider,dim}` for Task 38 dashboards.
7. Implement `ICostGate, IQuotaGate, IRateGate` for real with above classes; keep interfaces frozen.

## Requirements

- R1: Concurrent reservations cannot overspend (atomic).
- R2: Quota/rate return structured 429 codes.
- R3: Redis only for rate/fairness; PG for quota/cost correctness.
- R4: Preflights at start + expensive stages.
- R5: Records include estimate + usage + actuals.

## Edge Cases and Error Handling

- Race overspend → unique/tx serializes, loser gets QUOTA_EXCEEDED.
- Redis down → fail-open for rate? Decision: fail-closed for fairness? Document: rate limiter fail-open (allow + warning metric) to avoid blocking on Redis outage, quota/cost fail-closed (deny with STORAGE_UNAVAILABLE? No — INTERNAL_ERROR + alert) — document choice.
- Negative usage → validation error.
- Missing price version → default 1.0.0.

## Security and Safety Requirements

- Tenant-scoped keys; no cost PII leakage; atomicity prevents overspend abuse.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Cost/QuotaTests.cs` (PG+Redis Testcontainers): `Concurrent_Reservations_No_Overspend` (20 parallel, assert sum≤limit), `Quota_Exceeded_Structured`, `Rate_Dims_Enforced`, `Fairness_Cap_Enforced`, `Records_Have_Estimate_Usage`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~QuotaTests
```

## Completion Criteria

- Cost/quota/rate/fairness race-safe + observable; tests pass.

## Traceability

- Plan Section 25; Assumptions 64–67; Error checklist quota/rate structured.
