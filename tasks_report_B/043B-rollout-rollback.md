# Task 043B — Expand/Contract Rollout and Rollback

## Status

**COMPLETED.** All five instructions are implemented, the gate is green, and
`deploy/rollout.md` — the record the task names four times — exists and is
read by a gate rather than read by a person. `scripts/check-rollout-window.mjs`
returns `ROLLOUT_GATE_RESULT reason=OK status=PASS`; `deploy/verify.sh` runs it
as one of its checks and returns `VERIFY_RESULT reason=OK status=PASS`; the
task's three `Validation` commands pass **verbatim**, including
`kubectl rollout history deployment/dubbing-api`, which needed the Deployment to
be *named* `dubbing-api`.

**Five defects were found by *running* the thing, and all five are fixed.** One is
in a committed artefact of this repository; four were in code this task wrote and
then repaired. They are one underlying mistake — *a check that succeeds without
evidence* — in seven different spellings:

1. **The migration gate could be satisfied by a previous release's success.**
   A Job's `spec` is immutable, so `kubectl apply -f` against a Job that has
   already *completed* is a no-op that exits 0, and the `Complete` condition from
   the previous release is still on the object — so
   `kubectl wait --for=condition=complete` returns success **immediately**,
   having observed a migration that never ran. That is a false pass on the one
   gate R1 rests on, and it is invisible: the wait prints the Job's name and
   exits 0. Measured on a real cluster: *"the wait succeeded in 1s on the SAME
   Job object (uid 924bee0b, complete since 2026-10-01T06:43:09Z) — no
   migration ran."* The documented deploy order now DELETES the Job first, and
   `deploy/verify.sh` asserts that it does.
2. **The gate's own first version read a rollback as a failure.** `rollout undo`
   does not move the Deployment back to revision *N−1*; it records the previous
   ReplicaSet's template as a **new** revision *N+1*. The validator compared
   `revisionAfter < revisionBefore` and rejected a correct rehearsal. Found by
   running the rehearsal and reading the record it produced.
3. **A loop over an empty array reported success.** An earlier revision of
   `rehearse-rollback.sh` lost its `targets=(...)` array to a bad edit. Because
   `for x in "${arr[@]}"` over an empty array is a no-op and not an error, every
   loop ran **zero times**, every flag initialised to `yes` survived, and the
   script printed `ok revision 1 is serving on all five workloads` and
   `ROLLBACK_REHEARSAL_RESULT … passed=22 failed=0` and wrote a record whose
   `targets` array was empty. The gate rejected that record; the fix counts
   iterations instead of trusting a flag.
4. **A failed read was recorded as a measurement.** The revision reader ended in
   `|| echo 0`, so a `kubectl get` that failed produced `0`; because the success
   condition downstream is "the number **changed**", `0 !== 10` and the rehearsal
   wrote `ok dubbing-api: revision 10 -> 0, template rev2 -> rev1` into the record
   this task cites as evidence. No controller writes revision `0`, and the gate
   could not have caught it either. Fixed on **both** sides, because either alone
   leaves the hole open: the reader now returns non-zero and names why, and the
   gate now refuses a revision that is not a positive integer.
5. **The downtime measurement was confirmed by silence.** The availability probe
   logs one line per *failure*, so its log is empty for the first 50 samples — and
   the check that it was the process actually running asked "is the log free of
   complaints", which an empty log satisfies. It passed because nothing had gone
   wrong yet. The probe now announces itself with one `START` line and the check
   asks for that, so silence is evidence *against* it.

And three limits that had to be handled rather than papered over:

6. **`AddColumn … NOT NULL` with no default is invisible to a schema diff.** It
   *is* additive, so `scripts/migration-compat.sh` sees a new column and passes,
   and the failure lands on the first `INSERT` from a pod still running the
   previous release. This is the rule in the new gate and the one a diff
   provably cannot catch.
7. **`ENABLE ROW LEVEL SECURITY` on a pre-existing table is a silent break**, not
   an additive operation with a scary name: old code that does not set
   `app.tenant_id` then reads zero rows rather than an error. The rule is
   enforced only on tables the same `Up` created, and four of this repository's
   seven migrations use the pattern and all four create their own table.
8. **There is no previous release**, so the `old-api-new-db` compatibility cell
   **cannot be run**. It is declared, dated, reasoned and printed on every gate
   run rather than quietly dropped — see the decision below.

## Summary

Backend extensions now roll out over the Plan A system behind a gate that reads
the migration **source**, three JSON **records**, and the flag register's
**declarations**, in `tools/rollout-window.mjs` (pure, 88 unit tests) behind
`scripts/check-rollout-window.mjs`. The compatibility window is enforced by
classifying each EF `Up` method — a length-preserving C# masker, a brace-matcher
and an operation reader — against an allowlisted contract ledger
(`deploy/rollout/contract-ledger.json`) whose `windowReleases` must be exactly 1.
R3 and R5 are **rehearsed on real infrastructure and recorded**: the
compatibility matrix runs the candidate API image against a real PostgreSQL 16 in
both schema directions (`deploy/rollout/compat-smoke.sh`, PASS 2/2 cells), and
the rollback rehearsal applies the **real committed manifests** to a real kind
cluster, builds two genuinely different images, performs five `rollout undo`s
and measures zero downtime across 200 availability samples
(`deploy/rollout/rehearse-rollback.sh`, PASS 28/28, last run **2026-10-01**). The
API Deployment is **renamed `api` → `dubbing-api`**, which is what makes the task's
own third Validation command work.

## Files Created/Modified

### The gate (new)

