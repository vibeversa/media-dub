> **REVIEW FIX — SUPERSEDED (split/rewrite):** this file is superseded by `040A-cross-layer-harness.md` (rig) + `040B-cross-layer-seams.md` (seven named seams). Endpoint integration stays in 006–013 and must not be duplicated here.

# Task 040 — Backend Integration and Cross-Layer Tests

**Required/Optional:** Required
**Complexity:** M

## Goal
Verify new endpoints with backend integration tests and prove frontend→backend→infra flows with cross-layer tests.

## Context
Endpoints from Tasks 006–013 need integration coverage per Plan B §15.4; cross-layer flows per §15.5 prove the seams hold (FE→API→PG, upload→storage, start→SSE, review→versioned mutation, export→signed download, voice→invalidation, stale→refresh). Unit/MSW coverage from Task 039 is assumed; this task tests real wiring.

## Starting State
Tasks 006–013 and 039 done. Depends on Tasks 006–013 and 039.

## Scope
Included: backend integration tests (me/preferences/archive/workspace/activity/notification/review/preview/selection/admin), cross-layer Playwright flows (`@cross-layer`).
Excluded: pure unit/component/MSW (Task 039), full E2E journey + non-functional gates (Task 041), CI wiring (Task 042).

## Instructions
1. Create `tests/DubbingPlatform.IntegrationTests/Endpoints/MePreferencesTests.cs` (Testcontainers PostgreSQL): `/me` resolution, preference round-trip + unknown-key 400, tenant isolation on prefs, disabled-user 401.
2. Create `tests/DubbingPlatform.IntegrationTests/Endpoints/ProjectsArchiveTests.cs`: project CRUD, archive/unarchive round-trip (processing semantics unchanged), delete constraints with active run → 409, cross-tenant project access → 403/404.
3. Create `tests/DubbingPlatform.IntegrationTests/Endpoints/WorkspaceActivityTests.cs`: workspace aggregate shape (single-query, no N+1 — assert query count), activity pagination + filters, notification list + dedup constraint + read/read-all, review-context read model with version guard on resolve.
4. Create `tests/DubbingPlatform.IntegrationTests/Endpoints/PreviewSelectionAdminTests.cs`: preview endpoints require membership (anonymous/cross-tenant → 403/404), selection expected-version conflict → 409 with current version, admin endpoints → 403 for non-elevated / 200 for elevated (links to Task 036 `AdminAuthzTests` without duplicating).
5. Create Playwright `e2e/cross-layer/*.spec.ts` tagged `@cross-layer` against real FE+API+PG (+ storage emulator, mocked AI): FE→API→PG create-project round-trip, upload→storage artifact visible, processing-start→SSE progress received, review-resolve→versioned mutation persisted, export→signed-download fetchable, voice-assign→dependent queries invalidated, stale-edit→conflict UI offers refresh preserving local text.

## Requirements
- R1: Integration tests cover /me, preferences, archive, workspace, activity pagination, notification dedup, review context, preview authz, selection conflicts, admin authz.
- R2: Workspace aggregate proven single-shot (query-count assertion, no N+1).
- R3: Cross-layer flows pass against real FE+API+PG for all seven listed seams.
- R4: Every cross-tenant/anonymous negative case fails closed with structured errors + correlation IDs.
- R5: Selection/review conflicts return current-version payloads enabling client refresh (no blind retry).

## Edge Cases and Error Handling
- Concurrent review resolves (double-submit) → idempotency key dedupes, single audit event.
- Export download URL expires mid-test → single refetch path exercised, then success.
- SSE gap during start→SSE flow → polling fallback still delivers progress (Task 026 path).
- Storage emulator reset between upload tests → no cross-test artifact leakage.
- PG container slow-start → health-gated test setup, not fixed sleeps.

## Security and Safety Requirements
- Integration fixtures use synthetic tenants/users; no production-shaped PII.
- Signed URLs and tokens in test logs redacted; failure output shows correlation IDs only.
- Cross-layer specs run against ephemeral environments; no shared/staging mutation.

## Testing
- This task IS the test layer: `tests/DubbingPlatform.IntegrationTests/Endpoints/*.cs` + `e2e/cross-layer/*.spec.ts` (`@cross-layer`).
- Type: backend integration (Testcontainers PostgreSQL + storage emulator) + Playwright cross-layer (mocked AI providers).

## Validation
```bash
dotnet test --filter FullyQualifiedName~IntegrationTests
npx playwright test --grep="@cross-layer"
```

## Completion Criteria
- All endpoint integration tests and all seven cross-layer seams pass against real FE+API+PG with negatives failing closed; `IntegrationTests` + `@cross-layer` green.

## Traceability
- Plan B §15.4–§15.5. Depends on Tasks 006–013 and 039.

