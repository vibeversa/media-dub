# Task 043C - Incident Runbooks and Backup Drills

## Status

**COMPLETED.** All four instructions implemented, R1-R5 satisfied, the task's
`Validation` block passes **verbatim**, and the restore drill is **recorded with
its date, its result and nine gaps** (six of which were defects in the drill
itself, found by running it against broken inputs).

**Two of those six are the same defect at two levels, and they are the finding
worth carrying forward.** A check whose *subject list* comes from the same file
as the *data it checks* will always agree with itself:

- deleting the `notifications` group from `deploy/backup/scope.json` → the drill
  dumped 9 tables, compared 9 counts, every count matched, printed **PASS**
  (`gaps=0`, same as a clean run);
- truncating `scope.json`'s `referentialChecks` from 15 to 3 → the drill ran 3
  orphan queries, found nothing, printed **PASS** with byte-identical output.

Neither was visible by reading the drill. Both were found by editing the input
and re-running, and both are fixed by writing the required set down a **second**
time in a file the data change does not reach. That is now the repo's pattern in
three places: `REQUIRED_GROUPS` in `scripts/restore-drill.sh`,
`REQUIRED_GROUPS` in `tools/backup-coverage.test.mjs`, `REQUIRED_RUNBOOKS` /
`PRODUCT_RUNBOOKS` in the two runbook gates.

## Summary

Eight product incident runbooks in `docs/runbooks/product/`, indexed by what a
**user says** rather than what a component is doing, each linking (never
forking) its Plan A mechanism page, and each carrying two sections the mechanism
pages do not: a **degraded-mode triage** path that works with no dashboard and
no metrics pipeline, and an explicit **access-and-audit** restatement of the
`RequireTenantAdmin` policy and the `audit_events` requirement. Backup coverage
for the seven new durable-entity groups is declared as data
(`deploy/backup/scope.json`) with retention and restore priority, verified by a
re-runnable drill (`scripts/restore-drill.sh`) and documented in `docs/backup.md`.

**Six monitoring gaps were found while writing the runbooks and are named as gaps
with owners rather than deleted.** There is no auth, CDN, SSE, contract or
notification alert in `deploy/observability/alerts.yml`; no panel anywhere reads
the SSE, upload-funnel, review-latency or notification-projection counters that
the API **does** emit; and the SSE connection/reconnect counters are declared
(`BackendMetrics.SseConnections` / `SseReconnects`) but **never called from
production code** — only from a test. The mechanism page
[`docs/runbooks/sse-degraded.md`](../docs/runbooks/sse-degraded.md) tells a
responder to read `sse_connections` on a dashboard that does not exist. Deleting
those references would have made the runbooks look complete; the gate now
requires every named monitor to be real **or** to be a declared gap with an
owning task number.

## Files Created/Modified

### Created

| File | What it is |
| --- | --- |
| `docs/runbooks/product/index.md` | The set's index: the two-directory split, the uniform template, the access rule once, and the **tabletop record** with a date, a per-step table and three gaps. |
| `docs/runbooks/product/auth-outage.md` | 401/500 on login, mass sign-out, `TOKEN_REUSED`. Opens with the step-1 call whose *answer class* is the incident. |
| `docs/runbooks/product/cdn-outage.md` | Blank page / wrong build. The origin-vs-edge two-`curl` comparison that names the broken hop in ten seconds. |
| `docs/runbooks/product/sse-outage.md` | Frozen progress bar. **Fallback-polling verification** as the deciding check: `progress` is the source of truth, the stream is a hint. |
| `docs/runbooks/product/frontend-deploy-failure.md` | Broken after a deploy. The three-way version comparison, the `dist/` `emptyOutDir` trap, `add_header` replacing rather than augmenting. |
| `docs/runbooks/product/notification-backlog.md` | Missing notifications. The `list` vs `unread-count` split, and the expired-unread cause for a badge that will not clear. |
| `docs/runbooks/product/contract-drift.md` | "The page says the field doesn't exist." Three-way version comparison, and the post-contract rollback **refusal**. |
| `docs/runbooks/product/upload-surge-failure.md` | Upload fails at 90%. The three upload phases, `Media__MaxConcurrentMediaJobs = 2`, and the full scratch PVC that fails at the last step. |
| `docs/runbooks/product/review-backlog-surge.md` | Days awaiting review. Age distribution over count, routing over headcount, and the reviewer-token check that finds a stranded backlog. |
| `docs/backup.md` | The coverage table for the seven groups, retention, restore priority, the 11-check drill, the **recorded result**, and gaps D1-D6 (fixed) plus D1a-D3a (open). |
| `deploy/backup/scope.json` | The scope as data: 7 groups, parents, 15 referential checks, retention, restore priority, `addedBy` migration, and an empty `durableEntitiesNotYetBackedUp`. |
| `deploy/backup/drill-seed.sql` | Synthetic seed, 28 rows, idempotent and scoped to its own `de71…` tenant. Deliberately asymmetric and deliberately includes a `Running` and an expired row. |
| `scripts/restore-drill.sh` | The drill. 11 checks, argv-only SQL, loopback interlock, closed reason vocabulary, `RESTORE_DRILL_RESULT`. |
| `tools/product-runbooks.test.mjs` | 19 tests. Template, order, non-emptiness, user-report opening, monitor/diagnostics/rollback rows, R4 role+audit, R5 no-fork, the shared-name hazard, and a credential-shape sweep. |
| `tools/backup-coverage.test.mjs` | 19 tests. The seven groups from three independent directions, the extended project columns against the migration, the seed's coverage and synthetic-ness, the drill's four silent-failure checks, the reason vocabulary, and the doc. |

