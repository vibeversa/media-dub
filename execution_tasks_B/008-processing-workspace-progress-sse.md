# Task 008 — Processing, Workspace, Progress, SSE Shell

## Goal
Expose processing lifecycle, workspace aggregate, progress, and SSE stream endpoints with idempotency and authz.

## Context
The workspace is the live project screen: starting/cancelling/retrying runs, one-call workspace aggregate (no N+1), approximate progress, and an SSE stream that acts only as invalidation hints (Task 026 polls on hint). This task builds the endpoint shell; the SSE event-type freeze happens in Task 013.

## Starting State
Task 001 done. Plan A processing-run orchestration, stage machine, and progress tracking exist. No workspace aggregate DTO or processing HTTP shell.

## Scope
Included: `POST|GET .../processing`, `POST .../cancel|retry`, `GET .../progress|stream|workspace|activity|output|quality`, workspace aggregate DTO, idempotency-key support, per-endpoint authz.
Excluded: SSE envelope/event-type freeze + payload allowlist (Task 013), segments/voices/reviews/output bodies (Tasks 009–012), frontend workspace/SSE client (Tasks 025–026).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/ProcessingController.cs` under `/api/v1/projects/{projectId}`: `POST .../processing` (start run; requires `Idempotency-Key` header, returns 202 + `runId`), `GET .../processing` (run list, paginated), `POST .../processing/{runId}/cancel` (202, idempotent), `POST .../processing/{runId}/retry` (new run linked via `RetryOfRunId`, requires fresh idempotency key).
2. Create `src/DubbingPlatform.Api/Controllers/WorkspaceController.cs`: `GET .../workspace` (aggregate DTO below, single handler call), `GET .../progress` (approximate percent + current stage only), `GET .../stream` (SSE transport shell — headers `text/event-stream`, auth via query `access_token` mapped to same policy; event serialization deferred to Task 013), `GET .../activity|output|quality` (thin projections delegating to Task 002/012/008-quality services, paginated where lists).
3. Define `WorkspaceDto` in `src/DubbingPlatform.Application/Workspace/WorkspaceDto.cs`: `{ project, media, run {id, status, configHash}, phase, stage, progress {percentApproximate, currentStage, updatedAt}, review {pendingCount, oldestWaitingAt}, warnings[], output {state, completeness}, cost {runCost, monthToDate}, activity {recent[] max 10}, permissions {allowedActions[]} }`. One handler, one DB round-trip batch (no N+1 — test asserts query count ceiling).
4. Implement idempotency in `src/DubbingPlatform.Application/Processing/ProcessingIdempotency.cs`: `(TenantId, Idempotency-Key)` → existing run returned with 200 + `Idempotent-Replayed: true`; keys expire after 24h; retry requires a NEW key (reuse of a completed-start key for different payload → 422 `IDEMPOTENCY_KEY_REUSED`).
5. Enforce authz per endpoint: `processing.start` (start/retry), `processing.cancel` (cancel), `project.view` (all GETs); archived project blocks start/retry with 409 `PROJECT_ARCHIVED`.
6. Update OpenAPI for processing/workspace/progress/stream schemas with 202 + 409 examples.

## Requirements
- R1: Start with idempotency key twice → same `runId`, second response flagged replayed, only one run created.
- R2: `GET workspace` returns all ten sections in one call with bounded queries (≤ N+1 ceiling asserted in test).
- R3: Cancel is idempotent (cancel twice → 202 both, single terminal transition); cancel on terminal run → 409 `RUN_ALREADY_TERMINAL`.
- R4: `GET progress` percent is labeled approximate (field name `percentApproximate`) and never drives billing.
- R5: Archived project start/retry rejected; active-run conflict on start returns 409 `RUN_ALREADY_ACTIVE` unless `force` with `processing.retry` permission.
- R6: SSE `stream` requires auth and tenant-scoped run; unauthenticated → 401, cross-tenant → 404.

## Edge Cases and Error Handling
- Missing `Idempotency-Key` on start → 400 `IDEMPOTENCY_KEY_REQUIRED`.
- Retry of failed run copies config hash; settings changed since → 409 `CONFIG_CHANGED_SINCE_RUN` with current hash.
- Progress on run with no stages yet → `{ percentApproximate: 0, currentStage: null }`, not 404.
- Stream disconnect → client resumes with `Last-Event-ID` (shell accepts header; replay semantics frozen in Task 013).

## Security and Safety Requirements
- Tenant + membership checks on every route; run IDs unguessable (GUID) and never enumerated cross-tenant.
- SSE token via query param is single-use-scoped (short TTL) and never logged.
- Cost fields are display approximations; billing source of truth stays in Plan A ledger.
- All mutations audited with correlationId + idempotency key.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Workspace/WorkspaceProgressTests.cs`: start idempotency + replay flag, missing-key 400, cancel idempotency + terminal 409, retry config-changed 409, workspace single-call shape + query-count ceiling, progress approximate field, archived-block, authz matrix (viewer cannot start), stream auth/tenant checks.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~WorkspaceProgressTests
```

## Completion Criteria
- Processing/workspace/progress/stream endpoints + aggregate DTO + idempotency exist; `WorkspaceProgressTests` pass; duplicate start provably creates one run.

## Traceability
- Plan B §9.4, §9.11, §12.6–§12.8.
