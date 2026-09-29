# Cross-layer test rig (Task 040A)

Real frontend + API + PostgreSQL + object storage + message transport, with **mock
AI only**. This rig exists so the seam specs added in Task 040B have a shared,
deterministic environment to run against.

Endpoint contract coverage is **not** here: per-route status codes belong to
Tasks 006-013 in `tests/DubbingPlatform.IntegrationTests`. The rig asserts that
the layers are wired to each other, not that each route answers correctly (R5).

## Run it

From the repository root:

```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npx playwright test --grep="@cross-layer-harness"
docker compose -f tests/cross-layer/docker-compose.cross.yml down
```

Add `-v` to `down` to discard the database volume as well; the rig re-seeds on
every run, so a plain `down` is usually enough.

Prerequisites: a running Docker daemon, the .NET 10 SDK, and Node with
`npm install` already run in `frontend/` and at the repository root.

### What `up -d` does not do

`docker compose up -d` starts the services. It does **not** build the frontend
bundle, apply migrations, or seed the database - `globalSetup`
(`tests/cross-layer/globalSetup.ts`) does all three, once per Playwright run, in
this order:

1. build `frontend/dist` with `--mode cross-layer` (so `VITE_API_BASE_URL` points
   at this rig rather than the dev API on `:5000`);
2. wait for the frontend to serve a `200` on its root;
3. assert every rig service is listening, failing closed if not;
4. build the .NET seeder, then run it (migrate -> reset -> seed -> verify).

The frontend container serves a bind mount of `frontend/dist`, so the bundle has
to exist before any browser work. Set `CROSS_LAYER_SKIP_FRONTEND_BUILD=1` to
reuse an existing bundle.

## Ports

Disjoint from the root `docker-compose.yml` stack so a running dev environment
cannot silently share state with the rig.

| Service            | Host port | Notes                                       |
| ------------------ | --------- | ------------------------------------------- |
| `api`              | 58080     | maps to 8080 in the container               |
| `frontend`         | 54173     | static `frontend/dist`, SPA fallback on 4173 |
| `postgres`         | 55432     | maps to 5432                                |
| `minio`            | 59000     | S3 API (console on 59001)                   |
| `rabbitmq`         | 55672     | AMQP (management on 55673)                  |
| `control` worker   | -         | pipeline orchestrator                       |
| `ai` worker        | -         | provider stages, mock AI only               |

A port collision is a **hard error naming the holder**, never a silent skip
(`assertRigPortsOpen` in `harness/config.ts`).

### Use `127.0.0.1`, not `localhost`

Every rig URL is spelled `127.0.0.1`, and the spelling is load-bearing. Docker
Desktop completes an IPv6 TCP handshake to a published port and then *resets*
the connection, while both Chromium and Node resolve `localhost` to `::1` first.
A rig URL written as `localhost` therefore fails against a healthy stack - as an
opaque `fetch failed` from Node and `net::ERR_CONNECTION_RESET` from the
browser - which reads like a broken service rather than an address-family
problem. Three places must agree on the spelling:

- `frontend/.env.cross-layer` -> `VITE_API_BASE_URL`
- `playwright.config.ts` -> `use.baseURL`
- `api` service -> `CORS__AllowedOrigins__0`

The app's CSP in `frontend/index.html` allows loopback in both spellings
(`http://localhost:* http://127.0.0.1:*`); Task 040A added the `127.0.0.1` entry
because the app was previously unable to call its own API when served from
`127.0.0.1`.

## The stack

| Service   | Role                 | Why it is required                                              |
| --------- | -------------------- | --------------------------------------------------------------- |
| `api`     | HTTP + SSE           | the system under test                                            |
| `postgres`| durable state        | seeded rows, run persistence                                     |
| `minio`   | object storage       | content objects                                                  |
| `rabbitmq`| message transport    | **real** transport; the in-memory bus cannot cross a process boundary, so with it the workers receive nothing and a run sits in `Pending` forever |
| `control` | pipeline orchestrator| without it a start is accepted and never progresses              |
| `ai`      | provider stages      | mock AI only                                                     |
| `frontend`| served bundle        | proves the browser can actually reach the API (CORS + build-time config) |

