# API Contract Authority (Task 014)

Source of truth chain (one direction only):

```
Controller annotations (Tasks 006-013, frozen inputs)
        |
        v
src/DubbingPlatform.Api/OpenApi/openapi.v1.json   <- hand-checked versioned bundle (openapi 3.0.3, info.version v1)
        |  `make generate-api`  (node tools/generate-client.mjs, hermetic: Node stdlib only, no network)
        v
frontend/src/api/generated/  (committed: index.ts, schemas.ts, client.ts, OPENAPI_VERSION)
        |  consumed by Task 015+ (generated types only, never hand-written fetch shapes)
        v
Task 017 query-key / error-normalization wiring
```

## Usage

- Regenerate: `make generate-api` (Windows without make: `node tools/generate-client.mjs`).
- Drift check: `make check-api-drift` (Windows: `node tools/check-api-drift.mjs`).
  Regenerates to a temp dir and diffs against the committed output; any diff
  fails with `API DRIFT: run make generate-api and commit`.
- Frontend build (`npm run build --prefix frontend`) runs the drift check as
  `prebuild`, then `tsc --noEmit` (strict) over the generated client plus
  `frontend/src/api/smoke.ts`.
- Backend: in CI (`CI=true`), `dotnet build` runs the `CheckApiClientDrift`
  MSBuild target (`DubbingPlatform.Api.csproj`), which execs the same drift
  script. Locally the target is skipped; run `make check-api-drift` instead
  (or pass `-p:SkipApiDriftCheck=true` in CI for node-less images).

## Toolchain pins

`tools/api-generator.version` pins the canonical generator
(`openapi-typescript-codegen@0.31.0`, output shape mirrored by the hermetic
local runner `tools/generate-client.mjs@1.0.0`) and `typescript@5.6.3`
(`frontend/package.json` devDependencies + `package-lock.json`).
A generator version bump must change the pin, the lockfile, and the
regenerated output in the same commit; the drift gate covers the output,
reviewers cover the pin.

## Checklists

Adding an enum value or error code:

1. Extend the backend catalog first (`ErrorCodes`, `SseEventTypes`, domain enums).
2. Add the value to `openapi.v1.json` (`ErrorCode` / `SseEventType` / feature enum).
3. Run `make generate-api` and commit bundle + generated output + stamp together.
4. Extend `OpenApiCoverageTests` expected lists when routes/types change.

Adding an SSE type (closed 14-type set, Task 013):

1. Update the Task 013 contract FIRST (`SseEventTypes`, `SsePayloadPolicy`,
   `AdminSseErrorContractTests` closure assertion); emitting an unknown type
   fails serialization by design.
2. Then follow the enum checklist above (bundle `SseEventType` enum must stay
   exactly in sync; coverage test asserts the 14 values).

## Drift dry-run (CI)

`check-api-drift` fails closed on two mutations (verified in Task 014):

- Touching `openapi.v1.json` without regenerating fails on the
  `OPENAPI_VERSION` bundle-hash line before any file comparison.
- Editing any committed generated file fails with `changed: <file>`.

Re-running `make generate-api` clears both. Two consecutive generations are
byte-identical (sorted keys/operations, fixed header, LF, no timestamps).

## Rules

- Generated files carry `/* auto-generated — do not edit; run make generate-api */`; never hand-edit them.
- `.gitignore` must never exclude `frontend/src/api/generated/`.
- No secrets or real tokens in bundle examples (`Bearer eyJ` is rejected by
  the generator and the coverage test; use `"***"` placeholders).
- `servers[]` stays relative (`/api/v1`); tenant comes from the JWT `tid`.
