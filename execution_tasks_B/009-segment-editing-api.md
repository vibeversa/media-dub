# Task 009 — Segments, Transcript, Translation Editing API

## Goal
Expose segment list/detail, selection, manual-edit, and retry endpoints with version-safe concurrency.

## Context
Transcript/translation review is the highest-concurrency surface: multiple editors, immutable version history, explicit selection, and auditable invalidation of downstream voice/timing work. The backend invariant (expected-version counter, 409 path) was built in Task 003; this task puts the HTTP contract on it.

## Starting State
Task 003 done (SegmentSelection + SelectionVersion counter, ConflictException, invalidation hook). Plan A SpeechSegment + immutable TranscriptVersion/TranslationVersion exist. No segment HTTP endpoints.

## Scope
Included: `GET .../segments[/{id}]`, `POST .../retry|transcript-selection|translation-selection|transcript-edits|translation-edits`, filters + pagination, expected-version enforcement, invalidation + stale-output marking wiring, audit.
Excluded: selection concurrency internals (Task 003, reuse as-is), review-resolve-with-edit (Task 011), frontend editors (Tasks 027–028).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/SegmentsController.cs` under `/api/v1/projects/{projectId}`: `GET .../segments` (filters: `speakerId, reviewStatus, qualityFlag, syncIssue, text` substring, `startMs,endMs` time window; pagination default 50/max 200; sort `startMs`), `GET .../segments/{segmentId}` (detail with current versions + selection version), `POST .../segments/{segmentId}/retry` (requeue ASR/translation for segment, 202), `POST .../transcript-selection|translation-selection` (body `{ versionId, expectedSelectionVersion, reason? }`), `POST .../transcript-edits|translation-edits` (body `{ text, expectedSelectionVersion, reason? }` → creates manual version + selects it).
2. Delegate all mutations to `src/DubbingPlatform.Application/Segments/SegmentSelectionService.cs` (Task 003); map `ConflictException` → 409 `SELECTION_CONFLICT` with `{ currentSelectionVersion, currentVersionIds }` so the client can refresh (Task 027/028 consumes this shape).
3. On every successful selection/edit, publish the Task 003 invalidation event and, when final output exists for the run, flag output stale (reuse existing output-state flag; no recompute here). Return `outputStale: true` + warning code in the mutation response.
4. Record AuditEvent per mutation with actor, reason (nullable, max 500, sanitized), old/new version IDs, and correlationId.
5. Enforce `review.view` for GETs; transcript/translation mutation requires `project.edit`; retry additionally requires `processing.retry`.
6. Update OpenAPI: segment schemas, filter params, 409 conflict shape with refresh payload, edit-request examples.

## Requirements
- R1: List supports all six filters + time window + pagination; combined filters AND together.
- R2: Stale `expectedSelectionVersion` → 409 `SELECTION_CONFLICT` with current versions (never silent overwrite).
- R3: Content versions immutable: edit creates a new manual version row; old rows unchanged (test reads old row post-edit).
- R4: Successful mutation publishes invalidation and marks stale output when final output exists.
- R5: Every mutation writes an audit record with actor + reason + version delta.
- R6: Selecting a version from another segment → 400 `VERSION_SEGMENT_MISMATCH`.

## Edge Cases and Error Handling
- Missing version id → 404 `VERSION_NOT_FOUND`.
- Edit with empty/whitespace text → 400 `SEGMENT_TEXT_EMPTY`.
- Retry on segment with active retry → 409 `SEGMENT_RETRY_ACTIVE` (idempotent poll returns existing job).
- Edit after output finalized → succeeds with `outputStale: true` warning (not an error).

## Security and Safety Requirements
- Tenant + project-membership check on every route; cross-tenant segment id → 404.
- Reason/text sanitized (max lengths, no HTML passthrough; stored as plain text).
- No transcript bodies in logs; log version IDs + correlationId only.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Segments/SegmentEditingApiTests.cs`: filter matrix, pagination, stale-write 409 with refresh payload, immutable-version assertion, invalidation published, stale-output flag, audit written, version-segment mismatch 400, authz matrix, cross-tenant 404.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~SegmentEditingApiTests
```

## Completion Criteria
- Segment endpoints + 409 refresh contract + audit + OpenAPI exist; `SegmentEditingApiTests` pass; concurrent stale writes provably rejected.

## Traceability
- Plan B §9.5, §12.9–§12.10. Depends on Task 003.
