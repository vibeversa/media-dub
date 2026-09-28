# 039B — Frontend State-Matrix Gap Closure

## Status
COMPLETED

## Summary

Closed every frontend loading/empty/error/permission/async-state gap the 039A
gap report named, for all screens in 019–036, using MSW-free `setInnerFetchForTests`
envelope interception only. **This was a resumed run:** roughly forty
`*Matrix*` / `barrels` / `stateMatrix` spec files plus edits to `vite.config.ts`,
`docs/coverage.md` and `RelativeTime.tsx` were already present when work
resumed; this run repaired the dirty specs, closed the three remaining gaps
(`useActivity` page-size clamps, `WorkspacePage` residual handlers, the i18n
missing-key handler), raised the vitest coverage gate from the 72/64/68/72
floor to 80 across the board, and finished the validation. `node
scripts/coverage-gap.mjs` now emits **zero** `COVERAGE_GAP` lines; the full
frontend suite is 143 files / 1578 tests green at 96.54 lines / 91.92 branches /
98.45 functions / 96.54 statements, and no backend file was touched (R5).

## Files Created/Modified

### Production (4) — minimal, behaviour-preserving or defect fixes only
- `frontend/src/i18n/i18n.ts` (**modified this run**) — `saveMissing: false` → `true`
  so i18next actually invokes the registered `missingKeyHandler`; the handler
  body moved into a new exported pure `missingKeyLocale(lngs)` so the
  non-array / empty-list defensive legs are reachable and asserted. Fixes a real
  defect: missing-translation telemetry was dead code.
- `frontend/src/components/product/RelativeTime/RelativeTime.tsx`
  (**modified before resume**) — `toLocaleString` wrapped in try/catch with an
  ISO fallback so an invalid locale renders a stamp instead of throwing.
- `frontend/vite.config.ts` (**modified before resume + this run**) — added the
  permanent `src/**/*.stories.*` coverage exclusion (Storybook demos are
  dev-only and are owned by 041B visual regression); **this run** raised
  `test.coverage.thresholds` from `72/64/68/72` to `80/80/80/80` and rewrote
  the surrounding comment to record why.
- `docs/coverage.md` (**modified before resume + this run**) — stories exclusion
  rationale, the 039B intentional-exclusions section (**updated this run** to
  state that no area is excluded and that defensive guards are covered through
  real call sites rather than excluded), and the threshold table/introduction
  (**updated this run** to the new 80/80/80/80 gate and measured numbers).

### Specs created before this run resumed (unchanged unless noted)
- `frontend/src/app/__tests__/barrels.test.ts` — import assertions over every pure re-export barrel.
- `frontend/src/app/__tests__/pagesMatrix.test.tsx` — every route/page shell; **this run** removed an unused `ActivityPage` import by adding a real empty-project-id quota-banner assertion.
- `frontend/src/app/__tests__/shellMatrix.test.tsx` — `App`/providers/session/`main.tsx`/router/guards.
- `frontend/src/app/layouts/__tests__/projectLayoutMatrix.test.tsx` — ProjectLayout defensive branches (mocked).
- `frontend/src/components/__tests__/stateMatrix.test.tsx` — runtime component state matrices.
- `frontend/src/api/__tests__/{httpMatrix,infraMatrix,keysMatrix,normalizeMatrix}.test.ts` — client/keys/normalize/error branches.
- `frontend/src/stores/__tests__/storeMatrix.test.ts` — zustand store matrices.
- `frontend/src/telemetry/__tests__/telemetryMatrix.test.ts` — telemetry builders/opt-out/sink failures.
- `frontend/src/hooks/__tests__/streamMatrix.test.tsx` — SSE/poll integration matrix.
- `frontend/src/i18n/__tests__/localeMatrix.test.tsx` — **extended this run** (4 new cases: telemetry-observed missing key, locale resolver matrix, resolved-key silence).
- `frontend/src/features/**/__tests__/*Matrix*.test.*` — 33 feature deltas across
  activity, admin (3), auth, cost (2), dashboard, exports (2), notifications
  (2), processing, projects (2), projects/wizard (2), quality (2), review (2),
  settings (2), timeline (2), transcript (2), translation (2), uploads (4),
  voices (2). **Edited this run:** `activityMatrix2` (+2 cases),
  `adminMatrix2` (typed `vi.fn` mock), `reviewMatrix2` (fixed a wrong
  `ReviewContextView.text` assertion), `translationMatrix` (fixed two
  `syncToneFor({})` type errors), `voicesMatrix2` (rewrote a `prefer-const`
  violation as typed `const` bindings).

### Specs created this run
- `frontend/src/features/processing/__tests__/workspaceMatrix2.test.tsx` (19
  tests) — load-error `ErrorState` recovery + retry refetch, archive mutation
  failure with/without retry, stale-banner refresh, all five secondary-column
  navigation links, and the aggregate display fallbacks (zero/oversize bytes,
  sub-minute durations, missing progress stamps, statusless failed run,
  forbidden recovery retry, empty review/warnings/activity/media, missing
  config hash/languages, pre-run placeholder).
