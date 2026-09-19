# Task 012B — Notifications API

**Required/Optional:** Required
**Complexity:** S

## Goal
Expose the durable notifications list, unread count, and read-state endpoints.

## Context
Split from oversized Task 012 (output/export + notifications). Notifications are the durable user-facing projection built by Task 002; this task is its HTTP surface for the notification center (034) and SSE invalidation (026). No export/output logic lives here.

## Starting State
Depends on Task 002 (Notification entity, projector, dedup). Task 012 (combined) is superseded by 012A + 012B.

## Scope
Included: `GET /notifications`, `GET /notifications/unread-count`, `POST /{id}/read`, `POST /read-all`; tenant/user scoping; deep-link fields; OpenAPI updates.
Excluded: output/export API (012A), notification projection logic (002), notification center UI (034), SSE envelope (013).

## Instructions
1. Implement in `src/DubbingPlatform.Api/Controllers/NotificationsController.cs`: `GET /api/v1/notifications` (cursor pagination, `unreadOnly` filter, newest-first; excludes expired), `GET /api/v1/notifications/unread-count` (lightweight count query, same scope), `POST /api/v1/notifications/{notificationId}/read` (idempotent), `POST /api/v1/notifications/read-all` (idempotent, scoped to caller).
2. Every item returns deep-link fields (`resourceType`, `resourceId`, `projectId`) sufficient for 034 routing; payloads carry short human-readable summaries only — never transcript bodies, signed URLs, secrets, or raw provider data.
3. Enforce tenant + recipient scoping in the repository (`TenantId + RecipientUserId == caller`); cross-user/cross-tenant reads return 404 without existence leak.
4. Emit `notification.created` (013) on projection; document that SSE is an invalidation hint — clients refetch this API as source of truth.
5. Update OpenAPI (feeds 014): notification schemas, pagination, idempotent-read semantics.

## Requirements
- R1: List is tenant/recipient-scoped, paginated, newest-first, excludes expired.
- R2: Unread count is lightweight and consistent with the list scope.
- R3: Mark-read/read-all are idempotent (repeat calls are no-ops, never errors).
- R4: Payloads contain no sensitive content (automated assertion).
- R5: Deep links resolve to an existing project/review/export or documented fallback.

## Edge Cases and Error Handling
- Already-read notification re-marked read → 200 no-op, not 409.
- `read-all` with zero unread → 200 with `marked:0`.
- Expired notification read → 404 (treated as gone), not 410.

## Security and Safety Requirements
- Strict recipient scoping; no admin bypass in this endpoint (admin views use 013 diagnostics aggregates, never other users inboxes).
- No notification body in logs/telemetry beyond ID + type.

## Testing
- Extend `tests/DubbingPlatform.IntegrationTests/Notifications/NotificationsApiTests.cs`: scoping, pagination order, unread-count consistency, idempotent reads, no-sensitive-payload assertion, cross-tenant/cross-user 404.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~NotificationsApiTests
```

## Completion Criteria
- Notifications API + scoping + idempotent reads + tests exist; supersedes the notifications half of Task 012; 034 can build on it.

## Traceability
- Plan B §8.3, §9.9, §12.16. Split from 012; projector in 002; UI in 034; SSE in 013/026.
