# Task 012A — Output and Export API

**Required/Optional:** Required
**Complexity:** M

## Goal
Expose the output summary and export lifecycle endpoints with signed-URL-only downloads.

## Context
Split from oversized Task 012 (output/export + notifications). Output/export is the final-artifact surface (final video/audio, subtitles, transcript/translation, timeline JSON, speakers, QC report) with partial-state semantics; it pairs with the exports UI (033) and workspace output panel (025). Task 008 owns only the workspace projection of output readiness — this task owns the API.

## Starting State
Depends on Tasks 002 (activity for export events), 004 (preview/QC artifacts referenced by output), 008 (processing lifecycle owns run state this API projects). Task 012 (combined) is superseded by 012A + 012B.

## Scope
Included: `GET .../output` aggregate, export create/status/download wiring, partial/completeness metadata, signed-URL issuance, idempotency on export creation.
Excluded: notifications API (012B), workspace aggregate shape (008), exports UI (033), review/activity surfaces.

## Instructions
1. Implement `GET /api/v1/projects/{projectId}/output` in `src/DubbingPlatform.Api/Controllers/OutputController.cs` (or existing export/output controller): returns per-asset entries (final video, audio-only, subtitles, transcript, translation, timeline JSON, speaker metadata, QC report) each `ready|generating|failed|partial|unavailable` with `completeness` (e.g. `96/100` segments) and `generationState`; never expose internal storage paths or credentials.
2. Wire existing Plan A export jobs: `POST /api/v1/projects/{projectId}/exports` (idempotency key, 7d), `GET .../exports[/{exportId}]`, download via `GET .../exports/{exportId}/download` returning short-lived signed URL (15-min, post-auth issuance, single-use where supported). All downloads are signed URLs only.
3. Partial-export rule: any export over incomplete segments includes `completeness` + `isPartial:true` + missing-segment summary; full-export request on incomplete run returns `409 EXPORT_INCOMPLETE` with the partial offer, never silent truncation.
4. Publish export events (`export.created/completed/failed`) to the notification/activity projectors (002); record actor + idempotency key for audit.
5. Update OpenAPI (feeds 014): output + export schemas, `EXPORT_INCOMPLETE` code, idempotency header, signed-URL response shape.

## Requirements
- R1: Every asset entry carries explicit readiness state; no asset is implied ready.
- R2: Partial exports always carry completeness metadata.
- R3: Downloads are short-lived signed URLs issued post-auth only.
- R4: Export creation is idempotent (same key → same job, no duplicate).
- R5: No internal storage paths, bucket names, or credentials in any response.

## Edge Cases and Error Handling
- Export requested with no final output → `409 EXPORT_NOT_READY` with actionable next step.
- Signed URL expired → `410 URL_EXPIRED` with re-issue action, never the raw storage error.
- Duplicate export (same key) → return original job, never a second job.

## Security and Safety Requirements
- Tenant + project-membership authorization on every route; cross-tenant returns 404 without existence leak.
- Signed URLs tenant-scoped, short-lived, never logged/cached long-term; no URL in telemetry (038).

## Testing
- Extend `tests/DubbingPlatform.IntegrationTests/Exports/OutputExportApiTests.cs`: readiness matrix, partial completeness, idempotent create, signed-URL-only download, cross-tenant 404, `EXPORT_INCOMPLETE` path.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~OutputExportApiTests
```

## Completion Criteria
- Output aggregate + export lifecycle + signed-URL downloads + tests exist; partial states explicit; supersedes the output half of Task 012.

## Traceability
- Plan B §9.8, §12.15. Split from 012; pairs with 033; workspace projection stays in 008.
