# Task 035B — Settings and Preferences UI

**Required/Optional:** Required
**Complexity:** S

## Goal
Implement the user settings and preferences screens backed by the preferences API.

## Context
Split from oversized Task 035 (activity + cost + settings). Preferences (`locale, timezone, theme, defaultProjectFilters, timelineZoom, notificationPreferences`) persist via Task 006; this task is their UI, including locale application from new Task 045. Activity/cost move to 035A.

## Starting State
Depends on Tasks 006 (preferences API), 018 (shell), 045 (locale/timezone application). Task 035 (combined) is superseded by 035A + 035B.

## Scope
Included: settings screens, per-key editors, optimistic update + rollback, validation, dirty/unsaved-guard states.
Excluded: activity timeline/cost UI (035A), notification center (034), backend preference storage (001/006), i18n framework itself (045).

## Instructions
1. Build `frontend/src/features/settings/SettingsPage.tsx` + `PreferencesForm.tsx`: editors for locale (select from supported list in 045), timezone (select + UTC fallback), theme (light/dark/system), default project filters, timeline zoom default, notification preferences (channel toggles; email/webhook shown as future-only where applicable).
2. Wire `GET|PUT /api/v1/me/preferences` via 017 client: optimistic update with rollback on 4xx/5xx, per-field server-validation errors, dirty state + unsaved-change guard on navigation, success toast.
3. Apply locale/timezone/theme immediately through 045 helpers on save (no reload required); record preference change in activity where backend projects it (no direct activity writes here).
4. Cover states: loading/skeleton, validation error, save conflict (409 → refetch + keep draft), offline (queued-save notice, not silent loss).

## Requirements
- R1: All six preference keys editable and round-tripped through the API.
- R2: Unknown keys rejected server-side; UI never sends unlisted keys.
- R3: Locale/timezone/theme apply without reload.
- R4: Dirty/navigate-away guard offers save/discard/cancel.
- R5: No secrets stored in preferences (4KB cap respected, asserted).

## Edge Cases and Error Handling
- Concurrent edit (two tabs) → last-write-wins per key with conflict notice, never silent cross-key overwrite.
- Invalid timezone selection → 400 maps to inline field error + UTC fallback suggestion.
- Save fails offline → draft preserved + retry action, never discarded.

## Security and Safety Requirements
- Preferences scoped to caller (tenant + user); no editing other users preferences (403 + explanation).
- Preference values sanitized (max lengths); nothing rendered as raw HTML.

## Testing
- `frontend/src/features/settings/*.spec.ts` (editors, optimistic rollback, guards, conflict, offline draft).
- Playwright `@settings` journey: change locale/theme → persists across reload.

## Validation
```bash
npm run typecheck --prefix frontend
npm run test --prefix frontend -- src/features/settings
npx playwright test --grep="@settings"
```

## Completion Criteria
- Settings/preferences UI + guards + tests exist; supersedes the settings half of Task 035.

## Traceability
- Plan B §8.1, §9.1, §12.17–§12.18 (prefs slice). Split from 035; storage in 001/006; i18n application in 045.
