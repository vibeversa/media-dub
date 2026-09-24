# 019 — Authentication and Session UX

## Status
COMPLETED

## Summary
Implemented login/logout/restore/expiry on top of the Task 018 shell: new `features/auth` module (`api.ts` over the Task 017 transport, `authStore.ts` with in-memory-only tokens + `unknown|authenticated|expired|error|anonymous` status, `useSession.ts` with the pre-shell boot guard and `enabled: isAuthenticated` query gate, canonical `RequireAuth` with `/login?next=` preservation, real `LoginPage`/`LoggedOutPage`, `SessionExpiredDialog`, pure `ForbiddenPage`) plus `AuthProvider` at app boot (token-provider registration, global `auth:expired` handling, cross-tab logout broadcast, restore skipped under E2E seed). Silent refresh fires at 80% lifetime with single-flight coalescing; logout revokes best-effort then clears `queryClient` + stores before navigation. Vitest 30/30 on the task scope (151 full frontend), Playwright `@auth` 4/4, `@shell` 3/3 still green, backend 412/412, lint/build/drift/no-hex clean.

## Files Created/Modified
- `frontend/src/features/auth/api.ts` (created) — `login`/`refreshSession` via `anonymousClient`, `fetchMe` via `apiClient`, best-effort `logout`; generated types only through `src/api/client`.
- `frontend/src/features/auth/authStore.ts` (created) — zustand session: in-memory tokens, shared login/refresh/restore promises (R3), 80% silent timer, offline one-retry-then-expired, `handleUnauthorized` single re-resolve, logout ordering (R4), `LOGOUT_BROADCAST_KEY`, `resetForTests`.
- `frontend/src/features/auth/destination.ts` (created) — `isSafeNext`/`getNextPath`/`buildLoginPath` (R2, open-redirect-safe).
- `frontend/src/features/auth/useSession.ts` (created) — `useSession`, `useIsAuthenticated` (query `enabled` gate), `ensureRestoreStarted` boot guard.
- `frontend/src/features/auth/RequireAuth.tsx` (created) — canonical guard: unknown/loading skeleton (R5), else `/login?next=` redirect, outlet when authenticated.
- `frontend/src/features/auth/LoginPage.tsx` (created) — tenant/email/password form, double-submit guard, generic error, expiry dialog, post-login `next` restore.
- `frontend/src/features/auth/LoggedOutPage.tsx` (created) — post-logout confirmation outside the gate.
- `frontend/src/features/auth/SessionExpiredDialog.tsx` (created) — expiry notice with preserved-destination re-login link.
- `frontend/src/features/auth/ForbiddenPage.tsx` (created) — pure 403 with request-access hint, zero fetching.
- `frontend/src/features/auth/index.ts` (created) — barrel for non-components only (react-refresh rule; components imported by path).
- `frontend/src/features/auth/__tests__/destination.test.ts` (created) — 4 tests: safe/unsafe destinations, fallback, encoding.
- `frontend/src/features/auth/__tests__/authStore.test.ts` (created) — 15 tests: transitions, failed-login code, anonymous restore, USER_DISABLED error, expiry + no-loop, coalescing (refresh + login), 80% timer (pure + fake-timers), logout ordering/broadcast, storage-spy no-persist, offline retry.
- `frontend/src/features/auth/__tests__/sessionFlow.test.tsx` (created) — 11 tests: login→destination, double-click single request, generic 401 error, expiry dialog, guard skeleton/`?next=`/expired-deep-link/authenticated, 401/403 normalization, forbidden hint with zero requests.
- `frontend/src/app/providers/AuthProvider.tsx` (created) — boot wiring: token provider, `auth:expired` listener (active sessions only), `storage` logout listener, restore-once (skipped under E2E seed).
- `frontend/src/app/providers/index.ts` (modified) — exports `AuthProvider`.
- `frontend/src/app/App.tsx` (modified) — mounts `AuthProvider` inside `StoreProvider`.
- `frontend/src/app/guards/RequireAuth.tsx` (modified) — delegates to the canonical Task 019 guard.
- `frontend/src/app/guards/RequireAdmin.tsx` (modified) — also blocks on `unknown` (019 pre-shell state).
- `frontend/src/app/pages/LoginPage.tsx` (modified) — thin wrapper over the feature login (keeps lazy chunk).
- `frontend/src/app/pages/LoggedOutPage.tsx` (created) — wrapper over the feature page.
- `frontend/src/app/pages/ForbiddenPage.tsx` (modified) — adds `common:forbidden.requestAccess` hint (keeps `useTranslation` for the 018 R2 scan).
- `frontend/src/app/pages/lazy.ts` (modified) — adds `LoggedOutPage` chunk.
- `frontend/src/app/router.tsx` (modified) — adds `/logged-out` under `AuthLayout` + `routePaths`.
- `frontend/src/app/layouts/AppShell.tsx` (modified) — user menu always renders sign-out (default: `await logout()` then `/logged-out`); `data-testid="user-menu-signout"`.
- `frontend/src/stores/index.ts` (modified) — `SessionStatus` gains `unknown|expired|error` (019 states; old values kept).
- `frontend/src/i18n/locales/en/auth.json` (modified) — form labels, generic error, logged-out/expired copies.
- `frontend/src/i18n/locales/en/common.json` (modified) — `forbidden.requestAccess` hint.
- `frontend/e2e/auth.spec.ts` (created) — 4 `@auth` Playwright tests with intercepted `/api/v1` + CORS/preflight handling.
- `tasks_report_B/019-auth-session-ux.md` (created) — this report.