### Modified

| File | What changed |
| --- | --- |
| `docs/runbooks/index.md` | A **"The product runbooks"** section: the two-directory split, the eight by user report, each linked to its mechanism page, and why two of the runbooks have three sections the rest do not. |
| `docs/runbooks/auth-outage.md`, `notification-backlog.md`, `contract-drift.md` | A closing pointer to the product twin, because these three names exist in **both** directories. Each names the other and says why. |
| `docs/dr/drill-log.md` | The 2026-10-01 entry: scope, command, verdict, the six defects, and an explicit statement that the managed-PITR tier is **still staging-gated**. |
| `docs/ci-branch-protection.md` | Two new gate sections: `RESTORE_DRILL_RESULT`'s 16-reason vocabulary, and what each of the two new test files holds. Includes the GAP rule. |
| `package.json` | `check:runbooks`, `check:backup`, `drill:restore`. `test:tools` already globs `tools/*.test.mjs`, so both gates are covered by existing CI. |

## Decisions Made

1. **The product runbooks go in `docs/runbooks/product/`, not
   `docs/runbooks/`.** Three of the eight task-named filenames
   (`auth-outage.md`, `notification-backlog.md`, `contract-drift.md`) already
   exist as Plan A **mechanism** pages, and R5 forbids forking them. A
   subdirectory satisfies "add `docs/runbooks/` entries" while making the
   ownership split structural rather than a naming convention. The three
   overlapping names are the reason the split is worth a directory: two files
   named `auth-outage.md` with no visible distinction is a hazard, so each pair
   points at the other **and** the test asserts both links exist.

2. **Same template, two extra sections.** `## Degraded-mode triage` is
   non-optional because the task requires a logs-first path for a monitor being
   down, and **because the gap inventory proved the ordinary case**: with no auth,
   CDN, SSE, contract or notification alert, there is frequently no dashboard to
   be degraded from. `## Access and audit` is non-optional per R4.

3. **Monitoring gaps are declared, not deleted.** The alternative was writing
   only the monitors that exist, which would produce eight runbooks that read as
   complete and would fail silently at 3 a.m. The gate's rule — a named monitor
   must be a real alert, dashboard or emitted metric, **or** a `**GAP …** **Owner:
   NNN.**` — is what keeps that honest, and it is why `product-runbooks.test.mjs`
   fails a `GAP` with no owner.

4. **The runbook template is a hard gate, not a review convention.** A page that
   puts Mitigation before Escalation teaches a responder the document cannot be
   trusted to be in order. The test asserts presence, order **and** that no
   section is under three lines — a heading with nothing under it is padding.

5. **`deploy/backup/scope.json` exists because three consumers need the same
   list** (a responder, the drill, the docs) and three hand-maintained lists
   drift silently. Proven, not asserted: D2 and D6 were both found by editing
   the file.

6. **The required set is written down twice, deliberately.** See the Status
   section. This is a cost paid on purpose and the reason is the strongest
   argument in the report.

