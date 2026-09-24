# 015 — Frontend Scaffolding, Routing, Providers

## Status
COMPLETED

## Summary
Built the standalone `frontend/` app shell on top of the Task 014 generated client: strict TypeScript (incl. `noUncheckedIndexedAccess`, `noImplicitReturns`), Vite 5 + React 18 + React Router 6 + TanStack Query 5 + Zustand 5 + Tailwind 3, with 9 lazy route chunks, a single shared `QueryClient`, a zod-validated `VITE_*` reader with config-error screen, chunk-failure boundary with reload + version stamp, and vitest coverage (router + env guard). `typecheck`, `lint` (zero-warning), `build` (drift prebuild + typecheck + vite, 9 per-route chunks, no sourcemaps), and all 6 frontend tests pass; backend gates (`dotnet build`, UnitTests 412, OpenApiCoverage 8) still pass.

## Files Created/Modified
- `frontend/package.json` (modified) — scripts: `typecheck` (`tsc --noEmit`), `lint` (`eslint . --max-warnings=0`), `build` (`typecheck` → `vite build`, drift still via `prebuild`), `test` (`vitest run`), `preview`, `build-storybook` (placeholder); deps React 18.3.1/Router 6.28/Query 5.62/Zustand 5.0.2/zod 3.23.8; pins kept (`typescript@5.6.3`, `openapi-typescript-codegen@0.31.0`).
- `frontend/package-lock.json` (modified) — regenerated for the new dependency set.
- `frontend/tsconfig.json` (modified) — added `jsx: react-jsx`, `DOM.Iterable`, `noUncheckedIndexedAccess`, `noImplicitReturns`, `noFallthroughCasesInSwitch`, `types: [vite/client]`; includes config files; generated client stays typechecked.
- `frontend/vite.config.ts` (created) — react plugin, `build.sourcemap: false`, dev/preview ports, vitest jsdom config.
- `frontend/tailwind.config.ts` (created) — content-scanned baseline, default theme untouched (tokens in 016).
- `frontend/postcss.config.js` (created) — tailwindcss + autoprefixer.
- `frontend/eslint.config.js` (created) — flat config: `no-explicit-any` error, `no-restricted-syntax` banning `import.meta` member access under `src/**` except `src/lib/env.ts`, react-hooks + react-refresh, generated client ignored.
- `frontend/index.html` (created) — baseline CSP meta, `referrer strict-origin-when-cross-origin`, `#root` + `/src/main.tsx`.
- `frontend/.env.example` (created) — `VITE_API_BASE_URL`, `VITE_APP_VERSION`, `VITE_SSE_ENABLED`, `VITE_TELEMETRY_ENABLED` + no-secrets notice.
- `frontend/src/main.tsx` (created) — StrictMode root mount, asserts `#root`, imports Tailwind CSS.
- `frontend/src/vite-env.d.ts` (created) — `vite/client` reference.
- `frontend/src/styles/index.css` (created) — Tailwind directives.
- `frontend/src/app/App.tsx` (created) — env guard → `ConfigErrorScreen`, else Query/Store/Theme/Locale/Telemetry providers + `ChunkErrorBoundary` + `Suspense` + `RouterProvider` (v7_startTransition provider flag).
- `frontend/src/app/router.tsx` (created) — `routes` (RootLayout/AuthLayout shells, index → `/dashboard`, 8 lazy pages, `*` → lazy NotFound, no loaders), `routePaths`, `createAppRouter()`, `ROUTER_FUTURE_FLAGS` + `ROUTER_PROVIDER_FUTURE_FLAGS`.
- `frontend/src/app/pages/lazy.ts` (created) — 9 `React.lazy` page imports (one chunk each; isolated so `router.tsx` exports only route data per react-refresh rule).
- `frontend/src/app/pages/*.tsx` (created, 9 files) — Dashboard/Projects/ProjectDetails (`useParams` id)/Review/Notifications/Settings/Admin/Login/NotFound (link home, no redirect), each with `data-testid`.
- `frontend/src/app/providers/` (created) — `queryClient.ts` (singleton: `retry: false`, `staleTime` 30s, `gcTime` 5m, no window-focus refetch), `QueryProvider.tsx`, `StoreProvider`/`ThemeProvider`/`LocaleProvider`/`TelemetryProvider` stubs, `index.ts` barrel.
- `frontend/src/app/layouts/` (created) — `RootLayout.tsx` (nav + outlet + suspense + chunk boundary), `AuthLayout.tsx` (minimal card), `index.ts`.
- `frontend/src/app/RouteFallback.tsx`, `ChunkErrorBoundary.tsx` (reload + `VITE_APP_VERSION` stamp), `ConfigErrorScreen.tsx` (never blank) (created).
- `frontend/src/app/__tests__/router.test.tsx` (created) — routePaths table, fallback render, dashboard render, unknown → NotFound (memory router + Suspense).
- `frontend/src/lib/env.ts` + `index.ts` (created) — zod-validated `getEnv()` (cached) / `tryGetEnv()` / test-only `resetEnvCache()`; sole `import.meta.env` user.
- `frontend/src/lib/__tests__/env.test.ts` (created) — valid parse + missing-base-URL fail-fast.
- `frontend/src/stores/index.ts` (created) — Zustand root (`useAppStore`: `ready`/`markReady`).
- `frontend/src/{features,components,hooks,i18n,telemetry,types}/index.ts` (created) — empty index barrels for later tasks.
- `frontend/scripts/build-storybook-placeholder.mjs` (created) — succeeding placeholder (real config in 016).
- `.github/workflows/ci.yml` (modified) — api-contract job seeds `frontend/.env` from `.env.example` before the frontend build (R5 build gate).
- `.gitignore` (modified) — ignore `frontend/dist/`, `frontend/coverage/`.
- `tasks_report_B/015-frontend-scaffolding-routing.md` (created) — this report.

