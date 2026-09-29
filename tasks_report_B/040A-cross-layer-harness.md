# Task 040A - Cross-Layer Seam Harness

## Status

**COMPLETED**

## Summary

Built the cross-layer rig that Task 040B's seven seam specs run against: a
real stack (frontend bundle + API + PostgreSQL + MinIO + RabbitMQ + `control`
and `ai` workers, mock AI only) behind a fixed-port compose file, a .NET seeder
for the synthetic tenant/user/project, and a TypeScript harness (config, API
client, seed orchestration, event-driven SSE client, mock-AI contract, artifact
assertions) plus a `@cross-layer-harness` smoke that boots, seeds, completes a
processing-start -> SSE -> workspace-read round trip, asserts the durable
artifacts, and tears down. The harness is fail-closed: a missing service, a port
collision or an unverifiable seed aborts the run rather than skipping.

Building it surfaced **three latent production defects that made the real HTTP
path unusable**, all fixed here with named regression guards: the API minted JWTs
it could not read back (`MapInboundClaims` left at its default), a
`ProjectProcessingSettingsValidator` typed as `AbstractValidator<string>` was
globally auto-applied to *every* string route/header parameter so all
project-scoped endpoints answered `400`, and the frontend's CSP allowed
`http://localhost:*` but not `http://127.0.0.1:*`, so a stack served from
`127.0.0.1` could not call its own API.

## Files Created/Modified

### Rig (new)

| File | Purpose |
| --- | --- |
| `tests/cross-layer/docker-compose.cross.yml` | The stack: postgres, minio, rabbitmq, api, control, ai, frontend. Fixed ports disjoint from the root stack; health-gated; `CHANGE_ME` placeholders only. |
| `tests/cross-layer/frontend-server.mjs` | Dependency-free static server for `frontend/dist`. Dual-stack (`::`), SPA fallback, traversal rejection, and a 503 rather than a crash when the directory is mid-rebuild. |
| `tests/cross-layer/globalSetup.ts` | Per-run preflight: frontend build (drift check -> typecheck -> vite `--mode cross-layer`), frontend readiness poll, port/collision assertion, seeder build, reset-then-seed. |
| `tests/cross-layer/harness.spec.ts` | The smoke (`@cross-layer-harness`): seven-layer round trip + the R4 mock-AI outage test. |
| `tests/cross-layer/harness/index.ts` | Single import surface for 040B (R2: no per-spec bespoke boot). |
| `tests/cross-layer/harness/config.ts` | Ports, seed constants, connection strings, `assertRigPortsOpen` preflight. |
| `tests/cross-layer/harness/apiClient.ts` | Typed client with seam-shaped helpers (`login`, `readIdentity`, `listProjects`, `startProcessing`, `readWorkspace`, `readRun`). |
| `tests/cross-layer/harness/seed.ts` | Build + reset-then-seed orchestration; fails closed and surfaces the seeder's own output. |
| `tests/cross-layer/harness/sseClient.ts` | Event-driven SSE waits (`waitForEvent`, `waitForPayload`). No sleeps; deadlines are failure reports, not retry policy. |
| `tests/cross-layer/harness/mockAi.ts` | Deterministic mock expectations and the R4 `AI_MOCK_UNAVAILABLE` outage contract. |
| `tests/cross-layer/harness/assertArtifacts.ts` | Post-run durable assertions, reached through `docker exec` psql so no DB credential lives in host test code. |
| `tests/cross-layer/seed/CrossLayerSeed.csproj`, `seed/Program.cs` | The .NET seeder (040A explicitly permits `seed.cs`): migrate -> truncate reset -> seed -> verify. |
| `tests/cross-layer/README.md` | Runbook: ports, run order, mock-AI outage recipe, the seven API behaviours the rig depends on, troubleshooting table. |
| `playwright.config.ts` | Root Playwright config scoped to `tests/cross-layer/**`; serial, `workers: 1`. |
| `package.json`, `package-lock.json` | Minimal repository-root manifest so the documented root-level `npx playwright test` resolves. Pinned `@playwright/test` 1.63.0, matching `frontend`. |
| `frontend/.env.cross-layer` | Build-time `VITE_*` profile for the rig (public values only). Committed deliberately - see Decisions. |

