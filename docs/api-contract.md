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

## Entity contracts drifted from the plan (GAP-027)

Two plan examples disagreed with the shipped implementation. The implementation
is authoritative; the plan examples were corrected in
`implementation_plan-B.md` and are pinned by
`UnitTests/Docs/ContractDocDriftTests`.

- `ProjectProcessingSettings.reviewThreshold` — the plan example showed the enum
  string `"Default"`. The shipped validator
  (`Application/Validation/ProjectProcessingSettingsValidator.HaveValidShape`)
  requires a **number** in the inclusive range `[0,1]` (`null`/absent = product
  default). A string value is rejected, not coerced.
- `VoicePreviewJob` — the plan listed `VoiceProfileId`, `RequestedText`,
  `FailureCategory`, and `ExpiresAt`. None of those columns exist.

### VoicePreviewJob

Table `voice_preview_jobs` (`Domain/Entities/VoicePreviewJob.cs`). Field-for-field
with the entity:

| Field | Notes |
|---|---|
| `Id` | `vpv_` public ID |
| `TenantId` | tenant scope (RLS) |
| `ProjectId` | owning project |
| `SpeakerId` | speaker the preview targets |
| `VoiceId` | resolved voice id, **not** a profile id (plan said `VoiceProfileId`) |
| `Text` | requested preview text (plan said `RequestedText`) |
| `Status` | `Pending`/`Running`/`Completed`/`Failed`/`Cancelled` |
| `RequestedByUserId` | actor evidence for the audit trail |
| `IdempotencyKey` | nullable; replay key for preview requests |
| `QuotaCheck` | quota verdict recorded at request time |
| `QuotaCheckReason` | nullable; why the quota verdict was what it was |
| `ConsentState` | voice-consent state (cloning consent gate) |
| `ProviderExecutionId` | nullable; provider call that synthesized the preview |
| `ArtifactId` | nullable; preview artifact (separate from final generated audio) |
| `ErrorCode` | nullable; failure code (plan said `FailureCategory`) |
| `ErrorMessage` | nullable; failure detail |
| `CreatedAt` | request time |
| `StartedAt` | nullable; worker pickup |
| `CompletedAt` | nullable; terminal time |
| `IsTerminal` | computed (`Status is Completed/Failed/Cancelled`); not a column |

There is no per-job expiry column: preview artifacts follow the standard
retention/hold path (`RetentionService`), so nothing is deleted on a job timer.

## Admin scope reads and deployment-only surfaces (GAP-024)

Plan B §12.19 lists the operator admin areas. The read side of each is now
provisioned behind the same elevated gate as the diagnostics reads
(`admin.manage` or `diagnostics.view`; JWT `TenantAdmin`/`Service`):

| Route | Returns | Notes |
|---|---|---|
| `GET /admin/tenants` | `{ id, name, slug }[]` | Caller's tenant only; admin reads never cross tenants, so the list has at most one entry. |
| `GET /admin/users` | `{ id, displayName, roles[] }[]` | Roles come from `project_memberships`. No emails, external subjects, or credential state. |
| `GET /admin/retention` | `{ policies: [{ scope, retentionDays, description }] }` | Effective windows from configuration; hold state and sweeper decisions are never exposed. |
| `GET /admin/feature-flags` | `{ flags: [{ key, enabled, description, frozen }] }` | Read-only. Optional capabilities stay disabled by default. |
| `GET /admin/audit-events` | `{ items, page, pageSize, total, hasMore }` | Newest first; `details_json` payloads are never returned. |

Every read is tenant-scoped, `AsNoTracking`, audited as `admin.access`, and
returns ids, names, counts, and timestamps only.

### Explicitly out of scope for this deployment

Two surfaces stay unprovisioned **by decision**, recorded here rather than left
as an open placeholder. The frontend keeps its 404-tolerant `EmptyState` /
`NotAvailableState` path so a future deployment can add the route without a
frontend change:

- **Local-GPU device health** (`GET /admin/local-gpu`). Device inventory —
  accelerator model, revision, per-device latency — is node-level infrastructure
  telemetry with no product consumer: nothing in the pipeline reads it, and no
  operator action in the product depends on it. Exposing it would add an
  infrastructure-coupling route for zero product capability. The
  `Features:LocalInferenceEnabled` flag remains the operator-visible switch.
- **Enrichment runtime reads** (video-intelligence / lip-sync run inspection).
  Enrichment is a future-ready extension point (Plan A §19): the capability
  flags exist and stay `false` by default, but the stages are not part of the
  32-endpoint v1 contract, so a runtime-inspection read has no contract to hang
  off. When enrichment stages are provisioned, their read surfaces come with
  them.

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
