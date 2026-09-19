# Task 036 — Admin, Usage, Provider Health, Diagnostics UI

**Required/Optional:** Required
**Complexity:** M

## Goal
Implement the role-gated Admin area (tenants/users/roles/health/routes/usage/quotas/retention/audit/flags) and the operator diagnostics dashboard with safe destructive-action gates.

## Context
Read APIs come from Task 005 (diagnostics aggregations) and Task 013 (`GET /admin/...`, `GET /diagnostics/...` with elevated authz), mounted in the Task 018 shell with role-based route guards. This is the only surface where elevated operations exist, so every destructive action carries confirm + reason + permission + audit.

## Starting State
Tasks 005, 013, 018 done. No `features/admin`. Depends on Tasks 005 and 013.

## Scope
Included: Admin section (tenants/users/roles/health/routes/usage/quotas/retention/audit/flags), ops dashboard (queues/workers/errors/DLQ/leases/orphans/backlog/failures), elevated gating, secret-free display, destructive-action gates.
Excluded: diagnostics aggregation logic (Tasks 005/013), end-user settings (Task 035), optional local-GPU surfaces (Task 044).

## Instructions
1. Create `frontend/src/features/admin/AdminPage.tsx` + `frontend/src/features/admin/adminGuard.ts`: route guard requiring the elevated role from Task 013; non-elevated users get a 403 `ForbiddenState` (never a redirect loop); guard denial is logged to telemetry without user ids.
2. Create `frontend/src/features/admin/TenantsPanel.tsx` + `UsersRolesPanel.tsx`: tenant list/detail and user-role assignment on `queryKeys.adminTenants` / `queryKeys.adminUsers`; role changes require the assigner to hold a strictly higher grant and emit an audit reason (mandatory text field, min 10 chars).
3. Create `frontend/src/features/admin/OpsDashboard.tsx`: queues/workers/errors/DLQ/leases/orphans/backlog/failure panels fed by `queryKeys.diagnostics` with 30s refetch (paused when tab hidden); DLQ rows expose redrive/discard only where backend advertises `actions[]`; orphan leases show age + owner, never raw lock tokens.
4. Create `frontend/src/features/admin/HealthRoutesPanel.tsx` + `UsageQuotasPanel.tsx` + `RetentionAuditPanel.tsx`: provider health/routes matrix, usage-vs-quota bars (reuse Task 035 quota states), retention policies, and audit-event viewer (timestamp/actor/action/summary; same hidden-advanced rule as Task 035).
5. Create `frontend/src/features/admin/FlagsPanel.tsx`: feature-flag list with safe toggle (immediate, reversible) vs destructive flag-apply gated behind the destructive flow in step 6.
6. Implement `frontend/src/features/admin/DestructiveAction.tsx`: shared gate for every destructive operation — type-to-confirm + mandatory reason + permission re-check + explicit audit emission; on success invalidate affected admin queries + show receipt (action id, timestamp); on 403 show `ForbiddenState` without leaking the required role name.
7. Enforce no-secrets display: secret-free DTO assertion — scan rendered admin output for `secret|password|connectionString|apiKey|privateKey` in tests; connection strings render as masked fingerprints only.

## Requirements
- R1: Non-elevated users cannot reach any admin route (guard + 403 state, verified by negative test).
- R2: All seven admin areas (tenants/users/roles/health/routes/usage/quotas/retention/audit/flags) render from read APIs with loading/empty/failure states.
- R3: Ops dashboard shows queues/workers/errors/DLQ/leases/orphans/backlog/failures with advertised-actions-only controls.
- R4: Every destructive action requires confirm + reason + permission + audit receipt.
- R5: No secrets/connection strings/tokens appear anywhere in admin UI (scan test).
- R6: Role assignment requires higher-grant assigner + mandatory audit reason.

## Edge Cases and Error Handling
- Elevated role revoked mid-session → next admin query 403 → guard locks the section + toast, session otherwise intact.
- DLQ redrive 409 (already redriven) → row refreshes + toast, no duplicate redrive.
- Empty DLQ / zero orphans → healthy `EmptyState`, not hidden panels.
- Audit viewer pagination gap (retention expiry) → explicit "older events expired per retention policy" marker.
- Flag toggle during rollout freeze (backend 423) → dialog explains freeze, toggle reverts.

## Security and Safety Requirements
- Elevated gating enforced both in route guard and per-query 403 handling; client never caches the elevated role claim.
- No provider/DB/storage secrets reach the frontend; masked fingerprints only, never reversible.
- Destructive actions always carry confirm + reason + permission check + audit receipt; reason text sanitized before display.

## Testing
- Create `frontend/src/features/admin/__tests__/admin.test.tsx`: guard allows/denies, destructive gate (missing reason blocks), advertised-actions-only DLQ controls, no-secrets scan, quota-state reuse.
- Create `tests/DubbingPlatform.IntegrationTests/Admin/AdminAuthzTests.cs`: non-elevated callers get 403 on every `/admin/...` and `/diagnostics/...` endpoint; elevated callers succeed; destructive ops without reason/audit are rejected.
- Playwright `@admin`: elevated login reaches ops dashboard, destructive flow requires reason, non-elevated login sees 403 state.
- Type: unit (vitest) + backend integration (Testcontainers) + Playwright.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/admin
dotnet test --filter FullyQualifiedName~AdminAuthzTests
npx playwright test --grep="@admin"
```

## Completion Criteria
- Role-gated Admin + ops dashboard render all areas with safe destructive gates and zero secret leakage; admin unit tests, `AdminAuthzTests`, and `@admin` E2E pass.

## Traceability
- Plan B §12.19, §19.3. Depends on Tasks 005 and 013.
