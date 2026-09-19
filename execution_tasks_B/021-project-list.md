# Task 021 — Project List

## Goal
Implement the filterable, paginated project list with valid-actions-only row operations.

## Context
Primary project navigation; data from project endpoints (Task 007) via `queryKeys.projects` (Task 017), mounted in the Task 018 shell. Row actions must reflect backend-allowed actions only — never show operations the server would reject.

## Starting State
Task 007 (project CRUD/archive endpoints with `actions[]` allowlist) and Task 018 (shell, IA) done. `features/projects` does not exist yet. Depends on Tasks 007, 018.

## Scope
Included: columns, filters, server-side sort/pagination, URL-synced state, valid-actions-only row actions (open/cancel/retry/export/delete/archive).
Excluded: creation wizard (Task 022), workspace (Task 025), export download UX (Task 033).

## Instructions
1. Create `frontend/src/features/projects/`: `ProjectsPage.tsx`, `ProjectTable.tsx` (on Task 016 `DataGrid`), `ProjectFilters.tsx`, `useProjectsQuery.ts` (server-side pagination/sort/filter via `queryKeys.projects`; state synced to URL search params for shareable links).
2. Columns: name, media (source badge + duration), target language, status (`StatusBadge`), progress (approximate %), review (open count), created, activity (relative time); row click → workspace.
3. Filters: status, target language, review state, archived toggle (excluded by default), owner, created-date range; sort by created/activity/name/progress; page-size selector; preserve scroll on page change.
4. Valid-actions-only: per-row actions derived strictly from backend `actions[]` — `open/cancel/retry/export/delete/archive(/unarchive)` rendered only when allowed, never shown-disabled; destructive actions (delete/cancel) behind `ConfirmDialog` (+ typed name confirmation for delete, reason where required).

## Requirements
- R1: Filters/sort/page synced to URL (deep-linkable, back-button safe).
- R2: Pagination and sorting server-side; no client-side slicing of server pages.
- R3: Actions rendered strictly from backend `actions[]` allowlist (test asserts disallowed action absent from DOM).
- R4: Archived projects excluded by default; included only with archived filter on.

## Edge Cases and Error Handling
- Empty filter result → `EmptyState` + clear-filters action (not a blank table).
- Project deleted mid-page → row removed + toast (no full-page error).
- Sort on progress uses approximate ordering (note in column tooltip, consistent with no-fake-precision rule).

## Security and Safety Requirements
- Client-side permission check before showing actions, but server authoritative: 403 → toast + row refresh.
- Delete requires typed name confirmation + audit reason; cancel requires confirmation.

## Testing
- Create `frontend/src/features/projects/__tests__/`: URL sync round-trip, action-allowlist rendering, server-side pagination params, archived-default exclusion.
- Playwright `@projects`: filter/paginate/navigate, valid-actions-only behavior, delete confirmation.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/projects`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/projects
npx playwright test --grep="@projects"
```

## Completion Criteria
- List renders all columns with URL-synced server-side filter/sort/pagination; actions strictly allowlisted with confirmations; projects tests + `@projects` E2E pass.

## Traceability
- Plan B §12.3. Depends on Tasks 007, 018.