- Renamed `frontend/src/features/projects/wizard/__tests__/draftMatrix.test.ts`
  → `.tsx` (**corrected this run**) — the file contained JSX but had a `.ts`
  extension, so `tsc` failed to parse it.

### Removed
- `adminMatrix-run.log`, `frontend/workspaceMatrix-run.log` — scratch `npx
  vitest` logs from the interrupted run, not part of the implementation.

## Decisions Made
- **Fetch interception over MSW handlers in feature specs (R3).** The 039A MSW
  taxonomy server serves exactly 11 fixed paths; feature endpoints are not
  among them. Every added spec therefore uses the established
  `setInnerFetchForTests(mockFetch)` harness and hand-rolled taxonomy-shaped
  error envelopes (`{ error: { code, message, correlationId, details } }`).
  `src/mocks/conformance.spec.ts` still guards the 11-outcome contract; no
  network, no backend boot.
- **Root-cause the i18n gap rather than exclude it.** `i18n.ts` reported
  `functions=0.0` because `saveMissing: false` means i18next never calls
  `missingKeyHandler` — the missing-translation telemetry was dead code.
  Excluding the file or poking the callback from a test would have hidden a
  real defect, so the config was corrected (`saveMissing: true`; no backend
  connector is configured, so the handler is the only sink and there is still
  no network write) and the defensive locale legs were lifted into an exported
  pure `missingKeyLocale()` that the spec asserts directly.
- **Cover defensive guards through real call sites instead of excluding
  them.** `formatBytes`/`capitalize`/`useActivity` clamps were driven from
  their actual inputs (zero and oversize media sizes, sub-minute durations,
  `page/pageSize` of `0`/`-1`/`NaN`/`Infinity`) so a refactor that makes a
  guard reachable fails the gate instead of silently losing coverage.
- **Split rather than grow specs.** Deltas live in `*Matrix2` / `*Matrix3`
  files per feature directory; existing 015–036 specs were not rewritten.
- **`features/workspace/` does not exist.** Workspace page-level states are
  covered by `exportsMatrix2.test.tsx` (OutputsPage) and
  `processing/__tests__/workspaceMatrix*.test.tsx`.
- **Raised the vitest gate to 80.** 039A explicitly deferred this until the
  gap output was empty and documented the condition; it is now met, and the
  measured 96.5/91.9/98.5/96.5 leaves ample run-to-run headroom.
- **Only colour-independent signals are asserted** — `role`, `aria-*`, label
  text, and `data-testid` — never CSS class or colour (requirement 3 / 041C).
- **Forbidden-state specs assert no leakage** — e.g. no retry control and no
  run row when `processing.retry` is absent, no aggregate rows rendered before
  a load-error retry, no media table beside the media placeholder.
- **Left `master-prompt.md` uncommitted.** It was already dirty before this
  task started and is not part of 039B.

## Build/Test Results

- `npm run test --prefix frontend` →
  `Test Files  143 passed (143)` / `Tests  1578 passed (1578)` in 76.83s (exit 0).
- `npm run test --prefix frontend -- --coverage` →
  `Test Files  143 passed (143)` / `Tests  1578 passed (1578)` in 95.04s;
  `All files | 96.54 | 91.92 | 98.45 | 96.54`; the new
  `test.coverage.thresholds` 80/80/80/80 pass (exit 0). Last 10 lines:
  ```
    analytics.ts     |   99.06 |    96.77 |     100 |   99.06 | 188
    correlation.ts   |     100 |    83.33 |     100 |     100 | 18,23,36
    events.ts        |   97.26 |    94.28 |     100 |   97.26 | 63,86,120,130-131
    index.ts         |     100 |     100 |     100 |     100 |
    scrub.ts         |   98.41 |    94.59 |     100 |   98.41 | 110-111
    telemetry.ts     |   95.12 |    86.11 |     100 |   95.12 | ...24-125,217,231
   ...tryContext.ts |     100 |     100 |     100 |     100 |
   src/types         |     100 |     100 |     100 |     100 |
    index.ts         |     100 |     100 |     100 |     100 |
  -------------------|---------|----------|---------|---------|-------------------
  ```
- `node scripts/coverage-gap.mjs` → **no output at all**, `GAP_EXIT=0` — zero
  frontend-matrix gaps, R4 satisfied.
- `node scripts/presence-gate.mjs` → `PRESENCE_OK:26 areas with specs`, exit 0.
- `npm run typecheck --prefix frontend` → exit 0, `tsc --noEmit -p tsconfig.json` clean.
- `npm run lint --prefix frontend` → exit 0, `eslint . --max-warnings=0` clean.
- No backend file changed (`git status` shows no `tests/` or `*.cs` entries) — R5.
- Note on stderr: the run prints `Error: Uncaught …` and
  `Not implemented: navigation` lines from the specs that deliberately throw
  inside error boundaries and route guards. That is expected stderr noise from
  passing tests, not failures.

## Recommendations for Next Agent (039C)