## Decisions Made
- **Union adds `anonymous`:** spec lists `unknown|authenticated|expired|error`; clean logout is neither expiry nor failure, so `anonymous` was added (synced 1:1 into the extended `useAppStore.SessionStatus`). Old `loading` kept for 018 compat.
- **Reload ends the session (R1 consequence):** tokens are memory-only and the bundle has no cookie flow, so boot-time `restore()` with no tokens resolves `anonymous` without network. Logged-in 401 → `expired`; unexpected failures → `error`; tokens issued but identity 403 (disabled) → `error` via new `doLogin` branch.
- **E2E seed skips restore:** `AuthProvider` bypasses `ensureRestoreStarted()` when `readE2eSessionSeed()` is present, preserving hermetic `@shell` tests; `@auth` tests use real login against intercepted API.
- **`?next=` reads raw `location.search`:** `useSearchParams().get('next')` returns the decoded value, which `getNextPath` then mis-parsed to `/dashboard` — fixed by parsing the raw search string.
- **Offline gives up to `expired`:** per spec edge case, timeout or failed post-reconnect retry marks `expired` (not `error`).
- **`auth:expired` acts on active sessions only:** login-form 401s stay local/generic; concurrent events collapse through the shared refresh promise (no retry-loop; verified by test).
- **Barrel excludes components:** `features/auth/index.ts` omits `.tsx` components to satisfy `react-refresh/only-export-components`; app wrappers import components by file path.
- **Forbidden E2E uses SPA navigation:** `page.goto('/admin')` reloads and correctly drops the session, so the test pushes state + dispatches `popstate` instead.

