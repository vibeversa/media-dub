# Task 014 — OpenAPI Authority and TypeScript Client Generation

## Goal
Make versioned OpenAPI the single contract authority with generated TS client and CI drift-fail.

## Context
Frontend (Tasks 015+) must build against generated types, not hand-written fetch shapes; any backend contract change in Tasks 006–013 without regenerating the client is a defect. This task freezes OpenAPI as authoritative, adds `make generate-api`, commits the generated client under `frontend/src/api/generated/`, and fails CI/build when stale.

## Starting State
Tasks 006–013 done (all backend endpoints + OpenAPI annotations). No bundled OpenAPI artifact, no generator, no `frontend/src/api/generated/` (frontend scaffolding lands in Task 015; this task creates the target dir + .gitkeep-compatible generation).

## Scope
Included: versioned OpenAPI bundle (schemas, examples, enums, errors, pagination, SSE, idempotency, concurrency), `make generate-api`, generated client output, drift check (build fails when stale), docs for regeneration.
Excluded: frontend app scaffolding (Task 015), query-key/error-normalization wiring (Task 017), backend endpoint changes (Tasks 006–013, frozen inputs).

## Instructions
1. Bundle versioned OpenAPI in `src/DubbingPlatform.Api/OpenApi/openapi.v1.json` (generated at build from Swashbuckle/NSwag + hand-checked): must include all Tasks 006–013 routes with schemas, request/response examples, enums (statuses, severities, SSE event types, error codes), error envelope, pagination envelope, `Idempotency-Key` header + `If-Match`/expected-version concurrency docs, and SSE `stream` endpoint description.
2. Add `Makefile` target `generate-api` (repo root; also `make check-api-drift`): runs `openapi-typescript-codegen` (or `orval`) with pinned version in `package.json`/`tools/manifest`, outputs to `frontend/src/api/generated/` (`index.ts, schemas.ts, client.ts` or tool equivalent + `OPENAPI_VERSION` stamp file). Command must be hermetic (no network at generate time — bundle from step 1 is the input).
3. Commit generated output: `frontend/src/api/generated/` checked in; `.gitignore` must NOT exclude it; generated files carry `/* auto-generated — do not edit; run make generate-api */` header.
4. Add drift gate: `frontend/package.json` `prebuild` (or CI step + `make check-api-drift`) regenerates to a temp dir and diffs against committed `frontend/src/api/generated/`; any diff fails the build with `API DRIFT: run make generate-api and commit`. Backend CI runs the same check (`dotnet build` target invoking the diff script via `exec`).
5. Document regeneration in `docs/api-contract.md` (one page): source of truth diagram (annotations → bundle → generated client), `make generate-api` usage, enum/error-code addition checklist, SSE-type addition rule (Task 013 closed set must be updated first).

## Requirements
- R1: `openapi.v1.json` covers every Tasks 006–013 route (contract test asserts route count ≥ expected list; missing route fails).
- R2: Generated client contains all DTOs, enums (incl. 14 SSE types + error codes), and pagination/error envelopes.
- R3: `make generate-api` is hermetic and reproducible (two consecutive runs byte-identical).
- R4: Stale generated client fails build/CI (drift test: touch bundle → check fails until regenerate).
- R5: Generated files are committed and importable by `frontend/` strict TS (Task 015+ consumes them; smoke import compiles).

## Edge Cases and Error Handling
- OpenAPI bundle invalid → `generate-api` fails with schema validation errors (not silent partial output).
- Generator version bump → `OPENAPI_VERSION` stamp + lockfile change required in same commit (drift check covers output, reviewer covers version pin).
- `frontend/` not yet scaffolded (Task 015 pending) → `generate-api` still succeeds by creating `frontend/src/api/generated/` only.

## Security and Safety Requirements
- No secrets/examples with real tokens in OpenAPI examples (scan: `Bearer eyJ` forbidden outside `***` placeholders).
- SSE/concurrency docs state auth + tenant rules; no internal URLs in `servers[]` beyond relative `/api/v1`.
- Drift gate runs in CI on every PR touching `src/DubbingPlatform.Api/**`.

## Testing
- Backend: extend OpenAPI bundle test (route-coverage assertion) — location `tests/DubbingPlatform.IntegrationTests/OpenApi/OpenApiCoverageTests.cs` (assert all expected paths + error/pagination envelopes + 14 SSE enum values present).
- Frontend: generated-client smoke import compiles under strict TS (`frontend/src/api/generated/` import in a typecheck test; full wiring in Task 017).
- Drift: `make check-api-drift` script tested by mutating bundle in CI dry-run (documented in `docs/api-contract.md`).

## Validation
```bash
make generate-api
npm run build --prefix frontend
dotnet build
dotnet test --filter FullyQualifiedName~OpenApiCoverageTests
```

## Completion Criteria
- Versioned bundle + `make generate-api` + committed generated client + drift-fail exist; `OpenApiCoverageTests` pass; `npm run build --prefix frontend` succeeds on generated code; consecutive generations byte-identical.

## Traceability
- Plan B §10.4, §16.3. Depends on Tasks 006–013.
