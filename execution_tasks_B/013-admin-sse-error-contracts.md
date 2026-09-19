# Task 013 — Admin/Diagnostics API, SSE Contract Freeze, Error Mapping

## Goal
Freeze the SSE envelope with 14 event types and payload allowlist plus admin/diagnostics reads and backend error envelope.

## Context
SSE is the live-update transport for the whole frontend (Task 026 consumes it as invalidation hints) and admin/operator surfaces need elevated read endpoints. Both must be frozen now: exact envelope fields, the closed set of 14 event types, a payload allowlist that can never carry secrets/URLs/raw payloads/lease data, and a uniform error envelope with a documented code→HTTP mapping.

## Starting State
Task 005 done (diagnostics query services + secret-free DTOs). Task 008 done (SSE `stream` transport shell). No admin controllers, no frozen SSE contract, no uniform error envelope.

## Scope
Included: `GET /api/v1/admin/*` + diagnostics reads, SSE envelope + 14 types + allowlist + serialization tests, error envelope `{code,message,correlationId,details}` + mapping table + middleware.
Excluded: diagnostics mutations/replay, frontend SSE client (Task 026), frontend admin UI (Task 036), metrics (Task 038).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/AdminController.cs`: `GET /api/v1/admin/usage|quotas|provider-health|provider-routes` and `GET /api/v1/admin/diagnostics/queues|dlq|leases|orphans|review-backlog` — thin wrappers over Task 005 services; every route requires `admin.manage` or `diagnostics.view` (service guard re-checks); paginate leases/orphans/DLQ (default 50/max 200).
2. Freeze SSE contract in `src/DubbingPlatform.Api/Sse/SseEnvelope.cs`: `{ eventId, schemaVersion: 1, eventType, tenantId, projectId?, processingRunId?, occurredAt, payload }`. Closed event-type set (exactly these 14): `project.status_changed`, `run.status_changed`, `stage.started`, `stage.progress`, `stage.completed`, `stage.failed`, `stage.review_required`, `review.created`, `review.resolved`, `export.created`, `export.completed`, `export.failed`, `notification.created`, `output.ready`. Unknown types fail serialization tests.
3. Enforce payload allowlist in `src/DubbingPlatform.Api/Sse/SsePayloadPolicy.cs`: allowed = IDs, status enums, approximate percents, counts, machine-readable codes, timestamps; NEVER = secrets, tokens, signed URLs, internal paths, raw provider payloads, lease tokens/heartbeats, transcript/translation bodies. Add unit scan asserting serialized frames contain none of the forbidden keys (`signedUrl, token, secret, apiKey, connectionString, internalPath, rawPayload, leaseToken`).
4. Implement error envelope in `src/DubbingPlatform.Api/Errors/ApiError.cs` + `ErrorMappingMiddleware`: `{ code, message (human, no internals), correlationId, details? (field errors only) }`. Mapping table: validation→400, auth→401 (`TOKEN_EXPIRED, TOKEN_REUSED, INVALID_CREDENTIALS`), forbidden→403, not-found→404 (cross-tenant included), conflict→409 (`SELECTION_CONFLICT, REVIEW_VERSION_CONFLICT, SETTINGS_LOCKED_ACTIVE_RUN, RUN_ALREADY_ACTIVE, PREVIEW_STATE_CONFLICT`), quota→429, downstream/provider→502 (`PREVIEW_PROVIDER_TIMEOUT`), unknown→500 `INTERNAL` (message generic, detail in logs only).
5. Wire `Last-Event-ID` resume on `GET .../stream`: server accepts cursor, replays missed envelope headers only (no payload backfill beyond last 100 events), duplicates tolerated by client.
6. Update OpenAPI: admin schemas, SSE envelope + event-type enum + allowlist note, error envelope + code catalog.

## Requirements
- R1: Admin/diagnostics routes enforce elevated authz (viewer 200, non-viewer 403, cross-tenant 404).
- R2: Exactly 14 SSE event types; emitting any other type fails tests (closed-enum assertion).
- R3: Every SSE frame carries all eight envelope fields; `schemaVersion` is 1.
- R4: Serialized SSE payloads contain no forbidden keys (automated scan passes).
- R5: Every error response matches `{code,message,correlationId,details?}` and the mapping table (contract test per code).
- R6: 500s never leak stack traces or internals (message generic; correlationId links to server log).

## Edge Cases and Error Handling
- DLQ/leases/orphans empty → 200 zero-shape (not 404).
- Stream replay beyond 100-event window → 200 with `replayTruncated: true` header hint; client falls back to polling (Task 026).
- Unknown admin sub-path → 404 `ADMIN_ROUTE_UNKNOWN` (not generic 404 page).
- SSE payload exceeding 64KB → dropped + metric `sse.payload_dropped_total`, stream stays open.

## Security and Safety Requirements
- Tenant isolation + elevated authz on every admin route (defense in depth: controller + Task 005 service guard).
- No secrets/URLs/raw payloads/lease data in SSE or admin DTOs (scan tests).
- CorrelationId on every error + SSE frame; logs carry IDs, never bodies.
- Admin reads audited (who viewed what scope + when).

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Admin/AdminSseErrorContractTests.cs`: authz matrix, envelope-field presence, 14-type closed set, payload-forbidden-key scan, error-envelope shape + mapping-table cases (400/401/403/404/409/429/502/500), Last-Event-ID resume, oversize-payload drop.
- Type: integration (WebApplicationFactory) + unit scan for payload policy.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~AdminSseErrorContractTests
```

## Completion Criteria
- Admin endpoints + frozen SSE contract + error envelope exist; `AdminSseErrorContractTests` pass; forbidden-key scan and closed-type assertion green.

## Traceability
- Plan B §9.10, §9.11, §9.12, §12.19. Depends on Tasks 005, 008.
