# Task 015 — Frontend Scaffolding, Routing, Providers

## Goal
Create standalone `frontend/` app with strict TypeScript, Vite, React Router, TanStack Query, Zustand, and Tailwind baseline.

## Context
All product UX (Tasks 016–044) builds on this scaffold; routing, providers, and env conventions frozen here prevent per-feature drift. Backend contracts arrive via the Task 014 generated client; this task wires the shell they plug into.

## Starting State
Task 014 done (`frontend/src/api/generated/` exists with committed client). No `frontend/package.json`, no Vite config, no `App.tsx`. Depends on Task 014.

## Scope
Included: `frontend/` project setup, TS strict config, Vite, Router, Query client, Zustand store root, Tailwind, directory structure, `App.tsx`/`router.tsx`/providers/layouts, route code-splitting, `VITE_*` env.
Excluded: design tokens/primitives (Task 016), API client wiring (Task 017), shell/IA/i18n/telemetry (Task 018), any feature screens.

## Instructions
1. Scaffold `frontend/` with `frontend/package.json` (React 18+, TypeScript strict, Vite, `react-router-dom`, `@tanstack/react-query`, `zustand`, `tailwindcss`), `frontend/tsconfig.json` (`strict: true`, `noUncheckedIndexedAccess: true`, `noImplicitReturns`), `frontend/vite.config.ts`, `frontend/tailwind.config.ts`, `frontend/postcss.config.js`, `frontend/.env.example` documenting all `VITE_*` vars (`VITE_API_BASE_URL`, `VITE_APP_VERSION`, `VITE_SSE_ENABLED`, `VITE_TELEMETRY_ENABLED`).
2. Create directory structure under `frontend/src/`: `app/` (`App.tsx`, `router.tsx`, `providers/`, `layouts/`), `api/`, `features/`, `components/`, `stores/`, `hooks/`, `lib/`, `styles/`, `i18n/`, `telemetry/`, `types/` (empty index barrels only; feature content lands in later tasks).
3. Create `frontend/src/app/App.tsx`: composes `QueryClientProvider` + `RouterProvider` + theme/locale/telemetry providers (provider implementations land in Task 018; import from provider modules with stub exports here so App compiles).
4. Create `frontend/src/app/router.tsx`: route tree with lazy code-splitting (`React.lazy` + `Suspense` fallback) for `dashboard`, `projects`, `projects/:id/*`, `review`, `notifications`, `settings`, `admin`, `login`; `*` → NotFound; no route `loader`s (data via TanStack Query in Task 017+).
5. Create `frontend/src/app/providers/` (`QueryProvider.tsx` with single shared QueryClient: `retry: false` by default — GET-only retry lives in Task 017 — plus `staleTime`/`gcTime`; `StoreProvider`/`ThemeProvider`/`LocaleProvider` stubs) and `frontend/src/app/layouts/` (`RootLayout.tsx` outlet + suspense boundary, `AuthLayout.tsx` minimal).
6. Add `frontend/src/lib/env.ts`: typed `VITE_*` reader with zod validation at startup; missing/invalid required var fails fast with a config-error screen (never silent `undefined`).
7. Add scripts to `frontend/package.json`: `typecheck` (`tsc --noEmit`), `lint` (eslint flat config, zero-warning policy), `build` (typecheck → vite build), `test` (vitest), `preview`, `build-storybook` placeholder (real config in Task 016).

## Requirements
- R1: `tsc --noEmit` passes with strict flags; no `any` leaks in scaffold files (eslint `@typescript-eslint/no-explicit-any` set to error).
- R2: Every top-level route chunk is lazy-loaded; `vite build` emits separate chunks per route (assert via chunk naming in build output).
- R3: All env access goes through `lib/env.ts`; direct `import.meta.env` outside it fails lint (`no-restricted-syntax`).
- R4: QueryClient constructed once in `QueryProvider`; no per-component client construction.
- R5: Production build succeeds with `VITE_*` values from `.env.example` (CI uses example env for the build gate).

## Edge Cases and Error Handling
- Missing `VITE_API_BASE_URL` → startup guard renders config-error screen, never a blank page.
- Chunk load failure (deploy skew) → error boundary offers reload button with `VITE_APP_VERSION` stamp.
- Unknown route → NotFound with link home; no redirect loops.

## Security and Safety Requirements
- No secrets in `.env.example` (only public `VITE_*` vars; document that backend secrets never enter frontend env).
- `index.html` sets baseline CSP meta + `referrer` policy (full CSP headers land in Task 037; scaffold must not weaken them).
- Source maps disabled for production build by default.

## Testing
- Create `frontend/src/app/__tests__/router.test.tsx`: asserts route table renders expected paths, lazy fallback shows, unknown path → NotFound (vitest + MemoryRouter).
- Type: unit (vitest); run `npm run test -- src/app`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run lint
cd frontend && npm run build
```

## Completion Criteria
- `frontend/` builds, typechecks, and lints clean; route code-splitting verified in build output; env guard works; router test passes.

## Traceability
- Plan B §10.1, §10.2, §22. Depends on Task 014.
