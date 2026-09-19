# Task 041C — Accessibility Audit (WCAG 2.2 AA)

**Required/Optional:** Required
**Complexity:** M

## Goal
Prove WCAG 2.2 AA across keyboard, focus, labels, contrast, motion, dialogs, media, and live regions.

## Context
Split from Task 041. Accessibility behavior must be built into primitives (016) and feature screens (019–036); this task is the audit gate, not the implementation. It runs axe-core plus manual keyboard scripts over the same deterministic fixtures as 041B.

## Starting State
Depends on Tasks 016 (primitives carry focus/label/contrast/motion behavior), 019–036 (screens under audit), 046 (keyboard scripts harness). Task 041 (combined) is superseded.

## Scope
Included: `e2e/a11y/` axe + keyboard/focus/contrast/motion/dialog/media/live-region checks; `npm run test:a11y` gate.
Excluded: feature/a11y fixes (owned by 016/019–036), visual (041B), perf (041D), journeys (041A).

## Instructions
1. Add `e2e/a11y/*.spec.ts` run via `npm run test:a11y` (axe-core + manual scripts): full keyboard traversal (upload → review → export without a mouse), visible focus on every interactive element, labels on all inputs, contrast ≥ 4.5:1 (both themes), `prefers-reduced-motion` disables waveform/progress animation, dialogs trap + restore focus, media players keyboard-operable, live regions announce progress completion/errors (assert via `aria-live` capture).
2. Critical live events must not spam screen readers: assert announcement count caps for progress streams (026) — completion/error announced once, intermediate ticks suppressed.
3. Third-party widget violation → wrapper fix or replace in owning feature task; axe skip-rules require expiry dates and are rejected in review without one.
4. Record per-screen results in `e2e/a11y/README.md` (screen → criteria → pass/fail/waiver-with-expiry).

## Requirements
- R1: Keyboard-complete: every flow achievable without a mouse.
- R2: Focus-visible + labeled + contrast-safe in both themes.
- R3: Motion-respecting (`prefers-reduced-motion` disables non-essential animation).
- R4: Dialog-correct (trap + restore) and media-operable.
- R5: Live-region-announced without spam (capped progress announcements).

## Edge Cases and Error Handling
- Axe false positive → manual verification recorded; skip-rule only with expiry + issue link.
- Focus loss after modal close → fails (must restore to invoker).
- Dynamic content (SSE progress) → announcements asserted on completion/error only, never per-tick.

## Security and Safety Requirements
- A11y tests assert forbidden content is not exposed to assistive tech (e.g. hidden secrets never in `aria-label`s).
- Test recordings contain synthetic data only.

## Testing
- Added specs: `e2e/a11y/` + README matrix.
- Type: Playwright a11y (Chromium) + axe-core.

## Validation
```bash
npm run test:a11y
npx playwright test --grep="@a11y"
```

## Completion Criteria
- WCAG 2.2 AA audit green with per-screen matrix; supersedes the a11y quarter of Task 041.

## Traceability
- Plan B §10.10, §11.4 (live states), §15.9. Split from 041; behavior in 016/019–036.
