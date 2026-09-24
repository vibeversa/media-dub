# 016 — Design Tokens and Core UI Primitives

## Status
COMPLETED

## Summary
Implemented the token system and full domain-agnostic component library: `tokens.css` (brand/neutral/surface/text/border + 8 status tokens, spacing/type/radius/shadow, dark overrides) and `globals.css` (Tailwind + focus ring + reduced-motion + logical properties), 33 primitives plus 6 product components each with `.tsx` + `.test.tsx` + `.stories.tsx`, and real Storybook 8 (`main.ts` + `preview.ts` with theme/RTL/locale globals). `StatusBadge` maps every backend status exhaustively at compile time with neutral + console-warn fallback at runtime; dialogs trap and restore focus; Toast caps at 3 with dedupe; `check-no-hex` gate and `dangerouslySetInnerHTML` lint-ban enforce token and text-only rules. Validation passes: typecheck, 50 component tests, Storybook build, frontend build, backend build + 412 unit tests.

## Files Created/Modified
- `frontend/src/styles/tokens.css` (created) — all CSS vars; only file allowed to contain hex.
- `frontend/src/styles/globals.css` (created) — Tailwind directives, base resets, focus-visible ring, reduced-motion, scrollbar, logical properties only.
- `frontend/src/styles/index.css` (modified) — now imports `tokens.css` + `globals.css`.
- `frontend/tailwind.config.ts` (modified) — token-backed theme (`var(--color-*)`), dark mode via `[data-theme="dark"]`, no hex.
- `frontend/eslint.config.js` (modified) — `dangerouslySetInnerHTML` ban via `no-restricted-syntax`; ignores `storybook-static/`.
- `frontend/package.json` (modified) — adds `storybook@8.6.18`, `@storybook/react@8.6.18`, `@storybook/react-vite@8.6.18`; `check:no-hex` script; `build-storybook` now `storybook build -o storybook-static`.
- `frontend/package-lock.json` (modified) — Storybook dependency closure.
- `frontend/tsconfig.json` (modified) — includes `.storybook/main.ts`, `.storybook/preview.ts`; placeholder script removed.
- `frontend/.storybook/main.ts` (created) — stories glob `../src/**/*.stories.tsx`, `react-vite` framework.
- `frontend/.storybook/preview.ts` (created) — theme/direction/locale toolbar globals + decorator setting `data-theme`/`dir`/`lang`; imports global CSS.
- `frontend/scripts/build-storybook-placeholder.mjs` (deleted) — replaced by real Storybook build.
- `frontend/src/components/_shared/cx.ts` (created) — `cx()` classnames helper.
- `frontend/src/components/_shared/useFocusTrap.ts` (created) — `useFocusTrap(active, ref)` with focus restore.
- `frontend/src/components/_shared/useEscape.ts` (created) — `useEscape(active, onEscape)`.
- `frontend/src/components/Button/` (`.tsx` + `.test.tsx` + `.stories.tsx`) — `Button` primary/secondary/ghost/danger × sm/md/lg + loading.
- `frontend/src/components/IconButton/` — `IconButton` with `label` accessible name.
- `frontend/src/components/Input/` — `Input` label/hint/error + `aria-describedby`.
- `frontend/src/components/Textarea/` — `Textarea` label/error.
- `frontend/src/components/Select/` — `Select` native select + `SelectOption`.
- `frontend/src/components/Combobox/` — `Combobox` filterable listbox, arrow-key nav.
- `frontend/src/components/Checkbox/` — `Checkbox` native toggle.
- `frontend/src/components/Radio/` — `Radio` native radio.
- `frontend/src/components/Switch/` — `Switch` role=switch with RTL-aware thumb.
- `frontend/src/components/Slider/` — `Slider` native range with readout.
- `frontend/src/components/Modal/` — `Modal` focus-trapped portal, Escape closes.
- `frontend/src/components/Drawer/` — `Drawer` side panel, focus-trapped, Escape closes.
- `frontend/src/components/Popover/` — `Popover` toggle panel, Escape closes, viewport-clamped.
- `frontend/src/components/Tooltip/` — `Tooltip` hover/focus bubble, Escape closes.
- `frontend/src/components/Tabs/` — `Tabs` roving-tabindex arrow-key tabs.
- `frontend/src/components/Accordion/` — `Accordion` stacked disclosures.
- `frontend/src/components/Table/` — generic `Table<T>` sticky header + scroll wrapper.
- `frontend/src/components/DataGrid/` — generic `DataGrid<T>` Up/Down + Enter `onRowActivate`.
- `frontend/src/components/Pagination/` — `Pagination` page/pageSize/total + `onPageChange`.
- `frontend/src/components/Badge/` — `Badge` 8-tone pill (`BadgeTone`).
- `frontend/src/components/StatusBadge/StatusBadge.tsx` — `StatusBadge` component (neutral fallback + warn).
- `frontend/src/components/StatusBadge/statusMap.ts` — `BackendStatus`, `StatusVariant`, `statusToVariant()`, `KNOWN_STATUSES`, `isKnownStatus()`, `warnUnknownStatus()`, all domain mirrors.
- `frontend/src/components/StatusBadge/StatusBadge.test.tsx` — mapping, unknown fallback, generated-state coverage.
- `frontend/src/components/StatusBadge/StatusBadge.stories.tsx` — processing/success/error/review/cancelled/unknown.
- `frontend/src/components/ProgressBar/` — `ProgressBar` determinate + ARIA.
- `frontend/src/components/Ring/` — `Ring` SVG determinate ring.
- `frontend/src/components/Skeleton/` — `Skeleton` aria-hidden placeholders.
- `frontend/src/components/Alert/` — `Alert` title + text-only `details` in `<pre>`.
- `frontend/src/components/Toast/Toast.tsx` — `ToastProvider` (cap 3, dedupe, `aria-live="polite"` region).
- `frontend/src/components/Toast/toastContext.ts` — `ToastContext`, `ToastItem`, `ToastTone`, `ToastContextValue`.
- `frontend/src/components/Toast/useToast.ts` — `useToast()` hook (throws outside provider).
- `frontend/src/components/Toast/Toast.test.tsx` + `Toast.stories.tsx` — dedupe + cap-3 tests, provider demo story.
- `frontend/src/components/EmptyState/` — `EmptyState` title/description/action.
- `frontend/src/components/ErrorState/` — `ErrorState` title/message/correlationId/onRetry.
- `frontend/src/components/ConfirmDialog/` — `ConfirmDialog` over `Modal`.
- `frontend/src/components/CommandMenu/` — `CommandMenu` filter + Enter select.
- `frontend/src/components/Breadcrumbs/` — `Breadcrumbs` with `aria-current="page"`.
- `frontend/src/components/Card/` — `Card` raised section.
- `frontend/src/components/Panel/` — `Panel` flat section.
- `frontend/src/components/product/EntityId/` — `EntityId` monospace id + copy.
- `frontend/src/components/product/RelativeTime/` — `RelativeTime` `Intl.RelativeTimeFormat` + invalid-safe.
- `frontend/src/components/product/CostDisplay/` — `CostDisplay` `Intl.NumberFormat` USD.
- `frontend/src/components/product/QuotaMeter/` — `QuotaMeter` role=meter + 80% warning threshold.
- `frontend/src/components/product/ProviderBadge/` — `ProviderBadge` over `StatusBadge` with `provider: status` label.
- `frontend/src/components/product/CorrelationId/` — `CorrelationId` debug-only mono + copy.
- `frontend/src/components/index.ts` (modified) — barrel for all 39 components + `statusMap` + toast context/hook.
- `tools/check-no-hex.mjs` (created) — R1 grep gate; scans `src`, `.storybook`, configs; allows only `tokens.css`; skips generated client.
- `.github/workflows/ci.yml` (modified) — api-contract job gains token gate, component tests, Storybook build steps.
- `.gitignore` (modified) — ignores `frontend/storybook-static/`.
- `tasks_report_B/016-design-tokens-ui-primitives.md` (created) — this report.