## Decisions Made
- **Kept Task 014 outputs byte-stable:** `generate-api`/`check-drift`/`prebuild` scripts, generator pins, and `src/api/generated/` untouched; `build` became `typecheck && vite build` so the drift `prebuild` still runs first and the API-contract CI job keeps working.
- **Split rule-sensitive modules:** `queryClient` singleton lives in `providers/queryClient.ts` (not beside the component) and lazy pages in `pages/lazy.ts` (not beside route data) to satisfy `react-refresh/only-export-components` without weakening lint.
- **`v7_startTransition` placement:** it is a `<RouterProvider future>` prop in react-router 6.28, not a router-init flag (verified against `node_modules/react-router/dist/lib/components.d.ts`); init flags live in `ROUTER_FUTURE_FLAGS`, the provider flag in `ROUTER_PROVIDER_FUTURE_FLAGS`, used by both `App.tsx` and tests — zero future-flag warnings.
- **Extra env test kept:** task required only the router test, but the missing-`VITE_API_BASE_URL` edge case is a completion criterion, so `src/lib/__tests__/env.test.ts` pins it (2 tests, `vi.stubEnv` + `resetEnvCache`).
- **Placeholders scoped to spec:** `build-storybook` script and provider stubs exist because the task explicitly orders them; no other stubs — every page/layout/boundary renders real UI.
- **Local `.env` seeded from example:** `frontend/.env` (gitignored) used for the R5 build validation; CI now does the same copy.

