# Task 020 — Dashboard

## Goal
Implement the dashboard summary with drill-down into projects, outputs, quota, and warnings.

## Context
First landing screen after login; data from `GET /dashboard/summary` (Task 007) via `queryKeys.dashboard` (Task 017), mounted in the Task 018 shell. Must handle empty tenants and partial backend failures gracefully.

## Starting State
Task 007 (dashboard summary endpoint) and Task 018 (shell, IA, telemetry) done. No `features/dashboard`. Depends on Tasks 007, 018.

## Scope
Included: summary cards (counts, recent outputs, storage/cost, quota, provider warnings, backlog) + drill-down links, loading/empty-tenant/partial/quota-warning/error states.
Excluded: project list (Task 021), workspace (Task 025), admin diagnostics (Task 036).

## Instructions
1. Create `frontend/src/features/dashboard/`: `api.ts` (`useDashboardSummary` on `queryKeys.dashboard`), `DashboardPage.tsx`, components `StatCard`, `RecentOutputs`, `StorageCostCard`, `QuotaCard`, `ProviderWarnings`, `BacklogCard`.
2. Summary content: counts (active / in-review / failed / completed), recent outputs (deep-links to project/output), storage + cost totals, quota usage vs limit, provider warnings (degraded/unhealthy), review backlog count; every card drill-down navigates to the filtered list/workspace view.
3. States: loading skeletons; empty-tenant (zero projects → onboarding CTA routing to the creation wizard); partial (per-card error with retry, rest renders); quota-warning (≥80% meter + manage link); global error (`ErrorState` + retry).

## Requirements
- R1: Data only from `GET /dashboard/summary` (+ drill-down links); no extra aggregate calls.
- R2: Quota ≥80% shows warning meter; 100%/exceeded blocks costly actions with guidance.
- R3: Partial failure isolates per card; one failed section never blanks the page.
- R4: Empty-tenant CTA routes to the project creation wizard (Task 022).

## Edge Cases and Error Handling
- All-zero tenant → empty-tenant onboarding, not zero-cards grid.
- Stale cache (304) → render stale + background refresh indicator.
- Provider warnings empty → section hidden, not an empty card.
- Cost formatted in tenant currency/locale (Task 018 formatters).

## Security and Safety Requirements
- Cost/quota cards render only with permission; hidden without permission (no error flash leaking existence).
- No per-user data of other members displayed.

## Testing
- Create `frontend/src/features/dashboard/__tests__/`: summary render, drill-down links, empty-tenant CTA, partial-state isolation (MSW error injection per section), quota-warning threshold.
- Playwright `@dashboard`: counts render, drill-down navigation, empty-tenant CTA, partial state.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/dashboard`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/dashboard
npx playwright test --grep="@dashboard"
```

## Completion Criteria
- Dashboard renders all summary sections with drill-down; empty/partial/quota/error states verified; dashboard tests + `@dashboard` E2E pass.

## Traceability
- Plan B §12.2. Depends on Tasks 007, 018.
