> **REVIEW FIX — SUPERSEDED (split) + E2E OWNERSHIP FIX:** this combined file is superseded by `041A-journeys-smoke.md` + `041B-visual-regression.md` + `041C-accessibility.md` + `041D-performance.md`. Feature-level E2E specs (`e2e/features/<name>.spec.ts`, tags `@auth/@dashboard/@projects/@project-create/@upload/@processing-start/@workspace/@progress/@transcript/@translation/@voices/@timeline/@review/@quality/@exports/@notifications/@activity/@settings/@admin`) belong to Tasks 019–036, NOT to 041. 041A–D own journeys/smoke, visual, a11y, perf only.

# Task 041 — Playwright E2E, Full Smoke, Visual, A11y, Performance

**Required/Optional:** Required
**Complexity:** L

## Goal
Prove the §24 end-to-end journey plus visual, WCAG 2.2 AA, and performance-budget gates with Playwright.

## Context
All product flows from Tasks 019–036 must be exercised as user journeys per Plan B §15.6–§15.10 and §24: login through export plus cancel/retry/stale-conflict/401/403 paths; full smoke runs real FE+API+PG+storage+transport with only AI mocked; visual regression pins 12 screens across breakpoints/themes/directions; accessibility targets WCAG 2.2 AA; performance budgets guard interactive latency.

## Starting State
Tasks 019–036 done. Depends on Tasks 019–036.

## Scope
Included: Playwright journey scenarios, full smoke, visual regression (12 screens × desktop/tablet/mobile × dark/light × LTR/RTL), WCAG 2.2 AA audit, perf budgets.
Excluded: unit/component/MSW (Task 039), integration/cross-layer (Task 040), CI wiring (Task 042).

## Instructions
1. Create `e2e/journeys/*.spec.ts` covering in order: login, project create, upload, resume-after-reload, processing start (preflight confirm), live progress, workspace open, transcript edit, translation edit, voice assign, review resolve, output download, export request, cancel, retry, stale-version conflict, forced 401 (expired session → login), forced 403 (viewer attempts edit → forbidden state).
2. Create `e2e/smoke/full-smoke.spec.ts` tagged `@smoke`: the §24 happy path on real FE+API+PG+storage+transport with deterministic fixtures and mocked AI providers; asserts durable artifacts (project row, segments, export file) exist post-run; failing smoke blocks release (Task 042 gates on it).
3. Create `e2e/visual/*.spec.ts` + fixtures in `e2e/visual/fixtures/` tagged `@visual`: 12 screens (login, dashboard, project list, wizard, upload, workspace, transcript, translation, voices, review, QC, outputs/exports) × desktop/tablet/mobile × dark/light × LTR/RTL; deterministic fixtures (seeded data, frozen clock, masked timestamps); never pixel-compare dynamic progress animations — assert their presence structurally, compare static layout only.
4. Create `e2e/a11y/*.spec.ts` run via `npm run test:a11y` (axe-core + manual keyboard scripts): full keyboard traversal (upload → review → export without a mouse), visible focus on every interactive element, labels on all inputs, contrast ≥ 4.5:1 (both themes), `prefers-reduced-motion` disables waveform/progress animation, dialogs trap + restore focus, media players keyboard-operable, live-regions announce progress completion/errors (assert via `aria-live` capture, not screenshots).
5. Define perf budgets in `e2e/perf/budgets.ts` + `e2e/perf/*.spec.ts`: app-interactive ≤ 3s (desktop broadband), project-list render ≤ 1.5s at 200 rows, workspace open ≤ 2s, timeline interaction latency ≤ 100ms p95, segment search ≤ 300ms at 5k segments, media seek ≤ 500ms; budgets asserted in CI with trace artifacts on breach.
6. Stabilize the harness in `e2e/support/`: seeded auth (all roles), storage-emulator reset, SSE-aware waits (event-driven, no fixed sleeps), flake quarantine — any test failing 2/50 runs is quarantined with an owner + issue link, never silently retried.

## Requirements
- R1: All 17 journey scenarios pass in sequence on a clean environment.
- R2: Full smoke proves real FE+API+PG+storage+transport with mocked AI and post-run artifact assertions.
- R3: Visual regression covers 12 screens × 3 breakpoints × 2 themes × LTR/RTL with deterministic fixtures and no dynamic-progress pixel compares.
- R4: WCAG 2.2 AA: keyboard-complete, focus-visible, labeled, contrast-safe, motion-respecting, dialog-correct, media-operable, live-region-announced.
- R5: All six perf budgets hold in CI; breaches attach traces and fail the run.
- R6: Flake quarantine policy enforced — no silent retries, every quarantine has owner + issue.

## Edge Cases and Error Handling
- AI mock outage → journeys fail fast with `AI_MOCK_UNAVAILABLE`, never hang on timeouts.
- Visual diff from font rendering across OS → approved font-substitution list, failures beyond it investigated not blessed.
- A11y violation in third-party widget → wrapper fix or replace, never an axe skip-rule without expiry date.
- Perf breach on shared CI runners → 3-run median before verdict, breach opens a perf issue automatically.
- Stale-conflict journey must end with local text preserved post-refresh (data-loss assertion).

## Security and Safety Requirements
- E2E fixtures contain synthetic PII only; screenshots/screencasts scrubbed of any injected secrets.
- Forced-401/403 journeys assert structured errors + correlation IDs, never stack traces or internal paths.
- Smoke environment credentials ephemeral per run; never committed or logged.

## Testing
- This task IS the E2E layer: `e2e/journeys/`, `e2e/smoke/`, `e2e/visual/`, `e2e/a11y/`, `e2e/perf/` + `e2e/support/`.
- Type: Playwright (Chromium + WebKit + Firefox for journeys/smoke; Chromium for visual/a11y/perf).

## Validation
```bash
npx playwright test
npm run test:a11y
npm run test:visual
```

## Completion Criteria
- All journeys, full smoke, visual matrix, WCAG 2.2 AA audit, and perf budgets pass; quarantine log empty-or-owned; `playwright test`, `test:a11y`, `test:visual` green.

## Traceability
- Plan B §15.6–§15.10, §24. Depends on Tasks 019–036.

