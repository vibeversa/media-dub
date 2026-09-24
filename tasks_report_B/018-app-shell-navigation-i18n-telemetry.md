# 018 — App Shell, Navigation, i18n, Telemetry Baseline

## Status
COMPLETED

## Summary
Implemented the authenticated app shell all features mount into: `AppShell` (product nav from a single IA registry, notification bell with unread-badge slot, user menu slot, locale/theme switchers, sidebar/main, `VITE_APP_VERSION` footer) plus `ProjectLayout` with nine workspace-derived tabs; `RequireAuth`/`RequireAdmin` route guards with `/403` target and fail-closed Admin (nav item and route both gated, never CSS-only); i18n with namespaced `en` JSON bundles, `ar`/`ru` plural/RTL coverage, `en` fallback, `document.dir` switching, and locale/timezone-aware formatters (unknown timezone → UTC); and an allowlisted telemetry harness (path-only routes, opaque correlation IDs, kill-switch + persisted opt-out, silent drop on sink failure). Vitest 38/38 on the task scope (121 full frontend), Playwright `@shell` 3/3, backend 412/412, lint/build/drift/no-hex/Storybook all green.

## Files Created/Modified
- `frontend/src/app/layouts/AppShell.tsx` (created) — header/nav/bell/user-menu/switchers, sidebar/main, version footer; all strings via i18n; `unreadCount` (034) + `onSignOut` (019) slots.
- `frontend/src/app/layouts/ProjectLayout.tsx` (created) — project header + adaptive tabs + outlet from `getProjectTabs`; disabled tabs render as text with reason tooltips.
- `frontend/src/app/layouts/index.ts` (modified) — exports AppShell/AuthLayout/ProjectLayout; `RootLayout.tsx` deleted (superseded).
- `frontend/src/app/navigation/topNav.ts` (created) — `TopNavItem`, `getTopNavItems(permissions)` (Admin only with admin permission).
- `frontend/src/app/navigation/projectTabs.ts` (created) — 9-tab model, `getProjectTabs(state)` (translation needs transcript, exports needs output + ready badge, quality badges open reviews).
- `frontend/src/app/navigation/index.ts` (created) — barrel.
- `frontend/src/app/session/permissions.ts` (created) — `ADMIN_PERMISSION_ALIASES` (`admin.manage`, `diagnostics.view`, `admin:read`), `hasAdminPermission`, `isAdminPath`.
- `frontend/src/app/session/e2eSeed.ts` (created) — `E2E_SESSION_KEY` + `readE2eSessionSeed()` (malformed → ignored, fail-closed).
- `frontend/src/app/session/index.ts` (created) — barrel.
- `frontend/src/app/guards/RequireAuth.tsx` (created) — loading skeleton, anonymous → `/login`, else outlet.
- `frontend/src/app/guards/RequireAdmin.tsx` (created) — loading skeleton, non-admin → `/403`, else outlet.
- `frontend/src/app/guards/SessionSkeleton.tsx` (created) — i18n loading skeleton.
- `frontend/src/app/guards/index.ts` (created) — barrel.
- `frontend/src/app/pages/ForbiddenPage.tsx` (created) — i18n 403 page with dashboard link.
- `frontend/src/app/pages/lazy.ts` (modified) — adds `ForbiddenPage` lazy chunk.
- `frontend/src/app/router.tsx` (modified) — `RequireAuth` → `AppShell` → (dashboard/projects/project-tabs/review/notifications/settings/`RequireAdmin`→admin/`403`); `/403` added to `routePaths`.
- `frontend/src/app/App.tsx` (modified) — mounts `ToastProvider` (017 leftover) inside `TelemetryProvider`.
- `frontend/src/app/providers/ThemeProvider.tsx` (modified) — real body: syncs `data-theme`.
- `frontend/src/app/providers/LocaleProvider.tsx` (modified) — real body: `I18nextProvider` + `document.dir`/`lang` sync.
- `frontend/src/app/providers/TelemetryProvider.tsx` (modified) — real body: kill-switch + opt-out context.
- `frontend/src/app/providers/StoreProvider.tsx` (modified) — real body: theme/dir/i18n rehydration + E2E seed.
- `frontend/src/stores/index.ts` (modified) — session (`loading`/`authenticated`/`anonymous` + permissions), theme, locale, `tenantTimezone`, telemetry opt-out (persisted), `resetForTests`.
- `frontend/src/i18n/i18n.ts` (created) — i18next init (sync, `en` fallback, ICU plurals, missing-key telemetry hook).
- `frontend/src/i18n/format.ts` (created) — `isRtlLocale`/`applyDirection`, `resolveTimeZone` (unknown → UTC), `formatDate`/`formatNumber` (never throw), `getPluralCategory`.
- `frontend/src/i18n/useLocale.ts` (created) — `useLocale()` (store locale, tenant tz, setters syncing i18n+DOM+store).
- `frontend/src/i18n/locales/en/{common,nav,auth,dashboard,projects,errors}.json` (created) — `en` baseline namespaces.
- `frontend/src/i18n/locales/ar/{common,nav}.json`, `ru/common.json` (created) — Arabic 6-way plurals + RTL strings, Russian 4-way plurals.
- `frontend/src/i18n/index.ts` (modified) — real barrel.
- `frontend/src/telemetry/telemetry.ts` (created) — allowlisted events, `assertAllowlisted` (throws), kill-switch/opt-out, ring buffer + sink (silent drop, no retry), `trackPageView/trackRouteChange/trackApiFailure/trackUnknownStatus/trackMissingTranslation`.
- `frontend/src/telemetry/correlation.ts` (created) — `sanitizeRoute` (path-only), `resolveTelemetryCorrelation` (error → last request → fresh uuid).
- `frontend/src/telemetry/telemetryContext.ts` (created) — `TelemetryContext` + `useTelemetry()`.
- `frontend/src/telemetry/RouteTracker.tsx` (created) — page-view/route-change tracking, mounted in `AppShell`.
- `frontend/src/telemetry/index.ts` (modified) — real barrel.
- `frontend/src/components/StatusBadge/statusMap.ts` (modified) — `warnUnknownStatus` now also emits allowlisted telemetry.
- `frontend/src/testSetup.ts` (created) — jsdom `Request`/AbortSignal quirk patch for router `<Navigate>` (test-only).
- `frontend/vite.config.ts` (modified) — registers `setupFiles`.
- `frontend/tsconfig.json` (modified) — `resolveJsonModule: true` for locale JSON imports.
- `frontend/src/app/__tests__/router.test.tsx` (modified) — session+i18n setup for guarded routes; adds skeleton/anonymous/admin-redirect cases.
- `frontend/src/app/layouts/__tests__/shell.test.tsx` (created) — 13 tests: nav render, R1 admin absence/aliases, footer version, bell badge, guard redirects, R5 tab states, R2 literal scan.
- `frontend/src/i18n/__tests__/i18n.test.ts` (created) — 8 tests: en/ar/ru plurals, RTL switch, `en` fallback, R4 formatting/timezone.
- `frontend/src/telemetry/__tests__/telemetry.test.ts` (created) — 10 tests: R3 allowlist/throw, query-stripping, kill-switch, opt-out, sink-failure drop, correlation.
- `frontend/playwright.config.ts` + `frontend/e2e/shell.spec.ts` (created) — 3 `@shell` smokes (nav render, admin hidden, admin reachable) via localStorage session seed.
- `frontend/package.json` (modified) — adds `i18next`, `react-i18next`, `@playwright/test`, `dev` script.
- `frontend/package-lock.json` (modified) — new dependency closures.
- `.gitignore` (modified) — ignores `frontend/test-results/`, `frontend/playwright-report/`.
- `tasks_report_B/018-app-shell-navigation-i18n-telemetry.md` (created) — this report.