## Decisions Made
- **Real Storybook 8.6.18 over placeholder:** installed `storybook` + `@storybook/react-vite` + `@storybook/react` pinned at 8.6.18 (Vite 5-compatible; v10 requires newer Vite). `build-storybook` is now a genuine `storybook build` emitting `storybook-static/` (gitignored), so Task 041 visual tests inherit a working setup. Deleted the 015 placeholder script.
- **`BackendStatus` union covers generated + domain:** generated client only unions `OutputState`, `ReviewStatus` (3 values), `VoicePreviewStatus`, `ProviderHealth.status`, `circuitBreakerState`; remaining backend statuses arrive as `string`. `statusMap.ts` therefore imports the 5 generated unions by name (a new bundle member without a `statusToVariant` case fails typecheck via `assertNever` + `noImplicitReturns`) and mirrors every domain enum from `src/DubbingPlatform.Domain/Enums/*.cs` (`ProjectStatus`, `ProcessingRunStatus`, `StageStatus`, `DomainReviewStatus` with `Requeued`/`ResolvedWithEdit`, `ExportJobStatus`, `SyncStatus`, `QualityStatus`, `MediaAssetStatus`, `UploadStatus`, `ArtifactStatus`, `ContentObjectStatus`, `ConsentStatus`, `TenantUserStatus`, `VoicePreviewConsentState`). Component prop is `BackendStatus | (string & Record<never, never>)` so runtime unknowns still compile but render neutral + `console.warn` (Task 018 replaces with real telemetry). This satisfies both R2 exhaustiveness and the 016 review fix (no hard 014 task dependency — imports are type-only and the map stands alone).
- **Variant mapping (8 tokens):** success = Completed/Ready/Approved/ResolvedWithEdit/Healthy/Closed/Committed/Valid/Granted/Verified/Active/Pass/SyncAcceptable; warning = Pending/Scheduled/Queued/RetryPending/RetryRequired/PassWithWarnings/SyncAcceptableWithWarning/SyncRetryable/Degraded/Unknown/Partial; processing = Processing/Running/Generating/Cancelling/InProgress/Uploading; review = ManualReviewRequired/Open/Requeued; error = Failed/MediaRejected/Invalid/Blocked/Rejected/Unavailable/Down; cancelled = Cancelled/Aborted/Expired/Skipped/Deleted/Orphaned/Disabled/Revoked/Duplicate; info = Created/MediaReady.
- **react-refresh splits:** `statusMap.ts` holds all non-component exports for StatusBadge; `toastContext.ts` holds context/types and `useToast.ts` holds the hook while `Toast.tsx` exports only `ToastProvider` — same pattern as 015's `queryClient.ts`/`lazy.ts` splits, keeping `only-export-components` error-level clean without weakening lint.
- **No new runtime deps for components:** all primitives use React + CSS vars only (no Radix/clsx); per-component `<style>` blocks reference `var(--...)` exclusively so `check-no-hex` passes and R1 holds. Shared `dp-btn-*`/`dp-field`/`dp-label`/`dp-input` classes are re-declared per component for isolation (slight duplication accepted over a fragile global import order).
- **RTL:** shared CSS uses `margin-inline`/`padding-inline`/`inset-inline`/`inset-block`/`text-align:start` only (verified by grep); Switch thumb uses `translateX(±1rem)` flipped under `[dir="rtl"]`; Storybook `direction` global + `ar` locale auto-flips to RTL for visual coverage.

