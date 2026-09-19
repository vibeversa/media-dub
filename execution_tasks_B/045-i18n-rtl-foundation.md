# Task 045 — i18n / RTL Foundation

**Required/Optional:** Required
**Complexity:** S

## Goal
Provide the localization, pseudo-locale, and RTL foundation every feature UI builds on.

## Context
Plan B-2 explicitly requires i18n foundation (translation keys, locale-aware dates/numbers, timezone-aware timestamps, pluralization, RTL-compatible layout, default locale from user preference). The visual matrix (Task 041B) requires LTR/RTL coverage, which is impossible without this foundation. This task was missing and is added as an executable prerequisite before feature UI work.

## Starting State
Depends on Tasks 015 (scaffolding), 016 (tokens/primitives). Assumes `frontend/` exists with router/providers, `frontend/src/i18n/` directory exists or is created here.

## Scope
Included: i18n framework wiring, key extraction + no-hardcoded-copy gate, locale/timezone/plural helpers, RTL direction support + smoke, pseudo-locale test, default-locale-from-preference wiring.
Excluded: feature screen copy (owned by 019–036), visual regression matrix (041B), backend locale storage beyond Task 001/006 preferences passthrough.

## Instructions
1. Wire i18n framework in `frontend/src/i18n/` (`index.ts`, `locales/en.json` baseline, `locales/pseudo.json`): translation keys only — no hard-coded user-facing strings in `features/` after this task; add `scripts/check-no-hardcoded-copy.mjs` (or equivalent) that fails on literal UI copy outside `i18n/` + primitives, and run it in validation.
2. Add locale helpers in `frontend/src/lib/dates/` + `frontend/src/lib/formatting/`: locale-aware dates/numbers via `Intl`, timezone-aware timestamp display (user `timezone` preference from Task 006 with UTC fallback), pluralization support per locale.
3. Implement RTL support: `dir` attribute driven by locale (`ltr` default, `rtl` test locale), RTL-compatible CSS (logical properties, no hard-coded left/right in new styles), `?dir=rtl` override for manual testing, documented in `frontend/src/i18n/README.md`.
4. Add pseudo-locale test (`frontend/src/i18n/pseudo.spec.ts`): renders shell + one representative screen under pseudo-locale (expansion + brackets) and asserts no missing-key warnings, no layout crash, `dir` switching works.
5. Wire default locale from user preference: on `/me` resolution (Task 019) apply `locale` preference before first paint; document fallback chain `preference → browser → en`.

## Requirements
- R1: Zero hard-coded user-facing strings in feature code (extraction-gate passes).
- R2: Dates/numbers/timestamps render locale- and timezone-correctly (unit-tested with at least two locales/timezones).
- R3: RTL smoke passes: shell renders with `dir=rtl` without crash or overlapping chrome.
- R4: Pseudo-locale test passes with no missing-key warnings.
- R5: Default locale resolves from user preference with documented fallback.

## Edge Cases and Error Handling
- Missing key → falls back to `en` + console warning in dev, never blank screen; missing-key collector fails the pseudo test on new keys.
- Invalid `locale`/`timezone` preference → safe fallback (`en` / UTC) + warning, never crash.
- RTL + timeline canvas (Task 030) → canvas itself stays LTR-documented; chrome around it mirrors.

## Security and Safety Requirements
- No PII in locale bundles; no fetching remote translation files from untrusted origins (local bundle only unless allowlisted).
- `?dir=` override is presentation-only; never affects authz, tenant scoping, or API contracts.

## Testing
- New: `frontend/src/i18n/pseudo.spec.ts` (pseudo-locale + RTL smoke + fallback chain).
- New: `frontend/src/lib/dates/dates.spec.ts` + `frontend/src/lib/formatting/formatting.spec.ts` (multi-locale/timezone cases).
- Gate: `scripts/check-no-hardcoded-copy.mjs` run in validation.

## Validation
```bash
npm run typecheck --prefix frontend
npm run lint --prefix frontend
npm run test --prefix frontend -- src/i18n src/lib/dates src/lib/formatting
node scripts/check-no-hardcoded-copy.mjs
```

## Completion Criteria
- i18n framework + helpers + RTL + pseudo test + extraction gate exist and pass; feature tasks (019–036) can build locale-ready UI on this foundation.

## Traceability
- Plan B §10.9, §11.5 (RTL/responsive), §15.8 (visual LTR/RTL), §22 (B-2). Unblocks 018/019–036/041B.
