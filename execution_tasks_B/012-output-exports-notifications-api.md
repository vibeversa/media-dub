> **REVIEW FIX — SUPERSEDED (split):** this combined file is superseded by `012A-output-export-api.md` (output/export API) + `012B-notifications-api.md` (notifications API). New work goes to 012A/012B. Ownership: workspace projection stays in 008; output/export API in 012A; notifications API in 012B.

# Task 012 — Output, Exports, Notifications API

## Goal
Expose output summary with partial states, export lifecycle wiring, and notification read endpoints.

## Context
Users need one output call (video/audio/subs/transcript/translation/timeline/speakers/QC with completeness like 96/100 and generation state), export creation/download via signed URLs only, and durable notifications (list, unread count, mark-read/read-all) backed by the Task 002 projection.

## Starting State
Tasks 002 (Notification/ActivityEvent + projectors) and 004 (preview + QC evidence artifacts) done. Plan A export pipeline + blob storage exist. No output/exports/notifications HTTP endpoints.

## Scope
Included: `GET .../output`, export create/get/download wiring, `GET /api/v1/notifications|unread-count`, `POST .../read|read-all`, signed-URL-only delivery, OpenAPI updates.
Excluded: export pipeline internals (Plan A), notification projection (Task 002, reuse), SSE delivery (Task 013), frontend output/center (Tasks 033–034).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/OutputController.cs` under `/api/v1/projects/{projectId}`: `GET .../output` returns `{ state (Ready|Generating|Failed|Partial|Unavailable), completeness { ready, total } e.g. 96/100, items { video?, audio?, subtitles[], transcript?, translation?, timeline?, speakers?, qc { summary, issuesUrl? } }, warnings[], updatedAt }`. Every servable file exposed as time-boxed signed URL only — never internal paths, never `ContentObject` storage keys.
2. Wire exports in `src/DubbingPlatform.Api/Controllers/ExportsController.cs`: `POST .../exports` (body `{ format, profile }`, requires `Idempotency-Key`, `export.create`), `GET .../exports` (list, paginated), `GET .../exports/{exportId}` (status), `GET .../exports/{exportId}/download` (302 to signed URL, requires `export.download`; URL TTL ≤ 15 min). Export completion/failure publishes existing domain events (consumed by Task 002 projector + Task 013 SSE).
3. Create `src/DubbingPlatform.Api/Controllers/NotificationsController.cs`: `GET /api/v1/notifications` (paginated, filter `unreadOnly`, sort `createdAt desc`), `GET /api/v1/notifications/unread-count`, `POST /api/v1/notifications/{id}/read` (idempotent), `POST /api/v1/notifications/read-all` (idempotent, returns `markedCount`). Null-ProjectId quota/policy notifications visible at tenant level.
4. Partial-state rule: when `ready < total`, state is `Partial` with per-item `missing[]` reasons (e.g. `SEGMENT_PENDING`, `QC_BLOCKED`); `Generating` carries `progressApproximate`; `Failed` carries `errorCode` (no stack traces).
5. Enforce `project.view` (output/exports list), `export.create|download` (exports), and recipient-or-admin scoping on notifications (users see only their own + tenant-broadcast; cross-user id → 404).
6. Update OpenAPI for output/exports/notifications schemas, signed-URL shape, and error codes.

## Requirements
- R1: Output returns state + completeness + per-item availability in one call; partial (e.g. 96/100) explicitly labeled `Partial` with missing reasons.
- R2: No response contains internal storage paths or bucket keys (assertion test scans JSON).
- R3: Export download is a short-lived signed redirect (302); direct object URLs never exposed.
- R4: Notifications list/unread-count/read/read-all round-trip; read twice is idempotent; read-all returns count.
- R5: Export creation with duplicate idempotency key replays (single export row).
- R6: Cross-tenant output/export/notification id → 404.

## Edge Cases and Error Handling
- Output before any run → `Unavailable` (not 404) with `reason: NO_RUNS_YET`.
- Export requested while output Partial → 409 `OUTPUT_INCOMPLETE` unless `allowPartial: true` explicitly set.
- Download on expired/failed export → 409 `EXPORT_NOT_READY`.
- Read-all with zero unread → 200 `{ markedCount: 0 }`.

## Security and Safety Requirements
- Tenant isolation everywhere; notification recipient scoping (no reading another user's notifications).
- Signed URLs tenant-scoped, ≤ 15 min TTL, single-resource; logged as issuance events (URL value never logged).
- Export formats allowlisted server-side; path traversal in format/profile rejected with 400.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Output/OutputNotificationsApiTests.cs`: output full/partial/generating/failed/unavailable shapes, no-internal-path scan, export create/idempotency/list/download-redirect TTL, partial-export guard, notification list/unread/read/read-all idempotency, recipient isolation, cross-tenant 404.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL; storage emulator or fake URL signer).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~OutputNotificationsApiTests
```

## Completion Criteria
- Output/exports/notifications endpoints with signed-URL-only delivery exist; `OutputNotificationsApiTests` pass; partial output and idempotent exports provably correct.

## Traceability
- Plan B §9.8, §9.9, §12.15–§12.16. Depends on Tasks 002, 004.

