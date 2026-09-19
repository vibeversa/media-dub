# Task 025 — Project Workspace Shell

## Goal
Implement the aggregated workspace shell fed by a single workspace read model, with no N+1 fetching.

## Context
Post-start home for a project; workspace aggregate endpoint from Task 008, live updates from Task 026, mounted in Task 018 project tabs. Phase projection is UI-only presentation of aggregate stage data — no client-side pipeline simulation, no fake ETAs.

## Starting State
Task 008 (workspace aggregate endpoint) and Task 018 (shell, `ProjectLayout`, tabs) done. `features/processing` has preflight (Task 024); no workspace page. Depends on Tasks 008, 018.

## Scope
Included: workspace page (header, `PipelineStepper`/`StageProgress`, review/warnings/output main column, media/config/run/cost/activity secondary column), UI-only phase projection with parallelism note, state-adaptive panels.
Excluded: live streaming (Task 026), transcript/translation editors (Tasks 027–028), review studio (Task 031), QC/output details (Tasks 032–033).

## Instructions
1. Create `frontend/src/features/processing/WorkspacePage.tsx` (+ `workspaceStore.ts` holding UI-only state: selected tab, panel sizes) fed by a single `useWorkspace(id)` on `queryKeys.workspace`; child components receive slices via props — no per-panel fetching (MSW test asserts one workspace request per refresh: no N+1).
2. Header: project name, `StatusBadge`, approximate progress %, valid-actions-only buttons from aggregate `actions[]` (open/cancel/retry/export/delete/archive).
3. `PipelineStepper` + `StageProgress`: stages from aggregate with per-stage state; parallel stages annotated with a "runs in parallel" note (UI-only phase projection of aggregate data); show elapsed time only where backend provides it — no ETA countdowns or time predictions anywhere.
4. Main column: review-queue summary, warnings, output readiness with deep links to Review/Quality/Exports tabs; secondary column: media info, config summary (+ hash, never secrets), run history, cost, activity excerpt (full feed lives in the Activity tab).

## Requirements
- R1: One workspace request per refresh (no N+1; test asserts single network call).
- R2: No ETA text anywhere in workspace UI (grep-gate test for `ETA|estimated time|remaining`).
- R3: Parallel stages carry the parallelism note; sequential stages do not.
- R4: Panels adapt to project state (pre-run hides progress, failed shows recovery actions, terminal states stop polling hooks).

## Edge Cases and Error Handling
- Workspace 404 (deleted) → redirect to list + toast.
- Aggregate version skew → stale banner + refresh action.
- Empty run history → `EmptyState` (not an empty table).

## Security and Safety Requirements
- Config panel shows hash + summary only, never secrets or internal paths.
- Cost visible per permission; hidden otherwise without error flash.

## Testing
- Create `frontend/src/features/processing/__tests__/workspace.test.tsx`: single-request assertion, stepper states from fixture aggregates, no-ETA text scan, state-adaptive panels, 404 redirect.
- Playwright `@workspace`: stepper renders, deep links navigate, failed-state recovery visible.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/processing`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/processing
npx playwright test --grep="@workspace"
```

## Completion Criteria
- Workspace renders from one aggregate request with stepper, main/secondary columns, parallelism notes, and zero ETA text; workspace tests + `@workspace` E2E pass.

## Traceability
- Plan B §12.7. Depends on Tasks 008, 018.