7. **The drill is data-only and refuses to create a schema.** A drill that
   migrates its own target verifies a schema nobody migrated, and the migration
   chain is verified separately by `scripts/migration-compat.sh` and the
   `BackupRestoreTests` tier. `SCHEMA_NOT_APPLIED` prints the exact `dotnet ef
   database update` command to run first.

8. **SQL is never built by concatenation, and `scope.json` is data rather than
   trusted input.** Table identifiers are validated against
   `^[a-z_][a-z0-9_]*$` before use. The one place a whole statement is generated
   — the referential checks — validates all four identifiers per rule and throws,
   because a silently dropped check is a restore that reports coherent. The test
   asserts each `psql -c` invocation interpolates a variable in only the two
   permitted positions.

9. **Non-zero post-restore counts are a separate failure reason.** `pg_restore`
   reports success after inserting zero rows; a restore run as a role without
   `BYPASSRLS` has every `COPY` filtered and exits 0. `EMPTY_RESTORE` exists for
   that, with the RLS cause in the message (D3).

10. **RLS is checked *before* any write** (D1), and the drill refuses to run at
    all against a non-loopback host without `DRILL_ALLOW_REMOTE=1`. Both are
    asserted in `backup-coverage.test.mjs`.

11. **The referential checks are 15 explicit orphan queries, not foreign keys.**
    The database has **no FKs** between any of these tables — the only FK
    constraints in the whole schema are MassTransit's `outbox_message →
    outbox_state` / `inbox_state`. A partial restore is therefore silent, and
    this is the only check that can see it.

12. **The seed is asymmetric on purpose.** `voice_preview_jobs` has one
    `Completed` and one deliberately left `Running`; `notifications` has one row
    past `expires_at` and one read row. "Every count is non-zero" is a check that
    can only fail if something is genuinely missing. The restored `Running` row
    and the restored-but-expired notification are both documented in
    `docs/backup.md`, because a responder meets both on day one of a real
    restore.

13. **The tabletop records what it could NOT verify.** The walk used a local
    compose stack, not shared staging, so the ingress-annotation half — the most
    likely cause of an SSE degradation — is recorded as unverified rather than
    assumed. `product-runbooks.test.mjs` requires the record to state its limits,
    because a tabletop with no stated limits overstates its result.

14. **`REASONS_SCOPE` is aliased to `REASONS_NO_SCOPE`.** The two spellings
    existed and `set -u` turned the divergence into an unbound variable at
    exactly the moment the drill was reporting a failure. Found by running the
    D6 fault; one-line fix, documented so the second name is not "cleaned up"
    later.

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ ls docs/runbooks/
auth-outage.md
backup-restore.md
cdn-cache-poison.md
contract-drift.md
db-failover.md
deploy-failed.md
dlq.md
escalation.md
index.md
lease.md
notification-backlog.md
orphan.md
product
provider-outage.md
quota-cost.md
review-backlog.md
review-surge.md
rollback.md
sse-degraded.md
storage-outage.md
upload-surge.md
EXIT=0
```

```
$ grep -l "Escalation" docs/runbooks/*.md
docs/runbooks/auth-outage.md
docs/runbooks/cdn-cache-poison.md
docs/runbooks/contract-drift.md
docs/runbooks/db-failover.md
docs/runbooks/deploy-failed.md
docs/runbooks/dlq.md
docs/runbooks/escalation.md
docs/runbooks/lease.md
docs/runbooks/notification-backlog.md
docs/runbooks/orphan.md
docs/runbooks/provider-outage.md
docs/runbooks/quota-cost.md
docs/runbooks/review-backlog.md
docs/runbooks/review-surge.md
docs/runbooks/sse-degraded.md
docs/runbooks/storage-outage.md
docs/runbooks/upload-surge.md
EXIT=0
```

```
$ grep -E "tenant users|preferences|notifications|activity|memberships|voice preview|project metadata" docs/backup.md
| 2 | **Project memberships** | `project_memberships` | PostgreSQL | 365 d | **yes** |
| 2 | **Extended project metadata** | `dubbing_projects` (9 new columns) | PostgreSQL | 365 d | **yes** |
| 3 | **Preferences** | `user_preferences` | PostgreSQL | 365 d | after auth works |
| 4 | **Notifications** | `notifications` | PostgreSQL | 90 d | after login works |
| 5 | **Activity events** | `activity_events` | PostgreSQL | 90 d | last; it is history |
restore that brings back notifications without memberships produces a product
where every user is signed in, sees a list of notifications, and cannot open the
project any of them point at. A restore that brings back preferences without
absence produces a **403 that reads as a broken release**: memberships gone means
    `tenant_users` 2, `user_preferences` 4, `notifications` 5, `activity_events`
    4, `project_memberships` 3, `voice_preview_jobs` 2, `dubbing_projects` 3,
| **D1** | ... | HIGH | 043C | **fixed** |
| **D2** | ... | HIGH | 043C | **fixed** |
left `Running`, and `notifications` with one row past its `expires_at`. Both are
EXIT=0
```