| File | What it is |
| --- | --- |
| `tools/rollout-window.mjs` | **New.** The pure decision layer for R2/R3/R4/R5. C# masking/brace-matching/operation reading, migration classification, the contract-ledger invariants, the compatibility-matrix audit (including declared exemptions), the flag-register audit, the rehearsal-record audit. No filesystem, no network |
| `tools/rollout-window.test.mjs` | **New.** 81 tests, one fixture per rule, plus the real repository's own migrations and flag register |
| `scripts/check-rollout-window.mjs` | **New.** The CLI. `ROLLOUT_GATE_RESULT reason=<R> status=PASS|FAIL migrations=<n> findings=<n>`. `--today` for the staleness rules; prints every exemption on every run |
| `tools/rollout-record.mjs` | **New.** `mergeRecord`/`renderRecord` — the writer that preserves a record's committed `$comment` while replacing every measurement |
| `tools/rollout-record.test.mjs` | **New.** 7 tests, including the regression both runners hit |
| `scripts/rollout-record.mjs` | **New.** `write <new.json> [<existing.json>]` — argument handling over the tested module |

### The records (new, all committed and gate-read)

| File | What it is |
| --- | --- |
| `deploy/rollout/contract-ledger.json` | **New.** The allowlist of migrations permitted to contain contract operations, with `baseline` (the grandfather line), `baselineReviewedOn`, `windowReleases: 1` and `approvedContracts: []` |
| `deploy/rollout/compat-matrix.json` | **New.** The three cells, their **declared** `expect` blocks, the `unavailable` exemption, and the recorded run |
| `deploy/rollout/flags.json` | **New.** Nine flags with `gates`, `default`, `authorization: false`, `declaredIn`, `declaredAs` and `reviewBy` |
| `deploy/rollout/rollback-rehearsal.json` | **New.** The rehearsal record: date, environment, five targets with revision **and pod template** before/after, three observations, six `notCovered` entries |
| `deploy/rollout.md` | **New.** The record document the task names: §1 migration ordering, §2 the window, §3 the matrix, §4 the flag register, §5 the rollback + the rehearsal log |

### The runners (new)

| File | What it is |
| --- | --- |
| `deploy/rollout/compat-smoke.sh` | **New.** R3. Applies the chain with `dotnet ef database update <head>` against a throwaway `postgres:16`, starts the API image on a user-defined Docker network, and probes. `--base-url` is the staging form |
| `deploy/rollout/rehearse-rollback.sh` | **New.** R5. Real manifests on a real cluster; two distinct images; two rollouts; five undos; downtime measured from the Service ClusterIP; the stale-Job false pass demonstrated and then disproved. Every reader that feeds the record either fails the run or returns a sentinel — none of them has a default |
| `deploy/rollout/rehearsal/Dockerfile` | **New.** The stand-in image, with `/app/efbundle` so the *real* initContainer command path is what blocks the rollout. Never promoted, never referenced by `deploy/k8s` |
| `deploy/rollout/rehearsal/server.mjs` | **New.** Serves `/health/live`, `/health/ready`, `/healthz`, `/version` and the workspace read, with the release baked in at build time |
| `deploy/rollout/rehearsal/probe.mjs` | **New.** The availability probe: polls the Service ClusterIP, logs one line per **failure** and a progress line every 50 samples |
| `deploy/rollout/rehearsal/workspace-probe.mjs` | **New.** Mints an HS256 token and runs the three compatibility probes; reports the `migration-currency` check on its own |

### The manifests and the gate wiring (modified)

| File | What changed |
| --- | --- |
| `deploy/k8s/api-deployment.yaml` | **Renamed `api` → `dubbing-api`** (Deployment and Service) with the reasoning; the `app.kubernetes.io/component: api` **label is unchanged** and the comment says why |
| `deploy/k8s/ingress.yaml`, `deploy/k8s/overlays/staging/ingress-patch.yaml` | The Ingress is `dubbing-api` and routes to the `dubbing-api` Service. The patch file says explicitly that a patch whose `metadata.name` differs from the base selects nothing and reports success |
| `deploy/k8s/overlays/{staging,prod}/kustomization.yaml` | `replicas: {name: dubbing-api}` |
| `deploy/k8s/migration-job.yaml` | The stale-Job false pass, measured, with the delete in the documented order; the least-privilege and DDL-only properties stated on the manifest |
| `deploy/verify.sh` | A new static-tier check that runs the rollout gate (fail-closed on a missing `node`); three new python-tier assertions — **one gate binary on both the Job and the initContainer, the maintenance role on both, `maxUnavailable: 0`**; and one that the documented deploy order contains `delete job dubbing-migration`. A `MIGRATION_NOT_ADDITIVE` verdict is reported separately from `MANIFEST_CHECK_FAILED` |
| `package.json` | `check:rollout`; `test:tools` picks up the two new test files automatically |
| `.github/workflows/basic-ci.yml` | One step in `backend-basic`: the rollout window. It needs only node, which is what makes a destructive migration refusable in the PR that introduces it |
| `docs/ci-branch-protection.md` | The `ROLLOUT_GATE_RESULT` vocabulary and **all seven reasons**, plus the new `MIGRATION_NOT_ADDITIVE` on `VERIFY_RESULT` |
| `deploy/README.md` | Step 4 is now delete → apply → wait, with the measured false pass; pointers to `rollout.md` |
| `docs/rollout.md`, `docs/runbooks/*.md`, `docs/dr/backup-restore.md`, `docs/ci-branch-protection.md` | `deploy/api` → `deploy/dubbing-api` throughout, and a pointer from `docs/rollout.md` to the record |

## Decisions Made

1. **`deploy/k8s/migration-job.yaml` is reused, not replaced.** The task allows
   either. It already had `backoffLimit: 3` and the ArgoCD/Helm hooks; what it
   did not have was the delete-before-apply that the plain-`kubectl` path needs.
   The ordering *mechanism* is unchanged; the ordering *procedure* is now correct.