## Decisions Made
- **Admin alias accepts 3 strings:** spec says `admin:read`, backend catalog (`Permissions.All`, frozen 12) issues `admin.manage` (+ `diagnostics.view`). `hasAdminPermission` accepts any of the three so shell matches both spec and server; endpoints stay authoritative. R1 tests cover all three aliases.
- **Guards redirect with `<Navigate>` (kept):** jsdom+undici rejects router-internal `new Request(url, {signal})` (foreign AbortSignal) making every redirect test fail with unhandled rejections. Fixed in test harness only (`src/testSetup.ts` drops the signal when the native constructor rejects it); production code untouched.
- **E2E session via localStorage seed:** Task 019 (real login) is out of scope, so `@shell` seeds `{status, permissions}` through `dubbing.e2e.session` read once by `StoreProvider`; absent/malformed in production is a no-op (fail-closed to loading). No `import.meta` backdoor, no lint exemption.
- **Duplicate nav testids:** header + sidebar render the same items, so testids are prefixed (`nav-*` header, `sidenav-*` sidebar); R1 asserts both admin variants absent.
- **No MSW/new fetch deps, no `i18next-icu` plugin:** ICU plural categories come from i18next's built-in `Intl.PluralRules` suffixes (`_zero/_one/_two/_few/_many/_other`), verified by ar/ru tests. `ru` is test/formatting-only (switcher offers en/ar); `es` Storybook global falls back to `en` (supportedLngs = en/ar/ru).
- **`RootLayout.tsx` deleted:** fully superseded by `AppShell`; barrel updated. No test referenced it.
- **R2 scan semantics:** bans the 13 shell/nav literals as JSX text (`>literal<`) in layouts/guards/ForbiddenPage and requires `useTranslation` where text renders (guards rendering no text exempt). Pages stay raw placeholders (feature tasks own them).
- **`useAppMutation` untouched:** hook-level test still open (noted in 017); 018 does not consume mutations.