### The restore drill — `RESTORE_DRILL_RESULT reason=OK status=PASS`

```
$ bash scripts/restore-drill.sh
== new-entity restore drill (Task 043C) ==
  ok 10 table(s) in scope: tenant_users user_preferences notifications activity_events project_memberships voice_preview_jobs dubbing_projects tenants speakers voice_profiles
  -- reusing the running container dubbing-drill-pg
  -- connected; 57 table(s) in the public schema
  ok 7 migration(s) applied
  ok row level security is enabled on every scoped table
  ok seeded synthetic rows (deploy/backup/drill-seed.sql)
     tenant_users: 2
     user_preferences: 4
     notifications: 5
     activity_events: 4
     project_memberships: 3
     voice_preview_jobs: 2
     dubbing_projects: 3
     tenants: 1
     speakers: 2
     voice_profiles: 2
  ok counted 10 table(s) before the restore
  ok dumped 6251 bytes
  ok wiped every scoped table to zero
  ok restored the archive
  ok all 10 count(s) identical before and after
  ok no dangling references across 7 group(s); all 15 declared relationship(s) ran
  ok spot-read one project, one user and one notification, with the Plan B columns
  -- NOTE: the restore ran as dubbing. A production restore must use the
  --       maintenance role (BYPASSRLS); as the app role the restore would have
  --       inserted zero rows and still reported success.

  date: 2026-10-01T07:26:55Z
  scope: 10 tables
  gaps recorded: 0

RESTORE_DRILL_RESULT reason=OK status=PASS tables=10 rows=28 gaps=0
EXIT=0
```

### The two new gates, and the two existing ones

```
$ node --test tools/backup-coverage.test.mjs tools/product-runbooks.test.mjs tools/runbooks-index.test.mjs
# tests 51
# pass 51
# fail 0
```

```
$ npm run test:tools
# tests 255
# pass 254
# fail 1        <- tools/npm-audit-gate.test.mjs, PRE-EXISTING, see below
```

### Backend

```
$ dotnet build DubbingPlatform.sln -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:02:09.68

$ dotnet test tests/DubbingPlatform.UnitTests -c Release
Failed!  - Failed: 2, Passed: 3038, Skipped: 0, Total: 3040
```

Both failures are **pre-existing and environmental** — proved by
`git stash -u` + re-run on the unmodified tree, which produces the identical
`Failed: 2`:

1. `MediaValidationTests.Probe_Real_Files_Via_Ffprobe` — `ffprobe` is not
   installed in this container (`No such file or directory`).
2. `OptionsValidationMatrixTests.AuthOptions_Rejects_NonAbsolute_Authority` —
   an assertion that expects `true` and gets `false`; unrelated to any file this
   task touched.

Neither is caused by this task, and no test was deleted or weakened.

### Frontend

```
$ npm run typecheck --prefix frontend          -> (no output)   EXIT=0
$ npm run lint --prefix frontend              -> (no output)   EXIT=0   (--max-warnings=0)
$ npm run test --prefix frontend
  Test Files  146 passed (146)
       Tests  1651 passed (1651)
$ npm run check:no-hex --prefix frontend
  check-no-hex: no hardcoded hex outside tokens.css.
$ npm run typecheck:e2e                      -> (no output)   EXIT=0
$ node tools/generate-client.mjs && git diff --exit-code -- frontend/src/api/generated/
  CLIENT_DRIFT=none
```

**The frontend suite needs three env vars that CI supplies and a bare shell does
not.** With them unset, 30 tests fail on `Invalid frontend configuration:
VITE_API_BASE_URL: Required` and 2 more on a missing version stamp. The version
is `VITE_APP_VERSION`, which `frontend/.env.example` sets to `0.1.0-dev` and
`deploy/config-inject.sh` injects. Reproduced on the stashed tree: setting
`VITE_API_BASE_URL`, `VITE_CDN_ORIGIN`, `VITE_ENVIRONMENT`, `VITE_APP_VERSION`
gives **146/146 files, 1651/1651 tests**. This is a local-environment
requirement, not a regression, and it is worth knowing before a next agent reads
30 red tests as this task's damage.

