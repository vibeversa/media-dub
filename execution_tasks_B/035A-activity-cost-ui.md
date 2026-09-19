# Task 035A — Activity Timeline and Cost/Quota UI

**Required/Optional:** Required
**Complexity:** S

## Goal
Implement the project activity timeline and cost/quota visibility surfaces.

## Context
Split from oversized Task 035 (activity + cost + settings). Activity is the user-visible history projection (002/008); cost/quota is the estimate/actual + quota-state surface from workspace preflight and admin aggregates. Settings/preferences move to 035B.

## Starting State
Depends on Tasks 002 (activity projection), 007–008 (project/workspace/cost inputs), 018 (shell). Task 035 (combined) is superseded by 035A + 035B.

## Scope
Included: `AuditTimeline` (user-friendly default + authorized advanced diagnostics), `CostSummary` (estimated/actual, duration/units/storage, quota states), loading/empty/error states.
Excluded: settings/preferences screens (035B), notification center (034), admin ops dashboard (036), backend projections (002/008).

## Instructions
1. Build `frontend/src/features/activity/AuditTimeline.tsx`: paginated `GET .../activity` (cursor, newest-first), rows timestamp/actor/action/summary; default view user-friendly; advanced section (run/stage/attempt/provider/latency/cost/artifact/correlation) behind `diagnostics.view` permission only.
2. Build `frontend/src/features/cost/CostSummary.tsx` + quota badges: estimated vs actual with `estimate` labeling on preflight values, duration/provider-units/storage breakdown, quota states `available|near|exceeded|reserved`; never render reservation IDs or raw cost internals.
3. Cover states: loading/skeleton, empty (no activity yet), partial (projection lag notice), error with retry, forbidden (advanced section hidden, not error).
4. Reuse query keys from 017 (`activity`, `workspace`, `quotas`); invalidate on SSE `run.status_changed` / `export.*` via 026.

## Requirements
- R1: Activity covers upload/start/translation/review/edit/export/complete actions in order.
- R2: Advanced diagnostics hidden by default; visible only with permission.
- R3: Preflight/estimate values always labeled estimates, never guarantees.
- R4: No reservation IDs, provider secrets, or internal cost keys rendered.
- R5: Empty/partial/error states implemented, not just happy path.

## Edge Cases and Error Handling
- Projection lag (activity missing just-completed action) → `partial` notice + refetch, not an error toast.
- Quota exceeded mid-flow → badge + `contact admin` recovery action (per error UX 011.6).
- Large histories → virtualized list + cursor pagination, never full fetch.

## Security and Safety Requirements
- Tenant-scoped queries only; no cross-project activity leakage via shared keys.
- Cost details visible to authorized roles only where backend gates them; frontend hides without bypassing.

## Testing
- `frontend/src/features/activity/*.spec.ts` + `frontend/src/features/cost/*.spec.ts` (ordering, permission gating, estimate labeling, quota states, empty/partial/error).
- Playwright `@activity` journey: complete an action → appears in timeline; quota states render.

## Validation
```bash
npm run typecheck --prefix frontend
npm run test --prefix frontend -- src/features/activity src/features/cost
npx playwright test --grep="@activity"
```

## Completion Criteria
- Activity timeline + cost/quota surfaces + states + tests exist; supersedes the activity/cost half of Task 035.

## Traceability
- Plan B §12.17, §12.18. Split from 035; projections in 002/008; settings in 035B.
