# Task 039B — Frontend State-Matrix Gap Closure

**Required/Optional:** Required
**Complexity:** M

## Goal
Close every missing frontend loading/empty/error/permission/async-state test identified by the 039A gap report.

## Context
Split from oversized Task 039. Feature tasks (015–036) author their happy-path + primary specs; this task closes the state matrix (Plan §11.4: loading, skeleton, empty, ready, refreshing, partial, submitting, success, recoverable/blocking failure, unauthorized/forbidden/not-found, offline, cancelled, processing, review) wherever 039A reports it missing. It authors no new features and no backend tests.

## Starting State
Depends on Tasks 046 (harness), 039A (gap list; this task is done when the list is empty for frontend matrices). Feature specs from 015–036 are the baseline — this task adds only what is missing. Task 039 (combined) is superseded.

## Scope
Included: missing component/state tests for upload, badges, progress, editors, voice selector, review/export panels, notification center, QC list, and every screen state matrix entry.
Excluded: test infra (039A), backend unit gaps (039C), integration/cross-layer (040A/B), E2E/visual/a11y/perf (041A–D).

## Instructions
1. Run `node scripts/coverage-gap.mjs` (039A) and close every frontend matrix gap in priority order: auth/session states (019), dashboard/project-list states (020–021), upload states incl. duplicate/rejected (023), workspace/progress states incl. SSE fallback (025–026), editor conflict/stale states (027–028), voice consent/quota states (029), timeline empty/issue states (030), review conflict states (031), QC blocking states (032), output/export partial states (033), notification/activity/cost states (034/035A), settings guards (035B), admin forbidden states (036).
2. Each added spec uses the 046 MSW taxonomy (never live API) and asserts the recovery action per error UX (§11.6: retry/resume/replace/review/contact-admin/wait) — a failure state without its recovery action fails review.
3. No color-only status assertions: every status test also asserts non-color signal (label/icon/text), supporting 041C.
4. Update the gap list to empty for these areas; record any intentional exclusion in `docs/coverage.md` with reason + expiry.

## Requirements
- R1: Every screen in 019–036 has its relevant §11.4 states tested.
- R2: Every recoverable error test asserts its recovery action.
- R3: MSW-backed only; no network, no backend boot required.
- R4: 039A gap script reports zero frontend-matrix gaps on completion.
- R5: No backend test changes in this task.

## Edge Cases and Error Handling
- Gap caused by untestable animation → test structure/presence + reduced-motion path, never pixel assertion (visual is 041B).
- Gap caused by permission-gated UI → test both allowed + forbidden renders.
- New gap introduced by a concurrent feature PR → re-run gap script before closing; close the delta too.

## Security and Safety Requirements
- State tests use synthetic fixtures only (046); no real user data in snapshots.
- Forbidden-state tests assert no data leakage (no hidden-but-rendered sensitive content).

## Testing
- Added specs live beside features: `frontend/src/features/*/*.spec.ts`, `frontend/src/components/**/*.spec.ts` (only the missing ones).
- Type: Vitest component/state.

## Validation
```bash
npm run test --prefix frontend
npm run test --prefix frontend -- --coverage
node scripts/coverage-gap.mjs
```

## Completion Criteria
- 039A reports zero frontend-matrix gaps; all added specs pass; supersedes the frontend-matrix third of Task 039.

## Traceability
- Plan B §11.4, §11.6, §15.1–§15.3 (frontend slice). Split from 039; infra in 039A; backend in 039C.
