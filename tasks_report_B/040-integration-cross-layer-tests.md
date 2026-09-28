# 040 — Backend Integration and Cross-Layer Tests

## Status
BLOCKED

## Summary

Task 040 is **superseded** by `040A-cross-layer-harness.md` (the rig) and
`040B-cross-layer-seams.md` (the seven seam specs), and the two halves land in
very different states. The superseded endpoint-integration half (040
Instructions 1–4, R1/R2/R5) is **already delivered** by 006–013 — 54 existing
specs, 386 tests, including the R2 no-N+1 query-ceiling assertion and the R5
409-with-current-version assertions — so there is nothing left to write there
and the file's own header forbids duplicating it. The cross-layer half cannot be
built or verified in this environment: **this machine has no Docker daemon and
no Docker Desktop** (CLI 29.8.0 and Compose v5.5.1 only), every backing port
is closed, and both 040A and 040B begin their validation with
`docker compose … up -d`. `npx playwright test --grep="@cross-layer"` currently
exits 1 with `Error: No tests found`. I therefore did **not** write the compose
file, Playwright config, harness helpers or seam specs: they could not be
executed or even collected once against a live API, and 040A's own fail-closed
rule ("a failing harness blocks 040B") exists to stop exactly that. I shipped
only `tests/cross-layer/README.md`, the runbook the next agent needs first —
documentation that cannot break a build or a test.

## Files Created/Modified

- `tests/cross-layer/README.md` (created) — the only file added. Documents the
  040A→040B split, the verified "already delivered by 006–013" table, the fixed
  cross-layer port map (55432 / 59000 / 58080 / 54173, chosen so a running dev
  compose cannot collide), the environment placeholder set, the eight rig rules
  the next agent must honour, the seam→owning-task→pass-criteria map for
  040B instruction 4, the local run commands, and the blocker with its raw
  evidence. Includes the note that `AI_MOCK_UNAVAILABLE` is a harness-level
  sentinel that is **not** in `ErrorCodes.cs` (closest real codes are
  `PROVIDER_TIMEOUT` / `STORAGE_UNAVAILABLE`), so the next agent does not go
  looking for it.
- No production source, test, compose or config file was modified. `master-prompt.md`
  remains the pre-existing dirty scratch file and is intentionally uncommitted.

## Decisions Made
- **Honoured the supersession marker instead of the body.** The first line of the
  target file says it is "superseded (split/rewrite)" by 040A + 040B and that
  "endpoint integration stays in 006–013 and must not be duplicated here".
  I read those two files because the target file itself redirects to them and
  because the alternative — executing the body — would have duplicated ~10
  existing spec files and violated the file's own instruction. This is a
  deliberate, documented departure from the "do not read other task files"
  default, which exists to prevent scope drift, not to block a file's own
  successor pointer.
- **Verified the superseded half instead of re-doing it.** Rather than assume
  006–013 covered Instructions 1–4, I checked the named specs and the two
  requirements that need a concrete assertion. Both are already met:
  `Workspace/WorkspaceProgressTests.cs:277` asserts
  `queries <= WorkspaceService.QueryCeiling` (ceiling `12`, `WorkspaceService.cs:32`)
  — that is 040 R2 verbatim — and `Segments/SelectionConcurrencyTests.cs:59,186`
  assert HTTP 409 plus `ex.CurrentSelectionVersion`, which is 040 R5.
- **Did not write unverifiable test code.** The blocker is not "Docker is slow",
  it is "no daemon exists and Docker Desktop is not installed", so there is no
  iteration loop available. Committing ~2 000 lines of Playwright that has never
  been collected against a live stack would convert a loud, visible blocker into
  a hidden one, and would breach 040A R3 / 040B's fail-closed rule. The task's
  absolute rules also forbid declaring completion until validation is satisfied,
  so the status is BLOCKED rather than a partial "COMPLETED".
- **Did not install Docker Desktop autonomously.** It is a large, system-level,
  WSL2-dependent change that the task does not authorise, and the report records
  the exact remediation instead.
- **Checked whether the validation command could pass vacuously — it cannot.**
  `npx playwright test --grep="@cross-layer"` exits 1 with `Error: No tests
  found`, so there is no way to accidentally report success for an empty suite.

## Build/Test Results

- `dotnet test --filter FullyQualifiedName~IntegrationTests` → **exit 0, but
  only 171 of 386 tests actually ran**; the 215 container-backed tests skip
  because there is no Docker daemon (`Fixtures/TestFixtureBase.cs` calls
  `Skip.If` on its Docker probe). Last 4 lines:
  ```
    Skipped DubbingPlatform.UnitTests.IdempotencyTests.Forbidden_Tenant_403 [1 ms]
    Skipped DubbingPlatform.UnitTests.IdempotencyTests.Concurrent_Duplicates_Single_Row [1 ms]

  Passed!  - Failed:     0, Passed:   171, Skipped:   215, Total:   386, Duration: 1 m 16 s - DubbingPlatform.IntegrationTests.dll (net10.0)
  ```
  (preceded by the equivalent `Admin/AdminAuthzTests`, `Admin/AdminSseErrorContractTests` and `Api/IdempotencyTests` skip lines.)
- `npx playwright test --grep="@cross-layer"` → **exit 1, FAILED** (the reason
  this task is blocked). Last 3 lines:
  ```
  Error: No tests found
  ```
  `npx playwright test --list --grep="@cross-layer"` confirms
  `Total: 0 tests in 0 files`.
