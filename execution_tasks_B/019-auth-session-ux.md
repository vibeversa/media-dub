# Task 019 — Authentication and Session UX

## Goal
Implement login/logout/restore/expiry with pre-shell `/me` resolution and in-memory tokens.

## Context
Backend auth endpoints (`POST /api/v1/auth/login|refresh|logout`, `GET /me`) come from Task 006; shell slots (`RequireAuth`, guards) from Task 018. This is the first feature mounted in the shell and sets the session pattern every later task relies on.

## Starting State
Task 006 (auth/me endpoints) and Task 018 (shell, `RequireAuth` slot, i18n, telemetry) done. No `features/auth`. Depends on Tasks 006, 018.

## Scope
Included: login/logout/restore/expiry pages + store + hooks, silent refresh, failed-refresh → login preserving destination, 401/403 states, in-memory access token, token-provider registration for Task 017 client.
Excluded: preferences screens (Task 035), admin gating internals (Task 036), backend auth changes.

## Instructions
1. Create `frontend/src/features/auth/`: `api.ts` (login/refresh/logout/me via Task 017 client), `authStore.ts` (zustand: `{accessToken` — in-memory only, `user, status: unknown | authenticated | expired | error}`), `useSession.ts`, `RequireAuth.tsx` wiring, pages `LoginPage.tsx`/`LoggedOutPage.tsx`, `SessionExpiredDialog.tsx`, `ForbiddenPage.tsx`.
2. Pre-shell `/me` resolution: `useSession` restores on app boot (`authStore.restore()`: refresh → me); `RequireAuth` blocks shell until `status !== 'unknown'` (skeleton meanwhile); no feature query fires before auth resolves (queries use `enabled: isAuthenticated`).
3. Silent refresh: schedule at 80% token lifetime; concurrent requests share a single refresh promise (no refresh storms); failed refresh → `status: 'expired'` → redirect `/login?next=<destination>`; post-login navigates back to destination.
4. 401/403 states: 401 on an active session → single re-resolve, then login flow; 403 → `ForbiddenPage` with request-access hint (never retry-loop); handle the `auth:expired` event emitted by the Task 017 client globally.
5. Logout: `POST /logout` + clear in-memory token + `queryClient.clear()` + reset stores, then navigate; back-button after logout never reveals cached data (cache cleared before nav).
6. Register the in-memory token provider + `auth:expired` handler with the Task 017 client at app boot (`frontend/src/app/providers/` wiring).

## Requirements
- R1: Access token in memory only (test spies `localStorage`/`sessionStorage` for token writes).
- R2: Destination preserved across expiry (`?next=`) and restored post-login.
- R3: Single in-flight refresh shared by concurrent callers.
- R4: Query cache + stores cleared on logout before navigation.
- R5: Shell never renders for unauthenticated users (pre-shell gate blocks).

## Edge Cases and Error Handling
- Refresh while offline → one retry on reconnect, else `expired`.
- Multi-tab: `storage`-event `auth:logout` broadcast logs out other tabs.
- Expired deep link → login → original destination restored.
- Login submit double-click → single request (button disabled while pending).

## Security and Safety Requirements
- No token in logs/telemetry/URLs; `httpOnly` refresh cookie never touched by JS.
- Login errors generic (no user-enumeration); session expiry audited backend-side (Task 006).
- 403 page reveals nothing about resource existence beyond the denial.

## Testing
- Create `frontend/src/features/auth/__tests__/`: store transitions, refresh coalescing, destination preserve, storage-spy no-persist assertion, 401/403 mapping.
- Playwright `@auth`: login happy path, expiry → login → return to destination, logout clears data, forbidden page.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/auth`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/auth
npx playwright test --grep="@auth"
```

## Completion Criteria
- Login/logout/restore/expiry work with destination preservation; token never persisted; single-refresh coalescing verified; auth tests + `@auth` E2E pass.

## Traceability
- Plan B §12.1, §13.3. Depends on Tasks 006, 018.
