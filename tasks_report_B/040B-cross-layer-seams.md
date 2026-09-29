# Task 040B - Seven Named Cross-Layer Seam Specs

## Status

**COMPLETED**

## Summary

Added the seven named seam specs under `tests/cross-layer/seams/`, each tagged
`@cross-layer`, all running on the Task 040A rig with real frontend + API +
PostgreSQL + object storage + RabbitMQ and mock AI only. Every seam asserts both
sides - client state *and* a server row or artifact - and every seam covers a
named failure half; none is happy-path-only. All 24 cross-layer tests (3 harness
smoke + 21 seam assertions) pass together, repeatedly, from a clean volume via
the documented compose/playwright sequence.

Building them surfaced **one further production defect**: `IdempotencyFilter`
hashed every bound action argument including the action's `CancellationToken`,
so `System.Text.Json` walked into `CancellationToken.WaitHandle` and threw. Every
mutation endpoint taking a `CancellationToken` and called with an
`Idempotency-Key` answered `500 INTERNAL_ERROR` before its action ran - including
`POST /uploads`, which the upload seam could not get past. Fixed, with six
regression tests.

## Files Created/Modified

### Seam specs (new, the deliverable)

| File | Seam | Tests |
| --- | --- | --- |
| `tests/cross-layer/seams/seam-upload-storage.spec.ts` | frontend upload -> storage bytes -> server validation -> ready | 4 |
| `tests/cross-layer/seams/seam-processing-sse.spec.ts` | processing start -> SSE -> workspace refetch as truth | 3 |
| `tests/cross-layer/seams/seam-review-mutation.spec.ts` | review resolution -> versioned mutation + audit | 3 |
| `tests/cross-layer/seams/seam-export-download.spec.ts` | export -> signed-URL download + completeness | 3 |
| `tests/cross-layer/seams/seam-voice-invalidation.spec.ts` | voice change -> dependent invalidation + gate | 3 |
| `tests/cross-layer/seams/seam-stale-conflict.spec.ts` | stale edit -> 409 -> refresh with draft kept | 2 |
| `tests/cross-layer/seams/seam-notification.spec.ts` | backend event -> durable notification -> deep link | 3 |
| `tests/cross-layer/seams/seamContext.ts` | Shared, memoised seam context. Not a spec. | - |
| `tests/cross-layer/seams/README.md` | Seam -> owning tasks -> pass criteria map, isolation and quarantine policy. | - |

### Harness additions

