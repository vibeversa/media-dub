# Task 011 — Review Context and Mutations

## Goal
Expose GET /api/v1/reviews/{id}/context single-screen read model and harden review mutations.

## Context
Reviewers resolve queue items on one screen: the item plus project/run/segment/version history/voice/audio/sync/QC evidence, allowed actions, and full history. Mutations must be idempotent, version-guarded, reasoned, and audited; ResolvedWithEdit creates a real manual content version (not a silent text patch).

## Starting State
Task 003 done (selection versioning, manual-version semantics). Plan A review queue + statuses exist. No context aggregate endpoint; existing mutations lack idempotency/version/reason hardening.

## Scope
Included: `GET /api/v1/reviews/{id}/context`, mutation hardening (`resolve|dismiss|reopen|resolve-with-edit`) with idempotency key + expected version + reason + audit, `ResolvedWithEdit` → manual version creation.
Excluded: queue listing filters UI, segment endpoints (Task 009), frontend studio (Task 031).

## Instructions
1. Create `src/DubbingPlatform.Application/Reviews/ReviewContextDto.cs`: `{ item {id, type, severity, status, version}, project {id, name}, run {id, status, configHash}, segment {id, startMs, endMs, speakerId}, versions {transcript[], translation[], selectedIds, selectionVersion}, voice {speakerId, voiceId, consentState}, audio {previewArtifactId?, signedUrl?: null — resolved by Task 012 at serve time}, sync {offsetMs, driftFlag}, qc {issues[], evidenceArtifactIds[]}, actions {allowed[]}, permissions {canResolve, canEdit}, history[] }`. Single handler, batched queries (no N+1).
2. Create `src/DubbingPlatform.Api/Controllers/ReviewsController.cs`: `GET /api/v1/reviews/{id}/context` (requires `review.view`); mutations `POST /api/v1/reviews/{id}/resolve|dismiss|reopen|resolve-with-edit` requiring `Idempotency-Key` header + body `{ expectedVersion, reason (required, max 500), editText? (resolve-with-edit only) }`.
3. Implement `src/DubbingPlatform.Application/Reviews/ReviewMutationService.cs`: expected-version mismatch → 409 `REVIEW_VERSION_CONFLICT` with current version; duplicate idempotency key → replay original result with `Idempotent-Replayed: true`; missing/blank reason → 400 `REVIEW_REASON_REQUIRED`; `resolve-with-edit` delegates to Task 003 manual-version creation then resolves with `ResolvedWithEdit` status linking the new version id.
4. Write AuditEvent per mutation (actor, action, reason, old/new status, version delta, correlationId, idempotency key).
5. Enforce `review.resolve` permission for all mutations; cross-tenant review id → 404.
6. Update OpenAPI with context schema, mutation bodies, 409 refresh shape, and `ResolvedWithEdit` examples.

## Requirements
- R1: Context returns all eleven sections in one 200 (item, project, run, segment, versions, voice, audio, sync, QC, actions/permissions, history).
- R2: Stale `expectedVersion` → 409 with current version (never silent resolve).
- R3: Duplicate idempotency key replays original result; no second state transition (test asserts history length unchanged).
- R4: Mutations without reason rejected (400); reason persisted + audited.
- R5: `resolve-with-edit` creates a new immutable manual version and links it on the review (old versions untouched).
- R6: Allowed-actions reflect actual server policy (test asserts disallowed action attempt → 403 even when client forges it).

## Edge Cases and Error Handling
- Resolve on already-resolved → 409 `REVIEW_ALREADY_RESOLVED` (with current status; idempotent key replay still 200).
- Reopen on open item → 409 `REVIEW_NOT_RESOLVED`.
- Edit text empty on resolve-with-edit → 400 `REVIEW_EDIT_EMPTY`.
- Version history capped at 50 entries in context (with `truncated: true` flag).

## Security and Safety Requirements
- Tenant + membership + `review.view|resolve` checks per route; existence never leaked cross-tenant (404).
- Reason/edit text sanitized (plain text, max lengths); no transcript dumps in logs (IDs only).
- Audio/QC evidence exposed as IDs here; signed URLs minted only by Task 012 output path.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Reviews/ReviewContextTests.cs`: context shape single-call, version-conflict 409 + refresh, idempotent replay, reason-required 400, resolve-with-edit creates version + links, already-resolved 409, allowed-actions honesty, audit written, cross-tenant 404.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~ReviewContextTests
```

## Completion Criteria
- Context endpoint + hardened mutations + audit exist; `ReviewContextTests` pass; replayed mutations provably cause no second transition.

## Traceability
- Plan B §9.7, §12.13. Depends on Task 003.