`Auth__SigningKey` is set to a `CHANGE_ME` placeholder. It has no default and
`AuthService.CreateAccessToken` throws when it is under 32 characters, so
without it every authenticated request fails with a bare `500` (the exception
middleware does not log the exception, so there is no other signal).

## Mock AI

Mock AI is mandatory. No seam may reach a real provider, and no real credential
belongs in this stack - every secret is a `CHANGE_ME` placeholder.

`harness/mockAi.ts` owns two things:

- **Deterministic expectations** mirroring the real mock providers
  (`expectedTranscriptText`, `EXPECTED_MOCK_CONFIDENCE`).
- **The R4 outage contract.** `AI_MOCK_UNAVAILABLE` is a *harness-level*
  sentinel, deliberately **not** added to
  `DubbingPlatform.Application/Errors/ErrorCodes.cs`: the product already has
  precise codes for each mock failure mode
  (`PROVIDER_RATE_LIMITED`, `PROVIDER_TIMEOUT`, `PROVIDER_INVALID_RESPONSE`,
  `PROVIDER_FAILED`, `PROVIDER_QUOTA_EXHAUSTED`,
  `PROVIDER_CONFIGURATION_ERROR`), and inventing a public error code to describe
  a test rig would leak test vocabulary into the API contract. The sentinel names
  the rig's conclusion and carries the product code that caused it.

To reproduce an outage, set a failing scenario on the `api` and `ai` services
and restart them:

```bash
# in docker-compose.cross.yml, api and ai:
#   Providers__Mock__Scenario: rate-limited
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d --force-recreate api ai
```

The rig then fails within `MOCK_AI_PROBE_TIMEOUT_MS` (30s) with
`AI_MOCK_UNAVAILABLE`, naming the product code. It never hangs until a generic
timeout.

## What the smoke asserts, and what it deliberately does not

`harness.spec.ts` (`@cross-layer-harness`) proves, in order:

1. the frontend bundle is served and served from the right origin;
2. `POST /auth/login` mints a token that `GET /me` can resolve to the seeded
   tenant - the rig never fabricates a token;
3. the seeded project is listed and is `MediaReady`;
4. `POST /processing` is accepted, the SSE stream opens and delivers a
   `stage.progress` event naming the same run, and the stream is closed
   unconditionally;
5. the workspace read agrees with the run, and the workspace never claims
   progress that nothing produced;
6. the durable rows exist: a transitioned project row, a matching active-run
   pointer, a `processing_runs` row, and a review count that matches the stored
   review rows;
7. the browser can reach the API - the only assertion involving CORS and the
   build-time `VITE_API_BASE_URL`. Everything else runs from Node, where the
   browser's origin rules do not apply.

**The rig does not run the pipeline to segment/export completion.** Reaching
segments and exports needs the FFmpeg-backed `media-preparation` /
`media-render` / `export` workers plus a real uploaded media file, which is
media-pipeline integration rather than harness scope. `assertRunArtifacts`
therefore asserts what a *start* guarantees and cross-checks the
pipeline-dependent counts against what the workspace claims; it does not assert
they are non-zero, and it does not skip them silently either. A 040B seam that
genuinely needs pipeline completion must add those workers and a media fixture
first.

## API behaviours the rig depends on

Found the hard way; each one cost a failed run.

- **Start before subscribe.** `GET /progress/stream` resolves the project's
  active run and answers `404` when none exists, so `POST /processing` must come
  first. The intuitive order (subscribe, then start) does not work.
- **`Idempotency-Key` is mandatory** on `POST /processing`.
- **A project must be `MediaReady` to start.** The seeder promotes it, and must
  also supply valid `processing_settings_json` (object root, numeric
  `schemaVersion` of 1); a null blob is rejected at the API boundary with a 400
  that names a JSON parse error rather than the missing seed data.
- **Three id shapes for one project:** the seeder's dashed GUID, the REST public
  id (`prj_` + 32 hex) and the SSE envelope's raw 32-hex N form. Database
  columns are `uuid`, so `harness/assertArtifacts.ts` normalises with `toUuid()`
  rather than interpolating whatever the caller holds.