### Deploy / CI gates

```
$ bash deploy/verify.sh
FAIL: kustomize build staging + prod (see the load-restrictor and patch-target notes above)
== result: 2 passed, 1 failed ==
VERIFY_RESULT reason=MANIFEST_CHECK_FAILED status=FAIL exit=1
```
**Pre-existing, identical on the stashed tree.** This is 043A's Finding 6
verbatim: the kustomize overlays have never built (load restrictor rejects `..`
resources; the base manifests hard-code `namespace: dubbing-prod` while the
patches declare none). 043A assigned it to 043B. This task adds no manifest and
does not touch the overlays.

```
$ bash deploy/tests/hosting.test.sh
  passed: 28, failed: 0
  static: PASS, docker: PASS
HOSTING_GATE_RESULT reason=OK status=PASS exit=0 static=PASS docker=PASS
```
28, not the 30 the 043A report claims — **also identical on the stashed tree**,
so the 043A count is stale, not a regression here.

```
$ npm run check:unit-containers
CI_GATE_RESULT reason=OK status=PASS files=77 tests=1440
$ bash scripts/workflow-lint.sh
workflow-lint: 5 workflow file(s), 0 finding(s)
$ bash scripts/quarantine-check.sh
quarantine-check: every entry is owned, tracked, in window and unexpired.
CI_GATE_RESULT reason=OK status=PASS
$ bash scripts/migration-compat.sh
  613 columns in the previous release, all still present with the same type and nullability.
  10 column(s) added - additive, which is what the rule requires.
MIGRATION_COMPAT_RESULT reason=OK status=PASS previous=20260921115016_AddVoicePreviewJobs current=20260922082522_AddRefreshSessions
```

### The gates proved red, on the real artefacts

| Class | Injected fault | Observed | After |
| --- | --- | --- | --- |
| drill scope | `notifications` group deleted from `scope.json` | `tables=9 rows=23`, all counts matched, **`status=PASS gaps=0`** | `SCOPE_INVALID`, "1 required entity group(s) are missing from the backup scope" |
| drill scope | `referentialChecks` truncated 15 → 3 | 3 orphan queries, nothing found, **`status=PASS gaps=0`** | `SCOPE_INVALID`, "declares 3 referential check(s) for 7 entity group(s) (gap D6)" |
| tenant isolation | `ALTER TABLE notifications DISABLE ROW LEVEL SECURITY` | names the table, `TENANT_ISOLATION_MISSING` | check 5, before any write |
| safety interlock | `DRILL_HOST=db.production.internal` without the override | `REMOTE_TARGET_REFUSED`, names the two env vars | — |
| schema | `DROP SCHEMA public CASCADE` | `SCHEMA_NOT_APPLIED` with the `dotnet ef database update` command | — |
| drill | `REASONS_SCOPE` / `REASONS_NO_SCOPE` divergence | `line 479: REASONS_SCOPE: unbound variable` — the drill could not report its own failure | aliased, with the reason in the source |
| runbook monitor | a `GAP` with no owner | `declares a GAP without naming an owning task` | `**Owner: NNN.**` on every gap row |
| runbook signals | a `## Signals` table row wrapped across three lines | Markdown renders the continuation as a paragraph, so the row loses its tail — **including the `Owner:`**. The gate failed on a gap that was correctly attributed on paper | rows are single-line; asserted |
| runbook monitor | `notification_backlog` named as a metric | not declared, emitted or graphed anywhere | rewritten to name the emitted counter and declare the gap |
| runbook monitor | `chunk_received` treated as a metric | it is a `stage` **label** of `upload_funnel_total` | the gate collects label values from the declaration site |
| backup doc | "Result: PASS" buried in a sentence | the gate could be satisfied by a `PASS` in a later paragraph | anchored to the `Result` line, `PASS` required |
| seed | a second `INSERT` without a preceding scoped `DELETE` | `pk_tenants` violation on re-run | the seed is idempotent, and the test asserts every `DELETE` carries the `de71…` id |
| SQL safety | a `psql -c` interpolating a whole statement | the test enumerates every invocation and rejects any variable outside the two permitted identifier positions | — |

### Environment notes for the next agent