2. **The Deployment is renamed to `dubbing-api`.** The task's third Validation
   command is `kubectl rollout history deployment/dubbing-api` and the object was
   `api`. Beyond making the command work, a Deployment named `api` in a shared
   namespace is one suffix away from every other `api` object, and the object a
   rollback most often has to move backwards is the one whose name collides. The
   **label** is deliberately unchanged: renaming it would be a selector migration
   across the NetworkPolicies and the PDB with no benefit.
3. **The migration window is checked by reading C# source, not by applying
   migrations.** `scripts/migration-compat.sh` already asks "did it apply and did
   anything the previous release needed disappear". This asks a different
   question — "was it *allowed* to" — and it needs neither Docker nor a
   database, so it can refuse a drop in the PR that introduces it. A contract
   phase is a *deliberate, recorded, separate* release, and a diff against the
   previous head cannot tell that from a Tuesday mistake.
4. **The contract ledger is an allowlist, with `approvedContracts: []`.** An
   allowlist because a rule that fires only on operations somebody already thought
   of misses the one nobody did — and `Sql` proves it: raw SQL can drop a table
   and no operation-name list can see it. The empty array is correct: there are
   no deprecations pending contraction, and inventing an entry would be a record
   of a decision nobody made.
5. **The compatibility cell is keyed on the `migration-currency` check, not on
   `/health/ready`.** `/health/ready` is 200 or 503 over PostgreSQL, object
   storage, the broker, Redis *and* migration currency together, so a matrix
   keyed on it is keyed on whether a bucket is reachable. The harness runs with no
   object store (quay.io is unreachable from this host), and gating on the
   aggregate turned all three cells red for a reason unrelated to the schema.
   `/health` answers the same aggregate *and names each check*.
6. **`new-api-old-db` expects `migration-currency: Unhealthy`, and that is
   correct.** `MigrationCurrencyCheck` deliberately reports a build *ahead* of
   its schema as not-ready, so a pod never takes traffic before its own migration
   has run. This cell is what proves that gate exists, and expecting `Healthy`
   would have required disabling it.
7. **The write probe is not optional.** A new `NOT NULL` column with no default
   breaks the `INSERT`; a read of the project the harness just created succeeds
   happily. A read-only matrix proves half of what it claims, so each cell does a
   write and then the read.
8. **`old-api-new-db` is a DECLARED exemption, not a suppression.** Five fields
   are required (`reason`, `unblockedWhen`, `riskAccepted`, `reviewBy`, `cell`),
   the gate fails without each, fails when the date passes, fails when the entry
   names a cell that does not exist, fails when *every* cell is exempt, and
   **prints every exemption on every run**. The difference from a suppression is
   that a suppression hides a failure while a declaration says which check cannot
   be performed, says why in reviewable words, and stops being accepted on a
   date.
9. **The rehearsal uses a stand-in image and says so.** A rehearsal that needs
   PostgreSQL, a broker, object storage and credentials is a rehearsal that never
   happens. The record's `notCovered` is **required to be non-empty** by the gate,
   and it names the application, the database, the CDN pin, the operator, the
   unapplied manifests and the hostPath volume stand-in.
10. **Two images, not one image with two tags.** A tag that was re-pushed is a
    real way for a rollback to leave the same build running, and the runbook
    already warns to confirm with `/version` rather than the rollout status. The
    rehearsal image bakes `REHEARSAL_RELEASE` at build time so the two can be
    told apart by what they **serve**, and the script asserts the image ids
    differ.
11. **A record's `expect` block is data, read by the gate.** A run cannot pass by
    declaring its own expectations, and a cell that acquires a new expectation is
    a reviewed diff. A cell marked `PASS` whose recorded statuses contradict its
    declaration is a finding.
12. **`docs/rollout.md` stays the *procedure*; `deploy/rollout.md` is the
    *record*.** The task names the latter four times. A procedure is what somebody
    follows and a record is what somebody is asked to trust, and a procedure that
    has drifted from reality still gets followed. `deploy/README.md` and
    `docs/rollout.md` both point at it.
13. **A recorded number must be a number something could have written.** The gate
    originally accepted any `revisionAfter` that differed from `revisionBefore`,
    which is a comparison and not a measurement — it cannot tell a real revision
    from a failed read that defaulted to `0`. It now requires a positive integer on
    both sides, and one of the new unit tests reads the **committed record** and
    asserts it, so the rule and the evidence cannot drift apart. A gate that only
    compares what it is handed is trusting the writer, which is the mistake this
    whole task is about.

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ kubectl apply --dry-run=client -f deploy/k8s/
deployment.apps/worker-maintenance created (dry run)
persistentvolumeclaim/media-scratch unchanged (dry run)
deployment.apps/worker-media-prep configured (dry run)
deployment.apps/worker-media-render created (dry run)
EXIT=0

$ bash deploy/verify.sh
== structural checks (python3 + PyYAML) ==
structural OK: 20 files, kinds as specified
probe/resource/replica contract OK: /health/live 10s, /health/ready 15s FT3, 500m/1Gi -> 2/4Gi, 3 replicas
gpu contract OK: 0 replicas, nodeSelector, toleration, nvidia.com/gpu:1
migration contract OK: backoffLimit 3, ArgoCD PreSync + helm pre-upgrade hooks
migration ordering OK: one gate binary, maintenance role on both, maxUnavailable 0
deploy order OK: the migration Job is deleted before it is re-applied
media contract OK: concurrency 2, 50Gi scratch PVC, 3 replicas
frontend delivery contract OK: Dockerfile.frontend present, index.html uncached, SPA fallback present
static origin contract OK: ClusterIP, one TLS ingress, 8080, /healthz probes, no runtime env
overlay contract OK: 2 overlays, every resource/patch path exists, every pinned image and replica target resolves to a base manifest
topology contract OK: default-deny (networkpolicies.yaml), api/migration/static policies present
PASS: structural + contract checks