## Build/Test Results
- `npm run typecheck` → `tsc --noEmit -p tsconfig.json`, exit 0.
- `npm run lint` → `eslint . --max-warnings=0`, exit 0 (after splitting `statusMap`/`toastContext`/`useToast` for `react-refresh/only-export-components`).
- `node ../tools/check-no-hex.mjs` → `check-no-hex: no hardcoded hex outside tokens.css.`
- `npm run test -- src/components` → `Test Files 39 passed (39) / Tests 50 passed (50)`, ~22.7s (includes StatusBadge unknown-fallback, Modal/Drawer/ConfirmDialog focus+Escape, Toast dedupe + cap-3, DataGrid keyboard activate, Combobox/CommandMenu keyboard select).
- `npm run build-storybook` → `storybook build -o storybook-static`: `139 modules transformed`, `✓ built in 6.40s`, `Preview built (19s)`, `Output directory: .../frontend/storybook-static` (last 10 lines: manager built 225ms, preview built, iframe.html 17.54 kB, entry-preview-docs 302.03 kB, `✓ built in 6.40s`, telemetry notice).
- `npm run build` → drift `generated client matches the committed bundle`, `tsc` clean, `vite v5.4.11 building for production... 103 modules transformed`, 9 per-route chunks, `✓ built in 3.62s`.
- `dotnet build --nologo -v q` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test tests/DubbingPlatform.UnitTests --nologo -v q` → `Passed! - Failed: 0, Passed: 412, Skipped: 0, Total: 412`.
- Grep checks: no `margin-left|...|ml-|mr-|pl-|pr-` in `src/styles`; zero `dangerouslySetInnerHTML` in `src/components`.

## Recommendations for Next Agent (017)
- **State:** 001–016 done, all uncommitted (016 delta: modified `ci.yml`, `.gitignore`, `eslint.config.js`, `package.json`, `package-lock.json`, `tailwind.config.ts`, `tsconfig.json`, `src/components/index.ts`, `src/styles/index.css`; deleted `frontend/scripts/`; new `src/styles/tokens.css`, `src/styles/globals.css`, `.storybook/`, 39 component folders, `tools/check-no-hex.mjs`; plus lingering 014/015 deltas and pre-existing `master-prompt.md` modification). Backend pinned: `ErrorCodes.All`=65, bundle 75 paths/83 ops/57 schemas, UnitTests 412; frontend tests 56 total (6 scaffold + 50 components). `frontend/dist/`, `frontend/storybook-static/`, `frontend/.env` are gitignored — do not commit.
- **Key APIs:** `statusToVariant(status: BackendStatus): StatusVariant`, `isKnownStatus(s: string): boolean`, `warnUnknownStatus(s: string): void`, types `BackendStatus`, `StatusVariant`, `KNOWN_STATUSES` in `frontend/src/components/StatusBadge/statusMap.ts`; `StatusBadge({ status, label })` in `.../StatusBadge/StatusBadge.tsx`; `ToastProvider`, `ToastContext`, `useToast()` in `frontend/src/components/Toast/{Toast,toastContext,useToast}.ts(x)` (R5: cap 3 via `.slice(-3)`, dedupe via `some(tone+message)`); `cx()` in `.../_shared/cx.ts`; `useFocusTrap(active, ref)` in `.../_shared/useFocusTrap.ts`; `useEscape(active, fn)` in `.../_shared/useEscape.ts`; everything re-exported from `frontend/src/components/index.ts`. Tokens live in `frontend/src/styles/tokens.css` (`--color-*`, `--space-*`, `--font-size-*`, `--radius-*`, `--shadow-*`, `[data-theme="dark"]` overrides); theme mapping in `frontend/tailwind.config.ts` (no hex — gate fails otherwise).
- **Gotchas:** (1) `react-refresh/only-export-components` is error-level — keep non-component exports out of `.tsx` (follow the `statusMap`/`toastContext`/`useToast` split precedent). (2) `no-restricted-syntax` bans `import.meta` outside `src/lib/env.ts` AND `dangerouslySetInnerHTML` everywhere in `src/` — render untrusted strings as text; `Alert details` uses `<pre>{details}</pre>` as the approved pattern. (3) `check-no-hex` fails on any `#[0-9a-fA-F]{3,8}` outside `tokens.css` (generated client exempt) — use `var(--color-*)` or token-backed Tailwind utilities (`bg-brand`, `text-ink-muted`, `border-line`); run `npm run check:no-hex --prefix frontend` before committing. (4) `npm run build` runs drift `prebuild` first — after bundle edits run `node tools/generate-client.mjs`; `make` unavailable on Windows, invoke `node tools/*.mjs` directly. (5) Storybook `preview.ts` globals are `theme`/`direction`/`locale` (`ar` auto-forces RTL); add stories as CSF `Meta`/`StoryObj` under matching `title: 'Primitives/X' | 'Product/X'` so `storybook build` picks them up. (6) Tests use explicit `vitest` imports + manual `cleanup()` (no globals); jsdom from `vite.config.ts`.
- **Incomplete integration points:** 017 owns API wiring over `ApiClient` (`frontend/src/api/generated/index.js` authority — never hand-write fetch shapes) and will fill `QueryProvider` GET-only retry + error normalization + query keys; `ToastProvider` is NOT yet mounted in `App.tsx` (017/018 decides placement); provider stubs (`StoreProvider`/`ThemeProvider`/`LocaleProvider`/`TelemetryProvider`) still passthrough — 018 owns real bodies + `useAppStore` slices + `warnUnknownStatus` → real telemetry sink; `ConfirmDialog`/`Modal` focus-trap is self-contained via `useFocusTrap`, reuse it for future dialogs.
- **Test helpers:** colocated `*.test.tsx` pattern (`cleanup` + `fireEvent` + `screen.getByRole`); extend `StatusBadge.test.tsx` when adding statuses; Toast cap/dedupe tests in `Toast.test.tsx` (`Pusher`/`CapPusher` pattern); route/env tests from 015 untouched.
- **Warnings:** `.env.example` `VITE_API_BASE_URL=http://localhost:5000` is dev-only; never put secrets in `VITE_*` (bundle is public); `index.html` CSP meta is baseline — full headers land in Task 037, do not weaken; `storybook-static/` output is large — never commit (gitignored) but CI builds it on every PR.