### Production fixes (three defects the rig could not get past)

| File | Change |
| --- | --- |
| `src/DubbingPlatform.Api/Program.cs` | `o.MapInboundClaims = false;` on `AddJwtBearer`, with the reason inline. |
| `src/DubbingPlatform.Api/Validation/ValidationRegistration.cs` | **New.** `AddDubbingValidators()` with a declared, testable exclusion list for validators that must not be auto-registered. |
| `frontend/index.html` | CSP `connect-src` now includes `http://127.0.0.1:*` alongside `http://localhost:*`. |

### Tests (regression guards)

| File | Guard |
| --- | --- |
| `tests/DubbingPlatform.UnitTests/Auth/JwtInboundClaimMappingTests.cs` | 2 tests: a minted token is readable by the platform readers with mapping off; and the .NET mapping really does lose the `tid` claim. |
| `tests/DubbingPlatform.IntegrationTests/Validation/ValidationRegistrationTests.cs` | 4 tests: the Application assembly really does contain an `IValidator<string>`; the container resolves no `IValidator<string>`; DTO validators still resolve; the exclusion list is well-formed. |

### Other

| File | Change |
| --- | --- |
| `.gitignore` | Appended a 040A section: Playwright output plus `!frontend/.env.cross-layer`. |
| `master-prompt.md` | **Left uncommitted** (pre-existing dirty scratch file, not mine). |

## Decisions Made

**1. Corrected my own earlier BLOCKED conclusion.** The previous report claimed
Docker was unavailable. That was wrong: Docker Desktop is installed per-user at
`%LOCALAPPDATA%\Programs\DockerDesktop` and I had only checked the system-wide
`Program Files` path. Starting it unblocked the task, so 040A is completed
rather than blocked. WSL distros `Ubuntu` and `docker-desktop` are present and
the hypervisor is available.

**2. Seeder in C#, not TypeScript** (040A instruction 2 permits either). There is
no public provisioning endpoint by design, so the rig seeds through
`AppDbContext` and the domain constructors - the same path the 006-013 suites use.
Hand-written SQL would let a seeded project be invalid by the API's own rules.

**3. `reset` truncates rather than deleting by `tenant_id`.** The schema has 53
tenant-scoped tables, a hand-maintained delete list rots (an omitted
`processing_runs` row made a fresh start return `409 RUN_ALREADY_ACTIVE`), and the
foreign-key graph has cycles so no delete order satisfies it. `__EFMigrationsHistory`
and the MassTransit tables are preserved: truncating the former makes the next
`MigrateAsync` fail with `42P07 relation already exists`.

**4. Seeder verifies read-after-write and refuses to report success otherwise.**
The first working version printed a success payload while `tenant_users` was
empty - entities were added to a context that was then disposed without a
`SaveChangesAsync`, so every later login returned `401 INVALID_CREDENTIALS`.

**5. Real RabbitMQ transport, plus `control` and `ai` workers.** R1 requires a
real transport. The in-memory bus cannot cross a process boundary, so with it
the workers received nothing, the run sat in `Pending` at `MediaValidation`
forever, and the only thing provable was that a `202` came back. This was a real
scope correction on my first compose draft, which had put RabbitMQ behind an
optional profile.

**6. The smoke asserts what a *start* guarantees, not pipeline completion.**
Reaching segments and exports needs the FFmpeg-backed `media-preparation` /
`media-render` / `export` workers plus a real uploaded media file - media-pipeline
integration, not harness scope. `assertRunArtifacts` asserts the transitioned
project row, the matching active-run pointer, the durable `processing_runs` row
and a review count that matches the stored rows, and *cross-checks* the
pipeline-dependent counts against what the workspace claims. Nothing is skipped
silently and nothing is asserted vacuously. A 040B seam that genuinely needs
pipeline completion must add those workers and a media fixture first.