== rollout window (043B) ==
migration window: 7 migration(s), 276 expand op(s), 2 contract op(s), 0 ledgered
EXEMPTED: compatibility cell 'old-api-new-db' is not run. Reason: THERE IS NO PREVIOUS RELEASE. … Review by: 2026-12-29
flag register: 9 flag(s), 11 authorization source file(s) scanned
== result: all rollout-window checks passed ==
ROLLOUT_GATE_RESULT reason=OK status=PASS migrations=7 findings=0
PASS: rollout window: migration additivity, compatibility matrix, flag register, rollback rehearsal

PASS: kubectl apply --dry-run=client -f deploy/k8s/
SKIP: kubeconform (kubeconform not installed)
SKIP: kustomize build staging + prod (kustomize overlay builds are not supported on a Windows host; built in CI on ubuntu-latest)
SKIP: helm lint (helm not installed)
== result: 3 passed, 0 failed ==
VERIFY_RESULT reason=OK status=PASS exit=0

$ kubectl rollout history deployment/dubbing-api
deployment.apps/dubbing-api
REVISION  CHANGE-CAUSE
1         <none>
2         <none>
3         <none>
4         <none>
5         <none>
6         <none>
7         <none>
8         <none>
10        <none>
11        <none>
EXIT=0
```

### The rollback rehearsal — 28 assertions, PASS (R5)

The final run, against a kind v1.34.0 cluster, with the committed script:

```
== rollback rehearsal ==
  ok    built dubbing-rehearsal:rev1, serving /version release=rehearsal-rev1
  ok    built dubbing-rehearsal:rev2, serving /version release=rehearsal-rev2
  ok    distinct image ids (bc38e601… / aed9fa96…)
  ok    images loaded into the cluster (the manifests' Always pull policy is overridden to IfNotPresent)
  ok    dubbing-secrets present with the committed key set (CHANGE_ME placeholders; no credential)
  ok    configmap.yaml and pdb.yaml applied (the PDB is what minAvailable: 1 is rehearsed against)
  ok    dubbing-api, worker-control, worker-ai, worker-media-prep and frontend applied from deploy/k8s
  ok    a hostPath PV stands in for the managed scratch disk, so media-scratch binds as the manifest declares it (ReadWriteMany)
  ok    media-scratch is Bound, so media-prep's Pending state would mean a real problem
  ok    container resource requests reduced to fit one node; replica counts left as the manifests declare them
  ok    the migration gate exited 1 (Error) and the API pod never reached Running, so the rollout is blocked by the initContainer alone
  ok    the Service still routes to the PREVIOUS revision (10.244.0.28) and to no pod whose gate failed - a blocked rollout is not an outage
  ok    the same manifest rolls to ready once the gate passes
  ok    the rollback target list names all five workloads the procedure covers
  ....  dubbing-api / worker-control / worker-ai / worker-media-prep / frontend: revision 1 rolled out
  ok    revision 1 is serving on all 5 workloads, and it SERVES rehearsal-rev1
  ok    availability probe announced itself and is polling the Service ClusterIP 10.96.147.74
  ok    revision 2 rolled out on all 5 workloads, and the pods now SERVE rehearsal-rev2
  ok    rollout history lists the revisions an operator reads during an incident
  ok    dubbing-api: revision 10 -> 11, template rev2 -> rev1
  ok    worker-control: revision 5 -> 6, template rev2 -> rev1
  ok    worker-ai: revision 5 -> 6, template rev2 -> rev1
  ok    worker-media-prep: revision 5 -> 6, template rev2 -> rev1
  ok    frontend: revision 5 -> 6, template rev2 -> rev1
  ok    after the undo the API pods SERVE rehearsal-rev1, so the Deployment pointer and the running build agree
  ok    the API Service answered /health/ready on every one of 200 samples across both rollouts and the undo: no downtime
  ok    the first migration Job completed, so there is now a completed Job to be fooled by
  ok    CONFIRMED FALSE PASS: the wait succeeded in 1s on the SAME Job object (uid 924bee0b, complete since 2026-10-01T06:43:09Z) - no migration ran.
  ok    after the documented preflight delete there is no Job, and the wait can no longer be satisfied by a previous release's success
== rollback rehearsal: 28 passed, 0 failed ==
ROLLBACK_REHEARSAL_RESULT reason=OK status=PASS passed=28 failed=0
```

Two lines in that transcript are the point of findings 5 and 6: `revision 10 -> 11`
is a number the controller wrote, where an earlier run of the same script printed
`revision 10 -> 0` and reported `ok`; and `availability probe announced itself`
is a positive claim about the probe being alive, where the earlier run asked
whether the log was free of complaints and a healthy probe's log is empty.

### The compatibility matrix — 2/2 cells PASS (R3)

Last run **2026-09-30**; the rollback rehearsal below is later (2026-10-01) because
it was re-run after two of its checks were changed. Both records are inside the
90-day window the gate enforces.

```
migration heads: previous = 20260921115016_AddVoicePreviewJobs, current = 20260922082522_AddRefreshSessions
candidate image: dubbing-compat:new, built from commit 5bf914a
PostgreSQL postgres:16 ready on 55491

-- new-api-new-db: candidate against 20260922082522_AddRefreshSessions --
  migration-currency=Healthy (schema is current at 20260922082522_AddRefreshSessions)
  POST /api/v1/projects=201, GET /api/v1/projects/{id}/workspace=200
  ok
-- old-api-new-db: DECLARED UNAVAILABLE, not run --
-- new-api-old-db: candidate against 20260921115016_AddVoicePreviewJobs --
  migration-currency=Unhealthy (1 migration(s) pending; applied head 20260921115016_AddVoicePreviewJobs,
  this build defines 20260922082522_AddRefreshSessions)
  POST /api/v1/projects=201, GET /api/v1/projects/{id}/workspace=200
  ok
== compatibility matrix: PASS ==
COMPAT_SMOKE_RESULT reason=OK status=PASS cells=2
```

### No regression elsewhere

```
$ npm run test:tools
ℹ tests 304   ℹ pass 304   ℹ fail 0                (216 before; 88 added)

$ dotnet build DubbingPlatform.sln
    0 Warning(s)   0 Error(s)

$ dotnet test tests/DubbingPlatform.UnitTests
Passed!  - Failed: 0, Passed: 3040, Skipped: 0, Total: 3040   (29 s)

$ npm run test --prefix frontend
  Test Files  146 passed (146)
       Tests  1651 passed (1651)

$ npm run typecheck --prefix frontend
  tsc --noEmit -p tsconfig.json          (no output, exit 0)

$ npm run lint --prefix frontend
  eslint . --max-warnings=0              (no output, exit 0)

$ bash scripts/workflow-lint.sh
workflow-lint: 5 workflow file(s), 0 finding(s)

$ bash scripts/quarantine-check.sh
CI_GATE_RESULT reason=OK status=PASS

$ npm run check:unit-containers
CI_GATE_RESULT reason=OK status=PASS files=77 tests=1440

$ bash scripts/vite-env-audit.sh --allowlist-sync
VITE_ENV_AUDIT_RESULT reason=OK status=PASS files=3 keys=21

$ bash scripts/migration-compat.sh
  613 columns in the previous release, all still present with the same type and nullability.
  10 column(s) added - additive, which is what the rule requires.
MIGRATION_COMPAT_RESULT reason=OK status=PASS previous=20260921115016_AddVoicePreviewJobs current=20260922082522_AddRefreshSessions

$ bash deploy/tests/hosting.test.sh
  passed: 28, failed: 0
HOSTING_GATE_RESULT reason=OK status=PASS exit=0 static=PASS docker=PASS
```

### The gates proved red, on the real artefacts

| Class | Injected fault | Result | After |
| --- | --- | --- | --- |
| migration window | `AddColumn<bool>(name:"b", nullable:false)` — no default | `[break] … the first INSERT from a pod still running the previous release` | nullable-or-default required |
| migration window | `DropColumn(name:"bus_name")` inside the window | `[contract]` with the file and line, and the two-release remedy | ledger allowlist required |
| migration window | `Sql("""DROP TABLE legacy_widget;""")` | `[contract]` — no operation-name list can see it | — |
| migration window | `Sql("""DO $$ … $$""")` | `[unclassified]` — fails closed | — |
| migration window | `ENABLE ROW LEVEL SECURITY` on a table the `Up` did not create | `[break] … reads zero rows, which is silent` | — |
| migration window | a migration whose `Up` reads as empty | `[unreadable]` — a brace-matching failure is not "clean" | — |
| ledger | `windowReleases: 2` | `windowReleases must be exactly 1` | frozen at 1 |
| ledger | an approved contract with no `followUpTask` | refused | all five fields required |
| ledger | an approval whose `reviewBy` has passed | `[stale]` — an untracked contract | — |
| flag register | a flag that is only in the register | `[not-declared]` — documentation does not gate a surface | — |
| flag register | a default that disagrees with the file | `[default-mismatch]`, naming both values | — |
| flag register | a `Service` role check in `src/…/Auth/` | `[flag-in-authorization]` | — |
| matrix | a probe for a route the document does not serve | `[probe-not-a-route]` | `/health`, `/version` exempt (registered outside the document) |
| matrix | a run marked `PASS` whose `migrationCurrency` contradicts the cell | `[status-mismatch]` | — |
| matrix | an exemption with no `riskAccepted` | `[exemption-incomplete]` | five fields required |
| matrix | an exemption past its `reviewBy` | `[exemption-expired]` | — |
| rehearsal | an undo that left the revision annotation unchanged | `[no-revision-change]` | — |
| rehearsal | a new revision whose pod template is unchanged | `[no-image-change]` | — |
| rehearsal | `revisionAfter: 0` — **the real defect, on the real record** | `[impossible-revision]`, naming both numbers | reader fails closed too |
| rehearsal | a probe pod whose log is empty (the healthy probe) | `bad the probe pod never announced itself` | probe prints `START url=…` |
| rehearsal | a probe pod running the rehearsal server instead | `bad … running the REHEARSAL SERVER, not the probe` | unchanged, now reachable |
| rehearsal | an empty `targets` array (a real defect) | `[not-rehearsed]` × 5 | iteration counters |
| deploy order | `delete job dubbing-migration` removed from `deploy/README.md` | `deploy/verify.sh` failed on the new assertion | — |
| manifest contract | the Job and the initContainer on different commands/images | `migration ordering OK` assertion failed | one gate binary, asserted |
| manifest contract | the gate on the app connection string instead of `maintenance-connection` | assertion failed, naming the key | — |
| hosting gate | (unrelated to this task) | `HOSTING_GATE_RESULT reason=OK status=PASS`, 28/28 | no regression |

## Findings

### 1. The migration gate was a false pass waiting to happen

The single most important finding, and it is in a committed artefact. A Job's
`spec` is immutable, so re-applying a completed Job is a no-op that exits 0, and
`kubectl wait --for=condition=complete` is then satisfied by the **previous
release's** success — in **0 to 4 seconds** across four runs, with the Job's UID
and `Complete` transition time unchanged, which is the evidence that no new pod
existed. ArgoCD and Helm are not exposed (`BeforeHookCreation` /
`before-hook-creation`), so this is specific to the plain-`kubectl` path — which
is why the delete is now step 4.0 of the documented order and why
`deploy/verify.sh` asserts it is there.

### 2. `rollout undo` increments the revision

It does not move the Deployment back to revision *N−1*; it records the previous
ReplicaSet's template as a **new** revision *N+1*, because a rollback is a change
like any other and the history has to be able to show it happened. The natural
success condition — "the revision went down" — rejects every correct rehearsal.
Found by reading the rehearsal's own record, not by reading the code. The record
now carries `imageBefore`/`imageAfter` as well, because a Deployment can record a
new revision whose template is identical to the one it had, and that is what a
rollback to a re-pushed tag looks like.

### 3. A blocked rollout is not an outage

The rehearsal's first R1 assertion required the API Service to have **no** ready
endpoints while the migration gate failed. That is true only on a first deploy.
With `maxUnavailable: 0` the previous revision is still serving, which is the
design working, and the assertion was reporting the design as a failure. The
property worth asserting — and the one now asserted — is *no pod whose gate
failed is in the Service*.

### 4. A loop over an empty array is not an error

`for x in "${arr[@]}"` over an empty array is a no-op. An earlier revision of
`rehearse-rollback.sh` lost its `targets=(...)` array, every loop ran zero times,
every flag initialised to `yes` survived, and the script reported
`ok revision 1 is serving on all five workloads` with `passed=22 failed=0` and
wrote a record with an empty `targets`. The gate rejected the record; the fix
counts iterations. This is the generalisable lesson and it is the one this task's
own tooling had to learn twice.

### 5. A failed read became a passing measurement

`revision()` ended in `|| echo 0`. When a `kubectl get` failed — once, in a run
that otherwise passed 28/28 — that produced `0`, and because the success
condition is "the number **changed**", `0` satisfied it. The record said:

```
ok    dubbing-api: revision 10 -> 0, template rev2 -> rev1
```

No controller writes revision `0`, so that is not a revision; it is a failed read
wearing one, written into the file whose whole job is to be believed. The gate
was structurally incapable of catching it (`revisionAfter !== revisionBefore` is
true for `0`), which is the actual lesson: **a rule that only compares two
recorded values cannot tell a measurement from a fabrication.** Both sides are
fixed — the reader fails closed and names kubectl's stderr, and the gate
requires a positive integer (`impossible-revision`) — and one of the new tests
asserts the *committed record* satisfies the new rule, so the two cannot drift.

Findings 1, 3 and 5 are one bug in six costumes: `|| echo 0`, `|| true`, a flag
initialised to `yes`, an empty `for` body, a loop that runs zero times, and a
completed Job nobody re-created. Each converts *"I could not tell"* into *"it is
fine"*, and each is invisible precisely because it succeeds. Finding 6 adds a
seventh costume to the list.

The fix for 5 then had the same bug one level up, which is the most useful part
of it. The reader reports its failure through a variable; the caller read it as
`before="$(revision x)"`. A command substitution runs the function in a
**subshell**, so the variable is discarded by the time the caller reads it and
every failure message would have carried the same empty string whatever `kubectl`
actually said — a diagnostic that is always identical tells the reader nothing.
The reader now sets a global the caller reads after checking the exit status, and
all five of its failure modes (kubectl non-zero, empty output, non-numeric
output, `0`, success) were checked against a stand-in before spending ten
minutes on a cluster to find out.

### 6. "The probe is measuring" was decided by silence

The downtime probe polls the Service ClusterIP and logs one line per **failure**
— the right design, and the reason a log nobody reads is not a measurement. But
it means the probe's log is *empty* for its first 50 samples (25 s at 500 ms).
The check that the probe was the process actually running asked *"is the log free
of complaints and is the phase `Running`"*, and an empty log is precisely what a
healthy probe looks like for the first 25 seconds. It passed because nothing had
gone wrong yet, not because something had gone right.

Found by auditing the script for other `|| true`-shaped holes after fixing #5 —
the same defect, in the one place it had been hiding in plain sight because the
thing it checks is *supposed* to be quiet. The probe now writes one
`START url=…` line before its first sample and the check asks for it, so silence
is evidence *against* the probe. All five branches (banner present, log empty,
server banner, wrong phase, error in log) were exercised against a stand-in
before the next cluster run.

Findings 1, 3, 5 and 6 are one bug in seven costumes: `|| echo 0`, `|| true`, a
flag initialised to `yes`, an empty `for` body, a loop that runs zero times, a
completed Job nobody re-created, and an empty log. Each converts *"I could not
tell"* — or *"nothing to report"* — into *"it is fine"*, and each is invisible
precisely because it reports success. The rule that comes out of them is short:
**read what you assert on, not what is absent.**

### 7. A readiness check is not a schema check

`/health/ready` is 200 or 503 over PostgreSQL, object storage, the broker, Redis
*and* migration currency together. A compatibility matrix keyed on it is keyed on
whether a bucket is reachable — and on this host `quay.io` is unreachable, so
all three cells went red for a reason that had nothing to do with the schema.
`/health` answers the same aggregate **and names each check**, and `absent` is a
real answer distinct from `Unhealthy`: a build that predates
`MigrationCurrencyCheck` is not a build whose check says the schema is wrong.

### 8. There is no previous release, and that is not a small thing

`old-api-new-db` is the ROLLBACK cell, and it cannot be run: this repository has
never been released. Building an image from "the commit before the newest
migration" gives a tree with no `WorkspaceController`, no `/health` or `/version`
and no `MigrationCurrencyCheck`, answering 404 on `/api/v1/me`,
`/api/v1/dashboard`, `/api/v1/notifications` and on the workspace route, and 403
on every project route with a token the current build accepts. A cell that
"passed" against that build would be measuring a different product; a cell that
failed would be blamed on the schema. It is declared, dated and printed, and it is
the cell the **first release cut** will need.

## Recommendations for Next Agent (043C)

### Repo state

- `main` carries 043B. **`master-prompt.md` is modified and uncommitted** — a
  pre-existing scratch file, deliberately left alone (042, 042A, 043 and 043A did
  the same). Do not commit it.
- Green: `dotnet build` 0 warnings; **3040** backend unit tests; **1651**
  frontend tests; `npm run test:tools` **304/304** (216 + 88 new);
  `deploy/verify.sh` (`VERIFY_RESULT reason=OK`); `deploy/tests/hosting.test.sh`
  (28/28); `scripts/workflow-lint.sh`; `scripts/quarantine-check.sh`;
  `npm run check:unit-containers`; `scripts/vite-env-audit.sh`;
  `scripts/migration-compat.sh`; `node scripts/check-rollout-window.mjs`.
- Still red **by design and pre-existing**: `scripts/contract-snapshot.sh`
  (quarantined as `API_CONTRACT_DIVERGENCE`, issue `#422`) and the 042 blocking
  audit. Do not "fix" either by suppressing it.
- **Branch protection is not configured** — it is a repository setting.
  `docs/ci-branch-protection.md` §1.1 is the checklist and it lists **five**
  required checks.

### The rollout gate, and how to extend it

- `tools/rollout-window.mjs` is the **pure** layer; `scripts/check-rollout-window.mjs`
  is a thin CLI. Exported: `MIGRATIONS_DIR`, `EXPAND_OPERATIONS`,
  `CONTRACT_OPERATIONS`, `REQUIRED_MATRIX_CELLS`, `MATRIX_CELL_PURPOSE`,
  `REQUIRED_ROLLBACK_TARGETS`, `FLAG_GATES`, `FLAG_DECLARATION_FILES`,
  `AUTHORIZATION_SOURCE_GLOBS`, `finding`, `stripCSharp`, `findUpBody`, `lineOf`,
  `readOperations`, `sqlStatementTable`, `classifySqlStatement`,
  `addColumnIsNullableOrDefaulted`, `auditMigrations`, `validateLedger`,
  `auditCompatMatrix`, `probeRoute`, `serverBasePath`, `stripBase`,
  `auditFlagRegister`, `readDeclaredValue`, `normaliseFlagValue`,
  `auditRehearsalRecord`, `daysBetween`, `readMigrationSources`.
- The CLI reads every date from `--today` (default: now), so the 90-day staleness
  rules are testable. **`MAX_RECORD_AGE_DAYS = 90` lives in the CLI, not the
  module** — change it there and the test fixtures still work.
- **To add a flag**: append to `deploy/rollout/flags.json` with `key`, `gates`
  (one of `presentation`/`behaviour`/`rollout`/`inert`), `authorization: false`,
  `default`, `declaredIn` (one of `FLAG_DECLARATION_FILES`), optional
  `declaredAs` for a token that differs from the key, and `reviewBy`. The gate
  reads the declaration and fails on a default mismatch. `declaredAs` is required
  for every C# flag: the env name is `Features__X` and the property is `X`.
- **To add a matrix cell**: `id` must be in `REQUIRED_MATRIX_CELLS` unless it is
  declared in `unavailable` with all five fields. Each cell needs `expect`
  (`migrationCurrency` string, `write` and `workspace` integers) and a
  `readinessNote` whenever `expect.migrationCurrency !== 'Healthy'`.
- **A record is written by `scripts/rollout-record.mjs`**, which preserves the
  committed `$comment`. Do not splice JSON in shell. Both runners had that bug
  and `tools/rollout-record.test.mjs` exists because of it.
- **When you write a `for … in "${arr[@]}"` loop in a script that produces a
  record, count the iterations.** An empty array is a no-op, not an error, and a
  record of a loop that never ran is worse than no record.
- **A reader that can fail must fail, loudly and by name.** `revision()` ended in
  `|| echo 0` and wrote `revision 10 -> 0` into the rehearsal record. Whenever a
  harness reads something to *compare* (`!=`, `<`, `-eq`), a default value turns
  "could not tell" into "fine", because a fabricated number differs from the real
  one. Every such reader here now returns non-zero and sets a global naming why,
  and the callers `bad` on it so the run fails.
- **Do not read a function's diagnostic out of a command substitution.**
  `x="$(f)"` runs `f` in a subshell, so anything `f` sets is gone when the caller
  reads it. Hand the value back in a global and check `$?` instead. This bit the
  fix for the defect above, one level up.
- **Never let a gate compare two recorded numbers and call that a measurement.**
  `revisionAfter !== revisionBefore` accepted `0`; the gate now also requires both
  to be positive integers. One of the new tests reads the **committed record** and
  asserts it, which is what keeps the rule and the evidence from drifting apart.
- **After you fix a fail-open, grep for the rest of them.** Fixing `|| echo 0` in
  the revision reader is what made me go looking for the same shape elsewhere, and
  the availability-probe check — *passing* because a log designed to be quiet was
  quiet — was sitting in the same file. The pattern to search for is any assertion
  phrased as an absence: "no `FAIL` lines", "no error", "phase is Running",
  "nothing complained". Each one is a check that a broken thing also satisfies.
- **Exercise a check's branches against a stand-in before spending a cluster on
  them.** Both fixes here (the reader's five failure modes, the probe check's five
  log/phase combinations) were five-line bash harnesses that took seconds, and they
  found the subshell bug that a ten-minute run would have hidden.

### Running the two rehearsals on this host

1. **Start Docker first.** After a host restart the daemon is down and
   `docker version` fails with `open //./pipe/dockerDesktopEngine: The system
   cannot be found`. The binary is
   `C:\Users\fazeli\AppData\Local\Programs\DockerDesktop\Docker Desktop.exe` —
   `Start-Process` it and wait ~45 s. **There is no `Docker Desktop.exe` under
   `C:\Program Files`.**
2. **kind** is at `$env:USERPROFILE\bin\kind.exe` (v0.30.0, downloaded from
   `https://kind.sigs.k8s.io/dl/v0.30.0/kind-windows-amd64`). It is **not** on
   PATH — prepend `$env:USERPROFILE\bin`.
3. **CRDs** are cached at `$env:USERPROFILE/bin/keda-crds.yaml` and
   `$env:USERPROFILE/bin/external-secrets.yaml`. `kubectl apply --dry-run=client -f
   deploy/k8s/` fails without them (`no matches for kind "ScaledObject"`). Install
   them with `kubectl create -f`, **not** `apply -f`: `apply` stores the whole
   document in `last-applied-configuration`, and KEDA's
   `scaledjobs.keda.sh` is over the 256 KiB annotation limit, so `apply` fails
   with `metadata.annotations: Too long: may not be more than 262144 bytes` on a
   perfectly valid CRD. That is a `kubectl` limitation, not a bad manifest.
4. **Both runners use `"C:\Program Files\Git\bin\bash.exe" -lc`**, not the
   `shell` tool's default, and take ~10 minutes each. Run them with
   `& "C:\Program Files\Git\bin\bash.exe" -lc "bash … > file 2>&1"` and read the
   file afterwards; a 10-minute foreground command is at the mercy of the host.
5. **`MSYS_NO_PATHCONV=1` is per-command, never exported.** Git Bash rewrites
   `/app/efbundle` and `/app/probe.mjs` into `C:/Program Files/Git/app/…`, and
   the symptom is `StartError … no such file or directory` on a container whose
   image and command are obviously correct. Exporting it globally instead breaks
   every `kubectl apply -f /c/Users/…` path.
6. **`kubectl run … --command -- node /app/probe.mjs` needs `--command`.** Without
   it the args are APPENDED to the image entrypoint, the pod runs the *server*
   instead of the probe, comes up Ready, the "probe is running" check passes,
   and the log is empty — a green result from a process that never ran.
7. **The rehearsal shrinks requests and stands a hostPath PV in** for
   `media-scratch`; the local-path provisioner **cannot** serve its `ReadWriteMany`
   access mode (`NodePath only supports ReadWriteOnce and ReadWriteOncePod`), and
   the PV's reclaim policy must be `Delete` **and** the PV deleted at start-up,
   or the next run's claim never binds.
8. **The rehearsal deletes its namespace on exit** unless `--keep`. Pass
   `--keep` when you also want to run `kubectl rollout history` against it.
9. **quay.io returns 401 UNAUTHORIZED from this host**, so MinIO cannot be pulled.
   That is why the compatibility cell is keyed on `migration-currency` and not on
   the aggregate `/health` code — see Finding 7. Do not "fix" it by restoring the
   aggregate.

### `scripts/migration-compat.sh` and the new gate are complementary

`migration-compat.sh` asks *"did it apply, and did anything the previous release
needed disappear?"* — it needs Docker, applies a real chain to a real
PostgreSQL, and diffs the catalogue. The new gate asks *"was it allowed to?"* —
it needs only node, reads C# source, and refuses a drop in the PR. Both are
wired: the first in `ci.yml`, the second in `Basic CI / backend-basic`.

### Open items this task did not fix

- **The kustomize overlays still do not build** (043A's open item, explicitly
  handed to 043B by 043A's report). Two blockers: the `..` `resources:` entries
  need `--load-restrictor=LoadRestrictionsNone` (already added by 043A), and the
  overlay patches do not match their targets because the base manifests hard-code
  `namespace: dubbing-prod` while the kustomizations set `namespace:` to the
  environment and the patches declare none. **I did not fix it**: dropping the
  hard-coded namespace changes the flat `kubectl apply -f deploy/k8s/` path, and
  043B's rename made it slightly worse in one place — `deploy/k8s/ingress.yaml`
  and `overlays/staging/ingress-patch.yaml` now both hard-code
  `dubbing-api`/`dubbing-prod`. Whoever fixes it should remove the namespace from
  all base manifests at once. `deploy/verify.sh` asserts overlay integrity
  *without* kustomize, which is why the gap is visible.
- **`old-api-new-db` stays unrun until the first tagged release.** The first
  release job must build the previous tag's image; nothing in this repository can
  substitute for a release existing. The exemption's `reviewBy` is **2026-12-29**.
- **All nine flag `reviewBy` dates are 2026-12-29**, and the exemption's is the
  same. A gate failure on either is *working as designed*.
- **No CI job calls `compat-smoke.sh` or `rehearse-rollback.sh`** — both need
  Docker and a cluster. They are release-pipeline and quarterly jobs; only the
  *record* check is in the always-on early gate.
- **`deploy/k8s/migration-job.yaml` has never been applied to a cluster with a
  database.** The rehearsal proved the ordering mechanism and the false pass; the
  bundle itself is exercised by `MigrationCompatTests`, not by this rehearsal.
- **The `CHANGE_ME` handles** in `.github/CODEOWNERS`,
  `deploy/helm/dubbing/values-prod.yaml`, the four in
  `deploy/k8s/frontend/ingress.yaml` and the two overlay ingress patches must
  become real hostnames, a real registry org and a real TLS `secretName` before a
  deploy.
- **043A's open items 043B did not wire**: no release job renders
  `deploy/k8s/frontend/configmap.yaml` into `frontend/.env.production`, and no
  release job calls `deploy/config-inject.sh`. The highest-value wiring left.
- **The five HIGH/CRITICAL advisories** (upgrade `vitest`,
  `@vitest-coverage-v8`, `postcss`, `vite`, `react-router-dom`) — a dependency
  PR, not a gate change.
- **The contract divergence (`#422` placeholder)** — regenerate the bundle from
  the server document, `make generate-api`, update `OpenApiCoverageTests`, delete
  the registry row and remove the `continue-on-error` in the same commit.
- **Admission control for unsigned images** — cluster-side, not CI-side.
