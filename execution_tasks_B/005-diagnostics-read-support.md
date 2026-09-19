# Task 005 — Provider Health and Diagnostics Read Support

## Goal
Add read-only diagnostics query services for provider health, queues, leases, orphans, and review backlog.

## Context
Admin/operator UI needs a secret-free, elevated-authz read layer over provider routing, queue depth, DLQ, stale leases, orphan artifacts, review backlog, and worker health. These are aggregations over Plan A runtime state — no new orchestration, no mutations.

## Starting State
Task 001 done. Plan A provider router, outbox/inbox, lease manager, worker host, and review store exist. No diagnostics query services or DTOs.

## Scope
Included: read-only query services + secret-free DTOs, elevated-authz hooks (service-layer role check), correlation IDs on every DTO.
Excluded: HTTP endpoints (Task 013), mutations/retry/DLQ-replay actions, frontend admin UI (Task 036), metrics pipeline (Task 038).

## Instructions
1. Create `src/DubbingPlatform.Application/Diagnostics/ProviderHealthQueryService.cs`: aggregates per-provider `Status` (Healthy/Degraded/Down/Unknown), `LatencyMsP95`, `ErrorRate`, `LastSuccessAt`, `ActiveRoutes`, `CircuitBreakerState` from existing router health probes. Never include keys, endpoints with credentials, or raw payloads.
2. Create `src/DubbingPlatform.Application/Diagnostics/QueueDiagnosticsService.cs`: returns queue depth per queue, DLQ depth + oldest-entry age, dead-letter reason breakdown (top 10 codes), all from existing MassTransit/outbox stores.
3. Create `src/DubbingPlatform.Application/Diagnostics/LeaseOrphanService.cs`: `GetStaleLeasesAsync` (lease older than configured TTL + heartbeat missed), `GetOrphanArtifactsAsync` (ContentObject with no owning run/segment reference), each with `correlationId`, `lastHeartbeatAt`, `ownerHint` (run/project id only, no secrets).
4. Create `src/DubbingPlatform.Application/Diagnostics/ReviewBacklogService.cs`: counts by severity/status/oldest-waiting, per-project breakdown, from existing review store.
5. Create `src/DubbingPlatform.Application/Diagnostics/WorkerHealthService.cs`: per-worker `Status`, `LastHeartbeatAt`, `ActiveJobs`, `Version` from existing worker heartbeat table.
6. Add DTOs in `src/DubbingPlatform.Application/Diagnostics/Dto/`: `ProviderHealthDto`, `ProviderRouteDto`, `QueueDepthDto`, `DlqSummaryDto`, `StaleLeaseDto`, `OrphanArtifactDto`, `ReviewBacklogDto`, `WorkerHealthDto` — every DTO carries `correlationId`; no `Secret`, `ApiKey`, `ConnectionString`, `InternalPath`, or `RawPayload` fields exist.
7. Add service-layer guard `RequireDiagnosticsViewerAsync(TenantId, UserId)` (role: TenantAdmin/Operator or `diagnostics.view` permission) called at the top of every query method; throws `ForbiddenException(code: DIAGNOSTICS_FORBIDDEN)` on failure.

## Requirements
- R1: Every query method enforces elevated authz before touching data (covered by unit test with viewer vs non-viewer).
- R2: No DTO contains secrets, connection strings, signed URLs, internal paths, or raw provider payloads (assertion test scans serialized JSON).
- R3: Every DTO/response carries a `correlationId` (generated per call if caller omits one).
- R4: Stale-lease cutoff uses configured TTL (not hardcoded); test pins TTL via options override.
- R5: All queries are read-only (no INSERT/UPDATE/DELETE; verified by EF read-only/no-tracking + test asserting zero change-tracker entries).

## Edge Cases and Error Handling
- Provider never probed → status `Unknown`, not `Down`.
- Empty DLQ → return zero depth + null oldest age (not 404).
- Orphan scan on huge table → paginated (default 50, max 200) with `hasMore` cursor.
- Missing worker heartbeat → worker listed as `Unknown` with `lastHeartbeatAt: null`.

## Security and Safety Requirements
- Tenant isolation: operators see only their tenant; cross-tenant query returns empty/404, never neighbor data.
- Elevated authz defense-in-depth (service layer now, endpoints re-check in Task 013).
- Logs contain correlation IDs + counts only, never payload bodies or lease tokens.

## Testing
- Create `tests/DubbingPlatform.UnitTests/Diagnostics/DiagnosticsReadTests.cs`: authz denial for non-viewer, secret-free serialization scan, correlationId presence, stale-lease TTL boundary, empty-DLQ shape, pagination cap, read-only (no tracked changes).
- Type: unit (mocked stores/options).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~DiagnosticsReadTests
```

## Completion Criteria
- All five query services + DTOs + authz guard exist; `DiagnosticsReadTests` pass; serialized DTOs contain no secret-bearing fields.

## Traceability
- Plan B §8.8, §8.9, §9.10.
