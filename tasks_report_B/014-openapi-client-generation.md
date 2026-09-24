# 014 — OpenAPI Authority and TypeScript Client Generation

## Status
COMPLETED

## Summary
Re-verified the Task 014 contract authority end to end: the committed `src/DubbingPlatform.Api/OpenApi/openapi.v1.json` bundle (75 paths, 83 operations, 57 schemas, 65 error codes, 14 SSE types, secret-free) regenerates the committed `frontend/src/api/generated/` client (`index.ts, schemas.ts, client.ts, OPENAPI_VERSION`) byte-identically via the hermetic `node tools/generate-client.mjs` runner, the drift gate passes, `npm run build --prefix frontend` (drift prebuild + strict tsc) is clean, and all test suites pass. No implementation changes were required — the prior session's work is intact and still meets every requirement (R1–R5) and completion criterion.

## Files Created/Modified
- `src/DubbingPlatform.Api/OpenApi/openapi.v1.json` (verified, no change) — versioned bundle: 75 paths/83 ops covering all 006–013 routes, shared parameters/responses, 57 schemas incl. `ErrorCode` (65), `SseEventType` (14), `SseEnvelope`, `ErrorResponse`, `PaginatedResult`, `Idempotency-Key`/`If-Match`/`Last-Event-ID`/`access_token` docs, `text/event-stream` stream op, `servers: [/api/v1]`, `***` token placeholders.
- `tools/generate-client.mjs` (verified, no change) — hermetic generator (Node stdlib only): validates bundle (fails loudly on schema errors), emits deterministic `schemas.ts`/`client.ts` (`ApiClient`, `ApiError`, per-operation typed methods, SSE `openStream`/`parseFrame`)/`index.ts`/`OPENAPI_VERSION` (bundle sha256 + toolchain pin).
- `tools/check-api-drift.mjs` (verified, no change) — drift gate: regenerates to temp dir, verifies stamp hash, byte-diffs; fails with `API DRIFT: run make generate-api and commit`.
- `tools/api-generator.version` (verified, no change) — pins `openapi-typescript-codegen@0.31.0`, local runner `1.0.0`, `typescript@5.6.3`.
- `frontend/package.json` / `frontend/package-lock.json` (verified, no change) — `generate-api`/`check-drift`/`prebuild` (drift)/`build` (`tsc --noEmit`); pinned devDeps.
- `frontend/tsconfig.json` (verified, no change) — strict, `noEmit`, `Bundler` resolution, `ES2022`+`DOM`.
- `frontend/src/api/generated/` (verified, no change) — `index.ts`, `schemas.ts`, `client.ts` (83 typed methods), `OPENAPI_VERSION`; auto-generated headers.
- `frontend/src/api/smoke.ts` (verified, no change) — strict-TS smoke import (14 SSE types, codes, envelopes, client construction).
- `tests/DubbingPlatform.IntegrationTests/OpenApi/OpenApiCoverageTests.cs` (verified, no change) — 8 hermetic tests: version/servers, 83-route coverage + unique operationIds, envelopes (65 codes), SSE contract + stream docs, mutation idempotency/concurrency docs, secret scan, dup-key scan, generated-client + stamp-hash match.
- `Makefile` (verified, no change) — `generate-api` and `check-api-drift` targets.
- `src/DubbingPlatform.Api/DubbingPlatform.Api.csproj` (verified, no change) — `CheckApiClientDrift` target (`AfterTargets=Build`, runs only when `CI==true`).
- `.gitignore` (verified, no change) — ignores `node_modules/` (+frontend), explicitly keeps `frontend/src/api/generated/`.
- `.github/workflows/ci.yml` (verified, no change) — `api-contract` job (setup-node 24, `npm ci`, drift gate, frontend build, coverage tests).
- `docs/api-contract.md` (verified, no change) — one-page authority diagram, usage, pin/enum/SSE checklists, drift dry-run.
- `tasks_report_B/014-openapi-client-generation.md` (rewritten) — this re-verification report.

## Decisions Made
- **No code changes on re-execution:** every 014 artifact from the prior session exists with the expected content (bundle, both tools, version pin, frontend package/config/generated output, coverage tests, Makefile targets, MSBuild gate, `.gitignore` exception, CI job, docs). Re-running the full validation suite passed without modification, so the correct production-safe action was to verify, not to churn files.
- **`make` still unavailable on this Windows host:** `make generate-api` cannot run here (`make` not installed); the target body (`node tools/generate-client.mjs`) was executed directly, which is exactly what the Makefile target invokes. CI (ubuntu-latest) runs the make targets.
- **Same-path output note stands:** `GET /projects/{projectId}/output` keeps one JSON key pinning the 012/012A readiness aggregate with an explicit note about the pre-existing C# workspace-projection ambiguity (runtime tech debt, out of scope); the dup-key coverage test guards it.
- **Notifications read endpoints stay exempt from `Idempotency-Key` docs:** `read`/`read-all` are naturally idempotent (like `/auth/*`, `/me*`); the coverage test allowlists exactly these two.

