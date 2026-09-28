# Coverage (Task 039A)

What is measured, what is excluded and why, and how to close a gap. The 80%
target below is the goal gap-closure tasks 039B (frontend matrices) and 039C
(backend units) work toward; the CI floor that fails builds today is the
vitest `thresholds` block plus the presence gate.

## Frontend (Vitest + V8)

- Canonical config: `frontend/vite.config.ts` (`test.coverage`). There is no
  separate `vitest.config.ts` by design — one config, no dual-config drift.
- Command: `npm run test --prefix frontend -- --coverage`.
- Artifacts (gitignored, never committed): `frontend/coverage/` — `text`
  (console), `lcov` (`lcov.info`), `html` (browseable), `json-summary`
  (`coverage-summary.json`, machine input for `scripts/coverage-gap.mjs`).
- Tool versions are lockfile-pinned: `vitest` and `@vitest/coverage-v8` must
  share a major (currently `2.1.8`); `msw` is pinned (`2.15.0`) for the
  taxonomy server. `scripts/coverage-gap.mjs` verifies the vitest/coverage-v8
  majors before reading any summary and fails with
  `COVERAGE_TOOL_VERSION_MISMATCH` on drift — never a silent zero-coverage
  pass. If the summary is absent it fails with `COVERAGE_SUMMARY_MISSING`
  (run the coverage command first).

### Thresholds

| Layer | Lines | Branches | Functions | Statements | Enforced by |
| --- | --- | --- | --- | --- | --- |
| CI floor (global) | 72 | 64 | 68 | 72 | `test.coverage.thresholds` in `vite.config.ts` |
| Per-file target | 80 | 80 | 80 | 80 | `scripts/coverage-gap.mjs` report for 039B/039C |

The floor sits below the measured baseline (78.2 lines / 71.18 branches /
74.66 funcs) minus run-to-run headroom so the gate is stable; it is raised to
80 once the gap-closure suites land. The per-file 80% target covers
`src/features`, `src/api`, `src/hooks`, `src/lib`, `src/stores`,
`src/telemetry`, `src/app`, `src/components`, `src/i18n`, `src/mocks`.
`node scripts/coverage-gap.mjs` lists every below-target file as
`COVERAGE_GAP:<path> <metric>=<pct>...`; empty output means 039B/039C are
done. The script exits 0 with gaps present (it is a report; the vitest
thresholds are the gate).

### Exclusion policy

Explicit `exclude` list in `vite.config.ts` (policy, never accident):

- `src/api/generated/**` — generated OpenAPI client owned by Task 014
  (generator + drift gate); testing generated output would test the generator.
- `src/**/*.test.*`, `src/**/*.spec.*`, `src/testSetup.ts` — tests and harness.
- `**/*.d.ts`, `playwright.config.ts`, `e2e/**` — types and E2E (041A–D own).
- `dist/**`, `storybook-static/**`, `.storybook/**` — build/output artifacts.
  `include` stays `src/**` so V8 never pulls bundles into the table.

## Backend (coverlet + XPlat Code Coverage)

- Collector `coverlet.collector` is pinned (`6.0.4`) in every test project
  (`tests/DubbingPlatform.{UnitTests,IntegrationTests,ContractTests,E2ETests}/*.csproj`).
- Gate reference settings: `tests/coverage.runsettings` (formats
  `cobertura,json`; excludes test assemblies and `*.Generated`).
- Commands (from the repo root):
  - `dotnet test --filter FullyQualifiedName~UnitTests --collect:"XPlat Code Coverage"`
  - With the runsettings: `dotnet test --filter FullyQualifiedName~UnitTests --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings`
- Reports land under each project's `TestResults/` (gitignored). The per-area
  gap list for backend units is consumed by 039C; no numeric backend gate is
  enforced here (039C sets it after measuring, mirroring the frontend
  floor-then-target approach).

## Closing a gap

1. Run the coverage command for your side (frontend/backend above).
2. Frontend: `node scripts/coverage-gap.mjs` names the below-80 files; add
   specs in the owning feature task's suite (ownership: `docs/test-ownership.md`),
   reusing the MSW taxonomy (`frontend/src/mocks/`) for failure shapes.
3. Backend: add units in `tests/DubbingPlatform.UnitTests` (039C owns the list).
4. Re-run until the gap script output for your area is empty.

## Quarantine and bypass policy

There is no coverage-gate bypass flag. A red gate is closed by adding specs or
by an expiry-tracked quarantine entry per the 046 policy (owner + issue +
expiry date, never a silent retry or a lowered threshold without a tracking
issue). Coverage reports contain file paths + counts only — never source
excerpts, tokens, URLs, media, or transcript content.