## Build/Test Results
- `npm run test -- src/app src/i18n src/telemetry` → `Test Files 4 passed (4) / Tests 38 passed (38)` (shell 13, router 7, i18n 8, telemetry 10).
- `npm test` (full frontend) → `Test Files 47 passed (47) / Tests 121 passed (121)` (87 pre-existing + 34 new… 31 task tests + 3 added router cases).
- `npx playwright test --grep="@shell"` → `3 passed (10.2s)` (nav render, admin hidden + /403, admin reachable).
- `npm run lint` → `eslint . --max-warnings=0`, exit 0.
- `npm run build` → drift `generated client matches the committed bundle`, `tsc` clean, `168 modules transformed`, `✓ built in 3.06s` (incl. new `ForbiddenPage` chunk).
- `npm run check:no-hex` → `no hardcoded hex outside tokens.css.`
- `npm run build-storybook` → `153 modules transformed`, `Preview built (8.6s)`.
- `dotnet build --nologo -v q` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test tests/DubbingPlatform.UnitTests --no-build` → `Passed! - Failed: 0, Passed: 412, Skipped: 0, Total: 412`.

## Recommendations for Next Agent (019)
- **State:** 001–018 done, all uncommitted (018 delta listed above plus prior 014/015/016/017 deltas and a pre-existing `master-prompt.md` edit). Backend pinned: `ErrorCodes.All`=65, bundle 75/83/57, UnitTests 412; frontend 121 tests + 3 Playwright `@shell`. Gitignored — do not commit: `frontend/dist/`, `storybook-static/`, `.env`, `test-results/`, `playwright-report/`, `coverage/`.
- **Key APIs for 019:** `useAppStore` (`sessionStatus`, `permissions`, `setSession(status, perms)`, `setTenantTimezone`, `setTelemetryOptOut`) in `frontend/src/stores/index.ts`; `hasAdminPermission`/`ADMIN_PERMISSION_ALIASES`/`isAdminPath` in `frontend/src/app/session/permissions.ts`; `AUTH_EXPIRED_EVENT`/`emitAuthExpired`/`setTokenProvider`/`clearTokenProvider`/`anonymousClient` in `frontend/src/api/client/httpClient.ts`; `AppShell` slots `onSignOut` + `unreadCount`; E2E seed `E2E_SESSION_KEY`/`readE2eSessionSeed` in `frontend/src/app/session/e2eSeed.ts` (reuse for login specs).
- **Gotchas:** (1) `no-restricted-imports` still bans `api/generated` deep imports — use `src/api/client` or `src/api/hooks` barrels. (2) `react-refresh/only-export-components` is error-level — non-component exports stay out of `.tsx` (see `e2eSeed.ts` split). (3) `import.meta` banned outside `src/lib/env.ts`; tests use `process.cwd()`+`node:fs` (`"node"` in tsconfig types). (4) Router tests render the full guarded tree — always `setSession` + wrap in `LocaleProvider` (see `router.test.tsx` setup). (5) `src/testSetup.ts` patches `Request` only when the native constructor rejects jsdom signals; leave it alone. (6) `npm run build` runs drift `prebuild` first; `make` missing on Windows — `node tools/*.mjs` directly. (7) `Response` single-use in fetch mocks; `toThrowError(string)` matches `message` not `code` (017 patterns).
- **Incomplete integration points:** login/session `/me` resolution (019 owns: call `setSession`, register token provider via `setTokenProvider`, wire `AppShell onSignOut` → logout + `clearTokenProvider`); consume `auth:expired` event for silent refresh (018 only emits/telemetry-tracks it); bell `unreadCount` data wiring (034); tab outlets render `ProjectDetailsPage` placeholders for all 9 subpaths (020+ fill them, pass live `workspaceState` into `ProjectLayout`); tenant timezone writer (035 → `setTenantTimezone`); feature pages still raw strings (convert to i18n when built); Storybook `es` locale falls back to `en` (no `es` bundle — add one if stories need it).
- **Test helpers:** colocated `__tests__` pattern; store `resetForTests()` + `setSession` in `beforeEach`; `renderAt` (memory router + `LocaleProvider`) in `shell.test.tsx`; env stubbing via `vi.stubEnv` + `resetEnvCache()`; telemetry `clearBufferedEvents`/`setTelemetrySink`; Playwright seed via `page.addInitScript` + `E2E_SESSION_KEY`, run with `npx playwright test --grep="@shell"` (chromium headless-shell already installed).
- **Warnings:** never put secrets in `VITE_*`; never log/emit tokens, URL queries, media, transcript/translation text, or PII (telemetry `assertAllowlisted` throws on 23 forbidden keys — emails omitted, not hashed); error/i18n strings render as text only; `index.html` CSP stays baseline until Task 037.
