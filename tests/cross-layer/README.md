# Cross-Layer Harness (Task 040A) — Runbook

> **Status: NOT STARTED — the rig does not exist yet.**
> This directory currently contains documentation only. No compose file, no
> Playwright config, no harness helpers and no seam specs have been written.
> The reason is recorded under [Why this is empty](#why-this-is-empty) and is
> an environment blocker, not a design decision.

Task 040 (combined) is superseded by:

- **040A** — this harness: real FE + API + PostgreSQL + object storage +
  transport, with **mock AI only**.
- **040B** — the seven named seam specs that run on this rig.

Endpoint integration is **not** duplicated here; it stays in Tasks 006–013
(see [Already delivered](#already-delivered-by-006013)).

---

## Why this is empty

The rig is defined by a Docker Compose stack. This machine has the Docker
**CLI** (29.8.0) and the Compose plugin (v5.5.1) but **no daemon and no
Docker Desktop**, so no container can be started:

```
$ docker info
failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine;
check if the path is correct and if the daemon is running:
open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.

C:\Program Files\Docker\Docker\Docker Desktop.exe exists=False
C:\Program Files\Docker\Docker\DockerCli.exe     exists=False
Get-Service com.docker.service                   -> 0 services
```

Every backing port is closed (`5432`, `5672`, `6379`, `8080`, `9000` all
`open=False`).

Consequence: the cross-layer acceptance criteria cannot be evaluated at all.
Neither 040A nor 040B has a runnable validation command, because both begin
with `docker compose -f tests/cross-layer/docker-compose.cross.yml up -d`.

Because the stack cannot be booted, any compose file, Playwright config,
harness helper or seam spec written here would be **code that has never been
executed** — not even collected once against a live API. 040A R3 and its
"failing harness blocks 040B (fail-closed)" edge case exist precisely to stop
seam specs being built on an unproven rig, so none were written.

**Remediation:** install Docker Desktop (WSL2 backend) or any Docker daemon and
confirm `docker info` reports a `ServerVersion`, then run this task. No repo
change is needed to unblock it.

---

## Already delivered by 006–013

The non-superseded, non-blocked half of Task 040 (its Instructions 1–4 —
endpoint integration) is already in the repository and must not be rewritten:

| Task 040 requirement | Existing spec | Tests |
| --- | --- | --- |
| `/me`, preference round-trip, tenant isolation (Instr. 1) | `Auth/MePreferencesTests.cs` | 12 |
| project CRUD, archive/unarchive, delete-with-active-run, cross-tenant (Instr. 2) | `Projects/ProjectsApiTests.cs` | 11 |
| workspace aggregate, activity pagination, notifications, review context (Instr. 3) | `Workspace/WorkspaceProgressTests.cs`, `Notifications/NotificationActivityTests.cs`, `Reviews/ReviewContextTests.cs` | 11 / 16 / 10 |
| preview authz, selection conflicts, admin authz (Instr. 4) | `Previews/PreviewArtifactTests.cs`, `Segments/SelectionConcurrencyTests.cs`, `Admin/AdminAuthzTests.cs` | 21 / 12 / 6 |

Two requirements are already met with a concrete assertion, not just intent:

- **R2 (workspace is single-shot, no N+1)** —
  `Workspace/WorkspaceProgressTests.cs:277` asserts
  `queries <= WorkspaceService.QueryCeiling` (ceiling `12`,
  `src/DubbingPlatform.Application/Workspace/WorkspaceService.cs:32`).
- **R5 (conflicts carry the current version)** —
  `Segments/SelectionConcurrencyTests.cs:59,186` assert HTTP `409` together with
  `ex.CurrentSelectionVersion`, so a client can refresh instead of blind-retry.

Run them with `dotnet test --filter FullyQualifiedName~IntegrationTests`. Note
that the container-backed cases **skip** when no daemon is present (see
`Fixtures/TestFixtureBase.cs`, which calls `Skip.If` on a Docker probe), so a
green run without Docker is not proof the suite executed.

---

## Port map to use (046 fixed ports)

Cross-layer uses a **distinct** range so a running dev compose on the same
machine cannot collide silently. Every port is fixed; a collision is a hard
error naming the holder, never a silent skip.

| Service | Cross-layer host port | Dev compose default | Note |
| --- | --- | --- | --- |
| PostgreSQL 16 | 55432 | 5432 | `POSTGRES_PORT` override in dev compose |
| Object storage (MinIO) | 59000 / 59001 | 9000 / 9001 | API + console |
| API | 58080 | 8080 | `ASPNETCORE_URLS` |
| Frontend (built `dist`) | 54173 | 5173 (vite dev) | `vite preview` default is 4173 |
| Message transport | in-memory by default | 5672 (RabbitMQ) | opt in with the `full` profile |
| Cache | not required by default | 6379 (Redis) | opt in with the `full` profile |

Environment for the API container (emulator + placeholders only, never a real
credential):

```
ConnectionStrings__Default=Host=postgres;Port=5432;Database=dubbing;Username=dubbing;Password=CHANGE_ME
Storage__Endpoint=http://minio:9000
Storage__AccessKey=CHANGE_ME
Storage__SecretKey=CHANGE_ME
Providers__DefaultProvider=mock
Transport__Provider=InMemory
```

---

## Rules the rig must honour (040A R1–R5)

1. **Mock AI only.** `Providers__DefaultProvider=mock`. No real provider is
   ever called from a cross-layer run, and mock output must be deterministic —
   any randomness fails the seam (040B edge case).
2. **Health-gated boot, never fixed sleeps.** PG slow-start is handled with
   compose `healthcheck` + `depends_on: condition: service_healthy`, matching
   the pattern already used in the root `docker-compose.yml`. A partial boot
   (DB up, broker down) must fail closed with a service matrix, never
   half-run specs.
3. **`reset` before `seed`, always**, so a prior run cannot leak state.
4. **Event-driven waits only.** SSE progress waits subscribe to frames and
   resolve on the awaited event; fixed-sleep waits are rejected in review
   (040B edge case).
5. **Named fail-fast on mock-AI outage.** When the mock provider cannot be
   reached the harness raises the sentinel `AI_MOCK_UNAVAILABLE` — note this is
   a **harness-level** sentinel, it is not in
   `src/DubbingPlatform.Application/Errors/ErrorCodes.cs` (the closest real
   codes are `PROVIDER_TIMEOUT` / `STORAGE_UNAVAILABLE`). It must fail fast,
   never hang on a timeout.
6. **Tenant isolation per worker.** A seam that passes alone but fails in the
   full suite is fixed by per-worker tenants, never a shared-tenant shortcut.
7. **Synthetic data only.** Synthetic tenants/users; seeded credentials are
   ephemeral per run. Downloaded bytes in the export seam are fixture media,
   scrubbed before any CI attach. Failure output shows correlation ids only.
8. **The harness asserts seams, not route status codes** — per-route matrices
   belong to 006–013 (040A R5, 040B R2).

---

## Seam → owning task map (for `seams/README.md`, 040B instruction 4)

To be written alongside the specs. Recorded here so it is not lost:

| Seam spec | Seam | Owning feature tasks | Pass criteria |
| --- | --- | --- | --- |
| `seam-upload-storage.spec.ts` | FE upload → storage bytes + server validation → ready | 023 | artifact row exists; client shows ready; server rejects a bad container |
| `seam-processing-sse.spec.ts` | processing start → SSE frames → workspace refetch | 024 / 026 / 008 | frames received; workspace agrees with the last frame; polling fallback covers an SSE gap |
| `seam-review-mutation.spec.ts` | review resolve → versioned mutation + audit + invalidation | 031 / 003 / 009 / 011 | new version persisted; one audit event; double-submit dedupes on the idempotency key |
| `seam-export-download.spec.ts` | export create → signed-URL download + completeness | 033 / 012A | bytes fetchable; expired URL forces exactly one refetch, then succeeds |
| `seam-voice-invalidation.spec.ts` | voice change → dependent invalidation + consent gate | 029 / 010 | dependent queries invalidated; revoked consent blocks the change |
| `seam-stale-conflict.spec.ts` | stale edit → 409 → refresh UX, draft preserved | 027 / 028 / 003 / 009 | 409 carries the current version; UI offers refresh and keeps local text |
| `seam-notification.spec.ts` | backend event → durable notification → center + deep link | 034 / 002 / 012B | row persisted; center renders it; deep link opens the target |

Every seam must assert **both** sides — client state *and* server rows/artifacts
via `assertArtifacts` — plus its named failure half. Happy-path-only seams fail
review (040B instruction 2).

---

## Local run (once a daemon exists)

```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npx playwright test --grep="@cross-layer-harness"   # 040A smoke
npx playwright test --grep="@cross-layer"           # 040B seams
docker compose -f tests/cross-layer/docker-compose.cross.yml down
```

Current state of that command, for reference:

```
$ npx playwright test --grep="@cross-layer"
Error: No tests found
```

Note that the 20 specs already in `frontend/e2e/` are **mock-based**
(`page.route` interception) and need no backend; they are the 019–036 UI specs,
not cross-layer. They are discovered by Playwright's default config because the
repository has **no `playwright.config.ts` at all** — creating one is part of
this rig's work.
