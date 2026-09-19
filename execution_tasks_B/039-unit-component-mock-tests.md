> **REVIEW FIX — SUPERSEDED (split/rewrite):** this broad file is superseded by `039A-test-infra-coverage.md` (infra/gap report) + `039B-frontend-state-matrices.md` (frontend gap closure) + `039C-backend-unit-gaps.md` (backend gap closure). Feature tasks (006–036) author their own specs against harness 046; 039A/B/C close gaps only. No initial test authorship here.

# Task 039 — Unit, Component, API-Mock Tests

**Required/Optional:** Required
**Complexity:** M

## Goal
Cover pure logic with unit tests, components with state-matrix tests, and API edge states with MSW mocks across all failure classes.

## Context
Features from Tasks 015–036 need systematic coverage per Plan B §15.1–§15.3: pure functions (status mapping, permissions, formatting, validation, error mapping, timeline calc, query keys, editor transforms, upload retry) get unit tests; interactive components get loading/empty/failure/disabled/permission-state matrices; MSW covers the full HTTP failure taxonomy so UI behavior is proven without a backend.

## Starting State
Tasks 015–036 done (units under test exist). Depends on Tasks 015–036.

## Scope
Included: unit tests for pure logic, component state-matrix tests, MSW suites for all error/edge classes.
Excluded: backend integration (Task 040), Playwright E2E/visual/a11y/perf (Task 041), CI wiring (Task 042).

## Instructions
1. Add unit tests in `frontend/src/**/__tests__/*.test.ts(x)` (colocated) for: status mapping (pipeline/output/export states), permission helpers (valid-actions-only), formatting (duration/bytes/relative-time/cost), validation (wizard/settings/prefs), error mapping (Task 017 normalizer), timeline calculations (lanes/zoom/seek), query-key factory (tenant scoping), editor transforms (segment edit payloads), upload retry/backoff computation.
2. Add component tests with Testing Library in each feature's `__tests__/`: upload (`MediaUploader` progress/pause/resume/cancel), badges, progress bars, transcript/translation editors, voice selector, review/export panels, notification center, QC list — each asserting loading/empty/failure/disabled/permission-denied states, not just the happy path.
3. Build MSW suites in `frontend/src/test/mocks/handlers.ts` + per-feature `*.mock.test.tsx`: success, 401 (→ login redirect), 403 (→ forbidden state), 404 (→ gone/empty state), 409 (version conflict → refresh offer; duplicate → existing-row toast), 429 (→ backoff notice + retry), 500 (→ error state + correlation id shown), validation-error shape (→ per-field messages), provider-error shape (→ honest degraded state), partial-availability shape (→ partial explanation), stale-version conflict (→ refresh, no data loss).
4. Add backend unit tests in `tests/DubbingPlatform.UnitTests/`: settings validators, consent gate transitions, signed-URL expiry computation, error-envelope mapping, selection version checks — pure-domain, no containers.
5. Enforce coverage in `frontend/vitest.config.ts` (`thresholds: lines/branches/functions/statements ≥ 80` on `src/features`, `src/api`, `src/telemetry`) and `tests/DubbingPlatform.UnitTests` — CI (Task 042) fails below threshold; add `npm run test -- --coverage` output artifact.

## Requirements
- R1: Every listed pure-logic area has unit tests including boundary/negative inputs.
- R2: Every listed component asserts loading/empty/failure/disabled/permission states.
- R3: MSW covers success/401/403/404/409/429/500/validation/provider/partial/stale-conflict with correct UI outcome per class.
- R4: Backend unit tests cover validators, consent transitions, URL expiry, error mapping, version checks.
- R5: Coverage thresholds enforced (≥80% on features/api/telemetry); suite runs green via `npm run test` + `dotnet UnitTests`.

## Edge Cases and Error Handling
- Flaky-timer tests → fake timers + deterministic fixtures only; no wall-clock assertions.
- MSW unhandled-request warnings → fail the test (no silent real-network fallback).
- 429 during MSW suite → assert backoff UI, not instant retry.
- Stale-conflict test must prove local edits survive the refresh offer (no data-loss path).
- Coverage of generated code (`api/generated`) excluded from thresholds.

## Security and Safety Requirements
- Mock fixtures contain zero real secrets/tokens; snapshot tests scrub dynamic URLs/ids.
- Permission-state tests assert denial rendering without naming required roles beyond what UI shows.
- No test disables the scrubber (Task 038) or downgrades auth in shared setup.

## Testing
- This task IS the test layer: colocated `__tests__` per feature + `src/test/mocks/handlers.ts` + `tests/DubbingPlatform.UnitTests/`.
- Type: unit + component (vitest/Testing Library) + MSW; backend unit (xUnit, no containers).

## Validation
```bash
dotnet test --filter FullyQualifiedName~UnitTests
cd frontend && npm run test
cd frontend && npm run test -- --coverage
```

## Completion Criteria
- Unit, component state-matrix, and MSW suites cover all listed areas and failure classes with coverage thresholds met; `UnitTests`, `npm run test`, and coverage runs green.

## Traceability
- Plan B §15.1–§15.3. Depends on Tasks 015–036.