## Build/Test Results
- `node tools/generate-client.mjs` → `generate-api: wrote 4 files to frontend\src\api\generated` (run three times; `Get-FileHash` identical across runs: `client.ts 76B2DCE0…`, `schemas.ts AC9275C6…`, `index.ts 9AD9C3F6…`, `OPENAPI_VERSION AE176C81…` — R3 byte-identical confirmed).
- `node tools/check-api-drift.mjs` → `check-api-drift: generated client matches the committed bundle.`
- `npm run build --prefix frontend` → prebuild drift `check-api-drift: generated client matches the committed bundle.`, then `tsc --noEmit -p tsconfig.json` clean (exit 0).
- `dotnet build --nologo -v q` → `Build succeeded. 0 Warning(s) 0 Error(s) Time Elapsed 00:00:13.64`.
- `dotnet test --filter FullyQualifiedName~OpenApiCoverageTests --nologo -v q` → `Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8, Duration: 211 ms - DubbingPlatform.IntegrationTests.dll (net10.0)`.
- `dotnet test tests/DubbingPlatform.UnitTests --nologo -v q` → `Passed! - Failed: 0, Passed: 412, Skipped: 0, Total: 412, Duration: 10 s - DubbingPlatform.UnitTests.dll (net10.0)`.

## Recommendations for Next Agent (015)
- **State:** 001–014 done; 014 delta still uncommitted (new: `OpenApi/openapi.v1.json`, `tools/*.mjs`, `tools/api-generator.version`, `frontend/` incl. `package-lock.json`, `IntegrationTests/OpenApi/`, `docs/api-contract.md`, this report; modified: `Makefile`, `Api.csproj`, `.gitignore`, `ci.yml`, plus pre-existing `master-prompt.md` modification — leave that file alone). Counts pinned: `ErrorCodes.All`=65, `SseEventTypes.All`=14, bundle 75 paths/83 ops/57 schemas, UnitTests 412. Docker-gated suites still skip locally; CI runs them plus the new `api-contract` job live.
- **Key APIs:** generator `tools/generate-client.mjs` (`--bundle=`, `--out=`; `tsType(schema, owner, prefix)`, `resolveTypeName`, `emitClient`/`emitSchemas`); drift `tools/check-api-drift.mjs` (stamp-hash first, then byte-diff); client `ApiClient({baseUrl, getToken})` with `request<T>`/`requestRaw`/`openStream`/`parseFrame`, per-op `XxxParams { path, query? }` interfaces, `RequestOptions { idempotencyKey?, ifMatch?, lastEventId?, signal? }`, `ApiError { status, code, message, correlationId, details }`; test helpers in `OpenApiCoverageTests` (`RepoRoot()` via `DubbingPlatform.sln` walk, `ResolveParameterNames` with `$ref` resolution).
- **Gotchas:** (1) Params shape is `{ path, query? }` — path values live under `.path` (codegen substitutes `{param}` from there). (2) Client type refs use the `S.` (`schemas.js`) prefix; generated imports use `.js` extensions under `Bundler` resolution — keep `frontend/tsconfig.json` as is. (3) After ANY bundle edit, run `node tools/generate-client.mjs` (or `make generate-api`) — `OPENAPI_VERSION` hash, drift gate, and stamp test will fail otherwise. (4) Generator output must stay byte-identical: sorted keys/ops, fixed header, LF, no timestamps. (5) `TreatWarningsAsErrors` is on — keep test code nullable-clean. (6) Never put real tokens in bundle examples (`Bearer eyJ` fails generation + tests); never log `?access_token`.
- **Incomplete integration points:** 015 scaffolds the app around `frontend/src/api/generated/` + `smoke.ts` (use generated types only, never hand-written fetch shapes); 017 wires query keys/error normalization over `ApiClient`/`ApiError`; 026 consumes `SseEnvelope` + `openStream`/`parseFrame` with `Last-Event-ID` resume; `notification.created` still has no producer (future worker must build `SseEnvelope` + `SseEventBuffer.Append` per 013). The same-path `GET .../output` C# ambiguity (workspace projection vs aggregate) is pre-existing backend tech debt — flag to whoever owns runtime routing.
- **Test helpers:** `OpenApiCoverageTests` is fully hermetic (reads bundle + generated files, no Docker); extend `ExpectedRoutes`/`ExpectedGeneratedTypes`/`ExpectedSseTypes` in the same commit as any contract change. `frontend/src/api/smoke.ts` is the strict-TS consumption proof — extend it when 015 needs new DTOs covered.
- **Warnings:** `frontend/node_modules/` is git-ignored but `package-lock.json` is committed (CI uses `npm ci`); `CheckApiClientDrift` skips when `CI != true` — always run `make check-api-drift` (or `node tools/check-api-drift.mjs` on Windows) before finishing backend contract changes.
