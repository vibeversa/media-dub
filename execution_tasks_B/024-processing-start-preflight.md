# Task 024 — Processing Start and Preflight

## Goal
Implement cost/quota/consent preflight with explicit confirmation before any costly run starts.

## Context
Gate between a ready project (Task 023 media ready) and the processing pipeline (Task 008 endpoints, Task 025 workspace). Estimates are labeled estimates — never promises — and voice-cloning consent must be acknowledged before start.

## Starting State
Task 008 (processing endpoints with estimate + idempotency + 409 conflict) and Task 018 (shell, dialogs, i18n) done. No `features/processing`. Depends on Tasks 008, 018.

## Scope
Included: preflight dialog (estimate, quota, cloning warnings), explicit costly-op confirmation, 202 → workspace navigation, conflict-action disabling.
Excluded: workspace shell (Task 025), live progress (Task 026), cost accounting display (Task 035).

## Instructions
1. Create `frontend/src/features/processing/`: `PreflightDialog.tsx`, `usePreflight.ts` (estimate fetch), `CostEstimateCard.tsx`.
2. Preflight dialog on Start processing: cost/time estimate labeled "estimate", quota impact (remaining after run), per-cloned-voice consent warnings (explicit acknowledge checkbox covering all listed voices), configuration summary hash.
3. Explicit confirmation: confirm button disabled until estimate loaded + consent acknowledged (+ override reason entered when over quota); button label states cost ("Start run — est. $X").
4. 202 → navigate to workspace (`/projects/:id?started=1`) with "Run started" toast; 409 active-run conflict → dialog switches to conflict state: start actions disabled with explanation + link to workspace to cancel first (conflict-action disabling, not hiding).
5. Send idempotency key on start (Task 017 `useAppMutation`); double-click submits once.

## Requirements
- R1: No processing start without dialog confirmation (no direct-start code path).
- R2: Estimate always labeled "estimate" adjacent to the figure (test asserts label).
- R3: Cloning warnings block start until acknowledged (button stays disabled).
- R4: 202 navigates to workspace with confirmation toast.
- R5: Conflicting actions disabled with reason in conflict state (never silently enabled).

## Edge Cases and Error Handling
- Estimate fetch fails → start blocked with "estimate unavailable" + retry (never start blind on a costly op).
- Quota exceeded → start blocked with upgrade/manage path.
- Double-click Start → single effective run via idempotency key.

## Security and Safety Requirements
- Cost figures display-only, never editable client-side.
- Consent acknowledgement ships timestamp + user to backend; server re-validates (Task 008) — no devtools bypass.

## Testing
- Create `frontend/src/features/processing/__tests__/`: confirmation gating (button enablement matrix), estimate labeling, conflict-state disabling, idempotent double-submit (MSW).
- Playwright `@processing-start`: dialog flow → workspace nav; conflict state; quota-exceeded block.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/processing`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/processing
npx playwright test --grep="@processing-start"
```

## Completion Criteria
- Preflight dialog with labeled estimate, quota, and consent gating works; 202 → workspace; conflicts disable actions with explanation; processing-start tests + `@processing-start` E2E pass.

## Traceability
- Plan B §12.6, §12.18. Depends on Tasks 008, 018.
