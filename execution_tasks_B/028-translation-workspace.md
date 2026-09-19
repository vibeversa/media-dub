# Task 028 — Translation Workspace

## Goal
Implement the side-by-side translation review workspace with immutable candidates and guarded manual editing.

## Context
Per-segment translation selection for the dub; translation endpoints from Task 009 (candidates immutable, select-or-edit-as-manual with expected-version + 409), transcript rows from Task 027, mounted in the Task 018 project tabs. Candidates are never edited in place — selection or manual edit only.

## Starting State
Tasks 009 (translation select/edit API), 027 (transcript workspace patterns: versioned edits, 409→refresh, lineage display) done. No `features/translation`. Depends on Tasks 009, 027.

## Scope
Included: side-by-side source/selected/alternatives view, `TranslationEditor`, candidate immutability, select-or-edit-as-manual, navigate-away save/discard/cancel guard, conflict→refresh.
Excluded: transcript text editing (Task 027), voice assignment (Task 029), review queue actions (Task 031).

## Instructions
1. Create `frontend/src/features/translation/TranslationWorkspace.tsx`: per-segment side-by-side layout — source text (read-only, from selected transcript version) | selected translation (editable draft) | alternatives list; header row per segment shows speaker, time window, duration, sync status, glossary hits, assigned voice.
2. Create `frontend/src/features/translation/TranslationEditor.tsx`: draft editor for the selected translation with glossary-term highlighting, duration/sync indicator (over/under target window, read-only display), character/reading-speed hint; save creates a manual version, never mutates a candidate.
3. Render candidates from `useTranslations(projectId)` on `queryKeys.translations` as immutable cards (provider/model/version badge, text, score where provided); each card offers Select only — no inline edit affordance on candidates (test asserts candidates render without editable inputs).
4. Implement select (`POST .../translations/select` with `expectedVersion`) and edit-as-manual (`POST .../translations/manual` with `expectedVersion` + text): 409 conflict → stale banner + refetch + preserved draft; success invalidates `queryKeys.translations` (+ Task 026 event wiring).
5. Implement navigate-away guard (`frontend/src/features/translation/useDirtyGuard.ts`): dirty draft blocks route/tab change with save/discard/cancel dialog; save → manual-version flow, discard → reset draft, cancel → stay.
6. Sync/glossary/voice strip per segment: sync badge (in-window/overflow), glossary matches with tooltip definitions, assigned-voice chip linking to Task 029; missing data renders as `—` with explanatory tooltip, never blank.

## Requirements
- R1: Candidates immutable — no edit affordance on candidate cards (test asserts read-only rendering).
- R2: Every select/manual call sends `expectedVersion`; 409 triggers refetch + banner, never silent overwrite.
- R3: Dirty draft always triggers save/discard/cancel on navigate-away (route, tab, or segment change).
- R4: Source text shown is the currently-selected transcript version, with version label.
- R5: Sync/duration display is read-only (no manual timing edits; timing lives in Task 030 as read-only).

## Edge Cases and Error Handling
- 409 on select/manual → stale banner + refresh; draft preserved, never auto-resubmitted.
- No candidates yet (translation pending) → `EmptyState` with progress link, not an empty editor.
- Glossary term missing definition → term highlighted without tooltip, no crash.
- Source transcript version changes mid-edit → dirty guard fires with source-changed notice + rebase option.

## Security and Safety Requirements
- Candidate provider/model metadata displayed only; no secrets, keys, or internal paths.
- Draft text sanitized on display; no raw HTML rendering of source or candidate content.

## Testing
- Create `frontend/src/features/translation/__tests__/translation.test.tsx`: candidate immutability (no inputs), select + 409 refresh, manual edit + rollback, dirty-guard dialog paths (save/discard/cancel), source-version label.
- Playwright `@translation`: side-by-side renders, select updates selected card, dirty guard blocks navigation, conflict shows banner.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/translation`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/translation
npx playwright test --grep="@translation"
```

## Completion Criteria
- Translation workspace selects or manually versions immutable candidates side-by-side with source, guarded by dirty-check and conflict refresh; translation tests + `@translation` E2E pass.

## Traceability
- Plan B §12.10. Depends on Tasks 009, 027.

## Review Fix — Dependency Correction
- **Parallel with 027:** this task depends on 009 + shared editor primitives only — NOT on 027 transcript UI. 027 and 028 may execute in parallel.