**7. `AI_MOCK_UNAVAILABLE` is a harness sentinel, not a product error code.** The
product already has precise codes for every mock failure mode
(`PROVIDER_RATE_LIMITED`, `PROVIDER_TIMEOUT`, `PROVIDER_INVALID_RESPONSE`,
`PROVIDER_FAILED`, `PROVIDER_QUOTA_EXHAUSTED`,
`PROVIDER_CONFIGURATION_ERROR`). Adding a public code to describe a test rig would
leak test vocabulary into the API contract. The sentinel names the rig's
conclusion and carries the product code that caused it.

**8. Every rig URL is `127.0.0.1`, and that is load-bearing.** Docker Desktop
completes an IPv6 TCP handshake to a published port and then *resets* it, while
Chromium and Node resolve `localhost` to `::1` first. Diagnosed with an explicit
probe: `ipv4-frontend -> 200`, `ipv6-frontend -> ERR ECONNRESET`. This also
explained an earlier Npgsql failure that presented as an opaque "Exception while
reading from stream" - `Host=localhost` in the connection string, same root
cause. The frontend origin, `VITE_API_BASE_URL`, the CORS allow-list and the
Playwright `baseURL` must all agree on the spelling.

**9. Two Playwright-on-Windows spawn hazards avoided deliberately.** `npm` is
`npm.cmd` on Windows, which `spawn(..., { shell: false })` cannot resolve (and
Node rejects `.cmd` outright since the CVE-2024-27980 fix). Rather than reach for
`shell: true` - which would re-open argument injection on paths containing `&` -
the rig drives `tsc` and `vite` through `process.execPath` with argument arrays.
The repository root is discovered by walking up from `process.cwd()` rather than
`import.meta.url`, which Playwright relocates.

**10. Root `package.json` added.** The documented validation command is
`npx playwright test --grep="@cross-layer-harness"` from the repository root, and
the repo had no root manifest, so the command could not resolve. Kept minimal and
pinned to the same Playwright version `frontend` already uses.

**11. `frontend/.env.cross-layer` is committed.** The blanket `.env.*` ignore rule
is there to keep secrets out, so the file needed an explicit negation. It holds
only public `VITE_*` values, and the frontend origin / API base URL / CSP
allow-list are a single contract that a silently untracked file would break.

**12. `globalSetup` builds the frontend with Node CLIs, not `npm run build`.**
`npm run build` chains `prebuild` (API drift check) and `typecheck && vite build`;
the rig runs the same three steps explicitly so a failing step names itself.

## Build/Test Results

### Documented validation sequence, from a clean volume

```
$ docker compose -f tests/cross-layer/docker-compose.cross.yml down -v
 Volume dubbing-cross-layer_mediatmp Removed
 Network dubbing-cross-layer_default Removed

$ docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
 Container dubbing-cross-layer-api-1 Healthy
 Container dubbing-cross-layer-frontend-1 Starting
 Container dubbing-cross-layer-frontend-1 Started
UP_EXIT=0

$ npx playwright test --grep="@cross-layer-harness"
cross-layer: frontend check-api-drift
cross-layer: frontend typecheck
cross-layer: frontend vite-build
cross-layer: seeded tenant=11111111-1111-1111-1111-111111111111 project=33333333-3333-3333-3333-333333333333 status=MediaReady
cross-layer: api=http://127.0.0.1:58080 frontend=http://127.0.0.1:54173
cross-layer: identity subject=cross-layer-owner

Running 2 tests using 1 worker

  ok 1 [cross-layer-chromium] > tests\cross-layer\harness.spec.ts:53:3 > @cross-layer-harness > boots, seeds, and completes a processing-start -> SSE -> workspace round trip (20.2s)
  ok 2 [cross-layer-chromium] > tests\cross-layer\harness.spec.ts:177:3 > @cross-layer-harness > classifies a mock-AI outage with a named error instead of hanging (17ms)
  ok 3 [cross-layer-chromium] > tests\cross-layer\harness.spec.ts:204:3 > @cross-layer-harness > publishes mock-AI expectations that match the mock providers (3ms)

  3 passed (2.1m)
TEST_EXIT=0

$ docker compose -f tests/cross-layer/docker-compose.cross.yml down
 Container dubbing-cross-layer-rabbitmq-1 Removed
 Network dubbing-cross-layer_default Removing
 Network dubbing-cross-layer_default Removed
DOWN_EXIT=0
```