| File | Purpose |
| --- | --- |
| `tests/cross-layer/harness/environment.ts` | **New.** Seed snapshot: `globalSetup` writes it, every spec reads it. Fail-closed on a missing or stale snapshot. |
| `tests/cross-layer/harness/objectStorage.ts` | **New.** Real object storage: `putPart` (through the API's pre-signed part URL), `putObject`, `getObject`, `fetchSignedUrl`. |
| `tests/cross-layer/harness/apiClient.ts` | `requestRaw` + `ApiClient.errorCode` (failure halves), `resolveSeededProjectId`, `resolveSeededPipelineProjectId`. |
| `tests/cross-layer/harness/assertArtifacts.ts` | `countOpenReviewItemsForRun` (run-scoped), `readAssignedVoiceProfileId`, more `PROJECT_LINKS`. |
| `tests/cross-layer/harness/config.ts` | `SEED.pipelineProjectId`, `CONTAINERS`, `STORAGE_*`, `SEEDED_PIPELINE_PROJECT_NAME`. |
| `tests/cross-layer/harness/seed.ts`, `harness/index.ts`, `globalSetup.ts` | Pipeline-project argument, snapshot write, new exports. |
| `tests/cross-layer/seed/Program.cs` | Second seeded project, its own anchor run, seam fixtures, verification. |
| `tests/cross-layer/docker-compose.cross.yml` | `Storage__UseSsl: false` on all three storage clients. |

### Production fix (one further defect)

| File | Change |
| --- | --- |
| `src/DubbingPlatform.Api/Filters/IdempotencyFilter.cs` | `FlattenArguments` now excludes framework-injected parameters (`CancellationToken`, `HttpContext`, `ModelStateDictionary`, `IActionResult`, `Stream`) by type and by name. |
| `src/DubbingPlatform.Api/Program.cs` | Development-only unhandled-exception logging (see Decisions). |
| `tests/DubbingPlatform.UnitTests/Api/IdempotencyFilterHashTests.cs` | **New.** 6 regression tests. |

## Decisions Made

**1. Five of the seven seams need entities the pipeline would create, and the rig cannot run the pipeline.** Re-confirmed rather than assumed: the rig's `control` container has neither `ffmpeg` nor `ffprobe`, the seeded `ContentObject` has no bytes in object storage, and a started run sits in `Pending` at `MediaValidation` for the whole session. Rather than quietly weaken the seams, the seeder creates the row each seam mutates and the seams prove the **write path across frontend, API and database**. Ingestion is explicitly *not* claimed - `seams/README.md` has a "Scope honesty" section saying so, and a seam needing pipeline completion must add the media workers and a real upload first.

**2. `IdempotencyFilter` hashed the `CancellationToken`.** `ConfigurationHashCalculator` canonicalises the bound arguments with `System.Text.Json`, which walks `CancellationToken.WaitHandle` into an `IntPtr` and throws `NotSupportedException`. The filter wrapped only the claim, not the hash, so the exception escaped as a bare `500 INTERNAL_ERROR` on every mutation endpoint with an `Idempotency-Key`. Found because the upload seam could not create an upload. Excluding it is also *semantically* right: the hash identifies the request, and a per-call token differs on every call, so hashing it would defeat replay detection entirely. Guarded by 6 tests.

**3. Added Development-only unhandled-exception logging.** `ErrorMappingMiddleware` answers `500` with a generic envelope and never surfaces the exception, so the correlation id resolves to nothing. Diagnosing the defect above required temporarily instrumenting the pipeline. The middleware is left alone (changing it would alter every error path); the log is added in `IsDevelopment()` only, because in production it would push request detail into an operator's log sink.

**4. One login per worker, memoised.** `POST /auth/login` is rate limited to 5/min per IP. A seven-seam suite that logs in per test exhausts it and fails `429` on a request unrelated to the seam - which is exactly what happened. `openSeamContext()` now memoises, which is also what R2 asks for.

**5. Run-starting seams get their own project.** A project has exactly one run slot: a start pins an active run, a second start is `409 RUN_ALREADY_ACTIVE`. The 040A smoke already starts a run on the pilot project, so `seam-processing-sse` uses a second seeded project (`SEED.pipelineProjectId`, "Cross Layer Pipeline"). This is the 040B edge case "seam passes alone but fails in full suite" - the fix is per-spec isolation, never a shared-tenant shortcut. The seeder now creates and promotes both, with per-project content keys and hashes (`ix_content_objects_tenant_id_content_hash` is unique per tenant, so two projects sharing one hash fail the second insert).

**6. No quarantine, and that is stated as policy.** R5 asks for quarantine of flakes with an owner and an issue. Nothing flaked, so there is nothing to quarantine: `playwright.config.ts` keeps `retries: 0`, no spec has a skip, and `seams/README.md` records that a flake is to be fixed rather than silenced.

**7. Verified API contracts rather than assuming them.** Several first-draft assertions were wrong about the product and were corrected to match reality, with the finding recorded in a comment:
   - `workspace.review.pendingCount` is **run-scoped**, not project-scoped. Comparing it to a project-wide review count compares two scopes and fails a green rig.
   - The review item **read** does not expose a version; `ReviewMutationResponse.Version` is a decision count returned only by mutations.
   - A resolved item cannot be resolved again (`409 REVIEW_ALREADY_RESOLVED`), so the edit path is reached via reopen -> resolve-with-edit.
   - `speaker_voice_assignments` holds one row per speaker and the API **replaces** it, so a voice change does not add a row. The assertion became a stored-pointer read, which is stronger.
   - Clearing a voice (`voiceId: null`) is **refused** (`404 VOICE_NOT_FOUND`), so the failure half asserts the refusal and that the stored pointer is untouched.
   - A declared-size mismatch is not a completion failure: the expected part count is `ceil(declared / partSize)` and `partSize` is 8 MiB, so declaring slightly more still expects one part. The failure half became "no part uploaded" (`400 UPLOAD_INCOMPLETE`).
   - Voice profile ids are returned in the public `voice_<32hex>` form, so id comparisons are normalised.

**8. Upload parts go through the pre-signed URL, not around it.** An earlier draft wrote parts with `mc cp`. `ListPartsAsync` does not see a part written any other way, so completion failed with `UPLOAD_INCOMPLETE` for a reason unrelated to the seam. `putPart` now PUTs through the URL the API issued. A consequence documented in `objectStorage.ts`: the pre-signer **always** emits `https://` (verified against AWSSDK.S3 4.0.103.1 - `UseHttp` on the SDK config is ignored by the pre-signer), and the URL's host is the compose-network name, so the scheme is rewritten for transport and the fetch runs inside the network. SigV4 covers method, path, query and Host, not the scheme, so the signature still validates - proven by the tampered-path test being refused while the untampered one serves bytes.

**9. `Storage__UseSsl: false` on all three storage clients.** Without it the pre-signed URL is `https://minio:9000/...` against a plaintext MinIO and every download fails with an OpenSSL "wrong version number" that reads like a MinIO fault.

**10. R2 respected by construction.** No spec asserts a bare per-route status matrix. Each status assertion is attached to a cross-layer claim ("a tampered path is not served", "an unknown id is indistinguishable from another unknown id"), and the 006-013 suites remain the contract coverage.

## Build/Test Results

### Documented validation sequence, from a clean volume

```
$ docker compose -f tests/cross-layer/docker-compose.cross.yml down -v
 Network dubbing-cross-layer_default Removed

$ docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
 Container dubbing-cross-layer-frontend-1 Starting
 Container dubbing-cross-layer-frontend-1 Started
UP_EXIT=0

$ npx playwright test --grep="@cross-layer"
Running 24 tests using 1 worker

  ok 1 [cross-layer-chromium] > tests\cross-layer\harness.spec.ts:53:3 > @cross-layer-harness > boots, seeds, and completes a processing-start -> SSE -> workspace round trip (2.4s)
  ok 2 [cross-layer-chromium] > tests\cross-layer\harness.spec.ts:177:3 > @cross-layer-harness > classifies a mock-AI outage with a named error instead of hanging (14ms)
  ok 3 [cross-layer-chromium] > tests\cross-layer\harness.spec.ts:204:3 > @cross-layer-harness > publishes mock-AI expectations that match the mock providers (2ms)
  ok 4 [cross-layer-chromium] > tests\cross-layer\seams\seam-export-download.spec.ts:40:3 > @cross-layer export-download > a completed export issues a signed URL whose bytes match what was stored (2.5s)
  ok 5 [cross-layer-chromium] > tests\cross-layer\seams\seam-export-download.spec.ts:135:3 > @cross-layer export-download > a tampered signed URL is refused by object storage, not served (1.5s)
  ok 6 [cross-layer-chromium] > tests\cross-layer\seams\seam-export-download.spec.ts:171:3 > @cross-layer export-download > an incomplete export refuses a download with a named code (237ms)
  ok 7 [cross-layer-chromium] > tests\cross-layer\seams\seam-notification.spec.ts:37:3 > @cross-layer notification > a durable notification is listed, counted as unread, and carries a deep link (706ms)
  ok 8 [cross-layer-chromium] > tests\cross-layer\seams\seam-notification.spec.ts:96:3 > @cross-layer notification > marking a notification read is durable and decrements the unread count (154ms)
  ok 9 [cross-layer-chromium] > tests\cross-layer\seams\seam-notification.spec.ts:141:3 > @cross-layer notification > an unknown notification is refused by name, and an unauthorised read leaks nothing (48ms)
  ok 10 [cross-layer-chromium] > tests\cross-layer\seams\seam-processing-sse.spec.ts:53:3 > @cross-layer processing-sse > a started run is reported on the stream and is the state the workspace serves (2.7s)
  ok 11 [cross-layer-chromium] > tests\cross-layer\seams\seam-processing-sse.spec.ts:145:3 > @cross-layer processing-sse > a second start while a run is active is refused with the active run named (352ms)
  ok 12 [cross-layer-chromium] > tests\cross-layer\seams\seam-processing-sse.spec.ts:180:3 > @cross-layer processing-sse > an unauthenticated stream request is refused and leaks no run detail (6ms)
  ok 13 [cross-layer-chromium] > tests\cross-layer\seams\seam-review-mutation.spec.ts:41:3 > @cross-layer review-mutation > resolving a review records a versioned decision, a new version and the new state (3.4s)
  ok 14 [cross-layer-chromium] > tests\cross-layer\seams\seam-review-mutation.spec.ts:157:3 > @cross-layer review-mutation > refuses a stale resolve and does not advance the stored decision (882ms)
  ok 15 [cross-layer-chromium] > tests\cross-layer\seams\seam-review-mutation.spec.ts:188:3 > @cross-layer review-mutation > refuses a read of a review the caller cannot see, without leaking it exists (39ms)
  ok 16 [cross-layer-chromium] > tests\cross-layer\seams\seam-stale-conflict.spec.ts:36:3 > @cross-layer stale-conflict > a stale edit is refused with 409, the winner stands, and the draft is still available (1.1s)
  ok 17 [cross-layer-chromium] > tests\cross-layer\seams\seam-stale-conflict.spec.ts:180:3 > @cross-layer stale-conflict > an empty edit is refused before any version is consumed (78ms)
  ok 18 [cross-layer-chromium] > tests\cross-layer\seams\seam-upload-storage.spec.ts:47:3 > @cross-layer upload-storage > bytes written by the client are the bytes the server accepted and stored (2.1s)
  ok 19 [cross-layer-chromium] > tests\cross-layer\seams\seam-upload-storage.spec.ts:163:3 > @cross-layer upload-storage > refuses a completion while a part is still missing, and leaves no usable session (780ms)
  ok 20 [cross-layer-chromium] > tests\cross-layer\seams\seam-upload-storage.spec.ts:218:3 > @cross-layer upload-storage > aborts a session so a later completion of it is refused (983ms)
  ok 21 [cross-layer-chromium] > tests\cross-layer\seams\seam-upload-storage.spec.ts:264:3 > @cross-layer upload-storage > refuses a read of an upload the caller is not authorised for, without leaking it exists (381ms)
  ok 22 [cross-layer-chromium] > tests\cross-layer\seams\seam-voice-invalidation.spec.ts:39:3 > @cross-layer voice-invalidation > assigning a different voice invalidates the dependent output and is persisted (1.6s)
  ok 23 [cross-layer-chromium] > tests\cross-layer\seams\seam-voice-invalidation.spec.ts:142:3 > @cross-layer voice-invalidation > re-assigning the same voice changes nothing and does not add a row (504ms)
  ok 24 [cross-layer-chromium] > tests\cross-layer\seams\seam-voice-invalidation.spec.ts:173:3 > @cross-layer voice-invalidation > refuses an unknown speaker, and refuses to clear the voice pointer (278ms)

  24 passed (1.3m)
TEST_EXIT=0

$ docker compose -f tests/cross-layer/docker-compose.cross.yml down
 Network dubbing-cross-layer_default Removed
DOWN_EXIT=0
```

### Repeatability

Four consecutive `npx playwright test --grep="@cross-layer"` runs, all `24 passed`
(1.0m / 1.1m / 1.2m / 1.3m), with no manual intervention and no retry. The last was
from a clean volume after `down -v`.

### Backend unit suite (includes the 6 new idempotency guards)

```
$ dotnet test tests/DubbingPlatform.UnitTests/DubbingPlatform.UnitTests.csproj -v q --nologo
Passed!  - Failed:     0, Passed:  2998, Skipped:     0, Total:  2998, Duration: 47 s
```

2992 after 040A, +6 from `IdempotencyFilterHashTests`.

### Frontend and coverage gates

```
$ npm --prefix frontend run lint        > eslint . --max-warnings=0      LINT_EXIT=0
$ npm --prefix frontend run typecheck   > tsc --noEmit -p tsconfig.json TS_EXIT=0
$ node scripts/coverage-gap.mjs            (frontend)  exit 0, empty report
$ node scripts/coverage-gap.mjs --backend  (backend)   exit 0, empty report
```

`globalSetup` additionally runs the API drift check, the frontend typecheck and
`vite build --mode cross-layer` on every run; a failure in any aborts the run with
the step named.

### Integration suite: 38 pre-existing failures, still not ours

The Auth/Project filter was re-run to confirm the 040B production change
introduced no regression. 040A measured `38 failed / 14 passed / 52 total` both
with and without its own fixes. 040B re-ran the same filter and obtained the same
38/14/52. The `IdempotencyFilter` change touches only the request-hash
construction, and the failure set is unchanged - these remain pre-existing and
undiagnosed, exactly as 040A reported.

## Recommendations for Next Agent (041A)

### Current repo state

- The cross-layer slice is complete: 7 seam files, 21 seam assertions, plus the
  3-test 040A harness smoke, all green in one `npx playwright test
  --grep="@cross-layer"` run. Start with
  `tests/cross-layer/seams/README.md` (the map) and `tests/cross-layer/README.md`
  (the rig runbook).
- Docker Desktop must be running:
  `& "$env:LOCALAPPDATA\Programs\DockerDesktop\Docker Desktop.exe"`, then wait
  ~20-30s for `docker info` to answer.
- Working tree is clean apart from `master-prompt.md`, a pre-existing scratch
  file left uncommitted on purpose.

### The three production fixes now in place - do not reintroduce

1. **`IdempotencyFilter` must keep excluding framework-injected arguments.** The
   bug made every mutation endpoint with an `Idempotency-Key` return
   `500 INTERNAL_ERROR`; guard is `IdempotencyFilterHashTests` (6 tests).
2. **`JwtBearerOptions.MapInboundClaims` must stay `false`** (040A). Guard:
   `JwtInboundClaimMappingTests`.
3. **`ValidationRegistration` must keep `ProjectProcessingSettingsValidator` out
   of auto-registration** (040A). It is an `AbstractValidator<string>`, so
   auto-registration applied it to every string route/header parameter. Guard:
   `ValidationRegistrationTests` (4 tests, no Docker, never skip).
4. **The frontend CSP must keep `http://127.0.0.1:*` in `connect-src`** (040A).

### Gotchas that will cost you a run each

1. **Never `localhost`.** Use `127.0.0.1` for every rig URL. Docker Desktop
   completes an IPv6 handshake to a published port and then resets it, and
   Chromium and Node resolve `localhost` to `::1` first.
2. **`openSeamContext()` is memoised on purpose.** `POST /auth/login` is rate
   limited to 5/min per IP. Log in per test and a suite dies on `429` on an
   unrelated request.
3. **A project has one run slot.** A start pins an active run; a second start is
   `409 RUN_ALREADY_ACTIVE`. Two seeded projects exist: the pilot
   (`SEED.projectId`, holds all fixtures) and the pipeline
   (`SEED.pipelineProjectId`, fixture-free, used by the processing seam). Add a
   third rather than sharing one.
4. **Upload parts must go through the pre-signed URL** (`putPart`). A part
   written with `mc cp` does not exist to `ListPartsAsync`, and completion fails
   `UPLOAD_INCOMPLETE` for an unrelated reason.
5. **The pre-signer always emits `https://`** and the URL's host is the
   compose-network name. `fetchSignedUrl` rewrites the scheme (SigV4 does not
   cover the scheme) and fetches from inside the network. `mc pipe` deadlocks -
   use `docker cp` + `mc cp`.
6. **Several API contracts differ from the obvious guess** - each is documented
   in a comment where it is used, and summarised in
   `seams/README.md`: run-scoped `review.pendingCount`; review reads carry no
   version; a resolved item cannot be resolved again; voice assignments are
   replaced in place; clearing a voice is refused; a declared-size mismatch does
   not fail completion.
7. **Three id shapes.** REST public (`prj_` + 32 hex), SSE envelope raw 32-hex,
   seeder dashed. Use `sameId()` / `toUuid()`.
8. **Windows spawns:** no `npm` with `shell: false`; use `process.execPath` with
   argument arrays. `spawn` reports a missing working directory as
   `ENOENT <command>`, which points at the wrong thing.

### Incomplete integration points and open items

- **The rig still cannot run the pipeline.** No `ffmpeg`/`ffprobe` in the worker
  images, no real media in object storage, so a run sits in `Pending` at
  `MediaValidation`. Five of the seven seams therefore test a *mutation over
  seeded state*, not ingestion. Adding the media workers plus a real upload
  unblocks pipeline-completion seams; until then, do not write one that needs
  finished segments or a rendered export.
- **The integration suite still has 38 pre-existing failures** in the
  Auth/Project filter, unchanged and undiagnosed. Highest-value follow-up, and
  the same warning as 040A applies: take a baseline before assuming your change
  caused a red run.
- **Task 046 is still unowned** and 040A/040B depend on it: fixtures, reset,
  auth seeds, the log scrubber, per-worker tenant isolation. 040B added a second
  seeded project and memoised auth as a stopgap; when 046 lands,
  `harness/seed.ts`, `harness/environment.ts` and `seed/Program.cs` are the files
  to retire in favour of it. Per-**worker** (not per-project) isolation is still
  outstanding - `playwright.config.ts` pins `workers: 1`, so the seams are
  serialised rather than isolated.
- **No log scrubbing.** `tests/cross-layer/.artifacts/` holds unscrubbed traces,
  screenshots and the seed snapshot (which contains ids only, no secrets). 046
  owns the scrubber; 042B must apply it before any CI attach.
- **No CI wiring** (042B). `playwright.config.ts` is CI-shaped (`forbidOnly` under
  `CI`, `github` reporter) but nothing invokes it and no service containers are
  provisioned.
- **`/health/ready` still fails** on this stack (RabbitMQ/Redis checks registered,
  no Redis service). The harness probes `/health/live` only.
- **Error diagnosis is still weak outside Development.** The new
  unhandled-exception log is `IsDevelopment()`-gated. A 500 in production still
  resolves only to a correlation id. Worth a follow-up that logs the exception
   type and a redacted message at `Error`, without request bodies.

### Naming and config conventions

- Spec tags: `@cross-layer-harness` (040A), `@cross-layer <name>` (040B, one
  suite per seam), `@cross-layer-ai` (reserved for a non-default mock scenario).
- One file per seam in `tests/cross-layer/seams/`, importing only from
  `../harness/index.js`. Suites are `test.describe.serial` within a file.
- New DB assertions go in `PROJECT_LINKS` in `assertArtifacts.ts`; a table with
  no declared link is refused rather than interpolated into SQL.
- Secrets stay `CHANGE_ME`. All non-ASCII punctuation in the files touched here
  is normalised to ASCII, because PowerShell's console mangles it and a mangled
  comment in a source file is worse than an ASCII one.
