# Task 041B — Visual Regression

**Required/Optional:** Required
**Complexity:** M

## Goal
Pin the 12-screen visual matrix across breakpoints, themes, and directions with deterministic fixtures.

## Context
Split from Task 041. Visual regression is a non-functional gate over feature UI (019–036 + 045); it authors no feature specs and no journeys. Requires the i18n/RTL foundation (045) for the LTR/RTL axis and the harness (046) for deterministic fixtures.

## Starting State
Depends on Tasks 019–036 (screens exist), 045 (RTL + pseudo-locale), 046 (fixtures, frozen clock, masked timestamps). Task 041 (combined) is superseded.

## Scope
Included: `e2e/visual/` specs + fixtures for 12 screens × desktop/tablet/mobile × dark/light × LTR/RTL; structural assertions for dynamic progress (never pixel-compare animations).
Excluded: feature specs (019–036), journeys/smoke (041A), a11y (041C), perf (041D).

## Instructions
1. Add `e2e/visual/*.spec.ts` (tagged `@visual`) + `e2e/visual/fixtures/` (seeded data via 046, frozen clock, masked timestamps/IDs): 12 screens — login, dashboard, project list, wizard, upload, workspace, transcript, translation, voices, review, QC, outputs/exports (+ settings/admin as covered screens where changed).
2. Matrix: desktop (≥1280) / tablet (768–1279) / mobile (<768) × dark/light (where supported) × LTR/RTL (045 `?dir=`); mobile timeline degrades to list inspection per responsive rules — assert the degraded layout, not the desktop editor.
3. Dynamic progress rule: assert progress presence structurally (role/label), compare static layout only; never pixel-compare animated progress/waveform frames.
4. Font-rendering rule: approved font-substitution list for cross-OS diffs; failures beyond it are investigated, not auto-blessed; baselines versioned per theme/direction.

## Requirements
- R1: 12 screens × 3 breakpoints × themes × LTR/RTL baselines exist and pass.
- R2: Deterministic fixtures (no live data, no real clock, masked IDs).
- R3: No pixel comparison of dynamic progress/animation.
- R4: Responsive degradation (mobile list vs desktop editor) asserted, not snapshotted as failure.
- R5: Baseline updates require explicit re-baseline commit with reviewer approval.

## Edge Cases and Error Handling
- New screen added by a feature PR → visual spec required in the same PR or explicitly deferred with issue link.
- RTL-only breakage → fails with direction-labeled diff, never silently skipped.
- Theme unsupported for a screen → matrix entry marked N/A in code with reason, not deleted.

## Security and Safety Requirements
- Visual fixtures synthetic only; screenshots scrubbed (046 scrubber) before CI attach.
- Baselines contain no real user data or signed URLs.

## Testing
- Added specs: `e2e/visual/` + fixtures.
- Type: Playwright visual (Chromium) + `npm run test:visual`.

## Validation
```bash
npx playwright test --grep="@visual"
npm run test:visual
```

## Completion Criteria
- Full matrix green with versioned baselines; supersedes the visual quarter of Task 041.

## Traceability
- Plan B §11.5, §15.8. Split from 041; RTL in 045; harness in 046.