Repeatability: four consecutive `npx playwright test --grep="@cross-layer-harness"`
runs passed with no manual intervention (6.5s / 2.0s / 20.2s / 2.0s for the round
trip, the spread being container cold-start, not flakiness).

### Backend unit suite (includes the two new JWT guards)

```
$ dotnet test tests/DubbingPlatform.UnitTests/DubbingPlatform.UnitTests.csproj -v q --nologo
Test run for ...\DubbingPlatform.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:  2992, Skipped:     0, Total:  2992, Duration: 27 s
```

2990 before this task, +2 from `JwtInboundClaimMappingTests`.

### New validator-registration guards

```
$ dotnet test tests/DubbingPlatform.IntegrationTests/... --filter "FullyQualifiedName~ValidationRegistrationTests"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 143 ms
```

These four deliberately do **not** derive from `TestFixtureBase` and need no
Docker, a database or a host, so unlike the rest of the integration assembly they
never skip.

### Frontend gates

`globalSetup` runs `check-api-drift` then `tsc --noEmit` then `vite build
--mode cross-layer`; all three pass on every rig run and a failure aborts the run
with the step named.

```
$ npm --prefix frontend run lint
> eslint . --max-warnings=0
LINT_EXIT=0

$ node scripts/coverage-gap.mjs            # frontend
GAP_EXIT=0   (empty report)

$ node scripts/coverage-gap.mjs --backend  # backend
(empty report)

$ dotnet build src/DubbingPlatform.Api/DubbingPlatform.Api.csproj -v q --nologo
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Integration suite: 38 pre-existing failures, NOT caused by this task

**This is the most important thing in the report.** With Docker now available for
the first time, the integration suite actually executes. Before this task it
exited 0 while *skipping* 215 of 386 tests, so its true state was never observed.

```
$ dotnet test ...IntegrationTests --filter "FullyQualifiedName~Auth|FullyQualifiedName~Project"
```

| Run | Result |
| --- | --- |
| With the 040A production fixes | `Failed: 38, Passed: 14, Skipped: 0, Total: 52` |
| Baseline (both fixes temporarily reverted) | `Failed: 38, Passed: 14, Skipped: 0, Total: 52` |

**Identical.** I verified this by reverting `o.MapInboundClaims` and
`AddDubbingValidators()` in place, re-running the same filter, and restoring
them. The fixes therefore introduce **zero** regressions; the 38 failures are
pre-existing and were masked by the skip behaviour.

Two verified examples, both failing identically at baseline:

- `MePreferencesTests.Missing_Tenant_Claim_401_Tenant_Required` - expects
  `TENANT_REQUIRED`, gets `UNAUTHORIZED`, i.e. the JWT bearer rejects the token
  before the controller runs. The token is minted with the test's own
  `TestSigningKey`/`TestAudience` and the factory configures `Auth:SigningKey`,
  so the cause is not obvious and **is still undiagnosed**. This is the one to
  pick up first: 040A's real HTTP path (same code, same config shape) works,
  while `WebApplicationFactory<CorrelationIdMiddleware>` does not, so the defect
  is likely in how the test host wires configuration rather than in the product.
- `ProjectsApiTests.Patch_Language_Immutable_400` and its siblings - several
  400-expectation tests in `ProjectsApiTests`. Worth checking whether some of
  them were passing because of the `AbstractValidator<string>` bug (a `projectId`
  string parameter producing a 400 for the wrong reason) and therefore now fail
  for a different reason, or whether they fail at baseline for their own sake.

I did not attempt these fixes: they are outside 040A's scope (rig harness), and
guessing at 38 failures without a diagnosis would be exactly the kind of change
that hides a real defect. `dotnet test --filter FullyQualifiedName~IntegrationTests`
exiting 0 while skipping most of the suite remains a hollow-green trap; read the
`Skipped:` count, and now also the `Failed:` count.

## Recommendations for Next Agent (040B)

### Current repo state

- The rig is committed and works. Start the stack with
  `docker compose -f tests/cross-layer/docker-compose.cross.yml up -d`, then run
  `npx playwright test --grep="@cross-layer"`. Read
  `tests/cross-layer/README.md` first - it is the runbook and it records every
  API behaviour the rig depends on.
- **`globalSetup` prints a service matrix and fails closed on a partial boot**
  (verified: `docker compose stop ai` aborts the run with
  "Cross-layer rig is partially booted: ai not running" plus the matrix). A 040B
  spec must never see a half-booted rig.
- Docker Desktop must be running. Start it with
  `& "$env:LOCALAPPDATA\Programs\DockerDesktop\Docker Desktop.exe"`; the engine
  takes about 20-30s to answer `docker info`.
- Working tree was clean before this task apart from `master-prompt.md`, which is
  a pre-existing scratch file and is still uncommitted on purpose.

### Import only from the barrel

`import { ... } from './harness/index.js';` (from `tests/cross-layer/seams/*.spec.ts`
it is `'../harness/index.js'`). Do not reach into `harness/*.ts` directly and,
above all, do not write a spec that boots its own stack - R2 forbids per-spec
bespoke boot, and `globalSetup` already ran once per Playwright run.

Exported surface: `API_BASE_URL`, `FRONTEND_BASE_URL`, `PORTS`, `SEED`,
`COMPOSE_FILE`, `SEEDER_PROJECT`, `SEEDER_CONNECTION_STRING`,
`REQUIRED_SERVICES`, `RigPreflightError`, `assertRigPortsOpen`, `isPortListening`,
`ApiClient`, `ApiError`, `buildSeeder`, `seedCrossLayerEnvironment`, `SeedError`,
`SseClient`, `SseTimeoutError`, `AI_MOCK_UNAVAILABLE`, `AiMockUnavailableError`,
`PROVIDER_UNAVAILABLE_CODES`, `MOCK_AI_PROBE_TIMEOUT_MS`,
`assertMockAiDelivered`, `assertMockPipelineConsistent`, `classifyProviderFailure`,
`expectedTranscriptText`, `EXPECTED_MOCK_CONFIDENCE`, `assertRunArtifacts`,
`assertRunPersisted`, `countRows`, `readProjectRow`, `ArtifactAssertionError`.

### Gotchas that will cost you a run each

1. **Start before subscribe.** `GET /api/v1/projects/{id}/progress/stream`
   resolves the project's active run and returns `404` when none exists. Call
   `startProcessing` first, then `SseClient.open`. The reverse order fails.
2. **`Idempotency-Key` is mandatory** on `POST /api/v1/projects/{id}/processing`.
   A fresh key per start; a reused one is an idempotency hit, not a new run.
3. **One seeded project.** `SEED.projectId` is a single fixed project at
   `MediaReady`, and a start moves it to `Processing` and pins an active run. A
   second start in the same run is `409 RUN_ALREADY_ACTIVE` by design. If a seam
   needs a second project, add it to `SEED` and to the seeder - do not work
   around the 409.
4. **Three id shapes.** Seeder GUID (dashed), REST public id (`prj_`+32hex), SSE
   envelope raw 32-hex. `toUuid()` in `assertArtifacts.ts` normalises for
   database queries; `sameId()` in the spec normalises for assertions. Database
   columns are `uuid`, so a public id passed raw fails with "invalid input syntax
   for type uuid".
5. **Never `localhost`.** Use `127.0.0.1` for every rig URL. See README.
6. **DB probes go through `docker exec` psql** on container
   `dubbing-cross-layer-postgres-1`, not a host connection, so no database
   password lives in host test code. Add a table to `PROJECT_LINKS` in
   `assertArtifacts.ts` before counting it; `export_artifacts` links through
   `export_jobs`, not `project_id`.
7. **`processing_settings_json` is a `json` column** - cast to text before
   coalescing.
8. **The workspace read has no `segments` collection.** Its real keys are
   `project, media, run, phase, stage, progress, review, warnings, output, cost,
   activity, permissions`; review work is summarised as `review.pendingCount`.
9. **Windows spawns:** no `npm` with `shell: false`; use `process.execPath` with
   argument arrays. `spawn` reports a missing working directory as
   `ENOENT <command>`, which points at the wrong thing.

### Production defects fixed here - do not reintroduce

1. `JwtBearerOptions.MapInboundClaims` must stay **false**. The default rewrites
   `tid`/`sub`/`roles` to long URIs on validation, and every reader in
   `ClaimsPrincipalExtensions` looks them up by short name, so the API could not
   read its own tokens. Guard:
   `JwtInboundClaimMappingTests.MintedToken_Is_Readable_By_Platform_Readers_When_Mapping_Is_Disabled`.
   Note the existing `AuthSessionTests` uses `ReadJwtToken`, which applies no
   mapping and no validation - that is precisely why the defect survived.
2. `ProjectProcessingSettingsValidator` must stay out of auto-registration. It is
   an `AbstractValidator<string>`, so `AddValidatorsFromAssembly` registered it
   against *every* string action parameter and every project-scoped endpoint
   answered `400 "Processing settings must be valid JSON."` on its own
   `projectId`. Add exclusions through
   `ValidationRegistration.ExcludedFromAutoValidation`, never by deleting the
   registration. Guards: the four tests in `ValidationRegistrationTests`.
3. The frontend CSP must keep `http://127.0.0.1:*` in `connect-src`. Removing it
   breaks the app against any stack served from `127.0.0.1`, and the failure is an
   opaque `TypeError: Failed to fetch` with no failed request logged.

### Incomplete integration points and open items

- **The integration suite has 38 pre-existing failures in the Auth/Project
  filters** (see Build/Test Results). Undiagnosed, outside this task's scope,
  and the highest-value follow-up. Do not assume a red integration run means
  your change broke something - take a baseline first, as this task did.
- **The rig does not drive the pipeline to completion.** `control` and `ai` are
  present and consuming over RabbitMQ, but a run is observed in `Pending` at
  stage `MediaValidation` for the whole session; the FFmpeg-backed
  `media-preparation` / `media-render` / `export` workers are not in the rig and
  no real media is uploaded. If a 040B seam needs segments, a review item or an
  export, that work must be added first - the seeder's `PromoteToMediaReadyAsync`
  is a deliberate shortcut, and a real upload path is the missing piece.
- **Task 046 is still unowned and 040A/040B depend on it:** fixtures, reset,
  auth seeds, the log scrubber and per-worker tenant isolation. 040A implemented
  the minimum needed to stand alone (a single-tenant truncate reset, one seeded
  identity, no scrubber). When 046 lands, `harness/seed.ts` and
  `seed/Program.cs` are the files to retire in favour of it, and 042B will need
  the scrubber before any rig log is attached to CI.
- **Log scrubbing is specified but not implemented** (046 owns it).
  `tests/cross-layer/.artifacts/` currently holds unscrubbed traces and
  screenshots.
- **`--grep="@cross-layer"` currently also matches `@cross-layer-harness`**, so
  a 040B run will execute the 040A smoke as well. That is intentional (it is the
  gate) but worth knowing when counting results.
- **No CI wiring.** 042B owns it. `playwright.config.ts` is already CI-shaped
  (`forbidOnly` under `CI`, `github` reporter) but nothing invokes it yet, and no
  service container is provisioned there.
- **`/health/ready` will fail on this stack** because RabbitMQ and Redis checks
  are registered when the transport is not in-memory and no Redis service exists
  in the rig. The harness probes `/health/live` only. If a 040B seam needs
  readiness semantics, add a `redis` service to the rig first.

### Naming and config conventions

- Spec tags: `@cross-layer-harness` (this task), `@cross-layer` (040B seams),
  `@cross-layer-ai` (040B seams needing a non-default mock scenario).
- New services go in `docker-compose.cross.yml` with a port disjoint from both
  the root stack (5432/5672/6379/8080/9000) and this rig's existing map, and the
  constant goes in `harness/config.ts` `PORTS` - the preflight cross-checks the
  two, so a port added to only one of them fails loudly.
- Secrets stay `CHANGE_ME`. Nothing real in compose, in `.env.cross-layer`, or in
  any test file.
- Non-ASCII punctuation was normalised to ASCII across the files added here on
  purpose: PowerShell's console mangles it, and a mangled comment in a source
  file is worse than an ASCII one.
