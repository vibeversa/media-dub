# Task 018 — App Shell, Navigation, i18n, Telemetry Baseline

## Goal
Implement the app shell, IA navigation, locale readiness, and safe telemetry harness all features mount into.

## Context
Tasks 019+ render inside this shell; navigation IA, i18n conventions, and telemetry allowlists frozen here prevent per-feature divergence. Consumes Task 015 scaffold, Task 016 primitives, Task 017 client.

## Starting State
Tasks 015 (scaffold, providers, layouts slots), 016 (primitives), 017 (client, errors, keys) done. No real shell, IA, i18n bundles, or telemetry harness. Depends on Tasks 015, 016, 017.

## Scope
Included: layouts + IA (top-level nav, conditional Admin, adaptive project tabs), route guards, i18n keys/plural/RTL/locale dates/timezone, telemetry harness + correlation.
Excluded: auth session logic (Task 019), feature screens, notification data wiring (Task 034), backend analytics (Task 038).

## Instructions
1. Implement layouts in `frontend/src/app/layouts/`: `AppShell.tsx` (header: product nav, notification bell with unread-badge slot, user menu, locale/theme switchers; sidebar/main; footer with `VITE_APP_VERSION`), `ProjectLayout.tsx` (project header + tabs outlet).
2. Implement IA: top-level `Dashboard`, `Projects`, `Review`, `Notifications`, `Settings` + conditional `Admin` (rendered only when `/me` permissions include `admin:read`; route-guarded in `router.tsx` too — never CSS-only hiding). Project tabs `Overview, Media, Transcript, Translation, Voices, Timeline, Quality, Exports, Activity` via `frontend/src/app/navigation/projectTabs.ts` tab model derived from workspace state (e.g. `Translation` disabled until transcript exists, `Exports` badged when ready) — never hardcoded per page.
3. Implement i18n in `frontend/src/i18n/`: `en` baseline `*.json` namespaced (`common, nav, auth, dashboard, projects, errors, ...`), `i18n.ts` init (react-i18next, ICU/plural support, `en` fallback), `useLocale.ts` + locale-aware `formatDate`/`formatNumber` (`Intl`, tenant timezone from preferences — Task 035 reads it — defaulting to browser tz), RTL via `document.dir` switch (shared CSS already logical-properties-only from Task 016).
4. Implement telemetry in `frontend/src/telemetry/`: `telemetry.ts` harness (`trackPageView(route)`, `trackRouteChange`, `trackApiFailure({route, code, correlationId, latencyMs})`), `correlation.ts` (request → error → telemetry correlation-ID propagation), `TelemetryProvider` with `VITE_TELEMETRY_ENABLED` kill-switch + persisted opt-out; allowlist-based payloads only — never tokens, URLs (path only, query stripped), media bytes/URLs, or transcript/translation text.
5. Add route guards in `frontend/src/app/guards/`: `RequireAuth` (waits for pre-shell `/me` resolution from Task 019; shell skeleton meanwhile), `RequireAdmin` (permission-gated, redirects to 403).

## Requirements
- R1: Admin nav + route unreachable without `admin:read` (test asserts absence, not hiding).
- R2: All shell/nav strings via i18n keys (test scans shell components for raw JSX string literals).
- R3: Telemetry payloads constrained by allowlist type + test (forbidden fields throw/strip).
- R4: Dates/numbers respect tenant timezone/locale; unknown timezone → UTC fallback.
- R5: Project tab model derives from workspace state; state-gated tabs disabled with reason tooltip.

## Edge Cases and Error Handling
- Locale bundle missing → `en` fallback + telemetry warning (never blank strings).
- Telemetry endpoint down → drop events silently (never block UI, never retry-loop).
- Permissions load failure → render non-admin shell (fail closed on Admin).

## Security and Safety Requirements
- Telemetry never contains tokens, URL queries, media, transcript/translation text, or PII (email hashed or omitted).
- Correlation IDs random; admin gating is UX defense-in-depth (server authoritative per Tasks 013/036).

## Testing
- Create `frontend/src/app/layouts/__tests__/shell.test.tsx` (nav render, admin absent without permission, tab model states), `frontend/src/i18n/__tests__/i18n.test.ts` (plural rules incl. Arabic/Russian samples, RTL switch, `en` fallback), `frontend/src/telemetry/__tests__/telemetry.test.ts` (allowlist enforcement, kill-switch, query-stripping).
- Playwright `@shell`: login → shell renders nav; admin hidden for non-admin.
- Type: unit (vitest) + Playwright.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/app src/i18n src/telemetry
npx playwright test --grep="@shell"
```

## Completion Criteria
- Shell + adaptive IA + guards, i18n with plural/RTL/timezone, and allowlisted telemetry exist; admin fail-closed; shell/i18n/telemetry tests + `@shell` smoke pass.

## Traceability
- Plan B §10.9, §11.1, §11.4, §11.5, §14.2, §14.3, §14.4. Depends on Tasks 015, 016, 017.