## Build/Test Results
- `npm run typecheck` → `tsc --noEmit -p tsconfig.json`, exit 0.
- `npm run lint` → `eslint . --max-warnings=0`, exit 0 (fixed 11 initial errors: node global, react-refresh splits).
- `npm run test` → `Test Files 2 passed (2) / Tests 6 passed (6)` (router 4 + env 2), zero router future-flag warnings.
- `npm run test -- src/app` → same router suite passes (task command).
- `npm run build` → drift `check-api-drift: generated client matches the committed bundle.`, typecheck clean, `vite v5.4.11 building for production... 103 modules transformed`, 9 per-route chunks (`DashboardPage/ProjectsPage/ProjectDetailsPage/ReviewPage/NotificationsPage/SettingsPage/AdminPage/LoginPage/NotFoundPage-*.js`) + `✓ built in 3.95s`.
- `npm run build-storybook` → `build-storybook: Storybook is configured in Task 016.`
- `dotnet build --nologo -v q` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test tests/DubbingPlatform.UnitTests` → `Passed! - Failed: 0, Passed: 412, Skipped: 0, Total: 412`.
- `dotnet test --filter FullyQualifiedName~OpenApiCoverageTests` → `Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8`.
- Grep checks: `import.meta` only in `src/lib/env.ts` (code + comment); zero `: any` in `src/`; `dist/assets` contains 0 `.map` files.

## Recommendations for Next Agent (016)
- **State:** 001–015 done, all uncommitted (015 delta: modified `ci.yml`, `.gitignore`, `frontend/package.json`, `package-lock.json`, `tsconfig.json`; new scaffold files listed above; plus the still-uncommitted 014 delta). Backend counts pinned: `ErrorCodes.All`=65, `SseEventTypes.All`=14, bundle 75 paths/83 ops/57 schemas, UnitTests 412, frontend tests 6. `frontend/.env` + `frontend/dist/` exist locally but are gitignored — do not commit them.
- **Key APIs:** `getEnv()`/`tryGetEnv()`/`resetEnvCache()` in `frontend/src/lib/env.ts` (sole `import.meta.env` user — lint fails otherwise); `queryClient` singleton in `frontend/src/app/providers/queryClient.ts` (import directly, it is intentionally absent from `providers/index.ts`); `routes`/`routePaths`/`createAppRouter()`/`ROUTER_FUTURE_FLAGS`/`ROUTER_PROVIDER_FUTURE_FLAGS` in `frontend/src/app/router.tsx`; lazy pages in `frontend/src/app/pages/lazy.ts`; `useAppStore` in `frontend/src/stores/index.ts`; generated client `ApiClient`/`ApiError` from `frontend/src/api/generated/index.js` (Task 014 authority — never hand-write fetch shapes).
- **Gotchas:** (1) `npm run build` always runs the drift `prebuild` first — after any bundle edit run `node tools/generate-client.mjs`. (2) `react-refresh/only-export-components` is error-level: keep non-component exports out of `.tsx` component files. (3) `no-restricted-syntax` bans `import.meta` member access outside `src/lib/env.ts` — read env only via `getEnv()`/`tryGetEnv()`. (4) `v7_startTransition` goes on `<RouterProvider future>`, other v7 flags on router init — see `router.tsx`. (5) `make` is unavailable on this Windows host; invoke `node tools/*.mjs` directly. (6) Tests use explicit `vitest` imports + manual `cleanup()` (no globals mode); jsdom env comes from `vite.config.ts`.
- **Incomplete integration points:** 016 owns design tokens/primitives + real Storybook config (replace `scripts/build-storybook-placeholder.mjs` wiring); 017 owns API wiring over `ApiClient` (GET-only retry, error normalization, query keys) and will fill `QueryProvider` defaults; 018 owns shell/IA/i18n/telemetry + real `StoreProvider`/`ThemeProvider`/`LocaleProvider`/`TelemetryProvider` bodies (slots + nesting order frozen in `App.tsx`); feature dirs (`features/`, `components/`, `hooks/`, `i18n/`, `telemetry/`, `types/`) are empty barrels awaiting 016–044.
- **Test helpers:** extend `src/app/__tests__/router.test.tsx` (`EXPECTED_PATHS`, `createMemoryRouter(routes, { initialEntries, future: {...ROUTER_FUTURE_FLAGS} })` + provider flags) alongside any route change; `src/lib/__tests__/env.test.ts` pattern (`vi.stubEnv` + `resetEnvCache` + `vi.unstubAllEnvs`) for future env vars.
- **Warnings:** `VITE_API_BASE_URL` in `.env.example` is `http://localhost:5000` (dev only); never put backend secrets in any `VITE_*` var (bundle is public); `index.html` CSP is baseline only — full headers land in Task 037, do not weaken the meta tag.