- **The .NET 10 SDK is not on this host's `PATH`.** `global.json` pins
  `10.0.100`; `/usr/share/dotnet` has only `8.0.408`. I installed
  `10.0.401` to `/tmp/opencode/dotnet10` and every `dotnet` command above ran
  with `export PATH="/tmp/opencode/dotnet10:$PATH"`. Without it, `dotnet build`,
  `dotnet test` and `scripts/migration-compat.sh` all fail on SDK resolution
  (`migration-compat.sh` reported `reason=PREVIOUS_SCHEMA_FAILED` until the PATH
  was set, which looks like a migration defect and is not one).
- **`dotnet ef` needs `dotnet tool restore` first** (`.config/dotnet-tools.json`,
  `dotnet-ef 10.0.12`). The first `dotnet ef database update` failed with
  "Run `dotnet tool restore`".
- **`npm ci` is required in `frontend/`** — `node_modules` was absent, and
  `typecheck` failed on `Cannot find type definition file for 'vite/client'`.
- `bc`, `python3` and `docker` are present; `psql`/`pg_dump`/`ffprobe` are not,
  which is why the drill prefers the container's own client.

## Findings

Every one of these was found by running the thing.

### 1. The scope file and the drill were validating each other (D2, D6)

The drill read its table list from `deploy/backup/scope.json` and checked
completeness against the same file. Deleting a group removed it from the dump,
from the count comparison **and** from the completeness check in one edit, and
the drill reported `status=PASS gaps=0` — indistinguishable from a clean run.
Truncating `referentialChecks` did the same one level down.

This is not a scripting slip, it is a structural property: **a check whose
subject list is derived from the same file as the data it checks is a tautology
on that file.** The fix is duplication with a purpose, and the repo already had
the pattern in `tools/runbooks-index.test.mjs` (`REQUIRED_RUNBOOKS` + the
comment "a runbook that exists but is not linked is never found"). It is now used
in four places. Expect it to look redundant; it is the load-bearing part.

### 2. `BackendMetrics.SseConnected` / `SseReconnected` are never called

`src/DubbingPlatform.Api/Observability/BackendMetrics.cs` declares
`sse.connections_total` and `sse.reconnects_total` and provides
`SseConnected` / `SseReconnected` helpers. The **only** callers in the
repository are `tests/…/ObservabilityTests.cs:197-198`. Nothing in
`ProcessingController.StreamProgress` calls them, so the counters stay at zero
in production.

The consequence is concrete: `docs/runbooks/sse-degraded.md` tells a responder
"**`sse_connections` on the API dashboard** is well below its baseline while
`sse_reconnects` is above it" as the discriminating triage check, and it cannot
be run. The product runbook substitutes the direct `curl` plus a pod log count,
both of which were verified during the tabletop. A counter with a test is not an
instrumented code path — that is the finding, and it is the same shape as 043A's
"a gate whose two failure modes are indistinguishable cannot detect either".

**Owner: 038** for the panel, and the mechanism page's triage step 2 should be
amended in the same change so the two documents stop disagreeing.

### 3. Seven counters are emitted and nothing graphs them

`upload_funnel_total`, `review_latency_ms`, `export_latency_ms`,
`preview_latency_ms`, `readmodel_query_duration_ms`, `correlation_minted_total`,
`notifications_projection_failures_total` and `sse.payload_dropped_total` all
exist. `grep sse deploy/observability/dashboards/*.json` returns nothing, and
`reviews.json` does not read `review_latency_ms`. Every product runbook names
the missing panel as a gap with 038 as the owner, and the substitute is a
`curl` against `/metrics` that was verified during the tabletop.

### 4. The 30th hosting-gate assertion in the 043A report does not exist

`deploy/tests/hosting.test.sh` reports `passed: 28`. The 043A report claims 30
and enumerates them. Identical on the stashed tree, so nothing here regressed —
**the 043A report's count and enumeration are wrong or refer to assertions
since removed.** Worth reconciling, because a report that overstates a gate's
coverage is the same class of problem as a gate that overstates its own.

### 5. `frontend` tests need four env vars, and 30 red tests look like damage

