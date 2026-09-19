# Task 022 — Project Creation Wizard

## Goal
Implement the creation wizard with progressive disclosure, immutable target language, and config-hash transparency.

## Context
Entry point for new dubbing work; creates projects via Task 007 endpoints, embeds the Task 023 uploader (or defers upload), and hands off to preflight (Task 024). Users must never be asked about providers, queues, or models.

## Starting State
Task 007 (project creation endpoint with server validation + config hash) and Task 018 (shell, i18n) done. `features/projects` list exists (Task 021); no wizard. Depends on Tasks 007, 018.

## Scope
Included: wizard steps Create→Language→Settings→Upload→Review→Start, progressive disclosure, client+server validation, config-hash display, draft persistence.
Excluded: upload engine internals (Task 023), processing preflight (Task 024), post-creation editing.

## Instructions
1. Create `frontend/src/features/projects/wizard/`: `CreateWizard.tsx` stepper, steps `BasicsStep` (name required, description) → `LanguageStep` (source auto-detect note + target language select) → `SettingsStep` → `UploadStep` (embeds Task 023 uploader or "upload later" skip) → `ReviewStep` → Start; `wizardStore.ts` (zustand, draft persisted to `localStorage`, cleared on submit/discard).
2. Progressive disclosure in `SettingsStep`: collapsed Advanced sections for source-separation policy, output profile, timing strictness, voice policy, glossary editor (`sourceTerm/targetTerm/notes`), style instructions, review threshold; sensible defaults preselected, each with plain-language i18n help text.
3. Target language immutable: `LanguageStep` warns it "cannot be changed after creation"; `ReviewStep` repeats the notice; no edit-after-create UI for target language may exist.
4. Validation: name required (client zod + server 400 mapped via Task 017 `Validation` kind to inline field errors); Start → `POST /projects` → navigate to workspace (or upload-first state when upload deferred).
5. Config-hash transparency: `ReviewStep` shows `settingsVersion` + config hash preview + human-readable summary of chosen settings. Never ask provider/queue/model: no such fields exist (grep-gated).

## Requirements
- R1: Name required, enforced client- and server-side with field-level errors; draft intact on failure.
- R2: Target language uneditable post-creation (no UI path to change it).
- R3: Draft survives refresh until submit/discard (file objects excluded — re-attach after refresh).
- R4: `ReviewStep` displays settings version + config hash + human summary.
- R5: No provider/model/queue inputs exist anywhere in the wizard (grep-gate test).

## Edge Cases and Error Handling
- Session expiry mid-wizard → login → draft preserved → resume at `ReviewStep`.
- Duplicate name allowed (no uniqueness constraint) — show created-date disambiguation hint.
- Server 400 → field-level errors mapped per Task 017 kinds; wizard state preserved.

## Security and Safety Requirements
- Draft in `localStorage` holds only non-sensitive settings (no tokens, no media bytes).
- Server validation authoritative; client validation is UX-only.

## Testing
- Create `frontend/src/features/projects/wizard/__tests__/`: step flow, validation errors, draft persistence round-trip, immutable-language notice, provider-field absence gate.
- Playwright `@project-create`: full wizard → workspace; validation errors; upload-later path.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/projects`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/projects
npx playwright test --grep="@project-create"
```

## Completion Criteria
- Wizard completes Create→Start with progressive disclosure, immutable-language rule, and config-hash review; draft persistence verified; wizard tests + `@project-create` E2E pass.

## Traceability
- Plan B §12.4. Depends on Tasks 007, 018.