- `docker info` → **fails**; the blocker in full:
  ```
  failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine;
  check if the path is correct and if the daemon is running:
  open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.
  ```
- Environment probes confirming there is no daemon to start:
  ```
  docker --version                          -> Docker version 29.8.0, build 88096ef
  docker compose version                    -> Docker Compose version v5.5.1
  C:\Program Files\Docker\Docker\Docker Desktop.exe exists=False
  C:\Program Files\Docker\Docker\DockerCli.exe     exists=False
  Get-Service com.docker.service            -> 0 services
  ports 5432/5672/6379/8080/9000             -> all open=False
  ```
- Working parts of the toolchain, for the next agent: `npx playwright --version`
  → `Version 1.63.0`, chromium already installed under
  `%USERPROFILE%\AppData\Local\ms-playwright\chromium_headless_shell-1243`;
  `npx playwright test --list` from `frontend/` collects the 20 existing
  mock-based `frontend/e2e/*.spec.ts` specs.
- `docker compose config` is still usable without a daemon (the root
  `docker-compose.yml` header notes this), so compose syntax can be validated
  once the file is authored.

## Recommendations for Next Agent (041A)

- **State: BLOCKED on infrastructure, not on design.** 040A and 040B cannot
  start until a Docker daemon exists. Install Docker Desktop (WSL2 backend) or
  any daemon and confirm `docker info` prints a `ServerVersion`; no repo change
  is needed to unblock. Do not re-derive the design — read
  `tests/cross-layer/README.md` first; it carries the port map, the eight rig
  rules, the seam→task→criteria map, and the 006–013 "already delivered" table.
- **Do not re-implement the 006–013 half.** 040's Instructions 1–4 and its
  R1/R2/R5 are already satisfied by 54 existing specs (386 tests). R2 is
  `Workspace/WorkspaceProgressTests.cs:277` (query ceiling 12) and R5 is
  `Segments/SelectionConcurrencyTests.cs:59,186` (409 + `CurrentSelectionVersion`).
  The 040 header explicitly forbids duplicating them.
- **Gotcha — a green integration run can be hollow.** `Tests/TestResults`-style
  green output is not proof: `tests/DubbingPlatform.IntegrationTests/Fixtures/TestFixtureBase.cs`
  calls `Skip.If(true, …)` when its Docker probe fails, so 215 of 386 tests skip
  silently and the command still exits 0. Always read the `Skipped:` count before
  claiming an integration result.
- **Gotcha — the repository has no `playwright.config.ts` at all.**
  `npx playwright test --list` works from `frontend/` only because Playwright
  falls back to defaults, which is how it finds the 20 mock-based
  `frontend/e2e/*.spec.ts` specs. Creating the real config is part of 040A's
  work; place it so the cross-layer `testDir` does not also swallow the vitest
  `*.test.ts` files (Playwright's default `testMatch` would).
- **Gotcha — `AI_MOCK_UNAVAILABLE` is a harness sentinel, not a backend code.**
  It does not exist in `src/DubbingPlatform.Application/Errors/ErrorCodes.cs`
  (closest real codes: `PROVIDER_TIMEOUT` 504, `STORAGE_UNAVAILABLE` 503). The
  harness defines and raises it; don't hunt for it in the API.
- **Gotcha — ports.** The root `docker-compose.yml` already owns 5432 / 5672 /
  6379 / 8080 / 9000 and documents `POSTGRES_PORT` / `API_PORT` overrides.
  Use the distinct cross-layer range (55432 / 59000 / 58080 / 54173) so a
  running dev compose cannot collide, and turn any collision into an error that
  names the holder — never a silent skip.
- **Incomplete integration points:** everything in 040A instruction 1–5
  (compose file, `harness/{seed,mockAi,sseClient,assertArtifacts}.ts`,
  `harness.spec.ts`, README runbook) and all of 040B instruction 1–4 (the seven
  `seams/*.spec.ts` plus `seams/README.md`) are unwritten. No `playwright.config.ts`.
- **Test helpers available to the next agent:** Playwright 1.63.0 with chromium
  already downloaded; Node 24.11.0 / npx 11.6.1; the root `docker-compose.yml`
  `fast` profile already runs API + PostgreSQL 16 + MinIO with
  `Providers__DefaultProvider=mock` and `Transport__Provider=InMemory` and
  health-gated `depends_on`, so 040A may either add a `cross` profile there or
  ship a self-contained `tests/cross-layer/docker-compose.cross.yml` — both are
  permitted by 040A instruction 1.
- **Warnings:** 046 (fixtures, reset, auth seeds, the scrubber) is still
  unowned, and 040A/040B explicitly depend on it — synthetic tenants, the
  `reset`-then-`seed` order, the log scrubber before CI attach, and the
  quarantine policy (owner + issue, never a silent retry) all come from there.
  Do not build a private substitute. Also note the 046 dependency means the
  per-worker tenant isolation the seams require has no implementation yet.
- **Carried over from 039B/039C, still true:** the backend unit project pins
  **xunit 2.9.3** (not v3) with `TreatWarningsAsErrors` repo-wide;
  `node scripts/coverage-gap.mjs --backend` and the frontend default mode are
  both empty and must stay that way; `master-prompt.md` is dirty and uncommitted
  by design.
