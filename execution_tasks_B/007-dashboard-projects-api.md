# Task 007 — Dashboard and Projects API

## Goal
Expose GET /dashboard/summary and full project CRUD with archive semantics and settings-change guards.

## Context
Dashboard and project list are the entry surfaces: counts, recent outputs, storage/cost, quota, warnings, and backlog in one summary call; projects need filterable CRUD where target language is immutable after creation and settings changes are blocked while a run is active (with config-hash transparency + audit).

## Starting State
Task 001 done (DubbingProject extensions, membership, settings validator). No dashboard/projects controllers.

## Scope
Included: `GET /api/v1/dashboard/summary`, `GET|POST /api/v1/projects`, `GET|PATCH /api/v1/projects/{id}`, `POST .../archive|unarchive`, `DELETE /api/v1/projects/{id}`, filters/pagination/sort, settings-change guard + config hash + audit.
Excluded: processing start (Task 008), workspace aggregate (Task 008), segments/voices/reviews/output (Tasks 009–012), frontend list/wizard (Tasks 021–022).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/DashboardController.cs`: `GET /api/v1/dashboard/summary` returns `{ projectCounts {active, archived, total}, recentOutputs[] (max 5), storage {usedBytes, quotaBytes}, cost {monthToDate, currency}, quota {remaining, resetsAt}, warnings[] {code, message, projectId?}, backlog {pendingReviews, runningJobs} }` — single aggregated query service, tenant-scoped.
2. Create `src/DubbingPlatform.Api/Controllers/ProjectsController.cs`: `GET /api/v1/projects` (filters: `status, ownerId, search, archived`; pagination `page, pageSize` default 20/max 100; sort `createdAt|updatedAt|name` + `sortDir`), `POST /api/v1/projects` (Name required ≤200, targetLanguage required + immutable thereafter), `GET /api/v1/projects/{id}`, `PATCH /api/v1/projects/{id}` (name/description/settings only), `POST /api/v1/projects/{id}/archive|unarchive`, `DELETE /api/v1/projects/{id}` (soft-delete → archived + `DeletedAt`; hard delete only via admin path, not here).
3. Implement settings-change guard in `src/DubbingPlatform.Application/Projects/ProjectSettingsGuard.cs`: `PATCH` with `processingSettings` rejected with 409 `SETTINGS_LOCKED_ACTIVE_RUN` when any active run exists for the project; on success bump `SettingsVersion`, recompute config hash (SHA-256 over canonical settings JSON in `src/DubbingPlatform.Application/Projects/ProjectConfigHash.cs`), persist + AuditEvent with old/new hash.
4. Enforce target-language immutability server-side: any `PATCH` containing `targetLanguage` → 400 `LANGUAGE_IMMUTABLE` (frontend also hides the field — Task 022 — but backend is authoritative).
5. Add `GET /api/v1/projects` response envelope `{ items[], page, pageSize, total, sort, sortDir }`; archived projects excluded by default (`archived=false`), included only with explicit `archived=true|all`.
6. Update OpenAPI for dashboard + projects schemas, error codes (`LANGUAGE_IMMUTABLE`, `SETTINGS_LOCKED_ACTIVE_RUN`, `PROJECT_NOT_FOUND`), and pagination envelope examples.

## Requirements
- R1: Dashboard summary returns all seven sections in one 200 response; warnings carry machine-readable codes.
- R2: Project list supports filters + pagination + sort; default excludes archived; max pageSize 100 enforced.
- R3: `PATCH` changing targetLanguage always fails with 400 `LANGUAGE_IMMUTABLE`.
- R4: Settings change with active run fails 409; without active run succeeds, bumps version, changes hash, writes audit.
- R5: Archive/unarchive are idempotent (already-archived → 200, no error); DELETE on running project → 409 `PROJECT_HAS_ACTIVE_RUN`.
- R6: Cross-tenant project id returns 404 (not 403).

## Edge Cases and Error Handling
- Duplicate project name in tenant → allowed (names not unique), but empty/overlong name → 400.
- `pageSize > 100` → clamped to 100 with `clamped: true` hint, not an error.
- PATCH with unknown fields → 400 validation, no partial apply.
- Concurrent PATCH (lost update) → ETag/`If-Match` on `SettingsVersion`; mismatch → 409 `SETTINGS_VERSION_CONFLICT`.

## Security and Safety Requirements
- Tenant isolation on every query/command; project access additionally requires membership or ownership (403/404 per policy).
- `project.delete` permission required for DELETE; `project.edit` for PATCH/archive.
- Audit every mutation (create/patch/archive/unarchive/delete) with actor + correlationId.
- No cost/storage internals beyond the summary DTO (no per-invoice detail here).

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Projects/ProjectsApiTests.cs`: dashboard shape, list filters/pagination/sort, create + get, language-immutable 400, settings guard 409 with active run vs success + hash change + audit without, archive idempotency, delete-with-active-run 409, cross-tenant 404, ETag conflict.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~ProjectsApiTests
```

## Completion Criteria
- Dashboard + projects endpoints, guard, hash, audit, and OpenAPI exist; `ProjectsApiTests` pass; language change and guarded settings change provably rejected.

## Traceability
- Plan B §9.2, §12.2–§12.4.