`VITE_API_BASE_URL`, `VITE_CDN_ORIGIN`, `VITE_ENVIRONMENT`, `VITE_APP_VERSION`.
`testSetup.ts` sets none of them and `vite.config.ts` has no `env` block for
tests, so a bare `npm run test --prefix frontend` fails 30 tests on
`Invalid frontend configuration` and 2 more on a missing version stamp — before
any change is considered. `.env.example` carries the values and
`deploy/config-inject.sh` injects them for a real build; nothing does it for
`vitest`. Not fixed here (out of scope, and touching `testSetup.ts` would change
a shared harness for a task about runbooks), but the next agent reading those
30 failures should know they are not their fault.

## Recommendations for Next Agent (044)

### Repo state

- `main` carries 043C on top of 043A. The previous report in
  `tasks_report_B/` is **043A** — **043B has no report and its changes are not
  on `main`**. `git log` goes `5bf914a` (043A) → `dbc1d5a` (043, combined) →
  `c1d7c22` (042A). **043B (rollout/rollback) is still outstanding**, and 043C
  links `../../rollout.md` and `../rollback.md` for it, which is correct and
  resolves — those pages exist from 043.
- `master-prompt.md` is modified and uncommitted, pre-existing scratch, left
  alone as 042/042A/043/043A did.
- Green: `dotnet build` 0 warnings/0 errors; frontend typecheck, lint,
  1651/1651 tests, `check:no-hex`, `typecheck:e2e`, client drift; `test:tools`
  254/255; `check:unit-containers`; `workflow-lint` 0 findings;
  `quarantine-check`; `migration-compat`; `hosting.test.sh` 28/28.
- Red **and pre-existing, proved by `git stash -u` + re-run** — do not "fix"
  either by suppressing it:
  - `deploy/verify.sh` → `MANIFEST_CHECK_FAILED` (kustomize overlays; 043A
    Finding 6, assigned to 043B);
  - `tools/npm-audit-gate.test.mjs` → 1 failure
    ("a shell-free candidate must exist before the shell fallback is reached");
  - 2 `DubbingPlatform.UnitTests` failures (`ffprobe` absent;
    `AuthOptions_Rejects_NonAbsolute_Authority`).
- `scripts/contract-snapshot.sh` remains quarantined as
  `API_CONTRACT_DIVERGENCE` (issue `#422`).

### The environment trap that will cost you 20 minutes

**`export PATH="/tmp/opencode/dotnet10:$PATH"` before any `dotnet` command.**
The SDK is not installed on this host by default; `global.json` pins
`10.0.100` and `/usr/share/dotnet` has `8.0.408`. Symptom if you forget:
`scripts/migration-compat.sh` prints
`MIGRATION_COMPAT_RESULT reason=PREVIOUS_SCHEMA_FAILED status=FAIL`, which reads
exactly like a broken migration chain and is not one. If `/tmp/opencode` was
cleared, reinstall with
`curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
--install-dir /tmp/opencode/dotnet10 --no-path`.

Also: `dotnet tool restore` is required before `dotnet ef`; `npm ci` in
`frontend/`; the frontend suite needs the four `VITE_*` vars above.

### Naming and shape conventions — the pattern to follow

**A required set is written down a second time, in a file the data change does
not reach.** This is the single most important convention this task
established, because it is the fix for the failure mode that produces a *green*
result. Four instances now:

| Required set | Duplicated in |
| --- | --- |
| `REQUIRED_GROUPS` (7) | `scripts/restore-drill.sh` and `tools/backup-coverage.test.mjs` |
| `referentialChecks` floor (1/group) | `scripts/restore-drill.sh` (`FLOOR`) and `tools/backup-coverage.test.mjs` |
| `REQUIRED_RUNBOOKS` | `tools/runbooks-index.test.mjs` |
| `PRODUCT_RUNBOOKS` / `USER_REPORT` | `tools/product-runbooks.test.mjs` |

`npm run test:tools` globs `tools/*.test.mjs`, so a new test file needs no CI
change. `check:runbooks` and `check:backup` are convenience aliases.

### Key paths and APIs

- `scripts/restore-drill.sh` — the drill. `DRILL_HOST` / `DRILL_PORT` /
  `DRILL_DB` / `DRILL_USER` / `DRILL_PGPASSWORD` / `DRILL_CONTAINER` /
  `DRILL_IMAGE` / `DRILL_ALLOW_REMOTE`; flags `--keep-container`,
  `--require-schema`. Emits `RESTORE_DRILL_RESULT reason=<R> status=<PASS|FAIL>
  tables=<n> rows=<n> gaps=<n>`. **16 reasons**, all documented in
  `docs/ci-branch-protection.md`; add a reason there in the same commit.