- **`processing_settings_json` is a `json` column**, so it must be cast before
  it can be coalesced with a text literal.

## Seeded environment

Fixed, synthetic, and reproducible (`harness/config.ts` -> `SEED`):

| Field            | Value                                       |
| ---------------- | ------------------------------------------- |
| tenant           | `11111111-1111-1111-1111-111111111111`      |
| user             | `22222222-2222-2222-2222-222222222222`      |
| project          | `33333333-3333-3333-3333-333333333333`      |
| external subject | `cross-layer-owner`                         |
| email            | `owner@cross-layer.invalid` (RFC 2606)      |
| languages        | `en` -> `es`                                |

The seeder (`tests/cross-layer/seed`) is a small .NET console tool - 040A
explicitly permits a `seed.cs` seeder. It writes through `AppDbContext` and the
domain constructors rather than hand-written SQL, so a seeded project is valid by
the same rules the API enforces. There is no public provisioning endpoint by
design.

`reset` runs before `seed`, always - there is no incremental path, because an
incremental seed is how stale state from a previous session leaks in. The reset
**truncates** every domain table rather than deleting by `tenant_id`:

- the schema has 53 tenant-scoped tables and a hand-maintained delete list rots
  (an omitted `processing_runs` row made a fresh start return
  `409 RUN_ALREADY_ACTIVE`);
- the foreign-key graph contains cycles, so no delete order satisfies it;
- `__EFMigrationsHistory` and the MassTransit tables are preserved - truncating
  the former makes the next `MigrateAsync` fail with `42P07 relation already
  exists`, and the latter belong to the bus, not to the domain.

The seeder then verifies, read-after-write, that every row it promised exists,
and refuses to print a success payload otherwise.

## Layout

```
tests/cross-layer/
  docker-compose.cross.yml   the stack
  frontend-server.mjs        dependency-free static server (dual-stack, SPA fallback)
  globalSetup.ts             preflight, frontend build, seed - once per run
  harness.spec.ts            the smoke (@cross-layer-harness)
  harness/
    index.ts                 the only import 040B specs need
    config.ts                ports, seed constants, connection strings, preflight
    apiClient.ts             typed client; seam-shaped helpers, not a route matrix
    seed.ts                  build + reset-then-seed orchestration
    sseClient.ts             event-driven waits, no sleeps
    mockAi.ts                deterministic expectations + the R4 outage contract
    assertArtifacts.ts       durable post-run assertions
  seed/                      .NET seeder (CrossLayerSeed.csproj, Program.cs)
  .artifacts/                Playwright output (git-ignored)
```

`harness/index.ts` is the single import surface, so no 040B spec can quietly grow
its own boot path (R2).

## Tags

| Tag                    | Owner | Purpose                                   |
| ---------------------- | ----- | ----------------------------------------- |
| `@cross-layer-harness` | 040A  | this smoke                                |
| `@cross-layer`         | 040B  | the seven named seam specs                |
| `@cross-layer-ai`      | 040B  | seam specs needing a non-default mock scenario |

## Troubleshooting

| Symptom                                                   | Cause                                                                     |
| --------------------------------------------------------- | ------------------------------------------------------------------------- |
| `RigPreflightError: ... not listening`                    | the stack is not up; run `docker compose ... up -d`                        |
| `The frontend never served a 200`                         | the container exited; its logs name the reason, usually a half-written `dist` |
| `SeedError: CROSS_LAYER_SEED_FAILED`                      | the seeder prints the SQLSTATE and the full exception chain                |
| `ApiError: 401 INVALID_CREDENTIALS` on login              | the reset/seed did not persist; the seeder's verification would have said so |
| `ApiError: 409 RUN_ALREADY_ACTIVE`                        | a prior run's `processing_runs` row survived; the reset truncates, so this means the seed was skipped |
| Browser `TypeError: Failed to fetch` reaching the API     | CORS or CSP spelling mismatch - the three places above must agree          |
| `ApiError: 500` on any authenticated call                 | `Auth__SigningKey` missing or under 32 characters                          |

Logs must be scrubbed (Task 046's scrubber) before being attached to CI
artifacts.
