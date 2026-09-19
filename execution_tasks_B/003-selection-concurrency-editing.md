# Task 003 — Segment Selection Concurrency and Version-Aware Editing Backend

## Goal
Add explicit segment selection state with expected-version concurrency control for transcript/translation edits.

## Context
Transcript/translation versions are immutable; user edits create new manual versions and selection changes are explicit auditable operations. Without a selection version counter, concurrent editors silently lose updates. This task provides the backend invariant Tasks 009/027/028 depend on.

## Starting State
Plan A SpeechSegment + TranscriptVersion/TranslationVersion exist (immutable). Task 001 done. No selection counter or conflict path.

## Scope
Included: selection projection/counter, domain service, EF changes + migration, conflict errors, invalidation hook, audit.
Excluded: HTTP endpoints (Task 009), review-resolve-with-edit wiring (Task 011), frontend editors (Tasks 027–028).

## Instructions
1. Create or extend `src/DubbingPlatform.Domain/Entities/SegmentSelection.cs` (or extend `SpeechSegment`): `TenantId`, `ProjectId`, `SegmentId`, `SelectedTranscriptVersionId?`, `SelectedTranslationVersionId?`, `SelectedAudioArtifactId?`, `SelectionVersion` (int, starts 0), `UpdatedAt`, `UpdatedByUserId`. Unique `(TenantId, SegmentId)`.
2. Add `SegmentSelectionConfiguration.cs` + migration `AddSegmentSelection`; RLS on tenant.
3. Create `src/DubbingPlatform.Application/Segments/SegmentSelectionService.cs` with `SelectTranscriptAsync(...)`, `SelectTranslationAsync(...)`, `CreateManualTranscriptVersionAsync(...)`, `CreateManualTranslationVersionAsync(...)` — all require `expectedSelectionVersion`; on mismatch throw `ConflictException(code: SELECTION_CONFLICT)`; record actor + reason; bump counter atomically (row-version / `UPDATE ... WHERE SelectionVersion=@expected`).
4. Emit domain event `SegmentSelectionChanged` → invalidates dependent voice/timing work (publish existing invalidation message; do not execute recompute here).
5. If final output exists for the run, mark output stale via existing output-state flag (no recompute).

## Requirements
- R1: Stale `expectedSelectionVersion` returns conflict, never overwrites.
- R2: Content version rows never updated in place (new row per manual edit).
- R3: Every selection change records actor, reason (nullable), timestamp.
- R4: Concurrent selects serialize (exactly one winner).
- R5: Dependent-stage invalidation event always published on change.

## Edge Cases and Error Handling
- Missing version id → 404 `VERSION_NOT_FOUND`.
- Selecting version from another segment → 400 `VERSION_SEGMENT_MISMATCH`.
- Edit after output finalized → succeeds but output flagged stale + warning returned.

## Security and Safety Requirements
- Tenant + project-membership authorization at service layer (defense in depth; endpoints re-check).
- Reason text sanitized (max 500 chars, no HTML passthrough).

## Testing
- `tests/DubbingPlatform.IntegrationTests/Segments/SelectionConcurrencyTests.cs`: happy-path select, stale-write 409, concurrent race (one winner), manual edit creates new version, invalidation published, cross-tenant blocked.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~SelectionConcurrencyTests
```

## Completion Criteria
- Selection entity + service + migration + tests exist; stale writes rejected; invalidation emitted.

## Traceability
- Plan B §8.5, §8.9, §9.5, §4.6.