## Build/Test Results
- `npm run test -- src/features/auth` → `Test Files 3 passed (3) / Tests 30 passed (30)` (destination 4, authStore 15, sessionFlow 11).
- `npx playwright test --grep="@auth"` → `4 passed (8.0s)` (login, expiry→return, logout/back-button, forbidden).
- `npm test` (full frontend) → `Test Files 50 passed (50) / Tests 151 passed (151)`.
- `npx playwright test --grep="@shell"` → `3 passed (6.9s)` (no 018 regression).
- `npm run lint` → `eslint . --max-warnings=0`, exit 0.
- `npm run build` → drift `generated client matches the committed bundle`, `184 modules transformed`, `✓ built in 3.95s` (incl. new `LoggedOutPage` chunk).
- `npm run check:no-hex` → `no hardcoded hex outside tokens.css.`
- `dotnet build --nologo -v q` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test tests/DubbingPlatform.UnitTests --no-build` → `Passed! - Failed: 0, Passed: 412, Skipped: 0, Total: 412`.

## Recommendations for Next Agent (020)
- **State:** 001–019 done, all uncommitted (019 delta above plus prior 014–018 deltas and a pre-existing `master-prompt.md` edit). Pinned: `ErrorCodes.All`=65, bundle 75/83/57, UnitTests 412; frontend 151 vitest + 7 Playwright (3 `@shell` + 4 `@auth`). Gitignored — do not commit: `frontend/dist/`, `storybook-static/`, `.env`, `test-results/`, `playwright-report/`, `coverage/`.
- **Key APIs for 020+:** `useAuthStore` (`status`, `login`, `logout`, `logoutLocal`, `restore`, `refreshNow`, `handleUnauthorized`, `resetForTests`) in `frontend/src/features/auth/authStore.ts`; `useSession()`/`useIsAuthenticated()`/`isAuthenticatedStatus()` in `frontend/src/features/auth/useSession.ts` (gate every feature query with `enabled: useIsAuthenticated()`); `getNextPath`/`buildLoginPath`/`isSafeNext` in `destination.ts`; `setTokenProvider`/`clearTokenProvider`/`AUTH_EXPIRED_EVENT`/`setInnerFetchForTests` in `src/api/client`; `queryClient` in `src/app/providers/queryClient.ts`; `E2E_SESSION_KEY` in `src/app/session/e2eSeed.ts`.
- **Gotchas:** (1) `no-restricted-imports` bans `api/generated` deep imports — use `src/api/client` or `src/api/hooks` barrels. (2) `react-refresh/only-export-components` is error-level — keep non-components out of `.tsx` and out of component barrels (see `features/auth/index.ts`). (3) `import.meta` banned outside `src/lib/env.ts`. (4) Router/guard tests set `useAppStore` session directly + wrap in `LocaleProvider`; `src/testSetup.ts` `Request`/AbortSignal patch stays untouched. (5) `Response` single-use in fetch mocks (fresh `jsonResponse` per call); `toThrowError(string)` matches `message`. (6) `npm run build` runs drift `prebuild` first; `make` missing on Windows — `node tools/*.mjs`. (7) PowerShell has no `&&`/`tail` — run npm scripts separately. (8) `LoginPage` must read raw `location.search` for `?next=` (decoded values mis-parse). (9) `Alert` renders an inline `<style>` tag — assert `toContain` on alert text, not `toBe`. (10) `navigator.onLine` needs `Object.defineProperty(..., {configurable: true})` to stub; dispatch `online` only after a tick so the waiter is attached. (11) Fake-timer refresh test: `advanceTimersByTimeAsync` + restore `useRealTimers` in `finally`.
- **Incomplete integration points:** bell `unreadCount` wiring (034); tab outlets still render `ProjectDetailsPage` placeholders for all 9 subpaths (020+ fill them with live `workspaceState`); tenant timezone writer (035 → `setTenantTimezone`); preferences screens (035); admin gating internals (036); feature pages still raw strings (convert to i18n when built); `es` Storybook locale falls back to `en`; full-page reload intentionally drops the session (memory-only R1) — do not "fix" with storage persistence.
- **Test helpers:** colocated `__tests__`; `resetForTests()` on both stores + `resetRestoreStartedForTests()` + `setInnerFetchForTests(mock)` in `beforeEach`; Playwright API mocks via `page.route('**/api/v1/**')` with CORS headers + OPTIONS handler (see `e2e/auth.spec.ts` `mockAuthApi`); SPA-only navigation via `pushState` + `PopStateEvent`; sign-out button is `user-menu-signout`; run with `npx playwright test --grep="@auth"` (chromium headless-shell installed).
- **Warnings:** never put secrets in `VITE_*`; never persist/log/emit tokens, credentials, URL queries, media, transcript/translation text, or PII (telemetry `assertAllowlisted` throws on 23 forbidden keys); login errors stay generic (one message for all rejections); error/i18n strings render as text only; `index.html` CSP stays baseline until Task 037.