- `deploy/backup/scope.json` — `scopeVersion`, `mechanism`, `newEntityGroups[]`
  (`id`, `title`, `tables`, optional `columns`, `restorePriority`,
  `retentionDays`, `restoredBy`, `why`, `addedBy`), `parents.tables`,
  `notInPostgres[]`, `referentialChecks[]` (`child.col -> parent.col`),
  `durableEntitiesNotYetBackedUp.entries` (**must stay empty**).
- `deploy/backup/drill-seed.sql` — the seed. Ids all begin `de71`; emails all
  `@drill.invalid`; the header block records the expected counts.
- `docs/runbooks/product/index.md` — the split, the template, the access rule,
  and the **tabletop record table** (add a row per release).
- The eight product runbooks and their mechanism twins, in
  `MECHANISM_PAGE` in `tools/product-runbooks.test.mjs`. **Three names exist in
  both directories**: `auth-outage.md`, `notification-backlog.md`,
  `contract-drift.md`. Do not consolidate them; the test asserts each pair links
  the other.
- RLS column is `rowsecurity` in `pg_tables` (single `s` after `row`). This cost
  me a confusing gate failure.
- `BackendMetrics` (`src/DubbingPlatform.Api/Observability/BackendMetrics.cs`) is
  the emitted-metric source of truth; `SseEnvelope.cs` holds
  `sse.payload_dropped_total`. `tools/product-runbooks.test.mjs` reads **both**,
  including label values like `chunk_received`.

### Incomplete integration points (mine, explicitly)

- **The drill is not wired into any pipeline.** `drill:restore` is an npm alias
  and nothing calls it. It needs a real staging instance and an access window,
  so it is not a PR gate — but it should be a **quarterly promotion input**, and
  `deploy/verify.sh --post-deploy` is the natural place to demand a recent
  passing record rather than re-deriving one.
- **The managed-service PITR tier is still unverified** (gap D2a). This drill is
  `pg_dump`/`pg_restore` of the data half against a disposable single node. WAL
  continuity, the 7-day PITR window and the measured RTO need a real managed
  instance. `docs/dr/drill-log.md` has said "staging-gated" since 2026-09-17 and
  my entry does not pretend otherwise.
- **The media half is not restored by this drill** (gap D1a). A project can
  restore perfectly and render an empty workspace.
- **No `contract-canary` runs in production.** `scripts/contract-canary.sh`
  exists and no release job calls it, which is why
  `product/contract-drift.md` names a periodic `openapi.json`-vs-bundle check as
  a gap. Owner 042/038.
- **No release job calls `deploy/config-inject.sh` or reads
  `deploy/k8s/frontend/configmap.yaml`** — inherited from 043A, still open, and
  the reason `product/frontend-deploy-failure.md` has to tell a responder to read
  a build log line by hand.
- **The kustomize overlay patch-target failure** (043A Finding 6) remains 043B's.
  I added no manifest and touched no overlay.
- **The 30-vs-28 hosting-gate count** — 043A's report overstates it (Finding 4).
  Reconcile before anyone cites that number as a baseline.

### If you change a product runbook

1. Keep the section order exactly: `## Signals`, `## Symptoms`, `## Five-minute
   triage`, `## Degraded-mode triage`, `## Mitigation`, `## Escalation`,
   `## Postmortem trigger`, `## Access and audit`. Three lines minimum each.
2. Open with `**What a user says:** "…"` and use a phrase the `USER_REPORT` map
   in `tools/product-runbooks.test.mjs` recognises — add the phrase there if the
   page deserves its own entry.
3. `## Signals` rows must each be **one physical line**; a wrapped row loses its
   tail when Markdown renders it, `Owner:` included.
4. Every monitor you name must be a real alert / dashboard / emitted metric, or
   a `**GAP …** **Owner: NNN.**` row.
5. Every `/api/v1/admin/…` route you cite must exist on `AdminController`; the
   test resolves the routes from the source.
6. `## Access and audit` must name `RequireTenantAdmin`, both roles, `401` **and**
   `403`, `ProjectViewer`, `audit_events`, and link
   `../../operations/support-access.md`.
7. Do not restate a mechanism — link the page in `MECHANISM_PAGE`. The test
   fails on mechanism vocabulary inside `## Mitigation`.
8. No credential shapes and only synthetic ids (`prj_01…`, `usr_01…`,
   `ntf_01…`, `ten_01…`).
