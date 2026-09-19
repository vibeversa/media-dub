# Task 041A — Cross-Feature Journeys and Full Smoke

**Required/Optional:** Required
**Complexity:** M

## Goal
Prove cross-feature journeys and the §24 full smoke without owning feature-level E2E specs.

## Context
Split/rewrite from oversized Task 041 (which wrongly owned feature E2E while feature tasks 019–036 cited `@...` passes they could not author). Ownership fix: feature specs (`e2e/features/<name>.spec.ts`, tags `@auth/@dashboard/@projects/@project-create/@upload/@processing-start/@workspace/@progress/@transcript/@translation/@voices/@timeline/@review/@quality/@exports/@notifications/@activity/@settings/@admin`) belong to Tasks 019–036 and run on harness 046. This task owns only cross-feature journeys + the §24 full smoke; visual/a11y/perf move to 041B/C/D.

## Starting State
Depends on Tasks 019–036 (feature specs exist as the baseline), 040A (stack for smoke), 046 (tags, auth seeds, quarantine). Task 041 (combined) is superseded by 041A + 041B + 041C + 041D.

## Scope
Included: `e2e/journeys/` cross-feature specs + `e2e/smoke/full-smoke.spec.ts` (`@smoke`) on real FE+API+PG+storage+transport with mock AI.
Excluded: feature specs (019–036), visual (041B), a11y (041C), perf (041D), cross-layer seams (040B).

## Instructions
1. Add `e2e/journeys/*.spec.ts`: end-to-end user paths spanning features (login → create → upload → resume-after-reload → start → progress → workspace → transcript edit → translation edit → voice assign → review resolve → output download → export), plus cancel, retry, stale-version conflict (draft preserved post-refresh), forced 401 (expired session → login with `?next=`), forced 403 (viewer attempts edit → forbidden state). Reuse 046 auth/reset/SSE helpers; no fixed sleeps.
2. Add `e2e/smoke/full-smoke.spec.ts` (`@smoke`): the §24 happy path on the 040A-equivalent stack with deterministic fixtures + mocked AI; asserts durable artifacts (project row, segments, export file, notification row) post-run; failing smoke blocks release (042B gates on it).
3. Assert structured errors + correlation IDs on 401/403/409 paths; never stack traces or internal paths.
4. Enforce quarantine policy (046): flake (2/50) → quarantine with owner + issue, never silent retry; record journey→feature-task mapping in `e2e/journeys/README.md`.

## Requirements
- R1: All cross-feature journeys pass in sequence on a clean environment.
- R2: Full smoke proves real FE+API+PG+storage+transport with mocked AI + post-run artifact assertions.
- R3: No feature-spec authorship here (presence check: `e2e/features/` owned by 019–036).
- R4: Stale-conflict journey preserves local draft post-refresh (data-loss assertion).
- R5: Quarantine policy enforced.

## Edge Cases and Error Handling
- AI mock outage → `AI_MOCK_UNAVAILABLE` fail-fast, never hang.
- Smoke passes but a feature spec fails → release still blocked (both gates required in 042B).
- Session expiry mid-journey → re-login resumes at preserved destination, journey continues.

## Security and Safety Requirements
- Synthetic PII only; screenshots/screencasts scrubbed of injected secrets.
- Smoke credentials ephemeral per run; never committed or logged.

## Testing
- Added specs: `e2e/journeys/`, `e2e/smoke/` (+ `README.md` mapping).
- Type: Playwright (Chromium + WebKit + Firefox).

## Validation
```bash
npx playwright test --grep="@journeys"
npx playwright test --grep="@smoke"
```

## Completion Criteria
- Journeys + full smoke pass; feature-spec ownership respected; supersedes the journeys/smoke quarter of Task 041.

## Traceability
- Plan B §15.6–§15.7, §24. Split from 041; feature E2E in 019–036; visual/a11y/perf in 041B/C/D.