- **State:** 039B done. `node scripts/coverage-gap.mjs` is **empty** — do not
  expect to iterate on the frontend; 039C consumes the same script for
  `tests/DubbingPlatform.UnitTests`. Frontend: 143 files / 1578 tests green,
  96.54/91.92/98.45/96.54. Vitest `thresholds` are now **80/80/80/80** — if a
  new feature lands below that, add a spec; never lower the threshold
  (`docs/coverage.md` "Quarantine and bypass policy").
- **Coverage artifacts:** `frontend/coverage/` is gitignored and **overwritten
  by every run**. A focused `--coverage.include` run clobbers
  `coverage-summary.json`; always re-run the full `--coverage` before
  `coverage-gap.mjs`. Coverage reports contain paths + counts only — never add
  source excerpts, tokens, URLs, media, or transcript content.
- **Coverage config:** canonical file is `frontend/vite.config.ts` (no
  `vitest.config.ts`). `exclude` list is policy: `src/api/generated/**`,
  `src/**/*.{test,spec,spec}.{ts,tsx}`, **`src/**/*.stories.*` (added by
  039B — Storybook demos are dev-only; 041B owns them visually)**, `src/testSetup.ts`,
  `**/*.d.ts`, `playwright.config.ts`, `e2e/**`, `dist/**`,
  `storybook-static/**`, `.storybook/**`. `include` stays `src/**/*.{ts,tsx}`.
- **Spec harness to reuse (do not duplicate):**
  `setInnerFetchForTests(mockFetch)` / `restoreInnerFetchForTests()` /
  `clearTokenProvider()` / `setTokenProvider(() => 'test-token')` from
  `frontend/src/api/client/index.js`; envelope shape
  `{ error: { code, message, correlationId, details: {} } }` with a `corr-*`
  id; `queryClient` from `frontend/src/app/providers/queryClient.ts`;
  `useAppStore.getState().setSession('authenticated', [...])`,
  `useAuthStore.setState({ status: 'authenticated' })`,
  `useAuthStore.getState().resetForTests()`,
  `resetRestoreStartedForTests()` (from `features/auth/useSession.ts`),
  `useWorkspaceStore.getState().resetForTests()`.
- **Gotchas that cost time this run:**
  (1) A spec file containing JSX **must** be `.tsx` — `draftMatrix.test.ts`
  failed `tsc` with `TS1005 '>' expected` until renamed.
  (2) `vi.fn(async () => …)` typed with **no** parameters makes
  `mock.calls[0]?.[0]` a `TS2493`; use
  `vi.fn<(reason: string) => Promise<…>>(async () => …)`.
  (3) `prefer-const` fires on `let x: T | undefined;` + a single assignment —
  use `const` at the assignment site and a named `type` alias for the type.
  (4) Parsed view shapes use `undefined`, never `null` (`assignedVoice: undefined`,
  `syncStatus: undefined`) — `{ syncStatus: undefined }`, not `{}`.
  (5) jsdom: `offsetParent` is always null, `navigator.clipboard` is absent,
      stub `window.location.reload`, native `.click()` may not fire React
      handlers (use `fireEvent`), and every router `Link` target needs a route
      in the `createMemoryRouter` table or navigation throws.
  (6) `AuthProvider` restore wipes token-less sessions — seed
      `useAuthStore` access/refresh tokens when rendering `App` authenticated.
  (7) A `missing` telemetry event is only buffered when
      `VITE_TELEMETRY_ENABLED=true` **and** `resetEnvCache()` was called after
      `vi.stubEnv`, **and** `setTelemetryOptOut(false)`.
  (8) i18next only calls `missingKeyHandler` when `saveMissing: true`; a
      `t()` on a missing key then also requires a `defaultValue` to reach the
      `usedDefault` leg. `missingKeyLocale()` in `frontend/src/i18n/i18n.ts`
      is the exported seam for the defensive array/empty cases.
- **Incomplete integration points:** the full 046 harness (Playwright tags,
  `Synthetic*.cs` factories, scrubber tests, quarantine doc) is still unowned —
  do not build it inside a gap-closure task. 039C owns the backend per-area gap
  list and sets the backend numeric gate (mirror the frontend
  floor-then-target approach). 040A/B own cross-layer seams; 041A–D own
  journeys/visual/a11y/perf (041B consumes the Storybook stories 039B excluded
  from Vitest coverage).
- **Fixtures stay synthetic:** `corr-*` correlation ids, `prj_1`/`run_1`/
  `seg_001` ids, no real tenants, tokens, signed URLs, media, or transcript
  text — in fixtures, snapshots, or telemetry payloads.
- **Windows/PowerShell:** no `tail`/`head`/`grep`; use `Get-Content`,
  `Select-String`, `node -e`. Redirected output may be UTF-16 — read the
  harness `.out` file with `Get-Content` rather than piping `npm` output into
  a file.
- **Unrelated dirty file:** `master-prompt.md` is still modified in the working
  tree from before 039B; it is intentionally not committed.
