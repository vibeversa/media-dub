# Task 002 — Notifications and Activity Projection

## Goal
Add durable Notification and ActivityEvent entities with outbox-driven projection.

## Context
Notifications must survive reload and be delivered as a durable projection, not transient events; activity feed is the user-visible history while AuditEvent stays the security record. Both are required by the notification center, workspace, and admin surfaces.

## Starting State
Task 001 done (TenantUser exists). Plan A outbox/inbox and AuditEvent exist. No Notification/ActivityEvent tables or projectors.

## Scope
Included: entities, EF configs, migration + RLS, projector from backend events/outbox, deduplication, expiry.
Excluded: HTTP endpoints (Task 012), SSE payload (Task 013), frontend center (Task 034).

## Instructions
1. Create `src/DubbingPlatform.Domain/Entities/Notification.cs`: `Id` (`ntf_`), `TenantId`, `RecipientUserId`, `ProjectId?`, `Type` (ProcessingCompleted/Failed, ManualReviewRequired, ReviewResolved, ExportCompleted/Failed, UploadRejected, QuotaWarning, ProviderPolicyWarning), `Severity`, `Title` (max 200), `Body` (max 1000, summary only), `ResourceType`, `ResourceId`, `SourceEventId`, `ReadAt?`, `CreatedAt`, `ExpiresAt?`. Unique `(TenantId, RecipientUserId, SourceEventId)` where SourceEventId not null; index `(TenantId, RecipientUserId, ReadAt, CreatedAt)`.
2. Create `src/DubbingPlatform.Domain/Entities/ActivityEvent.cs`: `Id` (`act_`), `TenantId`, `ProjectId?`, `ProcessingRunId?`, `Type`, `ActorType`, `ActorUserId?`, `Summary`, `Severity`, `CorrelationId`, `OccurredAt`, `SchemaVersion=1`, `MetadataJson`. Index `(TenantId, ProjectId, OccurredAt)`; append-only (no update/delete repository methods).
3. Add configs + migration `AddNotificationsActivity`; enable RLS on both.
4. Create `src/DubbingPlatform.Application/Notifications/NotificationProjector.cs`: maps run/review/export/upload/quota events → notifications (recipient resolution via ProjectMembership + Owner); deduplicates on SourceEventId; never includes transcript bodies, signed URLs, or secrets.
5. Create `src/DubbingPlatform.Application/Activity/ActivityProjector.cs`: maps upload/start/translation/review/edit/export/complete actions → ActivityEvent; security-relevant actions also write AuditEvent.
6. Wire projectors to existing MassTransit consumers / domain-event handlers idempotently.

## Requirements
- R1: Duplicate source event does not create second notification.
- R2: Notifications survive API restart (persisted, not in-memory).
- R3: Activity is append-only and paginated by `(ProjectId, OccurredAt)`.
- R4: Bodies contain short human-readable summaries only.
- R5: RLS + tenant isolation on both tables.

## Edge Cases and Error Handling
- Missing recipient (no membership) → fall back to project owner; if none, skip + metric `notifications.skipped_total`.
- Null ProjectId allowed only for quota/policy types; others require ProjectId.
- Expired notifications excluded from list but retained until retention job.

## Security and Safety Requirements
- Tenant/user scoping on every read; cross-tenant access returns 404 (not 403 with existence leak) — endpoint behavior verified in Task 012.
- No sensitive content in Title/Body/MetadataJson (test asserts absence of tokens/URLs).

## Testing
- `tests/DubbingPlatform.IntegrationTests/Notifications/NotificationActivityTests.cs`: dedup, survival across restart (re-project same event), pagination order, cross-tenant isolation, no-secret assertion.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~NotificationActivityTests
```

## Completion Criteria
- Tables + projectors + tests exist; dedup, pagination, isolation verified.

## Traceability
- Plan B §8.3, §8.4, §8.9, §9.9, §20.
