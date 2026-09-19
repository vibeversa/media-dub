# Task 046 — Test Harness and Fixtures (Early Prerequisite)

**Required/Optional:** Required
**Complexity:** M

## Goal
Own the shared frontend/backend test harness, fixtures, and tags so feature tasks can verify locally and in CI.

## Context
Feature tasks (006–036) require Vitest, MSW, Playwright tags, synthetic tenants/users/projects/runs/segments, and PII scrubbers, but no early task owned them — the harness only appeared implicitly in late aggregate tasks 039–041. This task makes the harness an executable prerequisite: whoever implements a feature writes its specs against this harness instead of waiting for a later task.

## Starting State
Depends on Tasks 014 (OpenAPI bundle for mock generation), 015 (frontend scaffold). Must land before or alongside the first feature work (019+); later tasks 039–041 become gap closure, not initial authorship.

## Scope
Included: Vitest/MSW/Playwright config + tags, MSW handler taxonomy, synthetic fixture factories (tenants/users/projects/runs/segments), storage-emulator reset, SSE-aware waits, PII scrubbers, flake-quarantine policy.
Excluded: feature specs themselves (owned by 006–036), coverage enforcement (039A), cross-layer seams (040B), journeys/visual/a11y/perf (041A–D), full CI gates (042/042B).

## Instructions
1. Frontend harness: `frontend/vitest.config.ts` (or equivalent), `frontend/src/test/setup.ts`, `frontend/src/mocks/handlers.ts` taxonomy (`success, 401, 403, 404, 409, 429, 500, validation, provider-error, partial, stale-conflict`), `frontend/src/mocks/server.ts` (MSW), documented in `frontend/src/mocks/README.md`.
2. Playwright harness: `e2e/playwright.config.ts` (projects Chromium/WebKit/Firefox + `@smoke/@visual` tags), `e2e/support/auth.ts` (seeded auth for all roles), `e2e/support/reset.ts` (PG + storage-emulator reset), `e2e/support/sse-waits.ts` (event-driven waits, no fixed sleeps), `e2e/support/quarantine.md` (quarantine policy: 2 failures/50 runs → quarantine with owner + issue, never silent retry).
3. Backend fixtures: `tests/DubbingPlatform.TestFixtures/` (or existing fixtures dir): `SyntheticTenants.cs`, `SyntheticUsers.cs`, `SyntheticProjects.cs` (incl. runs/segments/review items), `PiiScrubber.cs` (asserts no real PII/secrets in fixtures, screenshots, or logs).
4. Add harness smoke tests: `frontend/src/mocks/handlers.spec.ts` (each taxonomy handler returns the documented envelope), `e2e/support/smoke.spec.ts` (reset + seeded login works), backend `TestFixturesTests.cs` (factories build valid graphs, scrubber passes).
5. Document ownership contract in `docs/test-ownership.md` (one page): feature tasks own their unit/component/integration/E2E slices against this harness; 039–041 own gap closure, seams, and non-functional gates only.

## Requirements
- R1: MSW taxonomy covers all 11 states with envelope-correct responses.
- R2: Playwright config supports tagged runs (`@smoke`, `@visual`, feature `@tags`) across required browsers.
- R3: Synthetic factories build tenant-isolated graphs usable by backend + E2E tests.
- R4: PII scrubber passes on all fixtures.
- R5: Ownership doc exists; no feature task is blocked waiting for 039–041 to author its specs.

## Edge Cases and Error Handling
- MSW handler missing → test fails with `MSW_HANDLER_MISSING` naming the taxonomy entry, not a generic network error.
- Storage emulator down → `e2e/support/reset.ts` fails fast with `STORAGE_EMULATOR_UNAVAILABLE`, never hangs.
- Parallel workers share tenant → factories issue isolated tenant per worker (no cross-test leakage).

## Security and Safety Requirements
- Fixtures contain synthetic PII only; scrubber test enforces it.
- Seeded credentials are ephemeral per run, never committed; no real tokens in handlers.

## Testing
- This task IS harness + its smoke tests: `handlers.spec.ts`, `e2e/support/smoke.spec.ts`, `TestFixturesTests.cs`.
- Type: unit + config smoke.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~TestFixturesTests
npm run typecheck --prefix frontend
npm run test --prefix frontend -- src/mocks
npx playwright test --project=chromium e2e/support/smoke.spec.ts
```

## Completion Criteria
- Harness configs + taxonomy + factories + scrubbers + ownership doc exist; harness smoke tests pass; feature tasks can author specs immediately.

## Traceability
- Plan B §15.1–§15.6 (test prerequisites), §18 (support diagnostics use same seeds). Unblocks 006–041 feature verification; converts 039–041 to gap closure.
