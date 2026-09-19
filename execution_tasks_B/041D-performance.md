# Task 041D — Performance Budgets

**Required/Optional:** Required
**Complexity:** M

## Goal
Enforce frontend performance budgets on representative fixtures in CI.

## Context
Split from Task 041. Performance practices (code splitting, virtualization, preview media, memoization, debounced timeline) are built in 015/017/025/027/030; this task is the measurement gate with six budgets, traces on breach, and a median-of-three rule for shared runners.

## Starting State
Depends on Tasks 015 (splitting), 017 (query efficiency), 025/027/030 (workspace/list/timeline under test), 046 (representative fixtures). Task 041 (combined) is superseded.

## Scope
Included: `e2e/perf/budgets.ts` + `e2e/perf/*.spec.ts` for the six budgets below; CI breach handling.
Excluded: perf fixes (owning feature tasks), visual (041B), a11y (041C), journeys (041A), backend load testing (out of Plan B scope unless 042B adds it).

## Instructions
1. Define budgets in `e2e/perf/budgets.ts`: app-interactive ≤ 3s (desktop broadband), project-list render ≤ 1.5s at 200 rows, workspace open ≤ 2s, timeline interaction latency ≤ 100ms p95, segment search ≤ 300ms at 5k segments, media seek ≤ 500ms. Budgets versioned; changes require recorded reason.
2. Add `e2e/perf/*.spec.ts` using representative fixtures (046, never idealized hardware): each spec measures one budget with trace capture; breach attaches trace + fails the run and opens (or updates) a perf issue automatically per repo policy.
3. Shared-runner rule: breach verdict uses 3-run median before fail; single-run spike quarantines per 046 instead of failing release.
4. Assert performance practices structurally where cheap: route-level splitting present (015), list virtualization active at 200+ rows, timeline uses preview peaks not archival (030), search debounced — failures point at the owning task.

## Requirements
- R1: All six budgets measured on representative fixtures.
- R2: Breaches attach traces and fail (or quarantine-then-fail per median rule).
- R3: No idealized-hardware numbers; fixture sizes recorded in the spec.
- R4: Structural practice assertions present alongside timing.
- R5: Budget changes versioned with reason.

## Edge Cases and Error Handling
- CI runner slowdown → median-of-three verdict, never single-spike release block without quarantine record.
- Fixture growth (10k segments) → budget holds or explicit re-baseline with reason; silent threshold bump rejected.
- Trace upload failure → run still fails with timing evidence; missing-trace warning attached.

## Security and Safety Requirements
- Traces scrubbed (URLs, tokens, media bytes) before CI attach per 038 allowlist.
- Perf specs use synthetic fixtures only.

## Testing
- Added specs: `e2e/perf/` + `budgets.ts`.
- Type: Playwright perf (Chromium) with trace.

## Validation
```bash
npx playwright test --grep="@perf"
```

## Completion Criteria
- Six budgets green (or median-rule quarantined with owner + issue); supersedes the perf quarter of Task 041.

## Traceability
- Plan B §10.11, §15.10. Split from 041; practices in 015/017/025/027/030; fixtures in 046.
