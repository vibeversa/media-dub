# Task 016 — Design Tokens and Core UI Primitives

## Goal
Implement the token system and domain-agnostic component library backing all product screens.

## Context
Every feature task (019+) consumes these primitives; no feature may invent one-off buttons, badges, or dialogs — variants come from here. Status visualization must cover every backend status with consistent tokens.

## Starting State
Task 015 done (frontend scaffold, Tailwind configured). No `tokens.css`, no `components/`. Depends on Task 015.

## Scope
Included: `tokens.css`/`globals.css`, all listed primitives + shared product components, Storybook.
Excluded: feature screens, API wiring, shell layouts, i18n string content (Task 018).

## Instructions
1. Create `frontend/src/styles/tokens.css`: CSS custom properties for color (brand/neutral/surface/text/border + status: `success, warning, error, info, neutral, processing, review, cancelled`), spacing scale, type scale (font-size/line-height/weight), radius, shadow/elevation; dark-theme overrides via `[data-theme="dark"]`.
2. Create `frontend/src/styles/globals.css`: Tailwind directives + base resets, focus-visible ring, `prefers-reduced-motion` handling, scrollbar styling, RTL-safe logical properties (`margin-inline`, `padding-inline`, `inset-inline` — never physical left/right in shared CSS).
3. Implement primitives in `frontend/src/components/` (one folder per component, each with `.tsx` + `.test.tsx` + `.stories.tsx`): `Button`, `IconButton`, `Input`, `Textarea`, `Select`, `Combobox`, `Checkbox`, `Radio`, `Switch`, `Slider`, `Modal`, `Drawer`, `Popover`, `Tooltip`, `Tabs`, `Accordion`, `Table`, `DataGrid`, `Pagination`, `Badge`, `StatusBadge` (maps every backend status → token: success/warning/error/info/neutral/processing/review/cancelled), `ProgressBar`, `Ring`, `Skeleton`, `Alert`, `Toast` (+ `ToastProvider`/`useToast`), `EmptyState`, `ErrorState`, `ConfirmDialog`, `CommandMenu`, `Breadcrumbs`, `Card`, `Panel`.
4. Implement shared product components in `frontend/src/components/product/`: `EntityId` (monospace id with copy button), `RelativeTime` (locale-aware), `CostDisplay` (currency formatting), `QuotaMeter` (usage bar with warning threshold), `ProviderBadge` (healthy/degraded/down), `CorrelationId` (debug-only display with copy).
5. Accessibility: every interactive primitive keyboard-operable; focus-trapped dialogs (`Modal`/`Drawer`/`ConfirmDialog` trap + restore focus on close); `aria-*` labels; `Tooltip`/`Popover` escape-to-close; `Toast` region `aria-live="polite"`; `DataGrid` row keyboard navigation.
6. Storybook: `frontend/.storybook/main.ts` + `preview.ts` (light/dark theme switcher, RTL toggle, locale selector); stories cover all variants/states of each primitive; `npm run build-storybook` passes.

## Requirements
- R1: All color usage references tokens; no hardcoded hex outside `tokens.css` (stylelint `color-no-hex` or grep gate in CI).
- R2: `StatusBadge` covers every backend status enum from the generated client (exhaustiveness type-check; a new enum value without mapping fails typecheck).
- R3: Dialogs trap focus and restore focus on close (test-asserted).
- R4: RTL: logical properties only in shared CSS; LTR+RTL covered in Storybook (visual tests consume in Task 041).
- R5: `Toast` queue caps at 3 visible and dedupes identical messages.

## Edge Cases and Error Handling
- Unknown status string at runtime → `StatusBadge` renders `neutral` variant + telemetry warning (never crashes).
- Content overflow: `Tooltip`/`Popover` clamp to viewport; `Table`/`DataGrid` horizontal scroll with sticky header.
- Reduced motion: animations disabled via media query; `ProgressBar`/`Ring` render statically.

## Security and Safety Requirements
- No `dangerouslySetInnerHTML` in primitives (lint-ban); untrusted strings render as plain text.
- `Toast`/`Alert` never render raw backend HTML; error `details` shown as pre-formatted text only.

## Testing
- Colocated tests `frontend/src/components/*/*.test.tsx` (vitest + Testing Library): render variants, keyboard interaction, focus trap, unknown-status fallback.
- Type: unit/component (vitest); run `npm run test -- src/components`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/components
cd frontend && npm run build-storybook
```

## Completion Criteria
- Tokens + all primitives + product components exist with stories; typecheck/tests/Storybook build pass; status exhaustiveness enforced at compile time.

## Traceability
- Plan B §11.2, §11.3. Depends on Task 015.

## Review Fix — Dependency Correction
- **Unblocked from 014:** tokens/primitives proceed after 015 styling baseline only; the generated API client is NOT required for this task. Remove any 014 dependency; 017 API wiring consumes 014 separately.
